# Recorded Foley runtime bank

151 mono 48 kHz PCM16 clips in 49 existing cue families, built from selected CC0 recordings. Rebuild with `python -X utf8 Tools/Audio/BuildRecordedFoley.py`; compare with `--verify`. Existing WAV names and Unity GUIDs are preserved.

`manifest.json` maps every output to original source filename/hash, take window, processing and measured levels. Source authors/licenses are in `SourceArt/Audio/RecordedFoley/LICENSES.md`; the full editing and verification notes are in `Tools/Audio/SFX_README.md`.

Wood steps, stone mechanism and fictional transition cues are identified as recorded-material arrangements, not false field-recording claims. The old mathematical synthesizer is retired. Runtime owns spatialization and room acoustics. Subjective listening acceptance is separate from numerical validation.
