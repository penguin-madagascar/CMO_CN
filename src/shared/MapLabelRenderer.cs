using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;

namespace CMOShared
{
    public sealed class MapLabelPlace
    {
        public string Name;
        internal string ReviewedName;
        public string DisplayName { get { return ReviewedName ?? Name; } }
        public float Lat;
        public float Lon;
        public int Level;
        // Rank 决定「谁先画、用哪种字形、在哪些缩放出现」；Level 是历史字段（0=国家/海域，1=城市）。
        // 0=国家/海域（粗体、全缩放）、1=省/州（粗体、按 MinZoom 进位、到 z8 收尾）、2=城市（常规、z>5）。
        public int Rank;
        public float MinZoom;
        internal string SourceLayer;
        internal int SourceIndex;
    }

    /// <summary>
    /// 省/州级标签的读取器（R88）。数据放在词典的 `places` 分区里：
    /// 该分区排在 `comments` 之前，旧引擎会连同 `_meta` 一起忽略，向后兼容不受影响，
    /// 也避免为它新开一个引擎资产（安装/打包/入包对账的接口面保持不变）。
    /// 形状：[[中文, lat, lon, min_zoom], ...]
    /// </summary>
    public static class MapLabelRegions
    {
        // 反斜杠用 char 常量表达：这份源码同时被引擎与安装器编译，
        // 字面量反斜杠在跨工具链粘贴时最易被多解一层。
        private const char Backslash = (char)92;

        public static List<MapLabelPlace> LoadFromDictionary(string dictionaryPath)
        {
            var places = new List<MapLabelPlace>();
            if (string.IsNullOrEmpty(dictionaryPath) || !File.Exists(dictionaryPath)) return places;
            string text;
            try { text = File.ReadAllText(dictionaryPath, Encoding.UTF8); }
            catch (Exception) { return places; }
            return ParsePlaces(text);
        }

        internal static List<MapLabelPlace> ParsePlaces(string text)
        {
            var places = new List<MapLabelPlace>();
            int pos = TopLevelArray(text, "places");
            if (pos < 0) return places;
            int i = pos + 1;
            int n = text.Length;
            while (i < n)
            {
                char c = text[i];
                if (c == ']') break;
                if (c != '[') { i++; continue; }
                i++;
                string name = null;
                float lat = 0f, lon = 0f, minZoom = 0f;
                int field = 0;
                while (i < n && text[i] != ']')
                {
                    c = text[i];
                    if (c == '"')
                    {
                        var sb = new StringBuilder();
                        i++;
                        while (i < n)
                        {
                            char ch = text[i];
                            if (ch == Backslash) { i++; if (i < n) sb.Append(text[i]); i++; continue; }
                            if (ch == '"') { i++; break; }
                            sb.Append(ch);
                            i++;
                        }
                        if (field == 0) name = sb.ToString();
                        field++;
                        continue;
                    }
                    if (c == ',' || char.IsWhiteSpace(c)) { i++; continue; }
                    int start = i;
                    while (i < n && text[i] != ',' && text[i] != ']' && !char.IsWhiteSpace(text[i])) i++;
                    if (i > start)
                    {
                        float value;
                        string token = text.Substring(start, i - start);
                        if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                        {
                            if (field == 1) lat = value;
                            else if (field == 2) lon = value;
                            else if (field == 3) minZoom = value;
                            field++;
                        }
                    }
                }
                if (i < n) i++;
                if (!string.IsNullOrEmpty(name))
                {
                    places.Add(new MapLabelPlace
                    {
                        Name = name, Lat = lat, Lon = lon, Level = 2, Rank = 1, MinZoom = minZoom
                    });
                }
            }
            return places;
        }

