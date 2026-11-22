using Emberdelve.Rules;
using NUnit.Framework;

namespace Emberdelve.Rules.Tests.DescentPortals
{
    public sealed class DescentPortalPlacementTests
    {
        private static SwarmSchedule Schedule() => new SwarmSchedule(0, 0, 100,
            new SwarmPhase(60, 60, 20, 0, 40, 1), new SwarmPhase(60, 60, 25, 0, 15, 2));

        [Test]
        public void RandomGroupsRemainSafeThroughMovingAndShrinkingZone()
        {
            var swarm = Schedule();
            var candidates = new[] { new PortalLocation(25, 0), new PortalLocation(25, 8),
                new PortalLocation(25, -8), new PortalLocation(-80, 0), new PortalLocation(double.NaN, 0) };
            Assert.True(DescentPortalPlacement.TryChoose(swarm, candidates, 3, 2, 5, 11, out var chosen));
            foreach (var p in chosen)
                for (int second = 0; second <= 240; second++)
                {
                    Assert.True(swarm.TrySample(second, out var zone));
                    Assert.True(zone.Contains(p.X + 2, p.Z)); Assert.True(zone.Contains(p.X - 2, p.Z));
                    Assert.True(zone.Contains(p.X, p.Z + 2)); Assert.True(zone.Contains(p.X, p.Z - 2));
                }
            Assert.True(DescentPortalPlacement.TryChoose(swarm, candidates, 3, 2, 5, 11, out var repeat));
            for (int i = 0; i < chosen.Length; i++)
            { Assert.That(repeat[i].X, Is.EqualTo(chosen[i].X)); Assert.That(repeat[i].Z, Is.EqualTo(chosen[i].Z)); }
        }

        [Test]
        public void ABlockedFirstRandomPickDoesNotHideAValidPair()
        {
            var candidates = new[] { new PortalLocation(21, 0), new PortalLocation(29, 0), new PortalLocation(25, 0) };
            Assert.True(DescentPortalPlacement.TryChoose(Schedule(), candidates, 2, 1, 7, 1, out var chosen));
            Assert.That(System.Math.Abs(chosen[0].X - chosen[1].X), Is.EqualTo(8));
        }

        [Test]
        public void DistinctSeedsCanChooseDifferentPortalLocations()
        {
            var candidates = new[] { new PortalLocation(25, 0), new PortalLocation(25, 8), new PortalLocation(25, -8) };
            Assert.True(DescentPortalPlacement.TryChoose(Schedule(), candidates, 1, 2, 5, 1, out var first));
            Assert.True(DescentPortalPlacement.TryChoose(Schedule(), candidates, 1, 2, 5, 2, out var second));
            Assert.That(first[0].Z, Is.Not.EqualTo(second[0].Z));
        }

        [Test]
        public void InsufficientSafeSpaceDoesNotPublishPartialOrUnsafePlacement()
        {
            var candidates = new[] { new PortalLocation(25, 0), new PortalLocation(25, 0), new PortalLocation(-80, 0) };
            Assert.False(DescentPortalPlacement.TryChoose(Schedule(), candidates, 3, 2, 5, 11, out var chosen));
            Assert.That(chosen, Is.Empty);
            Assert.False(Schedule().ContainsThroughEnd(25, 0, 16));
            Assert.False(Schedule().ContainsThroughEnd(25, 0, double.NaN));
        }

        [Test]
        public void InvalidSpacingIsRejected()
        {
            var candidates = new[] { new PortalLocation(25, 0) };
            Assert.False(DescentPortalPlacement.TryChoose(Schedule(), candidates, 1, 1, 0, 11, out _));
            Assert.False(DescentPortalPlacement.TryChoose(Schedule(), candidates, 1, 1, 1, 11, out _));
            Assert.False(DescentPortalPlacement.TryChoose(Schedule(), candidates, 1, -1, 1, 11, out _));
            Assert.False(DescentPortalPlacement.TryChoose(Schedule(), candidates, 0, 1, 1, 11, out _));
        }
    }
}
