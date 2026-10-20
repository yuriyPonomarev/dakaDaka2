# U1B — owned, atomic equipment transfers + an honest real-damage measurement

Status: **implemented (partial)** — code and report saved with file tools only. No Unity run, no compile
check, no test executed by the implementer. Nothing here is a passed test.

## Changed files

- `Assets/Emberdelve/Runtime/Items/Inventory.cs`
  - new `TryEquipTransfer(int bagIndex, ItemData expectedIncoming, ItemData outgoing, out string reason)`
  - new `TryUnequipTransfer(ItemData item, out string reason)`
  - `FindFreeOrigin(size)` now delegates to a new private `FindFreeOriginIgnoring(size, ignoreIndex)`
    (same scan, same row-major order; `ignoreIndex = -1` reproduces the old behaviour exactly)
- `Assets/Emberdelve/Runtime/Items/Equipment.cs`
  - `TryEquipFromInventory` → validates, then calls the new private `TryEquipSelected(bagIndex, item, out reason)`
  - `TryEquipItem` → now requires the definition to be present in the bag
  - `TryUnequipSlot` → refuses null-inventory / dead / empty-slot / bad-index before mutating, and uses
    `TryUnequipTransfer` instead of public `TryAdd`
  - `TryUnequip` now returns `TryUnequipSlot(...)` directly (the transfer itself refreshes stats)
- `Assets/Emberdelve/Editor/EmberdelveRuntimeSmokeTest.cs` — only `ItemFlow` wiring, `EquipmentFlow`,
  `GuardWithGearFlow`, plus new `EquipmentTransferFlow`, `SubmitMeasuredGuardHit`, `CheckMeasurement`,
  `GearHitMeasurement`. No combat/AI/extraction/death/lobby/archer/skill flow was touched.
- `Docs/Tasks/U1B.md` (this report)

## Production behavior implemented

### 1. The bag half of an equipment change is one planned transfer (`Inventory`)

`TryEquipTransfer` validates the slot and the expected item, **plans** the destination hole for the item
leaving the equipment slot *with the incoming stack treated as absent*, and only then commits:

- a stack of one unit vacates its own cells, so the item coming out of a slot may land in exactly that hole
  (this is the case the old code refused);
- a stack that survives the removal keeps every cell it already had, so it opens no hole and the plan is
  made against the grid as it is;
- if no placement exists the method returns false having touched nothing — no stack moved, no totals moved,
  no event raised;
- on success it commits both moves, updates `TotalUnits`/`CarriedValue` for exactly the unit that moved,
  raises **one** `InventoryChanged` after the committed grid exists, and raises **no** `ItemCollected` and
  touches neither `LifetimeCollected` nor `RecoveredValue`;
- `expectedIncoming` is checked against the stack actually at `bagIndex`, so the caller can never be handed
  a different item than the one it validated, and there is no post-commit failure path.

`TryUnequipTransfer` is the same rule for gear going back: it plans a hole of the item's own shape, refuses
with `bag has no WxH hole for X` when there is none, and on success adds one unit and its value with **no**
collection credit and no collection event. Moving already-owned gear is not loot.

### 2. Equipment refuses before it mutates (`Equipment`)

- `TryEquipItem(item)` now rejects `null`, non-equippable definitions, a missing `Inventory`, and — the
  confirmed defect — **a definition the bag does not hold** (`"X is not in the bag to equip"`). No item is
  ever created out of a `ScriptableObject` description.
- `TryEquipFromInventory(index)` consumes **the selected slot**: it reads `Slots[index].Item` and passes that
  index to the transfer. `IndexOfItem`'s first-duplicate behaviour can no longer consume the wrong copy.
- `TryEquipSelected` decides everything that can be decided without touching state (slot index, granted
  weapon, dead rig, missing rig/inventory) and then lets the bag transfer be the last step and the last
  chance to refuse. The old "unequip the previous item into a still-full bag first, then take the incoming
  one out, then put the previous one back on if that failed" sequence is gone.
- `TryUnequipSlot` refuses `null` slot item, no rig, dead rig and **no inventory** (previously a null bag
  silently deleted the worn item), then delegates to `TryUnequipTransfer`. It refreshes stats itself, so
  every equip and unequip ends with a resolved stat source.
- Stat accumulation is unchanged in mechanism (`CharacterStatStack.SetSource` replaces the equipment source)
  and is now asserted: two `RefreshStats()` calls and a full equip/unequip cycle return the pools to the
  class numbers.