        private static int TopLevelArray(string text, string key)
        {
            int depth = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
                else if (c == '"')
                {
                    int j = i + 1;
                    bool match = j + key.Length < text.Length
                        && text[j + key.Length] == '"'
                        && string.CompareOrdinal(text, j, key, 0, key.Length) == 0;
                    if (match && depth == 1)
                        return text.IndexOf('[', j + key.Length);
                    i = j;
                    while (i < text.Length)
                    {
                        if (text[i] == Backslash) { i += 2; continue; }
                        if (text[i] == '"') break;
                        i++;
                    }
                }
            }
            return -1;
        }
    }

    // Both cached and runtime labels use the same pixels, ordering and collision geometry.
    public static partial class MapLabelRenderer
    {
        private const int TileSize = 256;
        private const float EdgeMargin = 2f;

        public static Bitmap RenderTile(IList<MapLabelPlace> places, int z, int x, int y,
            out int candidates, out int rendered)
        {
            return RenderTile(places, z, x, y, out candidates, out rendered, 14f);
        }

        /// <summary>0=国家/海域，1=省/州，2=城市。未显式设置 Rank 的旧调用按 Level 推断。</summary>
        private static int RankOf(MapLabelPlace place)
        {
            if (place.Rank != 0) return place.Rank;
            return place.Level == 0 ? 0 : 2;
        }

        /// <summary>
        /// 各层的出现区间与原版 Stamen 标签层对齐（R88）：国家/海域全缩放；
        /// 省/州从上游建议的 min_zoom 开始、到 z8 收尾（更深只有城市）；城市沿用 z>5。
        /// </summary>
        private static bool VisibleAtZoom(MapLabelPlace place, int z)
        {
            int rank = RankOf(place);
            if (rank == 0) return true;
            if (rank == 1) return z >= (int)Math.Ceiling(place.MinZoom) && z <= 8;
            return z > 5;
        }

        public static Bitmap RenderTile(IList<MapLabelPlace> places, int z, int x, int y,
            out int candidates, out int rendered, float cityEmPixels)
        {
            return RenderOriginal(places, z, x, y, out candidates, out rendered, cityEmPixels, null);
        }

        private static Bitmap RenderOriginal(IList<MapLabelPlace> places, int z, int x, int y,
            out int candidates, out int rendered, float cityEmPixels, TileLayout reviewed)
        {
            if (places == null) throw new ArgumentNullException("places");
            if (z < 0 || z > 30) throw new ArgumentOutOfRangeException("z");
            if (x < 0 || x >= (1 << z)) throw new ArgumentOutOfRangeException("x");
            if (y < 0 || y >= (1 << z)) throw new ArgumentOutOfRangeException("y");
            if (cityEmPixels <= 0 || float.IsNaN(cityEmPixels) || float.IsInfinity(cityEmPixels))
                throw new ArgumentOutOfRangeException("cityEmPixels");

            double n = Math.Pow(2.0, z);
            double minLon = x / n * 360.0 - 180.0;
            double maxLon = (x + 1) / n * 360.0 - 180.0;
            double maxLat = TileLatitude(y, n);
            double minLat = TileLatitude(y + 1, n);
            var selected = new List<MapLabelPlace>();
            foreach (MapLabelPlace place in places)
                if (place.Lon >= minLon && place.Lon <= maxLon && place.Lat >= minLat && place.Lat <= maxLat
                    && VisibleAtZoom(place, z) && !string.IsNullOrEmpty(place.Name)) selected.Add(place);
            candidates = selected.Count;
            rendered = 0;

            var bitmap = new Bitmap(TileSize, TileSize, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Transparent);
                    if (selected.Count == 0) return bitmap;
                    using (var family = new FontFamily("Microsoft YaHei"))
                    using (var format = StringFormat.GenericTypographic)
                    using (var halo = new Pen(Color.FromArgb(255, 20, 20, 20), 2f))
                    using (var fill = new SolidBrush(Color.White))
                    {
                        graphics.PageUnit = GraphicsUnit.Pixel;
                        graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        halo.LineJoin = LineJoin.Round;
                        var occupied = new List<RectangleF>();
                        // OrderBy is stable: equal-rank labels keep the WWP input order.
                        // 先国家、再省/州、最后城市：原版在省级视角就是「大区名压住城市名」，
                        // 反过来（城市先画）会让省名几乎全被城市挤掉。
                        foreach (MapLabelPlace place in selected.OrderBy(RankOf).ThenBy(p => p.Level))
                        {
                            int rank = RankOf(place);
                            float emPixels = rank == 0 ? cityEmPixels + (z <= 3 ? 0 : z == 4 ? 1 : 2)
                                : rank == 1 ? cityEmPixels + (z >= 7 ? 1 : 0)
                                : cityEmPixels;
                            FontStyle style = rank <= 1 ? FontStyle.Bold : FontStyle.Regular;
                            using (var path = new GraphicsPath())
                            {
                                path.AddString(place.Name, family, (int)style, emPixels, PointF.Empty, format);
                                RectangleF bounds = path.GetBounds(null, halo);
                                if (bounds.Width <= 0 || bounds.Height <= 0) continue;
                                if (bounds.Width > TileSize - EdgeMargin * 2 || bounds.Height > TileSize - EdgeMargin * 2)
                                {
                                    // Fit only oversized outlines. The halo remains two pixels, and the text stays complete.
                                    RectangleF glyph = path.GetBounds();
                                    float availableWidth = TileSize - EdgeMargin * 2 - (bounds.Width - glyph.Width);
                                    float availableHeight = TileSize - EdgeMargin * 2 - (bounds.Height - glyph.Height);
                                    float scale = Math.Min(availableWidth / glyph.Width, availableHeight / glyph.Height);
                                    using (var fitting = new Matrix())
                                    {
                                        fitting.Scale(scale, scale);
                                        path.Transform(fitting);
                                    }
                                    bounds = path.GetBounds(null, halo);
                                }
                                double latRadians = place.Lat * Math.PI / 180.0;
                                float rawX = (float)(((place.Lon + 180.0) / 360.0 * n - x) * TileSize);
                                float rawY = (float)(((1.0 - Math.Log(Math.Tan(latRadians) + 1.0 / Math.Cos(latRadians)) / Math.PI)
                                    / 2.0 * n - y) * TileSize);
                                float centerX = Math.Max(bounds.Width / 2 + EdgeMargin,
                                    Math.Min(TileSize - bounds.Width / 2 - EdgeMargin, rawX));
                                float centerY = Math.Max(bounds.Height / 2 + EdgeMargin,
                                    Math.Min(TileSize - bounds.Height / 2 - EdgeMargin, rawY));
                                float dx = centerX - (bounds.Left + bounds.Width / 2);
                                float dy = centerY - (bounds.Top + bounds.Height / 2);
                                bounds.Offset(dx, dy);
                                bounds.Inflate(1f, 1f);
                                bool collides = false;
                                foreach (RectangleF previous in occupied)
                                    if (bounds.IntersectsWith(previous)) { collides = true; break; }
                                if (!collides) occupied.Add(bounds);
                                if (reviewed != null)
                                {
                                    if (!reviewed.Eligible(place, bounds, !collides) || reviewed.IsReplaced(place)) continue;
                                    if (!reviewed.PlaceLegacy(place, path, family, format, halo, style, emPixels,
                                        rawX, rawY, out dx, out dy)) continue;
                                }
                                else if (collides) continue;
                                using (var translation = new Matrix())
                                {
                                    translation.Translate(dx, dy);
                                    path.Transform(translation);
                                }
                                graphics.DrawPath(halo, path);
                                graphics.FillPath(fill, path);
                                rendered++;
                            }
                        }
                    }
                }
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }

        private static double TileLatitude(int y, double n)
        {
            return Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * y / n))) * 180.0 / Math.PI;
        }
    }
}
namespace CMOShared
{
    // The dictionary also contains UI and database translations. Materialize only reviewed_map.
    // The engine and installer both build against the framework without a JSON dependency.
    internal sealed class ReviewedMapReader
    {
        readonly string text;
        int position;
        ReviewedMapReader(string text)
        {
            this.text = text;
        }

        public static Dictionary<string, object> ReadSection(string dictionaryText)
        {
            if (string.IsNullOrWhiteSpace(dictionaryText))
                return null;
            var reader = new ReviewedMapReader(dictionaryText);
            reader.Expect('{');
            if (reader.Consume('}'))
                return null;
            do
            {
                string key = reader.ReadString();
                reader.Expect(':');
                if (key == "reviewed_map")
                    return reader.ReadObject();
                reader.SkipValue();
            }
            while (reader.Consume(','));
            reader.Expect('}');
            return null;
        }

        void SkipWhitespace()
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
                position++;
        }

        bool Consume(char expected)
        {
            SkipWhitespace();
            if (position >= text.Length || text[position] != expected)
                return false;
            position++;
            return true;
        }

        void Expect(char expected)
        {
            if (!Consume(expected))
                throw new InvalidDataException("Map dictionary expected '" + expected + "' at " + position + ".");
        }

        string ReadString()
        {
            Expect('"');
            var result = new StringBuilder();
            while (position < text.Length)
            {
                char current = text[position++];
                if (current == '"')
                    return result.ToString();
                result.Append(current == '\\' ? ReadEscape() : current);
            }

            throw new InvalidDataException("Unterminated map string.");
        }

        char ReadEscape()
        {
            switch (text[position++])
            {
                case '\\':
                    return '\\';
                case '"':
                    return '"';
                case '/':
                    return '/';
                case 'b':
                    return '\b';
                case 'f':
                    return '\f';
                case 'n':
                    return '\n';
                case 'r':
                    return '\r';
                case 't':
                    return '\t';
                case 'u':
                    char unicode = (char)ushort.Parse(text.Substring(position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    position += 4;
                    return unicode;
                default:
                    throw new InvalidDataException("Invalid escape in map string.");
            }
        }

        void SkipValue()
        {
            SkipWhitespace();
            int depth = 0;
            bool inString = false;
            while (position < text.Length)
            {
                char current = text[position];
                if (!inString && depth == 0 && (current == ',' || current == '}' || current == ']'))
                    return;
                position++;
                if (inString)
                {
                    if (current == '\\')
                        position++;
                    else if (current == '"')
                        inString = false;
                }
                else if (current == '"')
                    inString = true;
                else if (current == '[' || current == '{')
                    depth++;
                else if (current == ']' || current == '}')
                    depth--;
            }
        }

        Dictionary<string, object> ReadObject()
        {
            Expect('{');
            var fields = new Dictionary<string, object>(StringComparer.Ordinal);
            if (Consume('}'))
                return fields;
            do
            {
                string key = ReadString();
                Expect(':');
                fields.Add(key, ReadValue());
            }
            while (Consume(','));
            Expect('}');
            return fields;
        }

        object ReadValue()
        {
            SkipWhitespace();
            if (text[position] == '"')
                return ReadString();
            if (text[position] == '{')
                return ReadObject();
            if (Consume('['))
            {
                var items = new List<object>();
                if (Consume(']'))
                    return items;
                do
                {
                    items.Add(ReadValue());
                }
                while (Consume(','));
                Expect(']');
                return items;
            }

            int start = position;
            SkipValue();
            string token = text.Substring(start, position - start).Trim();
            if (token == "null")
                return null;
            double number;
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out number) || double.IsNaN(number) || double.IsInfinity(number))
                throw new InvalidDataException("Invalid map number: " + token);
            return number;
        }

