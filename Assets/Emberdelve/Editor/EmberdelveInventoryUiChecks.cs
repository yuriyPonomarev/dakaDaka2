using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
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
            public int HotbarEvents;
            public readonly List<string> Toasts = new List<string>();

            public void OnItemCollected(int playerId, ItemData item, int totalCollected) { CollectedEvents++; }
            public void OnInventoryChanged(int playerId) { InventoryEvents++; }
            public void OnHotbarChanged(int playerId) { HotbarEvents++; }
            public void OnToast(int playerId, string message) { Toasts.Add(message); }
        }

        public static IEnumerator Run(PlayerRig rig, RunDirector director, System.Action<bool, string> check, bool quickAccessOnly = false)
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
            GameEvents.HotbarChanged += fixture.OnHotbarChanged;
            GameEvents.ToastRaised += fixture.OnToast;

            try
            {
                // R6c2: the raid loadout the run just established must have bound the first usable stacks.
                InitialLoadoutBindsDefaults(fixture);

                yield return PrepareScreen(fixture);
                if (fixture.UiRoot == null) yield break;

                if (!quickAccessOnly)
                {
                yield return ClickedMoveConservesTheStack(fixture);
                yield return ValidSwapConservesBothStacks(fixture);
                yield return ImpossibleSwapChangesNothing(fixture);
                yield return PartialMergeKeepsTheRemainder(fixture);
                yield return SelectedCopyEquipsNotTheFirstMatch(fixture);
                yield return WrongSlotRefusalKeepsOwnership(fixture);
                yield return CancelAndCloseConserveEverything(fixture);
                yield return ExternalBagChangeKeepsSelectionHonest(fixture);
                yield return StableSelectionTracksIdentity(fixture);
                yield return RebindingDropsSelectionFromTheOldBag(fixture);
                }
                yield return RightClickUseSubmitsCommand(fixture);
                yield return HotbarDefaultsAtLoadoutAndRestore(fixture);
                yield return HotbarKeyUsesBoundStackIdentity(fixture);
                yield return HotbarRetiredBindingDoesNothing(fixture);
                yield return HotbarAssignFromOpenScreen(fixture);
                yield return HotbarRealKeyboardPath(fixture);
                yield return HotbarHudMatchesBindings(fixture);
                if (!quickAccessOnly)
                {
                    yield return CloakSlotAndTooltips(fixture);
                    yield return PlayerFacingFixes(fixture);
                }

                check(!rig.IsDead, $"the rig survived the pointer tests ({rig.CurrentHealth:F0}/{rig.MaxHealth:F0})");
            }
            finally
            {
                // Observers and screen state are the fixture's to clean up, not the run's to inherit.
                GameEvents.ItemCollected -= fixture.OnItemCollected;
                GameEvents.InventoryChanged -= fixture.OnInventoryChanged;
                GameEvents.HotbarChanged -= fixture.OnHotbarChanged;
                GameEvents.ToastRaised -= fixture.OnToast;

                fixture.Screen.Close();
                fixture.Equipment.ClearToBare();
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

            yield return ClickEquipSlot(f, EquipmentSlot.Feet);

            f.Check(f.Equipment.Equipped(EquipmentSlot.Feet) == f.Boots && f.Equipment.FilledCount == 1,
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

        // ------------------------------------------------------------------ U2B11: cloak slot, containment, instance-keyed tooltips

        private static bool Contains(Vector3[] outer, Vector3[] inner, float tolerance)
        {
            return inner[0].x >= outer[0].x - tolerance && inner[0].y >= outer[0].y - tolerance &&
                   inner[2].x <= outer[2].x + tolerance && inner[2].y <= outer[2].y + tolerance;
        }

        private static Vector2Int OriginOfInstance(Fixture f, ItemInstance instance)
        {
            int index = f.Inventory.IndexOfInstance(instance.Uid);
            return index >= 0 ? f.Inventory.Slots[index].Origin : new Vector2Int(-1, -1);
        }

        private static IEnumerator StableSelectionTracksIdentity(Fixture f)
        {
            Debug.Log("[Emberdelve][Smoke:inventoryui] ---- stable stack identity selection");
            ResetBag(f);
            yield return WaitFrames(2);
            OpenScreen(f);
            yield return WaitFrames(2);

            Vector2Int originalCell = new Vector2Int(0, 0);
            Vector2Int unrelatedCell = new Vector2Int(4, 0);
            Vector2Int movedCell = new Vector2Int(6, 0);
            Vector2Int finalCell = new Vector2Int(8, 1);

            f.Check(f.Inventory.TryAddAt(f.Coin, 2, unrelatedCell, out string addUnrelated),
                $"an unrelated stack is placed before the selected stack ({addUnrelated})");
            f.Check(f.Inventory.TryAddAt(f.Blade, 1, originalCell, out string addOriginal),
                $"original blade instance placed at {originalCell} ({addOriginal})");
            yield return WaitFrames(2);
            int originalIndex = f.Inventory.IndexAtCell(originalCell);
            f.Check(originalIndex >= 0, "the original blade resolves in the live inventory");
            if (originalIndex < 0) yield break;
            InventorySlot originalBefore = f.Inventory.Slots[originalIndex];
            ulong originalStackId = originalBefore.StackId;
            System.Guid originalUid = originalBefore.Instance != null ? originalBefore.Instance.Uid : System.Guid.Empty;
            f.Check(originalIndex == 1, "the selected blade begins after the unrelated stack in list order");

            yield return ClickCell(f, originalCell);
            f.Check(originalUid != System.Guid.Empty && f.Screen.SelectedStackId == originalStackId,
                $"the exact blade stack is selected by stable ID {originalStackId} and UID {originalUid}");

            int unrelatedIndex = f.Inventory.IndexAtCell(unrelatedCell);
            int unrelatedRemoved = f.Inventory.TryRemoveAt(unrelatedIndex, 2);
            yield return WaitFrames(2);
            int shiftedIndex = f.Inventory.IndexOfStack(originalStackId);
            f.Check(unrelatedRemoved == 2 && shiftedIndex == 0 && f.Screen.SelectedStackId == originalStackId,
                "removing an earlier list entry shifts indices without changing the selected identity");

            f.Check(f.Inventory.TryMove(shiftedIndex, movedCell, out string moveReason),
                $"an external bag move repositions the selected stack ({moveReason})");
            yield return WaitFrames(2);
            f.Check(f.Screen.SelectedStackId == originalStackId && f.Screen.SelectedOrigin == movedCell,
                "an external move keeps the selected StackId and refreshes its current origin");

            f.Check(f.Inventory.TryAddAt(f.Blade, 1, originalCell, out string addLookalike),
                $"a lookalike blade occupies the old origin ({addLookalike})");
            yield return WaitFrames(2);
            int lookalikeIndex = f.Inventory.IndexAtCell(originalCell);
            f.Check(lookalikeIndex >= 0, "the lookalike resolves at the former origin");
            if (lookalikeIndex < 0) yield break;
            InventorySlot lookalike = f.Inventory.Slots[lookalikeIndex];
            f.Check(lookalike.StackId != originalStackId && lookalike.Instance != null
                && lookalike.Instance.Uid != originalUid,
                "the same definition at the old cell is a different live stack and gear UID");
            f.Check(f.Screen.SelectedStackId == originalStackId && f.Screen.SelectedOrigin == movedCell,
                "rebuild at the old origin does not retarget the selection to a lookalike");

            f.InventoryEvents = 0;
            ulong beforeCommandRevision = f.Inventory.Revision;
            yield return ClickCell(f, finalCell);
            int finalIndex = f.Inventory.IndexOfStack(originalStackId);
            int lookalikeAtOrigin = f.Inventory.IndexAtCell(originalCell);
            f.Check(finalIndex >= 0 && f.Inventory.Slots[finalIndex].Origin == finalCell
                && f.Inventory.Slots[finalIndex].Instance != null
                && f.Inventory.Slots[finalIndex].Instance.Uid == originalUid,
                $"the UI command moves the originally selected UID to {finalCell}");
            f.Check(lookalikeAtOrigin >= 0 && f.Inventory.Slots[lookalikeAtOrigin].StackId == lookalike.StackId
                && f.Inventory.Slots[lookalikeAtOrigin].Instance != null
                && lookalike.Instance != null
                && f.Inventory.Slots[lookalikeAtOrigin].Instance.Uid == lookalike.Instance.Uid,
                "the lookalike at the former origin stays untouched by the UI command");
            f.Check(f.Inventory.Revision == beforeCommandRevision + 1 && f.InventoryEvents == 1,
                $"the accepted UI command commits one shared revision and one inventory event ({f.Inventory.Revision - beforeCommandRevision}, {f.InventoryEvents})");
            f.Check(f.Screen.SelectedStackId == 0 && !f.Screen.HasSelection,
                "a committed move clears the selection");

            // Retiring a selected StackId and replacing it with the same definition at the same origin must
            // clear the cursor, so a later click cannot act through a stale selection.
            yield return ClickCell(f, finalCell);
            f.Check(f.Screen.SelectedStackId == originalStackId, "the moved original stack can be selected again");
            finalIndex = f.Inventory.IndexOfStack(originalStackId);
            int removed = f.Inventory.TryRemoveAt(finalIndex, 1);
            f.Check(removed == 1, "the selected stack is removed by an external inventory owner");
            f.Check(f.Inventory.TryAddAt(f.Blade, 1, finalCell, out string addReplacement),
                $"a replacement blade reuses the retired stack's origin ({addReplacement})");
            yield return WaitFrames(2);
            int replacementIndex = f.Inventory.IndexAtCell(finalCell);
            f.Check(replacementIndex >= 0, "the replacement resolves at the retired origin");
            if (replacementIndex < 0) yield break;
            InventorySlot replacement = f.Inventory.Slots[replacementIndex];
            f.Check(replacement.StackId != originalStackId && replacement.Instance != null && replacement.Instance.Uid != originalUid,
                "replacement at the same definition and cell has new stack and gear identity");
            f.Check(!f.Screen.HasSelection && f.Screen.SelectedStackId == 0,
                "rebuild clears selection when its exact StackId is gone");

            Vector2Int untouchedCell = new Vector2Int(9, 0);
            yield return ClickCell(f, untouchedCell);
            replacementIndex = f.Inventory.IndexOfStack(replacement.StackId);
            f.Check(replacementIndex >= 0 && f.Inventory.Slots[replacementIndex].Origin == finalCell,
                "a post-removal click cannot move the replacement through a stale selection");
        }

        // R6d: a selection belongs to the bag it was made in. StackIds are minted per bag, so a stack id from
        // bag A must never act in bag B. The screen's selection model is reached by reflection because no public
        // member re-points the screen at a different bag without a second rig.
        private static IEnumerator RebindingDropsSelectionFromTheOldBag(Fixture f)
        {
            Debug.Log("[Emberdelve][Smoke:inventoryui] ---- rebinding the screen to another bag");
            ResetBag(f);
            yield return WaitFrames(2);
            OpenScreen(f);
            yield return WaitFrames(2);

            Vector2Int coinCell = new Vector2Int(0, 0);
            f.Check(f.Inventory.TryAddAt(f.Coin, 3, coinCell, out string addReason), "a coin stack is placed in bag A: " + addReason);
            yield return WaitFrames(2);
            yield return ClickCell(f, coinCell);
            f.Check(f.Screen.HasSelection && f.Screen.SelectedItem == f.Coin, "the coin stack of bag A is selected");

            FieldInfo modelField = typeof(InventoryScreenController).GetField("_model", BindingFlags.Instance | BindingFlags.NonPublic);
            object model = modelField != null ? modelField.GetValue(f.Screen) : null;
            MethodInfo rebind = model != null
                ? model.GetType().GetMethod("RebindInventory", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                : null;
            f.Check(rebind != null, "the screen's selection model exposes its bag rebinding");
            if (rebind == null) yield break;

            GameObject otherObject = new GameObject("RebindCheckBag");
            Inventory otherBag = otherObject.AddComponent<Inventory>();
            try
            {
                bool changed = (bool)rebind.Invoke(model, new object[] { otherBag });
                f.Check(changed && !f.Screen.HasSelection && f.Screen.SelectedStackId == 0 && f.Screen.SelectedItem == null,
                    "rebinding to bag B drops the selection made in bag A");

                bool unchanged = (bool)rebind.Invoke(model, new object[] { otherBag });
                f.Check(!unchanged, "rebinding to the bag already bound changes nothing");
            }
            finally
            {
                rebind.Invoke(model, new object[] { f.Inventory });
                UnityEngine.Object.DestroyImmediate(otherObject);
            }

            yield return WaitFrames(2);
            f.Check(!f.Screen.HasSelection && f.Inventory.IndexAtCell(coinCell) >= 0,
                "bag A still holds its coins and the old selection does not come back after rebinding to it");
            f.Screen.Close();
        }

        private static IEnumerator RightClickUseSubmitsCommand(Fixture f)
        {
            Debug.Log("[Emberdelve][Smoke:inventoryui] ---- right-click use command");
            ItemData draught = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_BloodDraught.asset");
            f.Check(draught != null && draught.healAmount > 0, "the healing consumable is loadable for the UI use command");
            if (draught == null || draught.healAmount <= 0) yield break;

            ResetBag(f);
            yield return WaitFrames(2);
            OpenScreen(f);
            yield return WaitFrames(2);

            f.Rig.Health.SetHealth(Mathf.Max(1f, f.Rig.MaxHealth - 20f));
            Vector2Int cell = new Vector2Int(0, 0);
            f.Check(f.Inventory.TryAddAt(draught, 1, cell, out string reason),
                "one draught is placed for the right-click command: " + reason);
            yield return WaitFrames(2);

            int index = f.Inventory.IndexAtCell(cell);
            ulong stackId = f.Inventory.Slots[index].StackId;
            float healthBefore = f.Rig.CurrentHealth;
            ulong revisionBefore = f.Inventory.Revision;
            f.InventoryEvents = 0;

            RectTransform cellRect = FindChildRect(f.UiRoot, "Cell_0_0");
            f.Check(cellRect != null, "the potion cell is rendered");
            if (cellRect == null) yield break;
            f.Screen.HandleClick(ScreenPoint(f, cellRect), true);

            f.Check(Mathf.Abs(f.Rig.CurrentHealth - healthBefore) < 0.01f &&
                    f.Inventory.IndexOfStack(stackId) >= 0 && f.Inventory.HeldStackId == stackId,
                 $"right-click holds without consuming/healing: held={f.Inventory.HeldStackId}, expected={stackId}, index={f.Inventory.IndexOfStack(stackId)}, HP={healthBefore}/{f.Rig.CurrentHealth}");
            f.Check(f.Inventory.Revision == revisionBefore + 1 && f.InventoryEvents == 1,
                "taking in hand commits one authoritative inventory change");
            f.Check(!f.Screen.IsOpen && !f.Director.InventoryOpen && !f.Screen.HasSelection,
                "taking in hand closes the bag and clears its selection");
        }

        // ------------------------------------------------------------------ R6c2 hotbar scenarios
        //
        // Key N is read by HotbarController.Update, which needs a keyboard device that batch mode does not
        // expose (see the archer smoke note). It calls exactly Inventory.Hotbar.TryUse(N - 1, ...), so these
        // scenarios call that same public method. Assignment from the open screen calls
        // InventoryScreenController.TryAssignHotbarSlot, which is what the screen's 1-4 key handler calls.

        private static readonly Vector2 OffGrid = new Vector2(-4000f, -4000f);

        private static ulong StackIdOfDefinition(Fixture f, ItemData item)
        {
            for (int i = 0; i < f.Inventory.Slots.Count; i++)
                if (f.Inventory.Slots[i].Item == item) return f.Inventory.Slots[i].StackId;
            return 0;
        }

        private static ulong StackIdAtCell(Fixture f, Vector2Int cell)
        {
            int index = f.Inventory.IndexAtCell(cell);
            return index >= 0 ? f.Inventory.Slots[index].StackId : 0;
        }

        private static string BoundIds(Fixture f)
        {
            var ids = new string[HotbarBindings.SlotCount];
            for (int i = 0; i < ids.Length; i++) ids[i] = f.Inventory.Hotbar.StackIdAt(i).ToString();
            return string.Join(",", ids);
        }

        private static int BoundCount(Fixture f, ulong stackId)
        {
            int count = 0;
            for (int i = 0; i < HotbarBindings.SlotCount; i++)
                if (f.Inventory.Hotbar.StackIdAt(i) == stackId) count++;
            return count;
        }

        private static void InitialLoadoutBindsDefaults(Fixture f)
        {
            List<ulong> expected = new List<ulong>();
            for (int i = 0; i < f.Inventory.Slots.Count && expected.Count < HotbarBindings.SlotCount; i++)
            {
                InventorySlot slot = f.Inventory.Slots[i];
                if (HotbarBindings.IsUsableDefinition(slot.Item)) expected.Add(slot.StackId);
            }

            bool match = true;
            for (int i = 0; i < HotbarBindings.SlotCount; i++)
            {
                ulong want = i < expected.Count ? expected[i] : 0;
                if (f.Inventory.Hotbar.StackIdAt(i) != want) match = false;
            }
            f.Check(expected.Count > 0,
                "the actual authored class loadout contains at least one usable starting stack");

            ItemData authoredDraught = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_BloodDraught.asset");
            ItemData authoredArrows = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_ArrowBundle.asset");
            bool hasNamedAuthoredConsumable = false;
            for (int i = 0; i < expected.Count; i++)
            {
                if (!f.Inventory.TryGetStack(expected[i], out InventorySlot stack)) continue;
                if (stack.Item == authoredDraught || stack.Item == authoredArrows) hasNamedAuthoredConsumable = true;
            }
            f.Check(hasNamedAuthoredConsumable,
                "the authored starting kit includes a Blood Draught or Arrow Bundle consumable");
            f.Check(match, $"the established raid loadout bound its first {expected.Count} usable stack(s) in bag order to keys 1-4 ({BoundIds(f)})");
        }

        private static IEnumerator HotbarDefaultsAtLoadoutAndRestore(Fixture f)
        {
            Debug.Log("[Emberdelve][Smoke:inventoryui] ---- hotbar defaults at loadout, clear and restore");
            ItemData draught = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_BloodDraught.asset");
            ItemData arrows = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_ArrowBundle.asset");
            f.Check(draught != null && arrows != null && HotbarBindings.IsUsableDefinition(draught) &&
                    HotbarBindings.IsUsableDefinition(arrows) && !HotbarBindings.IsUsableDefinition(f.Coin) &&
                    !HotbarBindings.IsUsableDefinition(f.Blade),
                "the draught and the arrow bundle are usable hotbar definitions; the coin and the blade are not");
            if (draught == null || arrows == null) yield break;

            ResetBag(f);
            yield return WaitFrames(2);
            f.Check(BoundCount(f, 0) == HotbarBindings.SlotCount && f.Inventory.Hotbar.StackIdAt(0) == 0,
                "Clear retires every binding");

            f.InventoryEvents = 0;
            f.Inventory.GiveStartingItems(new[] { f.Coin, draught, f.Blade, arrows });
            yield return WaitFrames(2);
            ulong draughtId = StackIdOfDefinition(f, draught);
            ulong arrowsId = StackIdOfDefinition(f, arrows);
            f.Check(draughtId != 0 && arrowsId != 0 && draughtId != arrowsId,
                "the starting kit holds a draught stack and an arrow stack with their own StackIds");
            f.Check(f.Inventory.Hotbar.StackIdAt(0) == draughtId && f.Inventory.Hotbar.StackIdAt(1) == arrowsId &&
                    f.Inventory.Hotbar.StackIdAt(2) == 0 && f.Inventory.Hotbar.StackIdAt(3) == 0,
                $"the starting kit binds the first usable stacks in bag order and skips the coin and the blade ({BoundIds(f)})");

            // No auto-refill: a usable stack that appears later does not take an empty slot.
            f.Check(f.Inventory.TryAddAt(draught, 1, new Vector2Int(8, 3), out string addLate),
                "a second draught stack appears mid-raid: " + addLate);
            ulong lateId = StackIdAtCell(f, new Vector2Int(8, 3));
            yield return WaitFrames(2);
            f.Check(lateId != 0 && lateId != draughtId && f.Inventory.Hotbar.SlotOf(lateId) < 0 &&
                    f.Inventory.Hotbar.StackIdAt(2) == 0 && f.Inventory.Hotbar.StackIdAt(0) == draughtId,
                $"a stack that appears mid-raid is never auto-bound ({BoundIds(f)})");

            // Restore: retire all old identities, bind defaults for the restored loadout.
            ResetBag(f);
            yield return WaitFrames(2);
            f.Check(f.Inventory.TryAddAt(f.Coin, 1, new Vector2Int(0, 0), out string a1) &&
                    f.Inventory.TryAddAt(draught, 2, new Vector2Int(2, 0), out string a2),
                "a coin and a draught stack are placed for the restore scenario");
            ContainerSnapshot snapshot = f.Inventory.ToContainerSnapshot();
            ulong oldDraughtId = StackIdOfDefinition(f, draught);
            f.Inventory.Hotbar.TryAssign(3, oldDraughtId, out _);
            f.Check(f.Inventory.Hotbar.StackIdAt(3) == oldDraughtId, "the draught is explicitly bound to slot 4 before the restore");
            f.Check(f.Inventory.Catalog != null, "the bag has an item catalog, so a snapshot can be restored");
            if (f.Inventory.Catalog == null) yield break;

            bool restored = f.Inventory.TryRestore(snapshot, out string restoreReason);
            yield return WaitFrames(2);
            ulong newDraughtId = StackIdOfDefinition(f, draught);
            f.Check(restored, "the bag snapshot restores: " + restoreReason);
            f.Check(newDraughtId != 0 && newDraughtId != oldDraughtId,
                "the restored draught stack has a fresh StackId");
            // D3c (old -> new): the restore used to retire every binding and bind the first usable stacks by default (key 1 = the draught).
            // The belt now survives a restore by ITEM IDENTITY: key 4 was bound to the draught, so it is bound to the restored draught
            // stack (a fresh StackId), the old StackId is retired, and no default fills key 1. A belt that held nothing still gets the defaults.
            f.Check(f.Inventory.Hotbar.StackIdAt(3) == newDraughtId && f.Inventory.Hotbar.SlotOf(oldDraughtId) < 0 &&
                    f.Inventory.Hotbar.StackIdAt(0) == 0 && BoundCount(f, newDraughtId) == 1,
                $"restore keeps the belt by item identity: key 4 follows the draught to its restored stack, nothing is auto-bound to key 1 ({BoundIds(f)})");
        }

        private static IEnumerator HotbarKeyUsesBoundStackIdentity(Fixture f)
        {
            Debug.Log("[Emberdelve][Smoke:inventoryui] ---- hotbar key uses the bound StackId");
            ItemData draught = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_BloodDraught.asset");
            if (draught == null) { f.Check(false, "the draught is loadable"); yield break; }

            ResetBag(f);
            yield return WaitFrames(2);
            Vector2Int coinCell = new Vector2Int(0, 0);
            Vector2Int draughtCell = new Vector2Int(2, 0);
            f.Check(f.Inventory.TryAddAt(f.Coin, 1, coinCell, out _) &&
                    f.Inventory.TryAddAt(draught, 2, draughtCell, out _),
                "a coin (earlier in the list) and a two-unit draught stack are placed");
            ulong draughtId = StackIdAtCell(f, draughtCell);
            int indexBefore = f.Inventory.IndexOfStack(draughtId);
            f.Check(f.Inventory.Hotbar.TryAssign(0, draughtId, out string assignReason),
                "the draught stack is bound to key 1: " + assignReason);

            f.Rig.Health.SetHealth(Mathf.Max(1f, f.Rig.MaxHealth - 30f));
            f.Check(f.Inventory.RequestHoldStack(draughtId, out _), "the bound stack is taken in hand before use");
            float healthBefore = f.Rig.CurrentHealth;
            ulong revisionBefore = f.Inventory.Revision;
            f.InventoryEvents = 0;
            bool used = CompleteAuthoredDrink(f, f.Inventory.Hotbar.TryUse(0, out _));
            float expectedHealth = Mathf.Min(f.Rig.MaxHealth, healthBefore + draught.healAmount);
            int afterIndex = f.Inventory.IndexOfStack(draughtId);
            f.Check(used && afterIndex >= 0 && f.Inventory.Slots[afterIndex].Count == 1 &&
                    Mathf.Abs(f.Rig.CurrentHealth - expectedHealth) < 0.01f,
                $"key 1 consumed exactly one unit of the bound stack and healed from the definition ({f.Rig.CurrentHealth:F1}/{expectedHealth:F1} HP)");
            f.Check(f.Inventory.Revision == revisionBefore + 1 && f.InventoryEvents == 1,
                $"the hotbar use commits one revision and one inventory event ({f.Inventory.Revision - revisionBefore}, {f.InventoryEvents})");
            f.Check(f.Inventory.Hotbar.StackIdAt(0) == draughtId,
                "a partial use keeps the binding on the same StackId");

            // Compaction: an earlier list entry leaves; the key still names the same identity.
            int coinIndex = f.Inventory.IndexAtCell(coinCell);
            f.Check(f.Inventory.TryRemoveAt(coinIndex, 1) == 1 && f.Inventory.IndexOfStack(draughtId) == indexBefore - 1,
                "removing the earlier coin compacts the list so the bound stack shifts down one index");
            f.Check(f.Inventory.Hotbar.StackIdAt(0) == draughtId,
                "after compaction the key still names the same StackId");

            f.Rig.Health.SetHealth(Mathf.Max(1f, f.Rig.MaxHealth - 30f));
            revisionBefore = f.Inventory.Revision;
            f.InventoryEvents = 0;
            used = CompleteAuthoredDrink(f, f.Inventory.Hotbar.TryUse(0, out _));
            f.Check(used && f.Inventory.IndexOfStack(draughtId) < 0 && f.Inventory.Revision == revisionBefore + 1 &&
                    f.InventoryEvents == 1,
                "after compaction the key consumes that same stack's last unit (one revision, one event) and retires it");
            f.Check(f.Inventory.Hotbar.StackIdAt(0) == 0, "the fully consumed stack leaves the slot empty");
        }

        // UI identity/count fixtures advance the real channel clock instead of changing shared item assets.
        private static bool CompleteAuthoredDrink(Fixture f, bool started)
        {
            if (!started) return false;
            ConsumableChannel channel = f.Rig.GetComponent<ConsumableChannel>();
            if (channel == null || !channel.IsChanneling) return true;
            var previousSpeed = channel.SpeedRatioOverride;
            try
            {
                channel.SpeedRatioOverride = () => 0f;
                channel.Tick(channel.ChannelDuration + 0.01f);
            }
            finally { channel.SpeedRatioOverride = previousSpeed; }
            return true;
        }

        private static IEnumerator HotbarRetiredBindingDoesNothing(Fixture f)
        {
            Debug.Log("[Emberdelve][Smoke:inventoryui] ---- retired hotbar binding never retargets");
            ItemData draught = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_BloodDraught.asset");
            if (draught == null) { f.Check(false, "the draught is loadable"); yield break; }

            ResetBag(f);
            yield return WaitFrames(2);
            Vector2Int boundCell = new Vector2Int(2, 0);
            Vector2Int lookalikeCell = new Vector2Int(4, 0);
            f.Check(f.Inventory.TryAddAt(draught, 1, boundCell, out _) &&
                    f.Inventory.TryAddAt(draught, 2, lookalikeCell, out _),
                "a bound one-unit draught stack and a same-definition lookalike stack are placed");
            ulong boundId = StackIdAtCell(f, boundCell);
            ulong lookalikeId = StackIdAtCell(f, lookalikeCell);
            f.Check(boundId != 0 && lookalikeId != 0 && boundId != lookalikeId &&
                    f.Inventory.Hotbar.TryAssign(0, boundId, out _),
                "the first stack is bound to key 1; the lookalike has its own StackId and no binding");

            f.Rig.Health.SetHealth(Mathf.Max(1f, f.Rig.MaxHealth - 30f));
            f.Check(f.Inventory.RequestHoldStack(boundId, out _), "the bound stack is held before use");
            f.Check(CompleteAuthoredDrink(f, f.Inventory.Hotbar.TryUse(0, out _)) && f.Inventory.IndexOfStack(boundId) < 0,
                "key 1 consumes the bound stack's only unit and retires it");
            int lookalikeIndex = f.Inventory.IndexOfStack(lookalikeId);
            f.Check(lookalikeIndex == 0, "the lookalike now sits at the list index the bound stack used to hold");

            f.Rig.Health.SetHealth(Mathf.Max(1f, f.Rig.MaxHealth - 30f));
            float healthBefore = f.Rig.CurrentHealth;
            ulong revisionBefore = f.Inventory.Revision;
            int unitsBefore = f.Inventory.TotalUnits;
            f.InventoryEvents = 0;
            bool used = CompleteAuthoredDrink(f, f.Inventory.Hotbar.TryUse(0, out _));
            lookalikeIndex = f.Inventory.IndexOfStack(lookalikeId);
            f.Check(!used && f.Inventory.Hotbar.StackIdAt(0) == 0,
                "key 1 on a retired binding does nothing");
            f.Check(lookalikeIndex == 0 && f.Inventory.Slots[lookalikeIndex].Count == 2 &&
                    f.Inventory.TotalUnits == unitsBefore && f.Inventory.Revision == revisionBefore &&
                    f.InventoryEvents == 0 && Mathf.Abs(f.Rig.CurrentHealth - healthBefore) < 0.01f,
                "the lookalike at the old index is untouched: same count, no revision, no event, no healing");
            f.Check(f.Inventory.Hotbar.SlotOf(lookalikeId) < 0,
                "the lookalike never inherited the retired binding");

            // The index-based adapter is kept for existing callers: it still addresses the list position.
            f.Check(!f.Inventory.TryUseSlot(0), "the legacy adapter also rejects an unheld item");
            f.Check(f.Inventory.RequestHoldStack(lookalikeId, out _), "the remaining stack is taken in hand");
            f.Check(CompleteAuthoredDrink(f, f.Inventory.TryUseSlot(0)) && f.Inventory.Slots[f.Inventory.IndexOfStack(lookalikeId)].Count == 1,
                "the legacy index-based TryUseSlot adapter still works for existing callers");
        }

        private static IEnumerator HotbarAssignFromOpenScreen(Fixture f)
        {
            Debug.Log("[Emberdelve][Smoke:inventoryui] ---- assign hotbar slots from the open satchel");
            ItemData draught = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_BloodDraught.asset");
            ItemData arrows = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_ArrowBundle.asset");
            if (draught == null || arrows == null) { f.Check(false, "the draught and arrow bundle are loadable"); yield break; }

            ResetBag(f);
            yield return WaitFrames(2);
            OpenScreen(f);
            yield return WaitFrames(2);

            Vector2Int coinCell = new Vector2Int(0, 0);
            Vector2Int draughtCell = new Vector2Int(2, 0);
            Vector2Int arrowCell = new Vector2Int(4, 0);
            Vector2Int bladeCell = new Vector2Int(6, 0);
            f.Check(f.Inventory.TryAddAt(f.Coin, 1, coinCell, out _) &&
                    f.Inventory.TryAddAt(draught, 2, draughtCell, out _) &&
                    f.Inventory.TryAddAt(arrows, 2, arrowCell, out _) &&
                    f.Inventory.TryAddAt(f.Blade, 1, bladeCell, out _),
                "a coin, draughts, arrows and a blade are placed in the open satchel");
            yield return WaitFrames(2);
            ulong coinId = StackIdAtCell(f, coinCell);
            ulong draughtId = StackIdAtCell(f, draughtCell);
            ulong arrowId = StackIdAtCell(f, arrowCell);
            ulong bladeId = StackIdAtCell(f, bladeCell);
            f.Check(coinId != 0 && draughtId != 0 && arrowId != 0 && bladeId != 0 && BoundIds(f) == "0,0,0,0,0,0",
                "all four stacks are live and the hotbar starts empty");

            ulong revisionBefore = f.Inventory.Revision;
            f.HotbarEvents = 0;

            // Selection path: select the draught by click, then press 2 with the pointer over no stack.
            yield return ClickCell(f, draughtCell);
            f.Check(f.Screen.SelectedStackId == draughtId, "the draught stack is selected by StackId");
            bool bound = f.Screen.TryAssignHotbarSlot(1, OffGrid);
            f.Check(bound && f.Inventory.Hotbar.StackIdAt(1) == draughtId && f.HotbarEvents == 1,
                $"key 2 with a selected stack binds that StackId to slot 2 and raises one hotbar event ({BoundIds(f)}, {f.HotbarEvents})");

            // Hover path: the stack under the pointer wins over the selection.
            RectTransform arrowRect = FindChildRect(f.UiRoot, "Cell_" + arrowCell.x + "_" + arrowCell.y);
            f.Check(arrowRect != null, "the arrow cell rectangle is rendered");
            if (arrowRect == null) yield break;
            bound = f.Screen.TryAssignHotbarSlot(2, ScreenPoint(f, arrowRect));
            f.Check(bound && f.Inventory.Hotbar.StackIdAt(2) == arrowId && f.Inventory.Hotbar.StackIdAt(1) == draughtId &&
                    f.Screen.SelectedStackId == draughtId,
                $"key 3 over the arrow stack binds the hovered stack to slot 3 and leaves the selection alone ({BoundIds(f)})");

            // Reassignment moves the binding: no duplicate.
            f.HotbarEvents = 0;
            bound = f.Screen.TryAssignHotbarSlot(3, OffGrid);
            f.Check(bound && f.Inventory.Hotbar.StackIdAt(3) == draughtId && f.Inventory.Hotbar.StackIdAt(1) == 0 &&
                    BoundCount(f, draughtId) == 1 && f.HotbarEvents == 1,
                $"binding the selected draught to slot 4 moves it off slot 2 ({BoundIds(f)})");
            bound = f.Screen.TryAssignHotbarSlot(3, ScreenPoint(f, arrowRect));
            f.Check(bound && f.Inventory.Hotbar.StackIdAt(3) == arrowId && f.Inventory.Hotbar.StackIdAt(2) == 0 &&
                    BoundCount(f, arrowId) == 1 && BoundCount(f, draughtId) == 0,
                $"binding the arrows over an occupied slot replaces it and still holds no duplicate ({BoundIds(f)})");
            f.HotbarEvents = 0;
            bound = f.Screen.TryAssignHotbarSlot(3, ScreenPoint(f, arrowRect));
            f.Check(bound && f.Inventory.Hotbar.StackIdAt(3) == arrowId && f.HotbarEvents == 0,
                "re-binding the same stack to the same slot is a no-op without an event");

            // Unusable items are refused with a toast, and nothing changes.
            string bindingsBefore = BoundIds(f);
            f.Toasts.Clear();
            f.HotbarEvents = 0;
            RectTransform coinRect = FindChildRect(f.UiRoot, "Cell_" + coinCell.x + "_" + coinCell.y);
            RectTransform bladeRect = FindChildRect(f.UiRoot, "Cell_" + bladeCell.x + "_" + bladeCell.y);
            f.Check(coinRect != null && bladeRect != null, "the coin and blade cell rectangles are rendered");
            if (coinRect == null || bladeRect == null) yield break;
            bool coinBound = f.Screen.TryAssignHotbarSlot(0, ScreenPoint(f, coinRect));
            bool bladeBound = f.Screen.TryAssignHotbarSlot(1, ScreenPoint(f, bladeRect));
            f.Check(!coinBound && !bladeBound && BoundIds(f) == bindingsBefore && f.HotbarEvents == 0,
                $"a coin and a blade cannot be put on the hotbar ({BoundIds(f)})");
            f.Check(f.Toasts.Count == 2 && f.Toasts[0].Contains(f.Coin.displayName) && f.Toasts[1].Contains(f.Blade.displayName),
                "each refusal uses the screen's toast feedback and names the item");

            // Nothing under the pointer and nothing selected: refused.
            f.Screen.Close();
            OpenScreen(f);
            yield return WaitFrames(2);
            f.Toasts.Clear();
            bool nothing = f.Screen.TryAssignHotbarSlot(0, OffGrid);
            f.Check(!nothing && f.Toasts.Count == 1 && BoundIds(f) == bindingsBefore,
                "with no stack under the pointer and no selection, the key is refused with a toast");

            f.Check(f.Inventory.Revision == revisionBefore,
                "assigning hotbar slots never touches the shared inventory revision");
            RectTransform currentArrowRect = FindChildRect(f.UiRoot, "Cell_" + arrowCell.x + "_" + arrowCell.y);
            f.Check(currentArrowRect != null, "the arrow cell is still rendered after the satchel rebuild");
            Vector2 arrowPoint = ScreenPoint(f, currentArrowRect);

            // A closed screen assigns nothing.
            f.Screen.Close();
            f.Check(!f.Screen.TryAssignHotbarSlot(0, arrowPoint) && BoundIds(f) == bindingsBefore,
                "a closed satchel ignores hotbar assignment");
        }

        private static void LogHotbarInputDiagnostic(Fixture f, Keyboard testKeyboard, string stage)
        {
            Keyboard currentKeyboard = Keyboard.current;
            Emberdelve.InputLayer.EmberdelveInput input = f.Rig != null ? f.Rig.Input : null;
            InputActionMap playerMap = ReadInputMap(input, "_playerMap");
            InputActionMap systemMap = ReadInputMap(input, "_systemMap");
            InputAction playerInventory = playerMap != null ? playerMap.FindAction("Inventory") : null;
            InputAction systemInventory = systemMap != null ? systemMap.FindAction("Inventory") : null;

            HudController[] huds = UnityEngine.Object.FindObjectsByType<HudController>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            InventoryScreenController[] screens = UnityEngine.Object.FindObjectsByType<InventoryScreenController>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            FieldInfo hudScreenField = typeof(HudController).GetField("_inventoryScreen", BindingFlags.Instance | BindingFlags.NonPublic);
            string hudState = string.Empty;
            for (int i = 0; i < huds.Length; i++)
            {
                HudController hud = huds[i];
                if (i > 0) hudState += " | ";
                InventoryScreenController boundScreen = hudScreenField != null ? hudScreenField.GetValue(hud) as InventoryScreenController : null;
                hudState += $"{hud.name}#{hud.GetInstanceID()} enabled={hud.enabled} active={hud.gameObject.activeInHierarchy} boundScreen={(boundScreen != null ? boundScreen.name + "#" + boundScreen.GetInstanceID() : "null")}";
            }

            string screenState = string.Empty;
            for (int i = 0; i < screens.Length; i++)
            {
                InventoryScreenController screen = screens[i];
                if (i > 0) screenState += " | ";
                screenState += $"{screen.name}#{screen.GetInstanceID()} enabled={screen.enabled} active={screen.gameObject.activeInHierarchy} open={screen.IsOpen}";
            }

            string actionState = input == null ? "input=null" :
                $"inputEnabled={input.enabled},inputActive={input.gameObject.activeInHierarchy},InventoryPressed={input.InventoryPressed}; player={DescribeInventoryAction(playerMap, playerInventory)}; system={DescribeInventoryAction(systemMap, systemInventory)}";
            string keyboardState = testKeyboard == null ? "testKeyboard=null" :
                $"testKeyboardId={testKeyboard.deviceId},enabled={testKeyboard.enabled},I.isPressed={testKeyboard.iKey.isPressed},I.wasPressed={testKeyboard.iKey.wasPressedThisFrame}";
            string currentState = currentKeyboard == null ? "currentKeyboard=null" :
                $"currentKeyboardId={currentKeyboard.deviceId},enabled={currentKeyboard.enabled},I.isPressed={currentKeyboard.iKey.isPressed},I.wasPressed={currentKeyboard.iKey.wasPressedThisFrame},sameAsTest={ReferenceEquals(currentKeyboard, testKeyboard)}";

            Debug.Log($"[Emberdelve][HotbarInputDiag] stage={stage} frame={Time.frameCount} batch={Application.isBatchMode} focused={Application.isFocused} inputUpdateMode={InputSystem.settings.updateMode} backgroundBehavior={InputSystem.settings.backgroundBehavior} editorRouting={InputSystem.settings.editorInputBehaviorInPlayMode} deviceCount={InputSystem.devices.Count}; {keyboardState}; {currentState}; {actionState}; directorState={f.Director.State}/{f.Director.LocalPlayerState} inventoryOpen={f.Director.InventoryOpen}; hudCount={huds.Length} [{hudState}]; screenCount={screens.Length} [{screenState}]");
        }

        private static InputActionMap ReadInputMap(Emberdelve.InputLayer.EmberdelveInput input, string fieldName)
        {
            if (input == null) return null;
            FieldInfo field = input.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            return field != null ? field.GetValue(input) as InputActionMap : null;
        }

        private static string DescribeInventoryAction(InputActionMap map, InputAction action)
        {
            if (map == null) return "map=null";
            if (action == null) return $"{map.name}.Inventory=null (mapEnabled={map.enabled})";
            return $"{map.name}.Inventory enabled={action.enabled},wasPressed={action.WasPressedThisFrame()} (mapEnabled={map.enabled})";
        }
        private static IEnumerator HotbarRealKeyboardPath(Fixture f)
        {
            Debug.Log("[Emberdelve][Smoke:inventoryui] ---- real keyboard events drive hotbar assignment and use");

            InputSettings originalSettings = InputSystem.settings;
            InputSettings temporarySettings = UnityEngine.Object.Instantiate(originalSettings);
            Keyboard keyboard = null;
            Mouse mouse = null;
            bool healthUpdatesEnabled = f.Rig.Health.enabled;

            try
            {
                // Update only performs passive regeneration; keep potion Heal/Changed active and isolate its timing.
                f.Rig.Health.enabled = false;
                // The Input System disables new keyboard/mouse devices after focus loss. Use only a transient clone
                // for this fixture, route input to the game, and restore the original settings object in finally.
                temporarySettings.hideFlags = HideFlags.HideAndDontSave;
                temporarySettings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                temporarySettings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
                InputSystem.settings = temporarySettings;

                keyboard = InputSystem.AddDevice<Keyboard>();
                mouse = InputSystem.AddDevice<Mouse>();
                if (keyboard != null) InputSystem.EnableDevice(keyboard);
                if (mouse != null) InputSystem.EnableDevice(mouse);

                f.Check(keyboard != null && keyboard.enabled && mouse != null && mouse.enabled,
                    "the real input fixture created and enabled its owned Keyboard and Mouse devices");
                if (keyboard == null || mouse == null) yield break;
                ItemData draught = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_BloodDraught.asset");
                if (draught == null) { f.Check(false, "the Blood Draught is loadable for the keyboard path"); yield break; }

                ResetBag(f);
                f.Director.SetInventoryOpen(false);
                yield return WaitFrames(2);

                Vector2Int draughtCell = new Vector2Int(2, 0);
                f.Check(f.Inventory.TryAddAt(draught, 2, draughtCell, out string addReason),
                    "a two-unit Blood Draught stack is placed for real keyboard input: " + addReason);
                ulong draughtId = StackIdAtCell(f, draughtCell);
                f.Check(draughtId != 0 && f.Inventory.Hotbar.StackIdAt(0) == 0 &&
                        f.Inventory.Hotbar.StackIdAt(1) == 0 && f.Inventory.Hotbar.StackIdAt(2) == 0 &&
                        f.Inventory.Hotbar.StackIdAt(3) == 0 && f.Inventory.Hotbar.StackIdAt(4) == 0 && f.Inventory.Hotbar.StackIdAt(5) == 0,
                    "the stack has a StackId and all six wheel slots start empty");
                if (draughtId == 0) yield break;

                f.InventoryEvents = 0;
                InputSystem.QueueStateEvent(mouse, new MouseState { position = new Vector2(-1000f, -1000f) });
                LogHotbarInputDiagnostic(f, keyboard, "before I queue");
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.I));
                LogHotbarInputDiagnostic(f, keyboard, "after I queue / before frame");
                yield return WaitFrames(1);
                LogHotbarInputDiagnostic(f, keyboard, "after first frame");
                yield return WaitFrames(1);
                LogHotbarInputDiagnostic(f, keyboard, "after second frame");
                f.Check(f.Screen.IsOpen && f.Director.InventoryOpen,
                    "a real I key event opens the satchel through the HUD and disables gameplay input");
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                yield return WaitFrames(1);

                yield return ClickCell(f, draughtCell);
                f.Check(f.Screen.SelectedStackId == draughtId, "the draught is selected by its rendered cell");

                ulong revisionBeforeAssignment = f.Inventory.Revision;
                int unitsBeforeAssignment = f.Inventory.TotalUnits;
                f.InventoryEvents = 0;
                f.HotbarEvents = 0;
                var controller = f.Rig.GetComponent<HotbarController>();
                for (int i = 0; i < HotbarBindings.SlotCount; i++)
                {
                    InputSystem.QueueStateEvent(mouse, new MouseState { position = new Vector2(-1000f, -1000f) });
                    InputSystem.QueueStateEvent(keyboard, new KeyboardState(i < 3 ? Key.Digit3 : Key.Digit4));
                    yield return WaitFrames(1);
                    f.Check(controller.WheelOpen, "holding the quick key opens assignment wheel");
                    float angle = (i % 3) * 120f * Mathf.Deg2Rad;
                    Vector2 pointer = new Vector2(Screen.width * .5f, Screen.height * .5f) + new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)) * 130f;
                    InputSystem.QueueStateEvent(mouse, new MouseState { position = pointer });
                    yield return WaitFrames(1);
                    InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                    yield return WaitFrames(1);
                    f.Check(f.Inventory.Hotbar.StackIdAt(i) == draughtId && f.Inventory.Hotbar.SlotOf(draughtId) == i,
                        $"wheel release assigns sector {i + 1} to the selected stack ({BoundIds(f)})");
                    f.Check(f.Inventory.TotalUnits == unitsBeforeAssignment && f.Inventory.Revision == revisionBeforeAssignment && f.InventoryEvents == 0,
                        "assignment does not consume an item or mutate inventory authority");
                    f.Check(f.HotbarEvents == i + 1, "one binding-change event per assignment");
                }

                InputSystem.QueueStateEvent(mouse, new MouseState { position = new Vector2(-1000f, -1000f) });
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.I));
                yield return WaitFrames(1);
                f.Check(!f.Screen.IsOpen && !f.Director.InventoryOpen,
                    "a real I key event closes the satchel and re-enables gameplay input");
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                yield return WaitFrames(1);

                f.Rig.Health.SetHealth(Mathf.Max(1f, f.Rig.MaxHealth - 30f));
                float healthBeforeUse = f.Rig.CurrentHealth;
                float expectedHealth = Mathf.Min(f.Rig.MaxHealth, healthBeforeUse + draught.healAmount);
                int unitsBeforeUse = f.Inventory.TotalUnits;
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Digit4));
                yield return WaitFrames(1);
                float selectedAngle = 240f * Mathf.Deg2Rad;
                InputSystem.QueueStateEvent(mouse, new MouseState { position = new Vector2(Screen.width * .5f, Screen.height * .5f) + new Vector2(Mathf.Sin(selectedAngle), Mathf.Cos(selectedAngle)) * 130f });
                yield return WaitFrames(1);
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                yield return WaitFrames(2);
                f.Check(f.Inventory.HeldStackId == draughtId && f.Inventory.TotalUnits == unitsBeforeUse,
                    "wheel release holds the selected stack without consuming it");
                ulong revisionBeforeUse = f.Inventory.Revision;
                f.InventoryEvents = 0;
                InputSystem.QueueStateEvent(mouse, new MouseState { buttons = 1 });
                yield return WaitFrames(1);
                InputSystem.QueueStateEvent(mouse, new MouseState());
                ConsumableChannel drink = f.Rig.GetComponent<ConsumableChannel>();
                f.Check(drink != null && drink.IsChanneling && drink.ChannelStackId == draughtId &&
                        f.Inventory.TotalUnits == unitsBeforeUse && f.Inventory.Revision == revisionBeforeUse &&
                        Mathf.Abs(f.Rig.CurrentHealth - healthBeforeUse) < 0.01f,
                    $"a real LMB event begins drinking without early consumption or healing (channel={drink != null && drink.IsChanneling}, held={drink?.ChannelStackId}/{draughtId}, units={f.Inventory.TotalUnits}/{unitsBeforeUse}, revision={f.Inventory.Revision}/{revisionBeforeUse}, health={f.Rig.CurrentHealth:F4}/{healthBeforeUse:F4})");
                CompleteAuthoredDrink(f, true);

                f.Check(f.Inventory.TotalUnits == unitsBeforeUse - 1 &&
                        Mathf.Abs(f.Rig.CurrentHealth - expectedHealth) < 0.01f,
                    $"a real LMB event uses one held draught through authority and heals to {expectedHealth:F1} HP");
                f.Check(f.Inventory.Revision == revisionBeforeUse + 1 && f.InventoryEvents == 1,
                    $"the real-key authority use commits exactly one inventory revision and change event ({f.Inventory.Revision - revisionBeforeUse}, {f.InventoryEvents})");
                f.Check(f.Inventory.Hotbar.StackIdAt(5) == draughtId,
                    "the partial real-key use keeps the slot bound to the same StackId");
                yield return WaitFrames(1);
            }
            finally
            {
                f.Rig.Health.enabled = healthUpdatesEnabled;
                if (keyboard != null)
                {
                    if (keyboard.enabled) InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                    InputSystem.RemoveDevice(keyboard);
                }
                if (mouse != null)
                {
                    if (mouse.enabled) InputSystem.QueueStateEvent(mouse, new MouseState());
                    InputSystem.RemoveDevice(mouse);
                }

                // The temporary clone has no asset path and is nonserializable; restore the project settings object.
                if (!ReferenceEquals(InputSystem.settings, originalSettings)) InputSystem.settings = originalSettings;
                if (temporarySettings != null) UnityEngine.Object.Destroy(temporarySettings);
            }
        }
        private static IEnumerator HotbarHudMatchesBindings(Fixture f)
        {
            Debug.Log("[Emberdelve][Smoke:inventoryui] ---- HUD hotbar chips follow the bindings");
            ItemData draught = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_BloodDraught.asset");
            ItemData arrows = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_ArrowBundle.asset");
            if (draught == null || arrows == null) { f.Check(false, "the draught and arrow bundle are loadable"); yield break; }

            ResetBag(f);
            yield return WaitFrames(2);
            Vector2Int coinCell = new Vector2Int(0, 0);
            Vector2Int draughtCell = new Vector2Int(2, 0);
            Vector2Int arrowCell = new Vector2Int(4, 0);
            f.Check(f.Inventory.TryAddAt(f.Coin, 1, coinCell, out _) &&
                    f.Inventory.TryAddAt(draught, 2, draughtCell, out _) &&
                    f.Inventory.TryAddAt(arrows, 3, arrowCell, out _),
                "a coin, two draughts and three arrow bundles are placed for the HUD check");
            ulong draughtId = StackIdAtCell(f, draughtCell);
            ulong arrowId = StackIdAtCell(f, arrowCell);
            f.Check(f.Inventory.Hotbar.TryAssign(0, arrowId, out _) && f.Inventory.Hotbar.TryAssign(1, draughtId, out _),
                "arrows are bound to key 1 and draughts to key 2");
            yield return WaitFrames(3);

            RectTransform row = FindHudHotbarRow();
            f.Check(row != null, "the HUD built its hotbar row");
            if (row == null) yield break;

            f.Check(ChipText(row, 1) == "Arrow Bundle x3" && ChipText(row, 2) == "Blood Draught x2" &&
                    ChipText(row, 3) == null && ChipText(row, 4) == null,
                $"chips show the bound stacks' names and counts and empty slots as empty ('{ChipText(row, 1)}', '{ChipText(row, 2)}', '{ChipText(row, 3)}', '{ChipText(row, 4)}')");

            // Compaction: the coin (index 0) leaves; chips are unchanged.
            f.Check(f.Inventory.TryRemoveAt(f.Inventory.IndexAtCell(coinCell), 1) == 1, "the earlier coin is removed");
            yield return WaitFrames(3);
            row = FindHudHotbarRow();
            f.Check(row != null && ChipText(row, 1) == "Arrow Bundle x3" && ChipText(row, 2) == "Blood Draught x2",
                "after list compaction the chips still show the same bound stacks");

            // A partial use updates the count and keeps the chip.
            f.Rig.Health.SetHealth(Mathf.Max(1f, f.Rig.MaxHealth - 30f));
            f.Check(f.Inventory.RequestHoldStack(draughtId, out _), "the HUD-bound draught is taken in hand");
            f.Check(CompleteAuthoredDrink(f, f.Inventory.Hotbar.TryUse(1, out _)), "key 2 drinks one draught");
            yield return WaitFrames(3);
            row = FindHudHotbarRow();
            f.Check(row != null && ChipText(row, 2) == "Blood Draught", $"the chip count follows a partial use ('{ChipText(row, 2)}')");

            // An explicit assignment refreshes the strip through the hotbar event.
            f.Check(f.Inventory.Hotbar.TryAssign(3, draughtId, out _), "the draught moves to key 4");
            yield return WaitFrames(3);
            row = FindHudHotbarRow();
            f.Check(row != null && ChipText(row, 2) == null && ChipText(row, 4) == "Blood Draught" &&
                    ChipText(row, 1) == "Arrow Bundle x3",
                $"moving a binding refreshes the strip ('{ChipText(row, 2)}', '{ChipText(row, 4)}')");

            // A retired binding empties its chip.
            f.Check(f.Inventory.TryRemoveAt(f.Inventory.IndexOfStack(arrowId), 3) == 3, "the bound arrows leave the bag");
            yield return WaitFrames(3);
            row = FindHudHotbarRow();
            f.Check(row != null && ChipText(row, 1) == null && ChipText(row, 4) == "Blood Draught",
                "a retired binding shows as an empty chip");

            // Clear retires everything.
            f.Inventory.Clear();
            yield return WaitFrames(3);
            row = FindHudHotbarRow();
            f.Check(row != null && ChipText(row, 1) == null && ChipText(row, 2) == null &&
                    ChipText(row, 3) == null && ChipText(row, 4) == null && BoundIds(f) == "0,0,0,0,0,0",
                "Clear empties every chip and binding");
        }

        private static RectTransform FindHudHotbarRow()
        {
            Canvas[] canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
            for (int i = 0; i < canvases.Length; i++)
            {
                RectTransform row = FindChildRect(canvases[i].transform, "InventoryRow");
                if (row != null) return row;
            }
            return null;
        }

        /// <summary>The label of HUD chip 1..4, or null when the chip is drawn as empty (it has no Label child).</summary>
        private static string ChipText(RectTransform row, int slotNumber)
        {
            RectTransform chip = FindChildRect(row, "Slot" + slotNumber);
            if (chip == null) return null;
            RectTransform label = FindChildRect(chip, "Label");
            if (label == null) return null;
            Text text = label.GetComponent<Text>();
            return text != null ? text.text : null;
        }

        private static IEnumerator CloakSlotAndTooltips(Fixture f)
        {
            Debug.Log("[Emberdelve][Smoke:inventoryui] ---- cloak slot and tooltips");
            ResetBag(f);
            yield return WaitFrames(2);
            OpenScreen(f);
            yield return WaitFrames(2);

            ItemData ashen = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_AshenCloak.asset");
            ItemData mantle = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_EmberthreadMantle.asset");
            f.Check(ashen != null && mantle != null && ashen.equipSlot == EquipmentSlot.Back, "the two cloak definitions are loadable Back items");
            if (ashen == null || mantle == null) yield break;

            // ---- six worn slots rendered, all contained in the satchel panel, which is itself on screen
            RectTransform panel = FindChildRect(f.UiRoot, "SatchelPanel");
            f.Check(panel != null, "the satchel panel rectangle exists");
            if (panel == null) yield break;

            Vector3[] panelCorners = new Vector3[4];
            panel.GetWorldCorners(panelCorners);
            // The panel has a fixed pixel size and offset from the window centre (no canvas scaler), so containment is
            // asserted for the 1280x720 and 1920x1080 windows from its extents relative to the centre. The batch
            // window itself is 640x480, smaller than the panel was ever designed for.
            float leftExtent = panelCorners[0].x - Screen.width * 0.5f, rightExtent = panelCorners[2].x - Screen.width * 0.5f;
            float bottomExtent = panelCorners[0].y - Screen.height * 0.5f, topExtent = panelCorners[2].y - Screen.height * 0.5f;
            foreach (Vector2 window in new[] { new Vector2(1280f, 720f), new Vector2(1920f, 1080f) })
            {
                f.Check(window.x * 0.5f + leftExtent >= 0f && window.x * 0.5f + rightExtent <= window.x &&
                        window.y * 0.5f + bottomExtent >= 0f && window.y * 0.5f + topExtent <= window.y,
                    $"the satchel panel (x {leftExtent:F0}..{rightExtent:F0}, y {bottomExtent:F0}..{topExtent:F0} from centre) lies inside a {window.x:F0}x{window.y:F0} window");
            }

            int slotRows = 0;
            Vector3[] corners = new Vector3[4];
            foreach (EquipmentSlot slot in new[] { EquipmentSlot.Weapon, EquipmentSlot.OffHand, EquipmentSlot.Head,
                         EquipmentSlot.Chest, EquipmentSlot.Legs, EquipmentSlot.Back, EquipmentSlot.Hands, EquipmentSlot.Feet,
                         EquipmentSlot.Necklace, EquipmentSlot.RingLeft, EquipmentSlot.RingRight,
                         EquipmentSlot.AltWeapon, EquipmentSlot.AltOffHand })
            {
                RectTransform rect = FindChildRect(f.UiRoot, "Equip_" + slot);
                RectTransform label = FindChildRect(f.UiRoot, "EquipLabel_" + slot);
                f.Check(rect != null && label != null, $"worn slot {slot} rendered with its label");
                if (rect == null || label == null) continue;
                slotRows++;

                rect.GetWorldCorners(corners);
                f.Check(Contains(panelCorners, corners, 0.5f), $"the {slot} slot lies inside the satchel panel");
                label.GetWorldCorners(corners);
                f.Check(Contains(panelCorners, corners, 0.5f), $"the {slot} label rectangle lies inside the satchel panel");
            }
            f.Check(slotRows == 13, "the equipment column has thirteen rows (eleven before D3c appended AltWeapon and AltOffHand, six before D3b)");

            // ---- two rolled drops of one base cloak
            List<AffixDefinition> affixes = new List<AffixDefinition>();
            foreach (string guid in AssetDatabase.FindAssets("t:AffixDefinition", new[] { EmberdelveAssetLibrary.AffixFolder }))
                affixes.Add(AssetDatabase.LoadAssetAtPath<AffixDefinition>(AssetDatabase.GUIDToAssetPath(guid)));
            f.Check(ItemInstanceAuthority.TryCreate(new[] { ashen, mantle }, affixes, null, out ItemInstanceAuthority auth, out string reason),
                "an authority over the real cloaks and affixes: " + reason);
            if (auth == null) yield break;

            bool made = ItemInstance.TryCreateNew(ashen, 1, AffixRarity.Rare,
                new[] { new AffixRecord("affix_armor", AffixStatKey.Armor, 4), new AffixRecord("affix_wisdom", AffixStatKey.Wisdom, 2) },
                auth, out ItemInstance rare, out reason);
            made &= ItemInstance.TryCreateNew(ashen, 1, AffixRarity.Uncommon,
                new[] { new AffixRecord("affix_max_health", AffixStatKey.MaxHealth, 9) }, auth, out ItemInstance uncommon, out reason);
            f.Check(made, "two ashen cloak instances with different rolls minted: " + reason);
            if (!made) yield break;

            f.Check(f.Inventory.TryAddInstanceAt(rare, new Vector2Int(0, 0), out reason) &&
                    f.Inventory.TryAddInstanceAt(uncommon, new Vector2Int(2, 0), out reason), "both cloaks placed in the bag: " + reason);
            yield return WaitFrames(2);

            RectTransform cellRare = FindChildRect(f.UiRoot, "Cell_0_0");
            RectTransform cellUncommon = FindChildRect(f.UiRoot, "Cell_2_0");
            f.Check(cellRare != null && cellUncommon != null, "cells under both cloaks rendered");
            if (cellRare == null || cellUncommon == null) yield break;

            ItemTooltipModel hoverRare = f.Screen.ShowTooltipAt(ScreenPoint(f, cellRare));
            string textRare = hoverRare != null ? hoverRare.ToPlainText() : string.Empty;
            RectTransform tip = FindChildRect(f.UiRoot, "TooltipPanel");
            f.Check(hoverRare != null && tip != null && tip.gameObject.activeInHierarchy, "hovering a cloak shows the tooltip panel");
            if (tip != null)
            {
                tip.GetWorldCorners(corners);
                f.Check(Contains(panelCorners, corners, 0.5f), "the tooltip panel lies inside the satchel panel");
            }

            ItemTooltipModel hoverUncommon = f.Screen.ShowTooltipAt(ScreenPoint(f, cellUncommon));
            string textUncommon = hoverUncommon != null ? hoverUncommon.ToPlainText() : string.Empty;
            f.Check(hoverRare != null && hoverUncommon != null && textRare != textUncommon,
                "hovering two drops of the same base cloak shows two different tooltips");
            f.Check(hoverRare != null && hoverRare.RarityName == "Rare" && hoverRare.RolledLines.Count == 2 &&
                    textRare.Contains("+4 armor") && textRare.Contains("+2 wisdom"), "the rare drop lists its two rolled affixes");
            f.Check(hoverUncommon != null && hoverUncommon.RarityName == "Uncommon" && hoverUncommon.RolledLines.Count == 1 &&
                    textUncommon.Contains("+9 max_health") && !textUncommon.Contains("+4 armor"), "the uncommon drop lists only its own affix");
            f.Check(hoverRare != null && hoverUncommon != null && hoverRare.FixedLines.Count == hoverUncommon.FixedLines.Count &&
                    textRare.Contains("Armour +2") && textUncommon.Contains("Armour +2"), "both show the same fixed armour 2");

            // ---- wrong slot refused, the right slot wears exactly the selected instance
            int before = f.Inventory.TotalUnits;
            yield return ClickCell(f, new Vector2Int(0, 0));
            f.Check(f.Screen.SelectedItem == ashen, "the rare cloak is selected");
            yield return ClickEquipSlot(f, EquipmentSlot.Legs);
            f.Check(f.Equipment.FilledCount == 0 && f.Screen.SelectedItem == ashen, "a cloak does not fit the Legs slot and the selection survives");
            yield return ClickEquipSlot(f, EquipmentSlot.Back);
            f.Check(ReferenceEquals(f.Equipment.EquippedInstance(EquipmentSlot.Back), rare) && f.Equipment.FilledCount == 1,
                "clicking the Back slot wore the selected instance, not the first ashen cloak in the bag");
            f.Check(!f.Inventory.HoldsInstanceUid(rare.Uid) && f.Inventory.HoldsInstanceUid(uncommon.Uid) && f.Inventory.TotalUnits == before - 1,
                "exactly one cloak left the bag");
            f.Check(f.Rig.GetArmorRating() == 6f, $"the worn cloak's fixed 2 plus rolled 4 armour is the rig's rating ({f.Rig.GetArmorRating()})");

            RectTransform backRect = FindChildRect(f.UiRoot, "Equip_Back");
            ItemTooltipModel wornTip = backRect != null ? f.Screen.ShowTooltipAt(ScreenPoint(f, backRect)) : null;
            f.Check(wornTip != null && wornTip.ToPlainText() == textRare, "hovering the worn slot shows the same instance tooltip as in the bag");

            // ---- right click swaps the other drop in, then back; both uids conserved, no stats accumulate
            yield return ClickEquipSlot(f, EquipmentSlot.Back, true);
            f.Check(f.Screen.SelectedItem == ashen, "the right click on a worn slot took the cloak off and selected the returned stack");
            if (f.Screen.HasSelection) yield return ClickCell(f, new Vector2Int(9, 3), true);
            f.Check(f.Equipment.FilledCount == 0 && f.Inventory.HoldsInstanceUid(rare.Uid) && f.Inventory.HoldsInstanceUid(uncommon.Uid) && f.Rig.GetArmorRating() == 0f,
                "taking the cloak off returned the same instance and the armour to 0");

            yield return ClickCell(f, OriginOfInstance(f, uncommon), true);
            f.Check(ReferenceEquals(f.Equipment.EquippedInstance(EquipmentSlot.Back), uncommon) && f.Rig.GetArmorRating() == 2f, "right click wore the uncommon cloak (armour 2)");
            yield return ClickCell(f, OriginOfInstance(f, rare), true);
            f.Check(ReferenceEquals(f.Equipment.EquippedInstance(EquipmentSlot.Back), rare) && f.Inventory.HoldsInstanceUid(uncommon.Uid) &&
                    f.Inventory.TotalUnits == before - 1 && f.Equipment.FilledCount == 1 && f.Rig.GetArmorRating() == 6f,
                "the swap put the rare cloak on, the uncommon one back in the bag (bag units + worn = 2), nothing lost and armour 6 (not 8)");

            ResetBag(f);
            f.Check(f.Rig.GetArmorRating() == 0f, "clearing the kit removes the cloak's armour");

            // ---- character pane: the last row lies inside the pane background (review M1)
            Button characterTab = null;
            foreach (Button b in f.UiRoot.GetComponentsInChildren<Button>(true)) if (b.name == "TabCharacter") characterTab = b;
            f.Check(characterTab != null, "the CHARACTER tab button exists");
            if (characterTab != null)
            {
                characterTab.onClick.Invoke();
                Canvas.ForceUpdateCanvases();
                yield return WaitFrames(2);
                RectTransform pane = FindChildRect(f.UiRoot, "StatsPane");
                RectTransform lastRow = FindChildRect(f.UiRoot, "StatValue" + (CharacterStatsView.LineCount - CharacterStatsView.SectionCount - 1));
                f.Check(pane != null && lastRow != null, "the pane background and its last row rendered");
                if (pane != null && lastRow != null)
                {
                    Vector3[] paneCorners = new Vector3[4];
                    Vector3[] rowCorners = new Vector3[4];
                    pane.GetWorldCorners(paneCorners);
                    lastRow.GetWorldCorners(rowCorners);
                    f.Check(rowCorners[0].y >= paneCorners[0].y - 0.5f && rowCorners[2].y <= paneCorners[2].y + 0.5f,
                        $"the last pane row (y {rowCorners[0].y:F0}..{rowCorners[2].y:F0}) lies inside the pane background (y {paneCorners[0].y:F0}..{paneCorners[2].y:F0})");
                    panel.GetWorldCorners(panelCorners);
                    f.Check(paneCorners[0].y >= panelCorners[0].y - 0.5f, "the pane background stays inside the satchel panel");
                }
                foreach (Button b in f.UiRoot.GetComponentsInChildren<Button>(true)) if (b.name == "TabBag") b.onClick.Invoke();
                yield return WaitFrames(2);
            }
        }

        // ------------------------------------------------------------------ P4c

        /// <summary>
        /// P4c player-facing gates: rarity colours, affix display names, equip labels, right-click equip for
        /// every kind of gear (with and without a leftover selection), prompt text and hint-line fit.
        /// </summary>
        private static IEnumerator PlayerFacingFixes(Fixture f)
        {
            Debug.Log("[Emberdelve][Smoke:inventoryui] ---- P4c player-facing fixes");

            // ---- rarity colours: distinct from the body text colour and readable on the panel
            Color body = ItemTooltip.NeutralColor;
            Color panelBg = new Color(0.055f, 0.05f, 0.045f, 1f);
            Color tipBg = new Color(0.045f, 0.04f, 0.036f, 1f);
            foreach (AffixRarity rarity in AffixRarities.ByRank) // D3a: all seven tiers, Poor..Unique
            {
                Color c = ItemTooltip.RarityColor(rarity);
                float distance = Mathf.Sqrt((c.r - body.r) * (c.r - body.r) + (c.g - body.g) * (c.g - body.g) + (c.b - body.b) * (c.b - body.b));
                f.Check(distance >= 0.2f, $"{rarity} colour is clearly distinct from the body text colour (RGB distance {distance:F2})");
                f.Check(ItemTooltip.ContrastRatio(c, panelBg) >= 4.5f && ItemTooltip.ContrastRatio(c, tipBg) >= 4.5f,
                    $"{rarity} colour is readable on the panel and tooltip backgrounds (contrast {ItemTooltip.ContrastRatio(c, tipBg):F1}:1)");
            }
            Color rare = ItemTooltip.RareColor;
            f.Check(rare.b > rare.r + 0.3f && rare.b > rare.g + 0.2f, "Rare is a readable blue");

            // ---- affix display names: every key, no raw underscores, signs handled
            int values = System.Enum.GetValues(typeof(AffixStatKey)).Length;
            f.Check(values == AffixStatKeys.KeyCount, $"the enum has {values} keys, the vocabulary says {AffixStatKeys.KeyCount}");
            for (int i = 0; i < AffixStatKeys.KeyCount; i++)
            {
                AffixStatKey key = (AffixStatKey)i;
                string name = AffixStatKeys.DisplayName(key);
                string plus = AffixStatKeys.DisplayLine(key, 3f);
                string minus = AffixStatKeys.DisplayLine(key, -3f);
                f.Check(name.Length > 0 && !name.Contains("_") && char.IsUpper(name[0]), $"{key} has a display name ({name})");
                f.Check(!plus.Contains("_") && !minus.Contains("_") && !plus.Contains("+-") && !minus.Contains("+-") &&
                        plus.StartsWith("+") && minus.StartsWith("-"), $"{key} lines read cleanly: {plus} / {minus}");
            }
            f.Check(AffixStatKeys.DisplayLine(AffixStatKey.Armor, 4f) == "+4 Armour", "armor shows as Armour (British, like the fixed lines)");
            f.Check(AffixStatKeys.DisplayLine(AffixStatKey.Strength, -2f) == "-2 Strength", "a negative attribute prints -2 Strength");
            f.Check(AffixStatKeys.DisplayLine(AffixStatKey.MoveSpeed, 0.09f) == "+0.09 Move speed", "move_speed shows as Move speed with its decimal");

            // ---- right-click equips every kind of gear, even with a leftover selection
            ItemData cloak = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_AshenCloak.asset");
            ItemData gloves = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_GraspwrapGloves.asset");
            ItemData necklace = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_EmberbeadNecklace.asset");
            ItemData ring = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_CinderBandRing.asset");
            f.Check(gloves != null && necklace != null && ring != null, "the D3b slot items load");
            ItemData[] gear = { cloak, f.Cuirass, f.Boots, f.Blade, gloves, necklace, ring };
            foreach (ItemData item in gear)
            {
                foreach (bool withSelection in new[] { false, true })
                {
                    ResetBag(f);
                    yield return WaitFrames(2);
                    OpenScreen(f);
                    yield return WaitFrames(2);

                    Vector2Int origin = new Vector2Int(0, 0);
                    Vector2Int coinCell = new Vector2Int(8, 3);
                    bool added = f.Inventory.TryAddAt(item, 1, origin, out string why);
                    if (withSelection) added &= f.Inventory.TryAddAt(f.Coin, 1, coinCell, out why);
                    f.Check(added, $"{item.displayName} placed for the right-click test ({why})");
                    if (!added) continue;
                    yield return WaitFrames(2);

                    if (withSelection)
                    {
                        yield return ClickCell(f, coinCell);
                        f.Check(f.Screen.SelectedItem == f.Coin, "a coin is selected before the right click");
                    }

                    yield return ClickCell(f, origin, true);
                    f.Check(f.Equipment.Equipped(item.equipSlot) == item && f.Inventory.IndexOfItem(item) < 0,
                        $"right click wore {item.displayName} into {item.equipSlot}{(withSelection ? " (with a selection pending)" : "")}");
                    f.Check(f.Screen.SelectedItem == null, "the right click leaves no selection behind");
                    RectTransform gearNameRect = FindChildRect(f.UiRoot, "EquipLabel_" + item.equipSlot);
                    Text gearNameText = gearNameRect != null ? gearNameRect.GetComponent<Text>() : null;
                    f.Check(gearNameText != null && gearNameText.preferredHeight <= gearNameRect.rect.height + 0.5f,
                        $"the worn {item.displayName} name fits its compact equipment row without clipping");
                }
            }

            // ---- the worn Back slot keeps its slot label and shows the item separately
            ResetBag(f);
            yield return WaitFrames(2);
            OpenScreen(f);
            f.Check(f.Inventory.TryAddAt(cloak, 1, new Vector2Int(0, 0), out string cloakWhy), "cloak placed: " + cloakWhy);
            yield return WaitFrames(2);
            yield return ClickCell(f, new Vector2Int(0, 0), true);
            RectTransform backLabel = FindChildRect(f.UiRoot, "EquipLabel_Back");
            Text backText = backLabel != null ? backLabel.GetComponent<Text>() : null;
            f.Check(backText != null && backText.text.Contains("Back") && backText.text.Contains(cloak.displayName),
                "the Back label names both the slot and the worn item: " + (backText != null ? backText.text.Replace("\n", " / ") : "missing"));
            RectTransform emptyLabel = FindChildRect(f.UiRoot, "EquipLabel_Head");
            Text emptyText = emptyLabel != null ? emptyLabel.GetComponent<Text>() : null;
            f.Check(emptyText != null && emptyText.text == "Head", "an empty slot shows only its slot name");

            // ---- the worn-slot tooltip sits beside the column, not over the other slot rows
            RectTransform backSlot = FindChildRect(f.UiRoot, "Equip_Back");
            RectTransform tipRect = FindChildRect(f.UiRoot, "TooltipPanel");
            if (backSlot != null && tipRect != null)
            {
                f.Screen.ShowTooltipAt(ScreenPoint(f, backSlot));
                Canvas.ForceUpdateCanvases();
                Vector3[] tipCorners = new Vector3[4];
                Vector3[] slotCorners = new Vector3[4];
                tipRect.GetWorldCorners(tipCorners);
                backSlot.GetWorldCorners(slotCorners);
                f.Check(tipRect.gameObject.activeInHierarchy && tipCorners[2].x <= slotCorners[0].x + 0.5f,
                    $"the worn-slot tooltip ends left of the equipment column (tip right {tipCorners[2].x:F0}, slot left {slotCorners[0].x:F0})");
                Image tipImage = tipRect.GetComponent<Image>();
                f.Check(tipImage != null && tipImage.color.a >= 0.999f, "the tooltip background is fully opaque");
            }

            // ---- the hint line fits its rectangle, idle and with a selection
            ResetBag(f);
            yield return WaitFrames(2);
            OpenScreen(f);
            f.Check(f.Inventory.TryAddAt(f.Cuirass, 1, new Vector2Int(0, 0), out string cWhy), "cuirass placed: " + cWhy);
            yield return WaitFrames(2);
            RectTransform carry = FindChildRect(f.UiRoot, "Carry");
            Text carryText = carry != null ? carry.GetComponent<Text>() : null;
            RectTransform panelRect = FindChildRect(f.UiRoot, "SatchelPanel");
            f.Check(carryText != null && panelRect != null, "the hint label and the panel exist");
            if (carryText != null && panelRect != null)
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    if (pass == 1) { yield return ClickCell(f, new Vector2Int(0, 0)); yield return WaitFrames(2); }
                    Vector3[] pc = new Vector3[4];
                    Vector3[] lc = new Vector3[4];
                    panelRect.GetWorldCorners(pc);
                    carry.GetWorldCorners(lc);
                    f.Check(carryText.preferredHeight <= carry.rect.height + 0.5f,
                        $"the {(pass == 0 ? "idle" : "selection")} hint ({carryText.preferredHeight:F0}px of text) fits its {carry.rect.height:F0}px rectangle");
                    f.Check(Contains(pc, lc, 0.5f), "the hint rectangle lies inside the satchel panel");
                }
            }

            // ---- M1: right click on a selected consumable is "let go", never "drink it"
            ItemData draught = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_BloodDraught.asset");
            foreach (ItemData consumable in new[] { draught, f.Coin })
            {
                if (consumable == null) { f.Check(false, "consumable asset loadable"); continue; }
                ResetBag(f);
                yield return WaitFrames(2);
                OpenScreen(f);
                yield return WaitFrames(2);
                f.Check(f.Inventory.TryAddAt(consumable, 3, new Vector2Int(0, 0), out string cwhy), "consumable stack placed: " + cwhy);
                yield return WaitFrames(2);
                int unitsBefore = f.Inventory.TotalUnits;
                float healthBefore = f.Rig.CurrentHealth;
                yield return ClickCell(f, new Vector2Int(0, 0));
                f.Check(f.Screen.SelectedItem == consumable, consumable.displayName + " selected");
                yield return ClickCell(f, new Vector2Int(0, 0), true);
                f.Check(f.Inventory.TotalUnits == unitsBefore && f.Rig.CurrentHealth <= healthBefore + 0.01f,
                    $"right click on the selected {consumable.displayName} did not use it (units {unitsBefore} -> {f.Inventory.TotalUnits})");
                f.Check(f.Screen.SelectedItem == null, "the right click let go of the selection");
                RectTransform carryRect = FindChildRect(f.UiRoot, "Carry");
                Text carryLabel = carryRect != null ? carryRect.GetComponent<Text>() : null;
                f.Check(carryLabel != null && !carryLabel.text.StartsWith("Selected"), "the UI was rebuilt: no stale Selected hint");
                f.Check(FindChildRect(f.UiRoot, "Selected_" + consumable.displayName) == null, "the selection highlight frame is gone");
            }

            // ---- player-visible tooltip text for a rolled affix (the screen's own text, not the canonical keys)
            {
                ResetBag(f);
                yield return WaitFrames(2);
                OpenScreen(f);
                List<AffixDefinition> defs = new List<AffixDefinition>();
                foreach (string guid in AssetDatabase.FindAssets("t:AffixDefinition", new[] { EmberdelveAssetLibrary.AffixFolder }))
                    defs.Add(AssetDatabase.LoadAssetAtPath<AffixDefinition>(AssetDatabase.GUIDToAssetPath(guid)));
                bool authOk = ItemInstanceAuthority.TryCreate(new[] { cloak }, defs, null, out ItemInstanceAuthority authority, out string aReason);
                f.Check(authOk, "authority for the display-text check: " + aReason);
                if (authOk)
                {
                    bool minted = ItemInstance.TryCreateNew(cloak, 1, AffixRarity.Rare,
                        new[] { new AffixRecord("affix_armor", AffixStatKey.Armor, 4), new AffixRecord("affix_max_health", AffixStatKey.MaxHealth, 9) },
                        authority, out ItemInstance rolled, out aReason);
                    minted &= f.Inventory.TryAddInstanceAt(rolled, new Vector2Int(0, 0), out aReason);
                    f.Check(minted, "rare cloak with two rolls placed: " + aReason);
                    yield return WaitFrames(2);
                    RectTransform cell00 = FindChildRect(f.UiRoot, "Cell_0_0");
                    if (minted && cell00 != null)
                    {
                        f.Screen.ShowTooltipAt(ScreenPoint(f, cell00));
                        RectTransform bodyRect = FindChildRect(f.UiRoot, "TooltipBody");
                        string shown = bodyRect != null ? bodyRect.GetComponent<Text>().text : string.Empty;
                        f.Check(shown.Contains("+4 Armour") && shown.Contains("+9 Max health") && !shown.Contains("max_health") && !shown.Contains("+4 armor"),
                            "the tooltip on screen reads +4 Armour / +9 Max health: " + shown.Replace("\n", " | "));
                    }
                }
            }

            // ---- prompt text never says hold twice
            GameObject probe = new GameObject("P4c_PromptProbe");
            PromptProbe stub = probe.AddComponent<PromptProbe>();
            stub.Text = "Hold to open chest"; stub.Hold = true;
            string holdPrompt = Emberdelve.Interaction.Interactor.PromptFor(f.Rig, stub);
            f.Check(holdPrompt == "Hold to open chest", "a prompt that already says Hold is not suffixed: " + holdPrompt);
            stub.Text = "Open the gate"; stub.Hold = true;
            f.Check(Emberdelve.Interaction.Interactor.PromptFor(f.Rig, stub) == "Open the gate  (hold)", "a plain label on a hold target gets one (hold)");
            stub.Text = "Open chest"; stub.Hold = false;
            f.Check(Emberdelve.Interaction.Interactor.PromptFor(f.Rig, stub) == "Open chest", "an instant target gets no suffix");
            UnityEngine.Object.Destroy(probe);

            ResetBag(f);
            yield return WaitFrames(2);
        }

        private sealed class PromptProbe : MonoBehaviour, IInteractable
        {
            public string Text;
            public bool Hold;
            public string PromptText => Text;
            public bool RequiresHold => Hold;
            public float HoldDuration => 1f;
            public bool CanInteract(IInteractionActor actor, out string blockedReason) { blockedReason = null; return true; }
            public void OnHoldComplete(IInteractionActor actor) { }
            public void OnInteractInstant(IInteractionActor actor) { }
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
            f.Equipment.ClearToBare();
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
