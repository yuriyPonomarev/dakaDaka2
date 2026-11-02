using Emberdelve.Stats;
using NUnit.Framework;
namespace Emberdelve.Rules.Tests
{
    public sealed class SkillCooldownRulesTests
    {
        [TestCase(1,1.20f)] [TestCase(6,1.06f)] [TestCase(10,1f)]
        [TestCase(16,.88f)] [TestCase(22,.70f)] [TestCase(30,.40f)]
        [TestCase(8,1.03f)] [TestCase(12,.96f)] [TestCase(13,.94f)]
        [TestCase(-5,1.20f)] [TestCase(100,.40f)]
        public void IndependentCooldownKnotsAndInteriors(float score,float expected)
        { Assert.That(SkillCooldownCurveDefaults.Curve.Evaluate(score),Is.EqualTo(expected).Within(.000001f)); }
        [TestCase(14,1,14)] [TestCase(7,1,7)]
        [TestCase(9,1.03f,9.27f)] [TestCase(11,1.03f,11.33f)]
        [TestCase(14,.96f,13.44f)] [TestCase(7,.96f,6.72f)]
        [TestCase(14,.4f,5.6f)] [TestCase(14,.01f,5.6f)]
        public void IndependentSavedSkillAndGearAnchors(float authored,float factor,float expected)
        { Assert.That(SkillCooldownRules.Duration(authored,factor),Is.EqualTo(expected).Within(.000002f)); }
        [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(float.NegativeInfinity)]
        [TestCase(0f)] [TestCase(-1f)]
        public void InvalidMultiplierUsesNeutral(float factor)
        { Assert.That(SkillCooldownRules.Duration(14,factor),Is.EqualTo(14)); }
        [Test] public void CapOverflowAndZeroMetadataAreExplicit()
        {
            Assert.That(SkillCooldownRules.Duration(14,float.Epsilon),Is.EqualTo(5.6f));
            Assert.That(SkillCooldownRules.Duration(float.MaxValue,2),Is.EqualTo(float.MaxValue));
            Assert.That(SkillCooldownRules.Duration(0,1),Is.Zero);
            Assert.That(SkillCooldownRules.Duration(-5,.5f),Is.Zero);
            Assert.That(SkillCooldownRules.Duration(float.NaN,1),Is.Zero);
            Assert.That(SkillCooldownRules.Duration(float.PositiveInfinity,1),Is.Zero);
            Assert.That(SkillCooldownRules.Duration(14,2),Is.EqualTo(28));
        }
        [Test] public void DefaultArrayCannotMutateCapturedCurve()
        {
            var points=SkillCooldownCurveDefaults.CreatePoints();points[2].value=.4f;
            Assert.That(SkillCooldownCurveDefaults.Curve.Evaluate(10),Is.EqualTo(1));
            Assert.That(SkillCooldownCurveDefaults.CreatePoints()[2].value,Is.EqualTo(1));
        }
    }
}
