# DakaDaka — current work

Updated 22 November 2026. The ONLY development checkout is `C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff`. Legacy `../DakaDaka` is read-only; `../DakaDaka_ci` is a disposable test/build mirror synchronized only from handoff. Never close or automate Play mode in the user’s main Editor. Visual reviews use a separate visible mirror Editor, MP4 with game audio and isolated `--ember-profile-dir` under mirror Logs/acceptance.

Runner and project rules updated 22 November. The Windows entry points validate the development root and test mirror before writing, preserve Unity locks and serialize runs. Reports and skill text select zero gameplay scenarios. Proof: 28 harness tests in `Logs/harness/editor-guard-20261122-tests.txt`, EditMode 16/16, NetProof 35/35 and NetAdmission 107/107 in `Logs/runs/20261122-050757-383`. Build output copying was checked with temporary player files. The shared visual and asset procedures are linked in AGENTS.md. Its [Unity pipeline rule](../AGENTS.md#unity-pipeline) now gives the named MCP connections priority for interactive Editor work.

## Current slice: softer stone walls

The user requested less sharp wall models, then explicitly requested temporarily disabling bots during environment inspection. Report: [SOFT_WALLS_20261122](../Tasks/SOFT_WALLS_20261122.md).

- Six visual-only wall derivatives add3.5cm rounded edges and corrected weighted shading. Nine outdoor prefabs replace90MeshFilter references; every collider, transform and gameplay component is unchanged. Source fingerprints prevent stale derivatives after future profile edits.
- Meshy-derived stone pier/crown retain geometry/UV/GUIDs; repaired normals remove backwards lighting corners and ImportedStone has less gloss/bump. Existing source assets are preserved.
- Focused planner fixture21/21 passes. Wrapper's minimum146 was mistakenly copied from the earlier broader suite; raw XML passes and the original wrapperFAIL is retained/explained. Cosmetic scope does not justify network/full-suite repeats.
- Final visible seeds12345/67890 each:300frames,1280×720,80walls/80original colliders,14.46m walk,21AI actors disabled, living full-health player.9.97s MP4s with stereo48k audio decoded and frames inspected; delivered in-chat. Independent6meshes×8scale normal audit passes. Evidence: `Logs/acceptance/soft-walls-20261122`.
- Bots were temporarily deactivated in the current primary Play session and the isolated review sessions at the user's request. Spawn settings/prefabs unchanged; a fresh ordinary raid restores them. Primary Editor remains open; owned mirror reviews closed and locks released.

Previous audio slice4b410795a is complete:151recorded CC0 effects, spatial distance/occlusion, audio fixtures8/8+5/5, NetCombat399/399+NetSkill127/127, ordinary/controlled recordings and measured distance attenuation. Report: [NATURAL_AUDIO_20261122](../Tasks/NATURAL_AUDIO_20261122.md). Listening acceptance remains with the user.

## Preserved baseline and limitations

Integration19519855b combined Meshy environment/Sol work with36m modules,+3.9m galleries,−1.5m passages and server-owned lever caches; protocol47. Earlier commits0875d13d enforce checkout andcbe51fad4 hold room/cache work. Reports: [HANDOFF_INTEGRATION_20261122](../Tasks/HANDOFF_INTEGRATION_20261122.md), [DOWNLOADED_ARCHITECTURE_20261122](../Tasks/DOWNLOADED_ARCHITECTURE_20261122.md).

Prior integration evidence remains: modules146/146, movement geometry11/11,11 network gates1763checks; combined71gates65pass/6baseline failure groups. Broad EditMode1479total1463pass4baseline failures12skip. Two seeds12345/67890 each14 ambience/lifecycle checks and recorded stereo audio. Full suite is not green; this audio slice uses bounded proof, not a repeat of the76-minute integration run.

Before this sound task, early ambience reviews touched the ordinary Squire profile and may have parked prepared gear in stash. Backups in `Logs/acceptance/ambience-20261122/profile-preservation`; user informed. No pre-run loadout proves a guessed restoration. New live reviews isolate saves; CLI network fixtures cannot enter OfflineProfileSession. Existing user Editor remains open.

Old stash `Preserve audio completion and inherited Unity drift before Sol integration` is retained and must never be blindly popped. Inherited material/Oasis/settings drift is excluded. No engine/package upgrades, legacy writes or push.

## Next three actions

1. User visual review of softened wall edges using the comparison PNG and final no-bot gameplay recordings.
2. Keep environment inspection free of AI when requested; current temporary deactivation does not change normal raid spawning.
3. Preserve inherited material/settings drift and prior integration debt; no broad suite rerun or push without a relevant reason.
