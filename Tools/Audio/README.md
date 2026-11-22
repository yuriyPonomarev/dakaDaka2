# Dark fantasy ambience

Original deterministic synthesis recovered from Sol's interrupted 21 November work and completed in the primary handoff checkout on 22 November 2026. No downloaded audio, reference-game recording, external samples, model service or API credits are used.

Regenerate from the primary checkout:

```powershell
node Tools/Audio/SynthesizeDarkFantasyAmbience.cjs Assets/Emberdelve/Resources/Emberdelve/Audio/DarkFantasyAmbience.wav
```

The user's follow-up replaces the drone with a melodic 48-second stereo PCM16/48 kHz chamber arrangement. Twelve bars at60 BPM move through D minor, B-flat, G minor and A dominant. Synthesized cello/viola, low horn, breathy woodwind melody, harp and sparse frame drum form six timbres. Three melodic phrases and restrained plucks keep space for game cues. Notes and room-reflection tails wrap around a circular buffer, preserving the loop without truncation. The audio importer keeps stereo and compresses in memory; one listener-relative source fades between outdoor/covered room levels. Short per-flame crackle emitters have been removed. World footsteps, combat and door cues remain spatial.

`AmbienceIntegrationReview.cs` is a mirror-only verification helper, not a runtime game script. Copy it to `DakaDaka_ci/Assets/Editor/`, then invoke `AmbienceIntegrationReview.Build` in batch mode for the merged door/module assets. Invoke `AmbienceIntegrationReview.Run` in a separate visible mirror Editor for an ordinary Solo raid recording. It preserves the real player camera, HUD, AI and lighting, starts capture near the audio loop boundary, and checks playback/disable/re-enable/resource ownership. Never run this helper in the primary Editor.

The melodic audio's source hash and measured peak/RMS/seam are recorded under `Logs/acceptance/ambience-20261122/melodic/audio-metrics.json`. Peak−3.50dBFS, RMS−14.74dBFS, no clipping, loop-edge sample steps6/6. Runtime recording and test results are separate evidence; generation alone is not an audio acceptance claim.

The first live check migrated the existing raid cameras and network player prefab to include the missing AudioListener. Future player cameras receive it from `EmberdelveLevelBuilder`; remote/bot presentation disables it as usual. The old drone recording passed13 playback/lifecycle checks; it does not validate the later melody/art revision. New reviews accept `-emberdelveRaidSeed N` and save a6-second first-person look sweep with game audio under `Logs/acceptance/ambience-20261122/melodic-review-N`. They require `--ember-profile-dir` pointing to an isolated directory under the mirror's `Logs/acceptance/`; the helper refuses ordinary personal saves. Camera/HUD/AI/lighting remain ordinary during recording. Only the subsequent component disable/re-enable/destruction checks pause simulation, preventing combat death from invalidating the live-player precondition. The complete helper has14 checks.
