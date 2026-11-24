using Emberdelve.Data;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Combat timing ported from the decoded Dark and Darker reference (user request 24 Nov 2026).
    /// Facts and their source paths: Docs/Tasks/DARKER_COMBAT_REFERENCE_20261124.md. Mapping and the values that are
    /// our own inference: Docs/Tasks/DARKER_COMBAT_PORT_20261124.md.
    ///
    /// Mapping of one reference attack montage to a swing: windup = start .. Collision.On, active = Collision.On .. Off,
    /// recovery = Collision.Off .. Attack.Transition (the moment a queued next attack starts). The last attack of a chain has
    /// no transition event: its recovery runs to the montage's blend-out (length - blend out time), which is an inference.
    /// The combo window is ComboWindow.Close - Collision.Off of the FIRST step (the weapon has one window value).
    ///
    /// It runs last in the weapon part of <see cref="EmberdelveAssetLibrary.BuildAll"/> and from its own menu entry, loading
    /// the assets by path, so the port can be applied without rebuilding unrelated data.
    /// </summary>
    internal static class EmberdelveDarkerCombat
    {
        private const float WallRecoil = 0.70f;   // reference: play rate -0.1 for 0.1 s, then a 0.6 s blend out

        [MenuItem("Emberdelve/Data/Apply Dark and Darker Combat Timing")]
        public static void ApplyFromMenu()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("[Emberdelve][DarkerCombat] Stop Play mode first.");
                return;
            }

            Apply();
            AssetDatabase.SaveAssets();
            Debug.Log("[Emberdelve][DarkerCombat] applied the decoded combat timing to weapon and pattern data");
        }

        public static void Apply()
        {
            SwingPatternData slashRL = Pattern("Pattern_SwordSlashRL");
            SwingPatternData slashLR = Pattern("Pattern_SwordSlashLR");
            SwingPatternData overhead = Pattern("Pattern_SwordOverhead");
            SwingPatternData riposteCut = Pattern("Pattern_SwordRiposteCut");
            SwingPatternData armingThrust = Pattern("Pattern_ArmingThrust");
            SwingPatternData rapierThrust = Pattern("Pattern_RapierThrust");
            SwingPatternData greatLR = Pattern("Pattern_GreatSlashLR");
            SwingPatternData greatRL = Pattern("Pattern_GreatSlashRL");
            SwingPatternData greatOverhead = Pattern("Pattern_GreatOverhead");
            SwingPatternData greatRiposte = Pattern("Pattern_GreatRiposteCut");

            // Arming sword (reference ArmingSword): the shared slash patterns carry its exact step timing.
            Time(slashRL, 0.399f, 0.204f, 0.234f, 0.875f, 0.75f);        // attack 1: hit 0.399-0.603, transition 0.837
            Time(slashLR, 0.400f, 0.157f, 0.376f, 0.875f, 0.75f);        // attack 2: hit 0.400-0.557, transition 0.933
            Time(armingThrust, 0.600f, 0.154f, 0.913f, 0.875f, 0.75f);   // attack 3: hit 0.600-0.754, blend out at 1.667
            Time(riposteCut, 0.431f, 0.205f, 0.299f, 0.85f, 0.85f);      // riposte: hit 0.431-0.636, input again at 0.935
            // Short sword attack 3 (the only user of the one-handed overhead in a chain).
            Time(overhead, 0.615f, 0.172f, 0.426f, 0.95f, 0.90f);        // hit 0.615-0.787, input again at 1.213

            WeaponData arming = Weapon("Weapon_ArmingSword");
            if (arming != null && Has(arming, 5))
            {
                // slash / slash / thrust at 1.00 / 1.05 / 1.10: the reference chain has three attacks, the last one pierces
                arming.comboSteps = new[]
                {
                    new ComboStep(0, 1.00f, comboWindow: 0.527f),   // 1.130 - 0.603
                    new ComboStep(1, 1.05f, comboWindow: 0.703f),   // 1.260 - 0.557
                    new ComboStep(3, 1.10f)
                };
                arming.comboWindow = 0.53f;              // default for a step without its own window
                arming.comboFinisherRecovery = 0f;       // the thrust pattern already holds the full recovery
                arming.cooldown = 0.84f;                 // the first step's total; the floor 0.8 x cooldown stays below every step
                arming.heldMoveSpeedPenalty = 20f;       // reference -20 on the 300 base
                arming.riposteSwingPatterns = new[] { 4 };   // one riposte attack in the reference
                Seed(arming, slashRL);
                EditorUtility.SetDirty(arming);
            }

            WeaponData sword = Weapon("Weapon_AshenShortSword");
            if (sword != null && Has(sword, 4))
            {
                // Reference ShortSword. The slash patterns are shared with the arming sword, so the steps scale them.
                sword.comboSteps = new[]
                {
                    Step(0, 1.00f, slashRL, 0.513f, 0.120f, 0.100f, 0.357f),   // hit 0.513-0.633, transition 0.733, close 0.990
                    Step(1, 1.05f, slashLR, 0.500f, 0.133f, 0.200f, 0.492f),   // hit 0.500-0.633, transition 0.833, close 1.125
                    new ComboStep(2, 1.10f)
                };
                sword.comboWindow = 0.36f;               // default for a step without its own window
                sword.comboFinisherRecovery = 0f;
                sword.cooldown = 0.73f;
                sword.heldMoveSpeedPenalty = 10f;
                Seed(sword, slashRL);
                EditorUtility.SetDirty(sword);
            }

            WeaponData rapier = Weapon("Weapon_EmberRapier");
            if (rapier != null && Has(rapier, 1))
            {
                // Reference Rapier: four thrusts at 1.00 / 1.05 / 1.10 / 1.15. One pattern, scaled per step.
                Time(rapierThrust, 0.433f, 0.069f, 0.180f, 0.875f, 0.75f);
                rapier.comboSteps = new[]
                {
                    Step(0, 1.00f, rapierThrust, 0.433f, 0.069f, 0.112f, 0.327f),   // hit 0.433-0.502, transition 0.614, close 0.829
                    Step(0, 1.05f, rapierThrust, 0.669f, 0.079f, 0.180f, 0.392f),   // hit 0.669-0.748, transition 0.928, close 1.140
                    Step(0, 1.10f, rapierThrust, 0.651f, 0.080f, 0.240f, 0.509f),   // hit 0.651-0.731, transition 0.971, close 1.240
                    Step(0, 1.15f, rapierThrust, 0.596f, 0.083f, 0.180f, 0f)        // hit 0.596-0.679; the rest is the finisher recovery
                };
                rapier.comboFinisherRecovery = 0.258f;   // attack 4 ends at 1.367 - 0.25 (default blend out) = 1.117
                rapier.comboWindow = 0.33f;              // default for a step without its own window
                rapier.cooldown = 0.61f;
                rapier.heldMoveSpeedPenalty = 17f;
                Seed(rapier, rapierThrust);
                EditorUtility.SetDirty(rapier);
            }

            WeaponData great = Weapon("Weapon_TwoHandedSword");
            if (great != null && Has(great, 5))
            {
                // Reference Longsword (two-handed, ripostes). Move multipliers are the zweihander's: the longsword rows did not decode.
                Time(greatLR, 0.932f, 0.229f, 0.283f, 0.825f, 0.65f);        // attack 1: hit 0.932-1.161, transition 1.444
                Time(greatRL, 0.679f, 0.234f, 0.054f, 0.825f, 0.65f);        // attack 2: hit 0.679-0.913, transition 0.967
                Time(greatOverhead, 0.757f, 0.233f, 0.727f, 0.825f, 0.65f);  // attack 3: hit 0.757-0.990, blend out at 1.717
                Time(greatRiposte, 0.333f, 0.167f, 0.333f, 0.85f, 0.85f);    // riposte 1: hit 0.333-0.500, transition 0.833
                great.comboSteps = new[]
                {
                    new ComboStep(0, 1.00f, comboWindow: 0.733f),   // 1.894 - 1.161
                    new ComboStep(1, 1.05f, comboWindow: 0.392f),   // 1.305 - 0.913
                    new ComboStep(2, 1.10f)
                };
                great.comboWindow = 0.39f;               // default for a step without its own window
                great.comboFinisherRecovery = 0f;
                great.cooldown = 0.97f;
                great.heldMoveSpeedPenalty = 40f;
                great.riposteSwingPatterns = new[] { 4 };
                Seed(great, greatLR);
                EditorUtility.SetDirty(great);
            }

            WeaponData bow = Weapon("Weapon_HollowBow");
            if (bow != null)
            {
                // Reference RecurveBow.
                bow.drawTime = 1.5f;                     // draw section of AM_RecurveBow_Stand_Attack
                bow.magazineSize = 8;                    // MaxAmmoCount
                bow.reloadTime = 2.5f;                   // Event.Special.Reload inside the 2.967 s reload montage
                bow.projectileGravity = 9.6f;            // ProjectileGravityScale 0.98 of 9.8 m/s^2
                EditorUtility.SetDirty(bow);
            }

            WeaponData crossbow = Weapon("Weapon_EmberCrossbow");
            if (crossbow != null)
            {
                // Reference Crossbow: one bolt, reload event at 2.673 s, the shot montage is 0.8 s.
                crossbow.magazineSize = 1;
                crossbow.reloadTime = 2.673f;
                crossbow.cooldown = 0.8f;
                EditorUtility.SetDirty(crossbow);
            }
        }

        /// <summary>The weapon's legacy timing fields (authoring seed, not read by the swept melee) follow its first pattern.</summary>
        private static void Seed(WeaponData weapon, SwingPatternData first)
        {
            if (first == null) return;
            weapon.windupTime = first.windupTime;
            weapon.activeWindow = first.activeTime;
            weapon.recoveryTime = first.recoveryTime;
        }

        private static bool Has(WeaponData weapon, int patterns) =>
            weapon.swingPatterns != null && weapon.swingPatterns.Length >= patterns;

        /// <summary>A chain step whose resolved timing is exactly the wanted one on a shared pattern.</summary>
        private static ComboStep Step(int patternIndex, float damage, SwingPatternData pattern, float windup, float active, float recovery,
            float comboWindow)
        {
            if (pattern == null) return new ComboStep(patternIndex, damage, comboWindow: comboWindow);
            return new ComboStep(patternIndex, damage,
                pattern.windupTime > 0f ? windup / pattern.windupTime : 1f,
                pattern.recoveryTime > 0f ? recovery / pattern.recoveryTime : 1f,
                pattern.activeTime > 0f ? active / pattern.activeTime : 1f,
                comboWindow);
        }

        private static void Time(SwingPatternData p, float windup, float active, float recovery, float prepareMove, float hitMove)
        {
            if (p == null) return;
            p.windupTime = windup;
            p.activeTime = active;
            p.recoveryTime = recovery;
            p.wallRecoilTime = WallRecoil;
            p.windupMoveMultiplier = prepareMove;
            p.activeMoveMultiplier = hitMove;
            p.recoveryMoveMultiplier = prepareMove;
            EditorUtility.SetDirty(p);
        }

        private static SwingPatternData Pattern(string name) =>
            AssetDatabase.LoadAssetAtPath<SwingPatternData>(EmberdelvePaths.Data + "/" + name + ".asset");

        private static WeaponData Weapon(string name) =>
            AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/" + name + ".asset");
    }
}
