using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    public sealed class SkillIntentGateTests
    {
        [Test]
        public void BothSlotsAcceptNewIntentsAndReplayCannotRepeatThem()
        {
            var gate = new SkillIntentGate();
            Assert.That(gate.Submit(0, 1, 0), Is.EqualTo(SkillIntentVerdict.Accepted));
            Assert.That(gate.Submit(1, 2, 0), Is.EqualTo(SkillIntentVerdict.Accepted));
            Assert.That(gate.Submit(1, 2, 1), Is.EqualTo(SkillIntentVerdict.Replay));
            Assert.That(gate.Submit(0, 1, 1), Is.EqualTo(SkillIntentVerdict.Replay));
            Assert.That(gate.LastSequence, Is.EqualTo(2u));
        }

        [Test]
        public void InvalidInputsDoNotMoveTheSequenceOrConsumeBudget()
        {
            var gate = new SkillIntentGate();
            Assert.That(gate.Submit(-1, 1, 0), Is.EqualTo(SkillIntentVerdict.Invalid));
            Assert.That(gate.Submit(2, 1, 0), Is.EqualTo(SkillIntentVerdict.Invalid));
            Assert.That(gate.Submit(0, 0, 0), Is.EqualTo(SkillIntentVerdict.Invalid));
            Assert.That(gate.Submit(0, 65, 0), Is.EqualTo(SkillIntentVerdict.Invalid));
            Assert.That(gate.Submit(0, 1, double.NaN), Is.EqualTo(SkillIntentVerdict.Invalid));
            Assert.That(gate.Submit(0, 1, double.PositiveInfinity), Is.EqualTo(SkillIntentVerdict.Invalid));
            Assert.That(gate.LastSequence, Is.EqualTo(0u));
            for (uint i = 1; i <= 4; i++)
                Assert.That(gate.Submit(0, i, 0), Is.EqualTo(SkillIntentVerdict.Accepted));
        }

        [Test]
        public void FloodIsBoundedAndRecoveryDoesNotLoseSequenceAlignment()
        {
            var gate = new SkillIntentGate();
            for (uint i = 1; i <= 4; i++)
                Assert.That(gate.Submit(0, i, 0), Is.EqualTo(SkillIntentVerdict.Accepted));
            for (uint i = 5; i <= 100; i++)
                Assert.That(gate.Submit(1, i, 0), Is.EqualTo(SkillIntentVerdict.Flood));
            Assert.That(gate.LastSequence, Is.EqualTo(100u));
            Assert.That(gate.Submit(1, 101, .25), Is.EqualTo(SkillIntentVerdict.Accepted));
            Assert.That(gate.Submit(1, 101, 1), Is.EqualTo(SkillIntentVerdict.Replay));
        }

        [Test]
        public void JumpBoundaryBackwardClockAndIndependentConnections()
        {
            var first = new SkillIntentGate();
            var second = new SkillIntentGate();
            Assert.That(first.Submit(0, 64, 10), Is.EqualTo(SkillIntentVerdict.Accepted));
            Assert.That(first.Submit(0, 129, 10), Is.EqualTo(SkillIntentVerdict.Invalid));
            Assert.That(first.Submit(0, 65, 9), Is.EqualTo(SkillIntentVerdict.Accepted));
            Assert.That(first.Submit(0, 66, 9), Is.EqualTo(SkillIntentVerdict.Accepted));
            Assert.That(first.Submit(0, 67, 9), Is.EqualTo(SkillIntentVerdict.Accepted));
            Assert.That(first.Submit(0, 68, 9), Is.EqualTo(SkillIntentVerdict.Flood));
            Assert.That(second.Submit(0, 1, 9), Is.EqualTo(SkillIntentVerdict.Accepted));
            Assert.That(first.Submit(0, 69, 10.25), Is.EqualTo(SkillIntentVerdict.Accepted));
        }
    }
}
