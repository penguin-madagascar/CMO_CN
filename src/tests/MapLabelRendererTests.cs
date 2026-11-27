using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

// Uses the actual engine DLL; the baseline path remains usable for a red/green comparison.
public static class MapLabelRendererTests
{
    private static Assembly engine;
    private static int assertions;
    private static IList lastPlaces;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        assertions++;
        Console.WriteLine("PASS " + message);
    }

    private static object Place(Type type, string name, int z, int x, int y, float px, float py, int level)
    {
        double n = Math.Pow(2, z);
        object place = Activator.CreateInstance(type);
        type.GetField("Name").SetValue(place, name);
        type.GetField("Lat").SetValue(place, Convert.ChangeType(
            Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (y + py / 256.0) / n))) * 180 / Math.PI,
            type.GetField("Lat").FieldType));
        type.GetField("Lon").SetValue(place, Convert.ChangeType((x + px / 256.0) / n * 360 - 180,
            type.GetField("Lon").FieldType));
        type.GetField("Level").SetValue(place, level);
        return place;
    }

    private static Bitmap Render(string[] names, float[] xs, float[] ys, int[] levels,
        int z, float pixels, out int candidates, out int rendered)
    {
        int x = z == 0 ? 0 : 1 << (z - 1), y = x;
        Type shared = engine.GetType("CMOShared.MapLabelRenderer");
        Type manager = engine.GetType("CMOLocalizer.CustomAppDomainManager", true);
        IList places;
        Type placeType;
        if (shared == null)
        {
            places = (IList)manager.GetField("mapPlaces", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            places.Clear();
            placeType = places.GetType().GetGenericArguments()[0];
        }
        else
        {
            placeType = engine.GetType("CMOShared.MapLabelPlace", true);
            places = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(placeType));
        }
        for (int i = 0; i < names.Length; i++) places.Add(Place(placeType, names[i], z, x, y, xs[i], ys[i], levels[i]));
        lastPlaces = places;
        if (shared == null)
        {
            object[] args = { z, x, y, 0, 0 };
            byte[] bytes = (byte[])manager.GetMethod("RenderChineseTileBytes", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);
            candidates = (int)args[3]; rendered = (int)args[4];
            using (var stream = new MemoryStream(bytes)) using (var image = new Bitmap(stream)) return new Bitmap(image);
        }
        object[] parameters = { places, z, x, y, 0, 0, pixels };
        foreach (MethodInfo method in shared.GetMethods())
            if (method.Name == "RenderTile" && method.GetParameters().Length == parameters.Length)
            {
                Bitmap result = (Bitmap)method.Invoke(null, parameters);
                candidates = (int)parameters[4]; rendered = (int)parameters[5];
                return result;
            }
        throw new Exception("Shared renderer style overload is missing");
    }

    private static Rectangle Bounds(Bitmap image, bool fillOnly)
    {
        int left = image.Width, top = image.Height, right = -1, bottom = -1;
        for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
        {
            Color color = image.GetPixel(x, y);
            if (color.A == 0 || (fillOnly && (color.A < 200 || color.R < 200 || color.G < 200 || color.B < 200))) continue;
            left = Math.Min(left, x); top = Math.Min(top, y);
            right = Math.Max(right, x); bottom = Math.Max(bottom, y);
        }
        return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static bool SamePixels(Bitmap first, Bitmap second)
    {
        for (int y = 0; y < first.Height; y++) for (int x = 0; x < first.Width; x++)
            if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb()) return false;
        return true;
    }

    private static byte[] CatalogBytes(string name, int minZoom, int maxZoom, int priority, int level)
    {
        byte[] payload;
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(1);
                writer.Write(name);
                writer.Write(-5f); writer.Write(5f);
                writer.Write(minZoom); writer.Write(maxZoom); writer.Write(priority); writer.Write(level);
            }
            payload = stream.ToArray();
        }
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            using (var hash = SHA256.Create())
            {
                writer.Write(Encoding.ASCII.GetBytes("CMOMAP01"));
                writer.Write(BitConverter.ToString(hash.ComputeHash(payload)).Replace("-", "").ToLowerInvariant());
                writer.Write(payload);
            }
            return stream.ToArray();
        }
    }

    private static object LoadCatalog(byte[] bytes)
    {
        Type type = engine.GetType("CMOShared.MapLabelCatalog", true);
        string path = Path.Combine(Path.GetDirectoryName(engine.Location), "catalog-test-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(path, bytes);
            return type.GetMethod("Load", new[] { typeof(string) }).Invoke(null, new object[] { path });
        }
        finally { File.Delete(path); }
    }

    private static void RejectCatalog(byte[] bytes, string message)
    {
        try { LoadCatalog(bytes); }
        catch (TargetInvocationException ex)
        {
            Check(ex.InnerException is InvalidDataException || ex.InnerException is EndOfStreamException,
                message);
            return;
        }
        throw new Exception("Catalog accepted: " + message);
    }

    private static Bitmap RenderPlaces(IList places, int z, out int candidates, out int rendered)
    {
        Type renderer = engine.GetType("CMOShared.MapLabelRenderer", true);
        object[] parameters = { places, z, 1 << (z - 1), 1 << (z - 1), 0, 0 };
        foreach (MethodInfo method in renderer.GetMethods())
            if (method.Name == "RenderTile" && method.GetParameters().Length == parameters.Length)
            {
                Bitmap result = (Bitmap)method.Invoke(null, parameters);
                candidates = (int)parameters[4]; rendered = (int)parameters[5];
                return result;
            }
        throw new Exception("Shared renderer overload is missing");
    }

    private static void CatalogTests()
    {
        Type catalogType = engine.GetType("CMOShared.MapLabelCatalog");
        Check(catalogType != null, "shared map catalog loader is available");
        object catalog = LoadCatalog(CatalogBytes("得克萨斯州", 3, 10, 20, 0));
        IList places = (IList)catalogType.GetProperty("Places").GetValue(catalog, null);
        string fingerprint = (string)catalogType.GetProperty("Fingerprint").GetValue(catalog, null);
        Check(places.Count == 1 && fingerprint.Length == 64,
            "a supplemental name absent from WWP loads with its payload fingerprint");
        int candidates, rendered;
        using (Bitmap image = RenderPlaces(places, 5, out candidates, out rendered))
            Check(candidates == 1 && rendered == 1 && !Bounds(image, false).IsEmpty,
                "a supplemental state name renders at its declared zoom");
        object cityCatalog = LoadCatalog(CatalogBytes("基西", 3, 5, 10, 1));
        IList cities = (IList)catalogType.GetProperty("Places").GetValue(cityCatalog, null);
        using (Bitmap image = RenderPlaces(cities, 5, out candidates, out rendered))
            Check(candidates == 1 && rendered == 1, "a catalog city can render below legacy city zoom");
        using (Bitmap image = RenderPlaces(cities, 6, out candidates, out rendered))
            Check(candidates == 0 && rendered == 0, "catalog maximum zoom excludes a city after its range");
        object laterCatalog = LoadCatalog(CatalogBytes("基西", 6, 10, 10, 1));
        using (Bitmap image = RenderPlaces((IList)catalogType.GetProperty("Places").GetValue(laterCatalog, null), 5,
            out candidates, out rendered))
            Check(candidates == 0 && rendered == 0, "catalog minimum zoom excludes a city before its range");
        var mixed = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(places[0].GetType()));
        mixed.Add(places[0]); mixed.Add(cities[0]);
        using (Bitmap image = RenderPlaces(mixed, 5, out candidates, out rendered))
        using (Bitmap winner = RenderPlaces(cities, 5, out candidates, out rendered))
            Check(SamePixels(image, winner), "catalog priority wins collisions before label level");
        byte[] corrupt = CatalogBytes("基西", 3, 10, 10, 1);
        corrupt[corrupt.Length - 1] ^= 1;
        RejectCatalog(corrupt, "corrupt payload fails its SHA256 fingerprint");
        byte[] badHeader = CatalogBytes("基西", 3, 10, 10, 1);
        badHeader[0] = 0;
        RejectCatalog(badHeader, "an unknown map catalog header is rejected");
        RejectCatalog(CatalogBytes("", 3, 10, 10, 1), "missing Chinese name is rejected");
        RejectCatalog(CatalogBytes("Kisii", 3, 10, 10, 1), "English fallback name is rejected");
        RejectCatalog(CatalogBytes("基西 Kisii", 3, 10, 10, 1), "mixed English fallback name is rejected");
        RejectCatalog(CatalogBytes("第\U0001FBF0城", 3, 10, 10, 1), "an unsupported Unicode digit is rejected");
        RejectCatalog(CatalogBytes("第\U0001E95E城", 3, 10, 10, 1), "an unsupported Unicode punctuation mark is rejected");
        RejectCatalog(CatalogBytes("第¹城", 3, 10, 10, 1), "a number outside the fixed digit set is rejected");
        foreach (string name in new[] { "第1城（第２城）·〇镇", "第〡城" })
        {
            object digits = LoadCatalog(CatalogBytes(name, 3, 10, 10, 0));
            IList digitPlaces = (IList)catalogType.GetProperty("Places").GetValue(digits, null);
            Check((string)digitPlaces[0].GetType().GetField("Name").GetValue(digitPlaces[0]) == name,
                "the fixed shared digits and Chinese punctuation load: " + name);
        }
        // Unicode 15 is fixed by the catalog contract, independently of the CLR Unicode tables.
        foreach (string name in new[] { "\u9FFF山", "\U00030000山", "\U00031350山", "\U0002B739山" })
        {
            object assigned = LoadCatalog(CatalogBytes(name, 3, 10, 10, 0));
            IList assignedPlaces = (IList)catalogType.GetProperty("Places").GetValue(assigned, null);
            Check((string)assignedPlaces[0].GetType().GetField("Name").GetValue(assignedPlaces[0]) == name,
                "assigned Unicode 15 Han loads without depending on CLR category tables: " + name);
        }
        RejectCatalog(CatalogBytes("汉\U0002FFFE", 3, 10, 10, 1), "an unassigned supplementary code point is rejected");
        RejectCatalog(CatalogBytes("汉\uFA6E", 3, 10, 10, 1), "an unassigned compatibility code point is rejected");
        RejectCatalog(CatalogBytes("汉\U0002EE5E", 3, 10, 10, 1), "an unassigned extension code point is rejected");
        RejectCatalog(CatalogBytes("基西", 6, 5, 10, 1), "an inverted catalog zoom range is rejected");
        byte[] trailing = CatalogBytes("基西", 3, 10, 10, 1);
        Array.Resize(ref trailing, trailing.Length + 1);
        RejectCatalog(trailing, "trailing map catalog bytes are rejected");
    }

    // Exercise deployed binary records through the same loader and PNG entry point as the game.
    private static void ReleasedWwpTests(string assetDirectory, string packPath)
    {
        int[] indices = { 1203, 2419, 2679, 2684 };
        string[] source = { "Tutamanda旅馆", "调布ケ丘", "古斯塔沃·A.马德罗", "古斯塔沃·A·马德罗" };
        string[] display = { "途塔曼达旅馆", "调布丘", "古斯塔沃·阿道夫·马德罗", "古斯塔沃·阿道夫·马德罗" };
        string scope = Path.Combine(Path.GetTempPath(), "wwp-display-" + Guid.NewGuid().ToString("N"));
        string gameFile = Path.Combine(scope, @"WW\PlacenameSets\Cities\0000_0000.wwp");
        string asset = Path.Combine(assetDirectory, "Cities.wwp");
        Directory.CreateDirectory(Path.GetDirectoryName(gameFile));
        try
        {
            File.Copy(asset, gameFile);
            MethodInfo load = engine.GetType("CMOShared.MapLabelCatalog", true).GetMethod("LoadBasePlaces");
            IList places = (IList)load.Invoke(null, new object[] { scope });
            Check(places.Count == 4287, "all shipped Cities records load through the runtime");
            Type type = places[0].GetType();
            FieldInfo name = type.GetField("Name");
            PropertyInfo printedName = type.GetProperty("DisplayName");
            int changed = 0;
            foreach (object place in places)
                if (!Equals(name.GetValue(place), printedName.GetValue(place, null))) changed++;
            Check(changed == indices.Length, "only the four reviewed WWP names change on screen");
            MethodInfo render = engine.GetType("CMOShared.MapLabelRenderer", true).GetMethod("RenderTileBytes");
            object pack = engine.GetType("CMOShared.MapLabelTilePack", true).GetMethod("Load").Invoke(null, new object[] { packPath });
            MethodInfo tryTile = pack.GetType().GetMethod("TryGetTile");
            for (int i = 0; i < indices.Length; i++)
            {
                object place = places[indices[i]];
                Check(Equals(name.GetValue(place), source[i]) && Equals(printedName.GetValue(place, null), display[i]),
                    "retained source identity and reviewed display at Cities index " + indices[i]);
                float lat = (float)type.GetField("Lat").GetValue(place), lon = (float)type.GetField("Lon").GetValue(place);
                double rad = lat * Math.PI / 180;
                int z = 9, x, y;
                while (true)
                {
                    double tiles = Math.Pow(2, z);
                    x = (int)((lon + 180.0) / 360 * tiles);
                    y = (int)((1 - Math.Log(Math.Tan(rad) + 1 / Math.Cos(rad)) / Math.PI) / 2 * tiles);
                    if (!(bool)tryTile.Invoke(pack, new object[] { z, x, y, null, 0 })) break;
                    if (++z > 30) throw new Exception("No unpacked host for WWP fallback test");
                }
                IList single = (IList)Activator.CreateInstance(places.GetType());
                single.Add(place);
                object[] renderArgs = { single, pack, z, x, y, 0, 0 };
                byte[] actual = (byte[])render.Invoke(null, renderArgs);
                Check((int)renderArgs[5] == 1 && (int)renderArgs[6] == 1, "released pack miss draws index " + indices[i] + " at zoom " + z);
                renderArgs[1] = null;
                object reference = Activator.CreateInstance(type);
                foreach (string field in new[] { "Lat", "Lon", "Level" })
                    type.GetField(field).SetValue(reference, type.GetField(field).GetValue(place));
                single[0] = reference;
                foreach (string referenceName in new[] { display[i], source[i] })
                {
                    name.SetValue(reference, referenceName);
                    byte[] expected = (byte[])render.Invoke(null, renderArgs);
                    using (var a = new MemoryStream(actual))
                    using (var b = new MemoryStream(expected))
                    using (var got = new Bitmap(a))
                    using (var want = new Bitmap(b))
                        Check(SamePixels(got, want) == (referenceName == display[i]),
                            "compared actual WWP glyphs to " + referenceName);
                }
            }

            // Each near match differs by one identity component. A duplicate of the unmodified
            // first record is appended at a new index to exercise the index separately.
            using (var reader = new BinaryReader(File.OpenRead(asset), Encoding.UTF8))
            using (var writer = new BinaryWriter(File.Create(gameFile), Encoding.UTF8))
            {
                int count = reader.ReadInt32();
                writer.Write(count + 1);
                byte[] duplicate = null;
                for (int i = 0; i < count; i++)
                {
                    long start = reader.BaseStream.Position;
                    string text = reader.ReadString();
                    int latBits = reader.ReadInt32(), lonBits = reader.ReadInt32();
                    uint flags = reader.ReadUInt32();
                    if (i == indices[0])
                    {
                        int size = (int)(reader.BaseStream.Position - start);
                        reader.BaseStream.Position = start;
                        duplicate = reader.ReadBytes(size);
                        text += "甲";
                    }
                    if (i == indices[1]) latBits ^= 1;
                    if (i == indices[2]) lonBits ^= 1;
                    if (i == indices[3]) flags ^= 1;
                    writer.Write(text); writer.Write(latBits); writer.Write(lonBits); writer.Write(flags);
                }
                writer.Write(duplicate);
            }
            IList nearMatches = (IList)load.Invoke(null, new object[] { scope });
            Check(nearMatches.Count == places.Count + 1, "near-match WWP records remain readable");
            foreach (object item in nearMatches)
                if (!Equals(name.GetValue(item), printedName.GetValue(item, null)))
                    throw new Exception("Unexpected display override for near match " + name.GetValue(item));
            Check(Equals(name.GetValue(nearMatches[places.Count]), source[0]), "same record at another index retains its source name");

            object[] packed = { 7, 28, 55, null, 0 };
            Check((bool)tryTile.Invoke(pack, packed) && (int)packed[4] > 0,
                "released pack contains the reviewed Tamaulipas tile");
            IList madero = (IList)Activator.CreateInstance(places.GetType());
            madero.Add(places[2679]);
            Check((bool)pack.GetType().GetMethod("AlreadyInPack", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(pack, new object[] { places[2679], 7 }), "the original WWP key still resolves to its packed caption");
            object[] drawPacked = { madero, pack, 7, 28, 55, 0, 0 };
            byte[] rendered = (byte[])render.Invoke(null, drawPacked);
            Check((int)drawPacked[6] == (int)packed[4] && System.Linq.Enumerable.SequenceEqual(rendered, (byte[])packed[3]),
                "no duplicate glyphs or labels on the packed Tamaulipas tile");
        }
        finally { Directory.Delete(scope, true); }
    }

    public static int Main(string[] args)
    {
        try
        {
            engine = Assembly.LoadFrom(args[0]);
            if (args.Length == 4 && args[1] == "--released-wwp")
            {
                ReleasedWwpTests(args[2], args[3]);
                return 0;
            }
            int candidates, rendered;
            using (Bitmap image = Render(new[] { "鸟取" }, new[] { 128f }, new[] { 128f }, new[] { 1 }, 8, 14f,
                out candidates, out rendered))
            {
                Rectangle ink = Bounds(image, true);
                Console.WriteLine("CITY_FILL " + ink.Width + "x" + ink.Height);
                Check(image.Width == 256 && image.Height == 256 && candidates == 1 && rendered == 1,
                    "single city uses a fixed 256px tile");
                Check(ink.Height >= 11, "city fill has at least 11 actual pixels of glyph height");
                Check(Math.Abs(ink.Left + ink.Width / 2f - 128) <= 1.5f
                    && Math.Abs(ink.Top + ink.Height / 2f - 128) <= 1.5f, "actual glyph bounds center on the source coordinate");
                object original = lastPlaces[0];
                Check((string)original.GetType().GetField("Name").GetValue(original) == "鸟取"
                    && lastPlaces.Count == 1, "rendering retains source names and record count");
                Type manager = engine.GetType("CMOLocalizer.CustomAppDomainManager", true);
                IList runtime = (IList)manager.GetField("mapPlaces", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                runtime.Clear(); runtime.Add(original);
                object[] parameters = { 8, 128, 128, 0, 0 };
                byte[] bytes = (byte[])manager.GetMethod("RenderChineseTileBytes", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, parameters);
                using (var stream = new MemoryStream(bytes)) using (var runtimeImage = new Bitmap(stream))
                    Check(SamePixels(image, runtimeImage) && (int)parameters[3] == 1 && (int)parameters[4] == 1,
                        "runtime PNG pixels and counts equal the default shared renderer");
            }
            int previousHeight = 0;
            foreach (float pixels in new[] { 12f, 14f, 16f })
                using (Bitmap image = Render(new[] { "松江" }, new[] { 128f }, new[] { 128f }, new[] { 1 }, 8, pixels,
                    out candidates, out rendered))
                {
                    int height = Bounds(image, true).Height;
                    Check(height > previousHeight && rendered == 1, pixels + "px styles increase actual glyph height");
                    previousHeight = height;
                }
            foreach (float px in new[] { 0.1f, 255.9f }) foreach (float py in new[] { 0.1f, 255.9f })
                using (Bitmap image = Render(new[] { "杨州" }, new[] { px }, new[] { py }, new[] { 1 }, 8, 14f,
                    out candidates, out rendered))
                {
                    Rectangle all = Bounds(image, false);
                    Check(rendered == 1 && all.Left > 0 && all.Top > 0 && all.Right < 256 && all.Bottom < 256,
                        "halo remains inside the tile at corner " + px + "," + py);
                }
            string longPlace = "阿伊尔伊阿·阿斯特尔·富萨诺·奥斯蒂阿·安蒂阿";
            using (Bitmap image = Render(new[] { longPlace }, new[] { 128f }, new[] { 128f }, new[] { 1 }, 8, 14f,
                out candidates, out rendered))
            {
                Rectangle all = Bounds(image, false);
                Console.WriteLine("LONG candidates=" + candidates + " rendered=" + rendered + " bounds=" + all);
                Check(candidates == 1 && rendered == 1 && all.Left > 0 && all.Right < 256,
                    "the existing longest place name remains visible and fits inside its tile");
                Check((string)lastPlaces[0].GetType().GetField("Name").GetValue(lastPlaces[0]) == longPlace,
                    "fitting a long place name preserves its complete source text");
            }
            using (Bitmap image = Render(new[] { new string('超', 100) }, new[] { 128f }, new[] { 128f }, new[] { 1 }, 8, 14f,
                out candidates, out rendered))
            {
                Rectangle all = Bounds(image, false);
                Check(candidates == 1 && rendered == 1 && !all.IsEmpty && all.Left > 0 && all.Right < 256,
                    "an extreme name fits its complete path inside the tile without clipping");
            }
            using (Bitmap dense = Render(new[] { "鸟取", "米子", "松江" }, new[] { 128f, 128f, 128f },
                new[] { 128f, 128f, 128f }, new[] { 1, 1, 1 }, 8, 14f, out candidates, out rendered))
            {
                Check(candidates == 3 && rendered == 1, "colliding city labels cannot overlap");
                using (Bitmap first = Render(new[] { "鸟取" }, new[] { 128f }, new[] { 128f }, new[] { 1 }, 8, 14f,
                    out candidates, out rendered)) Check(SamePixels(dense, first), "equal-level collision winners preserve WWP input order");
            }
            using (Bitmap mixed = Render(new[] { "鸟取", "日本" }, new[] { 128f, 128f }, new[] { 128f, 128f },
                new[] { 1, 0 }, 8, 14f, out candidates, out rendered))
            {
                Check(candidates == 2 && rendered == 1, "high zoom retains both synoptic and city candidates");
                using (Bitmap first = Render(new[] { "日本" }, new[] { 128f }, new[] { 128f }, new[] { 0 }, 8, 14f,
                    out candidates, out rendered)) Check(SamePixels(mixed, first), "synoptic labels retain priority before cities");
            }
            using (Bitmap image = Render(new[] { "鸟取", "日本" }, new[] { 64f, 192f }, new[] { 128f, 128f },
                new[] { 1, 0 }, 5, 14f, out candidates, out rendered))
                Check(candidates == 1 && rendered == 1, "low zoom excludes city candidates");
            using (Bitmap image = Render(new[] { "鸟取" }, new[] { 128f }, new[] { 128f }, new[] { 1 }, 30, 14f,
                out candidates, out rendered)) Check(candidates == 1 && rendered == 1, "offline zoom 30 remains supported");
            using (Bitmap image = Render(new string[0], new float[0], new float[0], new int[0], 8, 14f,
                out candidates, out rendered)) Check(candidates == 0 && rendered == 0 && Bounds(image, false).IsEmpty,
                    "empty tiles stay transparent");
            CatalogTests();
            if (args.Length > 1)
            {
                object pythonCatalog = LoadCatalog(File.ReadAllBytes(args[1]));
                IList pythonPlaces = (IList)pythonCatalog.GetType().GetProperty("Places").GetValue(pythonCatalog, null);
                Check(pythonPlaces.Count == 5
                    && (string)pythonPlaces[0].GetType().GetField("Name").GetValue(pythonPlaces[0]) == "得克萨斯州"
                    && (string)pythonPlaces[1].GetType().GetField("Name").GetValue(pythonPlaces[1]) == "基西"
                    && (string)pythonPlaces[2].GetType().GetField("Name").GetValue(pythonPlaces[2]) == "\u9FFF山"
                    && (string)pythonPlaces[3].GetType().GetField("Name").GetValue(pythonPlaces[3]) == "\U00030000山"
                    && (string)pythonPlaces[4].GetType().GetField("Name").GetValue(pythonPlaces[4]) == "第1城（第２城）·〇镇",
                    "Python compiled catalog, Unicode 15 Han and fixed digits/punctuation load in C#");
                for (int i = 2; i < args.Length; i++)
                    RejectCatalog(File.ReadAllBytes(args[i]), "Python negative fixture is rejected: " + Path.GetFileName(args[i]));
            }
            Console.WriteLine("ASSERTIONS=" + assertions);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
