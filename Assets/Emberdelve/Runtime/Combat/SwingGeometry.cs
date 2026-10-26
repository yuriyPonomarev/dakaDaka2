using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;

namespace Emberdelve.Combat
{
    /// <summary>The attacker's aim frame: eye position plus the yaw/pitch rotation of its view (no roll).</summary>
    public readonly struct SwingFrame
    {
        public readonly Vector3 Eye;
        public readonly Quaternion Rotation;

        public SwingFrame(Vector3 eye, float yawDegrees, float pitchDegrees)
        {
            Eye = eye;
            Rotation = Quaternion.Euler(pitchDegrees, yawDegrees, 0f);
        }

        public SwingFrame(Vector3 eye, Quaternion rotation)
        {
            Eye = eye;
            Rotation = rotation;
        }
    }

    /// <summary>
    /// Pure geometry for the swept melee: pattern pose -> world weapon segment, sample grid, and the
    /// segment/segment distance the sweep uses. No physics and no scene access (the wall test is the
    /// authority's), so the same code runs on a server and in the gates.
    /// </summary>
    public static class SwingGeometry
    {
        public const int MinSamples = 8;
        public const int MaxSamples = 400;

        /// <summary>World segment (base, tip) of the weapon for a pose in an aim frame.</summary>
        public static void BladeSegment(in SwingFrame frame, in SwingPose pose, float bladeLength,
            out Vector3 baseWorld, out Vector3 tipWorld)
        {
            baseWorld = frame.Eye + frame.Rotation * pose.Base;
            Vector3 direction = frame.Rotation * pose.LocalDirection;
            tipWorld = baseWorld + direction * bladeLength;
        }

        /// <summary>
        /// Number of equal time steps the ACTIVE phase is sampled in, for the default turn ceiling. See the
        /// three-argument overload; this is what the authority uses with a default (or old) <c>CombatFlowTuning</c>.
        /// </summary>
        public static int SampleCount(SwingPatternData pattern, WeaponData weapon) =>
            SampleCount(pattern, weapon, CombatFlowTuning.DefaultSwingTurnCeilingDegPerSec);

        /// <summary>
        /// Number of equal time steps the ACTIVE phase is sampled in. It is a pure function of the pattern data,
        /// the weapon and the turn ceiling, never of the frame rate or the tick length, which is what makes the sweep
        /// result independent of how the authority is stepped. K = ceil(translation + rotation), clamped to
        /// [MinSamples, MaxSamples]:
        ///   translation = (largest per-probe step of the base or the tip, over 128 equal time probes) x 128 / spacing
        ///                 (D1c2 / review M-b: the FASTEST segment of the path decides, not the average);
        ///   rotation    = ceilingRad x activeTime x EffectiveReach / spacing (D1c2 / review H2: the tip travel the aim
        ///                 turn can add while the blade is active, at the most the authority's follower lets through).
        /// With both terms, consecutive sampled segments are at most <c>spacing = clamp(sweepRadius / 2, 0.02, 0.06)</c>
        /// metres apart at their farthest point (unless the clamp at MaxSamples binds), so a contact the continuous
        /// sweep makes with a penetration deeper than half that spacing (at most 3 cm) is found. A ceiling of 0 or less
        /// uses the default 720 deg/s (the grid is never sized for an unlimited turn).
        /// </summary>
        public static int SampleCount(SwingPatternData pattern, WeaponData weapon, float turnCeilingDegPerSec)
        {
            float length = pattern.BladeLength(weapon);
            SwingFrame frame = new SwingFrame(Vector3.zero, Quaternion.identity);

            const int probes = 128;
            Vector3 previousBase = default, previousTip = default;
            float maxStep = 0f;
            for (int i = 0; i <= probes; i++)
            {
                BladeSegment(frame, pattern.ActivePose(i / (float)probes), length, out Vector3 b, out Vector3 t);
                if (i > 0)
                {
                    maxStep = Mathf.Max(maxStep, Mathf.Max(Vector3.Distance(previousBase, b), Vector3.Distance(previousTip, t)));
                }
                previousBase = b;
                previousTip = t;
            }

            float spacing = Mathf.Clamp(pattern.sweepRadius * 0.5f, 0.02f, 0.06f);
            float ceiling = turnCeilingDegPerSec > 0f && !float.IsInfinity(turnCeilingDegPerSec)
                ? turnCeilingDegPerSec : CombatFlowTuning.DefaultSwingTurnCeilingDegPerSec;
            float translation = maxStep * probes / spacing;
            float rotation = ceiling * Mathf.Deg2Rad * pattern.activeTime * pattern.EffectiveReach(weapon) / spacing;
            int count = Mathf.CeilToInt(translation + rotation);
            return Mathf.Clamp(count, MinSamples, MaxSamples);
        }

        /// <summary>Yaw/pitch in degrees (pitch positive down) of a direction. False for a zero or non-finite direction.</summary>
        public static bool TryAimAngles(Vector3 direction, out float yaw, out float pitch)
        {
            yaw = 0f;
            pitch = 0f;
            if (!(direction.sqrMagnitude > 1e-8f) || float.IsNaN(direction.x) || float.IsNaN(direction.y) || float.IsNaN(direction.z)
                || float.IsInfinity(direction.x) || float.IsInfinity(direction.y) || float.IsInfinity(direction.z))
            {
                return false;
            }

            direction.Normalize();
            yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            pitch = -Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f)) * Mathf.Rad2Deg;
            return true;
        }

        /// <summary>
        /// Distance between segment P1-Q1 and segment P2-Q2 (either may be a point). <paramref name="onFirst"/>
        /// is the closest point on the first segment. Standard clamped closest-point solution.
        /// </summary>
        public static float SegmentDistance(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out Vector3 onFirst)
        {
            Vector3 d1 = q1 - p1;
            Vector3 d2 = q2 - p2;
            Vector3 r = p1 - p2;
            float a = Vector3.Dot(d1, d1);
            float e = Vector3.Dot(d2, d2);
            float f = Vector3.Dot(d2, r);
            const float eps = 1e-9f;
            float s, t;

            if (a <= eps && e <= eps)
            {
                onFirst = p1;
                return r.magnitude;
            }

            if (a <= eps)
            {
                s = 0f;
                t = Mathf.Clamp01(f / e);
            }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= eps)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else
                {
                    float b = Vector3.Dot(d1, d2);
                    float denom = a * e - b * b;
                    s = denom > eps ? Mathf.Clamp01((b * f - c * e) / denom) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f)
                    {
                        t = 0f;
                        s = Mathf.Clamp01(-c / a);
                    }
                    else if (t > 1f)
                    {
                        t = 1f;
                        s = Mathf.Clamp01((b - c) / a);
                    }
                }
            }

            onFirst = p1 + d1 * s;
            Vector3 onSecond = p2 + d2 * t;
            return (onFirst - onSecond).magnitude;
        }
    }
}
