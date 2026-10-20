# P2 baseline sweep (tree at HEAD d00a3617)

Unity 6000.2.5f1, batch mode (`-batchmode -nographics`, `-quit` only for ValidateSlice since it does not self-exit), one process at a time, sequential. Logs in `Logs/p2_<name>.log`, exit codes in `Logs/p2_exitcodes.txt`. No source or tests were edited; nothing committed.
Success = `PASS ... failures=0`, no `error CS`, no `FAIL` line. All logs: 0 `error CS`.

| Entry point | Log | Result line | Checks | Verdict |
|---|---|---|---|---|
| Smoke RunCombatFlow | p2_combat.log | `[Smoke:combat] PASS checks=212 failures=0 exit=0` | 212 (baseline 201) | PASS |
| Smoke RunArcherFlow | p2_archer.log | `[Smoke:archer] PASS checks=117 failures=0 exit=0` | 117 (baseline 115) | PASS |
| Smoke RunEnemyAi | p2_ai.log | `[Smoke:ai] PASS checks=20 failures=0 exit=0` | 20 | PASS |
| Smoke RunDeath | p2_death.log | `[Smoke:death] PASS checks=32 failures=0 exit=0` | 32 | PASS |
| Smoke RunExtraction | p2_extraction.log | `[Smoke:extract] PASS checks=24 failures=0 exit=0` | 24 | PASS |
| Smoke RunLobbyFlow | p2_lobby.log | `[Smoke:lobby] PASS checks=19 failures=0 exit=0` | 19 (baseline 18) | PASS |
| Smoke RunResultLoop | p2_resultloop.log | `[Smoke:resultloop] PASS checks=62 failures=0 exit=0` | 62 (baseline 61) | PASS |
| AttributeRulesGate | p2_AttributeRules.log | `[AttributeRules] PASS checks=162 failures=0` | 162 | PASS |
| AttributeDataGate | p2_AttributeData.log | `[AttributeData] PASS checks=28 failures=0` | 28 | PASS |
| AttributeSkillGate | p2_AttributeSkill.log | `[AttributeSkill] FAIL checks=884 failures=11 authorityAccepted=55 authorityRejected=0 exit=1` | 884 | FAIL (stale fixture, see below) |
| AttributeVitalsGate | p2_AttributeVitals.log | `[AttributeVitals] PASS checks=200 failures=0 exit=0` | 200 | PASS |
| AttributeActionGate | p2_AttributeAction.log | `[AttributeAction] PASS checks=226 failures=0 authorityAccepted=16 authorityRejected=3 exit=0` | 226 | PASS |
| ArmorAffixGate | p2_ArmorAffix.log | `[ArmorAffix] PASS checks=44 failures=0` | 44 | PASS |
| FlatDamageAffixGate | p2_FlatDamageAffix.log | `[FlatDamageAffix] PASS checks=42 failures=0` | 42 | PASS |
| GearInstanceGate | p2_GearInstance.log | `[GearInstance] PASS checks=62 failures=0` | 62 | PASS |
| ItemInstanceGate | p2_ItemInstance.log | `[ItemInstance] PASS checks=49 failures=0` | 49 | PASS |
| LootRollerGate | p2_LootRoller.log | `[LootRoller] PASS checks=242 failures=0` | 242 | PASS |
| NumericAffixGate | p2_NumericAffix.log | `[NumericAffix] PASS checks=41 failures=0` | 41 | PASS |
| RolledAttributesGate | p2_RolledAttributes.log | `[RolledAttributes] PASS checks=83 failures=0` | 83 | PASS |
| BuilderOwnershipGate | p2_BuilderOwnership.log | `[BuilderOwnership] PASS checks=20 failures=0` | 20 | PASS |
| EmberdelveBootstrapMenu.ValidateSlice | p2_validateslice.log | `[Validate] PASS - level is connected and every gameplay reference resolves.` | n/a (no count) | PASS |

Not rerun (already in P1): AffixDefinition, AffixProfile, ChestInstance, ChestPartial, RunInventoryFlow, RunInventoryUiFlow.
Total: 20 of 21 entry points pass; 1 fails. All exit codes were 0 except AttributeSkillGate (1), which matches its log.

Check counts have grown versus the doc baselines (combat 212 vs 201, archer 117 vs 115, lobby 19 vs 18, resultloop 62 vs 61); all were zero-failure.

## Other log hygiene
- No NullReference, no unexpected exceptions. The only "Exception" text outside AttributeSkill is Unity's `IOException ... accepting client connection` in BuilderOwnership and LootRoller (editor IPC noise, harmless). In AttributeSkill the 920 "Exception" matches are just the stack-frame name `TryStep(..., out System.Exception&)`.
- Only `Debug.LogError` lines are the 11 AttributeSkill mismatches. No LogError in any other log.
- Warnings: `[EmberdelveInput] No InputActionAsset assigned; using built-in fallback bindings.` in AttributeAction and AttributeVitals (gates build rigs without the input asset; expected, not a defect).
- Licensing 404 / "Access token is unavailable" lines are environment noise (batch, no Unity login).

## Failures

### AttributeSkillGate: 11 failures, one root cause, verdict: STALE FIXTURE (gate bug), not a runtime defect
Failing checks (all in `RunBattleCryFlow`, `EmberdelveAttributeSkillGate.cs` ~line 921):
1. `u2a5_warrior_cha-2_cry placed in the bag`
2. `equip u2a5_warrior_cha-2_cry in Head refused: U2A5 CHA -2 is not in the bag to equip`
3. `warrior CHA 8 after the cursed charm: got 10, expected 8`
4. `ability scale query at CHA 8: got 1.0, expected 0.96`
5. to 11. the CHA 8 battle-cry cast: buff duration 6.0 vs 5.76, and host 0/1/2 slow magnitude (0.55 vs 0.568) and slow duration (4.0 vs 3.84).
Checks 3 to 11 are cascade: the -2 charm never entered the bag, so it was never worn and CHA stayed 10, scale stayed 1.0.

Root cause: the gate's fixture id `"u2a5_warrior_cha-2_cry"` contains a `-`. `Inventory.TryAdd` for equippable items mints a Common `ItemInstance` via `ItemIdentityIssuer.TryCreateInstance`, which calls `ItemIdentityRules.IsStableItemId` (`Runtime/Items/ItemInstance.cs:179`). That rule allows only `a-z`, `0-9`, `_` after a leading lowercase letter, and rejects `-`. Mint is refused, `TryAdd` rolls back and returns false (reason is surfaced only as a toast, hence not in the log), and the charm is not in the bag. The other charm ids (`cha8_cry`, `cha10_head_cry`, ...) are valid and pass. The runtime rule is correct and intended (stable ids, documented); the gate fixture predates it. Other gates and smokes that pass use valid ids.
Suggested fix (not applied): rename the fixture id to e.g. `u2a5_warrior_chaneg2_cry` (also anywhere else the gate uses a `-` in an item id; only this occurrence was found by grep of `cha-2`). Expected to bring the gate to 884+ checks, failures=0.
Not environment (no input device or similar involved), not a runtime defect.

## Tree state
`git status` after the sweep shows only `Docs/PLAN_CLAUDE.md` (modified) and `Docs/Reviews/CODEBASE_HEALTH_20261020.md` (untracked), neither touched by this task; no Data/scene assets changed by ValidateSlice/BuildAll. Logs and this file are the only output of P2.
