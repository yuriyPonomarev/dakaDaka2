# Handoff consolidation and ambience — 22 November 2026

User requested continuing Sol's interrupted dark-fantasy ambience, reviewing/improving uncommitted work and committing it. After discovering divergent checkouts, the user explicitly chose to combine everything in `DakaDaka_clean_handoff` and reinforced that all future development must use that checkout.

## Preserved source and ownership

- Primary pre-integration source: `89fed9ad6`; verified room/cache work committed as `cbe51fad4`.
- Mandatory checkout rule committed as `0875d13d`, with local redirects also installed in the workspace root and legacy checkout. Legacy `DakaDaka` is read-only for gameplay, generation and commits.
- Sol committed source: `d3a0b2dc3`. Although the histories have different commit IDs, primary `94471be302` and legacy `e108a54af` have identical trees. Applied the legacy delta from that common content with three-way merging, not a blanket directory replacement.
- Uncommitted ambience and inherited Unity serialization drift were preserved in stash `Preserve audio completion and inherited Unity drift before Sol integration`. Audio was recovered from it after merging. Original legacy files remain intact. The unused generated pedestal candidate with unavailable rights metadata was not imported; the actual original in-game pedestal was recovered from committed sources.
- Historical Sol status is retained in `Tasks/SOL_WORK_BEFORE_INTEGRATION_20261122.md`. Its old screenshots and test logs validate the legacy checkout only.

## Integration decisions

- Keep 36 m modules, 8 m enclosure, reachable +3.9 m balconies, −1.5 m passage, cache volumes/anchors and lever authority. Rebuild all 30 room prefabs from merged builders.
- Recover Meshy chest/shrine/sconce/community models, weathered brazier and pedestal, and compact animated doors. Adapt perimeter art/statue placements and folded walls to expanded rooms; require actual support under new floor statues.
- Preserve main player hands/combat/skeleton improvements and Cinder Warden controller/source manifest while adding Sol portal spectators, team-floor transitions and differentiated block/parry cues.
- Protocol 47 rejects clients with either previous incompatible geometry/wire format.
- Merge two competing offline loot services into one `OfflineLootService`. Preserve main put-back/searchable bodies and add Sol floor-retirement/access safeguards. Reserve death-loot custody before clearing the source and reject duplicate gear publication. The old Royale service is a forwarding adapter.
- Complete the original 48-second stereo ambience; remove repeated per-flame ticks, keep spatial footsteps/combat/door sounds, smooth the loop seam, retain shared clip ownership, and save its reproducible generator under `Tools/Audio`.
- Restore the client owner's Parry toast from the authoritative combat report without replaying world effects.
- Live playback exposed a missing AudioListener in authored player cameras. Add it to the level builder and migrate the three raid scenes plus the network player prefab; existing bot/server stripping disables their listeners. Preserve the ordinary camera and audio mix.
- Exclude incidental material float/legacy-map serialization and local Pipeline configuration from integration commits. Preserve meaningful retro-readability settings recovered from Sol.

## Validation

The independent room/secret code review found no new defect before integration. Previous room-only evidence: 146 level cases and 924 affected network checks passed; these do not validate this later merge.

Historical first drone asset: 48 seconds, 2 channels, 48 kHz PCM16; peak −5.46 dBFS, RMS −17.64 dBFS, zero clipped samples. Superseded by the melodic revision below. Original measurements remain at `Logs/acceptance/ambience-20261122/audio-metrics.json`.

Planner: `Logs/acceptance/ambience-20261122/integration-plan.json`. A full integration run is justified by the combined geometry, protocol, portal, combat-report, inventory-custody and asset changes. The final live review must use a visible mirror Editor and an MP4 with actual game audio, retaining the ordinary camera/HUD/AI/lighting.

