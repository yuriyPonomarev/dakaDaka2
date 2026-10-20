# U1C2C — contain the actual HUD and satchel inside the window (implementation saved, not verified)

Partial: the four defects named in the manager's rendered evidence are corrected in source. One further
containment defect was found while auditing every `UiFactory` caller and is **not** fixed here, because every
layout that would fix it either overlaps other HUD clusters or shrinks the widgets past readability. It is
reported below as a blocker for a separate decision.

## Files changed (only the three named files)

* `Assets/Emberdelve/Runtime/UI/UiFactory.cs`
* `Assets/Emberdelve/Runtime/UI/HudController.cs`
* `Assets/Emberdelve/Runtime/UI/InventoryScreenController.cs`

No scene, prefab, package, metadata, test, editor or other runtime file was touched. No asset was imported.
Event subscriptions, transaction logic, selection/ownership rules, hit-testing and the ghost coordinate
conversion are unchanged.

## Root cause 1 — `SetRect` forced a centre pivot on corner-anchored widgets

`SetRect` always set `pivot = (0.5, 0.5)`. `anchoredPosition` is the position of the **pivot**, so a widget
anchored to a corner with a small corner inset had its **centre** at that inset, and half of the widget landed
outside the corner it was named for.

Audited before changing anything: every `SetRect` caller in `Assets/Emberdelve` (`HudController`,
`InventoryScreenController`, `LobbyScreenView`, `ResultScreenController`). Most widgets are genuinely centred
(prompt, toast, pause, crosshair, result buttons, lobby cards, HUD inventory chips) and must keep the centre
pivot. So the factory gained an **additional overload** `SetRect(rect, anchorMin, anchorMax, sizeDelta,
anchoredPosition, pivot)` plus `DefaultPivot` / `AnchorPivot`, and the original five-argument form now simply
forwards `DefaultPivot`. Every existing call site therefore lays out exactly as before unless it was
deliberately re-pointed. `CreateImage` / `CreateText` / `Stretch` / `CreateGroup` are untouched.

`sizeDelta` semantics are documented in the new comment and were **not** silently reinterpreted: it stays
relative to the anchor region (explicit size for point anchors, inset for stretch anchors). Where a caller
passed a full size *and* a stretched anchor — which asks RectTransform for a rectangle wider than its parent —
the caller was corrected instead.

### HUD widgets re-pointed (`HudController.BuildHud`, `BuildSkillBar`, `BuildPassiveReadout`)

| Widget | Before (1280x720, image coords, y from top) | After |
|---|---|---|
| ClassLabel (anchor 0,1; offset 24,-22; 420x34) | rect x **-186..234**, text starts off the left edge | pivot (0,1) → x 24..444, y 22..56 |
| ObjectiveLabel (520x60) | x **-186..234** | x 24..544, y 58..118 |
| StatusLabel (520x26) | x **-186..234** | x 24..544, y 118..144 |
| KitLabel (430x92, offset 24,132) | x **-191..239** | pivot (0,0) → x 24..454, y 496..588 |
| AmmoLabel (320x28, anchor 1,0, offset -28,74) | x **1092..1412** (right 132px off screen) | pivot (1,0.5) → x 932..1252, y 632..660 |
| ManaLabel (220x20) | x 268..488, text started **inside** the mana bar (bar spans 24..364) | pivot (0,0.5) → x 378..598, clear of the bar |

HealthLabel, WeaponLabel, HealthFill/ManaFill/AmmoFill/HoldFill pivots, the health/mana bar geometry, the
prompt, hold bar, toast, pause label, crosshair and vignette are unchanged — they were already contained.

### Skill chips (the "titles/cost/key overlap")

Inside a 178x78 chip the labels used **stretched** anchors while `sizeDelta` still carried a full chip width,
so the requested rectangles were wider/taller than the chip: NameLabel came out 296x24 spanning x -96..200
(the chip is -89..89), CostLabel 336 wide, CooldownLabel 336x132, ReasonLabel 340 wide. Titles therefore
started left of the chip and overlapped the key glyph, and the cost/cooldown text ran past both edges.

Now: KeyLabel, NameLabel, CostLabel and ReasonLabel use point anchors with an explicit width and a corner
pivot; CooldownLabel keeps its stretch anchors and now passes the **inset** `(-20, -24)`.

Chip-local rectangles (chip is x -89..89, y -39..39): KeyLabel x -79..-33 y 9..31; NameLabel x -29..83 y 7..31;
CostLabel x -79..79 y -29..-9; CooldownLabel x -79..79 y -27..27; ReasonLabel x -81..81 y 1..35. All inside
the chip, key and title on the same row without overlapping. The chips themselves are unchanged and sit at
screen x 882..1060 and 1074..1252 at 1280x720 (1542..1720 / 1734..1912 at 1920x1080). `SkillBar` still has
exactly two children, which the existing smoke check asserts.

