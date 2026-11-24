using System.Collections.Generic;
using Emberdelve.AI;
using Emberdelve.Core;
using Emberdelve.Data;
using NUnit.Framework;
using UnityEngine;

namespace Emberdelve.Level.Tests
{
    /// <summary>The skeleton archer (Hollow Bowman): ranged brain tactics, the ballistic aim, the bow presentation clock and its visual mapping.</summary>
    public sealed class EnemyArcherTests
    {
        private static readonly Vector3 Base = new Vector3(5000f, 0f, 5000f);   // far from any scene geometry (the level mask is 0 anyway)

        private sealed class Target : ICombatEntity
        {
            public Vector3 Center;
            public bool Dead;
            public EntityKind Kind => EntityKind.RemotePlayer;
            public int CombatId => 77;
            public int TeamId => 1;
            public Vector3 CombatCenter => Center;
            public Collider HeadCollider => null;
            public bool IsDead => Dead;
            public float CurrentHealth => 100;
            public float MaxHealth => 100;
            public void ApplyDamage(DamageInfo damage) { }
        }

        private sealed class NoWalk : INavigationProvider
        {
            public bool TryFindPath(Vector3 from, Vector3 to, List<Vector3> path) { path.Clear(); path.Add(to); return true; }
            public bool IsWalkableWorld(Vector3 world) => false;
        }

        private EnemyData _data;
        private WeaponData _bow;

        [SetUp]
        public void SetUp()
        {
            _bow = ScriptableObject.CreateInstance<WeaponData>();
            _bow.kind = WeaponKind.Ranged;
            _bow.projectileSpeed = 30f;
            _bow.projectileGravity = 9.81f;
            _data = ScriptableObject.CreateInstance<EnemyData>();
            _data.attackWeapon = _bow;
            _data.sightRange = 22f; _data.sightCone = 360f; _data.hearingRange = 9f;
            _data.attackWindup = 1f; _data.attackCooldown = 2f;
            _data.rangedMinDistance = 6f; _data.rangedMaxDistance = 14f; _data.rangedFleeDistance = 3.5f;
            _data.chaseSpeed = 2.4f; _data.patrolSpeed = 1f;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_data);
            Object.DestroyImmediate(_bow);
        }

