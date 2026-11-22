# Asset plan and sourcing

## Asset sources and usage rights

| Source | Evidence to record before integration |
|---|---|
| Third-party downloads, including free packs and recordings | Prefer CC0. For any other free license, verify that the project permits it and that its terms cover the intended use and distribution. Keep the creator, source, exact license, attribution obligations and file hashes in the asset manifest. |
| Original project art or audio | Record the author or generator, source files, inputs and output hashes. For work incorporating third-party inputs, carry their licenses and attribution through to the derived asset. |
| Owner-supplied or generated files, including Meshy and Tripo | Identify the supplied archive or generation task, source files, hashes and applicable rights for this exact asset. Record any limits on use or redistribution. Owner delivery or a successful generation establishes provenance only. |

Where use rights are unclear, retain the source record and resolve the missing terms before a new integration or distribution. Existing entries below describe the evidence available for each asset.

## Downloaded Meshy architecture (22 November 2026)

The follow-up architecture pass uses the owner's downloaded Meshy StonePillar and RusticBarrel archives, plus the project's existing SealedChest source as a closed funerary reliquary. The exact source/archive hashes are in `Art/Models/AI/Meshy/DownloadedArchitecture/manifest.json`. These archives are owner-supplied and are not represented as CC0. Existing OvergrownArch geometry supplies cropped arch crowns and stone-facing pieces with original UVs. Shared display meshes limit repeated geometry; original sources remain intact. Pillars use the dungeon's world-space stone material to avoid the downloaded albedo's baked blue/black patches; their source geometry and original albedo are retained. Physical rooms, stair routes and secret controls are preserved. See `Tasks/DOWNLOADED_ARCHITECTURE_20261122.md` for the exact scope and review evidence.

## Original dark fantasy ambience (22 November 2026)

`Resources/Emberdelve/Audio/DarkFantasyAmbience.wav` is original deterministic synthesis, recovered from Sol and revised to the user-requested melodic dark-fantasy arrangement. No external samples or paid service are involved. Source: `Tools/Audio/SynthesizeDarkFantasyAmbience.cjs`; 48 seconds, stereo, 48 kHz PCM16. Cello/viola, low horn, woodwind melody, harp and sparse frame drum; 12 bars at60 BPM, D minor/B-flat/G minor/A dominant. Circular note/reverb tails make a seamless loop. SHA256: `c8eced308b3634687fdd8f7fecf3143a7b076b1847ab66c10c36fb6d1d84cf8d`. Peak −3.50 dBFS, RMS −14.74 dBFS, no clipping, seam steps6/6 PCM units. Replaces per-flame synthetic ticks and the old short wind loop; combat, doors and footsteps stay separate. See `Tools/Audio/README.md`.

## Expanded rooms and caches (21 November 2026)

Reuses the existing CC0 Poly Haven `WoodenTable_01`, `wine_barrel_01`, `wooden_crate_02` and `wooden_bucket_01` imports, including their original manifest/licenses below. Existing project `CryptArch.asset`, sarcophagus/column/brazier construction and stone surface materials furnish the expanded modules. New gallery/stair/undercroft/cache architecture and lever mechanism are authored with the existing masonry/wood/metal kit.

Public Meshy archway/coffin listings were researched, but individual asset retrieval was blocked by site access. No public Meshy model was downloaded, no license was inferred for an unverified individual file, and no paid generation occurred. These changes do not claim Meshy replacements for the existing project arches or sarcophagi. Implementation/evidence: `Tasks/ROOM_EXPANSION_20261121.md`.

19October2026: user explicitly authorized searching open-source textures/packs/assets. Codex curates downloads and licenses; local worker integrates only assigned files. No payments or external messages.

First inventory/reuse the existing packs at C:/Users/yrapa/Documents/just test/РЅРѕРІС‹Р№-РёРіСЂРѕРІРѕР№-РїСЂРѕРµРєС‚/assets/vendor and original manifest.json. They include KayKit dungeon/adventurer/skeleton meshes/animations and Kenney audio. Do not redownload the same files. Import into a Unity vendor namespace only when the relevant stage is assigned; preserve licenses/hashes and use prefab adapters so collider/gameplay sizes remain stable. Inspect actual file formats before deciding whether Unity needs an importer or alternate source format; do not claim glTF is natively ready merely because Godot loaded it.

