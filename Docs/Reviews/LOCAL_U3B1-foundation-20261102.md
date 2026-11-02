# LOCAL U3B1-foundation — 20261102

Local tester only. No game/test/`Tools/`/`ProjectSettings/`/asset edit, no commit, no controller resume, no
engine/package/model change, no builder executed, no network call beyond the one local batch Unity process I
started. Codex owns every fix. Scope: independent review of `SkillIntentGate` + `SkillIntentGateTests`, and one
full Emberdelve EditMode run.

## 0 Verdict

| Item | Result |
|---|---|
| Source identity | HEAD `4a338d6911f130fc0ba3fbcd306d6fb8f999424d` **unchanged**; tested tree **byte-identical to the controller's `source-before.json`** (274/274 guarded sources, `status: identical`) |
| Diff vs the tree that produced the 430-test baseline | **exactly 2 added guarded sources** — `Assets/Emberdelve/NetRules/SkillIntentGate.cs`, `Assets/Emberdelve/Tests/EditMode/NetRules/SkillIntentGateTests.cs`; 0 removed, 0 changed |
| Full EditMode suite (`-testFilter Emberdelve`) | **PASS — 434 tests, 0 failed, 0 skipped, 0 inconclusive, `result=Passed`, exit 0, `timedOut=false`, 100.74 s** |
| Count guard | 434 = **166** (`Emberdelve.NetRules.Tests.EditMode`) + **268** (`Emberdelve.Rules.Tests.EditMode`); 26 fixtures = 25 pre-existing with **identical per-fixture totals** + 1 new fixture `SkillIntentGateTests` (4). 0 fixtures removed, 0 changed |
| Four new tests, by name in the NUnit XML | `BothSlotsAcceptNewIntentsAndReplayCannotRepeatThem`, `InvalidInputsDoNotMoveTheSequenceOrConsumeBudget`, `FloodIsBoundedAndRecoveryDoesNotLoseSequenceAlignment`, `JumpBoundaryBackwardClockAndIndependentConnections` — all `result=Passed` |
| Raw log | 4531 lines / 319 627 B: **0** `error CS`, **0** `] FAIL`, **0** `mismatch`, **0** `Exception:`, **0** `Aborting batchmode`; 16 pre-existing `warning CS0618/CS0168`; `Test run completed. Exiting with code 0 (Ok).` |
| Gate is genuinely unused | `SkillIntentGate` / `SkillIntentVerdict` appear **only** in `SkillIntentGate.cs` and its tests — no RPC, no runtime call site (grep over all `Assets` + `AgentScripts`) |
| Independent review | **No compile or test failure.** 8 concrete review findings (F1–F8) + 4 test-coverage gaps; the rule as written is internally consistent and matches the existing `InputSanitizer` conventions, but it is **not** a complete flood control and has two permanent-degradation paths (clock, sequence ceiling) |
| Generated change from my run | **1 file**: `ProjectSettings/ProjectSettings.asset` — Unity dropped the `SENTIS_ANALYTICS_ENABLED` Standalone scripting define during the batch test run (§5). I did not revert it |
| Source guard after my run | **NO DRIFT** — 274/274 identical to my before snapshot and to the controller snapshot |
| Local stance | **Suite PASS, 434/434.** Review findings are for Codex; nothing repaired, no assertion or tolerance touched, no case bypassed |

---

## 1 Source identity

- HEAD `4a338d6911f130fc0ba3fbcd306d6fb8f999424d` — *"Complete frozen DEX draw timing and resilient local
  verification"* (2026-11-01T04:56:35+01:00). **Unchanged; no commit made.**
