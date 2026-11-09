using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.Tests.NetRules
{
    public sealed class FloorLootIntentGateTests
    {
        [Test]
        public void RequiresStrictSequenceAndRejectsReplay()
        {
            var gate = new FloorLootIntentGate();
            Assert.IsFalse(gate.TryAccept(0, 1, out _));
            Assert.IsFalse(gate.TryAccept(2, 1, out _));
            Assert.AreEqual(0u, gate.LastSequence);
            Assert.IsTrue(gate.TryAccept(1, 1, out _));
            Assert.IsFalse(gate.TryAccept(1, 2, out _));
            Assert.IsFalse(gate.TryAccept(3, 2, out _));
            Assert.IsTrue(gate.TryAccept(2, 2, out _));
        }

        [Test]
        public void RateLimitConsumesSequenceAndRequiresFreshRequest()
        {
            var gate = new FloorLootIntentGate();
            Assert.IsTrue(gate.TryAccept(1, 5, out _));
            Assert.IsFalse(gate.TryAccept(2, 5.05, out _));
            Assert.AreEqual(2u, gate.LastSequence);
            Assert.IsFalse(gate.TryAccept(2, 5.2, out _));
            Assert.IsTrue(gate.TryAccept(3, 5.2, out _));
        }

        [Test]
        public void RejectsNonFiniteClockAndSequenceExhaustion()
        {
            var gate = new FloorLootIntentGate();
            Assert.IsFalse(gate.TryAccept(1, double.NaN, out _));
            Assert.IsFalse(gate.TryAccept(FloorLootIntentGate.MaxSequence + 1u, 1, out _));
            Assert.IsTrue(gate.TryAccept(1, 1, out _));
        }
    }
}
