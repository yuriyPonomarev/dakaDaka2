---
name: dakadaka-orchestrate
description: Run DakaDaka work as an Opus orchestrator - plan the slice, route subtasks to Sonnet workers / Haiku helpers by difficulty, escalate the model when a subagent hangs or fails, analyze every result yourself, then verify and commit. Use for any DakaDaka gameplay, data, tooling or docs task larger than a few edits.
---

# DakaDaka orchestration

You are the orchestrator (Opus, the main session). You own the plan, the design decisions, the mirror/harness runs, the analysis of every result and the decision to commit. Subagents are hands, not judges: a worker's "done" or "tests pass" is a claim until you have checked it.

If the session model is not Opus, tell the user once and continue.

## 0. Ground rules (from AGENTS.md - read it if not read this session)

- Only checkout: `C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff`. Verify `git rev-parse --show-toplevel` before mutating. Every brief names this path.
- Read `Docs/CURRENT_WORK.md` and the active task report first; `Docs/ROADMAP.md` owns milestone order.
- Never close the user's Editor or run Play mode in it. Mirror runs (`Tools/Run-All.ps1`, `harness.py run`) are started only by you, one at a time.
- Systems/modules follow the local Dark and Darker reference (`C:/Users/yrapa/Downloads/DarkAndDarker-extracted`); record source paths and fact vs inference.

## 1. Plan the slice

State in one short block: defect/behavior, acceptance scenario, owned files, required checks (AGENTS.md verification matrix). Then:

```
python Tools/harness.py checkpoint Logs/harness/before-<slice>.json
```

Break the slice into subtasks and route each one:

| Tier | Agent (`subagent_type`) | Give it |
|---|---|---|
| Haiku | `dakadaka-scout` | Search/locate, list usages, read a run `summary.txt` and quote pass/fail, file existence, an exact edit you dictate verbatim. |
| Haiku | `dakadaka-git` | Commit/push of a path list you verified (see `dakadaka-commit`). |
| Sonnet | `dakadaka-worker` | A bounded implementation where you already decided the design: owned files, exact behavior, acceptance, checks allowed. |
| Opus (you) | - | Architecture, authority/network/persistence/item-loss code, cross-file design, Dark and Darker reference interpretation, debugging after a failed worker, anything whose brief would be as long as the change. |

Default: do it yourself if the task is small enough that writing the brief costs as much as doing it. Delegate in parallel only when subtasks own disjoint files and do not need each other's output. One owner per file.

## 2. Write the brief

Each brief is self-contained (the subagent starts cold):

```
Checkout: C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff (verify rev-parse first)
Goal: <behavior in 1-3 sentences>
Owned files (edit only these): <paths>
Read first: <paths / symbols with line hints>
Design decided: <the approach, constants, edge cases>
Acceptance: <observable result>
Checks: <compile via unity MCP | EditMode fixture X (editor: allowed) | none>
Do not: <specific traps for this task>
Report: the format from your agent definition
```

Launch with the Agent tool; run independent agents in one message with `run_in_background`. While they run, do your own share (non-overlapping files) instead of polling.

## 3. Escalate when a subagent hangs or fails

A subagent **hangs** if it has not completed long after the expected time (Haiku task: ~5 min; Sonnet slice: ~20 min; or no file changes appear when you look), or it loops on the same action. It **fails** if its result is wrong in analysis (step 4), it stopped with "stuck", or it broke rules (touched unowned files, ran forbidden commands).

Ladder - move one rung up per failure, never retry the same model on the same brief more than once:

1. Same model once more only if the failure was a brief defect you can fix (missing file, ambiguous spec): fix the brief, relaunch.
2. Haiku -> Sonnet: relaunch with the Agent tool's `model: "sonnet"` (same `subagent_type`, so its rules still apply) or move the task to `dakadaka-worker`.
3. Sonnet -> Opus: relaunch with `model: "opus"`, including in the brief what the previous attempt did wrong and the current state of the files.
4. Opus subagent fails or the task proved to need context only you have: do it yourself in the main session.

For a hung agent: stop it (TaskStop), inspect `git status` / `git diff -- <owned files>` for partial edits, decide keep/redo (revert only the agent's own edits to its owned files, by editing them back - never `git checkout`/`reset`), then escalate. Note each escalation (task, from -> to, reason) in the task report; if a type of task repeatedly needs escalation, route it one tier higher from the start next time.

## 4. Analyze every result yourself (mandatory)

After **every** subagent finishes, before building on its work, run the `dakadaka-review-result` checklist. Do not forward a subagent's claims to the user or to the next subagent without that check. The outcome is one of: accept / fix it yourself (small) / re-brief or escalate (step 3).

## 5. Verify the slice

```
python Tools/harness.py plan --since Logs/harness/before-<slice>.json
python Tools/harness.py run  --since Logs/harness/before-<slice>.json
```

Justify the selection against the matrix; never add `--allow-full` just to pass the guard. Read `Logs/runs/<stamp>/summary.txt` (delegate quoting it to `dakadaka-scout` if long). Missing summary / timeout is not success; exit codes alone prove nothing. Visual changes follow the graphical-review procedure in AGENTS.md.

## 6. Record and commit

- Update the task report (`Tasks/<SLICE>_<date>.md`) with evidence (run ids, counts, seeds), escalations, limitations; rewrite `Docs/CURRENT_WORK.md` (one page, three next actions).
- Commit only a completed, verified slice: run the `dakadaka-commit` skill. Push only when the user asked for it in this conversation.
- Final message to the user: what changed, actual evidence, what is not verified (e.g. in-game review), and what was committed/pushed.
