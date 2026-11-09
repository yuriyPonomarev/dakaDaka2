using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    public sealed class RunResultReceiptGateTests
    {
        [Test]
        public void OnlyTheMatchingOwnerCanReceiveOneResult()
        {
            var gate = new RunResultReceiptGate();
            Assert.That(gate.TryAccept(42, 41), Is.False, "a result for another player's object is refused");
            Assert.That(gate.HasReceived, Is.False, "a mismatch cannot consume the receipt");
            Assert.That(gate.TryAccept(42, 42), Is.True);
            Assert.That(gate.TryAccept(42, 42), Is.False, "a duplicate delivery has no second presentation effect");
            Assert.That(gate.HasReceived, Is.True);
        }

        [TestCase(0, 0)]
        [TestCase(-1, -1)]
        public void InvalidOwnerIdsCannotConsumeTheReceipt(int expected, int result)
        {
            var gate = new RunResultReceiptGate();
            Assert.That(gate.TryAccept(expected, result), Is.False);
            Assert.That(gate.HasReceived, Is.False);
        }
    }
}
