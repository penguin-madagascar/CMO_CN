using System;
using System.Drawing;
using System.IO;
using System.Linq;
using CMOShared;

internal static class MapLabelHistorySmoke
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidDataException("Historical map regression: " + message);
    }

    // Resolve the caption at its published location; array positions can change when new names are added.
    private static int Label(MapLabelIndex map, HistoryTrace.Tile tile, int z, int x, int y, string text)
    {
        var marks = map.Marks(z, x, y)
            .Where(m => !m.Symbol && string.Join("\n", m.Lines) == text).ToList();
        string host = string.Format("{0}/{1}/{2} {3}", z, x, y, text);
        Require(marks.Count == 1, host + " must have one caption");
        int draws;
        Require(tile.Labels.TryGetValue(marks[0].Id, out draws) && draws == 1, host + " must paint once");
        return marks[0].Id;
    }

    private static void Legacy(HistoryTrace.Tile tile, string member, int expected, string host)
    {
        int actual;
        tile.Legacy.TryGetValue(member, out actual);
        Require(actual == expected, string.Format("{0} {1}: expected {2} draws, got {3}", host, member, expected, actual));
    }

    private static Bitmap Pixels(HistoryTrace.Tile tile)
    {
        using (var stream = new MemoryStream(tile.Png, false))
        using (var png = new Bitmap(stream))
        {
            Require(png.Width == 256 && png.Height == 256, "renderer returned a non-tile image");
            return new Bitmap(png);
        }
    }

    private static void CheckSeam(MapLabelIndex map)
    {
        // The single original Majuro caption straddles the tile edge near y=130.
        var left = HistoryTrace.Draw(map, 7, 124, 61);
        var right = HistoryTrace.Draw(map, 7, 125, 61);
        int a = Label(map, left, 7, 124, 61, "马朱罗");
        int b = Label(map, right, 7, 125, 61, "马朱罗");
        Require(a == b, "Majuro was re-created separately on either side of the seam");
        using (var l = Pixels(left))
        using (var r = Pixels(right))
        {
            int joinedRows = 0;
            for (int y = 120; y < 139; ++y)
                if (l.GetPixel(255, y).A >= 64 && r.GetPixel(0, y).A >= 64) ++joinedRows;
            Require(joinedRows >= 4, "Majuro strokes no longer meet at 7/124–125/61");
        }
        // This city circle touches the next tile at x=0. GDI+ paints part of its rim
        // there although the nominal seven-pixel shape ends exactly at the boundary.
        using (var next = Pixels(HistoryTrace.Draw(map, 4, 1, 7)))
            Require(next.GetPixel(0, 7).A >= 64 && next.GetPixel(0, 8).A >= 64,
                    "City symbol lost its antialiased rim at 4/1/7");
    }

    private static void CheckHistoricalDuplicates(MapLabelIndex map)
    {
        var gulf = HistoryTrace.Draw(map, 10, 817, 455);
        Label(map, gulf, 10, 817, 455, "北部湾");
        Legacy(gulf, "Synoptic:252", 0, "10/817/455");
        Legacy(HistoryTrace.Draw(map, 10, 819, 453), "Synoptic:252", 0, "10/819/453");
        Legacy(HistoryTrace.Draw(map, 9, 409, 226), "Synoptic:252", 1, "9/409/226");
        Legacy(HistoryTrace.Draw(map, 11, 1638, 907), "Synoptic:252", 1, "11/1638/907");

        var city = HistoryTrace.Draw(map, 9, 414, 215);
        Label(map, city, 9, 414, 215, "冷水江");
        Legacy(city, "Cities:735", 0, "9/414/215");
        Legacy(HistoryTrace.Draw(map, 9, 414, 214), "Cities:735", 0, "9/414/214");
    }

    private static void CheckNamesAndZooms(MapLabelIndex map)
    {
        // Two cities romanized Jining, and two separate upstream provinces both named 中央省.
        var innerMongolia = HistoryTrace.Draw(map, 7, 104, 47);
        var shandong = HistoryTrace.Draw(map, 7, 105, 50);
        Require(Label(map, innerMongolia, 7, 104, 47, "集宁") != Label(map, shandong, 7, 105, 50, "济宁"),
                "Jining in Inner Mongolia and Shandong resolved to the same label");
        Legacy(innerMongolia, "Cities:904", 0, "7/104/47");
        Legacy(shandong, "Cities:749", 0, "7/105/50");
        Legacy(HistoryTrace.Draw(map, 8, 203, 89), "Regions:365", 1, "8/203/89");
        Legacy(HistoryTrace.Draw(map, 8, 148, 138), "Regions:497", 1, "8/148/138");

        // Bali and Altay retain the upstream coordinates and appear as reviewed captions only at z6.
        CheckProvince(map, 193, "巴厘省", 52, 33, 26, 16, 104, 66);
        CheckProvince(map, 338, "阿尔泰边疆区", 46, 20, 23, 10, 93, 42);
        var tibet = HistoryTrace.Draw(map, 10, 765, 429);
        Label(map, tibet, 10, 765, 429, "帕里");
        Label(map, tibet, 10, 765, 429, "卓木拉日峰\n7326米");
    }

    private static void CheckProvince(MapLabelIndex map, int index, string name, int x6, int y6,
                                      int x5, int y5, int x7, int y7)
    {
        var p = map.Base.Single(place => place.SourceLayer == "Regions" && place.SourceIndex == index);
        Require(p.DisplayName == name, name + " changed in the upstream province roster");
        string key = "Regions:" + index;
        var printed = HistoryTrace.Draw(map, 6, x6, y6);
        Label(map, printed, 6, x6, y6, name);
        Legacy(printed, key, 0, name + " z6");
        Legacy(HistoryTrace.Draw(map, 5, x5, y5), key, 1, name + " z5");
        Legacy(HistoryTrace.Draw(map, 7, x7, y7), key, 1, name + " z7");
        int x9 = (int)Math.Floor(MapLabelIndex.Left(p.Lon, 9) / 256);
        int y9 = (int)Math.Floor(MapLabelIndex.Top(p.Lat, 9) / 256);
        Legacy(HistoryTrace.Draw(map, 9, x9, y9), key, 0, name + " beyond z8");
    }

    public static void Run(string repo, string game)
    {
        Run(MapLabelIndex.Load(game, Path.Combine(repo, "dict", "CMOLocalizer.json")));
    }

    public static void Run(MapLabelIndex map)
    {
        CheckSeam(map);
        CheckHistoricalDuplicates(map);
        CheckNamesAndZooms(map);
    }
}
