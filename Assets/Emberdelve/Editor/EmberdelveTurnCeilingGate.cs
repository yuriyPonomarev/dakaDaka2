using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// D1c2 gate: the swing turn ceiling (review D1a H2) and the sample grid that goes with it (review M-b).
    /// Everything talks to a real LocalCombatAuthority on an injected clock stepped by hand, with plain fakes. The
    /// scenarios sit far outside the dungeon, so no real level geometry can touch them.
    ///
    /// Run: Unity -batchmode -nographics -projectPath . -logFile Logs/d1c2_turnceiling.log
    ///      -executeMethod Emberdelve.EditorTools.EmberdelveTurnCeilingGate.Run
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveTurnCeilingGate
    {
        private const string Pending = "Emberdelve.TurnCeilingGate.Pending";

        static EmberdelveTurnCeilingGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("TurnCeiling_Gate").AddComponent<TurnCeilingHarness>();
                }
            };
        }

        public static void Run()
        {
            SessionState.SetBool(Pending, true);
            EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_Level01.unity");
            EditorApplication.EnterPlaymode();
        }
    }

    public sealed class TurnCeilingHarness : MonoBehaviour
    {
        private const string Tag = "[TurnCeilingGate]";
        private static readonly Vector3 Origin = new Vector3(3000f, 0f, 3000f);

        private int _checks;
        private int _failures;
        private readonly List<UnityEngine.Object> _cleanup = new List<UnityEngine.Object>();
        private readonly List<Scenario> _scenarios = new List<Scenario>();

        private LocalCombatAuthority _authority;
        private GameConfig _config;
        private float _clock = 100f;   // small, so float resolution of the injected clock stays below 0.02 ms
        private WeaponData _sword;
        private WeaponData _rapier;

        private sealed class Swinger : ICombatEntity, IActiveWeaponProvider, IAimPoseProvider
        {
            public int CombatId { get; set; }
            public EntityKind Kind => EntityKind.LocalPlayer;
            public int TeamId => CombatTeams.PlayerTeam;
            public Vector3 Eye;
            public float Yaw;
            public float Pitch;
            public WeaponData Weapon;
            public bool HasPose = true;
            public Vector3 CombatCenter => Eye + Vector3.down * 0.7f;
            public Collider HeadCollider => null;
            public WeaponData ActiveWeapon => Weapon;
            public bool IsDead => false;
            public float CurrentHealth => 1000f;
            public float MaxHealth => 1000f;
            public void ApplyDamage(DamageInfo damage) { }

            public bool TryGetAimPose(out Vector3 eye, out float yawDegrees, out float pitchDegrees)
            {
                eye = Eye;
                yawDegrees = Yaw;
                pitchDegrees = Pitch;
                return HasPose;
            }
        }

        private sealed class Dummy : ICombatEntity, IHitShapeProvider
        {
            public int CombatId { get; set; }
            public EntityKind Kind => EntityKind.Enemy;
            public int TeamId => CombatTeams.MonsterTeam;
            public Vector3 Center;
            public float Radius;
            public int Hits;
            public Vector3 CombatCenter => Center;
            public Collider HeadCollider => null;
            public bool IsDead => false;
            public float CurrentHealth => 100000f;
            public float MaxHealth => 100000f;
            public void ApplyDamage(DamageInfo damage) { Hits++; }
            public void CollectHitShapes(List<HitShape> shapes) { shapes.Add(new HitShape(HitLocation.Body, Center, Center, Radius)); }
        }

        private sealed class Scenario : IDisposable
        {
            public Swinger Who;
            public readonly List<ICombatEntity> Registered = new List<ICombatEntity>();
            public void Dispose()
            {
                foreach (ICombatEntity e in Registered) CombatRegistry.Unregister(e.CombatId, e);
                Registered.Clear();
            }
        }

        private void Check(bool ok, string label)
        {
            _checks++;
            if (!ok)
            {
                _failures++;
                Debug.LogError($"{Tag} FAIL {label}");
            }
        }

        private Scenario NewScenario(WeaponData weapon, float yaw = 0f, float pitch = 0f)
        {
            Scenario s = new Scenario();
            s.Who = new Swinger { CombatId = CombatRegistry.AllocateId(), Weapon = weapon, Yaw = yaw, Pitch = pitch, Eye = Origin + new Vector3(0f, 1.8f, 0f) };
            CombatRegistry.Register(s.Who.CombatId, s.Who);
            s.Registered.Add(s.Who);
            _scenarios.Add(s);
            return s;
        }

        private Dummy AddBody(Scenario s, Vector3 local, float radius = 0.3f)
        {
            Dummy d = new Dummy { CombatId = CombatRegistry.AllocateId(), Center = Origin + local, Radius = radius };
            CombatRegistry.Register(d.CombatId, d);
            s.Registered.Add(d);
            return d;
        }

        private void SetCeiling(float value) { _config.flow.swingTurnCeilingDegPerSec = value; }

        /// <summary>
        /// Begins a swing and steps it with <paramref name="dts"/> (repeated), setting the reported pose from
        /// <paramref name="poseAt"/>(tick index starting at 1, simulated seconds) before every step.
        /// </summary>
        private MeleeSwingStatus Drive(Scenario s, int pattern, float[] dts, Func<int, float, Vector2> poseAt, Action<int, float> afterBegin = null)
        {
            Swinger who = s.Who;
            Vector2 start = poseAt(0, 0f);
            who.Yaw = start.x;
            who.Pitch = start.y;
            MeleeSwingIntent intent = new MeleeSwingIntent(who.CombatId, who.Weapon.weaponId, pattern, 0, who.Yaw, who.Pitch, CombatRegistry.NextSequence(who.CombatId));
            bool ok = _authority.TryBeginMeleeSwing(intent, out int id, out string reason);
            Check(ok, "swing accepted (" + reason + ")");
            if (!ok) return default;

            afterBegin?.Invoke(id, 0f);
            float elapsed = 0f;
            int tick = 0;
            MeleeSwingStatus status = default;
            while (elapsed < 4f)
            {
                float dt = dts[tick % dts.Length];
                tick++;
                _clock += dt;
                elapsed += dt;
                Vector2 pose = poseAt(tick, elapsed);
                who.Yaw = pose.x;
                who.Pitch = pose.y;
                _authority.StepSwings();
                if (_authority.TryGetMeleeSwingStatus(id, out status) && status.Finished) break;
            }

            _authority.TryGetMeleeSwingStatus(id, out status);
            _clock += 3f;   // far beyond any cooldown
            return status;
        }

        /// <summary>Ticks of 5 ms until <paramref name="preTicks"/> have passed, then one 20 ms tick, then 5 ms ticks again.</summary>
        private static float[] FlickPlan(int preTicks)
        {
            float[] dts = new float[900];
            for (int i = 0; i < dts.Length; i++) dts[i] = i == preTicks ? 0.02f : 0.005f;
            return dts;
        }

        private static int TicksFor(float seconds) => Mathf.RoundToInt(seconds / 0.005f);

        private IEnumerator Start()
        {
            yield return null;
            yield return null;

            Section("setup and tuning convention", Setup);
            Section("yaw flick", YawFlick);
            Section("pitch flick", PitchFlick);
            Section("legal turns are never clamped", LegalTurns);
            Section("sample grid", SampleGrid);

            foreach (Scenario s in _scenarios) s.Dispose();
            foreach (UnityEngine.Object o in _cleanup) if (o != null) Destroy(o);
            Debug.Log($"{Tag} {(_failures == 0 ? "PASS" : "FAIL")} checks={_checks} failures={_failures}");
            EditorApplication.Exit(_failures == 0 ? 0 : 1);
        }

        private void Section(string name, Action body)
        {
            int checksBefore = _checks;
            int failuresBefore = _failures;
            try { body(); }
            catch (Exception e)
            {
                _failures++;
                Debug.LogError($"{Tag} FAIL section '{name}' threw");
                Debug.LogException(e);
            }
            Debug.Log($"{Tag} section '{name}': {_checks - checksBefore} checks, {_failures - failuresBefore} failures");
        }

        // ------------------------------------------------------------------------------------------------

        private void Setup()
        {
            _sword = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_AshenShortSword.asset");
            _rapier = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_EmberRapier.asset");
            Check(_sword != null && _rapier != null, "weapon assets are available");
            if (_sword == null || _rapier == null) throw new InvalidOperationException("weapon assets missing");

            // the ceiling convention (CombatFlowTuning): positive as is, 0 = default (old asset), negative = unlimited
            CombatFlowTuning t = new CombatFlowTuning();
            Check(Mathf.Approximately(t.swingTurnCeilingDegPerSec, 720f) && Mathf.Approximately(t.ResolvedSwingTurnCeiling, 720f), "a new tuning object defaults to 720 deg/s");
            t.swingTurnCeilingDegPerSec = 0f;
            Check(Mathf.Approximately(t.ResolvedSwingTurnCeiling, 720f), "0 (an asset saved before D1c2) resolves to the default 720");
            t.swingTurnCeilingDegPerSec = 300f;
            Check(Mathf.Approximately(t.ResolvedSwingTurnCeiling, 300f) && Mathf.Approximately(t.GridSwingTurnCeiling, 300f), "a positive value is used as is");
            t.swingTurnCeilingDegPerSec = -1f;
            Check(float.IsPositiveInfinity(t.ResolvedSwingTurnCeiling) && Mathf.Approximately(t.GridSwingTurnCeiling, 720f), "a negative value is unlimited; the sample grid is still sized for 720");
            t.swingTurnCeilingDegPerSec = float.NaN;
            Check(Mathf.Approximately(t.ResolvedSwingTurnCeiling, 720f), "NaN falls back to the default");

            GameConfig authored = AssetDatabase.LoadAssetAtPath<GameConfig>(EmberdelvePaths.Data + "/EmberdelveGameConfig.asset");
            Check(authored != null && authored.flow != null && Mathf.Approximately(authored.flow.ResolvedSwingTurnCeiling, 720f), "the authored game config resolves to 720 deg/s");

            _config = ScriptableObject.CreateInstance<GameConfig>();
            _cleanup.Add(_config);
            _config.levelMask = LayerMask.GetMask(GameConfig.LevelLayerName);
            _config.hitLocations = null;

            GameObject go = new GameObject("TurnCeilingGate_Authority");
            _cleanup.Add(go);
            _authority = go.AddComponent<LocalCombatAuthority>();
            typeof(LocalCombatAuthority).GetField("config", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_authority, _config);
            _authority.SetClock(() => _clock);
            _authority.AutoStepSwings = false;
            _authority.AllowLegacyPatternSelection = true;
            _authority.ImpactZonesEnabled = false;
        }

        // ------------------------------------------------------------------------------------------------
        // 180 degree yaw flick inside one 20 ms tick, target directly behind. The rapier thrust is the probe: a line straight
        // ahead (yaw -6 from the aim), so a target behind is only reachable once the aim really points backwards. The
        // follower needs 0.25 s for 180 degrees at 720 deg/s, the thrust's active phase is 0.12 s.
        // ------------------------------------------------------------------------------------------------

        private void YawFlick()
        {
            SwingTiming timing = _rapier.ComboTiming(0, _rapier.swingPatterns[0]);
            float windup = timing.Windup;
            float active = timing.Active;
            Check(windup > 0.05f && active > 0.05f && active < 0.2f, $"the rapier thrust timing is the one this gate reasons about (windup {windup:F2}, active {active:F2})");

            // flick moments: early in the active phase, the last 20 ms of the active phase, and during the windup
            var moments = new (string name, float at)[]
            {
                ("early in the active phase", windup + 0.02f),
                ("in the last 20 ms of the active phase", windup + active - 0.02f),
                ("during the windup", Mathf.Max(0.02f, windup - 0.04f))
            };

            foreach (var m in moments)
            {
                int pre = TicksFor(m.at);
                float[] plan = FlickPlan(pre);
                Func<int, float, Vector2> flick = (tick, el) => new Vector2(tick > pre ? 180f : 0f, 0f);

                // capped (720): the target behind is not hit, the status reports the held-back degrees
                SetCeiling(720f);
                Scenario s = NewScenario(_rapier);
                Dummy behind = AddBody(s, new Vector3(0f, 1.6f, -1.8f));
                MeleeSwingStatus st = Drive(s, 0, plan, flick);
                Check(st.Finished && st.EndReason == SwingEndReason.Completed, $"yaw flick {m.name}: the swing completes");
                Check(behind.Hits == 0 && st.Hits.Count == 0, $"yaw flick {m.name}: a 180 degree flick in one 20 ms tick does not reach the target directly behind (hits {behind.Hits})");
                Check(st.TurnClampedDegrees >= 150f, $"yaw flick {m.name}: the status reports the held-back degrees ({st.TurnClampedDegrees:F1})");
                s.Dispose();

                // mutation: the same scenario with the ceiling off hits (so the check above really is the ceiling)
                SetCeiling(-1f);
                s = NewScenario(_rapier);
                behind = AddBody(s, new Vector3(0f, 1.6f, -1.8f));
                st = Drive(s, 0, plan, flick);
                Check(behind.Hits == 1 && st.Hits.Count == 1, $"yaw flick {m.name}, ceiling OFF (mutation): the target behind IS hit ({behind.Hits})");
                Check(Mathf.Approximately(st.TurnClampedDegrees, 0f), $"yaw flick {m.name}, ceiling OFF: nothing is reported clamped");
                s.Dispose();
            }

            SetCeiling(720f);

            // a flick that never exceeds the budget passes untouched: 14 degrees in one 20 ms tick (limit 14.4)
            {
                int pre = TicksFor(windup + 0.02f);
                Scenario s = NewScenario(_rapier);
                MeleeSwingStatus st = Drive(s, 0, FlickPlan(pre), (tick, el) => new Vector2(tick > pre ? 14f : 0f, 0f));
                Check(Mathf.Approximately(st.TurnClampedDegrees, 0f), $"a 14 degree step in 20 ms (700 deg/s) is under the ceiling: nothing clamped ({st.TurnClampedDegrees:F2})");
                s.Dispose();
            }

            // pose-less after the begin: the swing keeps its last pose, nothing clamped, the hit in front lands
            {
                Scenario s = NewScenario(_rapier);
                Dummy front = AddBody(s, new Vector3(0f, 1.6f, 1.8f));
                MeleeSwingStatus st = Drive(s, 0, new[] { 0.005f }, (tick, el) => new Vector2(0f, 0f), (id, t) => s.Who.HasPose = false);
                Check(st.Finished && front.Hits == 1, "pose-less after the begin: the swing completes on its last pose and hits the target in front");
                Check(Mathf.Approximately(st.TurnClampedDegrees, 0f), "pose-less: nothing clamped");
                s.Dispose();
            }
        }

        private void PitchFlick()
        {
            SwingTiming timing = _rapier.ComboTiming(0, _rapier.swingPatterns[0]);
            int pre = TicksFor(timing.Windup + timing.Active - 0.02f);
            float[] plan = FlickPlan(pre);
            Func<int, float, Vector2> flick = (tick, el) => new Vector2(0f, tick > pre ? 80f : 0f);

            // pitch 80 down in the last 20 ms: the segment would hang straight below the eye. The target sits on that line.
            Vector3 below = new Vector3(0.2f, 0.2f, 0.25f);

            SetCeiling(720f);
            Scenario s = NewScenario(_rapier);
            Dummy low = AddBody(s, below);
            MeleeSwingStatus st = Drive(s, 0, plan, flick);
            Check(low.Hits == 0, $"pitch flick: 80 degrees down inside one 20 ms tick does not reach the target below ({low.Hits})");
            Check(st.TurnClampedDegrees >= 60f, $"pitch flick: the status reports the held-back degrees ({st.TurnClampedDegrees:F1})");
            s.Dispose();

            SetCeiling(-1f);
            s = NewScenario(_rapier);
            low = AddBody(s, below);
            st = Drive(s, 0, plan, flick);
            Check(low.Hits == 1, $"pitch flick, ceiling OFF (mutation): the target below IS hit ({low.Hits})");
            s.Dispose();
            SetCeiling(720f);
        }

        // ------------------------------------------------------------------------------------------------
        // Honest turns: a slow pursuit and a fast but legal turn are never clamped and sweep exactly the same thing at every
        // sensible tick length; a turn just above the ceiling is clamped.
        // ------------------------------------------------------------------------------------------------

        private void LegalTurns()
        {
            SetCeiling(720f);
            float[][] plans = { new[] { 0.001f }, new[] { 0.005f }, new[] { 0.02f }, new[] { 0.016f, 0.017f, 0.018f }, new[] { 0.05f } };

            // 100 deg/s toward a target 40 degrees off (sword slash, 1.8 m)
            string reference = null;
            int referenceFirst = -1;
            for (int p = 0; p < plans.Length; p++)
            {
                Scenario s = NewScenario(_sword);
                float yawTarget = 40f * Mathf.Deg2Rad;
                Dummy d = AddBody(s, new Vector3(Mathf.Sin(yawTarget) * 1.8f, 1.5f, Mathf.Cos(yawTarget) * 1.8f));
                MeleeSwingStatus st = Drive(s, 0, plans[p], (tick, el) => new Vector2(100f * el, 0f));
                bool hit = st.Hits.Count == 1 && d.Hits == 1;
                Check(hit, $"slow turn (100 deg/s) toward a target 40 degrees off: hit, tick plan {p}");
                Check(Mathf.Approximately(st.TurnClampedDegrees, 0f), $"slow turn: nothing clamped, tick plan {p} ({st.TurnClampedDegrees:F3})");
                if (hit)
                {
                    string key = st.Hits[0].Location + ":" + st.Hits[0].Amount.ToString("F3");
                    if (reference == null) { reference = key; referenceFirst = st.Hits[0].FirstSample; }
                    Check(key == reference, $"slow turn: tick plan {p} lands the same location and damage ({key} vs {reference})");
                    Check(Mathf.Abs(st.Hits[0].FirstSample - referenceFirst) <= 2, $"slow turn: tick plan {p} first touches within 2 samples of the 1 ms plan ({st.Hits[0].FirstSample} vs {referenceFirst})");
                }
                s.Dispose();
            }

            // 600 deg/s (honest, fast) is below the ceiling: never clamped at any tick length
            for (int p = 0; p < plans.Length; p++)
            {
                Scenario s = NewScenario(_sword);
                MeleeSwingStatus st = Drive(s, 0, plans[p], (tick, el) => new Vector2(600f * el, 0f));
                Check(Mathf.Approximately(st.TurnClampedDegrees, 0f), $"a 600 deg/s turn is under the ceiling: nothing clamped, tick plan {p} ({st.TurnClampedDegrees:F3})");
                s.Dispose();
            }

            // 900 deg/s is above it: clamped, and the amount is positive (the follower falls behind and the excess is dropped)
            {
                Scenario s = NewScenario(_sword);
                MeleeSwingStatus st = Drive(s, 0, new[] { 0.01f }, (tick, el) => new Vector2(900f * el, 0f));
                Check(st.TurnClampedDegrees > 30f, $"a 900 deg/s turn is clamped ({st.TurnClampedDegrees:F1} degrees held back over the swing)");
                s.Dispose();
            }

            // a turn that changes direction (yaw and pitch together, honest speed) stays unclamped
            {
                Scenario s = NewScenario(_sword);
                MeleeSwingStatus st = Drive(s, 0, new[] { 0.02f }, (tick, el) => new Vector2(60f * Mathf.Sin(el * 6f), 30f * Mathf.Sin(el * 5f)));
                Check(Mathf.Approximately(st.TurnClampedDegrees, 0f), $"an honest wobble in yaw and pitch is not clamped ({st.TurnClampedDegrees:F3})");
                s.Dispose();
            }

            // thin target on the allowed path of a TURNING attacker (600 deg/s): found at several tick lengths
            WeaponData thinWeapon = Instantiate(_rapier);
            thinWeapon.weaponId = "thin_turn_rapier";
            SwingPatternData thin = ScriptableObject.CreateInstance<SwingPatternData>();
            EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(_rapier.swingPatterns[0]), thin);
            thin.patternId = "thin_turn";
            thin.sweepRadius = 0.02f;
            thinWeapon.swingPatterns = new[] { thin };
            _cleanup.Add(thinWeapon);
            _cleanup.Add(thin);

            SwingTiming timing = thinWeapon.ComboTiming(0, thin);
            foreach (float u in new[] { 0.3f, 0.6f })
            {
                float time = timing.Windup + timing.Active * u;
                float yawThen = 600f * time;
                SwingGeometry.BladeSegment(new SwingFrame(new Vector3(0f, 1.8f, 0f), yawThen, 0f), thin.ActivePose(u), thin.BladeLength(thinWeapon), out Vector3 b, out Vector3 tip);
                Vector3 mid = Vector3.Lerp(b, tip, 0.6f);
                foreach (float dt in new[] { 0.005f, 0.02f })
                {
                    Scenario s = NewScenario(thinWeapon);
                    Dummy target = AddBody(s, mid, 0.01f);
                    MeleeSwingStatus st = Drive(s, 0, new[] { dt }, (tick, el) => new Vector2(600f * el, 0f));
                    Check(target.Hits == 1, $"thin target (r 0.01) on the path of a 600 deg/s turning attacker at u={u:F1} is found with {dt * 1000f:F0} ms ticks");
                    Check(Mathf.Approximately(st.TurnClampedDegrees, 0f), $"thin target: honest turn not clamped (u={u:F1}, {dt * 1000f:F0} ms)");
                    s.Dispose();
                }
            }
        }

        // ------------------------------------------------------------------------------------------------
        // The sample grid: K is fine enough for translation (per-probe maximum) and rotation (ceiling), and never above 400.
        // ------------------------------------------------------------------------------------------------

        /// <summary>Largest distance any point of the blade (base or tip) moves between two consecutive samples, plus the rotation allowance.</summary>
        private static float WorstSampleGap(SwingPatternData p, WeaponData w, int k, float ceilingDegPerSec)
        {
            float length = p.BladeLength(w);
            SwingFrame frame = new SwingFrame(Vector3.zero, Quaternion.identity);
            float worst = 0f;
            SwingGeometry.BladeSegment(frame, p.ActivePose(0f), length, out Vector3 pb, out Vector3 pt);
            for (int i = 1; i <= k; i++)
            {
                SwingGeometry.BladeSegment(frame, p.ActivePose(i / (float)k), length, out Vector3 b, out Vector3 t);
                worst = Mathf.Max(worst, Mathf.Max(Vector3.Distance(pb, b), Vector3.Distance(pt, t)));
                pb = b;
                pt = t;
            }

            float rotation = ceilingDegPerSec * Mathf.Deg2Rad * (p.activeTime / k) * p.EffectiveReach(w);
            return worst + rotation;
        }

        private void SampleGrid()
        {
            foreach (WeaponData w in new[] { _sword, _rapier })
            {
                foreach (SwingPatternData p in w.swingPatterns)
                {
                    int k = SwingGeometry.SampleCount(p, w, 720f);
                    float spacing = Mathf.Clamp(p.sweepRadius * 0.5f, 0.02f, 0.06f);
                    Check(k < SwingGeometry.MaxSamples && k >= SwingGeometry.MinSamples, $"{p.patternId}: K at 720 deg/s is inside the clamp and below the maximum ({k})");
                    Check(k == SwingGeometry.SampleCount(p, w), $"{p.patternId}: the two-argument overload is the 720 deg/s grid");
                    float gap = WorstSampleGap(p, w, k, 720f);
                    Check(gap <= spacing * 1.03f, $"{p.patternId}: consecutive samples are at most {spacing:F3} m apart even while turning at 720 deg/s (worst {gap:F4} m)");
                    Check(SwingGeometry.SampleCount(p, w, 720f) > SwingGeometry.SampleCount(p, w, 0.001f), $"{p.patternId}: the rotation term adds samples (mutation guard: {SwingGeometry.SampleCount(p, w, 0.001f)} -> {k})");
                    Check(SwingGeometry.SampleCount(p, w, 360f) <= k && SwingGeometry.SampleCount(p, w, 360f) >= SwingGeometry.SampleCount(p, w, 0.001f), $"{p.patternId}: K grows with the ceiling");
                    Check(SwingGeometry.SampleCount(p, w, -1f) == k && SwingGeometry.SampleCount(p, w, float.PositiveInfinity) == k, $"{p.patternId}: a non-positive or infinite ceiling is sized as the default");
                }
            }

            // M-b: a path with a fast segment (110 degrees of blade yaw in the first tenth of the active time, then almost still)
            SwingPatternData fast = ScriptableObject.CreateInstance<SwingPatternData>();
            _cleanup.Add(fast);
            fast.patternId = "fast_segment";
            fast.activeTime = 0.3f;
            fast.sweepRadius = 0.15f;
            fast.reachScale = _sword.swingPatterns[0].reachScale;
            fast.reachInset = _sword.swingPatterns[0].reachInset;
            fast.keys = new[]
            {
                new SwingKey(0f, new Vector3(0.2f, -0.3f, 0.6f), -55f, -4f),
                new SwingKey(0.1f, new Vector3(0.2f, -0.3f, 0.6f), 55f, -4f),
                new SwingKey(1f, new Vector3(0.2f, -0.3f, 0.6f), 60f, -4f)
            };
            float spacingFast = Mathf.Clamp(fast.sweepRadius * 0.5f, 0.02f, 0.06f);
            float length = fast.BladeLength(_sword);
            float travel = 0f;
            const int probes = 512;
            SwingGeometry.BladeSegment(new SwingFrame(Vector3.zero, Quaternion.identity), fast.ActivePose(0f), length, out Vector3 pb, out Vector3 pt);
            for (int i = 1; i <= probes; i++)
            {
                SwingGeometry.BladeSegment(new SwingFrame(Vector3.zero, Quaternion.identity), fast.ActivePose(i / (float)probes), length, out Vector3 b, out Vector3 t);
                travel += Mathf.Max(Vector3.Distance(pb, b), Vector3.Distance(pt, t));
                pb = b;
                pt = t;
            }

            int averageRule = Mathf.CeilToInt(travel / spacingFast);
            int kFast = SwingGeometry.SampleCount(fast, _sword, 0.001f);
            Check(kFast > averageRule, $"a path with a fast segment gets more samples than the old average rule ({kFast} vs {averageRule})");
            Check(WorstSampleGap(fast, _sword, kFast, 0.001f) <= spacingFast * 1.03f || kFast == SwingGeometry.MaxSamples, $"the fast segment's samples are within the spacing ({WorstSampleGap(fast, _sword, kFast, 0.001f):F4} m of {spacingFast:F3})");
        }
    }
}
