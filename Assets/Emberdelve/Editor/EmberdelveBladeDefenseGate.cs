using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Emberdelve.BladeDefense;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// U3C1 gate: committed blade trajectory and directional height / endurance defense. Offline and synchronous (no play mode): it loads the
    /// authored weapon, pattern, class and tuning assets and drives the shared pure code the authority, the rig and the wire use. Every expected
    /// value is derived from that data (pattern keys, tuning, kit numbers), never typed in. A missing asset fails a check instead of throwing.
    ///
    /// Run: Unity -batchmode -nographics -projectPath . -logFile Logs/u3c_bladedefense.log
    ///      -executeMethod Emberdelve.EditorTools.EmberdelveBladeDefenseGate.Run
    /// Last line: [Emberdelve][BladeDefense] PASS|FAIL checks=N failures=M
    /// The live rig (height, back, endurance) is checked by EmberdelveCombatFlowGate (section "U3C rig").
    /// </summary>
    public static class EmberdelveBladeDefenseGate
    {
        private const string Tag = "[Emberdelve][BladeDefense]";
        private const float BodyRadius = 0.3f;

        private static int _checks;
        private static int _failures;

        private sealed class Case
        {
            public string Name;
            public SwingPatternData Pattern;
            public WeaponData Weapon;
        }

        private static void Check(bool ok, string label)
        {
            _checks++;
            if (!ok)
            {
                _failures++;
                Debug.LogError(Tag + " FAIL " + label);
            }
        }

        private static void Near(float expected, float actual, float tolerance, string label)
        {
            Check(Mathf.Abs(expected - actual) <= tolerance, label + " (expected " + expected.ToString("F4", CultureInfo.InvariantCulture) + ", got " + actual.ToString("F4", CultureInfo.InvariantCulture) + ")");
        }

        private static T Load<T>(string name) where T : UnityEngine.Object
        {
            return AssetDatabase.LoadAssetAtPath<T>(EmberdelvePaths.Data + "/" + name + ".asset");
        }

        public static void Run()
        {
            _checks = 0;
            _failures = 0;
            Section("data", DataSection);
            Section("warning equals sweep", WarningEqualsSweep);
            Section("evasion", EvasionSection);
            Section("front, back, height", FrontBackHeight);
            Section("endurance (empty pool)", EnduranceSection);
            Section("damage through the resolver", ResolverSection);
            Section("shields never parry", ShieldSection);
            Section("wire", WireSection);
            Debug.Log(Tag + " " + (_failures == 0 ? "PASS" : "FAIL") + " checks=" + _checks + " failures=" + _failures);
            EmberdelveGateExit.Exit(_failures == 0 ? 0 : 1);
        }

        private static void Section(string name, Action body)
        {
            int checksBefore = _checks;
            int failuresBefore = _failures;
            try { body(); }
            catch (Exception e)
            {
                _failures++;
                Debug.LogError(Tag + " FAIL section '" + name + "' threw");
                Debug.LogException(e);
            }

            Debug.Log(Tag + " section '" + name + "': " + (_checks - checksBefore) + " checks, " + (_failures - failuresBefore) + " failures");
        }

        // ---------------------------------------------------------------------------------------------
        // shared data
        // ---------------------------------------------------------------------------------------------

        private static WeaponData _sword;
        private static WeaponData _rapier;
        private static WeaponData _maul;
        private static WeaponData _claw;
        private static WeaponData _shield;
        private static CharacterClassData _warrior;
        private static CombatFlowTuning _flow;
        private static readonly List<Case> Cases = new List<Case>();

        private static bool LoadData()
        {
            Cases.Clear();
            _sword = Load<WeaponData>("Weapon_AshenShortSword");
            _rapier = Load<WeaponData>("Weapon_EmberRapier");
            _maul = Load<WeaponData>("Weapon_WardenMaul");
            _claw = Load<WeaponData>("Weapon_HollowClaw");
            _shield = Load<WeaponData>("Weapon_EmberbulwarkShield");
            _warrior = Load<CharacterClassData>("Class_Warrior");
            GameConfig config = Load<GameConfig>("EmberdelveGameConfig");
            _flow = config != null && config.flow != null ? config.flow : CombatFlowTuning.Default;

            AddCases(_sword);
            AddCases(_rapier);
            AddCases(_maul);
            AddCases(_claw);
            return _sword != null && _rapier != null && _maul != null && _claw != null && _shield != null && _warrior != null && Cases.Count > 0;
        }

        private static void AddCases(WeaponData weapon)
        {
            if (weapon == null || weapon.swingPatterns == null) return;
            for (int i = 0; i < weapon.swingPatterns.Length; i++)
            {
                SwingPatternData pattern = weapon.swingPatterns[i];
                if (pattern == null) continue;
                Cases.Add(new Case { Name = weapon.weaponId + "/" + pattern.patternId, Pattern = pattern, Weapon = weapon });
            }
        }

        private static int GridFor(Case c) =>
            SwingGeometry.SampleCount(c.Pattern, c.Weapon, _flow.GridSwingTurnCeiling, c.Pattern.activeTime);

        private static readonly Vector3 TestEye = new Vector3(3f, 1.8f, -2f);
        private const float TestYaw = 37f;
        private const float TestPitch = -11f;

        private static void EnsureData()
        {
            if (Cases.Count == 0) LoadData();
        }

        // ---------------------------------------------------------------------------------------------
        // 1. data
        // ---------------------------------------------------------------------------------------------

        private static void DataSection()
        {
            bool loaded = LoadData();
            Check(loaded, "weapon, pattern and class assets are available (sword, rapier, maul, claw, shield, warrior)");
            Check(_flow != null, "a combat flow tuning is available");
            if (!loaded) return;

            HashSet<BladeTrajectory> seen = new HashSet<BladeTrajectory>();
            foreach (Case c in Cases)
            {
                Check(c.Pattern.Validate(out string problem), c.Name + " pattern validates (" + problem + ")");
                BladeTrajectory authored = BladeTrajectories.Of(c.Pattern.kind);
                BladeTrajectory measured = BladeTrajectories.ClassifyKeys(c.Pattern);
                Check(authored != BladeTrajectory.None, c.Name + " has an authored trajectory (" + c.Pattern.kind + ")");
                Check(authored == measured, c.Name + ": the authored kind " + authored + " agrees with the class measured from its keys (" + measured + ")");
                seen.Add(authored);
            }

            Check(seen.Contains(BladeTrajectory.Horizontal) && seen.Contains(BladeTrajectory.Overhead) && seen.Contains(BladeTrajectory.Thrust),
                "the authored weapons cover horizontal, overhead and thrust");

            BladeHeight horizontal = _flow.HeightFor(BladeTrajectory.Horizontal);
            BladeHeight overhead = _flow.HeightFor(BladeTrajectory.Overhead);
            BladeHeight thrust = _flow.HeightFor(BladeTrajectory.Thrust);
            Check(horizontal != BladeHeight.Unspecified && overhead != BladeHeight.Unspecified && thrust != BladeHeight.Unspecified,
                "every trajectory has a height band in the tuning");
            Check(overhead != horizontal, "an overhead arrives in a different band than a horizontal slash");

            // the shipped decision (C2): blocking is free unless the tuning says otherwise
            Check(!CombatFlowTuning.Default.GuardEnduranceEnabled, "default tuning: blocking costs no endurance (the D1b / C2 decision)");
            Check(!CombatFlowTuning.Default.commitSwingAim, "default tuning: the sweep keeps following the live aim (D1c2 behaviour)");
        }

        // ---------------------------------------------------------------------------------------------
        // 2. warning == sweep
        // ---------------------------------------------------------------------------------------------

        private static void WarningEqualsSweep()
        {
            EnsureData();
            foreach (Case c in Cases)
            {
                int k = GridFor(c);
                BladeWarning w = BladeWarning.Describe(c.Pattern, c.Weapon, TestEye, TestYaw, TestPitch, _flow, k);
                Check(w.IsValid, c.Name + ": a warning is produced");
                if (!w.IsValid) continue;

                Check(w.SampleCount == k, c.Name + ": the warning uses the sweep's sample grid (" + w.SampleCount + " vs " + k + ")");
                Check(w.Trajectory == BladeTrajectories.Of(c.Pattern.kind), c.Name + ": trajectory comes from the pattern kind");
                Check(w.Height == _flow.HeightFor(w.Trajectory), c.Name + ": height band comes from the tuning table");
                c.Pattern.YawRange(out float minYaw, out float maxYaw);
                Near(minYaw, w.MinBladeYaw, 1e-5f, c.Name + ": warning min yaw is the pattern's (the enemy wedge uses the same numbers)");
                Near(maxYaw, w.MaxBladeYaw, 1e-5f, c.Name + ": warning max yaw is the pattern's");
                Near(c.Pattern.EffectiveReach(c.Weapon), w.Reach, 1e-5f, c.Name + ": warning reach is the pattern's effective reach");
                Near(c.Pattern.BladeLength(c.Weapon), w.BladeLength, 1e-5f, c.Name + ": warning blade length is the pattern's");

                // the segments the sweep builds (the long form the authority used before U3C) against the warning's, sample by sample
                SwingFrame frame = new SwingFrame(TestEye, TestYaw, TestPitch);
                float length = c.Pattern.BladeLength(c.Weapon);
                float worst = 0f;
                for (int s = 0; s <= k; s++)
                {
                    SwingGeometry.BladeSegment(frame, c.Pattern.ActivePose(s / (float)k), length, out Vector3 sweepBase, out Vector3 sweepTip);
                    w.SegmentAt(s, out Vector3 warnBase, out Vector3 warnTip);
                    worst = Mathf.Max(worst, Vector3.Distance(sweepBase, warnBase), Vector3.Distance(sweepTip, warnTip));
                }

                Near(0f, worst, 1e-5f, c.Name + ": every one of the " + (k + 1) + " warned segments is the swept segment");

                // the end samples against the raw authored keys
                SwingKey first = c.Pattern.keys[0];
                SwingKey last = c.Pattern.keys[c.Pattern.keys.Length - 1];
                w.SegmentAt(0, out Vector3 b0, out Vector3 t0);
                w.SegmentAt(k, out Vector3 bk, out Vector3 tk);
                Near(0f, Vector3.Distance(b0, TestEye + frame.Rotation * first.basePos), 1e-4f, c.Name + ": first warned hand position is key 0");
                Near(0f, Vector3.Distance(bk, TestEye + frame.Rotation * last.basePos), 1e-4f, c.Name + ": last warned hand position is the last key");
                Vector3 dir0 = frame.Rotation * (Quaternion.Euler(first.bladePitch, first.bladeYaw, 0f) * Vector3.forward);
                Near(0f, Vector3.Distance(t0, b0 + dir0 * length), 1e-4f, c.Name + ": first warned blade direction is key 0's yaw and pitch");

                // the committed path belongs to the body: a moved attacker moves the whole warning with it (aim kept)
                Vector3 moved = TestEye + new Vector3(1.5f, 0f, 2.5f);
                w.SegmentAt(moved, k / 2, out Vector3 mb, out Vector3 mt);
                w.SegmentAt(k / 2, out Vector3 hb, out Vector3 ht);
                Near(0f, Vector3.Distance(mb - hb, moved - TestEye), 1e-4f, c.Name + ": base follows the body");
                Near(0f, Vector3.Distance(mt - ht, moved - TestEye), 1e-4f, c.Name + ": tip follows the body");

                // trajectory shapes, measured from the keys
                float pitchSpan = w.MaxBladePitch - w.MinBladePitch;
                float yawSpan = w.MaxBladeYaw - w.MinBladeYaw;
                if (w.Trajectory == BladeTrajectory.Overhead) Check(pitchSpan > yawSpan, c.Name + ": an overhead sweeps more in pitch than in yaw (" + pitchSpan + " vs " + yawSpan + ")");
                if (w.Trajectory == BladeTrajectory.Horizontal) Check(yawSpan >= pitchSpan, c.Name + ": a horizontal sweeps at least as much in yaw as in pitch (" + yawSpan + " vs " + pitchSpan + ")");
                if (w.Trajectory == BladeTrajectory.Thrust)
                {
                    Near(0f, yawSpan, 1e-4f, c.Name + ": a thrust keeps its yaw");
                    Near(0f, pitchSpan, 1e-4f, c.Name + ": a thrust keeps its pitch");
                    Near((last.basePos - first.basePos).magnitude, Vector3.Distance(b0, bk), 1e-4f, c.Name + ": the thrust's hand travels the keyed distance");
                }

                // a body ON the committed path is inside the warning and inside the sweep's own contact test
                int mid = k / 2;
                w.SegmentAt(mid, out Vector3 sb, out Vector3 st);
                Vector3 onBlade = Vector3.Lerp(sb, st, 0.85f);
                Check(w.Touches(TestEye, onBlade, onBlade, BodyRadius), c.Name + ": a body on the committed path is inside the warning");
                Check(SwingGeometry.SweepDepth(sb, st, onBlade, onBlade, BodyRadius, c.Pattern.sweepRadius, out _) >= 0f, c.Name + ": ... and the sweep's contact test agrees");
                Check(SwingGeometry.SegmentDistance(sb, st, onBlade, onBlade, out _) <= BodyRadius + c.Pattern.sweepRadius + 1e-4f, c.Name + ": ... by the plain segment distance too");
            }
        }

        // ---------------------------------------------------------------------------------------------
        // 3. evasion: a target that left the swept volume is missed
        // ---------------------------------------------------------------------------------------------

        private static void EvasionSection()
        {
            EnsureData();
            foreach (Case c in Cases)
            {
                int k = GridFor(c);
                BladeWarning w = BladeWarning.Describe(c.Pattern, c.Weapon, TestEye, TestYaw, TestPitch, _flow, k);
                if (!w.IsValid) { Check(false, c.Name + ": warning missing"); continue; }

                // farthest any sampled point of the committed path gets from the eye
                float extent = 0f;
                for (int s = 0; s <= k; s++)
                {
                    w.SegmentAt(s, out Vector3 b, out Vector3 t);
                    extent = Mathf.Max(extent, Vector3.Distance(TestEye, b), Vector3.Distance(TestEye, t));
                }

                w.SegmentAt(k / 2, out Vector3 sb, out Vector3 st);
                Vector3 inside = Vector3.Lerp(sb, st, 0.85f);
                Check(w.Touches(TestEye, inside, inside, BodyRadius), c.Name + ": the target is hit while it stands in the volume");

                // it steps away along the same bearing until it is out of reach of every sample
                Vector3 away = (inside - TestEye).normalized;
                Vector3 gone = TestEye + away * (extent + w.SweepRadius + BodyRadius + 0.05f);
                Check(!w.Touches(TestEye, gone, gone, BodyRadius), c.Name + ": the target that stepped out of the volume is missed (extent " + extent.ToString("F2", CultureInfo.InvariantCulture) + ")");

                // and an attacker whose body walked away takes the volume with it: the old spot is then outside the volume
                Vector3 attackerMoved = TestEye + away * (extent + w.SweepRadius + BodyRadius + 0.05f) * 2f;
                Check(!w.Touches(attackerMoved, inside, inside, BodyRadius), c.Name + ": the volume moved with the attacker: the old spot is empty");

                if (w.Trajectory == BladeTrajectory.Thrust)
                {
                    // a thrust is a line: a target a body-width beside the line is missed, derived from the offset between the sampled lines
                    w.SegmentAt(k, out Vector3 lb, out Vector3 lt);
                    Vector3 lineDir = (lt - lb).normalized;
                    float strip = 0f;
                    for (int s = 0; s <= k; s++)
                    {
                        w.SegmentAt(s, out Vector3 b, out _);
                        strip = Mathf.Max(strip, Vector3.Cross(b - lb, lineDir).magnitude);
                    }

                    Vector3 side = Vector3.Cross(lineDir, Vector3.up).normalized;
                    Vector3 beside = Vector3.Lerp(lb, lt, 0.5f) + side * (BodyRadius + w.SweepRadius + strip + 0.05f);
                    Check(!w.Touches(TestEye, beside, beside, BodyRadius), c.Name + ": a target beside the thrust line is missed (strip " + strip.ToString("F3", CultureInfo.InvariantCulture) + ")");
                    Vector3 onLine = Vector3.Lerp(lb, lt, 0.5f);
                    Check(w.Touches(TestEye, onLine, onLine, BodyRadius), c.Name + ": a target on the thrust line is hit");
                }
            }
        }

        // ---------------------------------------------------------------------------------------------
        // 4. front / back / height with the authored kit
        // ---------------------------------------------------------------------------------------------

        private static GuardInput InputFor(DefenseProfile kit, WeaponData attacker, SwingPatternData pattern, float bearing,
            BladeHeight guardHeight, BladeHeight attackHeight)
        {
            return new GuardInput
            {
                Raised = kit.CanBlock,
                BlockReduction = kit.BlockReduction,
                ArcDegrees = kit.BlockArcDegrees,
                BearingDegrees = bearing,
                HasShield = kit.HasShield,
                GuardHeight = guardHeight,
                AttackHeight = attackHeight,
                HeightTolerance = kit.HasShield ? _flow.shieldHeightTolerance : _flow.bladeHeightTolerance,
                ImpactPower = attacker.impactPower * pattern.impactPowerScale,
                ImpactResist = kit.ImpactResist,
                BreakReductionScale = _flow.guardBreakReductionScale,
                EnduranceEnabled = false,
                EnduranceCurrent = 0f,
                EnduranceCost = 0f
            };
        }

        private static void FrontBackHeight()
        {
            EnsureData();
            if (_sword == null || _warrior == null || _shield == null) { Check(false, "front/back/height: assets missing"); return; }

            DefenseProfile blade = DefenseProfile.Resolve(_warrior, _sword, null);
            DefenseProfile shielded = DefenseProfile.Resolve(_warrior, _sword, _shield);
            Check(blade.CanBlock && shielded.CanBlock, "the warrior can block with the sword and with the shield");
            Check(shielded.HasShield && !blade.HasShield, "the shield kit has a shield, the blade kit does not");

            SwingPatternData slash = _sword.swingPatterns != null && _sword.swingPatterns.Length > 0 ? _sword.swingPatterns[0] : null;
            SwingPatternData over = null;
            if (_sword.swingPatterns != null)
            {
                foreach (SwingPatternData p in _sword.swingPatterns) if (p != null && p.kind == SwingKind.Overhead) over = p;
            }

            Check(slash != null && over != null, "the sword has a horizontal and an overhead pattern");
            if (slash == null || over == null) return;

            BladeHeight slashBand = BladeTrajectories.HeightOf(slash.kind, _flow);
            BladeHeight overBand = BladeTrajectories.HeightOf(over.kind, _flow);

            // product decision (U3C): a sword-vs-sword blade guard HOLDS, a heavy weapon still breaks it. Both derived from the authored data.
            Near(_sword.impactResist, blade.ImpactResist, 1e-5f, "blade: the kit's impact resist is the sword's authored resist");
            foreach (SwingPatternData p in _sword.swingPatterns)
            {
                if (p == null) continue;
                float swordImpact = _sword.impactPower * p.impactPowerScale;
                Check(!BladeGuardRules.IsGuardBroken(swordImpact, blade.ImpactResist),
                    "blade: the sword's " + p.patternId + " (impact " + swordImpact.ToString("F2", CultureInfo.InvariantCulture) + ") does not break the sword's own guard (resist " + blade.ImpactResist.ToString("F2", CultureInfo.InvariantCulture) + ")");
                GuardVerdict swordHit = BladeGuardRules.Resolve(InputFor(blade, _sword, p, 0f, BladeHeight.Mid, BladeTrajectories.HeightOf(p.kind, _flow)));
                Check(swordHit.Mitigated && !swordHit.Broken, "blade: a sword hit by " + p.patternId + " is held, not broken (" + swordHit.Outcome + ")");
                Near(BladeGuardRules.HeldGuardReduction, swordHit.Reduction, 1e-5f, "blade: " + p.patternId + " is held: the whole hit is taken off");
            }

            bool anyHeavy = false;
            foreach (SwingPatternData p in _maul.swingPatterns)
            {
                if (p == null) continue;
                anyHeavy = true;
                float maulImpact = _maul.impactPower * p.impactPowerScale;
                Check(!BladeGuardRules.IsGuardBroken(maulImpact, blade.ImpactResist),
                    "blade: the maul's " + p.patternId + " (impact " + maulImpact.ToString("F2", CultureInfo.InvariantCulture) + ") never breaks the blade guard (resist " + blade.ImpactResist.ToString("F2", CultureInfo.InvariantCulture) + ")");
                GuardVerdict maulHit = BladeGuardRules.Resolve(InputFor(blade, _maul, p, 0f, BladeHeight.Mid, BladeTrajectories.HeightOf(p.kind, _flow)));
                Check(maulHit.Mitigated && !maulHit.Broken, "blade: a maul hit by " + p.patternId + " is held, force never breaks the guard (" + maulHit.Outcome + ")");
                Near(BladeGuardRules.HeldGuardReduction, maulHit.Reduction, 1e-5f, "blade: a heavy hit on a covering blade guard is taken off whole");
            }

            Check(anyHeavy, "blade: the maul has a pattern to test the guard break with");

            foreach (DefenseProfile kit in new[] { blade, shielded })
            {
                string who = kit.HasShield ? "shield" : "blade";
                float half = kit.BlockArcDegrees * 0.5f;

                // front: inside the arc the guard holds, with the kit's own reduction
                GuardVerdict ahead = BladeGuardRules.Resolve(InputFor(kit, _sword, slash, 0f, BladeHeight.Mid, slashBand));
                Check(ahead.Mitigated, who + ": a frontal slash against a mid guard is held (" + ahead.Outcome + ")");
                Near(BladeGuardRules.HeldGuardReduction, ahead.Reduction, 1e-5f, who + ": held, the whole hit is taken off");

                GuardVerdict edge = BladeGuardRules.Resolve(InputFor(kit, _sword, slash, half, BladeHeight.Mid, slashBand));
                Check(edge.Mitigated, who + ": a hit exactly at the arc edge (" + half.ToString("F1", CultureInfo.InvariantCulture) + " deg) is held");

                // back and outside the arc: the guard is ignored
                GuardVerdict past = BladeGuardRules.Resolve(InputFor(kit, _sword, slash, half + 1f, BladeHeight.Mid, slashBand));
                Check(past.Outcome == GuardOutcome.OutsideArc && past.Reduction == 0f, who + ": one degree past the arc edge the guard is ignored (" + past.Outcome + ")");
                GuardVerdict behind = BladeGuardRules.Resolve(InputFor(kit, _sword, slash, 180f, BladeHeight.Mid, slashBand));
                Check(behind.Outcome == GuardOutcome.OutsideArc && behind.Reduction == 0f, who + ": a hit from behind ignores the guard (" + behind.Outcome + ")");

                // a mid guard covers every band the tuning produces
                foreach (BladeHeight band in new[] { slashBand, overBand, _flow.HeightFor(BladeTrajectory.Thrust) })
                {
                    GuardVerdict v = BladeGuardRules.Resolve(InputFor(kit, _sword, slash, 0f, BladeHeight.Mid, band));
                    Check(v.Mitigated, who + ": a mid guard holds an attack arriving at " + band + " (" + v.Outcome + ")");
                }
            }

            // the guard band follows the view pitch: looking down by the low threshold lowers it
            BladeHeight lowered = BladeGuardRules.GuardHeightFromPitch(_flow.guardLowPitchDegrees, _flow.guardHighPitchDegrees, _flow.guardLowPitchDegrees);
            BladeHeight raised = BladeGuardRules.GuardHeightFromPitch(-_flow.guardHighPitchDegrees, _flow.guardHighPitchDegrees, _flow.guardLowPitchDegrees);
            Check(lowered == BladeHeight.Low && raised == BladeHeight.High, "the tuning's pitch thresholds give a Low and a High guard (" + lowered + ", " + raised + ")");

            // wrong height: a lowered blade guard cannot stop an overhead; the same posture behind a shield can
            int distance = Math.Abs((int)lowered - (int)overBand);
            Check(_flow.bladeHeightTolerance < distance, "the shipped blade tolerance (" + _flow.bladeHeightTolerance + ") is smaller than the Low-to-overhead distance (" + distance + ")");
            GuardVerdict wrong = BladeGuardRules.Resolve(InputFor(blade, _sword, over, 0f, lowered, overBand));
            Check(wrong.Outcome == GuardOutcome.WrongHeight && wrong.Reduction == 0f && wrong.EnduranceSpent == 0f,
                "blade: a lowered guard against an overhead is the wrong height (" + wrong.Outcome + ")");
            GuardVerdict viaShield = BladeGuardRules.Resolve(InputFor(shielded, _sword, over, 0f, lowered, overBand));
            Check(viaShield.Mitigated, "shield: the same lowered guard still covers the overhead (" + viaShield.Outcome + ")");
            GuardVerdict raisedVsSlash = BladeGuardRules.Resolve(InputFor(blade, _sword, slash, 0f, raised, slashBand));
            Check(raisedVsSlash.Mitigated == (Math.Abs((int)raised - (int)slashBand) <= _flow.bladeHeightTolerance),
                "blade: a raised guard against a slash is held exactly when the bands are within the tolerance (" + raisedVsSlash.Outcome + ")");
        }

        // ---------------------------------------------------------------------------------------------
        // 5. endurance: an empty pool cannot block
        // ---------------------------------------------------------------------------------------------

        private static void EnduranceSection()
        {
            EnsureData();
            if (_sword == null || _warrior == null) { Check(false, "endurance: assets missing"); return; }

            SwingPatternData slash = _sword.swingPatterns != null && _sword.swingPatterns.Length > 0 ? _sword.swingPatterns[0] : null;
            if (slash == null) { Check(false, "endurance: the sword has no pattern"); return; }

            DefenseProfile kit = DefenseProfile.Resolve(_warrior, _sword, null);
            CombatFlowTuning pooled = new CombatFlowTuning();
            pooled.guardEnduranceRegenPerSecond = 25f;
            pooled.guardEnduranceRegenDelay = 1f;
            float impact = _sword.impactPower * slash.impactPowerScale;
            float cost = BladeGuardRules.BlockCost(impact, pooled.guardBlockCostBase, pooled.guardBlockCostPerImpact);
            Check(cost > 0f, "a held hit has a positive cost (" + cost + ")");
            pooled.guardEnduranceMax = cost * 2.5f;
            Check(pooled.GuardEnduranceEnabled, "a tuning with a pool enables the endurance rule");
            if (!(cost > 0f)) return;

            BladeHeight band = BladeTrajectories.HeightOf(slash.kind, pooled);
            GuardEnduranceState pool = new GuardEnduranceState();
            float now = 100f;
            int affordable = Mathf.FloorToInt(pooled.guardEnduranceMax / cost);
            int held = 0;
            GuardOutcome last = GuardOutcome.NotGuarding;
            for (int i = 0; i < affordable + 3; i++)
            {
                last = SimulateHit(kit, slash, pooled, band, pool, now);
                if (last == GuardOutcome.Blocked || last == GuardOutcome.Broken) held++;
            }

            Check(held == affordable, "a full pool holds exactly floor(max / cost) = " + affordable + " hits (" + held + ")");
            Check(last == GuardOutcome.Exhausted, "the hit that finds the pool empty is Exhausted (" + last + ")");
            Near(pooled.guardEnduranceMax - affordable * cost, pool.Current, 1e-3f, "each held hit paid its cost exactly once; refusals paid nothing");

            // the refusal is real damage: an exhausted hit has no reduction
            GuardVerdict dry = BladeGuardRules.Resolve(EnduranceInput(kit, slash, band, pooled, pool));
            Check(dry.Outcome == GuardOutcome.Exhausted && dry.Reduction == 0f && dry.EnduranceSpent == 0f, "exhausted: no reduction, no payment");

            // before the regeneration delay nothing comes back; after delay + the missing amount / rate the guard holds again
            float missing = cost - pool.Current;
            now += pooled.guardEnduranceRegenDelay * 0.5f;
            Check(SimulateHit(kit, slash, pooled, band, pool, now) == GuardOutcome.Exhausted, "inside the regeneration delay the pool stays empty");
            now = 100f + pooled.guardEnduranceRegenDelay + missing / pooled.guardEnduranceRegenPerSecond + 0.05f;
            GuardOutcome again = SimulateHit(kit, slash, pooled, band, pool, now);
            Check(again == GuardOutcome.Blocked || again == GuardOutcome.Broken, "after delay + missing / rate the guard holds again (" + again + ")");

            // a back hit or a wrong-height hit costs nothing even with an empty pool state
            GuardEnduranceState fresh = new GuardEnduranceState();
            fresh.Refresh(now, pooled.guardEnduranceMax, pooled.guardEnduranceRegenPerSecond, pooled.guardEnduranceRegenDelay);
            GuardInput backInput = EnduranceInput(kit, slash, band, pooled, fresh);
            backInput.BearingDegrees = 180f;
            GuardVerdict back = BladeGuardRules.Resolve(backInput);
            Check(back.Outcome == GuardOutcome.OutsideArc && back.EnduranceSpent == 0f, "a hit from behind costs no endurance");

            // free blocking with the shipped tuning: the same hit never exhausts anything
            GuardEnduranceState unused = new GuardEnduranceState();
            int freeHeld = 0;
            for (int i = 0; i < affordable + 3; i++)
            {
                GuardInput free = EnduranceInput(kit, slash, band, CombatFlowTuning.Default, unused);
                if (BladeGuardRules.Resolve(free).Mitigated) freeHeld++;
            }

            Check(freeHeld == affordable + 3, "default tuning: every hit is held for free (" + freeHeld + ")");
            Check(!unused.Started, "default tuning: no pool is ever created");
        }

        private static GuardInput EnduranceInput(DefenseProfile kit, SwingPatternData pattern, BladeHeight attackBand, CombatFlowTuning tuning, GuardEnduranceState pool)
        {
            float impact = _sword.impactPower * pattern.impactPowerScale;
            bool on = tuning.GuardEnduranceEnabled;
            GuardInput g = InputFor(kit, _sword, pattern, 0f, BladeHeight.Mid, attackBand);
            g.EnduranceEnabled = on;
            g.EnduranceCurrent = pool.Current;
            g.EnduranceCost = on ? BladeGuardRules.BlockCost(impact, tuning.guardBlockCostBase, tuning.guardBlockCostPerImpact) : 0f;
            return g;
        }

        // one hit exactly as PlayerRig.TryResolveGuard processes it: refresh the pool on the clock, decide, pay once
        private static GuardOutcome SimulateHit(DefenseProfile kit, SwingPatternData pattern, CombatFlowTuning tuning, BladeHeight attackBand,
            GuardEnduranceState pool, float now)
        {
            if (tuning.GuardEnduranceEnabled)
            {
                pool.Refresh(now, tuning.guardEnduranceMax, tuning.guardEnduranceRegenPerSecond, tuning.guardEnduranceRegenDelay);
            }

            GuardVerdict verdict = BladeGuardRules.Resolve(EnduranceInput(kit, pattern, attackBand, tuning, pool));
            if (tuning.GuardEnduranceEnabled && verdict.EnduranceSpent > 0f) pool.Spend(verdict.EnduranceSpent, now);
            return verdict.Outcome;
        }

        // ---------------------------------------------------------------------------------------------
        // 6. the damage numbers through the real resolver
        // ---------------------------------------------------------------------------------------------

        private sealed class GuardDummy : ICombatEntity, IGuardBreakDefender
        {
            public int CombatId { get; set; }
            public EntityKind Kind => EntityKind.Enemy;
            public int TeamId => CombatTeams.MonsterTeam;
            public Vector3 Center = new Vector3(0f, 1.1f, 0f);
            public Vector3 Facing = Vector3.forward;
            public DefenseProfile Kit;
            public BladeHeight GuardHeight = BladeHeight.Mid;
            public CombatFlowTuning Flow = new CombatFlowTuning();
            public GuardOutcome Last;

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
                if (toSource.sqrMagnitude < 0.0001f) toSource = -incoming.Direction;
                toSource.Normalize();
                GuardInput input = new GuardInput
                {
                    Raised = Kit.CanBlock,
                    BlockReduction = Kit.BlockReduction,
                    ArcDegrees = Kit.BlockArcDegrees,
                    BearingDegrees = Vector3.Angle(Facing, toSource),
                    HasShield = Kit.HasShield,
                    GuardHeight = GuardHeight,
                    AttackHeight = incoming.AttackHeight,
                    HeightTolerance = Kit.HasShield ? Flow.shieldHeightTolerance : Flow.bladeHeightTolerance,
                    ImpactPower = incoming.ImpactPower,
                    ImpactResist = Kit.ImpactResist,
                    BreakReductionScale = tuning != null ? tuning.guardBreakReductionScale : CombatFlowTuning.DefaultGuardBreakReductionScale,
                    EnduranceEnabled = false
                };

                GuardVerdict verdict = BladeGuardRules.Resolve(in input);
                Last = verdict.Outcome;
                result = verdict.Mitigated ? new GuardResult(verdict.Reduction, verdict.Broken) : default(GuardResult);
                return verdict.Mitigated;
            }
        }

        private static DamageInfo Swing(WeaponData weapon, SwingPatternData pattern, Vector3 attackerAt, ICombatEntity target)
        {
            Vector3 targetAt = target != null ? target.CombatCenter : Vector3.zero;
            AttackIntent intent = new AttackIntent(1, EntityKind.LocalPlayer, weapon, 1f, attackerAt, (targetAt - attackerAt).normalized,
                pattern.EffectiveReach(weapon), 1, 0f);
            HitCandidate hit = new HitCandidate(target != null ? target.CombatId : 2, targetAt, false);
            return DamageResolver.ResolveSwing(weapon, pattern, intent, hit, HitLocation.Body, 1f, target, 1f, 0f, 1f, 1f, _flow);
        }

        private static void ResolverSection()
        {
            EnsureData();
            if (_sword == null || _maul == null || _warrior == null || _shield == null) { Check(false, "resolver: assets missing"); return; }

            SwingPatternData slash = _sword.swingPatterns != null && _sword.swingPatterns.Length > 0 ? _sword.swingPatterns[0] : null;
            SwingPatternData over = null;
            if (_sword.swingPatterns != null)
            {
                foreach (SwingPatternData p in _sword.swingPatterns) if (p != null && p.kind == SwingKind.Overhead) over = p;
            }

            if (slash == null || over == null) { Check(false, "resolver: sword patterns missing"); return; }

            // the height band travels with the hit, from the pattern and the tuning
            DamageInfo bare = Swing(_sword, over, new Vector3(0f, 1.1f, 3f), null);
            Check(bare.AttackHeight == _flow.HeightFor(BladeTrajectories.Of(over.kind)), "an overhead swing's DamageInfo carries the overhead band (" + bare.AttackHeight + ")");
            DamageInfo bareSlash = Swing(_sword, slash, new Vector3(0f, 1.1f, 3f), null);
            Check(bareSlash.AttackHeight == _flow.HeightFor(BladeTrajectories.Of(slash.kind)), "a slash's DamageInfo carries the horizontal band (" + bareSlash.AttackHeight + ")");

            // the band survives every copy the resolver makes
            Check(bare.WithReduction(0.4f, out _).AttackHeight == bare.AttackHeight, "WithReduction keeps the band");
            Check(bare.WithImpactPower(5f).AttackHeight == bare.AttackHeight, "WithImpactPower keeps the band");
            Check(bare.WithProjectileGuardContact(true).AttackHeight == bare.AttackHeight, "WithProjectileGuardContact keeps the band");
            Check(DamageResolver.ApplyArmorRating(bare, 50f).AttackHeight == bare.AttackHeight, "armor keeps the band");
            Check(DamageResolver.ApplyProjectileFalloff(bare, 0.5f).AttackHeight == bare.AttackHeight, "falloff keeps the band");

            // the shield kit holds a sword hit without breaking (the shield's impact resist wins); the blade kit is the one a lowered guard test needs
            DefenseProfile shieldKit = DefenseProfile.Resolve(_warrior, _sword, _shield);
            DefenseProfile bladeKit = DefenseProfile.Resolve(_warrior, _sword, null);
            GuardDummy shieldDummy = new GuardDummy { CombatId = 77, Kit = shieldKit, Flow = _flow };
            GuardDummy bladeDummy = new GuardDummy { CombatId = 78, Kit = bladeKit, Flow = _flow };
            Vector3 front = new Vector3(0f, 1.1f, 3f);
            Vector3 back = new Vector3(0f, 1.1f, -3f);

            float unguarded = bare.Amount;
            Check(unguarded > 0f, "the unguarded overhead has damage (" + unguarded + ")");
            Check(!BladeGuardRules.IsGuardBroken(_sword.impactPower * over.impactPowerScale, shieldKit.ImpactResist),
                "fixture: the sword overhead does not break the shield kit (impact " + _sword.impactPower * over.impactPowerScale + " vs resist " + shieldKit.ImpactResist + ")");

            // front, mid guard: blocked, damage = unguarded x (1 - reduction)
            shieldDummy.GuardHeight = BladeHeight.Mid;
            DamageInfo blocked = Swing(_sword, over, front, shieldDummy);
            Check(blocked.Blocked && !blocked.GuardBroken, "front, mid guard: the overhead is blocked (" + shieldDummy.Last + ")");
            Near(unguarded * (1f - BladeGuardRules.HeldGuardReduction), blocked.Amount, 0.01f, "a held block takes 0 HP: unguarded x (1 - held reduction)");

            // back: full damage
            DamageInfo behind = Swing(_sword, over, back, shieldDummy);
            Check(!behind.Blocked && shieldDummy.Last == GuardOutcome.OutsideArc, "behind: not blocked (" + shieldDummy.Last + ")");
            Near(unguarded, behind.Amount, 0.01f, "behind: full damage");

            // wrong height: a lowered blade guard against the overhead takes full damage
            bladeDummy.GuardHeight = BladeGuardRules.GuardHeightFromPitch(_flow.guardLowPitchDegrees, _flow.guardHighPitchDegrees, _flow.guardLowPitchDegrees);
            DamageInfo wrong = Swing(_sword, over, front, bladeDummy);
            Check(!wrong.Blocked && bladeDummy.Last == GuardOutcome.WrongHeight, "front, lowered blade guard: the overhead is the wrong height (" + bladeDummy.Last + ")");
            Near(unguarded, wrong.Amount, 0.01f, "wrong height: full damage");

            // the same lowered blade guard against a slash (a band within the tolerance) holds
            DamageInfo slashHeld = Swing(_sword, slash, front, bladeDummy);
            int slashDistance = Math.Abs((int)bladeDummy.GuardHeight - (int)_flow.HeightFor(BladeTrajectories.Of(slash.kind)));
            Check(slashHeld.Blocked == (slashDistance <= _flow.bladeHeightTolerance), "front, lowered blade guard: the slash is held exactly when its band is within the tolerance (" + bladeDummy.Last + ")");

            // a heavy hit still does not break the guard
            SwingPatternData maulPattern = _maul.swingPatterns != null && _maul.swingPatterns.Length > 0 ? _maul.swingPatterns[0] : null;
            if (maulPattern == null) { Check(false, "resolver: the maul has no pattern"); return; }

            shieldDummy.GuardHeight = BladeHeight.Mid;
            DamageInfo maulBare = Swing(_maul, maulPattern, front, null);
            DamageInfo maulGuarded = Swing(_maul, maulPattern, front, shieldDummy);
            Check(maulGuarded.Blocked && !maulGuarded.GuardBroken, "the maul never breaks a covering guard, whatever its impact power");
            Near(maulBare.Amount * (1f - BladeGuardRules.HeldGuardReduction), maulGuarded.Amount, 0.01f, "maul damage: a held guard takes 0 HP");
        }

        // ---------------------------------------------------------------------------------------------
        // 7. shields never parry
        // ---------------------------------------------------------------------------------------------

        private static void ShieldSection()
        {
            EnsureData();
            if (_shield == null || _sword == null || _warrior == null) { Check(false, "shield: assets missing"); return; }

            Check(_shield.IsShield, "the bulwark is a shield");
            Check(_shield.CanBlock, "the shield blocks");
            Check(!_shield.CanParry, "the authored shield cannot parry");

            // ticking Parry on a shield does nothing
            WeaponData forged = UnityEngine.Object.Instantiate(_shield);
            try
            {
                forged.capabilities = DefenseCapability.Block | DefenseCapability.Parry;
                Check(forged.IsShield && forged.CanBlock && !forged.CanParry, "a shield with Parry ticked still cannot parry");
                DefenseProfile viaForged = DefenseProfile.Resolve(_warrior, _sword, forged);
                Check(viaForged.HasShield && !viaForged.CanParry, "a kit carrying that shield cannot parry");
                Check((viaForged.Capabilities & DefenseCapability.Parry) == 0, "the resolved capabilities carry no Parry bit");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(forged);
            }

            DefenseProfile shieldProfile = new DefenseProfile(DefenseCapability.Block | DefenseCapability.Parry, 0.6f, 120f, true);
            DefenseProfile bladeProfile = new DefenseProfile(DefenseCapability.Block | DefenseCapability.Parry, 0.6f, 120f, false);
            Check(!shieldProfile.CanParry, "a DefenseProfile built for a shield strips Parry");
            Check(bladeProfile.CanParry, "a parry-capable blade profile keeps it (the capability stays in data)");
            Check(!BladeGuardRules.CanParry(true, true) && BladeGuardRules.CanParry(false, true), "the pure rule agrees");

            // the pure verdict has no parry or riposte outcome at all
            foreach (GuardOutcome outcome in Enum.GetValues(typeof(GuardOutcome)))
            {
                string name = outcome.ToString();
                Check(name.IndexOf("parry", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("riposte", StringComparison.OrdinalIgnoreCase) < 0,
                    "guard outcome '" + name + "' is not a parry");
            }
        }

        // ---------------------------------------------------------------------------------------------
        // 8. the presentation event carries the committed trajectory
        // ---------------------------------------------------------------------------------------------

        private static void WireSection()
        {
            EnsureData();
            if (Cases.Count == 0) { Check(false, "wire: no cases"); return; }

            foreach (Case c in Cases)
            {
                int k = GridFor(c);
                BladeWarning w = BladeWarning.Describe(c.Pattern, c.Weapon, TestEye, TestYaw, TestPitch, _flow, k);
                Emberdelve.Net.CombatSwingWire wire = default(Emberdelve.Net.CombatSwingWire);
                wire.SetWarning(in w, true);
                Check(wire.TrajectoryKind == w.Trajectory && wire.HeightBand == w.Height, c.Name + ": the swing event carries the warning's trajectory and height band");
                Near(w.AimYaw, wire.AimYaw, 1e-6f, c.Name + ": event yaw");
                Near(w.AimPitch, wire.AimPitch, 1e-6f, c.Name + ": event pitch");
                Near(w.MinBladeYaw, wire.MinBladeYaw, 1e-6f, c.Name + ": event min blade yaw");
                Near(w.MaxBladeYaw, wire.MaxBladeYaw, 1e-6f, c.Name + ": event max blade yaw");
                Near(w.MinBladePitch, wire.MinBladePitch, 1e-6f, c.Name + ": event min blade pitch");
                Near(w.MaxBladePitch, wire.MaxBladePitch, 1e-6f, c.Name + ": event max blade pitch");
                Near(w.Reach, wire.Reach, 1e-6f, c.Name + ": event reach");
                Check(wire.Committed == 1, c.Name + ": event carries the aim-committed flag");
            }

            // a wire byte that is not a known class decodes to "none", never to a made-up one
            Emberdelve.Net.CombatSwingWire junk = default(Emberdelve.Net.CombatSwingWire);
            junk.Trajectory = 200;
            junk.Height = 200;
            Check(junk.TrajectoryKind == BladeTrajectory.None && junk.HeightBand == BladeHeight.Unspecified, "unknown wire bytes decode to none / unspecified");
        }
    }
}
