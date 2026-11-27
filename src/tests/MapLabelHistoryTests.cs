using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using U = Original;
using M = Current;

// Both renderers are compiled from the sources under test. The only hook is placed
// immediately after FillPath, which excludes collision rejects and measurement passes.
internal static class MapLabelHistoryTests
{
    private sealed class Tile
    {
        public int Z, X, Y;
        public readonly List<int> Indices = new List<int>();
        public override string ToString() { return Z + "/" + X + "/" + Y; }
    }

    private sealed class Totals
    {
        public int Baseline, Packed, Drawn, Missing, Doubled;

        public void Observe(bool covered, bool drawn, string context)
        {
            Baseline++;
            if (covered) Packed++;
            if (drawn) Drawn++;
            if (covered != drawn) return;
            if (covered) Doubled++; else Missing++;
            if (Missing + Doubled <= 20)
                Console.Error.WriteLine("{0} {1}", covered ? "Double" : "Missing", context);
        }

        public bool Report(string title, int expected)
        {
            Console.WriteLine("{0}: baseline={1}, already in pack={2}, drawn={3}, missing={4}, double={5}",
                title, Baseline, Packed, Drawn, Missing, Doubled);
            return Baseline == expected && Missing == 0 && Doubled == 0 && Packed + Drawn == Baseline;
        }
    }

    private static double Latitude(int y, int n)
    {
        return Math.Atan(Math.Sinh(Math.PI * (1 - 2.0 * y / n))) * 180 / Math.PI;
    }

    private static List<Tile> Hosts(List<U.MapLabelPlace> places)
    {
        var result = new Dictionary<string, Tile>();
        for (int i = 0; i < places.Count; i++)
        {
            U.MapLabelPlace p = places[i];
            for (int z = 3; z <= 10; z++)
            {
                if (p.Rank == 1 && (z < p.MinZoom || z > 8)) continue;
                if (p.Rank != 1 && p.Level == 1 && z <= 5) continue;
                int n = 1 << z;
                double rad = p.Lat * Math.PI / 180;
                double unitY = (1 - Math.Log(Math.Tan(rad) + 1 / Math.Cos(rad)) / Math.PI) / 2;
                if (Double.IsNaN(unitY) || unitY < 0 || unitY > 1) continue;
                int cx = (int)Math.Floor((p.Lon + 180d) / 360 * n);
                int cy = (int)Math.Floor(unitY * n);
                // The renderer includes both physical hosts at an exact geographic seam.
                for (int x = Math.Max(0, cx - 1); x <= Math.Min(n - 1, cx + 1); x++)
                for (int y = Math.Max(0, cy - 1); y <= Math.Min(n - 1, cy + 1); y++)
                {
                    double south = Latitude(y + 1, n), north = Latitude(y, n);
                    if (p.Lon < x * 360d / n - 180 || p.Lon > (x + 1) * 360d / n - 180 ||
                        p.Lat < south || p.Lat > north) continue;
                    string key = z + "/" + x + "/" + y;
                    Tile tile;
                    if (!result.TryGetValue(key, out tile))
                        result.Add(key, tile = new Tile { Z = z, X = x, Y = y });
                    tile.Indices.Add(i);
                }
            }
        }
        return result.Values.OrderBy(t => t.Z).ThenBy(t => t.X).ThenBy(t => t.Y).ToList();
    }

    private static HashSet<U.MapLabelPlace> UpstreamDraws(List<U.MapLabelPlace> places, Tile t)
    {
        var drawn = new HashSet<U.MapLabelPlace>();
        U.MapLabelRenderer.Painted = p => {
            if (!drawn.Add(p)) throw new InvalidDataException("Duplicate upstream draw at " + t);
        };
        int candidates, count;
        using (U.MapLabelRenderer.RenderTile(places, t.Z, t.X, t.Y, out candidates, out count)) { }
        if (drawn.Count != count) throw new InvalidDataException("Upstream trace count disagrees at " + t);
        return drawn;
    }

    private static int Run(string[] args)
    {
        var pack = M.MapLabelTilePack.Load(Path.Combine(args[0], "assets/placenames/ChineseMapTiles.dat"));
        if (pack.Fingerprint != M.PackedWwpCoverage.Fingerprint || pack.Fingerprint != M.PackedRegionCoverage.Fingerprint)
            throw new InvalidDataException("Coverage indexes do not match the DAT payload.");
        var current = M.MapLabelCatalog.LoadBasePlaces(args[1]);
        int stock = current.Count;
        var original = current.Select(p => new U.MapLabelPlace { Name = p.Name, Lat = p.Lat, Lon = p.Lon, Level = p.Level }).ToList();
        original.AddRange(U.MapLabelRegions.LoadFromDictionary(args[2]));
        current.AddRange(M.MapLabelRegions.LoadFromDictionary(Path.Combine(args[0], "dict/CMOLocalizer.json")));
        if (stock != 4608 || original.Count != stock + 3245 || current.Count != original.Count)
            throw new InvalidDataException("Incomplete stock or region roster.");
        for (int i = stock; i < original.Count; i++)
        {
            bool sameName = original[i].Name == current[i].Name ||
                (original[i].Name == "巴厘岛" && current[i].Name == "巴厘省") ||
                (original[i].Name == "阿尔泰共和国" && current[i].Name == "阿尔泰边疆区");
            if (!sameName || original[i].Lat != current[i].Lat || original[i].Lon != current[i].Lon ||
                Math.Ceiling(original[i].MinZoom) != current[i].MinZoom || original[i].Rank != current[i].Rank)
                throw new InvalidDataException("Region identity or zoom changed at index " + (i - stock));
        }
        var wwp = new Totals();
        var regions = new Totals();
        int historicalCount = 0, upstreamCount = 0;
        foreach (Tile t in Hosts(original))
        {
            var local = t.Indices.Select(i => original[i]).ToList();
            var historic = UpstreamDraws(local.Where(p => p.Rank != 1).ToList(), t);
            var upstream = UpstreamDraws(local, t);
            var now = new HashSet<M.MapLabelPlace>();
            M.MapLabelRenderer.Painted = p => {
                if (!now.Add(p)) throw new InvalidDataException("Duplicate current draw at " + t + " " + p.Name);
                if (pack.AlreadyInPack(p, t.Z)) throw new InvalidDataException("Packed and drawn at " + t + " " + p.Name);
            };
            int candidates, count;
            M.MapLabelRenderer.RenderTileBytes(current, pack, t.Z, t.X, t.Y, out candidates, out count);
            foreach (int i in t.Indices)
            {
                bool previous = upstream.Contains(original[i]);
                if (i < stock)
                {
                    bool beforeRegions = historic.Contains(original[i]);
                    if (beforeRegions) historicalCount++;
                    if (previous) upstreamCount++;
                    if (!previous && !beforeRegions) continue;
                }
                else if (!previous) continue;
                M.MapLabelPlace p = current[i];
                (i < stock ? wwp : regions).Observe(pack.AlreadyInPack(p, t.Z), now.Contains(p),
                    t + " " + (i < stock ? "WWP " + i : "R88 " + (i - stock)) + " " + p.Name);
            }
        }
        Console.WriteLine("WWP without provinces={0}, WWP with upstream R88={1}", historicalCount, upstreamCount);
        bool validWwp = wwp.Report("WWP union", 18489);
        bool validRegions = regions.Report("Upstream R88", 4669);
        return validWwp && validRegions && historicalCount == 18407 && upstreamCount == 17530 ? 0 : 1;
    }

    private static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.GetType().Name + ": " + e.Message);
            return 1;
        }
    }
}
