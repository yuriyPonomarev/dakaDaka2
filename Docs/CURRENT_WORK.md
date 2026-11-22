# DakaDaka - current work

Updated22 November2026. The ONLY development checkout is `C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff`. Legacy `../DakaDaka` is read-only; `../DakaDaka_ci` is the disposable verification mirror. Keep the user's primary Editor open. Reviews use isolated saves and owned mirror Editors; record/deliver MP4 with game audio.

## Completed slice: layout-aware furnishing and two-sided doors

Report: [LAYOUT_FURNISHING_20261122](../Tasks/LAYOUT_FURNISHING_20261122.md). All30 modules now have furniture recipes fitted to their ten physical layouts: furnished side chambers, gallery tables, wall-side stores, burial offerings and chapel side aisles. Existing downloaded PolyHaven/Meshy assets provide669 objects including tabletop candles. Socket approaches, stairs, chamber entrances and secret routes are protected; actual tomb-bypass blockage found during verification was repaired.

Door grille now reads on both sides. The original source remains intact; a closed thin wood core with deeper bevelled perimeter closes the oblique crown gap, with corrected rim UVs. Aperture, hinge, collision and original gameplay components remain unchanged. No depth-priority workaround or new downloads/generation.

Local extracted Dark and Darker is the mandatory primary reference. This slice inspected instantiated FourRooms furnishings and other room actor labels; exact furniture coordinates were not decoded. Our placement is an original adaptation to our saved layouts. Exact source paths and evidence limits are in the report.

## Verification status

Level run20261122-163503-562: **213/213 passed**, including15 new chamber-interior/secret-access cases. Corrected furniture review seed12345 completed alive at120health,AI0. All30 module families reviewed through furniture closeups/circulation; temporary diagnostic lighting is labelled. Prior failed recording remains retained and delivered. Door UV rebake preserves exact tested vertices/normals/indices/bounds.

Network run20261122-164929-010: all4 scenarios passed,567checks/0failures (NetMovement198,NetDoor49,NetInventory148,NetFinal172). Native seed67890 walks into both furnished Entry chambers and up/down the dais; health120,AI0, no lighting override. Both final videos decoded with non-silent stereo audio and delivered. Final eight E/N door states and72 motion samples show coherent rim texture, retained imported grille and no observed clipping/crown defect. Dark rear detail relies additionally on v2 diagnostic views with identical geometry. Scoped acceptance complete;45 owned files only, inherited15 excluded. Full evidence and limitations are in the report.

Bots remain disabled in new raids and later floors until explicitly restored. Harness subprocesses enable AI only for verification. The user's primary Editor remains open; reload Play/new raid to see saved prefab changes. Preserve inherited material/Oasis/settings drift and the old stash; no push.

## Preserved baseline and limitations

Previous completed layout/lobby slice b59f72dc5: [LAYOUT_FAMILIES_20261122](../Tasks/LAYOUT_FAMILIES_20261122.md), ten circulation families across30prefabs, repaired masonry/stair/arch joins, visible lobby braziers and migrated wall torches. Earlier integration19519855b preserved larger rooms, vertical routes and server-owned lever caches; protocol47. Earlier reports: [HANDOFF_INTEGRATION](../Tasks/HANDOFF_INTEGRATION_20261122.md), [NATURAL_AUDIO](../Tasks/NATURAL_AUDIO_20261122.md). Earlier integration had65/71 gates with baseline failures; current focused proof does not declare the entire repository green.

Early ambience review may have parked Squire prepared gear in stash; backup/evidence remains in Logs/acceptance/ambience-20261122/profile-preservation, user already informed. Do not guess a loadout restoration. No engine/package upgrades.

## Next three actions

1. User reviews a freshly loaded raid with AI still disabled.
2. Address concrete visual feedback with affected recorded views and focused checks; retain failed evidence.
3. Resume authorized unfinished gameplay work from ROADMAP; restore raid AI only on explicit request.
