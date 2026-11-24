using Emberdelve.Combat;
using Emberdelve.Data;
using NUnit.Framework;

namespace Emberdelve.Combat.Tests
{
    /// <summary>
    /// Pure rules of the reference defence port (Docs/Tasks/DARKER_COMBAT_PORT_20261124.md): a guard intercepts only after its
    /// ready time, and a blocked hit puts a weak / mid / heavy reaction on the defender by impact power against impact resist.
    /// </summary>
    public sealed class GuardTimingRulesTests
    {
        [Test]
        public void GuardInterceptsOnlyAfterItsReadyTime()
        {
            Assert.IsFalse(CombatFlowRules.IsGuardReady(10f, 10f, 0.15f), "the press itself does not block yet");
            Assert.IsFalse(CombatFlowRules.IsGuardReady(10f, 10.14f, 0.15f), "0.14 s after the press is still early");
            Assert.IsTrue(CombatFlowRules.IsGuardReady(10f, 10.15f, 0.15f), "ready exactly at the ready time");
            Assert.IsTrue(CombatFlowRules.IsGuardReady(10f, 12f, 0.15f), "a held guard stays ready");
        }

        [Test]
        public void GuardWithNoReadyTimeBlocksAtOnce_AndBadClocksNeverBlock()
        {
            Assert.IsTrue(CombatFlowRules.IsGuardReady(10f, 10f, 0f));
            Assert.IsTrue(CombatFlowRules.IsGuardReady(10f, 10f, -1f), "a negative ready time is treated as none");
            Assert.IsFalse(CombatFlowRules.IsGuardReady(float.NaN, 10f, 0.15f));
            Assert.IsFalse(CombatFlowRules.IsGuardReady(10f, float.NaN, 0.15f));
            Assert.IsTrue(CombatFlowRules.IsGuardReady(float.NegativeInfinity, 0f, 0.15f), "a guard that was never re-pressed counts as long held");
        }

        [Test]
        public void BlockReactionGrowsWithTheStrengthOfTheBlockedHit()
        {
            var tuning = new CombatFlowTuning();
            float weak = CombatFlowRules.BlockReactionSeconds(10f, 15f, tuning);     // 0.67 of the resist
            float mid = CombatFlowRules.BlockReactionSeconds(12f, 15f, tuning);      // 0.80
            float heavy = CombatFlowRules.BlockReactionSeconds(30f, 15f, tuning);    // 2.00
            Assert.AreEqual(tuning.blockReactionWeakSeconds, weak, 1e-6f);
            Assert.AreEqual(tuning.blockReactionMidSeconds, mid, 1e-6f);
            Assert.AreEqual(tuning.blockReactionHeavySeconds, heavy, 1e-6f);
            Assert.Less(weak, mid);
            Assert.Less(mid, heavy);
        }

        [Test]
        public void BlockReactionThresholdsAreInclusive_AndReferenceDurationsAreAuthored()
        {
            var tuning = new CombatFlowTuning();
            Assert.AreEqual(0.45f, tuning.blockReactionWeakSeconds, 1e-6f);
            Assert.AreEqual(0.90f, tuning.blockReactionMidSeconds, 1e-6f);
            Assert.AreEqual(1.30f, tuning.blockReactionHeavySeconds, 1e-6f);
            Assert.AreEqual(0.15f, tuning.guardReadySeconds, 1e-6f);
            Assert.AreEqual(1.267f, tuning.deflectedSeconds, 1e-6f);
            Assert.AreEqual(1.133f, tuning.deflectedTwoHandedSeconds, 1e-6f);
            Assert.AreEqual(tuning.blockReactionMidSeconds, CombatFlowRules.BlockReactionSeconds(tuning.blockReactionMidRatio * 20f, 20f, tuning), 1e-6f);
            Assert.AreEqual(tuning.blockReactionHeavySeconds, CombatFlowRules.BlockReactionSeconds(tuning.blockReactionHeavyRatio * 20f, 20f, tuning), 1e-6f);
        }

        [Test]
        public void HitWithoutImpactPowerOrDefenderWithoutResistReactsWeakly()
        {
            var tuning = new CombatFlowTuning();
            Assert.AreEqual(tuning.blockReactionWeakSeconds, CombatFlowRules.BlockReactionSeconds(0f, 15f, tuning), 1e-6f, "an arrow carries no impact power");
            Assert.AreEqual(tuning.blockReactionWeakSeconds, CombatFlowRules.BlockReactionSeconds(12f, 0f, tuning), 1e-6f);
            Assert.AreEqual(tuning.blockReactionWeakSeconds, CombatFlowRules.BlockReactionSeconds(float.NaN, 15f, tuning), 1e-6f);
            Assert.AreEqual(0f, CombatFlowRules.BlockReactionSeconds(12f, 15f, null), 1e-6f, "no tuning, no reaction");
        }
    }
}
