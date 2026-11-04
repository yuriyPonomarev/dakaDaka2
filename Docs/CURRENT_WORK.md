# CURRENT WORK - 4 Nov 2026

Rewrite this file each slice; history is in `Docs/Archive/`.

## State
- Base HEAD `e6155da8` + four uncommitted slices: V3a (Poly Haven props), V3b (undead enemies), V3c (weapon models + first-person arms), **U3C3 (sword parry -> riposte)**.
- U3C3 done, uncommitted (details in `Docs/Tasks/U3C3.md`): a sword block (never a shield) pressed again within 1.0 s (data: `riposteWindow`) starts a server-validated riposte of 2 strikes x1.5 (`riposteDamageMultiplier`) on the swept-melee path; it never breaks the opponent's block; impact power is not multiplied; one parry gives one riposte; damage, death, stagger, a swing or a weapon change cancel it; enemies do not riposte. Data on ashen_short_sword and ember_rapier (`canRiposte`).
- Checks: console errors none; EditMode 663/663 (629 + 34 new); fast gates 12/12; new gate `Riposte` 137/0; full `Run-All -Set all -EditMode` RESULT PASS (EditMode 663/0, 65/65 gates, `Logs/git/runall_u3c3_2.log`).
- Note: the first full Run-All failed once in `RuntimeSmokeTest.RunCombatFlow` (front-facing block reduction) because the player's rolled weapon differs per run (26 vs 15 base damage, reduction 0.25 vs 0.82); it passed alone and in the rerun. Flaky, not from this slice; worth making that check data-derived.
- Not verified in a build: the play feel of the FP arms during the riposte; there is no HUD cue.

## Known compromises
- V3c: FP weapon follows the arm clip while the Sweep_Edge bar draws the true damage segment; library clips are third person; walk reuses idle.
- U3C3: no host+client network session test of the riposte (covered by the authority gate and existing NetCombat gates); a refused swing begin keeps the parry.

## Decisions in force
- Sword-vs-sword block holds (`impactResist 15`); the sledgehammer breaks guard. Guard endurance/stamina OFF.
- Realistic CC0 assets (props V3a, enemies V3b, weapons V3c).
- Shields block and never parry; the sword parry is its own data flag (`canRiposte`), separate from `DefenseCapability.Parry`.

## Next
1. Playable build, play-test FP arms and the riposte, then commit V3a, V3b, V3c, U3C3 (separate commits).
2. U4A contested chests over the network, then U4B corpse/raid timer/extraction, U4C stash, U3C2.

## Blockers
- none. Repo size: PolyHaven about 203 MB + weapons; Quaternius 46 MB of FBX.
