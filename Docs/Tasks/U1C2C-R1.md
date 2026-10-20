# U1C2C-R1 - usable four-key raid hotbar (implementation saved, not verified)

Partial/complete-as-assigned: the legacy 40-chip capacity strip in the raid HUD is replaced by exactly four
hotkey chips that mirror `HotbarController` semantics, plus one compact bag summary line. No gameplay,
inventory, selection or ownership code was touched. No scene, prefab, package, metadata, editor or test file
was touched. No U1C2C geometry fix was rolled back.

## Files changed (only the two named files)

* `Assets/Emberdelve/Runtime/UI/HudController.cs` - HUD presentation only (`BuildHotbar`, `RebuildInventory`,
  `HotbarLabel`, `UpdateBagSummary`, one new field `_bagSummaryLabel`, one new constant block).
* `Assets/Emberdelve/Runtime/UI/InventoryScreenController.cs` - `Rebuild()` visibility restored from `private`
  back to `public` (it was public before U1C2C; the change to private was unrelated geometry cleanup). A
  doc-comment was added. **No other change in that file**: layout, hit-testing, selection, ownership, ghost,
  click routing and event subscriptions are byte-for-byte as accepted in U1C2C.

`UiFactory` was not modified: the existing `SetRect` overload with an explicit pivot and the
`DefaultPivot`-forwarding overload were sufficient.

## What the strip now shows (semantics preserved, not invented)

Verified against `Runtime/Items/HotbarController.cs`: `Update` loops `i = 0..3` and calls
`inventory.TryUseSlot(i)`, i.e. keys 1-4 address **stack indices 0..3 of `Inventory.Slots`** - the first four
stacks the bag currently holds, in bag order. `Inventory.Recompute` builds `_views` in `_stacks` order, so the
chip at position `i` reads `inventory.Slots[i]`, the exact index `TryUseSlot` consumes.

* Exactly four chips, always, named `Slot1`..`Slot4` (kept for any check that inspects those names).
* A chip is filled when `i < inventory.Slots.Count` and shows that stack's item name + `xN` count; otherwise
  it shows a dim `empty`. Capacity (`Inventory.Capacity = 10x4 = 40`) no longer drives the row.
* Each chip carries a bold 18px key glyph `1`..`4` in its own left column with a divider, so the hint is
  visible whether or not the slot is filled. The glyph is the key `EmberdelveInput` binds to `UseItem1..4`
  (`<Keyboard>/1..4`), the same actions `HotbarController.SlotPressed(i)` reads.
* Nothing is sorted, filtered, re-assigned, quick-slotted or hidden, and no item is depicted as usable that
  the bag would not accept: a worn-gear stack still appears at its own index and `TryUseSlot` still answers
  "cannot be used". Use behaviour is unchanged.
* Refresh path unchanged: `GameEvents.InventoryChanged` -> `OnInventoryChanged` -> `RebuildInventory`, which
  destroys the row's children and re-reads the bag. A consumed, removed, merged, swapped or shifted stack is
  therefore re-read from the bag rather than remembered, which is what the old code already did.
* Item name is wrapped inside the chip (`HorizontalWrapMode.Wrap` / `VerticalWrapMode.Truncate`, 62x30px
  region, 12px font) and only hard-shortened past 22 characters. The previous 14-character cut is gone; full
  name, footprint and summary remain in the satchel screen.
* Chip visuals: dark background, a 4px accent strip along the chip's top edge tinted with the item's
  `iconColor` (same convention as the skill chips), key glyph, divider, wrapped label. `raycastTarget = false`
  on all hotbar graphics; the satchel's pointer path (`HandlePointer` + `RectTransformUtility`) and the
  gameplay mouse reading are untouched, so pointer selection is unchanged.

## Compact bag summary

New single line `BagSummary` (15px, centred, colour `(0.84,0.79,0.70,0.95)` on the dark HUD backdrop), rebuilt
with the same subscription:

`1-4 use these stacks   bag 7/40 cells   value 120   worn 2   [I] satchel`

Read from `Inventory.UsedCells`, `Inventory.Capacity`, `Inventory.CarriedValue` and
`RunDirector.Player.Equipment.FilledCount`. Estimated at ~400px of LegacyRuntime text advance inside a 520px
rect, so it is centred without overflowing. The full 10x4 grid, per-cell detail, worn descriptions and
modifiers remain only in the I/Tab satchel screen.

## Geometry (computed, not observed)

The HUD canvas is created by `UiFactory.EnsureCanvas` with no `CanvasScaler` and the level scene contains no
other Canvas, so canvas units are window pixels 1:1 - the same basis the accepted U1C2C numbers use. All
neighbouring HUD clusters are corner-anchored with fixed pixel insets, while this strip is bottom-centred, so
the offsets below hold at both target resolutions.

New constants: `HotbarCapacity 4`, `HotbarChipWidth 96`, `HotbarChipHeight 44`, `HotbarChipGap 12`,
`HotbarChipCentre 50`, `BagSummaryCentre 90`, `BagSummaryWidth 520`, `BagSummaryHeight 22`. `InventoryRow` is
now a zero-size bottom-centre group (was offset `(0,40)`); chips are placed with their pivot 50px above the
window's bottom edge, the summary line's pivot 90px above it.

