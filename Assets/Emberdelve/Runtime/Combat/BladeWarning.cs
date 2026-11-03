using UnityEngine;
using Emberdelve.BladeDefense;
using Emberdelve.Data;

namespace Emberdelve.Combat
{
    /// <summary>
    /// U3C: maps the authored swing pattern (SwingKind, keys) to the pure defense vocabulary (BladeTrajectory, BladeHeight). The kind is
    /// authored data; <see cref="ClassifyKeys"/> re-derives the class from the keys so a gate can prove the two agree.
    /// </summary>
    public static class BladeTrajectories
    {
        public static BladeTrajectory Of(SwingKind kind)
        {
            switch (kind)
            {
                case SwingKind.HorizontalLeftToRight:
                case SwingKind.HorizontalRightToLeft:
                    return BladeTrajectory.Horizontal;
                case SwingKind.Overhead:
                    return BladeTrajectory.Overhead;
                case SwingKind.Thrust:
                    return BladeTrajectory.Thrust;
                default:
                    return BladeTrajectory.None;
            }
        }

        /// <summary>The height band a swing of this kind arrives in, from the tuning's table (the default tuning when null).</summary>
        public static BladeHeight HeightOf(SwingKind kind, CombatFlowTuning tuning)
        {
            CombatFlowTuning t = tuning ?? CombatFlowTuning.Default;
            return t.HeightFor(Of(kind));
        }

        /// <summary>The trajectory class measured from the pattern's keys (yaw extent, pitch extent, hand travel). None for a pattern without usable keys.</summary>
        public static BladeTrajectory ClassifyKeys(SwingPatternData pattern)
        {
            if (pattern == null || pattern.keys == null || pattern.keys.Length < 2) return BladeTrajectory.None;

            pattern.YawRange(out float minYaw, out float maxYaw);
            MeasurePitch(pattern, out float minPitch, out float maxPitch);
            Vector3 travel = pattern.keys[pattern.keys.Length - 1].basePos - pattern.keys[0].basePos;
            return BladeGuardRules.ClassifyPath(maxYaw - minYaw, maxPitch - minPitch, travel.magnitude);
        }

        internal static void MeasurePitch(SwingPatternData pattern, out float minPitch, out float maxPitch)
        {
            minPitch = 0f;
            maxPitch = 0f;
            if (pattern == null || pattern.keys == null || pattern.keys.Length == 0) return;

            minPitch = float.PositiveInfinity;
            maxPitch = float.NegativeInfinity;
            for (int i = 0; i < pattern.keys.Length; i++)
            {
                minPitch = Mathf.Min(minPitch, pattern.keys[i].bladePitch);
                maxPitch = Mathf.Max(maxPitch, pattern.keys[i].bladePitch);
            }
        }
    }

