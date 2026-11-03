using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    public sealed class ServerDrawTimerTests
    {
        [Test]
        public void ChargeIsMeasuredFromTheServerClockAndCapsAtOne()
        {
            var timer = new ServerDrawTimer();
            Assert.That(timer.Begin(10.0, 1.0f, 0.25f), Is.True);
            Assert.That(timer.ChargeAt(10.5), Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(timer.ChargeAt(99.0), Is.EqualTo(1f));
            Assert.That(timer.ChargeAt(9.0), Is.EqualTo(0f), "a clock that went backwards has no charge");
        }

        [Test]
        public void ReleaseBelowTheMinimumFiresNothingAndAlwaysLeavesTheTimerIdle()
        {
            var timer = new ServerDrawTimer();
            timer.Begin(0.0, 2.0f, 0.5f);
            DrawReleaseResult early = timer.Release(0.4);
            Assert.That(early.Outcome, Is.EqualTo(DrawReleaseOutcome.TooEarly));
            Assert.That(early.Charge, Is.EqualTo(0.2f).Within(1e-5f));
            Assert.That(timer.IsDrawing, Is.False);
            Assert.That(timer.TooEarly, Is.EqualTo(1));

            timer.Begin(5.0, 2.0f, 0.5f);
            DrawReleaseResult ok = timer.Release(6.5);
            Assert.That(ok.Outcome, Is.EqualTo(DrawReleaseOutcome.Fire));
            Assert.That(ok.Charge, Is.EqualTo(0.75f).Within(1e-5f));
            Assert.That(timer.Fired, Is.EqualTo(1));
        }

        [Test]
        public void ReleaseWithoutADrawIsNotDrawing()
        {
            var timer = new ServerDrawTimer();
            Assert.That(timer.Release(1.0).Outcome, Is.EqualTo(DrawReleaseOutcome.NotDrawing));
        }

        [Test]
        public void SecondBeginWhileDrawingAndUnusableNumbersAreRefused()
        {
            var timer = new ServerDrawTimer();
            Assert.That(timer.Begin(double.NaN, 1f, 0f), Is.False);
            Assert.That(timer.Begin(0.0, 0f, 0f), Is.False);
            Assert.That(timer.Begin(0.0, float.PositiveInfinity, 0f), Is.False);
            Assert.That(timer.Begin(0.0, 1f, 0f), Is.True);
            Assert.That(timer.Begin(0.5, 1f, 0f), Is.False);
            timer.Cancel();
            Assert.That(timer.IsDrawing, Is.False);
            Assert.That(timer.Begin(0.5, 1f, 0f), Is.True);
        }

        [Test]
        public void AnAbandonedDrawGoesStaleAfterTheOverholdLimit()
        {
            var timer = new ServerDrawTimer();
            timer.Begin(0.0, 1.0f, 0f);
            Assert.That(timer.IsStale(1.0 + CombatNetLimits.MaxDrawOverholdSeconds - 0.5), Is.False);
            Assert.That(timer.IsStale(1.0 + CombatNetLimits.MaxDrawOverholdSeconds + 0.5), Is.True);
        }
    }
}
