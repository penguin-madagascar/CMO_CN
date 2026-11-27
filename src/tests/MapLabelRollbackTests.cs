using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CMOShared;

internal static class MapLabelRollbackTests
{
    private const string Map = "ChineseMapTiles.dat";
    private const string Plain = "2.0.0", Embedded = "2.0.1", Sidecar = "2.0.2";
    private const string OldVersion = "9.9.9";
    private static string sandbox;

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidDataException(message);
    }

    private static string Hash(string value)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
    }

    private static string FileHash(string file) { return InstallManifest.Sha256OfFile(file); }
    private static string MapPath(string game) { return Path.Combine(game, Map); }
    private static string Marker(string game, string filename)
    {
        return Path.Combine(game, @"WW\Cache\Stamen Terrain - Labels", filename);
    }

    private static string NewGame(string name, string mode)
    {
        string game = Path.Combine(sandbox, name);
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "rollback-test-mode.txt"), mode);
        foreach (string nameInGame in InstallManifest.EngineFiles)
            File.WriteAllText(Path.Combine(game, nameInGame), "original " + nameInGame);
        Check(InstallManifest.Write(game, OldVersion, "previous setup", InstallManifest.HashEngineFiles(game)), name);
        return game;
    }

    private static string SeedLegacy(string game)
    {
        const string payload = "old map payload";
        File.WriteAllText(MapPath(game), "CMOTIL02@" + Hash(payload) + payload, new UTF8Encoding(false));
        string old = FileHash(MapPath(game));
        var files = InstallManifest.HashEngineFiles(game);
        files[Map] = old;
        Check(InstallManifest.Write(game, OldVersion, "previous setup", files), "Could not seed old manifest");
        string marker = Marker(game, ".chinese-map-tiles.sha256");
        Directory.CreateDirectory(Path.GetDirectoryName(marker));
        File.WriteAllText(marker, Hash(payload) + "\n");
        string body = Hash("inputs") + "\n9/298/198\t" + Hash("old tile") + "\n";
        File.WriteAllText(Marker(game, ".chinese-map-rendered.sha256"), Hash(body) + "\n" + body);
        return old;
    }

    private static void Run(string game, string target, bool success)
    {
        string error;
        bool ok = Rollback.Run(game, target, true, delegate { }, out error);
        Check(ok == success, Path.GetFileName(game) + " returned " + ok + ": " + error);
        if (!success) Check(!string.IsNullOrEmpty(error), "Failed rollback omitted its error");
    }

    private static void TargetMatches(string target)
    {
        string game = NewGame("match-" + target, "success");
        Run(game, target, true);
        InstallManifest manifest = InstallManifest.Read(game);
        bool hasMap = target != Plain;
        Check(manifest.Version == target && manifest.Files.Count == (hasMap ? 3 : 2), "Incorrect manifest " + target);
        string pkg = VersionStore.CachedPackage(target);
        foreach (string file in InstallManifest.EngineFiles)
        {
            string expected = PackageAssets.EmbeddedSha256(pkg, file);
            Check(manifest.Files[file] == expected && FileHash(Path.Combine(game, file)) == expected, file);
        }
        if (hasMap)
        {
            string expected = target == Embedded ? PackageAssets.EmbeddedSha256(pkg, "MAP_TILES")
                : FileHash(pkg + "." + FileHash(pkg) + ".map.dat");
            Check(manifest.Files[Map] == expected && FileHash(MapPath(game)) == expected, "Wrong map " + target);
        }
        else Check(!File.Exists(MapPath(game)), "Plain target unexpectedly installed a map");
    }

    private static void RejectBadOutput(string target, string mode)
    {
        string game = NewGame("reject-" + target + "-" + mode, mode);
        Run(game, target, false);
        Check(InstallManifest.Read(game).Version == OldVersion, "Registered the wrong installed output: " + mode);
    }

    private static void RetireOrRestore(string target, bool fail)
    {
        string game = NewGame("legacy-" + target + "-" + fail, fail ? "child-failure" : "success");
        string original = SeedLegacy(game), map = MapPath(game);
        string originalManifest = File.ReadAllText(CmoPaths.ManifestPath(game));
        Run(game, target, !fail);
        Check(File.ReadAllText(Path.Combine(game, "before-child.txt")) == "-\n" + original,
            "Child started before old DAT was safely staged");
        Check(!File.Exists(map + ".rollback"), "Finished operation left a staged map");
        if (fail)
        {
            Check(FileHash(map) == original && File.ReadAllText(CmoPaths.ManifestPath(game)) == originalManifest,
                "Failed child did not restore the exact original files");
            Check(File.Exists(Marker(game, ".chinese-map-tiles.sha256")), "Failed rollback cleared old evidence");
        }
        else
        {
            Check(!File.Exists(map) && InstallManifest.Read(game).Files.Count == 2, "Managed map survived retirement");
            Check(!File.Exists(Marker(game, ".chinese-map-tiles.sha256"))
                && !File.Exists(Marker(game, ".chinese-map-rendered.sha256")), "Managed markers survived retirement");
        }
    }

    private static void ProtectExistingData()
    {
        string modified = NewGame("altered-managed", "success");
        SeedLegacy(modified);
        File.WriteAllText(MapPath(modified), "user modification");
        Run(modified, Plain, false);
        Check(File.ReadAllText(MapPath(modified)) == "user modification"
            && !File.Exists(Path.Combine(modified, "before-child.txt")), "Ran child over a modified map");

        foreach (string target in new[] { Plain, Embedded })
        {
            string game = NewGame("unowned-" + target, "success");
            File.WriteAllText(MapPath(game), "unmanaged file");
            Run(game, target, target == Plain);
            Check(File.ReadAllText(MapPath(game)) == "unmanaged file", "Overwrote unrelated data");
        }
        string late = NewGame("created-during-rollback", "late-file");
        string old = SeedLegacy(late);
        Run(late, Plain, false);
        Check(File.ReadAllText(MapPath(late)) == "unrelated replacement"
            && FileHash(MapPath(late) + ".rollback") == old, "Lost either concurrent file");

        string held = NewGame("altered-during-rollback", "changed-held");
        SeedLegacy(held);
        Run(held, Plain, false);
        Check(File.ReadAllText(MapPath(held) + ".rollback") == "modified staging file", "Deleted modified staging file");
    }

    private static int Main(string[] args)
    {
        sandbox = Path.GetFullPath(args[0]);
        CmoPaths.LocalDirOverride = sandbox;
        CmoPaths.RoamingDirOverride = sandbox;
        // A sidecar for another build of the same named EXE must not affect the two-file target.
        File.WriteAllText(VersionStore.CachedPackage(Plain) + "." + new string('0', 64) + ".map.dat", "other build");
        foreach (string target in new[] { Plain, Embedded, Sidecar }) TargetMatches(target);
        RejectBadOutput(Plain, "corrupt-engine");
        foreach (string target in new[] { Embedded, Sidecar })
        {
            RejectBadOutput(target, "corrupt-map");
            RejectBadOutput(target, "omit-map");
        }
        RetireOrRestore(Plain, false);
        RetireOrRestore(Plain, true);
        RetireOrRestore(Embedded, true);
        ProtectExistingData();
        Console.WriteLine("Map label rollback checks passed (16 isolated game directories).");
        return 0;
    }
}