## Harness changes

### `GuardWithGearFlow` (rewritten)

- **Facing vs travel are separated.** `towardSource = enemy - rig` is what the guard arc is measured against
  and what the rig is rotated to face; `travel = rig.CombatCenter - origin` is the direction the attack
  actually moves in and is what the intent carries. The old fixture compared player forward against the
  travel vector, which is its opposite (the 180° "failure" was the fixture, not the guard).
- **`CaptureDamage` is subscribed around this measurement only** (`_rig.Health.Damaged += CaptureDamage`
  at entry, removed in `finally`), and each submission clears `_capturedValid`/`_capturedDamage` first, so a
  capture is a hit this flow caused.
- **The cuirass is the independent variable**: it is unequipped through the real transfer into the bag for
  the baseline, and re-equipped from the bag for the armored measurements. Slot state, bag membership and
  the resolved `Defense` are asserted before each hit.
- **One geometry for all four hits**: the same frozen attacker re-placed at the same standoff from the same
  anchor, the rig re-seated at the anchor, HP reset *outside* the measured interval, distinct fresh
  sequences from the enemy's own allocator, timestamps spaced by 3 s (claw cooldown 2 s × 0.8 grace).
- Each hit asserts: authority accepted it, `AcceptedCount` grew by exactly one, the claimed sequence is the
  one the authority consumed, a `DamageInfo` arrived on the rig, its `SourceCombatId`/`SourceKind` name that
  enemy, and positive HP left the pool.
- With shipped archer/shield data the **naked reduction is zero**, so `CanBlock == false`, `Blocked == false`
  and full weapon damage are asserted as the legitimate baseline — no shield stat was invented to make the
  baseline "block". Worn front must block, must be strictly smaller than the naked hit, must match
  `naked × (1 − declaredReduction)` within 0.75, and must report the item's own reduction. Worn rear (180°)
  and worn side (90°) must **not** block and must take the full weapon number.
- `finally` restores the subscription, the guard, the frozen agent, `MeleeCombat.enabled`, the pool and the
  gear that was worn on entry, including on an early failure.

### `EquipmentTransferFlow` (new, called from `ItemFlow` before `ChestFlow`)

Covers, in order: null / bad bag index / non-equippable / `EquipmentSlot.None` refusals with unchanged bag,
slots, pools, defense, hands and counters; **unowned definition** refusal; two same-definition cuirasses at
distinct cells where **slot 1** is the one consumed and slot 0 stays put; a **valid replacement in a bag full
to the last cell** (34 single coins placed cell by cell with `TryAddAt`, so the only 2×3 hole is the one the
incoming cuirass vacates) with conservation of stacks, cells, units, value and counters; a **geometrically
impossible replacement** (a temporary 1×1 chest plate cloned from the cuirass, destroyed in `finally`, no
shipped asset touched) asserted against a full snapshot of every stack's item/count/origin/order plus a
cell-by-cell occupancy comparison, totals, counters, equipment, hands and zero notifications; a **failed
unequip** that keeps the item worn and adds nothing; a successful unequip that adds exactly one unit and its
value with no collection credit and one notification; re-equipping the same definition with `RefreshStats()`
called twice to show no modifier accumulation; and a blade equip/unequip cycle that returns the class weapon
to the main hand with exactly one blade in the bag.

`ItemCollected` and `EquipmentChanged` are counted for the whole flow; legitimate loot adds (cuirasses, the
coin fill, the probe plate, the blade) move the baseline so the transfer assertions measure transfers only.

### `EquipmentFlow`

- `MainHandWeapon == blade.GrantsWeapon` and `OffhandWeapon == shieldItem.GrantsWeapon` replaced with
  `ReferenceEquals(..., item.grantedWeapon)` plus an explicit "the item declares a weapon to grant" check.
  `GrantsWeapon` is a `bool`, and `UnityEngine.Object`'s implicit bool conversion made the old comparison
  pass for any non-null weapon. Capability/combat-component assertions are unchanged.
- After the existing full-bag unequip refusal: the last cuirass is now taken **off** into a bag full of
  cuirasses (the hole it was occupying is a legal destination), an unequip of an already-empty slot is
  refused with an explanation, and a second blade equip/unequip cycle is asserted to conserve the bag and
  farm no extraction credit.

