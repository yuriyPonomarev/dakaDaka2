using UnityEngine;

namespace Emberdelve.Player
{
    /// <summary>The existing recurve mesh and grip authoring, shared by rendering and guard geometry.</summary>
    public static class BowSolidGeometry
    {
        public const int Rings = 17, Sides = 6, GripCount = 9;
        public static readonly Vector3 GripSize = new Vector3(.054f, .014f, .07f);
        public static Vector3 GripCenter(int index) => new Vector3(0f, -.065f + index * .016f, 0f);
        public static Vector3 Curve(float t, int sign, float draw) =>
            new Vector3(0f, sign * (.075f + .48f * t - .035f * draw * t),
                .12f * Mathf.Sin(t * Mathf.PI) - .055f * t - .085f * draw * t * t);
        public static Vector3 RingVertex(int side, int ring, int vertex, float draw)
        {
            float t = (float)ring / (Rings - 1);
            float angle = vertex * Mathf.PI * 2f / Sides;
            return Curve(t, side == 0 ? 1 : -1, draw) + new Vector3(
                Mathf.Cos(angle) * Mathf.Lerp(.025f, .008f, t), 0f,
                Mathf.Sin(angle) * Mathf.Lerp(.028f, .012f, t));
        }
    }

    public readonly struct BowGuardPart
    {
        public readonly Vector3 Center, Size;
        public BowGuardPart(Vector3 center, Vector3 size) { Center = center; Size = size; }
    }

    /// <summary>
    /// Union of tight boxes around each short undrawn limb span and actual grip wraps.
    /// A local approximation of the tapered hexagonal wood, not a triangle-exact collider.
    /// No volume spans the open bow interior; strings and ammunition are never armor.
    /// </summary>
    public readonly struct BowGuardShape
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public BowGuardShape(Vector3 position, Quaternion rotation) { Position = position; Rotation = rotation; }
        public bool TrySweep(Vector3 start, Vector3 end, float radius, out float distance)
        {
            distance = 0f;
            if (!Finite(start) || !Finite(end) || !Finite(Position) || !Finite(Rotation)
                || !(radius >= 0f) || float.IsInfinity(radius)) return false;
            bool found = false;
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < BowGuardPose.PartCount; i++)
            {
                BowGuardPart part = BowGuardPose.Part(i);
                var box = new GuardBladeBox(Position + Rotation * part.Center, Rotation, part.Size);
                if (box.TrySweep(start, end, radius, out float hit) && hit < nearest)
                { found = true; nearest = hit; }
            }
            if (found) distance = nearest;
            return found;
        }
        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        private static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
        private static bool Finite(Quaternion q) => Finite(q.x) && Finite(q.y) && Finite(q.z) && Finite(q.w)
            && Mathf.Abs(q.x*q.x + q.y*q.y + q.z*q.z + q.w*q.w - 1f) < .001f;
    }

    public static class BowGuardPose
    {
        // Original-authored sideways chest guard, not a measured reference pose.
        public static readonly Vector3 RaisedPosition = new Vector3(0f, -.18f, .48f);
        public static readonly Quaternion RaisedRotation = Quaternion.Euler(0f, 0f, -90f);
        public static readonly Vector3 LeftGrip = new Vector3(0f, -.05f, 0f);
        public static readonly Vector3 RightGrip = BowSolidGeometry.Curve(.4f, 1, 0f);
        private static readonly BowGuardPart[] Parts = BuildParts();
        public static int PartCount => Parts.Length;
        public static BowGuardPart Part(int index) => Parts[index];
        public static BowGuardShape Resolve(Vector3 eye, Quaternion aim) =>
            new BowGuardShape(eye + aim * RaisedPosition, aim * RaisedRotation);

        public static void ApplyModel(Transform root, Vector3 eye, Quaternion aim)
        {
            if (root == null) return;
            BowGuardShape shape = Resolve(eye, aim);
            root.SetPositionAndRotation(shape.Position, shape.Rotation);
            // Existing uniform positive-scale parents only; arbitrary shear is unsupported.
            Vector3 scale = root.parent != null ? root.parent.lossyScale : Vector3.one;
            root.localScale = new Vector3(Reciprocal(scale.x), Reciprocal(scale.y), Reciprocal(scale.z));
        }
        private static float Reciprocal(float v) => Mathf.Abs(v) > 1e-6f ? 1f / v : 1f;
        private static BowGuardPart[] BuildParts()
        {
            int limbCount = 2 * (BowSolidGeometry.Rings - 1);
            var parts = new BowGuardPart[limbCount + BowSolidGeometry.GripCount];
            int index = 0;
            for (int side = 0; side < 2; side++)
                for (int ring = 0; ring < BowSolidGeometry.Rings - 1; ring++)
                {
                    Vector3 min = BowSolidGeometry.RingVertex(side, ring, 0, 0f), max = min;
                    for (int end = 0; end < 2; end++)
                        for (int v = 0; v < BowSolidGeometry.Sides; v++)
                        {
                            Vector3 vertex = BowSolidGeometry.RingVertex(side, ring + end, v, 0f);
                            min = Vector3.Min(min, vertex); max = Vector3.Max(max, vertex);
                        }
                    parts[index++] = new BowGuardPart((min + max) * .5f, max - min);
                }
            for (int i = 0; i < BowSolidGeometry.GripCount; i++)
                parts[index++] = new BowGuardPart(BowSolidGeometry.GripCenter(i), BowSolidGeometry.GripSize);
            return parts;
        }
    }
}
