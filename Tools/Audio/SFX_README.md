# Original dark fantasy sound effects

`SynthesizeGameSfx.cjs` authors the game's short sound bank from deterministic mathematics. No downloaded audio, recorded human voices, voice cloning, external samples or paid generation is used. Voices are synthetic nonverbal breath/grunt sounds, not claimed human performances. The sound vocabulary is dusty stone, timber, leather, iron, taut strings, breath and restrained mineral resonance.

## Generate and reproduce

Run from the primary checkout:

```powershell
node Tools/Audio/SynthesizeGameSfx.cjs
node Tools/Audio/SynthesizeGameSfx.cjs --verify
```

The first command writes the bank and preview. `--verify` regenerates every sample in memory and compares the installed WAV hashes and complete manifest; it does not rewrite assets. Node 24.21.0 was used for the recorded verification. No npm dependencies are needed. Keep the same synthesis version/seed strings to preserve variants; changing recipes intentionally changes their hashes. The manifest also records the generator SHA256.

Output: `Assets/Emberdelve/Resources/Emberdelve/Audio/Sfx/`. Files use the exact runtime contract `{Cue}_01.wav`, `{Cue}_02.wav`, etc. There are **49 cues / 151 files**: six variants each for Step, StepWood and StepDirt; two each for the five Ui cues; three for every other cue.

| Family | Cue names |
| --- | --- |
| Movement and clothing | Step, StepWood, StepDirt, Jump, Land, LandHeavy, Cloth |
| Equipment and weapons | Equip, Draw, Reload, DryFire, Swing, BluntSwing, Stab, Bow |
| Impacts and guards | Impact, Metal, Parry, BluntImpact, ArrowImpact, ShieldBreak |
| Actors | Hurt, Death, EnemyHurt, EnemyDeath |
| World interaction | DoorCreak, DoorThud, ChestOpen, ChestClose, Lever, SecretStone, Pickup, Coins |
| UI and item handling | UiHover, UiClick, UiOpen, UiClose, UiError, ItemMove |
| Consumables | Potion, Bandage |
| Skills and transitions | SkillDash, SkillCry, SkillShot, SkillBuff, Portal, Extract, Fail, ZoneWarning |

## Sound and mix design

Each cue combines appropriate transient, body and tail layers: band-limited/granular noise, inharmonic iron modes, timber resonances, Karplus-Strong string excitation, leather movement and formant-shaped rough vocal excitation. Source variation changes both random microstructure and pitch/timing within a restrained range. Brief early reflections give physical sounds a shared room character without baking a long cavern tail into every action.

Files are **mono 48 kHz PCM16**, allowing runtime 3D positioning. Import metadata uses PCM, Decompress On Load, no normalization, preload enabled and mono. The whole bank is 14,907,658 bytes (about 14.2 MiB), 155.219 seconds. Unity's runtime memory overhead is additional; the bank does not require every clip to be loaded at once.

There is **no per-file loudness normalization**. Fixed recipe/category gains preserve quiet UI, cloth and footsteps beneath combat and actor cues. Only an attenuation-only safety ceiling is applied if a mix exceeds 0.84 sample amplitude. Representative mean variant peaks: stone steps -13.3 dBFS, dirt steps -16.5, UiHover -27.7, chest opening -12.6, ordinary impact -2.7, heavy impacts/parry approximately -1.5 to -3. These are source-file levels; final in-game balance depends on runtime cue gain, emitter distance, simultaneous voices and music. Do not auto-normalize the quiet assets during import.

Runtime owns trigger timing, positional attenuation, voice limits, cooldowns and random variant selection. The generator does not add runtime calls, ambience, footsteps tied to frame rate, or per-flame loops. Playback and mix acceptance remain the coordinator's responsibility.

## Manifest and review

`Sfx/manifest.json` maps each cue to category, description, authored gain and variant records. A variant includes filename, SHA256, bytes, PCM frame count/duration, peak/RMS dBFS, mean, adjacent-sample delta, endpoint samples, attenuation and pitch factor. RMS is plain whole-clip RMS, not LUFS or a perceptual loudness claim.

Ignored review artifacts are under `Logs/acceptance/game-audio-20261122/`:

- `GameSfx-preview.wav`: 71.086-second reel containing the first variant of all 49 cues at their authored levels, separated by 0.4 seconds. No spoken labels or reel normalization.
- `GameSfx-preview-timings.json`: named cue/category start/end times and reel hash.
- `pcm-validation.json`: independent Python `wave`/`array` decode, hash and numerical checks, including per-file measurements.

Verified: all 151 WAV headers are mono 48 kHz PCM16; all hashes match a fresh deterministic regeneration; all 151 hashes differ; all endpoints are exactly zero; independent RMS matches manifest; no clipped PCM samples. Measured peak range is -28.575 to -1.515 dBFS. Files have DC rejection and short endpoint fades. These measurements do not establish subjective sound quality, runtime trigger coverage, concurrency performance or a completed listening review. No Unity run was performed by the sound-bank worker.

## Runtime integration

The completed presentation coverage, focused validation and remaining limitations are tracked in `Tasks/GAME_AUDIO_20261122.md`. The bank worker’s measurements above remain asset-level evidence; runtime and recorded verification are separate.
