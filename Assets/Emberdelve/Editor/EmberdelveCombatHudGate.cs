using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Feedback;
using Emberdelve.Movement;
using Emberdelve.Player;
using Emberdelve.UI;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// B2 gate: combat HUD and feedback.
    ///   1. combo pips: the authority's read-only chain state (step, total, window) and the HUD pips built from it:
    ///      correct step / total, window open and closed, hidden for ranged weapons.
    ///   2. hit-marker variants by hit location, sourced from the authority's hit (head / body / arms / legs).
    ///   3. wall-stop feedback: the status carries the wall contact point, exactly one spark per wall-stopped swing at
    ///      that point, a camera kick that decays to exactly zero and never changes PlayerLook yaw / pitch or the aim
    ///      pose anything outside rendering reads, and the visual blade clip against level geometry.
    ///   4. steady state allocation (measured with GC.GetAllocatedBytesForCurrentThread).
    ///
    /// Run: Unity -batchmode -nographics -projectPath . -logFile Logs/b2_combathud.log
    ///      -executeMethod Emberdelve.EditorTools.EmberdelveCombatHudGate.Run
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveCombatHudGate
    {
        private const string Pending = "Emberdelve.CombatHudGate.Pending";

        static EmberdelveCombatHudGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("CombatHud_Gate").AddComponent<CombatHudHarness>();
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

    public sealed class CombatHudHarness : MonoBehaviour
    {
        private const string Tag = "[CombatHudGate]";
        private static readonly Vector3 Origin = new Vector3(3000f, 0f, 3000f);

        private int _checks;
        private int _failures;
        private readonly List<UnityEngine.Object> _cleanup = new List<UnityEngine.Object>();
        private readonly List<ICombatEntity> _registered = new List<ICombatEntity>();

        private LocalCombatAuthority _authority;
        private GameConfig _config;
        private float _clock = 9000f;
        private WeaponData _sword;
        private WeaponData _crossbow;
        private HudController _hud;
        private Transform _crosshair;

        // ------------------------------------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------------------------------------

        private sealed class Swinger : ICombatEntity, IActiveWeaponProvider, IWeaponChangeSource, IAimPoseProvider, IPlayerCombatFeedback
        {
            public int CombatId { get; set; }
            public int WeaponChangeCount { get; set; }
            public EntityKind Kind => EntityKind.LocalPlayer;
            public int TeamId => CombatTeams.PlayerTeam;
            public Vector3 Eye = new Vector3(0f, 1.8f, 0f);
            public WeaponData Weapon;
            public readonly List<HitFeedback> Dealt = new List<HitFeedback>();
            public bool ForwardToHud;

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
                yawDegrees = 0f;
                pitchDegrees = 0f;
                return true;
            }

            public void OnDamageTaken(in DamageInfo damage) { }

            public void OnHitDealt(in HitFeedback feedback)
            {
                Dealt.Add(feedback);
                if (ForwardToHud) GameEvents.RaiseDamageDealt(GameEvents.NoPlayer, feedback);
            }
        }

        private sealed class Dummy : ICombatEntity, IHitShapeProvider
        {
            public int CombatId { get; set; }
            public EntityKind Kind => EntityKind.Enemy;
            public int TeamId => CombatTeams.MonsterTeam;
            public readonly List<HitShape> Shapes = new List<HitShape>();
            public Vector3 Center;

            public Vector3 CombatCenter => Center;
            public Collider HeadCollider => null;
            public bool IsDead => false;
            public float CurrentHealth => 100000f;
            public float MaxHealth => 100000f;
            public void ApplyDamage(DamageInfo damage) { }
            public void CollectHitShapes(List<HitShape> shapes) { shapes.AddRange(Shapes); }
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

        private void Near(float expected, float actual, float tolerance, string label) =>
            Check(Mathf.Abs(expected - actual) <= tolerance, $"{label} (expected {expected:F4}, got {actual:F4})");

        private Swinger NewSwinger(WeaponData weapon)
        {
            Swinger s = new Swinger { CombatId = CombatRegistry.AllocateId(), Weapon = weapon };
            s.Eye = Origin + s.Eye;
            CombatRegistry.Register(s.CombatId, s);
            _registered.Add(s);
            return s;
        }

        private Dummy AddBody(Vector3 center, HitLocation location, float radius = 0.33f)
        {
            center += Origin;
            Dummy d = new Dummy { CombatId = CombatRegistry.AllocateId(), Center = center };
            d.Shapes.Add(new HitShape(location, center, center, radius));
            CombatRegistry.Register(d.CombatId, d);
            _registered.Add(d);
            return d;
        }

        private GameObject AddBox(Vector3 center, Vector3 size, string layerName, bool trigger = false)
        {
            GameObject go = new GameObject("CombatHudGate_" + layerName);
            go.transform.position = center + Origin;
            BoxCollider box = go.AddComponent<BoxCollider>();
            box.size = size;
            box.isTrigger = trigger;
            int layer = LayerMask.NameToLayer(layerName);
            if (layer >= 0) go.layer = layer;
            _cleanup.Add(go);
            Physics.SyncTransforms();
            return go;
        }

        private MeleeSwingIntent Intent(Swinger who, int step)
        {
            int pattern = who.Weapon.HasComboChain ? who.Weapon.comboSteps[step].patternIndex : 0;
            return new MeleeSwingIntent(who.CombatId, who.Weapon.weaponId, pattern, step, 0f, 0f, CombatRegistry.NextSequence(who.CombatId));
        }

        private MeleeSwingStatus Finish(int id)
        {
            MeleeSwingStatus status = default;
            float simulated = 0f;
            while (simulated < 4f)
            {
                _clock += 0.005f;
                simulated += 0.005f;
                _authority.StepSwings();
                if (_authority.TryGetMeleeSwingStatus(id, out status) && status.Finished) break;
            }

            _authority.TryGetMeleeSwingStatus(id, out status);
            return status;
        }

        private MeleeSwingStatus Swing(Swinger who, int step, out bool accepted, out int id)
        {
            accepted = _authority.TryBeginMeleeSwingAsOwner(Intent(who, step), out id, out _);
            return accepted ? Finish(id) : default;
        }

        private void ResetEntities()
        {
            foreach (ICombatEntity e in _registered) CombatRegistry.Unregister(e.CombatId, e);
            _registered.Clear();
            _clock += 5f;
        }

        // ------------------------------------------------------------------------------------------------
        // Run
        // ------------------------------------------------------------------------------------------------

        private IEnumerator Start()
        {
            yield return null;
            yield return null;

            Section("setup", Setup);
            Section("combo state from the authority", ComboState);
            Section("combo pips on the HUD", ComboPips);
            Section("hit marker variants by location", HitMarkers);
            Section("wall point in the swing status", WallPoint);
            Section("wall-stop spark", WallSpark);
            Section("camera kick", CameraKickChecks);
            Section("visual blade clip", BladeClip);
            Section("steady state allocation", Allocation);

            // the spark lives SparkSeconds in real time and then goes back to the pool
            WallStopFeedback lifetime = new GameObject("CombatHud_Lifetime").AddComponent<WallStopFeedback>();
            _cleanup.Add(lifetime.gameObject);
            lifetime.Configure(null);
            lifetime.Play(StatusWithWall(Origin));
            Check(lifetime.ActiveSparkCount == 1, "a played spark is active");
            yield return new WaitForSecondsRealtime(WallStopFeedback.SparkSeconds + 0.25f);
            Check(lifetime.ActiveSparkCount == 0, "the spark returned to the pool after its lifetime");

            ResetEntities();
            foreach (UnityEngine.Object o in _cleanup) if (o != null) Destroy(o);

            Debug.Log($"{Tag} {(_failures == 0 ? "PASS" : "FAIL")} checks={_checks} failures={_failures}");
            EmberdelveGateExit.Exit(_failures == 0 ? 0 : 1);
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
            _crossbow = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_EmberCrossbow.asset");
            Check(_sword != null && _crossbow != null, "sword and crossbow assets are available");
            if (_sword == null || _crossbow == null) throw new InvalidOperationException("weapon assets missing");

            _hud = FindFirstObjectByType<HudController>(FindObjectsInactive.Include);
            Check(_hud != null, "a HudController is live in the Level01 scene");
            if (_hud == null) throw new InvalidOperationException("no HUD");

            foreach (Canvas canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (canvas.name != "Emberdelve_Hud") continue;
                _crosshair = canvas.transform.Find("HudRoot/Crosshair");
            }

            Check(_crosshair != null, "the HUD has HudRoot/Crosshair");

            _config = ScriptableObject.CreateInstance<GameConfig>();
            _cleanup.Add(_config);
            _config.levelMask = LayerMask.GetMask(GameConfig.LevelLayerName);
            _config.hitLocations = null;

            GameObject go = new GameObject("CombatHudGate_Authority");
            _cleanup.Add(go);
            _authority = go.AddComponent<LocalCombatAuthority>();
            typeof(LocalCombatAuthority).GetField("config", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_authority, _config);
            _authority.SetClock(() => _clock);
            _authority.AutoStepSwings = false;
            _authority.ImpactZonesEnabled = false;
            Check(_sword.HasComboChain && _sword.ComboLength == 3, "the sword chain has three steps");
            Check(!_crossbow.HasComboChain, "the crossbow has no combo chain");
        }

        // ------------------------------------------------------------------------------------------------
        // 1. combo state
        // ------------------------------------------------------------------------------------------------

        private void ComboState()
        {
            IComboStateSource source = _authority;
            Swinger who = NewSwinger(_sword);
            AddBody(new Vector3(0f, 1.5f, 1.8f), HitLocation.Body);

            Check(source.TryGetComboState(who.CombatId, out int step, out int total, out float window) && step == 0 && total == 3 && window == 0f,
                $"idle sword: step 0 of 3, no window ({step}/{total}, {window:F3})");

            Check(_authority.TryBeginMeleeSwingAsOwner(Intent(who, 0), out int id0, out _), "step 1 begins");
            Check(source.TryGetComboState(who.CombatId, out step, out total, out window) && step == 1 && total == 3 && window > 0f,
                $"step 1 running: pip 1 of 3, window counting ({step}/{total}, {window:F3})");
            Finish(id0);
            Check(source.TryGetComboState(who.CombatId, out step, out total, out window) && step == 1 && total == 3 && window > 0f && window <= _sword.comboWindow + 1e-3f,
                $"step 1 done: still 1 of 3, window open and at most the weapon's {_sword.comboWindow:F2} s ({step}/{total}, {window:F3})");
            Check(_authority.TryGetExpectedComboStep(who.CombatId, out int expected) && expected == 1, "the state read agrees with TryGetExpectedComboStep");

            // reading is free of side effects
            for (int i = 0; i < 5; i++) source.TryGetComboState(who.CombatId, out _, out _, out _);
            Check(_authority.TryGetExpectedComboStep(who.CombatId, out expected) && expected == 1, "reading the combo state does not touch the chain");

            // window runs out
            float remaining = window;
            _clock += remaining + 0.05f;
            Check(source.TryGetComboState(who.CombatId, out step, out total, out window) && step == 0 && total == 3 && window == 0f,
                $"after the window: back to 0 of 3 ({step}/{total}, {window:F3})");

            // full chain
            Check(_authority.TryBeginMeleeSwingAsOwner(Intent(who, 0), out id0, out _), "chain restarts at step 1");
            Finish(id0);
            Check(_authority.TryBeginMeleeSwingAsOwner(Intent(who, 1), out int id1, out _), "step 2 begins inside the window");
            Check(source.TryGetComboState(who.CombatId, out step, out total, out _) && step == 2 && total == 3, $"step 2 running: 2 of 3 ({step}/{total})");
            Finish(id1);
            Check(_authority.TryBeginMeleeSwingAsOwner(Intent(who, 2), out int id2, out _), "step 3 (finisher) begins");
            Check(source.TryGetComboState(who.CombatId, out step, out total, out window) && step == 3 && total == 3 && window == 0f,
                $"finisher running: 3 of 3, no window ({step}/{total}, {window:F3})");
            Finish(id2);
            Check(source.TryGetComboState(who.CombatId, out step, out total, out window) && step == 0 && total == 3 && window == 0f,
                $"after the finisher the chain is complete: 0 of 3 ({step}/{total}, {window:F3})");

            // not a chain weapon / unknown id
            Swinger archer = NewSwinger(_crossbow);
            Check(!source.TryGetComboState(archer.CombatId, out _, out _, out _), "a ranged weapon has no combo state (HUD hides)");
            Check(!source.TryGetComboState(-12345, out _, out _, out _), "an unknown combat id has no combo state");

            // a weapon swap drops the chain
            _clock += 3f;
            Check(_authority.TryBeginMeleeSwingAsOwner(Intent(who, 0), out id0, out _), "chain starts again before the swap");
            Finish(id0);
            who.Weapon = _crossbow;
            who.WeaponChangeCount++;
            Check(!source.TryGetComboState(who.CombatId, out _, out _, out _), "after swapping to the crossbow the state is gone");
            who.Weapon = _sword;
            who.WeaponChangeCount++;
            Check(source.TryGetComboState(who.CombatId, out step, out total, out _) && step == 0 && total == 3, "back on the sword: the old chain did not survive the swap (0 of 3)");

            ResetEntities();
        }

        // ------------------------------------------------------------------------------------------------
        // 1b. the HUD pips
        // ------------------------------------------------------------------------------------------------

        private Image Pip(int i) => _crosshair.Find("ComboPips/ComboPip" + i).GetComponent<Image>();

        private void ComboPips()
        {
            Transform pips = _crosshair.Find("ComboPips");
            Check(pips != null, "Crosshair/ComboPips exists");
            Check(_crosshair.Find("ComboPips/ComboWindow") != null, "ComboPips/ComboWindow exists");
            for (int i = 0; i < HudController.MaxComboPips; i++) Check(_crosshair.Find("ComboPips/ComboPip" + i) != null, $"ComboPip{i} exists");

            _hud.ApplyComboPips(0, 0, 0f);
            Check(!_hud.ComboPipsVisible, "no chain (ranged / unarmed): the pips are hidden");

            _hud.ApplyComboPips(1, 3, 0.8f);
            Check(_hud.ComboPipsVisible, "sword chain: the pips show");
            Check(_hud.ComboPipsShown == 3 && _hud.ComboPipsLit == 1, $"1 of 3 pips lit ({_hud.ComboPipsLit}/{_hud.ComboPipsShown})");
            Check(Pip(0).gameObject.activeSelf && Pip(1).gameObject.activeSelf && Pip(2).gameObject.activeSelf && !Pip(3).gameObject.activeSelf && !Pip(4).gameObject.activeSelf,
                "exactly three pips are drawn for a three-step chain");
            Color litOpen = Pip(0).color;
            Check(Pip(0).color.a > 0.99f && Pip(1).color.a < 0.7f && Pip(2).color.a < 0.7f, "pip 1 is lit, pips 2 and 3 are dim");
            Check(_hud.ComboWindowOpen, "window open: the window bar shows");
            float barOpen = _crosshair.Find("ComboPips/ComboWindow").GetComponent<RectTransform>().sizeDelta.x;
            float full = 3f * 16f - 6f;
            Near(full * 0.8f, barOpen, full * 0.06f, "the window bar is about 80 percent of its full width");

            _hud.ApplyComboPips(2, 3, 0.25f);
            Check(_hud.ComboPipsLit == 2 && Pip(0).color.a > 0.99f && Pip(1).color.a > 0.99f && Pip(2).color.a < 0.7f, "2 of 3 lit");
            float barLow = _crosshair.Find("ComboPips/ComboWindow").GetComponent<RectTransform>().sizeDelta.x;
            Check(barLow < barOpen, "the window bar shrinks as the window runs out");

            _hud.ApplyComboPips(1, 3, 0f);
            Check(!_hud.ComboWindowOpen, "window closed: the bar is hidden");
            Check(Pip(0).color != litOpen, "window closed: the lit pip is dimmed relative to an open window");

            _hud.ApplyComboPips(3, 3, 0f);
            Check(_hud.ComboPipsLit == 3 && !_hud.ComboWindowOpen, "finisher: 3 of 3 lit, no window");

            _hud.ApplyComboPips(0, 3, 0f);
            Check(_hud.ComboPipsVisible && _hud.ComboPipsLit == 0 && !_hud.ComboWindowOpen, "idle chain weapon: pips visible, none lit, no window");

            _hud.ApplyComboPips(2, 2, 1f);
            Check(_hud.ComboPipsShown == 2 && !Pip(2).gameObject.activeSelf, "a two-step chain draws two pips");

            _hud.ApplyComboPips(9, 9, 1f);
            Check(_hud.ComboPipsShown == HudController.MaxComboPips && _hud.ComboPipsLit == HudController.MaxComboPips, "out-of-range values clamp to the pip capacity");

            _hud.ApplyComboPips(1, 1, 1f);
            Check(!_hud.ComboPipsVisible, "a one-step 'chain' is not a combo: hidden");

            _hud.ApplyComboPips(0, 0, 0f);
            Check(!_hud.ComboPipsVisible, "hidden again for a ranged weapon");
        }

        // ------------------------------------------------------------------------------------------------
        // 2. hit marker variants
        // ------------------------------------------------------------------------------------------------

        private void HitMarkers()
        {
            Transform marker = _crosshair.Find("HitMarker");
            Check(marker != null && marker.Find("SlashA") != null && marker.Find("SlashB") != null, "Crosshair/HitMarker keeps SlashA and SlashB");

            // pure style table
            HudController.HitMarkerStyle head = HudController.HitMarkerStyleFor(HitLocation.Head, true);
            HudController.HitMarkerStyle body = HudController.HitMarkerStyleFor(HitLocation.Body, false);
            HudController.HitMarkerStyle unknown = HudController.HitMarkerStyleFor(HitLocation.Unspecified, false);
            HudController.HitMarkerStyle arms = HudController.HitMarkerStyleFor(HitLocation.Arms, false);
            HudController.HitMarkerStyle hands = HudController.HitMarkerStyleFor(HitLocation.Hands, false);
            HudController.HitMarkerStyle legs = HudController.HitMarkerStyleFor(HitLocation.Legs, false);
            HudController.HitMarkerStyle feet = HudController.HitMarkerStyleFor(HitLocation.Feet, false);
            Check(head.Color != body.Color && head.Scale > body.Scale && head.Duration > body.Duration, "head: distinct colour, larger, held longer than body");
            Check(unknown.Color == body.Color && unknown.Scale == body.Scale, "an unknown location looks like the original body marker");
            Check(arms.Scale < body.Scale && legs.Scale < arms.Scale && arms.Color.a < 1f && legs.Color.a < arms.Color.a, "limbs are subtler than body: arms smaller and fainter, legs smaller still");
            Check(hands.Color == arms.Color && feet.Color == legs.Color, "hands share the arms look, feet the legs look");
            Check(HudController.HitMarkerStyleFor(HitLocation.Unspecified, true).Color == head.Color, "a headshot flag alone also gets the head marker");

            // through the authority: a swing hits a shape at each location, the feedback carries it, the HUD marker follows
            _authority.ImpactZonesEnabled = false;
            foreach ((HitLocation location, HudController.HitMarkerStyle style) c in new[]
            {
                (HitLocation.Head, head), (HitLocation.Body, body), (HitLocation.Arms, arms), (HitLocation.Legs, legs)
            })
            {
                Swinger who = NewSwinger(_sword);
                who.ForwardToHud = true;
                AddBody(new Vector3(0f, 1.5f, 1.8f), c.location);
                MeleeSwingStatus st = Swing(who, 0, out bool accepted, out _);
                Check(accepted && st.Hits != null && st.Hits.Count == 1, $"{c.location}: the swing hits the shape once");
                Check(st.Hits != null && st.Hits.Count == 1 && st.Hits[0].Location == c.location, $"{c.location}: the authority located the hit at {c.location}");
                Check(who.Dealt.Count == 1 && who.Dealt[0].Location == c.location, $"{c.location}: HitFeedback carries the location ({(who.Dealt.Count == 1 ? who.Dealt[0].Location.ToString() : "none")})");
                Check(who.Dealt.Count == 1 && who.Dealt[0].Headshot == (c.location == HitLocation.Head), $"{c.location}: headshot flag only for the head");
                Check(_hud.HitMarkerVisible && _hud.LastHitMarkerLocation == c.location, $"{c.location}: the HUD shows its marker for that location");
                Check(_hud.HitMarkerColor == c.style.Color, $"{c.location}: marker colour is the variant's");
                Near(c.style.Scale, _hud.HitMarkerScale, 1e-4f, $"{c.location}: marker scale is the variant's");
                Check(marker.Find("SlashA").GetComponent<Image>().color == marker.Find("SlashB").GetComponent<Image>().color, $"{c.location}: both slashes share the colour");
                ResetEntities();
            }
        }

        // ------------------------------------------------------------------------------------------------
        // 3. wall point
        // ------------------------------------------------------------------------------------------------

        private static MeleeSwingStatus StatusWithWall(Vector3 point) =>
            new MeleeSwingStatus(1, 1, SwingPhase.Finished, SwingEndReason.WallStop, 0.2f, 5, 10, 5, new List<MeleeSwingHit>(),
                wallPoint: point, wallNormal: Vector3.back, hasWallPoint: true);

        private void WallPoint()
        {
            // a free swing has no wall point
            {
                Swinger who = NewSwinger(_sword);
                MeleeSwingStatus free = Swing(who, 0, out bool ok, out _);
                Check(ok && !free.WallStopped && !free.HasWallPoint && free.WallPoint == Vector3.zero, "a free swing reports no wall point");
                ResetEntities();
            }

            // a wall stop reports the contact point on the wall
            {
                Swinger who = NewSwinger(_sword);
                GameObject wall = AddBox(new Vector3(0f, 1.8f, 1.0f), new Vector3(8f, 4f, 0.3f), GameConfig.LevelLayerName);
                MeleeSwingStatus st = Swing(who, 0, out bool ok, out _);
                Check(ok && st.WallStopped, $"the swing is stopped by the wall ({st.EndReason})");
                Check(st.HasWallPoint, "the wall-stopped status has a wall point");
                Bounds b = wall.GetComponent<BoxCollider>().bounds;
                b.Expand(0.4f);
                Check(st.HasWallPoint && b.Contains(st.WallPoint), $"the wall point lies on (or in) the wall ({st.WallPoint})");
                Check(st.HasWallPoint && Mathf.Abs(st.WallPoint.z - (Origin.z + 0.85f)) < 0.3f, $"the wall point is at the wall's near face z={st.WallPoint.z - Origin.z:F2}");
                ResetEntities();
                DestroyImmediate(wall);
            }

            // a prop on the interact layer is not a wall: no point
            {
                Swinger who = NewSwinger(_sword);
                GameObject prop = AddBox(new Vector3(0f, 1.8f, 1.0f), new Vector3(8f, 4f, 0.3f), GameConfig.InteractLayerName);
                MeleeSwingStatus st = Swing(who, 0, out _, out _);
                Check(!st.WallStopped && !st.HasWallPoint, "an interact-layer prop neither stops the swing nor yields a wall point");
                ResetEntities();
                DestroyImmediate(prop);
            }
        }

        // ------------------------------------------------------------------------------------------------
        // 3b. spark
        // ------------------------------------------------------------------------------------------------

        private GameObject NewKickRig(out PlayerLook look, out WallStopFeedback feedback)
        {
            GameObject rig = new GameObject("CombatHudGate_KickRig");
            rig.transform.position = Origin + new Vector3(0f, 1.7f, 0f);
            rig.transform.rotation = Quaternion.Euler(10f, 35f, 0f);
            look = rig.AddComponent<PlayerLook>();
            feedback = new GameObject("CombatHudGate_Feedback").AddComponent<WallStopFeedback>();
            feedback.Configure(rig.transform);
            _cleanup.Add(rig);
            _cleanup.Add(feedback.gameObject);
            return rig;
        }

        private void WallSpark()
        {
            GameObject rig = NewKickRig(out PlayerLook look, out WallStopFeedback feedback);
            List<Vector3> events = new List<Vector3>();
            Action<Vector3> handler = p => events.Add(p);
            WallStopFeedback.SparkSpawned += handler;
            try
            {
                Vector3 wallPoint = Origin + new Vector3(0.3f, 1.6f, 0.85f);
                MeleeSwingStatus st = StatusWithWall(wallPoint);
                feedback.Play(st);
                Check(events.Count == 1, $"one wall-stopped swing raises exactly one spark event ({events.Count})");
                Check(events.Count == 1 && events[0] == wallPoint, "the spark is at the status' wall point");
                Check(feedback.SparksPlayed == 1 && feedback.KicksPlayed == 1, "one spark and one kick were played");
                Check(feedback.ActiveSparkCount == 1, "one spark is active");

                // a status without a wall point plays the kick only
                MeleeSwingStatus noPoint = new MeleeSwingStatus(2, 1, SwingPhase.Finished, SwingEndReason.WallStop, 0.2f, 5, 10, 5, new List<MeleeSwingHit>());
                feedback.Play(noPoint);
                Check(events.Count == 1 && feedback.SparksPlayed == 1 && feedback.KicksPlayed == 2, "a status without a wall point raises no spark");

                // pool: many sparks never create more objects
                int children = feedback.transform.childCount;
                for (int i = 0; i < WallStopFeedback.PoolSize * 3; i++) feedback.Play(st);
                Check(feedback.transform.childCount == children && children == WallStopFeedback.PoolSize, "the spark pool is fixed: replaying recycles, nothing is created");
                Check(feedback.ActiveSparkCount <= WallStopFeedback.PoolSize, "never more active sparks than pool slots");
            }
            finally
            {
                WallStopFeedback.SparkSpawned -= handler;
            }

            Destroy(rig);
        }

        // ------------------------------------------------------------------------------------------------
        // 3c. camera kick
        // ------------------------------------------------------------------------------------------------

        private static bool Exact(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;

        private static float FieldFloat(object target, string name) =>
            (float)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

        private void CameraKickChecks()
        {
            GameObject rig = NewKickRig(out PlayerLook look, out WallStopFeedback feedback);
            CameraKick kick = feedback.Kick;
            Check(kick != null, "the feedback attached a CameraKick to the aim transform");

            float yaw0 = FieldFloat(look, "_yaw");
            float pitch0 = FieldFloat(look, "_pitch");
            Vector3 origin0 = look.AimOrigin;
            Vector3 dir0 = look.AimDirection;
            SwingGeometry.TryAimAngles(dir0, out float aimYaw0, out float aimPitch0);
            Vector3 localPos0 = rig.transform.localPosition;
            Quaternion localRot0 = rig.transform.localRotation;

            feedback.Play(StatusWithWall(Origin));
            Check(kick.IsActive && kick.PitchDegrees > 0f, $"the kick has a pitch impulse ({kick.PitchDegrees:F2} deg)");
            Near(WallStopFeedback.KickPitchDegrees, kick.PitchDegrees, 1e-4f, "the pitch impulse is the authored one");

            // outside rendering nothing that reads the camera sees the kick
            SwingGeometry.TryAimAngles(look.AimDirection, out float aimYaw1, out float aimPitch1);
            Check(Exact(look.AimOrigin, origin0) && Exact(look.AimDirection, dir0) && aimYaw1 == aimYaw0 && aimPitch1 == aimPitch0,
                "the authority pose (PlayerLook aim origin, direction, yaw, pitch) is unchanged while a kick is active");
            Check(FieldFloat(look, "_yaw") == yaw0 && FieldFloat(look, "_pitch") == pitch0 && look.Pitch == pitch0, "PlayerLook yaw and pitch are untouched");

            // inside the render window the view moves, then the exact pose comes back
            kick.BeginRender();
            Check(kick.IsApplied, "the kick is applied for rendering");
            float shown = Vector3.Angle(dir0, look.AimDirection);
            Check(shown > 0.5f && shown < CameraKick.MaxPitchDegrees + 0.01f, $"the rendered view is tilted by the kick ({shown:F2} deg)");
            Check(FieldFloat(look, "_yaw") == yaw0 && FieldFloat(look, "_pitch") == pitch0, "PlayerLook yaw and pitch are untouched while the kick is rendered");
            kick.BeginRender();
            kick.EndRender();
            Check(!kick.IsApplied && Vector3.Distance(rig.transform.localPosition, localPos0) < 1e-6f && Quaternion.Angle(rig.transform.localRotation, localRot0) < 1e-4f,
                "after the render window the exact local pose is back (a second BeginRender was idempotent)");
            Check(Exact(look.AimOrigin, origin0) && Exact(look.AimDirection, dir0), "aim origin and direction are bit-identical after the render window");

            // decay: monotonic, 95 percent gone in 0.3 s, exactly zero in the end, pose never changes
            float last = kick.PitchDegrees;
            float start = last;
            bool monotonic = true;
            int ticksTo95 = -1;
            int ticksToZero = -1;
            for (int i = 1; i <= 240; i++)
            {
                kick.Tick(1f / 60f);
                if (kick.PitchDegrees > last + 1e-6f) monotonic = false;
                last = kick.PitchDegrees;
                if (ticksTo95 < 0 && Mathf.Abs(kick.PitchDegrees) < start * 0.05f) ticksTo95 = i;
                if (ticksToZero < 0 && !kick.IsActive) ticksToZero = i;
            }

            Check(monotonic, "the kick decays monotonically");
            Check(ticksTo95 > 0 && ticksTo95 <= 18, $"95 percent of the kick is gone within 0.3 s ({ticksTo95} ticks at 60 Hz)");
            Check(ticksToZero > 0 && ticksToZero <= 60, $"the kick reaches exactly zero within a second ({ticksToZero} ticks)");
            Check(kick.PitchDegrees == 0f && kick.LocalOffset == Vector3.zero && !kick.IsActive, "at rest the kick is exactly zero");
            Check(Exact(look.AimOrigin, origin0) && Exact(look.AimDirection, dir0) && FieldFloat(look, "_yaw") == yaw0 && FieldFloat(look, "_pitch") == pitch0,
                "after the whole kick the aim pose and PlayerLook yaw / pitch are exactly what they were");
            kick.BeginRender();
            Check(!kick.IsApplied, "a zero kick writes nothing to the transform");

            // clamps
            for (int i = 0; i < 20; i++) kick.Impulse(1.1f, new Vector3(0f, 0f, -0.03f));
            Check(kick.PitchDegrees <= CameraKick.MaxPitchDegrees + 1e-5f, $"stacked pitch is clamped ({kick.PitchDegrees:F2})");
            Check(kick.LocalOffset.magnitude <= CameraKick.MaxOffsetMetres + 1e-5f, $"stacked offset is clamped ({kick.LocalOffset.magnitude:F3} m)");
            kick.Tick(5f);
            Check(!kick.IsActive, "a long frame clears the kick");

            // a render event for a different camera does not apply the kick (reflection into the handler would need a Camera: the guard is the camera match)
            Destroy(rig);
        }

        // ------------------------------------------------------------------------------------------------
        // 3d. visual blade clip
        // ------------------------------------------------------------------------------------------------

        private void BladeClip()
        {
            int level = LayerMask.GetMask(GameConfig.LevelLayerName);
            Vector3 hand = Origin + new Vector3(0f, 1.8f, 0f);

            Near(1.5f, WeaponVisualRig.ClipBladeLength(hand, Vector3.forward, 1.5f, level), 1e-5f, "no wall: the whole blade is drawn");

            GameObject wall = AddBox(new Vector3(0f, 1.8f, 1.0f), new Vector3(8f, 4f, 0.3f), GameConfig.LevelLayerName);
            Near(0.85f, WeaponVisualRig.ClipBladeLength(hand, Vector3.forward, 1.5f, level), 0.02f, "a wall 0.85 m ahead clips the blade at the wall face");
            Near(1.5f, WeaponVisualRig.ClipBladeLength(hand, Vector3.up, 1.5f, level), 1e-5f, "a blade pointing away from the wall is not clipped");
            Near(0.4f, WeaponVisualRig.ClipBladeLength(hand, Vector3.forward, 0.4f, level), 1e-5f, "a blade shorter than the wall distance is not clipped");
            Near(1.5f, WeaponVisualRig.ClipBladeLength(hand, Vector3.forward, 1.5f, 0), 1e-5f, "mask 0 turns the clip off");
            Near(0f, WeaponVisualRig.ClipBladeLength(Origin + new Vector3(0f, 1.8f, 1.0f), Vector3.forward, 1.5f, level), 1e-5f, "a hand buried in the wall draws no blade");
            DestroyImmediate(wall);

            GameObject prop = AddBox(new Vector3(0f, 1.8f, 1.0f), new Vector3(8f, 4f, 0.3f), GameConfig.InteractLayerName);
            Near(1.5f, WeaponVisualRig.ClipBladeLength(hand, Vector3.forward, 1.5f, level), 1e-5f, "an interact-layer prop does not clip the blade (same mask as the authority)");
            DestroyImmediate(prop);

            GameObject trigger = AddBox(new Vector3(0f, 1.8f, 1.0f), new Vector3(8f, 4f, 0.3f), GameConfig.LevelLayerName, true);
            Near(1.5f, WeaponVisualRig.ClipBladeLength(hand, Vector3.forward, 1.5f, level), 1e-5f, "a trigger collider does not clip the blade");
            DestroyImmediate(trigger);

            Check(_authority.SwingBlockerMask.value == level, "the clip mask used by the rig is the authority's swing blocker mask");
        }

        // ------------------------------------------------------------------------------------------------
        // 4. allocation
        // ------------------------------------------------------------------------------------------------

        private void Allocation()
        {
            GameObject rig = NewKickRig(out PlayerLook look, out WallStopFeedback feedback);
            CameraKick kick = feedback.Kick;
            MeleeSwingStatus st = StatusWithWall(Origin);
            int level = LayerMask.GetMask(GameConfig.LevelLayerName);
            Vector3 hand = Origin + new Vector3(0f, 1.8f, 0f);

            // warm up (first-call work is not steady state)
            feedback.Play(st);
            kick.BeginRender(); kick.EndRender(); kick.Tick(0.016f);
            WeaponVisualRig.ClipBladeLength(hand, Vector3.forward, 1.5f, level);
            _hud.ApplyComboPips(1, 3, 0.5f);
            _hud.ApplyComboPips(1, 3, 0.5f);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 500; i++)
            {
                kick.Impulse(0.5f, new Vector3(0f, 0f, -0.01f));
                kick.BeginRender();
                kick.EndRender();
                kick.Tick(1f / 60f);
            }
            long kickBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(kickBytes == 0, $"500 kick impulses / render windows / ticks allocate nothing ({kickBytes} bytes)");

            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 500; i++) WeaponVisualRig.ClipBladeLength(hand, Vector3.forward, 1.5f, level);
            long clipBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(clipBytes == 0, $"500 blade clips allocate nothing ({clipBytes} bytes)");

            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 500; i++) _hud.ApplyComboPips(1, 3, 0.5f);
            long pipBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(pipBytes == 0, $"500 unchanged pip updates allocate nothing ({pipBytes} bytes)");

            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50; i++) feedback.Play(st);
            long sparkBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(sparkBytes == 0, $"50 pooled sparks allocate nothing ({sparkBytes} bytes)");

            Destroy(rig);
        }
    }
}
