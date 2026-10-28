using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    public sealed class PredictionStateTests
    {
        [Test]
        public void Distance_IsThreeDimensional()
        {
            PredictedState a = new PredictedState { X = 1f, Y = 2f, Z = 3f };
            PredictedState b = new PredictedState { X = 4f, Y = 6f, Z = 3f };
            Assert.That(a.DistanceTo(b), Is.EqualTo(5f).Within(1e-5f));
        }

        [Test]
        public void IsFinite_RejectsNaNAndInfinity()
        {
            Assert.That(new PredictedState { CapsuleHeight = 1.8f }.IsFinite, Is.True);
            Assert.That(new PredictedState { X = float.NaN }.IsFinite, Is.False);
            Assert.That(new PredictedState { PlanarVz = float.PositiveInfinity }.IsFinite, Is.False);
            Assert.That(new PredictedState { JumpCooldown = float.NegativeInfinity }.IsFinite, Is.False);
        }

        private static MoveParamsData Good()
        {
            return new MoveParamsData { Walk = 4.2f, Sprint = 6.4f, Crouch = 2f, StandHeight = 1.8f, CrouchHeight = 1.15f, JumpHeight = 1.1f, GroundMask = 1 << 8 };
        }

        [Test]
        public void MoveParams_Valid_AndEquality()
        {
            MoveParamsData a = Good();
            MoveParamsData b = Good();
            Assert.That(a.IsValid, Is.True);
            Assert.That(a.Equals(b), Is.True);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));

            b.Sprint = 6.5f;
            Assert.That(a.Equals(b), Is.False);
            b = Good();
            b.GroundMask = 3;
            Assert.That(a.Equals(b), Is.False);
        }

        [Test]
        public void MoveParams_Invalid_WhenZeroOrNonFinite()
        {
            Assert.That(default(MoveParamsData).IsValid, Is.False, "a never-written variable is not usable");

            MoveParamsData p = Good();
            p.Walk = float.NaN;
            Assert.That(p.IsValid, Is.False);

            p = Good();
            p.StandHeight = 0f;
            Assert.That(p.IsValid, Is.False);

            p = Good();
            p.Sprint = float.PositiveInfinity;
            Assert.That(p.IsValid, Is.False);
        }
    }
}