Verified author sources:
- KayKit Dungeon Remastered: https://github.com/KayKit-Game-Assets/KayKit-Dungeon-Remastered-1.0 вЂ” author README/license CC0-1.0, dungeon props/walls/chests, FBX/GLTF/OBJ listed. Already downloaded content is preferred.
- ambientCG textures: https://docs.ambientcg.com/license/ вЂ” downloadable assets are CC0-1.0. Candidate source for missing worn stone/wood/moss surfaces; no particular texture downloaded/selected yet.

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

**5 Nov update:** enemy bodies now use the rigged Human Skeleton by Grab3D, CC0-1.0, https://grab3d.com/models/anatomy/human-skeleton/ ; source GLB `Art/ThirdParty/Grab3D/HumanSkeleton/human-skeleton-rigged.glb`, SHA-256 `40e26b694ecbb89ea1f00f1d0a8ecd4a2d9a1d48f70b10ae8aec15593bd30e73`. Original license and manifest are alongside it. The author states the mesh and textures were AI-generated and cleaned up in Blender, with automatic rigging. `Tools/OptimizeGrab3DSkeleton.py` uses Blender to make the skinned FBX and extract its texture: 270,030 в†’ 22,952 triangles, preserving the rig and weights; Unity imports the result as Humanoid. The existing CC0 Quaternius animation library remains in use. Four variants use the same skeleton with different bone tones and equipment; the game currently maps Minion, Rogue and Warrior. Finger bones are absent and extreme retargeted poses need in-game review. Client-only enemy views remain separate proxies.

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

## Inventory character preview (9 Nov 2026)

Resources/Emberdelve/UI/CharacterPreview.prefab reuses the existing Quaternius UAL2 Mannequin_F mesh and skeleton (CC0, Art/ThirdParty/Quaternius/UAL2/License.txt). EmberdelveCharacterPreview samples Idle_FoldArms_Loop from the already imported UAL2_Standard animation, then removes the Animator for a static portrait pose. No new external assets. The runtime uses a class-tinted material and isolated portrait camera; this is a mannequin preview, not a wearable-armour avatar.

## Class models and ability icons (9 Nov 2026)

- Warrior uses Knight, Archer uses Rogue_Hooded from KayKit Character Pack: Adventurers1.0, Kay Lousberg, CC0-1.0. Official source: https://github.com/KayKit-Game-Assets/KayKit-Character-Pack-Adventures-1.0 . Original selected GLBs, atlases, LICENSE.txt and SHA256 manifest are in Art/ThirdParty/KayKit/Adventurers. No assets from the unavailable third-party FBX mirror were imported.
- Tools/Convert-ClassModels.py converts the originals with Blender4.2.9 to FBX, retaining body/cape/helmet and required clips; optional simultaneous weapon variants are excluded. EmberdelveClassVisuals creates materials, named clips and class-prefab references in Resources/Emberdelve/PlayerVisuals. Existing skeleton/prop import rules remain unchanged; Adventurers has a dedicated import branch.
- Resources/Emberdelve/SkillIcons/Abilities.png is an original generated4x2 atlas, created for this project from a text brief inspired by engraved silver fantasy symbols. No image from the reference game is copied into the asset. Top row: Dash, Battle Cry, Ashen Might, Bulwark Training. Bottom: Multi-shot, Piercing Shot, Farshot, Fleet Footing. SkillIconAtlas caches sprites by stable skill ID.

## Human-proportioned player outfits (9 Nov 2026)

- Current Warrior/Archer prefabs replace KayKit with the already imported Quaternius UAL1 human mannequin/avatar and UAL1/UAL2 animation clips. CC0-1.0 license: Art/ThirdParty/Quaternius/UAL1/License.txt (existing source/checksums above); no new external assets or licenses.
- Original fitted armour, hood/cowl, tabard, pouches, quiver/arrows and deterministic material grain are authored by EmberdelveGrimClassVisuals and stored in Art/Models/GrimPlayers. Derived body meshes preserve the source skinning and split cloth/glove materials.
- Resources/Emberdelve/PlayerVisuals/{Warrior,Archer}.prefab now reference this human art. KayKit source FBXs and their provenance are retained as unused historical assets. The established EmberdelveClassVisuals build entry delegates to the current builder.
- Fixed class outfits; no claim of equipment-driven apparel, simulated cloth or photoreal scanned assets. Acceptance: Tasks/GRIM_PLAYERS_20261109.md.

