using System;
using Emberdelve.Feedback;
using NUnit.Framework;

namespace Emberdelve.Tests.Visual
{
    public sealed class SpatialAudioTests
    {
        [Test]
        public void EveryWorldCueGetsQuieterWithDistanceAndEndsInSilence()
        {
            foreach (WorldSound cue in Enum.GetValues(typeof(WorldSound)))
            {
                AcousticProfile profile = AcousticProfile.For(cue);
                if (!profile.IsSpatial) continue;
                Assert.That(profile.NearDistance, Is.GreaterThan(0f), cue.ToString());
                Assert.That(profile.FarDistance, Is.GreaterThan(profile.NearDistance), cue.ToString());
                Assert.That(profile.DistanceGain(0f), Is.EqualTo(1f), cue.ToString());
                Assert.That(profile.DistanceGain(profile.NearDistance), Is.EqualTo(1f), cue.ToString());
                float previous = 1f;
                for (int step = 1; step <= 100; step++)
                {
                    float distance = profile.NearDistance + (profile.FarDistance - profile.NearDistance) * step / 100f;
                    float gain = profile.DistanceGain(distance);
                    Assert.That(gain, Is.InRange(0f, previous), cue + " at " + distance);
                    previous = gain;
                }
                Assert.That(profile.DistanceGain(profile.FarDistance), Is.Zero, cue.ToString());
                Assert.That(profile.DistanceGain(profile.FarDistance + 20f), Is.Zero, cue.ToString());
            }
        }

        [Test]
        public void DistantFootstepsAreSubstantiallyQuieterAndHeavyContactsCarryFarther()
        {
            AcousticProfile step = AcousticProfile.For(WorldSound.Step);
            AcousticProfile heavy = AcousticProfile.For(WorldSound.LandHeavy);
            Assert.That(step.DistanceGain(10f), Is.LessThan(.15f));
            Assert.That(heavy.DistanceGain(10f), Is.GreaterThan(step.DistanceGain(10f)));
            Assert.That(heavy.FarDistance, Is.GreaterThan(step.FarDistance));
            Assert.That(AcousticProfile.For(WorldSound.Cloth).DistanceGain(10f), Is.Zero);
            Assert.That(AcousticProfile.For(WorldSound.SkillCry).FarDistance, Is.GreaterThan(heavy.FarDistance));
        }

        [Test]
        public void GainHasNoAudibleJumpAtNearOrFarBoundary()
        {
            foreach (WorldSound cue in Enum.GetValues(typeof(WorldSound)))
            {
                AcousticProfile profile = AcousticProfile.For(cue);
                if (!profile.IsSpatial) continue;
                Assert.That(profile.DistanceGain(profile.NearDistance + .01f), Is.GreaterThan(.999f), cue.ToString());
                Assert.That(profile.DistanceGain(profile.FarDistance - .01f), Is.LessThan(.0001f), cue.ToString());
                Assert.That(profile.RoomSend, Is.InRange(0f, .3f), "Room reflections must stay below the direct sound: " + cue);
            }
        }

        [Test]
        public void OnlyUiCueProfilesBypassWorldAttenuation()
        {
            foreach (WorldSound cue in Enum.GetValues(typeof(WorldSound)))
            {
                bool ui = cue == WorldSound.UiHover || cue == WorldSound.UiClick || cue == WorldSound.UiOpen ||
                    cue == WorldSound.UiClose || cue == WorldSound.UiError;
                AcousticProfile profile = AcousticProfile.For(cue);
                Assert.That(profile.IsSpatial, Is.EqualTo(!ui), cue.ToString());
                if (ui)
                {
                    Assert.That(profile.DistanceGain(1000f), Is.EqualTo(1f), cue.ToString());
                    Assert.That(profile.RoomSend, Is.Zero, cue.ToString());
                }
            }
        }

        [Test]
        public void InvalidDistanceCannotProduceAnInvalidSourceGain()
        {
            AcousticProfile profile = AcousticProfile.For(WorldSound.Impact);
            Assert.That(profile.DistanceGain(float.NaN), Is.Zero);
            Assert.That(profile.DistanceGain(float.PositiveInfinity), Is.Zero);
            Assert.That(profile.DistanceGain(float.NegativeInfinity), Is.Zero);
        }
    }
}
