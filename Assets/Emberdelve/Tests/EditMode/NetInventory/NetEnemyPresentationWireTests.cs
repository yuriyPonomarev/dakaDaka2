using Emberdelve.Net;
using NUnit.Framework;
using UnityEngine;
using Unity.Collections;
using Unity.Netcode;

namespace Emberdelve.Tests.NetInventory
{
    public sealed class NetEnemyPresentationWireTests
    {
        [Test]
        public void EnemyPoseRequiresPositiveIdAndFinitePositionAndYaw()
        {
            Assert.That(Pose().IsValid, Is.True);
            Assert.That(Pose(enemy: 0).IsValid, Is.False);
            Assert.That(Pose(enemy: -1).IsValid, Is.False);

            float[] invalid = { float.NaN, float.PositiveInfinity, float.NegativeInfinity };
            for (int i = 0; i < invalid.Length; i++)
            {
                float value = invalid[i];
                Assert.That(Pose(x: value).IsValid, Is.False, "X must be finite");
                Assert.That(Pose(y: value).IsValid, Is.False, "Y must be finite");
                Assert.That(Pose(z: value).IsValid, Is.False, "Z must be finite");
                Assert.That(Pose(yaw: value).IsValid, Is.False, "Yaw must be finite");
            }
        }

        [Test]
        public void AcceptsCurrentKnownPresentationAndDecodesFlags()
        {
            var state = new EnemyPresentationWire
            {
                Version = EnemyPresentationWire.CurrentVersion,
                Enemy = 42,
                VisualKind = 3,
                Flags = EnemyPresentationWire.WindingUpFlag,
                SwingsBegun = 7,
                HitSequence = 12,
                WindupSeconds = 0.65f
            };
            Assert.That(state.IsValid, Is.True);
            Assert.That(state.IsWindingUp, Is.True);
            Assert.That(state.IsDead, Is.False);
        }

        [TestCase(4)]
        [TestCase(255)]
        public void RejectsUnsupportedVisualKinds(byte kind)
        {
            var state = Valid();
            state.VisualKind = kind;
            Assert.That(state.IsValid, Is.False);
        }

        [Test]
        public void AcceptsAllFourVisualKinds()
        {
            for (byte kind = 0; kind <= 3; kind++)
            {
                var state = Valid();
                state.VisualKind = kind;
                Assert.That(state.IsValid, Is.True, "visual kind " + kind + " is a supported zero-based variant");
            }
        }

        [Test]
        public void RejectsUnknownVersionFlagsAndInvalidNumbers()
        {
            var state = Valid();
            state.Version++;
            Assert.That(state.IsValid, Is.False, "wire version mismatch is rejected");
            state = Valid(); state.Flags = 0x80;
            Assert.That(state.IsValid, Is.False, "unknown action bits are rejected");
            state = Valid(); state.WindupSeconds = float.NaN;
            Assert.That(state.IsValid, Is.False, "non-finite timing cannot reach the animator");
            state = Valid(); state.WindupSeconds = 10.01f;
            Assert.That(state.IsValid, Is.False, "unbounded timing cannot reach the animator");
        }

        [TestCase("Minion")]
        [TestCase("Rogue")]
        [TestCase("Warrior")]
        [TestCase("Mage")]
        public void ResourcesContainEachBoundSkeletonController(string variant)
        {
            GameObject prefab = Resources.Load<GameObject>("Emberdelve/NetEnemyVisuals/EnemyVisual_" + variant);
            Assert.That(prefab, Is.Not.Null, variant + " resource prefab exists");
            Animator animator = prefab.GetComponentInChildren<Animator>(true);
            Assert.That(animator, Is.Not.Null, variant + " has an Animator");
            Assert.That(animator.runtimeAnimatorController, Is.Not.Null, variant + " uses its authored controller");
            Assert.That(animator.runtimeAnimatorController.name, Does.EndWith("_" + variant));
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(-0.01f)]
        [TestCase(30.01f)]
        public void RejectsInvalidSwingDuration(float seconds)
        {
            var state = Valid();
            state.SwingSeconds = seconds;
            Assert.That(state.IsValid, Is.False);
        }

