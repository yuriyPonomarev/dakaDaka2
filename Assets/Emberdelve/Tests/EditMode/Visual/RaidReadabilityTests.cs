using NUnit.Framework;
using UnityEngine;
using Emberdelve.Feedback;
using Emberdelve.Level;

namespace Emberdelve.Tests.Visual
{
    public sealed class RaidReadabilityTests
    {
        [Test]
        public void Footsteps_RequireTravelAndRejectTeleportsAirborneAndDeadMovement()
        {
            var cadence = new FootstepCadence();
            Vector3 position = Vector3.zero;
            Assert.IsFalse(cadence.Advance(position, .1f, true, true));
            for (int i = 0; i < 20; i++) Assert.IsFalse(cadence.Advance(position, .1f, true, true));
            for (int i = 0; i < 4; i++) { position.x += .3f; Assert.IsFalse(cadence.Advance(position, .1f, true, true)); }
            position.x += .3f;
            Assert.IsTrue(cadence.Advance(position, .1f, true, true));
            position.x += 20f;
            Assert.IsFalse(cadence.Advance(position, .1f, true, true));
            for (int i = 0; i < 20; i++) { position.x += .3f; Assert.IsFalse(cadence.Advance(position, .1f, false, true)); }
            for (int i = 0; i < 20; i++) { position.x += .3f; Assert.IsFalse(cadence.Advance(position, .1f, true, false)); }
            Assert.IsFalse(cadence.Advance(position + Vector3.right * .3f, 0f, true, true));
        }

        [TestCase(0f, "N")]
        [TestCase(359f, "N")]
        [TestCase(-90f, "W")]
        [TestCase(90f, "E")]
        [TestCase(180f, "S")]
        public void Compass_WrapsAcrossNorth(float angle, string expected) => Assert.AreEqual(expected, DungeonWayfinding.Heading(angle));

        [Test]
        public void RoomLabel_UsesWorldOriginAndOnlyTheOccupiedRoom()
        {
            var layout = ScriptableObject.CreateInstance<DungeonLayoutData>();
            try
            {
                layout.origin = new Vector3(30, 0, -20);
                layout.cellSize = 2;
                layout.rooms.Add(new RoomSpec { roomName = "crypt_vault_01", minCell = new Vector2Int(2, 2), maxCell = new Vector2Int(4, 4) });
                Assert.AreEqual("Sealed Treasury", DungeonWayfinding.RoomAt(layout, layout.CellCenterWorld(new Vector2Int(4, 4))));
                Assert.AreEqual("Passage", DungeonWayfinding.RoomAt(layout, layout.CellCenterWorld(new Vector2Int(5, 4))));
                Assert.AreEqual(string.Empty, DungeonWayfinding.RoomAt(null, Vector3.zero));
            }
            finally { Object.DestroyImmediate(layout); }
        }

        [Test]
        public void SynthesizedClips_AreFiniteNonSilentAndDoNotConsumeGameplayRandom()
        {
            Random.State previous = Random.state;
            Random.InitState(278);
            float expected = Random.value;
            Random.InitState(278);
            var clips = new[] { ProceduralAudio.SwordSwing(), ProceduralAudio.Impact(), ProceduralAudio.StoneStep(), ProceduralAudio.FireCrackle() };
            try
            {
                Assert.AreEqual(expected, Random.value);
                foreach (var clip in clips)
                {
                    var samples = new float[clip.samples];
                    Assert.IsTrue(clip.GetData(samples, 0));
                    float peak = 0f;
                    foreach (float sample in samples) { Assert.IsTrue(float.IsFinite(sample)); peak = Mathf.Max(peak, Mathf.Abs(sample)); }
                    Assert.That(peak, Is.GreaterThan(.01f).And.LessThanOrEqualTo(1f));
                }
            }
            finally { foreach (var clip in clips) Object.DestroyImmediate(clip); Random.state = previous; }
        }
    }
}
