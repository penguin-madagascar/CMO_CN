using System;
using System.Collections;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

public static class MapLabelTilePackTests
{
    static Type packType;
    static int checks;
    static string root;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    static object Call(object pack, string name, params object[] args) { return packType.GetMethod(name).Invoke(pack, args); }
    static object Load(string file) { return Call(null, "Load", file); }
    static bool Equal(byte[] a, byte[] b) { return Convert.ToBase64String(a) == Convert.ToBase64String(b); }
    static bool EqualPixels(byte[] a, byte[] b) {
        using(var sa=new MemoryStream(a)) using(var sb=new MemoryStream(b)) using(var ia=new Bitmap(sa)) using(var ib=new Bitmap(sb)) {
            if(ia.Size!=ib.Size) return false;
            for(int y=0;y<ia.Height;y++) for(int x=0;x<ia.Width;x++) if(ia.GetPixel(x,y)!=ib.GetPixel(x,y)) return false;
            return true;
        }
    }
    static byte[] Png(int size, Color color) {
        using (var image = new Bitmap(size, size)) using (var g = Graphics.FromImage(image)) using (var stream = new MemoryStream()) {
            g.Clear(color); image.Save(stream, ImageFormat.Png); return stream.ToArray();
        }
    }
    static byte[] Payload(int z, int x, int y, int labels, byte[] png, int count, int length, bool duplicate) {
        using (var stream = new MemoryStream()) using (var w = new BinaryWriter(stream)) {
            w.Write(count); for (int i = 0; i < (duplicate ? 2 : 1); i++) {
                w.Write(z); w.Write(x); w.Write(y); w.Write(labels); w.Write(length); w.Write(png);
            } return stream.ToArray();
        }
    }
    static string Pack(byte[] payload, string name, bool badHash) {
        string file = Path.Combine(root, name + ".dat"); string hash;
        using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(payload)).Replace("-", "").ToLowerInvariant();
        using (var w = new BinaryWriter(File.Create(file))) { w.Write(Encoding.ASCII.GetBytes("CMOTIL01")); w.Write(badHash ? new string('0',64) : hash); w.Write(payload); } return file;
    }
    static string RulesPack(byte[] png, string name, int z, string oldName, float lat, float lon, int level, int repeats) {
        byte[] payload;
        using(var stream=new MemoryStream()) using(var w=new BinaryWriter(stream,Encoding.UTF8)) {
            w.Write(Payload(7,0,0,1,png,1,png.Length,false)); w.Write(repeats);
            for(int i=0;i<repeats;i++) { w.Write(z);w.Write(oldName);w.Write(lat);w.Write(lon);w.Write(level); }
            payload=stream.ToArray();
        }
        string path=Pack(payload,name,false); byte[] wire=File.ReadAllBytes(path); wire[7]=(byte)'2';File.WriteAllBytes(path,wire);return path;
    }
    static string Valid(byte[] png, string name) { return Pack(Payload(3,4,4,2,png,1,png.Length,false), name, false); }
    static void CheckPackedRegionIdentity(Assembly engine) {
        var index = engine.GetType("CMOShared.PackedRegionCoverage", true);
        const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Static;
        var covered = index.GetMethod("Contains", hidden);
        var fingerprint = (string)index.GetField("Fingerprint", hidden).GetRawConstantValue();
        string file = Path.Combine(root, "cross-tile-regions.json");
        File.WriteAllText(file, "{\"places\":[[\"甘肃省\",38.7393,100.735,4.6],[\"青海省\",35.2652,96.2377,4.6],"
            + "[\"甘肃省\",38.7393,100.736,4.6],[\"金沙萨\",-4.4952,15.8492,6]]}", Encoding.UTF8);
        var places = (IList)engine.GetType("CMOShared.MapLabelRegions").GetMethod("LoadFromDictionary").Invoke(null, new object[]{file});
        Func<string,int,int,bool> includes = (pack, row, z) => (bool)covered.Invoke(null, new object[]{pack, places[row], z});
        Check(includes(fingerprint, 0, 7), "exact Gansu province already supplied on neighboring z7 tile");
        Check(!includes(new string('0', 64), 0, 7), "unrelated pack retains dictionary Gansu");
        Check(!includes(fingerprint, 0, 4), "Gansu is not globally suppressed at other zooms");
        Check(!includes(fingerprint, 1, 7), "Qinghai with no canonical province label is retained");
        Check(!includes(fingerprint, 2, 7), "changed R88 anchor is not mistaken for the reviewed entity");
        Check(!includes(fingerprint, 3, 6), "same-named city does not remove Kinshasa province");
        places[0].GetType().GetField("Rank").SetValue(places[0], 2);
        Check(!includes(fingerprint, 0, 7), "same-named city at identical anchor is not an administrative match");
    }
    static void CheckRegionOverlay(Assembly engine, MethodInfo render) {
        string dict = Path.Combine(root, "region-dictionary.json");
        File.WriteAllText(dict, "{\"places\":[[\"某省\",-20,22.5,2.5]]}", Encoding.UTF8);
        object places = engine.GetType("CMOShared.MapLabelRegions").GetMethod("LoadFromDictionary").Invoke(null, new object[]{dict});
        Color original = Color.FromArgb(89, 97, 154, 213);
        byte[] baseline;
        using (var bitmap = new Bitmap(256, 256)) using (var stream = new MemoryStream()) {
            bitmap.SetPixel(9, 9, original);
            bitmap.Save(stream, ImageFormat.Png);
            baseline = stream.ToArray();
        }
        object pack = Load(Valid(baseline, "region-coverage"));
        object[] hit = {places, pack, 3, 4, 4, 0, 0};
        byte[] overlay = (byte[])render.Invoke(null, hit);
        Check((int)hit[5] == 3 && (int)hit[6] == 3, "eligible R88 region added to supplied PNG");
        using (var stream = new MemoryStream(overlay)) using (var image = new Bitmap(stream)) {
            Check(image.GetPixel(9, 9).ToArgb() == original.ToArgb(), "original semitransparent ink retains exact ARGB");
            bool drawn = false;
            for (int y = 120; y < 160 && !drawn; y++)
                for (int x = 100; x < 155; x++) if (image.GetPixel(x, y).A != 0) { drawn = true; break; }
            Check(drawn, "added Chinese text lies at its R88 label coordinates");
        }
        var solid = Png(256, Color.Red);
        hit[1] = Load(Valid(solid, "region-collision"));
        Check(Equal((byte[])render.Invoke(null, hit), solid) && (int)hit[5] == 3 && (int)hit[6] == 2,
            "collision preserves original packed bytes and counts only original labels");
        // The first region's original anchor is covered; the second remains free. A retry must
        // leave the second at its original coordinates and preserve every opaque source pixel.
        byte[] crowded;
        using (var bitmap = new Bitmap(256, 256)) using (var g = Graphics.FromImage(bitmap))
        using (var stream = new MemoryStream()) {
            g.FillRectangle(Brushes.Red, 80, 0, 96, 125);
            bitmap.Save(stream, ImageFormat.Png);
            crowded = stream.ToArray();
        }
        var regionalReader = engine.GetType("CMOShared.MapLabelRegions").GetMethod("LoadFromDictionary");
        File.WriteAllText(dict, "{\"places\":[[\"乙省\",-24,22.5,2.5]]}", Encoding.UTF8);
        object second = regionalReader.Invoke(null, new object[]{dict});
        var partial = Load(Valid(crowded, "region-nearby"));
        object[] anchored = {second, partial, 3, 4, 4, 0, 0};
        byte[] fixedRegion = (byte[])render.Invoke(null, anchored);
        File.WriteAllText(dict, "{\"places\":[[\"某省\",-20,22.5,2.5],[\"乙省\",-24,22.5,2.5]]}", Encoding.UTF8);
        object[] adjusted = {regionalReader.Invoke(null, new object[]{dict}), partial, 3, 4, 4, 0, 0};
        byte[] both = (byte[])render.Invoke(null, adjusted);
        Check((int)anchored[6] == 3 && (int)adjusted[6] == 4, "nearby placement restores distinct region blocked by canonical text");
        using (var originalStream = new MemoryStream(fixedRegion)) using (var fixedImage = new Bitmap(originalStream))
        using (var bothStream = new MemoryStream(both)) using (var bothImage = new Bitmap(bothStream)) {
            bool same = true;
            for (int y = 0; y < 256 && same; y++) for (int x = 0; x < 256; x++) {
                Color pixel = fixedImage.GetPixel(x, y);
                if (pixel.A != 0 && pixel.ToArgb() != bothImage.GetPixel(x, y).ToArgb()) { same = false; break; }
            }
            Check(same, "all canonical ink and the unobstructed province stay at exact original pixels");
        }
        Check(Equal(both, (byte[])render.Invoke(null, adjusted)), "nearby province layout is deterministic");
        File.WriteAllText(dict, "{\"places\":[[\"谢胡布尔\",-20,22.5,2.5]]}", Encoding.UTF8);
        using (var smallSpace = new Bitmap(256, 256)) using (var stream = new MemoryStream()) {
            using (var g = Graphics.FromImage(smallSpace)) g.Clear(Color.Red);
            for (int y = 99; y < 136; y++) for (int x = 112; x < 144; x++) smallSpace.SetPixel(x, y, Color.Transparent);
            smallSpace.Save(stream, ImageFormat.Png);
            var tight = Load(Valid(stream.ToArray(), "region-narrow"));
            object[] narrow = {regionalReader.Invoke(null, new object[]{dict}), tight, 3, 4, 4, 0, 0};
            byte[] result = (byte[])render.Invoke(null, narrow);
            Check((int)narrow[6] == 3 && !Equal(result, stream.ToArray()), "crowded host fits a required CJK name into a narrow clear column");
        }
        using (var stream = new MemoryStream(crowded)) using (var originalImage = new Bitmap(stream))
        using (var output = new MemoryStream()) {
            // Leave one native-transparent channel at the true z3 geographic anchor (128,116.16).
            for (int y = 0; y < 125; y++) for (int x = 127; x <= 129; x++) originalImage.SetPixel(x, y, Color.Transparent);
            originalImage.Save(output, ImageFormat.Png);
            hit[1] = Load(Valid(output.ToArray(), "region-leader"));
            byte[] linked = (byte[])render.Invoke(null, hit);
            using (var linkedStream = new MemoryStream(linked)) using (var linkedImage = new Bitmap(linkedStream)) {
                bool atAnchor = false;
                for (int y = 115; y <= 118; y++) for (int x = 127; x <= 129; x++)
                    if (linkedImage.GetPixel(x, y).A > 0) atAnchor = true;
                Check(atAnchor, "displaced label has a visible leader at its true geographic anchor");
                Check(linkedImage.GetPixel(126, 116).ToArgb() == Color.Red.ToArgb(), "leader never alters the adjacent native glyph");
            }
        }
        object[] outside = {places, null, 2, 2, 2, 0, 0};
        render.Invoke(null, outside);
        Check((int)outside[5] == 0, "R88 fractional minimum zoom rounds up");
        string game = Path.Combine(root, "region-cache");
        Check((int)Call(pack, "ApplyToCacheWithLegacy", game, places) == 1
            && Equal(overlay, File.ReadAllBytes(Tile(game))), "packed warm cache matches live regional overlay");
        Check((int)Call(pack, "ApplyToCacheWithLegacy", game, places) == 0, "repeat does not rewrite regional overlay");
        string fallback = Path.Combine(Cache(game), @"4\9\8.png");
        Write(fallback, solid);
        object[] miss = {places, pack, 4, 9, 8, 0, 0};
        byte[] expected = (byte[])render.Invoke(null, miss);
        Check((int)miss[6] == 1 && (int)Call(pack, "ApplyToCacheWithLegacy", game, places) == 1
            && Equal(expected, File.ReadAllBytes(fallback)), "existing unpacked region host is refreshed in the warm cache");
    }

    static void CheckWarmCacheVerification(Assembly engine, MethodInfo render) {
        string game = Path.Combine(root, "warm");
        string dictionary = Path.Combine(root, "warm-cache-regions.json");
        File.WriteAllText(dictionary, "{\"places\":[[\"甲省\",-12,24,2.5],[\"乙州\",-12,24,2.5]]}", Encoding.UTF8);
        MethodInfo readRegions = engine.GetType("CMOShared.MapLabelRegions").GetMethod("LoadFromDictionary");
        var places = (IList)readRegions.Invoke(null, new object[]{dictionary});
        string packFile = Valid(Png(256, Color.Transparent), "warm-cache-verification");
        object pack = Load(packFile);
        string packed = Tile(game), fallback = Path.Combine(Cache(game), @"4\9\8.png");
        string receipt = Path.Combine(Cache(game), ".chinese-map-rendered.sha256");
        byte[] original = Png(256, Color.Blue);
        Write(packed, original);
        Write(fallback, original);
        Func<int> apply = () => (int)Call(pack, "ApplyToCacheWithLegacy", game, places);
        // Use a fresh pack so the comparison never shares the tested instance's cached roster index.
        Func<int,int,int,byte[]> expected = (z,x,y) =>
            (byte[])render.Invoke(null, new object[]{places, Load(packFile), z, x, y, 0, 0});
        byte[] first = expected(3, 4, 4), firstFallback = expected(4, 9, 8);
        Check(apply() == 2 && Equal(first, File.ReadAllBytes(packed)) && Equal(firstFallback, File.ReadAllBytes(fallback))
            && File.Exists(receipt), "cold cache writes a verification receipt for packed and existing fallback tiles");
        var fixedTime = new DateTime(2003, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(packed, fixedTime);
        File.SetLastWriteTimeUtc(fallback, fixedTime);
        pack = Load(packFile);
        places = (IList)readRegions.Invoke(null, new object[]{dictionary});
        Check(apply() == 0 && File.GetLastWriteTimeUtc(packed) == fixedTime && File.GetLastWriteTimeUtc(fallback) == fixedTime,
            "a new pack and roster instance reuse intact cached pixels");
        foreach (string path in new[]{packed, fallback}) {
            byte[] damaged = File.ReadAllBytes(path);
            damaged[damaged.Length / 2] ^= 1;
            File.WriteAllBytes(path, damaged);
            File.SetLastWriteTimeUtc(path, fixedTime);
        }
        Check(apply() == 2 && Equal(first, File.ReadAllBytes(packed)) && Equal(firstFallback, File.ReadAllBytes(fallback)),
            "identical length and modification time cannot hide packed or fallback PNG corruption");
        File.Delete(packed);
        File.WriteAllBytes(receipt, new byte[]{1, 2, 3});
        Check(apply() == 1 && Equal(first, File.ReadAllBytes(packed)) && new FileInfo(receipt).Length > 3,
            "a truncated receipt is rebuilt and the missing covered PNG is restored");
        object firstPlace = places[0];
        places.RemoveAt(0);
        places.Add(firstPlace);
        byte[] reordered = expected(3, 4, 4);
        Check(!Equal(first, reordered) && apply() == 2 && Equal(reordered, File.ReadAllBytes(packed))
            && Equal(expected(4, 9, 8), File.ReadAllBytes(fallback)), "changing roster order respects equal-priority label placement");
        byte[] lastReceipt = File.ReadAllBytes(receipt);
        object priorityWinner = places[0];
        priorityWinner.GetType().GetField("Name").SetValue(priorityWinner, "更名省");
        byte[] renamed = expected(3, 4, 4), renamedFallback = expected(4, 9, 8);
        Check(!Equal(renamed, reordered), "changing a field on the same roster changes its rendered bytes");
        using (var hold = File.Open(fallback, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
            bool failed = false;
            try { apply(); } catch (TargetInvocationException e) { failed = e.InnerException is IOException; }
            Check(failed && (!File.Exists(receipt) || Equal(lastReceipt, File.ReadAllBytes(receipt))),
                "failed tile replacement cannot publish a receipt for changed roster data");
        }
        apply();
        Check(Equal(renamed, File.ReadAllBytes(packed)) && Equal(renamedFallback, File.ReadAllBytes(fallback)) && apply() == 0,
            "retry finishes the changed roster and subsequent startup keeps its verified outputs");
        Check(Equal(original, File.ReadAllBytes(Path.Combine(Cache(game) + ".orig", @"3\4\4.png")))
            && Equal(original, File.ReadAllBytes(Path.Combine(Cache(game) + ".orig", @"4\9\8.png"))),
            "warm repairs and failed updates preserve the original cache");
        File.Delete(packed);
        File.WriteAllBytes(fallback, original);
        using (var start = new System.Threading.ManualResetEventSlim(false)) {
            Func<int> repair = () => {
                object fresh = Load(packFile);
                start.Wait();
                return (int)Call(fresh, "ApplyToCacheWithLegacy", game, places);
            };
            var left = System.Threading.Tasks.Task.Factory.StartNew(repair);
            var right = System.Threading.Tasks.Task.Factory.StartNew(repair);
            start.Set();
            Check(left.Result + right.Result == 2 && Equal(renamed, File.ReadAllBytes(packed))
                && Equal(renamedFallback, File.ReadAllBytes(fallback)) && apply() == 0,
                "concurrent refreshes repair both files once and leave a reusable receipt");
        }
        places.Clear();
        Check(apply() == 2 && Equal(expected(3, 4, 4), File.ReadAllBytes(packed))
            && Equal(expected(4, 9, 8), File.ReadAllBytes(fallback)),
            "removing the last label clears its previously managed fallback tile");
        File.Delete(fallback);
        Check(apply() == 0 && !File.Exists(fallback), "a deleted off-pack cache tile remains on demand");
    }

    static void CheckHistoricalWwp(Assembly engine, MethodInfo render) {
        var type = engine.GetType("CMOShared.MapLabelPlace");
        string file = Path.Combine(root, "wwp-region-collision.json");
        File.WriteAllText(file, "{\"places\":[[\"同址省\",-1.4,1.4,6]]}", Encoding.UTF8);
        var loaded = engine.GetType("CMOShared.MapLabelRegions").GetMethod("LoadFromDictionary").Invoke(null, new object[]{file});
        var roster = (IList)loaded;
        foreach (string name in new[]{"原有城","历史隐藏城"}) {
            var city = Activator.CreateInstance(type);
            type.GetField("Name").SetValue(city, name);
            type.GetField("Level").SetValue(city, 1);
            type.GetField("Lat").SetValue(city, -1.4f);
            type.GetField("Lon").SetValue(city, 1.4f);
            roster.Add(city);
        }
        string stamp = (string)engine.GetType("CMOShared.PackedWwpCoverage").GetField("Fingerprint",
            BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue();
        byte[] transparent = Png(256, Color.Transparent);
        foreach (bool hit in new[]{true, false}) {
            int x = hit ? 64 : 0;
            object pack = Load(Pack(Payload(7, x, 64, 0, transparent, 1, transparent.Length, false), "union-" + x, false));
            // The miniature fixture stands in for an attested package without copying its real 9,377 PNGs.
            packType.GetProperty("Fingerprint").GetSetMethod(true).Invoke(pack, new object[]{stamp});
            object[] args = {roster, pack, 7, 64, 64, 0, 0};
            byte[] served = (byte[])render.Invoke(null, args);
            Check((int)args[6] == 2, (hit ? "packed" : "fallback") + " retains both historical city and upstream province, excludes always-hidden city");
            string game = Path.Combine(root, "wwp-warm-" + x);
            string tile = Path.Combine(Cache(game), @"7\64\64.png");
            Write(tile, transparent);
            Call(pack, "ApplyToCacheWithLegacy", game, roster);
            Check(Equal(served, File.ReadAllBytes(tile)), (hit ? "packed" : "fallback") + " indexed cache equals original-roster rendering");
        }
        object[] original = {roster, null, 7, 64, 64, 0, 0};
        byte[] ordinary = (byte[])render.Invoke(null, original);
        Check((int)original[6] == 1, "without optional map pack upstream R88 collision rules remain in effect");
        using (var stream = new MemoryStream(ordinary)) using (var image = new Bitmap(stream))
            Check(Math.Abs(image.HorizontalResolution - 96f) < 0.1f && Math.Abs(image.VerticalResolution - 96f) < 0.1f,
                "ordinary fallback PNG density is independent of the process display DPI");
    }
    static void Reject(string file, string name) {
        try { Load(file); throw new Exception("Accepted " + name); }
        catch (TargetInvocationException e) { Check(e.InnerException is InvalidDataException || e.InnerException is EndOfStreamException || e.InnerException is ArgumentException || e.InnerException is FormatException, "reject " + name); }
    }
    static string Cache(string game) { return Path.Combine(game, @"WW\Cache\Stamen Terrain - Labels"); }
    static string Tile(string game) { return Path.Combine(Cache(game), @"3\4\4.png"); }
    static void Write(string file, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllBytes(file, bytes); }
    static int Apply(object pack, string game) { object value = Call(pack, "ApplyToCache", game); return value == null ? -1 : (int)value; }
    public static int Main(string[] args) {
        try {
            root=Path.GetFullPath(args[2]); Directory.CreateDirectory(root);
            var engine=Assembly.LoadFrom(args[0]); packType=engine.GetType("CMOShared.MapLabelTilePack");
            Check(packType != null, "shared verified tilepack API exists");
            byte[] rulePng=Png(256,Color.Red);
            object rules=Load(RulesPack(rulePng,"v2",7,"旧城",0f,0f,1,1));
            Type rulePlaceType=engine.GetType("CMOShared.MapLabelPlace",true);
            object rulePlace=Activator.CreateInstance(rulePlaceType);
            rulePlaceType.GetField("Name").SetValue(rulePlace,"旧城");rulePlaceType.GetField("Level").SetValue(rulePlace,1);
            Func<object,int,bool> suppressed=(place,z)=>(bool)Call(rules,"IsLegacySuppressed",place,z);
            Check(suppressed(rulePlace,7),"v2 exact legacy point suppressed");
            rulePlaceType.GetField("Lat").SetValue(rulePlace,BitConverter.ToSingle(BitConverter.GetBytes(unchecked((int)0x80000000)),0));
            Check(!suppressed(rulePlace,7),"negative zero does not equal original positive zero bits");rulePlaceType.GetField("Lat").SetValue(rulePlace,0f);
            rulePlaceType.GetField("Name").SetValue(rulePlace,"别城");Check(!suppressed(rulePlace,7),"other name retained");rulePlaceType.GetField("Name").SetValue(rulePlace,"旧城");
            Check(!suppressed(rulePlace,8),"other zoom not suppressed");
            foreach(string field in new[]{"Lat","Lon"}) {
                rulePlaceType.GetField(field).SetValue(rulePlace,0.0001f);
                Check(!suppressed(rulePlace,7),"same name different "+field+" retained");rulePlaceType.GetField(field).SetValue(rulePlace,0f);
            }
            rulePlaceType.GetField("Level").SetValue(rulePlace,0);Check(!suppressed(rulePlace,7),"other level retained");rulePlaceType.GetField("Level").SetValue(rulePlace,1);
            rulePlaceType.GetField("IsLegacy").SetValue(rulePlace,false);Check(!suppressed(rulePlace,7),"explicit catalog point retained");rulePlaceType.GetField("IsLegacy").SetValue(rulePlace,true);
            object rulePlaces=Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(rulePlaceType));
            rulePlaces.GetType().GetMethod("Add").Invoke(rulePlaces,new[]{rulePlace});
            var ruleRenderer=engine.GetType("CMOShared.MapLabelRenderer",true).GetMethod("RenderTileBytes");
            CheckPackedRegionIdentity(engine);
            CheckRegionOverlay(engine, ruleRenderer);
            CheckWarmCacheVerification(engine, ruleRenderer);
            CheckHistoricalWwp(engine, ruleRenderer);
            object[] ruleHit={rulePlaces,rules,7,0,0,0,0};
            Check(Equal(rulePng,(byte[])ruleRenderer.Invoke(null,ruleHit)) && (int)ruleHit[5]==1 && (int)ruleHit[6]==1,"v2 direct hit retains exact approved PNG and counts");
            object[] without={rulePlaces,null,7,64,64,0,0},with={rulePlaces,rules,7,64,64,0,0};
            ruleRenderer.Invoke(null,without);byte[] blank=(byte[])ruleRenderer.Invoke(null,with);
            Check((int)without[6]==1 && (int)with[6]==0,"v2 fallback removes only approved old point");
            string ruleGame=Path.Combine(root,"rule-cache");string oldRuleTile=Path.Combine(Cache(ruleGame),@"7\64\64.png");byte[] blueRule=Png(256,Color.Blue);Write(oldRuleTile,blueRule);
            Check((int)Call(rules,"ApplyToCacheWithLegacy",ruleGame,rulePlaces)==2,"v2 cache repairs package and outside legacy point");
            Check(Equal(blank,File.ReadAllBytes(oldRuleTile)),"outside cache becomes exact filtered rendering");
            Check(Equal(blueRule,File.ReadAllBytes(oldRuleTile.Replace("Labels\\","Labels.orig\\"))),"outside legacy backup preserved");
            Check((int)Call(rules,"ApplyToCacheWithLegacy",ruleGame,rulePlaces)==0,"v2 repeat no writes");
            Write(oldRuleTile,blueRule);Check((int)Call(rules,"ApplyToCacheWithLegacy",ruleGame,rulePlaces)==1,"v2 outside damaged tile repaired");
            Check(((IList)rulePlaces).Count==1 && object.ReferenceEquals(((IList)rulePlaces)[0],rulePlace),"render and refresh preserve caller collection");
            string missingRuleGame=Path.Combine(root,"missing-rule-cache");
            Check((int)Call(rules,"ApplyToCacheWithLegacy",missingRuleGame,rulePlaces)==1 && !File.Exists(Path.Combine(Cache(missingRuleGame),@"7\64\64.png")),"no absent outside legacy cache is created");
            string boundaryGame=Path.Combine(root,"v2-boundary");
            foreach(int bx in new[]{63,64}) foreach(int by in new[]{63,64}) {
                object[] oldBoundaryArgs={rulePlaces,null,7,bx,by,0,0};byte[] oldBoundaryPng=(byte[])ruleRenderer.Invoke(null,oldBoundaryArgs);
                Check((int)oldBoundaryArgs[6]==1 && !Equal(blank,oldBoundaryPng),"legacy renderer actually drew boundary copy at "+bx+","+by);
                Write(Path.Combine(Cache(boundaryGame),"7",bx.ToString(),by+".png"),oldBoundaryPng);
            }
            string notBoundary=Path.Combine(Cache(boundaryGame),@"7\62\64.png");Write(notBoundary,blueRule);
            Check((int)Call(rules,"ApplyToCacheWithLegacy",boundaryGame,rulePlaces)==5,"boundary placement refreshes all four inclusive existing tiles plus pack");
            foreach(int bx in new[]{63,64}) foreach(int by in new[]{63,64}) Check(Equal(blank,File.ReadAllBytes(Path.Combine(Cache(boundaryGame),"7",bx.ToString(),by+".png"))),"boundary legacy copy removed at "+bx+","+by);
            Check(Equal(blueRule,File.ReadAllBytes(notBoundary)),"neighbor outside inclusive placement is not refreshed");
            string pngOnlyGame=Path.Combine(root,"v2-png-only");string pngOnlyOld=Path.Combine(Cache(pngOnlyGame),@"7\64\64.png");Write(pngOnlyOld,blueRule);
            Check(Apply(rules,pngOnlyGame)==1 && Equal(blueRule,File.ReadAllBytes(pngOnlyOld)),"old ApplyToCache remains PNG-only for v2");
            object keptPlace=Activator.CreateInstance(rulePlaceType);
            rulePlaceType.GetField("Name").SetValue(keptPlace,"保留城");rulePlaceType.GetField("Lat").SetValue(keptPlace,-1f);rulePlaceType.GetField("Lon").SetValue(keptPlace,1f);rulePlaceType.GetField("Level").SetValue(keptPlace,1);
            ((IList)rulePlaces).Add(keptPlace);
            object keptPlaces=Activator.CreateInstance(rulePlaces.GetType());((IList)keptPlaces).Add(keptPlace);
            object[] keptArgs={keptPlaces,null,7,64,64,0,0};byte[] keptPng=(byte[])ruleRenderer.Invoke(null,keptArgs);
            object[] mixedArgs={rulePlaces,rules,7,64,64,0,0};byte[] mixedPng=(byte[])ruleRenderer.Invoke(null,mixedArgs);
            Check((int)mixedArgs[6]==1 && Equal(keptPng,mixedPng),"filtered PNG preserves the other legacy name in same tile");
            Write(oldRuleTile,blueRule);Call(rules,"ApplyToCacheWithLegacy",ruleGame,rulePlaces);
            Check(Equal(keptPng,File.ReadAllBytes(oldRuleTile)),"outside cache retains other name pixels");
            string failRuleGame=Path.Combine(root,"v2-failed-outside");string failedOld=Path.Combine(Cache(failRuleGame),@"7\64\64.png");Write(failedOld,blueRule);bool failedRule=false;
            using(var held=File.Open(failedOld,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)) {
                try { Call(rules,"ApplyToCacheWithLegacy",failRuleGame,rulePlaces); } catch(TargetInvocationException e) { failedRule=e.InnerException is IOException; }
                Check(failedRule && Equal(blueRule,File.ReadAllBytes(failedOld)),"blocked v2 outside atomic write propagates and preserves bytes");
            }
            Check(!File.Exists(Path.Combine(Cache(failRuleGame),".chinese-map-tiles.sha256")) && Equal(blueRule,File.ReadAllBytes(failedOld.Replace("Labels\\","Labels.orig\\")))
                && Directory.GetFiles(Cache(failRuleGame),"*.tmp-*",SearchOption.AllDirectories).Length==0,"failed v2 refresh keeps full backup and no completion marker or temporary PNG");
            Check((int)Call(rules,"ApplyToCacheWithLegacy",failRuleGame,rulePlaces)==1 && Equal(keptPng,File.ReadAllBytes(failedOld)),"v2 retry repairs blocked outside tile and counts only write");
            File.SetLastWriteTimeUtc(failedOld,new DateTime(2001,1,1,0,0,0,DateTimeKind.Utc));Call(rules,"ApplyToCacheWithLegacy",failRuleGame,rulePlaces);
            Check(File.GetLastWriteTimeUtc(failedOld).Year==2001,"v2 unchanged outside PNG is not rewritten");
            Reject(RulesPack(rulePng,"v2duplicate",7,"旧城",0,0,1,2),"duplicate legacy rule");
            Reject(RulesPack(rulePng,"v2zoom",8,"旧城",0,0,1,1),"rule without covered zoom");
            Reject(RulesPack(rulePng,"v2name",7,"English",0,0,1,1),"non Chinese legacy rule");
            Reject(RulesPack(rulePng,"v2nan",7,"旧城",float.NaN,0,1,1),"nonfinite legacy rule");
            Reject(RulesPack(rulePng,"v2inf",7,"旧城",0,float.PositiveInfinity,1,1),"infinite longitude");
            Reject(RulesPack(rulePng,"v2lat",7,"旧城",90.001f,0,1,1),"out of range latitude");
            Reject(RulesPack(rulePng,"v2lon",7,"旧城",0,-180.001f,1,1),"out of range longitude");
            Reject(RulesPack(rulePng,"v2level",7,"旧城",0,0,2,1),"invalid legacy level");
            Reject(RulesPack(rulePng,"v2zero",7,"旧城",0,0,1,0),"zero v2 rule count");
            Check(Load(RulesPack(rulePng,"v2pole",7,"极点",90,180,0,1))!=null,"canonical polar coordinate accepted");
            string trailingRules=RulesPack(rulePng,"v2trailing-source",7,"旧城",0,0,1,1);byte[] ruleWire=File.ReadAllBytes(trailingRules);int ruleOffset=8+1+64;
            byte[] trailingPayload=new byte[ruleWire.Length-ruleOffset+1];Array.Copy(ruleWire,ruleOffset,trailingPayload,0,trailingPayload.Length-1);
            string trailingV2=Pack(trailingPayload,"v2trailing",false);byte[] trailingWire=File.ReadAllBytes(trailingV2);trailingWire[7]=(byte)'2';File.WriteAllBytes(trailingV2,trailingWire);Reject(trailingV2,"v2 trailing payload");
            byte[] png=Png(256,Color.Red); string valid=Valid(png,"valid"); object pack=Load(valid);
            object[] lookup={3,4,4,null,0}; Check((bool)Call(pack,"TryGetTile",lookup),"lookup hit");
            Check(Equal(png,(byte[])lookup[3]) && (int)lookup[4]==2,"exact PNG and label count");
            ((byte[])lookup[3])[0]=0; object[] again={3,4,4,null,0}; Call(pack,"TryGetTile",again); Check(Equal(png,(byte[])again[3]),"returned bytes cannot mutate pack");
            object[] miss={3,0,0,null,0}; Check(!(bool)Call(pack,"TryGetTile",miss),"unlisted tile misses");
            Reject(Pack(Payload(3,4,4,2,png,1,png.Length,false),"hash",true),"hash");
            byte[] magic=File.ReadAllBytes(valid); magic[0]=0; string mf=Path.Combine(root,"magic.dat"); File.WriteAllBytes(mf,magic); Reject(mf,"magic");
            foreach (int z in new[]{2,11}) Reject(Pack(Payload(z,0,0,0,png,1,png.Length,false),"zoom"+z,false),"zoom "+z);
            foreach (int coord in new[]{-1,8}) { Reject(Pack(Payload(3,coord,0,0,png,1,png.Length,false),"x"+coord,false),"x "+coord); Reject(Pack(Payload(3,0,coord,0,png,1,png.Length,false),"y"+coord,false),"y "+coord); }
            Reject(Pack(Payload(3,4,4,-1,png,1,png.Length,false),"labels",false),"negative labels");
            Reject(Pack(Payload(3,4,4,0,png,2,png.Length,true),"duplicate",false),"duplicate coordinate");
            foreach(int count in new[]{0,-1,2,int.MaxValue}) Reject(Pack(Payload(3,4,4,0,png,count,png.Length,false),"count"+count,false),"count "+count);
            foreach(int length in new[]{0,-1,png.Length+1,png.Length-1,int.MaxValue}) Reject(Pack(Payload(3,4,4,0,png,1,length,false),"length"+length,false),"length "+length);
            byte[] small=Png(128,Color.Red); Reject(Pack(Payload(3,4,4,0,small,1,small.Length,false),"size",false),"PNG dimensions");
            byte[] bad={1,2,3,4}; Reject(Pack(Payload(3,4,4,0,bad,1,4,false),"decode",false),"PNG decoding");
            using(var bmp=new Bitmap(256,256)) using(var s=new MemoryStream()) { bmp.Save(s,ImageFormat.Bmp); byte[] b=s.ToArray(); Reject(Pack(Payload(3,4,4,0,b,1,b.Length,false),"bmp",false),"non-PNG image"); }
            byte[] trailing=new byte[Payload(3,4,4,0,png,1,png.Length,false).Length+1]; Array.Copy(Payload(3,4,4,0,png,1,png.Length,false),trailing,trailing.Length-1); Reject(Pack(trailing,"trailing",false),"trailing bytes");
            string game=Path.Combine(root,"game"); byte[] old={9,8,7}; Write(Tile(game),old); string other=Path.Combine(Cache(game),@"3\1\1.png"); Write(other,old); string terrain=Path.Combine(game,@"WW\Cache\Stamen Terrain\3\4\4.png"); Write(terrain,old);
            Apply(pack,game); Check(Equal(png,File.ReadAllBytes(Tile(game))),"existing transparent/bad tile replaced"); Check(Equal(old,File.ReadAllBytes(Tile(game).Replace("Labels\\","Labels.orig\\"))),"first backup retains original bytes"); Check(Equal(old,File.ReadAllBytes(other)) && Equal(old,File.ReadAllBytes(terrain)),"only listed Labels tile changes");
            string marker=Path.Combine(Cache(game),".chinese-map-tiles.sha256"); Check(File.ReadAllText(marker).Trim()==(string)packType.GetProperty("Fingerprint").GetValue(pack,null),"completed fingerprint");
            DateTime fixedTime=new DateTime(2001,1,1); File.SetLastWriteTimeUtc(Tile(game),fixedTime); File.SetLastWriteTimeUtc(marker,fixedTime); Apply(pack,game); Check(File.GetLastWriteTimeUtc(Tile(game))==fixedTime && File.GetLastWriteTimeUtc(marker)==fixedTime,"repeat does not rewrite identical bytes");
            File.Delete(Tile(game)); Apply(pack,game); Check(Equal(png,File.ReadAllBytes(Tile(game))),"deleted destination repaired despite marker"); File.WriteAllBytes(Tile(game),old); Apply(pack,game); Check(Equal(png,File.ReadAllBytes(Tile(game))),"corrupt destination repaired despite marker");
            byte[] blue=Png(256,Color.Blue); Apply(Load(Valid(blue,"blue")),game); Check(Equal(blue,File.ReadAllBytes(Tile(game))) && Equal(old,File.ReadAllBytes(Tile(game).Replace("Labels\\","Labels.orig\\"))),"source fingerprint changes without overwriting orig");
            string empty=Path.Combine(root,"empty"); Directory.CreateDirectory(empty); Apply(pack,empty); Check(File.Exists(Tile(empty)) && Directory.Exists(Cache(empty)+".orig"),"missing cache backs up empty and adds pack tile");
            string fail=Path.Combine(root,"fail"); Write(Tile(fail),old); string locked=Path.Combine(Cache(fail),"locked.bin"); Write(locked,old);
            using(var hold=File.Open(locked,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) { try { Apply(pack,fail); throw new Exception("backup must fail"); } catch(TargetInvocationException) {} }
            Check(!Directory.Exists(Cache(fail)+".orig") && !File.Exists(Path.Combine(Cache(fail),".chinese-map-tiles.sha256")) && Equal(old,File.ReadAllBytes(Tile(fail))),"failed backup never publishes orig or modifies cache");
            string blocked=Path.Combine(root,"blocked"); Directory.CreateDirectory(blocked); Write(Path.Combine(Cache(blocked),"3"),old);
            try { Apply(pack,blocked); throw new Exception("write must fail"); } catch(TargetInvocationException) {}
            Check(!File.Exists(Path.Combine(Cache(blocked),".chinese-map-tiles.sha256")) && Equal(old,File.ReadAllBytes(terrain)),"failed write has no completion marker and terrain is untouched");
            string readOnly=Path.Combine(root,"readonly"); Write(Tile(readOnly),old); File.SetAttributes(Tile(readOnly),FileAttributes.ReadOnly);
            try { try { Apply(pack,readOnly); throw new Exception("read-only write must fail"); } catch(TargetInvocationException) {} Check(!File.Exists(Path.Combine(Cache(readOnly),".chinese-map-tiles.sha256")),"read-only target fails without marker"); } finally { File.SetAttributes(Tile(readOnly),FileAttributes.Normal); }
            Type rendererType=engine.GetType("CMOShared.MapLabelRenderer",true);
            Type placeType=engine.GetType("CMOShared.MapLabelPlace",true);
            object emptyPlaces=Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(placeType));
            object[] fallback={emptyPlaces,null,3,0,0,0,0}; object[] missingPack={emptyPlaces,pack,3,0,0,0,0};
            Check(Equal((byte[])rendererType.GetMethod("RenderTileBytes").Invoke(null,fallback),(byte[])rendererType.GetMethod("RenderTileBytes").Invoke(null,missingPack)),"pack miss preserves fallback PNG bytes and counts");
            Type manager=engine.GetType("CMOLocalizer.CustomAppDomainManager",true); manager.GetField("mapTilePack",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,pack);
            object[] render={3,4,4,0,0}; byte[] runtime=(byte[])manager.GetMethod("RenderChineseTileBytes",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,render); Check(Equal(png,runtime) && (int)render[4]==2,"runtime reflection entry uses exact pack bytes");
            string offline=Path.Combine(root,"offline"); Directory.CreateDirectory(offline); File.Copy(valid,Path.Combine(offline,"ChineseMapTiles.dat"));
            var installer=Assembly.LoadFrom(args[1]).GetType("CMOLocalizerInstaller.InstallerCore",true); var localize=installer.GetMethod("LocalizeMapLabelTiles",BindingFlags.Static|BindingFlags.NonPublic);
            string offlineRules=Path.Combine(root,"offline-rules");Directory.CreateDirectory(offlineRules);File.Copy(Path.Combine(root,"v2.dat"),Path.Combine(offlineRules,"ChineseMapTiles.dat"));
            string offlineRuleTile=Path.Combine(Cache(offlineRules),@"7\64\64.png");Write(offlineRuleTile,blueRule);
            string ruleWwp=Path.Combine(offlineRules,@"WW\PlacenameSets\Cities\0000_0000.wwp");Directory.CreateDirectory(Path.GetDirectoryName(ruleWwp));
            using(var writer=new BinaryWriter(File.Create(ruleWwp),Encoding.UTF8)) {
                writer.Write(2);writer.Write("旧城");writer.Write(0f);writer.Write(0f);writer.Write((uint)1);
                writer.Write("保留城");writer.Write(-1f);writer.Write(1f);writer.Write((uint)1);
            }
            int offlineRuleWrites=(int)localize.Invoke(null,new object[]{offlineRules});
            Check(offlineRuleWrites==2,"installer v2 counted writes="+offlineRuleWrites);
            // Installer workers can encode a different screen DPI; compare actual glyph pixels across threads.
            Check(EqualPixels(keptPng,File.ReadAllBytes(offlineRuleTile)),"installer v2 removes exact old point and retains same-tile other name pixels");
            Check((int)localize.Invoke(null,new object[]{offlineRules})==1,"installer v2 repeat keeps original legacy redraw count");
            string failedOfflineRules=Path.Combine(root,"offline-rules-failed");Directory.CreateDirectory(failedOfflineRules);File.Copy(Path.Combine(root,"v2.dat"),Path.Combine(failedOfflineRules,"ChineseMapTiles.dat"));
            string failedOfflineTile=Path.Combine(Cache(failedOfflineRules),@"7\64\64.png");Write(failedOfflineTile,blueRule);
            string failedOfflineWwp=Path.Combine(failedOfflineRules,@"WW\PlacenameSets\Cities\0000_0000.wwp");Directory.CreateDirectory(Path.GetDirectoryName(failedOfflineWwp));File.Copy(ruleWwp,failedOfflineWwp);
            using(var held=File.Open(failedOfflineTile,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)) {
                bool failed=false;try { localize.Invoke(null,new object[]{failedOfflineRules}); } catch(TargetInvocationException e) { failed=e.InnerException is AggregateException; }
                Check(failed && Equal(blueRule,File.ReadAllBytes(failedOfflineTile)),"installer v2 failed outside replacement preserves old bytes");
                Check(!File.Exists(Path.Combine(Cache(failedOfflineRules),".chinese-map-tiles.sha256")),"installer v2 cannot complete marker before outside suppression refresh");
            }
            Check((int)localize.Invoke(null,new object[]{failedOfflineRules})==1 && EqualPixels(keptPng,File.ReadAllBytes(failedOfflineTile)),"installer v2 retries exact remaining write after lock closes");
            Check(File.ReadAllText(Path.Combine(Cache(failedOfflineRules),".chinese-map-tiles.sha256")).Trim()==(string)packType.GetProperty("Fingerprint").GetValue(rules,null),"installer v2 publishes marker after successful complete refresh");
            Check((int)localize.Invoke(null,new object[]{offline})==1 && Equal(png,File.ReadAllBytes(Tile(offline))),"installer adds pack coverage when cache and WWP absent");
            Check((int)localize.Invoke(null,new object[]{offline})==0,"installer reports actual writes for unchanged pack");
            string mixed=Path.Combine(root,"installer-mixed"); Directory.CreateDirectory(mixed);
            File.Copy(valid,Path.Combine(mixed,"ChineseMapTiles.dat"));
            string fallbackTile=Path.Combine(Cache(mixed),@"7\109\44.png"); Write(fallbackTile,old);
            string cityWwp=Path.Combine(mixed,@"WW\PlacenameSets\Cities\0000_0000.wwp");
            Directory.CreateDirectory(Path.GetDirectoryName(cityWwp));
            using(var writer=new BinaryWriter(File.Create(cityWwp),Encoding.UTF8)) {
                writer.Write(1); writer.Write("城市"); writer.Write(0f); writer.Write(0f); writer.Write((uint)1);
            }
            bool fallbackFailed=false;
            // This reader permits in-place writes but forbids replacement, distinguishing truncation from atomic publication.
            using(var held=File.Open(fallbackTile,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)) {
                try { localize.Invoke(null,new object[]{mixed}); }
                catch(TargetInvocationException e) { fallbackFailed=e.InnerException is AggregateException; }
                byte[] observed=new byte[checked((int)held.Length)]; held.Read(observed,0,observed.Length);
                Check(Equal(old,observed) && Equal(old,File.ReadAllBytes(fallbackTile)),"installer fallback failure preserves old bytes through shared read handle");
                Check(fallbackFailed,"installer propagates blocked fallback atomic replacement with pack present");
            }
            Check(Equal(png,File.ReadAllBytes(Tile(mixed))) && !File.Exists(Path.Combine(Cache(mixed),".chinese-map-tiles.sha256")),"installer fallback failure cannot publish completion marker");
            Check(Equal(old,File.ReadAllBytes(fallbackTile.Replace("Labels\\","Labels.orig\\"))) && Directory.GetFiles(Cache(mixed),"*.tmp-*",SearchOption.AllDirectories).Length==0,"fallback failure retains original backup and removes temporary PNG");
            Check((int)localize.Invoke(null,new object[]{mixed})==1 && !Equal(old,File.ReadAllBytes(fallbackTile)),"installer retries fallback after reader closes with exact successful save count");
            File.SetLastWriteTimeUtc(fallbackTile,fixedTime);
            Check((int)localize.Invoke(null,new object[]{mixed})==1 && File.GetLastWriteTimeUtc(fallbackTile)>fixedTime,"unchanged fallback retains legacy counted redraw and explicit refresh timestamp");
            string linked=Path.Combine(root,"linked"); Directory.CreateDirectory(Path.Combine(Cache(linked),"3"));
            string junction=Path.Combine(Cache(linked),@"3\4"); string outside=Path.Combine(root,"outside"); Directory.CreateDirectory(outside); Write(Path.Combine(outside,"4.png"),old);
            var start=new System.Diagnostics.ProcessStartInfo("cmd.exe", "/d /c mklink /J \""+junction+"\" \""+outside+"\"") { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
            using(var process=System.Diagnostics.Process.Start(start)) { process.WaitForExit(); Check(process.ExitCode==0,"create fixture junction"); }
            try { try { Apply(pack,linked); throw new Exception("junction must fail"); } catch(TargetInvocationException) {} Check(Equal(old,File.ReadAllBytes(Path.Combine(outside,"4.png"))) && !Directory.Exists(Cache(linked)+".orig"),"cache junction rejected before backup or external write"); }
            finally { Directory.Delete(junction); }
            string broken=Path.Combine(root,"broken"); Directory.CreateDirectory(Path.Combine(Cache(broken),"3"));
            string brokenLink=Path.Combine(Cache(broken),@"3\4"); string absent=Path.Combine(root,"absent-target"); Directory.CreateDirectory(absent);
            start.Arguments="/d /c mklink /J \""+brokenLink+"\" \""+absent+"\"";
            using(var process=System.Diagnostics.Process.Start(start)) { process.WaitForExit(); Check(process.ExitCode==0,"create dangling junction fixture"); }
            Directory.Delete(absent);
            try { try { Apply(pack,broken); throw new Exception("dangling junction must fail"); } catch(TargetInvocationException) {} Check(!Directory.Exists(absent) && !Directory.Exists(Cache(broken)+".orig"),"dangling junction rejected without recreating its target"); }
            finally { Directory.Delete(brokenLink); }
            string runtimeRules=Path.Combine(root,"runtime-rule-failure");Directory.CreateDirectory(runtimeRules);File.Copy(Path.Combine(root,"v2.dat"),Path.Combine(runtimeRules,"ChineseMapTiles.dat"));
            string runtimeOld=Path.Combine(Cache(runtimeRules),@"7\64\64.png");Write(runtimeOld,blueRule);
            string runtimeWwp=Path.Combine(runtimeRules,@"WW\PlacenameSets\Cities\0000_0000.wwp");Directory.CreateDirectory(Path.GetDirectoryName(runtimeWwp));File.Copy(ruleWwp,runtimeWwp);
            using(var held=File.Open(runtimeOld,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)) {
                manager.GetMethod("StartLocalTileServer",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{runtimeRules});
                Check(manager.GetField("tileListener",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null)==null && Equal(blueRule,File.ReadAllBytes(runtimeOld))
                    && !File.Exists(Path.Combine(Cache(runtimeRules),".chinese-map-tiles.sha256")),"runtime outside refresh failure aborts before listener and marker");
            }
            Check(((IList)manager.GetField("mapPlaces",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null)).Count==2,"runtime loads WWP before trying legacy cache refresh");
            string corruptGame=Path.Combine(root,"runtime-corrupt"); Directory.CreateDirectory(corruptGame); File.WriteAllBytes(Path.Combine(corruptGame,"ChineseMapTiles.dat"),new byte[]{1,2,3});
            manager.GetMethod("StartLocalTileServer",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{corruptGame});
            Check(manager.GetField("tileListener",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null)==null && !Directory.Exists(Cache(corruptGame)),"corrupt pack aborts runtime before listener or cache initialization");
            Console.WriteLine("PASS total="+checks); return 0;
        } catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
