using System;
using System.Collections.Generic;
using System.IO;
using Emberdelve.EditorTools;
using NUnit.Framework;

namespace Emberdelve.Tests.Harness
{
    public sealed class PlayerBuildCacheTests
    {
        string root;
        [SetUp] public void Setup()
        {
            root = Path.Combine(Path.GetTempPath(), "DakaCacheTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
        }
        [TearDown] public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }

        [Test] public void FingerprintTracksBytesNamesDeletionsAndProfileNotTimestamps()
        {
            string file = Path.Combine(root, "input.cs");
            File.WriteAllText(file, "aaaa");
            var inputs = new Dictionary<string, string> { { "Assets", root } };
            string initial = EmberdelvePlayerBuildCache.Fingerprint("net-proof", inputs);
            var time = File.GetLastWriteTimeUtc(file);
            File.SetLastWriteTimeUtc(file, time.AddSeconds(5));
            Assert.That(EmberdelvePlayerBuildCache.Fingerprint("net-proof", inputs), Is.EqualTo(initial));
            Assert.That(EmberdelvePlayerBuildCache.Fingerprint("net-final", inputs), Is.Not.EqualTo(initial));
            File.WriteAllText(file, "bbbb");
            File.SetLastWriteTimeUtc(file, time);
            Assert.That(EmberdelvePlayerBuildCache.Fingerprint("net-proof", inputs), Is.Not.EqualTo(initial));
            File.WriteAllText(file, "aaaa");
            File.Move(file, file + ".renamed");
            Assert.That(EmberdelvePlayerBuildCache.Fingerprint("net-proof", inputs), Is.Not.EqualTo(initial));
            File.Delete(file + ".renamed");
            Assert.That(EmberdelvePlayerBuildCache.Fingerprint("net-proof", inputs), Is.Not.EqualTo(initial));
        }

        [TestCase(0)][TestCase(3)][TestCase(400001)]
        public void FileHashMatchesStandardSha256AcrossStreamingChunks(int length)
        {
            byte[] bytes = new byte[length];
            new Random(42).NextBytes(bytes);
            string file = Path.Combine(root, "hash-input");
            File.WriteAllBytes(file, bytes);
            using (var standard = System.Security.Cryptography.SHA256.Create())
            {
                string expected = BitConverter.ToString(standard.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                Assert.That(EmberdelvePlayerBuildCache.HashFile(file), Is.EqualTo(expected));
            }
        }

        [TestCase("modified")][TestCase("missing")][TestCase("extra")]
        public void OutputManifestRejectsAnyPayloadDrift(string change)
        {
            string file = Path.Combine(root, "player.exe");
            File.WriteAllText(file, "exe");
            Directory.CreateDirectory(Path.Combine(root, "player_Data"));
            string data = Path.Combine(root, "player_Data", "data");
            File.WriteAllText(data, "content");
            var manifest = EmberdelvePlayerBuildCache.Describe("key", root);
            Assert.That(EmberdelvePlayerBuildCache.Valid("key", root, manifest), Is.True);
            Assert.That(EmberdelvePlayerBuildCache.Valid("wrong", root, manifest), Is.False);
            if (change == "modified") File.WriteAllText(data, "changed");
            if (change == "missing") File.Delete(data);
            if (change == "extra") File.WriteAllText(Path.Combine(root, "stale.dll"), "extra");
            Assert.That(EmberdelvePlayerBuildCache.Valid("key", root, manifest), Is.False);
        }

        [Test] public void MissingOrIncompleteManifestNeverPasses()
        {
            Assert.That(EmberdelvePlayerBuildCache.Valid("key", root, null), Is.False);
            Assert.That(EmberdelvePlayerBuildCache.Valid("key", root, new EmberdelvePlayerBuildCache.Manifest { key = "key" }), Is.False);
            Assert.Throws<DirectoryNotFoundException>(() => EmberdelvePlayerBuildCache.Fingerprint("test",
                new Dictionary<string, string> { { "Packages", Path.Combine(root, "missing") } }));
        }

        [TestCase("Assets/Data/item.asset", true)]
        [TestCase("Packages/local/data.asset", true)]
        [TestCase("ProjectSettings/ProjectSettings.asset", true)]
        [TestCase("Library/unity default resources", false)]
        [TestCase("", false)]
        public void DirtyEditorWindowStateIsNotAnAuthoredBuildInput(string path, bool expected)
        {
            Assert.That(EmberdelvePlayerBuildCache.IsBuildInputPath(path), Is.EqualTo(expected));
        }
    }
}
