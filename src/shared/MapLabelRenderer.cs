using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CMOShared
{
    public sealed class MapLabelPlace
    {
        public string Name;
        internal string ReviewedWwpName;
        public string DisplayName { get { return ReviewedWwpName ?? Name; } }
        public float Lat;
        public float Lon;
        public int Level;
        public int Rank;
        public int MinZoom = 0;
        public int MaxZoom = 30;
        public int Priority;
        public bool IsLegacy = true;
    }

    // This payload fingerprint detects corruption and versions caches; it does not prove original-map coverage.
    public sealed class MapLabelCatalog
    {
        private const string NameSeparators = " !\"#%&'()*,-./:;?@[\\]_{}·、。・‧…‐‑‒–—―‘’“”（）［］【】《》〈〉「」『』〔〕，；：？！－．";

        public string Fingerprint { get; private set; }
        public IList<MapLabelPlace> Places { get; private set; }

        private MapLabelCatalog(string fingerprint, List<MapLabelPlace> places)
        {
            Fingerprint = fingerprint;
            Places = places.AsReadOnly();
        }

        public static List<MapLabelPlace> LoadBasePlaces(string gameDirectory)
        {
            string catalog = Path.Combine(gameDirectory, "ChineseMapLabels.dat");
            if (File.Exists(catalog)) return Load(catalog).Places.ToList();
            var places = new List<MapLabelPlace>();
            foreach (string set in new[] { "Synoptic", "Cities" })
            {
                string wwp = Path.Combine(gameDirectory, "WW", "PlacenameSets", set, "0000_0000.wwp");
                if (!File.Exists(wwp)) continue;
                try
                {
                    using (var reader = new BinaryReader(File.OpenRead(wwp), Encoding.UTF8))
                    {
                        int count = reader.ReadInt32();
                        for (int i = 0; i < count; i++)
                        {
                            var place = new MapLabelPlace { Name = reader.ReadString(), Lat = reader.ReadSingle(),
                                Lon = reader.ReadSingle(), Level = set == "Synoptic" ? 0 : 1 };
                            uint flags = reader.ReadUInt32();
                            if (set == "Cities" && flags == 0)
                                place.ReviewedWwpName = ReviewedCityDisplay(i, place);
                            places.Add(place);
                        }
                    }
                }
                catch { } // The legacy loader tolerates incomplete or unavailable optional WWP sets.
            }
            return places;
        }

        // These four Cities.wwp members keep their original Name for exact pack/cache identity.
        // Match index, name and both stored float32 coordinates before selecting a display spelling.
        private static string ReviewedCityDisplay(int index, MapLabelPlace place)
        {
            switch (index)
            {
                case 1203:
                    if (place.Name == "Tutamanda旅馆" && place.Lat == -0.19727f && place.Lon == -78.4975f)
                        return "途塔曼达旅馆";
                    break;
                case 2419:
                    if (place.Name == "调布ケ丘" && place.Lat == 35.65924f && place.Lon == 139.54837f)
                        return "调布丘";
                    break;
                case 2679:
                    if (place.Name == "古斯塔沃·A.马德罗" && place.Lat == 22.8694f && place.Lon == -98.75886f)
                        return "古斯塔沃·阿道夫·马德罗";
                    break;
                case 2684:
                    if (place.Name == "古斯塔沃·A·马德罗" && place.Lat == 19.49016f && place.Lon == -99.10978f)
                        return "古斯塔沃·阿道夫·马德罗";
                    break;
            }
            return null;
        }

        public static MapLabelCatalog Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
            {
                if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "CMOMAP01")
                    throw new InvalidDataException("Unknown map catalog format.");
                string fingerprint = reader.ReadString();
                int payloadOffset = checked((int)stream.Position);
                string actual;
                using (var hash = SHA256.Create())
                    actual = BitConverter.ToString(hash.ComputeHash(bytes, payloadOffset, bytes.Length - payloadOffset))
                        .Replace("-", "").ToLowerInvariant();
                if (fingerprint != actual)
                    throw new InvalidDataException("Map catalog payload SHA256 differs from its fingerprint.");
                int count = reader.ReadInt32();
                if (count <= 0 || count > (bytes.Length - stream.Position) / 25)
                    throw new InvalidDataException("Invalid map catalog record count.");
                var places = new List<MapLabelPlace>(count);
                for (int i = 0; i < count; i++)
                {
                    var place = new MapLabelPlace
                    {
                        Name = reader.ReadString(), Lat = reader.ReadSingle(), Lon = reader.ReadSingle(),
                        MinZoom = reader.ReadInt32(), MaxZoom = reader.ReadInt32(),
                        Priority = reader.ReadInt32(), Level = reader.ReadInt32(), IsLegacy = false
                    };
                    if (!ChineseName(place.Name) || float.IsNaN(place.Lat) || float.IsInfinity(place.Lat)
                        || float.IsNaN(place.Lon) || float.IsInfinity(place.Lon)
                        || Math.Abs(place.Lat) > 85.05113f || Math.Abs(place.Lon) > 180f
                        || place.MinZoom < 3 || place.MinZoom > place.MaxZoom || place.MaxZoom > 10
                        || place.Priority < 0 || place.Priority > 1000000 || (place.Level != 0 && place.Level != 1))
                        throw new InvalidDataException("Invalid Chinese map catalog record at index " + i + ".");
                    places.Add(place);
                }
                if (stream.Position != stream.Length)
                    throw new InvalidDataException("Trailing map catalog bytes.");
                return new MapLabelCatalog(fingerprint, places);
            }
        }

        internal static bool ChineseName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Trim() != name) return false;
            bool hasHan = false;
            for (int i = 0; i < name.Length; i++)
            {
                int code = char.ConvertToUtf32(name, i);
                // Assigned Han ranges are fixed to Unicode 15, matching the Python producer.
                // The CLR Unicode tables predate some of these characters.
                bool han = (code >= 0x3400 && code <= 0x4DBF) || (code >= 0x4E00 && code <= 0x9FFF)
                    || (code >= 0xF900 && code <= 0xFA6D) || (code >= 0xFA70 && code <= 0xFAD9)
                    || (code >= 0x20000 && code <= 0x2A6DF) || (code >= 0x2A700 && code <= 0x2B739)
                    || (code >= 0x2B740 && code <= 0x2B81D) || (code >= 0x2B820 && code <= 0x2CEA1)
                    || (code >= 0x2CEB0 && code <= 0x2EBE0) || (code >= 0x2F800 && code <= 0x2FA1D)
                    || (code >= 0x30000 && code <= 0x3134A) || (code >= 0x31350 && code <= 0x323AF);
                if (han) hasHan = true;
                // Digits and separators are also fixed, rather than classified by either runtime.
                else if (!((code >= 0x30 && code <= 0x39) || (code >= 0xFF10 && code <= 0xFF19)
                    || code == 0x3007 || (code >= 0x3021 && code <= 0x3029)
                    || (code <= 0xFFFF && NameSeparators.IndexOf((char)code) >= 0))) return false;
                if (code > 0xFFFF) i++;
            }
            return hasHan;
        }
    }

    // A pack certifies exact tile bytes, not language or geographic completeness.
    public sealed class MapLabelTilePack
    {
        private sealed class Tile
        {
            public int Z, X, Y, LabelCount;
            public byte[] Png;
        }

        private sealed class LegacyRule
        {
            public int Z;
            public MapLabelPlace Place;
        }

        private readonly Dictionary<string, Tile> tiles = new Dictionary<string, Tile>();
        private readonly HashSet<string> legacyKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<LegacyRule> legacyRules = new List<LegacyRule>();
        private static readonly MapLabelPlace[] NoPlaces = new MapLabelPlace[0];
        private sealed class PlaceIndex
        {
            internal IList<MapLabelPlace> Roster;
            internal readonly Dictionary<string, List<MapLabelPlace>> Hosts = new Dictionary<string, List<MapLabelPlace>>(StringComparer.Ordinal);
            internal readonly HashSet<string> OutsideHosts = new HashSet<string>(StringComparer.Ordinal);

            internal IList<MapLabelPlace> Lookup(int z, int x, int y)
            {
                List<MapLabelPlace> places;
                return Hosts.TryGetValue(Key(z, x, y), out places) ? (IList<MapLabelPlace>)places : NoPlaces;
            }
        }
        private volatile PlaceIndex overlayIndex;
        public string Fingerprint { get; private set; }

        private static string Key(int z, int x, int y)
        {
            return z + "/" + x + "/" + y;
        }

        public static MapLabelTilePack Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
            {
                string format = Encoding.ASCII.GetString(reader.ReadBytes(8));
                if (format != "CMOTIL01" && format != "CMOTIL02")
                    throw new InvalidDataException("Unknown Chinese map tilepack format.");
                string fingerprint = reader.ReadString();
                int offset = checked((int)stream.Position);
                string actual;
                using (var hash = SHA256.Create())
                    actual = BitConverter.ToString(hash.ComputeHash(bytes, offset, bytes.Length - offset))
                        .Replace("-", "").ToLowerInvariant();
                if (fingerprint != actual)
                    throw new InvalidDataException("Chinese map tilepack payload SHA256 mismatch.");
                int count = reader.ReadInt32();
                if (count <= 0 || count > (stream.Length - stream.Position) / 21)
                    throw new InvalidDataException("Invalid Chinese map tilepack count.");
                var pack = new MapLabelTilePack { Fingerprint = fingerprint };
                for (int i = 0; i < count; i++)
                {
                    var tile = new Tile { Z = reader.ReadInt32(), X = reader.ReadInt32(),
                        Y = reader.ReadInt32(), LabelCount = reader.ReadInt32() };
                    int length = reader.ReadInt32();
                    if (tile.Z < 3 || tile.Z > 10 || tile.X < 0 || tile.X >= (1 << tile.Z)
                        || tile.Y < 0 || tile.Y >= (1 << tile.Z) || tile.LabelCount < 0
                        || length <= 0 || length > stream.Length - stream.Position)
                        throw new InvalidDataException("Invalid Chinese map tilepack record at index " + i + ".");
                    string key = Key(tile.Z, tile.X, tile.Y);
                    if (pack.tiles.ContainsKey(key)) throw new InvalidDataException("Duplicate Chinese map tile: " + key);
                    tile.Png = reader.ReadBytes(length);
                    // Verify PNG identity as well as decoding; Image.FromStream also accepts JPEG/BMP.
                    byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
                    if (length < signature.Length || !signature.SequenceEqual(tile.Png.Take(signature.Length)))
                        throw new InvalidDataException("Chinese map tile is not PNG: " + key);
                    using (var png = new MemoryStream(tile.Png, false))
                    using (var image = Image.FromStream(png, true, true))
                    {
                        if (image.Width != 256 || image.Height != 256)
                            throw new InvalidDataException("Chinese map tile must be 256x256: " + key);
                        using (var decoded = new Bitmap(image)) { decoded.GetPixel(255, 255); }
                    }
                    pack.tiles.Add(key, tile);
                }
                if (format == "CMOTIL02")
                {
                    int ruleCount = reader.ReadInt32();
                    if (ruleCount <= 0 || ruleCount > (stream.Length - stream.Position) / 18)
                        throw new InvalidDataException("Invalid legacy suppression count.");
                    var zooms = new HashSet<int>(pack.tiles.Values.Select(t => t.Z));
                    for (int i = 0; i < ruleCount; i++)
                    {
                        int z = reader.ReadInt32();
                        var place = new MapLabelPlace { Name = reader.ReadString(), Lat = reader.ReadSingle(),
                            Lon = reader.ReadSingle(), Level = reader.ReadInt32() };
                        if (!zooms.Contains(z) || !MapLabelCatalog.ChineseName(place.Name)
                            || float.IsNaN(place.Lat) || float.IsInfinity(place.Lat) || Math.Abs(place.Lat) > 90f
                            || float.IsNaN(place.Lon) || float.IsInfinity(place.Lon) || Math.Abs(place.Lon) > 180f
                            || (place.Level != 0 && place.Level != 1))
                            throw new InvalidDataException("Invalid legacy suppression at index " + i + ".");
                        if (!pack.legacyKeys.Add(LegacyKey(place, z)))
                            throw new InvalidDataException("Duplicate legacy suppression at index " + i + ".");
                        pack.legacyRules.Add(new LegacyRule { Z = z, Place = place });
                    }
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing Chinese map tilepack bytes.");
                return pack;
            }
        }

        private static string LegacyKey(MapLabelPlace place, int z)
        {
            return z + "/" + place.Level + "/" + BitConverter.ToInt32(BitConverter.GetBytes(place.Lat), 0)
                + "/" + BitConverter.ToInt32(BitConverter.GetBytes(place.Lon), 0) + "/" + place.Name;
        }

        public bool IsLegacySuppressed(MapLabelPlace place, int z)
        {
            return place.IsLegacy && legacyKeys.Contains(LegacyKey(place, z));
        }

        internal bool AlreadyInPack(MapLabelPlace place, int z)
        {
            return IsLegacySuppressed(place, z) || PackedWwpCoverage.Contains(Fingerprint, place, z)
                || PackedRegionCoverage.Contains(Fingerprint, place, z);
        }

        // Build once after loading the fixed game roster, before starting cache/HTTP workers.
        // A direct renderer caller can still pass any other list, including mutable test inputs.
        public void IndexOverlayPlaces(IList<MapLabelPlace> places)
        {
            BuildPlaceIndex(places);
        }

        private PlaceIndex BuildPlaceIndex(IList<MapLabelPlace> places)
        {
            var result = new PlaceIndex { Roster = places };
            foreach (MapLabelPlace place in places)
            {
                if (Math.Abs(place.Lat) > 85.05113 || float.IsNaN(place.Lat) || float.IsNaN(place.Lon)) continue;
                double lat = place.Lat * Math.PI / 180;
                double unitY = (1.0 - Math.Log(Math.Tan(lat) + 1.0 / Math.Cos(lat)) / Math.PI) / 2.0;
                for (int z = 3; z <= 10; z++)
                {
                    if (!MapLabelRenderer.VisibleAtZoom(place, z)) continue;
                    int n = 1 << z;
                    int x = Math.Max(0, Math.Min(n - 1, (int)Math.Floor((place.Lon + 180d) / 360d * n)));
                    int y = Math.Max(0, Math.Min(n - 1, (int)Math.Floor(unitY * n)));
                    // Float32 labels exactly on a seam belong to both inclusive legacy hosts.
                    for (int tx = Math.Max(0, x - 1); tx <= Math.Min(n - 1, x + 1); tx++)
                    for (int ty = Math.Max(0, y - 1); ty <= Math.Min(n - 1, y + 1); ty++)
                    {
                        if (!MapLabelRenderer.TileContains(place, tx / (double)n * 360 - 180, (tx + 1) / (double)n * 360 - 180,
                            MapLabelRenderer.TileLatitude(ty + 1, n), MapLabelRenderer.TileLatitude(ty, n))) continue;
                        string key = Key(z, tx, ty);
                        if (!tiles.ContainsKey(key) && (place.Rank == 1 || (place.IsLegacy && Fingerprint == PackedWwpCoverage.Fingerprint)))
                            result.OutsideHosts.Add(key);
                        List<MapLabelPlace> local;
                        if (!result.Hosts.TryGetValue(key, out local)) result.Hosts.Add(key, local = new List<MapLabelPlace>());
                        local.Add(place);
                    }
                }
            }
            overlayIndex = result;
            return result;
        }

        internal IList<MapLabelPlace> PlacesForTile(IList<MapLabelPlace> places, int z, int x, int y)
        {
            PlaceIndex index = overlayIndex;
            return index != null && ReferenceEquals(places, index.Roster) ? index.Lookup(z, x, y) : places;
        }

        internal bool ContainsTile(int z, int x, int y)
        {
            return tiles.ContainsKey(Key(z, x, y));
        }

        public bool TryGetTile(int z, int x, int y, out byte[] png, out int labelCount)
        {
            Tile tile;
            if (tiles.TryGetValue(Key(z, x, y), out tile))
            {
                png = (byte[])tile.Png.Clone();
                labelCount = tile.LabelCount;
                return true;
            }
            png = null;
            labelCount = 0;
            return false;
        }

        internal static string CacheDirectory(string gameDir)
        {
            return Path.Combine(Path.GetFullPath(gameDir), @"WW\Cache\Stamen Terrain - Labels");
        }

        internal static void AssertPlainPath(string path)
        {
            for (string current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current))
                    && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Reparse points are not allowed in map cache paths: " + current);
        }

        // Traverse one directory at a time: never recurse through an unchecked reparse point.
        internal static List<string> CacheFiles(string directory)
        {
            AssertPlainPath(directory);
            var files = new List<string>();
            if (Directory.Exists(directory)) CollectCacheFiles(directory, files);
            return files;
        }

        private static void CollectCacheFiles(string directory, List<string> files)
        {
            foreach (string path in Directory.GetFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Reparse points are not allowed in map cache paths: " + path);
                if ((attributes & FileAttributes.Directory) != 0) CollectCacheFiles(path, files);
                else files.Add(path);
            }
        }

        private static void CopyCache(string source, string destination)
        {
            AssertPlainPath(source);
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source))
            {
                AssertPlainPath(file);
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
            }
            foreach (string child in Directory.GetDirectories(source))
                CopyCache(child, Path.Combine(destination, Path.GetFileName(child)));
        }

        internal static void EnsureCacheBackup(string gameDir)
        {
            string cache = CacheDirectory(gameDir);
            string backup = cache + ".orig";
            AssertPlainPath(cache);
            AssertPlainPath(backup);
            if (Directory.Exists(backup)) return;
            string staging = backup + ".tmp-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.GetDirectoryName(cache));
            try
            {
                if (Directory.Exists(cache)) CopyCache(cache, staging);
                else Directory.CreateDirectory(staging);
                Directory.Move(staging, backup);
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    foreach (string file in CacheFiles(staging)) File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(staging, true);
                }
            }
        }

        internal static bool WriteCacheBytes(string path, byte[] bytes, bool forceWrite = false)
        {
            AssertPlainPath(path);
            if (!forceWrite && File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(bytes)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(temporary, bytes);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return true;
        }

        // The receipt is disposable. Every cached PNG must still match the digest of the
        // renderer's output before it can skip drawing; file length and time are not evidence.
        internal sealed class CacheReceipt : IDisposable
        {
            private readonly string path, inputs;
            private readonly System.Threading.Mutex mutex;
            private readonly Dictionary<string, string> previous = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> current = new Dictionary<string, string>(StringComparer.Ordinal);
            private bool sameInputs;

            internal CacheReceipt(string gameDir, string pack, IList<MapLabelPlace> places)
            {
                string cache = CacheDirectory(gameDir);
                path = Path.Combine(cache, ".chinese-map-rendered.sha256");
                inputs = InputHash(gameDir, pack, places);
                mutex = new System.Threading.Mutex(false, @"Local\CMOMapCache-" + Hash(Encoding.UTF8.GetBytes(cache.ToUpperInvariant())));
                try { mutex.WaitOne(); }
                catch (System.Threading.AbandonedMutexException) { } // Our handle now owns the lock.
                try { Load(); }
                catch { Dispose(); throw; }
            }

            public void Dispose()
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
            }

            private static string Hash(byte[] bytes)
            {
                using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
            }

            private static string FileHash(string file)
            {
                using (var input = File.OpenRead(file))
                using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
            }

            // An installer resource is immutable for this AppDomain. The Framework cannot reflection-load
            // the same byte-array assembly twice, including from concurrent cache refreshes.
            private static readonly Lazy<string> EmbeddedRendererBuild = new Lazy<string>(ReadEmbeddedRendererBuild);

            private static string ReadEmbeddedRendererBuild()
            {
                var module = typeof(MapLabelRenderer).Module;
                using (var resource = module.Assembly.GetManifestResourceStream("CMOLocalizer.dll"))
                {
                    if (resource == null) return null;
                    using (var copy = new MemoryStream())
                    {
                        resource.CopyTo(copy);
                        byte[] engine = copy.ToArray();
                        try { return System.Reflection.Assembly.ReflectionOnlyLoad(engine).ManifestModule.ModuleVersionId.ToString(); }
                        catch (BadImageFormatException) { return Hash(engine); } // Resource-only installer test hosts.
                    }
                }
            }

            private static string RendererBuild(string gameDir)
            {
                var module = typeof(MapLabelRenderer).Module;
                if (module.Assembly.GetName().Name == "CMOLocalizer") return module.ModuleVersionId.ToString();
                string embedded = EmbeddedRendererBuild.Value;
                if (embedded != null) return embedded;
                string file = Path.Combine(gameDir, "CMOLocalizer.dll");
                return File.Exists(file) ? FileHash(file) : module.ModuleVersionId.ToString();
            }

            private static string InputHash(string gameDir, string pack, IList<MapLabelPlace> places)
            {
                using (var buffer = new MemoryStream())
                using (var writer = new BinaryWriter(buffer, Encoding.UTF8))
                {
                    writer.Write("CMO map cache v1");
                    writer.Write(pack);
                    if (places != null)
                    {
                        writer.Write(RendererBuild(gameDir));
                        writer.Write(Environment.OSVersion.VersionString);
                        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                        foreach (string name in new[] { "msyh.ttc", "msyhbd.ttc", "msyh.ttf", "msyhbd.ttf" })
                        {
                            string file = Path.Combine(fonts, name);
                            writer.Write(File.Exists(file) ? FileHash(file) : "");
                        }
                        writer.Write(places.Count);
                        // Rendering has stable priority ties, so the input order also matters.
                        foreach (MapLabelPlace place in places)
                        {
                            writer.Write(place.Name ?? ""); writer.Write(place.DisplayName ?? "");
                            writer.Write(place.Lat); writer.Write(place.Lon); writer.Write(place.Level);
                            writer.Write(place.Rank); writer.Write(place.MinZoom); writer.Write(place.MaxZoom);
                            writer.Write(place.Priority); writer.Write(place.IsLegacy);
                        }
                    }
                    writer.Flush();
                    return Hash(buffer.ToArray());
                }
            }

            private void Load()
            {
                AssertPlainPath(path);
                var file = new FileInfo(path);
                if (!file.Exists || file.Length < 131 || file.Length > 64 * 1024 * 1024) return;
                string text = File.ReadAllText(path, Encoding.UTF8);
                if (text.Length < 65 || text[64] != '\n') return;
                string body = text.Substring(65);
                if (Hash(Encoding.UTF8.GetBytes(body)) != text.Substring(0, 64)) return;
                using (var lines = new StringReader(body))
                {
                    sameInputs = lines.ReadLine() == inputs;
                    string line;
                    while ((line = lines.ReadLine()) != null)
                    {
                        int tab = line.IndexOf('\t');
                        if (tab <= 0 || line.Length - tab != 65 || previous.ContainsKey(line.Substring(0, tab)))
                        {
                            previous.Clear(); sameInputs = false; return;
                        }
                        previous.Add(line.Substring(0, tab), line.Substring(tab + 1));
                    }
                }
            }

            internal bool Matches(string key, string png)
            {
                string digest;
                if (!sameInputs || !previous.TryGetValue(key, out digest) || !File.Exists(png) || FileHash(png) != digest) return false;
                current[key] = digest;
                return true;
            }

            internal bool PreviouslyManaged(string key) { return previous.ContainsKey(key); }
            internal bool HasPrevious { get { return previous.Count != 0; } }

            internal void Record(string key, byte[] bytes)
            {
                string digest = Hash(bytes);
                lock (current) current[key] = digest; // Installer fallback writes run in parallel.
            }

            internal void Save()
            {
                var lines = new StringBuilder().Append(inputs).Append('\n');
                foreach (var row in current.OrderBy(p => p.Key, StringComparer.Ordinal))
                    lines.Append(row.Key).Append('\t').Append(row.Value).Append('\n');
                string body = lines.ToString();
                WriteCacheBytes(path, Encoding.UTF8.GetBytes(Hash(Encoding.UTF8.GetBytes(body)) + "\n" + body));
            }
        }

        // Return actual PNG writes; a marker alone never makes a missing/damaged tile trustworthy.
        public int ApplyToCache(string gameDir)
        {
            return ApplyAndComplete(gameDir, null);
        }

        public int ApplyToCacheWithLegacy(string gameDir, IList<MapLabelPlace> places)
        {
            if (places == null) throw new ArgumentNullException("places");
            return ApplyAndComplete(gameDir, places);
        }

        private int ApplyAndComplete(string gameDir, IList<MapLabelPlace> places)
        {
            using (var receipt = new CacheReceipt(gameDir, Fingerprint, places))
            {
                int written = ApplyCache(gameDir, places, receipt, true);
                CompleteCacheRefresh(gameDir, receipt);
                return written;
            }
        }

        internal void CompleteCacheRefresh(string gameDir, CacheReceipt receipt)
        {
            receipt.Save();
            WriteCacheBytes(Path.Combine(CacheDirectory(gameDir), ".chinese-map-tiles.sha256"), Encoding.ASCII.GetBytes(Fingerprint + "\n"));
        }

        internal int ApplyCache(string gameDir, IList<MapLabelPlace> places, CacheReceipt receipt, bool refreshFallback)
        {
            string cache = CacheDirectory(gameDir);
            List<string> existing = CacheFiles(cache);
            EnsureCacheBackup(gameDir);
            int written = 0;
            PlaceIndex index = places == null ? null : BuildPlaceIndex(places);
            foreach (Tile tile in tiles.Values)
            {
                string path = Path.Combine(cache, tile.Z.ToString(), tile.X.ToString(), tile.Y + ".png");
                string key = Key(tile.Z, tile.X, tile.Y);
                if (receipt.Matches(key, path)) continue;
                int candidates, rendered;
                byte[] png = places == null ? tile.Png
                    : MapLabelRenderer.RenderPackedTile(tile.Png, tile.LabelCount, index.Lookup(tile.Z, tile.X, tile.Y), this,
                        tile.Z, tile.X, tile.Y, out candidates, out rendered);
                if (WriteCacheBytes(path, png)) written++;
                receipt.Record(key, png);
            }
            if (refreshFallback && places != null)
            {
                var refreshed = new HashSet<string>(StringComparer.Ordinal);
                foreach (LegacyRule rule in legacyRules)
                {
                    if (!places.Any(p => p.IsLegacy && p.Name == rule.Place.Name
                        && LegacyKey(p, rule.Z) == LegacyKey(rule.Place, rule.Z))) continue;
                    int n = 1 << rule.Z;
                    double latitude = Math.Max(-85.05112878, Math.Min(85.05112878, rule.Place.Lat)) * Math.PI / 180.0;
                    int x = Math.Max(0, Math.Min(n - 1, (int)Math.Floor((rule.Place.Lon + 180.0) / 360.0 * n)));
                    int y = Math.Max(0, Math.Min(n - 1, (int)Math.Floor((1.0 - Math.Log(Math.Tan(latitude)
                        + 1.0 / Math.Cos(latitude)) / Math.PI) / 2.0 * n)));
                    // The legacy renderer includes both edges: a boundary point can occur in four tiles.
                    for (int tx = Math.Max(0, x - 1); tx <= Math.Min(n - 1, x + 1); tx++)
                    for (int ty = Math.Max(0, y - 1); ty <= Math.Min(n - 1, y + 1); ty++)
                    {
                        if (!MapLabelRenderer.TileContains(rule.Place, tx / (double)n * 360.0 - 180.0,
                            (tx + 1) / (double)n * 360.0 - 180.0, MapLabelRenderer.TileLatitude(ty + 1, n),
                            MapLabelRenderer.TileLatitude(ty, n))) continue;
                        string key = Key(rule.Z, tx, ty);
                        string path = Path.Combine(cache, rule.Z.ToString(), tx.ToString(), ty + ".png");
                        AssertPlainPath(path);
                        if (tiles.ContainsKey(key) || !File.Exists(path) || !refreshed.Add(key)) continue;
                        if (receipt.Matches(key, path)) continue;
                        int candidates, rendered;
                        byte[] png = MapLabelRenderer.RenderTileBytes(places, this, rule.Z, tx, ty, out candidates, out rendered);
                        if (WriteCacheBytes(path, png)) written++;
                        receipt.Record(key, png);
                    }
                }
                // Existing fallback PNGs bypass the tile server. Refresh hosts with stock or
                // administrative labels at this zoom, including both sides of inclusive seams.
                if (index.OutsideHosts.Count > 0 || receipt.HasPrevious)
                {
                    foreach (string path in existing)
                    {
                        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                        string[] parts = path.Substring(cache.Length + 1).Split(Path.DirectorySeparatorChar);
                        int z, x, y;
                        if (parts.Length != 3 || !int.TryParse(parts[0], out z) || z < 0 || z > 10
                            || !int.TryParse(parts[1], out x) || !int.TryParse(Path.GetFileNameWithoutExtension(parts[2]), out y)) continue;
                        int n = 1 << z;
                        string key = Key(z, x, y);
                        if (x < 0 || x >= n || y < 0 || y >= n || tiles.ContainsKey(key) || !refreshed.Add(key)) continue;
                        if (!index.OutsideHosts.Contains(key) && !receipt.PreviouslyManaged(key)) continue;
                        if (receipt.Matches(key, path)) continue;
                        int candidates, rendered;
                        byte[] png = MapLabelRenderer.RenderTileBytes(places, this, z, x, y, out candidates, out rendered);
                        if (WriteCacheBytes(path, png)) written++;
                        receipt.Record(key, png);
                    }
                }
            }
            return written;
        }
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
                        Name = name, Lat = lat, Lon = lon, Level = 2, Rank = 1, IsLegacy = false,
                        MinZoom = (int)Math.Ceiling(minZoom), MaxZoom = 8
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

    // The same administrative area can have a different translation and an anchor several
    // tiles away in the pack. Only reviewed identities at a covered zoom can skip the overlay.
    internal static class PackedRegionCoverage
    {
        // BEGIN GENERATED PACKED REGION COVERAGE
        // Reviewed map/administrative entity joins: edf0f5ef37123e26b9ee6e68bbd2c412d998f6e6bfc534cfc7ec6375108f9e2e
        internal const string Fingerprint = "157f35c045bf015c75f43854b4bb30c05dc52f5cdbf0fd919621d7d0f0f7b99a";
        private static readonly Dictionary<string, int> ZoomMasks = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "万丹省/C0C840B8/42D45581", 192 },
            { "下诺夫哥罗德州/42608DB9/423319B4", 192 },
            { "不列颠哥伦比亚/425AC6F7/C2F952F2", 192 },
            { "东加里曼丹省/3FA50481/42E8B53F", 192 },
            { "东努沙登加拉省/C10959B4/42F160C5", 64 },
            { "东爪哇省/C0FC339C/42E13B64", 192 },
            { "中加里曼丹省/BFEBCD36/42E2926F", 192 },
            { "中央邦/41B785F0/429CD7C2", 224 },
            { "中爪哇省/C0E94880/42DBCAC1", 192 },
            { "中苏拉威西省/BFA6872B/42F04E56", 192 },
            { "乌德穆尔特共和国/42655F8A/42532ECC", 192 },
            { "云南省/41C3AEB2/42CB526F", 224 },
            { "亚利桑那州/420937E9/C2DFDEB8", 112 },
            { "亚拉巴马州/42036B9F/C2AD6FD2", 192 },
            { "伊利诺伊州/421FC8B4/C2B265F0", 240 },
            { "伯南布哥/C1079097/C2152EE6", 96 },
            { "佐治亚州/42036B36/C2A6D0CB", 240 },
            { "佛罗里达州/41E14120/C2A33EE0", 128 },
            { "佛蒙特州/42305ABA/C29176A1", 112 },
            { "俄亥俄州/42205E9E/C2A55803", 224 },
            { "俄克拉荷马州/420DCED9/C2C24305", 224 },
            { "俄勒冈州/422F554D/C2F0C5A2", 240 },
            { "內布拉斯加州/42260034/C2C75EFA", 240 },
            { "内华达州/421DB838/C2EA0A3D", 240 },
            { "内蒙古自治区/4226600D/42DF3EFA", 224 },
            { "列宁格勒州/426FF646/4203182B", 64 },
            { "加利福尼亚州/4212FF97/C2EF2E98", 240 },
            { "北卡罗来纳州/420E75F7/C29DBB64", 240 },
            { "北方邦/41D5C2C4/42A1FE42", 224 },
            { "北苏拉威西省/3F5A5E35/42F8E45A", 192 },
            { "北苏门答腊省/40173B64/42C66EA5", 192 },
            { "北达科他州/423DDEB8/C2C89AA0", 240 },
            { "北里约格朗德/C0B52BD4/C2123055", 96 },
            { "北阿坎德邦/41F075F7/429E9176", 224 },
            { "北马鲁古省/C0128588/42FBF3B6", 192 },
            { "华盛顿哥伦比亚特区/421B919D/C29A05C9", 128 },
            { "华盛顿州/423DF22D/C2F0B8D5", 240 },
            { "南加里曼丹省/C0407454/42E6E6E9", 192 },
            { "南卡罗来纳州/42076E63/C2A14B51", 224 },
            { "南苏拉威西省/C06FA440/42EFFAE1", 192 },
            { "南苏门答腊省/C058793E/42D02560", 192 },
            { "南达科他州/4231E268/C2C8828F", 240 },
            { "南里奥格兰德州/C1EDD254/C2569FBE", 96 },
            { "南马托格罗索州/C1A567A1/C25A3368", 32 },
            { "卡尔梅克共和国/423A138F/4235DF56", 192 },
            { "卡拉恰伊-切尔克斯共和国/422ED446/4226BF14", 192 },
            { "卡纳塔克邦/4165E3BD/42975581", 192 },
            { "印古什共和国/422D76E3/42336320", 192 },
            { "印第安纳州/421F8CB3/C2AC477A", 224 },
            { "古吉拉特邦/41B60034/428E9A44", 224 },
            { "吉林省/422D30F2/42FCEE98", 224 },
            { "哈卡斯共和国/4255750B/42B3AE56", 64 },
            { "哈里亚纳邦/41E8CD6A/42988AC1", 224 },
            { "喀拉拉邦/4125DD98/42990C22", 192 },
            { "喜马偕尔邦/41FD676D/429A9333", 192 },
            { "四川省/41F45845/42CCC49C", 224 },
            { "图瓦共和国/424E6B9F/42BBFC43", 64 },
            { "圣保罗州/C1B1D048/C2421518", 96 },
            { "圣卡塔琳娜州/C1D85048/C24CA268", 96 },
            { "堪察加邊疆區/42622148/43202FDF", 64 },
            { "堪萨斯州/421A0000/C2C4A96C", 224 },
            { "塔斯马尼亚州/C2288D9F/43129A5E", 128 },
            { "塞尔希培州/C1297803/C21588CE", 96 },
            { "塞阿腊/C0AC0831/C21D5F21", 96 },
            { "奥里萨邦/41A50000/42A8DE42", 224 },
            { "威斯康星州/42317BCD/C2B32A8C", 240 },
            { "宁夏回族自治区/4214B46E/42D41375", 224 },
            { "安大略省/424A18FC/C2A996AE", 192 },
            { "安得拉邦/4183E219/429E6A7F", 224 },
            { "安徽省/41FFA12D/42EA8189", 192 },
            { "宾夕法尼亚州/422370BE/C29B3803", 240 },
            { "密歇根州/422DBCB9/C2A9E553", 240 },
            { "密苏里州/421A31DE/C2B8E45A", 224 },
            { "密西西比州/4203767A/C2B37014", 240 },
            { "山东省/42114EF3/42EC3A5E", 224 },
            { "山西省/421708CE/42E0C72B", 224 },
            { "巴什科尔托斯坦共和国/4258D59B/42623AE1", 192 },
            { "巴伊亚/C145D773/C22735F7", 96 },
            { "巴厘省/C105758E/42E65BA6", 64 },
            { "巴布亚省/C0837660/430AB062", 192 },
            { "巴拉那州/C1C54B5E/C24D4A8C", 96 },
            { "布里亚特共和国/42539FD9/42DAAE98", 64 },
            { "帕拉/C08E2DE0/C25298AE", 96 },
            { "帕拉伊巴/C0EAF41F/C2111724", 96 },
            { "广东省/41BE56D6/42E370A4", 224 },
            { "广西壮族自治区/41BDF5F7/42D98312", 224 },
            { "康涅狄格州/4226982B/C29184D0", 224 },
            { "廖内省/3ECB367A/42CB7D71", 192 },
            { "廖内群岛省/BE1205BC/42D133B6", 192 },
            { "弗吉尼亚州/4216F611/C29C7C78", 224 },
            { "得克萨斯州/41F90C4A/C2C5857A", 240 },
            { "怀俄明州/422BFFE6/C2D71AA0", 240 },
            { "恰蒂斯加尔邦/41AE6F69/42A49D22", 224 },
            { "戈亚斯/C17DCED9/C246507D", 96 },
            { "托坎廷斯/C1239168/C2410034", 32 },
            { "拉贾斯坦邦/41D5F972/4293B611", 224 },
            { "拉达克/42081A37/429AF611", 192 },
            { "新不伦瑞克/423A542C/C284E95F", 192 },
            { "新墨西哥州/420A0034/C2D40C4A", 240 },
            { "新斯科舍/4234EACE/C27B3EC5", 192 },
            { "新泽西州/42202DFA/C294EE3C", 224 },
            { "新疆维吾尔自治区/42247CEE/42AAD9C1", 224 },
            { "新罕布什尔州/422E65AF/C28F429C", 224 },
            { "旁遮普邦/41F8322D/4296C09D", 224 },
            { "明尼苏达州/42383C9F/C2BABA5E", 240 },
            { "曼尼托巴/425B6666/C2C2C3FE", 192 },
            { "曼尼普尔邦/41C5F41F/42BBB100", 192 },
            { "朗多尼亚州/C12F8A09/C27C935B", 96 },
            { "果阿邦/41750347/4293FD7E", 224 },
            { "查谟-克什米尔邦/4207DCFB/4299476D", 224 },
            { "梅加拉亚邦/41CBD7DC/42B69B30", 224 },
            { "楚瓦什共和国/425DF405/423CAA30", 192 },
            { "楠榜省/C09D6873/42D21E35", 192 },
            { "比哈尔邦/41CD7B7F/42ABA076", 224 },
            { "汉特-曼西自治区/4275B9DB/428EC2DE", 64 },
            { "江苏省/4203F007/42EFE24E", 224 },
            { "江西省/41DD1E1B/42E808B4", 224 },
            { "河北省/421A14FE/42E6A0C5", 224 },
            { "河南省/42079F3B/42E2F7CF", 224 },
            { "泰米尔纳德邦/41304120/429C8A72", 224 },
            { "浙江省/41E8DE01/42EFF0A4", 224 },
            { "海南省/41997DF4/42DBA666", 224 },
            { "湖南省/41DD5567/42DF6C8B", 224 },
            { "滨海边疆区/423372E5/43069810", 192 },
            { "爱德华王子岛/42395DE7/C27D8B78", 192 },
            { "爱达荷州/422F2148/C2E44419", 240 },
            { "特伦甘纳邦/418B6B1C/429CEFD2", 224 },
            { "特拉华州/421B767A/C296D289", 240 },
            { "特里普拉邦/41BED0B1/42B767FD", 192 },
            { "犹他州/421E00B8/C2DF1687", 240 },
            { "犹太自治州/42428F5C/4304C20C", 192 },
            { "甘肃省/421AF50B/42C97852", 224 },
            { "田纳西州/420F016F/C2ACAED9", 240 },
            { "皮奥伊州/C101645A/C22CCA23", 96 },
            { "福建省/41D34396/42EC5B23", 224 },
            { "科罗拉多州/421BFFCC/C2D31604", 224 },
            { "米佐拉姆邦/41B9A12D/42B9AE8A", 224 },
            { "米纳斯吉拉斯/C194B74C/C231EC57", 96 },
            { "纽约州/422CCB92/C296A5FE", 240 },
            { "纽芬兰-拉布拉多/4242A3A3/C260DE1B", 64 },
            { "缅因州/4234978D/C28A6505", 240 },
            { "罗得岛州/42267F2E/C28F0433", 16 },
            { "罗赖马州/3FF81062/C275547B", 224 },
            { "聖埃斯皮里圖州/C19D8866/C2222CA5", 96 },
            { "肯塔基州/421598FC/C2AB2553", 224 },
            { "艾奥瓦州/42282B51/C2BAC738", 208 },
            { "莫尔多瓦共和国/4258762B/4231DA37", 192 },
            { "莫斯科州/425C9A6B/421AAF1B", 64 },
            { "萨哈林州/423D6440/4318778D", 64 },
            { "萨斯喀彻温/4259FC6A/C2D35D2F", 192 },
            { "蒙大拿州/423BFC6A/C2DC1687", 240 },
            { "西加里曼丹省/BD8240B8/42DE9BA6", 192 },
            { "西努沙登加拉省/C109233A/42E89687", 192 },
            { "西孟加拉邦/41B86B1C/42AF7532", 192 },
            { "西巴布亞省/BFA9A36E/4304D333", 192 },
            { "西爪哇省/C0DD0B0F/42D746A8", 64 },
            { "西維吉尼亞州/421A919D/C2A16CF4", 240 },
            { "西苏拉威西省/C02C8CE7/42EEA1CB", 128 },
            { "西苏门答腊省/BF24816F/42C938D5", 64 },
            { "西藏自治区/41FB9CAC/42B0D3D0", 224 },
            { "贵州省/41D66D29/42D51E35", 224 },
            { "贾坎德邦/41BC2D43/42AA1DE7", 224 },
            { "路易斯安那州/41F4381D/C2B7FF8A", 240 },
            { "车臣共和国/422CDC78/4237072B", 128 },
            { "辽宁省/42258B44/42F623D7", 96 },
            { "达吉斯坦共和国/422970A4/423C91D1", 128 },
            { "那加兰邦/41D0E00D/42BD21FF", 192 },
            { "邦加-勿里洞省/C03D5326/42D5A354", 192 },
            { "里约热内卢州/C1B33D3C/C22C75F7", 32 },
            { "重庆市/41F0236E/42D775C3", 224 },
            { "阿克里州/C10EDB23/C28C985F", 96 },
            { "阿尔伯塔省/425D205C/C2E60000", 192 },
            { "阿尔泰边疆区/4251B50B/42A5BBC0", 64 },
            { "阿拉戈斯州/C11C61E5/C212C44D", 96 },
            { "阿拉斯加州/4282B8C8/C3179AA0", 48 },
            { "阿穆尔州/4253B1C4/43005AA0", 64 },
            { "阿肯色州/420B0674/C2B8491D", 240 },
            { "阿萨姆邦/41D2A440/42B9FC5D", 160 },
            { "阿迪格共和国/4231DCFB/42208467", 64 },
            { "阿马帕/3FB4AF4F/C24EBC9F", 96 },
            { "阿鲁纳恰尔邦/41E33EAB/42BCEF42", 192 },
            { "陕西省/420715D0/42D8B9DB", 192 },
            { "鞑靼斯坦共和国/425D7EFA/424AF213", 128 },
            { "马哈拉施特拉邦/419BC745/4296EDED", 224 },
            { "马托格罗索州/C1564817/C25FB1AA", 96 },
            { "马拉尼昂州/C0A09BA6/C2358E56", 96 },
            { "马萨诸塞州/42297EE0/C28FFFA4", 224 },
            { "马里兰州/421D8CB3/C29A173F", 192 },
            { "马里埃尔共和国/426229AD/423FA704", 192 },
            { "马鲁古省/C103F838/43002937", 64 },
            { "魁北克/42510986/C2936F00", 192 },
            { "黑龙江省/423B6162/42FFF0A4", 224 },
        };
        // END GENERATED PACKED REGION COVERAGE

        internal static bool Contains(string fingerprint, MapLabelPlace place, int z)
        {
            if (place.Rank != 1 || z > 8 || fingerprint != Fingerprint) return false;
            string key = place.Name + "/" + BitConverter.ToUInt32(BitConverter.GetBytes(place.Lat), 0).ToString("X8")
                + "/" + BitConverter.ToUInt32(BitConverter.GetBytes(place.Lon), 0).ToString("X8");
            int mask;
            return ZoomMasks.TryGetValue(key, out mask) && (mask & (1 << z)) != 0;
        }
    }

    internal static class PackedWwpCoverage
    {
        // BEGIN GENERATED PACKED WWP COVERAGE
        internal const string Fingerprint = "157f35c045bf015c75f43854b4bb30c05dc52f5cdbf0fd919621d7d0f0f7b99a";
        private static readonly Dictionary<string, int> ZoomMasks = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "0/不丹/41DC0000/42B50000", 112 },
            { "0/东地中海/420B8B31/41B2F1FE", 1024 },
            { "0/东帝汶/C10D5552/42FB8000", 120 },
            { "0/东海/41E80000/42FA0000", 480 },
            { "0/中国/420C0000/42D20000", 120 },
            { "0/中非共和国/40E00000/41A80000", 96 },
            { "0/丹麦/42600000/41200000", 232 },
            { "0/乌克兰/42440000/42000000", 120 },
            { "0/乌兹别克斯坦/4226AAAC/427F5554", 104 },
            { "0/乌干达/3FA00000/42020000", 104 },
            { "0/乌拉圭/C2040000/C2600000", 32 },
            { "0/乍得/41700000/41980000", 120 },
            { "0/也门/41780000/423E0000", 120 },
            { "0/亚丁湾/41400000/42400000", 64 },
            { "0/亚得里亚海/422C0000/41800000", 192 },
            { "0/亚美尼亚/42210000/42340000", 336 },
            { "0/亚速海/42380000/42100000", 192 },
            { "0/以色列/41FC0000/420B0000", 48 },
            { "0/伊利湖/4229001A/C2A1FFDD", 8 },
            { "0/伊拉克/42040000/42300000", 56 },
            { "0/伊朗/42000000/42540000", 120 },
            { "0/佛得角/41800000/C1C00000", 192 },
            { "0/俄罗斯/42700000/42C80000", 56 },
            { "0/保加利亚/422AAAAC/41CA0000", 88 },
            { "0/俾斯麦海/C0800000/43140000", 96 },
            { "0/克罗地亚/4234AAAC/41780000", 64 },
            { "0/冈比亚/41580000/C1780000", 72 },
            { "0/冰岛/42820000/C1900000", 56 },
            { "0/几内亚/412D5552/C12AAAAE", 112 },
            { "0/几内亚比绍/41400000/C1700000", 64 },
            { "0/几内亚湾/40000000/40200000", 96 },
            { "0/列支敦士登/423CAAAC/41188885", 64 },
            { "0/刚果共和国/BF800000/41780000", 96 },
            { "0/刚果民主共和国/C0200000/41BC0000", 112 },
            { "0/利古里亚海/422E0000/41100000", 128 },
            { "0/利比亚/41E00000/41880000", 120 },
            { "0/利比里亚/40D00000/C1180000", 120 },
            { "0/加利福尼亚湾/41D63348/C2DD2200", 128 },
            { "0/加勒比海/41700126/C2960025", 128 },
            { "0/加拿大/42706F47/C2E34900", 72 },
            { "0/加纳/4101999A/BF99999A", 112 },
            { "0/加蓬/BF800000/413C0000", 64 },
            { "0/匈牙利/423C0000/41A00000", 96 },
            { "0/北冰洋/42AA0000/00000000", 48 },
            { "0/北大西洋/42200000/C2200000", 64 },
            { "0/北海/425D5554/40400000", 112 },
            { "0/北马其顿/4226AAAC/41AE0000", 608 },
            { "0/南海/41700000/42E60000", 992 },
            { "0/南苏丹/40F00000/41F00000", 112 },
            { "0/南非/C1E80000/41C00000", 56 },
            { "0/博茨瓦纳/C1B00000/41C00000", 48 },
            { "0/卡塔尔/41CC0000/424D0000", 80 },
            { "0/卢旺达/C0000000/41F00000", 200 },
            { "0/卢森堡/42470000/40C5555C", 120 },
            { "0/印度/41B00000/429E0000", 80 },
            { "0/印度尼西亚/C0A00000/42F00000", 184 },
            { "0/印度洋/C1200000/428C0000", 104 },
            { "0/危地马拉/41780000/C2B48000", 8 },
            { "0/厄勒海峡/425F5554/414AAAAE", 128 },
            { "0/厄瓜多尔/BFA00000/C29C8000", 72 },
            { "0/厄立特里亚/41700000/421C0000", 112 },
            { "0/叙利亚/420C0000/42180000", 96 },
            { "0/古巴/41B00000/C29F0000", 8 },
            { "0/台湾/41C00000/42F20000", 120 },
            { "0/吉尔吉斯斯坦/42260000/42960000", 80 },
            { "0/吉布提/413D5552/422A0000", 112 },
            { "0/哈萨克斯坦/42400000/42880000", 104 },
            { "0/哈马黑拉海/BF800000/43010000", 128 },
            { "0/哥伦比亚/40800000/C2928000", 8 },
            { "0/喀拉海/42980000/42A00000", 48 },
            { "0/喀麦隆/40C00000/41480000", 112 },
            { "0/图瓦卢/C1084669/43332510", 64 },
            { "0/土库曼斯坦/421F0000/426EAAAC", 112 },
            { "0/土耳其/421C0000/420C0000", 360 },
            { "0/圣多美和普林西比/3F800000/40E00000", 32 },
            { "0/圣文森特和格林纳丁斯/41515552/C274CCCD", 512 },
            { "0/圣马力诺/422FBF2E/4147240B", 96 },
            { "0/圭亚那/40A00000/C26C0000", 64 },
            { "0/坦桑尼亚/C0C00000/420C0000", 56 },
            { "0/埃及/41D80000/41F00000", 120 },
            { "0/埃塞俄比亚/41100000/421E0000", 104 },
            { "0/基里巴斯/3FB5E354/432CFBE7", 96 },
            { "0/塔吉克斯坦/421C0000/428E0000", 104 },
            { "0/塔斯曼海/C2200000/43200000", 48 },
            { "0/塞兰海/C0200000/43000000", 224 },
            { "0/塞内加尔/41680000/C1640000", 112 },
            { "0/塞尔维亚/42334693/41A3AE0A", 112 },
            { "0/塞拉利昂/41080000/C1380000", 96 },
            { "0/塞浦路斯/420C0000/42040000", 56 },
            { "0/塞舌尔/C092AAA4/425EAAAC", 64 },
            { "0/墨西哥/41B80000/C2CC0000", 136 },
            { "0/墨西哥湾/41C80000/C2B40000", 128 },
            { "0/多哥/410AAAAE/3F8AAA8F", 64 },
            { "0/多米尼克/41780000/C2755554", 64 },
            { "0/奥地利/423D5554/41555552", 112 },
            { "0/委内瑞拉/41000000/C2840000", 64 },
            { "0/孟加拉国/41C00000/42B40000", 504 },
            { "0/孟加拉湾/4158A090/42AF5780", 240 },
            { "0/安哥拉/C1480000/41940000", 48 },
            { "0/安提瓜和巴布达/41886666/C2773333", 64 },
            { "0/安达曼海/41200000/42BE0000", 496 },
            { "0/安道尔/422A3333/3FCAAA8F", 120 },
            { "0/尼日利亚/41200000/41000000", 112 },
            { "0/尼日尔/41900000/41100000", 120 },
            { "0/尼泊尔/41E00000/42A80000", 64 },
            { "0/巴伦支海/42940000/42100000", 48 },
            { "0/巴利阿里海/42200000/40000000", 448 },
            { "0/巴基斯坦/41F00000/428C0000", 104 },
            { "0/巴巴多斯/4152AAAE/C26E2221", 64 },
            { "0/巴布亚新几内亚/C0C00000/43130000", 56 },
            { "0/巴拉圭/C1BAAAA9/C2680000", 104 },
            { "0/巴拿马/41100000/C2A00000", 72 },
            { "0/巴林/41D04443/424A3333", 96 },
            { "0/巴芬湾/42940000/C2880000", 32 },
            { "0/巴西/C1200000/C25C0000", 104 },
            { "0/布基纳法索/41480000/BFD55571", 56 },
            { "0/布隆迪/C0600000/41F00000", 192 },
            { "0/希腊/421C0000/41B00000", 120 },
            { "0/帝汶海/C1280000/42FC0000", 96 },
            { "0/德国/424E0000/41280000", 96 },
            { "0/意大利/422B5554/414D5552", 96 },
            { "0/所罗门海/C10DD053/431A8300", 96 },
            { "0/所罗门群岛/C1000000/431F0000", 120 },
            { "0/托米尼湾/BF18F5C3/42F2307F", 128 },
            { "0/拉克代夫海/41000000/42960000", 96 },
            { "0/拉布拉多海/42640000/C25C0000", 32 },
            { "0/拉普捷夫海/42980000/42FC0000", 48 },
            { "0/拉脱维亚/42640000/41C80000", 120 },
            { "0/挪威/42780000/41200000", 120 },
            { "0/挪威海/428C0000/40A00000", 48 },
            { "0/捷克/42470000/41700000", 96 },
            { "0/摩尔多瓦/423D0000/41E4AAA9", 96 },
            { "0/摩洛哥/42000000/C0C00000", 56 },
            { "0/摩纳哥/422EECF7/40ED68B2", 96 },
            { "0/文莱/40900000/42E55556", 112 },
            { "0/斐济/C1900000/43320000", 112 },
            { "0/斯威士兰/C1D40000/41FC0000", 32 },
            { "0/斯洛伐克/4242AAAC/419C0000", 96 },
            { "0/斯洛文尼亚/42385554/41700000", 112 },
            { "0/斯里兰卡/40F80000/42A18000", 120 },
            { "0/新加坡/3FAEEF0B/42CF999A", 96 },
            { "0/新西兰/C2280000/432E0000", 56 },
            { "0/日本/420EBDCF/430BC0CB", 120 },
            { "0/日本海/422E0000/4307C000", 496 },
            { "0/智利/C1F00000/C28E0000", 40 },
            { "0/望加锡海峡/C0000000/42EB0000", 128 },
            { "0/朝鲜/42200000/42FE0000", 112 },
            { "0/林肯海/42A60000/C2680000", 112 },
            { "0/柬埔寨/41500000/42D20000", 112 },
            { "0/格林纳达/4141DDE1/C276AAAC", 64 },
            { "0/格陵兰海/42960000/C1200000", 48 },
            { "0/格鲁吉亚/4227FFFB/422DFFE6", 40 },
            { "0/梵蒂冈/42279BE7/414740B8", 80 },
            { "0/楚科奇海/428A0000/C32E0000", 40 },
            { "0/比利时/424B0000/40900000", 64 },
            { "0/比斯开湾/42300000/C0800000", 32 },
            { "0/毛里塔尼亚/41A20000/C1280000", 120 },
            { "0/毛里求斯/C1A26666/42665554", 32 },
            { "0/汤加/C1A00000/C32F0000", 96 },
            { "0/沙特阿拉伯/41C80000/42340000", 80 },
            { "0/法国/42380000/40000000", 120 },
            { "0/波兰/42500000/41A00000", 80 },
            { "0/波尼湾/C05DABC9/42F147D3", 128 },
            { "0/波弗特海/428C0000/C3070000", 40 },
            { "0/波罗的海/42600000/41900000", 224 },
            { "0/泰国/41780000/42CA0000", 608 },
            { "0/泰国湾/41200000/42CC0000", 496 },
            { "0/津巴布韦/C1980000/41EE0000", 56 },
            { "0/海地/41980000/C290D556", 8 },
            { "0/澳大利亚/C1C80000/43070000", 56 },
            { "0/濑户内海/42095554/43058000", 512 },
            { "0/爪哇海/C0A00000/42DC0000", 112 },
            { "0/爱奥尼亚海/421C0000/41980000", 480 },
            { "0/爱尔兰/42540000/C1000000", 112 },
            { "0/爱沙尼亚/426C0000/41D00000", 120 },
            { "0/爱琴海/421C0000/41C80000", 384 },
            { "0/特立尼达和多巴哥/41300000/C2740000", 64 },
            { "0/玻利维亚/C1880000/C2820000", 72 },
            { "0/珊瑚海/C1A00000/431B0000", 48 },
            { "0/班达海/C05D0C89/42F5730E", 112 },
            { "0/瑙鲁/BF045A1D/4326EED9", 64 },
            { "0/瑞典/42780000/41700000", 112 },
            { "0/瓦努阿图/C1800000/43270000", 120 },
            { "0/白俄罗斯/42540000/41E00000", 120 },
            { "0/白海/42830000/42180000", 48 },
            { "0/科威特/41EC0000/423F0000", 368 },
            { "0/科摩罗/C143BBB8/4231C839", 32 },
            { "0/科特迪瓦/41000000/C0B00000", 96 },
            { "0/科索沃/422A5554/41A75557", 320 },
            { "0/秘鲁/C1200000/C2968000", 104 },
            { "0/突尼斯/42080000/41100000", 112 },
            { "0/立陶宛/425DAAAC/41C00000", 112 },
            { "0/第勒尼安海/42200000/41400000", 448 },
            { "0/索马里/40C00000/42400000", 240 },
            { "0/红海/41A22388/421A2361", 64 },
            { "0/纳米比亚/C1B00000/41880000", 296 },
            { "0/缅甸/41A80000/42C00000", 120 },
            { "0/罗马尼亚/42380000/41C80000", 120 },
            { "0/美国/421F0A3D/C2C50000", 8 },
            { "0/老挝/41900000/42D20000", 624 },
            { "0/肯尼亚/3F800000/42180000", 112 },
            { "0/芬兰/42800000/41D00000", 56 },
            { "0/苏丹/41800000/41F00000", 120 },
            { "0/苏拉威西海/40400000/42F40000", 224 },
            { "0/苏禄海/41100000/42F00000", 240 },
            { "0/苏里南/40800000/C2600000", 64 },
            { "0/英国/425B08A4/C02C7FF6", 112 },
            { "0/荷兰/42510000/40B80000", 120 },
            { "0/菲律宾/41500000/42F40000", 120 },
            { "0/菲律宾海/41A00000/43070000", 112 },
            { "0/萨摩亚/C15CCCCD/C32C2222", 32 },
            { "0/葡萄牙/421EC72B/C10216D1", 104 },
            { "0/蒙古/42380000/42D20000", 120 },
            { "0/西北航道/42960000/C2C80000", 8 },
            { "0/西地中海/4220FBE5/40BF3FFB", 256 },
            { "0/西班牙/42200000/C0800000", 80 },
            { "0/贝宁/41180000/40100000", 352 },
            { "0/赞比亚/C1655552/41E40000", 56 },
            { "0/赤道几内亚/3FD9999A/41280000", 88 },
            { "0/越南/41815557/42D7AAAA", 112 },
            { "0/鄂霍次克海/425C0000/43160000", 112 },
            { "0/里海/42280000/42480000", 352 },
            { "0/阿富汗/42040000/42840000", 112 },
            { "0/阿尔及利亚/41E00000/40400000", 120 },
            { "0/阿尔巴尼亚/42240000/41A00000", 256 },
            { "0/阿拉伯海/414360FE/4280AC00", 112 },
            { "0/阿拉弗拉海/C1100000/43050000", 112 },
            { "0/阿拉斯加东南与不列颠哥伦比亚沿海水域/42535139/C302E9C0", 64 },
            { "0/阿曼/41A80000/42640000", 120 },
            { "0/阿曼湾/41C80000/42680000", 192 },
            { "0/阿根廷/C2080000/C2800000", 40 },
            { "0/阿联酋/41BE0000/425A0000", 96 },
            { "0/韩国/42120000/42FF8000", 120 },
            { "0/马尔代夫/404CCCCD/42920000", 96 },
            { "0/马尔马拉海/4222AAAC/41E20000", 256 },
            { "0/马拉维/C1580000/42080000", 32 },
            { "0/马来西亚/40200000/42E10000", 24 },
            { "0/马耳他/420FAAAC/4166EEEB", 96 },
            { "0/马达加斯加/C1A00000/423C0000", 48 },
            { "0/马里/41900000/C0000000", 1648 },
            { "0/马鲁古海/3E51A4BE/42FB3885", 160 },
            { "0/黄海/42100000/42F80000", 1984 },
            { "0/黎巴嫩/42075554/420F5554", 64 },
            { "0/黑山/422B0000/419A0000", 288 },
            { "1/丁迪古尔/4125E743/429BF5F2", 128 },
            { "1/万象/418FBBBD/42CD3333", 960 },
            { "1/万鸦老/3FBDB813/42F9B2A6", 128 },
            { "1/三亚/4191F1C9/42DB028F", 1920 },
            { "1/三宝颜/40DD2104/42F425D5", 192 },
            { "1/三投斯将军市/40C39BE5/42FA57E5", 192 },
            { "1/三明/41D1FD27/42EB3CBA", 1280 },
            { "1/三河市/4239616F/C291186C", 128 },
            { "1/三田町/420B89AB/43073A19", 512 },
            { "1/上海/41F9C71B/42F2EA87", 448 },
            { "1/上饶/41E3A032/42EBEFEC", 384 },
            { "1/下关/4207CCCD/4302F333", 512 },
            { "1/下卡姆斯克/425E8BD9/424F4C42", 256 },
            { "1/下塔吉尔/4267AD82/426FDC29", 64 },
            { "1/下诺夫哥罗德/4261508F/42300219", 192 },
            { "1/下龙/41A79A86/42D6258F", 512 },
            { "1/丘德达帕/4167A3D7/429DA5A6", 128 },
            { "1/东京/420EC20C/430BB114", 448 },
            { "1/东吁/41978B14/42C0DE40", 384 },
            { "1/东吉/41BF21E0/42B4CDFD", 256 },
            { "1/东方市/C1CC13E8/C25A71C7", 64 },
            { "1/东汉普顿/42142644/C298A9C9", 128 },
            { "1/东海/42162CF2/43011B60", 1024 },
            { "1/东莞/41B86420/42E37D4C", 256 },
            { "1/东陵/422741FD/42F726D3", 512 },
            { "1/个旧/41BAE305/42CE4EB4", 192 },
            { "1/丰中/420B2138/43077825", 512 },
            { "1/丰沙尔/42028888/C1873333", 128 },
            { "1/临汾/42105B06/42DF09AC", 896 },
            { "1/临沂/420C4093/42ECAF81", 1984 },
            { "1/临海/41E6C903/42F24192", 256 },
            { "1/丹东/42208445/42F8CA19", 1024 },
            { "1/丹佛/421EF4E4/C2D1F82B", 256 },
            { "1/丹吉尔/420F11AF/C0B9978D", 128 },
            { "1/丹巴德/41BE6A7A/42ACE2F0", 256 },
            { "1/丹戎槟榔/3F6AAAE3/42D0E666", 128 },
            { "1/丹老/4147085B/42C53358", 384 },
            { "1/丽水/420B0D16/42FF54A0", 1920 },
            { "1/丽江/41D6F348/42C87102", 128 },
            { "1/久姆里/42232D43/422F6191", 256 },
            { "1/久留米/42054445/43028444", 512 },
            { "1/乌兰乌德/424F4F10/42D73669", 64 },
            { "1/乌兰巴托/423FA17F/42D5C438", 192 },
            { "1/乌姆盖萨尔/41F04A23/423FAD94", 256 },
            { "1/乌季达/420AB8DD/BFF42D8C", 192 },
            { "1/乌尔米耶/42163601/42344DE0", 256 },
            { "1/乌山/42149BE0/42FE2420", 1024 },
            { "1/乌拉尔地区卡缅斯克/4261AC8B/4277BB4A", 128 },
            { "1/乌斯季卡缅诺戈尔斯克/4247E2BF/42A53633", 64 },
            { "1/乌日霍罗德/42427778/41B26666", 512 },
            { "1/乌杜皮/41555ACF/42957E0A", 128 },
            { "1/乌汶叻差他尼/4173D0A6/42D1B284", 384 },
            { "1/乌沙克/421AB1AD/41EB3F14", 896 },
            { "1/乌海/421EBCD9/42D5A1B4", 128 },
            { "1/乌维拉/C0594D40/41E91A32", 128 },
            { "1/乌苏里斯克/422F362E/4303F21F", 384 },
            { "1/乌贝兰迪亚/C1975950/C2411BE0", 64 },
            { "1/乌贝拉巴/C19DFC94/C23FBA4E", 64 },
            { "1/乌达内塔/417F9E26/42F12469", 1024 },
            { "1/乌里扬诺夫斯克/4259501E/42418BD9", 64 },
            { "1/乌隆他尼/418B534B/42CD9260", 896 },
            { "1/乌鲁木齐/422F342F/42AF336F", 64 },
            { "1/乌鲁阿潘/419B4A0E/C2CC1CE6", 128 },
            { "1/乐山/41EC7F87/42CF8719", 128 },
            { "1/乔尔卢/4224A2FD/41DE6666", 512 },
            { "1/乔斯/411EDB18/410E4620", 256 },
            { "1/乔治敦/40D9BE4D/C2689EFF", 192 },
            { "1/乔鲁姆/42223210/420BD036", 256 },
            { "1/九台/42309C29/42FBAA62", 384 },
            { "1/九江/41EDA354/42E8010E", 384 },
            { "1/九里/421664F7/42FE475F", 1024 },
            { "1/云浮/41B76D38/42E0143F", 512 },
            { "1/井里汶/C0D69A02/42D91D2F", 128 },
            { "1/亚丁/414C7896/4234258D", 64 },
            { "1/亚兹德/41FF2D82/42597852", 192 },
            { "1/亚历山大/41F9B9A1/41EFA465", 448 },
            { "1/亚历山德里亚/421B3828/C29A1806", 128 },
            { "1/亚历山德里亚/4242ADAE/420476B6", 512 },
            { "1/亚庇/40BF3261/42E82512", 128 },
            { "1/亚松森/C1CA67C0/C2668B2C", 64 },
            { "1/亚特兰大/4206FEFA/C2A8C6A5", 128 },
            { "1/亚的斯亚贝巴/41106647/421AFCD1", 320 },
            { "1/亚穆苏克罗/40DA41F2/C0A8DB0E", 64 },
            { "1/亚罗士打/40C3DF8F/42C8B864", 256 },
            { "1/亨德森/421028A7/C2E5F6C1", 1024 },
            { "1/亨茨维尔/420AEBE6/C2AD2C15", 128 },
            { "1/京斯敦/418FF9E6/C2999650", 128 },
            { "1/京都/420C1593/4307C0FC", 896 },
            { "1/什切青/4255B73C/4168D92B", 128 },
            { "1/什里夫波特/420219C1/C2BB8018", 128 },
            { "1/仁安羌/41A3B867/42BDBE0E", 256 },
            { "1/仁川/4215D36A/42FD6909", 1536 },
            { "1/仙台/421907C6/430CDDE0", 320 },
            { "1/仙台/42191112/430CDDDE", 320 },
            { "1/仙桃/41F2F766/42E2E2C9", 384 },
            { "1/代尔祖尔/420D57F1/42209038", 256 },
            { "1/代尼兹利/421718C0/41E8B333", 896 },
            { "1/代顿/421F092A/C2A8621B", 128 },
            { "1/仰光/41867137/42C04FEE", 384 },
            { "1/任抹/C102C0F6/42E36629", 64 },
            { "1/伊万诺-弗兰基夫斯克/4243AF9E/41C5AD82", 384 },
            { "1/伊万诺沃/4263FD1F/4223E2B4", 384 },
            { "1/伊兹密尔/4219A6A3/41D91B67", 960 },
            { "1/伊兹密特/42231159/41EF55E5", 512 },
            { "1/伊内格尔/42204FEF/41EC1B4D", 512 },
            { "1/伊利/42288452/C2A02B8D", 128 },
            { "1/伊卜里/41B9CE4C/42621019", 384 },
            { "1/伊基克/C1A1C34C/C28C473C", 64 },
            { "1/伊尔库茨克/425130ED/42D097C0", 64 },
            { "1/伊尔比德/420238E5/420F6666", 1792 },
            { "1/伊巴丹/40EC14F9/4079FA6E", 64 },
            { "1/伊帕廷加/C19BBF24/C22A258D", 64 },
            { "1/伊德利卜/420FB8F4/42128925", 768 },
            { "1/伊拉克利翁/420D4FBD/41C925B4", 1920 },
            { "1/伊拉姆市/42068CB3/4239B0D8", 384 },
            { "1/伊拉普阿托/41A569FC/C2CAB66A", 128 },
            { "1/伊斯坦布尔/42240E2C/41E798E7", 448 },
            { "1/伊斯帕尔塔/42170EC9/41F46AF2", 256 },
            { "1/伊斯梅利亚/41F4D58C/420116C9", 384 },
            { "1/伊斯法罕/42029C1E/424EB2D0", 192 },
            { "1/伊斯肯德伦/42125946/4210B1A2", 768 },
            { "1/伊春/423EE2BF/4300E013", 128 },
            { "1/伊比里特/C1A02CEF/C2303C4E", 64 },
            { "1/伊洛伊洛/412B26AB/42F520FE", 960 },
            { "1/伊热夫斯克/42636627/4254D163", 128 },
            { "1/伊瓜拉/4192CB53/C2C71427", 128 },
            { "1/伊瓜苏/C1CC61DA/C25A5A2C", 64 },
            { "1/伊瓦拉/3EB41355/C29C3EA2", 128 },
            { "1/伊瓦格/408E0B63/C29676E6", 128 },
            { "1/伊达贾伊/C1D74322/C242A5D4", 64 },
            { "1/伏尔加格勒/4242E0A8/423201E0", 192 },
            { "1/伏尔加顿斯克/423E0DF0/42289B06", 384 },
            { "1/休斯敦/41EE1B33/C2BEB9FF", 128 },
            { "1/休特佩克/41970D16/C2C65B06", 128 },
            { "1/伦敦/424E08BC/BE00C1FD", 64 },
            { "1/伯尔兹/423F0D75/41DF6F3F", 384 },
            { "1/伯尔尼/423BCAD8/40EE516E", 128 },
            { "1/伯明翰/4251ECF9/BFF32DA1", 64 },
            { "1/佐世保/4204A2FD/4301B908", 896 },
            { "1/佐贺/4204EEEE/43024CCD", 512 },
            { "1/佛山/41B836D3/42E24351", 256 },
            { "1/佛统/415D1D15/42C816AB", 512 },
            { "1/佛罗伦萨/422F1DF4/4133F0AE", 128 },
            { "1/佩塔提克瓦/42005929/420B8CC5", 1024 },
            { "1/佩奇/42385554/4191DDDC", 128 },
            { "1/佩斯卡拉/4229D567/41633ECB", 128 },
            { "1/佩洛塔斯/C1FE2CEF/C2515EB8", 64 },
            { "1/佩雷拉/409A06CD/C2976469", 128 },
            { "1/佩鲁贾/422C72E5/41463871", 128 },
            { "1/佬沃/41919518/42F13100", 448 },
            { "1/佳木斯/423B3274/430250FB", 192 },
            { "1/俄斯特拉发/424756AE/4192419E", 128 },
            { "1/保利斯塔瓦尔泽亚/C1B9B0ED/C23B5036", 64 },
            { "1/保定/421B6789/42E6FB06", 768 },
            { "1/保禄市/4138C487/42D79D8D", 1280 },
            { "1/信阳/42007DBA/42E42191", 384 },
            { "1/元山/421C9C72/42FEE321", 896 },
            { "1/先达/403D6873/42C6232D", 128 },
            { "1/光州/420C9E6F/42FDD4C4", 1728 },
            { "1/克列缅丘格/424445A7/4205AE80", 896 },
            { "1/克利夫兰/4225FF7D/C2A3640D", 128 },
            { "1/克勒克卡莱/421F6191/4206068B", 384 },
            { "1/克卢日-纳波卡/423B1112/41BCCCCD", 128 },
            { "1/克塔克/41A3B842/42ABC230", 128 },
            { "1/克孜勒/424EDBE0/42BCE821", 64 },
            { "1/克孜勒奥尔达/42336440/4282FFF2", 64 },
            { "1/克孜勒奥尔达/4233693F/428304B2", 64 },
            { "1/克尔曼/41F24404/426450AE", 128 },
            { "1/克尔曼沙阿/420941B6/423C428F", 384 },
            { "1/克拉克斯维尔/42121E7C/C2AEB80A", 128 },
            { "1/克拉斯诺亚尔斯克/426012D5/42B9BBFE", 64 },
            { "1/克拉斯诺达尔/42342DEB/421BE774", 128 },
            { "1/克拉科夫/42483EE8/419F7E1E", 192 },
            { "1/克拉约瓦/42314445/41BE6666", 128 },
            { "1/克拉马托尔斯克/4242E467/421639A4", 512 },
            { "1/克莱佩达/425EDE6F/41A8F0A4", 384 },
            { "1/克里希讷格尔/41BB3EFF/42B0FB41", 256 },
            { "1/克里沃罗格/423FA37E/42058592", 768 },
            { "1/克雷塔罗/41A4B459/C2C8C6B0", 128 },
            { "1/克麦罗沃/425D5554/42AC2AAA", 64 },
            { "1/全州/420F49AB/42FE4C3B", 1792 },
            { "1/八代/4202049C/430299C5", 512 },
            { "1/八尾/420A7778/4307999A", 512 },
            { "1/八户/42220000/430D8000", 384 },
            { "1/八打雁/415C1B71/42F21DE7", 384 },
            { "1/八王子/420E9F92/430B52EA", 256 },
            { "1/公主岭/422E00C5/42F9A3BC", 512 },
            { "1/六盘水/41D4C16A/42D1AAAA", 192 },
            { "1/兰州/42103A61/42CFAE03", 192 },
            { "1/兰开斯特/420ACACB/C2EC4603", 128 },
            { "1/兰辛/422AEE1C/C2A91C6E", 128 },
            { "1/关丹/4073B15B/42CEA6E9", 128 },
            { "1/关口/41E144D5/42E34112", 512 },
            { "1/关塔那摩/41A127D0/C2966B18", 128 },
            { "1/兴实达/418D320E/42BEEA02", 384 },
            { "1/内坦亚/420154E6/420B708F", 1536 },
            { "1/内比都/419DF5C3/42C0426B", 384 },
            { "1/内江/41ECAB17/42D21FD3", 192 },
            { "1/内洛尔/41674212/429FF936", 128 },
            { "1/内瓦/403B58E2/C2969054", 128 },
            { "1/内罗毕/BFA44428/42134445", 448 },
            { "1/冈山/420A999A/4305EEEF", 768 },
            { "1/冷水江/41DD8126/42DEDBE0", 512 },
            { "1/凑/4208DC5D/4307266D", 512 },
            { "1/凡城/4219FA71/422D886D", 384 },
            { "1/凤凰城/4205CB24/C2E025E9", 640 },
            { "1/凤城/4221D07F/42F824B2", 1536 },
            { "1/凯尔采/424B7B38/41A50529", 640 },
            { "1/凯恩斯/C1876263/4311C429", 64 },
            { "1/凯比尔堡/420C01C8/C0BCF88C", 128 },
            { "1/凯鲁万/420EB660/41218A91", 256 },
            { "1/函馆/42271A73/430CBC96", 448 },
            { "1/切图马尔/41941CF0/C2B09B8D", 128 },
            { "1/切尔克斯克/4230E4B1/42283B2B", 128 },
            { "1/切尔卡瑟/4245B6D3/42003F8F", 896 },
            { "1/切尔尼戈夫/424E05A4/41FA476A", 768 },
            { "1/切尔诺夫策/42412A7C/41CF85D1", 896 },
            { "1/切萨皮克/421346B2/C2988CC5", 128 },
            { "1/利伯维尔/3EC8E9F7/411741C8", 64 },
            { "1/利佩茨克/42526996/421E4875", 640 },
            { "1/利卡西/C12FBA7E/41D5E83E", 128 },
            { "1/利尼亚雷斯/C19B20FE/C22049F4", 64 },
            { "1/利川/42151DDF/42FEE28F", 1024 },
            { "1/利摩日/4237537D/3FA0FFEB", 128 },
            { "1/利沃夫/42475A61/41C02F98", 960 },
            { "1/利西昌斯克/42439E91/4219C4AE", 128 },
            { "1/利雅得/41C58079/423AE32D", 192 },
            { "1/利马索尔/420ABC7A/420426DA", 896 },
            { "1/别尔哥罗德/424A7166/42125213", 128 },
            { "1/别尔江斯克/423B10D6/421331E4", 256 },
            { "1/别府/42051E28/43037F5D", 512 },
            { "1/刻赤/4235715B/4211E1E5", 128 },
            { "1/前桥/4211999A/430B1555", 256 },
            { "1/前郭/423477EC/42F9B6A9", 256 },
            { "1/加兹温/42111338/42480433", 384 },
            { "1/加尔各答/41B48044/42B0B9E0", 256 },
            { "1/加拉加斯/4127CEE4/C285C225", 448 },
            { "1/加拉尼温斯/C10E1CC1/C2120236", 64 },
            { "1/加沙/41FC0000/4209DDDF", 1856 },
            { "1/加济安泰普/42143CDE/421587AE", 768 },
            { "1/加济阿巴德/41E552A3/429AE0D8", 64 },
            { "1/加的斯/412F24DD/42F69375", 896 },
            { "1/加的斯/42121E7F/C0C96DC6", 128 },
            { "1/加贝斯/4207869D/4121923A", 384 },
            { "1/加里宁格勒/425AD372/41A4166D", 192 },
            { "1/加鲁阿/4114D2A8/41565D05", 64 },
            { "1/努库斯/4229CFEF/426E70ED", 64 },
            { "1/努瓦克肖特/4190AFBD/C17FA7F0", 64 },
            { "1/勃固/418AAE83/42C0F674", 384 },
            { "1/勃生/41863BC8/42BD76D8", 384 },
            { "1/勒阿弗尔/4245F9A7/3DDC8217", 64 },
            { "1/勿里达/C10192A3/42E05611", 128 },
            { "1/包头/42229BE0/42DBA4FA", 192 },
            { "1/包奇/4124F712/411D8088", 256 },
            { "1/北九州/42075554/4302D555", 384 },
            { "1/北京/421FA148/42E8CB62", 960 },
            { "1/北榄/415996E6/42C93189", 512 },
            { "1/北海/40ACC56D/42C8BA47", 256 },
            { "1/北海/41ABDDDC/42DA3333", 896 },
            { "1/北皮奥里亚/4222DEC3/C2B32B24", 128 },
            { "1/北票/42272AF2/42F18EEF", 768 },
            { "1/北茹阿泽鲁/C0E6D163/C21D42D9", 64 },
            { "1/北见/422F3655/430FE40D", 128 },
            { "1/匹兹堡/4221C332/C29FFDE5", 128 },
            { "1/半田/420B8888/4308EEEF", 512 },
            { "1/华城/4214D3C9/42FDA241", 1024 },
            { "1/华沙/4250EB49/41A81820", 960 },
            { "1/华盛顿特区/421B9498/C29A129F", 128 },
            { "1/卑尔根/4271926C/40AA5F70", 64 },
            { "1/卑谬市/419698DD/42BE71BF", 384 },
            { "1/南京/42003F26/42ED8E39", 448 },
            { "1/南充/41F65C53/42D42B62", 192 },
            { "1/南卡希亚斯/C1E95830/C24CB7BF", 64 },
            { "1/南宁/41B6888A/42D8A223", 960 },
            { "1/南定市/41A3789B/42D45AC6", 384 },
            { "1/南昌/41E578C0/42E7B4C4", 64 },
            { "1/南浦/421AF333/42FAD0C9", 1536 },
            { "1/南票/42246494/42F17EEF", 512 },
            { "1/南萨哈林斯克/423BD0F8/430EBC6C", 192 },
            { "1/南通/42001F02/42F1BFDB", 384 },
            { "1/南邦/419256AC/42C6FC4C", 384 },
            { "1/南阳/4203FA98/42E110C9", 448 },
            { "1/博博迪乌拉索/4132D59B/C0898866", 320 },
            { "1/博卡罗/41BE4C6E/42ABE996", 256 },
            { "1/博尔本德尔/41AD2335/428B37F5", 128 },
            { "1/博山/4211EEEE/42EBAAAA", 1536 },
            { "1/博布鲁伊斯克/42548DB9/41E9C56D", 384 },
            { "1/博托沙尼/423F0000/41D55557", 384 },
            { "1/博格拉/41C6CECF/42B2BDFE", 256 },
            { "1/博洛尼亚/4231F9A9/41356B85", 128 },
            { "1/博蒙特/41F0B03B/C2BC3426", 128 },
            { "1/博里萨尔/41B5A3C7/42B4BD82", 384 },
            { "1/博阿维斯塔/4034764B/C272B17D", 128 },
            { "1/卜利达/4211E152/4034F909", 896 },
            { "1/占碑/BFCCCCCD/42CF3BBC", 128 },
            { "1/占贾/4222BB2B/42397137", 256 },
            { "1/卡利/405BFB6A/C2990B85", 192 },
            { "1/卡利亚里/421CEC13/4111E81F", 384 },
            { "1/卡加延德奥罗/4107B72C/42F94B60", 384 },
            { "1/卡卢加/425A1E01/42111A08", 128 },
            { "1/卡塔尼亚/4215F80B/41712066", 960 },
            { "1/卡塔赫纳/41266541/C2970765", 448 },
            { "1/卡塔赫纳/42166BA5/BF7C7992", 384 },
            { "1/卡姆马姆/4189FB3A/42A04990", 128 },
            { "1/卡尔加里/424C3350/C2E42BAB", 64 },
            { "1/卡尔巴拉/420276D1/4230197A", 256 },
            { "1/卡尔希/421B7137/428393FE", 128 },
            { "1/卡尚/4207EEAD/424DBEEA", 128 },
            { "1/卡希亚斯/C09B7C07/C22D6CA8", 64 },
            { "1/卡拉奇/41C73EAB/42862A16", 192 },
            { "1/卡拉季/420F54A5/424BF759", 256 },
            { "1/卡拉巴尔/409EAF8E/41053B30", 256 },
            { "1/卡拉曼/4214B975/4204DC29", 384 },
            { "1/卡拉沃索/410EC95C/C286DBCC", 384 },
            { "1/卡杜纳/41286C2D/40EE0A91", 320 },
            { "1/卡班卡兰/411FBE0E/42F5A0E3", 512 },
            { "1/卡瑞/420F2A89/C29D8FEF", 128 },
            { "1/卡维马斯/41263665/C28EE13F", 256 },
            { "1/卡缅涅茨-波多利斯基/4242BCEE/41D4AF4A", 768 },
            { "1/卡罗尔顿/4203D09F/C2C1C7D3", 256 },
            { "1/卡萨布兰卡/42065A6E/C0F3906D", 192 },
            { "1/卡萨拉/41773741/42119994", 64 },
            { "1/卡蒂哈尔/41CC4EE4/42AF2411", 256 },
            { "1/卡诺/4140007E/4108447C", 64 },
            { "1/卡贝略港/412791A7/C2880666", 256 },
            { "1/卡赫拉曼马拉什/421656BC/4213B4A5", 768 },
            { "1/卡门城/41952AD8/C2B7A8EA", 128 },
            { "1/卡马圭/41AB0BF1/C29BD579", 128 },
            { "1/卡马河畔切尔尼/425EE6DC/4251A517", 256 },
            { "1/卡鲁帕诺/412AAF5A/C27D08B2", 256 },
            { "1/卢克索/41CD9769/42029183", 448 },
            { "1/卢布尔雅那/4238344E/41681503", 128 },
            { "1/卢布林/424D0000/41B4888A", 896 },
            { "1/卢济阿尼亚/C182051F/C23FCD16", 64 },
            { "1/卢甘斯克/424244A9/421D44AB", 640 },
            { "1/卢茨克/424B098B/41CABD51", 896 },
            { "1/印多尔/41B5BE4D/4297AAA6", 64 },
            { "1/印第安纳波利斯/421F12D2/C2AC50EB", 128 },
            { "1/原州/421567D3/42FFE3FC", 1792 },
            { "1/厦门/41C3D69C/42EC29EB", 448 },
            { "1/双城/42356666/42FC9111", 128 },
            { "1/双溪大年/40B4B439/42C8F9B6", 384 },
            { "1/双鸭山/423A8B60/43032765", 128 },
            { "1/古卢/4031928E/4201322A", 64 },
            { "1/古尔伯加/418AAFC8/4299ACD6", 192 },
            { "1/古斯塔沃·A.马德罗/41B6F488/C2C58489", 128 },
            { "1/古斯曼城/419DA325/C2CEEC64", 128 },
            { "1/古晋/3FC66666/42DCAAAA", 192 },
            { "1/古杰兰瓦拉/4200A58D/4294606A", 64 },
            { "1/古瓦哈提/41D179A7/42B77DD9", 192 },
            { "1/古赖亚特/41FAA772/42155F0C", 512 },
            { "1/古邦/C122BFB1/42F7370A", 64 },
            { "1/台东/41B61100/42F249EF", 1920 },
            { "1/台中/41C12CDA/42F15E28", 1920 },
            { "1/台北/41C861D0/42F3104F", 1984 },
            { "1/台南/41B7ED38/42F06D3A", 1920 },
            { "1/叶列茨/42527EA1/421A01BB", 512 },
            { "1/叶卡捷琳堡/42636858/427272E5", 192 },
            { "1/叶夫帕托里亚/4234CDBB/42057759", 128 },
            { "1/叶纳基耶沃/4240EEE9/4218D871", 512 },
            { "1/合肥/41FEE93F/42EA8FC9", 256 },
            { "1/合艾/40E0447C/42C8F40F", 384 },
            { "1/吉大港/41B2B50B/42B7A9D2", 448 },
            { "1/吉安/41D8EFF2/42E5F563", 1920 },
            { "1/吉尔贾/41D2B4C2/41FF2204", 256 },
            { "1/吉杰勒/4213483E/40B888E3", 512 },
            { "1/吉林/422F6740/42FD1EDD", 768 },
            { "1/吉舒/42314445/42FD999A", 256 },
            { "1/吉达/41AC56CB/421CCAB9", 64 },
            { "1/吉隆坡/4049096C/42CB5F81", 192 },
            { "1/吉马/40F5555C/42135554", 256 },
            { "1/名古屋市/420CB9D3/4308E80A", 448 },
            { "1/君士坦丁/421175C3/40D3ABC9", 448 },
            { "1/吴川/41ABA834/42DD8825", 512 },
            { "1/吴市/4208EDCB/4304910B", 512 },
            { "1/周口/42068888/42E54444", 384 },
            { "1/周村/42134445/42EBA223", 1024 },
            { "1/呵叻/416F87D3/42CC3434", 384 },
            { "1/呼和浩特/42233E03/42DF4DF0", 448 },
            { "1/和平市/41A68990/42D2ACD9", 768 },
            { "1/和歌山/4208EEEE/43072AAB", 896 },
            { "1/和泉/4209EEEE/43076EEF", 512 },
            { "1/和田/42146E14/429FDEF7", 64 },
            { "1/咸兴/421FAC5F/42FF12A2", 896 },
            { "1/哈伊勒/41DC2CCF/4226C34F", 256 },
            { "1/哈利法克斯/423294D1/C27E4A21", 192 },
            { "1/哈密/422B55F2/42BB0314", 192 },
            { "1/哈密尔顿/422D001D/C29FB303", 128 },
            { "1/哈尔格萨/4118F5C3/4230428F", 256 },
            { "1/哈尔滨/42370000/42FD4CCD", 448 },
            { "1/哈尔科夫/4247EC59/421102C9", 192 },
            { "1/哈巴罗夫斯克/4241EE4C/43071573", 64 },
            { "1/哈布拉/41B6BC75/42B14FE7", 256 },
            { "1/哈恩/421713AE/C07293F3", 128 },
            { "1/哈拉帕/419C3FFB/C2C1D4F0", 128 },
            { "1/哈桑/41501D49/42983141", 128 },
            { "1/哈瓦利/41EAA989/42401D4C", 256 },
            { "1/哈瓦那/41B9106D/C2A4C41E", 384 },
            { "1/哈科特港/4098E0A0/40E06DC6", 320 },
            { "1/哈雷（萨勒河畔）/424E0000/41400000", 128 },
            { "1/哈马/420C86F4/42130805", 768 },
            { "1/哈马丹/420B3267/42420EE9", 448 },
            { "1/哥伦布/4201D80B/C2A9F9B5", 128 },
            { "1/哥伦布/421FD840/C2A5FF61", 128 },
            { "1/哥印拜陀/413016BC/4299EEA7", 128 },
            { "1/哥打巴托/40E727D0/42F87E27", 384 },
            { "1/哥打巴鲁/40C443D4/42CC7A2A", 128 },
            { "1/哥本哈根/425EB42A/41490C69", 192 },
            { "1/唐山/421E8888/42EC5DDD", 384 },
            { "1/商丘/4209CCCD/42E74CCD", 384 },
            { "1/喀什/421DE1F5/4297FAA1", 64 },
            { "1/喀土穆/4178D40D/42022130", 320 },
            { "1/喀山/425F27AB/42447D12", 64 },
            { "1/喀布尔/420A1CCE/428A583C", 64 },
            { "1/嘉兴/41F60481/42F18000", 384 },
            { "1/嘉峪关/421F3EAE/42C49286", 192 },
            { "1/四平/422CA54E/42F8C176", 256 },
            { "1/因帕尔/41C676E3/42BBE36E", 128 },
            { "1/图伊河畔奥库马雷/4121DD25/C2858D5C", 256 },
            { "1/图兰辛戈/41A0AB46/C2C4BA05", 128 },
            { "1/图兹拉/42322758/41955633", 128 },
            { "1/图卜鲁格/4200559E/41BFCFA6", 960 },
            { "1/图卢兹/422E6AC3/3FB8CA2E", 128 },
            { "1/图卢阿/4082B589/C2986406", 128 },
            { "1/图尔古特卢/4219FB38/41DD98FC", 512 },
            { "1/图尔萨/42109DAD/C2BFFC4C", 384 },
            { "1/图拉/4258C8CC/4216790F", 128 },
            { "1/图斯潘/41A7A96E/C2C2D0E3", 128 },
            { "1/图斯特拉古铁雷斯/418613ED/C2BA39E6", 192 },
            { "1/图森/4200E310/C2DDDA5C", 128 },
            { "1/图莱/41A73857/42958E01", 128 },
            { "1/土库曼纳巴德/421C4B0A/427E507F", 192 },
            { "1/土格加劳市/418CE78C/42F37431", 384 },
            { "1/土瓦/41612E14/42C46372", 384 },
            { "1/圣丽塔/C0E3A4FD/C20BE989", 64 },
            { "1/圣何塞/411EEEEB/C2A82AAA", 64 },
            { "1/圣佩德罗苏拉/41781115/C2B00CCD", 128 },
            { "1/圣保罗/C1BC6148/C23A8B60", 64 },
            { "1/圣克拉拉/41B33333/C29FEEEF", 128 },
            { "1/圣克里斯托瓦尔/40F88AC6/C2907333", 192 },
            { "1/圣克鲁斯-德拉谢拉/C18E4A52/C27CB985", 64 },
            { "1/圣克鲁斯-德特内里费/41E3BEF5/C1820976", 128 },
            { "1/圣卡洛斯/C1B023D7/C23F9036", 64 },
            { "1/圣地亚哥-德古巴/41A032A0/C297A4D5", 128 },
            { "1/圣地亚哥-德洛斯卡瓦耶罗斯/419B9D15/C28D64E1", 128 },
            { "1/圣地亚哥/41858130/42F318EF", 1536 },
            { "1/圣地牙哥/4202DC7F/C2EA5084", 128 },
            { "1/圣塔伦/C01C5B18/C25AD554", 192 },
            { "1/圣塔克拉利塔/4209910F/C2ED15CE", 512 },
            { "1/圣塞巴斯蒂安/422D4057/BFFCCC79", 128 },
            { "1/圣多明哥/4194003F/C28BFA26", 192 },
            { "1/圣安东尼奥/41EB6499/C2C4FCBD", 128 },
            { "1/圣安唐维多利亚/C101E393/C20D2A62", 64 },
            { "1/圣安娜德科罗/413678D5/C28B58CD", 128 },
            { "1/圣安德烈乌-德帕洛马尔/4225BDDC/400C2603", 256 },
            { "1/圣巴勃罗/416117C2/42F2A6B5", 256 },
            { "1/圣斯皮里图斯/41AF7011/C29EE28F", 128 },
            { "1/圣洛伦索/C1CAB7AA/C2660900", 64 },
            { "1/圣玛丽亚/C1ED792E/C2573A4E", 64 },
            { "1/圣玛尔塔/4133DA47/C29465E9", 128 },
            { "1/圣科洛马-德格拉梅内特/4225CE5B/400D5183", 256 },
            { "1/圣米格尔-德尔帕德龙/41B887F7/C2A496E6", 256 },
            { "1/圣胡安/4193BB0B/C2843621", 448 },
            { "1/圣艾蒂安/4235BC4E/408C7AE1", 128 },
            { "1/圣若泽杜斯皮尼艾斯/C1CC3DD9/C244D55C", 64 },
            { "1/圣萨尔瓦多/415B0794/C2B25FD6", 128 },
            { "1/圣贝纳迪诺/42086EF1/C2EA945D", 128 },
            { "1/圣费尔南多/41707525/42F1612D", 1024 },
            { "1/圣路易斯/C021E6EF/C231360C", 64 },
            { "1/圣路易斯波托西/41B132D5/C2C9F554", 192 },
            { "1/圣路易斯里奥科罗拉多/4201D311/C2E58B31", 512 },
            { "1/圭亚那城/41059E99/C27A9068", 192 },
            { "1/地拉那/42254F5C/419E8D16", 896 },
            { "1/坎佩切/419EB449/C2B50E2F", 128 },
            { "1/坎大哈/41FCE814/42836B96", 192 },
            { "1/坎帕拉/3EA1EF74/4202542A", 64 },
            { "1/坎斯克/4260CE83/42BF6F5C", 64 },
            { "1/坎昆/41A964F2/C2ADB170", 192 },
            { "1/坎普尔/41D3B8CB/42A0B312", 64 },
            { "1/坎普斯/C1AE04A6/C225525F", 64 },
            { "1/坎皮纳斯/C1B73E96/C23C3E4A", 64 },
            { "1/坤甸/BCFEB075/42DAA4DC", 128 },
            { "1/坦佩雷/4275FF17/41BE4C06", 64 },
            { "1/坦塔/41F64EC9/41F803EF", 384 },
            { "1/坦托尤卡/41AACE1C/C2C4738F", 128 },
            { "1/坦波夫/4252ED40/4225C5E6", 128 },
            { "1/坦皮科/41B20AD0/C2C3BCBA", 128 },
            { "1/坦皮科/41B239B1/C2C3BC46", 128 },
            { "1/坦盖尔/41C1FFAC/42B3D546", 256 },
            { "1/埃代阿/40733333/4122221F", 256 },
            { "1/埃利斯塔/42393B2B/423105F8", 128 },
            { "1/埃努古/40CE1F4B/40EFF66A", 64 },
            { "1/埃基巴斯图兹/424EE514/4296A54F", 64 },
            { "1/埃尔多雷特/3F053650/420D1469", 320 },
            { "1/埃尔比勒/4210C531/42300AE0", 256 },
            { "1/埃尔津詹/421EF4EE/421DF5EA", 384 },
            { "1/埃尔祖鲁姆/421FA26B/42251B96", 384 },
            { "1/埃尔蒂格雷/410E3348/C2807DAA", 128 },
            { "1/埃德蒙顿/42563358/C2E2EFFB", 64 },
            { "1/埃拉泽/421AB27E/421CE491", 256 },
            { "1/埃文斯维尔/4217E627/C2AF1C98", 128 },
            { "1/埃斯基谢希尔/421F1B4F/41F42A1B", 384 },
            { "1/埃斯梅拉达斯/3F758E22/C29F4ED5", 128 },
            { "1/埃罗德/41357C1C/429B746F", 128 },
            { "1/埃莫西约/41E8D220/C2DDF463", 128 },
            { "1/埃迪尔内/4226B571/41D472A0", 896 },
            { "1/埃里温/4220B975/42320DF0", 256 },
            { "1/城南/4215C123/42FE468B", 1280 },
            { "1/基加利/BFF997F6/41F07886", 128 },
            { "1/基多/BE6B5DCC/C29D0CC6", 192 },
            { "1/基奎特/C0A14FB5/4196878F", 64 },
            { "1/基尔库克/420DDF4B/423191A2", 320 },
            { "1/基希讷乌/423C05B2/41E6DC29", 128 },
            { "1/基戈马/C09C0FE4/41ED036C", 128 },
            { "1/基斯马尤/BEB7620F/422A2E73", 384 },
            { "1/基林/41F8EFDD/C2C374A2", 256 },
            { "1/基桑加尼/3F03E964/41C98726", 64 },
            { "1/基洛夫/426A62F8/4246A3E9", 64 },
            { "1/基纳/41D1503E/4202E827", 384 },
            { "1/基苏木/BDD1537A/420B0BFE", 384 },
            { "1/基辅/4249D192/41F430BE", 960 },
            { "1/基隆/41C906A8/42F37BDA", 1536 },
            { "1/堪察加彼得巴甫洛夫斯克/42542D82/431EA698", 64 },
            { "1/堺/420A5554/43077778", 512 },
            { "1/塔什干/42250F00/428A6EBB", 192 },
            { "1/塔伊兹/415945B7/42301569", 64 },
            { "1/塔克纳/C1901E01/C28C81DA", 64 },
            { "1/塔兰托/4221AA62/418A09CA", 896 },
            { "1/塔古姆/40EE5437/42FB9D95", 128 },
            { "1/塔尔苏斯/4213ACCF/420B837B", 768 },
            { "1/塔布克/41E332CA/4212493A", 832 },
            { "1/塔帕丘拉/416E82E8/C2B88611", 128 },
            { "1/塔德帕尔利古德姆/4186811B/42A30EB7", 128 },
            { "1/塔扎/4208D70A/C08051EC", 128 },
            { "1/塔拉兹/422B999A/428EBBBC", 64 },
            { "1/塔拉戈纳/42247778/3FA00000", 384 },
            { "1/塔拉赫西/41F3818E/C2A88FBC", 128 },
            { "1/塔林/426DBF72/41C6073B", 64 },
            { "1/塔甘罗格/423CF1D7/421B9668", 256 },
            { "1/塔里哈/C1AC48AF/C2817589", 64 },
            { "1/塔里萨伊/4123EA60/42F7B2EA", 512 },
            { "1/塔马利/411669A3/BF56DC5D", 256 },
            { "1/塞伊达/420B5242/3E1B59DE", 384 },
            { "1/塞勒姆/413A6B31/429C513D", 128 },
            { "1/塞卜哈/41D84D21/4166DA66", 192 },
            { "1/塞塔特/4204010E/C0F3BB5A", 128 },
            { "1/塞姆南/420E4EC1/42559176", 384 },
            { "1/塞得港/41FA0D65/420122EE", 448 },
            { "1/塞拉塔博昂/C1BD0246/C23B2AAC", 64 },
            { "1/塞拉莱/41881ED3/42585E96", 192 },
            { "1/塞提夫/4210C3B5/40AD3D47", 896 },
            { "1/塞格德/42390312/41A12F98", 128 },
            { "1/塞瓦斯托波尔/42325AF6/420616F0", 192 },
            { "1/塞米伊/4249B4FE/42A0888C", 64 },
            { "1/塞维利亚/42158805/C0BF2435", 192 },
            { "1/塞萨洛尼基/4222930E/41B77267", 960 },
            { "1/塞蒂拉瓜斯/C19BBA05/C230FC97", 64 },
            { "1/墨西哥城/419B6D82/C2C6415D", 128 },
            { "1/墨西拿/4218C698/4178D749", 384 },
            { "1/夏洛特/420CE88A/C2A1AFAF", 128 },
            { "1/多伦多/422ECCEA/C29ED525", 128 },
            { "1/多哈/41CA3C0C/424E16FD", 128 },
            { "1/多拉杜斯/C1B1C4D5/C25B38E5", 64 },
            { "1/大不里士/421851EC/42392AE8", 448 },
            { "1/大分/4204EEEE/4303999A", 896 },
            { "1/大卢基/42615C29/41F45C82", 384 },
            { "1/大叻市/413F24B3/42D8E245", 384 },
            { "1/大吉岭/41D84443/42B08889", 256 },
            { "1/大同/42205FDB/42E29531", 896 },
            { "1/大庆/423A5554/42FA0000", 448 },
            { "1/大急流城/422BDA7B/C2AB5610", 128 },
            { "1/大津/420C0000/4307DDDE", 512 },
            { "1/大牟田/42042221/43027333", 512 },
            { "1/大理/41CCAD8C/42C86CB1", 192 },
            { "1/大瓦尔泽亚/C17A58C3/C26087AE", 64 },
            { "1/大田/4211491A/42FED6E6", 1664 },
            { "1/大萨德伯里/4239F5C3/C2A1FAE3", 128 },
            { "1/大连/421BA61D/42F33456", 448 },
            { "1/大邱/420F7B2B/43009753", 1920 },
            { "1/大阪市/420AC664/4307808F", 960 },
            { "1/大马士革/42060A72/42112A45", 1792 },
            { "1/天安/421339DB/42FE4DED", 1536 },
            { "1/天水/420A516E/42D37C19", 192 },
            { "1/天津/421C91A2/42EA5A74", 896 },
            { "1/天福/4214C9F7/42F41AC4", 1024 },
            { "1/太原/41ACC0F6/42D3B243", 896 },
            { "1/太原/42177A4E/42E11EDD", 960 },
            { "1/太子港/41945038/C290AB85", 192 },
            { "1/太平/409B3333/42C97777", 256 },
            { "1/头顿/4125892D/42D62B24", 1536 },
            { "1/夸察夸尔科斯/41911153/C2BCE285", 128 },
            { "1/奇姆肯特/42293333/428B3333", 192 },
            { "1/奇尔潘辛戈/418C67A1/C2C702F6", 128 },
            { "1/奇特拉杜尔嘎/41638FDA/4298CCFF", 128 },
            { "1/奇瓦瓦/41E5150E/C2D42D83", 128 },
            { "1/奈哈蒂/41B726D5/42B0D496", 256 },
            { "1/奈拜提耶/420583FC/420DEF81", 1536 },
            { "1/奈良市/420ABD7E/4307CE0B", 512 },
            { "1/奋斗/423A90D0/42F9B9C5", 256 },
            { "1/奎达/41F1978D/428604F9", 192 },
            { "1/奎隆/410E191C/42992B5C", 128 },
            { "1/奔萨/4254CD7A/423404C0", 64 },
            { "1/奥伦堡/424F173F/425C652C", 64 },
            { "1/奥伦塞/422958C5/C0FBA676", 128 },
            { "1/奥克兰/421737AD/C2F48AA6", 512 },
            { "1/奥克拉荷马市/420DDEC8/C2C3086A", 384 },
            { "1/奥克斯纳德/4208CA3D/C2EE5AA6", 512 },
            { "1/奥兰/420EC3B2/BF24447C", 448 },
            { "1/奥兰加巴德/419F0543/4296AF3D", 192 },
            { "1/奥兰多/41E44E85/C2A2C22C", 128 },
            { "1/奥尔堡/42643127/411EB2FF", 128 },
            { "1/奥尔杜/4223E966/42177BC0", 384 },
            { "1/奥尔沙/425A084B/41F3566D", 384 },
            { "1/奥尔莫克/41301A2C/42F9370A", 896 },
            { "1/奥尔金/41A71907/C29886B0", 128 },
            { "1/奥布宁斯克/425C6322/421270B4", 128 },
            { "1/奥廖尔/4253DC3E/42105060", 640 },
            { "1/奥拉迪亚/423C4445/41AF7776", 256 },
            { "1/奥斯曼尼耶/42144BF3/4210FDBA", 512 },
            { "1/奥斯汀/41F22320/C2C37C72", 128 },
            { "1/奥斯皮塔莱特-德略夫雷加特/4225704D/40066AFD", 256 },
            { "1/奥斯陆/426FA6A3/412BEFFC", 64 },
            { "1/奥维耶多/422D70F0/C0BB0846", 128 },
            { "1/奥罗拉/42270AD5/C2B0A3E0", 128 },
            { "1/奥胡斯/4260A080/41235F46", 128 },
            { "1/奥里萨巴/4196CEDE/C2C233A1", 128 },
            { "1/奥隆阿波/416D4448/42F090C9", 256 },
            { "1/奥鲁罗/C18FDDDC/C2864CCD", 64 },
            { "1/好莱坞/420864B3/C2ECA74A", 512 },
            { "1/姆万扎/C021111F/4203999A", 192 },
            { "1/姆布吉马伊/C0C45A5C/41BCB7E4", 128 },
            { "1/姆班达卡/3D474539/4192152D", 64 },
            { "1/姆西拉/420ED2C5/40915793", 512 },
            { "1/姆韦内迪图/C0E04A38/41BB9F4B", 128 },
            { "1/姬路/420B4445/4306B333", 512 },
            { "1/威奇托/4216C4DB/C2C2ACD2", 256 },
            { "1/威奇托福尔斯/4207A7A4/C2C4FC9E", 384 },
            { "1/威尔明顿/4208E726/C29BE3B1", 128 },
            { "1/威廉斯塔德/4141BC02/C289DDF9", 512 },
            { "1/威海/4216095C/42F43A25", 384 },
            { "1/孔敬/418392DD/42CDAA7F", 384 },
            { "1/孝感/41F76A45/42E3D82C", 384 },
            { "1/孟买/41989528/4291C3E5", 128 },
            { "1/孟菲斯/420C991E/C2B41914", 128 },
            { "1/季米特洛夫格勒/4258DAFE/42467939", 256 },
            { "1/宁波/41EF0688/42F31952", 384 },
            { "1/宇治/420B8FD0/4307CDA2", 512 },
            { "1/宇部/4207C5B2/43034049", 512 },
            { "1/宇都宫/42124445/430BE222", 256 },
            { "1/安东/42124322/4300B99A", 1920 },
            { "1/安养/421591EC/42FDDA98", 1024 },
            { "1/安卡拉/421FADF2/42036AC6", 448 },
            { "1/安塔利亚/4213A1EA/41F59082", 384 },
            { "1/安塔基亚/4210D382/4210A0FE", 512 },
            { "1/安娜堡/42291C39/C2A77B55", 128 },
            { "1/安山/42154B60/42FDA4D5", 1536 },
            { "1/安巴托/BF9FE1DA/C29D3BC7", 128 },
            { "1/安庆/41F41BF5/42EA182F", 384 },
            { "1/安康/4202B852/42DA08D1", 192 },
            { "1/安扎利港/4215E3E9/4245D653", 256 },
            { "1/安曼/41FFA44A/420FC7B6", 1792 },
            { "1/安汶/C06C81ED/43002E71", 64 },
            { "1/安纳巴/4213999A/40F88890", 960 },
            { "1/安赫莱斯市/41726666/42F12AAA", 384 },
            { "1/安达/42399BAB/42FAA10B", 256 },
            { "1/安阳/4210624E/42E4C3FC", 384 },
            { "1/安集延/422320D4/4290B040", 64 },
            { "1/安顺/41D20000/42D3DDDD", 128 },
            { "1/宗古尔达克/4225CE39/41FE582B", 384 },
            { "1/定州/421A0D60/42E5FDBA", 896 },
            { "1/宜宾/41E62224/42D13F67", 128 },
            { "1/宜昌/41F5B72C/42DE91C7", 384 },
            { "1/宜春/41DEAAA9/42E4CCCD", 768 },
            { "1/宝冢/420B328B/43075B62", 512 },
            { "1/实兑/41A12B80/42B9CBF5", 448 },
            { "1/宣化/422270ED/42E616E6", 768 },
            { "1/宣州/41F79EB8/42ED82B4", 256 },
            { "1/宫崎/41FF5557/43036AAB", 960 },
            { "1/宰廷布尔努/4223FA47/41E73BBD", 512 },
            { "1/宿务市/41251149/42F7C80B", 832 },
            { "1/宿州/42068B60/42E9F531", 448 },
            { "1/密尔沃基/422C27D5/C2AFD01D", 128 },
            { "1/密州/420FFA98/42EECB85", 512 },
            { "1/富川/4215FEDD/42FD90ED", 1024 },
            { "1/富田林町/420A00AA/43079A24", 512 },
            { "1/寝屋川/420B108A/4307A0AA", 512 },
            { "1/小岩城/420AFC65/C2B89445", 128 },
            { "1/尚勒乌尔法/4214AB17/421B2CF9", 768 },
            { "1/尼亚美/415837F4/400706F7", 576 },
            { "1/尼什/422D4C83/41AF3A05", 128 },
            { "1/尼姆/422F5554/408B3333", 128 },
            { "1/尼扎马巴德/41955F50/429C3296", 128 },
            { "1/尼斯/422ED001/40E883BA", 128 },
            { "1/尼科波尔/423E48E6/420995E2", 896 },
            { "1/尼科西亚/420CB384/420574F1", 896 },
            { "1/尼赖吉哈佐/423FD252/41ADBBD2", 256 },
            { "1/居林/40ABAE00/42C91FA0", 256 },
            { "1/居銮/4001F30E/42CEA23F", 128 },
            { "1/屈塔希亚/421DB25A/41EFDDDC", 768 },
            { "1/山口/4208BBBB/43037778", 512 },
            { "1/山口洋/3F68C49C/42D9F821", 128 },
            { "1/山形/4218EEEE/430C5DDE", 384 },
            { "1/山打根/40BAE2EB/42EC3C5D", 128 },
            { "1/岐阜市/420DB10F/4308C2A9", 256 },
            { "1/岘港/41808AD0/42D87111", 384 },
            { "1/岩国/4208A6E2/43043852", 512 },
            { "1/岳阳/41EAFF14/42E2308B", 384 },
            { "1/岸和田/4209DDDF/43075DDE", 512 },
            { "1/峇株巴辖/3FED6A16/42CDDD71", 128 },
            { "1/川越/420FA26B/430B7C3B", 256 },
            { "1/巴东/BF730165/42C8B563", 128 },
            { "1/巴东石林潘/3FB0941D/42C68AFD", 128 },
            { "1/巴亚马雷/423E9CFD/41BCA2CC", 384 },
            { "1/巴亚马雷/423EA111/41BC8B6E", 384 },
            { "1/巴伦西亚/412297A2/C28803EB", 448 },
            { "1/巴伦西亚/421DE106/BEC13943", 448 },
            { "1/巴克乌/423A4445/41D73333", 128 },
            { "1/巴兰基亚/412F7F24/C2959009", 320 },
            { "1/巴利亚多利德/42269EE8/C09728B7", 128 },
            { "1/巴利瓦格/416F4688/42F1CB3C", 1024 },
            { "1/巴勒克埃西尔/421E98C0/41DF16C1", 896 },
            { "1/巴勒莫/42188738/41555EA9", 960 },
            { "1/巴博勒/4212348D/4252B73F", 256 },
            { "1/巴卢尔加特/41C9C496/42B18DFD", 256 },
            { "1/巴厘巴板/BF9EFB2B/42E9C9D3", 192 },
            { "1/巴厘巴板/BFA23E6C/42E9A862", 192 },
            { "1/巴古拜/4206FC82/4232931C", 256 },
            { "1/巴吞鲁日/41F39B23/C2B64F21", 128 },
            { "1/巴哈拉姆普尔/41C0D67D/42B080CB", 256 },
            { "1/巴哥市/41288885/42F5AAAA", 512 },
            { "1/巴基西梅托/41212EA7/C28AA543", 384 },
            { "1/巴塔/3FEE949A/411C40CC", 256 },
            { "1/巴塞罗那/4122221F/C2816666", 384 },
            { "1/巴塞罗那/42258E1F/400A2CE4", 448 },
            { "1/巴士拉/41F443A0/423F309C", 448 },
            { "1/巴士拉老城/41F40679/423F42A2", 256 },
            { "1/巴夫那加尔/41AE3213/42904E14", 128 },
            { "1/巴富萨姆/40AF49BA/4126AE73", 256 },
            { "1/巴尔瑙尔/42557137/42A786F8", 64 },
            { "1/巴尔的摩/421D2959/C2993971", 128 },
            { "1/巴巴塞纳/C1A9CE80/C22F182D", 64 },
            { "1/巴布纳/41C00D30/42B27972", 256 },
            { "1/巴库/422182BC/4247916B", 448 },
            { "1/巴德拉瓦蒂/415D934B/429768F8", 128 },
            { "1/巴拉索尔/41ABF0F8/42ADDDF1", 128 },
            { "1/巴拉萨特/41B1CD21/42B0E6D7", 256 },
            { "1/巴拉诺维奇/425487E3/41D01C78", 256 },
            { "1/巴拿马城/410FE5C9/C29F0A1A", 192 },
            { "1/巴格兰/421085D1/42896AA5", 192 },
            { "1/巴格达/42055CC1/42319A80", 320 },
            { "1/巴特曼/42178CAD/42248762", 768 },
            { "1/巴特纳/420E3950/40C5928E", 384 },
            { "1/巴甫洛格勒/42422315/420F7BE2", 512 },
            { "1/巴甫洛达尔/42511896/429A023E", 64 },
            { "1/巴科洛德市/412AAAAE/42F5E666", 768 },
            { "1/巴统/422691B2/42268922", 384 },
            { "1/巴耶斯城/41AFDDDC/C2C60889", 128 },
            { "1/巴耶杜帕尔/41276905/C29281A6", 256 },
            { "1/巴蒂/C0D83055/42DE1375", 128 },
            { "1/巴西利亚/C17C79BC/C23FB809", 64 },
            { "1/巴赫达尔/41397F8D/42159026", 64 },
            { "1/巴达霍斯/421B82F6/C0DF0F3D", 128 },
            { "1/巴里/42247228/4186D7DC", 896 },
            { "1/巴里/423199B6/C29F552B", 128 },
            { "1/巴里巴里/C0806E98/42EF4042", 128 },
            { "1/巴门达/40BEB5DD/412255E5", 320 },
            { "1/巴雷拉斯/C14271C9/C233F5C3", 64 },
            { "1/巴革/415E9BB0/42C8FF67", 512 },
            { "1/巴马/41385769/415B0846", 256 },
            { "1/巴马科/414A6666/C1000000", 832 },
            { "1/巴黎/424369E4/401652BD", 64 },
            { "1/布什尔/41E7BF48/424B5AA0", 128 },
            { "1/布列斯特/425063E4/41BD8083", 960 },
            { "1/布加勒斯特/4231BAA0/41D0D99F", 192 },
            { "1/布卡拉曼加/40E40332/C2923D56", 128 },
            { "1/布卡武/C01F68C7/41E6BE13", 384 },
            { "1/布哈拉/421F1950/4280DB73", 192 },
            { "1/布埃纳文图拉/4078538F/C29A0FF4", 128 },
            { "1/布尔加斯/422A0635/41DBBE13", 384 },
            { "1/布尔戈斯/422966A0/C06B37A0", 128 },
            { "1/布尔汉布尔/41AA782D/429875E5", 128 },
            { "1/布尔萨/4220C849/41E87B25", 960 },
            { "1/布尔诺/4244C7E8/4184DD1A", 128 },
            { "1/布巴内什瓦尔/41A22DE5/42ABAAEE", 192 },
            { "1/布拉戈维申斯克/42491E52/42FF14BC", 64 },
            { "1/布拉格/42485A27/4166BB6F", 128 },
            { "1/布拉索夫/4236982D/41CCD95B", 128 },
            { "1/布拉茨克/426087AE/42CB3A74", 64 },
            { "1/布拉赫马普尔/419A7DF9/42A995F7", 128 },
            { "1/布拉迪斯拉发/424097B7/4188DA9B", 128 },
            { "1/布滕博/3E110A13/41EA5451", 576 },
            { "1/布琼布拉/C05875F7/41EAEA4B", 128 },
            { "1/布良斯克/42550224/42097C97", 192 },
            { "1/布莱尔港/413AAAAE/42B98000", 384 },
            { "1/布达佩斯/423DFDF6/419851BC", 128 },
            { "1/布里/419E62D1/42ABA65F", 128 },
            { "1/布里奇波特/4224AB0D/C29268DF", 128 },
            { "1/布阿拉里季堡/42104AF8/40985AC4", 512 },
            { "1/希什利/42243DE9/41E7E5B9", 512 },
            { "1/希伯仑/41FC3C1C/420C600D", 1792 },
            { "1/希奥利艾/425FBBBB/41BA888A", 256 },
            { "1/希拉/4201DACC/4231ADB3", 384 },
            { "1/希莫加/415EE7B6/429722C5", 128 },
            { "1/帕丘卡/41A0EF8E/C2C57772", 128 },
            { "1/帕内韦日斯/425EEEEE/41C2CCCD", 256 },
            { "1/帕卢/BF644674/42EFBDCC", 128 },
            { "1/帕嘎蒂安/40FA6C22/42F6DFBE", 128 },
            { "1/帕尔诺冈/41C66EF6/42B1E5C3", 256 },
            { "1/帕尔马/421E470E/40299D88", 384 },
            { "1/帕尔马/42333289/41253809", 128 },
            { "1/帕尔马斯/C122ADE0/C2414F86", 64 },
            { "1/帕巴登/415A896C/42C91137", 512 },
            { "1/帕拉卡德/412C63F1/42995000", 128 },
            { "1/帕拉库/41156502/40285700", 256 },
            { "1/帕拉马里博/40BBB963/C25CAAD3", 192 },
            { "1/帕斯托/3F9B5793/C29A8FEE", 192 },
            { "1/帕朗卡拉亚/C00DAB9F/42E3D254", 128 },
            { "1/帕格尔布尔/41C9F4A7/42ADF194", 320 },
            { "1/帕洛波/C03F851F/42F064D5", 128 },
            { "1/帕特雷/4218FA4E/41ADE022", 896 },
            { "1/帕纳拉诺伊/4134048C/42FA0346", 576 },
            { "1/帕纳米林/C0BD4C44/C20D0D16", 64 },
            { "1/帕苏丰杜/C1E21A2C/C251A06E", 64 },
            { "1/带广/422BAB3C/430F3456", 128 },
            { "1/常州/41FE3050/42EFE874", 256 },
            { "1/常德/41E85EFC/42DF5B4A", 384 },
            { "1/平凉/420E281C/42D55F4A", 128 },
            { "1/平城/421CFC4E/42FBBE6F", 1024 },
            { "1/平壤/421C22AA/42FB8236", 1984 },
            { "1/平斯克/42507DD9/41D0C2C4", 896 },
            { "1/平顶山/4206F431/42E29A36", 256 },
            { "1/广元/4201C49E/42D3A560", 128 },
            { "1/广岛/4209999A/43047333", 960 },
            { "1/庆山/420F4B17/4300BCDF", 1024 },
            { "1/庆州/420F5F02/43013630", 1536 },
            { "1/库亚巴/C17989AB/C26062FD", 64 },
            { "1/库什蒂亚/41BF38EF/42B23D26", 256 },
            { "1/库利亚坎/41C65293/C2D6C690", 192 },
            { "1/库塔伊西/42291257/422AC743", 256 },
            { "1/库奥蒂特兰伊斯卡伊/419D26AB/C2C66E95", 128 },
            { "1/库姆/420A8F76/424B816F", 128 },
            { "1/库尔塔利/41AF3333/42B0CCCD", 128 },
            { "1/库尔斯克/424EF307/4210BFD9", 128 },
            { "1/库尔纳/41B67A73/42B320F8", 320 },
            { "1/库尔蒂/41BDDA71/42ADAFFC", 256 },
            { "1/库斯提/41529B3D/4202A765", 256 },
            { "1/库斯科/C1585CBC/C28FEF47", 64 },
            { "1/库特/42020D1B/423745CE", 384 },
            { "1/库米拉/41BBB1E4/42B65EBC", 384 },
            { "1/库里奇巴/C1CB6C18/C245179D", 64 },
            { "1/库马纳/41274376/C2805D79", 384 },
            { "1/底特律/42295362/C2A6176D", 128 },
            { "1/庞塞/419016B1/C2853A66", 384 },
            { "1/康康/41262AAE/C114E411", 256 },
            { "1/康斯坦察/4230B911/41E51316", 192 },
            { "1/康迪/4132258F/403C1230", 256 },
            { "1/廉江/41AD2C81/42DC903E", 512 },
            { "1/廊坊/421E09F4/42E963B2", 896 },
            { "1/延冈/42025554/4303AAAB", 512 },
            { "1/延吉/422BA148/430181FE", 256 },
            { "1/开原/422A21DA/42F814A0", 512 },
            { "1/开城/4217E221/42FD1BE0", 1792 },
            { "1/开塞利/421AEDCB/420DF0ED", 256 },
            { "1/开姆尼茨/424B57C2/414EDE16", 128 },
            { "1/开封/420B31C4/42E49D66", 256 },
            { "1/开罗/41F08044/41F9FF53", 448 },
            { "1/弗吉尼亚比奇/42136966/C297F4BB", 128 },
            { "1/弗拉基米尔/42608BD4/42219619", 128 },
            { "1/弗拉季高加索/422C258D/4232ABCF", 256 },
            { "1/弗朗卡/C1A44F13/C23D9A73", 64 },
            { "1/弗林特/422C0CD5/C2A75FFB", 128 },
            { "1/弗洛伦西亚/3FCEA401/C2973664", 128 },
            { "1/弗罗茨瓦夫/424C6666/41884443", 128 },
            { "1/弗里敦/4107BE77/C153ADD6", 256 },
            { "1/弗里斯科/42049A49/C2C1A5B0", 256 },
            { "1/弗雷斯尼约/41B97338/C2CDBE23", 128 },
            { "1/弘前/42225F4B/430C78F6", 384 },
            { "1/张家口/42233D71/42E5C246", 896 },
            { "1/归仁市/415C6C76/42DA7285", 1408 },
            { "1/彦根/420D0000/43084000", 512 },
            { "1/彭世洛/41869936/42C88465", 896 },
            { "1/彼尔姆/42680AB6/4261002D", 64 },
            { "1/彼得罗利纳/C11660B5/C22200DA", 64 },
            { "1/彼得罗巴甫尔/425B7DBD/428A4937", 128 },
            { "1/德利西亚斯/41E18563/C2D2F0B4", 128 },
            { "1/德利西亚斯区/422698DD/BF685682", 256 },
            { "1/德岛/42084445/43069111", 768 },
            { "1/德州/4215CE1A/42E89EF5", 896 },
            { "1/德布勒森/423E2221/41AD110F", 384 },
            { "1/德惠/42322221/42FB6666", 256 },
            { "1/德累斯顿/424C341D/415BD029", 192 },
            { "1/德罕/420FF9E3/C29DCC18", 128 },
            { "1/德里久尔/41284448/42986EEF", 128 },
            { "1/德雷达瓦/41197D2C/422776E6", 64 },
            { "1/德黑兰/420EC70E/424DAFA0", 448 },
            { "1/忻州/4219A2FD/42E17777", 896 },
            { "1/怀化/41DC6541/42DBEB18", 512 },
            { "1/怡保/4092B0F2/42CA2A72", 384 },
            { "1/恩城/41B17E23/42E09BC5", 512 },
            { "1/恩康桑巴/409E8CE7/411F0BE1", 256 },
            { "1/恩森那达/41FEEDD6/C2E9330E", 128 },
            { "1/恩泽雷科雷/40F8331E/C10D161E", 64 },
            { "1/恩特普赖斯/421019DB/C2E67BE0", 1024 },
            { "1/恩纳胡德/414B3333/41E37776", 256 },
            { "1/恩贾梅纳/4141B520/4170B5DD", 320 },
            { "1/恰斯/41BD15A0/42AC5591", 256 },
            { "1/悉尼/42388A58/C270BB7F", 192 },
            { "1/惠山/42259B4F/43002D83", 896 },
            { "1/惠州/41B8E44A/42E4D499", 256 },
            { "1/慕尼黑/42408CBA/41393535", 64 },
            { "1/戈亚尼亚/C1856DCB/C24503FC", 64 },
            { "1/戈尔哈布尔/418590A6/4294769D", 192 },
            { "1/戈尔洛夫卡/424157D1/42183687", 512 },
            { "1/戈尔甘/42135DD9/4259C642", 128 },
            { "1/戈梅利/4251BCEE/41F7CD9F", 896 },
            { "1/戈萨巴/41B152E2/42B199F5", 256 },
            { "1/戈马/BFD64895/41E9D3DE", 384 },
            { "1/成都/41F55557/42D02223", 192 },
            { "1/戴利城/4216D2B5/C2F4EC81", 256 },
            { "1/扎兰屯/42400000/42F56EEF", 256 },
            { "1/扎博勒/41F83EAB/4275FAC7", 128 },
            { "1/扎尔卡/42004A7F/42105A12", 1792 },
            { "1/扎波罗热/423F4AA4/420CC2E1", 896 },
            { "1/扎瑙津/422D5D59/4253729B", 64 },
            { "1/扎维耶/42030246/414BA4FD", 256 },
            { "1/扎维耶/42030745/414BA46A", 256 },
            { "1/扎赉诺尔/4245CCCD/42EB6666", 384 },
            { "1/扎里亚/4131C7CE/40F7205C", 256 },
            { "1/扎黑丹/41EBF86C/4273739C", 128 },
            { "1/打拉市/4177AEC7/42F13225", 256 },
            { "1/打横/C0EA7A10/42D87100", 128 },
            { "1/托兰斯/420757E9/C2ECAE67", 512 },
            { "1/托卢卡/419A4D8A/C2C74E75", 128 },
            { "1/托木斯克/4261FDA8/42A9F2E1", 64 },
            { "1/托莱多/4126096C/42F746F7", 512 },
            { "1/托莱多/4226A7E0/C2A71C44", 128 },
            { "1/托莱多/C1C5B579/C256F8E5", 64 },
            { "1/托雷翁/41CC59E3/C2CED685", 192 },
            { "1/扬州/420196C1/42EEDF25", 256 },
            { "1/扶余/4234BBBB/42F9A223", 256 },
            { "1/承德/4223E3D7/42EBDF4A", 896 },
            { "1/抚顺/42276C5F/42F7D8BF", 768 },
            { "1/拉什特/42151F82/42465530", 384 },
            { "1/拉伯克/42064FBB/C2CBB5D9", 384 },
            { "1/拉卡/420FCFB3/421C0812", 768 },
            { "1/拉各斯/40CE87BE/40594246", 64 },
            { "1/拉合尔/41FC65D4/4294AFEE", 64 },
            { "1/拉塔基亚/420E2071/420F2913", 768 },
            { "1/拉塞瓦/417C27C6/C2AD907E", 128 },
            { "1/拉尔卡纳/41DC70CE/42886DA4", 64 },
            { "1/拉尼甘杰/41BCEE63/42AE42DF", 256 },
            { "1/拉巴斯/41C127AB/C2DC99DB", 128 },
            { "1/拉巴斯/C1840000/C2884CCD", 64 },
            { "1/拉巴特/42080D91/C0DAA440", 192 },
            { "1/拉斯帕尔马斯/41E0CC3F/C1769D69", 192 },
            { "1/拉斯海马/41CE50F5/425FC5D6", 128 },
            { "1/拉斯维加斯/4210B32B/C2E64642", 1152 },
            { "1/拉杜尔/41932D7C/429922BC", 128 },
            { "1/拉杰南德冈/41A8C664/42A20ECC", 128 },
            { "1/拉杰沙希/41C2FDF4/42B133C9", 256 },
            { "1/拉法叶/41F1CAF0/C2B80A28", 128 },
            { "1/拉法市/41FA4BC7/420909C0", 1024 },
            { "1/拉法耶蒂顾问城/C1A54841/C22F24FA", 64 },
            { "1/拉萨/41ED3333/42B63333", 128 },
            { "1/拉贾帕拉耶姆/41173F53/429B1B51", 128 },
            { "1/拉里萨/421E8C2D/41B35744", 896 },
            { "1/拉雷多/41DC0D21/C2C703DC", 128 },
            { "1/拉马贡达姆/4196681F/429EE774", 128 },
            { "1/拉马迪/4205AEA7/422D3B2B", 256 },
            { "1/拜多阿/404748FE/422E9965", 256 },
            { "1/拜拉布巴扎尔/41C06B51/42B5F3EB", 256 },
            { "1/拿骚/41C87741/C29AAFA6", 128 },
            { "1/捷尔诺波尔/4246393B/41CCD830", 896 },
            { "1/提亚雷特/420D7BEF/3FA89321", 384 },
            { "1/提尔/420517E4/420CC68B", 1536 },
            { "1/提瓦坎/4193B11C/C2C2C920", 128 },
            { "1/摩加迪沙/40026003/42356000", 448 },
            { "1/摩苏尔/4211570A/422C79BE", 320 },
            { "1/撒马尔罕/421E9DDF/4285EB60", 192 },
            { "1/敏建/41ABAE1F/42BEC6DC", 384 },
            { "1/敖德萨/4239E8EE/41F5DC68", 192 },
            { "1/敦化/422D7D71/43003E14", 384 },
            { "1/文尼察/4244EE5E/41E3D907", 896 },
            { "1/斋浦尔/41D75B62/4297935C", 64 },
            { "1/斗湖/4087D591/42EBC845", 128 },
            { "1/斯基克达/42138133/40DD1840", 896 },
            { "1/斯塔夫罗波尔/42342BD4/4227E4C3", 192 },
            { "1/斯德哥尔摩/426D5490/419084EA", 64 },
            { "1/斯摩棱斯克/425B2090/42002910", 448 },
            { "1/斯普利特/422E0920/41838361", 128 },
            { "1/斯普林菲尔德/421F34F6/C2B34994", 128 },
            { "1/斯法克斯/420AF655/412C2A1B", 384 },
            { "1/斯科普里/4227FC60/41AB7387", 896 },
            { "1/新义州/422066F9/42F8CBCF", 1984 },
            { "1/新切尔卡斯克/423DAE44/42205D83", 256 },
            { "1/新加坡/3FA513E8/42CFB33C", 128 },
            { "1/新古比雪夫斯克/42546234/4247C8E9", 256 },
            { "1/新奥尔良/41EFA320/C2B42670", 128 },
            { "1/新居滨/4207D785/43054E23", 512 },
            { "1/新库兹涅茨克/425705D6/42AE3845", 64 },
            { "1/新汉堡/C1ED6D38/C24C85B2", 64 },
            { "1/新泰/420F9A2C/42EB80FE", 512 },
            { "1/新潟/42178BA5/430B0182", 384 },
            { "1/新竹/41C66DCB/42F1EFEE", 896 },
            { "1/新罗西斯克/4232E5C6/421711F1", 128 },
            { "1/新西伯利亚/425C2A7F/42A5DE84", 64 },
            { "1/新诺伊施塔特/4250999A/413A221F", 128 },
            { "1/无锡/41FC8D0C/42F093BF", 448 },
            { "1/日内瓦/4238CF13/40C4A97E", 192 },
            { "1/日巴拉那/C12E2A1B/C277CE83", 64 },
            { "1/日托米尔/42490F3A/41E569DC", 960 },
            { "1/日照/420DB5C3/42EEE91A", 896 },
            { "1/旧堡/42244445/42F5E666", 1536 },
            { "1/旧奥斯科尔/424D2FCA/42175DDF", 128 },
            { "1/旧扎戈拉/4229BB2B/41CD22B1", 384 },
            { "1/旭川/422F1520/430E5D69", 128 },
            { "1/昆卡/C039A29C/C29E0252", 64 },
            { "1/昆巴/40945C92/41172681", 256 },
            { "1/昆明/41C84FA6/42CD6FC9", 128 },
            { "1/昌原/420CE989/4300AE5D", 1536 },
            { "1/昌迪加尔/41F5E3EC/429993A9", 64 },
            { "1/明亚/41E0E109/41F6009D", 384 },
            { "1/明古鲁/C0733A69/42CC87F5", 128 },
            { "1/明斯克市/4257999A/41DC888A", 448 },
            { "1/明水/4212DDDF/42EB0000", 1024 },
            { "1/春川/42177FB7/42FF77E5", 1792 },
            { "1/春武里市/4155CB92/42C9F787", 512 },
            { "1/昭通/41DA888A/42CF6EEF", 128 },
            { "1/是拉差/4152C78F/42C9DCBA", 768 },
            { "1/晋城/420E0246/42E1AA62", 896 },
            { "1/晋州/420CC568/430015B0", 1536 },
            { "1/普伦茨劳贝格/425227B6/4156CA77", 128 },
            { "1/普列文/422DAAAC/41C4EEF1", 128 },
            { "1/普卡尔帕/C1061100/C2951B95", 64 },
            { "1/普埃布拉/419858C3/C2C46763", 128 },
            { "1/普尔尼亚/41CE3B4A/42AEF2CD", 256 },
            { "1/普拉亚/416EE782/C1BC19AF", 192 },
            { "1/普斯科夫/42674120/41E2CBFB", 64 },
            { "1/普林塞萨港/411BD3A4/42ED7877", 384 },
            { "1/普洛耶什蒂/4233CCCD/41D02224", 128 },
            { "1/普罗夫迪夫/4228999A/41C60000", 256 },
            { "1/普罗德达图尔/416C00D2/429D18A4", 128 },
            { "1/普罗维登斯/42274BC4/C28ED35E", 128 },
            { "1/普诺/C17D79A7/C28C0A30", 64 },
            { "1/普里什蒂纳/422AB0DE/41A955C5", 384 },
            { "1/普里兹伦/4228DB06/41A5EAF2", 768 },
            { "1/普雷图河畔圣若泽/C1A68EC9/C245848C", 64 },
            { "1/普韦布洛/4219048F/C2D137E1", 256 },
            { "1/普鲁利耶/41BAA51C/42ACB9DF", 256 },
            { "1/普鲁登特总统城/C1B10126/C24D8E39", 64 },
            { "1/景德镇/41EA5B8C/42EA6A71", 384 },
            { "1/暖武里/415DC5A2/42C90790", 512 },
            { "1/暹粒/4155C9E4/42CFB89B", 256 },
            { "1/曼坦派/4102AAAE/42F86EEF", 256 },
            { "1/曼塔/BF764D7F/C2A16CE8", 128 },
            { "1/曼彻斯特/4255EC7E/C00F320E", 64 },
            { "1/曼德勒/41AFCC3F/42C02ACC", 192 },
            { "1/曼苏拉/41F84A7C/41FB0BA7", 384 },
            { "1/曼萨尼约/41A2BF24/C29A3BBC", 128 },
            { "1/曼西林甘/412A1907/42F5F531", 512 },
            { "1/曼谷/415C104D/42C900BD", 960 },
            { "1/曼达韦市/41252D82/42F7D82D", 512 },
            { "1/朔庄市/41199965/42D3F1A1", 384 },
            { "1/朗布尔/41CDF91E/42B280DA", 256 },
            { "1/望加锡/C0A47AE1/42EED81D", 192 },
            { "1/朝阳/422647F7/42F0EACF", 384 },
            { "1/木各具/41AAADDB/42BE2B34", 256 },
            { "1/木浦/420B2CA8/42FCC6F8", 384 },
            { "1/本地治里/413EF0E3/429FA8DA", 64 },
            { "1/本溪/42252789/42F787AE", 1920 },
            { "1/札幌/422C4445/430D599A", 192 },
            { "1/朱古/411B5624/3FD53ED5", 256 },
            { "1/朱奈纳/41573DEE/41B393F8", 64 },
            { "1/朱巴/409B40B8/41FCA8E6", 64 },
            { "1/朱拜勒/41D816FB/4246A20C", 128 },
            { "1/杜兰戈/41C0299E/C2D150AC", 128 },
            { "1/杜姆古尔/415576BE/429A33B6", 128 },
            { "1/杜尔加布尔/41BBFF1F/42AEA4A2", 256 },
            { "1/杜阿拉/40818B6E/411B44BB", 320 },
            { "1/杜马/42064979/42119C5D", 1024 },
            { "1/杨州/4217551B/42FE1F96", 1024 },
            { "1/杭州/41F25965/42F052A6", 448 },
            { "1/杰克逊/420131EE/C2B45E9F", 128 },
            { "1/杰克逊维尔/41F2A84E/C2A34FB1", 128 },
            { "1/杰兹卡兹甘/423F2221/42878889", 128 },
            { "1/杰冈/41D6C802/42B2C04C", 256 },
            { "1/杰勒法/420AB0F0/4050D4FE", 384 },
            { "1/杰尔/423EBBBB/418D14BA", 128 },
            { "1/杰尔拜古里/41D42224/42B17777", 256 },
            { "1/杰索尔/41B95B91/42B26D6B", 256 },
            { "1/杰迪代/4205050A/C10818A8", 128 },
            { "1/松原/420A4445/43078CCD", 512 },
            { "1/松山/42075B4D/4304C408", 384 },
            { "1/松本/4210EEEE/4309F778", 384 },
            { "1/松江/420DEEEE/43050CCD", 896 },
            { "1/林茨/424139BE/416493E8", 128 },
            { "1/枚方/420B410B/4307A62E", 512 },
            { "1/枝城/41F25D4F/42DF026B", 256 },
            { "1/枣庄/420B7579/42EB1BBC", 128 },
            { "1/柏培拉/41270890/42340EAA", 320 },
            { "1/柏林/425218F4/41569188", 192 },
            { "1/查亚普拉/C022284E/430CB7D7", 64 },
            { "1/查塔努加/420C2EBA/C2AA9E8E", 128 },
            { "1/查尔斯顿/42031B35/C29FDCA2", 128 },
            { "1/查查克/422F90C9/41A2CC3A", 128 },
            { "1/柯枝/411F09C0/4298853C", 128 },
            { "1/株洲/41DEAAA9/42E24CCD", 896 },
            { "1/格伦代尔/42062794/C2E05F3A", 512 },
            { "1/格但斯克/42596880/41952BC4", 192 },
            { "1/格利维采/424930C1/419569A3", 128 },
            { "1/格勒诺布尔/4234AAAC/40B6EEF6", 128 },
            { "1/格拉/424B856B/41414F57", 128 },
            { "1/格拉斯哥/425F75EA/C0883E81", 64 },
            { "1/格拉纳达/4214C0B0/C066D3AE", 128 },
            { "1/格拉茨/423C4445/41773333", 128 },
            { "1/格林斯伯勒/42104A62/C29F957E", 128 },
            { "1/格林贝/4232139F/C2B00A27", 128 },
            { "1/格罗德诺/4256C0EC/41BE9B3D", 384 },
            { "1/格里姆讷格尔/41938361/429E41D3", 128 },
            { "1/桂林/41CA416A/42DC92A2", 192 },
            { "1/桃园/41C7F30E/42F2980B", 1536 },
            { "1/桑坦德/422DDBE0/C0737BF2", 128 },
            { "1/桑蒂普尔/41B9FA54/42B0DDB5", 256 },
            { "1/桑贾克泰佩/42240280/41E9DADF", 512 },
            { "1/桦甸/422BDA73/42FD7EDD", 512 },
            { "1/梁赞/425A81F2/421EC433", 192 },
            { "1/梅克内斯/420792F7/C0B1833C", 128 },
            { "1/梅利托波尔/423B6549/420D7619", 384 },
            { "1/梅尔辛/42132E59/420A78C0", 768 },
            { "1/梅州/41C261AB/42E836E8", 1408 },
            { "1/梅萨/4205A66E/C2DFA9ED", 512 },
            { "1/梅迪尼布尔/41B35E7F/42AEA528", 256 },
            { "1/梅里达/4109861A/C28E496C", 192 },
            { "1/梅里达/41A7CD8F/C2B33BE2", 192 },
            { "1/梧州/41BBDDDC/42DEA223", 896 },
            { "1/梭罗/C0F1CBA7/42DDA9D1", 128 },
            { "1/棉兰/40655547/42C55556", 192 },
            { "1/森赖斯马诺/4210D825/C2E62568", 1024 },
            { "1/楠代德/41994827/429AA144", 128 },
            { "1/榆树/42333333/42FD1111", 256 },
            { "1/榆次/4216B89B/42E176C1", 512 },
            { "1/横滨/420DBBBB/430BA666", 256 },
            { "1/橿原市/420A092D/4307CAFB", 512 },
            { "1/檀香山/41AA749D/C31DDBBC", 64 },
            { "1/欧拜伊德/4152F286/41F1BBC8", 64 },
            { "1/欧申赛德/4204C892/C2EAC24B", 128 },
            { "1/武汉/41F4AAA9/42E48889", 448 },
            { "1/武端/410F2FCD/42FB1654", 256 },
            { "1/比亚埃尔莫萨/418FE527/C2B9DC4E", 128 },
            { "1/比亚维森西奥/40848B44/C29340D7", 128 },
            { "1/比亚韦斯托克/42548888/41B9508C", 896 },
            { "1/比什凯克/422B7AE1/42952E14", 192 },
            { "1/比塞大/42151902/411DFB89", 896 },
            { "1/比尔詹德/42037712/426CE273", 128 },
            { "1/比得哥什/42547E77/41900F9B", 128 },
            { "1/比德尔/418F4E75/429B0F6B", 128 },
            { "1/比拉德讷格尔/41D3DD64/42AE9116", 256 },
            { "1/比斯克拉/420B66CA/40B74C30", 384 },
            { "1/比莱/41A9AC6C/42A2DB64", 192 },
            { "1/比贾布尔/4186986A/42976E45", 128 },
            { "1/比那尔德里奥/41B3570A/C2A76568", 128 },
            { "1/比里吉/C1AA4F13/C2495C29", 64 },
            { "1/毕尔巴鄂/422D0D04/C03B37CA", 192 },
            { "1/毛淡棉/4183EC91/42C341AA", 384 },
            { "1/民都鲁/404AAAB9/42E21111", 128 },
            { "1/水原/42152A19/42FE048D", 1664 },
            { "1/水户/42116666/430C7333", 384 },
            { "1/水牛城/422B8BBA/C29DC1BA", 128 },
            { "1/汉中/42044A87/42D60F81", 128 },
            { "1/汉堡/42564D21/41203ED5", 64 },
            { "1/汕头/41BAF1F3/42E96DF9", 448 },
            { "1/汗尤尼斯/41FAB8B0/4209399F", 1536 },
            { "1/江界/4223E0BA/42FD2BA3", 896 },
            { "1/江门/41B4AAA9/42E22AAA", 384 },
            { "1/江陵/421705B2/4300E567", 1536 },
            { "1/汤斯维尔/C19A2191/4312CE42", 64 },
            { "1/沃思堡/4202E6D2/C2C2A446", 384 },
            { "1/沃洛格达/426CE546/421F8932", 64 },
            { "1/沃罗涅日/424EB02B/421CBCB9", 192 },
            { "1/沈阳/42272B3C/42F6DD95", 896 },
            { "1/沙佩科/C1D8C568/C252792B", 64 },
            { "1/沙赫扎德普尔/41C16A3B/42B33296", 256 },
            { "1/沙赫特/423ED621/4220DB94", 384 },
            { "1/沙里院/421A0765/42FB82FC", 1536 },
            { "1/沙马林达/BEF8C155/42EA4B0F", 128 },
            { "1/沧州/42194445/42E9BBBC", 768 },
            { "1/河内市/41A8322D/42D3AEAE", 960 },
            { "1/河南/421628F6/42FE693F", 1024 },
            { "1/泉州/41C74FA6/42ED2BF2", 1984 },
            { "1/法兰克福/4248764B/410AF25C", 64 },
            { "1/法奥/41EFCB2F/4241E472", 256 },
            { "1/法尤姆/41EA7AC7/41F6BC02", 448 },
            { "1/法里德普尔/41BCD955/42B3AE68", 256 },
            { "1/泗水/C0E7F933/42E1806D", 128 },
            { "1/波兹南/4251A0B0/4187707F", 128 },
            { "1/波哥大/409382BF/C29429DB", 128 },
            { "1/波多利斯克/425DB25F/42163809", 128 },
            { "1/波尔图/42249933/C109C69D", 128 },
            { "1/波尔塔瓦/42465FFB/420A29B5", 128 },
            { "1/波尔多/42335C9C/BF149BA6", 192 },
            { "1/波帕扬/401C0BF6/C29939F0", 128 },
            { "1/波德戈里察/4229C3B2/419A1BE0", 384 },
            { "1/波托西/C19CAB3C/C2838191", 64 },
            { "1/波来古市/415FBBB8/42D80000", 384 },
            { "1/波苏阿莱格里/C1B1D70A/C237BEDD", 64 },
            { "1/波萨达斯/C1DAEFC8/C25F9596", 64 },
            { "1/波萨里卡/41A443E4/C2C2EB3E", 128 },
            { "1/泰基尔达/4223E97B/41DC1173", 896 },
            { "1/泰安/4210BDBA/42EA3D71", 384 },
            { "1/泰尔梅兹/4214E58D/42868E81", 128 },
            { "1/泰尼/41203EB6/429AF6C9", 128 },
            { "1/泰州/4201F677/42EFD0F5", 384 },
            { "1/泰贝萨/420D9DDF/4101FC9A", 384 },
            { "1/泽西市/4222E9A3/C29427C0", 128 },
            { "1/洛斯莫奇斯/41CE5CE0/C2D9FC21", 128 },
            { "1/洛杉矶/4208357C/C2EC7CC4", 640 },
            { "1/洛格罗尼奥/4229DDDF/C01CCCCD", 128 },
            { "1/洛桑/423A1062/40D44010", 128 },
            { "1/洛美/40C4663C/3F9B2BAA", 64 },
            { "1/洛阳/420ABC04/42E0E840", 128 },
            { "1/洛阿贾南/BF153C36/42EA30A8", 128 },
            { "1/津/420AEEEE/43088444", 896 },
            { "1/津德尔/415CEA21/410FCF42", 64 },
            { "1/济南/4212AC5F/42E9FE94", 384 },
            { "1/济宁/420D9EB8/42E929AC", 384 },
            { "1/济州/420609F4/42FD0B3C", 896 },
            { "1/浦那/41942814/4293B5F0", 192 },
            { "1/浦项/421020FE/43015D71", 1920 },
            { "1/海伦/423DD375/42FDE849", 128 },
            { "1/海口/41A05DDC/42DCAEEF", 512 },
            { "1/海地角/419E1336/C2906574", 128 },
            { "1/海城/42236805/42F57EBE", 1024 },
            { "1/海州/42182989/42FB6DF0", 1920 },
            { "1/海得拉巴/418B1289/429CE9A8", 192 },
            { "1/海得拉巴/41CB23AD/4288BF50", 64 },
            { "1/海法/4203460D/420BF439", 1792 },
            { "1/海防市/41A6EB22/42D55DED", 896 },
            { "1/涅夫捷卡姆斯克/42605E35/4259107D", 128 },
            { "1/涅夫捷尤甘斯克/4274662F/429134FD", 64 },
            { "1/淄博/42132989/42EC206D", 1792 },
            { "1/淮北/4207E5D4/42E99556", 384 },
            { "1/淮南/4202816C/42E9FE6F", 384 },
            { "1/淮安/42065ABD/42EE09D1", 256 },
            { "1/深圳/41B45D44/42E422F8", 256 },
            { "1/清化/419E6666/42D38889", 384 },
            { "1/清州/42128C83/42FEFABD", 1792 },
            { "1/清津/42272EA7/4301C69D", 384 },
            { "1/清迈/419652B3/42C5F828", 448 },
            { "1/清远/41BD999A/42E21111", 896 },
            { "1/渥太华/4235A50A/C2976570", 128 },
            { "1/温哥华/4244FFA7/C2F63D1A", 64 },
            { "1/温尼伯/424789A0/C2C24B49", 64 },
            { "1/温州/41DFFED0/42F15569", 448 },
            { "1/渭南/420A03A3/42DB0490", 128 },
            { "1/湖州/41F6F660/42F02FC5", 384 },
            { "1/湘潭/41DECCCD/42E1CCCD", 512 },
            { "1/湛江/41AA4069/42DCAF78", 960 },
            { "1/滨州/42157778/42EC0889", 896 },
            { "1/滨松/420ACCCD/4309BBBC", 384 },
            { "1/滨河圣胡安/41A31A37/C2C7FDFA", 128 },
            { "1/漯河/42064964/42E41210", 256 },
            { "1/漳州/41C41B4D/42EB4FA6", 256 },
            { "1/潍坊/4212D70A/42EE3431", 256 },
            { "1/潘切/412EDCBC/42D83444", 1920 },
            { "1/潘普洛纳/422B447A/BFD2555C", 128 },
            { "1/潮州/41BD3B4F/42E93EC8", 256 },
            { "1/濮阳城关镇/420ED1FB/42E60737", 512 },
            { "1/烟台/4215E7ED/42F2E1B2", 448 },
            { "1/热基耶/C15DBD71/C220572A", 64 },
            { "1/热舒夫/42482A50/41AFFDF9", 896 },
            { "1/热那亚/4231B4A2/410EA49E", 128 },
            { "1/焦作/420CF579/42E27754", 384 },
            { "1/焦特布尔/41D225B4/4292030B", 64 },
            { "1/熊本/4203393B/4302B11A", 384 },
            { "1/爱尔巴桑/42247333/41A0A863", 768 },
            { "1/爱迪生/4222132B/C294D2FF", 128 },
            { "1/牙克石/42452221/42F17777", 384 },
            { "1/牡丹江/42325554/4301999A", 384 },
            { "1/特古西加尔巴/41614F0E/C2AE69E3", 128 },
            { "1/特奥菲卢奥托尼/C18EDC29/C2260568", 64 },
            { "1/特尔古·穆列什/423A2B78/41C475B3", 128 },
            { "1/特拉布宗/4224051F/421EE863", 384 },
            { "1/特拉维夫/420052D2/420B1F4E", 1856 },
            { "1/特拉萨/42264445/4001111F", 256 },
            { "1/特维尔/42636EF6/420F9A2F", 448 },
            { "1/特莱姆森/420B8369/BFA851EC", 384 },
            { "1/特里凡得琅/4107C49C/4299E603", 192 },
            { "1/特隆赫姆/427DB8D2/4126522A", 64 },
            { "1/特鲁希略/C101DB18/C29E0F5A", 64 },
            { "1/玉林/41B5110F/42DC4CCD", 1984 },
            { "1/玉溪/41C2D70A/42CD159E", 128 },
            { "1/玛琅/C0FF59B4/42E142C4", 128 },
            { "1/玻利瓦尔城/41021153/C27E29D7", 128 },
            { "1/珠海/41B2372C/42E322B4", 384 },
            { "1/班加罗尔/414F8D11/429B2FF8", 192 },
            { "1/班加西/4200759E/41A08C79", 448 },
            { "1/班基/408B8F1D/4194708F", 64 },
            { "1/班德尔马/422168AC/41DFD038", 512 },
            { "1/班达亚齐/40B1D8AE/42BEA4F7", 192 },
            { "1/班达楠榜/C0AD9D34/42D2841D", 192 },
            { "1/班顿杜/C0544799/418B0B88", 64 },
            { "1/瓜亚基尔/C00D2C7C/C29FD0DF", 192 },
            { "1/瓜伊马斯/41DF5AAF/C2DDCB8C", 128 },
            { "1/瓜地马拉市/416A4064/C2B506CB", 128 },
            { "1/瓜廖尔/41D1D6B1/429C58C4", 64 },
            { "1/瓜拉普阿瓦/C1CB1FB4/C24DDC94", 64 },
            { "1/瓜拉登嘉楼/40AA9100/42CE4817", 128 },
            { "1/瓜达拉哈拉/41A555A6/C2CEC89D", 128 },
            { "1/瓦乌/40F67DD4/41DFF660", 512 },
            { "1/瓦乌布日赫/424B15ED/4182464A", 128 },
            { "1/瓦加杜古/4145D9BE/BFC4562E", 320 },
            { "1/瓦哈卡市/418885FB/C2C17282", 192 },
            { "1/瓦多达拉/41B26531/42926A8F", 64 },
            { "1/瓦尔任阿/C1AC693F/C235B89B", 64 },
            { "1/瓦尔格拉/41FF9835/40AA6690", 896 },
            { "1/瓦尔纳/422CDDDF/41DF5557", 128 },
            { "1/瓦德/42056CA0/40DB9F41", 384 },
            { "1/瓦房店/421E792B/42F40420", 256 },
            { "1/瓦拉纳西/41CA8890/42A60554", 64 },
            { "1/瓦拉达里斯州长市/C196CF13/C227CC3A", 64 },
            { "1/瓦斯科·达伽马/41765567/4293A1A1", 128 },
            { "1/瓦朗加尔/41900000/429F2AAA", 128 },
            { "1/瓦纳佐尔/422337DF/4231F9C1", 256 },
            { "1/瓦里/40B08E4C/40B8007E", 320 },
            { "1/甘达吉卡/C0D7D75E/41BFA051", 128 },
            { "1/生驹/420ABBBB/4307B333", 512 },
            { "1/甲万那端市/4177C615/42F1EED6", 384 },
            { "1/甲府/420EAAAC/430A9111", 384 },
            { "1/界首/42050DB3/42E6B8DF", 384 },
            { "1/登巴萨/C10A6666/42E66EEF", 128 },
            { "1/登顿/4204DBFF/C2C24422", 384 },
            { "1/白城/42366E0A/42F5A43B", 384 },
            { "1/白山/4227C10E/42FCD6DC", 768 },
            { "1/白沙瓦/42080831/428F2830", 64 },
            { "1/白采尔科维/42473CD1/41F0E58F", 768 },
            { "1/的里雅斯特/423692B0/415CA512", 128 },
            { "1/的黎波里/42038032/4152FFD6", 448 },
            { "1/的黎波里/4209BF26/420F661D", 1792 },
            { "1/皮乌拉/C0A63943/C2A14401", 64 },
            { "1/皮奥里亚/4222C64C/C2B32D90", 128 },
            { "1/皮文迪/419A66DF/42921E1C", 128 },
            { "1/皮特什蒂/42336666/41C6EEF1", 128 },
            { "1/益山/420FC68B/42FDE8AC", 1536 },
            { "1/益阳/41E4B69F/42E0A81B", 128 },
            { "1/盐城/42056E14/42F0508A", 256 },
            { "1/盐湖城/42230B0A/C2DFC838", 512 },
            { "1/盖尼特拉/42090B46/C0D29100", 128 },
            { "1/盖布泽/42233607/41EB7208", 512 },
            { "1/盖恩斯维尔/41ED368A/C2A4A650", 128 },
            { "1/盘山/4224C093/42F41950", 1024 },
            { "1/盛冈/421ECCCD/430D2666", 384 },
            { "1/石家庄/42182A62/42E4F50C", 960 },
            { "1/石河子/4231358E/42AC12EA", 64 },
            { "1/碧瑶/418354C4/42F12FA6", 1472 },
            { "1/磐城/42143333/430CE222", 384 },
            { "1/祖拜尔/41F32315/423ECE98", 256 },
            { "1/神户/420AC3E4/43072ED9", 768 },
            { "1/福井市/421041FA/430838FA", 384 },
            { "1/福冈/42066666/43026AAB", 960 },
            { "1/福塔雷萨/C06DE6EF/C21A2C18", 64 },
            { "1/福岛/42170000/430C7778", 384 },
            { "1/福州/41D07DBA/42EE9CBA", 1920 },
            { "1/福莫萨/C1D16B95/C268B66A", 64 },
            { "1/福贾/4225D574/4178D480", 384 },
            { "1/秋明/42649BE0/42830DF0", 192 },
            { "1/秋田/421EDDDF/430C1DDE", 384 },
            { "1/科克舍套/4255213B/428ACB39", 128 },
            { "1/科卢韦齐/C12B6FFC/41CBBBE2", 128 },
            { "1/科塔/41C975D8/4297AD9B", 64 },
            { "1/科夫罗夫/42616DCB/422546D5", 128 },
            { "1/科尔多瓦/419712D7/C2C1D9E7", 128 },
            { "1/科尔多瓦/421790F2/C098BA5E", 192 },
            { "1/科尼亚/42177C43/4201F045", 448 },
            { "1/科希策/4242DB16/41AA108C", 384 },
            { "1/科林斯堡/4222574E/C2D22B39", 256 },
            { "1/科泽科德/4133F7E4/42978F91", 192 },
            { "1/科珀斯克里斯蒂/41DE6796/C2C2CAF2", 128 },
            { "1/科纳克里/41189B71/C15AD62E", 256 },
            { "1/科罗拉多斯普林斯/421B55E5/C2D1A489", 256 },
            { "1/科英布拉/4220D293/C106B67A", 128 },
            { "1/科霍戈/41175417/C0B425C4", 256 },
            { "1/秦皇岛/421FBA08/42EF2D3A", 384 },
            { "1/穆尔西亚/4217F2BB/BF90A527", 384 },
            { "1/穆斯塔加奈姆/420FB97F/3DB6A401", 384 },
            { "1/穆斯塔法凯马尔帕沙/42202711/41E344F0", 512 },
            { "1/穆索马/BFC00000/42073333", 128 },
            { "1/穆罗姆/425E4CCD/42282B9F", 128 },
            { "1/穆阿斯凯尔/420D9629/3E0FA2F0", 384 },
            { "1/空銮/416108E3/42C94AA4", 512 },
            { "1/突尼斯/421346A0/4122A713", 960 },
            { "1/符拉迪沃斯托克/422C6C28/4303DFA0", 448 },
            { "1/第一乌拉尔斯克/42639F02/426FC642", 128 },
            { "1/第比利斯/4226C6C5/423355B0", 448 },
            { "1/箕面/420B4EC1/43077877", 512 },
            { "1/米什科尔茨/42406666/41A64443", 128 },
            { "1/米兰/4235DB6A/4113083C", 192 },
            { "1/米子/420DBBBB/43055555", 512 },
            { "1/米德兰/41FFFA93/C2CC27E4", 384 },
            { "1/米扬道阿卜/4213DDDF/4238703E", 512 },
            { "1/米特罗维察/422B8888/41A6EEF1", 256 },
            { "1/米苏拉塔/4201805C/41717B0B", 448 },
            { "1/米里亚尔古达/4186FA44/429F1FFC", 128 },
            { "1/素叻他尼/41123DE4/42C6AA8D", 448 },
            { "1/索利戈尔斯克/42532681/41DC54FE", 512 },
            { "1/索哈杰/41D474A2/41FD8EE9", 896 },
            { "1/索契/422E658D/421EE71B", 192 },
            { "1/索拉布尔/418D5F46/4297D225", 128 },
            { "1/索科德/410FBBB8/3F9110F5", 256 },
            { "1/索科托/415100C7/40A7C875", 64 },
            { "1/索科罗圣母城/C12DAE14/C2148123", 64 },
            { "1/索莱达德-迪亚斯古铁雷斯/41B18093/C2C9E243", 128 },
            { "1/索菲亚/422ACA40/41BA97DC", 384 },
            { "1/索龙/BF612AD8/430342D4", 128 },
            { "1/累西腓/C100DCBC/C20B8642", 64 },
            { "1/红岗/42399ADF/42F9C435", 256 },
            { "1/纳什维尔/4210A9DF/C2AD91A2", 128 },
            { "1/纳兹雷特/4108CCCD/421D1112", 256 },
            { "1/纳尔奇克/422DFE03/422E79BE", 128 },
            { "1/纳巴德维普/41BB40EC/42B0BCBA", 256 },
            { "1/纳布卢斯/4200E26B/420D048C", 1792 },
            { "1/纳库鲁/BE9110A1/42104445", 384 },
            { "1/纳恩迪亚尔/4177D215/429CF914", 128 },
            { "1/纳扎拉巴德/420FD102/424A6C9E", 256 },
            { "1/纳拉扬甘杰/41BCE87D/42B50187", 384 },
            { "1/纳杰夫/42001A90/4231628F", 320 },
            { "1/纳沃伊/42205677/4282C223", 128 },
            { "1/纳沃伊/42205F2C/4282BE0E", 128 },
            { "1/纳沃华/41D88FAB/C2DAE348", 128 },
            { "1/纳济利/4217AA4D/41E293F8", 512 },
            { "1/纳西克/419FFA69/429394F9", 192 },
            { "1/纳西里耶/41F876C3/4239076F", 384 },
            { "1/纽约市/4222DB6A/C294030E", 128 },
            { "1/绍兴/41F004DB/42F12844", 256 },
            { "1/绥化/423A8EE4/42FDFD7B", 128 },
            { "1/维也纳/4240D57E/4182FA05", 192 },
            { "1/维亚芒/C1F0A61D/C24C17E4", 64 },
            { "1/维克多维尔/420A24FA/C2EA9513", 128 },
            { "1/维塞利亚/42115228/C2EE9589", 128 },
            { "1/维多利亚-德拉斯图纳斯/41A7B180/C299E6F8", 128 },
            { "1/维多利亚-达孔基斯塔/C16DDB96/C2235B96", 64 },
            { "1/维多利亚/C1A28E37/C22159E3", 64 },
            { "1/维多利亚城/41BDEF15/C2C64ABF", 128 },
            { "1/维尔纽斯/425AC1B3/41CA3D08", 448 },
            { "1/维戈/4228EE68/C10B8FEF", 192 },
            { "1/维捷布斯克/425CC2F8/41F1A3A3", 384 },
            { "1/维杰亚瓦达/4184277C/42A142D0", 128 },
            { "1/维罗纳/4235B838/412FC044", 128 },
            { "1/绵阳/41FBACBA/42D1822A", 192 },
            { "1/绿山城/424FBDEE/41781A56", 128 },
            { "1/罗兹/424F0000/419BBBBD", 128 },
            { "1/罗利/420F16A1/C29D46F8", 128 },
            { "1/罗勇市/414AE52C/42CA8416", 384 },
            { "1/罗哈斯市/41395D4F/42F58091", 384 },
            { "1/罗夷/412A8F13/42D78B4B", 1536 },
            { "1/罗彻斯特/422C9E7F/C29B3B2B", 128 },
            { "1/罗斯托克/42585AD4/41423F72", 128 },
            { "1/罗马/42279156/41482E68", 384 },
            { "1/罗马纳/41936B2C/C289F219", 128 },
            { "1/美湫/4125C2B9/42D4B84D", 256 },
            { "1/美里/408D460B/42E4048F", 128 },
            { "1/群山/420FEA19/42FD6C3B", 1920 },
            { "1/翁果尔/41781A4C/42A018EB", 128 },
            { "1/考拉克/4162EB85/C18206D2", 128 },
            { "1/考那斯/425B999A/41BF3333", 384 },
            { "1/耶拉汉卡/41519EE2/429B3149", 128 },
            { "1/耶拿/424BB712/4139703B", 128 },
            { "1/耶路撒冷/41FE26FE/420CDD86", 1792 },
            { "1/聊城/4211D2E7/42E7F490", 384 },
            { "1/肇东/42384556/42FBF71B", 384 },
            { "1/胡姆斯/4202982D/416430C9", 384 },
            { "1/胡富夫/41CAEAA4/424642E3", 128 },
            { "1/胡布利/4175906D/4296447F", 192 },
            { "1/胡志明市/412D2B17/42D54261", 64 },
            { "1/自贡/41EABBA3/42D18DC5", 128 },
            { "1/舍讷贝格区/4251DDDF/4155999A", 128 },
            { "1/艾哈迈迪/41E89D93/424055E7", 256 },
            { "1/艾因/41C1888A/425F0AD0", 128 },
            { "1/艾季达比耶/41F60B29/41A1CF5C", 384 },
            { "1/艾尔帕索/41FE11DC/C2D4F94F", 384 },
            { "1/艾恩维尔/4219D36D/C2A56280", 128 },
            { "1/艾斯尤特/41D9729B/41F9782D", 448 },
            { "1/艾格瓦特/42073333/40375E74", 384 },
            { "1/艾登/4217614A/41DEB790", 768 },
            { "1/艾藻尔/41BDD4DE/42B96F92", 384 },
            { "1/芜湖/41FAB2C0/42ECBF3D", 384 },
            { "1/芝加哥/4227666E/C2AF4CD3", 128 },
            { "1/花莲/41BFD0C6/42F33579", 1920 },
            { "1/芹湥/4129BCB4/42D557BB", 512 },
            { "1/芹苴/41209801/42D39396", 384 },
            { "1/芽庄/4143EBCF/42DA637E", 1408 },
            { "1/苏丹加济/42246D14/41E6F2A0", 512 },
            { "1/苏丹港/419CF08A/4214DDA2", 64 },
            { "1/苏伊士/41EFCA28/42021AE7", 448 },
            { "1/苏克雷/C198443D/C2828686", 64 },
            { "1/苏加武眉/C0DD60BF/42D5DA74", 128 },
            { "1/苏哈尔/41C2EA16/4262FCBC", 384 },
            { "1/苏姆盖特/42225BE0/4246ACA8", 256 },
            { "1/苏家屯/4226A2FD/42F6ADA8", 1536 },
            { "1/苏尔古特/42750000/4292D556", 64 },
            { "1/苏尔特/41F9ABDE/4184B593", 384 },
            { "1/苏州/41FA6EC1/42F130D6", 384 },
            { "1/苏恰瓦/423E8888/41D20000", 256 },
            { "1/苏拉特/41A99149/4291A914", 192 },
            { "1/苏斯/420F4D33/412A311C", 448 },
            { "1/苏格艾赫拉斯/42112543/40FE6F7E", 384 },
            { "1/苏梅/424BAFB8/420B337F", 128 },
            { "1/苏莱曼尼亚/420E4285/4235BB4A", 896 },
            { "1/苏里亚佩特/41891FD3/429F3DAC", 128 },
            { "1/苏韦格/41BECBA7/4265C123", 256 },
            { "1/若茵维莱/C1D26F7E/C24361DA", 64 },
            { "1/苫小牧/422A8C3A/430D9A74", 128 },
            { "1/英格利希巴扎尔/41C80928/42B04A9D", 256 },
            { "1/茂物/C0D305A7/42D5940E", 128 },
            { "1/茹伊斯迪福拉/C1AE1D05/C22D66B0", 64 },
            { "1/荆门/41F844D5/42E068D1", 384 },
            { "1/草津/420C1112/4307F778", 512 },
            { "1/荣市/41956310/42D36278", 384 },
            { "1/莆田/41CB83F9/42EE0543", 1920 },
            { "1/莫吉廖夫/4257AACE/41F2C25B", 384 },
            { "1/莫尔兹比港/C117171A/43132E02", 64 },
            { "1/莫拉达巴德/41E6B621/429D8DBE", 64 },
            { "1/莫斯塔尔/422D5F92/418E76E8", 128 },
            { "1/莫斯科/425F0246/42167655", 128 },
            { "1/莫普提/4167BFB1/C085DACF", 1792 },
            { "1/莫比尔/41F58E0D/C2B0160B", 128 },
            { "1/莫洛杰奇诺/4259444D/41D6D4FE", 256 },
            { "1/莫济里/425032B0/41E9F6FD", 896 },
            { "1/莫雷利亚/419D9B33/C2CA5E6E", 128 },
            { "1/莱凯/41918BE6/C2937DF5", 128 },
            { "1/莱昂/41470184/C2ADC18E", 128 },
            { "1/莱昂/41A90B4E/C2CB578D", 192 },
            { "1/莱格尼察/424CD71A/41814B92", 128 },
            { "1/莱比锡/424D5BC5/4145F0CE", 128 },
            { "1/莱芜/4210C568/42EB505A", 1792 },
            { "1/莱里达/4226778B/3F1F4730", 256 },
            { "1/莱阳/4213E740/42F16D5E", 384 },
            { "1/菏泽/420CF508/42E6F279", 384 },
            { "1/萍乡/41DCEF0B/42E3B502", 1984 },
            { "1/营口/4222A8C7/42F474E8", 384 },
            { "1/萨凡纳/4200558C/C2A2331D", 128 },
            { "1/萨南达季/420D4282/423BFECD", 768 },
            { "1/萨卜泽瓦尔/4210D9B4/4266BA47", 128 },
            { "1/萨卡特卡斯/41B625BF/C2CD29AF", 128 },
            { "1/萨图马雷/423F3333/41B7110F", 256 },
            { "1/萨姆巴尔普尔/41AB999A/42A7EEEF", 128 },
            { "1/萨姆松/4225258D/421151EC", 448 },
            { "1/萨尔/41124952/4193236E", 256 },
            { "1/萨尔蒂约/41CB62BC/C2CA02B7", 128 },
            { "1/萨拉戈萨/42269FCE/BF60995B", 384 },
            { "1/萨拉托夫/424E2989/423808D1", 64 },
            { "1/萨拉曼卡/41A49360/C2CA6212", 128 },
            { "1/萨拉曼卡/4223E012/C0B53E81", 128 },
            { "1/萨拉热窝/422F6502/4192D9FD", 192 },
            { "1/萨斯卡通/42507798/C2D544E0", 64 },
            { "1/萨格勒布/423741FD/417FA5CE", 128 },
            { "1/萨波潘/41A5C9DA/C2CEC503", 128 },
            { "1/萨特希拉/41B5AA55/42B224CA", 256 },
            { "1/萨玛沃/41FAA7E5/42352D77", 384 },
            { "1/萨瓦德尔/42262C54/400700BD", 256 },
            { "1/萨莫拉-德伊达尔戈/419FE24E/C2CC9157", 128 },
            { "1/萨莱诺/4222B3A9/416CB146", 256 },
            { "1/萨菲/42013293/C113CB7D", 192 },
            { "1/萨那/4175ACEF/4230D3A1", 64 },
            { "1/萨里市/421240D7/42543D88", 384 },
            { "1/萨韦/420C15D0/42496D29", 384 },
            { "1/萨马拉/4254CCDF/4248999A", 320 },
            { "1/蒂塔加尔/41B5F0AE/42B0BF1C", 256 },
            { "1/蒂拉斯波尔/423B5EF7/41ED0860", 128 },
            { "1/蒂米什瓦拉/423703CF/41A9CE41", 128 },
            { "1/蒂鲁吉拉帕利/412D0C4A/429D649D", 128 },
            { "1/蒂鲁普/4131D8B8/429AB589", 128 },
            { "1/蒙克洛瓦/41D74145/C2CAD754", 128 },
            { "1/蒙吉尔/41CB0126/42ACF271", 256 },
            { "1/蒙哥马利/4201779D/C2AC9996", 128 },
            { "1/蒙巴萨/C081BFC6/421EA784", 64 },
            { "1/蒙彼利埃/422E7195/40782489", 128 },
            { "1/蒙杜/41091115/4180AAA9", 64 },
            { "1/蒙特利尔/4236090D/C2932CF5", 128 },
            { "1/蒙特里亚/410BF7BA/C297C34B", 128 },
            { "1/蒙特雷/41CD668B/C2C8A30E", 128 },
            { "1/蒙育瓦/41B0DE55/42BE458C", 384 },
            { "1/蒙蒂斯克拉鲁斯/C185E148/C22F725A", 64 },
            { "1/蒲圻/41EDBBBD/42E3C444", 256 },
            { "1/蓬塔格罗萨/C1C8C28F/C248A5D4", 64 },
            { "1/蓬托菲霍/413B212D/C28C662E", 128 },
            { "1/蓬纳尼/412C456D/4297D9B8", 128 },
            { "1/蔚山/420E261D/43015111", 1920 },
            { "1/薄寮市/4114B4CC/42D3749D", 256 },
            { "1/蚌埠/4203C369/42EAB8BF", 384 },
            { "1/衡水/4216EDCB/42E766F8", 768 },
            { "1/襄阳/42002B36/42E04A22", 384 },
            { "1/西乔丹/4222704D/C2DFE0D2", 512 },
            { "1/西哈努克市/4129BFC6/42CF0F25", 512 },
            { "1/西圣巴巴拉/C1B60765/C23DA789", 64 },
            { "1/西宁/4212808E/42CB83C9", 192 },
            { "1/西安/42090888/42D9DB73", 192 },
            { "1/西宫滨/420ADCCB/430754FD", 512 },
            { "1/西尔恰尔/41C69E5F/42B99882", 128 },
            { "1/西尾/420B7778/43090CCD", 512 },
            { "1/西昌/41DF2BDE/42CC86DE", 192 },
            { "1/西瓦利城/4222C435/C2E0008A", 512 },
            { "1/西米谷/420913EB/C2ED901E", 512 },
            { "1/西纳/42133AE7/422C273B", 512 },
            { "1/西迪贝勒阿巴斯/420CC280/BF217F63", 384 },
            { "1/西里古里/41D5AE29/42B0DB66", 256 },
            { "1/西隆/41CC8D26/42B7C42A", 128 },
            { "1/让蒂尔港/BF382603/410C8111", 64 },
            { "1/议政府/4216F74C/42FE1845", 1536 },
            { "1/讷河/4241EEEE/42F9AAAA", 384 },
            { "1/设拉子/41ECE1EA/42521FE1", 192 },
            { "1/诗巫/40133333/42DFA223", 128 },
            { "1/诺克斯维尔/420FD7B2/C2A7D76B", 128 },
            { "1/诺尔辛迪/41BF6243/42B56F74", 256 },
            { "1/诺曼/420CE3E9/C2C2E104", 256 },
            { "1/诺维萨德/423501B6/419EB20E", 128 },
            { "1/诺金斯克/425F7744/4219C674", 128 },
            { "1/谢利夫/4210A937/3FAAD18D", 384 },
            { "1/谢尔布尔/41C82686/42B408F7", 384 },
            { "1/谢尔普霍夫/425BA9C2/4215A502", 128 },
            { "1/豪拉/41B49D73/42B0A31C", 256 },
            { "1/贝伦/BFBA58A3/C242048C", 64 },
            { "1/贝克斯菲尔德/420D7E40/C2EE0994", 640 },
            { "1/贝古萨赖/41CB5926/42AC448D", 256 },
            { "1/贝尔加莫/4236C8B7/411AACE4", 128 },
            { "1/贝尔格莱德/4233374E/41A3B896", 192 },
            { "1/贝尔法斯特/425A6325/C0BD9CF5", 64 },
            { "1/贝尔谢巴/41FA03B5/420B2A4B", 1792 },
            { "1/贝尔高姆/417DA249/4295024A", 128 },
            { "1/贝尼苏韦夫/41E89864/41F8C866", 256 },
            { "1/贝拉里/417254FE/4299D5C4", 128 },
            { "1/贝沙尔/41FCEEF1/C00DDDEC", 128 },
            { "1/贝洛奥里藏特/C19F5DDC/C22FC049", 64 },
            { "1/贝贾亚/42130603/40A2B2D5", 896 },
            { "1/贝鲁特/42078E46/420DFA49", 1792 },
            { "1/贡塔卡尔/4172BD7B/429ABB36", 128 },
            { "1/贡德尔/4149999A/4215DDDF", 64 },
            { "1/贡迪耶/41ABAE63/42A06252", 128 },
            { "1/贵阳/41D4AAA9/42D56EEF", 128 },
            { "1/费卢杰/42056582/422F24DB", 256 },
            { "1/费城/421FCF30/C29653DC", 128 },
            { "1/费耶特维尔/420C35ED/C29DC1B8", 128 },
            { "1/贾姆穆/4202F159/4295BCFC", 64 },
            { "1/贾巴尔普尔/41B955F4/429FE66E", 64 },
            { "1/贾马尔普尔/41C75B71/42B3E570", 256 },
            { "1/贾马梅/3D8EB463/422AFAD9", 256 },
            { "1/赖久尔/4181A4C8/429AB61A", 128 },
            { "1/赖布尔/41A9DDDC/42A34444", 128 },
            { "1/赖根杰/41CCE709/42B03FBD", 256 },
            { "1/赖格尔/41AF3333/42A6CCCD", 128 },
            { "1/赛义德布尔/41CE38B6/42B1C88C", 256 },
            { "1/赛达/42063D5E/420D80A8", 1792 },
            { "1/赞詹/4212B1C4/4241EA30", 256 },
            { "1/赤塔/42502079/42E30072", 64 },
            { "1/赤峰/422912C5/42EDED5E", 448 },
            { "1/赫尔松/423A9F8D/420278A1", 128 },
            { "1/赫尔辛基/4270AD97/41C77BCD", 64 },
            { "1/赫拉特/42096487/4278CC76", 192 },
            { "1/赫梅利尼茨基/4245AFBB/41D7F8E5", 768 },
            { "1/路易维尔/42190457/C2AB84D1", 128 },
            { "1/车里雅宾斯克/425C9DB7/4275B773", 64 },
            { "1/辛塞莱霍/4114E022/C296CBAA", 128 },
            { "1/辛杜普尔/415D43E9/429AFC1C", 128 },
            { "1/辛菲罗波尔/4233D42A/42087173", 128 },
            { "1/辛辛那提/421CA5E3/C2A8E9ED", 128 },
            { "1/边和市/412F1D73/42D5A60D", 1792 },
            { "1/辽源/422B9D4C/42FA458C", 1920 },
            { "1/辽阳/42251677/42F6589B", 1280 },
            { "1/达卡/41BDAEE6/42B4D09C", 384 },
            { "1/达喀尔/416B1965/C18B8D6F", 64 },
            { "1/达尔文/C14760CA/4302D783", 64 },
            { "1/达尔马瓦拉姆/4166A398/429B709D", 128 },
            { "1/达拉斯/420321DA/C2C19D04", 384 },
            { "1/达曼胡尔/41F845CC/41F3BEEF", 256 },
            { "1/达沃/40E25682/42FB39BE", 192 },
            { "1/达蓝/41D67FF6/42AE912D", 256 },
            { "1/达迈尔/418CBE03/4207D631", 64 },
            { "1/达霍克/421377E6/422BF42C", 512 },
            { "1/迈杜古里/413D8CFC/41528390", 320 },
            { "1/迈索尔/4144C43D/4299474C", 192 },
            { "1/迈莱维/41DDD9E8/41F6BBB3", 256 },
            { "1/迈门辛/41C60D06/42B4D01C", 384 },
            { "1/迈阿密/41CE31B4/C2A06327", 128 },
            { "1/连科兰/421B0462/42436709", 384 },
            { "1/迪亚巴克尔/4217A78F/4220DE6C", 256 },
            { "1/迪布鲁加尔/41DBD6D1/42BDD116", 128 },
            { "1/迪拜/41C8868E/425CAF64", 192 },
            { "1/迪瓦尼耶/41FFF170/4233B3BC", 256 },
            { "1/迪石市/412032FF/42D2296D", 384 },
            { "1/迪纳杰布尔/41CD0505/42B1468C", 256 },
            { "1/迪马布尔/41CF4B3E/42BB7183", 128 },
            { "1/通哈/40B12104/C292BC4E", 128 },
            { "1/通州/421F9DA5/42E952DB", 512 },
            { "1/通贝斯/C06448BF/C2A0E72F", 64 },
            { "1/通辽/422E7333/42F487D3", 448 },
            { "1/遂宁/41F4106D/42D3258A", 128 },
            { "1/遵义/41DD7E4D/42D5D07F", 128 },
            { "1/邢台/42144093/42E4FD04", 896 },
            { "1/那不勒斯/42236CDD/4163F14C", 448 },
            { "1/那格浦尔/41A92BA5/429E2B79", 192 },
            { "1/那牙/4159E81F/42F65CDF", 1408 },
            { "1/那空是贪玛叻/4106EEEB/42C7EEEF", 384 },
            { "1/那霸/41D1BBBD/42FF5DDD", 192 },
            { "1/邦冈/41B85D3F/42B1A964", 256 },
            { "1/邦唐/3E0793DE/42EAF886", 128 },
            { "1/邦美蜀市/414AADF5/42D81354", 1408 },
            { "1/邯郸/421266F9/42E4EF81", 960 },
            { "1/邵武/41DABA25/42EAF759", 1408 },
            { "1/郑州/420B07F7/42E34C17", 384 },
            { "1/郡山/4215999A/430C6222", 256 },
            { "1/郴州/41CE6666/42E21111", 384 },
            { "1/都城/41FDDDDC/43031111", 768 },
            { "1/都拉斯/42254AD0/419B87F7", 768 },
            { "1/都柏林/4255550E/C0C7F6E8", 64 },
            { "1/都灵/4234482F/40F5FA6E", 128 },
            { "1/鄂木斯克/425BF842/4292BCB8", 64 },
            { "1/酒田/421BAAAC/430BDAE1", 448 },
            { "1/里加/4263C8B4/41C0D8DD", 128 },
            { "1/里士满/4216370D/C29AEBA7", 128 },
            { "1/里夫内/424A7E09/41D1D1C7", 896 },
            { "1/里奥班巴/BFD5E2AC/C29D4B53", 128 },
            { "1/里斯本/421ADDDF/C112221F", 192 },
            { "1/里昂/4236FE6C/409B1840", 192 },
            { "1/里约布兰科/C11F9874/C2879EB8", 64 },
            { "1/里贾纳/4249CCE2/C2D13C50", 64 },
            { "1/里雄莱锡安/41FFC4A6/420B2856", 1024 },
            { "1/重庆/41EC8093/42D51B06", 192 },
            { "1/金兰/413EBE38/42DA5179", 1280 },
            { "1/金华/41E8DAAF/42EF49D6", 256 },
            { "1/金奈/415167CB/42A08E94", 64 },
            { "1/金斯顿/4230EB46/C298F643", 128 },
            { "1/金昌/421A00A3/42CC6338", 128 },
            { "1/金沙萨/C08A7B89/41750462", 64 },
            { "1/金泽/42126666/43089DDE", 384 },
            { "1/金海/420CEFCA/4300E190", 1024 },
            { "1/金瓯市/4112D441/42D24E0A", 384 },
            { "1/金边/4138FFCC/42D1D4FF", 448 },
            { "1/金迪亚/4120E925/C14DDA27", 256 },
            { "1/釜山/420C693F/43010A50", 448 },
            { "1/钏路/422BE666/43105FEE", 192 },
            { "1/钦博特/C1115D4F/C29D281B", 64 },
            { "1/钦州/41AFC89F/42D94093", 128 },
            { "1/钱德拉布尔/419F93AE/429E9796", 128 },
            { "1/铁力/423BEBF6/43000B83", 128 },
            { "1/铁岭/42292C18/42F7AECB", 640 },
            { "1/铃鹿/420B8888/43089555", 512 },
            { "1/银川/4219DF4B/42D48BCF", 192 },
            { "1/锡卡/BF8441DD/421446FE", 384 },
            { "1/锡卡索/413514AF/C0B5544C", 256 },
            { "1/锡尔赫特/41C7313C/42B7BE74", 128 },
            { "1/锡拉杰甘杰/41C3A964/42B36A82", 256 },
            { "1/锡林浩特/422FDDDF/42E81111", 128 },
            { "1/锡瓦斯/421EFE4A/4214107F", 384 },
            { "1/锡诺普/C13DD3A4/C25E028F", 64 },
            { "1/锦州/42246E5E/42F24889", 896 },
            { "1/镇江/4200D7EC/42EEE900", 384 },
            { "1/长冈/4215CCCD/430AD99A", 384 },
            { "1/长崎/42030000/4301E222", 960 },
            { "1/长春/422F851F/42FAA543", 768 },
            { "1/长沙/41E19705/42E1F116", 512 },
            { "1/长野/4212999A/430A2EEF", 384 },
            { "1/门头沟/421FC0B5/42E82FA7", 512 },
            { "1/门格洛尔/414EACF9/4295B64A", 192 },
            { "1/门迪亚/414857DC/4299CA61", 128 },
            { "1/阜新/42280FEF/42F3515A", 1792 },
            { "1/阜阳/4203999A/42E7A223", 384 },
            { "1/阳春/41B15557/42DF9111", 512 },
            { "1/阳朔/41C63F19/42DCFAB6", 512 },
            { "1/阳江/41AED855/42DFECEA", 896 },
            { "1/阳泉/42176E14/42E3206D", 768 },
            { "1/阿什凯隆/41FD5AA5/420A4935", 1536 },
            { "1/阿什哈巴德/4217CCCD/42698888", 192 },
            { "1/阿什杜德/41FE5648/420A9940", 1536 },
            { "1/阿伦敦/42226F08/C296FAF9", 128 },
            { "1/阿克伦/42245365/C2A309BC", 128 },
            { "1/阿克套/422E979D/424CB05A", 64 },
            { "1/阿克托比/42491E67/4264D427", 64 },
            { "1/阿克拉/40B1CAEA/BE49A027", 64 },
            { "1/阿利坎特/42196174/BEF685DB", 384 },
            { "1/阿加尔塔拉/41BEB03B/42B68F0C", 384 },
            { "1/阿加德兹/4187C961/40FFB72C", 64 },
            { "1/阿加迪尔/41F35C87/C1199206", 128 },
            { "1/阿勒泰/423F6502/42B04469", 64 },
            { "1/阿勒皮/4117D734/4298A71E", 128 },
            { "1/阿勒颇/4210CE12/4214A50A", 832 },
            { "1/阿卡普尔科/4186E829/C2C7C7BB", 192 },
            { "1/阿卡里瓜/4118DF46/C28A642B", 128 },
            { "1/阿多尼/417A005E/429A8CFC", 128 },
            { "1/阿姆劳蒂/41A77776/429B8000", 128 },
            { "1/阿尔乔姆/422D7021/4304305A", 256 },
            { "1/阿尔切夫斯克/4241E02F/421B444A", 512 },
            { "1/阿尔及尔/4213028F/4042AFA3", 448 },
            { "1/阿尔梅里亚/42135A41/C01D6C61", 128 },
            { "1/阿尔瓦塞特/421BFA1A/BFED9F80", 128 },
            { "1/阿尔瓦罗·奥夫雷贡区/419ADE8E/C2C66816", 128 },
            { "1/阿尔达比勒/4218FFCC/42412C57", 384 },
            { "1/阿尔阿尔/41F7CD6F/422426FE", 768 },
            { "1/阿尔马维尔/4233F4F1/42247E5D", 128 },
            { "1/阿尤恩/41D92268/C15301ED", 192 },
            { "1/阿巴/40A3691A/40EBBBC3", 256 },
            { "1/阿巴丹/41F2B6AE/4241379A", 256 },
            { "1/阿巴坎/4256DCBC/42B6DBBC", 64 },
            { "1/阿巴斯港/41D97DF4/42611F8A", 192 },
            { "1/阿布奎基/420C5685/C2D54D62", 384 },
            { "1/阿布扎比/41C3BBBD/42597778", 128 },
            { "1/阿布贾/4110ECF4/40EFD7B2", 64 },
            { "1/阿希尔/42104165/409413FD", 512 },
            { "1/阿德亚曼/42170EC1/42191AEC", 256 },
            { "1/阿拉克/42085DF1/4246C1B3", 384 },
            { "1/阿拉德/4238BBBB/41AA888A", 256 },
            { "1/阿拉拉夸拉/C1AE5B03/C240B3C6", 64 },
            { "1/阿拉木图/422D06B2/4299DB62", 64 },
            { "1/阿拉瓜伊纳/C0E61D93/C240D431", 64 },
            { "1/阿散索尔/41BD7776/42ADF777", 256 },
            { "1/阿斯塔纳/424CB86C/428EE457", 64 },
            { "1/阿斯旺/41C0BA00/42039902", 320 },
            { "1/阿斯特拉罕/42396613/424029BD", 64 },
            { "1/阿斯马拉/417568A7/421BBA34", 64 },
            { "1/阿杰隆/42015542/420F02DB", 1024 },
            { "1/阿格拉/41D97776/429C0889", 64 },
            { "1/阿比林/4201CB82/C2C7775E", 384 },
            { "1/阿比让/40A9E8BC/C08067B6", 64 },
            { "1/阿灵顿/4202F159/C2C23755", 256 },
            { "1/阿特劳/423C7778/424F8888", 64 },
            { "1/阿瓜斯卡连特斯/41AF0F08/C2CC90B0", 128 },
            { "1/阿瓦士/41FA8D55/4242BC9F", 448 },
            { "1/阿科拉/41A5AD33/4299FF07", 192 },
            { "1/阿莫勒/4211E0E1/42516723", 384 },
            { "1/阿菲永卡拉希萨尔/421B06D5/41F458BD", 384 },
            { "1/阿西斯/C1B54B1A/C249A61D", 64 },
            { "1/阿达帕扎勒/42231F4B/41F33A05", 896 },
            { "1/阿达纳/421401B6/420D50C9", 832 },
            { "1/阿里什/41F90D7F/4207319A", 1792 },
            { "1/阿里卡/C193CBFB/C28C9889", 64 },
            { "1/阿里格尔/41DF0D36/429C2637", 64 },
            { "1/阿雷基帕/C18330ED/C28F11EC", 64 },
            { "1/阿雷格里港/C1F043B5/C24CEB85", 64 },
            { "1/阿马拉/41FEAFE2/423C9372", 384 },
            { "1/阿马里洛/420CE354/C2CBA9A0", 384 },
            { "1/阿鲁沙/C0577785/4212BBBB", 64 },
            { "1/陶格夫匹尔斯/425F8888/41D44443", 384 },
            { "1/陶里亚蒂/42561F07/42456268", 256 },
            { "1/雅典/4207D803/C2A6C181", 128 },
            { "1/雅典/4217EAF5/41BDBAD2", 960 },
            { "1/雅加达/C0C6DE2B/42D5B0B5", 128 },
            { "1/雅温得/40777785/41384448", 64 },
            { "1/雅罗斯拉夫尔/426684FD/421F7EA6", 64 },
            { "1/雅西/423CAAAC/41DCCCCD", 384 },
            { "1/集宁/42241C29/42E2362F", 384 },
            { "1/雷乌斯/42249FDE/3F8DADEB", 256 },
            { "1/霍伊/421A3382/4233CEF3", 256 },
            { "1/霍奇米尔科/419A1014/C2C635D4", 128 },
            { "1/霍姆斯/420AE844/4212E4C0", 768 },
            { "1/霍尔迪亚/41B07BD2/42B03831", 256 },
            { "1/霍拉姆沙赫尔/41F386BD/4240BCB4", 256 },
            { "1/霍拉马巴德/4205F37D/42416C5F", 384 },
            { "1/霍斯佩特/41745009/4298C632", 128 },
            { "1/霍苏尔/414BC895/429BAA50", 128 },
            { "1/霍隆/42000A97/420B1DE1", 1024 },
            { "1/青岛/421043A3/42F0BD21", 960 },
            { "1/青森市/42234445/430CBBBC", 384 },
            { "1/静冈/420BEEEE/430A6222", 384 },
            { "1/非斯/4208260B/C09FFE5D", 192 },
            { "1/鞍山/42247E94/42F5FAE1", 1920 },
            { "1/韦利亚镇/C1A2A344/C2212B85", 64 },
            { "1/韦尔瓦/421510C6/C0DE14CF", 128 },
            { "1/韦恩堡/422485BC/C2AA41FA", 128 },
            { "1/韦拉克鲁斯/41997296/C2C0492A", 128 },
            { "1/韦柳港/C10C30E8/C27F9D95", 64 },
            { "1/韦科/41FC6507/C2C24B18", 384 },
            { "1/韶关/41C66666/42E32AAA", 448 },
            { "1/顺义/42207CAC/42E94BB0", 512 },
            { "1/顺化/4183B1F9/42D730E0", 384 },
            { "1/顺天/420BCAD5/42FEFA9C", 1024 },
            { "1/顿河畔罗斯托夫/423CECE7/421EE4A4", 448 },
            { "1/顿涅茨克/4240178D/4217357E", 704 },
            { "1/首尔/42164396/42FDF4F1", 1984 },
            { "1/香港/41B248BF/42E450BD", 384 },
            { "1/马丁内斯德拉托雷/41A0910A/C2C21F1F", 128 },
            { "1/马什哈德/4211432A/426E4597", 192 },
            { "1/马克耶夫卡/424030F8/4217B3FA", 512 },
            { "1/马六甲/400C8B44/42CC7B23", 128 },
            { "1/马卡帕/3D1F4B1F/C24C43FC", 64 },
            { "1/马哈奇卡拉/422BE7D0/423E026B", 256 },
            { "1/马哈巴德/42130D6A/4236E388", 768 },
            { "1/马图林/411BEE59/C27CBBA1", 128 },
            { "1/马坦萨斯/41B85431/C2A327AE", 384 },
            { "1/马塔加尔帕/414ECF37/C2ABD5BF", 128 },
            { "1/马塔普拉/C05A4C30/42E5BA78", 128 },
            { "1/马塔罗/42262B1F/401C72B0", 256 },
            { "1/马塔莫罗斯/41CF09AB/C2C30223", 192 },
            { "1/马塞约/C11AA73D/C20EF0ED", 64 },
            { "1/马尔卡/3FDBA3EC/4233162E", 256 },
            { "1/马尔泰佩/4223BE20/41E93D95", 512 },
            { "1/马尼拉/4169AACE/42F1F6E3", 448 },
            { "1/马尼萨/421A72B5/41DB6969", 896 },
            { "1/马尼萨莱斯/40A23459/C29708E6", 192 },
            { "1/马库尔迪/40F77AE1/4108579D", 320 },
            { "1/马德望/4151A4B3/42CE657D", 384 },
            { "1/马德里/4221AA7F/C06CF6BE", 192 },
            { "1/马扎里沙里夫/4212D60F/428638C4", 192 },
            { "1/马拉凯/4123F377/C2873111", 256 },
            { "1/马拉加/4212E172/C08D736D", 128 },
            { "1/马拉博/40700000/410C8885", 320 },
            { "1/马拉卡尔/411888E3/41FD48AF", 64 },
            { "1/马拉喀什/41FD12C2/C0FFFF82", 128 },
            { "1/马拉巴/C0ABCBA7/C244789B", 64 },
            { "1/马拉开波/412A1B52/C28F47F7", 448 },
            { "1/马拉维市/41000DED/42F89162", 256 },
            { "1/马拉耶尔/42093007/42434B44", 256 },
            { "1/马拉蒂亚/42196696/42194445", 256 },
            { "1/马拉迪/41580000/40E34174", 64 },
            { "1/马斯喀特/41BCE935/426A5E6A", 192 },
            { "1/马杜赖/411EAD77/429C3D3F", 192 },
            { "1/马林加/C1BB66F9/C24FC123", 64 },
            { "1/马格尼托哥尔斯克/4255ACA8/426C305A", 64 },
            { "1/马格德堡/425082CC/413A110A", 128 },
            { "1/马洛洛斯/416D8241/42F19EEB", 1024 },
            { "1/马瑙斯/C046862F/C270199A", 192 },
            { "1/马纳瓜/41422008/C2AC8034", 128 },
            { "1/马莱冈/41A46DFA/42950EC0", 128 },
            { "1/马萨特兰/41B9DCFB/C2D4CFF9", 192 },
            { "1/马赛/422D3014/40AC31BA", 192 },
            { "1/马辰/C05478C0/42E52E77", 128 },
            { "1/马达纳帕尔莱/4158CE07/429D0179", 128 },
            { "1/马里乌波尔/423C616C/42162A4D", 896 },
            { "1/马里利亚/C1B1B60C/C247C888", 64 },
            { "1/马雷/42166008/4277523D", 192 },
            { "1/马鲁阿/41297488/41650E0D", 64 },
            { "1/高冈/42130000/43090444", 256 },
            { "1/高州/41AF8390/42DDB130", 512 },
            { "1/高松/42095554/43060CCD", 768 },
            { "1/高槻/420B64B1/43079DE5", 512 },
            { "1/高知/42063333/43058888", 448 },
            { "1/高阳/4216A025/42FDAB85", 1536 },
            { "1/高雄/41B4EE1A/42F0A06D", 1984 },
            { "1/魁北克市/423B3FC6/C28E6DD8", 192 },
            { "1/鲁塞/422F6CF2/41CFC443", 384 },
            { "1/鲁德内/4253E440/427C7793", 64 },
            { "1/鲁赛法/42001235/42102F81", 1024 },
            { "1/鲍里索夫/4258E95F/41E40A3D", 384 },
            { "1/鸟取/420E0000/43063BBC", 896 },
            { "1/鸡西/42353451/4302F50A", 192 },
            { "1/鹤岗/423D679C/43024CD5", 128 },
            { "1/鹿儿岛/41FC888A/43028CCD", 640 },
            { "1/麦地那/41C3BFB7/421E74E9", 64 },
            { "1/麦德林/40C80F13/C297208F", 192 },
            { "1/麦纳麦/41D1B90F/424A5532", 128 },
            { "1/麦迪亚/42110E83/40304064", 896 },
            { "1/麦迪逊/422C4ACE/C2B2CD6E", 128 },
            { "1/麻城/41F970E8/42E60B55", 384 },
            { "1/黄冈/41BD6A94/42E9FFCD", 1024 },
            { "1/黄石/41F1F9FB/42E618A6", 384 },
            { "1/黎牙实比市/4152320E/42F77809", 384 },
            { "1/黑角/C098D5BB/413DD0FA", 64 },
            { "1/默克莱/4157F25C/421DE6B0", 64 },
            { "1/默吉利伯德讷姆/41817FF0/42A2471B", 128 },
            { "1/默弗里斯伯勒/420F61EA/C2ACC7D1", 128 },
            { "1/齐齐哈尔/423D5D10/42F7EBC0", 448 },
            { "1/龙凤/423A2071/42FA3525", 256 },
            { "1/龙多诺波利斯/C183C443/C25A8AD0", 64 },
            { "1/龙川市/41262EA7/42D2DED0", 256 },
            { "1/龙江/423D52A8/42F65B38", 384 },
            { "1/龟尾/42107454/43005604", 1536 },
        };
        // END GENERATED PACKED WWP COVERAGE

        internal static bool Contains(string fingerprint, MapLabelPlace place, int z)
        {
            if (!place.IsLegacy || fingerprint != Fingerprint) return false;
            string key = place.Level + "/" + place.Name + "/" + BitConverter.ToUInt32(BitConverter.GetBytes(place.Lat), 0).ToString("X8")
                + "/" + BitConverter.ToUInt32(BitConverter.GetBytes(place.Lon), 0).ToString("X8");
            int mask;
            return ZoomMasks.TryGetValue(key, out mask) && (mask & (1 << z)) != 0;
        }
    }

    // Both cached and runtime labels use the same pixels, ordering and collision geometry.
    public static class MapLabelRenderer
    {
        private const int TileSize = 256;
        private const float EdgeMargin = 2f;

        public static byte[] RenderTileBytes(IList<MapLabelPlace> places, MapLabelTilePack pack,
            int z, int x, int y, out int candidates, out int rendered)
        {
            byte[] png;
            if (pack != null && pack.TryGetTile(z, x, y, out png, out rendered))
            {
                return RenderPackedTile(png, rendered, pack.PlacesForTile(places, z, x, y), pack, z, x, y, out candidates, out rendered);
            }
            if (pack != null && places != null && z >= 3 && z <= 10 && pack.Fingerprint == PackedWwpCoverage.Fingerprint)
            {
                IList<MapLabelPlace> local = pack.PlacesForTile(places, z, x, y);
                return RenderPackedTile(null, 0, local, pack, z, x, y, out candidates, out rendered);
            }
            return RenderFallback(places, pack, z, x, y, out candidates, out rendered);
        }

        private static byte[] RenderFallback(IList<MapLabelPlace> places, MapLabelTilePack pack, int z, int x, int y,
            out int candidates, out int rendered)
        {
            using (Bitmap bitmap = RenderTile(places, z, x, y, out candidates, out rendered, 14f, null, pack))
            using (var stream = new MemoryStream())
            {
                // Store the same density on the installer, HTTP worker and cache refresh thread.
                bitmap.SetResolution(96f, 96f);
                bitmap.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        }

        // Compose the historical WWP and R88 layers around the certified image. Passing no PNG
        // reconciles the same release layers on a fallback host that the package does not supply.
        internal static byte[] RenderPackedTile(byte[] png, int count, IList<MapLabelPlace> places,
            MapLabelTilePack pack, int z, int x, int y, out int candidates, out int rendered)
        {
            candidates = rendered = count;
            bool restoreWwp = pack.Fingerprint == PackedWwpCoverage.Fingerprint;
            bool fallback = png == null;
            if (!fallback && (places == null || places.Count == 0 || (z > 8 && !restoreWwp))) return png;
            double n = 1 << z;
            double west = x / n * 360 - 180, east = (x + 1) / n * 360 - 180;
            double south = TileLatitude(y + 1, n), north = TileLatitude(y, n);
            var local = new List<MapLabelPlace>();
            var originalPlaces = new List<MapLabelPlace>();
            foreach (MapLabelPlace place in places)
                if (VisibleAtZoom(place, z) && TileContains(place, west, east, south, north))
                {
                    originalPlaces.Add(place);
                    if ((place.Rank == 1 || (restoreWwp && place.IsLegacy && MapLabelCatalog.ChineseName(place.DisplayName)))
                        && !pack.AlreadyInPack(place, z)) local.Add(place);
                }
            // Preserve the standard fallback when historical WWP and upstream R88 cannot disagree.
            // Supplemental catalogs retain the priority/range semantics of their own independent inputs.
            if (fallback && (!originalPlaces.Any(p => p.IsLegacy) || originalPlaces.Any(p => !p.IsLegacy && p.Rank != 1)
                || !originalPlaces.Any(p => p.Rank == 1 || pack.AlreadyInPack(p, z))))
                return RenderFallback(originalPlaces, pack, z, x, y, out candidates, out rendered);
            if (local.Count == 0)
                return fallback ? RenderFallback(local, null, z, x, y, out candidates, out rendered) : png;
            using (var input = fallback ? null : new MemoryStream(png, false))
            using (var background = fallback ? new Bitmap(TileSize, TileSize) : new Bitmap(input))
            {
                int eligible, added;
                var preferred = new HashSet<MapLabelPlace>();
                using (RenderTile(originalPlaces, z, x, y, out eligible, out added, 14f, null, null, null, preferred)) { }
                // R88 introduced a province layer between countries and cities. Preserve any WWP
                // shown by either release; a point hidden in both does not create a new city label.
                if (restoreWwp && originalPlaces.Any(p => p.Rank == 1))
                    using (RenderTile(originalPlaces.Where(p => p.IsLegacy).ToList(), z, x, y,
                        out eligible, out added, 14f, null, null, null, preferred)) { }
                local.RemoveAll(p => (p.IsLegacy || fallback) && !preferred.Contains(p));
                if (local.Count == 0)
                    return fallback ? RenderFallback(local, null, z, x, y, out candidates, out rendered) : png;
                using (Bitmap overlay = RenderTile(local, z, x, y, out eligible, out added, 14f, new PackedInk(background), null, preferred))
                {
                    candidates += eligible;
                    rendered += added;
                    if (added == 0 && !fallback) return png;
                    // The drawing is disjoint. Copy original coverage exactly, including antialiased edges.
                    var area = new Rectangle(0, 0, TileSize, TileSize);
                    BitmapData original = background.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        BitmapData output = overlay.LockBits(area, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                        try
                        {
                            int[] source = new int[TileSize], row = new int[TileSize];
                            for (int line = 0; line < TileSize; line++)
                            {
                                IntPtr rowAddress = IntPtr.Add(output.Scan0, line * output.Stride);
                                System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(original.Scan0, line * original.Stride), source, 0, TileSize);
                                System.Runtime.InteropServices.Marshal.Copy(rowAddress, row, 0, TileSize);
                                for (int col = 0; col < TileSize; col++)
                                    if ((uint)source[col] >> 24 != 0 || (uint)row[col] >> 24 == 0)
                                        row[col] = source[col];
                                System.Runtime.InteropServices.Marshal.Copy(row, 0, rowAddress, TileSize);
                            }
                        }
                        finally { overlay.UnlockBits(output); }
                    }
                    finally { background.UnlockBits(original); }
                    using (var stream = new MemoryStream())
                    {
                        // A warm-cache worker and the live server can have different display DPI.
                        overlay.SetResolution(96f, 96f);
                        overlay.Save(stream, ImageFormat.Png);
                        return stream.ToArray();
                    }
                }
            }
        }

        // A bit per pixel keeps checking nearby label rectangles cheap without allocating a
        // 256x256 integral image on the large object heap for every packed tile.
        private sealed class PackedInk
        {
            private const int WordsPerRow = TileSize / 32;
            private readonly uint[] rows = new uint[TileSize * WordsPerRow];

            internal PackedInk(Bitmap bitmap)
            {
                var area = new Rectangle(0, 0, TileSize, TileSize);
                BitmapData bits = bitmap.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var pixels = new int[TileSize];
                    for (int y = 0; y < TileSize; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), pixels, 0, TileSize);
                        for (int x = 0; x < TileSize; x++)
                            if ((uint)pixels[x] >> 24 != 0) rows[y * WordsPerRow + x / 32] |= 1u << (x % 32);
                    }
                }
                finally { bitmap.UnlockBits(bits); }
            }

            internal bool Intersects(RectangleF bounds)
            {
                int left = Math.Max(0, (int)Math.Floor(bounds.Left));
                int right = Math.Min(TileSize, (int)Math.Ceiling(bounds.Right)) - 1;
                int bottom = Math.Min(TileSize, (int)Math.Ceiling(bounds.Bottom));
                for (int word = left / 32; word <= right / 32; word++)
                {
                    uint mask = uint.MaxValue;
                    if (word == left / 32) mask &= uint.MaxValue << (left % 32);
                    if (word == right / 32) mask &= uint.MaxValue >> (31 - right % 32);
                    for (int y = Math.Max(0, (int)Math.Floor(bounds.Top)); y < bottom; y++)
                        if ((rows[y * WordsPerRow + word] & mask) != 0) return true;
                }
                return false;
            }

            internal bool BlocksPoint(int x, int y)
            {
                return x < 0 || y < 0 || x >= TileSize || y >= TileSize
                    || (rows[y * WordsPerRow + x / 32] & (1u << (x % 32))) != 0;
            }
        }

        private const int MaxRegionShift = 96;
        private static readonly Point[] NearbyOffsets = (from x in Enumerable.Range(-MaxRegionShift, 2 * MaxRegionShift + 1)
            from y in Enumerable.Range(-MaxRegionShift, 2 * MaxRegionShift + 1)
            let distance = x * x + y * y
            where distance <= MaxRegionShift * MaxRegionShift
            orderby distance, Math.Abs(x), y, x
            select new Point(x, y)).ToArray();

        private static bool FindRegionOffset(RectangleF original, PackedInk ink, List<RectangleF> occupied, int limit, out Point shift)
        {
            foreach (Point offset in NearbyOffsets)
            {
                if (offset.X * offset.X + offset.Y * offset.Y > limit * limit) break;
                RectangleF trial = original;
                trial.Offset(offset.X, offset.Y);
                if (trial.Left < 1 || trial.Top < 1 || trial.Right > TileSize - 1 || trial.Bottom > TileSize - 1) continue;
                if (ink.Intersects(trial) || occupied.Any(trial.IntersectsWith)) continue;
                shift = offset;
                return true;
            }
            shift = Point.Empty;
            return false;
        }

        private static readonly string[] AdministrativeSuffixes = { "维吾尔自治区", "哥伦比亚特区", "自治共和国", "特别行政区",
            "回族自治区", "壮族自治区", "自治区", "共和国", "边疆区", "自治州", "自治市", "直辖市" };

        private static string CompactRegionName(string name)
        {
            if (name.Length >= 6)
                foreach (string suffix in AdministrativeSuffixes)
                    if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length - suffix.Length >= 2)
                        return name.Substring(0, name.Length - suffix.Length) + "\n" + suffix;
            return name;
        }

        private static string NarrowLabelName(string name)
        {
            string compact = CompactRegionName(name);
            int split = compact.IndexOf('\n');
            string stem = split < 0 ? name : compact.Substring(0, split);
            if (split < 0)
                foreach (string suffix in new[] { "讷格尔", "普尔", "布尔" })
                    if (stem.EndsWith(suffix, StringComparison.Ordinal) && stem.Length - suffix.Length >= 2)
                        return stem.Substring(0, stem.Length - suffix.Length) + "\n" + suffix;
            if (stem.Length < 4 || stem.Any(c => c < '\u3400' || c > '\u9fff')) return compact;
            int midpoint = (stem.Length + 1) / 2;
            return stem.Substring(0, midpoint) + "\n" + stem.Substring(midpoint) + (split < 0 ? "" : compact.Substring(split));
        }

        private sealed class Leader
        {
            internal PointF Anchor, Edge;
            internal RectangleF Bounds;
        }

        private static float Distance(PointF a, PointF b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        private static bool InsideLabel(Rectangle[] labels, int x, int y)
        {
            foreach (Rectangle box in labels)
                if (box.Contains(x, y)) return true;
            return false;
        }

        private static readonly int[] LeaderBends = { 0, -4, 4, -8, 8, -12, 12 };

        // Sample the same protected pixels used by the final composite and text clip. Favour a
        // continuous visible trail near both endpoints when the shortest leader is itself hidden.
        private static double LeaderScore(PointF[] path, PackedInk ink, Rectangle[] protectedText, out int visible)
        {
            int steps = 0, first = -1, last = -1, run = 0, longest = 0;
            visible = 0;
            for (int part = 1; part < path.Length; part++)
            {
                PointF a = path[part - 1], b = path[part];
                int count = Math.Max(1, (int)Math.Ceiling(Distance(a, b)));
                for (int i = 0; i < count; i++, steps++)
                {
                    float t = (i + 0.5f) / count;
                    int x = (int)Math.Floor(a.X + t * (b.X - a.X)), y = (int)Math.Floor(a.Y + t * (b.Y - a.Y));
                    if (ink.BlocksPoint(x, y) || InsideLabel(protectedText, x, y))
                    {
                        run = 0;
                        continue;
                    }
                    if (first < 0) first = steps;
                    last = steps;
                    visible++;
                    longest = Math.Max(longest, ++run);
                }
            }
            if (longest < 3) return double.PositiveInfinity;
            return steps + 3 * first + 4 * (steps - 1 - last) + (steps - visible);
        }

        private static PointF[] RouteLeader(Leader leader, PackedInk ink, Rectangle[] protectedText)
        {
            PointF[] best = { leader.Anchor, leader.Edge };
            int visible;
            LeaderScore(best, ink, protectedText, out visible);
            if (visible >= 3) return best;

            // Keep the geographic endpoint fixed. Try nearby edges of the same label and short
            // bends around intervening text; all routes remain inside the original tile.
            RectangleF box = leader.Bounds;
            var ends = new List<PointF> { leader.Edge };
            for (float x = box.Left; x <= box.Right; x += 4)
            {
                ends.Add(new PointF(x, box.Top));
                ends.Add(new PointF(x, box.Bottom));
            }
            for (float y = box.Top; y <= box.Bottom; y += 4)
            {
                ends.Add(new PointF(box.Left, y));
                ends.Add(new PointF(box.Right, y));
            }
            float maximum = Distance(leader.Anchor, leader.Edge) + 24;
            double bestScore = double.PositiveInfinity;
            foreach (PointF end in ends)
            {
                float length = Distance(leader.Anchor, end);
                if (length < 1 || length > maximum) continue;
                float nx = (end.Y - leader.Anchor.Y) / length, ny = (leader.Anchor.X - end.X) / length;
                foreach (int bend in LeaderBends)
                {
                    var mid = new PointF((leader.Anchor.X + end.X) / 2 + bend * nx, (leader.Anchor.Y + end.Y) / 2 + bend * ny);
                    if (mid.X < 0 || mid.Y < 0 || mid.X >= TileSize || mid.Y >= TileSize
                        || Distance(leader.Anchor, mid) + Distance(mid, end) > maximum) continue;
                    PointF[] trial = bend == 0 ? new[] { leader.Anchor, end } : new[] { leader.Anchor, mid, end };
                    double score = LeaderScore(trial, ink, protectedText, out visible);
                    if (score < bestScore) { best = trial; bestScore = score; }
                }
            }
            return best;
        }

        private static void DrawLeaders(Graphics graphics, List<Leader> leaders, List<RectangleF> occupied, PackedInk ink)
        {
            if (leaders.Count == 0) return;
            // Glyphs and their halos stay intact. The final merge also restores every covered pack pixel.
            Rectangle[] protectedText = occupied.Select(Rectangle.Ceiling).ToArray();
            foreach (Rectangle bounds in protectedText) graphics.ExcludeClip(bounds);
            using (var outline = new Pen(Color.FromArgb(190, 30, 30, 30), 2f))
            using (var line = new Pen(Color.FromArgb(215, 235, 235, 235), 0.85f))
                foreach (Leader leader in leaders)
                {
                    PointF[] route = RouteLeader(leader, ink, protectedText);
                    graphics.DrawLines(outline, route);
                    graphics.DrawLines(line, route);
                }
        }

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
        internal static bool VisibleAtZoom(MapLabelPlace place, int z)
        {
            if (z < place.MinZoom || z > place.MaxZoom) return false;
            if (place.Rank == 1) return z <= 8;
            return !place.IsLegacy || place.Level == 0 || z > 5;
        }

        public static Bitmap RenderTile(IList<MapLabelPlace> places, int z, int x, int y,
            out int candidates, out int rendered, float cityEmPixels)
        {
            return RenderTile(places, z, x, y, out candidates, out rendered, cityEmPixels, null, null);
        }

        private static Bitmap RenderTile(IList<MapLabelPlace> places, int z, int x, int y,
            out int candidates, out int rendered, float cityEmPixels, PackedInk background, MapLabelTilePack pack,
            HashSet<MapLabelPlace> preferred = null, HashSet<MapLabelPlace> measured = null)
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
                if (TileContains(place, minLon, maxLon, minLat, maxLat) && VisibleAtZoom(place, z)
                    && !string.IsNullOrEmpty(place.Name) && (pack == null || !pack.AlreadyInPack(place, z)))
                    selected.Add(place);
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
                        var leaders = new List<Leader>();
                        // Stable ties preserve the source order within a catalog priority and label rank.
                        var ranked = selected.OrderBy(p => p.Priority).ThenBy(RankOf).ThenBy(p => p.Level).ToList();
                        List<MapLabelPlace>[] groups = preferred == null ? new[] { ranked }
                            : new[] { ranked.Where(preferred.Contains).ToList(), ranked.Where(p => !preferred.Contains(p)).ToList() };
                        foreach (List<MapLabelPlace> ordered in groups)
                        {
                            int anchorCount = ordered.Count;
                            int narrowStart = int.MaxValue;
                            for (int index = 0; index < ordered.Count; index++)
                            {
                                if (index == anchorCount) narrowStart = ordered.Count;
                                MapLabelPlace place = ordered[index];
                                int rank = RankOf(place);
                                float emPixels = rank == 0 ? cityEmPixels + (z <= 3 ? 0 : z == 4 ? 1 : 2)
                                    : rank == 1 ? cityEmPixels + (z >= 7 ? 1 : 0)
                                    : cityEmPixels;
                                bool retry = index >= anchorCount;
                                bool narrow = index >= narrowStart;
                                if (retry) emPixels = 12f;
                                if (narrow) emPixels = 11f;
                                FontStyle style = rank <= 1 ? FontStyle.Bold : FontStyle.Regular;
                                using (var path = new GraphicsPath())
                                {
                                    string display = measured == null ? place.DisplayName : place.Name;
                                    string caption = narrow ? NarrowLabelName(display) : retry ? CompactRegionName(display) : display;
                                    path.AddString(caption, family, (int)style, emPixels, PointF.Empty, format);
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
                                    if (retry)
                                    {
                                        // All unobstructed original anchors have already claimed their positions.
                                        Point shift;
                                        int limit = preferred.Contains(place) ? MaxRegionShift : 8;
                                        if (!FindRegionOffset(bounds, background, occupied, limit, out shift))
                                        {
                                            if (!narrow && preferred.Contains(place)) ordered.Add(place);
                                            continue;
                                        }
                                        bounds.Offset(shift.X, shift.Y);
                                        dx += shift.X;
                                        dy += shift.Y;
                                        var edge = new PointF(Math.Max(bounds.Left, Math.Min(bounds.Right, rawX)),
                                            Math.Max(bounds.Top, Math.Min(bounds.Bottom, rawY)));
                                        float leadX = edge.X - rawX, leadY = edge.Y - rawY;
                                        if (leadX * leadX + leadY * leadY > 4)
                                            leaders.Add(new Leader { Anchor = new PointF(rawX, rawY), Edge = edge, Bounds = bounds });
                                    }
                                    else if ((background != null && background.Intersects(bounds)) || occupied.Any(bounds.IntersectsWith))
                                    {
                                        if (background != null) ordered.Add(place);
                                        continue;
                                    }
                                    occupied.Add(bounds);
                                    if (measured != null)
                                    {
                                        measured.Add(place);
                                        continue;
                                    }
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
                        DrawLeaders(graphics, leaders, occupied, background);
                    }
                }
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }

        internal static bool TileContains(MapLabelPlace place, double minLon, double maxLon, double minLat, double maxLat)
        {
            return place.Lon >= minLon && place.Lon <= maxLon && place.Lat >= minLat && place.Lat <= maxLat;
        }

        internal static double TileLatitude(int y, double n)
        {
            return Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * y / n))) * 180.0 / Math.PI;
        }
    }
}
