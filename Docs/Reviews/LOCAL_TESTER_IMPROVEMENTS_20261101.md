# Local tester improvements — verification 2026-11-01

Role: LOCAL TESTER (verification only). No Unity/editor was started, no game/test/config/source was modified, no new controller or external service was started.

## Verdict

**PASS — scoped verification complete, 0 failures, 0 skips.**

| Check | Result |
|---|---|
| `python -m unittest discover -s Tools/LocalTesting -p 'test_*.py' -v` | **11 tests run, 11 ok, 0 failures, 0 errors, 0 skips, exit 0** |
| Digest of `D4c2-20261101/editmode-191934/summary.json` | `pass`, tests=402 failed=0 skipped=0 inconclusive=0, **exit 0** (expected 402 ✔) |
| Digest of `D4c2-R1-20261101/gates-cooldown-194727/summary.json` | `pass`, checks=245, **exit 0** (expected 245 ✔) |
| Digest of `D4c2-20261101/gates-a-192108/summary.json` | overall `attention`, **exit 1**; failing row kept (2 mismatches + `FAIL checks=245 failures=2`), 2 successful rows retained (161, 68) ✔ |
| Nonexistent summary path | `status: invalid` + error text, **exit 1**, stderr empty ✔ |
| Skill structure validation (`quick_validate.py`) | `Skill is valid!`, **exit 0** |
| prompt.md composition (contract → profile → brief, once each) | exact match ✔ |
| source-before.json contains contract + profile | both present ✔ |
| Provider/model/thinking + locks + no-skills/no-mcp unchanged | confirmed ✔ |
| Source drift guard recompute | 265/265 entries re-hashed, **0 drift, 0 missing** ✔ |

## Source identity

- Git HEAD: `4a338d6911f130fc0ba3fbcd306d6fb8f999424d` (branch `main`). Worktree is dirty with Codex WIP: 17 modified tracked paths, 26 untracked paths (game sources/gates/tests, `Docs/Tasks/D4c1.md`, `D4c2.md`, `ProjectSettings/Packages/com.unity.pipeline/`, and the new tester files).
- Tested worktree record: `Logs/local-testing/runs/Tester-improvements-20261101/source-before.json` — **265 entries** (254 `.cs`, 6 `.py`, 3 `.ps1`, 2 `.md`).
- Recompute of all 265 entries after my runs: `drift = []`, `missing = []` → no evidence or source file was altered by this job.
- Tester-file hashes (identical to source-before values):
  - `local_tester.py 5c2c2716f30b2ca7c5b6009b4da0b6093a3791384ab530eff083fd4f935e5d75`
  - `prompt_builder.py 183f9d460bb284383cc96e88b62ffbcc501d6d9971748ec95f5440029ca433ab` (new)
  - `summarize_results.py d7d37a4cca7967cf4139088d8cae0b3d5c2b2293ebd8da7826a3406bfc0348e8` (new)
  - `test_summarize_results.py f4671d5ab95f927b761a1668e0f9a0b918f3af7b50569dea0473ece755c9b95f` (new)
  - `status_writer.py fd7d145244720504702cfb22256e100c66b368886d02fd5c90ad31980d603440` (unchanged, `git diff` empty)
  - `test_status_writer.py be3b910017c645c1c6847c90abb81d6e8cac81044691f88b348aa4c014b2fefc` (unchanged, `git diff` empty)
  - `TESTER_CONTRACT.md 534b5dd3e9c79a419df78663d32ac5ba7c9114afafcffc78a4eeb6a91fd8ded1`
  - `unity-tester/SKILL.md 754d29fbf2dd691fea84ec862ae784f8ebfaa03644fa564e80c3757270d4abeb`
  - runners unchanged: `Run-EmberdelveGates.ps1 de12e68a…`, `Run-EmberdelveEditModeTests.ps1 72ea6138…`
- Edit timing evidence: the previous run (`D4c2-local-R1-20261101`, source-before 260 entries) did **not** list `prompt_builder.py`, `summarize_results.py`, `test_summarize_results.py`, `TESTER_CONTRACT.md` or `unity-tester/SKILL.md`, and recorded `local_tester.py = c89074c642409f0918dcc8a6f85647dc738bf5983c44f51a6a54c45958b6583b`. 260 + 5 new = 265, and the local_tester hash changed → the improvements landed after the last local QA run, i.e. this job is the first run driven by the new prompt builder.

