# Recorded dark-fantasy Foley bank

The active sound bank uses selected CC0 recordings. The rejected mathematical synthesis is retired; `SynthesizeGameSfx.cjs` now exits before writing anything. Recorded humans remain human performances without pitch/formant replacement. No generated noise, oscillator or synthetic sweep is used in this replacement bank.

## Rebuild and reproduce

Run from the primary checkout:

```powershell
python -X utf8 Tools/Audio/BuildRecordedFoley.py
python -X utf8 Tools/Audio/BuildRecordedFoley.py --verify
```

Requirements: Python, NumPy (2.2.6 for this build), FFmpeg 7.1. Supply `--ffmpeg <executable>` or the `FFMPEG` environment variable if FFmpeg is not on PATH. The builder has a read-only fallback to the existing workstation FFmpeg binary in the legacy checkout; it never writes there. Exact reproduction was checked with `ffmpeg version 7.1-essentials_build-www.gyan.dev`. A different decoder/resampler version can change PCM bytes and should be an intentional rebuild.

The default command rebuilds from committed originals in `SourceArt/Audio/RecordedFoley/`, without a network connection or the downloaded archives. `--verify` regenerates all outputs in memory and compares their hashes and the complete manifest without rewriting assets. The one-time `--import-from Logs/acceptance/natural-audio-20261122/sources` option copies only recipe-selected original files and license evidence from the already-extracted packs. It is not needed for ordinary rebuilds.

Output: `Assets/Emberdelve/Resources/Emberdelve/Audio/Sfx/`. Existing filenames and Unity GUIDs are preserved: **49 cues / 151 clips**, six variants for Step, StepWood and StepDirt, two for each of the five Ui cues, three otherwise. Mono 48 kHz PCM16; existing import metadata remains PCM/Decompress On Load without normalization.

## Recordings and editing

| Cue families | Recorded material |
| --- | --- |
| Step, StepDirt | Six Fantozzi stone/sand footfalls each, alternating left/right performances |
| StepWood | Actual footwear with a quiet wooden-bowl contact layer; designed wood-step Foley, not a claimed wood-floor field recording |
| Jump, Land, LandHeavy | Vehicle leather boot jump and Fantozzi sole contacts; heavier landing adds a recorded bodily contact |
| Cloth, Bandage, ItemMove, Equip | Wareya fabric pulls and Vehicle leather/buckle movements |
| Swing, BluntSwing, Bow, Draw, SkillShot | Actual seax/axe air passes, English longbow/recurve release and draw textures |
| Impact, Stab, BluntImpact | Independent.nu bodily hits with restrained recorded leather/material contact |
| Metal, Parry, ShieldBreak, ArrowImpact | Real blade contacts, wooden contacts and recorded material crunch/chain |
| Hurt, Death, EnemyHurt, EnemyDeath, SkillCry | Individual HaelDB male performer grunts and yells; unchanged pitch |
| DoorCreak, DoorThud, ChestOpen, ChestClose | qubodup door recordings, with Vehicle lockbox operations for chests |
| Reload, DryFire, Lever | Recorded quiver handling, crossbow mechanism, latch and chain |
| SecretStone | Real scrape/grit/chain arranged into a heavy moving mechanism; not a stone-door field recording |
| Pickup, Coins, Potion, UI | Recorded leather, coins, keys, clay bottle uncorking and Wareya quaff; small dry material contacts for UI |
| Portal, Extract, Fail, SkillBuff, ZoneWarning | Restrained designed arrangements of recorded iron/glass/coins; fictional events are not claimed literal real-world recordings |

The builder uses explicit source windows. Long medieval performance files are cut to single releases/passes; Wareya sequences use the author's exact 37,800-frame spacing at 44.1 kHz. Outer quiet margins are trimmed after the explicit take selection. DC removal and short 2 ms attack / 15 ms release fades prevent edit clicks. Internal source dynamics are retained. No artificial room tail is baked in; runtime owns positional attenuation, occlusion and room reflections, while a recording may retain its original room decay.

Only SecretStone's scrape layer is slowed to 0.87 playback speed. Portal reverses one recorded iron decay. Other material and voice recordings retain their original pitch. Multiple layers represent one action (for example, buckle plus leather or paired landing soles); deliberate lock/lid and uncork/quaff combinations retain their short action sequence. Final longest variants are coin settling for Extract, not a series of repeated weapon performances.

## Sources and license evidence

`SourceArt/Audio/RecordedFoley/LICENSES.md` records authors, primary source pages, download URLs and short CC0 license evidence. `provenance.json` additionally records source-page and original-archive SHA256 values, and the size/hash of every retained original. The 132 selected original recordings total **75,274,790 bytes**; original archives and full website HTML remain only in ignored Logs. This project does not claim authorship of those performances. CC0 is the selected alternative for HaelDB's multiply licensed pack.

`Sfx/manifest.json` maps every output to its exact original filename/hash, explicit original take window, final used source sample bounds, per-layer gain/offset/speed/reversal and final mix gain. Source bounds are specified against deterministic decoded 48 kHz audio and also in seconds against the original timeline. Original compressed/lossless files are preserved unchanged, including the `.mp3.flac` names supplied by Independent.nu; conversion to FLAC did not restore lost MP3 information.

## Levels and measured checks

Category calibration keeps UI and cloth below combat, rather than maximizing every cue. A static gain targets category RMS and a category peak ceiling; no compression or clipped limiting is applied. Quiet UI-hover peaks at about -21.4 dBFS, cloth at -14 dBFS, near footsteps at -9.4 dBFS, and large combat contacts peak no higher than -1.72 dBFS. Runtime gain, distance and simultaneous sources still determine the in-game mix.

The generated bank is **9,208,732 bytes (about 8.78 MiB), 95.855 seconds**. Clip durations range **0.153–4.171 seconds**; `Impact_01.wav` is 0.578 seconds. All 151 hashes are distinct. `--verify` passed byte-for-byte regeneration and complete manifest comparison. Every output has zero first/last PCM samples and a peak below 0.9. The worker inspected take envelopes and separated bursts to avoid whole multi-performance recordings in one-shot cues.

These are asset-level measurements, not a listening acceptance claim. The worker did not audition the entire bank, run Unity or verify final in-game spatial/mix quality; coordinator playback and the user's hearing remain the acceptance step.

## Preview

Ignored artifacts under `Logs/acceptance/natural-audio-20261122/`:

- `RecordedSfx-preview.wav`: **50.520 seconds**, first variant of all 49 cues, unchanged authored levels, 0.4-second gaps.
- `RecordedSfx-preview-timings.json`: named start/end times, categories and reel SHA256; no spoken labels.

The previous synthesized preview under `game-audio-20261122` is historical evidence and does not represent this bank. Runtime coverage and coordinator checks belong to `Tasks/NATURAL_AUDIO_20261122.md`.


## Spatial verification

`NaturalAudioReview.cs` is a mirror-only visible Editor helper. It requires an isolated profile, records ordinary raid movement/combat first, then creates a separately labelled controlled acoustic scene. The controlled Movie and Audio recorders share Unity Recorder's rendered audio mix; `CONTROLLED-cases.json` records frame windows and source geometry. An optional `-emberAudioReviewTag` keeps reruns in fresh evidence folders.

After the owned Editor closes, run `python Tools/Audio/VerifySpatialCapture.py <absolute-live-output-folder>`. It reads the captured stereo float WAV and verifies near/mid/far output, left/right panning and wall attenuation/restoration, then exports individual PCM16 cases. A source/geometry PASS alone is not a recorded-output PASS. The controlled scene does not claim ordinary gameplay acceptance; the separate raid recording does.
