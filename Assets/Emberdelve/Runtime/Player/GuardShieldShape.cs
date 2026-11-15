using UnityEngine;

namespace Emberdelve.Player
{
    /// <summary>One raised shield pose for authority, owner view and remote presentation.</summary>
    public static class GuardShieldPose
    {
        public static readonly Vector3 Grip = new Vector3(-.17f, -.17f, .52f);
        public static readonly Vector3 Mount = new Vector3(0f, -.115f * 1.3f, 0f);
        public static readonly Quaternion LocalRotation = Quaternion.Euler(-6f, 4f, 0f);
        public const float Scale = .72f * 1.3f; // Preserve the original first-person size including the arms model scale.

        public static GuardShieldShape Resolve(Vector3 eye, Quaternion aim)
        {
            Quaternion rotation = aim * LocalRotation;
            return new GuardShieldShape(eye + aim * Grip + rotation * Mount, rotation, Scale);
        }

        public static void ApplyModel(Transform model, in GuardShieldShape shield)
        {
            model.SetPositionAndRotation(shield.Position, shield.Rotation);
            SetWorldScale(model, shield.Scale);
        }

        public static void SetWorldScale(Transform model, float scale)
        {
            Vector3 parent = model.parent != null ? model.parent.lossyScale : Vector3.one;
            model.localScale = new Vector3(scale / Mathf.Max(.0001f, Mathf.Abs(parent.x)),
                scale / Mathf.Max(.0001f, Mathf.Abs(parent.y)), scale / Mathf.Max(.0001f, Mathf.Abs(parent.z)));
        }
    }

    /// <summary>Rounded sphere sweep against the convex silhouette and depth of our kite shield.</summary>
    public readonly struct GuardShieldShape
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly float Scale;

        // Prefab-root metres, measured from WV_kite_shield's metal trim, not screen pixels.
        // Collinear detail below 1 mm is removed. Source: shield-geometry-20261112/bounds-hulls.json.
        private static readonly Vector2[] Outline = {
            new Vector2(-.120698f,-.108270f), new Vector2(-.111032f,-.192622f),
            new Vector2(-.101500f,-.235269f), new Vector2(-.081162f,-.292563f),
            new Vector2(-.067067f,-.323899f), new Vector2(-.041517f,-.361868f),
            new Vector2(-.022701f,-.385505f), new Vector2(-.010521f,-.396356f),
            new Vector2(.001233f,-.400000f), new Vector2(.013982f,-.396356f),
            new Vector2(.045025f,-.361811f), new Vector2(.069508f,-.325534f),
            new Vector2(.084621f,-.292567f), new Vector2(.104961f,-.235269f),
            new Vector2(.114494f,-.192622f), new Vector2(.124159f,-.108270f),
            new Vector2(.157181f,.314424f), new Vector2(.120449f,.350510f),
            new Vector2(.085805f,.376043f), new Vector2(.059321f,.389772f),
            new Vector2(.024749f,.399967f), new Vector2(-.021287f,.399967f),
            new Vector2(-.059575f,.388672f), new Vector2(-.084207f,.375487f),
            new Vector2(-.116988f,.350510f), new Vector2(-.153720f,.314424f)
        };
        public const float Back = -.023780f;
        public const float Front = .053537f;

        public GuardShieldShape(Vector3 position, Quaternion rotation, float scale)
        { Position = position; Rotation = rotation; Scale = scale; }

        public bool TrySweep(Vector3 start, Vector3 end, float radius, out float distance)
        {
            distance = 0f;
            if (!(Scale > 0f) || float.IsInfinity(Scale) || float.IsNaN(radius) || float.IsInfinity(radius)) return false;
            float length = Vector3.Distance(start, end);
            if (!(length > .00001f) || float.IsInfinity(length)) return false;
            Quaternion inverse = Quaternion.Inverse(Rotation);
            Vector3 a = inverse * (start - Position) / Scale;
            Vector3 delta = inverse * (end - start) / Scale;
            float r = Mathf.Max(0f, radius) / Scale;
            float lo = 0f, hi = 1f;
            // Cheap expanded-prism clipping first. Rounded corners are checked by distance below.
            if (!Clip(a.z - Front, delta.z, r, ref lo, ref hi)
                || !Clip(Back - a.z, -delta.z, r, ref lo, ref hi)) return false;
            for (int i = 0; i < Outline.Length; i++)
            {
                Vector2 v = Outline[i], edge = Outline[(i + 1) % Outline.Length] - v;
                Vector2 normal = new Vector2(edge.y, -edge.x).normalized;
                if (!Clip(Vector2.Dot(normal, new Vector2(a.x, a.y) - v),
                    normal.x * delta.x + normal.y * delta.y, r, ref lo, ref hi)) return false;
            }
            float entry = lo, exit = hi;
            float rr = r * r + 1e-10f;
            if (DistanceSquared(a + delta * lo) <= rr) { distance = lo * length; return true; }
            // Distance to a convex solid is convex along a segment: find its minimum then first contact.
            for (int i = 0; i < 28; i++)
            {
                float p = lo + (hi - lo) / 3f, q = hi - (hi - lo) / 3f;
                if (DistanceSquared(a + delta * p) <= DistanceSquared(a + delta * q)) hi = q; else lo = p;
            }
            float closest = (lo + hi) * .5f;
            if (DistanceSquared(a + delta * exit) < DistanceSquared(a + delta * closest)) closest = exit;
            if (DistanceSquared(a + delta * closest) > rr) return false;
            lo = entry; hi = closest;
            for (int i = 0; i < 28; i++)
            {
                float mid = (lo + hi) * .5f;
                if (DistanceSquared(a + delta * mid) <= rr) hi = mid; else lo = mid;
            }
            distance = hi * length;
            return true;
        }

        private static bool Clip(float signedDistance, float rate, float radius, ref float lo, ref float hi)
        {
            if (Mathf.Abs(rate) < 1e-8f) return signedDistance <= radius;
            float t = (radius - signedDistance) / rate;
            if (rate > 0f) hi = Mathf.Min(hi, t); else lo = Mathf.Max(lo, t);
            return lo <= hi;
        }

        private static float DistanceSquared(Vector3 point)
        {
            Vector2 p = new Vector2(point.x, point.y);
            bool inside = true;
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < Outline.Length; i++)
            {
                Vector2 a = Outline[i], edge = Outline[(i + 1) % Outline.Length] - a;
                Vector2 offset = p - a;
                if (edge.x * offset.y - edge.y * offset.x < 0f) inside = false;
                float t = Mathf.Clamp01(Vector2.Dot(offset, edge) / edge.sqrMagnitude);
                nearest = Mathf.Min(nearest, (offset - edge * t).sqrMagnitude);
            }
            float z = point.z - Mathf.Clamp(point.z, Back, Front);
            return (inside ? 0f : nearest) + z * z;
        }
    }
}
