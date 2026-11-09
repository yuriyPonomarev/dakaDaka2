using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.Tests.NetRules
{
    public sealed class InventoryIntentGateTests
    {
        [Test]
        public void RejectsReplayInvalidJumpAndClockWithoutAdvancing()
        {
            var gate = new InventoryIntentGate();
            Assert.AreEqual(SkillIntentVerdict.Invalid, gate.Submit(0, 1));
            Assert.AreEqual(SkillIntentVerdict.Invalid, gate.Submit(65, 1));
            Assert.AreEqual(SkillIntentVerdict.Invalid, gate.Submit(1, double.NaN));
            Assert.AreEqual(0, gate.LastSequence);
            Assert.AreEqual(SkillIntentVerdict.Accepted, gate.Submit(1, 1));
            Assert.AreEqual(SkillIntentVerdict.Replay, gate.Submit(1, 2));
            Assert.AreEqual(SkillIntentVerdict.Accepted, gate.Submit(2, 2));
        }

        [Test]
        public void FloodConsumesSequenceAndServerClockRefillsBudget()
        {
            var gate = new InventoryIntentGate();
            for (ulong i = 1; i <= 10; i++) Assert.AreEqual(SkillIntentVerdict.Accepted, gate.Submit(i, 10));
            Assert.AreEqual(SkillIntentVerdict.Flood, gate.Submit(11, 10));
            Assert.AreEqual(SkillIntentVerdict.Replay, gate.Submit(11, 11));
            Assert.AreEqual(SkillIntentVerdict.Accepted, gate.Submit(12, 11));
            Assert.AreEqual(SkillIntentVerdict.Invalid, gate.Submit(InventoryIntentGate.SequenceCeiling + 1, 11));
        }
    }
}