## Checks actually run by the implementer

None executed — file tools only, per the contract. Source-level verification performed instead:

- `Inventory` API and grid semantics read from the file itself (`CanPlace(origin, size, ignoreIndex,
  alsoIgnore)`, `Recompute`, `TryAddAt` merging only when the target cell already holds a matching stack,
  `TryRemoveAt` not touching lifetime counters, `Clear` not touching lifetime counters).
- `ItemData`: equippable items are forced `maxStack = 1` in `OnValidate` and `CanStack == false`, so a bag
  stack of an equippable definition is always one unit — the `opensHole` rule in `TryEquipTransfer` matches
  that, and the `Count > 1` branch is kept correct for anything else.
- Data facts used by the fixtures: bag 10×4 (40 cells); cuirass 2×3, value 40, `MaxHealth +30`,
  `BlockReduction +0.08`; Ember Coin 1×1, `maxStack 8`, value 12; blade 1×3 → Ember Rapier (capabilities
  Block|Parry, role Weapon, not two-handed); shield item 2×2 → Emberbulwark Shield (role Shield,
  capabilities sanitized to Block, no reduction/arc override); archer `blockReduction 0`, `blockArc 90`,
  no off-hand shield; Hollow Claw `baseDamage 15`, `cooldown 2`, melee (no falloff).
- Hand simulation of the fixtures: 5 cuirasses pack 10×4 as `floor(10/2) × floor(4/3)`; a worn cuirass plus
  5 bag cuirasses leaves a 2-row band with no 2×2 or 2×3 hole (so the full-bag unequip still refuses) but a
  2×2 hole at (0,0) (so the last cuirass can still come off); the coin-filled bag has exactly one 2×3 hole,
  the one the incoming cuirass vacates; the 1×1 probe plate frees exactly one cell, which cannot host a 2×3
  block, so that replacement must be refused.
- Expected measurement numbers: naked front 15.0 (unblocked), worn front 13.8 (blocked, 0.08), worn rear and
  side 15.0 (unblocked). The previous run's 13.8 → 13.8 is consistent with the cuirass having been worn for
  both hits.

## Pending manager verification

1. Compile all three files (no compile was run here).
2. Run the items gate: expect the five U1A equipment/defense failures to be replaced by the new transfer and
   measurement assertions, and the check count to rise by roughly 150 `Check` calls (exact total is the
   manager's to report).
3. Run the combat gate unchanged (201 checks expected) to prove the harness edits did not disturb M1–M4.
4. Mutation candidates the manager may want: make `TryEquipTransfer` credit `LifetimeCollected`/raise
   `ItemCollected`; make `TryUnequipSlot` fall back to `TryAdd`; make `TryEquipFromInventory` resolve the
   item with `IndexOfItem`; make `TryEquipItem` accept an absent definition; commit the transfer before
   planning the hole; re-subscribe `CaptureDamage` globally; re-wear the cuirass before the baseline hit.
   Each should produce a visible `FAIL` and a nonzero exit.
5. The dead-rig and null-`Inventory` refusals are implemented but **not exercised** by the harness: killing
   the player inside the items flow would turn the run into a death test, and the real rig's `Equipment`
   always has an `Inventory`. Cover them with a dedicated fixture if independent proof is required.

## Remaining work / blockers

- **UI integration gap (out of U1B's named files):** `InventoryScreenController.HandleLeftClick` equips the
  cursor item with `_equipment.TryEquipItem(_carried, ...)`, but `PickUp` has already removed that item from
  the bag. Under the required ownership rule that now refuses with "X is not in the bag to equip". No gear
  is lost (the item stays on the cursor and right-click returns it), and right-clicking a bag cell still
  equips via `TryEquipFromInventory(index)`, but the cursor→slot path needs a small follow-up: either
  re-place the carried item into the bag before equipping, or a dedicated cursor-transfer API. Flagged
  rather than fixed because it is outside the assigned files.
- `Equipment.Clear()` still discards worn gear without transferring it to the bag. It is used only by
  harness/reset paths today; making it conservation-preserving is a separate decision and was left alone.
- This is local gameplay acceptance only. It is not proof of networking, not a height/stamina defense, and
  not whole-M5 completion. U2A (no-free-healing and its old fixtures), U1C (stash/container acceptance) and
  the UI slice remain open.
- No Unity/editor commands were run; nothing here is verified beyond source reading.
