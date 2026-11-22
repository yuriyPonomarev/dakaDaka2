# DakaDaka — current work

Updated 22 November 2026. The ONLY development checkout is `C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff`. Legacy `../DakaDaka` is read-only; `../DakaDaka_ci` is the disposable verification mirror. Keep the user's primary Editor open in its current state. Live reviews use isolated saves and an owned mirror Editor, recorded MP4 with game audio and delivery in chat.

Runner and project rules updated 22 November. The Windows entry points validate the development root and test mirror before writing, preserve Unity locks and serialize runs. Reports and skill text select zero gameplay scenarios. Proof: 28 harness tests in `Logs/harness/editor-guard-20261122-tests.txt`, EditMode 16/16, NetProof 35/35 and NetAdmission 107/107 in `Logs/runs/20261122-050757-383`. Build output copying was checked with temporary player files. The shared visual and asset procedures are linked in AGENTS.md. Its [Unity pipeline rule](../AGENTS.md#unity-pipeline) now gives the named MCP connections priority for interactive Editor work.

## Current slice: repair depth artifacts and architectural joins

The user rejected the prior result with visible floating/colliding architecture. Earlier traversal-only visual proof was inadequate. Active report: [MODULE_JOINS_20261122](../Tasks/MODULE_JOINS_20261122.md).

- All 30 saved modules: 2,400 tread renderers now have separated joints; 60 retaining-wall renderers stop below the ground slab instead of duplicating its faces. Shared bevel sources remain; 61 fitted derivatives preserve walking colliders and transforms.
- Column-fragment wall tiling is replaced by continuous rounded stone surfaces. Closed boundary meshes fit within their owning room's footprint, eliminating duplicated neighbour surfaces while preserving the inner envelope. 90 shared architectural derivatives including the fitted door backing; no open clipped shells or depth/render-queue hack.
- Arch springlines meet actual supporting columns. Two unsupported overhead ornaments in base Vault/Extraction are omitted with their MeshColliders. 9,204 module colliders otherwise keep exact geometry/ancestor transforms; all 30 catalog references, 83 script components, 1,307 nested instances and 212 light/script identity paths remain unchanged.
- Door masonry restores its exact arched aperture. The existing Meshy leaf and blocker share a face-mounted hinge at z=−0.435 m, with a measured 5 mm clearance throughout 0–96° motion. Portal veil fits floor-to-lintel in all three saved scenes. Rules, skills and review procedure now explicitly reject depth artifacts, unsupported joins and broken doorway motion.
- Saved repair/validation passes. Level EditMode 150/150; NetMovement 198/198, NetDoor 49/49, NetInventory 148/148 and NetFinal 172/172 pass in `Logs/runs/20261122-081547-253`. The final visual-only backing delta preserves every door collider/script document.
- Visible assembly seeds 12345/67890 passed ordinary stair routes and labelled map/door/gate camera inspection; 120 catalog views cover all 30 saved prefabs. Both MP4s fully decoded, frames inspected and delivered. A later door review exposed an oversized backing gap; it was corrected with a uniform 5 mm fitted backing. Final `door-final-sealed` recordings on both seeds show open/closed front/back views, were decoded, inspected and delivered. Final primary/mirror content matches across 348 affected asset/editor files. User acceptance remains separate. Evidence: `Logs/acceptance/module-joins-20261122`.

AI remains disabled for new raids and later floors by the saved setting from 8d890de8f; re-enable only when requested. Verification processes explicitly override AI on for normal combat. No personal save edits. This pass repairs existing construction; distinct room-layout families and exact reference-game dimensions are not claimed. [Reference audit](../Tasks/DARKER_MODULE_REFERENCE_20261122.md).

Previous audio 4b410795a remains recorded in [NATURAL_AUDIO_20261122](../Tasks/NATURAL_AUDIO_20261122.md). Walls/traversal prior reports remain historical, superseded wherever this repair finds assembly defects.

## Preserved baseline and limitations

Integration19519855b combined Meshy environment/Sol work with36m modules,+3.9m galleries,−1.5m passages and server-owned lever caches; protocol47. Earlier commits0875d13d enforce checkout andcbe51fad4 hold room/cache work. Reports: [HANDOFF_INTEGRATION_20261122](../Tasks/HANDOFF_INTEGRATION_20261122.md), [DOWNLOADED_ARCHITECTURE_20261122](../Tasks/DOWNLOADED_ARCHITECTURE_20261122.md).

Prior integration evidence remains: modules146/146, movement geometry11/11,11 network gates1763checks; combined71gates65pass/6baseline failure groups. Broad EditMode1479total1463pass4baseline failures12skip. Two seeds12345/67890 each14 ambience/lifecycle checks and recorded stereo audio. Full suite is not green; current slices use bounded proof, not a repeat of the76-minute integration run.

Before the earlier sound task, early ambience reviews touched the ordinary Squire profile and may have parked prepared gear in stash. Backups in `Logs/acceptance/ambience-20261122/profile-preservation`; user informed. No pre-run loadout proves a guessed restoration. New live reviews isolate saves; CLI network fixtures cannot enter OfflineProfileSession. Existing user Editor remains open.

Old stash `Preserve audio completion and inherited Unity drift before Sol integration` is retained and must never be blindly popped. Inherited material/Oasis/settings drift is excluded. No engine/package upgrades, legacy writes or push.

## Next three actions

1. User inspection should start a new raid to instantiate the repaired saved modules; keep AI disabled.
2. Address any specific remaining visual feedback with the same depth/join checks and isolated recorded review; do not infer a broad room-layout or lighting acceptance.
3. Preserve unrelated drift, old stash and broad integration debt. No push or full-suite repeat without a relevant reason.
