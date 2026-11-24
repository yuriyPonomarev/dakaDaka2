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

        // Sword_Attack (the sword-clip enemies' attack): the weapon is raised over the right shoulder until 0.20, then chopped down
        // across the body (the hand passes the chest in front at 0.27). The sweep window is the part where the weapon is out to the
        // attacker's right, in front, then out to the left: before it the blade is still over the head, after it it follows through
        // low. The enemy damage sweep is baked from this same window (EmberdelveEnemyVisualFactory.TryBakeSwing), so the blow and the
        // hit are one motion. The authored controller blends out at 0.9; leave that final blend to locomotion.
        public const float SweepStart = 0.2625f;
        public const float SweepEnd = 0.30f;

        /// <summary>Sword_Attack: the weapon stops raised over the right shoulder here; the chop starts after it.</summary>
        public const float RaiseEnd = 0.20f;

        /// <summary>
        /// Seconds the anticipation keeps for the start of the chop (RaiseEnd..SweepStart of the 1.53 s clip at the sweep window's pace,
        /// x1.5): the long telegraph stretches the raise and the hold over the shoulder, never the blow itself.
        /// </summary>
        public const float ChopLeadSeconds = 0.14f;
        public const float RecoveryEnd = 0.89f;
        public const float ClubAimYaw = 45f;
        public const float ClubSweepStart = 0.83f;
        public const float ClubSweepEnd = 0.90f;
        public const float ClubRecoveryEnd = 0.99f;

        /// <summary>Attack variants of a Servitor-style sword enemy: 0 = diagonal chop (Sword_Attack), 1 = vertical overhead (derived clip).</summary>
        public const int DiagonalVariant = 0;
        public const int OverheadVariant = 1;
        public const string DiagonalState = "Attack";
        public const string OverheadState = "Overhead";

        // The derived overhead clip (EmberdelveEnemyVisualFactory.BuildOverheadClip) is authored to this timeline, in normalized clip time:
        // weapon raised over the head until 0.38, brought straight down in front of the body 0.42..0.56 (the sweep window), recovering to 0.92.
        public const float OverheadSweepStart = 0.42f;
        public const float OverheadSweepEnd = 0.56f;
        public const float OverheadRaiseEnd = 0.38f;
        public const float OverheadChopLeadSeconds = 0.12f;
        public const float OverheadRecoveryEnd = 0.92f;

        public static string StateName(int variant) => variant == OverheadVariant ? OverheadState : DiagonalState;

        // The archer's bow clip (EmberdelveEnemyArcherClip), played in the archer controller's "Attack" state, in normalized clip time: the bow
        // comes up and the right hand sets the string until ArcherDrawStart, the string is drawn to the cheek by ArcherDrawEnd (held there for the
        // rest of the brain's windup: the visible telegraph), released ArcherDrawEnd..ArcherReleaseEnd, and the bow lowered by ArcherRecoveryEnd.
        public const float ArcherDrawStart = 0.20f;
        public const float ArcherDrawEnd = 0.45f;
        public const float ArcherReleaseEnd = 0.50f;
        public const float ArcherRecoveryEnd = 0.92f;

        /// <summary>Seconds of the windup spent raising and drawing; a longer windup holds the full draw for the remainder.</summary>
        public const float ArcherDrawSeconds = 0.75f;

        /// <summary>Presentation seconds of the release (phase 2) and of lowering the bow (phase 3) after the server launched the arrow.</summary>
        public const float ArcherReleaseSeconds = 0.08f;
        public const float ArcherRecoverySeconds = 0.6f;

        /// <summary>Normalized time in the bow clip for this phase (archers only; the melee windows above are untouched).</summary>
        public float ArcherNormalizedTime(float age = 0f)
        {
            float t = Elapsed + Mathf.Max(0f, age);
            switch (Phase)
            {
                case 1: return Mathf.Lerp(0f, ArcherDrawEnd, Mathf.Clamp01(t / Mathf.Max(0.05f, Mathf.Min(ArcherDrawSeconds, Duration))));
                case 2: return Mathf.Lerp(ArcherDrawEnd, ArcherReleaseEnd, Mathf.Clamp01(t / Mathf.Max(0.02f, Duration)));
                case 3: return Mathf.Lerp(ArcherReleaseEnd, ArcherRecoveryEnd, Mathf.Clamp01(t / Mathf.Max(0.02f, Duration)));
                default: return 0f;
            }
        }

        /// <summary>End of the bow clip's hold after the release; the bow is lowered from here to ArcherRecoveryEnd.</summary>
        public const float ArcherHoldEnd = 0.62f;

        /// <summary>How far the bow is raised from its carry pose to the shooting grip (0..1) at a normalized time of the bow clip.</summary>
        public static float ArcherBowRaise(float normalized)
        {
            float x;
            if (normalized <= 0f) return 0f;
            if (normalized <= ArcherDrawStart) x = normalized / ArcherDrawStart;
            else if (normalized <= ArcherHoldEnd) return 1f;
            else x = 1f - Mathf.Clamp01((normalized - ArcherHoldEnd) / (ArcherRecoveryEnd - ArcherHoldEnd));
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }

        /// <summary>How far the bow string is drawn (0..1) at a normalized time of the bow clip: pulled from ArcherDrawStart to ArcherDrawEnd, slack after the release.</summary>
        public static float ArcherBowDraw(float normalized)
        {
            if (normalized <= ArcherDrawStart || normalized > ArcherDrawEnd + 0.005f) return 0f;
            float x = Mathf.Clamp01((normalized - ArcherDrawStart) / (ArcherDrawEnd - ArcherDrawStart));
            return x * x * (3f - 2f * x);
        }

        /// <summary>Sweep window (normalized clip time) of a sword attack variant.</summary>
        public static void SweepWindow(int variant, out float start, out float end)
        {
            if (variant == OverheadVariant) { start = OverheadSweepStart; end = OverheadSweepEnd; }
            else { start = SweepStart; end = SweepEnd; }
        }

        public static EnemyAttackPhase Recovery(in MeleeSwingStatus status, bool club = false, int variant = 0)
        {
            if (status.Recoiled)
            {
                // The authority stops at this exact sweep sample, potentially before ActiveEnd.
                float stoppedAt = status.Timing.Windup + status.Timing.Active * status.WallContactSample /
                    Mathf.Max(1, status.SampleCount);
                float contact = Mathf.Clamp01((float)status.WallContactSample / Mathf.Max(1, status.SampleCount));
                float recoilFrom, recoilTo;
                if (club) { recoilFrom = ClubSweepStart; recoilTo = ClubSweepEnd; }
                else SweepWindow(variant, out recoilFrom, out recoilTo);
                return new EnemyAttackPhase(4, status.Elapsed - stoppedAt, status.Timing.WallRecoil, Mathf.Lerp(recoilFrom, recoilTo, contact));
            }
            return new EnemyAttackPhase(3, status.Elapsed - status.Timing.ActiveEnd, status.Timing.Recovery);
        }

        public float NormalizedTime(float age = 0f, bool club = false, int variant = 0)
        {
            float fraction = Mathf.Clamp01((Elapsed + Mathf.Max(0f, age)) / Mathf.Max(0.02f, Duration));
            bool overhead = !club && variant == OverheadVariant;
            float start = club ? ClubSweepStart : overhead ? OverheadSweepStart : SweepStart;
            float end = club ? ClubSweepEnd : overhead ? OverheadSweepEnd : SweepEnd;
            float raiseEnd = overhead ? OverheadRaiseEnd : RaiseEnd;
            float recoveryEnd = club ? ClubRecoveryEnd : overhead ? OverheadRecoveryEnd : RecoveryEnd;
            switch (Phase)
            {
                case 1:
                {
                    if (club) return Mathf.Lerp(0f, start, fraction);
                    float lead = Mathf.Clamp((overhead ? OverheadChopLeadSeconds : ChopLeadSeconds) / Duration, 0f, 0.5f);
                    float split = 1f - lead;
                    return fraction < split
                        ? Mathf.Lerp(0f, raiseEnd, fraction / Mathf.Max(1e-4f, split))
                        : Mathf.Lerp(raiseEnd, start, (fraction - split) / Mathf.Max(1e-4f, lead));
                }
                case 2: return Mathf.Lerp(start, end, fraction);
                case 3: return Mathf.Lerp(end, recoveryEnd, fraction);
                case 4: return Mathf.Lerp(RecoilStart, 0f, fraction);
                default: return 0f;
            }
        }
    }
}
