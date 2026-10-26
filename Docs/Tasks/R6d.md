# R6d — inventory screen model extraction

Status (26 Oct 2026): DONE. Implemented by Sonnet subagent, reviewed by Claude orchestrator. Root fix after first compile: added `using Emberdelve.Data;` and `using Emberdelve.Stats;` to InventoryScreenModel.cs (ItemData, EquipmentSlot). Fresh gates (Logs/r6c2_r6d_20261026_121547): InventoryUI 420/0, InventoryAuthority 103/0, InventoryFlow 415/0, ResultLoop 80/0, InventoryStackId 56/0.

# R6d report: InventoryScreenController selection/command extraction

## Files changed (under current/Assets/Emberdelve)
- Runtime/UI/InventoryScreenModel.cs (+ .meta, guid 7c3e91a05b2d4f68a1d0e4b9c58f2a36): new `internal sealed class InventoryScreenModel`, plain C#, namespace Emberdelve.UI.
- Runtime/UI/InventoryScreenController.cs: now delegates to the model.
- Editor/EmberdelveInventoryUiChecks.cs: one new scenario `RebindingDropsSelectionFromTheOldBag`, registered after StableSelectionTracksIdentity.

## What moved into the model
- Binding: director / rig / inventory / equipment (`Bind` = old Configure body, `EnsureLiveRig` = old live-rig resolution, `RebindInventory`).
- Selection state: StackId, item, origin, amount (`SetSelection`, `TrySelectIndex`, `SelectReturnedStack`, `TryResolve`, `ValidateSelection`, `Clear`).
- Command build/submit (`Submit` = old SubmitInventoryCommand: TryCreateLocalCommand + Submit, same reason strings).
- R6c2 hotbar assignment (`StackIdAtCell`, `TryAssignHotbar`, same toast texts), `CommandsAllowed`, `ToastOwner`.
The controller keeps all widgets, hit-testing (TryCellAt/TryEquipSlotAt need rects), ghost, tooltip, Rebuild and tab logic. Its `_director/_rig/_inventory/_equipment` are now read-only expression-bodied views of the model so drawing code is unchanged.

## Facade
No public/internal/serialized member of InventoryScreenController was added, removed or re-signed: IsOpen, SelectedStackId, SelectedItem, SelectedOrigin, SelectedAmount, HasSelection, CarriedItem, CarriedAmount, CurrentTooltip, Configure, Open, Close(), Close(bool), Toggle, HandlePointer, TryAssignHotbarSlot, HandleClick, ShowTooltipAt. Private members used by the controller keep their names (SubmitInventoryCommand, TryResolveSelection, SelectReturnedStack, ValidateSelection, ClearSelection, ...). There are no [SerializeField] fields in this class. HudController (Configure, Toggle, IsOpen, HandlePointer; reflects only HudController._inventoryScreen) is untouched. No hierarchy changes (Build untouched).

## Rebinding
- Configure: model.Bind then ClearSelection (as before).
- EnsureLiveRig: selection dropped only when a different non-null bag replaces an existing one (as before); a half-bound rig being completed keeps it. Stats view reconfigure and ghost hide still happen in the controller.
- Gap closed: the model records which bag a selection was made in; `TryResolve` fails if that is not the currently bound bag. StackIds are minted per bag (start at 1), so a stale id could previously match a different bag's stack if the bag reference changed through a path that did not clear.

## New check
`RebindingDropsSelectionFromTheOldBag`: select a coin in bag A, rebind (via reflection on `_model.RebindInventory`) to a throwaway Inventory component, assert selection gone, same-bag rebind is a no-op, rebind back and assert bag A intact and no revived selection.

## Risks / uncertainties
- Not compiled. Check: `GameServices.TryGet<RunDirector>(out _director)` with a field in the model, `internal` class used from a public class's private field (fine), expression-bodied underscore properties.
- New check uses reflection (the file header says it avoids internals) and `AddComponent<Inventory>()` on a bare GameObject; if Inventory needs setup that throws, only this check fails.
- Model `TryAssignHotbar` reads `_inventory.Hotbar` directly; controller still guards IsOpen/_inventory null first.

## To compile / run
Compile, then gate groups: InventoryUI, InventoryAuthority, InventoryFlow, ResultLoop, InventoryStackId.