        internal static List<object> AsArray(object value)
        {
            var items = value as List<object>;
            if (items == null)
                throw new InvalidDataException("Expected map array.");
            return items;
        }

        internal static List<object> RequireFields(object value, int count)
        {
            var fields = AsArray(value);
            if (fields.Count != count)
                throw new InvalidDataException("Expected " + count + " map fields.");
            return fields;
        }

        internal static double AsNumber(object value)
        {
            if (!(value is double))
                throw new InvalidDataException("Expected map number.");
            return (double)value;
        }

        internal static int AsInteger(object value)
        {
            double number = AsNumber(value);
            if (number != Math.Truncate(number))
                throw new InvalidDataException("Expected map integer.");
            return checked((int)number);
        }

        internal static string AsText(object value)
        {
            string result = value as string;
            if (string.IsNullOrEmpty(result))
                throw new InvalidDataException("Expected nonempty map string.");
            return result;
        }
    }

    public sealed class MapLabelIndex
    {
        internal sealed class Entity
        {
            public string Name;
            public double Lat, Lon;
            public MapLabelPlace Base;
        }

        internal readonly List<MapLabelPlace> Base = new List<MapLabelPlace>();
        readonly Dictionary<long, List<MapLabelPlace>> baseTiles = new Dictionary<long, List<MapLabelPlace>>();
        readonly Dictionary<long, List<ReviewedMark>> marks = new Dictionary<long, List<ReviewedMark>>();
        readonly Dictionary<int, HashSet<MapLabelPlace>> replaced = new Dictionary<int, HashSet<MapLabelPlace>>();
        readonly Dictionary<long, HashSet<MapLabelPlace>> retained = new Dictionary<long, HashSet<MapLabelPlace>>();
        readonly Dictionary<string, List<MapLabelPlace>> layers = new Dictionary<string, List<MapLabelPlace>>();
        readonly Dictionary<string, string> sourceHashes = new Dictionary<string, string>();
        readonly HashSet<string> verifiedLayers = new HashSet<string>();
        public string Fingerprint { get; private set; }

        public int BaseCount
        {
            get
            {
                return Base.Count;
            }
        }

        public int ReviewedLabelCount { get; private set; }
        public int ReviewedSymbolCount { get; private set; }

        internal bool ReviewedData;
        const string Version = "r88-structured-3";
        static readonly IList<MapLabelPlace> NoBase = new List<MapLabelPlace>().AsReadOnly();
        static readonly IList<ReviewedMark> NoMarks = new List<ReviewedMark>().AsReadOnly();
        internal static long Key(int z, int x, int y)
        {
            return ((long)z << 40) | ((long)x << 20) | (uint)y;
        }

        static void Append<T>(Dictionary<long, List<T>> index, long key, T value)
        {
            List<T> local;
            if (!index.TryGetValue(key, out local))
            {
                local = new List<T>();
                index.Add(key, local);
            }

            local.Add(value);
        }

        internal static double Left(double lon, int z)
        {
            return ((lon + 180.0) / 360.0) * (1 << z) * 256.0;
        }

        internal static double Top(double lat, int z)
        {
            double radians = Math.Max(-85.0511287798066, Math.Min(85.0511287798066, lat)) * Math.PI / 180;
            return (1 - Math.Log(Math.Tan(radians) + 1 / Math.Cos(radians)) / Math.PI) * 0.5 * (1 << z) * 256.0;
        }

        static double Latitude(int y, int tileCount)
        {
            return Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * y / tileCount))) * 180 / Math.PI;
        }

        static string ComputeSha256(byte[] data)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        void IndexLegacy(MapLabelPlace place)
        {
            for (int z = 3; z <= 10; z++)
            {
                if ((place.Rank == 1 && (z < Math.Ceiling(place.MinZoom) || z > 8)) || (place.Level == 1 && z <= 5))
                    continue;
                int tileCount = 1 << z, centerX = (int)Math.Floor(Left(place.Lon, z) / 256), centerY = (int)Math.Floor(Top(place.Lat, z) / 256);
                for (int x = Math.Max(0, centerX - 1); x <= Math.Min(tileCount - 1, centerX + 1); x++)
                    for (int y = Math.Max(0, centerY - 1); y <= Math.Min(tileCount - 1, centerY + 1); y++)
                        if (place.Lon >= x / (double)tileCount * 360 - 180 && place.Lon <= (x + 1) / (double)tileCount * 360 - 180 && place.Lat <= Latitude(y, tileCount) && place.Lat >= Latitude(y + 1, tileCount))
                            Append(baseTiles, Key(z, x, y), place);
            }
        }

        void LoadWwp(string root, string layer)
        {
            var loaded = new List<MapLabelPlace>();
            layers.Add(layer, loaded);
            string path = Path.Combine(root, "WW", "PlacenameSets", layer, "0000_0000.wwp");
            if (!File.Exists(path))
                return;
            byte[] bytes = File.ReadAllBytes(path);
            sourceHashes[layer] = ComputeSha256(bytes);
            using (var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8))
            {
                int count = reader.ReadInt32();
                if (count < 0 || count > bytes.Length / 13)
                    throw new InvalidDataException("Invalid WWP count.");
                for (int i = 0; i < count; i++)
                {
                    var place = new MapLabelPlace
                    {
                        Name = reader.ReadString(),
                        Lat = reader.ReadSingle(),
                        Lon = reader.ReadSingle(),
                        Level = layer == "Synoptic" ? 0 : 1,
                        SourceLayer = layer,
                        SourceIndex = i
                    };
                    reader.ReadUInt32();
                    loaded.Add(place);
                }
            }

            Base.AddRange(loaded);
        }

        void LoadRegions(string dictionaryText)
        {
            var regions = MapLabelRegions.ParsePlaces(dictionaryText);
            layers.Add("Regions", regions);
            using (var bytes = new MemoryStream())
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8))
            {
                // Bind indexes to the effective R88 rows, including the original float precision.
                writer.Write(regions.Count);
                for (int index = 0; index < regions.Count; index++)
                {
                    var place = regions[index];
                    place.SourceLayer = "Regions";
                    place.SourceIndex = index;
                    byte[] name = Encoding.UTF8.GetBytes(place.Name);
                    writer.Write(name.Length);
                    writer.Write(name);
                    writer.Write(place.Lat);
                    writer.Write(place.Lon);
                    writer.Write(place.MinZoom);
                }

                writer.Flush();
                sourceHashes["Regions"] = ComputeSha256(bytes.ToArray());
            }

            Base.AddRange(regions);
        }

        MapLabelPlace FindVerifiedMember(string layer, int index)
        {
            List<MapLabelPlace> members;
            if (!layers.TryGetValue(layer, out members))
                throw new InvalidDataException("Unknown reviewed source " + layer);
            if (!verifiedLayers.Contains(layer))
                return null;
            if (index < 0 || index >= members.Count)
                throw new InvalidDataException("Reviewed source member outside layer.");
            return members[index];
        }

        static int ReadZoom(object value)
        {
            int zoom = ReviewedMapReader.AsInteger(value);
            if (zoom < 3 || zoom > 10)
                throw new InvalidDataException("Reviewed zoom outside 3–10.");
            return zoom;
        }

        static int ReadReference(object value, int count)
        {
            int index = ReviewedMapReader.AsInteger(value);
            if (index < 0 || index >= count)
                throw new InvalidDataException("Invalid reviewed object reference.");
            return index;
        }

        static double ReadCoordinate(object value, double limit)
        {
            double coordinate = ReviewedMapReader.AsNumber(value);
            if (Math.Abs(coordinate) > limit)
                throw new InvalidDataException("Invalid coordinate.");
            return coordinate;
        }

        void BindReplacement(int z, MapLabelPlace member)
        {
            if (member == null)
                return;
            HashSet<MapLabelPlace> set;
            if (!replaced.TryGetValue(z, out set))
            {
                set = new HashSet<MapLabelPlace>();
                replaced.Add(z, set);
            }

            set.Add(member);
        }

        void IndexReviewedMark(ReviewedMark mark)
        {
            int tileCount = 1 << mark.Zoom;
            int minX = (int)Math.Floor((mark.X + mark.Bounds.Left - 1) / 256.0), maxX = (int)Math.Floor((mark.X + mark.Bounds.Right + 1) / 256.0);
            int minY = Math.Max(0, (int)Math.Floor((mark.Y + mark.Bounds.Top - 1) / 256.0)), maxY = Math.Min(tileCount - 1, (int)Math.Floor((mark.Y + mark.Bounds.Bottom + 1) / 256.0));
            // At the date line adjacent tiles share a complete outline in continuous world space.
            var seen = new HashSet<long>();
            for (int x = minX; x <= maxX; x++)
                for (int y = minY; y <= maxY; y++)
                {
                    int wrappedX = ((x % tileCount) + tileCount) % tileCount;
                    long tile = Key(mark.Zoom, wrappedX, y);
                    if (seen.Add(tile))
                        Append(marks, tile, mark);
                }
        }

        void LoadReviewedMap(Dictionary<string, object> map)
        {
            VerifySourceLayers((Dictionary<string, object>)map["source_sha256"]);
            ReadDisplayNames(map);
            ReadRetainedPlaces(map);
            List<Entity> entities = ReadEntities(map["entities"]);
            List<ReviewedMark> labels;
            using (var family = new FontFamily("Microsoft YaHei"))
            using (var format = StringFormat.GenericTypographic)
            using (var halo = new Pen(Color.FromArgb(255, 20, 20, 20), 2f))
            {
                halo.LineJoin = LineJoin.Round;
                labels = ReadLabels(map["labels"], entities, family, format, halo);
            }

            ReadSymbols(map["symbols"], labels);
            ReadAdditionalBindings(map["joins"], labels);
            ReviewedData = true;
        }

        void VerifySourceLayers(Dictionary<string, object> expected)
        {
            foreach (var actual in sourceHashes)
            {
                object hash;
                if (expected.TryGetValue(actual.Key, out hash) && ReviewedMapReader.AsText(hash) == actual.Value)
                    verifiedLayers.Add(actual.Key);
            }
        }

        void ReadDisplayNames(Dictionary<string, object> map)
        {
            object names;
            if (!map.TryGetValue("display_names", out names))
                return;
            foreach (var value in ReviewedMapReader.AsArray(names))
            {
                var row = ReviewedMapReader.RequireFields(value, 3);
                var member = FindVerifiedMember(ReviewedMapReader.AsText(row[0]), ReviewedMapReader.AsInteger(row[1]));
                if (member != null)
                    member.ReviewedName = ReviewedMapReader.AsText(row[2]);
            }
        }

        void ReadRetainedPlaces(Dictionary<string, object> map)
        {
            object records;
            if (!map.TryGetValue("retained", out records))
                return;
            foreach (var value in ReviewedMapReader.AsArray(records))
            {
                var row = ReviewedMapReader.RequireFields(value, 5);
                var member = FindVerifiedMember(ReviewedMapReader.AsText(row[0]), ReviewedMapReader.AsInteger(row[1]));
                int zoom = ReadZoom(row[2]);
                int tileX = ReviewedMapReader.AsInteger(row[3]), tileY = ReviewedMapReader.AsInteger(row[4]);
                if (tileX < 0 || tileY < 0 || tileX >= (1 << zoom) || tileY >= (1 << zoom))
                    throw new InvalidDataException("Invalid retained tile.");
                if (member == null)
                    continue;
                long tile = Key(zoom, tileX, tileY);
                HashSet<MapLabelPlace> members;
                if (!retained.TryGetValue(tile, out members))
                {
                    members = new HashSet<MapLabelPlace>();
                    retained.Add(tile, members);
                }

                members.Add(member);
            }
        }

        List<Entity> ReadEntities(object records)
        {
            var entities = new List<Entity>();
            foreach (var value in ReviewedMapReader.AsArray(records))
            {
                var row = ReviewedMapReader.AsArray(value);
                string layer = ReviewedMapReader.AsText(row[0]);
                if (layer == "local")
                {
                    ReviewedMapReader.RequireFields(row, 5);
                    ReviewedMapReader.AsText(row[1]);
                    entities.Add(new Entity { Name = ReviewedMapReader.AsText(row[2]), Lat = ReadCoordinate(row[3], 90), Lon = ReadCoordinate(row[4], 180) });
                }
                else
                {
                    ReviewedMapReader.RequireFields(row, 2);
                    var member = FindVerifiedMember(layer, ReviewedMapReader.AsInteger(row[1]));
                    // Keep positional ids stable even when a customized WWP cannot be verified.
                    entities.Add(member == null ? null : new Entity { Name = member.DisplayName, Lat = member.Lat, Lon = member.Lon, Base = member });
                }
            }

            return entities;
        }

        List<ReviewedMark> ReadLabels(object records, IList<Entity> entities, FontFamily family, StringFormat format, Pen halo)
        {
            var labels = new List<ReviewedMark>();
            foreach (var value in ReviewedMapReader.AsArray(records))
            {
                var row = ReviewedMapReader.RequireFields(value, 7);
                var entity = entities[ReadReference(row[0], entities.Count)];
                int zoom = ReadZoom(row[1]);
                if (entity == null)
                {
                    labels.Add(null);
                    continue;
                }

                string[] lines = row[4] == null ? new[]
                {
                    entity.Name
                }

                : ReviewedMapReader.AsArray(row[4]).Select(ReviewedMapReader.AsText).ToArray();
                int style = ReviewedMapReader.AsInteger(row[6]);
                float fontSize = (float)ReviewedMapReader.AsNumber(row[5]);
                if (lines.Length < 1 || lines.Length > 16 || fontSize < 1 || fontSize > 128 || (style != 0 && style != 1))
                    throw new InvalidDataException("Invalid reviewed typography.");
                var label = new ReviewedMark
                {
                    Id = labels.Count,
                    Zoom = zoom,
                    X = Left(entity.Lon, zoom) + ReviewedMapReader.AsNumber(row[2]),
                    Y = Top(entity.Lat, zoom) + ReviewedMapReader.AsNumber(row[3]),
                    Lines = lines,
                    FontSize = fontSize,
                    Style = style == 1 ? FontStyle.Bold : FontStyle.Regular
                };
                label.Measure(family, format, halo);
                IndexReviewedMark(label);
                labels.Add(label);
                BindReplacement(zoom, entity.Base);
                ReviewedLabelCount++;
            }

            return labels;
        }

        void ReadSymbols(object records, IList<ReviewedMark> labels)
        {
            var symbols = ReviewedMapReader.AsArray(records);
            for (int id = 0; id < symbols.Count; id++)
            {
                var row = ReviewedMapReader.RequireFields(symbols[id], 10);
                int zoom = ReadZoom(row[0]);
                string kind = ReviewedMapReader.AsText(row[5]);
                if (kind != "circle" && kind != "triangle" && kind != "slash")
                    throw new InvalidDataException("Invalid reviewed symbol " + kind);
                if (row[8] != null)
                {
                    var owner = labels[ReadReference(row[8], labels.Count)];
                    if (owner == null)
                        continue;
                    if (owner.Zoom != zoom)
                        throw new InvalidDataException("Symbol owner zoom differs.");
                }

                float size = (float)ReviewedMapReader.AsNumber(row[6]), stroke = (float)ReviewedMapReader.AsNumber(row[7]);
                float width = size, height = size;
                if (row[9] != null)
                {
                    var dimensions = ReviewedMapReader.RequireFields(row[9], 2);
                    width = (float)ReviewedMapReader.AsNumber(dimensions[0]);
                    height = (float)ReviewedMapReader.AsNumber(dimensions[1]);
                }

                if (width <= 0 || height <= 0 || width > 128 || height > 128 || stroke <= 0 || stroke > 128)
                    throw new InvalidDataException("Invalid reviewed symbol dimensions.");
                var symbol = new ReviewedMark
                {
                    Id = id,
                    Symbol = true,
                    Zoom = zoom,
                    Kind = kind,
                    Width = width,
                    Height = height,
                    Stroke = stroke,
                    X = Left(ReadCoordinate(row[2], 180), zoom) + ReviewedMapReader.AsNumber(row[3]),
                    Y = Top(ReadCoordinate(row[1], 90), zoom) + ReviewedMapReader.AsNumber(row[4]),
                    Bounds = new RectangleF(-width / 2, -height / 2, width, height)
                };
                IndexReviewedMark(symbol);
                ReviewedSymbolCount++;
            }
        }

        void ReadAdditionalBindings(object records, IList<ReviewedMark> labels)
        {
            foreach (var value in ReviewedMapReader.AsArray(records))
            {
                var row = ReviewedMapReader.RequireFields(value, 4);
                var label = labels[ReadReference(row[0], labels.Count)];
                int zoom = ReadZoom(row[1]);
                if (label == null)
                    continue;
                if (label.Zoom != zoom)
                    throw new InvalidDataException("Joined label zoom differs.");
                BindReplacement(zoom, FindVerifiedMember(ReviewedMapReader.AsText(row[2]), ReviewedMapReader.AsInteger(row[3])));
            }
        }

        public static MapLabelIndex Load(string gameDirectory, string dictionaryPath)
        {
            var index = new MapLabelIndex();
            index.LoadWwp(gameDirectory, "Synoptic");
            index.LoadWwp(gameDirectory, "Cities");
            byte[] dictionaryBytes = File.Exists(dictionaryPath) ? File.ReadAllBytes(dictionaryPath) : new byte[0];
            string dictionaryText = Encoding.UTF8.GetString(dictionaryBytes).TrimStart('\uFEFF');
            index.LoadRegions(dictionaryText);
            foreach (var place in index.Base)
                index.IndexLegacy(place);
            var reviewedMap = ReviewedMapReader.ReadSection(dictionaryText);
            if (reviewedMap != null)
                index.LoadReviewedMap(reviewedMap);
            var sourceFingerprints = index.sourceHashes.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Key + ":" + entry.Value);
            string identity = Version + "|" + ComputeSha256(dictionaryBytes) + "|" + string.Join("|", sourceFingerprints);
            index.Fingerprint = ComputeSha256(Encoding.UTF8.GetBytes(identity));
            return index;
        }

        internal IList<MapLabelPlace> At(int z, int x, int y)
        {
            if (z < 3 || z > 10)
                return Base;
            List<MapLabelPlace> places;
            return baseTiles.TryGetValue(Key(z, x, y), out places) ? (IList<MapLabelPlace>)places : NoBase;
        }

        internal IList<ReviewedMark> Marks(int z, int x, int y)
        {
            List<ReviewedMark> tileMarks;
            return z >= 3 && z <= 10 && marks.TryGetValue(Key(z, x, y), out tileMarks) ? (IList<ReviewedMark>)tileMarks : NoMarks;
        }

        internal HashSet<MapLabelPlace> Suppressed(int z)
        {
            HashSet<MapLabelPlace> members;
            return replaced.TryGetValue(z, out members) ? members : null;
        }

        internal HashSet<MapLabelPlace> Retained(int z, int x, int y)
        {
            HashSet<MapLabelPlace> members;
            return z >= 3 && z <= 10 && retained.TryGetValue(Key(z, x, y), out members) ? members : null;
        }

        public IEnumerable<int[]> CoveredTiles()
        {
            foreach (long key in baseTiles.Keys.Concat(marks.Keys).Distinct().OrderBy(v => v))
                yield return new[]
                {
                    (int)(key >> 40),
                    (int)((key >> 20) & 0xfffff),
                    (int)(key & 0xfffff)
                };
        }
    }
}
namespace CMOShared
{
    // The catalog owns layout coordinates, never bitmaps or Graphics objects. Threads build
    // paths in local tile space, with the same origin on the two sides of every seam.
    internal sealed class ReviewedMark
    {
        public int Id, Zoom;
        public double X, Y;
        public bool Symbol;
        public string[] Lines;
        public string Kind;
        public float Width, Height, Stroke, FontSize;
        public FontStyle Style;
        public RectangleF Bounds;
        PointF[] lineOrigins;
        public void Measure(FontFamily family, StringFormat format, Pen halo)
        {
            var lineBounds = new RectangleF[Lines.Length];
            float width = 0, height = 0;
            for (int i = 0; i < Lines.Length; i++)
            {
                using (var path = new GraphicsPath())
                {
                    path.AddString(Lines[i], family, (int)Style, FontSize, PointF.Empty, format);
                    if (path.PointCount == 0)
                        throw new InvalidDataException("Empty reviewed text path.");
                    lineBounds[i] = path.GetBounds(null, halo);
                    width = Math.Max(width, lineBounds[i].Width);
                    height += lineBounds[i].Height;
                }
            }
            height += 2 * (Lines.Length - 1);
            Bounds = new RectangleF(-width / 2, -height / 2, width, height);
            lineOrigins = new PointF[Lines.Length];
            float y = -height / 2;
            for (int i = 0; i < Lines.Length; i++)
            {
                RectangleF line = lineBounds[i];
                lineOrigins[i] = new PointF(-line.Left - line.Width / 2, y - line.Top);
                y += line.Height + 2;
            }
        }
        public float LocalX(int tileX)
        {
            double span = (1 << Zoom) * 256.0, local = X - tileX * 256.0;
            local -= Math.Round((local - 128.0) / span) * span;
            return (float)local;
        }
        public RectangleF LocalBounds(int tileX, int tileY)
        {
            RectangleF local = Bounds;
            local.Offset(LocalX(tileX), (float)(Y - tileY * 256.0));
            return local;
        }
        public void Draw(Graphics graphics, FontFamily family, StringFormat format, Pen halo, Brush fill, float x, float y)
        {
            Paint(graphics, family, format, halo, fill, x, y);
        }
        internal void Paint(Graphics graphics, FontFamily family, StringFormat format, Pen halo, Brush fill, float x,
                            float y)
        {
            if (Symbol)
            {
                DrawSymbol(graphics, x, y);
                return;
            }
            using (var path = new GraphicsPath())
            {
                for (int i = 0; i < Lines.Length; i++)
                {
                    var origin = new PointF(x + lineOrigins[i].X, y + lineOrigins[i].Y);
                    path.AddString(Lines[i], family, (int)Style, FontSize, origin, format);
                }
                graphics.DrawPath(halo, path);
                graphics.FillPath(fill, path);
            }
        }
        void DrawSymbol(Graphics graphics, float x, float y)
        {
            // Place outlines inside their declared dimensions. Dots have a white rim and
            // a dark core; summit triangles and punctuation are independent geometric marks.
            float width = Math.Max(0.25f, Width - Stroke), height = Math.Max(0.25f, Height - Stroke);
            using (var path = new GraphicsPath())
            using (var white = new Pen(Color.White, Stroke))
            {
                white.LineJoin = LineJoin.Round;
                if (Kind == "circle")
                    path.AddEllipse(x - width / 2, y - height / 2, width, height);
                else if (Kind == "triangle")
                    path.AddPolygon(new[] { new PointF(x, y - height / 2), new PointF(x + width / 2, y + height / 2),
                                            new PointF(x - width / 2, y + height / 2) });
                else
                {
                    path.AddLine(x - width / 2, y + height / 2, x + width / 2, y - height / 2);
                    using (var rim = new Pen(Color.White, Stroke + 1.5f))
                    using (var core = new Pen(Color.Black, Stroke))
                    {
                        graphics.DrawPath(rim, path);
                        graphics.DrawPath(core, path);
                    }
                    return;
                }
                graphics.FillPath(Brushes.Black, path);
                graphics.DrawPath(white, path);
            }
        }
    }

