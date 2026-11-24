---
name: dakadaka-review-result
description: Orchestrator checklist for analyzing a DakaDaka subagent's result (Sonnet worker, Haiku scout/git, or an escalated Opus subagent) before accepting it - diff review, scope, invariants, evidence re-check, verdict. Use after every delegated task completes, and on request to re-check finished work.
---

# Analyze a subagent result

The subagent's report is a claim. Check the work itself. Keep it proportional: a scout's file list needs a spot check, a worker's gameplay diff needs a full read.

## 1. Scope

```
git -C "C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff" status --short
git -C "C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff" diff --stat -- <owned files>
```

- Only owned files changed? Compare against the brief and the pre-task status (inherited `.mat`/settings drift is not the agent's). Unowned edits = rule violation -> fail.
- Nothing deleted, reset or reverted that was not part of the task.

## 2. Read the diff

Read the full diff of owned files (not just the summary). Check:

- **Does it do what the brief asked** - acceptance behavior, edge cases listed in "Design decided".
- **Correctness**: null/destroyed objects, off-by-one, units (m, s, deg), frame-rate dependence, sign of directions, early returns that skip cleanup.
- **Invariants**: server authority (client sends intent only), NGO ownership/RPC direction, no runtime mutation of shared ScriptableObjects, item UID/affixes on instances, no item loss/duplication path, shields never parry.
- **Project rules**: smallest change, no unrequested refactor/rename/tooling, existing builders used, no grep-the-source assertions in gates, assertions not weakened.
- **Dark and Darker reference** (if the brief required it): source paths actually exist and say what is claimed; facts vs inference separated.
- **Data assets**: library-authored data changed in the library and regenerated together (see CURRENT_WORK baseline notes).

## 3. Re-check the evidence

- Every "passed/compiled" claim must have a result line you can reproduce or see. Re-run the cheap ones yourself (compile via `unity` MCP + Console errors, the focused fixture). "Not run" is acceptable when stated; an unverifiable "all green" is not.
- For scout/git results: open 1-2 of the reported locations or `git log -1 --stat` to confirm they are real.

## 4. Verdict

| Verdict | When | Action |
|---|---|---|
| Accept | Diff correct, in scope, evidence holds | Continue the slice |
| Fix myself | 1-2 small defects, clear fix | Edit, note it in the task report |
| Re-brief | Brief was wrong/ambiguous | Fix the brief, relaunch the same model once |
| Escalate | Wrong approach, repeated error, hang, rule violation | Next model up per `dakadaka-orchestrate` step 3 |

For authority/network/persistence/item-loss changes, consider an additional read-only Opus review (general-purpose Agent, `model: "opus"`, brief: diff paths + invariants above, report findings only) before the mirror run.

## 5. Record

One line per analyzed result in the task report: agent + model, verdict, defects found (if any), escalation. This history decides future routing: a task type that Sonnet keeps failing goes to Opus/yourself by default.