Merged door/module rebuild passed (`rebuild.log`, Unity exit0). Broad EditMode completed in274s:1479 total,1463 passed,4 failed,12 skipped, no compiler/runtime exception diagnostics. The four failures exactly match the room-only baseline: Quaternius mesh vertex count8462/30577, unregistered CinderWarden variant, raised crossbow left palm0.081631 vs0.08, and unmapped cinder_club. Audio import and new custody regressions passed. Results: `Logs/acceptance/ambience-20261122/editmode`.

The first visible run advanced real gameplay and loaded the stereo loop, but failed the active-listener check before recording. This exposed the authored-camera defect above; `live-editor.log` is failed evidence, not acceptance.

Corrected visible run (`live-editor-v2.log`, Unity exit0) passed13 checks, including ordinary Solo raid/map validation, advancing simulation, one active listener despite offline bots, loaded stereo clip, absence of old fire/wind clips, loop crossing, disable/re-enable fading and owned-source cleanup without destroying the shared clip. The MP4 was opened in the current chat and its rendered frame inspected. FFmpeg decoded the complete13.97s1280x720/30fps recording successfully, with AAC48kHz stereo audio, mean−27.6dBFS and peak−16dBFS. This proves working recorded audio; subjective final mix/level acceptance remains for the user. Evidence: `Logs/acceptance/ambience-20261122/live/ambience-loop-in-raid.mp4`, `raid-frame.png`, `result.txt`.

The user then requested replacing the remaining visible placeholder architecture with downloaded Meshy models and a more melodic instrumental ambience. The obsolete full run at `Logs/runs/20261122-014545-766` was intentionally stopped after16/71 gates (15 passed; existing RarityLadder7 failures). It is incomplete, not a completed all-run.

The replacement original chamber arrangement remains48s/stereo48kHz; six synthesized instrumental timbres,12 bars at60 BPM and three melodic phrases. Peak−3.50dBFS, RMS−14.74dBFS, no clipping, seam steps6/6. Hash and measurements: `Logs/acceptance/ambience-20261122/melodic/audio-metrics.json`. Regenerating from the checked-in generator produced identical WAV bytes.

Downloaded architecture rebuilt successfully (`melodic/rebuild.log`): all30 expanded modules and the dynamic door frame. Reused owner-provided pillar/barrel FBXs, existing closed Meshy chest as a funerary reliquary, and cropped OvergrownArch stone geometry for crowns/flat wall facing. Pillars retain their carved Meshy geometry but use the shared world-space crypt stone instead of the patchy baked albedo. Authored stairs/floors and broken outdoor silhouettes remain; this is not a claim that every mesh is imported. Geometry budget and exact source records: `DOWNLOADED_ARCHITECTURE_20261122.md`. Independent read-only review found no actionable collider/layer/cache-parent/reapplication defects.

Final visible melodic reviews on seeds12345 and67890 each passed14 checks and exited0 (`melodic/review-12345.log`, `melodic/review-67890-v2.log`). Both ordinary first-person sweeps recorded5.97s at1280x720/30fps with AAC48kHz stereo. Complete FFmpeg decode passed; mean/peak levels were−24.3/−5.8dBFS and−24.1/−4.8dBFS. Frames were inspected and both videos opened in the chat. Paths: `melodic-review-12345/ambience-loop-in-raid.mp4` and `melodic-review-67890/ambience-loop-in-raid.mp4` beneath the acceptance folder. These prove entry-area presentation, active simulation and actual seamless game playback; they do not represent a manual traversal of every cache/interior or a GPU benchmark. Audio mix acceptance remains subjective.

The earlier20s melodic recording and first67890 lifecycle check failed after combat killed the stationary player; the music correctly faded with death. The final helper keeps ordinary AI throughout recording and pauses simulation only afterwards for isolated component lifecycle assertions. It verifies the player is alive before pausing. Failed logs remain preserved.