    public static partial class MapLabelRenderer
    {
        public static byte[] RenderTileBytes(MapLabelIndex index, int z, int x, int y, out int candidates, out int rendered)
        {
            if (index == null)
                throw new ArgumentNullException("index");
            var marks = index.Marks(z, x, y);
            var suppress = index.Suppressed(z);
            TileLayout occupancy =
                index.ReviewedData ? new TileLayout(marks, suppress, index.Retained(z, x, y), x, y) : null;
            using (var bitmap = RenderOriginal(index.At(z, x, y), z, x, y, out candidates, out rendered, 14f, occupancy))
            {
                if (marks.Count != 0)
                {
                    int added = DrawReviewedMarks(bitmap, marks, x, y);
                    candidates += added;
                    rendered += added;
                }
                if (occupancy != null)
                    occupancy.DrawLeaders(bitmap);
                using (var stream = new MemoryStream())
                {
                    bitmap.SetResolution(96f, 96f);
                    bitmap.Save(stream, ImageFormat.Png);
                    return stream.ToArray();
                }
            }
        }
        static int DrawReviewedMarks(Bitmap bitmap, IList<ReviewedMark> marks, int x, int y)
        {
            int count = 0;
            using (var graphics = Graphics.FromImage(bitmap))
            using (var family = new FontFamily("Microsoft YaHei"))
            using (var format = StringFormat.GenericTypographic)
            using (var halo = new Pen(Color.FromArgb(255, 20, 20, 20), 2f))
            {
                graphics.PageUnit = GraphicsUnit.Pixel;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                halo.LineJoin = LineJoin.Round;
                foreach (var mark in marks)
                {
                    // GDI+ can emit a one-pixel antialias fringe beyond the geometric outline.
                    RectangleF painted = mark.LocalBounds(x, y);
                    painted.Inflate(1f, 1f);
                    if (!painted.IntersectsWith(new RectangleF(0, 0, 256, 256)))
                        continue;
                    mark.Draw(graphics, family, format, halo, Brushes.White, mark.LocalX(x), (float)(mark.Y - y * 256.0));
                    count++;
                }
            }
            return count;
        }
    }
}
namespace CMOShared
{
    // The two legacy sort orders decide which names are eligible. Actual GDI+ ink decides
    // where an eligible name can fit around the fixed, map-sheet labels and symbols.
    internal sealed class TileLayout
    {
        const int Size = 256;
        readonly IList<ReviewedMark> fixedMarks;
        readonly HashSet<MapLabelPlace> replaced;
        readonly HashSet<MapLabelPlace> retained;
        readonly List<RectangleF> wwpAlone = new List<RectangleF>();
        readonly List<Point[]> leaders = new List<Point[]>();
        readonly int tileX, tileY;
        byte[] occupied;
        static readonly Point[] Offsets = MakeOffsets();

