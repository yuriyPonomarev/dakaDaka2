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
session was working in this checkout at the same time (skeleton archer and locomotion, committed as 1eb80d552 at
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
