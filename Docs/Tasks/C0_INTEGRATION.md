# C0 — R2/R7 and projectile integration

23 October 2026. Codex reviewed, ran Unity and committed; Luna implemented the finishing changes. This integrates the pre-existing dirty Opus R2/R7 work with the overlapping projectile interception work. Exact initial 57-path checkpoint remains under C:/Users/yrapa/.pi/agent/projects/unity-emberdelve/checkpoints/pre-opus-completion-20261023.

## Accepted behavior

- Actor-specific interactions, loot, extraction, party hostility, session results and viewer events; mixed Warrior/Archer local sessions. Host admission requires the canonical rig reference and finite interaction geometry before chest roll/open or transfer.
- Authority-owned ordinary/skill projectile flight. Sword/rapier blocking requires live blade intersection before body impact; misses damage the body. Shields keep directional guard and cannot parry. Shared visible and authority blade pose; existing combat/guardbreak retained.
- Existing immediate ranged-hit fixtures migrated to real launches and flight. Numerical falloff uses independently predicted swept surface contact. Guard fixture measures authored rapier .70 plus cuirass .08 and retains front/rear/side health checks.
- The 32m affix fixture initially hit a real Servitor at15.09m before its test target. Recorded diagnostic proves correct nearer-body interception. Standalone numeric fixture now suspends/restores scene enemies, asserts its lane is clear, and retains damage literals and .001 tolerance.

## Verification

Root ran the full38-gate batch, then reran only repaired fixture groups and noisy runner results. Final selected evidence below is all PASS with exit0. Original failing/diagnostic logs remain unchanged. Compile errors introduced in fixture drafts were repaired before accepted runs. The ContainerSnapshot fixture deliberately logs its exact throwing-subscriber exception; the runner records that one diagnostic as expected for that entrypoint only. Unexpected Pipeline GET/api/commands shutdown exceptions were not allowlisted: affected CombatAuthority and real Warrior gates were rerun clean.

| Gate | checks / failures | Evidence directory under Logs |
|---|---:|---|
| EmberdelveMultiPlayerGate.Run | 134 / 0 | opus_finish_20261023_04_integration |
| EmberdelveProjectileBlockGate.Run | 50 / 0 | opus_finish_20261023_04_integration |
| EmberdelveEffectiveStatsGate.Run | 17 / 0 | opus_finish_20261023_04_integration |
| EmberdelveLootPipelineGate.Run | 9954 / 0 | opus_finish_20261023_04_integration |
| EmberdelveContainerSnapshotGate.Run | 954 / 0 | opus_finish_20261023_05_runner |
| EmberdelveRuntimeSmokeTest.RunCombatFlow | 231 / 0 | opus_finish_20261023_04_integration |
| EmberdelveRuntimeSmokeTest.RunArcherFlow | 120 / 0 | opus_finish_20261023_04_integration |
| EmberdelveRuntimeSmokeTest.RunEnemyAi | 20 / 0 | opus_finish_20261023_04_integration |
| EmberdelveRuntimeSmokeTest.RunDeath | 35 / 0 | opus_finish_20261023_04_integration |
| EmberdelveRuntimeSmokeTest.RunExtraction | 39 / 0 | opus_finish_20261023_04_integration |
| EmberdelveRuntimeSmokeTest.RunResultLoop | 80 / 0 | opus_finish_20261023_04_integration |
| EmberdelveRuntimeSmokeTest.RunLobbyFlow | 19 / 0 | opus_finish_20261023_04_integration |
| EmberdelveRuntimeSmokeTest.RunInventoryFlow | 415 / 0 | opus_finish_20261023_08_fixture |
| EmberdelveRuntimeSmokeTest.RunInventoryUiFlow | 300 / 0 | opus_finish_20261023_04_integration |
| EmberdelveRuntimeSmokeTest.RunEnemyVisual | 27 / 0 | opus_finish_20261023_04_integration |
| EmberdelveChestInstanceGate.Run | 21 / 0 | opus_finish_20261023_04_integration |
| EmberdelveChestPartialGate.Run | 17 / 0 | opus_finish_20261023_04_integration |
| EmberdelveAttributeActionGate.Run | 255 / 0 | opus_finish_20261023_08_fixture |
| EmberdelveAttributeVitalsGate.Run | 205 / 0 | opus_finish_20261023_04_integration |
| EmberdelveCloakSlotGate.Run | 10157 / 0 | opus_finish_20261023_04_integration |
| EmberdelveCombatAuthorityGate.Run | 49 / 0 | opus_finish_20261023_05_runner |
| EmberdelveSkillAuthorityGate.Run | 161 / 0 | opus_finish_20261023_04_integration |
| EmberdelveArmorAffixGate.Run | 44 / 0 | opus_finish_20261023_04_integration |
| EmberdelveFlatDamageAffixGate.Run | 47 / 0 | opus_finish_20261023_11_flat |
| EmberdelveAffixDefinitionGate.Run | 169 / 0 | opus_finish_20261023_04_integration |
| EmberdelveAffixProfileGate.Run | 13336 / 0 | opus_finish_20261023_04_integration |
| EmberdelveLootRollerGate.Run | 242 / 0 | opus_finish_20261023_04_integration |
| EmberdelveGearInstanceGate.Run | 62 / 0 | opus_finish_20261023_04_integration |
| EmberdelveItemInstanceGate.Run | 49 / 0 | opus_finish_20261023_04_integration |
| EmberdelveNumericAffixGate.Run | 41 / 0 | opus_finish_20261023_04_integration |
| EmberdelveRolledAttributesGate.Run | 83 / 0 | opus_finish_20261023_04_integration |
| EmberdelveBuilderOwnershipGate.Run | 20 / 0 | opus_finish_20261023_04_integration |
| EmberdelveItemCatalogGate.Run | 120 / 0 | opus_finish_20261023_04_integration |
| EmberdelveRuntimeSmokeTest.RunRealCombatWarrior | 103 / 0 | opus_finish_20261023_10_diag |
| EmberdelveRuntimeSmokeTest.RunRealCombatArcher | 118 / 0 | opus_finish_20261023_08_fixture |
| EmberdelveSweptMeleeGate.Run | 638 / 0 | opus_finish_20261023_04_integration |
| EmberdelveCombatFlowGate.Run | 376 / 0 | opus_finish_20261023_04_integration |
| EmberdelveAttributeSkillGate.Run | 925 / 0 | opus_finish_20261023_04_integration |

Total: 38 gates, 39633 checks, zero failures in the final selected runs.

## Limits and next work

These are offline/local multiple-actor proofs. No dedicated server, client replication, sender authentication, persistence or network acceptance is claimed. Runtime stack IDs/commands, full effective-stat integration, assembly separation and the remaining plan continue in OPUS_COMPLETION_PLAN.md. See R2R7.md and PROJECTILE_WEAPON_BLOCK.md for detailed behavior/provenance.
