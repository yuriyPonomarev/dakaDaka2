# DakaDaka — current work

Updated 22 November 2026. The ONLY development checkout is `C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff`. Legacy `../DakaDaka` is read-only; `../DakaDaka_ci` is a disposable test/build mirror synchronized only from handoff. Never close or automate Play mode in the user’s main Editor. Visual reviews use a separate visible mirror Editor, MP4 with game audio and isolated `--ember-profile-dir` under mirror Logs/acceptance.

## Current slice: cohesive game sound

User asked for jumping, walking, impacts and the rest of the game’s sounds. Report: [GAME_AUDIO_20261122](../Tasks/GAME_AUDIO_20261122.md).

- 49 original cue families/151 mono 48 kHz PCM16 variants,14.2 MiB. Movement/surfaces/jump/landing, leather/equipment, melee/ranged/guards, actors, doors/chests/cache mechanisms, inventory/UI, consumables, skills and run transitions. Shared cached clips, anti-repeat selection independent of gameplay RNG, bounded 24 spatial + 8 local voices and existing wall occlusion.
- Gameplay facts and presentation edges drive audio; no protocol, physics, inventory custody or damage changes. Remote owner uses the same feedback/action presenters. Initial chest/door/skill states are silent. Confirmed inventory transfers and coalesced final weapon state prevent snapshot chatter. No guessed consumable completion on cancellation.
- Original melodic 48 s ambience from 19519855b retained. Generator/provenance: `Tools/Audio/SynthesizeGameSfx.cjs`, `SFX_README.md`, bank manifest. Preview reel and raw evidence: `Logs/acceptance/game-audio-20261122`.
- Asset generation reproduced byte-for-byte,151 unique hashes, zero endpoints/no clipped samples. New focused audio fixture 8/8 passed. NetCombat 399, NetSkill 127, NetDoor 49, NetInventory 148:723 checks,4/4 passing, run `Logs/runs/20261122-042155-904`.
- Final visible seed 12345 passes 19 checks:13steps,1jump,1landing,2attacks,32spatial voices;6.97s MP4 has48kHz stereo audio, mean−24.8/peak−8.4dBFS. Ordinary camera/HUD/AI/lighting preserved; paused menu/lifecycle checks happen after recording. No claim of subjective mix acceptance before the user listens. See the report for small presentation fallbacks and intentionally reserved cues.

## Preserved baseline and limitations

Integration19519855b combined Meshy environment/Sol work with36m modules,+3.9m galleries,−1.5m passages and server-owned lever caches; protocol47. Earlier commits0875d13d enforce checkout andcbe51fad4 hold room/cache work. Reports: [HANDOFF_INTEGRATION_20261122](../Tasks/HANDOFF_INTEGRATION_20261122.md), [DOWNLOADED_ARCHITECTURE_20261122](../Tasks/DOWNLOADED_ARCHITECTURE_20261122.md).

Prior integration evidence remains: modules146/146, movement geometry11/11,11 network gates1763checks; combined71gates65pass/6baseline failure groups. Broad EditMode1479total1463pass4baseline failures12skip. Two seeds12345/67890 each14 ambience/lifecycle checks and recorded stereo audio. Full suite is not green; this audio slice uses bounded proof, not a repeat of the76-minute integration run.

Before this sound task, early ambience reviews touched the ordinary Squire profile and may have parked prepared gear in stash. Backups in `Logs/acceptance/ambience-20261122/profile-preservation`; user informed. No pre-run loadout proves a guessed restoration. New live reviews isolate saves; CLI network fixtures cannot enter OfflineProfileSession. Existing user Editor remains open.

Old stash `Preserve audio completion and inherited Unity drift before Sol integration` is retained and must never be blindly popped. Inherited material/Oasis/settings drift is excluded. No engine/package upgrades, legacy writes or push.

## Next three actions

1. User listening/mix review using the 71 s sound reel and actual gameplay recording; tune timbre/gains based on that feedback.
2. Extend audible close-up review to consumables, cache mechanisms and distinct weapons when requested; the entry video does not claim every cue was auditioned in context.
3. Address old schema/attribute/weapon/enemy/HUD test debt only as a separate requested slice. Do not rerun all passing integration gates without relevant changes.
