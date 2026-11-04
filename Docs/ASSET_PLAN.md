# Asset plan and sourcing

19October2026: user explicitly authorized searching open-source textures/packs/assets. Codex curates downloads and licenses; local worker integrates only assigned files. No payments or external messages.

First inventory/reuse the existing packs at C:/Users/yrapa/Documents/just test/новый-игровой-проект/assets/vendor and original manifest.json. They include KayKit dungeon/adventurer/skeleton meshes/animations and Kenney audio. Do not redownload the same files. Import into a Unity vendor namespace only when the relevant stage is assigned; preserve licenses/hashes and use prefab adapters so collider/gameplay sizes remain stable. Inspect actual file formats before deciding whether Unity needs an importer or alternate source format; do not claim glTF is natively ready merely because Godot loaded it.

Verified author sources:
- KayKit Dungeon Remastered: https://github.com/KayKit-Game-Assets/KayKit-Dungeon-Remastered-1.0 — author README/license CC0-1.0, dungeon props/walls/chests, FBX/GLTF/OBJ listed. Already downloaded content is preferred.
- ambientCG textures: https://docs.ambientcg.com/license/ — downloadable assets are CC0-1.0. Candidate source for missing worn stone/wood/moss surfaces; no particular texture downloaded/selected yet.

For every imported pack/material keep a machine-readable manifest: source URL, creator, license text/file, pinned version/commit if applicable, downloaded/copied SHA256, relative Unity path, modifications and usage. Open-source code dependencies and art licenses are distinct: inspect package/code license as well as asset license. Final selection prioritizes a coherent dark stylized crypt, readable enemy/player/weapon silhouettes and restrained telegraphs. Unlicensed/ambiguous, ripped or merely free-to-view content is not a deliverable.

Integration passes: one floor/wall/torch room -> one animated enemy and both player classes -> weapons/chests/loot -> sound/music. Render-inspect each representative pass. Avoid a bulk import that hides broken materials or takes a model implementation attempt away from gameplay.

## V3a: Poly Haven realistic props (3 Nov 2026)

User rejected the chibi KayKit dungeon props (wanted a grim, less cartoony Dark and Darker-like look, not just darker lighting). The dungeon/lobby props, chests and wall torches now use CC0 models from Poly Haven (https://polyhaven.com, license https://polyhaven.com/license, CC0 1.0). KayKit skeletons stay for enemies (V3b); the KayKit dungeon FBX files stay on disk but are no longer used by any builder.

- Location: `Assets/Emberdelve/Art/ThirdParty/PolyHaven/<id>/` (1k FBX + textures), `manifest.json` (id, page, authors from api.polyhaven.com/info, license, source url + SHA256 of every file, Unity path, usage), `LICENSE_CC0.txt`.
- Models (17): treasure_chest, Barrel_01, wine_barrel_01, wooden_barrels_01, wooden_crate_01, wooden_crate_02, wooden_bucket_01, Lantern_01, wooden_candlestick, brass_candleholders, WoodenTable_01, wooden_stool_01, wooden_bookshelf_worn, worn_metal_rack, GothicCabinet_01, rock_moss_set_01, root_cluster_01. Used in scenes: treasure_chest, Barrel_01, wine_barrel_01, wooden_barrels_01 (single barrels), wooden_crate_01/02, wooden_bucket_01, Lantern_01, brass_candleholders (standing candelabra piece), WoodenTable_01, wooden_stool_01, wooden_bookshelf_worn, worn_metal_rack, GothicCabinet_01 (lobby only: too dark for the dungeon), rock_moss_set_01 (single rocks), root_cluster_01. wooden_candlestick is downloaded but unused.
- Reproducible: `EmberdelvePolyHavenImport.Fetch(ids)` (menu Emberdelve/Visual/Fetch Poly Haven Props) downloads from api.polyhaven.com (fbx -> 1k, `include` map keeps relative paths; EXR maps are fetched as their PNG twin), `BuildMaterials(id)` makes URP/Lit materials (base colour x1.8 lift, OpenGL normal map, packed `_MaskMS` = R metallic*0.5, A smoothness from roughness) and remaps the FBX to them. Import settings: `EmberdelvePolyHavenPostprocessor` (textures Point filtered, max 512 px = the retro convention; models without rig/animation).
- Weapons (mace, war hammer, dagger, estoc, sledgehammer, hatchet, kite shield): not downloaded yet; the held/world weapon visuals are a separate slice.
