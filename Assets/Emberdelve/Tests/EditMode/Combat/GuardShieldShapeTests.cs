using Emberdelve.Player;
using NUnit.Framework;
using UnityEngine;

namespace Emberdelve.Combat.Tests
{
    public sealed class GuardShieldShapeTests
    {
        [Test]
        public void ModelUnderScaledArms_HasTheSameWorldPoseAndSizeAsAuthority()
        {
            var parent = new GameObject("shield scaled arms test");
            var model = new GameObject("shield model");
            try
            {
                parent.transform.localScale = Vector3.one * FirstPersonArmsDriver.ModelScale;
                model.transform.SetParent(parent.transform, false);
                var shield = GuardShieldPose.Resolve(new Vector3(7f,2f,4f), Quaternion.Euler(22f,90f,0f));
                GuardShieldPose.ApplyModel(model.transform,shield);
                Assert.That(Vector3.Distance(model.transform.position,shield.Position),Is.LessThan(.0001f));
                Assert.That(Quaternion.Angle(model.transform.rotation,shield.Rotation),Is.LessThan(.001f));
                Assert.That(Vector3.Distance(model.transform.lossyScale,Vector3.one*shield.Scale),Is.LessThan(.0001f));
                Assert.That(model.transform.localScale.x,Is.EqualTo(FirstPersonArmsDriver.ShieldScale).Within(.0001f));
            }
            finally { Object.DestroyImmediate(model); Object.DestroyImmediate(parent); }
        }

        [TestCase(0f)]
        [TestCase(.025f)]
        public void FaceContact_UsesActualDepthAndProjectileRadius(float radius)
        {
            var shield = new GuardShieldShape(Vector3.zero, Quaternion.identity, 1f);
            Assert.That(shield.TrySweep(Vector3.forward, Vector3.back, radius, out float distance), Is.True);
            Assert.That(distance, Is.EqualTo(1f - GuardShieldShape.Front - radius).Within(.0001f));
        }

        [TestCase(-.14f)]
        [TestCase(.14f)]
        public void EmptyLowerCorner_IsNotAnInvisibleRectangularShield(float x)
        {
            var shield = new GuardShieldShape(Vector3.zero, Quaternion.identity, 1f);
            Assert.That(shield.TrySweep(new Vector3(x, -.35f, 1f), new Vector3(x, -.35f, -1f), .01f, out _), Is.False);
        }

        [Test]
        public void RotationTranslationAndScale_FollowTheVisiblePose()
        {
            Vector3 position = new Vector3(10f, 3f, -4f);
            Quaternion rotation = Quaternion.Euler(37f, 123f, 8f);
            const float scale = .72f;
            var shield = new GuardShieldShape(position, rotation, scale);
            Assert.That(shield.TrySweep(position + rotation * Vector3.forward * scale,
                position + rotation * Vector3.back * scale, .02f * scale, out float distance), Is.True);
            Assert.That(distance, Is.EqualTo((1f - GuardShieldShape.Front - .02f) * scale).Within(.0001f));
        }

        [Test]
        public void RoundedTopCorner_DoesNotUseExpandedBoxCorner()
        {
            var shield = new GuardShieldShape(Vector3.zero, Quaternion.identity, 1f);
            Assert.That(shield.TrySweep(new Vector3(.17f,.42f,1f), new Vector3(.17f,.42f,-1f),.025f,out _), Is.False);
        }

        [Test]
        public void StartingInside_ReturnsImmediateContact_AndShortFlightOutsideMisses()
        {
            var shield = new GuardShieldShape(Vector3.zero, Quaternion.identity, 1f);
            Assert.That(shield.TrySweep(Vector3.zero, Vector3.forward, .01f, out float distance), Is.True);
            Assert.That(distance, Is.Zero);
            Assert.That(shield.TrySweep(Vector3.forward, Vector3.forward * .2f, .01f, out _), Is.False);
        }

        [Test]
        public void InvalidShapeOrRadius_FailsClosed()
        {
            Assert.That(new GuardShieldShape(Vector3.zero,Quaternion.identity,0f)
                .TrySweep(Vector3.forward,Vector3.back,.01f,out _),Is.False);
            Assert.That(new GuardShieldShape(Vector3.zero,Quaternion.identity,1f)
                .TrySweep(Vector3.forward,Vector3.back,float.NaN,out _),Is.False);
        }
    }
}
