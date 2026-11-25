# Dark and Darker combat port, pass 1 - 24 Nov 2026

User request: "look at the Dark and Darker decompile, finish it if it is incomplete, make combat the same:
animation timings, parries, bow and arrows". Decoded facts and source paths:
[DARKER_COMBAT_REFERENCE_20261124](DARKER_COMBAT_REFERENCE_20261124.md). This supersedes the sword tuning that
SWORD_DATA_CANONICAL_20261124 called canonical earlier the same day (newer explicit instruction).

Status: implemented, data applied in the working Editor, compiled. Verification: see the end. NOT visually reviewed.

## Mapping (reference attack montage -> our swing)

- windup = montage start .. `Collision.On`; active = `Collision.On` .. `Collision.Off`;
  recovery = `Collision.Off` .. `Attack.Transition` (where a queued next attack starts).
- Last attack of a chain has no transition event: recovery runs to the blend-out (length - blend-out time; 0.25 s
  default when the montage does not store one). INFERENCE, the ability code is native.
- Combo window = `ComboWindow.Close` - `Collision.Off`, authored PER STEP (`ComboStep.comboWindow`; 0 = the weapon's
  value). The first mirror run showed why one value per weapon is not enough: with the rapier's first-step window
  (0.33 s) its third thrust (recovery 0.24 s) left 0.09 s to continue the chain.
- Weapon `cooldown` = the first step's total, so the authority floor (0.8 x cooldown) never exceeds a step.
- Move multipliers: windup and recovery = the reference "attack prepare" modifier, active = the "attack" modifier.
- Wall recoil 0.70 s = reference stuck (play rate -0.1 for 0.1 s) + 0.6 s blend out.

## What changed

Model: `ComboStep.activeScale` (per-step length of the hit window; 0 in older assets means 1),
`SwingPatternData.TimingFor(..., activeScale)`, used by `WeaponData.ComboTiming`. Needed because two weapons share the
slash patterns but have different hit windows. `ComboStep.comboWindow` + `WeaponData.ComboWindowAfter(step)`, read by
the authority (chain window after an accepted step and after a wall recoil) and the HUD combo bar.

Data, authored by `Editor/EmberdelveDarkerCombat.cs` (called last in the weapon part of `BuildAll`, and from the menu
`Emberdelve/Data/Apply Dark and Darker Combat Timing`, which touches only these assets):

| Our weapon | Reference | Steps: windup / active / recovery (s) | Window after each step | Other |
|---|---|---|---|---|
| Arming Sword | ArmingSword | 0.399/0.204/0.234, 0.400/0.157/0.376, 0.600/0.154/0.913 (thrust) | 0.527, 0.703 | 3 steps (was 4), 1.00/1.05/1.10, one riposte strike, move penalty 20 |
| Ashen Short Sword | ShortSword | 0.513/0.120/0.100, 0.500/0.133/0.200, 0.615/0.172/0.426 | 0.357, 0.492 | move penalty 10 |
| Ember Rapier | Rapier | 0.433/0.069/0.112, 0.669/0.079/0.180, 0.651/0.080/0.240, 0.596/0.083/0.438 | 0.327, 0.392, 0.509 | 4 steps (was 3), 1.00..1.15, move penalty 17 |
| Two-Handed Sword | Longsword | 0.932/0.229/0.283, 0.679/0.234/0.054, 0.757/0.233/0.727 | 0.733, 0.392 | finisher 1.10 (was 1.15), one riposte strike, move penalty 40 |
| Riposte cut (1H) | ArmingSword riposte | 0.431/0.205/0.299 | | x1.5 unchanged |
| Riposte cut (2H) | Longsword riposte 1 | 0.333/0.167/0.333 | | x1.5 unchanged |
| Hollow Bow | RecurveBow | draw 1.5 s (was 0.85), 8 arrows (was 5), reload 2.5 s (was 1.9), gravity 9.6 | | |
| Ember Crossbow | Crossbow | reload 2.673 s (was 2.2), cooldown 0.8 (was 1.1), 1 bolt | | |

Previous values for comparison: one-handed slash 0.28/0.30/0.34 with step scales; two-handed 0.42/0.34/0.50.
The reference is markedly slower to start (windup 0.40-0.93 s) with a much shorter hit window (0.07-0.23 s).

## Not ported in this pass (open)

- Parry window rules and the reference weak/mid/heavy thresholds: native code, not decodable from the packages
  (pass 2 below uses our own thresholds).
- Pass 2 limits: no block-reaction or deflected animation and no HUD cue (only the lockout and the move factor);
  the lockout covers melee attacks, not drawing a bow; thin clients learn of it through the server's refusal.
- Hit-stop (attacker animation at 0.08 speed for 0.22 s on a hit).
- Arrow speed loss over flight (speed curve; our ballistics have no drag) and the recurve's muzzle speed (native
  default, not decoded). Our 42 m/s and the air-time damage falloff are unchanged.
- Bow recovery after release (about 0.97 s before the next draw) and move multipliers while drawing (x0.65) /
  reloading (x0.5).
- The short sword's and rapier's second riposte attack timing; zweihander, falchion and the
  other weapon families; damage numbers and impact power / resistance scales (ours are on a different scale).
- Skeleton enemy attack timing (chunk 301 was not decoded in this pass).

## Files

Runtime: `Data/WeaponData.cs`, `Data/SwingPatternData.cs`, `Combat/LocalCombatAuthority.Swings.cs` (two window
sites), `UI/HudController.cs` (combo bar). Editor: `EmberdelveDarkerCombat.cs` (new), `EmberdelveAssetLibrary.cs`
(one call). Gates re-specified from the data instead of the old literals (no check removed): `EmberdelveCombatFlowGate`,
`EmberdelveSweptMeleeGate`, `EmberdelveActionSpeedGate`, `EmberdelveDrawSpeedGate`, `EmberdelveReloadSpeedGate`. Data: `Weapon_ArmingSword`, `Weapon_AshenShortSword`, `Weapon_EmberRapier`,
`Weapon_TwoHandedSword`, `Weapon_HollowBow`, `Weapon_EmberCrossbow`, `Pattern_SwordSlashLR/RL`, `Pattern_SwordOverhead`,
`Pattern_SwordRiposteCut`, `Pattern_ArmingThrust`, `Pattern_RapierThrust`, `Pattern_GreatSlashLR/RL`,
`Pattern_GreatOverhead`, `Pattern_GreatRiposteCut`. Tools: `Tools/DarkerReference/*`.

