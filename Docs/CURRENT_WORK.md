# DakaDaka - current work

Updated 24 November 2026. The ONLY development checkout is `C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff`. Legacy `../DakaDaka` is read-only; `../DakaDaka_ci` is the disposable verification mirror. Keep the user's primary Editor open. Reviews use isolated saves and owned mirror Editors; record/deliver MP4 with game audio.

## Completed slices (24 Nov)

- **Skeleton archer "Hollow Bowman"** - [SKELETON_ARCHER_20261124](Tasks/SKELETON_ARCHER_20261124.md). Ranged undead: keeps 6-14 m with line of sight, backs off (1.5 s flee budget, then point blank), visible 1.1 s draw + aim strip, real server arrow through `LaunchProjectile` (16 Pierce, 30 m/s, gravity, ballistic aim). Draw bound to its target; refused when aimed > 40 deg off facing. Procedural bow (`EnemyBowRig`) and a generated two-arm IK bow clip. Two layout records (floor 1 created by the modular assembler when the scene authors monsters; floors 2-3 by `FloorContentSpawner`). Presentation wire v6 (ArcherKind), protocol 49; enemy arrows now replicate to clients. Brain fix: a windup ends when its target dies or leaves.
- **Speed-matched locomotion** - [LOCOMOTION_GAIT_20261124](Tasks/LOCOMOTION_GAIT_20261124.md). Loops play at the measured ground speed (per-model strides from planted feet), reverse for a retreat, the body turns toward sideways motion; monsters (server + client proxies) and other players / rival bots (`PlayerAvatar`). Planted-foot slide: Servitor chase 2.20 -> 0.46 m/s, rival bot at 3 m/s 1.89 -> 0.48, archer backing off 6.55 -> 0.41. Minion walk/run now Walk_Loop / Jog_Fwd_Loop.
- Earlier today: Servitor overhead attack ([SKELETON_OVERHEAD_20261124](Tasks/SKELETON_OVERHEAD_20261124.md)), sword combo data canonical ([SWORD_DATA_CANONICAL_20261124](Tasks/SWORD_DATA_CANONICAL_20261124.md)), raid AI population restored.

## Committed, awaiting in-game review: Dark and Darker combat port, passes 1-2 (separate session, 24 Nov)

Reports: [DARKER_COMBAT_REFERENCE_20261124](Tasks/DARKER_COMBAT_REFERENCE_20261124.md) (decoded facts), [DARKER_COMBAT_PORT_20261124](Tasks/DARKER_COMBAT_PORT_20261124.md) (mapping, changes, evidence, open items).

- The local extraction is now decodable: `Tools/DarkerReference` builds a property schema from the game executables' reflection tables and turns packages into JSON (`D:/DarkAndDarker-decoded`, outside the repo). Design data decodes 99 % exactly; weapon montages (notify times), ability defaults and curves decode. Blueprint graphs and native code do not.
- Ported (`Editor/EmberdelveDarkerCombat.cs`, menu `Emberdelve/Data/Apply Dark and Darker Combat Timing`, also the last weapon step of `BuildAll`): attack phases and chains of the arming sword (now 3 steps), short sword, rapier (now 4 steps) and two-handed sword (= reference longsword), riposte cuts, move multipliers, held move penalties, wall recoil 0.70 s, bow (draw 1.5 s, 8 arrows, reload 2.5 s, gravity 9.6), crossbow (reload 2.673 s). Model: `ComboStep.activeScale` and `ComboStep.comboWindow`. This replaces the sword tuning of SWORD_DATA_CANONICAL (newer explicit user request). Combat starts much slower: windups 0.40-0.93 s, hit windows 0.07-0.23 s.
- Evidence: mirror runs 20261124-211531-499 / 212807-676: CombatData, SweptMelee 730/0, CombatFlow 593/0, ActionSpeed, Riposte, BladeDefense, CombatAuthority, DrawSpeed, ReloadSpeed, TurnCeiling, Hands, D3Content, EnemySweep, CombatHud pass; NetCombat 397/0 (run 213019-227). Five gates had old literal expectations rewritten from the data. Not run: smoke combat flow, Visual, the other gates. NOT reviewed in game (animation feel, balance, bots).
- Pass 2 (same reports): the guard intercepts 0.15 s after the press, a blocked hit puts a 0.45 / 0.90 / 1.30 s reaction on the defender (no ordinary attack, riposte allowed), a parried attacker cannot attack for 1.27 s (two-handed 1.13 s). Tunables on `CombatFlowTuning`. Mirror run 20261124-215843-035: 14 combat gates pass (Riposte 147/0, ProjectileBlock 202/0, CombatFlow 599/0 ...); NetCombat: run 20261124-220434-195, 397/0. No animation or HUD cue for the new states yet.
- Open: hit-stop, arrow speed loss in flight, bow recovery after release, draw/reload move multipliers, other weapon families, enemy timing.

## Verification

EditMode: EnemyArcherTests 13/13, LocomotionGaitTests 7/7, NetEnemyPresentationWireTests 26/26, controller contract 5/5, RaidInspectionModeTests 4/4, EnemyVisualQuaterniusTests: only the two known baseline failures (derived mesh vertex count, CinderWarden variant mapping).
Mirror: run 20261124-200559-375 EnemySweep 210/0 (incl. archer draw -> real arrow hit at 9 m, no draw behind a wall), NetCombat 399/0, Visual 216/0, Hands 92/0, RetroVisual 104/3 (baseline: cinder_club unmapped, one weapon material, two enemy visual checks); run 20261124-202835-176 NetMovement 198/0, NetFinal 172/0; run 20261124-203603-865 RunCombatFlow 264/2 (baseline HUD passives / skill cooldown). The smoke's combat lane now requires 1 m half width (a right-to-left slash next to a wall stopped at sample 0).
Opus review of the archer: 3 important findings fixed (client loaded the Minion body; target switch mid-draw; a following player could keep the archer fleeing).

The mirror's 128 GB `Builds/HarnessCache` was deleted with the user's approval (C: had 0.1 GB free and player builds failed); `HarnessPlayers` (92 GB) and `Playable` (48 GB) remain.

Not done: in-game review by the user (archer feel/damage, locomotion look, overhead reach). Inherited material/model/Oasis/settings drift and ~30 `.mat` files remain uncommitted; `Tools/DarkerReference/` (Python decoders) and the combat-port files above were committed by the separate combat-port session. No push.

## Preserved baseline and limitations

Lighting 55c5379ed, furnishing 6f00729f8, layouts b59f72dc5. `EmberdelveAssetLibrary.BuildAll` authors the committed weapon/pattern data; change tuning in the library and regenerate+commit Data together. No dedicated strafe/backward loops in the animation library (sideways = body turn, backward = reversed loop).

## Next three actions

1. User plays a raid: archers (range, damage, retreat), monster and bot movement, Servitor overhead, and the reference weapon timings (sword chains, rapier, two-hander, 1.5 s bow draw); accept or adjust.
2. Address concrete feedback with focused gates; revisit remodelling only if the models themselves look wrong after the gait fix.
3. Resume authorized unfinished gameplay work from ROADMAP.