        public TileLayout(IList<ReviewedMark> marks, HashSet<MapLabelPlace> replacements,
                          HashSet<MapLabelPlace> retainedPlaces, int x, int y)
        {
            fixedMarks = marks;
            replaced = replacements;
            retained = retainedPlaces;
            tileX = x;
            tileY = y;
        }

        public bool IsReplaced(MapLabelPlace place)
        {
            return replaced != null && replaced.Contains(place);
        }

        public bool Eligible(MapLabelPlace place, RectangleF historicalBounds, bool upstreamVisible)
        {
            if (place.SourceLayer != "Regions" && !wwpAlone.Any(historicalBounds.IntersectsWith))
            {
                wwpAlone.Add(historicalBounds);
                return true;
            }
            return upstreamVisible || (retained != null && retained.Contains(place));
        }

        // Search nearest first with deterministic tie breaks; compact text before going farther.
        static Point[] MakeOffsets()
        {
            var offsets = new List<Point>();
            for (int y = -96; y <= 96; y += 2)
                for (int x = -96; x <= 96; x += 2)
                    if (x * x + y * y <= 96 * 96)
                        offsets.Add(new Point(x, y));
            return offsets.OrderBy(p => p.X * p.X + p.Y * p.Y).ThenBy(p => Math.Abs(p.X)).ThenBy(p => p.Y).ToArray();
        }

