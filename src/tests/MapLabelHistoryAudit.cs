using System;
using System.Collections.Generic;
using System.Globalization;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using CMOShared;

internal static class MapLabelHistoryAudit
{
    private sealed class Owner
    {
        internal string Member, Source, Caption;
        internal double X, Y;
        internal int Label, Zoom;
        internal readonly Dictionary<string, int> Reflow = new Dictionary<string, int>();
    }

    private sealed class Host
    {
        internal readonly HashSet<string> Required = new HashSet<string>();
        internal readonly List<Owner> Owners = new List<Owner>();
    }

    private sealed class Contract
    {
        internal readonly Dictionary<string, Host> Hosts = new Dictionary<string, Host>();
        internal readonly Dictionary<string, Owner> Owners = new Dictionary<string, Owner>();

        internal Host Get(string key)
        {
            Host value;
            if (!Hosts.TryGetValue(key, out value)) Hosts.Add(key, value = new Host());
            return value;
        }

        internal static Contract Read(string path)
        {
            var contract = new Contract();
            using (var file = File.OpenRead(path))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip, Encoding.UTF8))
            {
                if (reader.ReadLine() != "map-label-history-v1") throw new InvalidDataException("Historical fixture format");
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var f = line.Split('\t');
                    string member = f[1] + ":" + f[2];
                    if (f.Length == 4 && f[0] == "D")
                    {
                        if (!contract.Get(f[3]).Required.Add(member))
                            throw new InvalidDataException("Repeated historical draw: " + line);
                    }
                    else if (f.Length == 10 && f[0] == "O")
                    {
                        var owner = new Owner { Member = member, Zoom = int.Parse(f[3]), Source = f[4] + "/" + f[5],
                            X = double.Parse(f[6], CultureInfo.InvariantCulture), Y = double.Parse(f[7], CultureInfo.InvariantCulture),
                            Caption = f[8] };
                        contract.Owners.Add(member + "/" + owner.Zoom, owner);
                        foreach (string host in f[9].Split(',')) contract.Get(host).Owners.Add(owner);
                    }
                    else if (f.Length == 5 && f[0] == "R")
                    {
                        var owner = contract.Owners[member + "/" + f[3]];
                        foreach (var host in contract.Hosts.Values) host.Owners.Remove(owner);
                        foreach (string assignment in f[4].Split(','))
                        {
                            string[] parts = assignment.Split('=');
                            owner.Reflow.Add(parts[0], int.Parse(parts[1]));
                            contract.Get(parts[0]).Owners.Add(owner);
                        }
                    }
                    else throw new InvalidDataException("Invalid historical row: " + line);
                }
            }
            if (contract.Owners.Count != 5129 || contract.Hosts.Values.Sum(h => h.Required.Count) != 23396 ||
                contract.Owners.Values.Count(o => o.Reflow.Count > 0) != 32)
                throw new InvalidDataException("Historical fixture has incomplete membership or ownership.");
            return contract;
        }
    }

    // Compare the actual bindings to the published donor identities independently of label order.
    private static Dictionary<string, int> ReadBindings(string currentDictionary)
    {
        var data = ReviewedMapReader.ReadSection(File.ReadAllText(currentDictionary, Encoding.UTF8));
        if (data == null) throw new InvalidDataException("Historical audit requires the reviewed_map dictionary section.");
        var result = new Dictionary<string, int>();
        var entities = ReviewedMapReader.AsArray(data["entities"]);
        var labels = ReviewedMapReader.AsArray(data["labels"]);
        for (int id = 0; id < labels.Count; ++id)
        {
            var row = ReviewedMapReader.RequireFields(labels[id], 7);
            var entity = ReviewedMapReader.AsArray(entities[ReviewedMapReader.AsInteger(row[0])]);
            if (entity.Count == 2)
                result.Add(ReviewedMapReader.AsText(entity[0]) + ":" + ReviewedMapReader.AsInteger(entity[1]) + "/" + ReviewedMapReader.AsInteger(row[1]), id);
        }
        foreach (var value in ReviewedMapReader.AsArray(data["joins"]))
        {
            var row = ReviewedMapReader.RequireFields(value, 4);
            result.Add(ReviewedMapReader.AsText(row[2]) + ":" + ReviewedMapReader.AsInteger(row[3]) + "/" + ReviewedMapReader.AsInteger(row[1]), ReviewedMapReader.AsInteger(row[0]));
        }
        return result;
    }

    private static Dictionary<Original.MapLabelPlace, string> LoadOriginal(MapLabelIndex current, string game, string path,
        out List<Original.MapLabelPlace> roster)
    {
        var ids = new Dictionary<Original.MapLabelPlace, string>();
        roster = new List<Original.MapLabelPlace>();
        foreach (string layer in new[] { "Synoptic", "Cities" })
        {
            string wwp = Path.Combine(game, "WW", "PlacenameSets", layer, "0000_0000.wwp");
            using (var reader = new BinaryReader(File.OpenRead(wwp), Encoding.UTF8))
            {
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    var p = new Original.MapLabelPlace { Name = reader.ReadString(), Lat = reader.ReadSingle(),
                        Lon = reader.ReadSingle(), Level = layer == "Synoptic" ? 0 : 1 };
                    reader.ReadUInt32();
                    ids.Add(p, layer + ":" + i);
                    roster.Add(p);
                }
            }
        }
        var regions = Original.MapLabelRegions.LoadFromDictionary(path);
        for (int i = 0; i < regions.Count; ++i) ids.Add(regions[i], "Regions:" + i);
        roster.AddRange(regions);
        var actual = current.Base.ToDictionary(p => p.SourceLayer + ":" + p.SourceIndex);
        if (roster.Count != 7853 || actual.Count != roster.Count) throw new InvalidDataException("Historical source roster changed.");
        foreach (var p in roster)
        {
            string key = ids[p];
            var now = actual[key];
            string name = p.Name;
            if (key == "Regions:193" && name == "巴厘岛") name = "巴厘省";
            if (key == "Regions:338" && name == "阿尔泰共和国") name = "阿尔泰边疆区";
            if (now.Name != name || now.Lat != p.Lat || now.Lon != p.Lon || now.MinZoom != p.MinZoom)
                throw new InvalidDataException("Historical source identity changed: " + key + " " + p.Name);
        }
        return ids;
    }

    private static void VerifyOwner(MapLabelIndex index, HistoryTrace.Tile tile, int[] key, Owner owner)
    {
        int count;
        var marks = index.Marks(key[0], key[1], key[2]);
        var mark = marks.SingleOrDefault(m => !m.Symbol && m.Id == owner.Label);
        if (mark == null || mark.Zoom != owner.Zoom || Math.Abs(mark.X - owner.X) > 1 ||
            Math.Abs(mark.Y - owner.Y) > 1 || string.Join("|", mark.Lines) != owner.Caption ||
            !tile.Labels.TryGetValue(owner.Label, out count) || count != 1)
            throw new InvalidDataException(string.Format("Historical donor {0} ({1}, label {2}) lacks its approved caption at {3}.",
                owner.Source, owner.Member, owner.Label, string.Join("/", key)));
        int expected;
        if (!owner.Reflow.TryGetValue(string.Join("/", key), out expected)) return;
        // In these verified cross-tile changes the whole glyph moved onto fewer or different tiles.
        // Mute only its draw (its layout remains reserved) and count its actual contributions.
        HistoryTrace.MutedLabel = owner.Label;
        byte[] without;
        try { without = MapLabelRenderer.RenderTileBytes(index, key[0], key[1], key[2], out count, out count); }
        finally { HistoryTrace.MutedLabel = null; }
        int changed = 0;
        using (var on = new MemoryStream(tile.Png))
        using (var off = new MemoryStream(without))
        using (var full = new Bitmap(on))
        using (var muted = new Bitmap(off))
            for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++)
                    if (full.GetPixel(x,y).ToArgb() != muted.GetPixel(x,y).ToArgb()) changed++;
        if (changed != expected)
            throw new InvalidDataException(owner.Source + ": expected " + expected + " cross-tile glyph pixels, observed " + changed);
    }

    private static List<Original.MapLabelPlace> InTile(List<Original.MapLabelPlace> places, int[] key)
    {
        double n = 1 << key[0];
        double west = key[1] / n * 360 - 180, east = (key[1] + 1) / n * 360 - 180;
        double north = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * key[2] / n))) * 180 / Math.PI;
        double south = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (key[2] + 1) / n))) * 180 / Math.PI;
        return places.Where(p => p.Lon >= west && p.Lon <= east && p.Lat >= south && p.Lat <= north).ToList();
    }

    public static void Run(string repo, string game, string originalDictionary, string fixturePath)
    {
        var contract = Contract.Read(fixturePath);
        string dict = Path.Combine(repo, "dict", "CMOLocalizer.json");
        var current = MapLabelIndex.Load(game, dict);
        var members = current.Base.ToDictionary(p => p.SourceLayer + ":" + p.SourceIndex);
        var bindings = ReadBindings(dict);
        if (contract.Owners.Keys.Any(key => !bindings.ContainsKey(key)))
            throw new InvalidDataException("A frozen donor/member/zoom binding is missing.");
        foreach (var pair in contract.Owners)
        {
            var owner = pair.Value;
            owner.Label = bindings[pair.Key];
            var suppressed = current.Suppressed(owner.Zoom);
            if (suppressed == null || !suppressed.Contains(members[owner.Member]))
                throw new InvalidDataException("Inactive donor binding: " + pair.Key);
        }
        List<Original.MapLabelPlace> original;
        var ids = LoadOriginal(current, game, originalDictionary, out original);
        foreach (var key in current.CoveredTiles()) contract.Get(string.Join("/", key));
        for (int z = 0; z < 3; ++z)
            for (int x = 0; x < (1 << z); ++x)
                for (int y = 0; y < (1 << z); ++y) contract.Get(z + "/" + x + "/" + y);

        int direct = 0, transferred = 0, staticHosts = 0;
        int wwpAlone = 0, wwpWithRegions = 0, union = 0, regions = 0;
        try
        {
            foreach (var pair in contract.Hosts.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var key = pair.Key.Split('/').Select(int.Parse).ToArray();
                var host = pair.Value;
                var local = InTile(original, key);
                var historical = new HashSet<string>();
                var combined = new HashSet<string>();
                Original.MapLabelRenderer.Painted = p => historical.Add(ids[p]);
                int candidates, rendered;
                using (Original.MapLabelRenderer.RenderTile(local.Where(p => p.Level != 2).ToList(), key[0], key[1], key[2], out candidates, out rendered)) { }
                Original.MapLabelRenderer.Painted = p => combined.Add(ids[p]);
                using (Original.MapLabelRenderer.RenderTile(local, key[0], key[1], key[2], out candidates, out rendered)) { }
                host.Required.UnionWith(combined);
                host.Required.UnionWith(historical);
                if (key[0] >= 3 && key[0] <= 10)
                {
                    wwpAlone += historical.Count;
                    int regionCount = combined.Count(s => s.StartsWith("Regions:", StringComparison.Ordinal));
                    regions += regionCount;
                    wwpWithRegions += combined.Count - regionCount;
                    historical.UnionWith(combined.Where(s => !s.StartsWith("Regions:", StringComparison.Ordinal)));
                    union += historical.Count;
                }
                var tile = HistoryTrace.Draw(current, key[0], key[1], key[2]);
                foreach (var drawn in tile.Legacy)
                    if (drawn.Value != 1 || bindings.ContainsKey(drawn.Key + "/" + key[0]))
                        throw new InvalidDataException("Historical member drawn twice or alongside its donor: " + pair.Key + " " + drawn.Key);
                foreach (var member in host.Required)
                {
                    if (contract.Owners.ContainsKey(member + "/" + key[0])) { transferred++; continue; }
                    if (!tile.Legacy.ContainsKey(member))
                        throw new InvalidDataException("Historical member no longer paints in its tile: " + pair.Key + " " + member);
                    direct++;
                }
                foreach (var owner in host.Owners)
                {
                    VerifyOwner(current, tile, key, owner);
                    staticHosts++;
                }
            }
        }
        finally { Original.MapLabelRenderer.Painted = null; }
        if (wwpAlone != 18407 || wwpWithRegions != 17530 || union != 18489 || regions != 4669)
            throw new InvalidDataException(string.Format("Historical render totals differ: {0}/{1}/{2}/{3}.", wwpAlone, wwpWithRegions, union, regions));
        Console.WriteLine("z3-10 WWP alone {0}, WWP with regions {1}, union {2}, R88 regions {3}.", wwpAlone, wwpWithRegions, union, regions);
        Console.WriteLine("Verified {0} hosts, {1} direct instances, {2} transfers and {3} donor/member/tile checks.",
            contract.Hosts.Count, direct, transferred, staticHosts);
    }
}
