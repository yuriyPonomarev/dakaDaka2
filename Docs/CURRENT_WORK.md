# DakaDaka — current work

Updated 9 November 2026. Rules: ../AGENTS.md. Roadmap: ROADMAP.md.
Detailed evidence: Tasks/RAID_UI_ACCEPTANCE_20261108.md.
User priority: finish existing features; minimal sufficient checks; commit only completed milestones.

## Completed scope

Existing network raid, modular map, inventory/corpse/extraction/results/retry, hands/guard/enemy timing and readable HUD/countdown are accepted together. Shared dependencies make stages1–4 one integrated milestone; infrastructure and atomic equip were committed separately (3616c4fc/f7670ef2 and14c070ba).

- Real two-client UI: equip/drop/pickup/rejoin, combat/disconnect corpse custody, exact-UID simultaneous pickup (one winner), death/extraction/result/retry/lobby, host restart and peer rejoin.
- Live publication failure retains exact equipped UID/affixes/revision with no owner/peer floor duplicate; retry and late join pass equipment-drop21/0.
- Host/client grip and guard accepted. Enemy attack uses server phase timing; removed baked horizontal travel; wall recoil rewinds from actual contact. Client real hit12 matches phases. V24 real blocked hit0, HP120/120, shield contact/return and weapon swap return reviewed.
- Production600s timer enabled in asset/builder, readable on host/client; process-local5s timeout results reviewed. No claim of waiting600s live.
- Bridge rotation follows connected deck ends. Original regression seed2043687868 and seed11 traversed through deck and both doorways with ordinary movement/E.

## Final verification

Full20261109-055711-479: EditMode924/925,68/70 gates,1184.6s. Its three failures are closed by focused follow-up, with no gameplay changes after that run:
1. Shield test used an offscreen model pivot instead of rendered bounds. Corrected assertion; final-shield-visibility-recovered PASS1/1.
2. NetMovement read only the first telemetry segment of an interrupted walk. Now checks continuity, every endpoint/peak and final displacement at unchanged thresholds. Run20261109-062114-364 PASS190/0 on exact seed-1799084838/hashCF6B9D32.
3. Windows denied stable build directory promotion, preventing NetFinal execution. Prepared payload safely restored; same targeted run NetFinal PASS172/0 with successful stable build publication.

Combined evidence accepts the milestone. The historical full run remains FAIL; do not rerun passing suites merely to obtain one green aggregate. Source/code diff whitespace check passes. Actual visual evidence is separate from automated proof.

## Handoff and limits

- Main user Editor untouched. All tests/builds used ../DakaDaka_ci. V24 test players quit B10/A1; no test/build/player intentionally left running.
- Explicit milestone manifest398 paths. Six local-only changes retained outside commit: Stone_Ceiling.mat, Stone_Floor.mat, Assets/Logs.meta, connection-check screenshot/meta, Pipeline EditorPipelineConfig.json.
- Default session9 players; parser permits64, neither is measured capacity. Four-client movement proof exists. Controlled loss/latency and nine-client load are future release coverage.
- Persistent stash, new content/art/sound/balance are deferred. Existing corpse loot is session-only; procedural ranged art is retained.
- Historical reports are labeled; this file and the acceptance report own current status.

## Next optional steps

1. Use the accepted raid for user playtesting; fix concrete findings with focused checks.
2. Before wider multiplayer release, run bounded packet-loss/latency and nine-client capacity acceptance.
3. Start persistent stash or new content only as a new agreed milestone.
