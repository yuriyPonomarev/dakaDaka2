using System;
using UnityEngine;

namespace Emberdelve.Data
{
    public enum SwingKind { HorizontalLeftToRight, HorizontalRightToLeft, Overhead, Thrust }

    /// <summary>
    /// One key of a swing path. The weapon segment is described in the ATTACKER'S AIM FRAME (origin = the eye,
    /// +Z = where the player looks, +X = right, +Y = up, rotated by the aim yaw/pitch, no roll):
    /// <see cref="basePos"/> is where the hand/pommel is (metres), <see cref="bladeYaw"/> / <see cref="bladePitch"/>
    /// give the direction the blade points relative to the aim (degrees; yaw positive to the right, pitch
    /// positive DOWN, the same convention as Quaternion.Euler(pitch, yaw, 0)). <see cref="t"/> is the
    /// normalised time within the ACTIVE phase (0..1, ascending). Between keys everything is linearly
    /// interpolated in time, so the curve is exactly the data.
    /// </summary>
    [Serializable]
    public struct SwingKey
    {
        [Range(0f, 1f)] public float t;
        public Vector3 basePos;
        public float bladeYaw;
        public float bladePitch;

        public SwingKey(float t, Vector3 basePos, float bladeYaw, float bladePitch)
        {
            this.t = t;
            this.basePos = basePos;
            this.bladeYaw = bladeYaw;
            this.bladePitch = bladePitch;
        }
    }

    /// <summary>A sampled weapon pose in the aim frame. Roll is cosmetic only (the damage segment ignores it).</summary>
    public readonly struct SwingPose
    {
        public readonly Vector3 Base;
        public readonly float Yaw;
        public readonly float Pitch;
        public readonly float Roll;

        public SwingPose(Vector3 basePos, float yaw, float pitch, float roll)
        {
            Base = basePos;
            Yaw = yaw;
            Pitch = pitch;
            Roll = roll;
        }

        /// <summary>Unit blade direction in the aim frame.</summary>
        public Vector3 LocalDirection => Quaternion.Euler(Pitch, Yaw, 0f) * Vector3.forward;

        public Quaternion LocalRotation => Quaternion.Euler(Pitch, Yaw, Roll);

        public static SwingPose Lerp(in SwingPose a, in SwingPose b, float k) =>
            new SwingPose(Vector3.Lerp(a.Base, b.Base, k), Mathf.Lerp(a.Yaw, b.Yaw, k),
                Mathf.Lerp(a.Pitch, b.Pitch, k), Mathf.Lerp(a.Roll, b.Roll, k));
    }

    /// <summary>
    /// D1a swing pattern: the full, data-defined weapon motion of one melee attack. It is the ONE source for
    /// three consumers - the authority's continuous damage sweep (only the ACTIVE phase deals damage), the
    /// first-person weapon visual (windup, active and recovery all animate from <see cref="Evaluate"/>) and
    /// the gates - so what the player sees is the real sweep path. Nothing in an AttackIntent can change it.
    ///
    /// Our own authored tunables (Docs/Tasks/D1a.md); a combo chain (D1b) only adds patterns and an ordering.
    /// </summary>
    [CreateAssetMenu(menuName = "Emberdelve/05 Swing Pattern", fileName = "SwingPattern")]
    public class SwingPatternData : ScriptableObject
    {
        [Header("Identity")]
        public string patternId = "pattern";
        public SwingKind kind = SwingKind.HorizontalLeftToRight;

        [Header("Timing (seconds, authority clock)")]
        [Range(0f, 2f)] public float windupTime = 0.24f;
        [Range(0.02f, 1f)] public float activeTime = 0.14f;
        [Range(0f, 2f)] public float recoveryTime = 0.30f;
        [Tooltip("Extra recovery after the swing is stopped by level geometry (the recoil).")]
        [Range(0f, 2f)] public float wallRecoilTime = 0.35f;

        [Header("Damage")]
        [Tooltip("Multiplies the weapon's damage for this pattern (D1b combo steps will author this).")]
        [Range(0.1f, 3f)] public float damageMultiplier = 1f;
        [Tooltip("HOOK for D1b (weapon move penalty while swinging). Not read by movement yet; leave at 1.")]
        [Range(0.1f, 1f)] public float moveSpeedMultiplier = 1f;
        [Tooltip("Most distinct targets one swing can hit (cleave). Each target is still hit at most once.")]
        [Range(1, 8)] public int maxTargets = 4;

        [Header("Geometry")]
        [Tooltip("Effective reach = (weapon.reach + weapon.hitSphereRadius) * reachScale, measured from the eye to the farthest point the swept segment can touch.")]
        [Range(0.3f, 1.5f)] public float reachScale = 1f;
        [Tooltip("Distance from the eye to the segment base at the pose where the tip is farthest forward. Blade length = effective reach - sweepRadius - reachInset.")]
        public float reachInset = 0.30f;
        [Tooltip("Radius of the swept weapon segment (metres). Added to every hit volume's own radius.")]
        [Range(0.02f, 0.4f)] public float sweepRadius = 0.16f;
        [Tooltip("After a target is first touched, volumes touched within this many seconds are compared and the deepest one decides the hit location.")]
        [Range(0f, 0.1f)] public float contactWindow = 0.02f;

