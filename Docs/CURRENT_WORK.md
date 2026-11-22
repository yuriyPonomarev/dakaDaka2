# DakaDaka — current work

Updated 22 November 2026. The ONLY development checkout is `C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff`. Legacy `../DakaDaka` is read-only; `../DakaDaka_ci` is the disposable verification mirror. Never close or automate Play in the user's main Editor. Visible reviews use the mirror with isolated saves, real frame progression and delivered MP4s with game audio.

Runner and project rules updated 22 November. The Windows entry points validate the development root and test mirror before writing, preserve Unity locks and serialize runs. Reports and skill text select zero gameplay scenarios. Proof: 28 harness tests in `Logs/harness/editor-guard-20261122-tests.txt`, EditMode 16/16, NetProof 35/35 and NetAdmission 107/107 in `Logs/runs/20261122-050757-383`. Build output copying was checked with temporary player files. The shared visual and asset procedures are linked in AGENTS.md. Its [Unity pipeline rule](../AGENTS.md#unity-pipeline) now gives the named MCP connections priority for interactive Editor work.

## Current slice: quiet new raids and softer traversal

Completed implementation and focused/visual proof. Report: [INSPECTION_TRAVERSAL_20261122](../Tasks/INSPECTION_TRAVERSAL_20261122.md).

- `EmberdelveGameConfig.populateRaidWithAi` is saved false. New Offline/host raids and later floors have no automatic rival bots or monsters. Human admission, map readiness, safe landing, loot and carried gear remain. Restore the single field true when the user asks to bring AI back; clients follow host authority.
- Test wrappers explicitly use `EMBER_RAID_AI=on` for normal-combat proofs and restore the previous environment. The setting is uncached, does not mutate the asset and is covered through inspection → normal → inspection scene reloads.
- All 30 room prefabs use 70 shared dimension-specific meshes for 2,802 traversal surfaces: 25 mm rounded stair noses and 40 mm deck/parapet/retaining edges. Only MeshFilter references changed; collider/transform/material/gameplay properties remain identical. Builders preserve the derivatives. Instanced triangle cost rises by 493,152, with no added renderers; no performance benchmark claimed.
- Combined automated evidence: main run `Logs/runs/20261122-071424-043` passed 149/150 Level cases, NetMovement 198/198 and NetFinal 172/172. The sole failing new Host fixture omitted normal authoritative-plan setup. After a fixture-only correction, all four inspection cases passed in `Logs/acceptance/soft-traversal-20261122/inspection-fixture-final`. Thus all 146 existing Level cases plus four new cases have passing evidence; original aggregate FAIL retained. Harness routing 22/22 passes.
- Visible seeds 12345/67890 each: 756 progressed frames, 1280 × 720, gallery and lower passage reached (−1.47 to +3.93 m), health 120/120, AI=0 from normal new-raid spawning. Both 25.17 s recordings fully decoded with stereo audio and were delivered in chat. Four saved views and video frames per seed inspected. Final 181 affected-file hashes match primary/mirror. Owned Editors exited, locks released; primary PID 8352 remains open. Evidence: `Logs/acceptance/soft-traversal-20261122`.
- [Local Dark and Darker construction audit](../Tasks/DARKER_MODULE_REFERENCE_20261122.md) found distinct bridge/pit/tower/divided-room families. All our modules repeat the west-gallery/east-undercroft template. Three original internal layout variations are proposed, not implemented. Extracted connector labels do not establish exact reference dimensions or a decoded walkable graph.

Previous wall slice 78f6d8709 and audio slice 4b410795a remain complete: [walls](../Tasks/SOFT_WALLS_20261122.md), [natural audio](../Tasks/NATURAL_AUDIO_20261122.md). The former temporary current-session-only AI deactivation is superseded by the persistent spawn setting above. User visual/listening acceptance remains open.

## Preserved baseline and limitations

Integration19519855b combined Meshy environment/Sol work with36m modules,+3.9m galleries,−1.5m passages and server-owned lever caches; protocol47. Earlier commits0875d13d enforce checkout andcbe51fad4 hold room/cache work. Reports: [HANDOFF_INTEGRATION_20261122](../Tasks/HANDOFF_INTEGRATION_20261122.md), [DOWNLOADED_ARCHITECTURE_20261122](../Tasks/DOWNLOADED_ARCHITECTURE_20261122.md).

Prior integration evidence remains: modules146/146, movement geometry11/11,11 network gates1763checks; combined71gates65pass/6baseline failure groups. Broad EditMode1479total1463pass4baseline failures12skip. Two seeds12345/67890 each14 ambience/lifecycle checks and recorded stereo audio. Full suite is not green; current slices use bounded proof, not a repeat of the76-minute integration run.

Before the earlier sound task, early ambience reviews touched the ordinary Squire profile and may have parked prepared gear in stash. Backups in `Logs/acceptance/ambience-20261122/profile-preservation`; user informed. No pre-run loadout proves a guessed restoration. New live reviews isolate saves; CLI network fixtures cannot enter OfflineProfileSession. Existing user Editor remains open.

Old stash `Preserve audio completion and inherited Unity drift before Sol integration` is retained and must never be blindly popped. Inherited material/Oasis/settings drift is excluded. No engine/package upgrades, legacy writes or push.

## Next three actions

1. User inspection of softened traversal and environment with AI kept disabled in new raids.
2. Use the reference audit to replace repeated internal circulation in a bounded room variant; decode reference transforms before claiming exact source dimensions.
3. Restore normal AI only when requested; preserve inherited drift, old stash and prior integration debt. No broad suite rerun or push without a relevant reason.
