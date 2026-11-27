using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

// Runs as the cached installer child. All writes require a game directory created by the test.
internal static class MapLabelRollbackFixture
{
    private static void Extract(Assembly setup, string resource, string target)
    {
        using (Stream data = setup.GetManifestResourceStream(resource))
        using (Stream destination = File.Create(target))
            data.CopyTo(destination);
    }

    private static string Digest(string path)
    {
        using (var hash = SHA256.Create())
        using (var stream = File.OpenRead(path))
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    private static int Main(string[] args)
    {
        if (args.Length != 3 || args[0] != "--install" || args[2] != "--silent") return 2;
        string game = Path.GetFullPath(args[1]);
        string command = Path.Combine(game, "rollback-test-mode.txt");
        if (!File.Exists(command)) return 2;
        string mode = File.ReadAllText(command);
        string map = Path.Combine(game, "ChineseMapTiles.dat");
        string held = map + ".rollback";
        File.WriteAllText(Path.Combine(game, "before-child.txt"),
            (File.Exists(map) ? Digest(map) : "-") + "\n" + (File.Exists(held) ? Digest(held) : "-"));

        Assembly setup = Assembly.GetExecutingAssembly();
        foreach (string file in new[] { "CMOLocalizer.dll", "CMOLocalizer.json" })
            Extract(setup, file, Path.Combine(game, file));
        if (Array.IndexOf(setup.GetManifestResourceNames(), "MAP_TILES") >= 0)
            Extract(setup, "MAP_TILES", map);
        else
        {
            string sidecar = setup.Location + "." + Digest(setup.Location) + ".map.dat";
            if (File.Exists(sidecar)) File.Copy(sidecar, map, true);
        }
        switch (mode)
        {
            case "corrupt-engine": File.WriteAllText(Path.Combine(game, "CMOLocalizer.dll"), "wrong DLL"); break;
            case "corrupt-map": File.WriteAllText(map, "wrong DAT"); break;
            case "omit-map": File.Delete(map); break;
            case "late-file": File.WriteAllText(map, "unrelated replacement"); break;
            case "changed-held": File.WriteAllText(held, "modified staging file"); break;
            case "child-failure": return 1;
        }
        return 0;
    }
}
