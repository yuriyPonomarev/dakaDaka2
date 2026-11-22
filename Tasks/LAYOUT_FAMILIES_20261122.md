# Room layout families and lobby repair â€” 22 November 2026

The user rejected the repeated room circulation and supplied screenshots of an unsupported arch bearing, an oversized upright prop and a diagonal object cutting across Broken Crossing. This slice changes the actual saved room geometry and navigation; the earlier joins-only acceptance is not a claim that these new defects were absent.

## Reference and intended choices

Local extracted Dark and Darker is the mandatory reference. Exact inspected packages, actor/component exports and qualified placement evidence are in [DARKER_LAYOUT_EVIDENCE_20261122](DARKER_LAYOUT_EVIDENCE_20261122.md), extending [the earlier inventory](DARKER_MODULE_REFERENCE_20261122.md). These are original playable adaptations, not exact decoded metre-for-metre plans. Our external 36m footprint, four connector positions, stable catalog order and module IDs remain.

The existing extraction loop is retained: read the room, choose an exposed direct route or a protected detour, reach loot/exit and reconsider the route as enemies/zone pressure change. Distinct walls, stairs and sightlines make that choice spatially meaningful. Inspection AI stays off by user request; combat verification explicitly enables it.

| Existing module | New physical circulation | Reference family |
|---|---|---|
| Entry | Open arrival court, two side chambers, shallow northern dais | Atrium / subdivided halls |
| Hall 01 | Four enclosed chambers around cross passages | FourRooms |
| Hall 02 | Two west galleries, east descent, central screens | GreatWalkway |
| Hall 03 | Oppositely offset partition openings | MadCorridors / ComplexHalls |
| Ossuary 01 | Long burial aisles with side recesses and cross passage | Catacomb |
| Ossuary 02 | Actual sunken crypt with two stair approaches and ground bypass | LargeRoomPit |
| Shrine | Processional nave, side aisles and raised altar | Chapel |
| Vault | Inner enclosure with two gates, perimeter bypass and side gallery | CenterTower / Vault |
| Extraction | Offset antechambers around a broad crossroad | CrossRoad |
| Skybridge | Raised transverse crossing with opposing stairs and ground bypass | HBridge |

Each family is authored in all three destinations. Hall 02 alone retains the old west-gallery/east-undercroft route. The blanket diagonal terraces, duplicated internal decorations and old bridge/root ornaments are removed from the rebuilt interiors. Metadata, boundary roots, sockets, closure panels and existing Hidden_Reliquary roots are retained. New collision/nav placements require fresh gameplay proof; old 9,204-collider invariance is not claimed for this redesign.

## Screenshot causes and repairs

- Crown fit previously used only the lowest 3mm of a reduced mesh, whose right-foot sample missed the inner bearing rim. The new seated-crown derivative uses the full lower stone course and actual rendered pillar vertices; 260mm solid tapered capitals join both full footprints. Crown height follows source proportions, bounded by 100mm roof clearance; the first 65mm shelf version was rejected in recorded inspection. Original downloads are preserved.
- The tall diamond-panel object is the imported SealedReliquary chest, incorrectly stretched into every sarcophagus. That substitution is removed; original project stone casket/lid/effigy geometry is restored.
- The diagonal object cannot be uniquely identified from the screenshot. Old bridge ribs/partial arches and the oversized OldRoots are concrete candidates. Those legacy interior roots are removed by the new layout; the new crossing must be checked from both sides rather than claiming an unverified exact diagnosis.
- New layout treads have 4mm geometric joints and continuous collision; shared rounded stone derivatives preserve physical bevels. Old generic floor-relief placement is limited to its compatible gallery family to avoid stones floating over a newly positioned pit.

## Lobby

The saved Lobby.unity never received the courtyard Meshy integration: it still stored four Poly Haven lantern models. The builder already supports Meshy sconces, but the saved scene was stale. Two imported carved stone bowls on existing 0.9m pedestals are baked in an isolated preview scene and inserted as one owned prefab instance; active primary Play/scene is untouched. Only the existing Lobby_Room child-list document changes for that insertion (213 other documents identical).

