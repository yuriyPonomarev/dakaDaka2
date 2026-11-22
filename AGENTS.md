# DakaDaka - development contract

Unity 6000.2.5f1 / URP 17.2 / NGO 2.7 / C# / new Input System.
Root: Assets/Emberdelve. Design: Docs/Design/UNITY_DESIGN.md.
User-approved process reset: 8 November 2026.

## Start here (keep context small)

On this machine, the only development checkout for all agents is `C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff` (explicit user instruction reinforced 22 November 2026). Set every mutating command's workdir to this exact path and verify `git rev-parse --show-toplevel` before work. Every delegated task must name it. Open, edit, import/generate assets, run builders, commit and push only from this checkout. `../DakaDaka` is read-only legacy material with a different Git history: compare or recover work from it into clean_handoff, but never develop or commit there, even if an older chat used it or its commits are newer. `../DakaDaka_ci` is only the disposable test/build mirror synchronized from clean_handoff, never from DakaDaka. On other machines, use the checkout cloned from the current GitHub `main` instead of the machine-specific path.

Read Docs/CURRENT_WORK.md first, then only the active task report and relevant code.
Docs/ROADMAP.md owns milestone order; CURRENT_WORK owns current status and three next actions.
Archive and old chats are historical evidence, not mandatory startup reading.
Use rg scoped to relevant folders. Read summary/errors first, full logs only to diagnose.

## Work cycle

1. One active slice: state the defect/behavior, files owned, acceptance and required checks.
2. Inspect current changes; preserve unrelated and inherited work. Implement the smallest coherent fix.
3. Inspect `python Tools/harness.py plan --since <checkpoint-or-commit>`; run the selected checks with `run`. Compile/focused proof only; use the matrix below to review routing, not to add duplicate runs.
4. Record actual evidence, limitations and next action; rewrite CURRENT_WORK (one page).
5. Commit only completed, verified tasks grouped by roadmap milestone, with explicit paths. Never commit WIP snapshots or blanket-add inherited work. Automated proof alone does not close milestones requiring visual/client acceptance. Keep unfinished changes uncommitted and record their missing exit criteria.

One executor by default. Delegate only independent, bounded work when useful; send file paths and a short brief instead of full history. One owner per file. Only the coordinator starts mirror tests/builds; workers request checks and report results. Do not stop useful work merely to poll. Poll bounded calls; user updates for meaningful progress, failures, or decisions.

## Finish existing features first (user priority)

The existing raid was accepted in milestone7f203c9c. On9 November the user authorized a crypt architecture/content pass inspired by Dark and Darker and LOOT-01: paired inventory/container UI and server-owned chest/mob loot. On11–12 November the user explicitly authorized offline persistent stash/preparation/trading, followed by combat matching the current original Dark and Darker, then textures/models/environment/light. Follow CURRENT_WORK and preserve existing work. Server-persistent economy and unrelated new systems remain separate scope.
For each slice choose one concrete acceptance scenario. Prefer an existing focused fixture plus the directly affected runtime scenario; use the full EditMode suite only when changes cross fixture boundaries or isolation is uncertain. Inspect a broad planner result and explicitly justify a narrower selection using the verification matrix instead of automatically running every selected gate.
Do not add tests for cosmetic/documentation changes or duplicate existing proof. Do add a focused regression for item loss, duplication, authority violations and other critical defects. Passing checks are repeated only after relevant changes or new failures. Summarize results and keep raw logs out of conversation. No extra tooling/refactors unless they unblock the current feature.

## Verification matrix

| Change | Required focused proof |
|---|---|
| Pure rules | Relevant EditMode fixture; affected fast gate |
| Movement/spawn/map | MovementProofGeometry tests; NetMovement, NetFinal; NetDoor/NetInventory if placement changed |
| Inventory/equipment | Inventory/Hands EditMode tests; NetInventory; NetExtraction if corpse ownership changed |
| Combat/skills | Relevant rules tests; NetCombat/NetSkill and affected combat gates |
| Arms/animation | Visual EditMode tests; Hands, Visual; NetCombat if network enemy presentation changed; actual motion review |
| Process/docs only | Review paths/commands for accuracy; no gameplay test rerun solely for prose |

Compile + focused proof per slice. One full `Run-All.ps1 -Set all -EditMode` after integration or before accepting a baseline/build. Repeat passing checks only after relevant changes or new evidence. After a full run and a test-fixture-only correction, rerun the affected scenario (and failing seed); do not restart the entire suite solely to obtain one green aggregate. Record combined evidence honestly; reserve the next full run for substantive integration/release. Never reduce expectations to obtain green.
Readiness is separate: implemented -> automated proof -> visual proof -> accepted. Report seed/version/run path; old passes do not validate later changes. Generated maps need more than one layout for targeted acceptance.

## Harness commands (read Docs/HARNESS.md for details)

- Before a slice: `python Tools/harness.py checkpoint Logs/harness/before-<slice>.json` (content comparison, NEVER acceptance).
- After edits: `python Tools/harness.py plan --since Logs/harness/before-<slice>.json`; add `--json` for file-by-file reasons.
- Execute: `python Tools/harness.py run --since Logs/harness/before-<slice>.json`. Default base is HEAD and includes staged/unstaged/untracked/deleted/renamed files.
- `--files <paths>` is allowed for an explicitly reviewed slice in an inherited mixed tree; clearly limit the claim to those files. Do not hide relevant changes by resetting the checkpoint.
- Unknown/shared inputs conservatively select all; `run` refuses a full selection without `--allow-full`. Never add this flag automatically just to bypass the cost guard. Explain why broad coverage is needed, or select a justified bounded slice.
- Test-only changes run EditMode without gameplay gates. Docs-only changes launch no Unity. Routing lives in Tools/harness.py and has standard-library tests in Tools/tests.
- Player build cache is enabled by Run-All; it verifies content hashes, profiles/scenes/settings/dependencies and complete output manifests. PASS results are never cached. `--no-build-cache` / `-NoBuildCache` forces a fresh build.
- Network gates use stable Builds/HarnessPlayers/net-proof and net-final paths on the mirror, preventing timestamped Windows Firewall identities. Never replace an active player. Logs remain per-run; do not pass an unchecked `-ember-proof-reuse-exe`.
- Four script-owned firewall rules were installed for IPv4 localhost only. Tools/Configure-TestFirewall.ps1 previews/installs/removes just these rules; administrator consent is needed for changes. Do not disable the firewall/notifications or delete historical rules wholesale.
- Full suites remain for substantive integration/release. A harness change is verified with harness tests and the relevant short real-process scenarios, not repeated all-runs.

## Unity pipeline

Never close the user's Editor or invoke EditorApplication.Exit in it. Batch/playmode/scene-opening/network gates run only on ../DakaDaka_ci, using the primary development checkout as the runner source.
CLI in the main project (installed Pipeline command names; query if names differ):
- `unity status --format json`
- `unity command editor set_auto_tick --enable true`
- `unity command script recompile`, then `script recompile_status`
- `unity command test run --mode editor --filter <fixture> --async_tests true`, then `test status`
Read only test summary/errors; do not print entire successful test trees.
If async test state is stuck, diagnose/cancel that test before starting another. Never start overlapping mirror runs.
Direct local file edits and CLI are allowed; MCP is optional. No shell-within-shell deletion/moving.

Mirror: `powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Run-All.ps1 -Set <gate-or-set> [-EditMode] [-Build] [-NoBuildCache]`.
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
