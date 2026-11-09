# Crypt architecture and room content — 9 November 2026

User requested a populated crypt inspired by the first Crypts level in Dark and Darker. This authorizes the new map-content milestone after the accepted raid baseline7f203c9c.

Loop: read a room and choose a route, use cover/doors to fight or evade, collect loot, decide whether to risk another room or extract. Architecture must change sight lines and routes while keeping recognizable landmarks and clear doorway landings. Existing combat, loot and extraction supply consequences; no new mechanics are required.

Reference: https://darkerdb.com/database/maps/the-forgotten-castle and the official Steam screenshots https://store.steampowered.com/app/2016590/Dark_and_Darker/ . Use the principle of distinct chapel/burial/bridge/reliquary spaces, not a copied map or extracted assets.

Scope: saved module prefabs produced by the existing editor builder; original curved arch mesh, columns, tomb alcoves, pews and funerary vessels; elevated visual ceiling and distinct room arrangements. Keep the10 module IDs/order and four doorway sockets. Version6 admission prevents old clients with obsolete geometry joining the new build.

Acceptance: actual first-person review and traversal on two layouts; focused level/geometry tests and relevant network movement/final/door/inventory checks. Planner conservatively selects70 gates for editor/prefab content; narrow because rules, inventory transactions and combat timings do not change. No full sweep solely for decorative content.

Status: accepted MAP-01 milestone. Local-only materials and Pipeline configuration are preserved outside the commit.

## Visual iteration

V1: seed11/hashCC18B0DC host/client successfully assembled new rooms; client walked through doorway onto bridge. Screenshots entry/hall/bridge-first showed architecture was too dark. V2 added the barrel vault and visible flame cores; vault-ceiling confirms curved interior, but illumination was still weak at24m room scale. Final adjustment increases room lighting and limits it to level geometry so first-person hands do not overexpose. No postprocessing/global-quality change. BuildCrypt Rooms and Playable runs catalog+player in one Editor launch. All review players closed before replacing the build.

## Final live review

V3 final build: seed11/hashCC18B0DC, host33128/client15532. Visible vaulted room, tomb niches, columns and flame cores; vault-final.png, bridge-final.png, burial-gallery.png. Normal client mouse/keys/E crossed both doorways and the full bridge, ending(36.1314,0.03,53.4875); route-seed11.json. Closed B12/A2.

V4 same executable: seed2043687868/hash00B81D9C, host45276/client33192. Chapel pew aisles and reliquary room visibly differ; room-second-layout.png and room-after-bridge.png. Normal movement/E crossed the same through-route on this layout, ending(37.1399,0.03,53.4670); route-seed2043687868.json. Closed B12/A1. Fixtures only disabled enemies to inspect geometry; no teleport traversal or combat acceptance claim. Existing loot/enemy systems remain unchanged.

The architecture remains an original low-poly first pass using existing materials. This adds authored room character, cover, alcoves and vaulted proportions; it does not claim copied Dark and Darker art, new traps, secret doors or a complete content campaign. Reflected/brazier lighting targets level geometry, retaining the existing hand/character lighting. All new assets are generated project-owned geometry/materials.

Selected final checks: level EditMode assembly (includes planner and MovementProofGeometry); NetMovement, NetFinal, NetDoor and NetInventory. These cover changed collision, spawns, door access, client agreement and relocated pickups. No new test added for decorative meshes, no repeated unrelated combat/stat suites.

## Focused verification and fixture correction

Level assembly passed29/29 (level-tests.xml). Network run20261109-110347-338: NetFinal172/0, NetDoor37/0, NetInventory146/0; NetMovement190 checks with2 failed wall assertions. Seed-986579934/hash620D25CB staged W at(3,.15,9), capsule sweep reported obstacle6.840m ahead, but real controller stepped over low decorative collision and continued to(3.042,.35,18.459). The proof had treated any capsule collision as an unstepable wall.

MovementProofGeometry is development-only. Its wall selection now requires a head-on vertical face across three chest-height rays spanning the controller width, at the measured capsule contact plane. This rejects low footings and glancing round columns. Original distance/speed tolerances remain. Added two cases to the existing geometry fixture; other accepted network scenarios are not repeated. No gameplay controller, collision meshes or map content changed for this correction.

## Acceptance closure

Wall-fixture tests passed5/5 (the existing3 cases plus2 focused regressions). Run20261109-111129-372 NetMovement passed190/0 on the original seed-986579934/hash620D25CB, supplied as unsigned raid seed3308387362. The original4-gate run remains recorded as failed; combined evidence is29 level tests, the5-case corrected fixture, NetFinal172/0, NetDoor37/0, NetInventory146/0 and corrected NetMovement190/0. No thresholds relaxed, no successful unrelated scenario repeated.

All scoped Assets source/prefabs/materials/mesh match the mirror byte-for-byte after verification. Source/doc whitespace check passes. Explicit26-path milestone manifest excludes the six inherited local-only paths. No graphical players or mirror tests intentionally left running. Main user's Editor was not operated.
