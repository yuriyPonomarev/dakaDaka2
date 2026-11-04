# CURRENT WORK - 3 Nov 2026

Rewrite this file each slice; history is in `Docs/Archive/`.

## State
- Base HEAD `e6155da8` + uncommitted slice **V3a** (realistic props, not committed yet): the cartoony KayKit dungeon props are replaced by CC0 Poly Haven models.
- V3a: chests (treasure_chest, Ember/Sealed = same model tinted), wall torches (Lantern_01 on a dark iron bracket, same flame/light logic), room scatter (barrels, crates, shelves, racks, tables, stools, candelabra, moss rocks, root clusters; corners prefer rocks/roots) and a set-dressed lobby (`Lobby_Props`). Props have no colliders; gameplay objects and positions are unchanged. Scenes were rebuilt with `EmberdelveBootstrapMenu.BuildEverything`.
- Assets: `Assets/Emberdelve/Art/ThirdParty/PolyHaven/` (17 models, 1k, 203 MB incl. source maps), `manifest.json`, `LICENSE_CC0.txt`; reproducible via `EmberdelvePolyHavenImport` (Fetch / BuildMaterials). Albedo lift 1.8 because the scans are very dark under torch light only.
- Checks run in the open Editor: console errors none; EditMode 555/555; fast gates 12/12; the RetroVisual gate cannot run here (batch + Exit), its Poly Haven / torch / chest / prop / texture sections were run by reflection: 29 checks, 0 failures. Run-All still owed.
- Evidence: `Logs/quick/v3a/v3a_{torch,chest,barrels,lobby}.png`.

## Decisions in force
- Sword-vs-sword block holds (`Weapon_AshenShortSword.impactResist 15`); the sledgehammer breaks guard.
- Guard endurance/stamina OFF.
- Realistic CC0 props (Poly Haven) instead of KayKit for the environment; KayKit stays for skeleton enemies until V3b.

## Next
1. V3b: replace the cartoony KayKit skeletons with a grim realistic enemy look; weapon visuals (Poly Haven mace/hammer/dagger/estoc/shield are available).
2. Run `Tools/Run-All.ps1 -Set all -EditMode` (V3a touched scenes) and commit V3a.
3. U4A contested chests over the network, U4B corpse/raid timer/extraction, U4C stash, U3C2.

## Blockers
- none (repo size: PolyHaven folder is 203 MB; the unused rough/metal source PNGs could be dropped if that matters)
