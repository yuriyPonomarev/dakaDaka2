# LOCAL TESTER — modules R1 (20261102): finish of interrupted Tester-modules-20261101

Scope: offline verification of the local-tester modules/reference only. No Unity process was started, no game/test/source/config/Tools file was modified, no package/model/config change.

## Verdict

**PASS for the assigned offline checks: 11 recorded commands — 3 expected-positive (exit 0), 3 expected-negative-but-valid-failure (exit 1), 5 invalid-input controls (exit 2). 0 unexpected results, 0 skips.**

The interrupted job's completed artifacts are reusable: source identity is byte-identical, so the recorded **15/15 unittest pass (exit 0)** is reused rather than re-run.

| Check | Actual result | Exit |
|---|---|---|
| `compare_sources.py` previous-run snapshot → current-run snapshot | `identical`, 269/269 keys, added/removed/changed all empty | 0 |
| On-disk SHA-256 of all 12 `Tools/LocalTesting/*` keys vs current `source-before.json` | 12/12 MATCH | — |
| `summarize_render.py` R3 `render-prepare-attempt-4` + `render-measure-1280x720` | `pass`, issues `[]`, viewport `[1280,720]`, rows **41**, prepareFrame 1 → measureFrame 24545 (gameTime 0 → 144.627579) | 0 |
| `summarize_render.py` R2 `render-prepare-2` + `render-measure-1` | `attention`, failures retained: "Prepare did not succeed", "probe reports failure or wrong row counts", "missing or invalid measurement rows"; rows 54, viewport 2560x1440 | 1 |
| `summarize_render.py` missing input files | `invalid` (`No such file or directory`) | 2 |
| `quick_validate.py` on `Tools/LocalTesting/unity-tester` | `Skill is valid!` | 0 |
| Controls: R3 `render-prepare-attempt-1` (transport `success:false`) as prepare | `invalid` | 2 |
| Control: R3 measure envelope used as prepare (wrong `entry`) | `invalid` — "wrong or unsuccessful script-run envelope" | 2 |
| Control: previous `invalid-snapshot.json` | `invalid` — "snapshot must map source paths to SHA-256 hashes" | 2 |
| Control: missing snapshot file | `invalid` | 2 |
| Re-run of previous `compare R3 self` / `R2→R3` | reproduced exactly: identical/0 and changed/1 with `AgentScripts/ReviewD4c1.cs` only | 0 / 1 |

## Source identity

- HEAD `4a338d6911f130fc0ba3fbcd306d6fb8f999424d` ("Complete frozen DEX draw timing and resilient local verification", 2026-11-01 04:56:35 +0100) — unchanged from the interrupted run's `git-head.txt`.
- `Logs/local-testing/runs/Tester-modules-20261101/source-before.json` vs `.../Tester-modules-R1-20261102/source-before.json`: **identical (269 keys)**.
- Working-tree fingerprints match that snapshot for every tester module (`module-hash-verification.txt`), including `test_evidence_tools.py`, `test_status_writer.py`, `test_summarize_results.py`. Therefore the previous run's `unittest-discover.stdout.txt` (15 tests, `OK`, exit 0) is valid to reuse; its raw artifacts were left untouched and their SHA-256 recorded in `reused-artifact-hashes.txt`.
- `git status --porcelain` before and after this job: 48 lines, byte-identical → **no source drift, no generated asset changes** (no Unity/data builder was run).

## Review of the modules/reference added in the interrupted run

Added (untracked at HEAD): `compare_sources.py`, `summarize_render.py`, `test_evidence_tools.py`, `unity-tester/references/graphical-review.md`. Changed: `local_tester.py` (+8/−2), `unity-tester/SKILL.md`.