        [TestCase(true, 0.65f, 1.2f, 1.85f)]
        [TestCase(false, 0.65f, 1.2f, 1.2f)]
        [TestCase(true, 2f, 3f, 5f)]
        public void HostAndProxyPlaybackUseCompleteSwingWithoutSpeedClamping(bool telegraph, float windup, float swing, float expected)
        {
            Assert.That(Emberdelve.AI.EnemyVisualDriver.AttackPlaybackSeconds(telegraph, windup, swing),
                Is.EqualTo(expected).Within(0.0001f));
        }

        [Test]
        public void AttackPhasesMeetAtTheSweepBoundariesAndNeverPredictTheNextPhase()
        {
            var windup = new Emberdelve.AI.EnemyAttackPhase(1, 0f, 1f);
            var active = new Emberdelve.AI.EnemyAttackPhase(2, 0f, 0.3f);
            var recovery = new Emberdelve.AI.EnemyAttackPhase(3, 0f, 0.4f);
            Assert.That(windup.NormalizedTime(), Is.Zero);
            Assert.That(windup.NormalizedTime(100f), Is.EqualTo(active.NormalizedTime()));
            Assert.That(active.NormalizedTime(100f), Is.EqualTo(recovery.NormalizedTime()));
            Assert.That(recovery.NormalizedTime(100f), Is.EqualTo(Emberdelve.AI.EnemyAttackPhase.RecoveryEnd));
            Assert.That(default(Emberdelve.AI.EnemyAttackPhase).NormalizedTime(100f), Is.Zero);
            var late = new Emberdelve.AI.EnemyAttackPhase(2, 0.15f, 0.3f);
            Assert.That(late.NormalizedTime(), Is.EqualTo((Emberdelve.AI.EnemyAttackPhase.SweepStart +
                Emberdelve.AI.EnemyAttackPhase.SweepEnd) / 2f).Within(0.0001f));
        }

        [Test]
        public void RejectsInvalidPhaseClock()
        {
            var state = Valid(); state.AttackPhase = 5;
            Assert.That(state.IsValid, Is.False);
            state = Valid(); state.AttackPhase = 2;
            Assert.That(state.IsValid, Is.False, "active phase requires a duration");
            state = Valid(); state.PhaseElapsed = 1f;
            Assert.That(state.IsValid, Is.False, "elapsed cannot exceed duration");
            state = Valid(); state.RecoilStart = float.NaN;
            Assert.That(state.IsValid, Is.False);
            state = Valid(); state.RecoilStart = 1.01f;
            Assert.That(state.IsValid, Is.False);
            state = Valid(); state.SampleTime = double.NaN;
            Assert.That(state.IsValid, Is.False);
            state = Valid(); state.PhaseDuration = float.PositiveInfinity;
            Assert.That(state.IsValid, Is.False);
        }

        [Test]
        public void PhaseClockSurvivesNetworkSerialization()
        {
            var state = Valid();
            state.Flags = 0;
            state.AttackPhase = 4;
            state.RecoilStart = 0.5f;
            state.PhaseElapsed = 0.15f;
            state.PhaseDuration = 0.3f;
            state.SwingSeconds = 1.2f;
            state.SampleTime = 1234.567;
            using (var writer = new FastBufferWriter(256, Allocator.Temp))
            {
                writer.WriteValueSafe(state);
                using (var reader = new FastBufferReader(writer, Allocator.Temp))
                {
                    reader.ReadValueSafe(out EnemyPresentationWire decoded);
                    Assert.That(decoded.IsValid, Is.True);
                    Assert.That(decoded.AttackPhase, Is.EqualTo(state.AttackPhase));
                    Assert.That(decoded.PhaseElapsed, Is.EqualTo(state.PhaseElapsed));
                    Assert.That(decoded.PhaseDuration, Is.EqualTo(state.PhaseDuration));
                    Assert.That(decoded.RecoilStart, Is.EqualTo(state.RecoilStart));
                    Assert.That(decoded.SampleTime, Is.EqualTo(state.SampleTime));
                    Assert.That(decoded.SwingSeconds, Is.EqualTo(state.SwingSeconds));
                }
            }
        }

