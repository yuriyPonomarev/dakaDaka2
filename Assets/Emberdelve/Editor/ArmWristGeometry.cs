using System.Collections.Generic;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    /// <summary>Clips a skinned triangle at a wrist plane without moving retained source vertices.</summary>
    public static class ArmWristGeometry
    {
        public struct Vertex
        {
            public Vector3 Position;
            public Vector3 Normal;
            public BoneWeight Weight;
            public Vertex(Vector3 position, Vector3 normal, BoneWeight weight)
            { Position = position; Normal = normal; Weight = weight; }
        }

        public static List<Vertex> ClipTriangle(Vertex a, Vertex b, Vertex c,
            Vector3 planePoint, Vector3 planeNormal, bool keepPositive)
        {
            var source = new[] { a, b, c };
            var result = new List<Vertex>(4);
            Vertex previous = c;
            float previousDistance = Vector3.Dot(previous.Position - planePoint, planeNormal);
            bool previousInside = keepPositive ? previousDistance >= 0f : previousDistance <= 0f;
            foreach (Vertex current in source)
            {
                float distance = Vector3.Dot(current.Position - planePoint, planeNormal);
                bool inside = keepPositive ? distance >= 0f : distance <= 0f;
                if (inside != previousInside)
                {
                    // An endpoint exactly on the plane remains the original vertex, including its weights.
                    Vertex crossing = previousDistance == 0f ? previous : distance == 0f ? current :
                        Interpolate(previous, current, previousDistance / (previousDistance - distance));
                    AppendDistinct(result, crossing);
                }
                if (inside) AppendDistinct(result, current);
                previous = current; previousDistance = distance; previousInside = inside;
            }
            if (result.Count > 1 && SamePosition(result[0], result[result.Count - 1])) result.RemoveAt(result.Count - 1);
            return result;
        }

        private static bool SamePosition(Vertex a, Vertex b) => (a.Position - b.Position).sqrMagnitude <= 1e-12f;
        private static void AppendDistinct(List<Vertex> vertices, Vertex vertex)
        {
            if (vertices.Count == 0 || !SamePosition(vertices[vertices.Count - 1], vertex)) vertices.Add(vertex);
        }
        private static Vertex Interpolate(Vertex a, Vertex b, float t)
        {
            t = Mathf.Clamp01(t);
            Vector3 normal = Vector3.LerpUnclamped(a.Normal, b.Normal, t);
            if (normal.sqrMagnitude < 1e-12f) normal = a.Normal.sqrMagnitude > 1e-12f ? a.Normal : b.Normal;
            if (normal.sqrMagnitude < 1e-12f) normal = Vector3.up;
            var influences = new Dictionary<int, float>(8);
            Accumulate(influences, a.Weight, 1f - t);
            Accumulate(influences, b.Weight, t);
            var ranked = new List<KeyValuePair<int, float>>(influences);
            ranked.Sort((x, y) => {
                int byWeight = y.Value.CompareTo(x.Value);
                return byWeight != 0 ? byWeight : x.Key.CompareTo(y.Key);
            });
            float total = 0f;
            for (int i = 0; i < Mathf.Min(4, ranked.Count); i++) total += ranked[i].Value;
            var weight = new BoneWeight();
            if (total <= 0f) weight.weight0 = 1f;
            else
            {
                for (int i = 0; i < Mathf.Min(4, ranked.Count); i++)
                {
                    int bone = ranked[i].Key;
                    float value = ranked[i].Value / total;
                    switch (i)
                    {
                        case 0: weight.boneIndex0 = bone; weight.weight0 = value; break;
                        case 1: weight.boneIndex1 = bone; weight.weight1 = value; break;
                        case 2: weight.boneIndex2 = bone; weight.weight2 = value; break;
                        case 3: weight.boneIndex3 = bone; weight.weight3 = value; break;
                    }
                }
            }
            return new Vertex(Vector3.LerpUnclamped(a.Position, b.Position, t), normal.normalized, weight);
        }
        private static void Accumulate(Dictionary<int, float> influences, BoneWeight weight, float scale)
        {
            Add(influences, weight.boneIndex0, weight.weight0 * scale);
            Add(influences, weight.boneIndex1, weight.weight1 * scale);
            Add(influences, weight.boneIndex2, weight.weight2 * scale);
            Add(influences, weight.boneIndex3, weight.weight3 * scale);
        }
        private static void Add(Dictionary<int, float> influences, int bone, float value)
        {
            if (bone < 0 || value <= 0f) return;
            influences.TryGetValue(bone, out float previous);
            influences[bone] = previous + value;
        }
    }
}