At 1280x720 (image coords, y down from the top edge) the accepted neighbours occupy:

| neighbour | x | y |
|---|---|---|
| health bar / health label / weapon label | 24..364 | 666..686 / 635..657 / 603..629 |
| mana bar | 24..364 | 695..709 |
| mana label | 378..598 | 695..715 |
| kit label | 24..454 | 496..588 |
| ammo label / ammo fill | 932..1252 / 1036..1256 | 632..660 / 667..677 |
| skill chips | 882..1252 | 514..592 |
| prompt label | 260..1020 | 554..586 |
| hold bar | 490..790 | 586..598 |

New strip: four 96px chips on a 108px pitch, total 420px, centred -> **x 430..850, y 648..692**. Summary line
-> **x 380..900, y 619..641**.

Clearances: 3px above the mana label's bottom edge (and the mana bar/health bars are clear in x regardless);
50px below the hold bar; 21px between the summary line and the hold bar; 7px between the summary line and the
chip tops; 16px right of the weapon label; 32px left of the ammo label; the skill chips are clear in x
(850 < 882) and in y. Nothing in the strip covers the prompt, the hold bar, the crosshair or the toast.

At 1920x1080 the same offsets give chips **x 750..1170, y 1008..1052** and summary **x 700..1220, y 979..1001**,
against a mana label that ends at x 598 / y 1055..1075, a skill bar that starts at x 1522 / y 874..952, an ammo
label that starts at x 1572 / y 992..1020, an ammo fill at x 1676..1896 / y 1027..1037, a hold bar at
x 770..1150 / y 946..958 and a prompt at x 580..1340 / y 914..946 - all clear.

The strip is inside `HudRoot`, so `OnRunState` already hides it outside Briefing/Running/Paused, and the
`Emberdelve_InventoryScreen` host object is still a sibling of `HudRoot`, not a child of the row.

## Checks actually run by the worker

None. File tools only, per the task contract: no Unity, no compile, no play mode, no build, no screenshot, no
PNG opened. The rectangles above are arithmetic from the source constants, anchors, pivots and Unity's
"anchoredPosition is the pivot" rule, cross-checked against the accepted U1C2C numbers (which reproduce
exactly from the same source) - arithmetic, not a rendered result.

Static API confirmation used from existing source: `UiFactory.SetRect` 5- and 6-argument overloads and
`DefaultPivot` (`UiFactory.cs`), `Image.raycastTarget` / `Text.raycastTarget` (inherited from `Graphic`, used
as `block.raycastTarget = false` in `InventoryScreenController`), `Text.horizontalOverflow` /
`verticalOverflow` (used in `InventoryScreenController`), `Inventory.Slots/UsedCells/Capacity/CarriedValue`,
`PlayerRig.Equipment` -> `Equipment.FilledCount` (`PlayerRig.cs:65`, `Items/Equipment.cs:34`), and the
`<Keyboard>/1..4` bindings behind `EmberdelveInput.SlotPressed` (`EmberdelveInputFallback.cs`).

## Pending manager gates

1. Compile/import of the two files.
2. Populated and empty hotbar in the real player at **1280x720** and **1920x1080**: four chips visible and
   readable, `1..4` glyphs legible, names/counts correct for the starting kit, `empty` chips when the bag
   holds fewer than four stacks, and no coverage of health/mana/ammo/skills/prompt/hold.
3. Live refresh: pick up loot, press 1-4 to consume a stack, equip/unequip in the satchel, and confirm the
   chips re-read the shifted bag (stack removed -> the next stack takes its chip; nothing lags a rebuild).
4. Ordinary pointer equip/unequip and the chest/death/extraction/result loop, which remain the open U1C2
   items - unchanged code, still unverified.
5. Re-run the inventory UI regression flow (`EmberdelveInventoryUiChecks`, 157/0 before this task). Nothing in
   that file was edited and no assertion was weakened. No existing check inspects `Slot1..Slot4`; the names
   were kept anyway.
6. Visual judgement: whether the summary line's wording/length and the wrapped chip names read well at both
   resolutions, and whether the chip top accent (item icon colour) is a useful identifier.

## Remaining work / notes

* The strip deliberately does not claim an item is usable. Deciding that would duplicate
  `Inventory.TryUseSlot`'s rules (`healAmount > 0 || ammoGrant > 0`) in the HUD, which can drift from the
  authority. If the manager wants a "no use effect" marker, it should be a separate decision with a shared
  predicate, not a UI guess.
* The summary line is one line at a fixed 520px width. If a future locale or a long carried-value number makes
  it longer than ~520px it will overflow horizontally (default `Overflow`); the manager's screenshot decides
  whether it should wrap or shorten.
* Chip labels truncate at two wrapped 12px lines; deliberately so - full item detail stays in the satchel.
* No blocker. The only thing not done here is verification, which belongs to the manager.
