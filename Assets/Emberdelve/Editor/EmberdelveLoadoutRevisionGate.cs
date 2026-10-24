using System;
using System.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Player;

namespace Emberdelve.EditorTools
{
    /// <summary>R6b1 shared inventory/loadout revision gate. Run with Emberdelve.EditorTools.EmberdelveLoadoutRevisionGate.Run.</summary>
    [InitializeOnLoad]
    public static class EmberdelveLoadoutRevisionGate
    {
        private const string Pending = "Emberdelve.LoadoutRevision.Pending";

        static EmberdelveLoadoutRevisionGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("LoadoutRevision_Gate").AddComponent<LoadoutRevisionHarness>();
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

    public sealed class LoadoutRevisionHarness : MonoBehaviour
    {
        private int checks;
        private int failures;
        private int inventoryEvents;
        private int equipmentEvents;
        private bool observe;
        private bool observersSawExpectedState;
        private ulong expectedRevision;
        private int expectedTotalUnits;
        private Guid expectedLegsUid;
        private string expectedBag;
        private string expectedEquipment;
        private Inventory bag;
        private Equipment equipment;
        private ItemCatalog catalog;

        private void Check(bool ok, string label)
        {
            checks++;
            if (!ok)
            {
                failures++;
                Debug.LogError("[LoadoutRevision] mismatch " + label);
            }
        }

