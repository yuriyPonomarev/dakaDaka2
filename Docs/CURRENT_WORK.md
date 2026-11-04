# CURRENT WORK - 4 Nov 2026

Rewrite this file each slice; history is in `Docs/Archive/`.

## State
- V3d (FP swing animation + shield after weapon swap) is done, uncommitted: see `Docs/Tasks/V3d.md`. V3c bc7a6c28, U3C3 de67cce2 and FIX2 a03c1885 are committed.
- FP arms are solved procedurally onto the weapon rig's pose (the real SwingPatternData trajectory), idle Animator base only. Shield visual bug was visual only (Animator layer weight lost on re-activation / hands dressed before the off hand settled).
- Evidence: `Logs/quick/v3d/*.png`. Tests: `FirstPersonSwingTests` (25).
- Not verified in a build: play feel (reach compression, shield size, hit and block jolts).

## Known compromises
- Block pose follows the true guard blade; hand travel compressed to arm reach; maul model smaller than its damage segment; unused controller states kept.
