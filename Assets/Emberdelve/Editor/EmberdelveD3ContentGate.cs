using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Emberdelve.AI;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Interaction;
using Emberdelve.Items;
using Emberdelve.Level;
using Emberdelve.Player;
using Emberdelve.UI;

namespace Emberdelve.EditorTools
{
    [InitializeOnLoad]
    public static class EmberdelveD3ContentGate
    {
        private const string Pending = "Emberdelve.D3Content.Pending";
        private const string Hold = "Emberdelve.D3Content.Hold";
        private const string Checks = "Emberdelve.D3Content.Checks";
        private const string Failures = "Emberdelve.D3Content.Failures";
        private static int _checks, _failures;
        static EmberdelveD3ContentGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending, false)) return;
                SessionState.SetBool(Pending, false);
                new GameObject("D3Content_Gate").AddComponent<D3ContentHarness>();
            };
        }
        public static void Run() { Begin(false); }
        public static void RunReview() { Begin(true); }
        internal static bool HoldForReview => SessionState.GetBool(Hold, false) && !Application.isBatchMode;
        internal static int PureChecks => SessionState.GetInt(Checks, 0);
        internal static int PureFailures => SessionState.GetInt(Failures, 0);
        private static void Check(bool ok, string message)
        {
            _checks++;
            if (ok) return;
            _failures++;
            Debug.LogError("[D3Content] mismatch " + message);
        }
        private static string Hash(string path)
        {
            using (SHA256 sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path)));
        }
        private static void Begin(bool hold)
        {
            _checks = _failures = 0;
            try
            {
                string[] originals = { "Loot_SupplyChest.asset", "Loot_EmberChest.asset", "Loot_SealedChest.asset" };
                string[] before = new string[originals.Length];
                for (int i = 0; i < originals.Length; i++) before[i] = Hash(EmberdelvePaths.Data + "/" + originals[i]);
                EmberdelveBootstrapMenu.BuildDataOnly();
                string[] owned = { "Item_SmolderingTonic.asset", "Item_GraspwrapGloves.asset", "Loot_JewelerCache.asset", "DungeonLayout_AshenHalls.asset", "EmberdelveItemCatalog.asset" };
                string[] once = new string[owned.Length];
                for (int i = 0; i < owned.Length; i++) once[i] = Hash(EmberdelvePaths.Data + "/" + owned[i]);
                EmberdelveBootstrapMenu.BuildDataOnly();
                for (int i = 0; i < owned.Length; i++) Check(once[i] == Hash(EmberdelvePaths.Data + "/" + owned[i]), "idempotent data builder: " + owned[i]);
                for (int i = 0; i < originals.Length; i++) Check(before[i] == Hash(EmberdelvePaths.Data + "/" + originals[i]), "old loot table bytes unchanged: " + originals[i]);
                DungeonLayoutData layout = EmberdelveAssetLibrary.Layout;
                Check(layout.Validate().Count == 0, "layout validates including the ninth network spawn and new chest");
                Check(layout.chestSpawns.Count == 6, "five original chests plus one appended cache");
                Vector2Int[] originalCells = { new Vector2Int(5,4), new Vector2Int(14,4), new Vector2Int(3,12), new Vector2Int(7,19), new Vector2Int(22,12) };
                for (int i = 0; i < originalCells.Length; i++) Check(layout.chestSpawns[i].cell == originalCells[i] && layout.chestSpawns[i].seed == (i + 1) * 101, "original chest index, position and salt preserved: " + i);
                ChestSpawnSpec added = layout.chestSpawns[5];
                Check(added.cell == new Vector2Int(6,5) && added.seed == 606 && added.lootTable == EmberdelveAssetLibrary.JewelerLoot, "the appended cache points at the real jeweler table");
                Check(Reachable(layout.BuildWalkableGrid(), layout.playerStartCell, new Vector2Int(6,4)), "the cache approach is reachable from the normal entry");
                Check(EmberdelveAssetLibrary.Gloves.moveSpeedPenalty == 3f, "gloves author three movement points (one percent base)");
                ItemData tonic = EmberdelveAssetLibrary.SmolderingTonic;
                Check(tonic != null && tonic.itemId == "smoldering_tonic" && tonic.healAmount == 45 && tonic.useChannelSeconds == 1.8f && tonic.healOverTimeSeconds == 4f && tonic.maxStack == 2, "authored tonic: two per stack, 1.8s drink then 45 HP over 4s");
                Check(EmberdelveAssetLibrary.Draught.useChannelSeconds == 0f && EmberdelveAssetLibrary.Draught.healOverTimeSeconds == 0f, "old blood draught remains instant");
                Check(EmberdelveAssetLibrary.JewelerLoot.Validate().Count == 0 && EmberdelveAssetLibrary.JewelerLoot.maxRolls == 4 && EmberdelveAssetLibrary.JewelerLoot.nothingWeight == 0, "the new four-entry cache is a valid loot table");
            }
            catch (Exception exception) { _failures++; Debug.LogException(exception); }
            SessionState.SetInt(Checks, _checks);
            SessionState.SetInt(Failures, _failures);
            SessionState.SetBool(Hold, hold);
            SessionState.SetBool(Pending, true);
            EditorSceneManager.OpenScene(EmberdelvePaths.DungeonScenePath);
            EditorApplication.EnterPlaymode();
        }
        private static bool Reachable(bool[,] grid, Vector2Int from, Vector2Int to)
        {
            var visited = new HashSet<Vector2Int>();
            var queue = new Queue<Vector2Int>();
            queue.Enqueue(from);
            Vector2Int[] steps = { Vector2Int.left, Vector2Int.right, Vector2Int.up, Vector2Int.down };
            while (queue.Count > 0)
            {
                Vector2Int cell = queue.Dequeue();
                if (cell.x < 0 || cell.y < 0 || cell.x >= grid.GetLength(0) || cell.y >= grid.GetLength(1) || !grid[cell.x, cell.y] || !visited.Add(cell)) continue;
                if (cell == to) return true;
                foreach (Vector2Int step in steps) queue.Enqueue(cell + step);
            }
            return false;
        }
    }

    public sealed class D3ContentHarness : MonoBehaviour
    {
        private int _checks, _failures;
        private Keyboard _keyboard, _previousKeyboard;
        private Mouse _mouse;
        private InputSettings _originalSettings, _temporarySettings;
        private void Check(bool ok, string message)
        {
            _checks++;
            if (ok) return;
            _failures++;
            Debug.LogError("[D3Content] mismatch " + message);
        }
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
            Cleanup();
            int checks = _checks + EmberdelveD3ContentGate.PureChecks;
            int failures = _failures + EmberdelveD3ContentGate.PureFailures;
            string summary = "[Emberdelve][D3Content] " + (failures == 0 ? "PASS" : "FAIL") + " checks=" + checks + " failures=" + failures;
            if (failures == 0) Debug.Log(summary); else Debug.LogError(summary);
            if (EmberdelveD3ContentGate.HoldForReview && failures == 0)
            {
                var screen = FindFirstObjectByType<InventoryScreenController>();
                if (screen != null) screen.Open();
            }
            else EmberdelveGateExit.Exit(failures == 0 ? 0 : 1);
        }
        private IEnumerator Flow()
        {
            for (int i = 0; i < 10; i++) yield return null;
            PlayerRig rig = FindFirstObjectByType<PlayerRig>();
            Check(rig != null && rig.Inventory != null && rig.Equipment != null && rig.Look != null, "normal level creates its live player and inventory");
            if (rig == null) yield break;
            foreach (EnemyAgent enemy in FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None)) enemy.enabled = false;
            Inventory bag = rig.Inventory;
            rig.Equipment.Clear();
            bag.Clear();
            bag.ConfigureGrid(10, 6);
            var layout = AssetDatabase.LoadAssetAtPath<DungeonLayoutData>(EmberdelvePaths.Data + "/DungeonLayout_AshenHalls.asset");
            ChestInteractable cache = null;
            foreach (ChestInteractable chest in FindObjectsByType<ChestInteractable>(FindObjectsSortMode.None))
                if (chest.LootTable != null && chest.LootTable.tableId == "jeweler_cache") { Check(cache == null, "only one runtime jeweler cache"); cache = chest; }
            Check(cache != null && cache.ChestId == "Chest_Supply_6_5_5", "the production spawner creates the appended cache with a stable id");
            if (cache == null) yield break;
            CharacterController controller = rig.GetComponent<CharacterController>();
            if (controller != null) controller.enabled = false;
            rig.transform.position = layout.CellCenterWorld(new Vector2Int(6,4)) + Vector3.forward * 0.2f + Vector3.up * 0.1f;
            if (controller != null) controller.enabled = true;
            rig.Look.ApplyAimPose(0f, 36f);
            // Own a transient settings clone so batch focus cannot disable our keyboard.
            // Restore the exact shared settings object; never edit the saved project settings.
            _originalSettings = InputSystem.settings;
            _temporarySettings = UnityEngine.Object.Instantiate(_originalSettings);
            _temporarySettings.hideFlags = HideFlags.HideAndDontSave;
            _temporarySettings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            _temporarySettings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            InputSystem.settings = _temporarySettings;
            _previousKeyboard = Keyboard.current;
            _keyboard = InputSystem.AddDevice<Keyboard>();
            InputSystem.EnableDevice(_keyboard);
            _keyboard.MakeCurrent();
            _mouse = InputSystem.AddDevice<Mouse>();
            InputSystem.EnableDevice(_mouse);
            _mouse.MakeCurrent();
            Check(_keyboard.enabled, "the owned real-keyboard fixture stays enabled without window focus");
            for (int i = 0; i < 6; i++) yield return null;
            Interactor interactor = rig.GetComponent<Interactor>();
            Check(interactor != null && ReferenceEquals(interactor.CurrentTarget, cache), "ordinary interactor ray resolves the reachable chest");
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.F));
            yield return null; yield return null;
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState());
            yield return null; yield return null;
            Check(cache.Opened && cache.RemainingUnits == 0 && cache.TakenCount == 5, "real E input opens the chest and transfers tonic x2 and three gear instances");
            if (!cache.Opened) yield break;
            ItemData tonic = null, gloves = null;
            Check(bag.Catalog.TryResolveItem("smoldering_tonic", out tonic, out _) && bag.Catalog.TryResolveItem("graspwrap_gloves", out gloves, out _), "production catalog resolves the new consumable and gloves");
            Check(Count(bag, tonic) == 2 && bag.UsedSlots == 4, "the live bag contains four stacks, including two tonics");
            var identities = new List<ItemInstance>();
            for (int i = 0; i < bag.Slots.Count; i++)
            {
                ItemInstance item = bag.InstanceAt(i);
                if (item == null) continue;
                identities.Add(item);
                if (item.Definition.itemId == "graspwrap_gloves") Check(item.Rarity == AffixRarity.Common && item.Affixes.Count == 0, "common gloves keep their fixed armor without random affixes");
                if (item.Definition.itemId == "emberbead_necklace") Check(item.Rarity == AffixRarity.Uncommon && item.Affixes.Count == 1, "the necklace has exactly one rolled affix");
                if (item.Definition.itemId == "cinder_band_ring") Check(item.Rarity == AffixRarity.Rare && item.Affixes.Count == 2, "the ring has exactly two rolled affixes");
            }
            Check(identities.Count == 3, "all three gear drops have individual identities");
            int units = bag.TotalUnits;
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.F));
            yield return null; yield return null;
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState());
            yield return null;
            Check(bag.TotalUnits == units && cache.TakenCount == 5, "reopening the real chest grants nothing twice");
            for (int i = 0; i < identities.Count; i++) Check(bag.HoldsInstanceUid(identities[i].Uid), "the looted UID is still owned exactly once");
            int gloveIndex = bag.IndexOfItem(gloves);
            ItemInstance gloveInstance = bag.InstanceAt(gloveIndex);
            string exactGlove = gloveInstance.ToSnapshot().ToJson();
            float speed = rig.Movement.ResolveGroundSpeed(false, false), flowFactor = rig.GetMoveFlowFactor(), hp = rig.CurrentHealth;
            InventorySlot gloveStack = bag.Slots[gloveIndex];
            IInventoryAuthority authority = bag.CommandAuthority;
            bool created = authority.TryCreateLocalCommand(InventoryCommandKind.Equip, gloveStack.StackId, 0, Vector2Int.zero, EquipmentSlot.Hands, gloveInstance.Uid, Guid.Empty, out InventoryCommand equip, out string reason);
            Check(created && authority.Submit(rig, equip).Accepted, "the normal inventory command wears the looted gloves: " + reason);
            Check(rig.Equipment.EquippedInstance(EquipmentSlot.Hands).ToSnapshot().ToJson() == exactGlove && !bag.HoldsInstanceUid(gloveInstance.Uid), "equipping preserves the exact looted UID/rolls and removes the bag copy");
            Check(Mathf.Abs(rig.Movement.ResolveGroundSpeed(false, false) - speed * (flowFactor - 0.01f) / flowFactor) < 0.0001f && rig.CurrentHealth == hp, "the authored 1% base penalty affects actual walking without free healing");
            int tonicIndex = bag.IndexOfItem(tonic);
            ulong tonicStack = bag.Slots[tonicIndex].StackId;
            Check(bag.Hotbar.TryAssign(0, tonicStack, out reason), "the looted tonic is assigned to first quick-wheel slot: " + reason);
            rig.Health.Configure((int)rig.MaxHealth, 0f, 6f);
            rig.Health.SetHealth(rig.MaxHealth - 60f);
            Check(bag.RequestHoldStack(tonicStack, out reason), "the tonic is taken in hand before drinking: " + reason);
            float low = rig.CurrentHealth;
            ulong revision = bag.Revision;
            InputSystem.QueueStateEvent(_mouse, new MouseState { buttons = 1 });
            yield return null; yield return null;
            InputSystem.QueueStateEvent(_mouse, new MouseState());
            yield return null;
            ConsumableChannel channel = rig.GetComponent<ConsumableChannel>();
            Check(channel != null && channel.IsChanneling && Count(bag, tonic) == 2 && rig.CurrentHealth == low && bag.Revision == revision, "real LMB input starts drinking without consuming or healing early");
            Vector3 beforeMove = rig.transform.position;
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.W));
            float moveDeadline = Time.time + 1f, moveWatchdog = Time.realtimeSinceStartup + 10f;
            while (channel != null && channel.IsChanneling && Time.time < moveDeadline && Time.realtimeSinceStartup < moveWatchdog) yield return null;
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState());
            float settleUntil = Time.time + 0.5f;
            while (Time.time < settleUntil && Time.realtimeSinceStartup < moveWatchdog) yield return null;
            Check(Vector3.ProjectOnPlane(rig.transform.position - beforeMove, Vector3.up).magnitude > 0.01f, "the real movement key displaces the live character controller horizontally");
            Check(channel != null && !channel.IsChanneling && channel.LastCancelReason == "moving" && Count(bag, tonic) == 2 && rig.CurrentHealth == low && bag.Revision == revision,
                "real movement input interrupts drinking and preserves both potions and health; cancel=" + channel?.LastCancelReason + " count=" + Count(bag, tonic) + " hp=" + rig.CurrentHealth + " revision=" + bag.Revision);
            InputSystem.QueueStateEvent(_mouse, new MouseState { buttons = 1 });
            yield return null; yield return null;
            InputSystem.QueueStateEvent(_mouse, new MouseState());
            yield return null;
            Check(channel != null && channel.IsChanneling, "the same normal LMB input starts a fresh drink after cancellation");
            float started = Time.time;
            float deadline = Time.realtimeSinceStartup + 20f;
            while (Time.time < started + 0.7f && Time.realtimeSinceStartup < deadline) yield return null;
            Check(Count(bag, tonic) == 2 && rig.CurrentHealth == low, "the unfinished authored channel still consumes and heals nothing");
            while (channel != null && (channel.IsChanneling || channel.ActiveHealCount > 0) && Time.realtimeSinceStartup < deadline) yield return null;
            Check(Time.realtimeSinceStartup < deadline && channel != null && !channel.IsChanneling && channel.ActiveHealCount == 0, "the drink and gradual heal finish on the real game clock");
            Check(Count(bag, tonic) == 1 && bag.Revision == revision + 1 && Mathf.Abs(rig.CurrentHealth - low - 45f) < 0.02f, "one authored tonic consumes once, commits once and heals exactly 45 HP");
            float after = rig.CurrentHealth;
            for (int i = 0; i < 8; i++) yield return null;
            Check(rig.CurrentHealth == after && Count(bag, tonic) == 1, "the completed heal never pays out again");
            foreach (ItemInstance item in identities) Check(bag.HoldsInstanceUid(item.Uid) || rig.Equipment.HoldsUid(item.Uid), "potion use cannot change or destroy any looted gear identity");
        }
        private static int Count(Inventory bag, ItemData item)
        {
            int count = 0;
            foreach (InventorySlot slot in bag.Slots) if (slot.Item == item) count += slot.Count;
            return count;
        }
        private void Cleanup()
        {
            if (_mouse != null && _mouse.added) InputSystem.RemoveDevice(_mouse);
            _mouse = null;
            if (_keyboard != null && _keyboard.added) InputSystem.RemoveDevice(_keyboard);
            if (_previousKeyboard != null && _previousKeyboard.added) _previousKeyboard.MakeCurrent();
            _keyboard = _previousKeyboard = null;
            if (_originalSettings != null) InputSystem.settings = _originalSettings;
            _originalSettings = null;
            if (_temporarySettings != null) UnityEngine.Object.Destroy(_temporarySettings);
            _temporarySettings = null;
        }
        private void OnDestroy() { Cleanup(); }
    }
}
