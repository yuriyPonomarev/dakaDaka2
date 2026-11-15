using System;
using Emberdelve.Rules;
using NUnit.Framework;

namespace Emberdelve.Rules.Tests.DescentPortals
{
    public sealed class DescentPortalScheduleTests
    {
        [TestCase(RoyalePartyMode.Solo, 1)]
        [TestCase(RoyalePartyMode.Duo, 2)]
        [TestCase(RoyalePartyMode.Trio, 3)]
        public void ThreeGroupsAppearOnAuthorityTimeWithFinalWaveInLastMinute(RoyalePartyMode mode, int size)
        {
            var schedule = new DescentPortalSchedule(600, mode,
                new DescentPortalWave(300, 1), new DescentPortalWave(420, 1), new DescentPortalWave(540, 1));
            Assert.That(schedule.PortalsPerGroup, Is.EqualTo(size)); Assert.That(schedule.TotalGroups, Is.EqualTo(3));
            Assert.False(schedule.IsGroupOpen(0, 299.999)); Assert.True(schedule.IsGroupOpen(0, 300));
            Assert.False(schedule.IsGroupOpen(1, 419.999)); Assert.True(schedule.IsGroupOpen(1, 420));
            Assert.False(schedule.IsGroupOpen(2, 539.999)); Assert.True(schedule.IsGroupOpen(2, 540));
            Assert.True(schedule.IsGroupOpen(2, 599.999)); Assert.False(schedule.IsGroupOpen(0, 600));
            Assert.False(schedule.IsGroupOpen(-1, 550)); Assert.False(schedule.IsGroupOpen(3, 550));
        }

        [Test]
        public void SkippedFramesAndRepeatedQueriesDoNotChangeSchedule()
        {
            var waves = new[] { new DescentPortalWave(300, 2), new DescentPortalWave(570, 1) };
            var schedule = new DescentPortalSchedule(600, RoyalePartyMode.Trio, waves);
            waves[0] = new DescentPortalWave(0, 64);
            Assert.True(schedule.TryGetAvailableGroups(580, out int count)); Assert.That(count, Is.EqualTo(3));
            Assert.True(schedule.TryGetAvailableGroups(310, out count)); Assert.That(count, Is.EqualTo(2));
            Assert.True(schedule.TryGetAvailableGroups(580, out count)); Assert.That(count, Is.EqualTo(3));
            Assert.False(schedule.TryGetAvailableGroups(double.NaN, out count)); Assert.That(count, Is.Zero);
            Assert.False(schedule.TryGetAvailableGroups(-1, out _));
        }

        [Test]
        public void UnsafeTimelineIsRejectedInsteadOfSilentlyOmittingFinalWave()
        {
            Assert.Throws<ArgumentException>(() => new DescentPortalSchedule(600, RoyalePartyMode.Solo, new DescentPortalWave(539, 3)));
            Assert.Throws<ArgumentException>(() => new DescentPortalSchedule(600, RoyalePartyMode.Solo, new DescentPortalWave(600, 3)));
            Assert.Throws<ArgumentException>(() => new DescentPortalSchedule(600, RoyalePartyMode.Solo, new DescentPortalWave(550, 1), new DescentPortalWave(540, 2)));
            Assert.Throws<ArgumentException>(() => new DescentPortalSchedule(600, RoyalePartyMode.Solo, new DescentPortalWave(540, 0)));
        }
    }
}