- Guarded set (`snapshot-sources.ps1`, mirrors `local_tester.py source_hashes()`: `Assets/Emberdelve/**/*.cs` +
  `Tools/**/*.ps1|*.py` + `AgentScripts/**/*.cs` + `TESTER_CONTRACT.md` + `unity-tester/**/*.md`):
  **274 before, 274 after**.
  - controller `Logs/local-testing/runs/U3B1-foundation-20261102/source-before.json` → my before snapshot:
    **274 shared, 0 added, 0 removed, 0 changed** (`hash-comparison-controller-to-local-before.txt`, exit 0)
    → the tree I tested is exactly the tree the controller snapshotted for this job.
  - controller **D4d1-R1** before → controller **U3B1** before: `added: [SkillIntentGate.cs, SkillIntentGateTests.cs]`,
    0 removed, 0 changed (`hash-comparison-D4d1R1-to-U3B1.txt`, exit 1). D4d1-R1's tree is the one whose EditMode
    result was **430/430 Passed** (`D4d1-20261102/editmode-040435`), so 430 + 4 new = 434 is the expected floor,
    and I enforced it with `-MinimumTests 434` rather than eyeballing it.
  - my before → my after, and controller before → my after: **0 changed**
    (`hash-comparison-local-before-to-final.txt`, `hash-comparison-controller-to-local-final.txt`, both exit 0).
    **I modified no source, test, tool or setting.**
- New-file metadata checked: `SkillIntentGate.cs` guid `b4f0700472b84b8987a442747332ad10`,
  `SkillIntentGateTests.cs` guid `7a9202da60ac4661baa36e499f012546`; both `MonoImporter`, no import side effects.
  The gate sits in `Assets/Emberdelve/NetRules` → `Emberdelve.NetRules.asmdef` (`noEngineReferences: true`,
  `references: []`); the tests sit in `Assets/Emberdelve/Tests/EditMode/NetRules` →
  `Emberdelve.NetRules.Tests.EditMode.asmdef` (Editor-only, `UNITY_INCLUDE_TESTS`, references `Emberdelve.NetRules`).
  Namespace `Emberdelve.Net.Rules` matches all 13 sibling NetRules sources; the test namespace
  `Emberdelve.NetRules.Tests` matches the 13 sibling test files.

---

## 2 The EditMode run (real headless Unity process)

```
& ./Tools/Run-EmberdelveEditModeTests.ps1 -ProjectPath '.' -TestFilter 'Emberdelve' `
    -OutputDirectory Logs/local-testing/U3B1-20261102/editmode-20261102-093744 `
    -MinimumTests 434 -TimeoutSeconds 900
```

`editmode-20261102-093744/summary.json`: `passed=true exitCode=0 timedOut=false tests=434 failed=0 skipped=0
inconclusive=0 status="Passed" problems=[] elapsedSeconds=100.74`. Runner transcript
`editmode-20261102-093744.runner.txt`: `START EditMode Emberdelve pid=6204` →
`PASS EditMode tests=434 failures=0 exit=0`.

`python Tools/LocalTesting/summarize_results.py …/summary.json` → `status: pass`, `tests 434 / failed 0 /
skipped 0 / inconclusive 0` (`summarize-editmode.txt`, exit 0). The digest was **not** treated as proof; the
originals were read directly:

- **NUnit XML** (`results-xml-analysis.txt`): `test-run total=434 failed=0 skipped=0 inconclusive=0 passed=434
result=Passed duration=0.2229155`; `//test-case` = 434 nodes, **none** with `result != Passed`;
`//failure`, `//reason`, `//error`, `//stack-trace` = **0** nodes each. Assemblies: NetRules 166/166, Rules 268/268.
  Per-fixture diff against the 430-test baseline XML: **added 1 (`SkillIntentGateTests` total=4), removed 0,
  changed 0** — no pre-existing test lost, renamed or skipped.
