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
                if (w == sword || w == rapier) continue;
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
        public void ABlockedRiposteStrike_StillReducesByTheGuard_ThenTheMultiplier()
        {
            WeaponData sword = All.Find(w => w.weaponId == "ashen_short_sword");
            float mult = RiposteRules.StrikeMultiplier(sword.riposteDamageMultiplier);
            sword.TryResolveRiposteStrike(0, out _, out SwingPatternData pattern);
            Dummy open = new Dummy { Guarding = false };
            Dummy guard = new Dummy { Guarding = true, Resist = sword.impactResist, Reduction = 0.5f };
            DamageInfo unguarded = Swing(sword, pattern, mult, open);
            DamageInfo guarded = Swing(sword, pattern, mult, guard);
            Assert.That(guarded.Blocked, Is.True);
            Assert.That(guarded.Amount, Is.EqualTo(unguarded.Amount * (1f - guard.Reduction)).Within(unguarded.Amount * 1e-4f), "resolved like any blocked hit");
        }
    }
}
