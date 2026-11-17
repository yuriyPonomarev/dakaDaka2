using System;
using Emberdelve.BladeDefense;
using NUnit.Framework;

namespace Emberdelve.Rules.Tests
{
    /// <summary>
    /// U3C pure rules. Inputs are built from the rule's own constants and from each other; every expectation is derived from those inputs
    /// (arc/2, reduction x break scale, base + impact x per-impact, regen x elapsed), never typed in as a result.
    /// </summary>
    public sealed class BladeDefenseRulesTests
    {
        private static GuardInput Baseline()
        {
            return new GuardInput
            {
                Raised = true,
                BlockReduction = 0.6f,
                ArcDegrees = 120f,
                BearingDegrees = 0f,
                HasShield = false,
                GuardHeight = BladeHeight.Mid,
                AttackHeight = BladeHeight.Mid,
                HeightTolerance = 1,
                ImpactPower = 10f,
                ImpactResist = 20f,
                BreakReductionScale = 0.3f,
                EnduranceEnabled = false,
                EnduranceCurrent = 0f,
                EnduranceCost = 0f
            };
        }

        [Test]
        public void FrontHit_InsideTheArcIsBlockedWithTheWholeHit()
        {
            GuardInput g = Baseline();
            foreach (float bearing in new[] { 0f, g.ArcDegrees * 0.25f, g.ArcDegrees * 0.5f })
            {
                g.BearingDegrees = bearing;
                GuardVerdict v = BladeGuardRules.Resolve(in g);
                Assert.That(v.Outcome, Is.EqualTo(GuardOutcome.Blocked), "bearing " + bearing);
                Assert.That(v.Reduction, Is.EqualTo(BladeGuardRules.HeldGuardReduction), "a holding guard takes the whole hit, whatever the authored reduction");
                Assert.That(v.Mitigated, Is.True);
                Assert.That(v.Broken, Is.False);
            }
        }

        [Test]
        public void BackHit_OutsideTheArcIgnoresTheGuardAndCostsNothing()
        {
            GuardInput g = Baseline();
            g.EnduranceEnabled = true;
            g.EnduranceCurrent = 100f;
            g.EnduranceCost = 25f;
            foreach (float bearing in new[] { g.ArcDegrees * 0.5f + 0.01f, 135f, 180f })
            {
                g.BearingDegrees = bearing;
                GuardVerdict v = BladeGuardRules.Resolve(in g);
                Assert.That(v.Outcome, Is.EqualTo(GuardOutcome.OutsideArc), "bearing " + bearing);
                Assert.That(v.Reduction, Is.EqualTo(0f));
                Assert.That(v.EnduranceSpent, Is.EqualTo(0f));
                Assert.That(v.Mitigated, Is.False);
            }
        }

        [Test]
        public void GuardNotRaised_NeverMitigates()
        {
            GuardInput g = Baseline();
            g.Raised = false;
            Assert.That(BladeGuardRules.Resolve(in g).Outcome, Is.EqualTo(GuardOutcome.NotGuarding));
        }

        [Test]
        public void NanBearingOrArc_IsOutsideTheArc()
        {
            Assert.That(BladeGuardRules.InsideArc(float.NaN, 120f), Is.False);
            Assert.That(BladeGuardRules.InsideArc(0f, float.NaN), Is.False);
        }

        [Test]
        public void WrongHeight_AnOverheadPastALoweredBladeGuardGoesThroughAndCostsNothing()
        {
            GuardInput g = Baseline();
            g.EnduranceEnabled = true;
            g.EnduranceCurrent = 100f;
            g.EnduranceCost = 25f;
            g.HeightTolerance = 1;
            g.GuardHeight = BladeHeight.Low;
            g.AttackHeight = BladeHeight.High;
            GuardVerdict v = BladeGuardRules.Resolve(in g);
            Assert.That(v.Outcome, Is.EqualTo(GuardOutcome.WrongHeight));
            Assert.That(v.Reduction, Is.EqualTo(0f));
            Assert.That(v.EnduranceSpent, Is.EqualTo(0f));
        }