## Unified whole-body source and first-person hands (9 Nov 2026)

- Supersedes the Quaternius mannequin body for both player classes; Quaternius remains the animation source. Innerscene's MakeHuman-derived male base: https://www.innerscene.com/tools/library/3d-parts/male-base-mesh-3d-person-rigged-a-pose-380e6d89 . CC0 dedication is present on the primary source page and embedded GLB copyright. Original Source.glb, LICENSE.md and SourceManifest.json are retained in Art/ThirdParty/Innerscene/Human; source SHA256 86c7cdfa989924b45e2e5110c131a4a4e6251ceb4e71246be68e468f3a32ca86.
- Tools/Convert-UnifiedHuman.py uses Blender4.3.2: one weighted body including eyes, 34,146 triangles, 163 bones and all30 finger bones. All12,862 hand/finger-weighted source vertices survive conversion at identical coordinates. Removed importer helper; reduced body topology, preserved hand detail, joined eye geometry. No source texture images or animations; project materials/clothing are original.
- EmberdelveUnifiedHuman maps native MakeHuman bones to Humanoid; both class prefabs and FP_Arms derive from Human.fbx. Class armour/headwear fitted to the body, closed boots added. Existing inventory portrait automatically uses the class prefab. No separate third-party FPS arm mesh imported. Fixed apparel still does not reflect individual equipped armour items.
- Source has a Unity metacarpal in-between-rotation warning during avatar configuration; the resulting avatar is valid/humanoid, all finger mappings resolve, and retargeted grips are visually reviewed. No claim of mocap-perfect fingers or photographic skin textures.

## REF-01 original synthesized sound (11 Nov 2026)

Runtime/Feedback/ProceduralAudio.cs generates the new swing, impact, stone-step, flame and wind clips locally from original deterministic synthesis code. No downloaded or extracted Dark and Darker audio was used. No third-party source/license or imported binary asset applies to these generated clips. Existing CC0 art and animation licenses remain unchanged. This is a prototype sound pass, not a claim of recorded Foley quality.

## Warrior armour metalwork (12 Nov 2026)
- Original project geometry generated by Editor/EmberdelveArmourDetail.cs: closed helmet crown, plate edge beads, small rivet domes. Four Warrior_*PlateEdges / Warrior_*Rivets mesh assets in Art/Models/GrimPlayers, attached under existing Cuirass/GreatHelm. Existing WornSteel and OldBrass materials reused; no downloaded/extracted assets.
- Bounded saved-prefab migration preserves existing unified Innerscene body, avatar, hands, animation and all original components. Future GrimClassVisuals builder invokes the same detail authoring. Visual evidence in Tasks/RENDER_READABILITY_20261112.md; this is fixed costume detail, not equipment-driven appearance.

Shoulder follow-up12 November: original authored Warrior_ShoulderL_Plated and Warrior_ShoulderR_Plated mesh assets derived from this project's existing shoulder shell coordinates, three closed thin bands, existing WornSteel material. No external mesh/texture imported. Original source assets retained. Evidence shoulder-plates-live-20261112; thickness accepted, broad arm-opening fit still unfinished.

Shoulder fit follow-up: same original meshes tapered radially from unchanged shoulder socket to72percent distal ring, preserving source, bones and existing materials. Live shoulder-fit-live-20261112 portrait/guard/attack/step inspected; no new external assets. Latest exact playable122427-208.

## Reference UI pass (13 November 2026)
- Cinzel heading font: Copyright 2020 The Cinzel Project Authors, SIL Open Font License 1.1. Original unmodified variable font from https://github.com/google/fonts/tree/main/ofl/cinzel (Cinzel[wght].ttf); bundled at Assets/Emberdelve/Resources/UI/Fonts/Cinzel.ttf with OFL.txt. Body text retains the existing built-in font.
- DungeonUiStyle generates original deterministic panel grain and simple metal borders in code. No Dark and Darker textures, UI assets or models imported. Reference screenshot from the publisher's Steam store is retained only under Logs/acceptance/reference-ui-20261113, outside player assets.