## Prompt composition (headless assertion)

`Logs/local-testing/runs/Tester-improvements-20261101/prompt.md` (6906 B on disk; 6813 chars after CRLF→LF normalisation):

- Section order by index: contract @0, `---\nname: unity-local-tester` profile @2975, `# Verify local tester improvements only` brief @4815.
- Each of the three sources appears **exactly once** (literal-substring counts = 1/1/1). The two textual mentions of "TESTER_CONTRACT" are inside the profile and brief bodies, not duplicated sections.
- Byte-for-byte equality after newline normalisation: `contract.TrimEnd("\n") + "\n\n" + skill + "\n\n" + brief` == prompt.md ✔ (this is exactly `build_prompt` output).
- `source-before.json` includes both `Tools/LocalTesting/TESTER_CONTRACT.md` and `Tools/LocalTesting/unity-tester/SKILL.md` ✔.

## Commands and raw evidence

Evidence directory: `Logs/local-testing/tester-improvements-20261101/`

| Command | Exit | Raw evidence |
|---|---|---|
| `python -m unittest discover -s Tools/LocalTesting -p 'test_*.py' -v` | 0 | `unittest-discover.log` (1164 B, merged stdout+stderr via `2>&1`), `unittest-discover.exit.txt` |
| `python Tools/LocalTesting/summarize_results.py Logs/local-testing/D4c2-20261101/editmode-191934/summary.json` | 0 | `digest-editmode-191934.stdout.txt`, `…stderr.txt` (0 B) |
| `python Tools/LocalTesting/summarize_results.py Logs/local-testing/D4c2-R1-20261101/gates-cooldown-194727/summary.json` | 0 | `digest-gates-cooldown-194727.stdout.txt`, `…stderr.txt` (0 B) |
| `python Tools/LocalTesting/summarize_results.py Logs/local-testing/D4c2-20261101/gates-a-192108/summary.json` | 1 | `digest-gates-a-192108.stdout.txt`, `…stderr.txt` (0 B) |
| `python Tools/LocalTesting/summarize_results.py Logs/local-testing/tester-improvements-20261101/does-not-exist-summary.json` | 1 | `digest-missing.stdout.txt`, `…stderr.txt` (0 B) |
| `python C:/Users/yrapa/.codex/skills/.system/skill-creator/scripts/quick_validate.py Tools/LocalTesting/unity-tester` | 0 | `skill-quick-validate.stdout.txt` ("Skill is valid!"), `…stderr.txt` (0 B) |
| SHA-256 recompute of every `source-before.json` entry | — | `source-drift-check.json` (`entries: 265, missing: [], drift: []`) |
| Copy of the controller request JSON | — | `request-before.json` |

Digest output was cross-checked against the raw summaries (`passed`, per-row `passed`/`exitCode`/`timedOut`/`summaries`/`problems`/`log`) — the digest faithfully reflects the raw rows; the raw summaries and their logs remain authoritative. Raw summary SHA-256 (unchanged before/after digest runs, mtimes untouched 19:20:37 / 19:47:53 / 19:22:44):
`editmode-191934 5314591594b6024524234050e344712f67cb57760345251f081d4dbb09e5c4f0`, `gates-cooldown-194727 51de85446cf825e2c3e75eda08712c87dec84715f6305d4bd01daf049c683fc3`, `gates-a-192108 a173ccc92387afacb3537843d27e3a8d4dfca1f944fea84d4771ee282895e655`.

## Review findings (edits under test)

**`local_tester.py`** (7 insertions / 2 deletions vs HEAD): adds `from prompt_builder import build_prompt`, `PROFILE = …/unity-tester/SKILL.md`, `paths += [CONTRACT, PROFILE]` in `source_hashes()`, replaces the inline `CONTRACT + brief` concatenation with `build_prompt(CONTRACT, PROFILE, brief)`, and adds the missing final newline. Unchanged and re-verified: `--provider strata --model strata --thinking high`, `--print --mode json --approve --offline --no-context-files --no-skills --no-mcp --no-extensions`, `--tools read,powershell,write,grep,find,ls`, `--session-dir`, the shared `~/.pi/agent/projects/dungeon-extraction/controller.lock` `msvcrt` non-blocking lock and "another agent owns the lock" exit, `save()` provider/model/thinking stamping (live `status.json` shows `provider=strata model=strata thinking=high`), the no-timeout `process.wait()`, and the `needs_attention` break. No regression found. Adding contract+profile to the hash set is a strict improvement: prompt text is now drift-guarded, and no duplicate keys are created because the `Tools` rglob patterns only match `*.ps1`/`*.py`.