        [Test]
        public void Height_MidGuardCoversEveryBandAndTheToleranceDecidesTheRest()
        {
            foreach (BladeHeight attack in new[] { BladeHeight.Low, BladeHeight.Mid, BladeHeight.High })
            {
                Assert.That(BladeGuardRules.HeightCovered(BladeHeight.Mid, attack, 1), Is.True, "mid vs " + attack);
            }

            foreach (BladeHeight guard in new[] { BladeHeight.Low, BladeHeight.Mid, BladeHeight.High })
            {
                foreach (BladeHeight attack in new[] { BladeHeight.Low, BladeHeight.Mid, BladeHeight.High })
                {
                    for (int tolerance = 0; tolerance <= 2; tolerance++)
                    {
                        bool expected = Math.Abs((int)guard - (int)attack) <= tolerance;
                        Assert.That(BladeGuardRules.HeightCovered(guard, attack, tolerance), Is.EqualTo(expected), guard + " vs " + attack + " tol " + tolerance);
                    }
                }
            }

            // the widest tolerance (a shield) covers the largest distance between two bands
            int span = (int)BladeHeight.High - (int)BladeHeight.Low;
            Assert.That(BladeGuardRules.HeightCovered(BladeHeight.Low, BladeHeight.High, span), Is.True);
            Assert.That(BladeGuardRules.HeightCovered(BladeHeight.Low, BladeHeight.High, span - 1), Is.False);
        }

        [Test]
        public void Height_UnspecifiedIsAlwaysCoveredAndNegativeToleranceMeansSameBand()
        {
            Assert.That(BladeGuardRules.HeightCovered(BladeHeight.Low, BladeHeight.Unspecified, 0), Is.True);
            Assert.That(BladeGuardRules.HeightCovered(BladeHeight.Unspecified, BladeHeight.High, 0), Is.True);
            Assert.That(BladeGuardRules.HeightCovered(BladeHeight.Mid, BladeHeight.High, -5), Is.False);
            Assert.That(BladeGuardRules.HeightCovered(BladeHeight.High, BladeHeight.High, -5), Is.True);
        }

        [Test]
        public void GuardHeight_FollowsTheViewPitchThresholds()
        {
            float high = BladeGuardRules.DefaultHighPitchDegrees;
            float low = BladeGuardRules.DefaultLowPitchDegrees;
            Assert.That(BladeGuardRules.GuardHeightFromPitch(-high, high, low), Is.EqualTo(BladeHeight.High));
            Assert.That(BladeGuardRules.GuardHeightFromPitch(-high + 0.01f, high, low), Is.EqualTo(BladeHeight.Mid));
            Assert.That(BladeGuardRules.GuardHeightFromPitch(0f, high, low), Is.EqualTo(BladeHeight.Mid));
            Assert.That(BladeGuardRules.GuardHeightFromPitch(low - 0.01f, high, low), Is.EqualTo(BladeHeight.Mid));
            Assert.That(BladeGuardRules.GuardHeightFromPitch(low, high, low), Is.EqualTo(BladeHeight.Low));
            Assert.That(BladeGuardRules.GuardHeightFromPitch(float.NaN, high, low), Is.EqualTo(BladeHeight.Mid));
            // a broken threshold falls back to the default instead of locking the guard in one band
            Assert.That(BladeGuardRules.GuardHeightFromPitch(-high, 0f, float.NaN), Is.EqualTo(BladeHeight.High));
            Assert.That(BladeGuardRules.GuardHeightFromPitch(low, -3f, 0f), Is.EqualTo(BladeHeight.Low));
        }

        [Test]
        public void HeightTable_ReturnsTheAuthoredEntryPerTrajectory()
        {
            BladeHeight h = BladeHeight.Low, o = BladeHeight.High, t = BladeHeight.Mid;
            Assert.That(BladeGuardRules.HeightOf(BladeTrajectory.Horizontal, h, o, t), Is.EqualTo(h));
            Assert.That(BladeGuardRules.HeightOf(BladeTrajectory.Overhead, h, o, t), Is.EqualTo(o));
            Assert.That(BladeGuardRules.HeightOf(BladeTrajectory.Thrust, h, o, t), Is.EqualTo(t));
            Assert.That(BladeGuardRules.HeightOf(BladeTrajectory.None, h, o, t), Is.EqualTo(BladeHeight.Unspecified));
        }