    /// <summary>
    /// U3C: the trajectory a swing COMMITTED to when it began, in the form a victim (or a presentation layer) can read: class, height band,
    /// the aim frame it was committed in and the sampled path. It is built once, by the authority, from the same pattern asset, weapon and
    /// sample grid the hit sweep uses, and <see cref="SegmentAt"/> calls <see cref="SwingGeometry.PathSegment"/> - the very function the
    /// sweep samples - so the warning and the sweep cannot disagree. <see cref="Touches"/> is the sweep's own contact test
    /// (<see cref="SwingGeometry.SweepDepth"/>) over the same samples, without walls.
    /// With <c>CombatFlowTuning.commitSwingAim</c> on, the sweep also keeps this aim for the whole swing; with it off the warning is the
    /// prediction for the aim at begin and the live aim may still move the sweep (D1c2 turn ceiling).
    /// </summary>
    public readonly struct BladeWarning
    {
        public readonly bool IsValid;
        public readonly SwingPatternData Pattern;
        public readonly BladeTrajectory Trajectory;
        public readonly BladeHeight Height;
        /// <summary>The attacker's eye when the swing began (the path is relative to the eye: <see cref="SegmentAt(Vector3,int,out Vector3,out Vector3)"/> takes the live eye).</summary>
        public readonly Vector3 Eye;
        public readonly float AimYaw;
        public readonly float AimPitch;
        public readonly float BladeLength;
        /// <summary>Eye-to-farthest-touch distance of the pattern with the weapon.</summary>
        public readonly float Reach;
        public readonly float SweepRadius;
        /// <summary>Blade yaw extent in the aim frame (degrees, positive to the right): the same numbers the enemy ground wedge is drawn from.</summary>
        public readonly float MinBladeYaw;
        public readonly float MaxBladeYaw;
        /// <summary>Blade pitch extent in the aim frame (degrees, positive down).</summary>
        public readonly float MinBladePitch;
        public readonly float MaxBladePitch;
        /// <summary>Number of equal time steps of the active phase; samples 0..SampleCount inclusive are swept.</summary>
        public readonly int SampleCount;

        private BladeWarning(SwingPatternData pattern, BladeTrajectory trajectory, BladeHeight height, Vector3 eye, float aimYaw,
            float aimPitch, float bladeLength, float reach, float sweepRadius, float minYaw, float maxYaw, float minPitch, float maxPitch,
            int sampleCount)
        {
            IsValid = true;
            Pattern = pattern;
            Trajectory = trajectory;
            Height = height;
            Eye = eye;
            AimYaw = aimYaw;
            AimPitch = aimPitch;
            BladeLength = bladeLength;
            Reach = reach;
            SweepRadius = sweepRadius;
            MinBladeYaw = minYaw;
            MaxBladeYaw = maxYaw;
            MinBladePitch = minPitch;
            MaxBladePitch = maxPitch;
            SampleCount = sampleCount;
        }

        /// <summary>The warning of a swing of <paramref name="pattern"/> with <paramref name="weapon"/> begun from an aim frame. Invalid (default) when either asset is missing.</summary>
        public static BladeWarning Describe(SwingPatternData pattern, WeaponData weapon, Vector3 eye, float aimYawDegrees,
            float aimPitchDegrees, CombatFlowTuning tuning, int sampleCount)
        {
            if (pattern == null || weapon == null) return default;

            pattern.YawRange(out float minYaw, out float maxYaw);
            BladeTrajectories.MeasurePitch(pattern, out float minPitch, out float maxPitch);
            return new BladeWarning(pattern, BladeTrajectories.Of(pattern.kind), BladeTrajectories.HeightOf(pattern.kind, tuning), eye,
                aimYawDegrees, aimPitchDegrees, pattern.BladeLength(weapon), pattern.EffectiveReach(weapon), pattern.sweepRadius,
                minYaw, maxYaw, minPitch, maxPitch, Mathf.Max(1, sampleCount));
        }

        /// <summary>Weapon segment at sweep sample <paramref name="sample"/> (0..SampleCount) with the committed eye.</summary>
        public void SegmentAt(int sample, out Vector3 baseWorld, out Vector3 tipWorld) => SegmentAt(Eye, sample, out baseWorld, out tipWorld);

        /// <summary>Weapon segment at sweep sample <paramref name="sample"/> with the attacker's eye now (the committed aim is kept; the body may have moved).</summary>
        public void SegmentAt(Vector3 eye, int sample, out Vector3 baseWorld, out Vector3 tipWorld)
        {
            SwingFrame frame = new SwingFrame(eye, AimYaw, AimPitch);
            SwingGeometry.PathSegment(frame, Pattern, BladeLength, sample / (float)SampleCount, out baseWorld, out tipWorld);
        }

        /// <summary>
        /// True when the committed path touches a hit capsule (a, b, radius) at any sweep sample - the sweep's own contact test without
        /// walls. A body that is outside every sample's reach is outside the warning, and the sweep cannot hit it.
        /// </summary>
        public bool Touches(Vector3 eye, Vector3 capsuleA, Vector3 capsuleB, float capsuleRadius)
        {
            if (!IsValid) return false;
            for (int s = 0; s <= SampleCount; s++)
            {
                SegmentAt(eye, s, out Vector3 b, out Vector3 t);
                if (SwingGeometry.SweepDepth(b, t, capsuleA, capsuleB, capsuleRadius, SweepRadius, out _) >= 0f) return true;
            }

            return false;
        }

        public override string ToString() =>
            IsValid
                ? $"warning {Trajectory}/{Height} yaw={AimYaw:F1} pitch={AimPitch:F1} bladeYaw={MinBladeYaw:F1}..{MaxBladeYaw:F1} reach={Reach:F2} samples={SampleCount}"
                : "warning none";
    }
}