        private EnemyBrain Brain(INavigationProvider nav = null) => new EnemyBrain(_data, nav, Base, 0, startsDormant: false);
        private static BrainOrder Tick(EnemyBrain brain, float dt, ICombatEntity target) => brain.Tick(dt, Time.time, Base, Vector3.forward, target);
        private static Target At(float forward) => new Target { Center = Base + new Vector3(0f, 1.15f, forward) };
        private static float Flat(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

        [Test]
        public void ArcherDataIsRangedAndWearsTheArcherVisual()
        {
            Assert.That(_data.IsArcher, Is.True, "a monster whose attack weapon is ranged is an archer");
            Assert.That(EnemyData.VisualVariantFor(_data, 0), Is.EqualTo(EnemyData.ArcherVisual));
            Assert.That(EnemyData.VisualVariantFor(_data, 1), Is.EqualTo(EnemyData.ArcherVisual), "the archer never alternates into a sword body");
            var melee = ScriptableObject.CreateInstance<EnemyData>();
            try
            {
                Assert.That(melee.IsArcher, Is.False);
                Assert.That(EnemyData.VisualVariantFor(melee, 0), Is.EqualTo("Minion"));
                Assert.That(EnemyData.VisualVariantFor(melee, 1), Is.EqualTo("Rogue"));
            }
            finally { Object.DestroyImmediate(melee); }
        }

        [Test]
        public void TooCloseTargetMakesTheArcherBackOffInsteadOfShooting()
        {
            EnemyBrain brain = Brain();
            Target target = At(2f);
            BrainOrder order = Tick(brain, 0.1f, target);
            Assert.That(order.RequestAttack, Is.False);
            Assert.That(brain.IsWindingUp, Is.False, "no draw while the target is inside the flee distance and a retreat exists");
            Assert.That(Flat(order.MoveTo, target.Center), Is.GreaterThan(Flat(Base, target.Center) + 1f), "it steps away from the target");
            Assert.That(order.Speed, Is.GreaterThan(0f));
        }

        [Test]
        public void InBandTheArcherDrawsForTheWindupThenLoosesOnce()
        {
            EnemyBrain brain = Brain();
            Target target = At(10f);
            Assert.That(Tick(brain, 0.05f, target).RequestAttack, Is.False);   // chase -> attack state
            Assert.That(Tick(brain, 0.05f, target).RequestAttack, Is.False);   // the draw starts
            Assert.That(brain.IsWindingUp, Is.True, "the draw is the visible telegraph");
            BrainOrder holding = Tick(brain, 0.5f, target);
            Assert.That(holding.RequestAttack, Is.False, "still drawing");
            Assert.That(holding.Speed, Is.Zero, "an archer stands still while it draws");
            Assert.That(Tick(brain, 0.6f, target).RequestAttack, Is.True, "the arrow is loosed when the draw ends");
            Assert.That(brain.IsWindingUp, Is.False);
            for (int i = 0; i < 10; i++) Assert.That(Tick(brain, 0.1f, target).RequestAttack, Is.False, "one arrow per draw; the cooldown holds the next");
        }

        [Test]
        public void TargetBeyondTheBandIsApproachedWithoutDrawing()
        {
            EnemyBrain brain = Brain();
            Target target = At(20f);
            BrainOrder order = Tick(brain, 0.1f, target);
            Assert.That(order.RequestAttack, Is.False);
            Assert.That(brain.IsWindingUp, Is.False);
            Assert.That(Flat(order.MoveTo, target.Center), Is.LessThan(Flat(Base, target.Center)), "it closes to its preferred distance");
        }

        [Test]
        public void CorneredArcherShootsAtPointBlank()
        {
            EnemyBrain brain = Brain(new NoWalk());
            Target target = At(2f);
            Tick(brain, 0.05f, target);
            Assert.That(brain.IsCornered, Is.True, "no walkable retreat");
            Tick(brain, 0.05f, target);
            Assert.That(brain.IsWindingUp, Is.True, "cornered, it draws anyway");
            Assert.That(Tick(brain, 1.1f, target).RequestAttack, Is.True);
        }

        [Test]
        public void AFollowingTargetCannotKeepTheArcherFromShootingForever()
        {
            EnemyBrain brain = Brain();
            Target target = At(2f);
            float now = 100f;
            bool drew = false;
            // The player keeps pace at 2 m (the target follows the archer: its position is relative to the brain's fixed Base here).
            for (int i = 0; i < 40 && !drew; i++)
            {
                now += 0.1f;
                brain.Tick(0.1f, now, Base, Vector3.forward, target);
                drew = brain.IsWindingUp;
                if (!drew) Assert.That(now - 100f, Is.LessThanOrEqualTo(EnemyBrain.ArcherFleeBudget + 0.35f), "it only flees for the budget");
            }
            Assert.That(drew, Is.True, "after the flee budget a close, following target is shot at point blank");
            Assert.That(now - 100f, Is.GreaterThan(EnemyBrain.ArcherFleeBudget), "it first tried to get away");
        }

        [Test]
        public void LosingTheTargetBreaksTheDrawWithoutAShot()
        {
            EnemyBrain brain = Brain();
            Target target = At(10f);
            Tick(brain, 0.05f, target);
            Tick(brain, 0.05f, target);
            Assert.That(brain.IsWindingUp, Is.True);
            target.Dead = true;
            Assert.That(Tick(brain, 1.2f, target).RequestAttack, Is.False);
            Assert.That(brain.IsWindingUp, Is.False);
        }

        [TestCase(8f, 0f)]
        [TestCase(14f, 0.4f)]
        [TestCase(25f, -0.6f)]
        public void BallisticPitchCarriesTheArrowThroughTheAimPoint(float distance, float rise)
        {
            Vector3 from = new Vector3(1f, 1.6f, 2f);
            Vector3 to = from + new Vector3(0.6f * distance, rise, 0.8f * distance);
            const float speed = 30f, gravity = 9.81f;
            Assert.That(EnemyAgent.TryBallisticPitch(from, to, speed, gravity, out float pitch), Is.True);
            Assert.That(pitch, Is.LessThan(5f), "the flat solution: aimed up (negative) or at most slightly down");

            // Fly it: the authority's convention, direction = Euler(pitch, yaw) * forward, gravity down.
            float yaw = Mathf.Atan2(to.x - from.x, to.z - from.z) * Mathf.Rad2Deg;
            Vector3 velocity = Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward * speed;
            float horizontal = new Vector2(velocity.x, velocity.z).magnitude;
            float time = new Vector2(to.x - from.x, to.z - from.z).magnitude / horizontal;
            float height = from.y + velocity.y * time - 0.5f * gravity * time * time;
            Assert.That(height, Is.EqualTo(to.y).Within(0.01f), "the arrow arrives at the aim point's height");
        }

        [Test]
        public void WithoutGravityTheAimIsTheStraightLine()
        {
            Vector3 from = Vector3.zero, to = new Vector3(0f, 2f, 10f);
            EnemyAgent.TryBallisticPitch(from, to, 30f, 0f, out float pitch);
            Assert.That(pitch, Is.EqualTo(-Mathf.Atan2(2f, 10f) * Mathf.Rad2Deg).Within(1e-3f));
        }

        [Test]
        public void OutOfBallisticReachReportsFalse()
        {
            Assert.That(EnemyAgent.TryBallisticPitch(Vector3.zero, new Vector3(0f, 0f, 200f), 30f, 9.81f, out _), Is.False);
        }

        [Test]
        public void BowClockHoldsTheFullDrawThroughALongWindupThenReleasesAndLowers()
        {
            var draw = new EnemyAttackPhase(1, 0f, 1.1f);
            Assert.That(draw.ArcherNormalizedTime(), Is.EqualTo(0f));
            var drawn = new EnemyAttackPhase(1, EnemyAttackPhase.ArcherDrawSeconds, 1.1f);
            Assert.That(drawn.ArcherNormalizedTime(), Is.EqualTo(EnemyAttackPhase.ArcherDrawEnd).Within(1e-4f));
            var held = new EnemyAttackPhase(1, 1.05f, 1.1f);
            Assert.That(held.ArcherNormalizedTime(), Is.EqualTo(EnemyAttackPhase.ArcherDrawEnd).Within(1e-4f), "the rest of the windup holds the full draw");
            Assert.That(EnemyAttackPhase.ArcherBowDraw(EnemyAttackPhase.ArcherDrawEnd), Is.EqualTo(1f).Within(1e-4f));
            Assert.That(EnemyAttackPhase.ArcherBowDraw(EnemyAttackPhase.ArcherDrawStart), Is.EqualTo(0f));

            var release = new EnemyAttackPhase(2, EnemyAttackPhase.ArcherReleaseSeconds, EnemyAttackPhase.ArcherReleaseSeconds);
            Assert.That(release.ArcherNormalizedTime(), Is.EqualTo(EnemyAttackPhase.ArcherReleaseEnd).Within(1e-4f));
            Assert.That(EnemyAttackPhase.ArcherBowDraw(EnemyAttackPhase.ArcherReleaseEnd), Is.EqualTo(0f), "the string is slack after the release");

            var lowered = new EnemyAttackPhase(3, EnemyAttackPhase.ArcherRecoverySeconds, EnemyAttackPhase.ArcherRecoverySeconds);
            Assert.That(lowered.ArcherNormalizedTime(), Is.EqualTo(EnemyAttackPhase.ArcherRecoveryEnd).Within(1e-4f));
            Assert.That(EnemyAttackPhase.ArcherBowRaise(0f), Is.EqualTo(0f));
            Assert.That(EnemyAttackPhase.ArcherBowRaise(EnemyAttackPhase.ArcherDrawStart), Is.EqualTo(1f).Within(1e-4f));
            Assert.That(EnemyAttackPhase.ArcherBowRaise(EnemyAttackPhase.ArcherReleaseEnd), Is.EqualTo(1f));
            Assert.That(EnemyAttackPhase.ArcherBowRaise(EnemyAttackPhase.ArcherRecoveryEnd), Is.EqualTo(0f).Within(1e-4f));
        }
    }
}
