using System;
using Emberdelve.AI;
using Emberdelve.Data;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.Combat.Tests
{
    /// <summary>
    /// The Hollow Servitor has two melee attacks (pattern 0 diagonal, pattern 1 vertical overhead). The server brain picks one per swing; the
    /// authority validates the requested pattern index against the weapon's patterns (WeaponData.TryResolveComboStep is the call
    /// LocalCombatAuthority.TryBeginMeleeSwing makes), so a forged or stale index is refused.
    /// </summary>
    public sealed class EnemyAttackVariantTests
    {
        private WeaponData _weapon;
        private SwingPatternData _a, _b;

        [SetUp]
        public void SetUp()
        {
            EnemyAttackChoice.Override = -1;
            _a = ScriptableObject.CreateInstance<SwingPatternData>();
            _b = ScriptableObject.CreateInstance<SwingPatternData>();
            _weapon = ScriptableObject.CreateInstance<WeaponData>();
            _weapon.swingPatterns = new[] { _a, _b };
            _weapon.comboSteps = new ComboStep[0];
        }

        [TearDown]
        public void TearDown()
        {
            EnemyAttackChoice.Override = -1;
            UnityEngine.Object.DestroyImmediate(_weapon);
            UnityEngine.Object.DestroyImmediate(_a);
            UnityEngine.Object.DestroyImmediate(_b);
        }

        [Test]
        public void AuthorityRejectsPatternIndexOutsideTheWeaponsPatterns()
        {
            Assert.That(_weapon.TryResolveComboStep(0, 0, out _, out SwingPatternData first, out _), Is.True);
            Assert.That(first, Is.SameAs(_a));
            Assert.That(_weapon.TryResolveComboStep(0, 1, out _, out SwingPatternData second, out _), Is.True);
            Assert.That(second, Is.SameAs(_b));

            foreach (int bad in new[] { -1, 2, 7, int.MaxValue, int.MinValue })
            {
                Assert.That(_weapon.TryResolveComboStep(0, bad, out _, out SwingPatternData none, out string reason), Is.False, "index " + bad);
                Assert.That(none, Is.Null);
                Assert.That(reason, Is.Not.Empty);
            }
            Assert.That(_weapon.TryResolveSwingPattern(1, 0, out _), Is.False, "a monster has no combo step 1");
        }

        [Test]
        public void ChoiceStaysInRangeAndUsesBothAttacks()
        {
            var random = new System.Random(12345);
            int[] seen = new int[2];
            for (int i = 0; i < 400; i++)
            {
                int index = EnemyAttackChoice.Pick(2, random);
                Assert.That(EnemyAttackChoice.IsValid(index, 2), Is.True);
                seen[index]++;
            }
            Assert.That(seen[0], Is.GreaterThan(120), "the diagonal is chosen about half the time");
            Assert.That(seen[1], Is.GreaterThan(120), "the overhead is chosen about half the time");
        }

        [Test]
        public void ChoiceOnlyUsesUsablePatternSlots()
        {
            SwingPatternData valid = MakeValidPattern(), valid2 = MakeValidPattern();
            WeaponData weapon = ScriptableObject.CreateInstance<WeaponData>();
            try
            {
                weapon.comboSteps = new ComboStep[0];
                weapon.swingPatterns = new[] { valid, null, valid2 };
                var usable = EnemyAttackChoice.UsableIndices(weapon);
                Assert.That(usable, Is.EqualTo(new[] { 0, 2 }), "the null slot is skipped");
                var random = new System.Random(7);
                for (int i = 0; i < 200; i++) Assert.That(EnemyAttackChoice.PickFor(weapon, random), Is.Not.EqualTo(1));

                weapon.swingPatterns = new SwingPatternData[] { null, null };
                Assert.That(EnemyAttackChoice.PickFor(weapon, random), Is.Zero, "nothing usable falls back to 0");
                weapon.swingPatterns = new SwingPatternData[] { null, valid };
                Assert.That(EnemyAttackChoice.PickFor(weapon, random), Is.EqualTo(1), "the only usable slot is used");
                weapon.swingPatterns = new[] { valid, valid2 };
                EnemyAttackChoice.Override = 1;
                Assert.That(EnemyAttackChoice.PickFor(weapon, random), Is.EqualTo(1));
                EnemyAttackChoice.Override = 5;
                Assert.That(EnemyAttackChoice.PickFor(weapon, random), Is.EqualTo(0), "an unusable override falls back to the first usable slot");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(weapon);
                UnityEngine.Object.DestroyImmediate(valid);
                UnityEngine.Object.DestroyImmediate(valid2);
            }
        }

        private static SwingPatternData MakeValidPattern()
        {
            SwingPatternData p = ScriptableObject.CreateInstance<SwingPatternData>();
            p.patternId = "test";
            p.keys = new[] { new SwingKey(0f, Vector3.zero, 0f, 0f), new SwingKey(1f, Vector3.forward, 0f, 10f) };
            return p;
        }

        [Test]
        public void SingleAttackMonstersAlwaysUseThePatternZero()
        {
            var random = new System.Random(1);
            for (int i = 0; i < 50; i++)
            {
                Assert.That(EnemyAttackChoice.Pick(1, random), Is.Zero);
                Assert.That(EnemyAttackChoice.Pick(0, random), Is.Zero);
            }
            Assert.That(EnemyAttackChoice.IsValid(1, 1), Is.False);
            Assert.That(EnemyAttackChoice.IsValid(-1, 2), Is.False);
        }

        [Test]
        public void OverrideSeamPinsTheChoiceAndIsClampedIntoRange()
        {
            try
            {
                EnemyAttackChoice.Override = 1;
                Assert.That(EnemyAttackChoice.Pick(2, new System.Random(3)), Is.EqualTo(1));
                EnemyAttackChoice.Override = 5;
                Assert.That(EnemyAttackChoice.Pick(2, new System.Random(3)), Is.EqualTo(1));
                Assert.That(EnemyAttackChoice.Pick(1, new System.Random(3)), Is.Zero);
            }
            finally { EnemyAttackChoice.Override = -1; }
        }

        [Test]
        public void ServitorClawHasTheDiagonalAndTheOverheadPattern()
        {
            WeaponData claw = AssetDatabase.LoadAssetAtPath<WeaponData>("Assets/Emberdelve/Data/Weapon_HollowClaw.asset");
            Assert.That(claw, Is.Not.Null);
            // Needs the regenerated data (Emberdelve asset library: ApplyEnemySwings creates Pattern_ClawOverhead).
            Assert.That(claw.swingPatterns, Is.Not.Null.And.Length.EqualTo(2), "claw patterns: [0] diagonal, [1] overhead");
            Assert.That(claw.swingPatterns[0].name, Is.EqualTo("Pattern_ClawSlash"));
            Assert.That(claw.swingPatterns[1].name, Is.EqualTo("Pattern_ClawOverhead"));
            Assert.That(claw.swingPatterns[1].kind, Is.EqualTo(SwingKind.Overhead));
            Assert.That(claw.swingPatterns[1].keys.Length, Is.EqualTo(13));
            Assert.That(claw.swingPatterns[1].Validate(out string problem), Is.True, problem);
            SwingKey first = claw.swingPatterns[1].keys[0], last = claw.swingPatterns[1].keys[12];
            Assert.That(first.basePos.y, Is.GreaterThan(last.basePos.y), "the weapon comes down");
            Assert.That(first.bladePitch, Is.LessThan(last.bladePitch - 60f), "the blade turns from raised to pointing down");
            Assert.That(claw.TryResolveComboStep(0, 2, out _, out _, out _), Is.False);

            WeaponData maul = AssetDatabase.LoadAssetAtPath<WeaponData>("Assets/Emberdelve/Data/Weapon_WardenMaul.asset");
            Assert.That(maul.swingPatterns.Length, Is.EqualTo(1), "the Ashen Warden keeps its single attack");
        }
    }
}
