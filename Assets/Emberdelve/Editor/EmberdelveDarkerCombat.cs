using Emberdelve.Data;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Combat timing and attack directions ported from the decoded Dark and Darker reference (user requests 24 Nov 2026).
    /// Facts and their source paths: Docs/Tasks/DARKER_COMBAT_REFERENCE_20261124.md. Mapping and the values that are
    /// our own inference: Docs/Tasks/DARKER_COMBAT_PORT_20261124.md.
    ///
    /// Timing of one reference attack montage: windup = start .. Collision.On, active = Collision.On .. Off,
    /// recovery = Collision.Off .. Attack.Transition (the moment a queued next attack starts). The last attack of a chain has
    /// no transition event: its recovery runs to the montage's blend-out (length - blend out time), which is an inference.
    /// The combo window of a step is ComboWindow.Close - Collision.Off.
    ///
    /// Direction of one attack: the ability's ChangeCrosshairAngle (degrees). Read as the travel direction of the blade
    /// across the view: 0 = right to left, -90 = straight down, 180 = left to right (the sign convention is an inference,
    /// see the port report). An ability without the value is a thrust. The swing paths themselves are ours: a plain arc
    /// around the hand, turned to that direction. No reference animation data is used.
    ///
    /// It runs last in the weapon part of <see cref="EmberdelveAssetLibrary.BuildAll"/> and from its own menu entry, loading
    /// the assets by path, so the port can be applied without rebuilding unrelated data.
    /// </summary>
    internal static class EmberdelveDarkerCombat
    {
        private const float WallRecoil = 0.70f;   // reference: play rate -0.1 for 0.1 s, then a 0.6 s blend out

        // The one-handed arc of the project (EmberdelveAssetLibrary.ArcKeys) and the two-handed one (WideArcKeys).
        private const float ArcRadius = 0.85f, ArcHalfAngle = 55f;
        private const float WideArcRadius = 0.95f, WideArcHalfAngle = 70f;
        private static readonly Vector3 ArcPivot = new Vector3(0.2f, -0.3f, 0.1f);

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
            Debug.Log("[Emberdelve][DarkerCombat] applied the decoded combat timing and attack directions to weapon and pattern data");
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
            SwingPatternData greatThrust = Pattern("Pattern_GreatThrust");
            SwingPatternData greatRiposte = Pattern("Pattern_GreatRiposteCut");
            if (slashRL == null || greatRL == null) return;

            // The shared library patterns keep their shapes; they only carry the reference arming-sword timing as before.
            // The player weapons below swing their own directed cuts.
            Time(slashRL, 0.399f, 0.204f, 0.234f, 0.875f, 0.75f);
            Time(slashLR, 0.400f, 0.157f, 0.376f, 0.875f, 0.75f);
            Time(riposteCut, 0.431f, 0.205f, 0.299f, 0.85f, 0.85f);
            Time(overhead, 0.615f, 0.172f, 0.426f, 0.95f, 0.90f);

            WeaponData arming = Weapon("Weapon_ArmingSword");
            if (arming != null)
            {
                // Reference ArmingSword: -60 (down to the left), 230 (down to the right), thrust; riposte -115.
                SwingPatternData cut1 = Cut(arming, "Pattern_ArmingCut1", "arming_cut_1", slashRL, -60f, false, 4, 0.399f, 0.204f, 0.234f, 0.875f, 0.75f);   // hit 0.399-0.603, transition 0.837
                SwingPatternData cut2 = Cut(arming, "Pattern_ArmingCut2", "arming_cut_2", slashRL, 230f, false, 4, 0.400f, 0.157f, 0.376f, 0.875f, 0.75f);   // hit 0.400-0.557, transition 0.933
                Time(armingThrust, 0.600f, 0.154f, 0.913f, 0.875f, 0.75f);                                                                         // hit 0.600-0.754, blend out at 1.667
                SwingPatternData riposte = Cut(arming, "Pattern_ArmingRiposte", "arming_riposte", slashRL, -115f, false, 3, 0.431f, 0.205f, 0.299f, 0.85f, 0.85f);   // hit 0.431-0.636, input again at 0.935
                Assign(arming, cut1, cut2, overhead, armingThrust, riposte);
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
                Seed(arming, cut1);
                EditorUtility.SetDirty(arming);
            }

            WeaponData sword = Weapon("Weapon_AshenShortSword");
            if (sword != null)
            {
                // Reference ShortSword: -122 (down to the right), 375 (to the left, slightly rising), 340 (to the left, slightly falling);
                // ripostes 340 and 180 (flat, left to right).
                SwingPatternData cut1 = Cut(sword, "Pattern_ShortCut1", "short_cut_1", slashRL, -122f, false, 4, 0.513f, 0.120f, 0.100f, 0.95f, 0.90f);   // hit 0.513-0.633, transition 0.733
                SwingPatternData cut2 = Cut(sword, "Pattern_ShortCut2", "short_cut_2", slashRL, 375f, false, 4, 0.500f, 0.133f, 0.200f, 0.95f, 0.90f);    // hit 0.500-0.633, transition 0.833
                SwingPatternData cut3 = Cut(sword, "Pattern_ShortCut3", "short_cut_3", slashRL, 340f, false, 4, 0.615f, 0.172f, 0.426f, 0.95f, 0.90f);    // hit 0.615-0.787, input again at 1.213
                // OUR OWN CHOICE, not the reference (user, 25 Nov 2026): the chain ends with a thrust. It keeps the reference timing of
                // the third attack; the reference cut (340) stays in the list outside the chain (index 6).
                SwingPatternData thrust3 = Thrust("Pattern_ShortThrust", "short_thrust", armingThrust, 2, 0.615f, 0.172f, 0.426f, 0.95f, 0.90f);
                SwingPatternData riposte1 = Cut(sword, "Pattern_ShortRiposte1", "short_riposte_1", slashRL, 340f, false, 3, 0.385f, 0.118f, 0.131f, 0.85f, 0.85f);   // hit 0.385-0.503, transition 0.634
                SwingPatternData riposte2 = Cut(sword, "Pattern_ShortRiposte2", "short_riposte_2", slashRL, 180f, false, 3, 0.382f, 0.179f, 0.192f, 0.85f, 0.85f);   // hit 0.382-0.561, block again at 0.753
                // The library overhead stays in the list (index 5, outside the chain): content and gates that look a pattern up by kind still find one.
                Assign(sword, cut1, cut2, thrust3, riposte1, riposte2, overhead, cut3);
                sword.comboSteps = new[]
                {
                    new ComboStep(0, 1.00f, comboWindow: 0.357f),   // 0.990 - 0.633
                    new ComboStep(1, 1.05f, comboWindow: 0.492f),   // 1.125 - 0.633
                    new ComboStep(2, 1.10f)
                };
                sword.comboWindow = 0.36f;               // default for a step without its own window
                sword.comboFinisherRecovery = 0f;
                sword.cooldown = 0.73f;
                sword.heldMoveSpeedPenalty = 10f;
                sword.riposteSwingPatterns = new[] { 3, 4 };
                Seed(sword, cut1);
                EditorUtility.SetDirty(sword);
            }

            WeaponData rapier = Weapon("Weapon_EmberRapier");
            if (rapier != null && rapierThrust != null)
            {
                // Reference Rapier: thrust, -125 (down to the right), -90 (straight down), 180 (flat, left to right); ripostes -65 and a thrust.
                Time(rapierThrust, 0.433f, 0.069f, 0.112f, 0.875f, 0.75f);                                                                         // hit 0.433-0.502, transition 0.614
                SwingPatternData cut2 = Cut(rapier, "Pattern_RapierCut2", "rapier_cut_2", slashRL, -125f, false, 2, 0.669f, 0.079f, 0.180f, 0.875f, 0.75f);   // hit 0.669-0.748, transition 0.928
                SwingPatternData cut3 = Cut(rapier, "Pattern_RapierCut3", "rapier_cut_3", slashRL, -90f, false, 2, 0.651f, 0.080f, 0.240f, 0.875f, 0.75f);    // hit 0.651-0.731, transition 0.971
                SwingPatternData cut4 = Cut(rapier, "Pattern_RapierCut4", "rapier_cut_4", slashRL, 180f, false, 2, 0.596f, 0.083f, 0.438f, 0.875f, 0.75f);    // hit 0.596-0.679, ends at 1.367 - 0.25
                SwingPatternData riposte = Cut(rapier, "Pattern_RapierRiposte", "rapier_riposte", slashRL, -65f, false, 2, 0.458f, 0.166f, 0.080f, 0.85f, 0.85f);   // hit 0.458-0.624, transition 0.704
                Assign(rapier, rapierThrust, cut2, cut3, cut4, riposte);
                rapier.comboSteps = new[]
                {
                    new ComboStep(0, 1.00f, comboWindow: 0.327f),   // 0.829 - 0.502
                    new ComboStep(1, 1.05f, comboWindow: 0.392f),   // 1.140 - 0.748
                    new ComboStep(2, 1.10f, comboWindow: 0.509f),   // 1.240 - 0.731
                    new ComboStep(3, 1.15f)
                };
                rapier.comboFinisherRecovery = 0f;       // the last cut's pattern holds its full recovery
                rapier.comboWindow = 0.33f;              // default for a step without its own window
                rapier.cooldown = 0.61f;
                rapier.heldMoveSpeedPenalty = 17f;
                rapier.riposteSwingPatterns = new[] { 4, 0 };   // the -65 cut, then a thrust
                Seed(rapier, rapierThrust);
                EditorUtility.SetDirty(rapier);
            }

            WeaponData great = Weapon("Weapon_TwoHandedSword");
            if (great != null && greatLR != null && greatOverhead != null && greatThrust != null && greatRiposte != null)
            {
                // Reference Longsword (two-handed, ripostes): attack 1 has no direction value, attack 2 is -177 (flat, left to
                // right), attack 3 is -35 (to the left and down). An attack without the value is a thrust by the rule above, and
                // the user asked for a thrust as the opening attack (25 Nov 2026): the chain opens with the thrust, on attack 1's
                // timing. The right-to-left slash keeps the same timing and stays in the list outside the chain (index 1).
                // Move multipliers are the zweihander's: the longsword rows did not decode.
                Time(greatThrust, 0.932f, 0.229f, 0.283f, 0.825f, 0.65f);    // attack 1: hit 0.932-1.161, transition 1.444
                Time(greatRL, 0.932f, 0.229f, 0.283f, 0.825f, 0.65f);
                Time(greatLR, 0.679f, 0.234f, 0.054f, 0.825f, 0.65f);        // attack 2: hit 0.679-0.913, transition 0.967
                SwingPatternData cut3 = Cut(great, "Pattern_GreatCut3", "great_cut_3", greatRL, -35f, true, 3, 0.757f, 0.233f, 0.727f, 0.825f, 0.65f);   // attack 3: hit 0.757-0.990, blend out at 1.717
                Time(greatRiposte, 0.333f, 0.167f, 0.333f, 0.85f, 0.85f);    // riposte 1: hit 0.333-0.500, transition 0.833
                Assign(great, greatLR, greatRL, greatOverhead, greatThrust, greatRiposte, cut3);
                great.comboSteps = new[]
                {
                    new ComboStep(3, 1.00f, comboWindow: 0.733f),   // the thrust; 1.894 - 1.161
                    new ComboStep(0, 1.05f, comboWindow: 0.392f),   // 1.305 - 0.913
                    new ComboStep(5, 1.10f)
                };
                great.comboWindow = 0.39f;               // default for a step without its own window
                great.comboFinisherRecovery = 0f;
                great.cooldown = 0.97f;
                great.heldMoveSpeedPenalty = 40f;
                great.riposteSwingPatterns = new[] { 4 };
                Seed(great, greatThrust);
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
                bow.projectileSpeed = 38f;               // user request 25 Nov 2026: a little slower than 42 (the reference recurve speed is not decoded)
                bow.projectileTrailMaterial = slashRL.trailMaterial;   // the swing trail material: already a project asset in the build
                EditorUtility.SetDirty(bow);
            }

            WeaponData crossbow = Weapon("Weapon_EmberCrossbow");
            if (crossbow != null)
            {
                // Reference Crossbow: one bolt, reload event at 2.673 s, the shot montage is 0.8 s.
                crossbow.magazineSize = 1;
                crossbow.reloadTime = 2.673f;
                crossbow.cooldown = 0.8f;
                crossbow.projectileGravity = 9.6f;       // the same scale as the arrow; the bolt's own value is not decoded
                crossbow.projectileTrailMaterial = slashRL.trailMaterial;
                EditorUtility.SetDirty(crossbow);
            }

            WeaponData boneBow = Weapon("Weapon_BoneBow");
            if (boneBow != null)
            {
                boneBow.projectileTrailMaterial = slashRL.trailMaterial;   // a monster's arrow must be as easy to follow as the player's
                EditorUtility.SetDirty(boneBow);
            }
        }

        /// <summary>
        /// Travel direction of the blade across the view for a reference crosshair angle: x to the right, y up.
        /// 0 = to the left, -90 = down, 180 = to the right.
        /// </summary>
        internal static Vector2 TravelDirection(float referenceAngle)
        {
            float r = referenceAngle * Mathf.Deg2Rad;
            return new Vector2(-Mathf.Cos(r), Mathf.Sin(r));
        }

        /// <summary>
        /// Keys of a plain arc around the hand whose blade travels along <paramref name="referenceAngle"/>: the blade starts
        /// pointing against the travel direction and ends pointing along it, through straight ahead. At angle 0 this is the
        /// library's right-to-left slash.
        /// </summary>
        internal static SwingKey[] DirectedArcKeys(float referenceAngle, float radius, float halfAngle, float bladeLength = 0f, int count = 9)
        {
            Vector2 d = TravelDirection(referenceAngle);
            // The HAND keeps a compact path low in the view (HandRadius around a point in front of the body) and the BLADE does
            // the directed sweep. The first version swung the hand itself on the library's 0.85 m radius: turned to a steep
            // direction that put the fist at arm's length in front of the face, above eye level (review frames, 25 Nov 2026).
            // Straight ahead the hand is where the library arc has it, so the reach of the cut is unchanged.
            Vector3 pivot = ArcPivot + new Vector3(0f, 0f, radius - HandRadius);
            radius = HandRadius;
            // A cut that is not flat is shortened where its tip would leave the space in front of the body: the tip stays
            // within MaxTipRise above and MaxTipDrop below the hand, so a falling cut ends in front of the knees instead of in
            // the floor (a floor contact would stop the swing like a wall) and a rising start stays under a low ceiling.
            float tip = radius + Mathf.Max(0f, bladeLength);
            // A falling cut is drawn back further before it comes down (up to RaisedStartAngle for a vertical one). With the
            // library's 55 degrees the blade of a steep cut already hung over a close target's head when the hit window opened,
            // so nothing could be put in its way (CombatFlow "REAL ... guard catches": no blade pose caught it at 1.1 m).
            float backAngle = d.y < 0f ? Mathf.Lerp(halfAngle, Mathf.Max(halfAngle, RaisedStartAngle), -d.y) : halfAngle;
            float startAngle = -LimitedAngle(backAngle, -d.y, tip);
            float endAngle = LimitedAngle(halfAngle, d.y, tip);
            SwingKey[] keys = new SwingKey[count];
            for (int i = 0; i < count; i++)
            {
                float u = i / (float)(count - 1);
                float s = Mathf.Lerp(startAngle, endAngle, u) * Mathf.Deg2Rad;
                Vector3 v = new Vector3(Mathf.Sin(s) * d.x, Mathf.Sin(s) * d.y, Mathf.Cos(s));
                float yaw = Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;
                float pitch = -Mathf.Asin(Mathf.Clamp(v.y, -1f, 1f)) * Mathf.Rad2Deg;
                keys[i] = new SwingKey(u, pivot + radius * v, yaw, pitch);
            }
            return keys;
        }

        // Rise: dungeon walls are 3.6 m and the eye is about 1.65 m up, so a tip 1.5 m above the hand stays under the ceiling.
        private const float MaxTipRise = 1.5f, MaxTipDrop = 0.65f;
        private const float RaisedStartAngle = 80f;
        private const float HandRadius = 0.35f;

        /// <summary>The arc's half angle on the side where the tip moves by <paramref name="vertical"/> per unit (up positive), capped so the tip stays in range.</summary>
        private static float LimitedAngle(float halfAngle, float vertical, float tipDistance)
        {
            if (tipDistance <= 0f || Mathf.Abs(vertical) < 1e-4f) return halfAngle;
            float limit = (vertical > 0f ? MaxTipRise : MaxTipDrop) / (tipDistance * Mathf.Abs(vertical));
            return limit >= 1f ? halfAngle : Mathf.Min(halfAngle, Mathf.Asin(limit) * Mathf.Rad2Deg);
        }

        /// <summary>
        /// The kind of a directed cut, by the project's own measure of its keys (BladeTrajectories.ClassifyKeys: a path that sweeps more
        /// in pitch than in yaw is an overhead), so the authored kind always agrees with the defence rules. Otherwise its side.
        /// </summary>
        internal static SwingKind KindOf(SwingPatternData pattern, float referenceAngle)
        {
            if (Emberdelve.Combat.BladeTrajectories.ClassifyKeys(pattern) == Emberdelve.BladeDefense.BladeTrajectory.Overhead) return SwingKind.Overhead;
            return TravelDirection(referenceAngle).x < 0f ? SwingKind.HorizontalRightToLeft : SwingKind.HorizontalLeftToRight;
        }

        /// <summary>
        /// Gate fixture: the weapon with every chain and riposte cut replaced by the library's flat shapes (right-to-left slash,
        /// left-to-right slash, overhead, riposte cut) while each pattern keeps its own timing, reach and move factors. Mechanics
        /// scenarios (cleave, walls, shields, zones) are laid out for flat arcs; the shipped directions are checked on the asset.
        /// </summary>
        internal static WeaponData FlatArcFixture(WeaponData real, System.Collections.Generic.List<Object> cleanup = null)
        {
            SwingPatternData[] shapes =
            {
                Pattern("Pattern_SwordSlashRL"), Pattern("Pattern_SwordSlashLR"), Pattern("Pattern_SwordOverhead"), Pattern("Pattern_SwordRiposteCut")
            };
            WeaponData fixture = Object.Instantiate(real);
            fixture.name = real.name + "_FlatArcFixture";
            if (cleanup != null) cleanup.Add(fixture);
            SwingPatternData[] patterns = new SwingPatternData[real.swingPatterns.Length];
            for (int i = 0; i < patterns.Length; i++)
            {
                SwingPatternData p = Object.Instantiate(real.swingPatterns[i]);
                p.name = real.swingPatterns[i].name + "_Flat";
                if (cleanup != null) cleanup.Add(p);
                SwingPatternData shape = i < 3 ? shapes[i] : (i < 5 ? shapes[3] : null);
                if (shape != null)
                {
                    if (p.kind == SwingKind.Thrust)
                    {
                        // A thrust in the chain (the short sword's third attack) has a thrust's reach numbers; the flat arc that
                        // stands in for it swings with the arc's own, as the scenarios were laid out.
                        p.reachScale = shape.reachScale;
                        p.reachInset = shape.reachInset;
                        p.sweepRadius = shape.sweepRadius;
                        p.contactWindow = shape.contactWindow;
                    }
                    p.keys = shape.keys;
                    p.kind = shape.kind;
                    p.impactPowerScale = shape.impactPowerScale;
                    p.maxTargets = shape.maxTargets;
                }
                patterns[i] = p;
            }
            fixture.swingPatterns = patterns;
            return fixture;
        }

        /// <summary>A weapon's own directed cut: reach, radius and presentation come from the template slash, the path and timing are set here.</summary>
        private static SwingPatternData Cut(WeaponData weapon, string assetName, string patternId, SwingPatternData template, float referenceAngle, bool wide,
            int maxTargets, float windup, float active, float recovery, float prepareMove, float hitMove)
        {
            string path = EmberdelvePaths.Data + "/" + assetName + ".asset";
            SwingPatternData p = AssetDatabase.LoadAssetAtPath<SwingPatternData>(path);
            if (p == null)
            {
                p = Object.Instantiate(template);
                p.name = assetName;
                AssetDatabase.CreateAsset(p, path);
            }

            p.patternId = patternId;
            p.reachScale = template.reachScale;
            p.reachInset = template.reachInset;
            p.sweepRadius = template.sweepRadius;
            p.contactWindow = template.contactWindow;
            p.damageMultiplier = 1f;
            p.maxTargets = maxTargets;
            p.restBase = template.restBase;
            p.restYaw = template.restYaw;
            p.restPitch = template.restPitch;
            p.restRoll = template.restRoll;
            p.trailMaterial = template.trailMaterial;
            float blade = p.BladeLength(weapon);
            p.keys = wide ? DirectedArcKeys(referenceAngle, WideArcRadius, WideArcHalfAngle, blade) : DirectedArcKeys(referenceAngle, ArcRadius, ArcHalfAngle, blade);
            p.kind = KindOf(p, referenceAngle);
            p.impactPowerScale = p.kind == SwingKind.Overhead ? 1.2f : 1f;   // the library's rule for an overhead
            Time(p, windup, active, recovery, prepareMove, hitMove);
            return p;
        }

        /// <summary>A thrust of a weapon's own: the template thrust's shape under its own id and timing.</summary>
        private static SwingPatternData Thrust(string assetName, string patternId, SwingPatternData template, int maxTargets,
            float windup, float active, float recovery, float prepareMove, float hitMove)
        {
            if (template == null) return null;
            string path = EmberdelvePaths.Data + "/" + assetName + ".asset";
            SwingPatternData p = AssetDatabase.LoadAssetAtPath<SwingPatternData>(path);
            if (p == null)
            {
                p = Object.Instantiate(template);
                p.name = assetName;
                AssetDatabase.CreateAsset(p, path);
            }

            p.patternId = patternId;
            p.kind = template.kind;
            p.reachScale = template.reachScale;
            p.reachInset = template.reachInset;
            p.sweepRadius = template.sweepRadius;
            p.contactWindow = template.contactWindow;
            p.damageMultiplier = 1f;
            p.impactPowerScale = 1f;
            p.maxTargets = maxTargets;
            p.restBase = template.restBase;
            p.restYaw = template.restYaw;
            p.restPitch = template.restPitch;
            p.restRoll = template.restRoll;
            p.trailMaterial = template.trailMaterial;
            p.keys = (SwingKey[])template.keys.Clone();
            Time(p, windup, active, recovery, prepareMove, hitMove);
            return p;
        }

        private static void Assign(WeaponData weapon, params SwingPatternData[] patterns)
        {
            for (int i = 0; i < patterns.Length; i++) if (patterns[i] == null) return;
            weapon.swingPatterns = patterns;
        }

        /// <summary>The weapon's legacy timing fields (authoring seed, not read by the swept melee) follow its first pattern.</summary>
        private static void Seed(WeaponData weapon, SwingPatternData first)
        {
            if (first == null) return;
            weapon.windupTime = first.windupTime;
            weapon.activeWindow = first.activeTime;
            weapon.recoveryTime = first.recoveryTime;
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
