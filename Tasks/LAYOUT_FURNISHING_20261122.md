# Layout-aware furnishing and two-sided doors Р В Р вЂ Р В РІР‚С™Р Р†Р вЂљРЎСљ 22 November 2026

Furnishing slice after b59f72dc5. Author only in DakaDaka_clean_handoff. Preserve inherited material/Oasis/settings drift. No new downloads/generation or original asset changes; reuse existing licensed PolyHaven and owner-supplied Meshy models. Bots remain disabled.

## Reference actually inspected

Prefix P = C:/Users/yrapa/Downloads/DarkAndDarker-extracted/pakchunk2802-Windows_0_P/DungeonCrawler/Content/DungeonCrawler/Maps/Dungeon/Modules/Crypt/Center/.

FourRooms/Crypt_FourRooms_A.uexp: length-validated actor labels bench2@0xddc7, Mdv_Crate_2@0xea5b, SM_Crate_Lid_2@0x100b8, bench@0x1642f, SM_Chair_02@0x1795f. Matching uheader export map begins at28848,72-byte records: actors609/655/736/1093/1172 have children1193/1275/1360/1717/1796 through outer indices. These establish instantiated furnishings. CenterTower/CenterTower_A.uexp labels SM_OldWoodenTable2@0x43d20, SM_OldWoodenTable5@0x43d6b, SM_WoodenAmmoCrate2@0x43e02; LargeRoomPit/Crypt_LargeRoomPit_A.uexp bench4@0x29639, SM_Bench_01@0x2b94e, SM_Crate_4@0x2c48e; GreatWalkway/Crypt_GreatWalkway_A.uexp SM_Vase_01@0x1ad76. Only FourRooms was export-linked in this pass; no furnishing transform schema was decoded.

Original adaptation: furnished side chambers, guard stores, writing/refectory tables, burial offerings and chapel side-aisle furniture. All placement is authored against our actual room layout rather than claimed exact reference coordinates.

Door reference: pakchunk602-Windows/.../Environment/Dungeon/Crypt/EightToOne_01/Meshes/SM_Wooden_Door_01_L.uheader and _R.uheader contain MI_Metal_01, MI_Wood_01, NavCollision_Chaos/BodySetup. No exact dimensions inferred. Our existing grille has both surfaces, but opaque timber at rear z+.037..+.105 hid the rear metal. Centre the20mm timber core inside original210mm envelope; validate outward imported triangle area on both sides. No duplicated opaque faces or depth-priority hacks.

## Final implementation

All 30 saved module prefabs use explicit recipes for their ten layout families. Side chambers have tables, seating and shelves; galleries have furniture on actual landings; burial/chapel spaces have wall-side offerings; keeps and crossings have separate storage bays. Existing generic scatter/table roots are removed, protected secret subtrees remain. Existing downloaded PolyHaven wood furniture and Meshy shrine/votive models suffice; no new download or generation.

Authoring uses uniform scale from actual renderer bounds, grounded footprints, physical furniture collision and measured tabletop placement. It reserves central routes, socket approaches, chamber entrances, stair footprints/landings and secret access, rejecting occupied or unsupported placements. Hall02 shallow flagstone relief is cosmetic bedding, with its original underlying collider supplying support. All original gameplay script documents in the 30 modules are preserved; added behaviours belong to the existing VotiveStand light prefab.

Accepted objects including tabletop candles: Entry24, Extraction26, Hall01 23, Hall02 21, Hall03 24, Ossuary01 18, Ossuary02 18, Shrine22, Skybridge24, Vault23, each across three themes:669 total. Reproduce with Tools/Art/Furnishing.cs Apply; Validate reads saved results. Both catalog builders apply the same authoring after layout/pit repairs.

The door keeps its original imported grille, materials, aperture, hinge and collision. A single closed joined wood mesh replaces the masking backing:20mm centre,65mm bevelled perimeter reaching200mm depth inside the original210mm leaf,5mm aperture inset. Derived DoorCore_Rebated65mm_v1.asset has184 triangles,94 welded vertices,276 edges shared by exactly two faces; both-face imported surface exposure is validated. Rim UVs use depth/perimeter coordinates instead of collapsed XY projection. The final UV rebake proves vertices, normals, triangle indices and bounds identical before/after (door-uv-equivalence.txt). No depth priority workaround or duplicate opaque faces.

## Corrections from verification

Initial furniture pass had a modern-looking metal rack, isolated entry furniture and some unreadable review camera angles. Replaced the rack with existing worn wood shelves, moved entry groups to perimeter bays and Hall02 shelving to its partition. Review cameras now avoid masonry and use actual furniture fronts. Vault table is placed in the keep bay.

Level run20261122-161411-590 passed210/213: new chamber target was inside the original Ossuary02 tomb. Corrected target to adjacent exact navigation cell(7,0,-7), preserving clearance/path assertions. Run20261122-162914-741 still passed210/213, exposing a real furniture blockage around that tomb. Moved offerings to the back-wall bay at(8.5,-5.5)/(3.3,-5.5), retaining the test. Final run20261122-163503-562 passed **213/213**, zero failures,208.9s. This includes all original198 Level tests plus15 cases for actual chamber interiors and closed-lever/open-cache routes in rotated modules.