## Royale destination rooms and portal presentation (13 November 2026)
-20 original saved room prefabs in Prefabs/DungeonModules/Crypt and Infernal, generated by existing DungeonModuleContentBuilder with RoyaleRoomArchitecture. Reuse existing licensed stone/metal materials and project tomb/column/brazier helpers. No extracted original-game assets.
- Art/Generated/Royale/Infernal_Basalt, Infernal_Paving and Infernal_Vault are derived material instances from the existing project's stone materials with authored tints; original materials remain untouched.
- DescentPortalVisual generates original arch-rim, veil and downward-chevron meshes within the existing trigger bounds, with the existing Gate_Portal material and per-renderer property blocks. No new external art, particle system or global atmosphere.

Royale crypt dependency: Art/Generated/CryptFlame_BlueParticle.mat is a factory-derived copy of existing Torch_Flame.mat with blue tint; retained original shader/texture and source license. Required generated output alongside the20 crypt/infernal prefabs, not only Generated/Royale folder. No third-party game assets.

## Environment surface scale and import correction (13 November 2026)
- Reuses existing ambientCG Bricks100, PavingStones142 and Rock030 color/OpenGL normal/packed mask/AO maps, existing CC0 license and manifest. No new external or reference-game assets.
- Original DungeonWorldStone URP shader projects these maps in world space with signed normal blending, local-light/shadow/fog support and low-frequency tint variation. World_Stone and World_Infernal material assets live in Art/Generated/DungeonSurfaces; original Art/Materials files preserved.
- Wall texture period3m; paving7.14m, rock2.5m. Roughness response increased through lower smoothness multiplier, normal strength0.55-0.65, AO0.65. Six materials assigned to30 saved room prefabs without geometry/collision changes. Future room builders bind the same assets.
-166 importer metas copied from validated mirror to main:16 ambientCG2048,150 PolyHaven1024, trilinear/aniso8 instead of512/point. Main import/compile and shader diagnostic evidence: Logs/acceptance/environment-surfaces-20261113/main-import-check.json. Visual acceptance recorded in Tasks/ENVIRONMENT_SURFACES_20261113.md.

Room props/sky (13 November): reuses existing CC0 PolyHaven wooden_crate_02, wine_barrel_01 and wooden_bucket_01 meshes/materials. Original lathed ceramic vessel mesh and WeatheredClay material derive from project stone maps; procedural moon/craters/stars/clouds authored in MoonlitSky shader. No external downloads or original-game assets.

### Moonlit Ruins room shapes (21 November 2026)

Six original folded masonry meshes in `Art/Generated/RuinsWalls` share the existing `World_Stone_Wall` material. They provide recessed wall runs, collapsed crowns, weathered buttresses and two diagonal corners in each outdoor room. The original six meshes retain the physics surface. Six visual-only WeatheredMasonry derivatives add 3.5 cm three-segment bevels, weighted normals and a source geometry fingerprint. Nine outdoor prefabs use these surfaces; collider meshes, transforms and gameplay footprints remain unchanged. Inputs and the Blender reproduction recipe are in SourceArt/Environment/WeatheredWalls. No additional third-party source assets are required.

### Rustbound dungeon doors (21 November 2026)

The user-supplied `Meshy_AI_Rustbound_Gate_20261104_texture_fbx.zip` contains the grille FBX and albedo. Both are stored in `Art/Models/AI/Meshy/MapDressing/RustboundGate`. The original wood backing and arched masonry reuse the project's existing plank and dungeon stone materials. The saved `Resources/Emberdelve/DungeonDoor.prefab` is shared by the room catalogs.

### Moonlit Ruins courtyard trial (21 November 2026)