Reference: `C:/Users/yrapa/Downloads/DarkAndDarker-extracted/pakchunk605-Windows/DungeonCrawler/Content/DungeonCrawler/Environment/Tavern/Meshes/SM_StandingBrazier01.uheader` (1,204 bytes) and `.uexp` (314,313 bytes). Header names establish StandingBrazier01, BodySetup, NavCollision and MI_StandingBrazier02, not decoded metric placement. Our two-bowl composition is original and reuses existing Meshy assets.

## Verification and repair history

Checkpoint `Logs/harness/before-layout-families-20261122.json`. Planner conservatively selects all71 gates because scene/editor inputs are shared. Bounded verification: Level EditMode (including48 new physical family cases), NetMovement, NetDoor, NetInventory and NetFinal because actual navigation/placement changes. No unrelated full-suite repeat. Recorded reviews cover lobby, ordinary player traversal, actual generated neighbour views and all30 saved families with four room views/four connector views plus arch contacts.

Saved family authoring and geometry validation succeeded in primary isolated prefab contents. Lobby prefab baked/insertion verified; four stale wall-torch models migrated with12 changed documents and204 remaining documents byte-identical. The lobby UI backdrop is now45% opaque; class/mode cards remain95% opaque. This makes scene contribution possible without changing menu actions or text.

First Level run `20261122-142935-764`:198 total,36failed. Fixes: shorten burial-screen wall that intruded into NE cache, use5mm collision overlap behind4mm visual tread joints, shift Vault gallery0.5m so first low rise lies between navigation cell centres. Second `20261122-143824-523`:194/198 passed, including all48 new family cases; remaining4 cases correctly caught removed BreakableBarrel content. Restore two existing authored breakable barrels per room at clear positions; do not weaken assertions. Dedicated metadata audit confirms all30 module-definition documents unchanged; all secret script documents in12prefabs unchanged. This was an intermediate run; final proof and acceptance scope are recorded below.


## Recorded inspection iterations

- `live-12345-families-v1`: rejected helper route; a straight input path met the new side-chamber wall. Replaced it with actual baked path waypoints, still driven by ordinary player input.
- `live-12345-families-v2`: 5,814 recorded frames, 1280x720, stereo AAC, player health120, AI0, measured dais peak0.93m. All30 overview/120 room/120 port views inspected. Thin arch bearing shelves rejected; corrected to tapered capitals. Catalogue port views lacked runtime doorway infill, and dark/occluded stair views could not establish fine join acceptance.
- `live-12345-families-v3`: new capital proportions and live neighbour door-seam sweeps recorded. Review helper stopped on its wrong nested bearing lookup (fixed); this is not a game crash. A real roofed-room sky gap was confirmed in live Vault: opening a socket disabled the entire8m closure while the lintel ended4.275m. Permanent upper masonry was subsequently separated from the lower closure presentation, with matching upper collision and no duplicate opaque front faces. New targeted views include both arch feet and both directions of every stair.

These failed/intermediate artifacts are retained and delivered; they are not final acceptance. Primary Editor PID8352 remains open and untouched by review Play/scene operations.


## Final physics proof and further visual findings

`Logs/runs/20261122-152040-906/editmode`: **198/198 passed**, zero failed/skipped/inconclusive,198.99s. This includes all48 new family cases, every open port, actual stair/pit controller movement in both directions, cache reachability, restored breakable barrels, and upper-socket collision checks. The same run continues NetMovement/NetDoor/NetInventory/NetFinal.

`live-12345-families-v4`:8,634 frames,1280x720,4m47.77s H264+stereo AAC, health120, AI0. All60 arch-foot closeups and20 actual door-seam views inspected: solid capitals, no visible floating feet, prior live Vault sky gap removed. All66 stair closeups inspected: they revealed pale lower-floor/retaining joins in both pit families; moving frames confirmed the issue, so v4 is not final pit acceptance. Lower floor render margins and a closed backing beneath below-ground tread joints were subsequently added without changing gameplay collision. One seam angle was occluded by a brazier; final review adds the opposite lateral angle and door motion in both E/N orientations.

