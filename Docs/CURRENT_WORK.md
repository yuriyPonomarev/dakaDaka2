# CURRENT WORK - 4 Nov 2026

Rewrite this file each slice; history is in `Docs/Archive/`.

## State
- U3C3 (sword parry -> riposte) is committed `de67cce2`. **FIX2 is done, uncommitted** (`Docs/Tasks/FIX2.md`): the user's playable build showed no parry because the warrior's off-hand shield made the kit "a shield guard" and U3C3 refused it. Root cause, decisions and tests are in the task doc.
- Rules now in force:
  - A guard that HOLDS takes the whole hit: 0 HP (`BladeGuardRules.HeldGuardReduction = 1`), shields included. A broken guard keeps `authored reduction x guardBreakReductionScale`.
  - The parry is the sword's: `WeaponData.riposteWithOffhandShield` (true on ashen_short_sword) lets the warrior parry with sword + shield; a shield alone never parries. Flip the flag in data to forbid it.
  - A held block refreshes the parry; only real damage cancels it; blocked projectiles do neither.
- Checks: console errors none; EditMode 668/668; fast gates 12/12; Riposte gate 144/0; NetCombat 272/0 (new `melee-parry` session with two real player processes); smoke real-path riposte on a real PlayerRig + EnemyAgent; full `Run-All -Set all -EditMode` RESULT PASS (EditMode 667/0, 65/65 gates, NetCombat 272/0, smoke 247/0, `Logs/git/runall_fix2.log`).
- Not verified in a build: play feel of the FP arms during block/riposte (no hit-react on a 0 HP block, no HUD cue).

## Known compromises
- V3c: FP weapon follows the arm clip while the Sweep_Edge bar draws the true damage segment; library clips are third person; walk reuses idle.
- Enemies do not riposte. A refused swing begin keeps the parry.
- `RuntimeSmokeTest.RunCombatFlow` boosts one enemy's health for the riposte proof and restores it.

## Decisions in force
- Sword-vs-sword block holds (`impactResist 15`); the sledgehammer breaks guard. Guard endurance/stamina OFF.
- Realistic CC0 assets (props V3a, enemies V3b, weapons V3c).
- The sword parry is its own data flag (`canRiposte`), separate from `DefenseCapability.Parry`.

## Next
1. Commit FIX2; rebuild the playable build and play-test block (0 HP), parry and riposte as the warrior and the archer.
2. U4A contested chests over the network, then U4B corpse/raid timer/extraction, U4C stash, U3C2.

## Blockers
- none. Repo size: PolyHaven about 203 MB + weapons; Quaternius 46 MB of FBX.
