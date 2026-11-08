using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    /// <summary>Content-addressed player cache. Test results are never cached.</summary>
    public static class EmberdelvePlayerBuildCache
    {
        const string Tag = "[Emberdelve][BuildCache] ";
        [Serializable] public sealed class Entry { public string path; public string hash; }
        [Serializable] public sealed class Manifest
        {
            public int schema = 1;
            public string key;
            public string payload;
            public Entry[] files;
        }

        public static string HashFile(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                byte[] digest;
                if (Application.platform == RuntimePlatform.WindowsEditor) digest = WindowsSha256(stream);
                else using (var hash = SHA256.Create()) digest = hash.ComputeHash(stream);
                return BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();
            }
        }

        // Unity's Mono maps even SHA256Cng to managed SHA-256. Use Windows CNG directly
        // for streaming file hashes: identical SHA-256, without the managed multi-GB cost.
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        static extern int BCryptOpenAlgorithmProvider(out IntPtr provider, string algorithm, string implementation, uint flags);
        [DllImport("bcrypt.dll")]
        static extern int BCryptCreateHash(IntPtr provider, out IntPtr hash, IntPtr buffer, uint bufferSize, IntPtr secret, uint secretSize, uint flags);
        [DllImport("bcrypt.dll")]
        static extern int BCryptHashData(IntPtr hash, byte[] data, uint size, uint flags);
        [DllImport("bcrypt.dll")]
        static extern int BCryptFinishHash(IntPtr hash, [Out] byte[] digest, uint size, uint flags);
        [DllImport("bcrypt.dll")]
        static extern int BCryptDestroyHash(IntPtr hash);
        [DllImport("bcrypt.dll")]
        static extern int BCryptCloseAlgorithmProvider(IntPtr provider, uint flags);

        static void CheckCrypto(int status)
        {
            if (status < 0) throw new CryptographicException("Windows SHA-256 failed: 0x" + status.ToString("X8"));
        }

        static byte[] WindowsSha256(Stream stream)
        {
            IntPtr provider = IntPtr.Zero, hash = IntPtr.Zero;
            try
            {
                CheckCrypto(BCryptOpenAlgorithmProvider(out provider, "SHA256", null, 0));
                CheckCrypto(BCryptCreateHash(provider, out hash, IntPtr.Zero, 0, IntPtr.Zero, 0, 0));
                byte[] buffer = new byte[128 * 1024];
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                    CheckCrypto(BCryptHashData(hash, buffer, (uint)count, 0));
                byte[] digest = new byte[32];
                CheckCrypto(BCryptFinishHash(hash, digest, (uint)digest.Length, 0));
                return digest;
            }
            finally
            {
                if (hash != IntPtr.Zero) BCryptDestroyHash(hash);
                if (provider != IntPtr.Zero) BCryptCloseAlgorithmProvider(provider, 0);
            }
        }

        // Fail closed for links: don't silently omit external source or follow unbounded junctions.
        static IEnumerable<string> Files(string root)
        {
            foreach (string item in Directory.GetFileSystemEntries(root).OrderBy(x => x, StringComparer.Ordinal))
            {
                var attributes = File.GetAttributes(item);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked cache input/output: " + item);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (Path.GetFileName(item) == ".git") continue;
                    foreach (string child in Files(item)) yield return child;
                }
                else yield return item;
            }
        }

        public static string Fingerprint(string identity, IDictionary<string, string> roots)
        {
            var text = new StringBuilder(identity).Append('\n');
            foreach (var root in roots.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (!Directory.Exists(root.Value)) throw new DirectoryNotFoundException(root.Value);
                string full = Path.GetFullPath(root.Value).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                foreach (string file in Files(full))
                    text.Append(root.Key).Append('/').Append(file.Substring(full.Length).Replace('\\', '/'))
                        .Append('\0').Append(HashFile(file)).Append('\n');
            }
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
        }

        public static Manifest Describe(string key, string payload)
        {
            string root = Path.GetFullPath(payload).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return new Manifest { key = key, payload = Path.GetFileName(payload), files = Files(root)
                .Select(f => new Entry { path = f.Substring(root.Length).Replace('\\', '/'), hash = HashFile(f) }).ToArray() };
        }

        public static bool Valid(string key, string payload, Manifest manifest)
        {
            if (manifest == null || manifest.schema != 1 || manifest.key != key || manifest.files == null || manifest.files.Length == 0)
                return false;
            try
            {
                // Compare the complete inventory, including unexpected/removed files, not just the exe.
                var actual = Describe(key, payload).files;
                return actual.Length == manifest.files.Length && actual.Zip(manifest.files,
                    (a, b) => b != null && a.path == b.path && a.hash == b.hash).All(x => x);
            }
            catch (Exception) { return false; }
        }

        public static string TestPlayerPath(string requested, string profile)
        {
            if (!Application.isBatchMode) return requested;
            if (profile != "net-proof" && profile != "net-final") throw new ArgumentException("Unknown player profile");
            return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Builds", "HarnessPlayers", profile, Path.GetFileName(requested));
        }

        static bool StableDestination(string destination)
        {
            string root = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Builds", "HarnessPlayers");
            return new[] { "net-proof", "net-final" }.Any(profile =>
                string.Equals(Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar), Path.Combine(root, profile), StringComparison.OrdinalIgnoreCase));
        }

        static void RequirePlayerStopped(string exe)
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
            using (process)
            {
                try
                {
                    if (!process.HasExited && string.Equals(Path.GetFullPath(process.MainModule.FileName), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Player still running; refusing to replace " + exe);
                }
                catch (InvalidOperationException) { if (!process.HasExited) throw; }
            }
        }

        static void CopyPayload(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            // Stable paths prevent a new Windows Firewall application identity on every test run.
            // No overlay: unchanged payload stays in place; changed payload swaps whole directories.
            if (Directory.EnumerateFileSystemEntries(destination).Any())
            {
                if (!StableDestination(destination)) throw new IOException("Player output directory must be empty: " + destination);
                var contents = Describe("copy", source);
                if (Valid("copy", destination, contents)) return;
                if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked player output refused");
                foreach (string exe in Directory.GetFiles(destination, "*.exe")) RequirePlayerStopped(exe);
                string prepared = destination + ".next-" + Guid.NewGuid().ToString("N");
                string previous = destination + ".old-" + Guid.NewGuid().ToString("N");
                CopyPayload(source, prepared);
                Directory.Move(destination, previous);
                try { Directory.Move(prepared, destination); }
                catch { Directory.Move(previous, destination); throw; }
                // Retain the old generated payload for diagnosis; no recursive cleanup here.
                return;
            }
            string root = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string file in Files(root))
            {
                string target = Path.Combine(destination, file.Substring(root.Length));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, false);
            }
        }

        static string Identity(BuildPlayerOptions options, string profile)
        {
            return "player-cache-v1\n" + Application.unityVersion + "\n" + profile + "\n" + options.target + "\n" +
                options.options + "\n" + Path.GetFileName(options.locationPathName) + "\n" + string.Join("\n", options.scenes);
        }

        static Dictionary<string, string> Inputs(string project)
        {
            var roots = new Dictionary<string, string> {
                { "Assets", Path.Combine(project, "Assets") }, { "Packages", Path.Combine(project, "Packages") },
                { "ProjectSettings", Path.Combine(project, "ProjectSettings") }
            };
            // Registry/builtin packages are pinned by manifest/lock + Unity version. Mutable local/git
            // packages outside Packages also require a content hash, not just a package version string.
            foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                if (package.source == UnityEditor.PackageManager.PackageSource.Local ||
                    package.source == UnityEditor.PackageManager.PackageSource.Git)
                    roots["resolved/" + package.name] = package.resolvedPath;
            return roots;
        }

        static bool PersistentAssetsDirty()
        {
            var dirty = Resources.FindObjectsOfTypeAll<UnityEngine.Object>()
                .Where(o => o != null && EditorUtility.IsPersistent(o) && EditorUtility.IsDirty(o) &&
                    // Imported shader/texture objects carry editor-side dirty flags even
                    // after SaveAssets. Their build inputs are source + importer metadata;
                    // native serialized resources and importer edits must still be clean.
                    (AssetDatabase.IsNativeAsset(o) || o is AssetImporter) &&
                    IsBuildInputPath(AssetDatabase.GetAssetPath(o)))
                .Select(o => o.GetType().Name + ":" + AssetDatabase.GetAssetPath(o)).Distinct().ToArray();
            if (dirty.Length > 0) Debug.Log(Tag + "DIRTY " + string.Join(" | ", dirty.Take(8)));
            return dirty.Length > 0;
        }

        public static bool IsBuildInputPath(string path) => path != null &&
            (path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal) ||
             path.StartsWith("ProjectSettings/", StringComparison.Ordinal));

        public static bool Build(BuildPlayerOptions options, string profile, out string error)
        {
            error = null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (StableDestination(Path.GetDirectoryName(Path.GetFullPath(options.locationPathName)))) RequirePlayerStopped(options.locationPathName);
            bool enabled = Application.isBatchMode && Environment.GetEnvironmentVariable("EMBER_BUILD_CACHE") == "1";
            // The runner owns this disposable batch mirror. Persist builder-produced
            // resources before hashing so the snapshot describes the actual build state.
            // Never save the user's interactive Editor through this cache.
            if (enabled) AssetDatabase.SaveAssets();
            if (!enabled || PersistentAssetsDirty())
            {
                Debug.Log(Tag + "BYPASS profile=" + profile + " reason=" + (!enabled ? "disabled-or-interactive" : "dirty-persistent-assets"));
                return BuildFresh(options, out error);
            }
            try
            {
                string project = Directory.GetParent(Application.dataPath).FullName;
                var inputs = Inputs(project);
                string identity = Identity(options, profile);
                string key = Fingerprint(identity, inputs);
                string cache = Path.Combine(project, "Builds", "HarnessCache");
                Directory.CreateDirectory(cache);
                string record = Path.Combine(cache, key + ".json");
                Manifest manifest = null;
                try { if (File.Exists(record)) manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(record)); }
                catch (Exception) { /* corrupt/incomplete manifest is a miss */ }
                string payload = manifest != null && Guid.TryParseExact(manifest.payload, "N", out _) ? Path.Combine(cache, manifest.payload) : null;
                string destination = Path.GetDirectoryName(Path.GetFullPath(options.locationPathName));
                if (payload != null && Valid(key, payload, manifest) && File.Exists(Path.Combine(payload, Path.GetFileName(options.locationPathName))))
                {
                    CopyPayload(payload, destination);
                    if (key != Fingerprint(identity, inputs) || !Valid(key, destination, manifest))
                    { error = "Inputs or cached payload changed while copying"; return false; }
                    Debug.Log(Tag + "HIT profile=" + profile + " key=" + key + " seconds=" + watch.Elapsed.TotalSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                    return true;
                }
                Debug.Log(Tag + "MISS profile=" + profile + " key=" + key);
                string fresh = Path.Combine(cache, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(fresh);
                var build = options;
                build.locationPathName = Path.Combine(fresh, Path.GetFileName(options.locationPathName));
                if (!BuildFresh(build, out error)) return false; // no manifest published on failure
                AssetDatabase.SaveAssets();
                if (key != Fingerprint(identity, inputs) || PersistentAssetsDirty())
                {
                    // A build hook can change source/settings. Deliver this successful fresh build,
                    // but never label it a reusable snapshot of the previous inputs.
                    CopyPayload(fresh, destination);
                    Debug.Log(Tag + "NOT_STORED profile=" + profile + " reason=inputs-changed-during-build");
                    return true;
                }
                manifest = Describe(key, fresh);
                string temp = record + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, JsonUtility.ToJson(manifest, true));
                if (File.Exists(record)) File.Replace(temp, record, null); else File.Move(temp, record);
                CopyPayload(fresh, destination);
                Debug.Log(Tag + "STORED profile=" + profile + " key=" + key + " seconds=" + watch.Elapsed.TotalSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                return true;
            }
            catch (Exception ex)
            {
                error = "build cache: " + ex.Message;
                return false; // never quietly run an unverifiable binary
            }
        }

        static bool BuildFresh(BuildPlayerOptions options, out string error)
        {
            BuildReport report = BuildPipeline.BuildPlayer(options);
            bool ok = report.summary.result == BuildResult.Succeeded && File.Exists(options.locationPathName);
            error = ok ? null : "build result=" + report.summary.result;
            return ok;
        }
    }
}
