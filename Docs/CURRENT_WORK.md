# DakaDaka - current work

Updated 24 November 2026. The ONLY development checkout is `C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff`. Legacy `../DakaDaka` is read-only; `../DakaDaka_ci` is the disposable verification mirror. Keep the user's primary Editor open. Reviews use isolated saves and owned mirror Editors; record/deliver MP4 with game audio.

## Completed slices (24 Nov)

- **Skeleton archer "Hollow Bowman"** - [SKELETON_ARCHER_20261124](Tasks/SKELETON_ARCHER_20261124.md). Ranged undead: keeps 6-14 m with line of sight, backs off (1.5 s flee budget, then point blank), visible 1.1 s draw + aim strip, real server arrow through `LaunchProjectile` (16 Pierce, 30 m/s, gravity, ballistic aim). Draw bound to its target; refused when aimed > 40 deg off facing. Procedural bow (`EnemyBowRig`) and a generated two-arm IK bow clip. Two layout records (floor 1 created by the modular assembler when the scene authors monsters; floors 2-3 by `FloorContentSpawner`). Presentation wire v6 (ArcherKind), protocol 49; enemy arrows now replicate to clients. Brain fix: a windup ends when its target dies or leaves.
- **Speed-matched locomotion** - [LOCOMOTION_GAIT_20261124](Tasks/LOCOMOTION_GAIT_20261124.md). Loops play at the measured ground speed (per-model strides from planted feet), reverse for a retreat, the body turns toward sideways motion; monsters (server + client proxies) and other players / rival bots (`PlayerAvatar`). Planted-foot slide: Servitor chase 2.20 -> 0.46 m/s, rival bot at 3 m/s 1.89 -> 0.48, archer backing off 6.55 -> 0.41. Minion walk/run now Walk_Loop / Jog_Fwd_Loop.
- Earlier today: Servitor overhead attack ([SKELETON_OVERHEAD_20261124](Tasks/SKELETON_OVERHEAD_20261124.md)), sword combo data canonical ([SWORD_DATA_CANONICAL_20261124](Tasks/SWORD_DATA_CANONICAL_20261124.md)), raid AI population restored.

## Verification

EditMode: EnemyArcherTests 13/13, LocomotionGaitTests 7/7, NetEnemyPresentationWireTests 26/26, controller contract 5/5, RaidInspectionModeTests 4/4, EnemyVisualQuaterniusTests: only the two known baseline failures (derived mesh vertex count, CinderWarden variant mapping).
Mirror: run 20261124-200559-375 EnemySweep 210/0 (incl. archer draw -> real arrow hit at 9 m, no draw behind a wall), NetCombat 399/0, Visual 216/0, Hands 92/0, RetroVisual 104/3 (baseline: cinder_club unmapped, one weapon material, two enemy visual checks); run 20261124-202835-176 NetMovement 198/0, NetFinal 172/0; run 20261124-203603-865 RunCombatFlow 264/2 (baseline HUD passives / skill cooldown). The smoke's combat lane now requires 1 m half width (a right-to-left slash next to a wall stopped at sample 0).
Opus review of the archer: 3 important findings fixed (client loaded the Minion body; target switch mid-draw; a following player could keep the archer fleeing).

The mirror's 128 GB `Builds/HarnessCache` was deleted with the user's approval (C: had 0.1 GB free and player builds failed); `HarnessPlayers` (92 GB) and `Playable` (48 GB) remain.

Not done: in-game review by the user (archer feel/damage, locomotion look, overhead reach). Inherited material/model/Oasis/settings drift and ~30 `.mat` files remain uncommitted; `Tools/DarkerReference/` (Python decoders, not created by this session) is left untouched. No push.

## Preserved baseline and limitations

Lighting 55c5379ed, furnishing 6f00729f8, layouts b59f72dc5. `EmberdelveAssetLibrary.BuildAll` authors the committed weapon/pattern data; change tuning in the library and regenerate+commit Data together. No dedicated strafe/backward loops in the animation library (sideways = body turn, backward = reversed loop).

## Next three actions

1. User plays a raid: archers (range, damage, retreat), monster and bot movement, Servitor overhead.
2. Address concrete feedback with focused gates; revisit remodelling only if the models themselves look wrong after the gait fix.
3. Resume authorized unfinished gameplay work from ROADMAP.