Initial two-sided door core exposed an oblique crown crescent. The final deeper joined perimeter closes it. v2 motion review found stretched edge UVs; corrected only UVs/tangents, preserving tested geometry. Final native review is recorded after that rebake.

## Check selection and evidence

Planner checkpoint Logs/harness/before-layout-furnishing.json conservatively selected all71 gates for new/shared authoring inputs. Bounded selection is Level plus NetMovement, NetDoor, NetInventory and NetFinal: saved obstacles/door presentation changed, combat/economy/network rules did not. This is focused slice acceptance, not a full repository baseline claim.

Evidence root: Logs/acceptance/layout-furnishing-20261122. Per-prefab placement/rejection reports, component-preservation.json and final-inputs.json are retained. Initial final-inputs snapshot proves35 primary/mirror inputs matched for v2; the subsequent UV-only delta is separately verified.

Visual recordings live under Logs/acceptance/layout-families-20261122:

- live-12345-furnishing-v1: rejected interim,13,012frames/7m13.70s. Initial ordinary walk succeeded; long stationary diagnostic inspection later ended in normal zone death, obscuring final views. Camera/style/crown defects above were corrected. Recording decoded and delivered as failed evidence.
- live-12345-furnishing-v2: corrected furniture,9492 encoded frames/5m16.40s,1280x720 H264 with stereo48kHz AAC. Normal walk/dais scenario and inspection completed alive at120 health,AI0. Full decode passed. All30 families reviewed:203 furniture closeups+40 circulation+10 overview frames per theme; detailed reviews under crypt-review-v2, ruins-review and infernal-review. Ruins native checks include Hall02 gallery table, moved Ossuary02 statue group and Vault interior table. No concrete placement/intersection/support defect found in readable views. Some dark wide views face partitions and are weak evidence by themselves; actual navigation and closeups complement them. Diagnostic light is labelled and transient, after native gameplay/map views. Door audit covered eight E/N front/back/open/closed views and72 motion frames; final UV repair follows this recording.

Temporal v2 furniture review sampled216 frames at12fps across nine two-second spans:01:30,01:35,01:42,03:05,03:10,03:17,04:10,04:15,04:21. No recurring depth flicker, disappearing furniture, floating support or new passage obstruction found in that scope. Two frames around03:05.7 show macroblocking including HUD, immediately resolving: a recording artifact is likely, exact cause unproven. Adjacent furniture views remain readable. Audio track is present/non-silent (mean-25.4dB,peak-12.0dB); no subjective audio-quality claim is made. Final-uv-inputs.json confirms all35 final primary/mirror input hashes agree after rim UV correction.

Network run20261122-164929-010: **all4 passed**, NetMovement198, NetDoor49, NetInventory148, NetFinal172 checks, zero failures (567checks total). Elapsed704.5s. Primary final assets synchronized to mirror; no cached PASS results. Initial net-proof build inputs changed during import, so its cache was not stored; subsequent profiles built/stored normally. All actual gate executions passed.

Second-seed native recording live-67890-furnished-native-final completed:2667 encoded frames/1m28.90s,1280x720 H264/stereo48kHz AAC, full decode passed. Mean audio-25.5dB,peak-11.9dB. Ordinary input walks into both furnished Entry side chambers, back through their thresholds and up/down the welcome dais (height0..0.93m); no teleport, health120,AI0. Native screenshots and18 samples at two-second intervals reviewed through lobby, both chamber visits and dais return. No blocking or unsupported furniture seen. West room remains dark under saved native lighting; diagnostic catalogue views supply detailed support evidence. Following free-camera map/seam/door inspection is labelled and uses no lighting override. Recording delivered in chat. Both review Editors exited and mirror lock released; primary PID8352 remains open. All35 final gameplay/art/test input hashes remain identical between primary and mirror after network builds.

Final door motion audit passed within inspected scope: all eight E/N front/back/open/closed states plus72 native400x360 crops at15fps, Nopen78.5-79.7s,Nclose81.0-82.2s,Eopen83.5-84.7s,Eclose86.0-87.2s. Rim texture is coherent; prior stretched banding is gone. Imported grille remains visible, with no observed leaf/frame clipping or unstable crown opening. Native closed rear is very dark; detailed geometry evidence combines the unchanged-geometry v2 diagnostic faces with final UV/native motion. Report/frames: door-furnished-native-final-review under the recording root.

## Completion

Focused automated and visual acceptance complete; no known remaining furnishing/door defect in reviewed scope. All45 owned files form one local commit; inherited15 material/Oasis/settings changes and old stash stay excluded. No push. Saved bot-off configuration retained. Reload Play/start a new raid for updated prefabs; no primary scene/save manipulation was performed. The native recording is the final walkthrough; the catalogue recording carries exhaustive family closeup coverage, with the documented short encoding artifact/dark-view limits. This is not full-suite baseline acceptance.
