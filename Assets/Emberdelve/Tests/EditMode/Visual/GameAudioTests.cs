using System;
using Emberdelve.Feedback;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.Tests
{
    public sealed class GameAudioTests
    {
        [Test]
        public void EveryCueLoadsAllOriginalMonoVariantsWithHeadroom()
        {
            foreach (WorldSound cue in Enum.GetValues(typeof(WorldSound)))
                for (int i = 0; i < GameSoundBank.VariantCount(cue); i++)
                {
                    var clip = GameSoundBank.Get(cue, i);
                    Assert.That(clip, Is.Not.Null, cue + " variant " + i);
                    Assert.That(clip.channels, Is.EqualTo(1), clip.name);
                    Assert.That(clip.frequency, Is.EqualTo(48000), clip.name);
                    Assert.That(clip.length, Is.InRange(.03f, 8f), clip.name);
                    var data = new float[clip.samples];
                    Assert.That(clip.GetData(data, 0), Is.True, clip.name);
                    float peak = 0f; bool finite = true;
                    foreach (float value in data) { finite &= float.IsFinite(value); peak = Mathf.Max(peak, Mathf.Abs(value)); }
                    Assert.That(finite, Is.True, clip.name);
                    Assert.That(peak, Is.InRange(.001f, .95f), clip.name);
                    Assert.That(Mathf.Abs(data[0]) + Mathf.Abs(data[data.Length - 1]), Is.LessThan(.001f), clip.name);
                    var importer = (AudioImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(clip));
                    Assert.That(importer.defaultSampleSettings.loadType, Is.EqualTo(AudioClipLoadType.DecompressOnLoad));
                }
        }

        [Test]
        public void VariationDoesNotRepeatOrConsumeGameplayRandom()
        {
            var state = UnityEngine.Random.state;
            foreach (WorldSound cue in Enum.GetValues(typeof(WorldSound)))
            {
                AudioClip previous = null;
                for (int i = 0; i < 16; i++)
                { AudioClip next = GameSoundBank.Next(cue); Assert.That(next, Is.Not.Null); Assert.That(next, Is.Not.SameAs(previous)); previous = next; }
            }
            Assert.That(UnityEngine.Random.state, Is.EqualTo(state));
        }

        [Test]
        public void DestroyingWorldPoolKeepsSharedImportedClipsAlive()
        {
            var clip = GameSoundBank.Get(WorldSound.Impact, 0);
            var root = new GameObject("AudioLifetimeTest");
            root.AddComponent<WorldAudio>();
            UnityEngine.Object.DestroyImmediate(root);
            Assert.That(clip != null, Is.True);
            Assert.That(GameSoundBank.Get(WorldSound.Impact, 0), Is.SameAs(clip));
        }

        [Test]
        public void SoundIdentityDeduplicatesZeroDamageContactsAndResetsForNewSession()
        {
            var window = new SoundEventWindow();
            Assert.That(window.Accept(42), Is.True);
            Assert.That(window.Accept(42), Is.False);
            for (ulong i = 43; i < 299; i++) Assert.That(window.Accept(i), Is.True);
            Assert.That(window.Accept(42), Is.True);
            window.Clear(); Assert.That(window.Accept(42), Is.True);
        }

        [TestCase("Mossy_Stone", WorldSound.Step)]
        [TestCase("Oak_Planks", WorldSound.StepWood)]
        [TestCase("Gravel", WorldSound.StepDirt)]
        [TestCase("unknown", WorldSound.Step)]
        public void ExistingSurfaceNamesChooseFoley(string name, WorldSound expected) => Assert.That(FootstepSurface.Classify(name), Is.EqualTo(expected));
    }
}