Separately, perpendicular new wall boxes shared coplanar top areas at Chamber/Vault/Ossuary corners. Spans now butt at side-wall inner faces; side walls cover the outer corner, and an authoring validator rejects positive-area overlap at equal wall heights. Shrine front columns moved fromz4 toz2, ahead of the stair approach. These final physical changes are included in the198-case pass above. The final corner review targets their visible joints specifically.


The pit repair is now saved in all six affected prefabs:0.10m render underlap per floor side,0.06m into stair bases, and12 closed stone wedge backings under tread joints. Exact comparison against the still-running verification mirror is in `final-physics-equivalence.json`: all2,905 gameplay component documents identical and5,862 existing transforms unmoved across30 prefabs. The physical198-case/network evidence therefore still covers the final gameplay geometry. The final recorded geometry pass uses an explicitly labelled temporary camera light after native gameplay/map views, addressing the unreadable dark joins without changing saved game lighting.


Completed bounded run `20261122-152040-906`: Level198/198; NetMovement198checks; NetDoor49; NetInventory148; NetFinal172. All four network scenarios passed with zero failures (567 checks). Total run1033.9s. The final visual-only pit repair was compared to these exact tested prefab gameplay documents, as described above; no broad rerun was needed solely for those render meshes.


## Final recorded acceptance

- `live-12345-geometry-final-v1`:11,710 frames,1280x720,6m30.30s H264/stereo AAC. Native lobby, ordinary player walk and generated-map views precede explicitly labelled diagnostic-light inspection. Inspected30 overviews,132 wall corners,60 arch bearings,66 stair views,40 live seam angles and8 E/N door states. The former upper Vault gap and six lower-floor bevel gaps are closed. Neither render priority nor material depth bias was used.
- Motion samples:48 corner frames at8fps across01:30–01:32 (Crypt Extraction),03:04–03:06 (Ruins Entry),05:15–05:17 (Infernal Hall01);24 stair frames across six repaired rooms. E/N door transitions inspected frame-by-frame in68.05–68.65/70.55–71.15 and73.05–73.65/75.30–75.90s windows. No concrete remaining join/clipping defect found in this scope. Reports/frames are in `join-review-sheets`, `stairs-audit-frames` and sibling `temporal-corner-review` directories.
- The long free-camera run ended alive at40health because the ordinary zone reached the stationary player. Its initial walking proof preserved120health; AI remained0. This is not a full-duration health-preservation claim, and zone timing was not changed.
- `live-67890-families-final`:2,248 frames,1280x720,1m14.90s H264/stereo AAC, ordinary walk reaches0.93m and returns,120health,AI0. Native lighting throughout; second generated layout and E/N doors inspected. Lobby shows both updated braziers outside the existing menu panels. Full videos decoded and representative moving frames inspected; both recordings delivered in chat.

Lobby visibility refinement: fresh saved camera verified at(0,1.62,-5.5),pitch4°,FOV62. West stand(-4.96,0,6.2),uniformscale1; east(10.505,0,5.4),uniformscale0.72. Their conservative bounds remain within the room and clear the existing wall props; final native lobby capture confirms readable silhouettes. Only decorative prefab transforms changed at this last stage, not lobby actions or collision. Existing source-prefab GUID/reference links remain valid.

Acceptance is scoped to authored layout/contact geometry and sampled motion, not an assertion that every frame/pixel is flawless. Infernal native lighting remains deliberately dark; diagnostic light was temporary and clearly labelled, never saved into game assets. The reference property schema/metric placement remains undecoded. The original diagonal screenshot object is still not uniquely identified, although its entire legacy interior construction was replaced and the final bridge views have no such protrusion.

Primary Editor PID8352 stayed open; all owned review/test Editors exited and the mirror is idle. All30 module component documents and12 secret-script sets retain their original identities. No original downloaded model, personal save, legacy checkout, inherited material/Oasis/settings drift or old stash was overwritten. Only this verified slice is staged for the local commit; no push.
