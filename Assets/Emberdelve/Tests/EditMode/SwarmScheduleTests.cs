using System;
using Emberdelve.Rules;
using NUnit.Framework;

namespace Emberdelve.Rules.Tests
{
    public sealed class SwarmScheduleTests
    {
        private static SwarmSchedule Schedule() => new SwarmSchedule(0, 0, 100,
            new SwarmPhase(10, 20, 20, 0, 50, 2), new SwarmPhase(5, 10, 20, 10, 10, 5));

        [Test]
        public void WaitingMovementAndPhaseBoundariesUseAuthorityTime()
        {
            var schedule = Schedule(); Assert.That(schedule.Duration, Is.EqualTo(45));
            Assert.True(schedule.TrySample(9, out var s)); Assert.False(s.Shrinking); Assert.That(s.Radius, Is.EqualTo(100));
            Assert.That(s.SecondsToBoundary, Is.EqualTo(1));
            schedule.TrySample(10, out s); Assert.True(s.Shrinking); Assert.That(s.Radius, Is.EqualTo(100));
            schedule.TrySample(20, out s); Assert.That(s.X, Is.EqualTo(10)); Assert.That(s.Radius, Is.EqualTo(75));
            schedule.TrySample(30, out s); Assert.That(s.Phase, Is.EqualTo(1)); Assert.False(s.Shrinking);
            Assert.That(s.X, Is.EqualTo(20)); Assert.That(s.Radius, Is.EqualTo(50)); Assert.That(s.DamagePerSecond, Is.EqualTo(5));
            schedule.TrySample(40, out s); Assert.That(s.Z, Is.EqualTo(5)); Assert.That(s.Radius, Is.EqualTo(30));
            schedule.TrySample(45, out s); Assert.True(s.Completed); Assert.That(s.Radius, Is.EqualTo(10));
            schedule.TrySample(1000, out var after); Assert.That(after.Radius, Is.EqualTo(s.Radius));
        }

        [Test]
        public void SamplingIsIndependentOfPriorCallsAndCallerMutation()
        {
            var phases = new[] { new SwarmPhase(1, 10, 0, 0, 10, 3) };
            var schedule = new SwarmSchedule(0, 0, 20, phases);
            phases[0] = new SwarmPhase(0, 1, 0, 0, 0, 99);
            schedule.TrySample(6, out var expected);
            for (int i = 0; i < 200; i++) schedule.TrySample(i * .05, out _);
            schedule.TrySample(6, out var actual);
            Assert.That(actual.Radius, Is.EqualTo(15)); Assert.That(actual.Radius, Is.EqualTo(expected.Radius));
            Assert.That(actual.DamagePerSecond, Is.EqualTo(3));
        }

        [Test]
        public void SafeBoundaryIsInclusiveAndInvalidPositionIsNeverSafe()
        {
            Schedule().TrySample(20, out var s);
            Assert.True(s.Contains(85, 0)); Assert.False(s.Contains(85.001, 0));
            Assert.False(s.Contains(double.NaN, 0)); Assert.False(s.Contains(0, double.PositiveInfinity));
        }

        [TestCase(-1)] [TestCase(double.NaN)] [TestCase(double.PositiveInfinity)]
        public void InvalidElapsedTimeIsRefused(double elapsed) => Assert.False(Schedule().TrySample(elapsed, out _));

        [Test]
        public void ExpansionNonFiniteAndInvalidDurationsAreRejected()
        {
            Assert.Throws<ArgumentException>(() => new SwarmSchedule(0, 0, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SwarmSchedule(0, 0, double.NaN));
            Assert.Throws<ArgumentException>(() => new SwarmSchedule(0, 0, 10, new SwarmPhase(0, 1, 8, 0, 3, 1)));
            Assert.Throws<ArgumentException>(() => new SwarmSchedule(0, 0, 10, new SwarmPhase(0, 0, 0, 0, 3, 1)));
            Assert.Throws<ArgumentException>(() => new SwarmSchedule(0, 0, 10, new SwarmPhase(-1, 1, 0, 0, 3, 1)));
            Assert.Throws<ArgumentException>(() => new SwarmSchedule(0, 0, 10, new SwarmPhase(0, 1, 0, 0, 3, double.NaN)));
        }
    }
}
