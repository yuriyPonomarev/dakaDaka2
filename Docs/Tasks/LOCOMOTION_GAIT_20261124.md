# Speed-matched locomotion for monsters, rival bots and other players - 24 Nov 2026

User report: "what is wrong with the bots' movement animations; fix them so they look normal; maybe remodel the characters?"

## Diagnosis (measured, not the models)

The Quaternius loops play in place (root motion baked) while gameplay moves the bodies, and nothing matched the loop's playback to the ground speed. Measured per body from the planted feet (a foot within 4 cm of its lowest height; the loop's ground speed is the backward speed of the planted feet):

| Body | Walk loop | Run loop |
|---|---|---|
| Skeleton monsters (all variants) | 0.78 m/s | 4.15 m/s |
| Player Archer avatar | 0.83 m/s | 4.61 m/s |
| Player Warrior avatar (Meshy CryptDelver) | 1.11 m/s | 4.21 m/s |
| Zombie_Walk_Fwd_Loop (old Minion) | 0.30 m/s | - |

Defects this produced:
- Other players and rival bots (`PlayerAvatar`) walked until 3.8 m/s: a 0.8-1.1 m/s walk at 1.5-3.8 m/s skates 2-4x.
- Servitor (Minion) chased at 2.7 m/s with a zombie walk at x1.7 (0.5 m/s of foot travel).
- Backing off (the new archer) and side steps played the forward loop: a moonwalk.

An earlier estimate from the feet-apart distance at double support was right for walks but under-read runs about 2x (a run has no double support); it was replaced by the planted-feet measure before any numbers were committed.

## Change

- `Runtime/AI/LocomotionGait.cs` (new): loop rate = ground speed / measured stride (clamped 0.5-2.4), negative (reverse) for a retreat (> 100 deg from the facing), and a body turn toward a sideways move folded into +-90 and clamped to 50 deg. Run from 1.9 m/s.
- `Runtime/AI/LocomotionStrides.cs` (new): per-model walk/run stride, measured in the editor and stored on the model prefab.
- `Editor/EmberdelveLocomotionControllers.cs` (new): adds the `LocoScale` float to a controller and binds the Walk/Run states' speed to it (state speed 1, no fixed multipliers); `TryMeasureStride` / `MeasureInto` (pose restored after sampling); menu `Emberdelve/Visual/Apply Speed-Matched Locomotion` patched the 5 enemy controllers and the 2 class avatar controllers in place and stored strides on 5 enemy prefabs (+ Resources copies) and the 2 class avatars. The enemy visual factory applies the same on rebuild.
- Drivers: `EnemyVisualDriver` (server monsters), `NetEnemyViews` proxies (client monsters) and `PlayerAvatar` (other players, rival bots) set `LocoScale` from their measured velocity and turn the body toward sideways motion (not while attacking, guarding or swinging). Older controllers without the parameter fall back to a forward-only rate.
- Minion walk is now `Walk_Loop` and its run `Jog_Fwd_Loop` (the zombie walk cannot match any gameplay speed); the walk/run playback multipliers in the variant table are gone.

## Evidence

Planted-foot slide over the ground (m/s, lower is better), old -> new:

| Case | Old | New |
|---|---|---|
| Servitor chase 2.7 m/s | 2.20 (zombie x1.7) | 0.46 (jog x0.65) |
| Servitor patrol 1.05 m/s | 0.76 | 0.25 |
| Rival bot 3.0 m/s | 1.89 (walk) | 0.48 (run x0.71) |
| Rival bot 4.3 m/s | 0.66 | 0.68 (unchanged; was already right) |
| Archer backing off 2.4 m/s | 6.55 (moonwalk) | 0.41 (jog reversed) |

The remaining ~0.25-0.5 m/s is the loops' own foot drag. EditMode: LocomotionGaitTests 7/7; controller contract (Walk/Run bound to `LocoScale`, strides present and plausible) 5/5.

## Remodelling

Not needed for this defect: the models were fine; the playback was wrong. Remaining limits are the animation library's (no dedicated strafe/backward loops, fingerless skeleton hands). Revisit only after an in-game look.
