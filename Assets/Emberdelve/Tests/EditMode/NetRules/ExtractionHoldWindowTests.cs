using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    public sealed class ExtractionHoldWindowTests
    {
        [Test]
        public void ServerWindowRequiresTheConfiguredTimeAndSameSequence()
        {
            Assert.That(ExtractionHoldWindow.TryStart(3, 10.0, 3f, 1f, 2f, 3f, out ExtractionHoldWindow hold), Is.True);
            Assert.That(hold.Evaluate(3, 12.999, 1f, 2f, 3f), Is.EqualTo(ExtractionHoldVerdict.TooEarly));
            Assert.That(hold.Evaluate(3, 13.0, 1f, 2f, 3f), Is.EqualTo(ExtractionHoldVerdict.Valid));
            Assert.That(hold.Evaluate(4, 13.0, 1f, 2f, 3f), Is.EqualTo(ExtractionHoldVerdict.WrongSequence));
        }

        [Test]
        public void AnyAuthoritativeDisplacementBeyondToleranceInterrupts()
        {
            Assert.That(ExtractionHoldWindow.TryStart(1, 0, 1f, 0f, 0f, 0f, out ExtractionHoldWindow hold), Is.True);
            Assert.That(hold.Evaluate(1, 1, 0.04f, 0f, 0f), Is.EqualTo(ExtractionHoldVerdict.Valid));
            Assert.That(hold.Evaluate(1, 1, 0.051f, 0f, 0f), Is.EqualTo(ExtractionHoldVerdict.Moved));
            Assert.That(hold.Evaluate(1, 1, 0f, 0.06f, 0f), Is.EqualTo(ExtractionHoldVerdict.Moved));
        }

        [Test]
        public void InvalidAndLateWindowsCannotComplete()
        {
            Assert.That(ExtractionHoldWindow.TryStart(0, 1, 1f, 0f, 0f, 0f, out _), Is.False);
            Assert.That(ExtractionHoldWindow.TryStart(1, 1, 0f, 0f, 0f, 0f, out _), Is.False);
            Assert.That(ExtractionHoldWindow.TryStart(1, double.NaN, 1f, 0f, 0f, 0f, out _), Is.False);
            Assert.That(ExtractionHoldWindow.TryStart(1, 1, 1f, 0f, 0f, 0f, out ExtractionHoldWindow hold), Is.True);
            Assert.That(hold.Evaluate(1, 3.999, 0f, 0f, 0f), Is.EqualTo(ExtractionHoldVerdict.Valid));
            Assert.That(hold.Evaluate(1, 4.0, 0f, 0f, 0f), Is.EqualTo(ExtractionHoldVerdict.Expired), "grace boundary is inclusive");
            Assert.That(hold.Evaluate(1, 4.001, 0f, 0f, 0f), Is.EqualTo(ExtractionHoldVerdict.Expired));
            Assert.That(hold.Evaluate(1, 0, 0f, 0f, 0f), Is.EqualTo(ExtractionHoldVerdict.Expired));
        }

        [Test]
        public void HeartbeatLeaseRejectsStaleOrInvalidClocks()
        {
            Assert.That(ExtractionHoldWindow.IsHeartbeatFresh(5, 6, 1.0), Is.True);
            Assert.That(ExtractionHoldWindow.IsHeartbeatFresh(5, 6.01, 1.0), Is.False);
            Assert.That(ExtractionHoldWindow.IsHeartbeatFresh(6, 5, 1.0), Is.False);
            Assert.That(ExtractionHoldWindow.IsHeartbeatFresh(double.NaN, 6, 1.0), Is.False);
        }

        [Test]
        public void ServerCommitCanBeSealedOnlyOnce()
        {
            var latch = new ExtractionHoldCommitLatch();
            Assert.That(latch.TrySeal(), Is.True);
            Assert.That(latch.TrySeal(), Is.False);
            Assert.That(latch.IsSealed, Is.True);
        }
    }
}