- `WallPanel` uses the user-supplied `Meshy_AI_Rustic_Timber_Panel_20261018_texture_fbx.zip` and keeps 20,621 imported triangles. [Medieval Modular Wall by thedude767](https://www.meshy.ai/pt-PT/posts/medieval-modular-wall-01a14fdc-ba6c-7127-b103-4ab391e6cb1e)
- `RuinedArches` uses `Meshy_AI_Ruined_Arches_20260818_texture_fbx.zip` and reduces 1,978,106 imported triangles to 36,982. [Ruined Wall by eromaxi](https://www.meshy.ai/fr/posts/ruined-wall-01a013e9-8c12-70a1-94a5-cb02fd7c85a8)
- `OvergrownArch` uses `Meshy_AI_Overgrown_Stone_Arch_20260627_texture_fbx.zip` and reduces 360,634 imported triangles to 26,998. [Overgrown Stone Arch by Yarpii](https://www.meshy.ai/posts/019f0b22-ecb5-7953-b714-e4125d20c9be)

Meshy publishes community models under CC0 1.0. The original archive names and download files are retained by the project owner. Each Unity prefab in `Art/Models/AI/Meshy/CommunityRuins` uses its own 2K albedo and normal maps plus a 1K metallic/smoothness map packed from the source maps. The trial appears once in `Crypt_Entry`. Its material finish is tuned for the courtyard lighting. [Meshy community license](https://www.meshy.ai/terms-of-use)

The courtyard brazier uses the 48,508-triangle FBX and 2K albedo from the user-supplied Meshy archive `Meshy_AI_A_stone_brazier_standing_on_a_small_20250502_texture_fbx.zip`. The carved bowl prefab in `Art/Models/AI/Meshy/CommunityRuins/StoneBrazier` uses an original fit of the existing stone column and reuses the authored flame and point light. Its in-game CarvedBowl mesh removes the fused round slab and caps all four feet while retaining the original UVs and material. The derived mesh uses 34,706 triangles.

The replacement 90 cm brazier pedestal uses an original 1,736-triangle ashlar mesh with staggered joints and modeled chips. The user-supplied reference guided the square cap and recessed masonry joints. Its material uses the original 2K edition of the same CC0 Rock030 set and the project-authored shrine fissure masks. The color, normals, AO and packed smoothness are 2048 x 2048. [Rock030 on ambientCG](https://ambientcg.com/view?id=Rock030)

## Equipment-driven armor models (13 November 2026)
- Original cuirass, boots and two cloaks, leather/cloth/metal textures and URP materials authored by EmberdelveItemArmorModels; saved in Art/Models/Equipment and Prefabs/Visual/Equipment. Inventory sprites are rendered from those same models. No external downloads or Dark and Darker assets.
- Graspwrap glove garment derives hand/wrist topology and skin weights from the existing Innerscene Human CC0 source listed above. Source mesh remains unchanged; garment clips the wrist and offsets the surface. One skinned renderer/two materials preserves finger bending, 25,883 triangles for both gloves. Other pieces use bone-attached meshes.
- ItemVisualCatalog connects five existing item definitions to these cosmetic assets; wearer, portrait, first-person hands and ground loot reuse the same references. No new stats or item licensing dependencies.

## Handheld pitch torch model and fire (13 November 2026)
- Original project mesh PitchTorchBody (6,606 triangles, four submeshes) from EmberdelveHandTorchModel; wood grain, leather pores, hammered iron, cloth weave/ember emission textures generated deterministically. Saved Art/Models/HandTorch, no external downloads or copied reference-game assets.
- Original FlameTongue/SmokeWisp masks and HandTorchFire URP shader; four bounded particle systems authored by EmberdelveHandTorchFlame. Held Viewmodel_PitchTorch and unlit ground IV_pitch_torch share the body mesh. Inventory icon rendered from that body.
- Room/brazier particle materials and existing licensed assets unchanged. This item-only pass retains the authored point-light intensity/range and the existing weapon/utility rules.

Steel cuirass correction (2026-11-13): original formed breast/backplate, rolled rims, leather closures and three waist lames authored in EmberdelveItemArmorModels; dedicated original TemperedSteel/BurnishedSteelEdges textures/materials. Replaces leather visual for stable boiled_cuirass ID; no external model or Dark and Darker asset used. Evidence Tasks/STEEL_CUIRASS_20261113.md.

Reference flame correction (2026-11-13): original HandTorchVolume URP shader and generated64^3 seamless 3D value-noise asset; continuous fire volume, auxiliary smoke/sparks. User photo used as visual direction, not imported/extracted as texture. No external assets. Evidence Tasks/TORCH_REFERENCE_FLAME_20261113.md.

## Ground loot models (13 November 2026)
Original treasure and ammunition meshes authored by EmberdelveLootModels: struck coins, reliquary, gem-set ring, chain/bead pendant, fletched arrows/bolts and bound bundles. Original carved bow/crossbow ground shapes supplement existing licensed melee/shield meshes from WeaponViewModelSet. Original small procedural surface textures/materials saved under Art/Models/Loot; cosmetic prefabs under Prefabs/Items/Loot. No reference-game assets or external downloads. Existing potion/bandage and six armor/torch representations preserved. Coverage and visual evidence tracked in Tasks/GROUND_LOOT_MODELS_20261113.md.

## Cinder Warden (17 November 2026)

The original user-requested Meshy cyclops and separate ironbound club were transferred into the development checkout from `DakaDaka`. The [source manifest](../SourceArt/AI/Meshy/CinderWarden/manifest.json) records the generated reference images, Meshy terms, geometry and texture settings, source hashes and four task IDs. The original Meshy jobs used 44 credits. Integration reused those outputs.

The body has 27,520 triangles and the rigid club has 6,731. The body holds a fixed weapon grip with the club parented to the right hand. Pipe smoke reuses the project's SmokeWisp material. Five Meshy motions and the existing Quaternius `Death01` clip drive the Humanoid. The combat visual is `Resources/Emberdelve/NetEnemyVisuals/EnemyVisual_CinderWarden.prefab`. The source visual and URP materials are under `Art/Models/AI/Meshy/CinderWarden`.

## Meshy warrior and first-person arms (17 November 2026)

The user requested a Meshy player character, bare first-person hands, textures, rigging and animations, then rejected the first physique and asked for a more muscular and brutal warrior. The revised body uses a nano-banana-pro reference, meshy-t2.5 geometry with 31,484 triangles and meshy-7 PBR textures. Base color and normal maps are 4096 pixels. Metallic and roughness maps are 2048 pixels. The body has a Unity Humanoid rig at 1.9 metres with all 30 finger joints.

That Smart Topology prototype is superseded by the user-approved Meshy 7.1 detailed generation: 314,833 triangles, open separated fingers, 4K color/normal and 2K metallic/roughness maps. Both paid Meshy rig results omitted fingers despite verified requests. Tools/Repair-MeshyHeroHands.py adds 30 finger joints locally, weights them, blends webbing and keeps weights identical across split UV vertices. It preserves the generated surface and UVs. CryptWarriorHero.fbx and its derived 50,560-triangle first-person arms now use the same source and Hero texture set. The older generated revisions remain as rollback/provenance sources.

Both active rigs use Unity Humanoid. Existing generated idle, sword slash, shield push, archery, torch, walking and running clips are reused through Humanoid retargeting. The first-person equipment driver retains gameplay trajectories and uses rig-specific elbow alignment, support-grip roll and authored finger rotations. The native rotations are selected only by the new arms prefab.

Art/Models/Meshy/CryptDelver/manifest.json records task IDs, source checksums, all source revisions, derivation and measured charges. Charges were 38 credits initially, 29 for the muscular prototype, 44 for the approved detailed generation and 5 for the approved rig-only retry: 116 total, verified balance 979. The user's connected Meshy account terms govern use of these commissioned generations. Blender is used as a local authoring tool; no external character models or textures were imported.

Visual review and focused verification are recorded in Tasks/MESHY_CHARACTER_20261117.md. Unity maps all 30 repaired finger joints. The detailed revision's visual acceptance remains with the user.

## Cinder Warden boss visual (17 November 2026)

Meshy generated the original cyclops and separate ironbound club from the two project-created reference images. The [source manifest](../SourceArt/AI/Meshy/CinderWarden/manifest.json) records the references, Meshy terms, settings, source files, SHA-256 hashes and four task IDs. Meshy billed 44 credits.

The Unity prefab is `Assets/Emberdelve/Art/Models/AI/Meshy/CinderWarden/CinderWarden.prefab`. The 3.3 m body uses 27,520 triangles and a Humanoid avatar. The 6,731-triangle club follows the right wrist, with a fixed grip authored on the skin. The pipe smoke emitter follows the head and reuses the existing SmokeWisp material. The controller has Idle, Walk, Run, Attack and Hit states. URP material variants use Meshy's albedo, normal, and metallic/smoothness maps.

Unity 6000.2.5f1 imported the avatar and sampled all five clips in the Editor. Idle and attack renders, including the grip and smoke, are in `Logs/acceptance/cinder-warden`. This is a standalone visual prefab. Scene placement, AI and hitboxes are separate work.

## Meshy map dressing (20 November 2026)

The four chest meshes and the wall torch came from five FBX archives supplied by the project owner. The two praying statues and votive candles came from original Meshy tasks for this project. The [source manifest](../SourceArt/AI/Meshy/MapDressing/manifest.json) records the archives, original file hashes, task IDs, Unity files and Meshy terms. Archive exports did not carry an asset-specific license.

The prefab sources live under `Art/Models/AI/Meshy/MapDressing`. Each chest uses the existing loot variant, interactable and colliders, with the Hidden chest added as a deterministic Ember visual variant. The four chests have separate animated lids and fitted interior pieces. The archived wall torch uses the existing scene flame and light, with the shared sconce prefab also placed on modular room jambs. Shrine altars carry both statues and a votive tray. Runtime art uses 1K URP textures and compact meshes; the recorded geometry counts exclude the simple chest interiors.

The two shrine statues share an original procedural 1K aged limestone material with albedo, normal and packed occlusion/smoothness maps. The existing dungeon stone shader projects the texture at a consistent scale across the carved figures. The votive stand now has three animated fire volumes attached to the original wicks and one warm flickering light. The [source manifest](../SourceArt/AI/Meshy/MapDressing/manifest.json) records the authored texture and prefab hashes.

Shrine weathering uses per-vertex raycast occlusion and localized moss seeds on the two Meshy statues. Two original 1K maps give the moss fine mottling and the stone branching fissures. Each plinth has three small geometry chips, and the standing figure retains a diagonal head cut with a closed faceted interior. The sculpture masks are enabled on the shrine stone material. Evidence is in `Logs/acceptance/shrine-surface-detail`.

Modular raid layouts place the 2.10 m kneeling figures and 2.22 m standing figures on the shrine altars and along the walls of entrances and ossuaries. Extraction rooms use another lit votive stand on existing masonry. The same floor plan and chest spawner place the four archive chest appearances, alternating the two Ember visuals by their authored spawn order. Placement evidence is in `Logs/acceptance/map-dressing-placement`.

## Original game sound bank (22 November 2026, superseded)

Historical first pass `bd3975ba0` generated151 mathematical effects across49 cue families without external recordings. The user rejected its synthetic timbre. That implementation and its original provenance remain in Git history and `Tasks/GAME_AUDIO_20261122.md`; the old generator is now retired. The active bank and its third-party recording licenses are described below. The original melodic48s music remains unchanged.

## Recorded Foley replacement (22 November 2026)

The synthetic effect bank above was rejected for timbre and is superseded by edits of CC0 recordings. The existing original melodic music remains unchanged. Selected source files, source URLs, authors, source hashes and license evidence are retained under `SourceArt/Audio/RecordedFoley/`; the runtime bank manifest maps each output to its original recording and selected segment. `Tools/Audio/BuildRecordedFoley.py` reproduces the bank. Archive downloads stay in ignored review evidence, not runtime assets.

Sources: Vehicle's [weapons/apparel](https://opengameart.org/content/fantasy-weapons-and-apparel-sfx-library) and [accessories](https://opengameart.org/content/fantasy-accessory-sfx-library); [Fantozzi footsteps](https://opengameart.org/content/fantozzis-footsteps-grasssand-stone); [HaelDB nonverbal performances](https://opengameart.org/content/male-gruntyelling-sounds); [qubodup doors](https://opengameart.org/content/door-open-door-close-set); [Wareya RPG Foley](https://warsong.pages.dev/tags/sounds/); [Independent.nu impacts](https://opengameart.org/content/37-hitspunches); and [Ben Jaszczak/Brian Nelson weapon recordings](https://opengameart.org/content/medieval-sound-effects-weapon-textures). CC0 is the chosen offered license, including pages that offer alternative licenses. No dialogue, voice cloning, paid generation or reference-game extraction is used. Magical and interface cues are designed from these recordings, not advertised as literal real-world events. Runtime and review evidence: `Tasks/NATURAL_AUDIO_20261122.md`.


## Softer stairs and landings — 22 November 2026

The existing project-owned traversal kit now has 70 shared, dimension-specific visual meshes under `Art/Generated/WeatheredTraversal`. A 25 mm bevel softens stair noses; a 40 mm bevel softens decks, parapets and retaining edges. Blender works at physical size and exports normalized boxes with corrected normals, preserving all saved placement and collision. The 30 room/theme prefabs replace 2,802 MeshFilter references only. Original flat tread heights, bounds, materials, doors and cache mechanics remain intact.

The dimensions manifest and reproduction guide live in `SourceArt/Environment/WeatheredTraversal`; scripts are `Tools/Art/BevelTraversal.py` and the `TraversalSurfaces.cs` bridge. Intermediate mesh JSON and numeric reports are reproducible; committed Unity meshes retain their GUIDs on refresh. This is an original derivation of the existing boxes, with no external game asset imported. The local Dark and Darker package audit is construction reference only: `Tasks/DARKER_MODULE_REFERENCE_20261122.md` records exact evidence and unresolved dimensions.


## Module joins and depth repair — 22 November 2026

The user's screenshot rejected the prior visual acceptance. `ArchitecturalJoinRepair` replaces column-fragment wall tiling with closed rounded masonry using the existing project stone materials, aligns downloaded arch springlines with their supports, and fits boundary render envelopes to each module's own footprint. Original downloaded models remain intact. `StairSurfaceSeparation` derives non-overlapping rounded treads and the lower exposed retaining-wall surface from the existing traversal assets. New shared derivatives are saved under `Art/Generated/ArchitecturalJoins` and `Art/Generated/TraversalSeparation`.

The existing Meshy door leaf remains; its frame uses the actual authored arched aperture and the hinge mounts clear of the wall face. The project-owned 92-triangle `ArchitecturalJoins/DoorBacking_ApertureInset5mm.asset` follows that aperture with uniform 5 mm clearance; the inherited original backing asset is untouched. Reproduce saved repairs through `Tools/Art/ModuleJoins.cs` (Apply/Doors/Validate) with the explicit primary Unity project. Builders invoke the same repairs. No new external downloads or model-generation tasks were used. Evidence, the two explicitly removed unsupported overhead collider ornaments and verification limits: `Tasks/MODULE_JOINS_20261122.md`.

## Differentiated layout kit and lobby — 22 November 2026

`DungeonLayoutFamilies` builds ten physical circulation families in the three existing catalogs, grounded in the read-only local reference evidence in `Tasks/DARKER_LAYOUT_EVIDENCE_20261122.md`. `Art/Generated/LayoutFamilies` contains original shared rounded masonry derived from the existing project rounding code. Existing downloaded pillars/crowns, Poly Haven tables/barrels and authored stone sarcophagi supply the props; no external game meshes were copied. Original downloads remain intact.

The source crown receives a separate seated lower-course derivative plus physical bearing caps fitted to actual rendered supports. The generic chest-for-sarcophagus adapter is removed. Both lobby and raid braziers reuse `CommunityRuins/StoneBrazier/StoneBrazier.prefab`; raid bowls retain uniform proportions and sit on actual imported column tops, with the rim at the existing flame height. Lobby uses the existing weathered pedestal. Four stale saved lobby lanterns are migrated to the existing Meshy WallTorch source. Exact scope and final proof: `Tasks/LAYOUT_FAMILIES_20261122.md`.


Final join derivatives also include `Art/Generated/PitJointBacking`: widened versions of the two existing lower-floor meshes and two closed8-vertex stone wedges beneath recessed stair joints. They close bevel/tread views into the background, preserve floor height and all gameplay components, and retain source GUID/dependency hashes for regeneration. `Socket_UpperInfill` and seated arch capitals are original closed masonry derived from the existing project kit, not imported reference-game geometry.


## Layout-aware furnishing (22 November 2026)

Reuses the existing licensed CC0 PolyHaven WoodenTable_01, wooden_stool_01, wooden_bookshelf_worn, wooden_crate_02, wooden_bucket_01, wine_barrel_01 and wooden_candlestick imports, plus existing owner-supplied Meshy StandingShrine and VotiveStand. All original manifests/materials/source meshes remain intact. Authoring fits uniform scale to actual imported bounds and gives furniture physical collision; no new source download or generation. GothicCabinet_01 stays lobby-only. The existing RustboundGate leaf keeps its original Meshy faces; its opaque wooden backing becomes a joined thin core with a deeper perimeter rebate inside the leaf. The pale industrial rack was rejected during this pass. See Tasks/LAYOUT_FURNISHING_20261122.md for source-reference evidence and acceptance status.
