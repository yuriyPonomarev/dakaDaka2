using Emberdelve.Stats;
using NUnit.Framework;

namespace Emberdelve.Rules.Tests
{
    public sealed class ActionSpeedRulesTests
    {
        [TestCase(-1f, .82f)] [TestCase(1f, .82f)] [TestCase(6f, .92f)]
        [TestCase(10f, 1f)] [TestCase(16f, 1.12f)] [TestCase(22f, 1.2f)]
        [TestCase(30f, 1.28f)] [TestCase(31f, 1.28f)]
        [TestCase(3.5f, .87f)] [TestCase(8f, .96f)] [TestCase(11f, 1.02f)]
        [TestCase(13f, 1.06f)] [TestCase(17f, 1.1333333333f)] [TestCase(26f, 1.24f)]
        public void ActionCurve_UsesLiteralOwnKnotsAndLinearInteriors(float dex, float expected)
        {
            Assert.That(ActionSpeedCurveDefaults.Curve.Evaluate(dex), Is.EqualTo(expected).Within(.000001f));
        }

        [Test]
        public void ActionCurve_IsIndependentAndDoesNotExposeMutableDefaults()
        {
            CurvePoint[] points = ActionSpeedCurveDefaults.CreatePoints();
            var curve = new PiecewiseLinearCurve(points);
            points[2].value = 99f;
            Assert.That(curve.Evaluate(10), Is.EqualTo(1f));
            Assert.That(ActionSpeedCurveDefaults.CreatePoints()[2].value, Is.EqualTo(1f));
            Assert.That(curve.Evaluate(16), Is.EqualTo(1.12f));
            Assert.That(MovementCurveDefaults.Curve.Evaluate(16), Is.EqualTo(1.075f));
            Assert.That(curve.Evaluate(float.NaN), Is.EqualTo(.82f));
        }

        [TestCase(1f, .24f, .30f, .30f, .35f, .84f)]
        [TestCase(1.02f, .2352941176f, .2941176471f, .2941176471f, .3431372549f, .8235294118f)]
        [TestCase(1.12f, .2142857143f, .2678571429f, .2678571429f, .3125f, .75f)]
        [TestCase(1.1333333333f, .2117647059f, .2647058824f, .2647058824f, .3088235294f, .7411764706f)]
        [TestCase(.5f, .48f, .60f, .60f, .70f, 1.68f)]
        [TestCase(2f, .12f, .15f, .15f, .175f, .42f)]
        public void SwordTiming_LiteralPhasesAndRepeatFloor(float rate, float windup, float active,
            float recovery, float recoil, float repeat)
        {
            MeleeActionTiming result = ActionSpeedRules.Resolve(.24f, .3f, .3f, .35f, .92f, rate, .8f);
            Assert.That(result.Windup, Is.EqualTo(windup).Within(.000001f));
            Assert.That(result.Active, Is.EqualTo(active).Within(.000001f));
            Assert.That(result.Recovery, Is.EqualTo(recovery).Within(.000001f));
            Assert.That(result.WallRecoil, Is.EqualTo(recoil).Within(.000001f));
            Assert.That(result.RepeatInterval, Is.EqualTo(repeat).Within(.000001f));
        }

        [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(float.NegativeInfinity)]
        [TestCase(0f)] [TestCase(-1f)] [TestCase(float.Epsilon)]
        public void InvalidRateOrOverflowingDurationMath_FallsBackToNeutral(float factor)
        {
            MeleeActionTiming result = ActionSpeedRules.Resolve(.24f, .3f, .3f, .35f, .92f, factor, .8f);
            Assert.That(result.Factor, Is.EqualTo(1f));
            Assert.That(result.Active, Is.EqualTo(.3f));
            Assert.That(result.RepeatInterval, Is.EqualTo(.84f).Within(.000001f));
        }

        [Test]
        public void CooldownAndActiveFloors_ApplyAfterScalingWithoutDoubleScaling()
        {
            MeleeActionTiming tiny = ActionSpeedRules.Resolve(.001f, .02f, .001f, .001f, .01f, 100f, .8f);
            Assert.That(tiny.Windup, Is.EqualTo(.00001f).Within(.0000001f));
            Assert.That(tiny.Active, Is.EqualTo(.02f));
            Assert.That(tiny.Recovery, Is.EqualTo(.00001f).Within(.0000001f));
            Assert.That(tiny.WallRecoil, Is.EqualTo(.00001f).Within(.0000001f));
            Assert.That(tiny.RepeatInterval, Is.EqualTo(.08f).Within(.000001f));
            MeleeActionTiming longCooldown = ActionSpeedRules.Resolve(.24f, .3f, .3f, .35f, 2f, 2f, .8f);
            Assert.That(longCooldown.Total, Is.EqualTo(.42f).Within(.000001f));
            Assert.That(longCooldown.RepeatInterval, Is.EqualTo(.8f).Within(.000001f));
            MeleeActionTiming huge = ActionSpeedRules.Resolve(.24f, .3f, .3f, .35f, .92f, float.MaxValue, .8f);
            Assert.That(huge.Factor, Is.EqualTo(float.MaxValue));
            Assert.That(huge.Active, Is.EqualTo(.02f));
            Assert.That(huge.RepeatInterval, Is.EqualTo(.08f).Within(.000001f));
        }
    }
}