**`prompt_builder.py`** (new): reads all three parts with `utf-8-sig`, rejects empty/whitespace-only parts with `ValueError`, joins with `"\n\n"` in fixed contract→profile→brief order. Covered by `test_profile_is_injected_between_contract_and_brief`. Minor notes (not defects): the contract is now read as `utf-8-sig` instead of `utf-8` — behaviourally identical here because `TESTER_CONTRACT.md` has no BOM (prompt.md starts `0x23`); a missing/empty profile raises `ValueError` **before** `source_hashes()` and is not caught in `local_tester.py`, so a deleted `SKILL.md` would terminate the worker with an unhandled exception rather than a recorded `needs_attention` result — same failure class as the previous code's unguarded `CONTRACT.read_text()`.

**`summarize_results.py`** (new): read-only digest, never executes tests. Gate rows require `passed is True`, `exitCode == 0`, `timedOut is False`, empty `problems`, a `PASS … checks=N failures=0` line with N > 0 and a non-empty log string; EditMode requires `status == "Passed"`, `tests > 0`, `failed/skipped/inconclusive == 0`, `problems == []`, string `log` and `results`. Overall `pass` requires top-level `passed` **and** every row `pass`; anything else is `attention`, malformed/empty/unrecognised input is `invalid`, and `main()` returns 1 unless every report is `pass` — verified empirically above and by unit tests. Limitations to record: only the **first** matching PASS line per gate is reported (`matches[0]`), so a gate emitting several summary lines would be summarised by its first line only; `checks` is `null` for failing rows even when the raw `problems` line contains `FAIL checks=245 failures=2` (the raw summary/log stays authoritative for that number); `expectedDiagnostics` is not surfaced in the digest.

**Tests**: 11 total = 4 pre-existing `test_status_writer` cases (unchanged file) + 7 new `test_summarize_results` cases covering pass/attention/invalid classification, zero-exit-but-failed never becoming pass, mixed-batch retention of individual rows, skip/inconclusive requiring attention, CLI nonzero, and profile injection order. New tests write only inside `tempfile.TemporaryDirectory()` with `addCleanup` — no existing evidence was touched (confirmed by the 0-drift recompute).

**`unity-tester/SKILL.md`**: valid hyphen-case name (`unity-local-tester`, 19 chars) and description (103 chars, no angle brackets/TODO), frontmatter keys limited to `name`/`description`, no unbalanced code fences; validated by the system `quick_validate.py` (exit 0). Its content only narrows scope and points at the existing runners/digest; it does not grant new write targets beyond those already in the contract.

## Limitations

- All results here are **headless assertions** on files and process exit codes. No Unity editor, Play mode, rendered capture or visual judgement was performed, and none is claimed (this model is text-only).
- `Tee-Object … 2>&1` merges unittest stdout and stderr into one log; the runner's own per-case stdout/stderr split was therefore not separately preserved (stderr was empty apart from the test progress lines — no traceback appears in the log and the summary block reads `OK`).
- `quick_validate.py` is a minimal structural validator (frontmatter keys, name/description format, TODO placeholders, fence balance); it does not validate instruction semantics or tool-policy correctness. It requires PyYAML, which was already importable in the local Python 3.10.11 — nothing was installed.
- The digest is advisory by design; the raw `summary.json` files and their referenced `editor.log` / `*.Run.log` / `results.xml` files remain the authoritative evidence and were not re-executed (no Unity run was authorised by this brief).
- Codex's uncommitted game-source WIP (17 modified / 26 untracked paths) was not verified in this job; it is only recorded as the tested worktree state and hash-guarded.

## Cleanup

- Wrote only under `Logs/local-testing/tester-improvements-20261101/` plus this report. A copy of the controller request JSON was first written into the controller run directory and has been **moved out**; `Logs/local-testing/runs/Tester-improvements-20261101/` again contains only the controller's own `prompt.md`, `source-before.json`, `events.jsonl`, `stderr.log`.
- No Unity process was started or closed; no `Temp/UnityLockfile` touched; no input settings, scenes, fixtures, packages, config or source/test files modified; no commit, publish or message sent; no second controller started.

Awaiting the next job through the controller.
