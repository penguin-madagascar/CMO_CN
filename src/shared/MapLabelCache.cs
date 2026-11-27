namespace CMOShared
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Text.RegularExpressions;

    public static class MapLabelCache
    {
        private const string Marker = ".r88-map-rendered.sha256";

        private sealed class Tile
        {
            public string Path, Key;
            public int Zoom, X, Y;
        }

        private static void Collect(string cache, string folder, List<Tile> tiles)
        {
            foreach (string path in Directory.GetFileSystemEntries(folder))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Unexpected link in map label cache: " + path);
                if ((attributes & FileAttributes.Directory) != 0) { Collect(cache, path, tiles); continue; }
                if (!String.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase)) continue;
                string key = path.Substring(cache.Length + 1).Replace('\\', '/');
                key = key.Substring(0, key.Length - 4) + ".png";
                string[] bits = key.Substring(0, key.Length - 4).Split('/');
                // Only files directly inside numeric zoom and column folders belong to the tile cache.
                if (bits.Length != 3 || !Regex.IsMatch(key, @"\A[0-9]+/[0-9]+/")) continue;
                int z, x, y;
                if (!Coordinate(bits[0], out z) || z > 30 ||
                    !Coordinate(bits[1], out x) || !Coordinate(bits[2], out y) || x >= (1L << z) || y >= (1L << z))
                    throw new InvalidDataException("Invalid map label tile coordinates: " + key);
                tiles.Add(new Tile { Path = path, Key = key, Zoom = z, X = x, Y = y });
            }
        }

        private static bool Coordinate(string text, out int result)
        {
            return Int32.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= 0;
        }

        private static Dictionary<string,string> ReadPrevious(string marker, string fingerprint)
        {
            string prior;
            var records = MapLabelOriginalCache.ReadCurrentReceipt(marker, out prior);
            return records != null && String.Equals(prior, fingerprint, StringComparison.OrdinalIgnoreCase)
                ? records : new Dictionary<string,string>(StringComparer.Ordinal);
        }

        private static void Publish(string path, byte[] bytes)
        {
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        // Explicit refresh returns the number checked. Startup returns the number rewritten.
        public static int Refresh(string gamePath, MapLabelIndex index, bool force)
        {
            if (index == null) throw new ArgumentNullException("index");
            string game = Path.GetFullPath(gamePath);
            string cache = Path.Combine(game, @"WW\Cache\Stamen Terrain - Labels");
            using (MapLabelOriginalCache.Lock(cache))
            {
                if (!Directory.Exists(cache)) return 0;
                MapLabelOriginalCache.Plain(cache);
                var tiles = new List<Tile>();
                Collect(cache, cache, tiles);
                if (tiles.Count == 0) return 0;
                if (force) MapLabelOriginalCache.EnsureOriginalBackup(gamePath);
                string marker = Path.Combine(cache, Marker);
                var previous = force ? new Dictionary<string,string>() : ReadPrevious(marker, index.Fingerprint);
                var current = new SortedDictionary<string,string>(StringComparer.Ordinal);
                int written = 0;
                foreach (Tile tile in tiles)
                {
                    string actual = MapLabelOriginalCache.FileSha(tile.Path), expected;
                    if (!previous.TryGetValue(tile.Key, out expected) || !String.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                    {
                        int candidates, rendered;
                        byte[] png = MapLabelRenderer.RenderTileBytes(index, tile.Zoom, tile.X, tile.Y,
                            out candidates, out rendered);
                        expected = MapLabelOriginalCache.Hash(png);
                        if (expected != actual)
                        {
                            if (!force && written == 0) MapLabelOriginalCache.EnsureOriginalBackup(gamePath);
                            Publish(tile.Path, png);
                            if (MapLabelOriginalCache.FileSha(tile.Path) != expected)
                                throw new IOException("Map label PNG changed while refreshing: " + tile.Key);
                            written++;
                        }
                    }
                    current.Add(tile.Key, expected);
                }
                var output = new StringBuilder().Append(index.Fingerprint).Append('\n');
                foreach (var row in current)
                    output.Append(row.Key).Append('\t').Append(row.Value).Append('\n');
                string body = output.ToString();
                byte[] receipt = Encoding.UTF8.GetBytes(MapLabelOriginalCache.Hash(Encoding.UTF8.GetBytes(body)) + "\n" + body);
                if (!File.Exists(marker) || MapLabelOriginalCache.FileSha(marker) != MapLabelOriginalCache.Hash(receipt)) Publish(marker, receipt);
                return force ? tiles.Count : written;
            }
        }
    }
}