## Verification

Working Editor: compiles with zero errors; data applied with the menu entry (16 weapon/pattern assets changed).

Mirror gates (bounded selection from the verification matrix, "Combat/skills"; the planner selected all 71 gates
because shared data changed, the affected combat gates were run instead):

- Run 20261124-211531-499: CombatData 22/0, ActionSpeed 196/0, Riposte 144/0, BladeDefense 361/0, CombatAuthority 51/0,
  DrawSpeed 253/0, ReloadSpeed 171/0, TurnCeiling 122/0, Hands 92/0, D3Content 53/0, EnemySweep 210/0, CombatHud 132/0.
  SweptMelee 730/2 and CombatFlow 593/1 failed on three window-timing expectations, fixed and rerun:
- Run 20261124-212807-676: SweptMelee 730/0, CombatFlow 593/0.
- NetCombat: run 20261124-213019-227, 397/0 (the player build was not stored in the build cache: "inputs changed during build", a report file was edited meanwhile; the gate ran on the synced sources).

History of the failures (all were expectations holding the old numbers, except one real finding):
run 20261124-205511-087 SweptMelee 20, CombatFlow 33, ActionSpeed 4, DrawSpeed 13, ReloadSpeed 29 failures; run
20261124-210733-248 CombatFlow 29. Real finding: one combo window per weapon was too tight for the rapier's third
thrust -> per-step windows (model change above). Gate expectations were rewritten to derive from the weapon data
(phase lengths, repeat floor = max(0.8 x cooldown, swing total), reload/draw x action rate); the data pins were
re-pinned to the reference values. Two checks changed meaning with the data and were re-specified, not dropped:
"after a wall stop the cooldown still applies" is now "the next swing is never earlier than the repeat floor" (the
0.70 s wall recoil outlasts the floor), and the hard-coded contact sample "1/15 of the active phase" is now "the fast
and the slow swing touch at the same normalized point".

EditMode (runs 205511-087 and 211531-499): 1605 tests, 12 failed, the same 12 both times. None is in a file this slice
touches (bone_bow hand use and model mapping, cinder_club mapping, CinderWarden variant, derived mesh vertex count,
run stride of the enemy controllers, crossbow left-palm distance 0.0816 vs 0.08, a Royale bot portal test). Another
session was working in this checkout at the same time (skeleton archer and locomotion, committed as 2934e461c at
20:39); most of these tests are in its area. That they are not caused by this slice is my reading of the messages,
not a proven baseline.

Not verified: RuntimeSmokeTest.RunCombatFlow, the Visual gate, any in-game or recorded review of the new timing
(first-person swing animation at 0.4-0.9 s windups, the 1.5 s bow draw), balance against enemy timings, AI bots'
use of the slower weapons. The remaining 56 gates of the planner's selection were not run.


## Pass 2: guard ready time, block reactions, deflected attacker (user go-ahead, 24 Nov)

Tunables on `CombatFlowTuning` (`GameConfig.flow`; an older config deserializes to these defaults):

