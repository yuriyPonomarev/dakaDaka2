using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    public sealed class MoveInputFrameTests
    {
        [Test]
        public void Axis_RoundTripsWithinHalfAStep()
        {
            foreach (float v in new[] { -1f, -0.7071f, -0.25f, 0f, 0.3f, 0.5f, 1f })
            {
                float back = MoveInputFrame.DequantizeAxis(MoveInputFrame.QuantizeAxis(v));
                Assert.That(back, Is.EqualTo(v).Within(1f / 254f + 1e-6f), "axis " + v);
            }
        }

        [Test]
        public void Axis_NonFiniteIsZero_AndOutOfRangeClamps()
        {
            Assert.That(MoveInputFrame.QuantizeAxis(float.NaN), Is.EqualTo((sbyte)0));
            Assert.That(MoveInputFrame.QuantizeAxis(float.NegativeInfinity), Is.EqualTo((sbyte)0));
            Assert.That(MoveInputFrame.QuantizeAxis(9f), Is.EqualTo((sbyte)127));
            Assert.That(MoveInputFrame.QuantizeAxis(-9f), Is.EqualTo((sbyte)-127));
            Assert.That(MoveInputFrame.DequantizeAxis(-128), Is.EqualTo(-1f));
        }

        [Test]
        public void Angle_RoundTripsAndWraps()
        {
            foreach (float a in new[] { -179.9f, -90f, 0f, 37.25f, 123.4f, 179.9f })
            {
                float back = MoveInputFrame.DequantizeAngle(MoveInputFrame.QuantizeAngle(a));
                Assert.That(back, Is.EqualTo(a).Within(0.006f), "angle " + a);
            }

            Assert.That(MoveInputFrame.WrapDegrees(540f), Is.EqualTo(-180f).Within(1e-3f));
            Assert.That(MoveInputFrame.WrapDegrees(-190f), Is.EqualTo(170f).Within(1e-3f));
            Assert.That(MoveInputFrame.WrapDegrees(float.NaN), Is.EqualTo(0f));
            Assert.That(MoveInputFrame.DequantizeAngle(MoveInputFrame.QuantizeAngle(360f + 45f)), Is.EqualTo(45f).Within(0.006f));
        }

        [Test]
        public void WellFormed_RejectsNonFiniteAndUnknownFlags()
        {
            Assert.That(MoveInputFrame.Create(1, 0f, 1f, 10f, 5f, true, false, false).IsWellFormed, Is.True);
            Assert.That(MoveInputFrame.Create(1, float.NaN, 1f, 10f, 5f, false, false, false).IsWellFormed, Is.False);
            Assert.That(MoveInputFrame.Create(1, 0f, 1f, float.PositiveInfinity, 5f, false, false, false).IsWellFormed, Is.False);
            MoveInputFrame odd = new MoveInputFrame { Flags = 0x40 };
            Assert.That(odd.IsWellFormed, Is.False);
            Assert.That(odd.Quantized().Flags, Is.EqualTo((byte)0));
        }

        [Test]
        public void Flags_AreIndependentBits()
        {
            MoveInputFrame f = MoveInputFrame.Create(7, 0f, 0f, 0f, 0f, true, false, true);
            Assert.That(f.Sprint, Is.True);
            Assert.That(f.Jump, Is.False);
            Assert.That(f.CrouchToggle, Is.True);
            Assert.That(f.GuardHeld, Is.False);
            Assert.That(MoveInputFrame.MakeFlags(true, true, true, true), Is.EqualTo(MoveInputFrame.KnownFlags));
        }

        [Test]
        public void Quantized_ClampsPitch_WrapsYaw_KeepsSeqAndFlags()
        {
            MoveInputFrame q = MoveInputFrame.Create(42, 0.7071f, -0.7071f, 200f, 120f, true, true, false).Quantized();
            Assert.That(q.Seq, Is.EqualTo(42u));
            Assert.That(q.Pitch, Is.EqualTo(MoveInputFrame.DefaultMaxPitch));
            Assert.That(q.Yaw, Is.EqualTo(-160f).Within(0.006f));
            Assert.That(q.Sprint && q.Jump && !q.CrouchToggle, Is.True);
            Assert.That(q.MoveX, Is.EqualTo(0.7071f).Within(0.005f));
            Assert.That(q.MoveY, Is.EqualTo(-0.7071f).Within(0.005f));
        }
    }
}
