using Emberdelve.Rules;
using NUnit.Framework;

namespace Emberdelve.Rules.Tests
{
    public sealed class RaidCountdownRulesTests
    {
        [Test]
        public void DefaultDurationIsTenMinutes()
        {
            Assert.That(RaidCountdownRules.DefaultDurationSeconds, Is.EqualTo(600));
            Assert.That(RaidCountdownRules.NormalizeDurationSeconds(0), Is.EqualTo(600));
            Assert.That(RaidCountdownRules.NormalizeDurationSeconds(-5), Is.EqualTo(600));
        }

        [Test]
        public void ConfiguredDurationIsPreservedAndCountsDownToZero()
        {
            Assert.That(RaidCountdownRules.NormalizeDurationSeconds(90), Is.EqualTo(90));
            Assert.That(RaidCountdownRules.RemainingSeconds(90, 0f), Is.EqualTo(90f));
            Assert.That(RaidCountdownRules.RemainingSeconds(90, 12.5f), Is.EqualTo(77.5f));
            Assert.That(RaidCountdownRules.RemainingSeconds(90, 89.99f), Is.EqualTo(0.01f).Within(0.0001f));
        }

        [Test]
        public void DeadlineIsInclusiveAndNeverProducesNegativeTime()
        {
            Assert.That(RaidCountdownRules.HasExpired(10, 9.999f), Is.False);
            Assert.That(RaidCountdownRules.HasExpired(10, 10f), Is.True);
            Assert.That(RaidCountdownRules.HasExpired(10, 15f), Is.True);
            Assert.That(RaidCountdownRules.RemainingSeconds(10, 15f), Is.Zero);
        }

        [Test]
        public void ExtractionWindowClosesAtTheExactDeadline()
        {
            Assert.That(RaidCountdownRules.IsExtractionWindowOpen(10, 9.999f), Is.True);
            Assert.That(RaidCountdownRules.IsExtractionWindowOpen(10, 10f), Is.False);
            Assert.That(RaidCountdownRules.IsExtractionWindowOpen(10, 10.001f), Is.False);
        }

        [Test]
        public void ClientClockCannotCloseExtractionBeforeServerDecision()
        {
            // A client that loaded later can be past its local deadline while the server still has time remaining.
            Assert.That(RaidCountdownRules.IsExtractionWindowOpen(10, 12f, false), Is.True);
            Assert.That(RaidCountdownRules.IsExtractionWindowOpen(10, 9.999f, true), Is.True);
            Assert.That(RaidCountdownRules.IsExtractionWindowOpen(10, 10f, true), Is.False);
        }

        [Test]
        public void ElapsedTimeExcludesCompletedOfflinePauseIntervals()
        {
            Assert.That(RaidCountdownRules.ElapsedSeconds(10f, 100f, 30f, 0f, false), Is.EqualTo(60f));
        }

        [Test]
        public void ElapsedTimeFreezesAtPauseStartUntilOfflineResume()
        {
            Assert.That(RaidCountdownRules.ElapsedSeconds(10f, 100f, 0f, 40f, true), Is.EqualTo(30f));
            Assert.That(RaidCountdownRules.ElapsedSeconds(10f, 115f, 0f, 40f, true), Is.EqualTo(30f));
        }

        [Test]
        public void InvalidElapsedTimeCannotPrematurelyExpireTheRaid()
        {
            Assert.That(RaidCountdownRules.RemainingSeconds(10, float.NaN), Is.EqualTo(10f));
            Assert.That(RaidCountdownRules.RemainingSeconds(10, -1f), Is.EqualTo(10f));
            Assert.That(RaidCountdownRules.HasExpired(10, float.PositiveInfinity), Is.True);
        }
    }
}
