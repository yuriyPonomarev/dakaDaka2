# Cohesive game sound pass — 22 November 2026

Development: DakaDaka_clean_handoff only. Prior baseline 19519855b. User requested jumping, walking, impacts and the rest of the game soundscape. Existing melodic 48 s ambience is retained. No audio service/paid generation/external sample library was used.

## Implementation

- Original deterministic49-cue/151-variant mono 48 kHz PCM16 bank,14,907,658 bytes;6 variants per walking surface,2 per UI cue,3 otherwise. Generator, source descriptions, measurements and manifest in Tools/Audio and Resources/Emberdelve/Audio/Sfx.
- Shared lazy-loaded clips, no destruction of imported assets. Independent variation avoids consecutive repeats without consuming gameplay RNG. Bounded24 spatial+8 owner/UI voices. World attenuation/geometry low-pass retained; UI works in paused menus and lobby with a fallback listener only when the lobby lacks one.
- Stone/wood/earth walking, crouch/speed cadence and gain, upward takeoff, clothing and light/heavy landing. Teleports, floor changes and inactive states reset motion; controller/physics/authority untouched.
- Authority-driven all-player bow release; sword/blunt/stab impact families, arrows, blocks/parries/guard break, hurt/death. Client hit sounds deduplicate numbered server events independently of blood eligibility. Local actions cover draw, reload, actual empty attempts, held consumable start, equip, extraction/death, descent and zone warnings.
- Chest opening, lever/stone cache, doors, inventory windows/transfers, pickup/coins and accepted skill activations. Initial chest/door/skill snapshots are silent; host/client paths avoid replaying the same accepted action. Buttons use pointer/submit/onClick events, including existing hand-built buttons.
- Remote owner now receives the same FeedbackDirector/PlayerActionAudio as Solo. NetOwnerCombatView retains its existing ambience binding and end-of-run suppression. No network fields/protocol/gameplay decisions changed. Equipment snapshot callbacks are coalesced to the final active weapon; item/armor handling uses accepted inventory cues. WorldAudio teardown stops and releases its owned voice objects while preserving imported clips.

## Verification selection

Checkpoint Logs/harness/before-game-audio-20261122.json. Planner conservatively selected all 71 gates due shared presentation/new assets/tooling. This slice changes sound presentation only, so selected new GameAudioTests, NetCombat/NetSkill/NetDoor/NetInventory and a visible ordinary raid recording with real movement/combat inputs. No movement rules, item custody, damage math, generated map or asset geometry changed; the full76-minute previous integration suite is not repeated.

## Evidence (updated after runs)

- Generator --verify:151 hashes and complete manifest match a fresh in-memory regeneration. Independent PCM decode:151 unique hashes, mono 48 kHz PCM16, endpoints0, no clipped samples, RMS matches manifest; peaks−28.575..−1.515dBFS.
- EditMode audio fixture 8/8 passed (editmode-audio-2); all variants load with configured import settings, finite/unclipped data, no repeated selection/gameplay-RNG mutation, shared clip lifetime, event window and surface names.
- Initial compile found Unity6 moved preloadAudioData to sample settings; corrected, clean recovery exit0. No expectations reduced.
- Four real-process gates pass 723 checks: NetCombat 399, NetSkill 127, NetDoor 49, NetInventory 148. Run Logs/runs/20261122-042155-904 (898.4s total). Existing network packet format/protocol47 preserved.
- Final visible mirror review seed 12345 passes 19 checks:13 real walking requests,1 jump,1 landing,2 player melee starts and32 spatial voices. Ordinary camera/HUD/AI/lighting stay enabled during 210 recorded frames. Post-recording paused checks cover inventory open/close audio, stopping voices and destroying owned sources without destroying shared clips. Mirror exit0; main Editor8352 remains open.
- The 6.97 s MP4 decodes to 48 kHz stereo AAC; full decode reports mean−24.8dBFS, peak−8.4dBFS. Two recorded frames inspected: normal first-person raid, coherent movement, enemy active, HUD visible. No visual/layout edits in this slice. Evidence and video copied to primary Logs/acceptance/game-audio-20261122/live-12345.
- After the network run snapshot, final local changes coalesced transient weapon-restore callbacks and released WorldAudio-owned source objects on destruction (plus enum formatting). These are covered by the final compile/live menu/lifecycle proof; passing network simulations were not repeated for those presentation-only changes. The audio bank/variant/event-window fixture inputs did not change after 8/8 passed.

## Limits

Original synthetic Foley and nonverbal vocal textures, not recorded human performances. Numerical checks do not establish subjective listening quality. ChestClose is banked but intentionally unused because chests never close in gameplay. A cancelled consumable is not reported as completed. Reload-end latch requires observed ammo growth; a separately delayed client ammo update can omit that small latch. Client melee timbre uses current server-authored weapon presentation, not a new weapon field in each hit packet; enemy fallback remains generic. Client arrow-wall sound requires level geometry within8cm of the server-reported endpoint because the existing packet has no end reason; authority uses exact Wall reason. A range endpoint grazing geometry may therefore sound like contact on a client. Small same-floor corrections within ordinary movement speeds cannot always be distinguished from motion by presentation.

Visible-review saves stay under mirror Logs/acceptance via --ember-profile-dir. Network CLI fixtures cannot open OfflineProfileSession (NetRole guard); they do not enter offline preparation. Main user Editor and personal profile remain untouched. All inherited material/Oasis/settings drift and old stash are excluded from this slice.

Final content comparison: all 337 game/source assets in the explicit 343-file slice match the verified mirror byte-for-byte. Documentation, generator/readme and the stored review helper comprise the remaining six paths. No inherited material/settings paths are included.
