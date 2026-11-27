using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using CMOShared;
using CMOLocalizerInstaller;

internal static class MapLabelMigrationTests
{
    private const string Existing = "6/23/37";
    private const string OnDemand = "7/27/57";

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, bytes);
    }

    private static void Install(string game)
    {
        string error;
        Check(InstallerCore.Install(game, delegate(string _) {}, out error), "Installer: " + error);
        var manifest = InstallManifest.Read(game);
        Check(manifest != null && manifest.Files.Count == 2 &&
              manifest.Files.ContainsKey("CMOLocalizer.dll") && manifest.Files.ContainsKey("CMOLocalizer.json") &&
              InstallManifest.Verify(game,out error) == ManifestState.Ok, "R88 two-file manifest");
    }

    private static void Remove(string game)
    {
        string error;
        Check(InstallerCore.Uninstall(game, delegate(string _) {}, out error), "Uninstall: " + error);
        InstallManifest.Clear(game);
    }

    private static byte[][] ColdHttp(string game, string engine)
    {
        var manager = Assembly.LoadFrom(engine).GetType("CMOLocalizer.CustomAppDomainManager",true);
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        manager.GetField("logPath",flags).SetValue(null,Path.Combine(game,"test-engine.log"));
        manager.GetMethod("StartLocalTileServer",flags).Invoke(null,new object[]{ game });
        var listener = (TcpListener)manager.GetField("tileListener",flags).GetValue(null);
        Check(listener != null, "No production tile listener");
        try
        {
            var address = (IPEndPoint)listener.LocalEndpoint;
            Check(IPAddress.IsLoopback(address.Address), "Expected a loopback server");
            var response = new byte[2][];
            using (var http = new WebClient())
            {
                http.Proxy = null;
                string[] keys = { Existing, OnDemand };
                for (int i = 0; i < keys.Length; i++)
                {
                    response[i] = http.DownloadData("http://127.0.0.1:" + address.Port + "/MapTiles/Labels/" + keys[i] + ".png");
                    Check(response[i].Length > 8 && response[i][0] == 137 && http.ResponseHeaders["Content-Type"] == "image/png", keys[i]);
                }
            }
            return response;
        }
        finally { listener.Stop(); }
    }

    // A real CMOTIL01 record with one PNG exercises a manual old install with no installation manifest.
    private static void OldMap(string game, string cache, byte[] image)
    {
        byte[] payload;
        using (var buffer = new MemoryStream())
        {
            using (var w = new BinaryWriter(buffer,Encoding.UTF8,true))
            {
                w.Write(1); w.Write(6); w.Write(23); w.Write(37); w.Write(1); w.Write(image.Length); w.Write(image);
            }
            payload = buffer.ToArray();
        }
        string hash = MapLabelOriginalCache.Hash(payload).ToLowerInvariant();
        using (var w = new BinaryWriter(File.Create(Path.Combine(game,"ChineseMapTiles.dat"))))
        {
            w.Write(Encoding.ASCII.GetBytes("CMOTIL01")); w.Write(hash); w.Write(payload);
        }
        Write(Path.Combine(cache,MapLabelOriginalCache.OldTilesMarker),Encoding.ASCII.GetBytes(hash+"\n"));
        string entries = new string('A',64)+"\n"+Existing+"\t"+MapLabelOriginalCache.Hash(image)+"\n";
        Write(Path.Combine(cache,MapLabelOriginalCache.OldRenderMarker),
              Encoding.UTF8.GetBytes(MapLabelOriginalCache.Hash(Encoding.UTF8.GetBytes(entries))+"\n"+entries));
    }

    private static void Run(string folder)
    {
        string game = Path.Combine(folder,"game"), cache = MapLabelOriginalCache.DirectoryFor(game), orig = cache+".orig";
        CmoPaths.LocalDirOverride=Path.Combine(folder,"user-local");
        CmoPaths.RoamingDirOverride=Path.Combine(folder,"user-roaming");
        Write(Path.Combine(game,"Command.exe"),new byte[]{0});
        Write(Path.Combine(game,"Command.exe.config"),Encoding.UTF8.GetBytes("<configuration><runtime /></configuration>"));
        foreach(string set in new[]{"Cities","Synoptic"})
            Write(Path.Combine(game,"WW","PlacenameSets",set,"0000_0000.wwp"),new byte[4]);
        Install(game);
        byte[][] images = ColdHttp(game,Path.Combine(folder,"engine","CMOLocalizer.dll"));
        Check(!Directory.Exists(cache) || Directory.GetFiles(cache,"*.png",SearchOption.AllDirectories).Length==0,"Cold HTTP prefilled disk cache");
        Remove(game);

        string tile = Path.Combine(cache,Existing+".png"), original = Path.Combine(orig,Existing+".png");
        string onDemand = Path.Combine(cache,OnDemand+".png"), user = Path.Combine(cache,"10/10/10.png");
        byte[] before={1,2,3}, userImage={7,8,9};
        string editedKey = "10/819/453.png";
        string edited = Path.Combine(cache,editedKey), editedOriginal = Path.Combine(orig,editedKey);
        string screenshot = Path.Combine(cache,"screenshot.png"), nestedScreenshot = Path.Combine(cache,"custom","screenshot.png");
        Write(original,before); Write(tile,images[1]); OldMap(game,cache,images[1]);
        Write(editedOriginal,before); Write(edited,before);
        Write(screenshot,userImage); Write(nestedScreenshot,userImage);
        Directory.CreateDirectory(Path.Combine(orig,"original-empty"));
        Install(game);
        Check(!File.Exists(Path.Combine(game,"ChineseMapTiles.dat")) &&
              !File.Exists(Path.Combine(cache,MapLabelOriginalCache.OldTilesMarker)) &&
              !File.Exists(Path.Combine(cache,MapLabelOriginalCache.OldRenderMarker)), "Old PR artifacts remain");
        Check(MapLabelOriginalCache.FileSha(tile)==MapLabelOriginalCache.Hash(images[0]),"Installer cache differs from cold HTTP");
        DateTime stamp=File.GetLastWriteTimeUtc(tile);
        byte[] corrupt=(byte[])images[0].Clone(); corrupt[corrupt.Length/2]^=0xff;
        File.WriteAllBytes(tile,corrupt); File.SetLastWriteTimeUtc(tile,stamp);
        var index=MapLabelIndex.Load(game,Path.Combine(game,"CMOLocalizer.json"));
        Check(MapLabelCache.Refresh(game,index,false)==1 && MapLabelOriginalCache.FileSha(tile)==MapLabelOriginalCache.Hash(images[0]),
              "Same-length, same-time corruption survived");
        Check(MapLabelCache.Refresh(game,index,false)==0,"Repeated refresh wrote unchanged PNG bytes");
        Write(onDemand,images[1]); Write(user,userImage); Write(edited,userImage);
        Directory.CreateDirectory(Path.Combine(cache,"user-empty"));
        Remove(game);
        Check(MapLabelOriginalCache.FileSha(tile)==MapLabelOriginalCache.Hash(before),"Original cached tile was not restored");
        Check(!File.Exists(onDemand),"A matching on-demand HTTP tile remained");
        Check(MapLabelOriginalCache.FileSha(edited)==MapLabelOriginalCache.Hash(userImage) &&
              MapLabelOriginalCache.FileSha(editedOriginal)==MapLabelOriginalCache.Hash(before),
              "A player's edit or its original backup was lost");
        Check(MapLabelOriginalCache.FileSha(screenshot)==MapLabelOriginalCache.Hash(userImage) &&
              MapLabelOriginalCache.FileSha(nestedScreenshot)==MapLabelOriginalCache.Hash(userImage),
              "A player screenshot changed");
        Check(MapLabelOriginalCache.FileSha(user)==MapLabelOriginalCache.Hash(userImage) &&
              Directory.Exists(Path.Combine(cache,"original-empty")) && Directory.Exists(Path.Combine(cache,"user-empty")),
              "Uninstall removed untracked player files or empty directories");
    }

    private static int Main(string[] args)
    {
        try { Run(args[0]); Console.WriteLine("R88 install, real cold HTTP, old DAT migration and cache ownership passed."); return 0; }
        catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
