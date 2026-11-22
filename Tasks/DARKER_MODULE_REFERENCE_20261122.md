# Local Dark and Darker module reference — 22 November 2026

The user explicitly authorized inspecting the existing local extracted/decompiled game as a construction reference. This report records verified observations and their limits. No source assets, binaries, meshes or textures were copied into this project. No decryption, downloads, Unity sessions, builds or tests were performed.

## Result and confidence

The extracted packages provide useful evidence of distinct room families, reusable architectural kits, connector placements and secret mechanisms. They do **not yet provide verified physical room dimensions, stair rise/run, entrance coordinates or a traversable connection graph** in a decoded form. Do not describe our 36 m modules or current stair dimensions as matching Dark and Darker on this evidence.

The clearest actionable comparison is our repeated circulation template: all 30 saved module prefabs contain `Western_Gallery` and `Undercroft_Floor`, and the same builder adds these in the same local positions for every room. The reference has distinct bridge, pit, corridor, tower and subdivided-room families with materially different named components. A bounded next pass should vary internal circulation while preserving our validated external module contract.

## Source inventory and limits

Inspected local roots:

- `C:/Users/yrapa/Downloads/DarkAndDarker-extracted`
- `C:/DarkAndDarkerTest/Dark and Darker Test` — installed packaged client; no decoded map transform export identified.

The extracted root contains 123,133 `.uheader` files, 123,133 `.uexp` files, 13,854 `.ubulk` files and 62 `.ushaderbytecode` files. No JSON, CSV, OBJ or FBX exports were present in that inventory. Readable package names/imports and length-prefixed actor-label strings are present; these are not wholly unreadable encrypted blobs. Object properties remain binary. No matching installed package reader/schema was identified during the bounded tool search.

For the exact paths below, define these two literal prefixes:

- **P** = `C:/Users/yrapa/Downloads/DarkAndDarker-extracted/pakchunk2802-Windows_0_P/DungeonCrawler/Content/DungeonCrawler/Maps/Dungeon/Modules/Crypt/`
- **B** = `C:/Users/yrapa/Downloads/DarkAndDarker-extracted/pakchunk2802-Windows/DungeonCrawler/Content/DungeonCrawler/Maps/Dungeon/Modules/Crypt/`

Both base and patch versions were inspected for the six sampled modules. Counts below agree between them. Patch FourWayConnect, CliffBridge and LargeRoomPit have different byte lengths, so the patch files are the primary evidence, not an assumption that every base file is current.

## Connector and mechanism evidence

The counts below are exact occurrences of null-terminated ASCII labels with a preceding little-endian signed length equal to the string length including its terminator. This filters out arbitrary fragments from binary data. They are **serialized label occurrences**, not a count of enabled, unlocked or mutually connected doors.

Counted labels share `GameSpawner_Id_Spawner_New_Props_` followed by `CryptModuleConnector`, `CryptSecretWallDoor`, or `WallLever`.

| Exact file relative to P | Bytes | Connector labels | Secret-wall labels | Wall-lever labels |
|---|---:|---:|---:|---:|
| `Center/Vault/Crypt_Vault_A.uexp` | 512849 | 8 | 0 | 0 |
| `Center/FourWayConnect/FourWayConnect_A.uexp` | 414587 | 8 | 3 | 6 |
| `Center/HBridge/HBridge_A.uexp` | 412104 | 8 | 0 | 2 |
| `Center/CliffBridge/CliffBridge_A.uexp` | 430562 | 8 | 4 | 5 |
| `Center/FourRooms/Crypt_FourRooms_A.uexp` | 241155 | 8 | 0 | 0 |
| `Center/LargeRoomPit/Crypt_LargeRoomPit_A.uexp` | 490444 | 8 | 0 | 0 |

Reproducible byte offsets of the first ASCII character, relative to each `.uexp`:

- Vault connector labels: `0x815d, 0x8201, 0x82a5, 0x83e3, 0x8487, 0x852b, 0x85cf, 0x8673`.
- FourWayConnect connector labels: `0x1c7c, 0x2fb9, 0x319a, 0x3677, 0x3d29, 0x43db, 0x4a8d, 0x50bf`; secret-wall labels: `0x4dad, 0x4e50, 0x54fb`.
- CliffBridge connector labels: `0x5269, 0x53ac, 0x5450, 0x54f4, 0x5598, 0x56d6, 0x577a, 0x5c76`; secret-wall labels: `0x5964, 0x5aa0, 0x5db1, 0x5f86`.

**High confidence:** eight connector-labelled serialized placements exist in each sampled file; room-specific mechanism populations differ. **Not established:** two entrances per side, connector elevations, active-door selection, lever-to-door relationships, or whether any particular routes form a walkable loop. Reading those conclusions from the counts would overstate the evidence.

## Room families and vertical components

Actual module directories under `B/Center/` include `Atrium`, `BlindfallPit`, `CenterTower`, `CliffBridge`, `ComplexHalls`, `FourRooms`, `FourWayConnect`, `GreatWalkway`, `HBridge`, `LargeRoomPit`, `MadCorridors`, `Maze`, `Ramparts`, `TheCage`, `TheMiniWheel`, `ThePit`, `Tomb_Center`, and `Vault`.

Actual directories under `B/Edge/` include `Armory`, `Barracks`, `Catacomb`, `Chapel`, `Cistern`, `Connector_Trap_01`, `Connector_Trap_02`, `CorridorofDarkPriests`, `CrossRoad`, `GuardPost`, `Hallways`, `HighPriestOssuary`, `LowPyramid`, `Prison_01`, `Sewers`, `SingleLogBridge`, `SkeletonPit`, and `UndergroundAltar`.

The sampled architecture exports contain differentiated component labels, not merely different room names:

