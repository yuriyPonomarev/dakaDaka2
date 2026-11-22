# Recorded Foley and distance-aware sound — 22 November 2026

## Request and scope

The user rejected the synthetic timbre of bd3975ba0 and requested more natural, spacious sound whose volume depends on distance to its emitter. This slice replaces the effect bank with edits of CC0 recordings and corrects spatial presentation. The existing melodic music is retained. Work is only in DakaDaka_clean_handoff; DakaDaka is read-only and DakaDaka_ci is a disposable verification mirror.

## Implementation

- Recorded footsteps, clothes/leather, weapons, impacts, latches, bottles and nonverbal performances replace mathematical synthesis. Stable cue filenames and Unity GUIDs preserve references. Unreal/magical cues are explicitly described as designed arrangements of recordings, not literal field recordings.
- World sounds use category-specific near/far ranges, smooth distance gain, stereo direction, moving actor attachment and smoothed wall obstruction. Cloth has shorter reach than heavy metal impacts. UI feedback and personal run notifications remain local.
- Actor draw/reload/consumption/equipment audio follows authoritative or existing replicated action edges, including observers. Initial snapshots are silent; presentation neither changes gameplay nor adds protocol fields.
- Short room reflections complement dry recordings. The active listening camera, including a spectator camera, determines distance and room acoustics.

## Verification plan and boundaries

Acceptance requires asset format/provenance/reproduction checks, GameAudioTests and SpatialAudioTests, directly affected NetCombat/NetSkill checks, and a visible mirror review. Ordinary raid footage keeps the normal camera/HUD/AI/lighting. A separately labelled controlled scene compares the same recorded take at different distances, left/right positions and behind a wall, and checks a moving attached emitter. Objective audio checks do not imply subjective user acceptance.

Asset verification: 49 cue families /151 unique mono 48 kHz PCM16 files, 9,208,732 bytes (8.78 MiB), 95.855 seconds total. The 50.520-second preview plays the first variant of every cue. Rebuilding with FFmpeg 7.1 and NumPy 2.2.6 reproduces every WAV and the complete manifest. Independent decoding confirms format, zero endpoints, no clipping (maximum peak 0.81998), all hashes and 132 selected source originals. Evidence: `Logs/acceptance/natural-audio-20261122/reproduction.txt` and `independent-pcm-validation.json`. Final Unity and recorded-output results are below. No primary Editor Play mode or personal profile is used; live reviews require an isolated profile under mirror Logs/acceptance. No engine/package update or push is part of this slice.

## Remaining presentation limits

Short replicated action edges entirely between snapshots may be missed. Other clients do not receive a separate pickup sound intent; no pickup event is guessed from inventory snapshots. This is spatial stereo with raycast obstruction and room reverb, not a custom binaural/HRTF propagation engine.


## Check selection

The planner initially selected all 71 gates because shared presentation/new audio files conservatively route broadly. Reviewed scope is audio assets and presentation hooks only: no damage, physics, inventory custody, protocol or level generation logic changes. Use the two focused audio fixtures, NetCombat and NetSkill plus the recorded ordinary/controlled audio review; a repeat of the previous 76-minute integration suite is not justified by this slice. Inherited material/settings drift and concurrent rule/harness work are outside this commit. The first mirror attempt correctly refused an active concurrent rules-audit run; subsequent execution acquires the shared mirror runner lease before synchronization and holds it through tests and live review.


## Initial Unity evidence and correction

`editmode-bank` passed8/8 and `editmode-spatial` passed5/5. The initial visible seed12345 ordinary raid passed19 checks (13steps,1jump,1landing,2attacks; live enemies inflicted35 damage). The6.97s MP4 contains stereo game audio with measured peak−4.9dBFS. Evidence: `live-12345-initial` in the primary acceptance directory. It is an initial review, not final fixed-contact proof.

The initial controlled reviews failed in the verification helper, not silently passed: stage isolation disabled Unity Recorder's persistent `Unity-RecorderSessions` host, so StartRecording returned true without advancing capture/audio. The first listener callback had no samples; a subsequent WAV capture stayed at its header and old test voices never advanced. The helper now preserves the Recorder host and asserts that each case actually writes mixed audio. It records the engine's shared mixed WAV alongside the movie for independent PCM analysis. Neither failed attempt is distance-output evidence.

Review also found a real runtime defect: voices parented under a moving network owner could drag fixed impact/step positions. WorldAudio now retains each fixed event position and reapplies it while attached voices follow their emitters. The final controlled review adds a moving-pool-owner regression; the final network/live verification below includes this correction. The asset/profile fixtures predate this last position fix; their bank and gain math are unchanged.


## Final verification and delivery

- Final production code: NetCombat399/399 and NetSkill127/127, both pass, `Logs/runs/20261122-052407-639` (467.4s overall). The first build cache entry was deliberately not stored after Unity changed build inputs; the later stable entry was stored. No cached PASS results were used.
- Final visible Unity6000.2.5f1 review, seed12345, fresh isolated `profile-12345-final2`: ordinary raid19checks and controlled source/geometry cumulative38checks pass. Actual Game View1280×720;13steps,1jump,1landing,2melee starts. The ordinary6.97s MP4 retains real AI/HUD/camera/lighting and stereo48kHz AAC audio (mean−25.0, peak−6.3dBFS).
- `VerifySpatialCapture.py` independently reads the shared recorded stereo48kHz float WAV. All8cases and6 output comparisons pass, peak0.40131. The same recorded Impact take measures−30.42/−39.69/−52.55dBFS RMS in matched windows at2/8/18m:22.13dB reduction near-to-far. Wall at8m adds13.67dB attenuation and1100Hz filtering; removal restores clear output. Left/right cases have about20:1 channel-amplitude dominance in the corresponding channel. These numbers describe this controlled take/category, not every sound's reach.
- Moving-emitter playback follows the actor; the fixed impact stays at its event point while the pool owner moves. This specifically covers the network-owner parenting defect found during review.
- Final evidence copied to primary `Logs/acceptance/natural-audio-20261122/live-12345-final2`: ordinary MP4, separately labelled21.27s controlled MP4, mixed WAV,8individual PCM16 WAVs, case timing/source geometry, metrics and output PASS JSON. Both movies decode fully with stereo audio; ordinary and controlled frames were inspected and videos delivered in chat. `CONTROLLED-result.txt` is the source-phase result; `CONTROLLED-audio-result.json` is the completed output-phase PASS.
- All172 changed Unity assets/source files match the tested mirror byte-for-byte (`mirror-asset-comparison.json`). The owned mirror Editor exited and its lock is absent; primary Editor8352 remained responsive. No personal-profile writes, package changes or push.

The previous synthetic preview is superseded. The new50.520s reel contains the first variant of each family; it and the final recordings were delivered for listening. This establishes integration and actual spatial output, not a claim that every cue has been auditioned in every gameplay context or subjectively accepted by the user.
