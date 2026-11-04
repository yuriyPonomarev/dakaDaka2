# CURRENT WORK - 4 Nov 2026

Rewrite this file each slice; history is in `Docs/Archive/`.

## State
- Base HEAD `e6155da8` + three uncommitted slices: **V3a** (Poly Haven props), **V3b** (undead enemies), **V3c** (weapon models + first-person arms). None is committed yet.
- V3c (presentation only; damage, hit volumes, swing trajectories / timings and net messages untouched):
  - 7 Poly Haven CC0 weapon models imported and normalised (`EmberdelveWeaponModels`, prefabs `WV_<id>`, set `Resources/Emberdelve/WeaponViewModelSet`). Mapped: ashen_short_sword and ember_rapier to the estoc, warden_maul to the sledgehammer, emberbulwark_shield to the kite shield. Unmapped (keep blockout, no arms): ember_crossbow, hollow_bow, hollow_claw. Spare: mace, war hammer.
  - First-person arms for the LOCAL player only (`FirstPersonArmsDriver`, child of the weapon anchor, created in Play only): Quaternius Humanoid rig, arms-only skinned mesh (no head / torso / legs exist in it), quilted dark sleeve + weathered skin, 7 states (Idle, Walk, SwingHorizontal, SwingOverhead, Thrust, Block bool, HitReact trigger), shield on a masked left-arm layer, swing clip speed matched to the swing duration from data, wall push-back.
  - Enemies: rusted Poly Haven estoc (Minion), dagger (Rogue), hatchet + kite shield (Warrior/elite) replace the KayKit weapons.
  - **V3b bug fixed:** the enemy Model had a 180 degree yaw; the live Animator (root motion off) already faces +Z, so animated enemies faced backwards in Play. The yaw is now identity and grips are derived in the live frame (`EmberdelveFirstPersonArms.Evaluate`: edit-mode SampleAnimation needs applyRootMotion on, then the root is reset).
- Checks in the open Editor: console errors none; EditMode 629/629 (600 + 29 new in `WeaponViewModelTests`); fast gates 12/12; RetroVisual `CheckViewModels` by reflection 4 checks, 0 failures. Run-All NOT run (scenes rebuilt with BuildEverything). Play-mode feel of the arms NOT verified in a build.
- Evidence: `Logs/quick/v3c/` (`v3c_weapons*.png`, `fp_idle.png`, `fp_swingH.png`, `fp_swingOverhead.png`, `fp_block.png`, `enemies_idle.png`, `enemies_attack.png`).

## Known compromises (V3c)
- The visible FP weapon follows the arm clip; the Sweep_Edge bar and trail still draw the true SwingPatternData damage segment, so they can differ. Left-to-right and right-to-left horizontal swings share one clip.
- The library clips are third person; some swing frames leave the screen or show a sleeve edge, and the Block (pistol guard) turns the blade sideways. Needs an in-game look and probably hand-tuned FP clips.
- Walk reuses the idle hold (no sway yet).

## Decisions in force
- Sword-vs-sword block holds (`Weapon_AshenShortSword.impactResist 15`); the sledgehammer breaks guard. Guard endurance/stamina OFF.
- Realistic CC0 assets instead of KayKit (props V3a, enemies V3b, weapons V3c); the KayKit files stay on disk, unused by enemies.
- Enemy attacks must be blockable and parryable with blade trajectories like the player's (U3C3 parry: block again within 1 s after a sword block = two-hit combo x1.5, must not break the block).

## Next
1. U3C3 parry.
2. Run `Tools/Run-All.ps1 -Set all -EditMode`, play-test the FP arms, then commit V3a, V3b, V3c (separate commits).
3. U4A contested chests over the network, U4B corpse/raid timer/extraction, U4C stash, U3C2.

## Blockers
- none. Repo size: PolyHaven about 203 MB + weapons; Quaternius 46 MB of FBX.