- `local_tester.py` now composes the prompt as **contract → profile → brief** via `prompt_builder.build_prompt` (verified in this run's `prompt.md`: contract line 1, profile line 20, brief line 37) and adds `TESTER_CONTRACT.md` + `unity-tester/**/*.md` to `source_hashes()`. Consequence: **`graphical-review.md` is present and fingerprinted in the controller `source-before.json`** (key `Tools/LocalTesting/unity-tester/references/graphical-review.md`, hash `b207d27617ceb7d05ed377c250205844c3fe0ac7f02480fc313670c9820b61ea`), so edits to the reference now trip the drift guard. Launch flags remain `--provider strata --model strata --thinking high --no-skills --no-mcp --no-context-files --no-extensions --offline`; `status.json` confirms provider/model/thinking unchanged.
- `summarize_render.py` behaviour is correct against real evidence: it rejects non-`prepared` prepare results, non-empty probe `failures`, wrong row counts (41 required: 14 StatLabel + 14 StatValue + 13 `EquipLabel_`), non-advancing frame/gameTime, zero/negative or non-finite dimensions, failed `realMetrics/fits/onScreen/inPanel/inPane` flags, duplicate/missing row names, empty/`Null` graphics, `opening != expected`, and cross-call editor identity mismatch. It never converts a bad/partial input into a pass (exit 0 only when `status == "pass"`).
- `graphical-review.md` command schemas match the recorded R3 evidence: `unity command script run --project-path <project> --format json -- --file AgentScripts/ReviewD4c1.cs --entry ReviewD4c1.Prepare|Measure` (R3's `run-script.ps1` additionally passes `--timeout`), `editor set_auto_tick` and `editor.playmode set_run_in_background` (both recorded as session-only overrides in `auto-tick-on.txt` / `runbg-on.txt`), separate Prepare/Measure calls with frame advance, screenshot dimensions ≠ `Screen.width/height`, restore-then-stop-then-graceful-close, never delete the lock, numeric vs subjective distinction. No dependency installation is instructed.
- Test coverage: the 15 tests = 4 `test_evidence_tools` + 4 `test_status_writer` + 7 `test_summarize_results` (incl. `build_prompt` order/empty-input). `test_evidence_tools` covers added/removed/changed detection, bad-hash snapshot → exit 2, frame/gameTime non-advance, zero viewport, non-empty failures, duplicate names, failed containment flag, zero preferred dimension, editor-port mismatch, transport `success:false` → exit 2.

## Findings for Codex (no fixes made)

1. **Diagnostic quality, not a correctness defect:** when a prepare/measure envelope has `data.result = null` (transport failure, e.g. R3 `render-prepare-attempt-1`), `summarize_render.py` reports `{"status":"invalid","error":"'NoneType' object is not subscriptable"}` with no file name, while the wrong-entry case correctly names the file. Reproduction: `python Tools/LocalTesting/summarize_render.py Logs/local-testing/D4c1-R3-20261101/render-prepare-attempt-1.txt Logs/local-testing/D4c1-R3-20261101/render-measure-1280x720.txt` → exit 2. Exit code and non-pass behaviour are correct.
2. **Coverage gaps** (behaviour confirmed only by this run's real evidence, not by unit tests): row-count ≠ 41 (R2 gave 54), missing `prepared` key, missing input file, wrong `entry`/`file`/`command`, `graphics` empty or `"Null"`, `opening != expected`, prepare/measure `projectPath` mismatch (only `port` is unit-tested), non-finite numbers. Suggested only, not applied: extend `test_evidence_tools.py` with those cases and include the input path in the null-`result` error message.
3. R2's retained failure is genuine probe data, not a digest artifact: raw `render-prepare-2.txt` / `render-measure-1.txt` carry `pass:false`, `failures:["Expected13 real equipment labels"]`, `equipmentLabels` 52 / 26 and 54 measurement rows at 2560x1440.

## Evidence and cleanup

Fresh raw evidence (stdout + `EXIT=` files per command): `Logs/local-testing/tester-modules-R1-20261102/` — `command-manifest.txt`, `compare-PrevModules-to-Current.*`, `module-hash-verification.txt`, `reused-artifact-hashes.txt`, `render-R3-prepare4-measure1280.*`, `render-R2-prepare2-measure1.*`, `render-missing-input.*`, `render-R3-prepare1-failedtransport-measure1280.*`, `render-R3-measure1280-as-prepare.*`, `compare-R3-self.*`, `compare-R2-to-R3.*`, `compare-invalid-snapshot.*`, `compare-missing-input.*`, `skill-quick-validate.*`, `git-head.txt`, `git-head-detail.txt`, `git-status-porcelain.txt`, `git-status-porcelain-final.txt`.

Preserved untouched: all `Logs/local-testing/tester-modules-20261101/` artifacts (hashes recorded), and the R2/R3 evidence inputs. Validator used: existing `C:/Users/yrapa/.codex/skills/.system/skill-creator/scripts/quick_validate.py` (nothing installed). No temporary files to remove; no Unity Editor was opened or closed. Pre-existing processes left running and untouched: `Tools/LocalTesting/local_tester.py` controller (PIDs 17420 → 25284) and the Strata server (20304 → 26252), started 03:05:58 before this session.

Limitations: all conclusions are numeric/offline digest and file-identity facts. No rendered capture was produced here and no subjective visual inspection is claimed; the R3 pass reuses the previously recorded capture/measure evidence.
