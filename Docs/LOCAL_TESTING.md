# Local testing

Since 3 Nov 2026 (user decision) full verification is `Tools/Run-All.ps1` — a plain script, no LLM. Day-to-day checks are the fast loop in the open Editor (AGENTS.md).

## Optional: local Pi/Strata analyst
Use only when a Run-All failure is unclear. Give it the failing `Logs/runs/<stamp>/summary.txt` + logs and ask for a root-cause note; it does not re-run the whole suite.
Queue (unchanged): brief `Logs/local-testing/<job>.md`, request `Logs/local-testing/requests/<job>.json` {requested_by, brief, task}, result `results/<job>.json`, report `Docs/Reviews/LOCAL_<job>.md`. Contract: `Tools/LocalTesting/TESTER_CONTRACT.md`. Runtime: existing Strata (`C:/Users/yrapa/Downloads/Strata-main/Strata-main`, 127.0.0.1:8080, HIGH) — don't change its config.
The analyst and Run-All both use the mirror `..\DakaDaka_ci`: never at the same time.
