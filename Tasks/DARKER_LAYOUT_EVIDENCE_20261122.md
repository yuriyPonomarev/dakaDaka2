# Differentiated crypt layouts — local package evidence, 22 November 2026

Scope: read-only inspection of the existing extracted reference game, plus our traversal tests. No source meshes/textures copied, downloads, decryption, Unity runs or tests. Builds and implementation remain coordinator-owned.

## Stronger evidence than directory names

This extends `DARKER_MODULE_REFERENCE_20261122.md`. For each package below, the local name table and 72-byte export records were read directly from its `.uheader`. A length-prefixed actor-label occurrence in `.uexp` was located inside the corresponding export's declared serialized range, then linked to a child `StaticMeshComponent` export by its outer-object index. These are instantiated architectural actor/component records, not merely strings in an import list.

Literal source root **P**:

`C:/Users/yrapa/Downloads/DarkAndDarker-extracted/pakchunk2802-Windows_0_P/DungeonCrawler/Content/DungeonCrawler/Maps/Dungeon/Modules/Crypt/Center/`

All files below exist under P; their matching `.uheader` files supplied object relationships. Labels are author labels, not measured shape classifications. Counts below use case-insensitive substrings among labels belonging to actors with static-mesh-component children. In particular, **“stair” includes stair balustrades and must not be called a number of stair flights**. “Bridge” includes parapets. No enabled-state or walkability claim follows from an export's presence.

| Family / exact `.uexp` | Bytes | Linked stair labels | Linked bridge labels | Linked wall labels | Linked floor labels |
|---|---:|---:|---:|---:|---:|
| `CenterTower/CenterTower_A.uexp` | 1,066,378 | 29 | 0 | 461 | 104 |
| `ComplexHalls/ComplexHall_A.uexp` | 374,333 | 12 | 7 | 141 | 154 |
| `Maze/Maze_A.uexp` | 384,000 | 8 | 0 | 193 | 108 |
| `TheCage/TheCage_A.uexp` | 489,999 | 7 | 4 | 136 | 149 |
| `GreatWalkway/Crypt_GreatWalkway_A.uexp` | 301,129 | 4 | 4 | 133 | 109 |
| `FourRooms/Crypt_FourRooms_A.uexp` | 241,155 | 0 | 8 | 96 | 9 |
| `HBridge/HBridge_A.uexp` | 412,104 | 37 | 5 | 194 | 93 |
| `LargeRoomPit/Crypt_LargeRoomPit_A.uexp` | 490,444 | 5 | 0 | 90 | 42 |
| `CliffBridge/CliffBridge_A.uexp` | 430,562 | 19 | 5 | 225 | 155 |
| `MadCorridors/Crypt_MadCorridors_A.uexp` | 256,153 | 16 | 0 | 162 | 125 |

Reproducible examples, with zero-based export index and `.uexp` label offset:

