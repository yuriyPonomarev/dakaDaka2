using System;
using System.Collections;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using System.Collections.Generic;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Player;
using Emberdelve.Stats;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Emberdelve.EditorTools
{
    [InitializeOnLoad]
    public static class EmberdelveDrawSpeedGate
    {
        private const string Pending = "Emberdelve.DrawSpeed.Pending";
        private const string Done = "Emberdelve.DrawSpeed.Done";
        private const string Code = "Emberdelve.DrawSpeed.Code";
        private static double _requestedAt;
        static EmberdelveDrawSpeedGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("D4b3_DrawSpeedGate").AddComponent<DrawSpeedHarness>();
                }
                if (state != PlayModeStateChange.ExitingPlayMode || !SessionState.GetBool(Done, false)) return;
                int code = SessionState.GetInt(Code, 1);
                SessionState.SetBool(Done, false);
                EditorApplication.delayCall += () => EmberdelveGateExit.Exit(code);
            };
        }
        [MenuItem("Emberdelve/Validation/Local DEX Draw Speed (D4b3)")]
        public static void Run()
        {
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Run from edit mode.");
                for (int i = 0; i < SceneManager.sceneCount; i++)
                    if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Save open scenes before running the gate.");
                EmberdelveBootstrapMenu.BuildDataOnly();
                SessionState.SetBool(Pending, true);
                SessionState.SetBool(Done, false);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                _requestedAt = EditorApplication.timeSinceStartup;
                EditorApplication.update += Watchdog;
                EditorApplication.isPlaying = true;
            }
            catch (Exception exception) { Debug.LogException(exception); EmberdelveGateExit.Exit(1); }
        }
        private static void Watchdog()
        {
            if (EditorApplication.isPlaying) { EditorApplication.update -= Watchdog; return; }
            if (EditorApplication.timeSinceStartup - _requestedAt < 45) return;
            EditorApplication.update -= Watchdog;
            SessionState.SetBool(Pending, false);
            Debug.LogError("[DrawSpeed] Play mode did not start.");
            EmberdelveGateExit.Exit(1);
        }
        internal static void Finish(int checks, int failures)
        {
            Debug.Log("[Emberdelve][DrawSpeed] " + (failures == 0 ? "PASS" : "FAIL") + " checks=" + checks + " failures=" + failures);
            SessionState.SetInt(Code, failures == 0 ? 0 : 1);
            SessionState.SetBool(Done, true);
            EditorApplication.isPlaying = false;
        }
    }

    public sealed class DrawSpeedHarness : MonoBehaviour
    {
        private int _checks, _failures;
        private float _clock;
        private LocalCombatAuthority _authority;
        private GameConfig _config;
        private CharacterClassData _warrior, _archer;
        private WeaponData _bow, _crossbow;
        private ItemData _boots;
        private readonly List<UnityEngine.Object> _cleanup = new List<UnityEngine.Object>();
        private readonly List<PlayerRig> _rigs = new List<PlayerRig>();
        private InputSettings _originalSettings, _temporarySettings;
        private Keyboard _keyboard, _previousKeyboard;
        private Mouse _mouse, _previousMouse;
        private int _shots;
        private AttackIntent _lastShot;
        private void Check(bool ok, string label) { _checks++; if (ok) return; _failures++; Debug.LogError("[DrawSpeed] mismatch " + label); }
        private void Close(float actual, float expected, string label, float tolerance = .00003f) =>
            Check(Mathf.Abs(actual - expected) <= tolerance, label + " actual=" + actual + " expected=" + expected);
        private static T Load<T>(string name) where T : UnityEngine.Object => AssetDatabase.LoadAssetAtPath<T>(EmberdelvePaths.Data + "/" + name);
        private T Clone<T>(T source) where T : UnityEngine.Object { T copy = Instantiate(source); _cleanup.Add(copy); return copy; }
        private IEnumerator Start()
        {
            IEnumerator flow = Flow();
            while (true)
            {
                bool more;
                try { more = flow.MoveNext(); }
                catch (Exception exception) { _failures++; Debug.LogException(exception); break; }
                if (!more) break;
                yield return flow.Current;
            }
            RestoreInput(); foreach (UnityEngine.Object item in _cleanup) if (item != null) Destroy(item);
            EmberdelveDrawSpeedGate.Finish(_checks, _failures);
        }
        private IEnumerator Flow()
        {
            _config = Load<GameConfig>("EmberdelveGameConfig.asset"); _warrior = Load<CharacterClassData>("Class_Warrior.asset");
            _archer = Load<CharacterClassData>("Class_Archer.asset"); _bow = Load<WeaponData>("Weapon_HollowBow.asset");
            _crossbow = Load<WeaponData>("Weapon_EmberCrossbow.asset"); _boots = Load<ItemData>("Item_FootpadBoots.asset");
            Check(_config != null && _warrior != null && _archer != null && _bow != null && _crossbow != null && _boots != null, "saved fixtures exist");
            if (_config == null || _warrior == null || _archer == null || _bow == null || _crossbow == null || _boots == null) yield break;
            string before = SharedAssets();
            GameObject host = new GameObject("D4b3_LocalAuthority"); _cleanup.Add(host); _authority = host.AddComponent<LocalCombatAuthority>();
            var data = new SerializedObject(_authority); data.FindProperty("config").objectReferenceValue = _config; data.ApplyModifiedPropertiesWithoutUndo();
            _authority.SetClock(() => _clock); _authority.AutoStepSwings = false; _authority.AdvanceKitsTo(0);
            OwnInput(); for (int i = 0; i < 6; i++) yield return null;
            IEnumerator[] sections = { ClassDraw(_archer, _bow, true), ClassDraw(_warrior, _crossbow, false), FrozenRates(), CancellationPaths() };
            foreach (IEnumerator section in sections) while (section.MoveNext()) yield return section.Current;
            Check(before == SharedAssets(), "all shared class/weapon/profile/item assets unchanged");
            InputSettings original = _originalSettings; RestoreInput();
            Check(ReferenceEquals(InputSystem.settings, original), "exact original InputSettings object restored");
        }
        private string SharedAssets() => EditorJsonUtility.ToJson(_warrior) + EditorJsonUtility.ToJson(_archer) + EditorJsonUtility.ToJson(_bow) +
            EditorJsonUtility.ToJson(_crossbow) + EditorJsonUtility.ToJson(_boots) + EditorJsonUtility.ToJson(_warrior.attributeCurveProfile);
        private void OwnInput()
        {
            _originalSettings = InputSystem.settings; _temporarySettings = Instantiate(_originalSettings);
            _temporarySettings.hideFlags = HideFlags.HideAndDontSave;
            _temporarySettings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            _temporarySettings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            InputSystem.settings = _temporarySettings;
            _previousMouse = Mouse.current; _previousKeyboard = Keyboard.current;
            _mouse = InputSystem.AddDevice<Mouse>(); _keyboard = InputSystem.AddDevice<Keyboard>();
            InputSystem.EnableDevice(_mouse); InputSystem.EnableDevice(_keyboard); _mouse.MakeCurrent(); _keyboard.MakeCurrent();
        }
        private void MouseLeft(bool down) => InputSystem.QueueStateEvent(_mouse, new MouseState
        {
            displayIndex = 0, position = _mouse.position.ReadValue(), delta = Vector2.zero, scroll = Vector2.zero, buttons = (ushort)(down ? 1 : 0)
        });
        private PlayerRig Rig(CharacterClassData source, WeaponData primary)
        {
            foreach (PlayerRig old in _rigs) old.GetComponent<RangedCombat>().enabled = false;
            CharacterClassData owner = Clone(source); owner.primaryWeapon = primary; owner.secondaryWeapon = Load<WeaponData>("Weapon_AshenShortSword.asset");
            owner.startingReserveAmmo = owner.maxReserveAmmo = 20; owner.manaRegenDelay = 1000;
            // The no-refill probe needs wounded pools without lawful passive regeneration during its frames.
            // Configure starts the health regen clock eligible; a real fixture wound resets it to zero.
            owner.regenDelay = 1000;
            GameObject body = new GameObject("D4b3_" + source.classId); _cleanup.Add(body); body.transform.position = new Vector3(_rigs.Count * 20, 0, 0);
            PlayerRig rig = body.AddComponent<PlayerRig>(); rig.WireRig(); body.AddComponent<Inventory>(); body.AddComponent<MeleeCombat>();
            body.AddComponent<RangedCombat>(); body.AddComponent<ClassLoadout>(); rig.WireRig();
            GameObject anchor = new GameObject("DrawGate_WeaponAnchor"); anchor.transform.SetParent(body.transform, false);
            var wiring = new SerializedObject(rig.Loadout); wiring.FindProperty("weaponAnchor").objectReferenceValue = anchor.transform; wiring.ApplyModifiedPropertiesWithoutUndo();
            rig.Inventory.Initialize(rig); rig.Equipment.Initialize(rig); rig.ApplyClass(owner, _config); rig.Movement.ExternalDriven = true;
            Check(CombatSenders.Bind(CombatSenderId.ForPlayer(rig.PlayerId), rig.CombatId, out string why), "bind actual owner: " + why);
            Check(rig.MaxHealth > 40f, "fixture has enough health to apply a real nonlethal wound");
            rig.Health.ApplyDamage(new DamageInfo(rig.MaxHealth - 40f, rig.CombatCenter, Vector3.forward,
                rig.CombatCenter + Vector3.forward, 999, EntityKind.Enemy, DamageType.Physical, false, false, 0f));
            Close(rig.CurrentHealth, 40f, "wounded fixture starts at exactly40 health");
            Check(!rig.Health.IsRegenerating && rig.EffectiveStats.HealthRegenDelay == 1000f,
                "owned health delay isolates no-refill from legitimate passive regeneration");
            Check(EmberdelveKitFixtures.SpendMana(rig.Mana, 20), "spend real mana for no-refill checks");
            rig.GetComponent<RangedCombat>().AttackStarted += intent => { _shots++; _lastShot = intent; };
            _rigs.Add(rig); return rig;
        }
        private void NextClock() { _clock += 10; _authority.AdvanceKitsTo(_clock); }
        private IEnumerator Press(PlayerRig rig)
        {
            NextClock(); MouseLeft(false); yield return null;
            MouseLeft(true); yield return null;
            Check(rig.GetComponent<RangedCombat>().IsDrawing, "ordinary owned mouse press enters live Update draw path");
        }
        private IEnumerator Release()
        {
            MouseLeft(false); yield return null; yield return null;
        }
        private void Cleared(PlayerRig rig, string label)
        {
            RangedCombat ranged = rig.GetComponent<RangedCombat>();
            Check(!ranged.IsDrawing, label + " draw inactive"); Close(ranged.Charge, 0, label + " charge reset");
            Close(ranged.ActiveDrawDuration, 0, label + " capture reset"); Close(ranged.ActiveAuthoredDrawTime, 0, label + " authored capture reset");
            float expected = rig.Movement.ResolveWalkSpeed() * rig.GetMoveFlowFactor();
            Close(rig.Movement.ResolveGroundSpeed(false, false), expected, label + " aiming penalty reset", .0001f);
        }
        private void Visual(PlayerRig rig, bool crossbow)
        {
            WeaponVisualRig visual = rig.Loadout.Visual; Check(visual != null, "actual ranged visual exists"); if (visual == null) return;
            Transform line = visual.transform.Find(crossbow ? "Crossbow_String" : "Bow_String");
            Transform arrow = visual.transform.Find(crossbow ? "Crossbow_Bolt" : "Bow_Arrow");
            Check(line != null && arrow != null, "actual string and arrow exist"); if (line == null || arrow == null) return;
            float charge = rig.GetComponent<RangedCombat>().Charge;
            Close(line.localPosition.z, -.02f - .2f * charge, "string consumes same captured progress");
            Close(crossbow ? line.localScale.x : line.localScale.y,
                (crossbow ? .43f : .52f) + .06f * charge, "string stretch consumes same captured progress");
            Close(arrow.localPosition.z, .12f - .22f * charge, "arrow consumes same captured progress");
        }
        private IEnumerator HoldFractions(PlayerRig rig, float duration, bool crossbow)
        {
            RangedCombat ranged = rig.GetComponent<RangedCombat>(); float started = Time.time;
            foreach (float fraction in new[] { .25f, .5f, 1f, 1.2f })
            {
                while (Time.time - started < duration * fraction) yield return null;
                // Frame-quantized checks use the independent literal duration and observed elapsed engine time.
                Close(ranged.Charge, Mathf.Clamp01((Time.time - started) / duration), "quarter/half/full/saturated local charge", .0002f);
                Visual(rig, crossbow);
            }
        }
        private IEnumerator ClassDraw(CharacterClassData source, WeaponData weapon, bool archer)
        {
            PlayerRig rig = Rig(source, weapon); RangedCombat ranged = rig.GetComponent<RangedCombat>();
            IEnumerator press = Press(rig); while (press.MoveNext()) yield return press.Current;
            Close(ranged.ActiveDrawDuration, archer ? .7589285714f : .3f, "saved class local draw duration");
            Close(ranged.ActiveAuthoredDrawTime, archer ? .85f : .3f, "saved authored draw time captured");
            float hp = rig.CurrentHealth, mana = rig.Mana.CurrentMana; int magazine = ranged.Magazine, reserve = ranged.Reserve, shots = _shots, acceptedBefore = _authority.AcceptedCount;
            IEnumerator hold = HoldFractions(rig, archer ? .7589285714f : .3f, !archer); while (hold.MoveNext()) yield return hold.Current;
            IEnumerator release = Release(); while (release.MoveNext()) yield return release.Current;
            Check(_shots == shots + 1 && _authority.AcceptedCount == acceptedBefore + 1 && ranged.Magazine == magazine - 1 && ranged.Reserve == reserve, "valid saturated release is accepted and launches/spends exactly once");
            Close(_lastShot.Charge, 1, "ordinary intent carries old saturated charge only"); Cleared(rig, "valid release");
            for (int i = 0; i < 3; i++) yield return null;
            Check(_shots == shots + 1, "release emits no repeated launch");
            Check(rig.CurrentHealth == hp, "release does not refill health: " + hp + " -> " + rig.CurrentHealth);
            Check(rig.Mana.CurrentMana == mana, "release does not refill mana: " + mana + " -> " + rig.Mana.CurrentMana);
            Check(rig.Inventory.TryAdd(_boots, 1) && rig.Equipment.TryEquipItem(_boots, out _), "actual boots equipped");
            press = Press(rig); while (press.MoveNext()) yield return press.Current;
            Close(ranged.ActiveDrawDuration, archer ? .75f : .2941176471f, "next press uses actual DEX17/11 rate");
            ranged.CancelDraw(); Cleared(rig, "explicit cancel");
            release = Release(); while (release.MoveNext()) yield return release.Current;
            Check(_shots == shots + 1, "cancelled release launches nothing");
            // Fixed charge must retain the existing damage formula regardless of action rate.
            var target = new Target { Point = rig.AimOrigin + rig.AimDirection * 10 };
            AttackIntent intent = new AttackIntent(rig.CombatId, rig.Kind, weapon, .5f, rig.AimOrigin, rig.AimDirection, weapon.rangedRange, 1, 0);
            HitCandidate hit = new HitCandidate(0, target.Point, false);
            DamageInfo damage = DamageResolver.Resolve(weapon, in intent, in hit, target, rig.GetOutgoingDamageMultiplier(weapon));
            Close(damage.Amount, archer ? 31.45408f : 18.6f, "same half-charge damage unchanged", .0002f);
        }
        private void Rate(PlayerRig rig, float factor, bool copy = false)
        {
            if (copy) rig.Class.attributeCurveProfile = Clone(rig.Class.attributeCurveProfile);
            rig.Class.attributeCurveProfile.action = new[] { new CurvePoint(1, factor), new CurvePoint(30, factor) };
            rig.AttributeStats.Initialize(rig.Class); rig.RefreshEquipmentStats();
        }
        private IEnumerator FrozenRates()
        {
            PlayerRig rig = Rig(_archer, _bow); RangedCombat ranged = rig.GetComponent<RangedCombat>(); Rate(rig, .5f, true);
            IEnumerator press = Press(rig); while (press.MoveNext()) yield return press.Current;
            Close(ranged.ActiveDrawDuration, 1.7f, "slow local press capture"); Rate(rig, 2f);
            Close(ranged.ActiveDrawDuration, 1.7f, "fast regear cannot retime an existing slow draw");
            yield return null;
            Check(ranged.Charge > 0 && ranged.Charge < 1, "active capture keeps accumulating rather than restarting");
            ranged.CancelDraw(); Cleared(rig, "slow cancellation");
            IEnumerator release = Release(); while (release.MoveNext()) yield return release.Current;
            press = Press(rig); while (press.MoveNext()) yield return press.Current;
            Close(ranged.ActiveDrawDuration, .425f, "next press captures fast action rate"); Close(ranged.Charge, 0, "repress starts at zero");
            Rate(rig, .5f); Close(ranged.ActiveDrawDuration, .425f, "slow regear cannot stretch active fast draw");
            ranged.CancelDraw(); release = Release(); while (release.MoveNext()) yield return release.Current;
            // A very slow profile makes a real next-frame release unambiguously earlier than the saved .2 threshold.
            Rate(rig, .1f); int shots = _shots, magazine = ranged.Magazine, reserve = ranged.Reserve;
            press = Press(rig); while (press.MoveNext()) yield return press.Current;
            release = Release(); while (release.MoveNext()) yield return release.Current;
            Check(_shots == shots && ranged.Magazine == magazine && ranged.Reserve == reserve, "too-early real release causes no launch or ammo spend");
            Cleared(rig, "too-early release");
        }
        private IEnumerator CancellationPaths()
        {
            PlayerRig rig = Rig(_archer, _bow); RangedCombat ranged = rig.GetComponent<RangedCombat>();
            IEnumerator press = Press(rig); while (press.MoveNext()) yield return press.Current;
            rig.SetInputEnabled(false); Cleared(rig, "ordinary gameplay input disable");
            rig.SetInputEnabled(true); IEnumerator release = Release(); while (release.MoveNext()) yield return release.Current;
            press = Press(rig); while (press.MoveNext()) yield return press.Current;
            rig.Input.SetGameplayInputEnabled(false); yield return null; Cleared(rig, "direct gameplay map disable");
            rig.Input.SetGameplayInputEnabled(true); release = Release(); while (release.MoveNext()) yield return release.Current;
            press = Press(rig); while (press.MoveNext()) yield return press.Current;
            ranged.enabled = false; Cleared(rig, "component disable"); ranged.enabled = true;
            release = Release(); while (release.MoveNext()) yield return release.Current;
            press = Press(rig); while (press.MoveNext()) yield return press.Current;
            Check(EmberdelveKitFixtures.ConsumeAmmo(ranged, 1), "spend a round before reinitialize no-refill check");
            int beforeReinitialize = ranged.Magazine, reserveBeforeReinitialize = ranged.Reserve;
            ranged.Initialize(_bow, 99, 99, -1); Cleared(rig, "component reinitialize");
            Check(ranged.Magazine == beforeReinitialize && ranged.Reserve == reserveBeforeReinitialize,
                "reinitialize cannot refill the authority's known ammo pool");
            release = Release(); while (release.MoveNext()) yield return release.Current;
            press = Press(rig); while (press.MoveNext()) yield return press.Current;
            int magazine = ranged.Magazine, reserve = ranged.Reserve, shots = _shots;
            Check(!rig.Loadout.SwitchToSlot(1), "ordinary swap remains locked while drawing");
            Check(rig.Loadout.ForceSwitchToSlot(1) && rig.Loadout.ForceSwitchToSlot(0), "existing forced weapon change cancels and returns");
            Cleared(rig, "weapon switch"); Check(ranged.Magazine == magazine && ranged.Reserve == reserve, "switch/reinitialize do not refill known ammo pool");
            release = Release(); while (release.MoveNext()) yield return release.Current;
            Check(EmberdelveKitFixtures.ConsumeAmmo(ranged, 1), "spend round for reload cancellation fixture");
            press = Press(rig); while (press.MoveNext()) yield return press.Current;
            Check(_authority.TryStartReload(rig.CombatSender, rig.CombatId, out _), "sender-bound reload accepted during local draw");
            Cleared(rig, "accepted reload"); release = Release(); while (release.MoveNext()) yield return release.Current;
            Check(_shots == shots, "cancelled paths never emit ordinary launch");
            Check(rig.Loadout.ForceSwitchToSlot(1) && rig.Loadout.ForceSwitchToSlot(0), "cancel fixture reload through existing hand-change path");
            press = Press(rig); while (press.MoveNext()) yield return press.Current;
            Check(rig.Skills.TryCastSlot(0), "existing MultiShot skill accepts and cancels ordinary draw");
            Cleared(rig, "skill cancellation"); int afterSkill = ranged.Magazine;
            release = Release(); while (release.MoveNext()) yield return release.Current;
            Check(_shots == shots && ranged.Magazine == afterSkill, "skill cancellation leaves no residual ordinary shot or ammo spend");
            // Use a fresh real rig so the death probe has an ordinary round without granting any back.
            PlayerRig dying = Rig(_archer, _bow); RangedCombat dyingRanged = dying.GetComponent<RangedCombat>();
            press = Press(dying); while (press.MoveNext()) yield return press.Current;
            int deathMagazine = dyingRanged.Magazine;
            dying.ApplyDamage(new DamageInfo(99999, dying.transform.position, Vector3.forward,
                dying.transform.position + Vector3.forward, 0, EntityKind.Enemy, DamageType.Physical, false, false, 0));
            Check(dying.IsDead, "real damage kills the death fixture"); Cleared(dying, "death");
            release = Release(); while (release.MoveNext()) yield return release.Current;
            Check(_shots == shots && dyingRanged.Magazine == deathMagazine && dying.CurrentHealth == 0,
                "death leaves no launch, ammo creation or heal");
        }
        private void RestoreInput()
        {
            if (_mouse != null && _mouse.added) InputSystem.RemoveDevice(_mouse); _mouse = null;
            if (_keyboard != null && _keyboard.added) InputSystem.RemoveDevice(_keyboard); _keyboard = null;
            if (_previousMouse != null && _previousMouse.added) _previousMouse.MakeCurrent(); _previousMouse = null;
            if (_previousKeyboard != null && _previousKeyboard.added) _previousKeyboard.MakeCurrent(); _previousKeyboard = null;
            if (_originalSettings != null) InputSystem.settings = _originalSettings; _originalSettings = null;
            if (_temporarySettings != null) Destroy(_temporarySettings); _temporarySettings = null;
        }
        private void OnDestroy() => RestoreInput();
        private sealed class Target : ICombatEntity
        {
            public Vector3 Point;
            public int CombatId => -1; public EntityKind Kind => EntityKind.Enemy; public int TeamId => CombatTeams.MonsterTeam;
            public Vector3 CombatCenter => Point; public Collider HeadCollider => null; public bool IsDead => false;
            public float CurrentHealth => 100; public float MaxHealth => 100; public void ApplyDamage(DamageInfo damage) { }
        }
    }
}
