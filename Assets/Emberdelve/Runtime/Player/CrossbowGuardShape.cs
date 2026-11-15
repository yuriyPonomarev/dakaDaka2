using UnityEngine;

namespace Emberdelve.Player
{
    public enum CrossbowPartMaterial { Shaft, Grip, Edge }
    /// <summary>Solid components of our procedural crossbow. String and ammunition are not armor.</summary>
    public readonly struct CrossbowGuardPart
    {
        public readonly string Name;
        public readonly Vector3 Center, Size;
        public readonly Quaternion Rotation;
        public readonly CrossbowPartMaterial Material;
        public CrossbowGuardPart(string name, Vector3 center, Vector3 size, Quaternion rotation, CrossbowPartMaterial material = CrossbowPartMaterial.Shaft)
        { Name = name; Center = center; Size = size; Rotation = rotation; Material = material; }
    }

    /// <summary>A union of actual component boxes, not an enclosing rectangle over empty corners.</summary>
    public readonly struct CrossbowGuardShape
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public CrossbowGuardShape(Vector3 position, Quaternion rotation) { Position = position; Rotation = rotation; }

        public bool TrySweep(Vector3 start, Vector3 end, float radius, out float distance)
        {
            distance = 0f;
            if (!Finite(start) || !Finite(end) || !Finite(Position) || !(radius >= 0f) || float.IsInfinity(radius)) return false;
            bool found = false;
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < CrossbowGuardPose.PartCount; i++)
            {
                CrossbowGuardPart part = CrossbowGuardPose.Part(i);
                var box = new GuardBladeBox(Position + Rotation * part.Center, Rotation * part.Rotation, part.Size);
                if (box.TrySweep(start, end, radius, out float hit) && hit < nearest) { found = true; nearest = hit; }
            }
            if (found) distance = nearest;
            return found;
        }

        private static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x)
            && !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
    }

    public static class CrossbowGuardPose
    {
        public static readonly Vector3 ReadyPosition = new Vector3(.15f, -.12f, .39f);
        public static readonly Quaternion ReadyRotation = Quaternion.Euler(0f, -8f, 0f);
        // Project-authored guard pose, not a measured original animation.
        public static readonly Vector3 RaisedPosition = new Vector3(0f, -.1f, .55f);
        public static readonly Quaternion RaisedRotation = Quaternion.Euler(-65f, 0f, 0f);
        private static readonly CrossbowGuardPart[] Parts = {
            new CrossbowGuardPart("Crossbow_Stock", new Vector3(0,-.025f,.10f), new Vector3(.07f,.09f,.52f), Quaternion.identity),
            new CrossbowGuardPart("Crossbow_Grip", new Vector3(0,-.15f,-.01f), new Vector3(.065f,.20f,.08f), Quaternion.Euler(-12f,0,0), CrossbowPartMaterial.Grip),
            new CrossbowGuardPart("Crossbow_Limb", Vector3.zero, new Vector3(.52f,.045f,.07f), Quaternion.identity),
            new CrossbowGuardPart("Crossbow_Limb_Center", new Vector3(0,0,.03f), new Vector3(.12f,.075f,.10f), Quaternion.identity, CrossbowPartMaterial.Grip),
            new CrossbowGuardPart("Crossbow_Trigger", new Vector3(0,-.12f,.10f), new Vector3(.018f,.08f,.025f), Quaternion.identity, CrossbowPartMaterial.Edge)
        };
        public static int PartCount => Parts.Length;
        public static CrossbowGuardPart Part(int index) => Parts[index];
        public static CrossbowGuardShape Resolve(Vector3 eye, Quaternion aim) =>
            new CrossbowGuardShape(eye + aim * RaisedPosition, aim * RaisedRotation);

        /// <summary>Match authority dimensions under the existing uniformly scaled model parents; not a shear decomposition.</summary>
        public static void ApplyModel(Transform root, Vector3 eye, Quaternion aim)
        {
            if (root == null) return;
            CrossbowGuardShape shape = Resolve(eye, aim);
            root.SetPositionAndRotation(shape.Position, shape.Rotation);
            Vector3 parentScale = root.parent != null ? root.parent.lossyScale : Vector3.one;
            root.localScale = new Vector3(Reciprocal(parentScale.x), Reciprocal(parentScale.y), Reciprocal(parentScale.z));
        }

        private static float Reciprocal(float scale) => Mathf.Abs(scale) > 1e-6f ? 1f / scale : 1f;
    }
}
