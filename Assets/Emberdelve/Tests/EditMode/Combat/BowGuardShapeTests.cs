using Emberdelve.Player;
using NUnit.Framework;
using UnityEngine;

namespace Emberdelve.Combat.Tests
{
    public sealed class BowGuardShapeTests
    {
        [Test]
        public void GripStopsFlightAtVisibleWrapFace()
        {
            var shape = new BowGuardShape(Vector3.zero, Quaternion.identity);
            Assert.IsTrue(shape.TrySweep(new Vector3(0f, -.001f, 1f), new Vector3(0f, -.001f, -1f), .01f, out float distance));
            Assert.That(distance, Is.EqualTo(.955f).Within(.0001f));
        }

        [TestCase(1)]
        [TestCase(-1)]
        public void EachLimbStopsAtItsOwnWood(int sign)
        {
            var shape = new BowGuardShape(Vector3.zero, Quaternion.identity);
            Vector3 center = BowSolidGeometry.Curve(.5f, sign, 0f);
            Assert.IsTrue(shape.TrySweep(center + Vector3.forward, center - Vector3.forward, .005f, out float distance));
            Assert.That(distance, Is.InRange(.95f, 1f));
        }

        [TestCase(.3f, -.08f)] // Between wood and string.
        [TestCase(.3f, -.1f)] // Upper string, which is not armor.
        [TestCase(0f, .3f)] // Along the otherwise nocked arrow.
        public void OpenSpaceStringAndArrowAreNotArmor(float y, float z)
        {
            var shape = new BowGuardShape(Vector3.zero, Quaternion.identity);
            Assert.IsFalse(shape.TrySweep(new Vector3(-1f,y,z), new Vector3(1f,y,z), .005f, out _));
        }

        [Test]
        public void RotatedRaisedGuardKeepsGripContactDistance()
        {
            var shape = BowGuardPose.Resolve(new Vector3(8f,3f,-6f), Quaternion.Euler(24f,110f,0f));
            Vector3 start = shape.Position + shape.Rotation * new Vector3(0f,-.001f,1f);
            Vector3 end = shape.Position + shape.Rotation * new Vector3(0f,-.001f,-1f);
            Assert.IsTrue(shape.TrySweep(start,end,.01f,out float distance));
            Assert.That(distance, Is.EqualTo(.955f).Within(.0001f));
        }

        [Test]
        public void GeometryBoundsContainAllUndrawnMeshSpans()
        {
            for (int side=0; side<2; side++)
                for (int ring=0; ring<BowSolidGeometry.Rings-1; ring++)
                {
                    BowGuardPart part=BowGuardPose.Part(side*(BowSolidGeometry.Rings-1)+ring);
                    Vector3 min=part.Center-part.Size*.5f-Vector3.one*.000001f;
                    Vector3 max=part.Center+part.Size*.5f+Vector3.one*.000001f;
                    for (int end=0; end<2; end++)
                        for (int vertex=0; vertex<BowSolidGeometry.Sides; vertex++)
                        {
                            Vector3 v=BowSolidGeometry.RingVertex(side,ring+end,vertex,0f);
                            Assert.That(v.x, Is.InRange(min.x,max.x));
                            Assert.That(v.y, Is.InRange(min.y,max.y));
                            Assert.That(v.z, Is.InRange(min.z,max.z));
                        }
                }
        }

        [Test]
        public void ModelPoseCompensatesUniformParentScale()
        {
            var parent = new GameObject("BowScaleParent");
            var model = new GameObject("BowModel");
            try
            {
                parent.transform.SetPositionAndRotation(new Vector3(3f,2f,1f),Quaternion.Euler(12f,35f,6f));
                parent.transform.localScale=Vector3.one*1.3f;
                model.transform.SetParent(parent.transform,false);
                Vector3 eye=new Vector3(2f,1.7f,-3f);
                Quaternion aim=Quaternion.Euler(18f,125f,0f);
                BowGuardPose.ApplyModel(model.transform,eye,aim);
                BowGuardShape shape=BowGuardPose.Resolve(eye,aim);
                Assert.Less(Vector3.Distance(model.transform.position,shape.Position),.0001f);
                Assert.Less(Quaternion.Angle(model.transform.rotation,shape.Rotation),.001f);
                Assert.Less(Vector3.Distance(model.transform.lossyScale,Vector3.one),.0001f);
                Assert.Less(Vector3.Distance(model.transform.TransformPoint(BowGuardPose.RightGrip),
                    shape.Position+shape.Rotation*BowGuardPose.RightGrip),.0001f);
            }
            finally { Object.DestroyImmediate(parent); }
        }

        [Test]
        public void InvalidSweepFailsClosed()
        {
            var shape=new BowGuardShape(Vector3.zero,Quaternion.identity);
            Assert.IsFalse(shape.TrySweep(Vector3.zero,Vector3.zero,.01f,out _));
            Assert.IsFalse(shape.TrySweep(Vector3.forward,Vector3.back,float.NaN,out _));
            Assert.IsFalse(shape.TrySweep(Vector3.forward,new Vector3(float.PositiveInfinity,0f,0f),.01f,out _));
            Assert.IsFalse(new BowGuardShape(Vector3.zero,new Quaternion(0f,0f,0f,0f))
                .TrySweep(Vector3.forward,Vector3.back,.01f,out _));
        }
    }
}
