# U1B-R1 — the three demonstrated blockers repaired

Status: **implemented (partial)** — code and this report saved with file tools only. No Unity run, no
compile, no test executed by the implementer. Nothing here is a passed test. U1A and all other U1B
requirements (ownership, conservation, real-damage measurement) are untouched.

## Changed files

- `Assets/Emberdelve/Runtime/Items/Inventory.cs` — `TryEquipTransfer` (~473) and `TryUnequipTransfer` (~558)
- `Assets/Emberdelve/Runtime/Items/Equipment.cs` — `TryEquipSelected` (~197) and `TryUnequipSlot` (~341)
- `Assets/Emberdelve/Editor/EmberdelveRuntimeSmokeTest.cs` — `EquipmentFlow`, `EquipmentTransferFlow`, new
  `CommitSnapshot` + `CaptureCommit` members of `Harness`. No other flow, no UI, no production placement.

## 1. CS0165 (harness line 2103)

The two out-of-range `TryEquipFromInventory` calls were one `&&` expression, so `highIndexReason` was only
assigned when the first refusal did not short-circuit, and the message interpolated it anyway. They are now
two independent statements (`lowRefused`, `highRefused`) with four checks: each index refused, and each
refusal produced a non-empty reason. Neither check was dropped and no assertion was weakened.

## 2. Half-committed state seen by InventoryChanged subscribers

`Inventory.TryEquipTransfer` / `TryUnequipTransfer` are now `internal` and **raise no event at all** — they
commit the grid, totals and counters only. `Equipment` raises the single `GameEvents.RaiseInventoryChanged()`
after `ApplyToSlot`/slot clear, the hand change and `RefreshStats()` have all committed, then
`EquipmentChanged`. Refusals still notify nothing, and no `ItemCollected` / `LifetimeCollected` /
`RecoveredValue` change was introduced. No general transaction framework was added. `Equipment` is the only
caller of either helper (verified by search), so no other path can commit a bag change silently.

New harness proof (`EquipmentTransferFlow`): an `InventoryChanged` subscriber takes a full
`CommitSnapshot` **inside** the callback — bag units/value/stacks/cells/free plus every stack's item, count,
origin and size, all six equipment slots, `FilledCount`, both hands, `MaxHealth` and `BlockReduction`.
Copies are taken because the live collections keep mutating. It is armed only around one equipment call
(`ArmProbe`/`DisarmProbe`) so the loot adds that build the fixtures are not counted as transfers. Asserted
from inside the notification: equip (slot holds the item, bag no longer does, unselected copy untouched,
exact unit/value, pools and block reduction already refreshed); replacement (slot still worn, exactly one
cuirass in the bag — not zero and not two — already sitting in the hole the selected incoming copy vacated,
full-bag conservation, pools unchanged); unequip (slot already empty, one whole 2x3 footprint in the bag,
exact one unit + value + 6 cells, contribution already out of the pools); blade equip/unequip (the granted
weapon, then the class weapon, already in the main hand). Every refusal asserts zero notifications and a
null snapshot. Roughly 50 additional `Check` calls.

## 3. EquipmentFlow's impossible unequip expectation

The false claim ("the cells the worn item was occupying are a legal destination") is gone: worn gear occupies
no bag cells, and that is now asserted (`UsedCells == fill * 6` while a cuirass is worn). The unbounded
`while (inventory.TryAdd(cuirass, 1))` is bounded by a geometry-derived cap (`floor(W/2) * floor(H/3) + 2`)
with termination asserted, and the full-bag refusal plus exact conservation and zero credit are retained.
For the subsequent success the flow now removes **one controlled bag cuirass with `TryRemoveAt`**, re-reads
`FindFreeOrigin` to record the hole that genuinely exists, re-snapshots, and only then requires the unequip
to succeed into that hole: one unit and its value, one new stack, exactly 6 more cells, one whole unstacked
footprint at the planned hole with its far corner marked occupied, `LifetimeCollected`/`RecoveredValue`
unchanged, and one stack per cuirass with nothing duplicated. Production placement was not altered and no
hole was invented. The valid replacement in `EquipmentTransferFlow` (the selected incoming bag item really
vacating the space) is retained unchanged.

## Checks actually run

None. File tools only, per the contract. Source-level verification: `GameEvents` signatures, the only two
callers of the transfer helpers, `Inventory.Slots`/`Equipment.Slots` being live views (hence the copies),
`ItemData` footprint/stack rules, `EquipmentSlot` indices, and the 10x4 / 2x3 packing the fixtures rely on.

## Pending manager verification

1. Compile the three files (the CS0165 fix is unverified until the gate runs).
2. Items gate: expect the notification-ordering assertions to fail if the helpers are moved back to raising
   their own event, and the EquipmentFlow unequip assertions to fail if the hole is not opened.
3. Combat gate unchanged, to show the harness edits did not disturb M1–M4.
4. Mutation candidates: raise `InventoryChanged` from inside `TryUnequipTransfer`; raise it before
   `RefreshStats()`; drop the `TryRemoveAt` hole-opening step; make the fill loop unbounded again.

## Remaining work / blocker

- **Cursor→equipment UI path is still unintegrated** (unchanged from U1B): `InventoryScreenController`
  removes the carried item from the bag before calling the ownership-checked definition API, so that path
  refuses. It remains an explicit blocker to complete local-loop acceptance and belongs to U1C.
- `Equipment.Clear()` still discards worn gear instead of transferring it; harness/reset paths only.
- Local gameplay only: not proof of networking, not a height/stamina defense, not whole-M5 completion.
