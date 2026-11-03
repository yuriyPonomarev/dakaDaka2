using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    public sealed class CombatIntentGateTests
    {
        [Test]
        public void EveryKindIsAcceptedInOrderAndReplayCannotRepeatIt()
        {
            var gate = new CombatIntentGate();
            uint seq = 1;
            for (int kind = 1; kind <= CombatNetLimits.KindMax; kind++, seq++)
                Assert.That(gate.Submit(kind, seq, 0), Is.EqualTo(SkillIntentVerdict.Accepted), "kind " + kind);
            Assert.That(gate.Submit(1, 1, 1), Is.EqualTo(SkillIntentVerdict.Replay));
            Assert.That(gate.Submit(2, seq - 1, 1), Is.EqualTo(SkillIntentVerdict.Replay));
            Assert.That(gate.LastSequence, Is.EqualTo(seq - 1));
        }

        [Test]
        public void InvalidInputsDoNotMoveTheSequenceOrConsumeBudget()
        {
            var gate = new CombatIntentGate();
            Assert.That(gate.Submit(0, 1, 0), Is.EqualTo(SkillIntentVerdict.Invalid));
            Assert.That(gate.Submit(CombatNetLimits.KindMax + 1, 1, 0), Is.EqualTo(SkillIntentVerdict.Invalid));
            Assert.That(gate.Submit(-3, 1, 0), Is.EqualTo(SkillIntentVerdict.Invalid));
            Assert.That(gate.Submit(1, 0, 0), Is.EqualTo(SkillIntentVerdict.Invalid));
            Assert.That(gate.Submit(1, 1 + CombatIntentGate.MaxSequenceJump + 1, 0), Is.EqualTo(SkillIntentVerdict.Invalid));
            Assert.That(gate.Submit(1, 1, double.NaN), Is.EqualTo(SkillIntentVerdict.Invalid));
            Assert.That(gate.Submit(1, 1, double.PositiveInfinity), Is.EqualTo(SkillIntentVerdict.Invalid));
            Assert.That(gate.LastSequence, Is.EqualTo(0u));
            Assert.That(gate.Invalid, Is.EqualTo(7));
            for (uint i = 1; i <= (uint)CombatIntentGate.Burst; i++)
                Assert.That(gate.Submit(1, i, 0), Is.EqualTo(SkillIntentVerdict.Accepted));
        }

        [Test]
        public void FloodIsBoundedByTheBurstAndRefillsWithTime()
        {
            var gate = new CombatIntentGate();
            uint seq = 1;
            int accepted = 0, flood = 0;
            for (int i = 0; i < 100; i++, seq++)
            {
                SkillIntentVerdict v = gate.Submit(2, seq, 0);
                if (v == SkillIntentVerdict.Accepted) accepted++;
                else if (v == SkillIntentVerdict.Flood) flood++;
            }

            Assert.That(accepted, Is.EqualTo((int)CombatIntentGate.Burst));
            Assert.That(flood, Is.EqualTo(100 - accepted));
            Assert.That(gate.Flood, Is.EqualTo(flood));

            // a second later the budget has refilled by the rate, and the sequence alignment was kept
            Assert.That(gate.Submit(3, seq, 1.0), Is.EqualTo(SkillIntentVerdict.Accepted));
            Assert.That(gate.LastSequence, Is.EqualTo(seq));
        }

        [Test]
        public void ProbationGateStartsEmptyAndRefillsFromAdmission()
        {
            var gate = new CombatIntentGate(0.0, 10.0);
            Assert.That(gate.Submit(1, 1, 10.0), Is.EqualTo(SkillIntentVerdict.Flood));
            Assert.That(gate.Submit(1, 2, 10.5), Is.EqualTo(SkillIntentVerdict.Accepted));
        }

        [Test]
        public void ASequenceBeyondTheCeilingIsInvalid()
        {
            var gate = new CombatIntentGate();
            Assert.That(gate.Exhausted, Is.False);
            Assert.That(gate.Submit(1, uint.MaxValue, 0), Is.EqualTo(SkillIntentVerdict.Invalid));
            Assert.That(gate.Submit(1, CombatIntentGate.SequenceCeiling + 1u, 0), Is.EqualTo(SkillIntentVerdict.Invalid));
            Assert.That(gate.LastSequence, Is.EqualTo(0u));
        }
    }
}