        [Test]
        public void EmptyPool_CannotBlockAndTheHitGoesThroughUnpaid()
        {
            GuardInput g = Baseline();
            g.EnduranceEnabled = true;
            g.EnduranceCost = 12f;

            g.EnduranceCurrent = 0f;
            GuardVerdict empty = BladeGuardRules.Resolve(in g);
            Assert.That(empty.Outcome, Is.EqualTo(GuardOutcome.Exhausted));
            Assert.That(empty.Reduction, Is.EqualTo(0f));
            Assert.That(empty.EnduranceSpent, Is.EqualTo(0f));

            g.EnduranceCurrent = g.EnduranceCost - 0.01f;
            Assert.That(BladeGuardRules.Resolve(in g).Outcome, Is.EqualTo(GuardOutcome.Exhausted), "cannot cover the cost");

            g.EnduranceCurrent = float.NaN;
            Assert.That(BladeGuardRules.Resolve(in g).Outcome, Is.EqualTo(GuardOutcome.Exhausted), "NaN pool is empty");
        }

        [Test]
        public void Pool_ThatCoversTheCostBlocksAndReportsExactlyThatCost()
        {
            GuardInput g = Baseline();
            g.EnduranceEnabled = true;
            g.EnduranceCost = 12f;
            g.EnduranceCurrent = g.EnduranceCost;
            GuardVerdict v = BladeGuardRules.Resolve(in g);
            Assert.That(v.Outcome, Is.EqualTo(GuardOutcome.Blocked));
            Assert.That(v.EnduranceSpent, Is.EqualTo(g.EnduranceCost));
            Assert.That(v.Reduction, Is.EqualTo(BladeGuardRules.HeldGuardReduction), "a holding guard takes the whole hit, whatever the authored reduction");
        }

        [Test]
        public void DisabledEndurance_BlocksFromAnEmptyPoolForFree()
        {
            GuardInput g = Baseline();
            g.EnduranceEnabled = false;
            g.EnduranceCurrent = 0f;
            g.EnduranceCost = 999f;
            GuardVerdict v = BladeGuardRules.Resolve(in g);
            Assert.That(v.Outcome, Is.EqualTo(GuardOutcome.Blocked));
            Assert.That(v.EnduranceSpent, Is.EqualTo(0f));
        }

        [Test]
        public void ImpactPower_NeverBreaksTheGuard_TheHeldGuardTakesTheWholeHitAndPaysOnce()
        {
            GuardInput g = Baseline();
            g.EnduranceEnabled = true;
            g.EnduranceCurrent = 100f;
            g.EnduranceCost = 7f;
            foreach (float power in new[] { 0f, g.ImpactResist - 0.01f, g.ImpactResist, g.ImpactResist * 10f, 9999f })
            {
                g.ImpactPower = power;
                GuardVerdict v = BladeGuardRules.Resolve(in g);
                Assert.That(v.Outcome, Is.EqualTo(GuardOutcome.Blocked), "impact " + power);
                Assert.That(v.Broken, Is.False);
                Assert.That(v.Reduction, Is.EqualTo(BladeGuardRules.HeldGuardReduction));
                Assert.That(v.EnduranceSpent, Is.EqualTo(g.EnduranceCost));
                Assert.That(BladeGuardRules.IsGuardBroken(power, g.ImpactResist), Is.False);
            }
        }

        [Test]
        public void BlockCost_IsBasePlusImpactTerm_AndBadPartsCountAsZero()
        {
            float power = 30f, baseCost = 10f, perImpact = 0.5f;
            Assert.That(BladeGuardRules.BlockCost(power, baseCost, perImpact), Is.EqualTo(baseCost + power * perImpact).Within(1e-5f));
            Assert.That(BladeGuardRules.BlockCost(float.NaN, baseCost, perImpact), Is.EqualTo(baseCost).Within(1e-6f));
            Assert.That(BladeGuardRules.BlockCost(power, -4f, perImpact), Is.EqualTo(power * perImpact).Within(1e-5f));
            Assert.That(BladeGuardRules.BlockCost(power, baseCost, float.PositiveInfinity), Is.EqualTo(baseCost).Within(1e-6f));
        }