- CenterTower: actor 2657 `Stairs11` at `0x443b2`; actors 2658/2659/2660 label `Stairs14`/`Stairs16`/`Stairs17` at `0x443f3`/`0x44434`/`0x44475`. The header name table also references dedicated `CenterTower/meshes/Stairs/Stairs08`, `Stairs/Railing08`, `Floor_center`, `WallSlice` and round-tower kit pieces.
- ComplexHalls: actor 545 `SM_Stair_Tileable_01` at `0x17572`, actor 549 `SM_Stair_Tileable_03` at `0x1768f`, actor 553 `SM_Stair_Tileable_6` at `0x177aa`. Stair and bridge-labelled actors coexist with a large floor/wall assembly.
- Maze: actor 1621 `SM_Stair_Tileable_01` at `0x1cb39`, actor 1651 `SM_Stair_Tileable_03` at `0x1d361`. Separately counted strict labels include three `CryptSecretWall` substrings and ten `WallLever` substrings; these are not a decoded mechanism graph.
- TheCage: actors 728–734 include seven cage-labelled static actors. `SM_Cage8` occurs at `0x11629`. Actor 819 `SM_Stair_02` is at `0x12fde`; actor 1252 `GuardPost_Stair` at `0x1a4be`. This supports a cage/guard-post vocabulary, not a claimed exact cage plan.
- GreatWalkway: actor 858 `SM_Stair_01` at `0x11961`, actor 936 `SM_Stair_2` at `0x12e4e`, and `SM_Stair_3`/`SM_Stair_4` labels at `0x139ee`/`0x13cdc`. Bridge-parapet actors include export 925 at `0x12b60` and export 940 at `0x12f5a`.
- FourRooms: actor exports 701–708 correspond to `SM_Bridge_12` through `SM_Bridge_19`, labels `0xf756` through `0xf939`; linked component exports are 1321–1328. This is eight actual bridge-labelled actor/component pairs, with no stair-labelled static actor in this package.
- HBridge: actors 591–594 `SM_Stair_Tileable_3` through `_6` at `0x151f2`, `0x1523e`, `0x1528a`, `0x152d6`; stair-balustrade labels occur at `0x15322` and `0x1550f`.
- LargeRoomPit: actors 1196/1245/1290 label `SM_Wooden_Stair_01`, `SM_Wooden_Stair_2`, `SM_Wooden_Stair_3` at `0x2abb5`, `0x2b904`, `0x2c50f`; actor 1323 labels `SM_Stair_Tileable_44` at `0x2cdce`. Ruined wooden floors/supports and a wooden-ramp mesh reference complement the stair actors.
- CliffBridge: actor exports 837 and 838 label `SM_Tower_8Sided_Staircase_01` and `SM_Tower_8Sided_Staircase_2` at `0x14bee` and `0x14c43`; bridge actor 911 labels `SM_Bridge_6` at `0x15fd5`.
- MadCorridors: actors 803/814/837/848 label `SM_Stair_Tileable_5` through `_8` at `0xf61c`, `0xf913`, `0xff3f`, `0x10233`; further `_9` through `_12` labels occur at `0x10816`, `0x10b0a`, `0x110ec`, `0x113e3`.

## FourRooms placement evidence: structurally linked, interpretation provisional

The FourRooms header contains 503 names. Its export-map range is `[28848,161616)`, exactly 1,844 records of 72 bytes. Actor 701 has serialized range `[63294,63363)` and label `SM_Bridge_12`; its component child 1321 has range `[123951,124390)`. The other seven bridge components immediately follow, each with the same 439-byte payload length.

Each of these eight matched component records has a nine-double block at payload offset 245. It gives the following values, rounded here for readability:

| Actor label | First three doubles | Next three doubles | Final three doubles |
|---|---|---|---|
| `SM_Bridge_12` | 150, 810, -10 | 0, -90.00012, 0 | .75, .80000001, .80000001 |
| `SM_Bridge_13` | -140, 810, -10 | 0, -90.00012, 0 | .75, .80000001, .80000001 |
| `SM_Bridge_14` | 799.99908, 150.00098, 0 | 0, .000065, 0 | .75, .80000001, .80000001 |
| `SM_Bridge_15` | 789.99969, -139.99890, -10 | 0, .000065, 0 | .75, .80000001, .80000001 |
| `SM_Bridge_16` | -139.99756, -819.99561, -10 | 0, 90.00024, 0 | .75, .80000001, .80000001 |
| `SM_Bridge_17` | 150.00293, -819.99463, -10 | 0, 90.00024, 0 | .75, .80000001, .80000001 |
| `SM_Bridge_18` | -789.99951, -159.99963, 0 | 0, -179.99977, 0 | .75, .80000001, .80000001 |
| `SM_Bridge_19` | -790.00098, 130.00067, -10 | 0, -179.99977, 0 | .75, .80000001, .80000001 |

The repeated block shape, parent links, same mesh-actor family, four quarter-turn values and constant final triple are strongly consistent with location/rotation/scale and four paired bridge placements around the centre. **The property schema was not available, so that interpretation remains provisional.** These are not yet verified world transforms, metre dimensions, entrance locations, bridge spans or walkable links. The raw evidence is materially stronger than unrelated float scanning, but must not be advertised as a complete decoded plan.

## Original adaptations suggested by the evidence

These are DakaDaka designs, not reconstructed reference rooms. Keep our accepted external socket/footprint contract and author dimensions for our controller and navigation.

