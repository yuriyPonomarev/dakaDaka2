# DakaDaka — current work

Updated22 November2026. The ONLY development checkout is `C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff`. Legacy `../DakaDaka` is read-only; `../DakaDaka_ci` is a disposable test/build mirror synchronized only from handoff. Never close or automate Play mode in the user's main Editor. Visual reviews use a separate visible mirror Editor and record an MP4 with game audio.

## Current baseline: consolidated Meshy environment and melodic ambience

User authorized combining Sol's models/doors/raid work with enlarged rooms and lever caches, improving the result and committing it. Latest request adds downloaded Meshy architecture and melodic instrumental dark-fantasy ambience. Full record: [HANDOFF_INTEGRATION_20261122](../Tasks/HANDOFF_INTEGRATION_20261122.md).

- Checkout rule committed as `0875d13d`; parent/legacy redirects installed. Room/cache work committed as `cbe51fad4`. Consolidated integration is recorded by the commit containing this page.
- Preserved primary hands/combat,36m modules,+3.9m galleries,−1.5m passages and server-owned lever caches. Protocol47 combines geometry/wire changes. Unified offline loot custody, preserved put-back/body search and added reservation/duplicate/retired-floor protection; restored authoritative owner Parry feedback.
- Rebuilt all30 modules and dynamic door with downloaded pillars/barrels, Meshy reliquaries and cropped arch/stone-facing meshes. Shared crypt stone replaces patchy pillar albedo. Floors, stairs and irregular broken walls retain authored forms. [Architecture sources and budget](../Tasks/DOWNLOADED_ARCHITECTURE_20261122.md). Geometry/render counts increased; no FPS claim.
- New original48s stereo chamber loop: cello/viola, horn, woodwind melody, harp and frame drum. Reproducible, no clipping, circular seam. Missing player AudioListeners repaired; repetitive fire/wind loops removed. Generator/review helper: `Tools/Audio`.
- Final visible seeds12345/67890 each pass14 playback/lifecycle checks; both5.97s MP4s decode with stereo audio. Ordinary camera/HUD/AI/lighting preserved during recording. Post-recording component lifecycle assertions pause simulation. Videos: `Logs/acceptance/ambience-20261122/melodic-review-{seed}`.
- Final module suite146/146 and movement geometry11/11 pass. All11 real-process network gates pass1763 checks. Combined71-entry integration coverage:65 pass,6 retain baseline failures, no new failing assertions; one old retro-readability failure resolved. Broad EditMode:1479 total,1463 pass,4 existing failures,12 skipped. Full suite is not green.
- The final smoke fixture initially failed because new offline player-bots interfered with EnemyAgent measurements. Fixture now waits for real floor admission and parks unrelated bots; runtime combat and existing assertions are unchanged. Failed-seed and12345 reruns each complete264 checks with only2 old HUD failures. Other passing suites were not repeated for this fixture-only correction.

## Preservation and limitations

First three visible checks used the ordinary local Squire profile and may have parked prepared gear in stash. Current Warrior save and backup were preserved under `Logs/acceptance/ambience-20261122/profile-preservation`; no pre-run snapshot establishes the earlier loadout. User informed; no guessed restoration. Subsequent reviews require an isolated mirror `--ember-profile-dir`.

Original audio/incidental Unity drift remains backed up in stash `Preserve audio completion and inherited Unity drift before Sol integration`. Completed audio has been revised; never blindly pop the stash. Excluded material/Oasis/settings drift is preserved outside the integration commit. No engine/package upgrades or legacy gameplay writes. Final content comparison covers392 staged game/source files; diagnostic legacy-scene regeneration was discarded only in the mirror.

## Next three actions

1. User art/mix review of the new environment and full48s arrangement; entry sweeps do not claim every interior/cache close-up or GPU profiling.
2. Address the existing schema/attribute/weapon/enemy/HUD test debt as a separate requested slice; see the report's exact baseline comparison.
3. Continue development only in handoff, using isolated saves and bounded checks; do not rerun the entire integration suite without relevant new changes.
