using System;
using UnityEditor;
using UnityEngine;
using Emberdelve.Stats;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// U2A1 headless gate for the pure six-attribute rules (Docs/Design/ATTRIBUTES.md).
    /// No scene loading, no play mode, no ScriptableObject/GameObject, no builders: it only calls
    /// the pure math in Emberdelve.Stats and compares it against constants derived by hand in the
    /// comments below (never by calling the implementation to produce an expected value).
    ///
    ///   Unity.exe -batchmode -nographics -projectPath . -logFile Logs/u2a1.log
    ///             -executeMethod Emberdelve.EditorTools.EmberdelveAttributeRulesGate.Run
    ///
    /// Prints exactly one [Emberdelve][AttributeRules] PASS line and exits 0, or prints one FAIL
    /// summary line (plus one detail line per mismatched assertion) and exits 1.
    /// </summary>
    public static class EmberdelveAttributeRulesGate
    {
        private const string Tag = "[Emberdelve][AttributeRules]";
        private const float Tolerance = 1e-3f;

        private static int _checks;
        private static int _failures;

        public static void Run()
        {
            _checks = 0;
            _failures = 0;

            try
            {
                CheckModifiers();
                CheckScoreClamps();
                CheckDefaultScoresAreLegal();
                CheckWarriorBaseline();
                CheckArcherBaseline();
                CheckDerivedPoolLowerClamps();
                CheckMoveSpeedClamps();
                CheckWeaponDamageScales();
                CheckCharismaEffectScaleBounds();
                CheckBonusesRecomputeFromPristineBase();
                CheckCurrentPoolClamping();
                CheckAllowlistedKeys();
            }
            catch (Exception exception)
            {
                _failures++;
                Debug.LogError(Tag + " unhandled exception during the rules pass: " + exception);
            }

            if (_failures == 0)
            {
                Debug.Log(Tag + " PASS checks=" + _checks + " failures=0");
                EditorApplication.Exit(0);
                return;
            }

            Debug.LogError(Tag + " FAIL checks=" + _checks + " failures=" + _failures);
            EditorApplication.Exit(1);
        }

        /// <summary>floor((score - 10) / 2.0), hand-computed per score.</summary>
        private static void CheckModifiers()
        {
            CheckInt("modifier(9)", -1, AttributeRules.Modifier(9), "(9-10)/2=-0.5 floor=-1");
            CheckInt("modifier(11)", 0, AttributeRules.Modifier(11), "(11-10)/2=0.5 floor=0");
            CheckInt("modifier(16)", 3, AttributeRules.Modifier(16), "(16-10)/2=3.0 floor=3");
            CheckInt("modifier(10 anchor)", 0, AttributeRules.Modifier(10), "(10-10)/2=0 floor=0");
            CheckInt("modifier(13)", 1, AttributeRules.Modifier(13), "(13-10)/2=1.5 floor=1");
            CheckInt("modifier(15)", 2, AttributeRules.Modifier(15), "(15-10)/2=2.5 floor=2");
            CheckInt("modifier(29)", 9, AttributeRules.Modifier(29), "(29-10)/2=9.5 floor=9");
            CheckInt("modifier(30)", 10, AttributeRules.Modifier(30), "(30-10)/2=10.0 floor=10");
            // Negative odd scores floor away from zero, they do not truncate toward zero.
            CheckInt("modifier(7)", -2, AttributeRules.Modifier(7), "(7-10)/2=-1.5 floor=-2");
            CheckInt("modifier(5)", -3, AttributeRules.Modifier(5), "(5-10)/2=-2.5 floor=-3");
            CheckInt("modifier(3)", -4, AttributeRules.Modifier(3), "(3-10)/2=-3.5 floor=-4");
            CheckInt("modifier(2)", -4, AttributeRules.Modifier(2), "(2-10)/2=-4.0 floor=-4");
            CheckInt("modifier(1)", -5, AttributeRules.Modifier(1), "(1-10)/2=-4.5 floor=-5");
            // Out-of-range raw scores are clamped before the modifier is taken.
            CheckInt("modifier(0 clamped to 1)", -5, AttributeRules.Modifier(0), "clamp(0)=1 -> -4.5 floor=-5");
            CheckInt("modifier(31 clamped to 30)", 10, AttributeRules.Modifier(31), "clamp(31)=30 -> 10.0 floor=10");
        }

        private static void CheckScoreClamps()
        {
            CheckInt("clampScore(0)", 1, AttributeRules.ClampScore(0), "lower bound 1");
            CheckInt("clampScore(-7)", 1, AttributeRules.ClampScore(-7), "lower bound 1");
            CheckInt("clampScore(31)", 30, AttributeRules.ClampScore(31), "upper bound 30");
            CheckInt("clampScore(9999)", 30, AttributeRules.ClampScore(9999), "upper bound 30");
            CheckInt("clampScore(10)", 10, AttributeRules.ClampScore(10), "in range unchanged");

            // A snapshot clamps on creation, so every later read is already legal.
            AttributeScores clamped = new AttributeScores(0, 31, 14, 8, 10, 10);
            CheckInt("constructed strength read", 1, clamped.Strength, "clamp(0)=1");
            CheckInt("constructed dexterity read", 30, clamped.Dexterity, "clamp(31)=30");
            CheckInt("clamped strength modifier", -5, clamped.Modifier(SixAttribute.Strength), "score 1 -> -4.5 floor=-5");
            CheckInt("clamped dexterity modifier", 10, clamped.Modifier(SixAttribute.Dexterity), "score 30 -> 10.0 floor=10");
            CheckInt("Get() by enum", 2, clamped.Modifier(SixAttribute.Constitution), "(14-10)/2=2.0 floor=2");

            // Effective score = clamp(base + bonusTotal, 1, 30).
            CheckInt("effectiveScore(14,+2)", 16, AttributeRules.EffectiveScore(14, 2), "14+2=16 in range");
            CheckInt("effectiveScore(30,+3)", 30, AttributeRules.EffectiveScore(30, 3), "33 clamped to 30");
            CheckInt("effectiveScore(1,-3)", 1, AttributeRules.EffectiveScore(1, -3), "-2 clamped to 1");
        }

        /// <summary>
        /// default(AttributeScores), new AttributeScores[1] and default(AttributeSnapshot) never run the
        /// clamping constructor, so every public read must still report the legal minimum 1 (never 0)
        /// and a bonus on such a value must be added to the normalized 1, not to the raw 0.
        /// </summary>
        private static void CheckDefaultScoresAreLegal()
        {
            AttributeScores zeroed = default(AttributeScores);

            // Zero-initialized backing fields read as the legal minimum, not as 0.
            CheckInt("default strength property", 1, zeroed.Strength, "raw 0 -> clamp(0)=1");
            CheckInt("default dexterity property", 1, zeroed.Dexterity, "raw 0 -> clamp(0)=1");
            CheckInt("default constitution property", 1, zeroed.Constitution, "raw 0 -> clamp(0)=1");
            CheckInt("default intelligence property", 1, zeroed.Intelligence, "raw 0 -> clamp(0)=1");
            CheckInt("default wisdom property", 1, zeroed.Wisdom, "raw 0 -> clamp(0)=1");
            CheckInt("default charisma property", 1, zeroed.Charisma, "raw 0 -> clamp(0)=1");

            // Get() and its unmapped-enum fallback agree with the properties.
            CheckInt("default Get(strength)", 1, zeroed.Get(SixAttribute.Strength), "same bound as the property");
            CheckInt("default Get(charisma)", 1, zeroed.Get(SixAttribute.Charisma), "same bound as the property");
            CheckInt("default Get(unmapped enum)", 1, zeroed.Get((SixAttribute)99), "fallback is MinScore");

            // ToArray agrees with the properties, element by element in enum order.
            int[] zeroedArray = zeroed.ToArray();
            CheckTrue("default ToArray length", zeroedArray != null && zeroedArray.Length == 6,
                "six scores in enum order");
            CheckTrue("default ToArray all legal 1", zeroedArray[0] == 1 && zeroedArray[1] == 1 &&
                zeroedArray[2] == 1 && zeroedArray[3] == 1 && zeroedArray[4] == 1 && zeroedArray[5] == 1,
                "every element clamp(0)=1, matching Strength..Charisma");

            // A heap-allocated array element is zero-initialized the same way.
            AttributeScores[] heap = new AttributeScores[1];
            CheckInt("array element strength property", 1, heap[0].Strength, "new AttributeScores[1] element raw 0 -> 1");
            CheckInt("array element Get(wisdom)", 1, heap[0].Get(SixAttribute.Wisdom), "same bound through Get");
            int[] heapArray = heap[0].ToArray();
            CheckTrue("array element ToArray legal", heapArray[0] == 1 && heapArray[5] == 1,
                "ToArray of a default element reads 1..1");

            // Modifier is consistent with the score it reads: score 1 -> floor((1-10)/2) = -5.
            CheckInt("default strength modifier", -5, zeroed.Modifier(SixAttribute.Strength), "score 1 -> -4.5 floor -5");
            CheckInt("default charisma modifier", -5, zeroed.Modifier(SixAttribute.Charisma), "score 1 -> -4.5 floor -5");

            // Describe() prints the legal score, not the raw 0.
            CheckTrue("default Describe text", zeroed.Describe() ==
                "strength=1(-5) dexterity=1(-5) constitution=1(-5) intelligence=1(-5) wisdom=1(-5) charisma=1(-5)",
                "hand-written expected string for six scores of 1");

            // default(AttributeSnapshot): pristine base legal, bonus set empty, effective legal.
            AttributeSnapshot zeroedSnapshot = default(AttributeSnapshot);
            CheckTrue("default snapshot base legal", zeroedSnapshot.Base.Strength == 1 &&
                zeroedSnapshot.Base.Dexterity == 1 && zeroedSnapshot.Base.Constitution == 1 &&
                zeroedSnapshot.Base.Intelligence == 1 && zeroedSnapshot.Base.Wisdom == 1 &&
                zeroedSnapshot.Base.Charisma == 1, "base reads clamp(0)=1 through the struct accessors");
            CheckTrue("default snapshot bonuses empty", zeroedSnapshot.Bonuses.IsEmpty, "all six totals 0");
            CheckInt("default snapshot effective constitution", 1, zeroedSnapshot.Effective.Constitution, "1+0=1");
            CheckInt("default snapshot EffectiveScore(wisdom)", 1, zeroedSnapshot.EffectiveScore(SixAttribute.Wisdom), "1+0=1");
            CheckInt("default snapshot modifier", -5, zeroedSnapshot.Modifier(SixAttribute.Constitution), "score 1 -> -5");
            CheckTrue("default snapshot effective equals base", zeroedSnapshot.Effective.Strength == 1 &&
                zeroedSnapshot.Effective.Charisma == 1, "no bonuses, so effective equals base 1");

            // A bonus on a default value is added to the NORMALIZED 1: 1 + 1 = 2, not 0 + 1 = 1.
            AttributeScores bonused = zeroed.WithBonuses(new AttributeBonuses(1, 2, 0, 0, 0, 0));
            CheckInt("default +1 STR derives 2", 2, bonused.Strength, "normalized 1 + 1 = 2");
            CheckInt("default +2 DEX derives 3", 3, bonused.Dexterity, "normalized 1 + 2 = 3");
            CheckInt("default untouched CON stays 1", 1, bonused.Constitution, "normalized 1 + 0 = 1");
            CheckInt("default bonused STR modifier", -4, bonused.Modifier(SixAttribute.Strength), "(2-10)/2=-4.0 floor -4");
            CheckInt("default bonused DEX modifier", -4, bonused.Modifier(SixAttribute.Dexterity), "(3-10)/2=-3.5 floor -4");
            int[] bonusedArray = bonused.ToArray();
            CheckTrue("default bonused ToArray", bonusedArray[0] == 2 && bonusedArray[1] == 3 && bonusedArray[2] == 1,
                "2/3/1 in enum order");

            // Same rule through a default-base snapshot; the base itself stays pristine.
            AttributeSnapshot bonusedSnapshot = new AttributeSnapshot(zeroed, new AttributeBonuses(1, 0, 0, 0, 0, 0));
            CheckInt("default snapshot base stays 1", 1, bonusedSnapshot.Base.Strength, "base never mutated by bonusing");
            CheckInt("default snapshot effective 1+1", 2, bonusedSnapshot.Effective.Strength, "1+1=2");
            CheckInt("default snapshot bonus re-derive idempotent", 2,
                bonusedSnapshot.WithBonuses(new AttributeBonuses(1, 0, 0, 0, 0, 0)).Effective.Strength,
                "bonus set replaced, still 1+1=2");

            // Negative bonuses on a default value clamp back to the minimum, never below.
            CheckInt("default -3 STR clamps to 1", 1, zeroed.WithBonuses(new AttributeBonuses(-3, 0, 0, 0, 0, 0)).Strength,
                "1-3=-2 -> 1");
            CheckInt("default snapshot -5 CON clamps to 1", 1,
                new AttributeSnapshot(zeroed, new AttributeBonuses(0, 0, -5, 0, 0, 0)).Effective.Constitution, "1-5=-4 -> 1");

            // Derivation from a default snapshot runs on the legal score 1 (modifier -5) end to end.
            DerivedStatBases bases = new DerivedStatBases(100, 75, 3f, 4.3f);
            DerivedStats zeroedStats = AttributeRules.Derive(zeroedSnapshot, bases);
            CheckInt("default snapshot max health", 50, zeroedStats.MaxHealth, "100+10*(-5)=50");
            CheckInt("default snapshot max mana", 50, zeroedStats.MaxMana, "75+5*(-5)=50");
            CheckClose("default snapshot mana regen", 1.75f, zeroedStats.ManaRegen, "3+0.25*(-5)=1.75");
            CheckClose("default snapshot move speed", 3.7625f, zeroedStats.MoveSpeed, "4.3*max(0.5,1-0.125)=4.3*0.875=3.7625");
            CheckTrue("derived effective scores legal", zeroedStats.Effective.Strength == 1 &&
                zeroedStats.Effective.Dexterity == 1 && zeroedStats.Effective.Constitution == 1 &&
                zeroedStats.Effective.Intelligence == 1 && zeroedStats.Effective.Wisdom == 1 &&
                zeroedStats.Effective.Charisma == 1, "DerivedStats.Effective reads 1 on every attribute");
            CheckClose("default snapshot weapon damage", 20.8f,
                AttributeRules.WeaponDamage(26f, WeaponDamageScale.Melee, zeroedStats.Effective),
                "STR 1 -> modifier -5 -> 26*0.8=20.8");
            CheckClose("default snapshot effect scale", 0.8f, AttributeRules.AbilityEffectScale(zeroedStats.Effective),
                "CHA 1 -> modifier -5 -> 1-0.2=0.8");

            // Explicitly constructed legal scores are unchanged by the read-side normalization.
            AttributeScores constructed = new AttributeScores(1, 2, 3, 4, 5, 30);
            CheckTrue("explicit legal scores unchanged", constructed.Strength == 1 && constructed.Dexterity == 2 &&
                constructed.Constitution == 3 && constructed.Intelligence == 4 && constructed.Wisdom == 5 &&
                constructed.Charisma == 30, "1/2/3/4/5/30 read back exactly");
            int[] constructedArray = constructed.ToArray();
            CheckTrue("explicit Get/ToArray match properties", constructed.Get(SixAttribute.Strength) == constructed.Strength &&
                constructed.Get(SixAttribute.Charisma) == constructed.Charisma &&
                constructedArray[1] == constructed.Dexterity && constructedArray[4] == constructed.Wisdom,
                "one value per attribute, three ways of reading it");
            AttributeScores constructedBonused = constructed.WithBonuses(new AttributeBonuses(1, 1, 1, 1, 1, 1));
            CheckTrue("explicit +1 each unchanged shape", constructedBonused.Strength == 2 &&
                constructedBonused.Dexterity == 3 && constructedBonused.Constitution == 4 &&
                constructedBonused.Intelligence == 5 && constructedBonused.Wisdom == 6 &&
                constructedBonused.Charisma == 30, "2/3/4/5/6 and 30+1 clamped to 30");
            CheckTrue("explicit Describe unchanged", constructedBonused.Describe() ==
                "strength=2(-4) dexterity=3(-4) constitution=4(-3) intelligence=5(-3) wisdom=6(-2) charisma=30(10)",
                "hand-written expected string for 2/3/4/5/6/30");
        }

        /// <summary>Warrior 16/10/14/8/10/10 over bases 100HP/75mana -> 120/70.</summary>
        private static void CheckWarriorBaseline()
        {
            AttributeScores warrior = new AttributeScores(16, 10, 14, 8, 10, 10);
            // Explicit base values from the ATTRIBUTES.md amendment.
            DerivedStatBases bases = new DerivedStatBases(100, 75, 3f, 4.3f);
            DerivedStats stats = AttributeRules.Derive(new AttributeSnapshot(warrior), bases);

            // CON 14 -> (14-10)/2 = 2; 100 + 10*2 = 120
            CheckInt("warrior max health", 120, stats.MaxHealth, "100+10*2=120");
            // INT 8 -> (8-10)/2 = -1; 75 + 5*(-1) = 70
            CheckInt("warrior max mana", 70, stats.MaxMana, "75+5*(-1)=70");
            // WIS 10 -> 0; 3 + 0.25*0 = 3
            CheckClose("warrior mana regen", 3f, stats.ManaRegen, "3+0.25*0=3");
            // DEX 10 -> 0; 4.3 * max(0.5, 1+0.025*0) * 1 + 0 = 4.3
            CheckClose("warrior move speed", 4.3f, stats.MoveSpeed, "4.3*1.0=4.3");
            CheckTrue("warrior effective scores unchanged",
                stats.Effective.Strength == 16 && stats.Effective.Dexterity == 10 &&
                stats.Effective.Constitution == 14 && stats.Effective.Intelligence == 8 &&
                stats.Effective.Wisdom == 10 && stats.Effective.Charisma == 10,
                "no bonuses applied, effective == base 16/10/14/8/10/10");
        }

        /// <summary>Archer 10/16/12/10/14/8 over bases 80HP/90mana -> 90/90.</summary>
        private static void CheckArcherBaseline()
        {
            AttributeScores archer = new AttributeScores(10, 16, 12, 10, 14, 8);
            DerivedStatBases bases = new DerivedStatBases(80, 90, 3.5f, 4.3f);
            DerivedStats stats = AttributeRules.Derive(new AttributeSnapshot(archer), bases);

            // CON 12 -> (12-10)/2 = 1; 80 + 10*1 = 90
            CheckInt("archer max health", 90, stats.MaxHealth, "80+10*1=90");
            // INT 10 -> 0; 90 + 5*0 = 90
            CheckInt("archer max mana", 90, stats.MaxMana, "90+5*0=90");
            // WIS 14 -> (14-10)/2 = 2; 3.5 + 0.25*2 = 4.0
            CheckClose("archer mana regen", 4f, stats.ManaRegen, "3.5+0.25*2=4.0");
            // DEX 16 -> 3; 4.3 * (1 + 0.025*3) = 4.3 * 1.075 = 4.6225
            CheckClose("archer move speed", 4.6225f, stats.MoveSpeed, "4.3*1.075=4.6225");
        }

        /// <summary>Floors of the derived pools, forced with the worst legal score (1 -> -5).</summary>
        private static void CheckDerivedPoolLowerClamps()
        {
            const int worst = -5; // score 1 -> (1-10)/2 = -4.5 -> floor -5 (asserted in CheckModifiers)

            // 30 + 10*(-5) = -20 -> floor 25
            CheckInt("max health floor", 25, AttributeRules.MaxHealth(30, worst), "30-50=-20 -> 25");
            // 10 + 5*(-5) = -15 -> floor 10
            CheckInt("max mana floor", 10, AttributeRules.MaxMana(10, worst), "10-25=-15 -> 10");
            // 0.1 + 0.25*(-5) = -1.15 -> floor 0.1
            CheckClose("mana regen floor", 0.1f, AttributeRules.ManaRegen(0.1f, worst), "0.1-1.25=-1.15 -> 0.1");
            // Unusual negative odds beyond any legal score (modifier -25) must still respect the floors.
            CheckInt("max health floor at modifier -25", 25, AttributeRules.MaxHealth(100, -25), "100-250=-150 -> 25");
            CheckClose("mana regen floor at modifier -25", 0.1f, AttributeRules.ManaRegen(3f, -25), "3-6.25=-3.25 -> 0.1");
        }

        private static void CheckMoveSpeedClamps()
        {
            // 4.3 * max(0.5, 1 + 0.025*(-20)) = 4.3 * 0.5 = 2.15, inside 2..8.
            CheckClose("move speed factor floor at modifier -20", 2.15f,
                AttributeRules.MoveSpeed(4.3f, -20, 1f, 0f), "4.3*0.5=2.15");
            // 2 * 0.875 * 0.5 = 0.875 -> clamped up to the 2 m/s floor.
            CheckClose("move speed lower clamp", 2f,
                AttributeRules.MoveSpeed(2f, -5, 0.5f, 0f), "2*0.875*0.5=0.875 -> 2");
            // 30 * 1.25 + 5 = 42.5 -> clamped down to the 8 m/s ceiling.
            CheckClose("move speed upper clamp", 8f,
                AttributeRules.MoveSpeed(30f, 10, 1f, 5f), "30*1.25+5=42.5 -> 8");
            // Explicit flat item term: 4.3*1.075 + 0.5 = 5.1225.
            CheckClose("move speed with flat item term", 5.1225f,
                AttributeRules.MoveSpeed(4.3f, 3, 1f, 0.5f), "4.6225+0.5=5.1225");
            // Explicit passive multiplier: 4.3*1.075*1.2 = 5.547.
            CheckClose("move speed with passive multiplier", 5.547f,
                AttributeRules.MoveSpeed(4.3f, 3, 1.2f, 0f), "4.6225*1.2=5.547");
            // A -6 DEX bonus on a 16 DEX archer lands on 11 -> modifier 0 -> exactly base speed.
            AttributeScores archer = new AttributeScores(10, 16, 12, 10, 14, 8);
            AttributeScores nerfed = archer.WithBonuses(new AttributeBonuses(0, -5, 0, 0, 0, 0));
            CheckInt("dex bonus lands on 11", 11, nerfed.Dexterity, "16-5=11");
            CheckClose("modifier 0 restores base speed", 4.3f,
                AttributeRules.MoveSpeed(4.3f, nerfed.Modifier(SixAttribute.Dexterity)), "DEX 11 -> modifier 0 (asserted in CheckModifiers) -> 4.3*1.0=4.3");
        }

        /// <summary>STR melee, DEX ranged, finesse = max(STR,DEX), plus the 0.2 damage factor floor.</summary>
        private static void CheckWeaponDamageScales()
        {
            const float base26 = 26f;

            // 26 * (1 + 0.04*3) = 26 * 1.12 = 29.12
            CheckClose("melee damage at STR modifier 3", 29.12f, AttributeRules.MeleeDamage(base26, 3), "26*1.12=29.12");
            CheckClose("ranged damage at DEX modifier 3", 29.12f, AttributeRules.RangedDamage(base26, 3), "26*1.12=29.12");
            CheckClose("damage at modifier 0", 26f, AttributeRules.ScaleDamage(base26, 0), "26*1.0=26");
            // Lowest legal score 1 -> modifier -5 -> factor 1-0.2 = 0.8 -> 20.8 (above the 0.2 floor).
            CheckClose("damage at worst legal modifier", 20.8f,
                AttributeRules.MeleeDamage(base26, -5), "score 1 -> modifier -5 -> 26*0.8=20.8");
            // Unusual negative odds: factor 1 + 0.04*(-25) = 0 -> floored to 0.2 -> 5.2.
            CheckClose("damage factor floor at modifier -25", 5.2f, AttributeRules.ScaleDamage(base26, -25), "26*0.2=5.2");
            CheckClose("damage factor floor at modifier -30", 5.2f, AttributeRules.ScaleDamage(base26, -30), "1-1.2=-0.2 -> 0.2 -> 5.2");

            // Finesse uses the better modifier, in either attribute order.
            // STR 8 -> -1, DEX 16 -> 3 -> max = 3 -> 26*1.12 = 29.12
            CheckClose("finesse picks DEX", 29.12f,
                AttributeRules.FinesseDamage(base26, -1, 3), "max(-1,3)=3 -> 29.12");
            CheckClose("finesse picks STR", 29.12f,
                AttributeRules.FinesseDamage(base26, 3, -1), "max(3,-1)=3 -> 29.12");
            // STR 8 -> -1, DEX 10 -> 0 -> max = 0 -> 26
            CheckClose("finesse with no positive modifier", 26f,
                AttributeRules.FinesseDamage(base26, -1, 0), "max(-1,0)=0 -> 26");

            AttributeScores warrior = new AttributeScores(16, 10, 14, 8, 10, 10);
            AttributeScores archer = new AttributeScores(10, 16, 12, 10, 14, 8);
            // Warrior: STR 16 -> 3, DEX 10 -> 0.
            CheckClose("warrior melee", 29.12f, AttributeRules.WeaponDamage(base26, WeaponDamageScale.Melee, warrior), "STR mod 3 -> 29.12");
            CheckClose("warrior ranged", 26f, AttributeRules.WeaponDamage(base26, WeaponDamageScale.Ranged, warrior), "DEX mod 0 -> 26");
            CheckClose("warrior finesse", 29.12f, AttributeRules.WeaponDamage(base26, WeaponDamageScale.Finesse, warrior), "max(3,0)=3 -> 29.12");
            // Archer: STR 10 -> 0, DEX 16 -> 3.
            CheckClose("archer melee", 26f, AttributeRules.WeaponDamage(base26, WeaponDamageScale.Melee, archer), "STR mod 0 -> 26");
            CheckClose("archer ranged", 29.12f, AttributeRules.WeaponDamage(base26, WeaponDamageScale.Ranged, archer), "DEX mod 3 -> 29.12");
            CheckClose("archer finesse", 29.12f, AttributeRules.WeaponDamage(base26, WeaponDamageScale.Finesse, archer), "max(0,3)=3 -> 29.12");
        }

        /// <summary>clamp(1 + 0.04 * chaModifier, 0.6, 1.4).</summary>
        private static void CheckCharismaEffectScaleBounds()
        {
            CheckClose("effect scale at modifier 0", 1f, AttributeRules.AbilityEffectScale(0), "1+0=1");
            // CHA 30 -> 10 -> 1 + 0.4 = 1.4, exactly the ceiling.
            CheckClose("effect scale at CHA 30", 1.4f, AttributeRules.AbilityEffectScale(10), "CHA 30 -> modifier 10 -> 1+0.4=1.4 (ceiling)");
            // CHA 1 -> -5 -> 1 - 0.2 = 0.8.
            CheckClose("effect scale at CHA 1", 0.8f, AttributeRules.AbilityEffectScale(-5), "CHA 1 -> modifier -5 -> 1-0.2=0.8");
            // Unusual negative odds: 1 + 0.04*(-20) = 0.2 -> floored to 0.6.
            CheckClose("effect scale floor", 0.6f, AttributeRules.AbilityEffectScale(-20), "1-0.8=0.2 -> 0.6");
            // Unusual positive odds: 1 + 0.04*20 = 1.8 -> capped at 1.4.
            CheckClose("effect scale ceiling", 1.4f, AttributeRules.AbilityEffectScale(20), "1+0.8=1.8 -> 1.4");

            AttributeScores warrior = new AttributeScores(16, 10, 14, 8, 10, 10);
            AttributeScores archer = new AttributeScores(10, 16, 12, 10, 14, 8);
            CheckClose("warrior effect scale (CHA 10)", 1f, AttributeRules.AbilityEffectScale(warrior), "mod 0 -> 1");
            // Archer CHA 8 -> (8-10)/2 = -1 -> 1 - 0.04 = 0.96.
            CheckClose("archer effect scale (CHA 8)", 0.96f, AttributeRules.AbilityEffectScale(archer), "mod -1 -> 0.96");
        }

        /// <summary>
        /// Bonuses are summed and then clamped against the pristine base; re-deriving can never
        /// stack them and the base snapshot must stay untouched.
        /// </summary>
        private static void CheckBonusesRecomputeFromPristineBase()
        {
            AttributeScores warriorBase = new AttributeScores(16, 10, 14, 8, 10, 10);
            DerivedStatBases bases = new DerivedStatBases(100, 75, 3f, 4.3f);

            AttributeBonuses cuirass = new AttributeBonuses(0, 0, 2, 0, 0, 0);   // +2 CON
            AttributeBonuses twoPieces = new AttributeBonuses(0, 0, 1, 0, 0, 0); // +1 CON each, summed

            AttributeSnapshot equipped = new AttributeSnapshot(warriorBase, cuirass);

            // CON 14 + 2 = 16 -> (16-10)/2 = 3 -> 100 + 10*3 = 130
            CheckInt("bonused CON score", 16, equipped.Effective.Constitution, "14+2=16");
            CheckInt("bonused CON modifier", 3, equipped.Modifier(SixAttribute.Constitution), "(16-10)/2=3");
            CheckInt("bonused max health", 130, AttributeRules.Derive(equipped, bases).MaxHealth, "100+10*3=130");

            // Two +1 CON pieces sum to +2 before clamping: one application, not two.
            AttributeBonuses summed = twoPieces.Plus(twoPieces);
            CheckInt("two +1 CON sum", 2, summed.Total(SixAttribute.Constitution), "1+1=2");
            CheckInt("summed CON score", 16, new AttributeSnapshot(warriorBase, summed).Effective.Constitution, "14+2=16");
            CheckInt("summed bonused max health", 130,
                AttributeRules.Derive(new AttributeSnapshot(warriorBase, summed), bases).MaxHealth, "100+10*3=130");

            // Re-equipping the same set re-derives the identical number (no accumulation).
            int first = AttributeRules.Derive(equipped, bases).MaxHealth;
            int second = AttributeRules.Derive(equipped, bases).MaxHealth;
            CheckInt("re-derive is idempotent", first, second, "same base + same bonuses -> same 130");

            // WithBonuses REPLACES the bonus set, so chaining it cannot stack: 14 -> 16, not 18.
            AttributeSnapshot chained = new AttributeSnapshot(warriorBase, cuirass).WithBonuses(cuirass);
            CheckInt("chained bonus set does not stack", 16, chained.Effective.Constitution, "14+2=16 (not 14+2+2=18)");
            CheckInt("chained bonused max health", 130, AttributeRules.Derive(chained, bases).MaxHealth, "100+10*3=130");

            // Unequip = recompute with an empty bonus set, which returns exactly the baseline.
            AttributeSnapshot unequipped = equipped.WithBonuses(AttributeBonuses.None);
            CheckInt("unequip restores base CON", 14, unequipped.Effective.Constitution, "14+0=14");
            CheckInt("unequip restores baseline HP", 120, AttributeRules.Derive(unequipped, bases).MaxHealth, "100+10*2=120");

            // The pristine base is never mutated by bonusing or deriving.
            CheckTrue("base snapshot unchanged after bonus/derive",
                equipped.Base.Constitution == 14 && equipped.Base.Strength == 16 &&
                equipped.Base.Dexterity == 10 && equipped.Base.Intelligence == 8 &&
                equipped.Base.Wisdom == 10 && equipped.Base.Charisma == 10 &&
                warriorBase.Constitution == 14,
                "base stays 16/10/14/8/10/10 while effective is 16/10/16/8/10/10");
            CheckInt("effective differs from base while equipped", 16, equipped.Effective.Constitution, "effective 16 vs base 14");

            // Clamping happens once, on the sum, against the pristine base.
            AttributeScores highBase = new AttributeScores(16, 10, 30, 8, 10, 10);
            AttributeSnapshot highEquipped = new AttributeSnapshot(highBase, new AttributeBonuses(0, 0, 3, 0, 0, 0));
            CheckInt("high CON clamps to 30", 30, highEquipped.Effective.Constitution, "30+3=33 -> 30");
            // CON 30 -> 10 -> 100 + 10*10 = 200
            CheckInt("max health at CON 30", 200, AttributeRules.Derive(highEquipped, bases).MaxHealth, "100+10*10=200");

            AttributeScores lowBase = new AttributeScores(16, 10, 1, 8, 10, 10);
            AttributeSnapshot lowEquipped = new AttributeSnapshot(lowBase, new AttributeBonuses(0, 0, -3, 0, 0, 0));
            CheckInt("low CON clamps to 1", 1, lowEquipped.Effective.Constitution, "1-3=-2 -> 1");
            // CON 1 -> -5 -> 100 - 50 = 50 (above the 25 floor).
            CheckInt("max health at CON 1", 50, AttributeRules.Derive(lowEquipped, bases).MaxHealth, "100+10*(-5)=50");

            // Bonus totals are per attribute and sum across sets.
            AttributeBonuses a = new AttributeBonuses(1, 2, 3, 4, 5, 6);
            AttributeBonuses b = new AttributeBonuses(2, 3, 4, 5, 6, 7);
            AttributeBonuses ab = a.Plus(b);
            CheckInt("sum strength", 3, ab.Total(SixAttribute.Strength), "1+2=3");
            CheckInt("sum dexterity", 5, ab.Total(SixAttribute.Dexterity), "2+3=5");
            CheckInt("sum constitution", 7, ab.Total(SixAttribute.Constitution), "3+4=7");
            CheckInt("sum intelligence", 9, ab.Total(SixAttribute.Intelligence), "4+5=9");
            CheckInt("sum wisdom", 11, ab.Total(SixAttribute.Wisdom), "5+6=11");
            CheckInt("sum charisma", 13, ab.Total(SixAttribute.Charisma), "6+7=13");
            CheckTrue("empty bonus set is empty", AttributeBonuses.None.IsEmpty, "all six totals are 0");
            CheckTrue("non-empty bonus set is not empty", !cuirass.IsEmpty, "CON total 2");
        }

        /// <summary>Capacity changes never hand out free healing/refill.</summary>
        private static void CheckCurrentPoolClamping()
        {
            // Growing the maximum must not raise the current value.
            CheckInt("raising max does not refill", 120, AttributeRules.ClampCurrentToMax(120, 130), "120 <= 130 stays 120");
            // Shrinking the maximum clamps the current value down.
            CheckInt("lowering max clamps current", 100, AttributeRules.ClampCurrentToMax(120, 100), "120 > 100 -> 100");
            // A pool already below the new maximum is untouched.
            CheckInt("already-below pool untouched", 30, AttributeRules.ClampCurrentToMax(30, 100), "30 <= 100 stays 30");
            CheckClose("float pool lowering", 70f, AttributeRules.ClampCurrentToMax(75f, 70f), "75 > 70 -> 70");
            CheckClose("float pool raising", 70f, AttributeRules.ClampCurrentToMax(70f, 75f), "70 <= 75 stays 70");
        }

        private static void CheckAllowlistedKeys()
        {
            SixAttribute parsed;
            CheckTrue("key constitution parses", AttributeRules.TryParseKey("constitution", out parsed) && parsed == SixAttribute.Constitution, "canonical key");
            CheckTrue("key Dexterity parses case-insensitively", AttributeRules.TryParseKey("Dexterity", out parsed) && parsed == SixAttribute.Dexterity, "allowlisted");
            CheckTrue("key charisma parses", AttributeRules.TryParseKey(" charisma ", out parsed) && parsed == SixAttribute.Charisma, "trimmed");
            CheckFalse("key armor is not an attribute", AttributeRules.TryParseKey("armor", out parsed), "not one of the six scores");
            CheckFalse("empty key rejected", AttributeRules.TryParseKey("", out parsed), "empty");
            CheckFalse("null key rejected", AttributeRules.TryParseKey(null, out parsed), "null");
            CheckTrue("canonical key strings", AttributeRules.Key(SixAttribute.Strength) == "strength" &&
                AttributeRules.Key(SixAttribute.Dexterity) == "dexterity" &&
                AttributeRules.Key(SixAttribute.Constitution) == "constitution" &&
                AttributeRules.Key(SixAttribute.Intelligence) == "intelligence" &&
                AttributeRules.Key(SixAttribute.Wisdom) == "wisdom" &&
                AttributeRules.Key(SixAttribute.Charisma) == "charisma", "lowercase allowlist keys");
        }

        private static void CheckInt(string label, int expected, int actual, string derivation)
        {
            _checks++;
            if (expected == actual) return;
            ReportMismatch(label, expected.ToString(), actual.ToString(), derivation);
        }

        private static void CheckClose(string label, float expected, float actual, string derivation, float tolerance = Tolerance)
        {
            _checks++;
            if (Math.Abs(expected - actual) <= tolerance) return;
            ReportMismatch(label, expected.ToString("0.####"), actual.ToString("0.####"), derivation);
        }

        private static void CheckTrue(string label, bool condition, string derivation)
        {
            _checks++;
            if (condition) return;
            ReportMismatch(label, "true", "false", derivation);
        }

        private static void CheckFalse(string label, bool condition, string derivation)
        {
            _checks++;
            if (!condition) return;
            ReportMismatch(label, "false", "true", derivation);
        }

        private static void ReportMismatch(string label, string expected, string actual, string derivation)
        {
            _failures++;
            Debug.LogError(Tag + " mismatch #" + _failures + " " + label +
                           " expected=" + expected + " actual=" + actual + " derivation=" + derivation);
        }
    }
}