        static int[] Pixels(Bitmap bitmap)
        {
            var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var result = new int[rectangle.Width * rectangle.Height];
                for (int row = 0; row < rectangle.Height; row++)
                    System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(data.Scan0, row * data.Stride), result,
                                                                row * rectangle.Width, rectangle.Width);
                return result;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        void Reserve(int x, int y)
        {
            // Text layout needs a one-pixel gap. Leaders can use that gap, but can never
            // replace an actual glyph pixel (including the antialias fringe).
            for (int row = Math.Max(0, y-1); row <= Math.Min(Size-1, y+1); row++)
                for (int column = Math.Max(0, x-1); column <= Math.Min(Size-1, x+1); column++)
                {
                    int index = row*Size + column;
                    if (occupied[index] == 0) occupied[index] = 1;
                }
            occupied[y*Size+x] = 2;
        }

        void MeasureFixedInk(FontFamily family, StringFormat format, Pen halo)
        {
            if (occupied != null)
                return;
            occupied = new byte[Size * Size];
            if (fixedMarks.Count == 0)
                return;
            using (var bitmap = new Bitmap(Size, Size, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.PageUnit = GraphicsUnit.Pixel;
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    foreach (var mark in fixedMarks)
                        mark.Paint(graphics, family, format, halo, Brushes.White, mark.LocalX(tileX),
                                   (float)(mark.Y - tileY * 256.0));
                }
                int[] pixels = Pixels(bitmap);
                for (int i = 0; i < pixels.Length; i++)
                    if ((pixels[i] >> 24) != 0)
                        Reserve(i % Size, i / Size);
            }
        }

