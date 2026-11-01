using Emberdelve.Stats;
using NUnit.Framework;

namespace Emberdelve.Rules.Tests
{
    public sealed class DrawSpeedRulesTests
    {
        [TestCase(.85f, 1.12f, .7589285714f)]
        [TestCase(.85f, 1.1333333333f, .75f)]
        [TestCase(.3f, 1f, .3f)]
        [TestCase(.3f, 1.02f, .2941176471f)]
        [TestCase(.85f, .5f, 1.7f)] [TestCase(.85f, 2f, .425f)]
        [TestCase(.85f, 100f, .05f)] [TestCase(.01f, .5f, .05f)]
        public void LocalDrawDuration_UsesLiteralSavedDataAndPostScaleFloor(float authored, float rate, float expected)
        {
            Assert.That(ActionSpeedRules.DrawDuration(authored, rate), Is.EqualTo(expected).Within(.000001f));
        }
        [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(float.NegativeInfinity)]
        [TestCase(0f)] [TestCase(-1f)] [TestCase(float.Epsilon)]
        public void InvalidRateOrOverflow_UsesFiniteNeutralDuration(float rate)
        {
            Assert.That(ActionSpeedRules.DrawDuration(.85f, rate), Is.EqualTo(.85f));
        }
        [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(float.NegativeInfinity)]
        [TestCase(0f)] [TestCase(-1f)]
        public void InvalidAuthoredTime_HasSafeMinimum(float authored)
        {
            Assert.That(ActionSpeedRules.DrawDuration(authored, 1f), Is.EqualTo(.05f));
        }
        [Test]
        public void ExtremeFiniteFactors_AreBoundedWithoutChangingReloadOrMelee()
        {
            Assert.That(ActionSpeedRules.DrawDuration(float.MaxValue, .5f), Is.EqualTo(float.MaxValue));
            Assert.That(ActionSpeedRules.DrawDuration(.85f, float.MaxValue), Is.EqualTo(.05f));
            Assert.That(ActionSpeedRules.ReloadDuration(1.9f, 1.12f), Is.EqualTo(1.6964285714f).Within(.000001f));
            Assert.That(ActionSpeedRules.Resolve(.24f, .3f, .3f, .35f, .92f, 1f, .8f).RepeatInterval,
                Is.EqualTo(.84f).Within(.000001f));
        }
    }
}