| Tunable | Value | Source |
|---|---:|---|
| `guardReadySeconds` | 0.15 | reference `Event.Item.ImpactGuard.Ready` |
| `blockReactionWeak/Mid/HeavySeconds` | 0.45 / 0.90 / 1.30 | reference block montages (clip length / play rate) |
| `blockReactionMoveMultiplier` | 0.97 | reference block-reaction movement modifier |
| `blockReactionMidRatio` / `HeavyRatio` | 0.75 / 1.0 | OUR choice (impact power / impact resist); the reference rule is native |
| `deflectedSeconds` / `deflectedTwoHandedSeconds` | 1.267 / 1.133 | reference `AM_<Weapon>_Deflected` |
| `deflectedMoveMultiplier` | 0.6 | reference "stucked" modifier (inference: the deflected state's own modifier was not decoded) |

Behaviour:
- Guard: `PlayerRig` stamps the press on the authority clock; `IsGuardingWithDefense` (every interception path and
  the impact-guard window) is true only once the guard has been held for the ready time. `IsGuarding` (raise visual,
  move slowdown, refusing own attacks, the replicated flag) still starts with the press. Releasing and pressing
  again restarts the ready time.
- Block reaction: a blocked, unbroken hit (melee or projectile) puts a reaction on the defender, by
  `CombatFlowRules.BlockReactionSeconds(impactPower, impactResist)`. While it lasts an ordinary melee attack is refused
  ("block reaction in progress"); a riposte is allowed; move x0.97. A weaker later block never shortens it.
  With our data: a one-handed slash on a sword guard is weak, an overhead or two-hander mid, the maul heavy.
- Deflected: when a hit is a confirmed parry (blade parry region of a real player, never a shield), the attacker
  cannot begin any attack for 1.267 s (two-handed weapon 1.133 s) and moves at x0.6. The parried swing was already
  cut short by the existing guard stop.

Files: `Runtime/Data/CombatFlowTuning.cs`, `Runtime/Combat/CombatFlowRules.cs` (`BlockReactionSeconds`, `IsGuardReady`),
`Runtime/Combat/LocalCombatAuthority.Swings.cs` (two "until" maps, begin refusals, move factors, gate seam
`GuardReadyDelayEnabled`), `Runtime/Player/PlayerRig.cs` (`IsGuardReady`). Tests: `Tests/EditMode/Combat/GuardTimingRulesTests.cs`
(new, 5). Gates: CombatFlow proves the ready time on the real rig (+6 checks) and switches the delay off for its
geometry sections; ProjectileBlock switches it off and checks the block reaction and the parry-only deflect (+6);
Riposte checks the reaction lockout and that an abstract block does not deflect (+3).

Evidence: working Editor compiles; `GuardTimingRulesTests` 5/5. Mirror run 20261124-215843-035: Riposte 147/0,
ProjectileBlock 202/0, CombatFlow 599/0, SweptMelee 730/0, CombatData 22/0, ActionSpeed 196/0, BladeDefense 361/0,
CombatAuthority 51/0, TurnCeiling 122/0, Hands 92/0, EnemySweep 210/0, CombatHud 132/0, DrawSpeed 253/0,
ReloadSpeed 171/0. NetCombat: run 20261124-220434-195, 397/0.
First run of this pass (214841-693) failed CombatFlow 20, Riposte 2, ProjectileBlock 29: fixtures raised a guard and
struck in the same instant, and one defender began an ordinary attack right after blocking.
Not run: full EditMode suite, smoke combat flow, Visual, the other gates. NOT reviewed in game.

## Pass 3: attack directions (user request, 24 Nov, uncommitted)

Request: "port the animations and attack directions too, and the placement of the bow and hands, not only timings".

Done: attack DIRECTIONS. Each reference attack ability stores `ChangeCrosshairAngle`; the table is in the reference
report. Read as the blade's travel direction across the view (0 = right to left, -90 = down, 180 = left to right;
the sign is an inference, argued there). `EmberdelveDarkerCombat` now authors a directed cut per attack: our own plain
arc around the hand (the library arc's pivot, radius and half angle), turned to that direction. No reference animation
data is used.

| Our weapon | Chain (reference angle) | Riposte |
|---|---|---|
| Arming Sword | cut -60, cut 230, thrust | cut -115 |
| Ashen Short Sword | cut -122, cut 375, cut 340 | cut 340, cut 180 |
| Ember Rapier | thrust, cut -125, cut -90, cut 180 (was four thrusts) | cut -65, thrust |
| Two-Handed Sword | right-to-left slash (no reference value), left-to-right slash (-177), cut -35 (was an overhead) | unchanged |

Our own decisions:
- A cut that is not flat is shortened so its tip stays within 1.15 m above and 0.65 m below the hand. The first
  run showed why: a full 55 degree arc turned to -122 ends in the floor, the authority treats that as a wall stop, and
  every steep cut ended in the 0.70 s recoil. Steep cuts therefore sweep about 50 degrees instead of 110.
- The kind of a cut (which sets its height band for the directional guard and the x1.2 overhead impact) is taken
  from the project's own measure of the keys (`BladeTrajectories.ClassifyKeys`). Result: the opening cuts of the
  arming and short sword, the arming riposte and the rapier's cuts 2-3 are Overhead (high band); the near-flat cuts
  are horizontal. This is a gameplay consequence: those cuts now arrive high and hit a guard 20 percent harder.
- New assets `Pattern_ArmingCut1/2`, `Pattern_ArmingRiposte`, `Pattern_ShortCut1/2/3`, `Pattern_ShortRiposte1/2`,
  `Pattern_RapierCut2/3/4`, `Pattern_RapierRiposte`, `Pattern_GreatCut3`. The library slashes stay as assets; the
  short sword keeps the library overhead at index 5, outside its chain.

Gates: mechanics scenarios (cleave, walls, shields, impact, zones, turn ceiling) were laid out for flat arcs. They now
swing a FIXTURE (`EmberdelveDarkerCombat.FlatArcFixture`): the short sword with its cut shapes replaced by the library
slash / back slash / overhead / riposte cut and every timing, reach and move factor kept. The shipped asset is still
what the data pins, the per-pattern geometry probes, the defence classification gate and all real-rig sections use.
Limit of this choice: cleave counts, wall behaviour and shield interception are proven for flat arcs, not for the
directed cuts.

NOT done, and why: animations, hand and arm poses, bow placement and draw pose. The packages' animation tracks are
not decoded, and they are the reference game's artwork: the project uses its own assets (ASSET_PLAN), so they are not
imported. Our first-person swing follows the pattern keys, so the new directions are visible; the arms, the bow hold
and the draw are still our procedural poses.

Evidence: working Editor compiles; `SwordFamilyTests` 8/8 after its expectations were updated. Mirror run
20261124-231051-124: SweptMelee 1016/0, CombatFlow 607/0, BladeDefense 565/0, TurnCeiling 158/0, Riposte 147/0,
CombatData 22/0, ProjectileBlock 202/0, ActionSpeed 196/0, CombatAuthority 51/0, Hands 92/0, CombatHud 132/0,
EnemySweep 210/0, D3Content 53/0, DrawSpeed 253/0, ReloadSpeed 171/0. EditMode in that run: 1675 tests, 13 failed =
the 12 unrelated ones listed above + one `SwordFamilyTests` expectation fixed afterwards. NetCombat: run 20261125-001101-171, 396/0. The first run of this pass (20261124-232150-489) failed 391/12: the short sword's opening cut (-122) lands on the head (loc=Head x1.5, 36.7 instead of 24.5 on the body) and passed a mid-held guard. The user chose to keep the reference direction, so the duel scripts in `Net/NetCombatSender.cs` now raise the guard into a steep cut (view 30 degrees up; blade 8 degrees left, shield straight), from poses measured by the CombatFlow gate ("REAL ... guard catches", run 20261124-235033-273: blade 20-40 degrees up at yaw -10..-5, shield 20-40 degrees up).
Earlier runs of this pass: 225235-012 (60 failures over four gates), 230347-484 (27), 230850-373 (1).
Not run: smoke combat flow, Visual, the other gates. NOT reviewed in game: how the cuts look and read in first
person, whether the shortened steep cuts feel right, bots.

## Pass 4: an obstacle or a block ends the combo (user report 25 Nov, uncommitted)

User report: "hitting an obstacle or a block does not interrupt the attack combo; attack animations of bots and of
the character look crooked".

Combo: until now a wall stop kept the chain alive on purpose (review D1b: "the combo survives, its window restarts when
the recoil ends"). Changed in `LocalCombatAuthority.Swings.cs`: a wall stop removes the chain, `FinishSwing` removes it
for every end reason except Completed, and any blocked hit removes the attacker's chain (a physical guard contact
already did; a block resolved without one now does too). The next attack starts at step 1. CombatFlow's wall section
was re-specified: after the wall stop the chain expects step 1, step 3 is refused inside the old window, step 1 is
accepted.
Evidence: mirror run 20261125-011242-229: CombatFlow 611/0, SweptMelee 1016/0, Riposte 147/0, ProjectileBlock 202/0,
CombatAuthority 51/0. NOT run after this change: NetCombat, EditMode. The working Editor was in Play mode, so the
change is not compiled there yet.

Animations: NOT fixed, cause not established. Bots and other players are posed by `RemoteWeaponPresentation` straight
from the swing pattern (hand at the pattern's base, a two-bone reach for the arm), and the first-person arms follow the
same pose, so the directed cuts of pass 3 changed what both show. The edit-mode review renderer
(`Emberdelve/Debug/Render Combo And Block Review`) could not be used: the working Editor was in Play mode, and the
mirror runs without graphics (its frames come out blank). Next: render the review frames with Play stopped, look at
them, then correct the cut poses.

### Pass 4, animations (25 Nov, after the review frames; uncommitted)

Review frames (`Emberdelve/Debug/Render Combo And Block Review`, `Logs/review/combo/sheet.png`) showed the cause in
first person: the directed cuts of pass 3 swung the HAND on the library's 0.85 m radius, so a steep cut put the fist
at arm's length in front of the face, above eye level, and most other frames had the weapon outside the view.
Fix (`EmberdelveDarkerCombat.DirectedArcKeys`): the hand keeps a compact path (radius 0.35 m around a point in front of
the body, at the library arc's position when the blade points straight ahead, so reach is unchanged) and the blade
does the directed sweep. All 13 directed patterns were regenerated. Second render: the raised sword is held from the
bottom of the frame; the fist no longer sits in front of the face. Flat cuts still start outside the right edge of
the view, as the library slashes always did.

Bots and other players (`RemoteWeaponPresentation`): the weapon was placed at the start of the damage segment, about
0.9 m in front of the eye, and `Reach` moved the wrist bone there even though the arm is shorter (a stretched arm
with the sword ahead of the hand). New `ReachAndHold`: the right arm reaches towards the segment only to 94 percent of
its length and the weapon is held at that hand, keeping the swing's direction. Consequence: on avatars the visible
blade sits up to about 0.3 m closer to the body than the real hit segment. Not looked at in third person: there is no
third-person swing renderer, and no in-game review was done.

Evidence: working Editor compiles; EditMode there: FirstPersonSwingTests 146 passed / 1 failed (the old crossbow
left-palm case) / 26 skipped, SwordFamilyTests 8/8, RiposteDataTests 16/16, GuardTimingRulesTests 5/5.
NOT run after the pose change: every mirror gate (SweptMelee, CombatFlow, BladeDefense, NetCombat, Visual ...). The
mirror refused: `Temp/UnityLockfile` in DakaDaka_ci is left from run 20261125-011242-229, which I stopped while its
last step (the review renderer, which prints no PASS line) was waiting; no Unity process holds the mirror. The guard
says to leave the lock intact, so it was not removed. The guard poses the network scripts use were measured with the
old hand radius and must be re-measured (CombatFlow "REAL ... guard catches") before NetCombat can be trusted.

### Pass 4, verification after the lock was cleared (25 Nov)

The stale `DakaDaka_ci/Temp/UnityLockfile` (written 01:12:44 by my stopped run, no mirror Unity process) was removed
with the user's approval.

Two findings from the first mirror run with the compact hand path (20261125-022631-800), both fixed:
- At 1.1 m no blade pose caught the short sword's opener: with the library's 55 degree back angle the blade of a steep
  cut already hung over a close target's head when the hit window opened. Falling cuts now start further back (up to
  80 degrees for a vertical one; tip rise limit 1.5 m, walls are 3.6 m). Re-measured ("REAL ... guard catches", run
  20261125-023617-979): the blade catches the opener at 1.1 m (e.g. pitch -40 at yaw -5..20, pitch 0 at yaw -35..-15)
  and at 1.5 m; the shield at both distances over a wide range.
- With the longer path the opener's measured class is left-to-right again (not overhead): mid height band, no x1.2
  impact. The kind pins in CombatFlow and SweptMelee say so. The network duel scripts no longer rely on the kind: a cut
  whose hit window opens with the blade raised 30 degrees or more gets the raised guard pose (pitch -40; blade yaw +5,
  shield yaw 0).
- SweptMelee's probe-timing tolerance (the swept capsule reaches a probe before the blade's centre line) was a fixed
  0.13 of the sweep, right for long flat arcs; it is now the larger of 0.13 and (sweep radius + probe radius) / the
  distance that blade point travels + 0.03, so a short steep cut is judged by its own geometry.

Evidence, final state: mirror run 20261125-023918-981: SweptMelee 1016/0, CombatFlow 611/0, BladeDefense 565/0,
TurnCeiling 158/0, Riposte 147/0, CombatData 22/0, ProjectileBlock 202/0, ActionSpeed 196/0, CombatAuthority 51/0,
Hands 92/0, CombatHud 132/0, EnemySweep 210/0, Visual 216/0, NetCombat 396/0. EditMode in the working Editor before
the back-angle change: FirstPersonSwingTests 146/1 (old crossbow case), SwordFamilyTests 8/8, RiposteDataTests 16/16,
GuardTimingRulesTests 5/5; the full EditMode suite was not rerun.
Still NOT reviewed in game, and not looked at in third person.

## Pass 5: arrows, first-person bow, rival bodies (user report 25 Nov after play, uncommitted)

User report: bots still move strangely and their animations look very bad; flying arrows should be easier to see and a little
slower; the hands with the bow still look bad. None of this pass copies reference assets; the poses are our own.

### Arrows
- `WeaponData.projectileTrailMaterial` (new) + `ProjectileVisual.MakeFlightReadable`: the flying model is drawn 2.2x thicker and
  leaves a 0.28 s pale streak. Cosmetic only, the authority's collision radius is unchanged.
- Bow `projectileSpeed` 42 -> 38 m/s (user request; the reference recurve speed is not decoded). Crossbow gravity 9.6 like the arrow.
- `EmberdelveProjectileSimGate` pins follow the data (38 m/s, 9.6). It had not been run since pass 1 changed the gravity.

### First-person bow (review tool: `Emberdelve/Debug/Render Bow Review (Edit Mode)` -> `Logs/review/bow/sheet.png`)
Seen in the first frames: the bow stood upright through the screen centre, the string hand lay on the bow hand at rest, the draw
went 15 cm sideways and only 22 cm back with an open hand.
- `WeaponVisualBuilder.BowRestPosition/BowRestRotation`: right of and below the centre, canted 17 degrees.
- `BowPresentation.SetDrawShape`: the first-person bow draws 0.30 m straight back; avatars and the archer's baked clip keep the old shape.
- `FirstPersonArmsDriver.PoseBow`: the string hand waits low on the right, comes onto the string over the first 18 % of the draw,
  back of the hand up, fingers hooked from the start.
- The review tool's guard frame now really raises the guard (the first render showed a tool defect, not the game).

### Rival bodies (review tool: `Emberdelve/Debug/Render Avatar Review (Edit Mode)` -> `Logs/review/avatar/sheet-*.png`)
Seen in the baseline frames: the body stood in Idle while a straight right arm was dragged by IK, open hand, the weapon not
turned with the hand, the carried sword through the floor, a T-pose-like guard, and the legs stood still (slid) during a strike or guard.
- `RemoteWeaponPresentation.PoseMelee`: a melee weapon is always drawn in the fist (blade out of the thumb side). A strike, recoil
  or guard only tells the right arm where to carry it; the arm blends in and out (`HoldBlendIn/Out`), its extension follows the
  strike phase (`StrikeReach`: drawn back, thrown out, eased back), the elbow has a fixed direction, the wrist turns with the
  blade, the fingers close (`CloseFist`), bones are never stretched. The second hand holds a two-handed weapon.
  The visible blade keeps the swing's direction but sits at the hand, up to about 0.3 m inside the damage segment (as since pass 4).
- Arm layer (`AvatarArms`, built by `EmberdelveAvatarArmLayer`, menu `Emberdelve/Visual/Apply Avatar Arm Layer`, also called by the
  class visual builder): override layer masked to arms and fingers with `Sword_Idle`, `Sword_Block`, `Idle_Shield_Loop` from the
  Quaternius library already in the project. `PlayerAvatar` keeps the legs on Idle/Walk/Run under a strike or guard.
- The library's `Sword_Attack` / `Sword_Regular_A/B/C` were inspected (`Render Avatar Clip Sheet`) and not used: whole-body
  acrobatic moves whose direction does not follow our swing patterns.

### Evidence and limits
- Mirror run `Logs/runs/20261125-040557-133`: ProjectileSim 109/0, DrawSpeed 253/0, ReloadSpeed 171/0, ProjectileBlock 202/0,
  Visual 216/0, Hands 92/0, CombatFlow 611/0, NetCombat 396/0. EditMode 1675 tests, 12 failed: the same 12 known unrelated ones.
- Visual proof is edit-mode stills only. Not seen in motion: the arm layer's cross-fades in a real animator (the review emulates
  the layer by sampling the clip onto the arm bones), the arrow streak in flight, bot path behaviour. The bots' movement logic
  (`RoyaleBotController`) was not changed; the sliding legs are the only movement defect found and fixed here.