- The four new test cases, with full names and `result=Passed`, are listed in `results-xml-analysis.txt`.
- **Raw log** (`editmode-log-scan.txt`): 0 `error CS`, 0 `] FAIL`, 0 `mismatch`, 0 `Exception:`,
  0 `Aborting batchmode due to failure`. Both test assemblies were compiled in this run
  (`Csc …Emberdelve.NetRules.Tests.EditMode.dll`, `…ILPostProcess…`, `CopyFiles Library/ScriptAssemblies/…`)
  → the new sources compile cleanly. Only Unity line 4446 `Test run completed. Exiting with code 0 (Ok).`
  and line 4423 `Saving results to: …/editmode-20261102-093744/results.xml`.
  Pre-existing noise, unchanged by this job: `warning CS0618` ×2 (`NetPlayerRig.cs` obsolete `RequireOwnership`),
  `warning CS0168` (`EmberdelveLootRollerGate.cs(29,19)`), `warning CS0618` (`Assets/SharedAssets/Benchmark/PerformanceTest.cs`),
  and `[Licensing::Module] Error: Access token is unavailable; failed to update` (line 84).
- `asserts=0` on **every** test case in both this XML and the 430-test baseline XML — Unity's NUnit writer does not
  record assertion counts here, so per-test assert counts are **not** usable as evidence; names, results,
  fixture totals and the absence of failure nodes are.
- A runner exit code was not used as proof anywhere above.

---

## 3 Independent review of `SkillIntentGate` (read-only; nothing edited)

Method reviewed line by line (`Assets/Emberdelve/NetRules/SkillIntentGate.cs`, `Submit` at line 26): validity
(line 28) → replay (30) → forward window (31) → clock refill (33–40) → consume sequence (42) → flood (43) →
spend token (44). Cross-checked against the project's already-integrated per-sender admission
(`Assets/Emberdelve/NetRules/InputSanitizer.cs`), which the gate clearly mirrors.

`skillintent-gate-probe.py` → `skillintent-gate-probe.txt` is a faithful Python port of `Submit` (uint32 sequence
arithmetic, `double` clock) used only to probe the rule. **It first reproduces all four committed tests exactly**
(`port_reproduces_the_four_committed_tests: all match`), which is what makes the probe cases below trustworthy as
*rule* behaviour rather than port artefacts. It is not a test runner and does not replace the Unity run.

### 3.1 What is correct (verified, not assumed)

- **The uint subtraction is not wrap-exploitable.** `sequence <= _lastSequence` (line 30) is evaluated *before*
  `sequence - _lastSequence` (line 31), so at line 31 `sequence > _lastSequence` numerically and the subtraction
  cannot wrap. A "wrap the counter and replay an old low sequence" attack does not work.
- **Boundary pinned from both sides:** jump of exactly 64 accepted, 65 rejected (tests 2 and 4; probe agrees).
  Same `MaxSeqJump = 64` as `MoveSanitizeLimits.MaxSeqJump`.
- **A flooded request consumes its sequence** (line 42 before line 43) — identical to `InputSanitizer`'s documented
  rule, and it is what keeps an honest client from running into the forward-jump window after a flood
  (test 3: sequences 5…100 all Flood, then 101 accepted at +0.25 s).
- **Invalid requests leave no state behind** — no sequence advance, no token spend (test 2, probe
  `malformed_not_rate_limited`: 1000 malformed requests → `tokens_after 4.0`, `last_sequence 0`). Same rule as
  `InputSanitizer`'s "a rejected frame leaves NO state behind except its counter".
- **Burst cap works:** `Math.Min(Burst, …)` means a long idle or an absurd forward jump banks at most 4
  (probe `forward_jump_cap`: 4 accepted at t=0, then a request at `now=1e9` is still Accepted with `tokens_after 3.0`).
- **Non-finite time and sequence 0 rejected; slot bounds checked first.** `double.IsInfinity` covers both ±∞.
- **Per-connection isolation holds for freshly constructed gates** (test 4 + probe).
- **Pure rule code:** `using System;` only, in an assembly with `noEngineReferences: true` and `references: []` →
  headless-testable, no Unity dependency, no allocation, no engine call.
