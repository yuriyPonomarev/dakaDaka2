# Test audit — 8 November 2026

Scope: inventory of all 67 EditMode C# files, 887 NUnit results from run 190122; gate-set membership; selected high-risk implementation/assertions (movement, final, inventory/drop/hands, draw speed, skills, enemy visual/wire); timing from full run 042911. This is not a claim that every assertion received semantic review or mutation testing.

## Closure update — 9 November 2026

The findings below preserve the original audit. Since then:
- Atomic equip rollback closed in commit14c070ba with focused failure coverage and real inventory/hands gates.
- Live drop publication failure now exercises rollback through the existing equipment-drop scenario: exact UID/affixes/revision retained, owner/peer see no floor copy, retry and late join pass21/0 (20261109-054809-916).
- Real two-client UI, reconnect, corpse transfer, results and simultaneous exact-UID contention accepted; see RAID_UI_ACCEPTANCE_20261108.md.
- Enemy presentation uses authoritative phase timing and contact-based recoil; host/client motion and actual damage reviewed. Final suite20261109-055711-479 plus targeted corrections accepted; details in CURRENT_WORK.
- Verified build reuse and file-based test selection are implemented and committed; passing test verdicts are never cached. Do not remove fast complementary tests to save editor startup time.

Remaining coverage limits: controlled packet-loss/latency campaign, nine-client capacity and a wider independent runtime map corpus. These are future robustness/release checks, not established defects or claims of supported load. Two layouts were traversed live, including the bridge regression seed. Shield assertion now checks rendered bounds and passes1/1; wall-run fixture now handles fragmented telemetry and passes190/0 on the original failure seed.

## Original findings, priority and action

| Priority | Finding | Evidence / decision |
|---|---|---|
| P0 | Full suite omitted successful equipped-item drop proof | NetInventory.Run calls basic inventory, chest, corpse/host/death and floor-loot, but not ExecuteInventoryDropProof. Added RunInventoryDropProofOnly to net/all; standalone set retained for focused use. Existing full run 190643 started before config change; run missing gate separately afterward. |
| P0 | Movement verdict depended on logging phase | NetOwnerPrediction StatsLogSeconds=5; NetMovement waited 3.5s. Run 185648 showed SNAP error=2.000 and final counters 2/2, test read stale 1/1. Now bounded polling requires fresh counters AND owner/observer convergence at original tolerances. Follow-up 190122 caught observer report arriving later; fixed without widening tolerance. |
| P1 | Two-handed equip rollback is not proven | Equipment.TryEquipSelectedState commits TryUnequipSlotState before incoming transfer, then attempts recursive restoration on failure. Hands gate tests full-bag refusal before displacement and successful stow, not failure after stow, exact revision/events or restoration failure. Add injected second-step failure test before altering transaction implementation. |
| P1 | Drop failure rollback has no real transport proof | InventoryDropRollbackTests cover local injection, success-only net-inventory-drop proves owner/peer/late snapshots. Need server publication failure + exact UID conservation and client convergence. |
| P1 | Real UI/reconnect acceptance missing | Headless RPC/probe success does not test mouse/keyboard routing, UI refresh or a returning player's inventory. NetInventory forced disconnect/corpse and late join are useful but are different cases. |
| P1 | No end-to-end degraded-network campaign found in inspected gates | Pure sanitizer/reconciler tests cover reordering/state, but loopback process scenarios do not establish behavior under controlled packet loss/latency/jitter. Add a bounded seeded profile for movement plus one exact-once inventory transaction. |
| P1 | Animation state != impact timing | Visual asset and network state tests establish clips/states/wire, not rendered weapon contact matching authoritative damage. Enemy phase remains guessed at 0.5s. Add phase-boundary behavior proof with in-motion owner/peer acceptance when timing is implemented. |
| P1 | Reconstructed spawn expectations share production algorithm | EmberdelveModularSpawnProof rebuilds a separate scene but calls production spawn selection. This proves seed/layout agreement, not independent correctness of that algorithm. Keep direct capsule clearance/support/spacing invariants and add a fixed multi-seed corpus for live maps; planner seed sampling alone is not runtime spawn proof. |
| P2 | Full runtime sweep is expensive | 69 gates / 1193.4s in 042911; AttributeSkill 257.41s, NetInventory 116.18s, NetCombat 91.3s. Several network gates build proof players individually. Investigate one immutable proof build per compatible scene/config, retaining separate playable build for NetFinal; fingerprint inputs before reuse. Do not blindly cache stale builds. |
| P2 | Timing and fixture infrastructure deserves consolidation | NetFinal fixed 3.5s pauses; inventory many 350ms sleeps. Replace verdict-relevant sleeps with bounded evidence waits as failures demonstrate; keep actual gameplay timing tests. Shared helper must preserve scenario-specific assertions. |

## Duplication: what is and is not redundant

- Gate-set arrays contain no duplicate entries. `all` repeating members of `fast/combat/net` is intentional: choose one set, not all sets serially.
- `net-floor-loot` runs a subset already included in NetInventory. Useful for focused debugging; do not schedule both in a full acceptance run.
- Three parser-test methods have identical normalized bodies (NetScriptSpecTests.Malformed_IsRejected, CombatNetScriptAndArgsTests.CombatScripts_CannotBeChainedOrGivenADuration, SkillNetScriptAndArgsTests.SkillScripts_CannotBeChainedOrGivenADuration), but different inputs exercise different grammar. Keep coverage; optional helper consolidation only, no material speed saving.
- DrawSpeedRulesTests check numeric duration/floors/invalid values; DrawSpeed gate checks actual input, draw cancellation, weapon visuals, firing once and unchanged vitals. These are complementary, not duplicate tests.
- HandsRulesTests check data/slot rules; Hands gate exercises real equipment/item flow; InventoryDropRollbackTests inject failure paths. Preserve each layer.
- Asset license/hash/import tests do not establish visual quality, but protect provenance and valid rigs. Keep them in asset/integration verification, not every unrelated pure-rule edit.

## Measured economics

887 EditMode cases in 190122: summed test-case execution 5.923s, complete batch 90.66s (startup/import/shutdown included). Slowest individual tests: license/hash 0.493s, sequence exhaustion 0.435s. Bulk deletion of fast tests is poor value. Focus on fewer redundant editor launches, targeted gate sets, and bounded compact result reading.

## Safe changes now / follow-up

Implemented: omitted drop proof wired into net/all; movement telemetry/convergence wait repaired; process verification matrix and compact status reader.
No tests deleted, no assertion tolerances relaxed, no claim of complete coverage. Next tests in order: transactional two-handed equip failure, reconnect inventory/UI, live publication failure, degraded network, animation phase boundary. Stash restart tests belong to future stash implementation, not this baseline.

Capacity gap: default MaxPlayers=9 (NetLaunchArgs), parser accepts 1..64; existing movement process proof uses four clients. A nine-client join/raid/disconnect load proof is needed before claiming the default session size is accepted. Larger values are configuration limits, not measured capacity.

Verified inclusion separately: 193032 equipment-drop 17/0; smoke 249/0, then original-map repeat 193253 249/0. Full 190643 had 68/69 passing before the smoke fixture fix. No fresh unified 70-gate pass claimed. Repeating the full set solely for a green aggregate was unnecessary; 193510 cancelled.
