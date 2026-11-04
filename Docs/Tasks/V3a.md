# V3a - realistic CC0 props (Poly Haven)

Request: grim, less cartoony dark-fantasy look (Dark and Darker-like), not by darkening the lighting. Props replaced; enemies untouched (V3b).

## Done
- Downloaded 17 Poly Haven models (1k FBX + textures, CC0) with `EmberdelvePolyHavenImport.Fetch`: treasure_chest, Barrel_01, wine_barrel_01, wooden_barrels_01, wooden_crate_01, wooden_crate_02, wooden_bucket_01, Lantern_01, wooden_candlestick, brass_candleholders, WoodenTable_01, wooden_stool_01, wooden_bookshelf_worn, worn_metal_rack, GothicCabinet_01, rock_moss_set_01, root_cluster_01. `manifest.json` (authors, page, license CC0-1.0, source URL + SHA256 per file, Unity path, usage), `LICENSE_CC0.txt`, `Docs/ASSET_PLAN.md` section.
- Import: `EmberdelvePolyHavenPostprocessor` (textures Point, 512 px max = retro convention; models no rig/animation/colliders), `BuildMaterials` (URP/Lit, OpenGL normal map, packed `_MaskMS`, albedo lift 1.8, glass pane transparent, candle flame emissive), FBX remapped to project materials.
- `EmberdelvePropFactory` rewritten: chest set (Supply/Ember/Sealed = treasure_chest, tinted), room themes with Poly Haven models, density 60 % / max 7 per room, corner cells prefer root clusters and moss rocks, `#part` keys pick one piece of a set (rocks, barrels, candelabra), holder object keeps the FBX root transform (some files carry -90 deg X / x100), set pieces scaled to a target size, `BuildLobbyProps` (13 wall pieces under `Lobby_Props`). Still no colliders; same eligibility rules (not next to doorways/spawns/chests/enemies/start/extraction).
- `EmberdelveTorchFactory`: Lantern_01 (0.5 m) hangs from a dark iron wall bracket (plate + arm); child name `Sconce`, frame, flame particles and light wiring unchanged; KayKit torch no longer used.
- `EmberdelveLobbyBuilder` calls `BuildLobbyProps`. Level01 and Lobby were rebuilt through `EmberdelveBootstrapMenu.BuildEverything`.
- `EmberdelveRetroVisualGate` updated honestly for the new source: Lantern_01 instead of torch_mounted, chest message, Poly Haven manifest/license/hash/import/material checks (`CheckPolyHaven`), PolyHaven textures in the Point/<=512 check, every prop material must be a Poly Haven project material.

## Checks actually run (open Editor)
- `console_get level=error`: empty after the final compile.
- EditMode `Emberdelve` assembly: 555 total, 555 passed, 0 failed.
- `EmberdelveGateSuite.StartInteractive("fast")`: 12/12 PASS (`Logs/quick/v3a_20261103_194159/summary.txt`).
- RetroVisual gate sections (PolyHaven, textures, torches, props, chests) invoked by reflection on the rebuilt dungeon: 29 checks, 0 failures. The full RetroVisual gate (batch + Exit, enemies/lobby rebuild) was NOT run; it is in the `visual` set, for Run-All.

## Evidence
`Logs/quick/v3a/v3a_torch.png` (lantern on bracket), `v3a_chest.png` (treasure chest, Supply), `v3a_barrels.png` (barrels by a torch in a corridor room), `v3a_lobby.png` (lobby: bookshelf, crates, barrels). Camera captures from a temporary camera with the retro pass; torches were mounted with TorchFlicker.Start by reflection (edit mode); the temporary objects were removed and the scenes reloaded from the saved assets.

## Not done / risks
- Weapon visuals (mace, hammer, dagger, estoc, shield): not touched; `WeaponVisualRig` is a separate slice.
- Dungeon density is modest (14 props in Level01); shelves/racks/tables/stools only show up where room themes and wall cells allow (more in the lobby).
- Photo-scanned albedos are dark; a 1.8 lift was needed, the cabinet is still near black in torch light (dungeon themes no longer use it).
- 203 MB in git for the PolyHaven folder (source rough/metal PNGs included).
- Run-All (mirror) not run.