        [Test]
        public void WallRecoveryUsesTheContactSampleAndRecoilDuration()
        {
            var status = new Emberdelve.Combat.MeleeSwingStatus(1, 1,
                Emberdelve.Combat.SwingPhase.Recovery, Emberdelve.Combat.SwingEndReason.WallStop,
                0.45f, 4, 10, 3, System.Array.Empty<Emberdelve.Combat.MeleeSwingHit>(),
                timing: new Emberdelve.Data.SwingTiming(0.2f, 0.5f, 0.8f, 0.3f));
            var phase = Emberdelve.AI.EnemyAttackPhase.Recovery(status);
            Assert.That(phase.Elapsed, Is.EqualTo(0.1f).Within(0.0001f));
            Assert.That(phase.Duration, Is.EqualTo(0.3f).Within(0.0001f));
            Assert.That(phase.Phase, Is.EqualTo(4));
            float contactPose = Mathf.Lerp(Emberdelve.AI.EnemyAttackPhase.SweepStart,
                Emberdelve.AI.EnemyAttackPhase.SweepEnd, 0.3f);
            Assert.That(phase.RecoilStart, Is.EqualTo(contactPose).Within(0.0001f));
            Assert.That(phase.NormalizedTime(), Is.EqualTo(contactPose * (2f / 3f)).Within(0.0001f));
            Assert.That(phase.NormalizedTime(100f), Is.Zero);
        }

        [Test]
        public void DormancyAndWakeClockRoundTripWithoutImplyingDeath()
        {
            var state = Valid(); state.Flags=EnemyPresentationWire.WakingFlag; state.WakeElapsed=.625f;
            using(var writer=new FastBufferWriter(256, Allocator.Temp))
            {
                writer.WriteValueSafe(state);
                using(var reader=new FastBufferReader(writer, Allocator.Temp))
                {
                    reader.ReadValueSafe(out EnemyPresentationWire copy);
                    Assert.That(copy.IsValid,Is.True); Assert.That(copy.IsDead,Is.False);
                    Assert.That(copy.IsWaking,Is.True); Assert.That(copy.WakeElapsed,Is.EqualTo(.625f));
                }
            }
            state.Flags=EnemyPresentationWire.DormantFlag;
            Assert.That(state.IsValid,Is.True); Assert.That(state.IsDormant,Is.True);
            state.Flags|=EnemyPresentationWire.WakingFlag;
            Assert.That(state.IsValid,Is.False,"Cannot be dormant and rising simultaneously.");
            state.Flags=EnemyPresentationWire.DormantFlag|EnemyPresentationWire.DeadFlag;
            Assert.That(state.IsValid,Is.False,"A fake corpse is not a dead combat entity.");
            state.Flags=EnemyPresentationWire.WakingFlag; state.WakeElapsed=float.NaN;
            Assert.That(state.IsValid,Is.False);
        }

        private static EnemyPresentationWire Valid()
        {
            return new EnemyPresentationWire
            {
                Version = EnemyPresentationWire.CurrentVersion,
                Enemy = 42,
                VisualKind = 0,
                Flags = EnemyPresentationWire.DeadFlag,
                WindupSeconds = 0.65f
            };
        }

        private static EnemyPoseWire Pose(int enemy = 42, float x = 1f, float y = 2f, float z = 3f, float yaw = 90f)
        {
            return new EnemyPoseWire { Enemy = enemy, X = x, Y = y, Z = z, Yaw = yaw };
        }
    }
}
