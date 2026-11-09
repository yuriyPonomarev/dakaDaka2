# LOOT-01 inventory reference layout — 9 November 2026

Completed visual follow-up to 449c1522. User requested closer resemblance to the supplied Dark and Darker inventory screenshot.

## Scope

Three presentation files: LootPanelStyle, InventoryScreenController, CorpseContainerScreenController.
Shared paper doll now uses paired 50x128 weapon slots at the edges, central helmet/torso/legs, smaller jewelry and surrounding gloves/cloak/boots. Empty slots show subdued family silhouettes. Added metallic corners, engraved separators and background rays. Removed tiny item-name overlays and redundant equipment labels; tooltip names remain. Icons have shaded surface variation. Final graphical review corrected undersized weapons in tall slots.

No changes to transfer rules, item footprints, catalog, protocol7, authority or persistence. Original procedural icons remain stylized; this is not a detailed 3D-thumbnail asset pass or a pixel-for-pixel copy of the reference.

## Verification

Checkpoint Logs/harness/before-inventory-art.json. Planner selected 70 gates conservatively for the shared UI helper; narrowed to compilation and graphical/input acceptance because all three changed files contain presentation changes only. Previous network/rules proof remains documented under LOOT_UI_20261109.md, not claimed as rerun here.

Mirror-only builds: build.log PASS; build-final.log PASS after the weapon display-size correction. Seed11, protocol7, 1280x720 real graphical host/client, existing corpse review fixture and actual mouse/key input.

Final evidence under Logs/acceptance/20261109-inventory-art:
- final-host/snapshots/paired-final.png: matching living/fallen paper dolls, tall weapon display and bag layout.
- final-client/snapshots/chest-transfer.png: potion removed from chest, own stack increased to2.
- final-host/snapshots/worn-transfer.png: clicked corpse main-hand slot, sword moved into own bag; occupancy1 ->4.
- final-host/snapshots/unequip.png: right-click own new main-hand rectangle removes worn weapon into bag; occupancy4 ->7, equipped fists.
- final-host/snapshots/closed.png: Escape closes both panels without pause menu.

Review processes closed through bridge (client3, host8). Main Editor untouched. No additional test tooling or packages. Six inherited material/Pipeline/screenshot paths excluded from this milestone.
