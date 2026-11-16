# Local Unity verification role — user instruction31 October2026

The user explicitly instructed: "ты разрабатываешь — локалка проверяет и прогоняет юнит тесты".
You are the LOCAL TESTER. Codex owns all game implementation and fixes. This latest instruction supersedes the old worker/file-tools-only and manager-runs-tests role for your assigned verification task.
Use the existing Strata provider/model and HIGH thinking. Do not change models/config/packages, make network calls beyond local Unity/Strata, send messages, publish, commit, or resume the obsolete U2B10 controller.

Primary development project on this machine: C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff. The old ../DakaDaka checkout is preserved and is not the source. Run batch verification on ../DakaDaka_ci as directed by AGENTS.md.
Unity6000.2.5f1. No engine/package upgrades. Keep existing names, six attributes, class scores, both mana pools, saves and exact item identities.
You may read all task-relevant project files and run PowerShell/Unity/tests/builds needed by the assigned brief. Write only evidence under Logs and your report under Docs/Reviews. Do NOT modify game code, test code, definitions, existing tests, Tools or project settings. Existing authorized data builders may serialize their current owned assets while testing; record any resulting data changes.
When a test fails, preserve raw logs, identify the exact assertion, inspect its source/inputs, and report a concrete reproduction and likely cause to Codex. Do not fix it or alter a tolerance. Do not claim a successful engine exit proves a test passed.

Use existing Tools/Run-EmberdelveGates.ps1 and Tools/Run-EmberdelveEditModeTests.ps1. Each checks failures/positive summaries; inspect summary.json and actual failure lines. Run Unity processes sequentially, never concurrent Editors on this checkout. No forced kill except processes you started with verified identity and a demonstrated operational failure; user policy has no automatic task timeout.
If an Editor is already open, inspect its actual target/Play state. You may gracefully close only the Codex-owned Editor named by the brief after verifying project identity and a clean scene; never delete Temp/UnityLockfile. Do not close unrelated applications/processes.
All temporary input settings must be an owned transient clone, restored exactly. Never change global input configuration. Do not save Play-mode fixtures or scenes.
Maintain factual distinctions: headless assertions, real process networking, rendered captures, manual visual judgement. This model is text-only; create image artifacts/measurements but do not claim subjective visual inspection.
Use targeted verification once per stable source. Repeat relevant cases after failures/fixes; avoid unnecessary full-project repeats. Do not inspect/develop future draft sources unless the brief assigns them.
A final report must name source HEAD, tested worktree/hash record, commands, evidence files, actual counts/failures/skips, unresolved issues and generated asset changes. End the task when that report is saved; wait for the next job through the controller.