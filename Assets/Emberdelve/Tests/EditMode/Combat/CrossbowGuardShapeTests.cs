using Emberdelve.Player;
using NUnit.Framework;
using UnityEngine;

namespace Emberdelve.Combat.Tests
{
    public sealed class CrossbowGuardShapeTests
    {
        [TestCase(-.2f)]
        [TestCase(.2f)]
        public void LimbStopsFlightAtItsActualFace(float x)
        {
            var shape = new CrossbowGuardShape(Vector3.zero, Quaternion.identity);
            Assert.That(shape.TrySweep(new Vector3(x,0,1),new Vector3(x,0,-1),.01f,out float distance),Is.True);
            Assert.That(distance,Is.EqualTo(.955f).Within(.0001f));
        }

        [Test]
        public void StockContactWinsBeforeOverlappingCenter()
        {
            var shape = new CrossbowGuardShape(Vector3.zero, Quaternion.identity);
            Assert.That(shape.TrySweep(new Vector3(0,0,1),new Vector3(0,0,-1),.01f,out float distance),Is.True);
            Assert.That(distance,Is.EqualTo(.63f).Within(.0001f));
        }

        [TestCase(-.2f)]
        [TestCase(.2f)]
        public void EmptyCornerIsNotDefensiveSurface(float x)
        {
            var shape = new CrossbowGuardShape(Vector3.zero, Quaternion.identity);
            Assert.That(shape.TrySweep(new Vector3(x,1,.25f),new Vector3(x,-1,.25f),.01f,out _),Is.False);
        }

        [Test]
        public void RaisedPoseRotatesEveryPartWithAim()
        {
            var shape = CrossbowGuardPose.Resolve(new Vector3(8,3,-6),Quaternion.Euler(24,110,0));
            Vector3 start=shape.Position+shape.Rotation*new Vector3(.2f,0,1);
            Vector3 end=shape.Position+shape.Rotation*new Vector3(.2f,0,-1);
            Assert.That(shape.TrySweep(start,end,.01f,out float distance),Is.True);
            Assert.That(distance,Is.EqualTo(.955f).Within(.0001f));
        }

        [Test]
        public void InvalidFlightFailsClosed()
        {
            var shape = new CrossbowGuardShape(Vector3.zero, Quaternion.identity);
            Assert.That(shape.TrySweep(Vector3.zero,Vector3.zero,.01f,out _),Is.False);
            Assert.That(shape.TrySweep(Vector3.forward,Vector3.back,float.NaN,out _),Is.False);
            Assert.That(shape.TrySweep(Vector3.forward,new Vector3(float.PositiveInfinity,0,0),.01f,out _),Is.False);
        }
    }
}
