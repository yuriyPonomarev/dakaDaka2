# Sword data canonical - 24 Nov 2026

## Decision
The committed tuned sword timing data is the intended melee design and is canonical (commits 1e1a44f34 "Sword combos flow ..." and 1b0234139 "Melee feel pass ... seamless combos"). Authoring code (`EmberdelveAssetLibrary`) and the gates follow the data, not the other way round.

Canonical values: combo step scales (windup / recovery) 1/0.5, 0.6/0.5, 0.6/1 for the sword (rapier and two-handed 0.25 recovery, 0.35 windup on steps 2-3); `Pattern_SwordSlashLR/RL` windup 0.28, active 0.30, recovery 0.34; `Weapon_AshenShortSword` legacy windupTime 0.24 / recoveryTime 0.30 (authoring seed only, melee never reads it), cooldown 0.92, comboWindow 0.7, comboFinisherRecovery 0.35.

Derived (authority, step 0): windup 0.28, active 0.30, recovery 0.34 x 0.5 = 0.17, total 0.75, repeat interval max(0.8 x 0.92 = 0.736, 0.75) = 0.75.

## Library changes (`Editor/EmberdelveAssetLibrary.cs`)
- `SetPatternTiming` for the slash patterns now authors 0.28 / 0.30 / 0.34.
- Combo step tables authored with the committed scales (sword, rapier, two-handed, and the other chains).
- Overhead pattern keeps its on-disk trail material (narrow rebuild menu runs without the material library); item catalog list keeps committed order. Net effect: `BuildAll` twice leaves Data assets byte-identical apart from serialized-default fields.

## Gate fixes (cause -> fix)
SweptMelee (732/0):
- "slash windup/active/recovery mirrors the weapon timing" asserted weapon seed == pattern; stale. Now asserts DisplayWindup/Active/Recovery come from the pattern and the pattern is not faster than the seed.
- Repeat interval 0.84 s and wall-stop floor 0.84 s were hardcoded; now `max(0.8 x cooldown, pattern total)` from data. Production chain steps wait for that floor (a quick step ends before it).

CombatFlow:
- Tip chain "step 2 accepted (combo window expired 0.34 s late)" and step 3 follow-on: gate-side. `SwingStep` already waits for the repeat floor (0.736 s from begin for the 0.3 s test pattern), and the test then added `Advance(0.5)`; window end is activeEnd + 0.7 = 0.9 s after begin, the gate reached 0.741 + 0.5 = 1.24 s. Removed the redundant pause (floor wait stays inside the window). Not a gameplay problem.
- `recovery: walk x0.85` and `swap lock (recovery)` x7: gate used the raw pattern recovery (0.34) while the authority swings step 1 with recovery x0.5 (0.17). Now uses `ComboTiming(0, pattern)`, the authority's formula.
- `section 'impact zones' threw` was the tip chain's follow-on.
- Step-2 windup / finisher timing checks (earlier edits) read `comboSteps` scales.

ActionSpeed (196/46): all timing literals (0.24 / 0.30 / 0.84 and DEX/boots/slow/fast scalings) assumed the old sword. Expected values now derive from the loaded weapon: `Expect()` = `weapon.ComboTiming(0, pattern0)` per field divided by the action rate, active floored 0.02, interval `max(max(0.1, cooldown/rate) x 0.8, w+a+r)`; visual midpoint = accepted windup + active/2; slow-again interval compares against the slow swing's interval. The floored and cross-weapon (2 s cooldown) literals are unchanged. Tolerances unchanged; no checks removed. (`ActionSpeedRulesTests.SwordTiming_LiteralPhasesAndRepeatFloor` is a pure-rules test with explicit inputs and stays as is.)

## Gameplay findings (b)
None. With the tuned data the chain is reachable: step 1 total 0.75 s, interval 0.75 s, window closes 0.7 s after active end (0.30 + 0.28 + 0.7 = 1.28 s from begin), so a follow-up at 0.75 s has 0.53 s of slack.

ActionSpeed follow-up (coordinator): "a slow actor legitimately needs longer than the authored neutral weapon cooldown" compared the slow actor's accepted interval (0.915 s) with the raw cooldown (0.92 s); the authority's floor is max(0.8 x cooldown / rate, swing total), so the check now compares with the neutral actor's accepted interval (0.75 s) for the same weapon.

## Mirror evidence

Run 20261124-183929-950: SweptMelee 732/0, CombatFlow 589/0, Riposte 144/0, BladeDefense 361/0, CombatData 22/0, D3Content 53/0, Hands 92/0, CombatAuthority 51/0, TurnCeiling 122/0, DrawSpeed 253/0, ReloadSpeed 171/0, EnemySweep 194/0, NetCombat 397/0; RuntimeSmokeTest.RunCombatFlow 264/2 (baseline HUD passives / skill cooldown only). ActionSpeed 196/0 after the follow-up (run 20261124-184927-727).

History note: commit 830c64fb0 briefly regenerated the Data from the stale library (reverting the tuning); it was reverted in 063119fa3.