## Root cause 2 — the equipment column descended twice

`_equipOrigin = equipCenter - (EquipSlotSize/2, columnHeight/2)` already put the origin a full half-column
**below** the centre, and the loop then descended again with `-(i * equipStep) - EquipSlotSize/2`. The first
slot centre landed 332px below where it should be: local y -188, -260, -332, -404, -476 → image y 528, 600,
672, 744, 816 at 1280x720, i.e. Chest and Legs below the 720px window (matching the evidence).

The origin is now the column's **top-left corner** (`equipCenter + (-EquipSlotSize/2, +columnHeight/2)`) and
each row descends from it once. Slot centres: local y 164, 92, 20, -52, -124 → image y 196, 268, 340, 412, 484
at 1280x720. All five slots (64px) span image y 164..516, inside the panel (125..555) and inside the window.

Slot labels were also the "overflow right" half: they were 154px wide, centre-pivoted 84px right of the slot
centre, so they spanned screen x 897..1051 while the panel ends at 980. They are now `EquipLabelWidth = 118`,
pivot (0, 0.5), `MiddleLeft`, offset `EquipSlotSize/2 + EquipLabelGap (10)` from the slot centre → screen x
846..964 at 1280x720, inside the panel. Font size 13 and bold are unchanged; the names are short
(`Weapon`, `Offhand`, `Head`, `Chest`, `Legs`, or the worn item's display name).

## Root cause 3 — the satchel was laid out from loose offsets, not from the panel

`InventoryRoot` is a zero-size group, so every anchor point inside it coincides with its centre and only
pivot + offset matter. The header (420x26 at -400,190), detail (700x60 at -380,-150) and carry (420x24 at
-190,-190) labels were centre-pivoted, so half of each label sat left of the panel: the header started at
screen x 30 and the detail at x -90, while the panel spans x 220..980. The grid centre (-200,40) also put the
478px grid 20px outside the panel's left edge (evidence: grid x200..680 vs panel x220..980).

`Rebuild` now measures everything from the panel's own inner bounds and converts panel-local → root-local
through the new `PanelToRoot` helper. Constants: `PanelWidth 760`, `PanelHeight 430`, `PanelCenter (-40,20)`,
`PanelInset 16`, `ColumnGap 10`, `EquipLabelGap 10`, `EquipLabelWidth 118`, `HeaderHeight 26`,
`DetailHeight 74`, `CarryHeight 56`.

Derived layout (panel-local, origin at panel centre, y up; inner bounds x ±364, y ±199):

* worn-kit column left = `364 - (64 + 10 + 118)` = **172**, so slots span 172..236 and labels 246..364 — a
  slot label can no longer hang past the panel.
* left column width = `172 - 10 - (-364)` = **476** (the grid is 478 wide and spans -364..114, leaving a 58px
  gutter to the kit column).
* left column block (header + grid + detail + carry + three gaps = 376) is centred in the 398px inner height:
  header 188..162, grid 152..-38, detail -48..-122, carry -132..-188.
* the kit column (352 tall) is centred vertically: 176..-176.

Result at 1280x720 (image coords): panel 220..980 / 125..555 (unchanged), grid 236..714 / 188..378, header
236..712 / 152..178, detail 236..712 / 388..462, carry 236..712 / 472..528, slots 772..836 / 164..516, slot
labels 846..964. Everything is inside the panel, and the panel is inside the window. At 1920x1080 the same
numbers shift by the larger canvas centre and stay inside (panel 540..1300 / 345..775).

Cell/item/selection-frame/ghost placement maths, `_gridOrigin`, the `center - gridCenter` child offsets,
`selectedCenter` (still root-local, so the no-mouse ghost still parks over the footprint it names) and
`UpdateGhost`'s `RectTransformUtility.ScreenPointToLocalPointInRectangle(_root, …)` are unchanged. Hit-testing
still uses the rendered `_cellRects` / `_equipRects` with `RectTransformUtility.RectangleContainsScreenPoint`
and the same `CanvasCamera` rule, and the widget names the checks look for (`Cell_x_y`, `Item_…`,
`Selected_…`, `Equip_<slot>`, `EquipLabel_<slot>`) are unchanged.

The detail and instruction labels now wrap: `horizontalOverflow = Wrap`, `verticalOverflow = Truncate`, inside
their 476px column (4 lines of 16px detail, 3 lines of 15px instructions). An unwrapped
"grid 10x4 … worn: … modifiers: …" line is roughly 1200–1400px wide, which is what let it run off the window.
The header keeps Overflow on purpose: its rect ends at panel-local 112 and the 58px gutter plus the kit column
give it room, so nothing of the header line is clipped.

## Not fixed — HUD bag strip (`HudController.RebuildInventory`): reported blocker

`RebuildInventory` builds one chip per **capacity cell** (`Inventory.Capacity = 10 x 4 = 40`) at 96x44 in a
single row centred on the bottom edge: 3840px wide, so at 1280x720 the strip spans x -1280..2560 and the
`[1]…[4]` hotkey chips are off screen at x -1760..-1376, while the visible middle of the strip already covers
the mana bar (x 24..364, y 695..709 image), the health bar (y 666..686) and the ammo fill.

I prototyped the obvious repair (four rows of ten, chips unchanged) and rejected it after measuring it: the
block would be fully inside the window (x 160..1120, y 18..212 screen coords) but would then cover the health
bar, health label, weapon label, mana bar/label, ammo label/fill, kit label, prompt label, hold bar and the
left edge of the skill bar — strictly worse than today. The alternatives are all outside this task's
"no redesign / do not shrink to unreadability" limits:

* 10 per row at 96px needs 960px; the only free bottom band at 1280x720 is x 380..880 (left cluster ends at
  364, skill bar starts at 882), so 10 readable chips per row cannot fit beside the existing clusters.
* 5 per row at 96px fits horizontally (480px) but needs 8 rows ≈ 400px vertically, crossing the prompt/hold
  bar and the crosshair.
* A 10x4 mini-grid that does fit (48x22 chips, x 390..890, y 18..118) leaves ~36px per label — unreadable.

This is a design decision (what the raid strip should show now that the bag is a grid rendered in full in the
satchel screen — e.g. only the four stacks `HotbarController` actually consumes, plus a compact bag summary),
not a geometry fix, so the strip is left byte-for-byte as it was and handed to the manager.

## Checks actually run by the worker

None. File tools only, per the task contract: no Unity, no compile, no play mode, no screenshot, no PNG was
opened. The geometry above is computed from the source (constants, anchors, pivots and Unity's
`anchoredPosition`-is-the-pivot rule), cross-checked against the panel/grid numbers quoted in the manager's
U1C2A evidence, which reproduce exactly from the pre-change source — that agreement is what makes the
arithmetic trustworthy, but it is still arithmetic, not a rendered result.

Static API confirmation used: `RectTransform.anchorMin/Max/pivot/sizeDelta/anchoredPosition`,
`Text.horizontalOverflow` (`HorizontalWrapMode.Wrap`) and `Text.verticalOverflow` (`VerticalWrapMode.Truncate`)
are the same members already used in `UiFactory.CreateText`, `HudController` (`_healthFill.rectTransform.pivot`)
and `InventoryScreenController` (the `Count` label's `offsetMin/offsetMax`).

## Pending manager gates

1. Compile/import of the three files.
2. Rebuild the review player and re-take `raid-first.png` and `inventory.png` at **1280x720** and
   **1920x1080**; compare against `validation/U1C2A-player-20261019-052117/snapshots/`:
   class/objective/status/kit inside the left edge; skill key, title, cost and cooldown inside each chip;
   ammo readout inside the right edge; satchel header/detail/instructions inside the panel; the whole 10x4
   grid inside the panel; all five equipment slots and their labels inside the panel.
3. Re-run the inventory UI regression flow (`EmberdelveInventoryUiChecks`): its click fixtures resolve
   `Cell_x_y` / `Equip_<slot>` rectangles from the rendered hierarchy and click their real screen centres, and
   its existing `Cell_9_3` assertion is already a physical screen-containment check. Nothing in that file was
   edited, and no assertion was weakened. If the manager wants stronger physical bounds, the natural addition
   is a corner test over `Cell_0_0`, `Cell_9_3`, `Equip_Weapon`, `Equip_Legs`, `EquipLabel_Legs`, `Header`,
   `Detail`, `Carry` — deliberately left to the manager, because the same assertion at a small editor game-view
   resolution can fail for reasons unrelated to the fix.
4. Confirm the satchel still behaves: select → move / swap / merge / equip / unequip / cancel / close, the
   ghost following the pointer, and no duplicate collection credit. None of that code was touched.

## Remaining work

* Manager decision + task for the HUD bag strip described above.
* Visual judgement of the new satchel gutter (58px between grid and kit column) and of the wrapped detail
  line, which truncates if the worn/modifier description exceeds four 16px lines — the manager's screenshot
  decides whether the detail block should be taller or the description shortened.
* Nothing else in this task's scope is unfinished; the four evidenced defects are implemented but unverified.