- `P/Center/HBridge/HBridge_A.uexp`: repeated `SM_Stair_Tileable_*`, stair-balustrade components, ruined walls, door arches, and wooden/double-door spawner labels.
- `P/Center/LargeRoomPit/Crypt_LargeRoomPit_A.uexp`: `SM_Wooden_Stair_01`, `SM_Wooden_Stair_2`, ruined wooden floor/support pieces and jail-door labels.
- `P/Center/FourRooms/Crypt_FourRooms_A.uexp`: `SM_Bridge_12` through `SM_Bridge_19`, floor and dividing-wall pieces. This proves named bridge components, not their exact elevations or walkability.
- `P/Center/Vault/Crypt_Vault_A.uheader`: readable names include `Ceiling_2F`, `SM_Floor_Middle`, floor segments and tileable stairs. Its imports include `BP_DownStair`, fixed-stair map/portcullis objects and `BP_FloorPortal`; do not confuse these floor-transition systems with measured internal stair geometry.

Reusable stair mesh packages are available for a future schema-aware inspection at:

`C:/Users/yrapa/Downloads/DarkAndDarker-extracted/pakchunk602-Windows/DungeonCrawler/Content/DungeonCrawler/Environment/Dungeon/Crypt/`

- `Dungeon/Architecture/Stairs/SM_Stair_01.uheader` and `.uexp`
- `Dungeon/Architecture/Stairs/SM_Stair_Tileable_01.uheader` and `.uexp`
- `Chapel/Meshes/SM_Stair_Tileable_03.uheader` and `.uexp`
- `LargeRoomPit/Meshes/SM_Wooden_Stair_01.uheader` and `.uexp`

`SM_Wall_8m_*` and `SM_Archway_8x12_05` names also occur in the kit. These names suggest nominal kit sizes, but do not verify exported bounds, units, per-instance scale or module footprint. No metre measurements were inferred by scanning untyped floating-point bytes.

One module-definition reference is explicit: `C:/Users/yrapa/Downloads/DarkAndDarker-extracted/pakchunk500-Windows/DungeonCrawler/Content/DungeonCrawler/Data/Generated/V2/Dungeon/DungeonModule/Id_DungeonModule_Crypt_Vault.uheader` references the Vault `_A`, `_D`, `_HR_D`, `_S` packages and `Data/Art/DungeonModuleMapImage/Crypt/Crypt_Vault`. The split is verified; the complete semantic meaning of each suffix was not decoded here. The minimap texture is a useful local next reference once a reader is available, but it is not itself a metre scale.

## Comparison with our construction

These dimensions come from our source, not the reference game:

| Our current property | Source and interpretation |
|---|---|
| 36 × 36 m module | `Runtime/Level/DungeonModulePlan.cs:34` has 18 × 18 cells; `Editor/DungeonModuleContentBuilder.cs:15` defines half-width 18 m with two-metre cells. |
| Four centered, ground-level external sockets | `DungeonModuleContentBuilder.cs:184–192`: north/east/south/west at `(0,0,±18)` / `(±18,0,0)`. Nominal opening width 4 m; jamb geometry narrows the clear width. |
| Same east depression in every module | `DungeonRoomExpansion.cs:13–24`: lower floor at −1.5 m, two 5 m-wide stair flights. Each flight is ten 0.15 m rises with 0.30 m runs: 3 m horizontal travel. |
| Same west upper alcoves in every module | `DungeonRoomExpansion.cs:31–48`: two separate 5 × 5.75 m decks at +3.9 m; 4.5 m-wide stairs, 26 rises / 7.8 m run. End parapets close the inner ends; these are separate raised side routes, not a continuous west upper loop. |
| Additional diagonal terraces in the base architecture | `CryptRoomArchitecture.cs:21–25,51–69`: 1.8 m or 3.6 m height, 3.6 m-wide stairs, 0.15 m rise / 0.30 m run, solid deck supports and rear parapets. This builder is used through `DungeonModuleContentBuilder.cs:273`; destination themes also have their own central features. |
| Repetition across all destinations | `DungeonModuleContentBuilder.cs:195,224` calls the shared floor/dress methods unconditionally. The ten specs are also generated for destinations 2 and 3. Read-only prefab search found **30 files with `Western_Gallery` and 30 with `Undercroft_Floor`**. |

Our stair ratio is 1:2, approximately 26.6°. The reference stair ratio remains unknown. We should not change it to a guessed steeper value merely because a room name includes “pit” or “tower.”

## Bounded next layout variations — proposal only

Keep the current footprint, four external sockets, catalog order, secret authority and current navigation constraints for a first pass. Author three distinct internal silhouettes with existing project assets:

1. **Divided hall:** replace the universal west/east additions in one hall variant with two offset internal wall screens and two routes around them. Reference basis: `FourRooms`, `ComplexHalls`, `MadCorridors`; the exact layout would be original.
2. **Pit and bypass:** use the existing safe −1.5 m depression as a central/localized feature with two approaches and a continuous ground-level perimeter bypass. Reference basis: `LargeRoomPit`, `BlindfallPit`, `ThePit`; retain measured player clearance rather than guessing reference dimensions.
3. **Raised crossing:** replace isolated upper alcoves in one bridge variant with a connected raised route and two separated stair approaches. Keep solid supports or geometry that the current single-surface navigation can represent; do not introduce overlapping independently walkable floors without a navigation design change. Reference basis: `HBridge`, `CliffBridge`, `GreatWalkway`.

This is a proposal, not an implemented change or proof that these paths match the reference. Eight-connector external modules would require a separate socket/planner/network scope; their placement cannot be reconstructed from label counts alone. Exact reference dimensions and actual loops remain blocked on decoded transforms/bounds or a trustworthy calibrated map inspection, not on encryption demonstrated by this audit.
