# DakaDaka - current work

Updated 24 November 2026. The ONLY development checkout is `C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff`. Legacy `../DakaDaka` is read-only; `../DakaDaka_ci` is the disposable verification mirror. Keep the user's primary Editor open. Reviews use isolated saves and owned mirror Editors; record/deliver MP4 with game audio.

## Completed slice: skeleton vertical (overhead) attack

Report: [SKELETON_OVERHEAD_20261124](Tasks/SKELETON_OVERHEAD_20261124.md). The Hollow Servitor skeleton (Minion/Rogue visuals, `Weapon_HollowClaw`) now has two attacks: [0] the existing diagonal chop (unchanged data) and [1] a vertical overhead chop (`Pattern_ClawOverhead`, `SwingKind.Overhead`). The server brain picks one at random per windup and freezes it into the swing intent; the authority validates the pattern index. Telegraph (narrow strip derived from the baked tips), red attack line, host visual and client proxy show the chosen attack; presentation wire v5 carries it (protocol 47 -> 48). Ashen Warden and Cinder Warden are unchanged.

The clip is original procedural work on existing CC0 Quaternius material (no new third-party assets): the body holds the Idle pose, the right arm is solved in muscle space up over the head, down in the sagittal plane in front, and straight back to the guard; the damage sweep is baked from the same clip. Narrow regeneration: menu `Emberdelve/Visual/Rebuild Servitor Overhead Attack` (does NOT run `BuildAll`, which rewrites unrelated player weapon data).

The mandatory Dark and Darker reference was inspected (SkeletonFootman: two independent attack abilities/montages with per-montage collision windows); decoded facts vs. our original choices are in the report.

## Verification

EditMode via MCP: EnemyAttackVariantTests 6/6, NetEnemyPresentationWireTests 24/24, EnemyVisualQuaterniusTests 44/46 (two failures in untouched code: derived mesh vertex count, CinderWarden variant mapping). Mirror run 20261124-153105-273: EnemySweep 194/0, CombatAuthority 51/0, BladeDefense 361/0, NetCombat 399/0, Visual 216/0. SweptMelee 726/5 and RunCombatFlow 264/2 fail only on baselines: stale committed player sword data (same five failures in run 20261119-012844-536) and HUD passives/skill cooldown (runs 20261122-034940/035243). The smoke fixture now waits for the enemy's authority cooldown before the measured windup (diagnosed with EnemyDiag lines: the enemy had swung twice with 1.55 s cooldown left).

Motion review: baked-mesh frame strips (front/side) of the Minion clip; body yaw within 1.9 deg of idle, strike sagittal in front. Overhead farthest tip 1.44 m vs diagonal 1.66 m (same attack range). Not yet done: native in-game recording / user acceptance of the motion and of the shorter overhead reach.

Follow-ups: the player sword combo tuning from 1e1a44f34/1b0234139 is canonical; `EmberdelveAssetLibrary` now authors it and SweptMelee/CombatFlow/ActionSpeed assert it ([SWORD_DATA_CANONICAL_20261124](Tasks/SWORD_DATA_CANONICAL_20261124.md); mirror runs 20261124-183929-950 and 184927-727 all green except the smoke HUD baseline). Raid AI population restored (`EmberdelveGameConfig.populateRaidWithAi` = true, user request 24 Nov).

Inherited material/model/Oasis/settings drift and ~30 `.mat` files rewritten by a library rebuild remain uncommitted. No push.

## Preserved baseline and limitations

Lighting 55c5379ed: [LAYOUT_LIGHTING_20261122](Tasks/LAYOUT_LIGHTING_20261122.md). Furnishing 6f00729f8, layouts b59f72dc5, integration 19519855b. `EmberdelveAssetLibrary.BuildAll` authors the committed weapon/pattern data (byte-identical apart from serialized defaults); change tuning in the library and regenerate+commit Data together. Remaining smoke baseline: HUD names both passives, skill slot cooldown.

## Next three actions

1. Skeleton archer enemy (user request 24 Nov): ranged undead using the projectile authority, referenced against DaD SkeletonArcher.
2. User reviews the overhead attack in a raid (AI now enabled) and the shorter overhead reach.
3. Resume authorized unfinished gameplay work from ROADMAP.
