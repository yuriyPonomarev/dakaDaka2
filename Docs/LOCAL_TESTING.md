# Local testing — current development contract

User instruction31 October2026: **Codex develops; the local model reviews and runs all tests.** This supersedes historical orchestration instructions assigning implementation to local Pi and test execution to Codex.

## Running configuration

Existing Strata runtime: C:/Users/yrapa/Downloads/Strata-main/Strata-main, existing strata-iq3_xxs.json, loopback127.0.0.1:8080. Provider/model strata, HIGH thinking,262144 context. Do not change that configuration or restart a healthy model to adopt settings.

Tools/LocalTesting/local_tester.py runs a persistent local QA queue using the installed Pi CLI. It shares the original controller's Windows OS lock, preventing a second local controller. It does not resume/overwrite the obsolete paused U2B10 manager state. No automatic wall-clock timeout; failed jobs preserve logs and require Codex attention.

## Queue and evidence

Controller state: Logs/local-testing/status.json.
Requests: Logs/local-testing/requests/<job>.json, containing an absolute or project-relative brief path.
Results: Logs/local-testing/results/<job>.json.
Exact prompts, events, stderr, source fingerprints and final messages: Logs/local-testing/runs/<job>/.
Local QA reports go under Docs/Reviews/LOCAL_<task>-<date>.md.

The worker has read, PowerShell and report-writing tools. The exact task contract is Tools/LocalTesting/TESTER_CONTRACT.md. It may run existing Unity scripts/builders and capture evidence, but cannot edit game/test code, weaken checks, change settings/packages, commit or publish. The controller records C#/PowerShell hashes before/after; source drift requires attention.

Codex supplies a stable source checkpoint and exact task brief, then develops the next slice in a separate draft outside Assets. No live code changes while QA runs Unity on this checkout. After local QA reports a defect, Codex fixes it and queues targeted re-verification. Do not accept a test from exit0 alone: local report and raw logs must show the actual positive summary, counts, failures and skips.

This model is text-only. It can automate rendered captures and pose/frame metrics, but cannot claim subjective visual inspection. Keep that limitation distinct from headless and unit results.

## First assignment

D4b1-local-20261031: source review plus fresh locally owned EditMode, melee action-speed and both actual-class combat scenarios, with optional render/pose automation. Earlier Codex-run evidence remains historical and must not be mislabeled as local execution.

D4b2 reload and D4b3 local draw proposals remain isolated until the preceding stable source is accepted. The next game-source changes and any test repairs belong to Codex.