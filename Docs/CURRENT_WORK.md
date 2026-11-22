# DakaDaka — current work

Updated 22 November 2026. The ONLY development checkout is `C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff`. Legacy `../DakaDaka` is read-only; `../DakaDaka_ci` is a disposable test/build mirror synchronized only from handoff. Never close or automate Play mode in the user’s main Editor. Visual reviews use a separate visible mirror Editor, MP4 with game audio and isolated `--ember-profile-dir` under mirror Logs/acceptance.

Runner and project rules updated 22 November. The Windows entry points validate the development root and test mirror before writing, preserve Unity locks and serialize runs. Reports and skill text select zero gameplay scenarios. Proof: 28 harness tests in `Logs/harness/editor-guard-20261122-tests.txt`, EditMode 16/16, NetProof 35/35 and NetAdmission 107/107 in `Logs/runs/20261122-050757-383`. Build output copying was checked with temporary player files. The shared visual and asset procedures are linked in AGENTS.md. Its [Unity pipeline rule](../AGENTS.md#unity-pipeline) now gives the named MCP connections priority for interactive Editor work.

## Current slice: recorded Foley and spatial sound

The user rejected the synthetic timbre of bd3975ba0 and requested natural, spacious sounds with distance-dependent volume. Report: [NATURAL_AUDIO_20261122](../Tasks/NATURAL_AUDIO_20261122.md).

- Replaced the active49-family/151-clip bank with edits of real CC0 recordings; stable filenames/GUIDs.132 selected originals, authors/licenses and exact take provenance are retained. Designed magic/UI cues arrange recorded materials; wood steps and stone mechanisms are explicitly labelled material composites. Existing48s melodic music remains.
- World audio has category-specific smooth distance falloff, stereo direction, attached moving emitters and smoothed wall attenuation/filtering. Short1.05s stone-room reflections; actual active/spectator AudioListener controls acoustics. UI and personal run notifications remain local.
- Action audio now covers authority actors and client observers through existing replicated edges. Hurt/death/equipment/skills follow actors. Initial snapshots/floor transitions stay silent; no protocol, damage, inventory custody or level changes.
- Asset checks pass:151 unique mono48k PCM16 files,8.78MiB,95.855s total; byte-for-byte reproduction and independent source/PCM checks. Preview50.520s: `Logs/acceptance/natural-audio-20261122/RecordedSfx-preview.wav`. Audio fixtures8/8+5/5 pass; final NetCombat399/399 and NetSkill127/127 pass (`Logs/runs/20261122-052407-639`).

- Final visible seed12345:19 ordinary raid checks,38 cumulative source/geometry checks and6 recorded-output comparisons across8cases pass. Normal6.97s gameplay and separate21.27s controlled MP4s have stereo48k audio. Same impact is22.13dB quieter at18m than2m; wall adds13.67dB at8m. Fixed contacts no longer follow a moving pool owner. Final artifacts: `Logs/acceptance/natural-audio-20261122/live-12345-final2`. All172 changed Unity assets/source files match the tested mirror; primary Editor remained open and mirror closed cleanly. Listening acceptance remains with the user.

## Preserved baseline and limitations

Integration19519855b combined Meshy environment/Sol work with36m modules,+3.9m galleries,−1.5m passages and server-owned lever caches; protocol47. Earlier commits0875d13d enforce checkout andcbe51fad4 hold room/cache work. Reports: [HANDOFF_INTEGRATION_20261122](../Tasks/HANDOFF_INTEGRATION_20261122.md), [DOWNLOADED_ARCHITECTURE_20261122](../Tasks/DOWNLOADED_ARCHITECTURE_20261122.md).

Prior integration evidence remains: modules146/146, movement geometry11/11,11 network gates1763checks; combined71gates65pass/6baseline failure groups. Broad EditMode1479total1463pass4baseline failures12skip. Two seeds12345/67890 each14 ambience/lifecycle checks and recorded stereo audio. Full suite is not green; this audio slice uses bounded proof, not a repeat of the76-minute integration run.

Before this sound task, early ambience reviews touched the ordinary Squire profile and may have parked prepared gear in stash. Backups in `Logs/acceptance/ambience-20261122/profile-preservation`; user informed. No pre-run loadout proves a guessed restoration. New live reviews isolate saves; CLI network fixtures cannot enter OfflineProfileSession. Existing user Editor remains open.

Old stash `Preserve audio completion and inherited Unity drift before Sol integration` is retained and must never be blindly popped. Inherited material/Oasis/settings drift is excluded. No engine/package upgrades, legacy writes or push.

## Next three actions

1. User listening review using the new recorded-effect reel and final ordinary/controlled videos; tune specific timbres or levels from concrete feedback.
2. Extend close-up context review to individual weapons, consumables and cache mechanisms when requested; the brief raid recording does not claim every cue was heard in context.
3. Keep old integration test debt separate and preserve inherited material/settings drift. No broad suite rerun or push without a relevant reason.
