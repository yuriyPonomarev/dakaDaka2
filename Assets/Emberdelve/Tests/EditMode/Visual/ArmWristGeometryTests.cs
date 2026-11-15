using System.Collections.Generic;
using System.Linq;
using Emberdelve.EditorTools;
using NUnit.Framework;
using UnityEngine;
using Vertex = Emberdelve.EditorTools.ArmWristGeometry.Vertex;

namespace Emberdelve.Tests.Visual
{
    public sealed class ArmWristGeometryTests
    {
        private static BoneWeight Single(int bone) => new BoneWeight { boneIndex0 = bone, weight0 = 1f };
        private static Vertex V(float x, float y, int bone = 0) => new Vertex(new Vector3(x, y, 0), Vector3.forward, Single(bone));
        private static float Area(IReadOnlyList<Vertex> polygon)
        {
            float result = 0f;
            for (int i = 1; i + 1 < polygon.Count; i++)
                result += Vector3.Cross(polygon[i].Position - polygon[0].Position,
                    polygon[i + 1].Position - polygon[0].Position).magnitude * 0.5f;
            return result;
        }
        private static Dictionary<int, float> Weights(BoneWeight w)
        {
            var result = new Dictionary<int, float>();
            var ids = new[] { w.boneIndex0, w.boneIndex1, w.boneIndex2, w.boneIndex3 };
            var values = new[] { w.weight0, w.weight1, w.weight2, w.weight3 };
            for (int i = 0; i < 4; i++)
            {
                Assert.That(ids[i], Is.GreaterThanOrEqualTo(0));
                Assert.That(values[i], Is.InRange(0f, 1f));
                if (values[i] == 0f) continue;
                Assert.That(result.ContainsKey(ids[i]), Is.False, "shared bone influences must be accumulated");
                result.Add(ids[i], values[i]);
            }
            Assert.That(values.Sum(), Is.EqualTo(1f).Within(1e-6f));
            return result;
        }
        private static void NoDuplicates(IReadOnlyList<Vertex> polygon)
        {
            for (int i = 0; i < polygon.Count; i++)
                for (int j = i + 1; j < polygon.Count; j++)
                    Assert.That(Vector3.Distance(polygon[i].Position, polygon[j].Position), Is.GreaterThan(1e-6f));
        }
        [Test] public void CrossingTriangleConservesAreaAndSharesTheSameWeightedBoundary()
        {
            var a = V(-1, 0, 0); var b = V(1, 0, 1); var c = V(1, 2, 2);
            b.Normal = Vector3.up;
            var positive = ArmWristGeometry.ClipTriangle(a, b, c, Vector3.zero, Vector3.right, true);
            var negative = ArmWristGeometry.ClipTriangle(a, b, c, Vector3.zero, Vector3.right, false);
            Assert.That(positive.Count, Is.EqualTo(4)); Assert.That(negative.Count, Is.EqualTo(3));
            Assert.That(Area(positive) + Area(negative), Is.EqualTo(Area(new[] { a, b, c })).Within(1e-6f));
            Assert.That(positive.All(v => v.Position.x >= -1e-6f), Is.True);
            Assert.That(negative.All(v => v.Position.x <= 1e-6f), Is.True);
            NoDuplicates(positive); NoDuplicates(negative);
            var boundary = positive.Where(v => Mathf.Abs(v.Position.x) < 1e-6f).ToArray();
            Assert.That(boundary.Length, Is.EqualTo(2));
            Assert.That(boundary.Select(v => v.Position.y).OrderBy(y => y).ToArray(), Is.EqualTo(new[] { 0f, 1f }));
            foreach (var vertex in boundary)
            {
                var other = negative.Single(v => Vector3.Distance(v.Position, vertex.Position) < 1e-6f);
                Assert.That(vertex.Normal.magnitude, Is.EqualTo(1f).Within(1e-6f));
                Assert.That(Vector3.Distance(vertex.Normal, other.Normal), Is.LessThan(1e-6f));
                CollectionAssert.AreEquivalent(Weights(vertex.Weight), Weights(other.Weight));
                var weights = Weights(vertex.Weight);
                Assert.That(weights[0], Is.EqualTo(0.5f).Within(1e-6f));
                Assert.That(weights[vertex.Position.y < 0.5f ? 1 : 2], Is.EqualTo(0.5f).Within(1e-6f));
            }
            Assert.That(positive.Single(v => v.Position == b.Position).Normal, Is.EqualTo(b.Normal));
            Assert.That(positive.Single(v => v.Position == c.Position).Weight, Is.EqualTo(c.Weight));
        }
        [Test] public void IntersectionsAccumulateSharedBonesKeepStrongestFourAndNormalize()
        {
            var a = new Vertex(new Vector3(-1, 0, 0), Vector3.forward, new BoneWeight {
                boneIndex0 = 0, weight0 = .4f, boneIndex1 = 1, weight1 = .3f,
                boneIndex2 = 2, weight2 = .2f, boneIndex3 = 3, weight3 = .1f });
            var b = new Vertex(new Vector3(1, 0, 0), Vector3.up, new BoneWeight {
                boneIndex0 = 0, weight0 = .4f, boneIndex1 = 4, weight1 = .3f,
                boneIndex2 = 5, weight2 = .2f, boneIndex3 = 6, weight3 = .1f });
            var result = ArmWristGeometry.ClipTriangle(a, b, V(1, 2), Vector3.zero, Vector3.right, true);
            var crossing = result.Single(v => v.Position.sqrMagnitude < 1e-10f);
            var weights = Weights(crossing.Weight);
            CollectionAssert.AreEquivalent(new[] { 0, 1, 4, 2 }, weights.Keys);
            Assert.That(weights[0], Is.EqualTo(.4f / .8f).Within(1e-6f));
            Assert.That(weights[1], Is.EqualTo(.15f / .8f).Within(1e-6f));
            Assert.That(weights[4], Is.EqualTo(.15f / .8f).Within(1e-6f));
            Assert.That(weights[2], Is.EqualTo(.1f / .8f).Within(1e-6f));
        }
        [Test] public void WhollyKeptTriangleRetainsEveryVertexAndRejectedTriangleIsEmpty()
        {
            var a = V(1, 0, 2); var b = V(2, 0, 3); var c = V(2, 1, 4);
            a.Normal = new Vector3(0, 0, 2); // retained vertices must not be rewritten or renormalized
            var kept = ArmWristGeometry.ClipTriangle(a, b, c, Vector3.zero, Vector3.right, true);
            CollectionAssert.AreEqual(new[] { a, b, c }, kept);
            Assert.That(ArmWristGeometry.ClipTriangle(a, b, c, Vector3.zero, Vector3.right, false), Is.Empty);
        }
        [Test] public void ExactPlaneEdgeHasNoRepeatedEndpointsInEitherHalf()
        {
            var a = V(0, 0, 1); var b = V(0, 1, 2); var c = V(1, 0, 3);
            var kept = ArmWristGeometry.ClipTriangle(a, b, c, Vector3.zero, Vector3.right, true);
            var edge = ArmWristGeometry.ClipTriangle(a, b, c, Vector3.zero, Vector3.right, false);
            Assert.That(kept.Count, Is.EqualTo(3)); Assert.That(edge.Count, Is.EqualTo(2));
            NoDuplicates(kept); NoDuplicates(edge);
            CollectionAssert.AreEquivalent(new[] { a, b }, edge);
            Assert.That(Area(edge), Is.Zero);
        }
        [Test] public void TranslatedObliquePlaneClipsWithoutChangingWinding()
        {
            var a = V(0, 0); var b = V(3, 0); var c = V(0, 3);
            var point = new Vector3(1, 0, 0); var normal = new Vector3(2, 2, 0);
            var positive = ArmWristGeometry.ClipTriangle(a, b, c, point, normal, true);
            var negative = ArmWristGeometry.ClipTriangle(a, b, c, point, normal, false);
            Assert.That(Area(positive) + Area(negative), Is.EqualTo(4.5f).Within(1e-5f));
            foreach (var polygon in new[] { positive, negative })
                for (int i = 1; i + 1 < polygon.Count; i++)
                    Assert.That(Vector3.Cross(polygon[i].Position - polygon[0].Position,
                        polygon[i + 1].Position - polygon[0].Position).z, Is.GreaterThan(0f));
        }
    }
}