1. **Four chambers / paired passages:** a central dividing cross or offset screens creates four spatial chambers; pair passages around a central obstacle. FourRooms supplies instantiated bridge pairs and the provisional four-sided placement pattern. Provide multiple lateral transitions instead of cloning a west gallery.
2. **Tower island:** a substantial central raised mass with a surrounding ground loop and two deliberately distinct stair approaches. CenterTower supplies numerous dedicated stairs/railings plus round-tower/floor kit references. Avoid introducing independently walkable stacked floors while navigation stores only one surface per XZ cell.
3. **Long raised walk:** one dominant longitudinal raised route with approaches at both ends, separate lateral ground bypasses and broad sightlines. GreatWalkway supplies four stair actors and bridge-parapet placements; exact alignment remains original.
4. **Pit and broken-timber bypass:** localize a depression near the centre, use two separated descents, and preserve a ground-level bypass; existing project timber can distinguish its edge/support silhouette. LargeRoomPit supplies three wooden-stair actors and ruined wooden floors/support/ramp references. No reference drop depth is inferred.
5. **Complex split halls:** two offset partitions split and then reconnect the route, with one modest raised connector. ComplexHalls supplies instantiated stairs and bridge-labelled components with many walls/floors. This changes visibility and path choices rather than merely shifting decoration.
6. **Maze / lever pocket:** staggered opaque screens create short turns, a return loop and a discrete optional mechanism pocket. Maze supplies many wall actors, stair actors and serialized lever/secret-wall labels. The lever topology would be authored with existing authoritative doors.
7. **Cage court:** a fenced or barred central enclosure with circulation outside and an offset guard-post ascent. TheCage supplies seven actual cage actors and GuardPost_Stair. Preserve usable controller clearance and readable access.
8. **Cliff bridge / opposing platforms:** two separated high platforms linked by a dominant crossing or ground-level causeway, with distinct approaches. CliffBridge supplies two eight-sided staircase actors and bridge actor records. Our single-surface navigation means any true over/under crossing requires separate design, not a cosmetic bridge over another active route.
9. **Mad corridors:** a low, winding wall-dominated circulation family with several shallow stair transitions and interrupted sightlines. Its sixteen stair-labelled static actors and 162 wall-labelled actors distinguish the kit population from FourRooms and GreatWalkway, but do not establish the path shape by themselves.

A practical differentiated set can select six to eight of these, assign them across the ten module specs, and keep theme materials independent from circulation. Merely rotating the same west-gallery/east-undercroft construction would not satisfy the request.

## Focused proof changes suggested by existing code

`Assets/Emberdelve/Tests/EditMode/Level/ExpandedRoomTraversalTests.cs` currently instantiates only `Crypt_Hall_02` in each of the three themes. All assertions are hardcoded to the universal template:

- Controller routes use X = -13 for gallery and X = 13.5 for descent; Y extrema require >3.8 or <-1.35; endpoints assume the old Z lanes.
- Large-enemy doorway proof uses west-side X = -16.8, Z = 0, then a 5.3 m eastward capsule cast.
- Navigation targets cell `(2,6)` / `(2,11)` at exactly +3.9 m, starting near the west north/south edges.

Keep these checks for a family that actually retains those routes, rather than weakening the heights or pretending every family has a gallery. Add explicit route-case data for the newly authored families: prefab, waypoints, expected height extrema and endpoint. Walk every declared stair route both ways without jumping; a detour route should traverse multiple XZ waypoint segments, not a single Z line. Check actual family feature geometry rather than names alone.

For each distinct geometry family, test at least:

- All four exterior approach points connect through the baked navigation graph (pairwise or one-to-all in a connected undirected graph), and controller-sized capsule clearance remains at each doorway. At least one rotated room placement should be included where route coordinates are transformed.
- Every intended raised/depressed destination is reachable by a real stair path; the neighbouring unsupported cliff edge remains non-traversable. Ground bypasses promised by a pit/central obstacle layout actually connect.
- A controller can reach the family's declared destination and return at expected heights, with no flat hidden collision over a depression and no wall across a promised route.

Retain `HeightNavigationTests` as general rules. It already covers bidirectional treads versus cliffs, below-origin surfaces, solid terrace selection, doorway lintel handling and pathfinder height links. Those tests should not be rewritten just because a room is renamed. `MovementProofGeometry` plus NetMovement/NetFinal remain the project contract for changed placement; coordinator chooses the bounded harness selection and any required door/inventory proofs.

Do not test “six different hierarchy names” as layout diversity. Useful diversity evidence is an explicit topology/height signature per family plus the actual route proofs, followed by visual review of more than one generated seed. No test or visual acceptance is claimed by this reference note.
