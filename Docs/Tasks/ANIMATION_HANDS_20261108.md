# Hands and guard presentation — 8 November 2026

> Historical implementation note. Current acceptance and remaining work: [CURRENT_WORK](../CURRENT_WORK.md) and [raid acceptance](RAID_UI_ACCEPTANCE_20261108.md). Status statements below describe the original slice.


## Changes

- `FirstPersonArmsDriver`: preserve the scaled palm-to-grip offset in the two-bone solver. Previously the 1.3x mannequin scale was removed from this offset, making the grip miss its requested target. Reject degenerate bone lengths and handle a pole parallel to a vertical target.
- Two-handed melee now solves the left palm grip onto the handle with the weapon rotation, instead of targeting the wrist while retaining its idle rotation.
- Rendered hands ease into and out of blade guard over 0.14 seconds. This happens in the arms driver only; the weapon rig's gameplay guard box remains immediate. Swings bypass the blend and weapon changes clear it.
- Offline and network enemy drivers clear a pending Animator Hit trigger before Attack, preventing a flinch queued earlier in the frame from hiding the telegraph. Death clears both pending action triggers.

## Verification

- Added three `FirstPersonSwingTests.ArmSolver_PlacesScaledPalmGripOnTarget` cases, checking position, orientation and retained bone lengths at scales 1, 1.3 and 0.75.
- `git diff --check` passed for touched code.
- Editor connection attempt failed with socket access error 10013. Compilation and Unity tests remain pending coordinated verification; no visual-quality acceptance is claimed.
- Run `FirstPersonSwingTests`, the visual test assembly, Hands/Visual and NetCombat gates. Review raising/lowering guard, successful block recoil, two-handed attacks, and enemy hit immediately before windup in motion.

## Remaining

Enemy attack playback still assumes a 0.5 second swing instead of receiving authoritative phase timing. This needs a separate timing/protocol change and in-motion comparison with the damage sweep. Existing assets are preserved; no replacement attack clips were generated in this slice.