- **Honest-client behaviour on the project's real clock is sane.** With `NetMovementServer`'s clock
  (`now = _tick / NetTuning.TickRate`, TickRate 30) and 60 intents/s offered: accepted per second =
  **7, 4, 4, 4, 4, 4, 4, 4, 4, 4** (43 in 10 s). 7 in the first second = Burst 4 + one second of refill, minus
  tick quantisation (refill is 4/30 per tick) and the Burst cap. Sustained cap is exactly 4/s.

### 3.2 Findings for Codex (F1–F8)

**F1 — the clock has no floor and no ceiling: two permanent-starvation paths.** `_lastTime` is only ever raised
(line 36 `else if (now > _lastTime)`), and `now` is accepted at any finite magnitude.
- *Backward step:* after 4 requests at `now=1000`, 12 requests spanning `now=500…505.5` are **all Flood** — the
  bucket never refills until real time climbs back above 1000 (`backward_clock`).
- *Poisoned magnitude:* one request at `now=1e308` (finite, so line 28 passes) pins the mark; 10 later requests at
  `now=1e6` → `Accepted, Accepted, Accepted, Flood ×7` and then Flood forever (`poisoned_huge_clock`).
  This is only reachable if the seam ever hands the gate a client-influenced or re-based value — and the project's
  own authority clock convention is `Time.time` (`LocalCombatAuthority.cs:119`), which **resets on scene load /
  play-mode re-entry**, while `NetMovementServer` uses `_tick / TickRate`. Severity depends entirely on which clock
  U3B2 wires in. Suggested direction (Codex's call): feed the gate the tick-derived or `Stopwatch`-derived seconds,
  and/or clamp `now` to a monotone floor plus a maximum sane delta so a bad timestamp degrades one request instead
  of the connection.

**F2 — the 32-bit sequence space has no wrap or ceiling policy, and burning it is free.** Flood verdicts return
*after* `_lastSequence = sequence` and *without* spending a token, so advancement is not rate limited at all.
Simulated 100 000 requests at a constant clock: `Accepted 4, Flood 99 996`, `last_sequence` advanced by exactly 64
per request. Analytically the ceiling `4 294 967 295` is reached in **67 108 864 packets** (wire-rate, no token
cost). At the ceiling every later request is `Replay` (`ceiling_state_verdicts: Replay ×4`) — permanently, because
the gate exposes no `Reset`, epoch or resync path. `InputSanitizer` documents "not expected to wrap in a session (a
year of 30 Hz)"; that reasoning does **not** transfer here, because its window can be consumed 64× per request.
Self-inflicted for one connection, but it becomes a cross-player permanent lockout the moment a gate is keyed by
anything reusable (PlayerId, NetworkObject id, a pooled gate) instead of the connection instance.

**F3 — only well-formed requests are rate limited.** `Invalid` (line 28/31) and `Replay` (line 30) both return
before the token bucket: 1000 malformed requests and 1000 duplicate requests are free and leave no trace
(`malformed_not_rate_limited`, `replay_not_rate_limited`). The gate therefore cannot be the seam's flood control
against malformed or duplicate traffic — it bounds *admitted* intents only.

**F4 — no observability, unlike `InputSanitizer`.** The gate exposes only `LastSequence`. `InputSanitizer` exposes
`Accepted / Redundant / DroppedReplay / DroppedFlood / DroppedInvalid / InvalidNonFinite / InvalidSeqWindow / …`
and `LastInvalidReason`, which is exactly what `NetMovementServer.LogReject/LogInvalid` uses to log throttled
rejects. Without counters, the future U3B2 gate cannot print a positive `checks=… failures=0`-style summary about
flood behaviour, and the seam cannot detect or escalate abuse.

**F5 — the slot count `2` is a duplicated literal.** `slot >= 2` matches `SkillCaster.ActiveSlotCapacity = 2`
today (`SkillCaster.cs:29`, `TryCastSlot(0/1)` from `Skill1Pressed`/`Skill2Pressed`), but the direction of the
assembly references (`Emberdelve.Runtime` → `Emberdelve.NetRules`, never the reverse) means the gate cannot see
that constant. If the active kit ever grows, the gate silently answers `Invalid` for valid slots. Suggested
direction: put the constant in `Emberdelve.NetRules` and have `SkillCaster` reference it.

