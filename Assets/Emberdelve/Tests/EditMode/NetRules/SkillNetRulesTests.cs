using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    /// <summary>U3B2: the skill intent gate's hardened contract (slot constant, clock bounds, ceiling, probation, counters, no Redundant verdict).</summary>
    public sealed class SkillNetRulesTests
    {
        [Test]
        public void SlotBoundIsTheSharedConstant()
        {
            Assert.That(SkillNetLimits.ActiveSlots, Is.EqualTo(2));
            var gate = new SkillIntentGate();
            Assert.That(gate.Submit(SkillNetLimits.ActiveSlots - 1, 1, 0), Is.EqualTo(SkillIntentVerdict.Accepted));
            Assert.That(gate.Submit(SkillNetLimits.ActiveSlots, 2, 0), Is.EqualTo(SkillIntentVerdict.Invalid));
            Assert.That(gate.Submit(int.MaxValue, 2, 0), Is.EqualTo(SkillIntentVerdict.Invalid));
            Assert.That(gate.Submit(int.MinValue, 2, 0), Is.EqualTo(SkillIntentVerdict.Invalid));
        }

        [Test]
        public void ReliableOrderedChannel_DuplicateAndOutOfOrderAreReplays_NotRedundant()
        {
            // There is deliberately no Redundant verdict: the channel is reliable and ordered, so a sequence that is not newer is
            // a buggy or hostile client. Pinned from both sides: the exact duplicate and an older one.
            var gate = new SkillIntentGate();
            Assert.That(gate.Submit(0, 1, 0), Is.EqualTo(SkillIntentVerdict.Accepted));
            Assert.That(gate.Submit(1, 3, 0), Is.EqualTo(SkillIntentVerdict.Accepted));
            Assert.That(gate.Submit(1, 3, 0), Is.EqualTo(SkillIntentVerdict.Replay), "exact duplicate");
            Assert.That(gate.Submit(1, 2, 0), Is.EqualTo(SkillIntentVerdict.Replay), "out of order (older than the last)");
            Assert.That(System.Enum.GetNames(typeof(SkillIntentVerdict)), Is.EquivalentTo(new[] { "Accepted", "Invalid", "Replay", "Flood" }));
            Assert.That(gate.LastSequence, Is.EqualTo(3u));
        }

        [Test]
        public void SharedSequenceAcrossSlots_PerSlotNumberingIsRefused()
        {
            var gate = new SkillIntentGate();
            Assert.That(gate.Submit(0, 1, 0), Is.EqualTo(SkillIntentVerdict.Accepted));
            Assert.That(gate.Submit(1, 2, 0), Is.EqualTo(SkillIntentVerdict.Accepted), "second key continues the one counter");
            Assert.That(gate.Submit(1, 1, 0), Is.EqualTo(SkillIntentVerdict.Replay), "a per-slot counter restarting at 1 is a replay");
        }

        [Test]
        public void ClockMustBeFiniteNonNegativeAndBounded_AbsurdStampIsOneInvalidRequestNotAPoisonedGate()
        {
            var gate = new SkillIntentGate();
            Assert.That(gate.Submit(0, 1, -0.001), Is.EqualTo(SkillIntentVerdict.Invalid));
            Assert.That(gate.Submit(0, 1, SkillIntentGate.MaxClockSeconds * 2.0), Is.EqualTo(SkillIntentVerdict.Invalid));
            Assert.That(gate.Submit(0, 1, 1.0e308), Is.EqualTo(SkillIntentVerdict.Invalid));
            Assert.That(gate.Submit(0, 1, double.NegativeInfinity), Is.EqualTo(SkillIntentVerdict.Invalid));
            Assert.That(gate.LastSequence, Is.EqualTo(0u));
            Assert.That(gate.Submit(0, 1, 5.0), Is.EqualTo(SkillIntentVerdict.Accepted), "the gate was not poisoned");
            Assert.That(gate.Submit(0, 2, 5.0), Is.EqualTo(SkillIntentVerdict.Accepted));
            Assert.That(gate.Submit(0, 3, SkillIntentGate.MaxClockSeconds), Is.EqualTo(SkillIntentVerdict.Accepted), "the bound itself is legal");
        }

        [Test]
        public void SequenceCeiling_FloodsCannotWrapAndTheGateReportsExhaustion()
        {
            var fresh = new SkillIntentGate();
            Assert.That(fresh.Submit(0, SkillIntentGate.SequenceCeiling + 1u, 0), Is.EqualTo(SkillIntentVerdict.Invalid));
            Assert.That(fresh.Exhausted, Is.False);

            // Walk the whole space in the largest legal steps; a frozen clock makes every request after the burst a (free) Flood.
            var gate = new SkillIntentGate();
            uint sequence = 0;
            while (sequence + SkillIntentGate.MaxSequenceJump <= SkillIntentGate.SequenceCeiling)
            {
                sequence += SkillIntentGate.MaxSequenceJump;
                SkillIntentVerdict v = gate.Submit(0, sequence, 0);
                if (v == SkillIntentVerdict.Replay || v == SkillIntentVerdict.Invalid) Assert.Fail("walk stopped at " + sequence + " with " + v);
            }

            Assert.That(gate.Exhausted, Is.False);
            Assert.That(gate.Submit(0, SkillIntentGate.SequenceCeiling, 0), Is.EqualTo(SkillIntentVerdict.Flood));
            Assert.That(gate.Exhausted, Is.True);
            Assert.That(gate.Submit(0, SkillIntentGate.SequenceCeiling + 1u, 0), Is.EqualTo(SkillIntentVerdict.Invalid));
            Assert.That(gate.Submit(0, 1, 0), Is.EqualTo(SkillIntentVerdict.Replay), "no wrap-around to low numbers");
        }

        [Test]
        public void Probation_GateStartsWithNoTokensAndRefillsFromAdmission()
        {
            var gate = new SkillIntentGate(0.0, 10.0);
            Assert.That(gate.Tokens, Is.EqualTo(0.0));
            Assert.That(gate.Submit(0, 1, 10.0), Is.EqualTo(SkillIntentVerdict.Flood), "no burst on a returning abusive slot");
            Assert.That(gate.Submit(0, 2, 10.25), Is.EqualTo(SkillIntentVerdict.Accepted), "a quarter second later one token exists");
            Assert.That(gate.Submit(0, 3, 10.25), Is.EqualTo(SkillIntentVerdict.Flood));

            var normal = new SkillIntentGate();
            for (uint i = 1; i <= 4; i++) Assert.That(normal.Submit(0, i, 10.0), Is.EqualTo(SkillIntentVerdict.Accepted));
        }

        [Test]
        public void Probation_BadStartValuesFallBackToTheNormalGate()
        {
            var gate = new SkillIntentGate(double.NaN, -5.0);
            Assert.That(gate.Tokens, Is.EqualTo(SkillIntentGate.Burst));
            for (uint i = 1; i <= 4; i++) Assert.That(gate.Submit(0, i, 0.0), Is.EqualTo(SkillIntentVerdict.Accepted));
            var capped = new SkillIntentGate(99.0, 0.0);
            Assert.That(capped.Tokens, Is.EqualTo(SkillIntentGate.Burst));
        }

        [Test]
        public void CountersFollowVerdicts()
        {
            var gate = new SkillIntentGate();
            gate.Submit(0, 1, 0);              // accepted
            gate.Submit(0, 1, 0);              // replay
            gate.Submit(7, 2, 0);              // invalid
            gate.Submit(0, 0, 0);              // invalid
            for (uint i = 2; i <= 8; i++) gate.Submit(1, i, 0); // 2,3,4 accepted, 5..8 flood
            Assert.That(gate.Accepted, Is.EqualTo(4));
            Assert.That(gate.Replay, Is.EqualTo(1));
            Assert.That(gate.Invalid, Is.EqualTo(2));
            Assert.That(gate.Flood, Is.EqualTo(4));
        }
    }
}