Profile isolation correction: the first three visible runs used the ordinary local Squire profile. Entering Squire parks prepared owned gear in the stash; the current Warrior profile and its backup both contain7 stash items/gold34 with empty ready inventory, but no pre-test snapshot establishes its earlier loadout. Both files were preserved under `Logs/acceptance/ambience-20261122/profile-preservation`; no speculative restoration was made. The user was informed. Subsequent runs use separate `--ember-profile-dir` saves, and the helper refuses paths outside mirror `Logs/acceptance/`. Personal saves are not committed.

Final module suite (`melodic/module-tests`) passed146/146 with no skips or diagnostics in104.28s, including expanded traversal, height navigation and staged floor placement. The separate `Emberdelve.Tests.Level` namespace passed11/11; its wrapper initially expected146 because the module namespace order was mistaken, so that wrapper correctly reported insufficient selection. The correct `Emberdelve.Level.Tests` selection above was then run without repeating the11 movement cases.

Final planner output: `melodic/final-plan.json`. Remaining55 integration gates completed at `Logs/runs/20261122-022845-642` in4571.7s. All11 real-process network gates passed, including NetCombat398, NetDoor49, NetFinal172, NetExtraction136, NetInventory148, drop21, NetMovement198, NetRoyale372, NetProof35, NetSkill127 and NetAdmission107 (1763 checks). Visual passed216. RetroVisual improved from4 to3 existing failures because the readability preset is now correct. The first16 completed gates from the interrupted run cover unchanged rules/data and are combined rather than repeated.

The last smoke gate exposed fixture interference:225 checks/41 failures instead of the prior262/2. The modular scene now defaults to offline Solo, so new Royale player-bots attacked during a fixture that parked only EnemyAgent monsters. Foreign bow/sword events polluted exact authority counters and killed the player during guard measurements, causing the remaining failures to cascade. Independent source/log review confirmed this cause. The fixture now waits for the real initial floor admission, disables further offline population updates and parks only unrelated Royale bots before measurements. Existing combat assertions, player health, enemy AI and runtime combat remain unchanged; two explicit fixture-precondition checks were added. Focused reruns on the failed seed10059453682698409836 (0x8B9A5BD3BFBBD76C) and seed12345 each completed264 checks with only the same2 pre-existing HUD failures. Evidence: `Logs/runs/20261122-034940-545` and `Logs/runs/20261122-035243-330`; actual seed selection was confirmed in the logs. This fixture-only correction does not warrant repeating the passing runtime/network suites.


## Final combined result

All71 required gate entries are covered across the completed parts and focused corrected-fixture reruns:65 pass,6 retain existing failures. Compared with baseline `Logs/runs/20261121-192920-176`, there are no new failing assertions; the retro-readability preset assertion is resolved. Remaining failures: RarityLadder7, AttributeSkill12, ContainerSnapshot23, Slots3, RetroVisual3 and RuntimeSmokeTest2. The corrected smoke has264 checks (two additional isolation preconditions), and both seeds produce the same remaining HUD messages. Full suite is not green. Machine-readable comparison: `Logs/acceptance/ambience-20261122/melodic/combined-summary.json` (44,841 checks across the selected final gate entries; not a count of unique assertions).

All11 real-process network scenarios passed1763 checks; the final146 module cases and11 movement-geometry cases pass, as do both recorded14-check melodic live reviews. Broad EditMode retains the4 documented failures/12 skips. No runtime or asset changes followed those passes; the only later code edit isolates the smoke fixture.

A final content comparison verified392 staged game/source files against the mirror. The visual gate rebuilds the separate legacy Level01 scene as a fixture; that generated scene was restored from primary after the run, together with two byte-only line-ending differences in Retro_Fullscreen and ChestVisualSet. These diagnostic mutations were not copied into development. Primary gameplay remained unchanged. Evidence: `melodic/final-source-comparison.json`.

Reviewed integration is recorded by the commit containing this report. Incidental Unity material/Oasis/settings serialization remains outside the commit; the original stash is retained as a backup and must not be blindly popped over the completed audio. Subjective art/mix approval, comprehensive interior close-ups and GPU performance profiling are separate follow-up work, not claimed by these checks.