        private IEnumerator Start()
        {
            for (int i = 0; i < 10; i++) yield return null;
            try { Execute(); }
            catch (Exception e) { failures++; Debug.LogException(e); }
            finally
            {
                GameEvents.InventoryChanged -= OnInventoryChanged;
                GameEvents.EquipmentChanged -= OnEquipmentChanged;
            }

            Debug.Log("[Emberdelve][LoadoutRevision] " + (failures == 0 ? "PASS" : "FAIL") + " checks=" + checks + " failures=" + failures);
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        private void Execute()
        {
            PlayerRig rig = FindFirstObjectByType<PlayerRig>();
            Check(rig != null && !rig.IsDead && rig.Inventory != null && rig.Equipment != null,
                "real living level rig has inventory and equipment");
            if (rig == null || rig.Inventory == null || rig.Equipment == null) return;

            bag = rig.Inventory;
            equipment = rig.Equipment;
            catalog = bag.Catalog;
            string catalogReason = "missing catalog";
            Check(catalog != null && !catalog.IsTestCatalog && catalog.IsValid(out catalogReason),
                "bag uses the valid production catalog: " + catalogReason);
            if (catalog == null) return;

            equipment.Clear();
            bag.Clear();
            bag.ConfigureGrid(10, 6);
            GameEvents.InventoryChanged += OnInventoryChanged;
            GameEvents.EquipmentChanged += OnEquipmentChanged;

            ulong emptyRevision = bag.Revision;
            ResetEvents();
            bag.Clear();
            equipment.Clear();
            Check(bag.Revision == emptyRevision && inventoryEvents == 1 && equipmentEvents == 0,
                "empty bag/equipment clears are revision no-ops while bag Clear retains its event contract");

            ItemData coin = Resolve("ember_coin");
            ItemData boots = Resolve("footpad_boots");
            if (coin == null || boots == null) return;

            ulong before = bag.Revision;
            ResetEvents();
            ExpectSimpleState(before + 1, 3, Guid.Empty);
            Check(bag.TryAdd(coin, 3, out string addReason), "direct loot add succeeds: " + addReason);
            Check(bag.Revision == before + 1 && bag.TotalUnits == 3 && bag.IndexOfItem(coin) >= 0,
                "one successful direct loot add advances once and updates the bag");
            Check(inventoryEvents == 1 && observersSawExpectedState,
                $"direct-add subscriber observes final bag and already-advanced revision (events={inventoryEvents}, " +
                $"observersSawExpectedState={observersSawExpectedState}, revision={bag.Revision}/{expectedRevision}, " +
                $"units={bag.TotalUnits}/{expectedTotalUnits}, gearUid={equipment.EquippedInstance(EquipmentSlot.Legs)?.Uid}/{expectedLegsUid})");

            before = bag.Revision;
            ResetEvents();
            Check(!bag.TryAddAt(coin, 1, new Vector2Int(99, 99), out string refusal),
                "out-of-grid exact placement is refused: " + refusal);
            Check(bag.Revision == before && inventoryEvents == 0 && equipmentEvents == 0,
                "refused placement changes neither revision nor notifications");

            int coinIndex = bag.IndexOfItem(coin);
            Vector2Int sameOrigin = bag.Slots[coinIndex].Origin;
            ResetEvents();
            ExpectSimpleState(before, bag.TotalUnits, Guid.Empty);
            Check(bag.TryMove(coinIndex, sameOrigin, out string sameMove), "same-cell move remains accepted: " + sameMove);
            Check(bag.Revision == before && inventoryEvents == 1 && observersSawExpectedState,
                "same-cell move retains its notification contract but is a revision no-op");

            int oldWidth = bag.GridWidth;
            int oldHeight = bag.GridHeight;
            bag.ConfigureGrid(oldWidth, oldHeight);
            equipment.Clear();
            Check(bag.Revision == before, "same-grid reseat and clearing empty equipment do not advance revision");

            ContainerSnapshot bagSnapshot = bag.ToContainerSnapshot();
            string bagJson = bagSnapshot.ToJson();
            before = bag.Revision;
            Check(bag.TryAdd(coin, 1, out string extraCoin), "mutate bag before standalone restore: " + extraCoin);
            before = bag.Revision;
            ResetEvents();
            expectedBag = bagJson;
            expectedEquipment = equipment.ToContainerSnapshot().ToJson();
            expectedRevision = before + 1;
            expectedTotalUnits = 3;
            expectedLegsUid = Guid.Empty;
            observe = true;
            Check(bag.TryRestore(bagSnapshot, out string bagRestore), "standalone bag restore succeeds: " + bagRestore);
            observe = false;
            Check(bag.Revision == before + 1 && bag.ToContainerSnapshot().ToJson() == bagJson,
                "standalone bag restore advances once and restores canonical content");
            Check(inventoryEvents == 1 && observersSawExpectedState,
                "bag-restore subscriber sees the fully restored bag and revision");

            before = bag.Revision;
            ResetEvents();
            ContainerSnapshot badBag = bagSnapshot.Clone();
            badBag.checksum = "tampered";
            Check(!bag.TryRestore(badBag, out string badRestore), "tampered bag snapshot is refused: " + badRestore);
            Check(bag.Revision == before && inventoryEvents == 0,
                "failed restore leaves revision and notification count unchanged");

            Check(bag.TryAdd(boots, 1, out string bootsAdd), "add authored gear to bag: " + bootsAdd);
            int bootsIndex = bag.IndexOfItem(boots);
            before = bag.Revision;
            ResetEvents();
            ItemInstance incomingBoots = bag.InstanceAt(bootsIndex);
            ExpectSimpleState(before + 1, bag.TotalUnits - 1, incomingBoots != null ? incomingBoots.Uid : Guid.Empty);
            Check(equipment.TryEquipFromInventory(bootsIndex, out string equipReason), "equip gear succeeds: " + equipReason);
            Guid wornUid = equipment.EquippedInstance(EquipmentSlot.Legs)?.Uid ?? Guid.Empty;
            Check(wornUid != Guid.Empty && bag.Revision == before + 1 && equipmentEvents == 1 && inventoryEvents == 1,
                "equip commits one shared revision before both outward notifications");
            Check(observersSawExpectedState, "equip subscribers observe final bag, gear and revision");

            ContainerSnapshot gearSnapshot = equipment.ToContainerSnapshot();
            string gearJson = gearSnapshot.ToJson();
            before = bag.Revision;
            ResetEvents();
            equipment.Clear();
            Check(bag.Revision == before + 1 && inventoryEvents == 0 && equipmentEvents == 0,
                "nonempty equipment Clear commits once and retains its silent notification behavior");
            before = bag.Revision;
            ResetEvents();
            expectedBag = bag.ToContainerSnapshot().ToJson();
            expectedEquipment = gearJson;
            expectedRevision = before + 1;
            expectedTotalUnits = bag.TotalUnits;
            expectedLegsUid = wornUid;
            observe = true;
            Check(equipment.TryRestore(gearSnapshot, out string gearRestore), "standalone gear restore succeeds: " + gearRestore);
            observe = false;
            Check(bag.Revision == before + 1 && equipment.ToContainerSnapshot().ToJson() == gearJson,
                "standalone equipment restore commits once with exact gear snapshot");
            Check(inventoryEvents == 1 && equipmentEvents == 1 && observersSawExpectedState,
                "equipment-restore subscribers observe final gear, bag and revision");

            LoadoutSnapshot combined = LoadoutSnapshot.Capture(bag, equipment);
            string combinedBagJson = combined.bag.ToJson();
            string combinedGearJson = combined.equipment.ToJson();
            before = bag.Revision;
            Check(equipment.TryUnequip(EquipmentSlot.Legs, out string ignoredUnequip),
                "clear worn gear before combined restore: " + ignoredUnequip);
            Check(bag.Revision == before + 1, "successful unequip advances the shared revision once");
            Check(bag.TryAdd(coin, 2, out string diverge), "diverge bag before combined restore: " + diverge);
            before = bag.Revision;
            ResetEvents();
            expectedBag = combinedBagJson;
            expectedEquipment = combinedGearJson;
            expectedRevision = before + 1;
            expectedTotalUnits = combined.bag.entries != null && combined.bag.entries.Length > 0 ? combined.bag.entries[0].item.quantity : 0;
            expectedLegsUid = combined.equipment.entries != null && combined.equipment.entries.Length > 0 &&
                              Guid.TryParse(combined.equipment.entries[0].item.uid, out Guid combinedUid)
                ? combinedUid
                : Guid.Empty;
            observe = true;
            Check(LoadoutSnapshot.TryRestore(combined, bag, equipment, null, out string combinedReason),
                "combined loadout restore succeeds: " + combinedReason);
            observe = false;
            Check(bag.Revision == before + 1 && bag.ToContainerSnapshot().ToJson() == combinedBagJson &&
                  equipment.ToContainerSnapshot().ToJson() == combinedGearJson,
                "combined restore applies both halves and advances exactly once");
            Check(inventoryEvents == 1 && equipmentEvents >= 1 && observersSawExpectedState,
                "combined-restore subscribers see final bag and gear with the single revision");

            before = bag.Revision;
            ResetEvents();
            LoadoutSnapshot invalidCombined = combined.Clone();
            invalidCombined.checksum = "tampered";
            Check(!LoadoutSnapshot.TryRestore(invalidCombined, bag, equipment, null, out string invalidReason),
                "tampered combined restore is refused: " + invalidReason);
            Check(bag.Revision == before && inventoryEvents == 0 && equipmentEvents == 0,
                "failed combined restore changes neither revision nor outward events");
        }

        private ItemData Resolve(string id)
        {
            bool ok = catalog.TryResolveItem(id, out ItemData item, out string reason);
            Check(ok && item != null, "production catalog resolves " + id + ": " + reason);
            return item;
        }

        private void ExpectSimpleState(ulong revision, int totalUnits, Guid legsUid)
        {
            expectedRevision = revision;
            expectedTotalUnits = totalUnits;
            expectedLegsUid = legsUid;
            expectedBag = null;
            expectedEquipment = null;
            observe = true;
        }

        private void ResetEvents()
        {
            inventoryEvents = 0;
            equipmentEvents = 0;
            observersSawExpectedState = true;
            observe = false;
        }

        private void ObserveCurrentState()
        {
            if (!observe) return;
            observersSawExpectedState &= bag.Revision == expectedRevision &&
                bag.TotalUnits == expectedTotalUnits &&
                (equipment.EquippedInstance(EquipmentSlot.Legs)?.Uid ?? Guid.Empty) == expectedLegsUid &&
                (expectedBag == null || bag.ToContainerSnapshot().ToJson() == expectedBag) &&
                (expectedEquipment == null || equipment.ToContainerSnapshot().ToJson() == expectedEquipment);
        }

        private void OnInventoryChanged(int playerId)
        {
            inventoryEvents++;
            ObserveCurrentState();
        }

        private void OnEquipmentChanged(int playerId, ItemData item, bool equipped)
        {
            equipmentEvents++;
            ObserveCurrentState();
        }
    }
}
