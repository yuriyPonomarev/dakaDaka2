using Emberdelve.BladeDefense;
using NUnit.Framework;

namespace Emberdelve.Rules.Tests
{
    public sealed class ImpactGuardWindowTests
    {
        [Test]
        public void FirstHeldRaiseActivates_AndExpiresExactlyWithoutHoldRefresh()
        {
            var window = new ImpactGuardWindow();
            window.Update(true, true, 1, 8f, 1f, 2f);
            Assert.That(window.Remaining(8f), Is.EqualTo(1f));
            window.Update(true, true, 1, 8.5f, 1f, 2f);
            Assert.That(window.Remaining(8.5f), Is.EqualTo(.5f));
            Assert.That(window.Remaining(9f), Is.Zero);
            window.Update(true, true, 1, 12f, 1f, 2f);
            Assert.That(window.Remaining(12f), Is.Zero);
        }

        [TestCase(2f, 1f, 10f)]
        [TestCase(1f, 3f, 11f)]
        public void ReleaseClearsWindow_ButRearmWaitsForLongerDelay(float duration, float delay, float ready)
        {
            var window = new ImpactGuardWindow();
            window.Update(true, true, 1, 8f, duration, delay);
            window.Update(true, false, 1, 8.25f, duration, delay);
            Assert.That(window.Remaining(8.25f), Is.Zero);
            window.Update(true, true, 1, ready - .5f, duration, delay);
            Assert.That(window.Remaining(ready - .5f), Is.Zero);
            window.Update(true, true, 1, ready, duration, delay);
            Assert.That(window.Remaining(ready), Is.Zero, "waiting held does not create a new edge");
            window.Update(true, false, 1, ready, duration, delay);
            window.Update(true, true, 1, ready, duration, delay);
            Assert.That(window.Remaining(ready), Is.EqualTo(duration));
        }

        [Test]
        public void WeaponChangeClearsWindow_PreservesCooldown_AndRequiresRelease()
        {
            var window = new ImpactGuardWindow();
            window.Update(true, true, 1, 8f, 1f, 3f);
            window.Update(true, true, 2, 8.25f, 1f, 3f);
            Assert.That(window.Remaining(8.25f), Is.Zero);
            window.Update(true, false, 2, 8.5f, 1f, 3f);
            window.Update(true, true, 2, 9f, 1f, 3f);
            Assert.That(window.Remaining(9f), Is.Zero);
            window.Update(true, true, 2, 11f, 1f, 3f);
            Assert.That(window.Remaining(11f), Is.Zero);
            window.Update(true, false, 2, 11f, 1f, 3f);
            window.Update(true, true, 2, 11f, 1f, 3f);
            Assert.That(window.Remaining(11f), Is.EqualTo(1f));
        }

        [Test]
        public void LosingEligibilityClearsWindowWithoutResettingCooldown()
        {
            var window = new ImpactGuardWindow();
            window.Update(true, true, 1, 8f, 1f, 3f);
            window.Update(false, false, 1, 8.25f, 1f, 3f);
            Assert.That(window.Remaining(8.25f), Is.Zero);
            window.Update(true, true, 1, 9f, 1f, 3f);
            Assert.That(window.Remaining(9f), Is.Zero);
        }

        [TestCase(float.NaN, 1f, 2f)]
        [TestCase(float.PositiveInfinity, 1f, 2f)]
        [TestCase(float.NegativeInfinity, 1f, 2f)]
        [TestCase(8.25f, float.NaN, 2f)]
        [TestCase(8.25f, float.PositiveInfinity, 2f)]
        [TestCase(8.25f, 0f, 2f)]
        [TestCase(8.25f, -1f, 2f)]
        [TestCase(8.25f, 1f, float.NaN)]
        [TestCase(8.25f, 1f, float.PositiveInfinity)]
        public void InvalidInputFailsClosed_AndDoesNotCreateANewHeldEdge(float now, float duration, float delay)
        {
            var window = new ImpactGuardWindow();
            window.Update(true, true, 1, 8f, 1f, 2f);
            window.Update(true, true, 1, now, duration, delay);
            Assert.That(window.Remaining(8.5f), Is.Zero);
            window.Update(true, true, 1, 12f, 1f, 2f);
            Assert.That(window.Remaining(12f), Is.Zero);
        }

        [Test]
        public void RewoundClockCannotReactivateOrExtendWindow()
        {
            var window = new ImpactGuardWindow();
            window.Update(true, true, 1, 8f, 1f, 2f);
            Assert.That(window.Remaining(7f), Is.Zero);
            Assert.That(window.Remaining(float.NaN), Is.Zero);
            window.Update(true, false, 1, 7f, 1f, 2f);
            window.Update(true, true, 1, 7.5f, 1f, 2f);
            Assert.That(window.Remaining(8f), Is.Zero);
            window.Update(true, true, 1, 12f, 1f, 2f);
            Assert.That(window.Remaining(12f), Is.Zero);
        }

        [Test]
        public void ResetAllowsFreshHeldActivationWithANewClock()
        {
            var window = new ImpactGuardWindow();
            window.Update(true, true, 1, 8f, 1f, 20f);
            window.Reset();
            Assert.That(window.Remaining(8f), Is.Zero);
            window.Update(true, true, 2, 0f, 1f, 2f);
            Assert.That(window.Remaining(0f), Is.EqualTo(1f));
        }
    }
}