        [Test]
        public void Shield_NeverParries_AndAParryCapableBladeStillCan()
        {
            Assert.That(BladeGuardRules.CanParry(true, true), Is.False);
            Assert.That(BladeGuardRules.CanParry(true, false), Is.False);
            Assert.That(BladeGuardRules.CanParry(false, true), Is.True);
            Assert.That(BladeGuardRules.CanParry(false, false), Is.False);
        }

        [Test]
        public void ShieldAndBlade_ShareTheOutcomesAndDifferOnlyByTheHeightTolerance()
        {
            GuardInput blade = Baseline();
            blade.GuardHeight = BladeHeight.Low;
            blade.AttackHeight = BladeHeight.High;
            blade.HeightTolerance = 1;
            GuardInput shield = blade;
            shield.HasShield = true;
            shield.HeightTolerance = (int)BladeHeight.High - (int)BladeHeight.Low;

            Assert.That(BladeGuardRules.Resolve(in blade).Outcome, Is.EqualTo(GuardOutcome.WrongHeight));
            Assert.That(BladeGuardRules.Resolve(in shield).Outcome, Is.EqualTo(GuardOutcome.Blocked));

            shield.BearingDegrees = 180f;
            Assert.That(BladeGuardRules.Resolve(in shield).Outcome, Is.EqualTo(GuardOutcome.OutsideArc), "a shield does not guard the back either");
        }

        [Test]
        public void Verdict_ExposesNoParryOrRiposteMember()
        {
            foreach (var member in typeof(GuardVerdict).GetMembers())
            {
                Assert.That(member.Name.IndexOf("parry", StringComparison.OrdinalIgnoreCase), Is.LessThan(0), member.Name);
                Assert.That(member.Name.IndexOf("riposte", StringComparison.OrdinalIgnoreCase), Is.LessThan(0), member.Name);
            }
        }

        [Test]
        public void ClassifyPath_SeparatesThrustOverheadAndHorizontal()
        {
            Assert.That(BladeGuardRules.ClassifyPath(110f, 0f, 0.4f), Is.EqualTo(BladeTrajectory.Horizontal));
            Assert.That(BladeGuardRules.ClassifyPath(0f, 115f, 0.9f), Is.EqualTo(BladeTrajectory.Overhead));
            Assert.That(BladeGuardRules.ClassifyPath(0f, 0f, 0.78f), Is.EqualTo(BladeTrajectory.Thrust));
            Assert.That(BladeGuardRules.ClassifyPath(0f, 0f, 0f), Is.EqualTo(BladeTrajectory.Horizontal), "a path that goes nowhere is not a thrust");
            Assert.That(BladeGuardRules.ClassifyPath(float.NaN, float.NaN, float.NaN), Is.EqualTo(BladeTrajectory.Horizontal));
        }

        // ---- endurance pool ----------------------------------------------------------------------------

        [Test]
        public void Pool_StartsFullSpendsAndRegeneratesAfterTheDelay()
        {
            GuardEnduranceState pool = new GuardEnduranceState();
            float max = 100f, regen = 20f, delay = 1.5f, cost = 30f, t0 = 50f;

            pool.Refresh(t0, max, regen, delay);
            Assert.That(pool.Current, Is.EqualTo(max));
            Assert.That(pool.Spend(cost, t0), Is.True);
            Assert.That(pool.Current, Is.EqualTo(max - cost).Within(1e-5f));

            pool.Refresh(t0 + delay * 0.5f, max, regen, delay);
            Assert.That(pool.Current, Is.EqualTo(max - cost).Within(1e-5f), "no regeneration inside the delay");

            float extra = 0.75f;
            pool.Refresh(t0 + delay + extra, max, regen, delay);
            Assert.That(pool.Current, Is.EqualTo(max - cost + extra * regen).Within(1e-4f));

            pool.Refresh(t0 + delay + 1000f, max, regen, delay);
            Assert.That(pool.Current, Is.EqualTo(max), "capped at the max");
        }

