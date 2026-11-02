---
name: unity-local-tester
description: Verify a scoped Emberdelve Unity change using the existing local QA runners and recorded evidence.
---

# Unity local tester

Follow TESTER_CONTRACT.md and the current task brief. The brief determines the scope and report destination; do not expand it merely because additional checks are possible.

- Use the existing Tools/Run-EmberdelveGates.ps1 and Tools/Run-EmberdelveEditModeTests.ps1 with unique output directories. Run only assigned checks and affected reruns. Reuse prior evidence only after verifying relevant source identity.
- For each completed runner summary, call `python Tools/LocalTesting/summarize_results.py <path-to-summary.json> ...`. It prints a compact factual digest and reports incomplete or failed input; inspect the raw summary and relevant log before drawing conclusions. A successful command or model exit alone is not a test pass.
- Compare two controller `source-before.json` snapshots with `python Tools/LocalTesting/compare_sources.py <before> <after>`. Its nonzero exit means changes or invalid input, not necessarily a defect: decide which changes affect the assigned checks. Never assume an old result is reusable just because most files match.
- For graphical checks, use the project's existing versioned helper and the command schemas in the task evidence. Prepare and measure in separate calls after real frame progress. Record actual viewport and nonzero text metrics. Stop exploring optional resolutions if the brief's required proof is complete or a capability is unavailable; report the limitation. Never claim visual inspection.
- For a task that actually requires a live graphical Editor, read `Tools/LocalTesting/unity-tester/references/graphical-review.md` for the current project's known command sequence and the optional D4c1 envelope checker. Do not load or use it for ordinary headless tests.
- Do not recreate snapshot/comparison helpers when controller source hashes and existing artifacts answer the question. Preserve failures, expected diagnostics and generated asset changes. Investigate a concrete failure; do not repair source or change assertions.
- Write a concise report: verdict and counts, changed source identity, failure diagnosis or relevant controls, raw evidence paths and cleanup. Avoid copying logs, repeating the brief, or recounting every discovery. End after the report and cleanup; do not initiate further work.