        sealed class Glyph : IDisposable
        {
            public GraphicsPath Path;
            public RectangleF Bounds;
            public int Left, Top, Right, Bottom;
            public Point[] Ink;
            public float OriginX, OriginY;
            public void Dispose()
            {
                Path.Dispose();
            }
        }

        static float Clamp(float value, float low, float high)
        {
            return Math.Max(low, Math.Min(high, value));
        }

        static Glyph MeasureGlyph(GraphicsPath path, float rawX, float rawY, Pen halo)
        {
            var glyph = new Glyph { Path = path };
            var bounds = path.GetBounds(null, halo);
            glyph.OriginX = Clamp(rawX - bounds.Width / 2, 2, Size - 2 - bounds.Width) - bounds.Left;
            glyph.OriginY = Clamp(rawY - bounds.Height / 2, 2, Size - 2 - bounds.Height) - bounds.Top;
            bounds.Offset(glyph.OriginX, glyph.OriginY);
            glyph.Bounds = bounds;
            int left = (int)Math.Floor(bounds.Left) - 1;
            int top = (int)Math.Floor(bounds.Top) - 1;
            int width = (int)Math.Ceiling(bounds.Right) - left + 1;
            int height = (int)Math.Ceiling(bounds.Bottom) - top + 1;
            var ink = new List<Point>();
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                using (var outline = (GraphicsPath)path.Clone())
                using (
                    var transform = new Matrix())
                {
                    graphics.PageUnit = GraphicsUnit.Pixel;
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    transform.Translate(glyph.OriginX - left, glyph.OriginY - top);
                    outline.Transform(transform);
                    graphics.DrawPath(halo, outline);
                    graphics.FillPath(Brushes.White, outline);
                }
                int[] pixels = Pixels(bitmap);
                for (int i = 0; i < pixels.Length; i++)
                    if ((pixels[i] >> 24) != 0)
                        ink.Add(new Point(left + i % width, top + i / width));
            }
            glyph.Ink = ink.ToArray();
            if (ink.Count > 0)
            {
                glyph.Left = ink.Min(p => p.X);
                glyph.Right = ink.Max(p => p.X);
                glyph.Top = ink.Min(p => p.Y);
                glyph.Bottom = ink.Max(p => p.Y);
            }
            return glyph;
        }

        // Break at Unicode text-element boundaries. Each line keeps the same style and is
        // centered independently, including names that contain accents or surrogate pairs.
        static GraphicsPath TextPath(string name, int lineCount, FontFamily family, FontStyle style, float fontSize,
                                     StringFormat format, Pen halo)
        {
            var path = new GraphicsPath();
            int[] starts = StringInfo.ParseCombiningCharacters(name);
            float top = 0;
            for (int line = 0; line < lineCount; line++)
            {
                int first = starts.Length * line / lineCount;
                int after = starts.Length * (line + 1) / lineCount;
                int end = after == starts.Length ? name.Length : starts[after];
                string text = name.Substring(starts[first], end - starts[first]);
                using (var part = new GraphicsPath())
                using (var move = new Matrix())
                {
                    part.AddString(text, family, (int)style, fontSize, PointF.Empty, format);
                    RectangleF bounds = part.GetBounds(null, halo);
                    move.Translate(-bounds.Left - bounds.Width / 2, top - bounds.Top);
                    part.Transform(move);
                    path.AddPath(part, false);
                    top += bounds.Height + 1;
                }
            }
            return path;
        }

        bool Fits(Glyph glyph, Point shift)
        {
            if (glyph.Ink.Length == 0 || glyph.Left + shift.X < 1 || glyph.Right + shift.X >= Size - 1 ||
                glyph.Top + shift.Y < 1 || glyph.Bottom + shift.Y >= Size - 1)
                return false;
            int delta = shift.Y * Size + shift.X;
            foreach (var pixel in glyph.Ink)
                if (occupied[pixel.Y * Size + pixel.X + delta] != 0)
                    return false;
            return true;
        }

        bool FindPosition(IList<Glyph> options, int minimumRadius, int maximumRadius, out Glyph chosen, out Point shift)
        {
            foreach (var offset in Offsets)
            {
                int squared = offset.X * offset.X + offset.Y * offset.Y;
                if (squared > maximumRadius * maximumRadius)
                    break;
                if (squared < minimumRadius * minimumRadius)
                    continue;
                foreach (var option in options)
                {
                    if (!Fits(option, offset))
                        continue;
                    chosen = option;
                    shift = offset;
                    return true;
                }
            }
            chosen = null;
            shift = Point.Empty;
            return false;
        }

