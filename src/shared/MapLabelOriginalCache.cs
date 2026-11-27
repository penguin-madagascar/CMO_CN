using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CMOShared
{
    // Shared by the runtime, installer and updater. No UI, renderer or package dependencies.
    internal static class MapLabelOriginalCache
    {
        internal const string OldTilesMarker = ".chinese-map-tiles.sha256";
        internal const string OldRenderMarker = ".chinese-map-rendered.sha256";
        internal const string CurrentMarker = ".r88-map-rendered.sha256";
        internal static string DirectoryFor(string game)
        {
            return Path.Combine(Path.GetFullPath(game), @"WW\Cache\Stamen Terrain - Labels");
        }

        internal static string Hash(byte[] bytes)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
        }

        internal static string FileSha(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
        }

        internal static bool Digest(string value)
        {
            return value != null && Regex.IsMatch(value, @"\A[0-9a-fA-F]{64}\z");
        }

        internal static bool Same(string left, string right)
        {
            return String.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class Lease : IDisposable
        {
            private readonly Mutex mutex;
            internal Lease(string cache)
            {
                string name = @"Local\CMOMapCache-" + Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(cache).ToUpperInvariant()));
                mutex = new Mutex(false, name);
                try { mutex.WaitOne(); }
                catch (AbandonedMutexException) { }
            }
            public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
        }

        internal static IDisposable Lock(string cachePath) { return new Lease(cachePath); }

        internal static void Plain(string path)
        {
            for (string next = Path.GetFullPath(path); next != null; next = Path.GetDirectoryName(next))
            {
                try
                {
                    if ((File.GetAttributes(next) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("地图缓存路径包含重解析点：" + next);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }

        internal static Dictionary<string,string> ReadOldReceipt(string path)
        {
            string inputs;
            return ReadReceipt(path, false, out inputs);
        }

        internal static Dictionary<string,string> ReadCurrentReceipt(string path, out string fingerprint)
        {
            return ReadReceipt(path, true, out fingerprint);
        }

        private static Dictionary<string,string> ReadReceipt(string path, bool pngKeys, out string fingerprint)
        {
            fingerprint = null;
            var file = new FileInfo(path);
            if (!file.Exists || file.Length < 131 || file.Length > 64L * 1024 * 1024) return null;
            string text = File.ReadAllText(path, Encoding.UTF8);
            if (text.Length < 131 || text[64] != '\n') return null;
            string body = text.Substring(65);
            if (!Same(Hash(Encoding.UTF8.GetBytes(body)), text.Substring(0,64))) return null;
            var records = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            using (var lines = new StringReader(body))
            {
                fingerprint = lines.ReadLine();
                if (!Digest(fingerprint)) return null;
                string line;
                while ((line = lines.ReadLine()) != null)
                {
                    var parts = line.Split('\t');
                    if (parts.Length != 2 || !Regex.IsMatch(parts[0], pngKeys ? @"\A[0-9]+/[0-9]+/[0-9]+\.png\z" : @"\A[0-9]+/[0-9]+/[0-9]+\z") ||
                        !Digest(parts[1]) || records.ContainsKey(parts[0])) return null;
                    records.Add(parts[0], parts[1]);
                }
            }
            return records;
        }

        private static Dictionary<string,string> Managed(string cache, out bool currentReceiptValid)
        {
            var known = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            string oldPath = Path.Combine(cache,OldRenderMarker), currentPath = Path.Combine(cache,CurrentMarker);
            Plain(oldPath); Plain(currentPath);
            var old = ReadOldReceipt(oldPath);
            if (old != null) foreach (var row in old) known[row.Key + ".png"] = row.Value;
            string inputs;
            var current = ReadCurrentReceipt(currentPath,out inputs);
            currentReceiptValid = current != null;
            if (current != null) foreach (var row in current) known[row.Key] = row.Value;
            return known;
        }

        internal static void EnsureOriginalBackup(string gamePath)
        {
            string cache = DirectoryFor(gamePath), backup = cache + ".orig";
            using (Lock(cache))
            {
                Plain(cache); Plain(backup);
                if (Directory.Exists(backup)) return;
                if (!Directory.Exists(cache)) { Directory.CreateDirectory(backup); return; }
                bool current;
                var derived = Managed(cache,out current);
                string stage = backup + ".tmp-" + Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(stage);
                try
                {
                    CopyOriginals(cache,cache,stage,derived);
                    Directory.Move(stage,backup);
                }
                finally { if (Directory.Exists(stage)) Directory.Delete(stage,true); }
            }
        }

        private static void CopyOriginals(string root,string dir,string stage,Dictionary<string,string> derived)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(dir))
            {
                Plain(path);
                string rel = path.Substring(root.Length + 1);
                if (Directory.Exists(path))
                {
                    Directory.CreateDirectory(Path.Combine(stage,rel));
                    CopyOriginals(root,path,stage,derived);
                    continue;
                }
                if (Same(rel,OldTilesMarker) || Same(rel,OldRenderMarker) || Same(rel,CurrentMarker)) continue;
                string old;
                if (derived.TryGetValue(rel.Replace('\\','/'),out old) &&
                    Same(FileSha(path),old)) continue;
                string target = Path.Combine(stage,rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(path,target);
            }
        }

        // Restore only managed writes. If a player has since edited an original, retain the original
        // backup as a whole and leave that changed live file untouched.
        internal static int Restore(string gamePath, Func<int,int,int,byte[]> renderUnrecorded = null)
        {
            string cache = DirectoryFor(gamePath), backup = cache + ".orig";
            using (Lock(cache))
            {
                Plain(cache); Plain(backup);
                if (!Directory.Exists(backup)) return 0;
                if (!Directory.Exists(cache)) { CheckTree(backup); Directory.Move(backup,cache); return 0; }
                CheckTree(backup);
                bool current;
                var known = Managed(cache,out current);
                // World Wind may cache a fresh HTTP response after startup last recorded the folder.
                if (renderUnrecorded != null) FindGenerated(cache,cache,known,renderUnrecorded);
                string newMarker = Path.Combine(cache,CurrentMarker);
                int conflicts = 0;
                foreach (string original in Directory.GetFiles(backup,"*",SearchOption.AllDirectories))
                {
                    string rel = original.Substring(backup.Length + 1), live = Path.Combine(cache,rel);
                    Plain(live);
                    if (!File.Exists(live)) continue;
                    string actual = FileSha(live), managed;
                    if (!Same(actual,FileSha(original)) &&
                        (!known.TryGetValue(rel.Replace('\\','/'),out managed) || !Same(actual,managed))) conflicts++;
                }
                foreach (var row in known)
                {
                    string live = Path.Combine(cache,row.Key.Replace('/','\\')), original = Path.Combine(backup,row.Key.Replace('/','\\'));
                    Plain(live);
                    if (!File.Exists(original) && File.Exists(live) && Same(FileSha(live),row.Value))
                    {
                        File.Delete(live);
                        for (string folder = Path.GetDirectoryName(live); !Same(folder,cache); folder = Path.GetDirectoryName(folder))
                        {
                            string rel = folder.Substring(cache.Length + 1);
                            if (Directory.Exists(Path.Combine(backup,rel)) || Directory.GetFileSystemEntries(folder).Length != 0) break;
                            Directory.Delete(folder);
                        }
                    }
                }
                foreach (string originalDir in Directory.GetDirectories(backup,"*",SearchOption.AllDirectories))
                {
                    string destination = Path.Combine(cache,originalDir.Substring(backup.Length + 1));
                    Plain(destination);
                    Directory.CreateDirectory(destination);
                }
                foreach (string original in Directory.GetFiles(backup,"*",SearchOption.AllDirectories))
                {
                    string rel = original.Substring(backup.Length + 1), live = Path.Combine(cache,rel), managed;
                    if (File.Exists(live))
                    {
                        string actual = FileSha(live);
                        if (Same(actual,FileSha(original)) || !known.TryGetValue(rel.Replace('\\','/'),out managed) || !Same(actual,managed)) continue;
                    }
                    File.Copy(original,live,true);
                }
                if (current) File.Delete(newMarker);
                if (conflicts == 0) Directory.Delete(backup,true);
                return conflicts;
            }
        }

        private static void FindGenerated(string cache,string folder,Dictionary<string,string> known,Func<int,int,int,byte[]> render)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(folder))
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                if ((attrs & FileAttributes.Directory) != 0) { FindGenerated(cache,path,known,render); continue; }
                string key = path.Substring(cache.Length+1).Replace('\\','/');
                if (!key.EndsWith(".png",StringComparison.OrdinalIgnoreCase)) continue;
                string[] parts = key.Substring(0,key.Length-4).Split('/');
                int z,x,y;
                if (parts.Length!=3 || !Int32.TryParse(parts[0],out z) || !Int32.TryParse(parts[1],out x) ||
                    !Int32.TryParse(parts[2],out y) || z<0 || z>30 || x<0 || y<0 || x>=(1L<<z) || y>=(1L<<z)) continue;
                string actual = FileSha(path), managed;
                if (known.TryGetValue(key,out managed) && Same(managed,actual)) continue;
                byte[] bytes = render(z,x,y);
                if (bytes!=null && Same(actual,Hash(bytes))) known[key]=actual;
            }
        }

        private static void CheckTree(string dir)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(dir))
            {
                Plain(path);
                if (Directory.Exists(path)) CheckTree(path);
            }
        }
    }
}
