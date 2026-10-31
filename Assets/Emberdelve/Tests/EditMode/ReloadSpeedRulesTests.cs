using Emberdelve.Stats;
using NUnit.Framework;

namespace Emberdelve.Rules.Tests
{
    public sealed class ReloadSpeedRulesTests
    {
        [TestCase(2.2f, 1f, 2.2f)]
        [TestCase(2.2f, 1.02f, 2.1568627451f)]
        [TestCase(1.9f, 1.12f, 1.6964285714f)]
        [TestCase(1.9f, 1.1333333333f, 1.6764705882f)]
        [TestCase(1.9f, .5f, 3.8f)]
        [TestCase(1.9f, 2f, .95f)]
        [TestCase(1.9f, 100f, .1f)]
        [TestCase(.01f, .5f, .1f)]
        [TestCase(.01f, 2f, .1f)]
        public void ReloadDuration_UsesLiteralSavedDataAndPostScaleFloor(float authored, float rate, float expected)
        {
            Assert.That(ActionSpeedRules.ReloadDuration(authored, rate), Is.EqualTo(expected).Within(.000001f));
        }
        [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(float.NegativeInfinity)]
        [TestCase(0f)] [TestCase(-1f)] [TestCase(float.Epsilon)]
        public void InvalidRateOrOverflowingDivision_UsesNeutralDuration(float rate)
        {
            Assert.That(ActionSpeedRules.ReloadDuration(1.9f, rate), Is.EqualTo(1.9f));
        }
        [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(float.NegativeInfinity)]
        [TestCase(-1f)] [TestCase(0f)]
        public void InvalidAuthoredDuration_HasFiniteMinimum(float authored)
        {
            Assert.That(ActionSpeedRules.ReloadDuration(authored, 1f), Is.EqualTo(.1f));
        }
        [Test]
        public void FiniteExtremes_DoNotBecomeInfiniteOrChangeMeleeMath()
        {
            Assert.That(ActionSpeedRules.ReloadDuration(float.MaxValue, .5f), Is.EqualTo(float.MaxValue));
            Assert.That(ActionSpeedRules.ReloadDuration(1.9f, float.MaxValue), Is.EqualTo(.1f));
            Assert.That(ActionSpeedRules.Resolve(.24f, .3f, .3f, .35f, .92f, 1f, .8f).RepeatInterval,
                Is.EqualTo(.84f).Within(.000001f));
        }
    }
}
