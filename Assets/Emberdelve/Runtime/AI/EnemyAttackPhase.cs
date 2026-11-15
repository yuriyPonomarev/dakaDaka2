using UnityEngine;
using Emberdelve.Combat;

namespace Emberdelve.AI
{
    /// <summary>Read-only presentation clock; the combat authority remains the only source of hits.</summary>
    public readonly struct EnemyAttackPhase
    {
        public readonly byte Phase; // 0 idle, 1 anticipation, 2 damage sweep, 3 recovery, 4 wall recoil
        public readonly float Elapsed;
        public readonly float Duration;
        public readonly float RecoilStart;

        public EnemyAttackPhase(byte phase, float elapsed, float duration, float recoilStart = 0f)
        {
            Phase = phase;
            RecoilStart = Mathf.Clamp01(recoilStart);
            Duration = Mathf.Max(0.02f, duration);
            Elapsed = Mathf.Clamp(elapsed, 0f, Duration);
        }

        // Sword_Regular_A: hand goes from backswing to extension at frames 5..9 of 13.
        // The authored controller blends out at 0.9; leave that final blend to locomotion.
        public const float SweepStart = 5f / 13f;
        public const float SweepEnd = 9f / 13f;
        public const float RecoveryEnd = 0.89f;

        public static EnemyAttackPhase Recovery(in MeleeSwingStatus status)
        {
            if (status.Recoiled)
            {
                // The authority stops at this exact sweep sample, potentially before ActiveEnd.
                float stoppedAt = status.Timing.Windup + status.Timing.Active * status.WallContactSample /
                    Mathf.Max(1, status.SampleCount);
                float contact = Mathf.Clamp01((float)status.WallContactSample / Mathf.Max(1, status.SampleCount));
                return new EnemyAttackPhase(4, status.Elapsed - stoppedAt, status.Timing.WallRecoil,
                    Mathf.Lerp(SweepStart, SweepEnd, contact));
            }
            return new EnemyAttackPhase(3, status.Elapsed - status.Timing.ActiveEnd, status.Timing.Recovery);
        }

        public float NormalizedTime(float age = 0f)
        {
            float fraction = Mathf.Clamp01((Elapsed + Mathf.Max(0f, age)) / Mathf.Max(0.02f, Duration));
            switch (Phase)
            {
                case 1: return Mathf.Lerp(0f, SweepStart, fraction);
                case 2: return Mathf.Lerp(SweepStart, SweepEnd, fraction);
                case 3: return Mathf.Lerp(SweepEnd, RecoveryEnd, fraction);
                case 4: return Mathf.Lerp(RecoilStart, 0f, fraction);
                default: return 0f;
            }
        }
    }
}
