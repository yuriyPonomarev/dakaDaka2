using System;
using System.Collections.Generic;
using Emberdelve.Stats;
using NUnit.Framework;

namespace Emberdelve.Rules.Tests
{
    public sealed class PiecewiseLinearCurveTests
    {
        [TestCase(-100f, .875f)] [TestCase(1f, .875f)]
        [TestCase(6f, .950f)] [TestCase(10f, 1f)]
        [TestCase(16f, 1.075f)] [TestCase(22f, 1.135f)]
        [TestCase(30f, 1.175f)] [TestCase(100f, 1.175f)]
        [TestCase(3.5f, .9125f)] [TestCase(8f, .975f)]
        [TestCase(11f, 1.0125f)] [TestCase(12f, 1.025f)]
        [TestCase(14f, 1.05f)] [TestCase(17f, 1.085f)]
        [TestCase(18f, 1.095f)] [TestCase(26f, 1.155f)]
        public void DefaultMovement_LiteralKnotsAndLinearInteriors(float score, float expected)
        {
            Assert.That(MovementCurveDefaults.Curve.Evaluate(score), Is.EqualTo(expected).Within(.000001f));
        }

        [Test]
        public void NonFiniteInputs_HaveExplicitSafeEndpointBehavior()
        {
            Assert.That(MovementCurveDefaults.Curve.Evaluate(float.NaN), Is.EqualTo(.875f));
            Assert.That(MovementCurveDefaults.Curve.Evaluate(float.NegativeInfinity), Is.EqualTo(.875f));
            Assert.That(MovementCurveDefaults.Curve.Evaluate(float.PositiveInfinity), Is.EqualTo(1.175f));
        }

        [Test]
        public void InputAndReturnedPoints_DoNotMutateCapturedCurve()
        {
            var points = new List<CurvePoint> { new CurvePoint(1f, 2f), new CurvePoint(3f, 6f) };
            var curve = new PiecewiseLinearCurve(points);
            points[0] = new CurvePoint(1f, 99f);
            points.Clear();
            CurvePoint copy = curve.GetPoint(0);
            copy.value = -1f;
            Assert.That(curve.Count, Is.EqualTo(2));
            Assert.That(curve.Evaluate(2f), Is.EqualTo(4f));
            CurvePoint[] defaults = MovementCurveDefaults.CreatePoints();
            defaults[2].value = 999f;
            Assert.That(MovementCurveDefaults.Curve.Evaluate(10f), Is.EqualTo(1f));
            Assert.That(MovementCurveDefaults.CreatePoints()[2].value, Is.EqualTo(1f));
        }

        [Test]
        public void MissingAndShortLists_AreRefused()
        {
            Assert.Throws<ArgumentNullException>(() => new PiecewiseLinearCurve(null));
            Assert.Throws<ArgumentException>(() => new PiecewiseLinearCurve(new CurvePoint[0]));
            Assert.Throws<ArgumentException>(() => new PiecewiseLinearCurve(new[] { new CurvePoint(1, 1) }));
            Assert.That(PiecewiseLinearCurve.TryCreate(null, out var invalid, out string error), Is.False);
            Assert.That(invalid, Is.Null);
            Assert.That(error, Is.Not.Empty);
        }

        [TestCase(1f)] [TestCase(0f)] [TestCase(-1f)]
        public void DuplicateOrDecreasingX_IsRefused(float secondX)
        {
            Assert.Throws<ArgumentException>(() => new PiecewiseLinearCurve(
                new[] { new CurvePoint(1f, 1f), new CurvePoint(secondX, 2f) }));
        }

        [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(float.NegativeInfinity)]
        public void NonFiniteAuthoring_IsRefused(float invalid)
        {
            Assert.Throws<ArgumentException>(() => new PiecewiseLinearCurve(
                new[] { new CurvePoint(1f, invalid), new CurvePoint(2f, 2f) }));
            Assert.Throws<ArgumentException>(() => new PiecewiseLinearCurve(
                new[] { new CurvePoint(1f, 1f), new CurvePoint(invalid, 2f) }));
        }

        [Test]
        public void ExtremeFinitePoints_InterpolateWithoutOverflow()
        {
            var curve = new PiecewiseLinearCurve(new[] {
                new CurvePoint(-float.MaxValue, -float.MaxValue), new CurvePoint(float.MaxValue, float.MaxValue) });
            Assert.That(curve.Evaluate(0f), Is.EqualTo(0f));
        }

        [TestCase(0, 1f, 0f, 3.7625f)]
        [TestCase(31, 1f, 0f, 5.0525f)]
        [TestCase(10, 1f, 0f, 4.3f)]
        [TestCase(11, 1.18f, 0f, 5.137425f)]
        [TestCase(16, 1.15f, 0f, 5.315875f)]
        [TestCase(17, 1.357f, 0f, 6.3310835f)]
        [TestCase(18, 1.38f, 1f, 7.49773f)]
        [TestCase(11, 1.18f, .1f, 5.237425f)]
        [TestCase(11, 3f, 0f, 8f)]
        [TestCase(11, .1f, 0f, 2f)]
        public void ScoreWalking_UsesLiteralOrderAndClamps(int dex, float multiplier, float flat, float expected)
        {
            Assert.That(AttributeRules.MoveSpeedFromScore(4.3f, dex, MovementCurveDefaults.Curve, multiplier, flat),
                Is.EqualTo(expected).Within(.000002f));
        }

        [Test]
        public void LegacyIntegerApis_RetainModifierSemantics()
        {
            Assert.That(AttributeRules.MoveSpeed(4.3f, 0, 1.18f, 0f), Is.EqualTo(5.074f).Within(.000001f));
            Assert.That(AttributeRules.MoveSpeed(4.3f, 3), Is.EqualTo(4.6225f).Within(.000001f));
            Assert.That(AttributeRules.MoveSpeedFromScore(4.3f, 3, MovementCurveDefaults.Curve, 1f, 0f),
                Is.EqualTo(3.8915f).Within(.000001f));
        }

        [Test]
        public void CurveAwareDerivation_ChangesOnlyMovement()
        {
            var snapshot = new AttributeSnapshot(new AttributeScores(16, 11, 14, 8, 10, 10));
            var bases = new DerivedStatBases(100, 75, 3f, 4.3f);
            DerivedStats result = AttributeRules.Derive(snapshot, bases, MovementCurveDefaults.Curve);
            Assert.That(result.MaxHealth, Is.EqualTo(120));
            Assert.That(result.MaxMana, Is.EqualTo(70));
            Assert.That(result.ManaRegen, Is.EqualTo(3f));
            Assert.That(result.Modifier(SixAttribute.Dexterity), Is.EqualTo(0));
            Assert.That(result.MoveSpeed, Is.EqualTo(4.35375f).Within(.000001f));
        }
    }
}