**F6 — the sequence is per-gate, not per-slot, and there is no `Redundant` verdict.** Two natural client
implementations break against it:
- per-key counters: `Accepted, Replay, Accepted, Replay` — the second skill key's presses are refused forever
  (`per_slot_numbering`);
- out-of-order arrival (two channels, or a resend batch like `MoveInputAggregator`'s last-three-frames pattern):
  seq 3 accepted, then seq 2 → `Replay`, permanently lost (`out_of_order_arrival`).
`InputSanitizer` has a `RedundancyWindow` + `Redundant` verdict precisely for benign resends; `SkillIntentGate`
classifies a benign duplicate and a hostile replay identically, so the seam cannot tell them apart and the client
gets no signal. Test 1 does encode the shared-counter convention (slot 1 must use sequence 2), but nothing in the
API or comments states it for whoever implements the client sender.

**F7 — no client feedback channel.** `Flood` and `Replay` carry no retry-after, no reason, no per-slot detail. A
legitimate press can be dropped with nothing said to the player or to the QA harness. The brief defers client
feedback; this is where it will hurt.

**F8 — lifecycle and threading.** The gate is not thread-safe (no lock, plain fields) and carries no identity
binding: it cannot tell you that the caller validated connection→object ownership first. A reused/pooled gate
inherits the previous connection's sequence and empty bucket: `reused_gate` → `Replay, Replay` for the new
client's first presses. "Each connection requires its own gate and discard on disconnect" is a *requirement the
gate cannot enforce or reveal*; there is no `Reset()` and no generation token to detect staleness.

### 3.3 Test coverage (observations, not fixes)

Covered by the four tests: both slots + replay in both directions; invalid inputs move nothing and spend nothing
(proven by the 4 accepted requests that follow the 6 invalid ones); flood bound over 96 requests at a frozen
clock; recovery at +0.25 s; jump boundary 64/65; a backward clock step; two gates are independent.

Not covered (all reachable from the rule, none currently failing): sequence ceiling / exhaustion (F2); absurd
forward timestamps and negative `now` (F1); gate reuse or any cleanup path (F8); the per-slot numbering trap (F6);
absence of counters (F4). `JumpBoundaryBackwardClockAndIndependentConnections` proves two *freshly constructed*
gates do not share state — it does not prove the seam will create and discard one per connection.

---

## 4 What was not re-run, and why

The brief scoped this job to the full EditMode suite plus the review. No gate entrypoint, no graphical Editor
session and no builder was run: the two added sources are referenced only by their own test assembly, and every
other guarded source is byte-identical to the D4d1-R1 tree (§1). Prior headless gate results
(`SkillPowerGate` 119/0, `SkillCooldownGate` 245/0, `InteractionSpeedGate` 192/0, `SkillAuthorityGate` 161/0,
`RolledAttributesGate` 86/0, `CloakSlotGate` 10 170/0, `RunInventoryUiFlow` 485/0, `AttributeSkillGate`
1121/0) and the D4d1 graphical proof remain as recorded in `LOCAL_D4d1-R1-20261102.md` §4; they were **not**
re-executed and are **not** re-claimed here.

---

## 5 Generated asset / data changes (recorded, not fixed)

Guarded asset set (`Assets/Emberdelve/Data`, `Art/Materials`, `Scenes`, `ProjectSettings`): **231 files before and
after**. `asset-drift-editmode.txt`: `shared=231 added=0 removed=0 contentChanged=1`.

| File | before → after |
|---|---|
| `ProjectSettings/ProjectSettings.asset` | 28 173 B `4518c7ac…229a407` (git-clean) → 28 149 B `9efd3702dc6fbb8154800e3afbc53ec37baf1a82100a71b127e57d2b7fcfdebb` (**git `M`**) |

`projectsettings-churn.diff` — one line, one hunk:

```
   scriptingDefineSymbols:
-    Standalone: SENTIS_ANALYTICS_ENABLED
+    Standalone:
```

Cause, from the raw log: line 84 `[Licensing::Module] Error: Access token is unavailable; failed to update`, then
line 486 `[ScriptCompilation] Requested script compilation because: Define symbols changed`, and every Emberdelve
assembly is recompiled with **130** defines instead of **131**. **This is a pre-existing side effect of batch-mode
test runs in this checkout, not something U3B1 introduced:** the earlier `D4d1-20261102/editmode-040435/editor.log`
shows the same event (line 489) and the same 131→130 drop. It had been masked before my run because a later session
restored the define (the file was git-clean when my job started). **I did not revert it** — restoring it would
myself be a project-settings edit. Codex should either `git checkout -- ProjectSettings/ProjectSettings.asset` or
accept it; note that a build made from the current tree compiles without `SENTIS_ANALYTICS_ENABLED`.

Everything else: `Assets/Emberdelve/Data` 0 changes (`AttributeCurve_Default.asset` untouched, no builder run);
`Art/Materials` 0 changes (the five pre-existing `_Color` round-off `M` entries are unchanged from job start);
`Assets/Emberdelve/Scenes` 0 changes; no `Assets/Logs` tree created (`Test-Path` → `False` for `Assets/Logs` and
`Assets/Logs.meta`). `git status --porcelain`: 75 entries before, 76 after, the **only** difference being
`M ProjectSettings/ProjectSettings.asset` (`git-status-before.txt`, `git-status-final.txt`).
Unity also wrote `C:/Users/yrapa/AppData/LocalLow/DefaultCompany/DakaDaka/TestResults.xml` (outside the project,
not a project asset).

---

## 6 Commands (all local, sequential, one Unity process at a time)

```
Logs/local-testing/U3B1-20261102/snapshot-sources.ps1 -Out source-snapshot-before.json | source-snapshot-final.json
Logs/local-testing/U3B1-20261102/snapshot-assets.ps1  -Out asset-snapshot-before.json  | asset-snapshot-final.json
python Tools/LocalTesting/compare_sources.py <controller U3B1 source-before.json> <local before>      # 274/274 identical, exit 0
python Tools/LocalTesting/compare_sources.py <controller D4d1-R1 source-before.json> <controller U3B1 source-before.json>  # 2 added, exit 1
python Tools/LocalTesting/compare_sources.py <local before> <local final>                             # identical, exit 0
python Tools/LocalTesting/compare_sources.py <controller U3B1 source-before.json> <local final>       # identical, exit 0

& ./Tools/Run-EmberdelveEditModeTests.ps1 -ProjectPath '.' -TestFilter 'Emberdelve' `
    -OutputDirectory Logs/local-testing/U3B1-20261102/editmode-20261102-093744 -MinimumTests 434 -TimeoutSeconds 900
python Tools/LocalTesting/summarize_results.py Logs/local-testing/U3B1-20261102/editmode-20261102-093744/summary.json   # status "pass", exit 0

Logs/local-testing/U3B1-20261102/analyze-results-xml.ps1 -New <results.xml> -Old <D4d1 430-test results.xml> -Out results-xml-analysis.txt
Logs/local-testing/U3B1-20261102/skillintent-gate-probe.py        # rule probe, exit 0
Logs/local-testing/U3B1-20261102/compare-assets.py <before> <after> --out asset-drift-editmode.txt   # 1 content change, exit 1
git diff -- ProjectSettings/ProjectSettings.asset                  # recorded as projectsettings-churn.diff
```

Only the runner's existing `-TimeoutSeconds` (900) was used; **no timeout fired** (`timedOut=false`) and **no process
was killed**. Pre-run state (`pre-run-state.txt`): `unity status` → 0 instances, no `Unity*` process,
`Temp/UnityLockfile` **ABSENT**, 75 porcelain entries, HEAD as in §1. Post-run: 0
instances, no `Unity*` process, `Temp/UnityLockfile` **ABSENT** — the lockfile was created and released by my own
batch process and was **never deleted by me**. No Editor was opened or closed, no Play mode, no scene load or save,
no auto-tick or Run-In-Background change, no capture, and **`settings.input` was never called** (no global input
asset or configuration touched).

---

## 7 Evidence

`Logs/local-testing/U3B1-20261102/` — 30 top-level files + `editmode-20261102-093744/` (3 files):
`pre-run-state.txt`, `head.txt`, `git-status-before.txt`, `git-status-final.txt`, `snapshot-sources.ps1`, `snapshot-assets.ps1`,
`source-snapshot-before.json`, `source-snapshot-final.json`, the four `compare_sources` outputs
(`hash-comparison-controller-to-local-before.txt`, `hash-comparison-D4d1R1-to-U3B1.txt`,
`hash-comparison-local-before-to-final.txt`, `hash-comparison-controller-to-local-final.txt`),
`editmode-20261102-093744.runner.txt`, `editmode-20261102-093744/{results.xml,editor.log,summary.json}`,
`summarize-editmode.txt`, `results-xml-analysis.txt`, `editmode-log-scan.txt`,
`skillintent-gate-probe.py`, `skillintent-gate-probe.txt`, `asset-snapshot-before.json`,
`asset-snapshot-final.json`, `compare-assets.py`, `asset-drift-editmode.txt`, `projectsettings-churn.diff`,
`patch-report.py` / `patch-report2.py` / `patch-report3.py` (edits to this report only), `source-snapshot-postreport.json` and
`hash-comparison-controller-to-postreport.txt`.

Final guard, taken after this report was saved: **274/274 guarded sources identical to the controller's
`source-before.json`** (`hash-comparison-controller-to-postreport.txt`, exit 0). `git status --porcelain` is
77 entries — the 75 present at job start plus `M ProjectSettings/ProjectSettings.asset` (§5, written by
Unity, not by me) and `?? Docs/Reviews/LOCAL_U3B1-foundation-20261102.md` (this report). No Unity process is
running, `Temp/UnityLockfile` is absent, and nothing was deleted.

---

## 8 Limits of this evidence / what remains for Codex

1. **Headless assertions only.** This is a batch-mode EditMode result plus a read-only source review plus a Python
   port of the rule. It is **not** real process networking, not a rendered-frame proof, and not server-authority
   proof. No RPC exists, no client sender exists, no two-process run was performed, and no network combat claim is
   made or implied.
2. **This model is text-only.** No image was produced or inspected; no subjective visual judgement is claimed.
3. **The probe is a port, not the C# binary.** Its sanity condition is that it reproduces all four committed tests
   exactly; the authoritative statement about the C# build remains the Unity run (434/434, 0 `error CS`).
   F1/F2/F6/F8 reproductions are rule-level sequences a U3B2 seam would have to feed the real gate to confirm.
4. **F1 and F2 are the two findings I would treat as design decisions to make before U3B2**, because both are
   permanent-state degradations with no recovery path in the current API (clock high-water mark; 32-bit sequence
   ceiling), and both are reachable through verdicts that cost the attacker nothing (F3).
5. **`ProjectSettings/ProjectSettings.asset` is left modified** (§5) because reverting it would itself be a
   project-settings change. Codex decides; a build from the current tree loses `SENTIS_ANALYTICS_ENABLED`.
6. **This job does not accept the U3B1 milestone.** It reports the suite result and the review; acceptance and every
   fix belong to Codex/the controller. No commit was made. `Docs/Reviews/LOCAL_U3B1-foundation-20261102.md` is the
   only file I wrote outside `Logs/`.


