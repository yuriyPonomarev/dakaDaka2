# V3c: Poly Haven weapons and first-person arms (4 Nov 2026)

Status: implemented, uncommitted. Presentation only.

## Done
1. Import of antique_estoc, sledgehammer_01, ornate_medieval_mace, ornate_war_hammer, ornate_medieval_dagger, hatchet, kite_shield (Poly Haven CC0, 1k): manifest + SHA256, URP Lit materials, per-weapon base colour (`EmberdelveWeaponModels.Specs`).
2. Normalisation by measurement (long axis, blade tip = end away from the cross-guard, heads = larger cross-section end, shield thin axis = face): grip at the prefab origin, tip +Y, face +Z, length in metres in the spec. Mapping and unmapped reasons in `WeaponViewModelSet`.
3. First-person arms (`FirstPersonArmsDriver`, `EmberdelveFirstPersonArms`): local player only, states Idle / Walk / SwingHorizontal / SwingOverhead / Thrust / Block / HitReact, driven from existing client state (`PlaySwingVisual`, `SetGuardHeldVisual`, `Health.Damaged`, ground speed). Weapon on `Grip_R` (right hand), shield on `Grip_L`.
4. Enemies: rusted Poly Haven estoc / dagger / hatchet / kite shield; model yaw fixed (see CURRENT_WORK).
5. Tests: `Assets/Emberdelve/Tests/EditMode/Visual/WeaponViewModelTests.cs` (29); `EnemyVisualQuaterniusTests` adapted (rusted Poly Haven weapons, eyes checked in the live pose); RetroVisual gate `CheckViewModels`.

## Verification
Refresh + compiling=false; console errors none; EditMode `Emberdelve` assembly 629/629; fast gate suite 12/12 (`Logs/quick/v3c/summary.txt`); evidence PNGs in `Logs/quick/v3c/`.

## Not verified / owed
Run-All, Play-mode feel of the arms in a build (clip choice, framing, near clipping), ranged weapons (no arms yet).
