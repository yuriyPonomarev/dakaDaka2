using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.Tests.NetRules
{
    public sealed class EnemyHitPresentationQueueTests
    {
        [Test]
        public void HitDuringWindupWaitsUntilAttackPlaybackEnds()
        {
            var queue = new EnemyHitPresentationQueue();
            queue.Reset(0);
            queue.Observe(1, false);

            Assert.That(queue.HasPending, Is.True);
            Assert.That(queue.TryConsume(false, windingUp: true, swingStarted: false, attackPlaybackActive: false), Is.False);
            Assert.That(queue.TryConsume(false, windingUp: false, swingStarted: true, attackPlaybackActive: true), Is.False);
            Assert.That(queue.TryConsume(false, windingUp: false, swingStarted: false, attackPlaybackActive: true), Is.False);
            Assert.That(queue.HasPending, Is.True, "the hit edge remains queued during the attack clip");
            Assert.That(queue.TryConsume(false, windingUp: false, swingStarted: false, attackPlaybackActive: false), Is.True);
            Assert.That(queue.HasPending, Is.False);
            Assert.That(queue.TryConsume(false, windingUp: false, swingStarted: false, attackPlaybackActive: false), Is.False,
                "one authoritative hit produces one flinch");
        }

        [Test]
        public void DeadStateDiscardsAQueuedHit()
        {
            var queue = new EnemyHitPresentationQueue();
            queue.Reset(0);
            queue.Observe(1, false);
            Assert.That(queue.TryConsume(true, windingUp: false, swingStarted: false, attackPlaybackActive: false), Is.False);
            Assert.That(queue.HasPending, Is.False);
            Assert.That(queue.TryConsume(false, windingUp: false, swingStarted: false, attackPlaybackActive: false), Is.False);
        }

        [Test]
        public void InitialSnapshotSeedsSequenceWithoutReplayingHistory()
        {
            var queue = new EnemyHitPresentationQueue();
            queue.Reset(17);
            queue.Observe(17, false);
            Assert.That(queue.HasPending, Is.False);
            Assert.That(queue.TryConsume(false, windingUp: false, swingStarted: false, attackPlaybackActive: false), Is.False);
        }

        [Test]
        public void ReliableHitAfterInitialSnapshotSurvivesUntilFirstPoseCreatesProxy()
        {
            // The presentation channel can deliver multiple reliable states before the first unreliable pose creates a proxy.
            var queue = new EnemyHitPresentationQueue();
            queue.Reset(12); // first state is the joiner's historical baseline
            queue.Observe(13, false); // a new hit arrives while there is no proxy yet
            queue.Observe(15, false); // repeated hits coalesce while the attack is busy

            Assert.That(queue.HasPending, Is.True, "the edge after the baseline must survive pre-proxy snapshots");
            Assert.That(queue.TryConsume(false, windingUp: true, swingStarted: false, attackPlaybackActive: true), Is.False);
            Assert.That(queue.TryConsume(false, windingUp: false, swingStarted: false, attackPlaybackActive: false), Is.True,
                "the new edge eventually produces a reaction after proxy creation");
            Assert.That(queue.TryConsume(false, windingUp: false, swingStarted: false, attackPlaybackActive: false), Is.False,
                "coalesced hit sequences produce one non-overlapping flinch");
        }
    }
}
