# V3d - first-person swings follow the real trajectory; shield after weapon swap

User report: (1) the FP swing animations are bad (third-person clips leave the screen, block turns the sword sideways, LR and RL share one clip); (2) the warrior spawns with sword + shield, after switching to the crossbow and back the shield is gone.

## What changed (presentation only)
- `FirstPersonArmsDriver` no longer plays swing clips. The Animator keeps only the calm Idle base pose; every frame (execution order after `WeaponVisualRig`) an own analytic two-bone solver (`SolveArm`, public, usable in edit mode) puts the right hand on the pose of the weapon rig, i.e. exactly what `SwingPatternData.Evaluate` gives (windup, active, recovery, riposte strikes, guard pose). Blade ROTATION is the rig's (yaw/pitch/roll of the pattern). Hand POSITION is the pattern base brought to arm reach (`ViewHand`: offsets from the guard base scaled 0.42/0.35/0.40 around a viewmodel centre, the pattern bases travel up to 1 m from the eye, an arm is 0.55 m x 1.3) and clamped into the camera frustum (`ClampToView`, 90% of the half FOV, beyond near+0.15).
- Left hand: on the shield (rest / raised while the block is held, shield shown at 0.72 scale), on the second grip for a two-handed weapon, else idle pose.
- Model scale = clamp(damage segment / model length, 0.9, 1.5). `Sweep_Edge` renderer is hidden in FP (the model is the blade); the swing trail stays.
- Procedural: walk bob + breathing, hit-react jolt (camera-relative, away from the source), block-impact jolt on `DamageInfo.Blocked` with Amount 0 (FIX2 held block), wall push kept. Arms stay hidden for bows/crossbow (no model).
- Shield after swap: `ClassLoadout.EquipState` now re-dresses the hands (`SyncArms`) after `CompleteWeaponSet`, when the set's off hand is final; `SetWeapons` hides the old models at once; the left arm is solved to the shield every frame (no Animator layer weight, which is reset when the arms object is deactivated for the crossbow and reactivated).

## Shield bug: root cause
Gameplay was never broken: `PlayerRig.OffhandWeapon` falls back to the class shield and `DefenseProfile.Resolve(class, sword, shield)` has the shield again after the crossbow set (test `Defense_KeepsTheShield...`). Only the FP visual: the off-hand shield was shown through the masked Animator layer weight (`SetLayerWeight`, set while the arms object was inactive for the crossbow) and dressed before the rig's off hand was settled; the weight is lost on re-activation. Not reproduced in Play (no Play session run); the fix removes both possible causes.

## Tests (`Tests/EditMode/Visual/FirstPersonSwingTests.cs`, 25)
Per SwingPatternData in the data: blade direction = pattern direction at 7 phase times; sweep sign (yaw LR/RL, pitch overhead top-down, forward thrust); LR and RL distinct; grip inside the camera frustum at 17 times of the swing and at the target; block raises the shield and holds the guard blade of `GuardBladePose`; shield visual survives sword -> crossbow -> sword rounds; defense keeps the shield; model scale matches the damage segment within the clamp.

## Evidence
`Logs/quick/v3d/swing_*.png` (rest, windup mid, windup end, active mid, active end, recovery mid), `block_and_after_swap.png` (rest, block with shield, block sword only, after swap rest, after swap block, maul).

## Compromises
- Block = the rig's true guard pose (blade about 30 degrees up-forward; the guard box is gameplay). Horizontal-diagonal would need a data change.
- Hand travel is compressed to arm reach, so the model is nearer than the damage segment; direction and sweep are true.
- Maul model smaller than its damage segment (scale capped at 1.5).
- Controller states Swing*/Block/HitReact are kept (V3c contract) but unused.
- Play feel not verified in a running build.
