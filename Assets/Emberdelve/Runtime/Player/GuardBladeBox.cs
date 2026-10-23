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
    }

    /// <summary>Single authored local pose and shape shared by first-person visuals and authority queries.</summary>
    public static class GuardBladePose
    {
        public static readonly Vector3 RootRestPosition = new Vector3(0.26f, -0.20f, 0.30f);
        public static readonly Quaternion RootRestRotation = Quaternion.Euler(-8f, 12f, 14f);
        public static readonly Vector3 RootGuardOffset = new Vector3(0f, 0.08f, 0f);
        public static readonly Quaternion RootGuardRotation = Quaternion.Euler(-22f, -5f, 0f);
        // The metal tip mesh ends at local z=0.80, so the shared contact box covers the complete
        // visible blade while stopping at the hand guard rather than extending over the grip.
        public static readonly Vector3 BladeLocalCenter = new Vector3(0f, 0f, 0.40f);
        public static readonly Vector3 BladeSize = new Vector3(0.055f, 0.018f, 0.80f);

        public static GuardBladeBox Resolve(Vector3 aimOrigin, Quaternion aimRotation, bool guarding)
        {
            ResolveRoot(aimOrigin, aimRotation, guarding, out Vector3 rootPosition, out Quaternion rootRotation);
            Vector3 center = rootPosition + rootRotation * BladeLocalCenter;
            Quaternion rotation = rootRotation;
            return new GuardBladeBox(center, rotation, BladeSize);
        }

        public static void ResolveRoot(Vector3 aimOrigin, Quaternion aimRotation, bool guarding,
            out Vector3 rootPosition, out Quaternion rootRotation)
        {
            Vector3 localPosition = RootRestPosition + (guarding ? RootGuardOffset : Vector3.zero);
            Quaternion localRotation = RootRestRotation * (guarding ? RootGuardRotation : Quaternion.identity);
            rootPosition = aimOrigin + aimRotation * localPosition;
            rootRotation = aimRotation * localRotation;
        }
    }
}
