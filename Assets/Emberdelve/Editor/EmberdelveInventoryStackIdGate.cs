using System;
using System.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Player;

namespace Emberdelve.EditorTools
{
    /// <summary>R6a runtime stack identity regression gate. Run with Emberdelve.EditorTools.EmberdelveInventoryStackIdGate.Run.</summary>
    [InitializeOnLoad]
    public static class EmberdelveInventoryStackIdGate
    {
        private const string Pending = "Emberdelve.InventoryStackId.Pending";

        static EmberdelveInventoryStackIdGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("InventoryStackId_Gate").AddComponent<InventoryStackIdHarness>();
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

    public sealed class InventoryStackIdHarness : MonoBehaviour
    {
        private int checks;
        private int failures;
        private PlayerRig rig;
        private Inventory bag;
        private Equipment equipment;
        private ItemCatalog catalog;

        private void Check(bool ok, string label)
        {
            checks++;
            if (!ok)
            {
                failures++;
                Debug.LogError("[InventoryStackId] mismatch " + label);
            }
        }

        private IEnumerator Start()
        {
            for (int i = 0; i < 10; i++) yield return null;

            try { Execute(); }
            catch (Exception e) { failures++; Debug.LogException(e); }

            Debug.Log("[Emberdelve][InventoryStackId] " + (failures == 0 ? "PASS" : "FAIL") + " checks=" + checks + " failures=" + failures);
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        private void Execute()
        {
            rig = FindFirstObjectByType<PlayerRig>();
            Check(rig != null && !rig.IsDead && rig.Inventory != null && rig.Equipment != null,
                "real living level rig has inventory and equipment");
            if (rig == null || rig.Inventory == null || rig.Equipment == null) return;

            bag = rig.Inventory;
            equipment = rig.Equipment;
            catalog = bag.Catalog;
            string catalogReason = "missing catalog";
            bool validProductionCatalog = catalog != null && !catalog.IsTestCatalog && catalog.IsValid(out catalogReason);
            Check(validProductionCatalog,
                "bag uses the valid production catalog " + (catalog != null ? catalogReason : "missing"));
            if (catalog == null) return;

            equipment.Clear();
            bag.Clear();
            bag.ConfigureGrid(10, 6);
            ExerciseCarriedStacks();
            ExerciseRestore();
            ExerciseGearReplacement();
        }

        private ItemData Def(string id)
        {
            bool ok = catalog.TryResolveItem(id, out ItemData item, out string reason);
            Check(ok && item != null, "catalog resolves " + id + ": " + reason);
            return item;
        }

        private static InventorySlot Slot(Inventory inventory, ulong id)
        {
            return inventory.TryGetStack(id, out InventorySlot slot) ? slot : default;
        }

        private void ExerciseCarriedStacks()
        {
            ItemData coin = Def("ember_coin");
            if (coin == null) return;

            Check(bag.TryAddAt(coin, 6, new Vector2Int(0, 0), out string addA), "place first coin stack: " + addA);
            Check(bag.TryAddAt(coin, 4, new Vector2Int(1, 0), out string addB), "place same-definition second coin stack: " + addB);
            if (bag.Slots.Count != 2) { Check(false, "fixture contains two coin stacks"); return; }

            ulong a = bag.Slots[0].StackId;
            ulong b = bag.Slots[1].StackId;
            Check(a != 0 && b != 0 && a != b, "distinct live stacks receive unique nonzero IDs");
            Check(bag.IndexOfStack(a) == 0 && bag.IndexOfStack(b) == 1, "new IDs resolve to current indexes");
            Check(!bag.TryGetStack(0, out _) && bag.IndexOfStack(0) == -1, "zero is rejected by lookup");
            Check(!bag.TryGetStack(ulong.MaxValue, out _), "unknown ID is rejected by lookup");
            Check(new InventorySlot(coin, 1).StackId == 0, "legacy detached constructor retains zero ID");

            Check(bag.TryAddToSlot(bag.IndexOfStack(a), 1) == 1 && Slot(bag, a).Count == 7,
                "partial add into an existing stack preserves its ID");
            Check(bag.TryMergeStacks(bag.IndexOfStack(b), bag.IndexOfStack(a), out string partialMerge),
                "partial merge accepted: " + partialMerge);
            Check(Slot(bag, a).Count == 8 && Slot(bag, b).Count == 3,
                "partial merge keeps target ID and surviving source ID");
            Check(bag.TryRemoveAt(bag.IndexOfStack(b), 1) == 1 && Slot(bag, b).Count == 2,
                "partial removal preserves the surviving stack ID");

            Check(bag.TryMove(bag.IndexOfStack(a), new Vector2Int(4, 0), out string move), "move accepted: " + move);
            Check(bag.TrySwap(bag.IndexOfStack(a), bag.IndexOfStack(b), out string swap), "swap accepted: " + swap);
            Check(bag.IndexOfStack(a) >= 0 && bag.IndexOfStack(b) >= 0,
                "move and swap preserve both identities");

            Check(bag.TryAddAt(coin, 2, new Vector2Int(2, 0), out string addC), "place third stack: " + addC);
            if (bag.Slots.Count != 3) { Check(false, "third stack fixture exists"); return; }
            ulong c = bag.Slots[2].StackId;
            Check(c != 0 && c != a && c != b, "third stack has its own identity");

            bag.ConfigureGrid(10, 5);
            Check(bag.TryGetStack(a, out InventorySlot afterResizeA) && afterResizeA.Origin == new Vector2Int(0, 0) && afterResizeA.Count == 8,
                "resize/reseat keeps the moved stack ID and count at its deterministic repacked cell");
            Check(bag.TryGetStack(b, out InventorySlot afterResizeB) && afterResizeB.Origin == new Vector2Int(1, 0) && afterResizeB.Count == 2,
                "resize/reseat keeps the swapped stack ID and count at its deterministic repacked cell");
            Check(bag.TryGetStack(c, out InventorySlot afterResizeC) && afterResizeC.Origin == new Vector2Int(2, 0) && afterResizeC.Count == 2,
                "resize/reseat keeps the third stack ID and count after the first two");

            Check(bag.TryRemoveAt(bag.IndexOfStack(a), 3) == 3 && Slot(bag, a).Count == 5,
                "partial removal retains the target for the full merge");
            Check(bag.TryMergeStacks(bag.IndexOfStack(b), bag.IndexOfStack(a), out string fullMerge),
                "full merge accepted: " + fullMerge);
            Check(bag.IndexOfStack(b) == -1 && !bag.TryGetStack(b, out _),
                "fully consumed merge source ID retires");
            Check(bag.IndexOfStack(c) == 1 && Slot(bag, c).Count == 2,
                "removing the earlier source compacts indexes without changing the later ID");

            Check(bag.TryRemoveAt(bag.IndexOfStack(a), int.MaxValue) == 7 && bag.IndexOfStack(a) == -1,
                "full removal retires the target ID");
            Check(bag.TryAdd(coin, 10, out string refill), "add refills existing then creates a new stack: " + refill);
            Check(bag.TryGetStack(c, out InventorySlot refilled) && refilled.Count == 8,
                "refilling a stack preserves its ID");
            ulong newStack = 0;
            for (int i = 0; i < bag.Slots.Count; i++)
                if (bag.Slots[i].StackId != c) newStack = bag.Slots[i].StackId;
            Check(newStack != 0 && newStack != a && newStack != b && newStack != c,
                "overflow creates a fresh stack identity");

            ulong maxBeforeClear = Math.Max(Math.Max(a, b), Math.Max(c, newStack));
            bag.Clear();
            Check(bag.IndexOfStack(c) == -1 && bag.IndexOfStack(newStack) == -1,
                "Clear retires every live identity");
            Check(bag.TryAdd(coin, 1, out string afterClear), "new stack after Clear: " + afterClear);
            Check(bag.Slots.Count == 1 && bag.Slots[0].StackId > maxBeforeClear,
                "IDs remain monotonic across Clear");
        }

        private void ExerciseRestore()
        {
            ContainerSnapshot snapshot = bag.ToContainerSnapshot();
            string canonicalBefore = snapshot.ToJson();
            ulong oldId = bag.Slots[0].StackId;
            ItemData coin = Def("ember_coin");
            Check(bag.TryAddAt(coin, 2, new Vector2Int(1, 0), out string extra), "mutate before restore: " + extra);
            Check(bag.TryRestore(snapshot, out string restore), "valid snapshot restores: " + restore);
            Check(bag.ToContainerSnapshot().ToJson() == canonicalBefore,
                "restored canonical snapshot JSON is byte-identical");
            Check(bag.IndexOfStack(oldId) == -1 && bag.Slots.Count == 1 && bag.Slots[0].StackId != oldId,
                "successful restore retires prior IDs and allocates fresh IDs");

            ulong restoredId = bag.Slots[0].StackId;
            string beforeRefusal = bag.ToContainerSnapshot().ToJson();
            ContainerSnapshot invalid = snapshot.Clone();
            invalid.checksum = "tampered";
            Check(!bag.TryRestore(invalid, out string refusal), "invalid snapshot refused: " + refusal);
            Check(bag.ToContainerSnapshot().ToJson() == beforeRefusal && bag.Slots.Count == 1 &&
                  bag.Slots[0].StackId == restoredId && bag.TryGetStack(restoredId, out _),
                "failed restore leaves snapshot and runtime identities unchanged");
        }

        private void ExerciseGearReplacement()
        {
            bag.Clear();
            equipment.Clear();
            bag.ConfigureGrid(10, 6);
            ItemData boots = Def("footpad_boots");
            if (boots == null) return;

            AffixRecord firstRoll = new AffixRecord("affix_move_speed", AffixStatKey.MoveSpeed, 0.09f);
            AffixRecord secondRoll = new AffixRecord("affix_move_speed", AffixStatKey.MoveSpeed, 0.12f);
            Check(ItemInstance.TryCreateNew(boots, 1, AffixRarity.Uncommon, new[] { firstRoll }, catalog.Authority,
                out ItemInstance first, out string firstReason), "create first rolled boots: " + firstReason);
            Check(ItemInstance.TryCreateNew(boots, 1, AffixRarity.Uncommon, new[] { secondRoll }, catalog.Authority,
                out ItemInstance second, out string secondReason), "create second rolled boots: " + secondReason);
            if (first == null || second == null) return;

            Check(bag.TryAddInstance(first, out string addFirst), "carry first boots: " + addFirst);
            Check(bag.TryAddInstance(second, out string addSecond), "carry second boots: " + addSecond);
            ulong firstStack = bag.Slots[bag.IndexOfInstance(first.Uid)].StackId;
            ulong secondStack = bag.Slots[bag.IndexOfInstance(second.Uid)].StackId;
            string firstSnapshot = first.ToSnapshot().ToJson();
            string secondSnapshot = second.ToSnapshot().ToJson();

            Check(equipment.TryEquipFromInventory(bag.IndexOfInstance(first.Uid), out string equipFirst),
                "equip first rolled boots: " + equipFirst);
            Check(bag.IndexOfStack(firstStack) == -1 && equipment.EquippedInstance(EquipmentSlot.Legs).Uid == first.Uid,
                "equipping retires the incoming carried-stack ID");
            Check(equipment.TryEquipFromInventory(bag.IndexOfInstance(second.Uid), out string replace),
                "replace with second rolled boots: " + replace);
            Check(equipment.EquippedInstance(EquipmentSlot.Legs).Uid == second.Uid && bag.HoldsInstanceUid(first.Uid),
                "replacement equips the incoming exact UID and returns the outgoing UID");

            int firstIndex = bag.IndexOfInstance(first.Uid);
            Check(firstIndex >= 0, "outgoing boots have a bag stack");
            ulong outgoingStack = firstIndex >= 0 ? bag.Slots[firstIndex].StackId : 0;
            Check(outgoingStack != 0 && outgoingStack != firstStack && bag.IndexOfStack(firstStack) == -1,
                "reused replacement slot retires the incoming stack ID and gives outgoing gear a fresh ID");
            Check(first.ToSnapshot().ToJson() == firstSnapshot && second.ToSnapshot().ToJson() == secondSnapshot,
                "replacement preserves both gear UIDs, rarity and exact rolls");

            Check(equipment.TryUnequip(EquipmentSlot.Legs, out string unequip), "unequip replacement: " + unequip);
            int secondIndex = bag.IndexOfInstance(second.Uid);
            Check(secondIndex >= 0, "unequipped boots return to the bag");
            if (secondIndex >= 0)
            {
                ulong unequippedStack = bag.Slots[secondIndex].StackId;
                Check(unequippedStack != 0 && unequippedStack != secondStack && bag.IndexOfStack(secondStack) == -1,
                    "unequip creates a fresh carried-stack ID");
            }
            Check(first.ToSnapshot().ToJson() == firstSnapshot && second.ToSnapshot().ToJson() == secondSnapshot,
                "unequip still preserves exact gear snapshots");
        }
    }
}

