using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    public sealed class InputSanitizerTests
    {
        private static MoveInputFrame F(uint seq, float x = 0f, float y = 1f, float yaw = 0f, float pitch = 0f, byte flags = 0)
        {
            return new MoveInputFrame { Seq = seq, MoveX = x, MoveY = y, Yaw = yaw, Pitch = pitch, Flags = flags };
        }

        private static SanitizeVerdict Submit(InputSanitizer s, MoveInputFrame f, bool primary = true, double now = 0.0)
        {
            return s.Submit(f, primary, now, out _);
        }

        [Test]
        public void FirstFrame_IsAccepted_AndCounted()
        {
            InputSanitizer s = new InputSanitizer();
            Assert.That(s.Submit(F(1), true, 0.0, out MoveInputFrame clean), Is.EqualTo(SanitizeVerdict.Accepted));
            Assert.That(clean.Seq, Is.EqualTo(1u));
            Assert.That(clean.MoveY, Is.EqualTo(1f));
            Assert.That(s.Accepted, Is.EqualTo(1));
            Assert.That(s.LastSeq, Is.EqualTo(1u));
        }

        [Test]
        public void Replay_FarBehind_IsDroppedAndCounted_ButRedundancyIsSilent()
        {
            InputSanitizer s = new InputSanitizer();
            for (uint i = 1; i <= 20; i++) Assert.That(Submit(s, F(i), true, i / 30.0), Is.EqualTo(SanitizeVerdict.Accepted));

            Assert.That(Submit(s, F(10)), Is.EqualTo(SanitizeVerdict.DroppedReplay), "10 is far behind 20");
            Assert.That(Submit(s, F(16)), Is.EqualTo(SanitizeVerdict.DroppedReplay), "behind by 4 = outside the redundancy window");
            Assert.That(Submit(s, F(17)), Is.EqualTo(SanitizeVerdict.Redundant), "behind by 3 = reordering / redundancy");
            Assert.That(Submit(s, F(20)), Is.EqualTo(SanitizeVerdict.Redundant), "the exact same sequence again");
            Assert.That(Submit(s, F(3), false), Is.EqualTo(SanitizeVerdict.Redundant), "an old frame in an older slot is never a replay event");

            Assert.That(s.DroppedReplay, Is.EqualTo(2));
            Assert.That(s.Redundant, Is.EqualTo(3));
            Assert.That(s.Accepted, Is.EqualTo(20));
            Assert.That(s.LastSeq, Is.EqualTo(20u), "a replay never moves the sequence");
        }

        [Test]
        public void ForwardJump_BeyondWindow_IsInvalid_AndLeavesNoState()
        {
            InputSanitizer s = new InputSanitizer();
            Assert.That(Submit(s, F(5)), Is.EqualTo(SanitizeVerdict.Accepted));

            Assert.That(Submit(s, F(70)), Is.EqualTo(SanitizeVerdict.DroppedInvalid), "65 ahead is outside the 64 window");
            Assert.That(s.LastInvalidReason, Is.EqualTo(InvalidReason.SeqWindow));
            Assert.That(Submit(s, F(100000u)), Is.EqualTo(SanitizeVerdict.DroppedInvalid));
            Assert.That(s.LastSeq, Is.EqualTo(5u));
            Assert.That(Submit(s, F(6)), Is.EqualTo(SanitizeVerdict.Accepted), "the honest stream continues behind the rejected jump");
            Assert.That(Submit(s, F(6u + MoveSanitizeLimits.MaxSeqJump)), Is.EqualTo(SanitizeVerdict.Accepted), "exactly 64 ahead is allowed");
            Assert.That(s.InvalidSeqWindow, Is.EqualTo(2));
        }

        [Test]
        public void FirstFrame_AboveWindow_OrZero_IsInvalid()
        {
            InputSanitizer s = new InputSanitizer();
            Assert.That(Submit(s, F(0)), Is.EqualTo(SanitizeVerdict.DroppedInvalid));
            Assert.That(s.LastInvalidReason, Is.EqualTo(InvalidReason.SeqZero));
            Assert.That(Submit(s, F(65)), Is.EqualTo(SanitizeVerdict.DroppedInvalid));
            Assert.That(s.LastInvalidReason, Is.EqualTo(InvalidReason.SeqWindow));
            Assert.That(Submit(s, F(64)), Is.EqualTo(SanitizeVerdict.Accepted));
        }

        [Test]
        public void NonFinite_AnyField_IsInvalid()
        {
            InputSanitizer s = new InputSanitizer();
            Assert.That(Submit(s, F(1, float.NaN)), Is.EqualTo(SanitizeVerdict.DroppedInvalid));
            Assert.That(Submit(s, F(1, 0f, float.PositiveInfinity)), Is.EqualTo(SanitizeVerdict.DroppedInvalid));
            Assert.That(Submit(s, F(1, 0f, 1f, float.NegativeInfinity)), Is.EqualTo(SanitizeVerdict.DroppedInvalid));
            Assert.That(Submit(s, F(1, 0f, 1f, 0f, float.NaN)), Is.EqualTo(SanitizeVerdict.DroppedInvalid));
            Assert.That(s.InvalidNonFinite, Is.EqualTo(4));
            Assert.That(s.Accepted, Is.EqualTo(0));
            Assert.That(s.HasSeq, Is.False, "rejected frames leave no sequence behind");
        }

        [Test]
        public void Flags_Whitelist()
        {
            InputSanitizer s = new InputSanitizer();
            Assert.That(Submit(s, F(1, 0f, 1f, 0f, 0f, 0x80)), Is.EqualTo(SanitizeVerdict.DroppedInvalid));
            Assert.That(s.LastInvalidReason, Is.EqualTo(InvalidReason.Flags));
            Assert.That(Submit(s, F(1, 0f, 1f, 0f, 0f, 0xFF)), Is.EqualTo(SanitizeVerdict.DroppedInvalid));
            Assert.That(Submit(s, F(1, 0f, 1f, 0f, 0f, MoveInputFrame.KnownFlags)), Is.EqualTo(SanitizeVerdict.Accepted));
            Assert.That(s.InvalidFlags, Is.EqualTo(2));
        }

        [Test]
        public void Move_IsClampedToLengthOne_AndAbsurdLengthIsRejected()
        {
            InputSanitizer s = new InputSanitizer();

            Assert.That(s.Submit(F(1, 1f, 1f), true, 0.0, out MoveInputFrame diagonal), Is.EqualTo(SanitizeVerdict.Accepted));
            Assert.That(diagonal.MoveX * diagonal.MoveX + diagonal.MoveY * diagonal.MoveY, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(s.Clamped, Is.EqualTo(1), "(1,1) is 1.41 long: clamped and counted");

            // Quantization noise (a keyboard diagonal is 90/127 on both axes = 1.002) is scaled but not counted.
            Assert.That(s.Submit(F(2, 0.7087f, 0.7087f), true, 0.1, out MoveInputFrame noisy), Is.EqualTo(SanitizeVerdict.Accepted));
            Assert.That(noisy.MoveX * noisy.MoveX + noisy.MoveY * noisy.MoveY, Is.LessThanOrEqualTo(1.0001f));
            Assert.That(s.Clamped, Is.EqualTo(1));

            // A frame inside the unit circle is untouched.
            Assert.That(s.Submit(F(3, 0.3f, 0.4f), true, 0.2, out MoveInputFrame inside), Is.EqualTo(SanitizeVerdict.Accepted));
            Assert.That(inside.MoveX, Is.EqualTo(0.3f));
            Assert.That(inside.MoveY, Is.EqualTo(0.4f));

            Assert.That(Submit(s, F(4, 50f, 50f)), Is.EqualTo(SanitizeVerdict.DroppedInvalid));
            Assert.That(s.LastInvalidReason, Is.EqualTo(InvalidReason.MoveMagnitude));
            Assert.That(Submit(s, F(4, 3.4e38f, 3.4e38f)), Is.EqualTo(SanitizeVerdict.DroppedInvalid), "squared overflows to infinity: still rejected");
            Assert.That(s.InvalidMagnitude, Is.EqualTo(2));
            Assert.That(Submit(s, F(4, 1.5f, 0f)), Is.EqualTo(SanitizeVerdict.Accepted), "exactly the limit is clamped, not rejected");
        }

        [Test]
        public void Yaw_IsWrapped_AndPitchIsClamped()
        {
            InputSanitizer s = new InputSanitizer();
            s.Submit(F(1, 0f, 1f, 540f, 200f), true, 0.0, out MoveInputFrame a);
            Assert.That(a.Yaw, Is.EqualTo(-180f).Within(1e-3f));
            Assert.That(a.Pitch, Is.EqualTo(MoveInputFrame.DefaultMaxPitch));

            s.Submit(F(2, 0f, 1f, -190f, -200f), true, 0.1, out MoveInputFrame b);
            Assert.That(b.Yaw, Is.EqualTo(170f).Within(1e-3f));
            Assert.That(b.Pitch, Is.EqualTo(MoveInputFrame.DefaultMinPitch));

            s.Submit(F(3, 0f, 1f, 1e30f, 0f), true, 0.2, out MoveInputFrame c);
            Assert.That(c.Yaw, Is.InRange(-180f, 180f));
        }

        [Test]
        public void Flood_BurstIsBounded_ThenRefillsAtTwiceTheTickRate()
        {
            InputSanitizer s = new InputSanitizer();
            int accepted = 0;
            int flooded = 0;
            for (uint i = 1; i <= 100; i++)
            {
                SanitizeVerdict v = Submit(s, F(i), true, 0.0);
                if (v == SanitizeVerdict.Accepted) accepted++;
                if (v == SanitizeVerdict.DroppedFlood) flooded++;
            }

            Assert.That(accepted, Is.EqualTo(60), "2 x TickRate frames in the first instant");
            Assert.That(flooded, Is.EqualTo(40));
            Assert.That(s.DroppedFlood, Is.EqualTo(40));
            Assert.That(s.Accepted, Is.EqualTo(60));
            Assert.That(s.LastSeq, Is.EqualTo(100u), "a flood-dropped frame still consumes its sequence number: the stream stays aligned");

            // One second later the bucket is full again, and the honest next frame is not seen as a jump.
            int again = 0;
            for (uint i = 101; i <= 200; i++)
                if (Submit(s, F(i), true, 1.0) == SanitizeVerdict.Accepted) again++;
            Assert.That(again, Is.EqualTo(60));
            Assert.That(s.DroppedInvalid, Is.EqualTo(0));
        }

        [Test]
        public void HonestRate_IsNeverThrottled()
        {
            InputSanitizer s = new InputSanitizer();
            for (uint i = 1; i <= 300; i++)
                Assert.That(Submit(s, F(i), true, i / 30.0), Is.EqualTo(SanitizeVerdict.Accepted), "frame " + i);
            Assert.That(s.DroppedFlood, Is.EqualTo(0));
        }

        [Test]
        public void HonestBatches_OfThree_AcceptEachFrameExactlyOnce_AndRecoverLostPackets()
        {
            InputSanitizer s = new InputSanitizer();
            double now = 0.0;

            // Batches [s-2, s-1, s], oldest first, newest primary. Packets for s = 5 and 6 are lost.
            for (uint seq = 1; seq <= 12; seq++)
            {
                now += 1.0 / 30.0;
                if (seq == 5 || seq == 6) continue;

                uint first = seq >= 3 ? seq - 2 : 1;
                for (uint k = first; k <= seq; k++) Submit(s, F(k), k == seq, now);
            }

            Assert.That(s.Accepted, Is.EqualTo(12), "frames 5 and 6 were recovered from the redundancy slots of 7 and 8");
            Assert.That(s.DroppedReplay, Is.EqualTo(0));
            Assert.That(s.DroppedInvalid, Is.EqualTo(0));
            Assert.That(s.DroppedFlood, Is.EqualTo(0));
            Assert.That(s.LastSeq, Is.EqualTo(12u));
        }

        [Test]
        public void ReorderedPacket_JustBehind_IsNotCountedAsReplay()
        {
            InputSanitizer s = new InputSanitizer();
            for (uint i = 1; i <= 10; i++) Submit(s, F(i), true, i / 30.0);
            Assert.That(Submit(s, F(11), true, 0.4), Is.EqualTo(SanitizeVerdict.Accepted));
            Assert.That(Submit(s, F(9), true, 0.41), Is.EqualTo(SanitizeVerdict.Redundant));
            Assert.That(s.DroppedReplay, Is.EqualTo(0));
        }
    }
}
