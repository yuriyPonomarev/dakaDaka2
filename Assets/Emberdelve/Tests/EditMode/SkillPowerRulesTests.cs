using Emberdelve.Stats;
using NUnit.Framework;

namespace Emberdelve.Rules.Tests
{
    public sealed class SkillPowerRulesTests
    {
        [TestCase(1f, .70f)] [TestCase(6f, .90f)] [TestCase(10f, 1f)]
        [TestCase(16f, 1.12f)] [TestCase(22f, 1.24f)] [TestCase(30f, 1.40f)]
        [TestCase(3.5f, .80f)] [TestCase(8f, .95f)] [TestCase(13f, 1.06f)]
        [TestCase(14f, 1.08f)] [TestCase(17f, 1.14f)] [TestCase(26f, 1.32f)]
        [TestCase(-20f, .70f)] [TestCase(60f, 1.40f)]
        public void IndependentKnotsInteriorsAndHeldEndpoints(float wisdom, float expected)
        {
            Assert.That(SkillPowerCurveDefaults.Curve.Evaluate(wisdom), Is.EqualTo(expected).Within(.000001f));
        }
        [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(float.NegativeInfinity)]
        [TestCase(0f)] [TestCase(-1f)]
        public void InvalidPowerIsNeutral(float factor)
        {
            Assert.That(SkillPowerRules.SanitizeFactor(factor), Is.EqualTo(1f));
        }
        [TestCase(.01f, .6f)] [TestCase(.6f, .6f)] [TestCase(.95f, .95f)]
        [TestCase(1f, 1f)] [TestCase(1.08f, 1.08f)] [TestCase(1.4f, 1.4f)]
        [TestCase(9f, 1.4f)] [TestCase(float.MaxValue, 1.4f)]
        public void MagnitudeUsesExistingEffectBand(float factor, float expected)
        {
            Assert.That(SkillPowerRules.SanitizeFactor(factor), Is.EqualTo(expected));
        }
        [Test] public void AuthoringArraysDoNotMutateCapturedDefaults()
        {
            var points = SkillPowerCurveDefaults.CreatePoints();
            var captured = new PiecewiseLinearCurve(points);
            points[2].value = 99f;
            Assert.That(captured.Evaluate(10f), Is.EqualTo(1f));
            Assert.That(SkillPowerCurveDefaults.Curve.Evaluate(10f), Is.EqualTo(1f));
            Assert.That(SkillPowerCurveDefaults.CreatePoints()[2].value, Is.EqualTo(1f));
        }
    }
}