        public bool PlaceLegacy(MapLabelPlace place, GraphicsPath target, FontFamily family, StringFormat format, Pen halo,
                                FontStyle style, float fontSize, float rawX, float rawY, out float dx, out float dy)
        {
            MeasureFixedInk(family, format, halo);
            var options = new List<Glyph>();
            try
            {
                string name = place.DisplayName;
                var original = place.Name == name ? (GraphicsPath)target.Clone()
                                                  : TextPath(name, 1, family, style, fontSize, format, halo);
                options.Add(MeasureGlyph(original, rawX, rawY, halo));
                Glyph chosen;
                Point shift;
                if (!FindPosition(options, 0, 8, out chosen, out shift))
                {
                    int length = StringInfo.ParseCombiningCharacters(name).Length;
                    if (length >= 4)
                        options.Add(
                            MeasureGlyph(TextPath(name, 2, family, style, fontSize, format, halo), rawX, rawY, halo));
                    options.Add(MeasureGlyph(TextPath(name, 1, family, style, 12f, format, halo), rawX, rawY, halo));
                    if (length >= 3)
                        options.Add(MeasureGlyph(TextPath(name, 2, family, style, 12f, format, halo), rawX, rawY, halo));
                    if (!FindPosition(options, 0, 24, out chosen, out shift))
                    {
                        if (length >= 6)
                            options.Add(
                                MeasureGlyph(TextPath(name, 3, family, style, 11f, format, halo), rawX, rawY, halo));
                        options.Add(MeasureGlyph(TextPath(name, 1, family, style, 11f, format, halo), rawX, rawY, halo));
                        if (!FindPosition(options, 0, 96, out chosen, out shift))
                        {
                            dx = dy = 0;
                            return false;
                        }
                    }
                }
                foreach (var pixel in chosen.Ink)
                    Reserve(pixel.X + shift.X, pixel.Y + shift.Y);
                target.Reset();
                target.AddPath(chosen.Path, false);
                dx = chosen.OriginX + shift.X;
                dy = chosen.OriginY + shift.Y;
                var bounds = chosen.Bounds;
                bounds.Offset(shift.X, shift.Y);
                AddLeader(rawX, rawY, bounds);
                return true;
            }
            finally
            {
                foreach (var option in options)
                    option.Dispose();
            }
        }

        void AddLeader(float rawX, float rawY, RectangleF bounds)
        {
            var anchor = Point.Round(new PointF(Clamp(rawX, 0, Size - 1), Clamp(rawY, 0, Size - 1)));
            var edge =
                Point.Round(new PointF(Clamp(rawX, bounds.Left, bounds.Right), Clamp(rawY, bounds.Top, bounds.Bottom)));
            int dx = anchor.X - edge.X, dy = anchor.Y - edge.Y;
            if (dx * dx + dy * dy > 36)
                leaders.Add(new[] { anchor, edge });
        }

        static double Distance(Point a, Point b)
        {
            int dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx*dx + dy*dy);
        }

        // Prefer a short direct line. In crowded areas allow a single bend to expose the
        // link on both sides of intervening letters, without changing any text or its anchor.
        double RouteScore(Point[] route, double direct, int target)
        {
            int visible = 0, firstVisible = -1, lastVisible = -1, samples = 0;
            double length = 0;
            for (int segment = 1; segment < route.Length; segment++)
            {
                Point start = route[segment-1], end = route[segment];
                double distance = Distance(start, end);
                length += distance;
                int steps = Math.Max(1, (int)Math.Ceiling(distance));
                for (int step = segment == 1 ? 0 : 1; step <= steps; step++, samples++)
                {
                    double fraction = step / (double)steps;
                    int x = (int)Math.Round(start.X + (end.X-start.X)*fraction);
                    int y = (int)Math.Round(start.Y + (end.Y-start.Y)*fraction);
                    if (occupied[y*Size+x] == 2) continue;
                    if (firstVisible < 0) firstVisible = samples;
                    lastVisible = samples;
                    visible++;
                }
            }
            if (visible == 0) return -direct*5 - length;
            int endGaps = firstVisible + samples-1-lastVisible;
            return 3*Math.Min(visible, target) - endGaps - 0.6*(length-direct);
        }

        Point[] RouteLeader(Point[] line)
        {
            Point anchor = line[0], edge = line[1];
            double direct = Distance(anchor, edge);
            int target = Math.Min(15, Math.Max(4, (int)Math.Ceiling(direct*0.55)));
            double best = RouteScore(line, direct, target);
            if (best >= 3*target-2) return line;
            Point[] chosen = line;
            int limit = Math.Min(30, Math.Max(12, (int)Math.Ceiling(direct)));
            double normalX = (anchor.Y-edge.Y)/direct, normalY = (edge.X-anchor.X)/direct;
            for (int bend = 3; bend <= limit; bend += 3)
            {
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    var midpoint = new Point((int)Math.Round(Clamp((float)((anchor.X+edge.X)*0.5
                        + sign*bend*normalX), 0, Size-1)), (int)Math.Round(Clamp((float)((anchor.Y+edge.Y)*0.5
                        + sign*bend*normalY), 0, Size-1)));
                    var candidate = new[] {anchor, midpoint, edge};
                    double score = RouteScore(candidate, direct, target);
                    if (score > best)
                    {
                        best = score;
                        chosen = candidate;
                    }
                }
            }
            // An anchor and its label may both lie inside a dense row of other names.
            // Also consider a short diagonal elbow when a perpendicular bend stays hidden.
            if (best < 2*target)
            {
                int maxRadius = Math.Min(27, Math.Max(18, (int)Math.Ceiling(direct)+6));
                for (int radius = 9; radius <= maxRadius; radius += 9)
                {
                    for (int direction = 0; direction < 8; direction++)
                    {
                        double angle = direction*Math.PI/4;
                        int x = (int)Math.Round(Clamp((float)((anchor.X+edge.X)*0.5
                            + radius*Math.Cos(angle)), 0, Size-1));
                        int y = (int)Math.Round(Clamp((float)((anchor.Y+edge.Y)*0.5
                            + radius*Math.Sin(angle)), 0, Size-1));
                        var candidate = new[] {anchor, new Point(x,y), edge};
                        double score = RouteScore(candidate, direct, target);
                        if (score > best)
                        {
                            best = score;
                            chosen = candidate;
                        }
                    }
                }
            }
            return chosen;
        }

        public void DrawLeaders(Bitmap tile)
        {
            if (leaders.Count == 0)
                return;
            // Composite the leaders underneath every label, including labels placed later.
            using (var strokes = new Bitmap(Size, Size, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(strokes))
                using (
                    var halo = new Pen(Color.FromArgb(205, 20, 20, 20),
                                       2.5f))
                using (var fill = new Pen(Color.FromArgb(235, 245, 245, 245), 1f))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    foreach (var leader in leaders)
                    {
                        Point[] route = RouteLeader(leader);
                        graphics.DrawLines(halo, route);
                        graphics.DrawLines(fill, route);
                    }
                }
                int[] pixels = Pixels(strokes);
                for (int i = 0; i < pixels.Length; i++)
                {
                    if ((pixels[i] >> 24) == 0 || occupied[i] == 2)
                        continue;
                    int x = i % Size, y = i / Size;
                    if (tile.GetPixel(x, y).A == 0)
                        tile.SetPixel(x, y, Color.FromArgb(pixels[i]));
                }
            }
        }
    }
}
