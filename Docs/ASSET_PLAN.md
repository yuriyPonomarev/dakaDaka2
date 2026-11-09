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
- Weapons (mace, war hammer, dagger, estoc, sledgehammer, hatchet, kite shield): downloaded in V3c, see the V3c section below.

## V3b: Quaternius undead enemies (4 Nov 2026)

User rejected the chibi KayKit skeletons and chose a free CC0 animated human model turned into grim undead. Enemies now use the Quaternius Universal Animation Library 1 + 2 (Standard) mannequin, Humanoid. License CC0 1.0 (https://creativecommons.org/publicdomain/zero/1.0/); author Quaternius (https://quaternius.com). The KayKit skeleton characters are no longer used; the KayKit blade / axe / small shield meshes remain as weapon placeholders until the weapon-visual slice.

**5 Nov update:** enemy bodies now use the rigged Human Skeleton by Grab3D, CC0-1.0, https://grab3d.com/models/anatomy/human-skeleton/ ; source GLB `Art/ThirdParty/Grab3D/HumanSkeleton/human-skeleton-rigged.glb`, SHA-256 `40e26b694ecbb89ea1f00f1d0a8ecd4a2d9a1d48f70b10ae8aec15593bd30e73`. Original license and manifest are alongside it. The author states the mesh and textures were AI-generated and cleaned up in Blender, with automatic rigging. `Tools/OptimizeGrab3DSkeleton.py` uses Blender to make the skinned FBX and extract its texture: 270,030 → 22,952 triangles, preserving the rig and weights; Unity imports the result as Humanoid. The existing CC0 Quaternius animation library remains in use. Four variants use the same skeleton with different bone tones and equipment; the game currently maps Minion, Rogue and Warrior. Finger bones are absent and extreme retargeted poses need in-game review. Client-only enemy views remain separate proxies.

| Asset | Source | License | SHA256 | Unity path | Use |
|---|---|---|---|---|---|
| Universal Animation Library Standard (mannequin + 46 clips) | https://opengameart.org/node/174563 (universal_animation_librarystandard.zip) | CC0-1.0 | 42f16a4ed61d1e9f5fc7c2526894c5487148ac6ba82b64281ceb7943570f3c37 | `Art/ThirdParty/Quaternius/UAL1/AnimationLibrary_Unity_Standard.fbx` | male mannequin (Minion, Warrior, Mage), clips (idle, walk, jog, sword attack, hit, death, spell) |
| Universal Animation Library 2 Standard (mannequin + 43 clips) | https://opengameart.org/content/universal-animation-library-2 (universal_animation_library_2standard.zip) | CC0-1.0 | d4a2dd67bb12bf0c01891bc59ee697e04db679d26883d30bd937c2f3fb6fec90 | `Art/ThirdParty/Quaternius/UAL2/UAL2_Standard.fbx` | clips (zombie idle / walk, sword regular A-C, shield idle) |
| Mannequin_F (female mannequin, no clips, same rig) | same page as UAL2 | CC0-1.0 | 890784432838b1f4f9194fd6011769888872a7ba7f3159689771e1998c96a42c | `Art/ThirdParty/Quaternius/UAL2/Mannequin_F.fbx` | Rogue |

- Machine-readable: `Art/ThirdParty/Quaternius/manifest.json`, `LICENSE_CC0.txt`, the packs' own `License.txt` next to the FBX files. Import: `EmberdelveQuaterniusImport` (Humanoid, loop flags, baked root). Modifications to the files: none (byte for byte); the undead look is generated by `EmberdelveUndeadMaterials` (derived meshes with planar UVs, body textures, rusty metal, emissive eyes).

## V3c: Poly Haven weapons + first-person arms (4 Nov 2026)

Seven CC0 1k weapon models (Poly Haven, https://polyhaven.com/license, CC0 1.0) are imported with the same pipeline as the props (`EmberdelvePolyHavenImport.Fetch(WeaponIds)`, manifest + SHA256 in `Art/ThirdParty/PolyHaven/manifest.json`, URP Lit materials). FBX stay CPU readable (the normaliser measures the mesh). Modifications to the files: none; the look is a material tint (`EmberdelveWeaponModels.ApplyLook`) and the normalised prefabs `Prefabs/Visual/Weapons/WV_<id>.prefab` (grip at origin, tip +Y, face +Z).

| Asset (Poly Haven id) | Authors | SHA256 of the 1k FBX | Use |
|---|---|---|---|
| antique_estoc | James Ray Cock, Ulan Cabanilla | 676b2569800693529a9e4d88de524666aa81e513cea8fa332286dcc2663b4108 | player ashen_short_sword + ember_rapier; enemy Minion (rusted) |
| sledgehammer_01 | Dylan Guzman | 98ca21604fab0ae0503d84d6452cc0d29d1c3839990a78be6f7c0ae5aff3443b | player warden_maul |
| ornate_medieval_mace | Ulan Cabanilla | 949f83aa39dfaf42580586ec80bb322dd0d6045b26dfe223ebf27ff263f304ea | spare (no WeaponData yet) |
| ornate_war_hammer | James Ray Cock, Ulan Cabanilla | 5eb5225da18262949bae5e7175f4cee43ac3e62c961412dc9d098d04971948c8 | spare (no WeaponData yet) |
| ornate_medieval_dagger | Sofia Pahaoja | 090d66b5ddedb76b6d644b069abc4638be96757f068ccfc4a627f62b73475a56 | enemy Rogue (rusted) |
| hatchet | James Ray Cock, Ulan Cabanilla | 71fa38e6f7881af3e9f5607e65eba7f649f7f5148dae16a3efe098ecb6997a2c | enemy Warrior (rusted) |
| kite_shield | Ulan Cabanilla | 69fa452f7ee7f8c8aadda5e702ec99a9fad1425a4a5e788c842d22326b84f2d2 | player emberbulwark_shield; enemy Warrior (rusted) |

- Mapping lives in `EmberdelveWeaponModels.Mapping` / `Unmapped` and is written to `Resources/Emberdelve/WeaponViewModelSet.asset`. Unmapped (keep the blockout, no arms): ember_crossbow, hollow_bow (ranged, no draw animation yet), hollow_claw (enemy only).
- The first-person arms reuse the Quaternius mannequin and library clips already listed under V3b (no new files); the arms mesh, sleeve and skin textures are generated by `EmberdelveFirstPersonArms`.
- The KayKit blade / axe / small shield are no longer referenced by any enemy prefab.

## U5B: OpenGameArt sword models (4 Nov 2026)

Two CC0 models for the arming sword and the two-handed sword (Poly Haven has no suitable longsword; the Quaternius Modular Weapons download was quota-blocked). Files are byte for byte from the source, import and materials by `EmberdelveOpenGameArtImport`, manifest + license at `Art/ThirdParty/OpenGameArt/manifest.json` and `LICENSE_CC0.txt`, normalised prefabs `Prefabs/Visual/Weapons/WV_knights_sword.prefab` and `WV_long_sword.prefab`.

| Asset | Author | Source | License | SHA256 | Use |
|---|---|---|---|---|---|
| Knight's Sword (.obj) | Teh_Bucket | https://opengameart.org/content/knights-sword | CC0-1.0 | b0f8016725ea598fc3e94f542f3f44e148d2756d99edb8d989bb938b81b65fa3 | player arming_sword |
| Knight's Sword texture (.png) | Teh_Bucket | same page | CC0-1.0 | ac8f696de904b8716e357ad21e06548a41304025318438ebdd6b0e7afd94f656 | texture of the above |
| One Handed Sword (.fbx) | LordNeo | https://opengameart.org/content/one-handed-sword | CC0-1.0 | f5a55440ac8536ba40b586c4ecf74e2935692866d4dfc89cceb622e11c1a293b | player two_handed_sword |
| One Handed Sword texture (.jpg) | LordNeo | same page | CC0-1.0 | e9334d76e18c0fdd62e492693b69ff127cddfb0291038717f88700338e14cdbe | texture of the above |

- Rejected: Great Sword by Mikeask (toy-like, no crossguard); stretching the estoc (not acceptable). Better option for later: Quaternius Modular Weapons (Claymore), CC0, if its download works.

## Network client enemy visual reuse (7 Nov 2026)

`Resources/Emberdelve/NetEnemyVisual.prefab` is a runtime-loadable copy of the existing `Prefabs/Visual/EnemyVisual_Minion.prefab`; it introduces no new third-party files. Mesh, materials, rig and Quaternius animation controller retain their original asset GUID references. Source assets and their CC0-1.0 licenses are listed under V3b above. The client proxy removes the prefab's server-side visual driver and blood particle child after instantiation.
