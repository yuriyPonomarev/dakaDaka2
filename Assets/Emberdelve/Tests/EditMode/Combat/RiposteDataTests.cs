using System.Collections.Generic;
using Emberdelve.BladeDefense;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.Combat.Tests
{
    /// <summary>
    /// U3C3 data and damage proof against the real weapon assets and the real DamageResolver: which weapons riposte comes from the authored
    /// flag, the strikes resolve to patterns the weapon owns, a riposte strike does the data multiplier of the same swing and carries the
    /// SAME impact power (so it breaks exactly the guards the plain swing breaks, and none more). Expectations are derived from the assets.
    /// </summary>
    public sealed class RiposteDataTests
    {
        private static readonly List<WeaponData> All = new List<WeaponData>();
        // U5B: the plain arming sword and the two-handed sword riposte too (their flags are asserted in SwordFamilyTests)
        private static readonly HashSet<string> SwordFamily = new HashSet<string> { "arming_sword", "two_handed_sword" };

        [OneTimeSetUp]
        public void LoadWeapons()
        {
            All.Clear();
            foreach (string guid in AssetDatabase.FindAssets("t:WeaponData"))
            {
                WeaponData w = AssetDatabase.LoadAssetAtPath<WeaponData>(AssetDatabase.GUIDToAssetPath(guid));
                if (w != null && w.weaponId != null && w.weaponId.IndexOf("test", System.StringComparison.OrdinalIgnoreCase) < 0) All.Add(w);
            }

            Assert.That(All.Count, Is.GreaterThan(3), "the shipped weapon assets are found");
        }

        private static IEnumerable<WeaponData> Riposters()
        {
            foreach (WeaponData w in All) if (w.canRiposte) yield return w;
        }

        private sealed class Dummy : ICombatEntity, IGuardBreakDefender
        {
            public int CombatId { get; set; } = 2;
            public EntityKind Kind => EntityKind.Enemy;
            public int TeamId => CombatTeams.MonsterTeam;
            public Vector3 Center = new Vector3(0f, 1.1f, 2f);
            public Vector3 Facing = Vector3.back;
            public bool Guarding;
            public float Resist;
            public float Reduction = 0.5f;

            public Vector3 CombatCenter => Center;
            public Collider HeadCollider => null;
            public bool IsDead => false;
            public float CurrentHealth => 1000f;
            public float MaxHealth => 1000f;
            public void ApplyDamage(DamageInfo damage) { }

            public bool TryResolveGuard(in DamageInfo incoming, CombatFlowTuning tuning, out GuardResult result)
            {
                Vector3 toSource = incoming.SourcePosition - Center;
                toSource.y = 0f;
                toSource.Normalize();
                GuardInput input = new GuardInput
                {
                    Raised = Guarding,
                    BlockReduction = Reduction,
                    ArcDegrees = 120f,
                    BearingDegrees = Vector3.Angle(Facing, toSource),
                    HasShield = false,
                    GuardHeight = BladeHeight.Mid,
                    AttackHeight = incoming.AttackHeight,
                    HeightTolerance = tuning.bladeHeightTolerance,
                    ImpactPower = incoming.ImpactPower,
                    ImpactResist = Resist,
                    BreakReductionScale = tuning.guardBreakReductionScale
                };
                GuardVerdict verdict = BladeGuardRules.Resolve(in input);
                result = verdict.Mitigated ? new GuardResult(verdict.Reduction, verdict.Broken) : default(GuardResult);
                return verdict.Mitigated;
            }
        }

        private static DamageInfo Swing(WeaponData weapon, SwingPatternData pattern, float stepMultiplier, ICombatEntity target)
        {
            Vector3 from = Vector3.zero;
            AttackIntent intent = new AttackIntent(1, EntityKind.LocalPlayer, weapon, 1f, from, Vector3.forward, pattern.EffectiveReach(weapon), 1, 0f);
            HitCandidate hit = new HitCandidate(target.CombatId, target.CombatCenter, false);
            return DamageResolver.ResolveSwing(weapon, pattern, intent, hit, HitLocation.Body, 1f, target, 1f, 0f, stepMultiplier, 1f, new CombatFlowTuning());
        }

        // ---- the data -----------------------------------------------------------------------------------

        [Test]
        public void TheDefaultsAreTheDesignedOnes_OneSecondWindow_OneAndAHalfMultiplier()
        {
            WeaponData fresh = ScriptableObject.CreateInstance<WeaponData>();
            try
            {
                Assert.That(fresh.canRiposte, Is.False, "a weapon never ripostes unless its data says so");
                Assert.That(fresh.riposteWindow, Is.EqualTo(1.0f));
                Assert.That(fresh.riposteDamageMultiplier, Is.EqualTo(1.5f));
            }
            finally { Object.DestroyImmediate(fresh); }
        }

        [Test]
        public void TheSwordsAreFlagged_AndNothingElseIs()
        {
            WeaponData sword = All.Find(w => w.weaponId == "ashen_short_sword");
            WeaponData rapier = All.Find(w => w.weaponId == "ember_rapier");
            Assert.That(sword, Is.Not.Null);
            Assert.That(rapier, Is.Not.Null);
            Assert.That(sword.CanRiposteWeapon && rapier.CanRiposteWeapon, Is.True, "the two swords riposte");

            foreach (WeaponData w in All)
            {
                if (w == sword || w == rapier || SwordFamily.Contains(w.weaponId)) continue;
                Assert.That(w.canRiposte, Is.False, w.weaponId + " is not a sword");
                Assert.That(w.CanRiposteWeapon, Is.False, w.weaponId);
            }
        }

        [Test]
        public void EveryRiposteWeapon_IsMeleeSwept_NotAShield_WithResolvablePatterns()
        {
            int checkedWeapons = 0;
            foreach (WeaponData w in Riposters())
            {
                checkedWeapons++;
                Assert.That(w.IsShield, Is.False, w.weaponId + " shield never parries");
                Assert.That(w.IsRanged, Is.False, w.weaponId);
                Assert.That(w.UsesSweptMelee, Is.True, w.weaponId);
                Assert.That(w.RiposteStrikeCount, Is.GreaterThanOrEqualTo(1), w.weaponId);
                Assert.That(w.riposteWindow, Is.GreaterThan(0f), w.weaponId);
                Assert.That(w.riposteDamageMultiplier, Is.GreaterThan(1f), w.weaponId + " a riposte hits harder");

                for (int i = 0; i < w.RiposteStrikeCount; i++)
                {
                    Assert.That(w.TryResolveRiposteStrike(i, out int index, out SwingPatternData pattern), Is.True, w.weaponId + " strike " + i);
                    Assert.That(System.Array.IndexOf(w.swingPatterns, pattern), Is.EqualTo(index), "the strike pattern is one the weapon owns");
                    Assert.That(pattern.Validate(out string problem), Is.True, problem);
                }

                Assert.That(w.TryResolveRiposteStrike(w.RiposteStrikeCount, out _, out _), Is.False, "no strike past the authored count");
                Assert.That(w.TryResolveRiposteStrike(-1, out _, out _), Is.False);
            }

            Assert.That(checkedWeapons, Is.GreaterThanOrEqualTo(2));
        }

        [Test]
        public void ASwordsAuthoredTwoStrikes_AreTwo()
        {
            WeaponData sword = All.Find(w => w.weaponId == "ashen_short_sword");
            Assert.That(sword.RiposteStrikeCount, Is.EqualTo(2), "the design: a riposte is a two-strike combo");
        }

        [Test]
        public void AShieldWithTheFlagForcedOn_StillCannotRiposte()
        {
            WeaponData forged = ScriptableObject.CreateInstance<WeaponData>();
            try
            {
                WeaponData sword = All.Find(w => w.weaponId == "ashen_short_sword");
                forged.swingPatterns = sword.swingPatterns;
                forged.canRiposte = true;
                forged.riposteSwingPatterns = new[] { 0, 1 };
                forged.defenseRole = DefenseRole.Shield;
                Assert.That(forged.IsShield, Is.True);
                Assert.That(forged.CanRiposteWeapon, Is.False);
                Assert.That(RiposteRules.KitCanRiposte(forged.canRiposte, forged.IsShield, false, forged.IsRanged, forged.RiposteStrikeCount), Is.False);
            }
            finally { Object.DestroyImmediate(forged); }
        }

        // ---- damage ---------------------------------------------------------------------------------------

        [Test]
        public void EachRiposteStrike_DealsTheDataMultiplierOfTheSameSwing()
        {
            foreach (WeaponData w in Riposters())
            {
                float mult = RiposteRules.StrikeMultiplier(w.riposteDamageMultiplier);
                for (int i = 0; i < w.RiposteStrikeCount; i++)
                {
                    w.TryResolveRiposteStrike(i, out _, out SwingPatternData pattern);
                    Dummy open = new Dummy { Guarding = false };
                    float plain = Swing(w, pattern, 1f, open).Amount;
                    float riposte = Swing(w, pattern, mult, open).Amount;
                    Assert.That(riposte, Is.EqualTo(plain * mult).Within(plain * mult * 1e-4f), w.weaponId + " strike " + i + ": x" + mult);
                    Assert.That(riposte, Is.GreaterThan(plain));
                }
            }
        }

        [Test]
        public void TheMultiplier_IsAppliedToTheStrikeNotTheImpact()
        {
            foreach (WeaponData w in Riposters())
            {
                float mult = RiposteRules.StrikeMultiplier(w.riposteDamageMultiplier);
                for (int i = 0; i < w.RiposteStrikeCount; i++)
                {
                    w.TryResolveRiposteStrike(i, out _, out SwingPatternData pattern);
                    Dummy open = new Dummy();
                    DamageInfo plain = Swing(w, pattern, 1f, open);
                    DamageInfo riposte = Swing(w, pattern, mult, open);
                    Assert.That(riposte.ImpactPower, Is.EqualTo(plain.ImpactPower), "impact power is the weapon's normal one");
                    Assert.That(riposte.ImpactPower, Is.EqualTo(w.impactPower * pattern.impactPowerScale).Within(1e-4f), "and it comes from the weapon data");
                }
            }
        }

        [Test]
        public void ARiposte_BreaksExactlyTheGuardsThePlainSwingBreaks_NeverMore()
        {
            WeaponData sword = All.Find(w => w.weaponId == "ashen_short_sword");
            int comparisons = 0;
            foreach (WeaponData riposter in Riposters())
            {
                float mult = RiposteRules.StrikeMultiplier(riposter.riposteDamageMultiplier);
                for (int i = 0; i < riposter.RiposteStrikeCount; i++)
                {
                    riposter.TryResolveRiposteStrike(i, out _, out SwingPatternData pattern);

                    // the guard of every authored weapon, against this strike, plain and as a riposte
                    foreach (WeaponData defender in All)
                    {
                        if (defender.impactResist <= 0f) continue;
                        Dummy guard = new Dummy { Guarding = true, Resist = defender.impactResist };
                        DamageInfo plain = Swing(riposter, pattern, 1f, guard);
                        DamageInfo riposte = Swing(riposter, pattern, mult, guard);
                        Assert.That(riposte.Blocked, Is.EqualTo(plain.Blocked), "block outcome vs " + defender.weaponId);
                        Assert.That(riposte.GuardBroken, Is.EqualTo(plain.GuardBroken), "guard-break outcome vs " + defender.weaponId);
                        comparisons++;
                    }
                }
            }

            // the shipped fact the design rests on: a sword guard holds against a sword riposte (resist above the strike's impact)
            Dummy swordGuard = new Dummy { Guarding = true, Resist = sword.impactResist };
            foreach (WeaponData riposter in Riposters())
            {
                if (riposter != sword) continue;
                float mult = RiposteRules.StrikeMultiplier(riposter.riposteDamageMultiplier);
                for (int i = 0; i < riposter.RiposteStrikeCount; i++)
                {
                    riposter.TryResolveRiposteStrike(i, out _, out SwingPatternData pattern);
                    DamageInfo riposte = Swing(riposter, pattern, mult, swordGuard);
                    Assert.That(riposte.Blocked && !riposte.GuardBroken, Is.True, "a sword riposte does not break a holding sword guard");
                }
            }

            Assert.That(comparisons, Is.GreaterThan(0));
        }

        [Test]
        public void ABlockedRiposteStrike_IntoAHoldingGuard_TakesZero_AndABrokenOneKeepsThePartial()
        {
            WeaponData sword = All.Find(w => w.weaponId == "ashen_short_sword");
            float mult = RiposteRules.StrikeMultiplier(sword.riposteDamageMultiplier);
            sword.TryResolveRiposteStrike(0, out _, out SwingPatternData pattern);
            Dummy open = new Dummy { Guarding = false };
            Dummy guard = new Dummy { Guarding = true, Resist = sword.impactResist, Reduction = 0.5f };
            DamageInfo unguarded = Swing(sword, pattern, mult, open);
            DamageInfo guarded = Swing(sword, pattern, mult, guard);
            Assert.That(guarded.Blocked, Is.True);
            Assert.That(guarded.GuardBroken, Is.False);
            Assert.That(guarded.BlockReduction, Is.EqualTo(BladeGuardRules.HeldGuardReduction), "a holding guard reports the whole hit");
            Assert.That(guarded.Amount, Is.EqualTo(0f), "FIX2: a held block, a riposte strike included, costs 0 HP");
            Assert.That(guarded.IsValid, Is.True, "a held block is still a valid, resolved hit");

            // a guard the strike breaks keeps only the authored reduction x the break scale (the 1.0 floor applies again)
            CombatFlowTuning tuning = new CombatFlowTuning();
            Dummy broken = new Dummy { Guarding = true, Resist = 0.01f, Reduction = 0.5f };
            DamageInfo breaking = Swing(sword, pattern, mult, broken);
            Assert.That(breaking.GuardBroken, Is.True);
            float expected = Mathf.Max(1f, unguarded.Amount * (1f - broken.Reduction * Mathf.Clamp01(tuning.guardBreakReductionScale)));
            Assert.That(breaking.Amount, Is.EqualTo(expected).Within(expected * 1e-4f), "a broken guard keeps the partial reduction");
        }

        [Test]
        public void OnlyTheOneHandedSwords_RipostesWithAnOffhandShield()
        {
            foreach (WeaponData w in All)
            {
                bool expected = w.weaponId == "ashen_short_sword" || w.weaponId == "arming_sword";
                Assert.That(w.riposteWithOffhandShield, Is.EqualTo(expected), w.weaponId);
                if (w.riposteWithOffhandShield) Assert.That(w.canRiposte, Is.True, "the shield allowance only means something on a riposting weapon");
            }

            WeaponData sword = All.Find(w => w.weaponId == "ashen_short_sword");
            Assert.That(RiposteRules.KitCanRiposte(sword.canRiposte, sword.IsShield, true, sword.IsRanged, sword.RiposteStrikeCount, sword.riposteWithOffhandShield), Is.True, "the warrior's sword and shield");
        }

        [Test]
        public void AHeldBlock_TakesZero_ForEveryAuthoredFloorBeforeIt_AndStaysValid()
        {
            DamageInfo hit = new DamageInfo(1f, Vector3.zero, Vector3.forward, Vector3.zero, 1, EntityKind.Enemy, DamageType.Physical, false, false, 0f);
            foreach (float amount in new[] { 1f, 3f, 26f, 500f })
            {
                DamageInfo h = new DamageInfo(amount, Vector3.zero, Vector3.forward, Vector3.zero, 1, EntityKind.Enemy, DamageType.Physical, false, false, 0f);
                DamageInfo held = h.WithReduction(BladeGuardRules.HeldGuardReduction, out bool blocked);
                Assert.That(blocked, Is.True);
                Assert.That(held.Amount, Is.EqualTo(0f), "held, amount " + amount);
                Assert.That(held.IsValid, Is.True);

                DamageInfo partial = h.WithReduction(0.8f, out _);
                Assert.That(partial.Amount, Is.EqualTo(Mathf.Max(1f, amount * 0.2f)).Within(1e-4f), "a partial reduction keeps the 1.0 floor");

                DamageInfo brokenInfo = h.WithReduction(1f, out _, guardBroken: true);
                Assert.That(brokenInfo.Amount, Is.GreaterThanOrEqualTo(1f), "a broken guard never takes a hit to zero");

                Assert.That(DamageResolver.ApplyArmorRating(in held, 50f).Amount, Is.EqualTo(0f), "armor does not lift a held block to the floor");
                Assert.That(DamageResolver.ApplyProjectileFalloff(in held, 0.5f).Amount, Is.EqualTo(0f), "nor does projectile falloff");
            }

            Assert.That(hit.IsValid, Is.True);
            Assert.That(DamageInfo.Invalid.IsValid, Is.False);
            DamageInfo zeroUnblocked = new DamageInfo(0f, Vector3.zero, Vector3.forward, Vector3.zero, 1, EntityKind.Enemy, DamageType.Physical, false, false, 0f);
            Assert.That(zeroUnblocked.IsValid, Is.False, "zero damage is only valid as a held block");
        }
    }
}