### Pass 5b: joints and the shape of a cut (user review of the stills + reference recording, 25 Nov, uncommitted)

User: the sword hand was twisted, then the elbows; every joint must be anatomically right. Then, with a recording
(`C:/Users/yrapa/Videos/Captures/Dark and Darker   2026-11-18 14-21-33.mp4`, sword and round shield, first person, 20 s):
"these are the animations bots and the player must have".

Joints (`RemoteWeaponPresentation`):
- Wrist: no free rotation any more. `SolveWrist`: the forearm turns at most `ForearmTwist` 80 degrees from thumb-up, the wrist bends
  sideways within `WristRadial` 20 / `WristUlnar` 40, the handle lies across the palm (`GripCant` 20). What the limits cannot give is
  left out: the blade points where the hand can hold it.
- Elbow: a hinge. `LearnHinge` reads its axis from the animated pose (upper arm x forearm once the elbow is bent 12 degrees or more);
  `SetArm` builds both bones on that axis, also for the old `Reach` (bow, crossbow, shield, potion).
- Shoulder: the elbow's swing is chosen to ask least of the wrist and forearm, with the upper arm's own turn limited
  (`ShoulderOutward` 85, `ShoulderInward` 65) and the elbow never across the chest.
- Behind a shield the weapon arm keeps its stance (it used to be raised over the head into the blade guard).

