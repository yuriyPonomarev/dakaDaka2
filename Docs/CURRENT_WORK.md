# DakaDaka - current work

Updated 8 November 2026. Process commit: 3616c4fc.
Roadmap: ROADMAP.md. Open risks: CRITICAL_DEBT.md. Rules: ../AGENTS.md.
Compact readout: powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Get-WorkStatus.ps1

## Current evidence

- Development harness milestone completed: change-based selection, verified build cache and stable localhost test paths. Final 201255-412: NetAdmission 107/0, NetProof 35/0; fresh/store 36.32s, HIT 8.18s. Planner 13/13 and final cache fixture 13/13 pass. Details: Tasks/HARNESS_20261108.md; commands: HARNESS.md.
- Uncommitted-work inventory: Tasks/WORKTREE_AUDIT_20261108.md. Commit only completed tasks by milestone; no WIP snapshots. Map/inventory/animation changes remain pending their acceptance criteria.
- Full mirror run 20261108-190643: EditMode 887/887; 68 of 69 gates passed. Sole failure: smoke loot fixture placed player on a modular plinth. This full run is FAIL, not green.
- Fresh passes in that full run: NetMovement 189/0, NetFinal 172/0, NetInventory 146/0, NetDoor 37/0, NetCombat 312/0, NetExtraction 136/0, NetSkill 127/0, Hands 92/0, Visual 207/0, DrawSpeed 203/0.
- Fixed smoke fixture to choose supported capsule clearance, probe reach and production LOS; original target/collection assertions retained. Focused 20261108-193032: smoke 249/0 and equipment-drop 17/0 PASS. Original failing seed rerun 20261108-193253 passed 249/0 (same map hash; selected pickup differs).
- NetMovement telemetry race fixed by bounded fresh-counter AND owner/observer convergence wait; original tolerances retained.
- Audit found equipment-drop missing from net/all. Added it (now all=70); full 190643 predates this membership change, so drop was verified separately. No single 70-gate green run yet.

## Next three actions

1. Add failure-after-offhand-stow test before fixing two-handed equip atomicity (ITEMS-01); use the harness checkpoint/selection loop.
2. Real two-client inventory/reconnect/raid acceptance; commit completed milestone tasks only.
3. Hands/block and authoritative attack phase timing in motion. Retain full + targeted evidence; next full run only at substantive integration/release.

## Limits and ownership

- Test audit: Tasks/TEST_AUDIT_20261108.md (67 test files, measured runtime, overlap, omissions and missing critical scenarios). No tests deleted or tolerances weakened.
- Hands grip/guard and enemy trigger fixes have automated coverage; appearance unaccepted, enemy swing duration still approximate. Production timer remains disabled pending HUD review. Stash not implemented.
- Default session size is 9; parser allows up to 64, not measured capacity. Network movement proof uses four clients; nine-client load acceptance missing.
- Preserve inherited mixed changes (map/content, inventory/extraction, combat/visuals, gates). Process/harness work is separate from unfinished gameplay; no blanket commit/reset. Exclude material drift and local Pipeline settings.

Run 193510 was a redundant full rerun, explicitly cancelled after user feedback; raw runner exit is not a newly discovered regression. Main Editor preserved. No tests remain running from this work.
