using UnityEngine;

namespace Emberdelve.Player
{
    /// <summary>
    /// The authored local cube used by the visible sword blade, expressed as a world oriented box for
    /// authoritative projectile sweeps. It intentionally follows the displayed weapon transform.
    /// </summary>
    public readonly struct GuardBladeBox
    {
        public readonly Vector3 Center;
        public readonly Quaternion Rotation;
        public readonly Vector3 HalfExtents;

        public GuardBladeBox(Transform blade, Vector3 authoredSize)
        {
            Center = blade.TransformPoint(Vector3.zero);
            Rotation = blade.rotation;
            Vector3 scale = blade.lossyScale;
            HalfExtents = new Vector3(Mathf.Abs(scale.x * authoredSize.x),
                Mathf.Abs(scale.y * authoredSize.y), Mathf.Abs(scale.z * authoredSize.z)) * 0.5f;
        }

        public GuardBladeBox(Vector3 center, Quaternion rotation, Vector3 size)
        {
            Center = center;
            Rotation = rotation;
            HalfExtents = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z)) * 0.5f;
        }

        /// <summary>Returns the first contact distance along a swept projectile segment, including its radius.</summary>
        public bool TrySweep(Vector3 start, Vector3 end, float radius, out float distance)
        {
            Vector3 delta = end - start;
            float length = delta.magnitude;
            distance = 0f;
            if (length <= 0.00001f) return false;

            Vector3 localStart = Quaternion.Inverse(Rotation) * (start - Center);
            Vector3 localDirection = Quaternion.Inverse(Rotation) * (delta / length);
            float r = Mathf.Max(0f, radius);
            float lo = 0f, hi = length;
            // Squared distance from a point on the segment to the actual blade box is convex. Locate
            // its minimum, then find the first point whose rounded-box distance reaches projectile radius.
            for (int i = 0; i < 36; i++)
            {
                float a = lo + (hi - lo) / 3f;
                float b = hi - (hi - lo) / 3f;
                if (DistanceSquaredToBox(localStart + localDirection * a) <= DistanceSquaredToBox(localStart + localDirection * b)) hi = b;
                else lo = a;
            }
            float closest = (lo + hi) * 0.5f;
            if (DistanceSquaredToBox(localStart) <= r * r) { distance = 0f; return true; }
            if (DistanceSquaredToBox(localStart + localDirection * closest) > r * r) return false;

            lo = 0f;
            hi = closest;
            for (int i = 0; i < 36; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (DistanceSquaredToBox(localStart + localDirection * mid) <= r * r) hi = mid;
                else lo = mid;
            }
            distance = hi;
            return true;
        }

        private float DistanceSquaredToBox(Vector3 point)
        {
            Vector3 closest = new Vector3(Mathf.Clamp(point.x, -HalfExtents.x, HalfExtents.x),
                Mathf.Clamp(point.y, -HalfExtents.y, HalfExtents.y), Mathf.Clamp(point.z, -HalfExtents.z, HalfExtents.z));
            return (point - closest).sqrMagnitude;
        }

        /// <summary>Classifies an already confirmed blade contact, along the blade from hilt to tip.</summary>
        public bool IsInParryRegion(Vector3 contact, Vector2 region)
        {
            if (!(HalfExtents.z > 0f) || !(region.x >= 0f) || !(region.y <= 1f) || !(region.x < region.y)) return false;
            float along = ((Quaternion.Inverse(Rotation) * (contact - Center)).z + HalfExtents.z) / (2f * HalfExtents.z);
            // The caller already proved physical contact. Full-length parry includes the
            // rounded end contacts and avoids projection roundoff excluding the exact tips.
            if (region.x == 0f && region.y == 1f) return float.IsFinite(along);
            return along >= region.x && along <= region.y;
        }
    }

    /// <summary>Single authored local pose and shape shared by first-person visuals and authority queries.</summary>
    public static class GuardBladePose
    {
        public static readonly Vector3 RootRestPosition = new Vector3(0.26f, -0.20f, 0.30f);
        public static readonly Quaternion RootRestRotation = Quaternion.Euler(-8f, 12f, 14f);
        // One-handed guard stance (a high "ox" ward): the hilt is held beside the head, the blade points forward and down so the
        // tip is about waist height straight ahead. The first-person hand is placed exactly here, so the visible sword and the
        // authority's block volume are the same thing.
        public static readonly Vector3 GuardHandPosition = new Vector3(0.24f, -0.02f, 0.42f);
        public static readonly Quaternion GuardBladeRotation = Quaternion.Euler(30f, -14f, 0f);
        // Two-handed guard stance (the Dark and Darker hanging parry, the user's capture): the hands and the cross-guard are above and
        // left of the view, the blade enters at the top-left corner and runs diagonally down so its tip ends just right of and below
        // the crosshair (viewport about 0.56, 0.43).
        public static readonly Vector3 TwoHandedGuardHandPosition = new Vector3(-0.36f, 0.20f, 0.06f);
        public static readonly Quaternion TwoHandedGuardBladeRotation = Quaternion.Euler(12f, 22f, 0f);

        /// <summary>Guard root offset from the rest root (local to the aim frame) for a one- or two-handed weapon.</summary>
        public static Vector3 RootGuardOffset(bool twoHanded) =>
            (twoHanded ? TwoHandedGuardHandPosition : GuardHandPosition) - RootRestPosition;

        /// <summary>Guard root rotation relative to the rest root for a one- or two-handed weapon.</summary>
        public static Quaternion RootGuardRotation(bool twoHanded) =>
            Quaternion.Inverse(RootRestRotation) * (twoHanded ? TwoHandedGuardBladeRotation : GuardBladeRotation);
        // The first-person sword model is held at its grip (the root) and its blade runs about 1.05 m along +forward, so the
        // shared contact box covers that blade, starting just above the cross-guard and ending at the tip.
        // The fist sits right under the cross-guard, so the blade runs from just above the root to about 0.96 m.
        public static readonly Vector3 BladeLocalCenter = new Vector3(0f, 0f, 0.52f);
        public static readonly Vector3 BladeSize = new Vector3(0.055f, 0.03f, 0.88f);

        public static readonly Vector3 GripLocalCenter = new Vector3(0f, -0.02f, -0.07f);
        public static readonly Vector3 GripSize = new Vector3(0.035f, 0.035f, 0.15f);

        // The two-handed sword's visible blade, measured in the guard root's frame from the held model (review: Emberdelve
        // ShieldMotionReview.RenderGuardStill): the arms hold it 0.092 m right of and 0.068 m below the root axis, the cross-guard at
        // 0.12 m and the tip at 1.51 m. The block box is exactly that blade (FirstPersonSwingTests pins it to the drawn model).
        public static readonly Vector3 TwoHandedBladeLocalCenter = new Vector3(0.092f, -0.068f, 0.819f);
        public static readonly Vector3 TwoHandedBladeSize = new Vector3(0.055f, 0.03f, 1.39f);
        public static readonly Vector3 TwoHandedGripLocalCenter = new Vector3(0.092f, -0.088f, -0.07f);

        public static Vector3 BladeLocalCenterFor(bool twoHanded) => twoHanded ? TwoHandedBladeLocalCenter : BladeLocalCenter;
        public static Vector3 BladeSizeFor(bool twoHanded) => twoHanded ? TwoHandedBladeSize : BladeSize;
        public static Vector3 GripLocalCenterFor(bool twoHanded) => twoHanded ? TwoHandedGripLocalCenter : GripLocalCenter;

        /// <summary>The hand around the visible grip is vulnerable flesh, never defensive metal.</summary>
        public static Emberdelve.Combat.HitShape ResolveGripHitShape(Vector3 aimOrigin, Quaternion aimRotation, bool twoHanded = false)
        {
            ResolveRoot(aimOrigin, aimRotation, true, out Vector3 rootPosition, out Quaternion rootRotation, twoHanded);
            Vector3 center = rootPosition + rootRotation * GripLocalCenterFor(twoHanded);
            float radius = Mathf.Max(GripSize.x, GripSize.y) * .5f;
            Vector3 axis = rootRotation * Vector3.forward * (GripSize.z * .5f - radius);
            return new Emberdelve.Combat.HitShape(Emberdelve.Core.HitLocation.Arms, center - axis, center + axis, radius);
        }

        public static GuardBladeBox Resolve(Vector3 aimOrigin, Quaternion aimRotation, bool guarding, bool twoHanded = false)
        {
            ResolveRoot(aimOrigin, aimRotation, guarding, out Vector3 rootPosition, out Quaternion rootRotation, twoHanded);
            Vector3 center = rootPosition + rootRotation * BladeLocalCenterFor(twoHanded);
            Quaternion rotation = rootRotation;
            return new GuardBladeBox(center, rotation, BladeSizeFor(twoHanded));
        }

        public static void ResolveRoot(Vector3 aimOrigin, Quaternion aimRotation, bool guarding,
            out Vector3 rootPosition, out Quaternion rootRotation, bool twoHanded = false)
        {
            Vector3 localPosition = RootRestPosition + (guarding ? RootGuardOffset(twoHanded) : Vector3.zero);
            Quaternion localRotation = RootRestRotation * (guarding ? RootGuardRotation(twoHanded) : Quaternion.identity);
            rootPosition = aimOrigin + aimRotation * localPosition;
            rootRotation = aimRotation * localRotation;
        }
    }
}
