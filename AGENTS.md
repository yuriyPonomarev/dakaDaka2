# DakaDaka — development contract

Unity 6000.2.5f1 / URP 17.2 / NGO 2.7 / C# / new Input System.
Root: Assets/Emberdelve. Design: Docs/Design/UNITY_DESIGN.md.
User-approved process reset: 8 November 2026.

## Start here (keep context small)

Read Docs/CURRENT_WORK.md first, then only the active task report and relevant code.
Docs/ROADMAP.md owns milestone order; CURRENT_WORK owns current status and three next actions.
Archive and old chats are historical evidence, not mandatory startup reading.
Use rg scoped to relevant folders. Read summary/errors first, full logs only to diagnose.

## Work cycle

1. One active slice: state the defect/behavior, files owned, acceptance and required checks.
2. Inspect current changes; preserve unrelated and inherited work. Implement the smallest coherent fix.
3. Compile, run focused tests, then relevant mirror gates from the matrix below.
4. Record actual evidence, limitations and next action; rewrite CURRENT_WORK (one page).
5. Review diff and commit only the verified slice, with explicit paths. Never blanket-add inherited work.

One executor by default. Delegate only independent, bounded work when useful; send file paths and a short brief instead of full history. One owner per file. Only the coordinator starts mirror tests/builds; workers request checks and report results. Do not stop useful work merely to poll. Poll bounded calls; user updates for meaningful progress, failures, or decisions.

## Verification matrix

| Change | Required focused proof |
|---|---|
| Pure rules | Relevant EditMode fixture; affected fast gate |
| Movement/spawn/map | MovementProofGeometry tests; NetMovement, NetFinal; NetDoor/NetInventory if placement changed |
| Inventory/equipment | Inventory/Hands EditMode tests; NetInventory; NetExtraction if corpse ownership changed |
| Combat/skills | Relevant rules tests; NetCombat/NetSkill and affected combat gates |
| Arms/animation | Visual EditMode tests; Hands, Visual; NetCombat if network enemy presentation changed; actual motion review |
| Process/docs only | Review paths/commands for accuracy; no gameplay test rerun solely for prose |

Compile + focused proof per slice. One full `Run-All.ps1 -Set all -EditMode` after integration or before accepting a baseline/build. Repeat passing checks only after relevant changes or new evidence. Never reduce expectations to obtain green.
Readiness is separate: implemented -> automated proof -> visual proof -> accepted. Report seed/version/run path; old passes do not validate later changes. Generated maps need more than one layout for targeted acceptance.

## Unity pipeline

Never close the user's Editor or invoke EditorApplication.Exit in it. Batch/playmode/scene-opening/network gates run only on ../DakaDaka_ci.
CLI in the main project (installed Pipeline command names; query if names differ):
- `unity status --format json`
- `unity command editor set_auto_tick --enable true`
- `unity command script recompile`, then `script recompile_status`
- `unity command test run --mode editor --filter <fixture> --async_tests true`, then `test status`
Read only test summary/errors; do not print entire successful test trees.
If async test state is stuck, diagnose/cancel that test before starting another. Never start overlapping mirror runs.
Direct local file edits and CLI are allowed; MCP is optional. No shell-within-shell deletion/moving.

Mirror: `powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Run-All.ps1 -Set <gate-or-set> [-EditMode] [-Build]`.
Runner checks mirror lock, syncs source, runs batch Unity, writes Logs/runs/<stamp>/summary.txt + run.json.
Set may be fast, combat, net, visual, all or comma-separated gate names. Keep one run alive and collect completion. Missing summary/timeout is not success.
Playable builds follow accepted integration, not every edit. Network guide: Docs/NETWORK_PLAY.md.

## Invariants

- No package/engine upgrades or working-tree resets. Use existing scene/prefab builders, no monolithic runtime-generated replacement.
- Client sends intent; server decides damage/items/outcomes. Shields never parry.
- Shared ScriptableObjects immutable at runtime; item UID/affixes belong to instances.
- NUnit EditMode preferred for new logic tests. No source-text grep assertions in gameplay gates.
- Assets free/CC0 only; license/source in Docs/ASSET_PLAN.md.
- Exclude Art/Materials/*.mat drift and ProjectSettings/Packages/com.unity.pipeline/ from commits.
- Do not bundle old unverified work into a supposedly completed slice. Explain mixed-worktree limitations.
