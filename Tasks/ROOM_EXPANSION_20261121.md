# Expanded dungeon rooms and lever caches — 21 November 2026

User scope: larger modules with varied coherent existing/free props, rises/descents and lever-operated secret cache rooms. No paid generations.

## Implementation

- Existing saved module builder authors all three catalogs at 18×18 two-metre cells (36×36m), previously 12×12 (24×24m). All 30 module identities/order retained. Corridor/socket placement derives from footprint. Protocol 43 rejects older clients; catalog fingerprints include dimensions.
- Two western upper galleries at 3.9m with independent stair approaches, a real eastern undercroft at −1.5m with stairs at both ends, an unobstructed west doorway axis between the galleries, and perimeter column bays. Solid gallery abutments let the existing single-surface navigation find each upper wing without confusing it with ground beneath a bridge. Navigation baking includes below-origin surfaces.
- Varied refectory/supply/burial/shrine furnishing uses existing Poly Haven WoodenTable_01, barrels/crates/buckets, CryptArch mesh and project sarcophagus/column/brazier recipes. Existing stone surface shader/materials preserve texture scale. Floor wear skips the undercroft opening.
- Hall02, both ossuaries and vault in each theme contain a concealed corner cache. Lever-only reach/line-of-sight validation reuses door authority, snapshots and floor-scoped identities. Unlock is one-way for the raid; enemies cannot operate levers.
- Reserved cache volume excludes players and ordinary random content. An exact grid-centred SecretChestAnchor adds a deterministic chest with existing loot table and server inventory custody.

## Sourcing

Reused licensed assets only. Installed Meshy tools support account tasks but no free public catalog search/download. Public archway/coffin listings were found on meshy.ai; direct retrieval hit Cloudflare and browser access could not proceed. No individual Meshy public asset was downloaded or included; no generation or credit expenditure. Tables use the existing CC0 Poly Haven asset; arches and sarcophagi reuse existing project assets/recipes. See Docs/ASSET_PLAN.md.

## Verification

Main Editor final v6 compilation completed without errors. Mirror rebuild v6 exited 0, saved all 30 modules and rendered 15 views. Final artifacts copied into the development checkout; SHA256 equality verified for all 30 prefabs and three catalogs against the test mirror (`Logs/review/room-expansion/final-assets-sha256.json`). Views under `Logs/review/room-expansion` show the reused table, supported arches, gallery, actual lowered passage and concealed/open wall in all three themes; they use review lighting and are not ordinary live-player acceptance.

EditMode v3 (`Logs/review/room-expansion/editmode-v3`): 1461 total, 1445 passed, 4 failed, 12 skipped; 149.76s. Affected fixtures all pass: DungeonDoorInteractable 22, DungeonModulePlanner 21, ExpandedRoomTraversal 6, HeightNavigation 9, RoyaleFloorCatalog 21, StagedDungeonFloor 28, MovementProofGeometry 5, DoorNetworkBridge 3. New controller tests cross both stair directions without jumps in each theme. Existing staged tests exercise multiple seeds/rotations and solo/duo/trio placement.

Four failures match the previously recorded visual failures: skeleton derived-mesh count 8462 vs 30577; Cinder Warden variant mapping; crossbow support palm .08163m vs .08m threshold; missing cinder_club weapon mapping. No expectations weakened or unrelated assets repaired.

Integration `Logs/runs/20261121-192920-176` runs all 71 gates against the same rebuilt mirror after adding the real-process secret NetDoor proof. The planner conservatively selected all because geometry/protocol/new shared sources cross routing categories. This one broad integration run complements the completed full EditMode run; passing EditMode tests are not duplicated. Completed: 71/71 gates, 65 passed, 6 failed. All 11 network entries passed. Failures: RarityLadder 7, AttributeSkill 12, ContainerSnapshot 23, Slots 3, RetroVisual 4, RuntimeSmokeTest.RunCombatFlow 2. These concern old inventory schema expectations, claw trajectory, weapon/preset mappings and HUD. No unrelated expectations were weakened. Total gate runtime 1920.4s; runner 1988.9s.

Final v4 correction rebuilt successfully after this run: align crypt columns/ribs and infernal braces to the 8m enclosure, extend crypt transverse ribs to the new walls, and split the western upper gallery into two solid supported wings at 3.9m. The initial 2.4m continuous deck reduced the west doorway clearance; a 3.15m enemy clearance regression now protects the full route. Splitting the upper deck avoids stacked navigation surfaces and lets NPCs use either staircase. v4 rebuild/render completed. Its level suite passed 140/146 cases, including all controller/clearance and staged-floor cases, but all six NPC gallery tests found a buried support face at 3.6m instead of the visible 3.9m floor. v5 removes that internal horizontal collider face by making each gallery one solid deck. v5 corrected height selection (12 controller/clearance cases passed) but six upward path cases still failed. Diagnostic cell links identified a two-metre grid centre exactly on overlapping tread edges: downward and upward samples selected adjacent heights. v6 shifts both stair origins inward by 5cm (15.75m), leaving height/width unchanged and avoiding boundary ambiguity; also adds an existing-kit stone support behind the lever. All original assertions remain. Final v6 level suite passed all 146 cases (0 failures/skips, 93.67s), including all 18 expanded-room controller/clearance/NPC cases, height navigation and multiple-seed staged-floor placement. Evidence: `Logs/review/room-expansion/editmode-v6`. Final affected network run `Logs/runs/20261121-201826-956` passed 5/5 gates: NetMovement 198/0, NetFinal 172/0, NetDoor 49/0, NetInventory 148/0, NetRoyale 357/0 (924 checks, no failures; 460.2s gate time, 522s total). No further geometry/runtime changes followed. This bounded rerun follows the full v3 integration plus the final v6 level suite; the unrelated full-suite failures remain recorded above. MovementProofGeometry uses independent synthetic geometry, unchanged since its five passing v3 cases; no redundant rerun. The lever protocol is unchanged.

NetDoor proof now includes server AI refusal, remote-wall RPC rejection, accepted lever RPC, exactly one revision increment, matching wall lift and disabled collider on three clients, and rejected repeated activation. The late observer joins before secret activation; late snapshot mechanics also have unit coverage.

Earlier setup attempts: overlay mirror sync retained stale old-checkout scripts and failed compilation; corrected to validated full mirror sync. v2 built modules but review capture failed on an unsaved untitled batch scene; v3 opens NetBoot before additive review scenes. These setup failures caused no unrelated main-project changes.

## Ownership

Preserve pre-existing material, Oasis texture and ProjectSettings drift. No package or engine changes. User's open Editor is never closed or entered into Play mode; rebuild/render/process tests use DakaDaka_ci.

## Delivery state

Implemented and automatically verified in the primary checkout. Final staged visual review: Logs/review/room-expansion/contact-sheet.jpg plus 15 full-resolution PNGs. Main compilation has no errors. User Editor remained open and out of automated Play mode. Ordinary player-view acceptance remains with the user; the user explicitly requested committing reviewed work on 22 November; ordinary player-view acceptance remains open. No playable release build was substituted for acceptance.