Shape of a cut, observed frame by frame in the recording (frames extracted with PyAV into the session scratchpad, not kept in the repo):
- ready: blade upright on the right, shield low left;
- wind-up (about 0.2 s): the sword tilts back and leaves the picture to the weapon side, the shield drops out of the picture, then
  about 0.25 s nothing is on screen;
- strike (about 0.2 s): the forearm enters from that side, the hand leads, the blade trails behind it, across the middle and out low
  on the far side;
- then an empty screen until the next attack or the return to ready.
Adaptation (`SwingMotion`, our own motion, no reference asset or curve is used): a cut's shown hand path and blade direction are
generated from the pattern's own direction (blade at the start and end of the damaging window), in two frames: `FirstPerson`
(drawn back to a point beside the camera so the whole arm is out of the picture) and `Body` (drawn back beside the shoulder).
`FirstPersonArmsDriver` uses it for one-handed cuts (`SwingView`), `RemoteWeaponPresentation.PoseMelee` for bodies. The damage
segment, its timing and the trail are unchanged. `EmberdelveComboReview` now steps the driver's eased values at 60 Hz, so its
frames show what the game shows (it used to pass frozen blends and hid the old motion's defects).

Not done yet: thrusts and two-handed swords in first person keep the previous motion; the bow on a body still holds both arms
forward; the two-handed guard on a body is a high guard with the arms above the head. The decoder reads montage windows and
notifies, not bone curves, so "the animations from the decompile" are available as timings and directions only.

Evidence: mirror run `Logs/runs/20261125-044947-088` Visual 216/0, Hands 92/0, NetCombat 396/0; EditMode 1675, the same 12 known
unrelated failures. Stills: `Logs/review/combo/sheet.png` (first person), `Logs/review/avatar/sheet-*.png`. No motion proof.

### Pass 5c: the other weapons and the bow (user: "then do the same for the other weapons and the bow", 25 Nov, uncommitted)

Source limits, unchanged: the decoder gives montage windows, notifies and attack directions, not bone curves, and no reference
animation asset is used. The motion below is our own, driven by each pattern's decoded timing and direction.

- Thrusts (`SwingMotion.TrySample`, no longer false for a pattern without sweep: arming thrust, rapier thrust, great thrust).
  The hand is drawn back beside the body with the point on the target (`Frame.ThrustCock`), driven straight out along the blade
  (`ThrustEnd`, shifted by the pattern's own line) and pulled back. On a body the point is aimed `ThrustDrop` below the eye line.
  On a body the grip shifts for a thrust (`ThrustCant` 50 instead of `GripCant` 20): the handle lies along the palm, so the blade
  continues the forearm within the wrist's limits.
- Two-handed swords: `FirstPersonTwoHanded` / `BodyTwoHanded` frames (`SwingMotion.For`). First person now uses the same
  generated cut as one-handed swords; the wind-up waits low on the weapon side, because a wait beside the camera puts the second
  forearm across the whole picture (seen in the first render of this pass).
- Blade guard on a body: `GuardGrip` / `TwoHandedGuardGrip`: hands in front of the chest, blade up and across. The authority's
  block volume is unchanged (it sits above an arm's natural reach; the body used to raise both arms over the head to follow it).
- Bow on a body (`PoseBow`): carried low in the bow hand; to shoot, the bow arm is raised and held out, the string hand takes the
  nock and draws straight back to the face (`BodyDrawSide` 0, `BodyDrawDepth` 0.34), elbow out and level. `HoldPoint` keeps bone
  lengths (the old `Reach` moved the wrist bone onto the target and stretched the forearm); the crossbow and the bow guard use it too.
- Review tools: `EmberdelveComboReview` renders every sword family (`Logs/review/combo/sheet{,-arming,-rapier,-two}.png`);
  `EmberdelveAvatarReview` adds the rapier combo and `sheet-ranged.png` (bow carried / 5% / 50% / full draw, crossbow, crossbow guard,
  both bodies, front and side).

Not done: torch and fists keep the pattern-following hand in first person; the second hand of a two-handed grip and the bow hands
on a body are placed but their fingers are not closed; club / maul / claw belong to non-humanoid enemies and are not touched.
No motion proof: stills only.

Evidence: mirror run `Logs/runs/20261125-133747-988` Visual 216/0, Hands 92/0, NetCombat 396/0, CombatFlow 611/0; EditMode 1675, the same 12 known unrelated failures (names compared with run 20261125-044947-088).

### Pass 5d: first-person trajectory redone after the user's in-game recording (25 Nov, uncommitted)

User recorded the game (`C:/Users/yrapa/Videos/Captures/DakaDaka_clean_handoff - ... 2026-11-25 14-03-19.mp4`): "the attack animation
got worse", then: "the problem is the motion itself, the reference has a completely different trajectory".
Why the checks missed it: the review sampled a frame every 1/15 s (a strike lasts 0.12-0.2 s, so it showed one or two frames of it),
I judged whole sheets scaled down several times, and the 5b reading of the reference came from the same kind of small overview.
In the game the arm came from the lower right and lay across the whole screen for off-side cuts, with the blade standing upright.

Reference re-read at every frame, enlarged (first attack, 3.9-4.6 s):
- wind-up, 6 frames: the hand rises to the upper right and leaves at the edge; the blade is laid over horizontally with its point
  towards the middle of the screen;
- about 5 empty frames;
- strike, 6-7 frames: an upright forearm (hand up, elbow below the picture) enters at the right edge at mid height, moves to the
  left no further than the middle while sinking and getting smaller, leaves at the bottom; the blade lies back behind the hand
  (up and to the right) the whole time and never whips forward;
- second attack: forearm low along the bottom edge from left to right, blade trailing to the left;
- third: from the right edge towards the middle and away from the camera, blade up.
Adaptation (`SwingMotion.TrySample`, the `CockBesideCamera` branch, first person only; our own motion): hand path from the weapon
side edge to the middle with a sink, close to the camera (further out the arm cannot reach the upper half of the view); a
descending cut is shown as a move across; the blade trails for the whole strike; in the wind-up it is laid over and the hand
goes out up beside the head (`CockPoint`), above (`CockAbove`) or low (`CockOffSide`); `FirstPersonArmsDriver.SwingPole` hangs
the elbow below the hand during a cut. Bodies (bots) are unchanged in this pass.
`EmberdelveComboReview` now samples every 1/30 s, 8 columns.

Evidence: mirror run `Logs/runs/20261125-141430-341` Visual 216/0, Hands 92/0, NetCombat 396/0, EditMode 1675 / the same 12 known
failures; it synchronized before the last change of path constants (`Centre`, `HalfLength`, `Bulge` of the two first-person frames),
which compiled in the working Editor and were rendered there. Stills: `Logs/review/combo/arming-step1-30hz.png`,
`short-step1-30hz.png`, next to `reference-windup.png` / `reference-strike.png`. Still no motion proof; the user's recording is the
only motion evidence so far and it predates this pass.

### Pass 5e: combo transitions and the strike path, after the second in-game recording (25 Nov, uncommitted)

User recording `...2026-11-25 14-29-47.mp4`: "worse again: between combo steps the sword shows its idle pose for a frame, the
trajectory is wrong". Read at every frame (cropped to the Game view). Confirmed defects of pass 5d:
- Idle flash between steps: `SwingMotion` blended every swing out of the ready pose (weight 0 at its start) and back into it over
  the recovery. In a combo the next swing starts when the previous one ends, so the hand went end pose -> ready pose -> wind-up
  and the upright idle sword was on screen for several frames between strikes. The review never showed it: it rendered each step
  alone, never two steps chained through the driver's state.
- The pose held at the end of a strike (while the combo waits) was inside the picture, blade forward.
- Off-side cut: the arm lay across the whole lower half of the screen; the downward cut came from straight above, the forearm
  passing in front of the camera.
- The review camera used 70 degrees, the game uses `GameConfig.fieldOfView` (68).

Changes (first person only, our own motion):
- `FirstPersonArmsDriver.CutBlend`: one state for the shown pose across swings. A swing is taken up from wherever the hand is
  shown at that moment (ready pose for the first, end of the previous strike inside a combo) over `TakeUp` 0.20 s; inside a combo
  the way goes round below the picture with the blade pointing away (`ChainDetour`, `Stowed`); the hand returns to the ready pose
  only when no swing follows (`ReturnDelay` 0.06 s, `Return` 0.30 s). `SwingMotion` no longer blends to the ready pose in the
  first-person frames (weight 1).
- Strike path: enters from the weapon side with the hand in the picture, crosses to the middle at an even pace and sinks late,
  ends below the picture with the point down and away; the blade stays behind the hand for the first half of the strike and
  swings through in the second (`SwingThroughFrom`). Off-side flat cuts stay along the bottom edge. Downward cuts wait beside the
  head on the weapon side. Two-handed: the hands wait below the picture (beside the head the second forearm covered the view).
- Thrust in first person: drawn back at once and held, thrown forward at the end of the wind-up.
- `EmberdelveComboReview`: chains the steps through `CutBlend` at 60 Hz, a frame every 1/30 s, the game's field of view, plus half a
  second after the last step (the return). `RenderBatch` is the entry for a batch Editor on the mirror.

Process note: the working Editor was in Play mode (the user was playing), so nothing was compiled or rendered in it. Reviews were
rendered by a batch Editor on the mirror (`-executeMethod Emberdelve.EditorTools.EmberdelveComboReview.RenderBatch`) after copying the
five changed scripts there; a failed first batch run left `Temp/UnityLockfile` behind with no process holding it, removed by me.

Known and not fixed: the arming sword's last step (thrust, recovery 0.91 s) keeps the extended arm on screen for most of that
recovery; bodies (bots) still use the pass 5b/5c motion and were not compared with anything in motion; torch and fists unchanged.
Still no motion proof of this pass: stills at 1/30 s (`Logs/review/combo/chain-{short,arming,two,rapier}-30hz.png`).

Evidence: mirror run `Logs/runs/20261125-145133-715` Visual 216/0, Hands 92/0, NetCombat 395/0 (396 checks in the earlier runs; no failure, the one-check difference was not investigated); EditMode 1675, the same 12 known unrelated failures by name.

### Pass 5f: stroke ribbon follows the view; the short sword closes with a thrust (user, 25 Nov, uncommitted)

User after recording `...2026-11-25 15-05-59.mp4`: "not bad now; make the third strike a thrust; when moving and attacking at
once the attack stays where it was started instead of following the player and the camera".

- What stayed behind: the amber stroke ribbon of the player's own swing (`SwingTrail`). Its points were world-space vertices, so
  while the player walked or turned the ribbon hung where the strike began (visible in the recording as a stripe far from the arm).
  The arms are children of the camera and the authority's damage segment already follows the live eye and aim
  (`commitSwingAim` 0, turn ceiling 720 deg/s), so neither was the cause. Fix: `SwingTrail.SetSpace`; `FirstPersonArmsDriver` keeps
  its ribbon in the camera's space. Ribbons of other bodies stay in world space.
- Short sword chain: cut, cut, THRUST. This is OUR OWN choice at the user's request, not the reference (its third attack is a cut,
  340). New `Pattern_ShortThrust` (`short_thrust`, kind Thrust, the arming thrust's shape, the reference third attack's timing
  0.615 / 0.172 / 0.426, move factors 0.95 / 0.90, 2 targets). `Weapon_AshenShortSword.swingPatterns`: index 2 = the thrust, the
  reference cut `short_cut_3` kept at index 6 outside the chain; riposte and overhead indices unchanged.
  `EmberdelveDarkerCombat` builds the same (`Thrust` helper). The working Editor was in Play mode, so the two assets were written
  as files with the values the builder gives; the builder was NOT run afterwards (run `Emberdelve/Data/Apply Dark and Darker
  Combat Timing` once to confirm it leaves them unchanged).
- Gates follow the data: `SweptMelee` (seven patterns, index 2 a thrust, index 6 the cut), `CombatFlow` (chain kinds).
  `FlatArcFixture` gives the flat arc standing in for a thrust the arc's own reach numbers, as the scenarios were laid out.

Evidence: mirror run `Logs/runs/20261125-151224-698`: CombatFlow 615/0, SweptMelee 1066/0, BladeDefense 605/0, Riposte 147/0,
CombatData 22/0, TurnCeiling 164/0, ActionSpeed 196/0, Visual 216/0, Hands 92/0, NetCombat 396/0; EditMode 1680, the same 12 known
unrelated failures by name. A null guard in `UpdateViewTrail` was added after that run (compiled in the batch render only).
Stills: `Logs/review/combo/chain-short-thrust-30hz.png`. The ribbon fix has no visual evidence at all (the review renders no
ribbon); no motion proof of this pass.
Known: after the thrust the extended hand stays on screen for the 0.43 s recovery.

### Pass 5g: the two-handed sword opens with a thrust; commit (user, 25 Nov)

User: "good now; make the first strike of the two-handed sword a thrust and commit everything".
- Chain of `Weapon_TwoHandedSword`: THRUST (`great_thrust`, index 3), left-to-right slash, cut 3. The thrust takes attack 1's
  reference timing (0.932 / 0.229 / 0.283, move 0.825 / 0.65). The reference longsword's attack 1 has no direction value, which by
  the table's own rule is a thrust, so this also follows the reference; the previous right-to-left slash was our stand-in and
  stays in the list outside the chain (index 1).
- Written by the builder this time (`Emberdelve/Data/Apply Dark and Darker Combat Timing`, working Editor, Play stopped). The same
  run left `Pattern_ShortThrust` and `Weapon_AshenShortSword` as hand-written in pass 5f (no diff), which confirms those values.

Evidence: mirror run `Logs/runs/20261125-153652-244`: CombatFlow 615/0, SweptMelee 1066/0, BladeDefense 605/0, Riposte 147/0,
CombatData 22/0, TurnCeiling 164/0, ActionSpeed 196/0, ProjectileSim 109/0, DrawSpeed 253/0, ReloadSpeed 171/0, ProjectileBlock
202/0, Visual 216/0, Hands 92/0, NetCombat 396/0; EditMode 1680, the same 12 known unrelated failures by name.
Still: `Logs/review/combo/chain-two-thrust-30hz.png`. User accepted the motion of passes 5e/5f in play ("good now"); the
two-handed thrust and the camera-space ribbon were not seen in motion by anyone yet.
Left out of the commit: `Art/Materials/*.mat` drift, `ProjectSettings/TimeManager.asset` and
`Assets/Scenes/Oasis/Art/Environment/Textures/Cliff_Ramp.asset` (not this work).

## Pass 6: corpses, bot archers, arrows against a guard, nocking and reload (user, 25 Nov)

User: "bots' corpses cannot be walked through; bots have no bow draw or shot animation; arrows are not parried; the first-person bow
looks good now, add taking the arrow from a quiver (on the right hip) and a reload animation (look at how the reference does it)".

Reference facts used (decoded montages, `Item/Weapon/RecurveBow/Animations`):
- `AM_RecurveBow_Stand_Attack`: sections Default 0 / AimHold 1.5 / End 3.1 s; sound cues at 0.010, 0.382, 0.740, 1.414 s.
- `AM_WP_RecurveBow_Stand_Attack_Start` (the weapon's own montage): `DCPreReduceAmmoCountOnSourceObject` at 0.109 s. INFERENCE: the
  weapon mesh shows its ammunition and one arrow leaves it at 0.11 s, i.e. the spare arrows are kept at the bow, not in a quiver.
- `AM_RecurveBow_Reload`: 2.967 s, sound cues at 0.022 and 1.081 s, the reload event at 2.5 s.
Bone curves are not decoded; the motion below is ours on these timings.

1. Corpses. Cause: a dead `PlayerRig` (a rival bot is one) kept its `CharacterController` enabled. Monsters already switch their
   colliders off. `PlayerMovement.BeginCorpse` lets the body fall to the floor (at most 3 s) and then disables the controller;
   `EndCorpse` restores it when the rig is set up again (`PlayerRig` before `health.Configure`). Not applied on a pure client
   (remote bodies have no live controller there).
2. Bot archers. Cause: `PlayerAvatar.PresentBotCombat` never passed the draw to `RemoteWeaponPresentation` (remote players get it
   from the network pose), so a bot's bow never moved. It now presents `RangedCombat.IsDrawing / Charge`. The bow stays raised for
   0.45 s after the release (`BowFollowThrough`) with the string hand where it let go.
3. Arrows against a blade guard. Finding: blocking a projectile existed, but the blade box is 5.5 cm wide and, from the front, lies
   beside the body's middle line (hilt 0.22 m right of the eyes, tip 0.5 m below them), so a shot at a guarding swordsman
   passed it; the gate even pinned "a torso-line arrow misses the narrow blade". OUR OWN tuning now:
   `GuardBladePose.ProjectileCatchRadius` 0.24 m: a raised blade deflects a projectile passing that close (face and chest from
   the front; legs, flanks, back stay open). Melee keeps the exact box; a shot straight into the hand on the grip is still an
   arm hit. `ProjectileBlock` gate: the frontal torso arrow is now expected to be deflected, the "misses the blade" cases come
   from the open side (60 degrees). Shields were already wide and are unchanged. NOT VERIFIED which weapon the user tested with.
4. First-person bow.
   - Nocking is the first quarter of the draw (`BowPresentation.ArrowTakenAt` 0.07 = 0.11 s, `ArrowNockedAt` 0.25 = 0.38 s of the
     1.5 s draw): the string hand goes down to the quiver on the right hip (outside the picture), brings an arrow up by its nock
     end and sets it on the string; the string moves over the rest (`StringDraw`). The authority's charge is unchanged: only the
     shown string starts later. After the release the hand eases back from where it let go.
   - Reload (`PoseBow(.., reload)`): the bow is lowered and canted; the hand goes to the pack behind the right shoulder (0.30),
     returns with a handful of arrows at the 1.08 s cue (0.43), carries them through the picture down into the hip quiver (0.78),
     comes back (0.90-1). With the quiver on the hip (user decision) a reload refills that quiver; the reference keeps its spare
     arrows at the bow (inference above), so this part is our choice.
   - Bodies: the same nocking from a quiver built on the right hip of whoever carries a bow (`PlaceQuiver`).
   - `DrawSpeed` gate follows `StringDraw`.

Review tools: `EmberdelveBowReview` (draw 0.03-1.0 and reload 0.08-0.95, game field of view, `RenderBatch`), `EmberdelveAvatarReview`
(`RenderBatch`, bot draw at 0.06 / 0.16 / 0.5 / 1). Stills: `Logs/review/bow/nock-and-draw.png`, `Logs/review/bow/reload.png`,
`Logs/review/avatar/bot-bow-draw.png`. The working Editor was in Play mode, so everything was compiled and rendered on the mirror.
No regression test was added for the corpse (it needs a stepped physics scene); it is unverified until seen in play.
Not done: no reload animation on bodies (bots); no visible quiver in first person (it is below the picture).

Evidence: mirror run `Logs/runs/20261125-162055-062`: ProjectileBlock 204/0, ProjectileSim 109/0, DrawSpeed 253/0, ReloadSpeed 171/0, CombatFlow 615/0, SweptMelee 1066/0, EnemySweep 210/0, Visual 216/0, Hands 92/0, NetCombat 396/0, NetMovement 198/0; EditMode 1680, the same 12 known unrelated failures by name. Earlier run 20261125-161506-535 also had Riposte 147/0 and BladeDefense 605/0. No motion proof and no in-game check of any of the four items.