        [Header("Presentation")]
        [Tooltip("Material of the sweep edge bar and the afterimage ribbon. It is a project asset reference so the shader is always in the build; empty = a runtime fallback material.")]
        public Material trailMaterial;

        [Header("Rest pose (before windup / after recovery)")]
        public Vector3 restBase = new Vector3(0.26f, -0.20f, 0.30f);
        public float restYaw = 12f;
        public float restPitch = -8f;
        public float restRoll = 14f;

        [Header("Active path")]
        public SwingKey[] keys = new SwingKey[0];

        public float ActiveStart => windupTime;
        public float ActiveEnd => windupTime + activeTime;
        public float TotalTime => windupTime + activeTime + recoveryTime;
        public SwingPose RestPose => new SwingPose(restBase, restYaw, restPitch, restRoll);

        /// <summary>Eye-to-farthest-touch distance of this pattern with that weapon.</summary>
        public float EffectiveReach(WeaponData weapon) =>
            weapon == null ? 0f : Mathf.Max(0.5f, (weapon.reach + weapon.hitSphereRadius) * reachScale);

        /// <summary>Length of the damage segment (never shorter than 0.3 m).</summary>
        public float BladeLength(WeaponData weapon) =>
            Mathf.Max(0.3f, EffectiveReach(weapon) - sweepRadius - reachInset);

        /// <summary>Pose at normalised ACTIVE time u (0..1): linear interpolation between the keys.</summary>
        public SwingPose ActivePose(float u)
        {
            if (keys == null || keys.Length == 0) return RestPose;

            u = Mathf.Clamp01(u);
            if (keys.Length == 1 || u <= keys[0].t) return KeyPose(keys[0]);

            for (int i = 0; i < keys.Length - 1; i++)
            {
                SwingKey a = keys[i];
                SwingKey b = keys[i + 1];
                if (u > b.t && i < keys.Length - 2) continue;

                float span = b.t - a.t;
                float k = span <= 1e-6f ? 1f : Mathf.Clamp01((u - a.t) / span);
                return SwingPose.Lerp(KeyPose(a), KeyPose(b), k);
            }

            return KeyPose(keys[keys.Length - 1]);
        }

        private static SwingPose KeyPose(in SwingKey key) => new SwingPose(key.basePos, key.bladeYaw, key.bladePitch, 0f);

        /// <summary>
        /// Pose at swing time t (seconds since the swing began): eased from the rest pose to the first key during
        /// the windup, the keyed path during the active phase, eased back to rest during the recovery. Damage is
        /// only ever dealt by the active part; the other two parts are what the player sees.
        /// </summary>
        public SwingPose Evaluate(float swingTime)
        {
            float t = Mathf.Max(0f, swingTime);
            if (t < windupTime)
            {
                float k = windupTime <= 1e-5f ? 1f : Smooth(t / windupTime);
                return SwingPose.Lerp(RestPose, ActivePose(0f), k);
            }

            if (t <= windupTime + activeTime) return ActivePose((t - windupTime) / Mathf.Max(1e-5f, activeTime));

            float r = t - windupTime - activeTime;
            if (recoveryTime <= 1e-5f || r >= recoveryTime) return RestPose;
            return SwingPose.Lerp(ActivePose(1f), RestPose, Smooth(r / recoveryTime));
        }

        /// <summary>Authoring check. Returns false with a reason when the data cannot be swept safely.</summary>
        public bool Validate(out string problem)
        {
            problem = string.Empty;
            if (string.IsNullOrEmpty(patternId)) { problem = "patternId is empty"; return false; }
            if (keys == null || keys.Length < 2) { problem = "needs at least two keys"; return false; }
            if (Mathf.Abs(keys[0].t) > 1e-4f || Mathf.Abs(keys[keys.Length - 1].t - 1f) > 1e-4f)
            {
                problem = "first key must be t=0 and last key t=1";
                return false;
            }

            for (int i = 0; i < keys.Length; i++)
            {
                SwingKey k = keys[i];
                if (!IsFinite(k.basePos) || float.IsNaN(k.bladeYaw) || float.IsNaN(k.bladePitch) || float.IsNaN(k.t)
                    || float.IsInfinity(k.bladeYaw) || float.IsInfinity(k.bladePitch))
                {
                    problem = $"key {i} has a non-finite value";
                    return false;
                }

                if (i > 0 && k.t < keys[i - 1].t) { problem = $"key {i} is out of time order"; return false; }
            }

            if (!(activeTime > 0f) || !(windupTime >= 0f) || !(recoveryTime >= 0f)) { problem = "bad phase durations"; return false; }
            return true;
        }

        private static float Smooth(float k)
        {
            k = Mathf.Clamp01(k);
            return k * k * (3f - 2f * k);
        }

        private static bool IsFinite(Vector3 v) =>
            !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
              || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
    }
}
