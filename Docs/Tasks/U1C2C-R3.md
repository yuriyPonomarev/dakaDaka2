# U1C2C-R3 — readable worn equipment label (partial: pending manager verification)

Changed file: `Assets/Emberdelve/Runtime/UI/InventoryScreenController.cs` (Rebuild, equipment column only).
Behavior: `EquipLabel_<slot>` foreground for a worn item is now the existing light `TextColor` (0.88,0.84,0.78) instead of `Color.black`, so a dark icon colour (Footpad Boots) no longer makes the worn name invisible on the dark SatchelPanel; empty slots still use `DimColor`. Added a 4-line comment explaining the label is beside the slot, not inside the coloured block.
Unchanged: slot `Image` colour (`worn.iconColor` / `SlotColor`) and alpha, text 13/bold/MiddleLeft, `EquipLabelWidth`/18px rect, anchors/pivot/position, widget names, grid/ghost/hit-test/transaction/selection code, `Count` label (still black inside the coloured item block).
Checks actually run: none (file tools only) — no Unity compile, no smoke run, no screenshot taken by me.
Pending manager: fresh build, actual equip of Footpad Boots, `EquipLabel_Legs` contrast PNG plus the U1C2F loot/equip/unequip/use/shift regression flow.
Remaining work: none in this scope; label contrast confirmation and any wider UI polish (description/features) are manager-side.
