using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using CMOShared;

// Compiled into an instrumented copy of the renderer, never into the shipped assembly.
internal static class HistoryTrace
{
    internal sealed class Tile
    {
        public byte[] Png;
        public int Candidates, Rendered;
        public readonly Dictionary<string, int> Legacy = new Dictionary<string, int>();
        public readonly Dictionary<int, int> Labels = new Dictionary<int, int>();
        internal readonly List<Pixel> Witnesses = new List<Pixel>();
    }

    internal struct Pixel { public int X, Y, Alpha; }
    [ThreadStatic] private static Tile active;
    [ThreadStatic] internal static int? MutedLabel;

    private static bool Ink(RectangleF bounds, Action<Graphics> paint)
    {
        var crop = Rectangle.Ceiling(bounds);
        crop.Inflate(2, 2);
        crop.Intersect(new Rectangle(0, 0, 256, 256));
        if (crop.IsEmpty || crop.Width == 0 || crop.Height == 0) return false;
        using (var stamp = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb))
        {
            using (var graphics = Graphics.FromImage(stamp))
            {
                graphics.PageUnit = GraphicsUnit.Pixel;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TranslateTransform(-crop.X, -crop.Y);
                paint(graphics);
            }
            var area = new Rectangle(0, 0, stamp.Width, stamp.Height);
            var raw = stamp.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var row = new int[stamp.Width];
            var witness = new Pixel();
            try
            {
                for (int y = 0; y < stamp.Height && witness.Alpha < 255; y++)
                {
                    Marshal.Copy(IntPtr.Add(raw.Scan0, y * raw.Stride), row, 0, row.Length);
                    for (int x = 0; x < row.Length; x++)
                    {
                        int alpha = (int)((uint)row[x] >> 24);
                        if (alpha <= witness.Alpha) continue;
                        witness = new Pixel { X = crop.X + x, Y = crop.Y + y, Alpha = alpha };
                        if (alpha == 255) break;
                    }
                }
            }
            finally { stamp.UnlockBits(raw); }
            if (witness.Alpha == 0) return false;
            active.Witnesses.Add(witness);
            return true;
        }
    }

    private static void Count<T>(Dictionary<T, int> records, T key)
    {
        int previous;
        records.TryGetValue(key, out previous);
        records[key] = previous + 1;
    }

    internal static void Legacy(MapLabelPlace place, GraphicsPath path, Pen halo, Brush fill)
    {
        if (active != null && Ink(path.GetBounds(null, halo), delegate(Graphics g) {
            g.DrawPath(halo, path);
            g.FillPath(fill, path);
        }))
            Count(active.Legacy, place.SourceLayer + ":" + place.SourceIndex);
    }

    internal static void Label(ReviewedMark mark, FontFamily family, StringFormat format,
                               Pen halo, Brush fill, float x, float y)
    {
        if (active == null || mark.Symbol) return;
        var bounds = mark.Bounds;
        bounds.Offset(x, y);
        if (Ink(bounds, delegate(Graphics g) { mark.Paint(g, family, format, halo, fill, x, y); }))
            Count(active.Labels, mark.Id);
    }

    internal static Tile Draw(MapLabelIndex index, int z, int x, int y)
    {
        var result = new Tile();
        active = result;
        try
        {
            result.Png = MapLabelRenderer.RenderTileBytes(index, z, x, y, out result.Candidates, out result.Rendered);
            using (var stream = new MemoryStream(result.Png))
            using (var bitmap = new Bitmap(stream))
            {
                if (bitmap.Width != 256 || bitmap.Height != 256) throw new InvalidDataException("Unexpected PNG size.");
                foreach (var p in result.Witnesses)
                    if (bitmap.GetPixel(p.X, p.Y).A == 0)
                        throw new InvalidDataException("Glyph ink missing from PNG at " + z + "/" + x + "/" + y + " " + p.X + "," + p.Y);
            }
        }
        finally { active = null; }
        return result;
    }
}

internal static class MapLabelHistoryTests
{
    private static int Main(string[] args)
    {
        try
        {
            HistoryTrace.MutedLabel = null;
            MapLabelHistorySmoke.Run(args[0], args[1]);
            Console.WriteLine("PASS map history smoke: names, zooms, actual ink, seams and duplicate suppression.");
#if HISTORY_AUDIT
            MapLabelHistoryAudit.Run(args[0], args[1], args[2], args[3]);
#endif
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
