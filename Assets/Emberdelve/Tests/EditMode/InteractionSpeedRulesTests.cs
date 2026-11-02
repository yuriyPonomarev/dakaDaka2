using Emberdelve.Stats;
using NUnit.Framework;

namespace Emberdelve.Rules.Tests
{
    public sealed class InteractionSpeedRulesTests
    {
        [TestCase(1f, .75f)] [TestCase(6f, .9f)] [TestCase(10f, 1f)]
        [TestCase(16f, 1.12f)] [TestCase(22f, 1.2f)] [TestCase(30f, 1.28f)]
        [TestCase(8f, .95f)] [TestCase(12f, 1.04f)] [TestCase(19f, 1.16f)]
        [TestCase(-10f, .75f)] [TestCase(100f, 1.28f)]
        public void CharismaOpeningCurve_HasIndependentLiteralAnchors(float score, float expected)
        {
            Assert.That(InteractionCurveDefaults.Curve.Evaluate(score), Is.EqualTo(expected).Within(.000001f));
        }

        [TestCase(1.4f, 1f, 1.4f)]
        [TestCase(2.2f, 1f, 2.2f)]
        [TestCase(1.4f, .95f, 1.4736842105f)]
        [TestCase(2.2f, .95f, 2.3157894737f)]
        [TestCase(1.4f, 1.04f, 1.3461538462f)]
        [TestCase(2.2f, 1.04f, 2.1153846154f)]
        [TestCase(1.4f, 1.12f, 1.25f)]
        [TestCase(2.2f, 1.28f, 1.71875f)]
        public void ChestDuration_UsesSavedVariantTimesAndLiteralRates(float authored, float rate, float expected)
        {
            Assert.That(InteractionSpeedRules.Duration(authored, rate), Is.EqualTo(expected).Within(.000001f));
        }

        [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)] [TestCase(0f)] [TestCase(-2f)]
        public void InvalidActorRate_UsesNeutralTiming(float rate)
        {
            Assert.That(InteractionSpeedRules.Duration(1.4f, rate), Is.EqualTo(1.4f));
        }

        [Test]
        public void InstantSupply_RemainsInstantAndPositiveHoldsKeepTheirFloor()
        {
            Assert.That(InteractionSpeedRules.Duration(0f, .5f), Is.EqualTo(0f));
            Assert.That(InteractionSpeedRules.Duration(0f, float.NaN), Is.EqualTo(0f));
            Assert.That(InteractionSpeedRules.Duration(-1f, 2f), Is.EqualTo(0f));
            Assert.That(InteractionSpeedRules.Duration(1.4f, 100f), Is.EqualTo(.1f));
            Assert.That(InteractionSpeedRules.Duration(1.4f, float.MaxValue), Is.EqualTo(.1f));
        }

        [Test]
        public void NonFiniteArithmetic_CannotProduceAnInvalidHoldTimer()
        {
            Assert.That(InteractionSpeedRules.Duration(1.4f, float.Epsilon), Is.EqualTo(1.4f));
            Assert.That(InteractionSpeedRules.Duration(float.MaxValue, .5f), Is.EqualTo(float.MaxValue));
            Assert.That(InteractionSpeedRules.Duration(float.NaN, 1f), Is.EqualTo(.1f));
            Assert.That(InteractionSpeedRules.Duration(float.PositiveInfinity, 1f), Is.EqualTo(.1f));
        }

        [Test]
        public void DefaultPoints_DoNotExposeTheCapturedTableOrOtherStatCurves()
        {
            CurvePoint[] points = InteractionCurveDefaults.CreatePoints();
            points[2].value = 99f;
            Assert.That(InteractionCurveDefaults.Curve.Evaluate(10f), Is.EqualTo(1f));
            Assert.That(InteractionCurveDefaults.CreatePoints()[2].value, Is.EqualTo(1f));
            Assert.That(MovementCurveDefaults.Curve.Evaluate(10f), Is.EqualTo(1f));
            Assert.That(ActionSpeedCurveDefaults.Curve.Evaluate(10f), Is.EqualTo(1f));
        }
    }
}