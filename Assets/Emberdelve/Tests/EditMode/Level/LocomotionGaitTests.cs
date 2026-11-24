using Emberdelve.AI;
using NUnit.Framework;
using UnityEngine;

namespace Emberdelve.Level.Tests
{
    /// <summary>Speed-matched locomotion: loop rate from the ground speed, reverse for a retreat, a bounded body turn for a sideways move.</summary>
    public sealed class LocomotionGaitTests
    {
        private const float Stride = 2.2f;

        [Test]
        public void ForwardMoveMatchesTheLoopToTheGroundSpeed()
        {
            LocomotionGait.Resolve(new Vector3(0f, 0f, 2.75f), Vector3.forward, Stride, 1f, out float rate, out float turn);
            Assert.That(rate, Is.EqualTo(2.75f / Stride).Within(1e-4f), "the feet cover exactly the ground the body covers");
            Assert.That(turn, Is.EqualTo(0f).Within(1e-4f));
        }

        [Test]
        public void BackwardMovePlaysTheLoopInReverseWithoutTurningAround()
        {
            LocomotionGait.Resolve(new Vector3(0f, 0f, -1.5f), Vector3.forward, Stride, 1f, out float rate, out float turn);
            Assert.That(rate, Is.LessThan(0f), "a retreat plays the walk backward (no moonwalk)");
            Assert.That(Mathf.Abs(rate), Is.EqualTo(Mathf.Max(LocomotionGait.MinRate, 1.5f / Stride)).Within(1e-4f));
            Assert.That(Mathf.Abs(turn), Is.LessThan(1e-3f), "straight back: the body keeps facing its aim");
        }

        [TestCase(1f, 30f)]
        [TestCase(-1f, -30f)]
        public void SidewaysMoveTurnsTheBodyTowardTheMotionWithinTheLimit(float side, float expectedSign)
        {
            LocomotionGait.Resolve(new Vector3(side * 2f, 0f, 0f), Vector3.forward, Stride, 1f, out float rate, out float turn);
            Assert.That(Mathf.Abs(turn), Is.EqualTo(LocomotionGait.MaxBodyTurn).Within(1e-3f), "a pure side step turns the body as far as allowed");
            Assert.That(Mathf.Sign(turn), Is.EqualTo(Mathf.Sign(expectedSign)), "toward the side of the motion");
            Assert.That(rate, Is.GreaterThan(0f));
        }

        [Test]
        public void BackwardDiagonalFoldsTheTurnTowardTheReverseDirection()
        {
            // Moving back and to the right (135 deg): the body turns 45 deg to the LEFT and the loop plays backward.
            Vector3 velocity = Quaternion.Euler(0f, 135f, 0f) * Vector3.forward * 2f;
            LocomotionGait.Resolve(velocity, Vector3.forward, Stride, 1f, out float rate, out float turn);
            Assert.That(rate, Is.LessThan(0f));
            Assert.That(turn, Is.EqualTo(-45f).Within(0.5f));
        }

        [Test]
        public void StandingAndExtremeSpeedsStayInBounds()
        {
            LocomotionGait.Resolve(new Vector3(0.05f, 0f, 0f), Vector3.forward, Stride, 1f, out float rate, out float turn);
            Assert.That(rate, Is.EqualTo(1f));
            Assert.That(turn, Is.EqualTo(0f));
            LocomotionGait.Resolve(new Vector3(0f, 0f, 20f), Vector3.forward, Stride, 1f, out rate, out _);
            Assert.That(rate, Is.EqualTo(LocomotionGait.MaxRate));
            LocomotionGait.Resolve(new Vector3(0f, 0f, 0.2f), Vector3.forward, Stride, 1f, out rate, out _);
            Assert.That(rate, Is.EqualTo(LocomotionGait.MinRate));
        }

        [Test]
        public void ALargerBodyTakesLongerStrides()
        {
            LocomotionGait.Resolve(new Vector3(0f, 0f, 3f), Vector3.forward, Stride, 1.25f, out float big, out _);
            LocomotionGait.Resolve(new Vector3(0f, 0f, 3f), Vector3.forward, Stride, 1f, out float normal, out _);
            Assert.That(big, Is.LessThan(normal), "an elite's longer legs need a slower loop for the same speed");
        }
    }
}
