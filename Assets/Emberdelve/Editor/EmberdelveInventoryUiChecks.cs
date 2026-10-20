using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.Items;
using Emberdelve.Player;
using Emberdelve.UI;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Focused play-mode checks for the satchel screen's pointer routing (U1C1).
    ///
    /// Every click here is a real click: the fixture reads the rectangle the screen actually rendered,
    /// converts its centre to a screen coordinate with the owning canvas camera, and hands that coordinate to
    /// the same public HandleClick the HUD's mouse reading uses. Nothing reaches into private fields, private
    /// geometry or the controller's internals, so a check that passes here is a click a player could make.
    ///
    /// What is asserted is the state a player can lose: which stack is in which cell, how many units it holds,
    /// what the bag is worth, what the run has been credited with, how many world pickups exist and how many
    /// notifications an operation raised. A boolean return value is never the whole assertion.
    ///
    /// The bag is reset to exact cells before each scenario so the expected numbers are geometry, not
    /// whatever the class handed out. Shared item assets are only read, never edited.
    /// </summary>
    public static class EmberdelveInventoryUiChecks
    {
        private static readonly Vector2Int MoveTarget = new Vector2Int(4, 0);

        /// <summary>One bag state, read from the live inventory.</summary>
        private sealed class Snapshot
        {
            public int Units;
            public int Value;
            public int Cells;
            public int Stacks;
            public int Collected;
            public int Recovered;
            public int UsedSlotsView;

            public override string ToString()
            {
                return Units + " units, " + Value + " value, " + Cells + " cells, " + Stacks +
                       " stacks, " + Collected + " collected, " + Recovered + " recovered";
            }
        }

        private sealed class Fixture
        {
            public PlayerRig Rig;
            public RunDirector Director;
            public Inventory Inventory;
            public Equipment Equipment;
            public InventoryScreenController Screen;
            public Canvas Canvas;
            public Transform UiRoot;

            public ItemData Coin;
            public ItemData Cuirass;
            public ItemData Boots;
            public ItemData Blade;

            public System.Action<bool, string> Check;

            // Observers armed for the scenario that is running, and cleared before it.
            public int CollectedEvents;
            public int InventoryEvents;

            public void OnItemCollected(ItemData item, int totalCollected) { CollectedEvents++; }
            public void OnInventoryChanged() { InventoryEvents++; }
        }

        public static IEnumerator Run(PlayerRig rig, RunDirector director, System.Action<bool, string> check)
        {
            check(rig != null && director != null, "the inventory UI flow found a rig and a director");
            if (rig == null || director == null) yield break;

            Inventory inventory = rig.Inventory;
            Equipment equipment = rig.Equipment;
            check(inventory != null && equipment != null, "the rig carries an inventory and equipment slots");
            if (inventory == null || equipment == null) yield break;

            Fixture fixture = new Fixture
            {
                Rig = rig,
                Director = director,
                Inventory = inventory,
                Equipment = equipment,
                Check = check,
                Coin = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_EmberCoin.asset"),
                Cuirass = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_BoiledCuirass.asset"),
                Boots = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_FootpadBoots.asset"),
                Blade = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_EmberFangBlade.asset"),
            };

            check(fixture.Coin != null && fixture.Cuirass != null && fixture.Boots != null && fixture.Blade != null,
                "the item definitions the pointer fixtures need are loadable");
            if (fixture.Coin == null || fixture.Cuirass == null || fixture.Boots == null || fixture.Blade == null)
                yield break;

            yield return WaitFrames(20);

            InventoryScreenController screen = UnityEngine.Object.FindFirstObjectByType<InventoryScreenController>();
            check(screen != null, "the HUD built the satchel screen at runtime");
            if (screen == null) yield break;

            fixture.Screen = screen;
            fixture.Canvas = screen.GetComponentInParent<Canvas>();
            check(fixture.Canvas != null, "the satchel screen lives under a canvas");
            if (fixture.Canvas == null) yield break;

            check(!rig.IsDead, $"the rig is alive for the pointer tests ({rig.CurrentHealth:F0}/{rig.MaxHealth:F0})");

            GameEvents.ItemCollected += fixture.OnItemCollected;
            GameEvents.InventoryChanged += fixture.OnInventoryChanged;

            try
            {
                yield return PrepareScreen(fixture);
                if (fixture.UiRoot == null) yield break;

                yield return ClickedMoveConservesTheStack(fixture);
                yield return ValidSwapConservesBothStacks(fixture);
                yield return ImpossibleSwapChangesNothing(fixture);
                yield return PartialMergeKeepsTheRemainder(fixture);
                yield return SelectedCopyEquipsNotTheFirstMatch(fixture);
                yield return WrongSlotRefusalKeepsOwnership(fixture);
                yield return CancelAndCloseConserveEverything(fixture);
                yield return ExternalBagChangeKeepsSelectionHonest(fixture);

                check(!rig.IsDead, $"the rig survived the pointer tests ({rig.CurrentHealth:F0}/{rig.MaxHealth:F0})");
            }
            finally
            {
                // Observers and screen state are the fixture's to clean up, not the run's to inherit.
                GameEvents.ItemCollected -= fixture.OnItemCollected;
                GameEvents.InventoryChanged -= fixture.OnInventoryChanged;

                fixture.Screen.Close();
                fixture.Equipment.Clear();
                fixture.Inventory.Clear();

                check(fixture.Inventory.Slots.Count == 0 && fixture.Inventory.UsedCells == 0 &&
                      fixture.Equipment.FilledCount == 0,
                      "the pointer fixtures left the bag and the worn kit empty for the flows that follow");
                check(!fixture.Screen.IsOpen && fixture.Screen.SelectedItem == null,
                      "the satchel screen ends closed, with no ghost and no selection");
            }
        }

        // ------------------------------------------------------------------ scenarios

        private static IEnumerator PrepareScreen(Fixture f)
        {
            ResetBag(f);
            yield return WaitFrames(2);

            OpenScreen(f);
            yield return WaitFrames(2);
            Canvas.ForceUpdateCanvases();
            yield return WaitFrames(1);

            f.Check(f.Screen.IsOpen, "the satchel screen is open");

            RectTransform root = FindChildRect(f.Canvas.transform, "InventoryRoot");
            f.UiRoot = root;
            f.Check(root != null && root.gameObject.activeInHierarchy,
                "the screen's own root is active in the hierarchy, so the cells it draws are on screen");
            if (root == null) yield break;

            int cells = CountNamed(root, "Cell_0_0") + CountNamed(root, "Cell_9_3");
            f.Check(cells == 2, $"the grid rendered the cells the bag has (Cell_0_0 and Cell_9_3 found: {cells})");

            RectTransform corner = FindChildRect(root, "Cell_9_3");
            if (corner != null)
            {
                Vector2 cornerPoint = ScreenPoint(f, corner);
                f.Check(cornerPoint.x > 0f && cornerPoint.y > 0f &&
                       cornerPoint.x < Screen.width && cornerPoint.y < Screen.height,
                       $"the last cell of the grid maps to a real screen coordinate ({cornerPoint.x:F0},{cornerPoint.y:F0} of {Screen.width}x{Screen.height})");
            }

            f.Check(FindChildRect(root, "Equip_Chest") != null && FindChildRect(root, "Equip_Legs") != null,
                "the worn kit rendered the slots the clicks are aimed at");
        }

        private static IEnumerator ClickedMoveConservesTheStack(Fixture f)
        {
            Debug.Log("[Emberdelve][Smoke:inventoryui] ---- clicked move");
            ResetBag(f);
            yield return WaitFrames(2);
            OpenScreen(f);
            yield return WaitFrames(2);

            Vector2Int start = new Vector2Int(0, 0);
            f.Check(f.Inventory.TryAddAt(f.Cuirass, 1, start, out string addReason),
                $"a 2x3 cuirass was placed at {start} ({addReason})");
            yield return WaitFrames(2);

            Snapshot before = Take(f);
            f.CollectedEvents = 0;
            f.InventoryEvents = 0;

            yield return ClickCell(f, start);

            f.Check(f.Screen.SelectedItem == f.Cuirass && f.Screen.SelectedOrigin == start &&
                    f.Screen.SelectedAmount == 1,
                    $"clicking the cuirass selected the stack that is on screen ({Name(f.Screen.SelectedItem)} at " +
                    $"{f.Screen.SelectedOrigin}, {f.Screen.SelectedAmount} units)");
            f.Check(f.Inventory.Slots.Count == 1 && f.Inventory.Slots[0].Origin == start &&
                    f.Inventory.Slots[0].Count == 1 && f.Inventory.UsedCells == 6,
                    "selecting removed nothing from the bag: same stack, same cell, same count, same cells");
            f.Check(f.Inventory.TotalUnits == before.Units && f.Inventory.CarriedValue == before.Value,
                $"selecting moved no units and no value ({f.Inventory.TotalUnits} units, {f.Inventory.CarriedValue} value)");
            f.Check(f.CollectedEvents == 0 && f.InventoryEvents == 0,
                $"selecting is not an operation: no notification, no credit ({f.InventoryEvents} notifications)");
            f.Check(FindChildRect(f.UiRoot, "Selected_" + f.Cuirass.displayName) != null,
                "the selected stack is marked in place on the grid");
            f.Check(CountNamed(f.UiRoot, "Item_" + f.Cuirass.displayName) == 1,
                $"the selected stack is drawn once, not as two normal owned copies ({CountNamed(f.UiRoot, "Item_" + f.Cuirass.displayName)} item block)");

            yield return ClickCell(f, MoveTarget);

            f.Check(f.Inventory.Slots.Count == 1 && f.Inventory.Slots[0].Origin == MoveTarget &&
                    f.Inventory.Slots[0].Count == 1,
                    $"the selected stack moved to the clicked cell {MoveTarget} with its count intact");
            f.Check(f.Screen.SelectedItem == null && f.Screen.SelectedOrigin.x < 0,
                "a committed move ends the selection instead of leaving a ghost behind");
            f.Check(FootprintFree(f, start, f.Cuirass.EffectiveFootprint),
                $"the {start} cells the cuirass vacated are free again");
            f.Check(FootprintOccupied(f, MoveTarget, f.Cuirass.EffectiveFootprint),
                $"every cell of the {f.Cuirass.EffectiveFootprint} footprint is marked at {MoveTarget}");
            f.Check(f.Inventory.UsedCells == before.Cells && f.Inventory.TotalUnits == before.Units &&
                    f.Inventory.CarriedValue == before.Value && f.Inventory.UsedSlots == before.Stacks,
                    $"a move costs the same cells, units, value and stacks it already had ({Take(f)})");
            f.Check(f.Inventory.LifetimeCollected == before.Collected && f.Inventory.RecoveredValue == before.Recovered,
                $"a move credits nothing: {f.Inventory.LifetimeCollected} collected, {f.Inventory.RecoveredValue} recovered");
            f.Check(f.CollectedEvents == 0, "a move raised no ItemCollected event");
            f.Check(f.InventoryEvents == 1, $"one committed move raised exactly one InventoryChanged ({f.InventoryEvents})");
        }

        private static IEnumerator ValidSwapConservesBothStacks(Fixture f)
        {
            Debug.Log("[Emberdelve][Smoke:inventoryui] ---- valid swap");
            ResetBag(f);
            yield return WaitFrames(2);
            OpenScreen(f);
            yield return WaitFrames(2);

            Vector2Int cuirassOrigin = new Vector2Int(0, 0);
            Vector2Int bootsOrigin = new Vector2Int(4, 0);
            f.Check(f.Inventory.TryAddAt(f.Cuirass, 1, cuirassOrigin, out string addCuirass),
                $"cuirass placed at {cuirassOrigin} ({addCuirass})");
            f.Check(f.Inventory.TryAddAt(f.Boots, 1, bootsOrigin, out string addBoots),
                $"boots placed at {bootsOrigin} ({addBoots})");
            yield return WaitFrames(2);

            Snapshot before = Take(f);
            f.CollectedEvents = 0;
            f.InventoryEvents = 0;

            yield return ClickCell(f, cuirassOrigin);
            f.Check(f.Screen.SelectedItem == f.Cuirass, "the cuirass is the selected stack");

            yield return ClickCell(f, bootsOrigin);

            f.Check(f.Inventory.Slots.Count == 2, "a swap keeps both stacks in the bag");
            f.Check(OriginOf(f, f.Cuirass) == bootsOrigin && OriginOf(f, f.Boots) == cuirassOrigin,
                $"the two shapes traded cells (cuirass now {OriginOf(f, f.Cuirass)}, boots now {OriginOf(f, f.Boots)})");
            f.Check(f.Screen.SelectedItem == null, "a committed swap ends the selection");
            f.Check(f.Inventory.UsedCells == before.Cells && f.Inventory.TotalUnits == before.Units &&
                    f.Inventory.CarriedValue == before.Value,
                    $"a swap rearranges cells, it does not move items in or out ({Take(f)})");
            f.Check(f.Inventory.LifetimeCollected == before.Collected && f.Inventory.RecoveredValue == before.Recovered,
                "a swap credited no loot");
            f.Check(f.CollectedEvents == 0, "a swap raised no ItemCollected event");
            f.Check(f.InventoryEvents == 1, $"one committed swap raised exactly one InventoryChanged ({f.InventoryEvents})");
        }

        private static IEnumerator ImpossibleSwapChangesNothing(Fixture f)
        {
            Debug.Log("[Emberdelve][Smoke:inventoryui] ---- impossible swap");
            ResetBag(f);
            yield return WaitFrames(2);
            OpenScreen(f);
            yield return WaitFrames(2);

            // A 2x3 cuirass cannot trade places with a single coin: the coin's cell and the cells around it
            // are not a 2x3 hole, because other coins are standing in it.
            Vector2Int cuirassOrigin = new Vector2Int(0, 0);
            Vector2Int coinOrigin = new Vector2Int(2, 0);
            f.Check(f.Inventory.TryAddAt(f.Cuirass, 1, cuirassOrigin, out string addCuirass),
                $"cuirass placed at {cuirassOrigin} ({addCuirass})");
            f.Check(f.Inventory.TryAddAt(f.Coin, 1, coinOrigin, out string addCoin),
                $"coin placed at {coinOrigin} ({addCoin})");
            f.Check(f.Inventory.TryAddAt(f.Coin, 1, coinOrigin + new Vector2Int(0, 1), out string addCoin2),
                $"coin placed at {coinOrigin + new Vector2Int(0, 1)} ({addCoin2})");
            f.Check(f.Inventory.TryAddAt(f.Coin, 1, coinOrigin + new Vector2Int(0, 2), out string addCoin3),
                $"coin placed at {coinOrigin + new Vector2Int(0, 2)} ({addCoin3})");
            yield return WaitFrames(2);

            Snapshot before = Take(f);
            f.CollectedEvents = 0;
            f.InventoryEvents = 0;

            yield return ClickCell(f, cuirassOrigin);
            yield return ClickCell(f, coinOrigin);

            f.Check(f.Equipment.FilledCount == 0 && f.Equipment.Equipped(EquipmentSlot.Chest) == null,
                "no item appeared in an equipment slot on the way");
            f.Check(OriginOf(f, f.Cuirass) == cuirassOrigin && OriginOf(f, f.Coin) == coinOrigin &&
                    CountUnitsOf(f, f.Coin) == 3 && StacksOf(f, f.Coin) == 3,
                    $"both stacks kept their own cells ({Name(f.Cuirass)} at {OriginOf(f, f.Cuirass)}, " +
                    $"{CountUnitsOf(f, f.Coin)} coins in {StacksOf(f, f.Coin)} stacks)");
            f.Check(f.Screen.SelectedItem == f.Cuirass && f.Screen.SelectedOrigin == cuirassOrigin,
                "a refused swap keeps the selection pointing at the stack it started from");
            f.Check(Same(before, Take(f)), $"the refused swap changed nothing (before {before}, after {Take(f)})");
            f.Check(f.CollectedEvents == 0, "a refused swap raised no ItemCollected event");
            f.Check(f.InventoryEvents == 0, $"a refused swap notified nothing ({f.InventoryEvents} notifications)");
        }

        private static IEnumerator PartialMergeKeepsTheRemainder(Fixture f)
        {
            Debug.Log("[Emberdelve][Smoke:inventoryui] ---- partial merge");
            ResetBag(f);
            yield return WaitFrames(2);
            OpenScreen(f);
            yield return WaitFrames(2);

            int limit = f.Coin.StackLimitForFootprint;
            Vector2Int source = new Vector2Int(0, 0);
            Vector2Int target = new Vector2Int(1, 0);

            f.Check(limit > 5, $"the coin stack limit is a real cap ({limit})");
            f.Check(f.Inventory.TryAddAt(f.Coin, 6, source, out string addSource),
                $"six coins placed as one stack at {source} ({addSource})");
            f.Check(f.Inventory.TryAddAt(f.Coin, 5, target, out string addTarget),
                $"five coins placed as one stack at {target} ({addTarget})");
            yield return WaitFrames(2);

            int unitsBefore = f.Inventory.TotalUnits;
            int valueBefore = f.Inventory.CarriedValue;
            int collectedBefore = f.Inventory.LifetimeCollected;
            int recoveredBefore = f.Inventory.RecoveredValue;
            f.Check(unitsBefore == 11 && CountUnitsOf(f, f.Coin) == 11 && StacksOf(f, f.Coin) == 2,
                $"the bag holds eleven coins in two stacks ({unitsBefore} units, {StacksOf(f, f.Coin)} stacks)");

            Snapshot before = Take(f);
            f.CollectedEvents = 0;
            f.InventoryEvents = 0;

            yield return ClickCell(f, source);
            f.Check(f.Screen.SelectedItem == f.Coin && f.Screen.SelectedAmount == 6,
                $"the six-coin stack is selected with its full count ({f.Screen.SelectedAmount})");

            yield return ClickCell(f, target);

            int headroom = limit - 5;
            f.Check(f.Inventory.Slots.Count == 2 && CountUnitsOf(f, f.Coin) == 11 && StacksOf(f, f.Coin) == 2,
                $"the merge took only the target's headroom and kept the remainder as its own stack " +
                $"({CountUnitsOf(f, f.Coin)} coins in {StacksOf(f, f.Coin)} stacks, headroom was {headroom})");
            f.Check(CountAt(f, target) == limit && CountAt(f, source) == 6 - headroom,
                $"the target filled to {limit} and the source kept {6 - headroom} at {source} " +
                $"({CountAt(f, target)} at {target}, {CountAt(f, source)} at {source})");
            f.Check(f.Screen.SelectedItem == null, "a committed merge ends the selection");
            f.Check(f.Inventory.TotalUnits == unitsBefore && f.Inventory.CarriedValue == valueBefore,
                $"a merge rearranges units between stacks ({f.Inventory.TotalUnits} units, {f.Inventory.CarriedValue} value)");
            f.Check(f.Inventory.LifetimeCollected == collectedBefore && f.Inventory.RecoveredValue == recoveredBefore,
                $"a merge credited nothing ({f.Inventory.LifetimeCollected} collected, {f.Inventory.RecoveredValue} recovered)");
            f.Check(f.CollectedEvents == 0, "a merge raised no ItemCollected event");
            f.Check(f.InventoryEvents == 1, $"one committed merge raised exactly one InventoryChanged ({f.InventoryEvents})");

            // The exact remainder: the target is full now, so the second merge must refuse and touch nothing.
            f.InventoryEvents = 0;
            yield return ClickCell(f, source);
            f.Check(f.Screen.SelectedItem == f.Coin && f.Screen.SelectedAmount == 6 - headroom,
                $"the remainder at {source} is selectable with its exact count ({f.Screen.SelectedAmount})");

            yield return ClickCell(f, target);

            f.Check(CountAt(f, target) == limit && CountAt(f, source) == 6 - headroom &&
                    CountUnitsOf(f, f.Coin) == 11 && f.Inventory.Slots.Count == 2,
                    $"a merge into a full stack changed nothing ({CountAt(f, target)} at {target}, {CountAt(f, source)} at {source})");
            f.Check(f.Screen.SelectedItem == f.Coin && f.Screen.SelectedOrigin == source,
                "the refused merge kept the selection pointing at the remainder");
            f.Check(f.Inventory.TotalUnits == unitsBefore && f.Inventory.CarriedValue == valueBefore &&
                    f.Inventory.LifetimeCollected == collectedBefore && f.Inventory.RecoveredValue == recoveredBefore,
                "the refused merge conserved units, value and credit");
            f.Check(f.CollectedEvents == 0 && f.InventoryEvents == 0,
                $"the refused merge notified nothing ({f.InventoryEvents} notifications)");

            // The remainder is still the player's, and it still moves.
            Vector2Int remainderCell = new Vector2Int(5, 0);
            yield return ClickCell(f, remainderCell);

            f.Check(CountAt(f, remainderCell) == 6 - headroom && CountUnitsOf(f, f.Coin) == 11 &&
                    f.Inventory.TotalUnits == unitsBefore && f.Inventory.UsedCells == 2,
                $"the remainder moved to {remainderCell} and the eleven coins are still carried " +
                $"({CountUnitsOf(f, f.Coin)} coins in {StacksOf(f, f.Coin)} stacks, {f.Inventory.UsedCells} cells)");
            f.Check(f.CollectedEvents == 0 && f.Inventory.LifetimeCollected == collectedBefore,
                "moving the remainder credited nothing");
        }

        private static IEnumerator SelectedCopyEquipsNotTheFirstMatch(Fixture f)
        {
            Debug.Log("[Emberdelve][Smoke:inventoryui] ---- selected copy equips");
            ResetBag(f);
            yield return WaitFrames(2);
            OpenScreen(f);
            yield return WaitFrames(2);

            Vector2Int first = new Vector2Int(0, 0);
            Vector2Int second = new Vector2Int(2, 0);
            f.Check(f.Inventory.TryAddAt(f.Cuirass, 1, first, out string addFirst), $"first cuirass at {first} ({addFirst})");
            f.Check(f.Inventory.TryAddAt(f.Cuirass, 1, second, out string addSecond), $"second cuirass at {second} ({addSecond})");
            yield return WaitFrames(2);

            f.Check(f.Inventory.Slots.Count == 2 && CountUnitsOf(f, f.Cuirass) == 2 && f.Inventory.UsedCells == 12,
                $"two copies of one definition occupy two footprints ({StacksOf(f, f.Cuirass)} stacks, {f.Inventory.UsedCells} cells)");

            Snapshot before = Take(f);
            f.CollectedEvents = 0;
            f.InventoryEvents = 0;

            yield return ClickCell(f, second);
            f.Check(f.Screen.SelectedItem == f.Cuirass && f.Screen.SelectedOrigin == second,
                $"the copy at {second} is the one selected, not the first stack with that definition");

            yield return ClickEquipSlot(f, EquipmentSlot.Chest);

            f.Check(f.Equipment.Equipped(EquipmentSlot.Chest) == f.Cuirass && f.Equipment.FilledCount == 1,
                "the chest slot holds the cuirass");
            f.Check(f.Inventory.Slots.Count == 1 && f.Inventory.Slots[0].Origin == first &&
                    f.Inventory.Slots[0].Count == 1,
                $"the copy that was not selected is still one unit at its own cell {first} ({Take(f)})");
            f.Check(FootprintFree(f, second, f.Cuirass.EffectiveFootprint),
                $"the selected footprint at {second} is the one that left the grid");
            f.Check(f.Screen.SelectedItem == null, "a committed equip ends the selection");
            f.Check(f.Inventory.TotalUnits == before.Units - 1 && f.Inventory.CarriedValue == before.Value - f.Cuirass.value,
                $"exactly one unit left the bag ({f.Inventory.TotalUnits} units, {f.Inventory.CarriedValue} value)");
            f.Check(f.Inventory.LifetimeCollected == before.Collected && f.Inventory.RecoveredValue == before.Recovered,
                "equipping is not collecting");
            f.Check(f.CollectedEvents == 0, "equipping raised no ItemCollected event");
            f.Check(f.InventoryEvents == 1, $"one committed equip raised exactly one InventoryChanged ({f.InventoryEvents})");

            // Clicking the worn slot takes it off through the equipment API, and the returned stack is the
            // one the cursor may then point at - not the copy that never left the bag.
            yield return ClickEquipSlot(f, EquipmentSlot.Chest);

            f.Check(f.Equipment.Equipped(EquipmentSlot.Chest) == null && f.Equipment.FilledCount == 0,
                "clicking the worn slot unequipped the cuirass");
            f.Check(CountUnitsOf(f, f.Cuirass) == 2 && StacksOf(f, f.Cuirass) == 2 && f.Inventory.UsedCells == 12,
                $"the worn copy is back in the bag as one more stack, not a third copy ({CountUnitsOf(f, f.Cuirass)} units in {StacksOf(f, f.Cuirass)} stacks)");
            f.Check(f.Screen.SelectedItem == f.Cuirass && f.Screen.SelectedOrigin == second &&
                    CountAt(f, second) == 1 && CountAt(f, first) == 1,
                $"the cursor resolved to the stack the unequip just put back at {second}, not to the copy that never left the bag at {first} " +
                $"(selected at {f.Screen.SelectedOrigin}, {CountAt(f, first)} still held at {first})");
            f.Check(f.Inventory.TotalUnits == before.Units && f.Inventory.CarriedValue == before.Value,
                $"the unequip put the unit back rather than creating one ({f.Inventory.TotalUnits} units, {f.Inventory.CarriedValue} value)");
            f.Check(f.Inventory.LifetimeCollected == before.Collected && f.Inventory.RecoveredValue == before.Recovered,
                "the unequip credited nothing");
            f.Check(f.CollectedEvents == 0, "the unequip raised no ItemCollected event");
        }

        private static IEnumerator WrongSlotRefusalKeepsOwnership(Fixture f)
        {
            Debug.Log("[Emberdelve][Smoke:inventoryui] ---- wrong slot refusal");
            ResetBag(f);
            yield return WaitFrames(2);
            OpenScreen(f);
            yield return WaitFrames(2);

            Vector2Int origin = new Vector2Int(0, 0);
            f.Check(f.Inventory.TryAddAt(f.Boots, 1, origin, out string addBoots), $"boots placed at {origin} ({addBoots})");
            yield return WaitFrames(2);

            Snapshot before = Take(f);
            f.CollectedEvents = 0;
            f.InventoryEvents = 0;

            yield return ClickCell(f, origin);
            f.Check(f.Screen.SelectedItem == f.Boots && f.Screen.SelectedOrigin == origin, "the boots are selected");

            yield return ClickEquipSlot(f, EquipmentSlot.Chest);

            f.Check(f.Equipment.Equipped(EquipmentSlot.Chest) == null && f.Equipment.FilledCount == 0,
                "the chest slot stayed empty: leg gear was not worn as chest gear");
            f.Check(f.Screen.SelectedItem == f.Boots && f.Screen.SelectedOrigin == origin &&
                    f.Screen.SelectedAmount == 1,
                "the refusal kept the selection pointing at the boots");
            f.Check(f.Inventory.Slots.Count == 1 && f.Inventory.Slots[0].Origin == origin &&
                    f.Inventory.Slots[0].Count == 1,
                "the boots are still in the bag at their own cell");
            f.Check(Same(before, Take(f)), $"the refusal changed nothing ({Take(f)})");
            f.Check(f.CollectedEvents == 0 && f.InventoryEvents == 0,
                $"the refusal notified and credited nothing ({f.InventoryEvents} notifications)");

            yield return ClickEquipSlot(f, EquipmentSlot.Legs);

            f.Check(f.Equipment.Equipped(EquipmentSlot.Legs) == f.Boots && f.Equipment.FilledCount == 1,
                "the same selection equips through the slot the item declares");
            f.Check(f.Inventory.IndexOfItem(f.Boots) < 0 && f.Inventory.Slots.Count == 0,
                "the boots left the bag exactly once, so the refusal created no second copy");
            f.Check(f.Screen.SelectedItem == null, "the committed equip ends the selection");
            f.Check(f.Inventory.TotalUnits == before.Units - 1 && f.Inventory.CarriedValue == before.Value - f.Boots.value,
                $"one unit moved out of the bag ({f.Inventory.TotalUnits} units, {f.Inventory.CarriedValue} value)");
            f.Check(f.Inventory.LifetimeCollected == before.Collected && f.Inventory.RecoveredValue == before.Recovered,
                "the equip and its refusal credited nothing");
            f.Check(f.CollectedEvents == 0, "no ItemCollected event was raised");
        }

        private static IEnumerator CancelAndCloseConserveEverything(Fixture f)
        {
            Debug.Log("[Emberdelve][Smoke:inventoryui] ---- cancel and close");
            ResetBag(f);
            yield return WaitFrames(2);
            OpenScreen(f);
            yield return WaitFrames(2);

            Vector2Int origin = new Vector2Int(0, 0);
            f.Check(f.Inventory.TryAddAt(f.Cuirass, 1, origin, out string addReason), $"cuirass placed at {origin} ({addReason})");
            yield return WaitFrames(2);

            int pickupsBefore = LootPickups(f);
            Snapshot before = Take(f);
            f.CollectedEvents = 0;
            f.InventoryEvents = 0;

            yield return ClickCell(f, origin);
            f.Check(f.Screen.SelectedItem == f.Cuirass, "the cuirass is selected");

            // Same-cell left click is the grid's cancel gesture.
            yield return ClickCell(f, origin);
            f.Check(f.Screen.SelectedItem == null, "clicking the selected stack again cancels it");
            f.Check(f.Inventory.Slots.Count == 1 && f.Inventory.Slots[0].Origin == origin &&
                    f.Inventory.Slots[0].Count == 1, "the cancel left the stack exactly where it was");
            f.Check(f.InventoryEvents == 0, "a cancel notifies nothing");

            yield return ClickCell(f, origin);
            f.Check(f.Screen.SelectedItem == f.Cuirass, "the cuirass is selected again");

            // Right click is cancel too, and it cannot put anything anywhere.
            yield return ClickCell(f, MoveTarget, true);
            f.Check(f.Screen.SelectedItem == null, "right click let go of the selection");
            f.Check(Same(before, Take(f)), $"cancelling conserved the bag ({Take(f)})");
            f.Check(f.Inventory.LifetimeCollected == before.Collected && f.Inventory.RecoveredValue == before.Recovered,
                "cancelling credited nothing");
            f.Check(f.CollectedEvents == 0, "cancelling raised no ItemCollected event");
            f.Check(LootPickups(f) == pickupsBefore,
                $"cancelling dropped nothing in the world ({LootPickups(f)} pickups, was {pickupsBefore})");

            yield return ClickCell(f, origin);
            f.Check(f.Screen.SelectedItem == f.Cuirass && f.Screen.HasSelection, "the cuirass is selected for the close test");

            f.Screen.Close();
            yield return WaitFrames(2);

            f.Check(!f.Screen.IsOpen, "the screen closed");
            f.Check(f.Screen.SelectedItem == null && !f.Screen.HasSelection && f.Screen.SelectedAmount == 0,
                "closing cleared the selection and the ghost");
            f.Check(f.Inventory.Slots.Count == 1 && f.Inventory.Slots[0].Origin == origin &&
                    f.Inventory.Slots[0].Count == 1 && f.Inventory.UsedCells == 6,
                    $"closing did not move, drop, duplicate or collect the stack it was pointing at ({Take(f)})");
            f.Check(Same(before, Take(f)), $"closing conserved units, value, cells and credit ({Take(f)})");
            f.Check(f.CollectedEvents == 0, "closing raised no ItemCollected event");
            f.Check(LootPickups(f) == pickupsBefore,
                $"closing spawned no loot at the player's feet ({LootPickups(f)} pickups, was {pickupsBefore})");
            f.Check(f.InventoryEvents == 0, $"closing notified nothing ({f.InventoryEvents} notifications)");
        }

        private static IEnumerator ExternalBagChangeKeepsSelectionHonest(Fixture f)
        {
            Debug.Log("[Emberdelve][Smoke:inventoryui] ---- external bag change");
            ResetBag(f);
            yield return WaitFrames(2);
            OpenScreen(f);
            yield return WaitFrames(2);

            Vector2Int bladeOrigin = new Vector2Int(0, 0);
            Vector2Int coinOrigin = new Vector2Int(2, 0);
            Vector2Int target = new Vector2Int(6, 0);

            f.Check(f.Inventory.TryAddAt(f.Blade, 1, bladeOrigin, out string addBlade), $"1x3 blade placed at {bladeOrigin} ({addBlade})");
            f.Check(f.Inventory.TryAddAt(f.Coin, 3, coinOrigin, out string addCoin), $"three coins placed at {coinOrigin} ({addCoin})");
            yield return WaitFrames(2);

            yield return ClickCell(f, bladeOrigin);
            f.Check(f.Screen.SelectedItem == f.Blade && f.Screen.SelectedOrigin == bladeOrigin, "the blade is selected");

            Snapshot before = Take(f);
            f.CollectedEvents = 0;
            f.InventoryEvents = 0;

            int coinIndex = f.Inventory.IndexOfItem(f.Coin);
            int coinCount = f.Inventory.Slots[coinIndex].Count;
            int removed = f.Inventory.TryRemoveAt(coinIndex, coinCount);

            f.Check(removed == 3 && f.Inventory.IndexOfItem(f.Coin) < 0,
                $"an unrelated stack was taken out of the bag behind the cursor ({removed} coins removed)");
            yield return WaitFrames(2);

            f.Check(f.Screen.SelectedItem == f.Blade && f.Screen.SelectedOrigin == bladeOrigin,
                "the selection survived the removal because it names a cell, not an index");

            yield return ClickCell(f, target);

            f.Check(OriginOf(f, f.Blade) == target && CountUnitsOf(f, f.Blade) == 1 &&
                    FootprintOccupied(f, target, f.Blade.EffectiveFootprint),
                $"the still-owned selected stack is the one that moved to {target} ({Name(f.Blade)} at {OriginOf(f, f.Blade)})");
            f.Check(FootprintFree(f, bladeOrigin, f.Blade.EffectiveFootprint), "the blade's old cells are free");
            f.Check(f.Inventory.IndexOfItem(f.Coin) < 0 && CountUnitsOf(f, f.Coin) == 0,
                "the removed coins did not come back as a copy");
            f.Check(f.Inventory.TotalUnits == before.Units - 3 && f.Inventory.CarriedValue == before.Value - 3 * f.Coin.value,
                $"only the externally removed units left the bag ({f.Inventory.TotalUnits} units, {f.Inventory.CarriedValue} value)");
            f.Check(f.Inventory.LifetimeCollected == before.Collected && f.Inventory.RecoveredValue == before.Recovered,
                "the external removal and the move credited nothing");
            f.Check(f.CollectedEvents == 0, "no ItemCollected event was raised");

            // Now have the selected stack itself removed. The selection must die with it, and the next click
            // must not move whatever happens to sit where that stack used to be.
            Vector2Int coinCell = new Vector2Int(3, 0);
            f.Check(f.Inventory.TryAddAt(f.Coin, 2, coinCell, out string addCoinsAgain), $"two coins placed at {coinCell} ({addCoinsAgain})");
            yield return WaitFrames(2);

            yield return ClickCell(f, coinCell);
            f.Check(f.Screen.SelectedItem == f.Coin && f.Screen.SelectedOrigin == coinCell, "the coin stack is selected");

            Snapshot selected = Take(f);
            f.Check(selected.Units == 3 && CountUnitsOf(f, f.Coin) == 2 && StacksOf(f, f.Coin) == 1,
                $"the bag holds the blade and the two coins before the second external removal ({selected})");

            int selectedIndex = f.Inventory.IndexOfItem(f.Coin);
            int removedAgain = f.Inventory.TryRemoveAt(selectedIndex, f.Inventory.Slots[selectedIndex].Count);
            f.Check(removedAgain == 2, $"the selected stack itself was removed externally ({removedAgain} coins)");
            yield return WaitFrames(2);

            // Measured from after that removal: the only thing this click is allowed to change is nothing.
            Snapshot afterRemoval = Take(f);
            f.CollectedEvents = 0;
            f.InventoryEvents = 0;

            Vector2Int emptyCell = new Vector2Int(8, 0);
            f.Check(!f.Inventory.IsCellOccupied(emptyCell), $"cell {emptyCell} is free for the stale-selection click");

            yield return ClickCell(f, emptyCell);

            f.Check(f.Screen.SelectedItem == null, "a selection whose stack is gone is cancelled, not followed");
            f.Check(OriginOf(f, f.Blade) == target && CountUnitsOf(f, f.Blade) == 1,
                $"the click did not drag the stack that happens to sit nearby ({Name(f.Blade)} still at {OriginOf(f, f.Blade)})");
            f.Check(CountUnitsOf(f, f.Coin) == 0 && f.Inventory.IndexOfItem(f.Coin) < 0,
                "the removed coins were not duplicated back into the bag");
            f.Check(Same(afterRemoval, Take(f)),
                $"the stale selection path changed nothing ({afterRemoval} -> {Take(f)})");
            f.Check(f.CollectedEvents == 0 && f.InventoryEvents == 0,
                $"the cancelled selection notified and credited nothing ({f.InventoryEvents} notifications)");
        }

        // ------------------------------------------------------------------ click plumbing

        private static IEnumerator ClickCell(Fixture f, Vector2Int cell, bool rightClick = false)
        {
            RectTransform rect = FindChildRect(f.UiRoot, "Cell_" + cell.x + "_" + cell.y);
            if (rect == null)
            {
                f.Check(false, $"the screen rendered a cell rectangle for cell {cell}");
                yield break;
            }

            Vector2 screenPoint = ScreenPoint(f, rect);
            f.Screen.HandleClick(screenPoint, rightClick);
            Canvas.ForceUpdateCanvases();
            yield return WaitFrames(2);
        }

        private static IEnumerator ClickEquipSlot(Fixture f, EquipmentSlot slot, bool rightClick = false)
        {
            RectTransform rect = FindChildRect(f.UiRoot, "Equip_" + slot);
            if (rect == null)
            {
                f.Check(false, $"the screen rendered a rectangle for the {slot} equipment slot");
                yield break;
            }

            Vector2 screenPoint = ScreenPoint(f, rect);
            f.Screen.HandleClick(screenPoint, rightClick);
            Canvas.ForceUpdateCanvases();
            yield return WaitFrames(2);
        }

        /// <summary>
        /// The centre of a rendered rectangle, in the screen coordinates HandleClick is given. The canvas's
        /// own render mode decides whether a camera is needed, exactly as it does inside the screen.
        /// </summary>
        private static Vector2 ScreenPoint(Fixture f, RectTransform rect)
        {
            Camera camera = null;
            if (f.Canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                camera = f.Canvas.worldCamera != null ? f.Canvas.worldCamera : Camera.main;
            }

            return RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
        }

        private static void ResetBag(Fixture f)
        {
            // Closing goes through the production path, so any selection left by the previous scenario is
            // cleared the way a player's close would clear it.
            f.Screen.Close();
            f.Equipment.Clear();
            f.Inventory.Clear();
        }

        private static void OpenScreen(Fixture f)
        {
            if (!f.Screen.IsOpen) f.Screen.Open();
            Canvas.ForceUpdateCanvases();
        }

        // ------------------------------------------------------------------ read-only helpers

        private static Snapshot Take(Fixture f)
        {
            return new Snapshot
            {
                Units = f.Inventory.TotalUnits,
                Value = f.Inventory.CarriedValue,
                Cells = f.Inventory.UsedCells,
                Stacks = f.Inventory.Slots.Count,
                Collected = f.Inventory.LifetimeCollected,
                Recovered = f.Inventory.RecoveredValue,
                UsedSlotsView = f.Inventory.UsedSlots,
            };
        }

        private static bool Same(Snapshot a, Snapshot b)
        {
            return a.Units == b.Units && a.Value == b.Value && a.Cells == b.Cells &&
                   a.Stacks == b.Stacks && a.Collected == b.Collected && a.Recovered == b.Recovered &&
                   a.UsedSlotsView == b.UsedSlotsView;
        }

        private static int CountUnitsOf(Fixture f, ItemData item)
        {
            int units = 0;
            for (int i = 0; i < f.Inventory.Slots.Count; i++)
            {
                if (f.Inventory.Slots[i].Item == item) units += f.Inventory.Slots[i].Count;
            }
            return units;
        }

        private static int StacksOf(Fixture f, ItemData item)
        {
            int stacks = 0;
            for (int i = 0; i < f.Inventory.Slots.Count; i++)
            {
                if (f.Inventory.Slots[i].Item == item) stacks++;
            }
            return stacks;
        }

        private static Vector2Int OriginOf(Fixture f, ItemData item)
        {
            for (int i = 0; i < f.Inventory.Slots.Count; i++)
            {
                if (f.Inventory.Slots[i].Item == item) return f.Inventory.Slots[i].Origin;
            }
            return new Vector2Int(-1, -1);
        }

        private static int CountAt(Fixture f, Vector2Int cell)
        {
            int index = f.Inventory.IndexAtCell(cell);
            return index >= 0 ? f.Inventory.Slots[index].Count : 0;
        }

        private static bool FootprintFree(Fixture f, Vector2Int origin, Vector2Int size)
        {
            for (int x = 0; x < size.x; x++)
            {
                for (int y = 0; y < size.y; y++)
                {
                    if (f.Inventory.IsCellOccupied(origin + new Vector2Int(x, y))) return false;
                }
            }
            return true;
        }

        private static bool FootprintOccupied(Fixture f, Vector2Int origin, Vector2Int size)
        {
            for (int x = 0; x < size.x; x++)
            {
                for (int y = 0; y < size.y; y++)
                {
                    if (!f.Inventory.IsCellOccupied(origin + new Vector2Int(x, y))) return false;
                }
            }
            return true;
        }

        private static int LootPickups(Fixture f)
        {
            return UnityEngine.Object.FindObjectsByType<LootPickup>(FindObjectsSortMode.None).Length;
        }

        private static string Name(ItemData item)
        {
            return item != null ? item.displayName : "none";
        }

        private static RectTransform FindChildRect(Transform root, string name)
        {
            if (root == null) return null;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child.name == name) return child as RectTransform;

                RectTransform found = FindChildRect(child, name);
                if (found != null) return found;
            }

            return null;
        }

        private static int CountNamed(Transform root, string name)
        {
            if (root == null) return 0;

            int count = 0;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child.name == name) count++;
                count += CountNamed(child, name);
            }
            return count;
        }

        private static IEnumerator WaitFrames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }
    }
}
