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
    public static class EmberdelveReloadSpeedGate
    {
        private const string Pending = "Emberdelve.ReloadSpeed.Pending";
        private const string Done = "Emberdelve.ReloadSpeed.Done";
        private const string Code = "Emberdelve.ReloadSpeed.Code";
        private static double _requestedAt;
        static EmberdelveReloadSpeedGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("D4b2_ReloadSpeedGate").AddComponent<ReloadSpeedHarness>();
                }
                if (state != PlayModeStateChange.ExitingPlayMode || !SessionState.GetBool(Done, false)) return;
                int code = SessionState.GetInt(Code, 1);
                SessionState.SetBool(Done, false);
                EditorApplication.delayCall += () => EmberdelveGateExit.Exit(code);
            };
        }
        [MenuItem("Emberdelve/Validation/DEX Reload Speed (D4b2)")]
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
            Debug.LogError("[ReloadSpeed] Play mode did not start.");
            EmberdelveGateExit.Exit(1);
        }
        internal static void Finish(int checks, int failures)
        {
            Debug.Log("[Emberdelve][ReloadSpeed] " + (failures == 0 ? "PASS" : "FAIL") + " checks=" + checks + " failures=" + failures);
            SessionState.SetInt(Code, failures == 0 ? 0 : 1);
            SessionState.SetBool(Done, true);
            EditorApplication.isPlaying = false;
        }
    }

    public sealed class ReloadSpeedHarness : MonoBehaviour
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
        private void Check(bool ok, string label) { _checks++; if (ok) return; _failures++; Debug.LogError("[ReloadSpeed] mismatch " + label); }
        private void Close(float actual, float expected, string label, float tolerance = .00002f) =>
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
            RestoreInput();
            foreach (UnityEngine.Object item in _cleanup) if (item != null) Destroy(item);
            EmberdelveReloadSpeedGate.Finish(_checks, _failures);
        }
        private IEnumerator Flow()
        {
            _config = Load<GameConfig>("EmberdelveGameConfig.asset");
            _warrior = Load<CharacterClassData>("Class_Warrior.asset"); _archer = Load<CharacterClassData>("Class_Archer.asset");
            _bow = Load<WeaponData>("Weapon_HollowBow.asset"); _crossbow = Load<WeaponData>("Weapon_EmberCrossbow.asset"); _boots = Load<ItemData>("Item_FootpadBoots.asset");
            Check(_config != null && _warrior != null && _archer != null && _bow != null && _crossbow != null && _boots != null,
                "saved class/weapon/item fixtures exist");
            if (_config == null || _warrior == null || _archer == null || _bow == null || _crossbow == null || _boots == null) yield break;
            string before = SharedAssetText();
            Close(_bow.reloadTime, 2.5f, "saved bow reload metadata"); Close(_crossbow.reloadTime, 2.673f, "saved crossbow reload metadata");
            GameObject host = new GameObject("D4b2_Authority"); _cleanup.Add(host); _authority = host.AddComponent<LocalCombatAuthority>();
            var wiring = new SerializedObject(_authority); wiring.FindProperty("config").objectReferenceValue = _config; wiring.ApplyModifiedPropertiesWithoutUndo();
            _authority.SetClock(() => _clock); _authority.AutoStepSwings = false; _authority.AdvanceKitsTo(0);
            CheckClass(_warrior, _crossbow, 0, false);
            CheckClass(_archer, _bow, 20, true);
            CheckFrozenRatesAndCancellation();
            IEnumerator keys = RKeyPath(); while (keys.MoveNext()) yield return keys.Current;
            Check(before == SharedAssetText(), "reload leaves all shared class/weapon/profile/item assets unchanged");
        }
        private string SharedAssetText() => EditorJsonUtility.ToJson(_warrior) + EditorJsonUtility.ToJson(_archer) +
            EditorJsonUtility.ToJson(_bow) + EditorJsonUtility.ToJson(_crossbow) + EditorJsonUtility.ToJson(_boots) +
            EditorJsonUtility.ToJson(_warrior.attributeCurveProfile);
        private PlayerRig Rig(CharacterClassData source, WeaponData primary, float x)
        {
            CharacterClassData owner = Clone(source); owner.primaryWeapon = primary;
            owner.secondaryWeapon = Load<WeaponData>("Weapon_AshenShortSword.asset");
            owner.startingReserveAmmo = owner.maxReserveAmmo = 20;
            // Keep natural regen outside the finite timing scenarios so no-free-refill assertions isolate reload.
            owner.manaRegenDelay = 100;
            GameObject body = new GameObject("D4b2_" + source.classId); _cleanup.Add(body); body.transform.position = new Vector3(x, 0, 0);
            PlayerRig rig = body.AddComponent<PlayerRig>(); rig.WireRig();
            body.AddComponent<Inventory>(); body.AddComponent<MeleeCombat>(); body.AddComponent<RangedCombat>(); body.AddComponent<ClassLoadout>(); rig.WireRig();
            rig.Inventory.Initialize(rig); rig.Equipment.Initialize(rig); rig.ApplyClass(owner, _config); rig.Movement.ExternalDriven = true;
            Check(CombatSenders.Bind(CombatSenderId.ForPlayer(rig.PlayerId), rig.CombatId, out string reason), "bind actual owner: " + reason);
            rig.Health.SetHealth(40); Check(EmberdelveKitFixtures.SpendMana(rig.Mana, 20), "spend real mana before reload checks");
            _rigs.Add(rig); return rig;
        }
        private KitView View(PlayerRig rig)
        {
            Check(_authority.TryGetKit(rig.CombatId, out KitView view), "authority kit exists"); return view;
        }
        private void ResetClock() { _clock = 0; _authority.AdvanceKitsTo(0); }
        private void At(float time) { _clock = time; _authority.AdvanceKitsTo(time); }
        private KitView StartReload(PlayerRig rig, float literalDuration, string label)
        {
            Check(_authority.TryStartReload(rig.CombatSender, rig.CombatId, out string reason), label + ": " + reason);
            KitView view = View(rig); Close(view.ReloadDuration, literalDuration, label + " accepted duration");
            Close(view.ReloadRemaining, literalDuration, label + " remaining"); Close(view.ReloadTime, rig.ActiveWeapon.reloadTime, "authored metadata retained");
            Close(rig.GetComponent<RangedCombat>().ReloadDuration, literalDuration, "display accepted duration");
            Close(rig.GetComponent<RangedCombat>().ReloadProgress, 0, "display starts at zero progress"); return view;
        }
        private void CheckClass(CharacterClassData source, WeaponData weapon, float x, bool archer)
        {
            PlayerRig rig = Rig(source, weapon, x); ResetClock(); RangedCombat ranged = rig.GetComponent<RangedCombat>();
            Close(rig.ActionSpeedFactor, archer ? 1.12f : 1f, "real class rate");
            int rounds = archer ? 2 : 1;
            Check(archer || weapon.magazineSize == 1, "crossbow reload fixture uses its authored single-bolt capacity");
            Check(EmberdelveKitFixtures.ConsumeAmmo(ranged, rounds), $"spend {rounds} owned round(s)"); KitView empty = View(rig);
            float hp = rig.CurrentHealth, mana = rig.Mana.CurrentMana;
            CombatSenderId forged = CombatSenderId.ForPlayer(rig.PlayerId + 10000);
            Check(!_authority.TryStartReload(forged, rig.CombatId, out string forgedReason) && forgedReason.Contains("sender"), "forged sender reload refused");
            KitView unchanged = View(rig);
            Check(!unchanged.Reloading && unchanged.Magazine == empty.Magazine && unchanged.Reserve == empty.Reserve, "forged start changes no ammo or timer");
            KitView accepted = StartReload(rig, archer ? _bow.reloadTime / 1.12f : _crossbow.reloadTime, "bare class reload");
            Check(!_authority.TryStartReload(rig.CombatSender, rig.CombatId, out _), "repeat start rejected");
            Close(View(rig).ReloadDuration, accepted.ReloadDuration, "repeat start cannot restart or retime");
            Check(rig.Inventory.TryAdd(_boots, 1) && rig.Equipment.TryEquipItem(_boots, out _), "actual boots equipped during reload");
            Close(rig.ActionSpeedFactor, archer ? 1.1333333333f : 1.02f, "actual DEX17/11 action rate");
            Close(View(rig).ReloadDuration, accepted.ReloadDuration, "gear revision preserves old accepted duration");
            At(accepted.ReloadDuration * .5f); Close(ranged.ReloadProgress, .5f, "half accepted duration is half progress after regear");
            At(accepted.ReloadDuration - .001f); Check(View(rig).Reloading && View(rig).Magazine == empty.Magazine, "before exact boundary no ammo transfer");
            At(accepted.ReloadDuration); KitView done = View(rig);
            Check(!done.Reloading && done.Magazine == empty.Magazine + rounds && done.Reserve == empty.Reserve - rounds,
                $"exact boundary transfers {rounds} owned round(s) once");
            At(accepted.ReloadDuration + .1f); KitView again = View(rig);
            Check(again.Magazine == done.Magazine && again.Reserve == done.Reserve, "later ticks cannot transfer twice");
            Close(ranged.ReloadProgress, 0, "idle progress reset");
            Check(EmberdelveKitFixtures.ConsumeAmmo(ranged, 1), "spend one round for boots reload"); ResetClock();
            KitView bootsReload = StartReload(rig, archer ? _bow.reloadTime * 15f / 17f : _crossbow.reloadTime / 1.02f, "next reload uses latest boots rate");
            Check(rig.Equipment.TryUnequip(EquipmentSlot.Feet, out _), "remove boots while reload runs");
            Close(View(rig).ReloadDuration, bootsReload.ReloadDuration, "removal cannot slow an accepted reload");
            At(bootsReload.ReloadDuration - .001f);
            Check(View(rig).Reloading && View(rig).Magazine == done.Magazine - 1 && View(rig).Reserve == done.Reserve,
                "second reload cannot transfer its round before the accepted boundary");
            At(bootsReload.ReloadDuration); KitView final = View(rig);
            Check(!final.Reloading && final.Magazine == done.Magazine && final.Reserve == done.Reserve - 1, "boots reload transfers one owned round");
            At(bootsReload.ReloadDuration + .1f);
            Check(View(rig).Magazine == final.Magazine && View(rig).Reserve == final.Reserve,
                "second completed reload transfers once only");
            Check(archer || final.Magazine == 1 && final.Reserve == empty.Reserve - 2,
                "two crossbow reload cycles conserve exactly two reserve bolts");
            Check(rig.CurrentHealth == hp && rig.Mana.CurrentMana == mana, "reload and gear never heal or refill mana");
            Close(weapon.drawTime, archer ? 1.5f : .3f, "authored draw timing remains unchanged");
        }
        private void SetRate(PlayerRig rig, float factor, bool cloneProfile = false)
        {
            if (cloneProfile) rig.Class.attributeCurveProfile = Clone(rig.Class.attributeCurveProfile);
            rig.Class.attributeCurveProfile.action = new[] { new CurvePoint(1, factor), new CurvePoint(30, factor) };
            rig.AttributeStats.Initialize(rig.Class); rig.RefreshEquipmentStats();
        }
        private void CheckFrozenRatesAndCancellation()
        {
            PlayerRig rig = Rig(_archer, _bow, 50); RangedCombat ranged = rig.GetComponent<RangedCombat>();
            Check(EmberdelveKitFixtures.ConsumeAmmo(ranged, 1), "consume one round for slow reload"); ResetClock(); SetRate(rig, .5f, true);
            float hp = rig.CurrentHealth, mana = rig.Mana.CurrentMana;
            KitView slow = StartReload(rig, _bow.reloadTime * 2f, "slow reload accepted"); At(_bow.reloadTime);
            SetRate(rig, 2f); Close(View(rig).ReloadDuration, _bow.reloadTime * 2f, "fast regear/revision cannot retime slow reload");
            Close(ranged.ReloadProgress, .5f, "slow reload progress retains old denominator"); At(slow.ReloadDuration);
            KitView finishedSlow = View(rig); Check(!finishedSlow.Reloading, "slow reload completes at accepted boundary");
            Check(EmberdelveKitFixtures.ConsumeAmmo(ranged, 1), "consume one round for fast reload"); ResetClock();
            KitView fast = StartReload(rig, _bow.reloadTime * .5f, "next reload receives fast rate"); At(_bow.reloadTime * .25f);
            SetRate(rig, .5f); Close(View(rig).ReloadDuration, _bow.reloadTime * .5f, "slow regear/revision cannot stretch fast reload");
            Close(ranged.ReloadProgress, .5f, "fast reload progress retains old denominator"); At(fast.ReloadDuration);
            KitView finishedFast = View(rig); Check(!finishedFast.Reloading && finishedFast.Reserve == finishedSlow.Reserve - 1,
                "fast reload transfers only its single missing round");
            Check(EmberdelveKitFixtures.ConsumeAmmo(ranged, 1), "consume one round for cancellation"); ResetClock();
            KitView cancelledStart = StartReload(rig, _bow.reloadTime * 2f, "new slow reload receives latest factor"); At(.2f);
            Check(!rig.Loadout.SwitchToSlot(1), "ordinary weapon swap stays locked during reload");
            Check(rig.Loadout.ForceSwitchToSlot(1), "existing forced weapon-change path cancels reload");
            Check(!View(rig).Reloading && !ranged.IsReloading, "authority/view both cancel on hand change");
            Check(rig.Loadout.ForceSwitchToSlot(0), "return to original ranged pool"); At(_bow.reloadTime * 2f + 1.2f);
            KitView cancelled = View(rig);
            Check(!cancelled.Reloading && cancelled.Magazine == cancelledStart.Magazine && cancelled.Reserve == cancelledStart.Reserve,
                "cancelled timer never creates or transfers rounds after weapon returns");
            Close(cancelled.ReloadDuration, 0, "cancelled duration cleared");
            Check(rig.CurrentHealth == hp && rig.Mana.CurrentMana == mana, "slow/fast/cancel paths create no health or mana");
        }
        private IEnumerator RKeyPath()
        {
            PlayerRig rig = Rig(_archer, _bow, 80);
            foreach (PlayerRig other in _rigs) if (other != rig) other.GetComponent<RangedCombat>().enabled = false;
            RangedCombat ranged = rig.GetComponent<RangedCombat>();
            Check(rig.Inventory.TryAdd(_boots, 1) && rig.Equipment.TryEquipItem(_boots, out _), "R-key fixture wears actual boots");
            Check(EmberdelveKitFixtures.ConsumeAmmo(ranged, 1), "R-key fixture spends one round"); ResetClock(); KitView before = View(rig);
            _originalSettings = InputSystem.settings; _temporarySettings = Instantiate(_originalSettings);
            _temporarySettings.hideFlags = HideFlags.HideAndDontSave;
            _temporarySettings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            _temporarySettings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            InputSystem.settings = _temporarySettings; _previousKeyboard = Keyboard.current;
            _keyboard = InputSystem.AddDevice<Keyboard>(); InputSystem.EnableDevice(_keyboard); _keyboard.MakeCurrent();
            for (int i = 0; i < 6; i++) yield return null;
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.R)); yield return null; yield return null;
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState()); yield return null; yield return null;
            KitView started = View(rig);
            Check(started.Reloading && ranged.IsReloading && _authority.LastCommandSender == rig.CombatSender,
                "ordinary R key requests sender-bound reload through live RangedCombat");
            Close(started.ReloadDuration, _bow.reloadTime * 15f / 17f, "ordinary R key receives saved bow/DEX17 duration");
            Close(ranged.ReloadProgress, 0, "render frames do not advance frozen authority reload clock");
            At(started.ReloadDuration); KitView done = View(rig);
            Check(!done.Reloading && done.Magazine == before.Magazine + 1 && done.Reserve == before.Reserve - 1,
                "ordinary R reload finishes with exactly one owned ammo transfer");
            InputSettings original = _originalSettings; RestoreInput();
            Check(ReferenceEquals(InputSystem.settings, original), "exact original shared InputSettings object restored");
        }
        private void RestoreInput()
        {
            if (_keyboard != null && _keyboard.added) InputSystem.RemoveDevice(_keyboard); _keyboard = null;
            if (_previousKeyboard != null && _previousKeyboard.added) _previousKeyboard.MakeCurrent(); _previousKeyboard = null;
            if (_originalSettings != null) InputSystem.settings = _originalSettings; _originalSettings = null;
            if (_temporarySettings != null) Destroy(_temporarySettings); _temporarySettings = null;
        }
        private void OnDestroy() => RestoreInput();
    }
}