        [Test]
        public void Pool_RegenerationDoesNotDependOnTheStepSize()
        {
            float max = 100f, regen = 20f, delay = 1f, cost = 60f, t0 = 10f, horizon = 3.2f;
            GuardEnduranceState coarse = new GuardEnduranceState();
            GuardEnduranceState fine = new GuardEnduranceState();
            coarse.Refresh(t0, max, regen, delay);
            fine.Refresh(t0, max, regen, delay);
            coarse.Spend(cost, t0);
            fine.Spend(cost, t0);

            coarse.Refresh(t0 + horizon, max, regen, delay);
            int steps = 64;
            for (int i = 1; i <= steps; i++) fine.Refresh(t0 + horizon * i / steps, max, regen, delay);
            Assert.That(fine.Current, Is.EqualTo(coarse.Current).Within(1e-3f));
        }

        [Test]
        public void Pool_RefusesAnUnaffordableSpendWithoutChangingAndSpendsOnlyOnce()
        {
            GuardEnduranceState pool = new GuardEnduranceState();
            pool.Refresh(1f, 40f, 10f, 1f);
            float cost = 25f;
            Assert.That(pool.Spend(cost, 1f), Is.True);
            float after = pool.Current;
            Assert.That(pool.Spend(cost, 1f), Is.False, "the remainder cannot pay a second hit");
            Assert.That(pool.Current, Is.EqualTo(after));
            Assert.That(after, Is.EqualTo(40f - cost).Within(1e-5f));
            Assert.That(pool.Spend(0f, 1f), Is.True);
            Assert.That(pool.Current, Is.EqualTo(after));
        }

        [Test]
        public void Pool_DisabledHoldsNothingAndRestartsFullWhenEnabled()
        {
            GuardEnduranceState pool = new GuardEnduranceState();
            pool.Refresh(1f, 0f, 10f, 1f);
            Assert.That(pool.Started, Is.False);
            Assert.That(pool.Current, Is.EqualTo(0f));
            Assert.That(pool.Spend(5f, 1f), Is.False);

            pool.Refresh(2f, 60f, 10f, 1f);
            Assert.That(pool.Started, Is.True);
            Assert.That(pool.Current, Is.EqualTo(60f));

            pool.Refresh(float.NaN, 60f, 10f, 1f);
            Assert.That(pool.Current, Is.EqualTo(60f), "a bad clock changes nothing");
        }

        [Test]
        public void Pool_ClockGoingBackwardsNeitherRegeneratesNorDrains()
        {
            GuardEnduranceState pool = new GuardEnduranceState();
            pool.Refresh(100f, 50f, 10f, 0f);
            pool.Spend(20f, 100f);
            float after = pool.Current;
            pool.Refresh(90f, 50f, 10f, 0f);
            Assert.That(pool.Current, Is.EqualTo(after));
        }

        [Test]
        public void HeldGuardReduction_IsAllOfTheHit_ForAnyAuthoredReductionAndAnyImpact()
        {
            Assert.That(BladeGuardRules.HeldGuardReduction, Is.EqualTo(1f));
            GuardInput g = Baseline();
            foreach (float authored in new[] { 0.05f, g.BlockReduction, 0.95f })
            {
                g.BlockReduction = authored;
                g.ImpactPower = g.ImpactResist - 0.01f;
                Assert.That(BladeGuardRules.Resolve(in g).Reduction, Is.EqualTo(BladeGuardRules.HeldGuardReduction), "light, authored " + authored);
                g.ImpactPower = g.ImpactResist * 10f;
                Assert.That(BladeGuardRules.Resolve(in g).Reduction, Is.EqualTo(BladeGuardRules.HeldGuardReduction), "heavy, authored " + authored);
            }
        }

    }
}
