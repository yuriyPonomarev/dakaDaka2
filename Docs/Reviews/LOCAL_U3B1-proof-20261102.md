# LOCAL U3B1-proof — 20261102

Local tester only. No game/test/`Tools/`/`ProjectSettings/`/asset edit, no commit, no controller resume, no
engine/package/model change, no network call beyond local Unity processes on `127.0.0.1`. Codex owns every fix.
Scope per brief: review of the six named U3B1 sources + NetIdentity/NetServer lifecycle, full Emberdelve EditMode,
`EmberdelveNetAdmissionGate.Run` with a **fresh** development proof player, and a loopback proof with one server +
two independently launched clients. This is a **development-only experiment, not production network combat**.

## 0 Verdict

| Item | Result |
|---|---|
| Source identity | HEAD `4a338d6911f130fc0ba3fbcd306d6fb8f999424d` **unchanged**; tested tree **byte-identical to the controller's `source-before.json`** (274/274, `status: identical`, exit 0) — before, after every stage, and after this report |
| Diff vs the U3B1-foundation tree | **exactly the six named changed sources**, 0 added, 0 removed (`hash-comparison-U3B1foundation-to-U3B1proof.txt`, exit 1) |
| Full EditMode (`-testFilter Emberdelve`) | **PASS — 435 tests, 0 failed, 0 skipped, 0 inconclusive, `result=Passed`, exit 0, `timedOut=false`, 81.22 s** |
| New named test | `Emberdelve.NetRules.Tests.NetScriptSpecTests.SkillProof_IsAnIsolatedProbe` present in the NUnit XML, `result=Passed`; it is the **only** added test-case (fixture `NetScriptSpecTests` 25 → 26) |
| Raw compile log | 7 772 lines / 547 819 B: **0** `error CS`, **0** `] FAIL`, **0** `mismatch`, **0** `Exception:`, **0** `Aborting batchmode`; 20 `warning CS` (see §2) |
| Admission gate (`EmberdelveNetAdmissionGate.Run`) | **PASS — `checks=109 failures=0`**, exit 0, `timedOut=false`, 112.01 s, 0 `error CS`, 0 `Exception:` in the gate log |
| Fresh development player | built in that gate run: `Builds/net-admission/20261102-101720/player/EmberdelveNet.exe`, `result=Succeeded`, 245 688 708 B, sha256 `988c2ba92dd6b42cb446e5280d2eff8a0402ccd0bb2d98ab1841923e40d5f4d1` — **used for the loopback proof, no older exe reused** |
| Loopback proof (2 runs, real processes) | **60 of 64 checks pass in both runs, identical 4 failures in both**: the identity/replay/invalid/cooldown/lifecycle/order semantics hold, but the **mana numbers are 70 → 38, not 75 → 43** |
| Cause of the 4 failures | **not a network defect**: the server's real warrior pool is the *attribute-derived* 70 (`75 + 5 × INT modifier −1`), which the project's own gates assert. The brief's 75/43 is the authored asset base. Reported, **no expectation or tolerance changed** |
| Review findings | 12 concrete findings (P1–P12); the seam is identity-first, server-clocked, server-decided and release-compiled-out, but **rejected traffic is not rate limited**, a **reconnect resets the gate**, and **nothing is replicated back to the client** |
| Generated change | **no lasting change**: `ProjectSettings/ProjectSettings.asset` churned during the EditMode run and was **restored by the gate run** — final content identical to job start, git-clean; `ProjectSettings/GraphicsSettings.asset` mtime-only. 231 guarded assets, 0 content changes |
| Source guard | **NO DRIFT** at any stage (274/274 identical to the controller snapshot) |
| Local stance | **EditMode 435/435 PASS, gate 109/0 PASS. Loopback proof: semantics proven, the brief's mana numbers do not match the actual class data → loopback verdict FAIL as written.** Nothing repaired |

---

## 1 Source identity

- HEAD `4a338d6911f130fc0ba3fbcd306d6fb8f999424d` (`main`), unchanged; **no commit made** (`head.txt`).
- Guard set = `local_tester.py source_hashes()` mirror (`Assets/Emberdelve/**/*.cs` + `Tools/**/*.ps1|*.py` +
  `AgentScripts/**/*.cs` + `TESTER_CONTRACT.md` + `unity-tester/**/*.md`): **274 before, 274 after every stage**.
- controller `runs/U3B1-proof-20261102/source-before.json` → my before snapshot: **274 shared, 0 added, 0 removed,
  0 changed** (`hash-comparison-controller-to-local-before.txt`, exit 0) → I tested exactly the tree the controller
  snapshotted for this job.
- controller U3B1-foundation before → controller U3B1-proof before (`hash-comparison-U3B1foundation-to-U3B1proof.txt`,
  exit 1): changed = exactly `NetIdentityTable.cs`, `NetPlayerRig.cs`, `NetInputSender.cs`, `NetClientProbe.cs`,
  `NetScriptSpec.cs`, `NetScriptSpecTests.cs` — the six the brief names. Nothing else moved.
- controller D4d1-R1 before → this tree (`hash-comparison-D4d1R1-to-U3B1proof.txt`, exit 1): +2 added
  (`SkillIntentGate.cs`, `SkillIntentGateTests.cs`) + those 6 changed. The 434-test foundation baseline
  (`U3B1-20261102/editmode-20261102-093744`) is therefore the correct floor, +1 new test = **435**, which I enforced
  with `-MinimumTests 435` instead of eyeballing it.
- After EditMode, after the gate, after both loopback runs and after this report: **0 changed**
  (`hash-comparison-before-to-after-editmode.txt`, `hash-comparison-before-to-after-gate.txt`,
  `hash-comparison-controller-to-local-final.txt`, all exit 0). **I modified no source, test, tool or setting.**

---

## 2 Full EditMode (real headless Unity process)

```
& ./Tools/Run-EmberdelveEditModeTests.ps1 -ProjectPath '.' -TestFilter 'Emberdelve' `
    -OutputDirectory Logs/local-testing/U3B1-proof-20261102/editmode-20261102-101243 `
    -MinimumTests 435 -TimeoutSeconds 900
```

`editmode-20261102-101243/summary.json`: `passed=true exitCode=0 timedOut=false tests=435 failed=0 skipped=0
inconclusive=0 status="Passed" problems=[] elapsedSeconds=81.22`. Runner transcript
(`editmode-20261102-101243.runner.txt`): `START EditMode Emberdelve pid=24952` → `PASS EditMode tests=435 failures=0
exit=0`.

`python Tools/LocalTesting/summarize_results.py …/summary.json` → `status: pass`, 435/0/0/0
(`summarize-editmode.txt`, exit 0). The digest was **not** used as proof; the originals were read directly:

- **NUnit XML** (`results-xml-analysis.txt`): `test-run total=435 failed=0 skipped=0 inconclusive=0 passed=435
  result=Passed`; assemblies `Emberdelve.NetRules.Tests.EditMode.dll 167/167`,
  `Emberdelve.Rules.Tests.EditMode.dll 268/268`; 435 `//test-case` nodes, **none** with `result != Passed`;
  `//failure`, `//reason`, `//error`, `//stack-trace` = **0** each.
- Fixtures: 26 new vs 26 old, **added 0, removed 0, changed 1** — `NetScriptSpecTests` 25 → 26. Full-name diff of
  test-cases: **added exactly 1** = `…NetScriptSpecTests.SkillProof_IsAnIsolatedProbe`, **removed 0**.
- That named case: `result=Passed label= asserts=0 duration=0.000288`, no `<failure>`/`<reason>`/`<output>` node.
  (`asserts=0` on every case in both this XML and the 434-test baseline — Unity's writer does not record assertion
  counts here, so names/results/fixture totals and the absence of failure nodes are the evidence, not assert counts.)
- **Raw log** (`editmode-log-scan.txt`): 0 `error CS`, 0 `] FAIL`, 0 `mismatch`, 0 `Exception:`,
  0 `Aborting batchmode due to failure`; `Test run completed. Exiting with code 0 (Ok). Run completed.` (L7687);
  results written to both `…/editmode-20261102-101243/results.xml` and Unity's own
  `AppData/LocalLow/DefaultCompany/DakaDaka/TestResults.xml` (outside the project, not a project asset).
  Both test assemblies were compiled in this run (`Csc …Emberdelve.NetRules.Tests.EditMode.dll`, ILPostProcess,
  `CopyFiles Library/ScriptAssemblies/…`) → the six changed sources and the new test compile cleanly.
- Warnings, unchanged in kind, **increased by 4**: `warning CS` 16 (foundation) → **20** here. Breakdown:
  `CS0618 RpcAttribute.RequireOwnership` **8 → 12** (the new `SkillIntentProofRpc` at `NetPlayerRig.cs:204` adds a
  third obsolete site; each site appears in 4 compile passes), `CS0168` ×4 (`EmberdelveLootRollerGate.cs(29,19)`),
  `CS0618 EventSystem.SetUITookitEventSystemOverride` ×4 (`Assets/SharedAssets/Benchmark/PerformanceTest.cs`).
  No new warning *kind*.
- No runner exit code was used as proof anywhere above.

---

## 3 Admission gate + fresh development proof player

```
& ./Tools/Run-EmberdelveGates.ps1 -ProjectPath '.' `
    -Methods 'Emberdelve.EditorTools.EmberdelveNetAdmissionGate.Run' `
    -OutputDirectory Logs/local-testing/U3B1-proof-20261102/gate-20261102-101633 -TimeoutSeconds 1800
```

`gate-20261102-101633/summary.json`: `passed=true`, one result `exitCode=0 timedOut=false elapsedSeconds=112.01`,
`summaries=["[Emberdelve][NetAdmission] PASS checks=109 failures=0"]`, `problems=[]`, `expectedDiagnostics=[]`.
Digest (`summarize-gate.txt`): `status: pass`, `checks 109`, `problems []` (exit 0) — again verified against the raw
log, not taken as proof:

- Gate log: 0 `error CS`, 0 `[A-Za-z]+Exception:`; the source scan check passed —
  `ok: no [Rpc] signature under Net/ carries a player/combat/party id parameter` (the new RPC's parameter list is
  `(int slot, uint sequence, RpcParams rpcParams)`: no identity).
- Fresh build inside that run: `[BuildWindows] net-proof start scenes=[Emberdelve_NetBoot.unity,
  Emberdelve_Level01.unity] output=…\20261102-101720\player\EmberdelveNet.exe` →
  `net-proof report result=Succeeded totalSize=245688708 totalTime=00:00:38.6072444`.
  `BuildOptions.Development` → `DEVELOPMENT_BUILD` is defined in the player, which is what makes the
  `#if DEVELOPMENT_BUILD || UNITY_EDITOR` proof RPC exist at all.
- Exact artifact: `Builds/net-admission/20261102-101720/player/EmberdelveNet.exe`, 667 648 B, created
  2026-11-02 10:17:51, sha256 `988c2ba92dd6b42cb446e5280d2eff8a0402ccd0bb2d98ab1841923e40d5f4d1`
  (`proof-exe-sha256.txt`). **No older executable was reused.**
- The gate's own scenario (admission, refusals, forged-owner regression, hard-kill removal, freed seat, graceful
  end) ran with its own logs in `Builds/net-admission/20261102-101720/logs/`; I copied them to
  `Logs/local-testing/U3B1-proof-20261102/gate-scenario-logs-20261102-101720/` (7 files, 325 153 B server log) so the
  evidence survives any later `Builds/` cleanup. The gate killed only its own PIDs; no forced kill was needed
  (`KillSurvivors` logged nothing).
- The gate does **not** exercise the skill RPC — it exercises `forge-owner`. The skill path is proven only by §4.

---

## 4 Loopback proof — one server + two independently launched clients (real process networking)

Driver: `Logs/local-testing/U3B1-proof-20261102/run-loopback-proof.ps1` (evidence-only; launches, waits on real log
lines, stops by unique quit files, records raw lines, evaluates checks). Run twice, same script, unchanged
expectations:

| Run | Evidence dir | Port | PIDs (server / A / B) | Result |
|---|---|---|---|---|
| 1 | `loopback-20261102-102542` | 56361 | 22860 / 30260 / 13652 | `checks=64 failures=4 verdict=FAIL` (exit 1) |
| 2 (R1) | `loopback-20261102-103117-R1` | 63754 | 27176 / 4224 / 472 | `checks=64 failures=4 verdict=FAIL` (exit 1) — **identical values** |

Commands actually issued (server first, then A, then B; all `-batchmode -nographics`, so `Application.isBatchMode`
was true in all three processes — the boot line of each log says `batch=True`, i.e. **no real player input**):

```
<exe> -batchmode -nographics -logFile <dir>/logs/server.log   -ember-net server -ember-port <free> `
      -ember-net-quit-file <dir>/server.quit -ember-net-autoquit 240 -ember-net-disconnect-timeout-ms 4000 `
      -ember-net-max-players 2 -ember-net-no-enemies
<exe> … -logFile <dir>/logs/client_a.log -ember-net client -ember-address 127.0.0.1 -ember-port <free> `
      -ember-slot proof-a -ember-class warrior -ember-net-connect-timeout 60 -ember-net-disconnect-timeout-ms 4000 `
      -ember-net-quit-file <dir>/client_a.quit -ember-net-autoquit 200
<exe> … -logFile <dir>/logs/client_b.log -ember-net client -ember-address 127.0.0.1 -ember-port <free> `
      -ember-slot proof-b -ember-class warrior … -ember-net-quit-file <dir>/client_b.quit `
      -ember-net-script skill-proof -ember-net-autoquit 200
```

Identity parsed from the **server's own** admission lines (not assumed): `admitted client=1 id=1 … slot=proof-a
class=warrior … players=1 senders=1 registry=1` and `admitted client=2 id=2 … slot=proof-b class=warrior …
players=2 senders=2 registry=2` → **A = client 1 / player 1, B = client 2 / player 2**, distinct ids and distinct
client ids. Server ready line was `players=0 senders=0 registry=0 … enemies=off`. Both clients settled with
`owner id=<own> views=2 owned=1 class=warrior`. B logged
`[client] script skill-proof sent target=1 forged=1 self=4`.

### 4.1 The five server lines (verbatim, run 1 = run 2, `server-skill-proof-lines.txt`)

```
[Emberdelve][Net][server] skill-proof reject=identity sender=2 target=1 mana=70
[Emberdelve][Net][server] skill-proof admitted  sender=2 slot=1 seq=1 cast=True  manaBefore=70 manaAfter=38 cooldown=14
[Emberdelve][Net][server] skill-proof reject=Replay  sender=2 seq=1
[Emberdelve][Net][server] skill-proof reject=Invalid sender=2 seq=2
[Emberdelve][Net][server] skill-proof admitted  sender=2 slot=1 seq=2 cast=False manaBefore=38 manaAfter=38 cooldown=14
```

Exactly 5 `skill-proof` lines in the whole server process; a 3 s extra flush wait changed nothing (5 → 5), so this is
not an asynchronous-logging artefact.

### 4.2 What the two runs proved (60 checks, identical in both)

- **Forged identity rejected**: exactly **1** `reject=identity`, `sender=2` (B's connection), `target=1` (A's player),
  and the server's own read of A's pool at that moment is **A's full starting pool** — A never cast.
- **Exactly one valid cast**: 2 admitted requests total, both from `sender=2`; `slot=1 seq=1 cast=True`;
  mana spent **exactly the authored Battle Cry cost 32** (`Skill_WarriorBattleCry.asset manaCost 32`);
  `cooldown=14` = the authored 14 s (`cooldownSeconds 14`), positive.
- **Replay rejected**: exactly 1, `sender=2 seq=1` (the duplicate).
- **Invalid slot rejected**: exactly 1, `sender=2 seq=2` (slot 9). **0 Flood** rejections.
- **Cooldown-blocked cast**: `slot=1 seq=2` was **admitted** by the gate (the Invalid verdict did not consume
  sequence 2) and **`cast=False`** from the server's own `SkillCaster`, with mana **unchanged** (38 → 38) and
  `cooldown=14` still positive.
- **No collateral**: `cast=True` count = 1, `cast=False` count = 1, admitted-from-A count = 0, and **no**
  `skill-proof` line names A's connection at all.
- **Causal order** in the server log: forged identity reject → B seq1 admitted → replay reject → invalid reject →
  B seq2 admitted (the probe deliberately forges first so it cannot consume B's sequence — verified, not assumed).
- **Lifecycle / cleanup**: server `shutdown players=0 senders=0 registry=0`, server and both clients
  `shutdown complete clean=True`, exactly 2 actors removed, all three processes exited on their own quit files with
  **exit 0**, and **no process survived** (checked again 2 s later). No kill was needed; no `Temp/UnityLockfile`
  existed or was created by these player processes; no Editor was opened or closed.
- **Log hygiene**: 0 `error CS`, 0 `NullReferenceException`, 0 `Exception:`, 0 `InvalidOperationException` in all
  three player logs (1 438 / 571 / 546 lines).

### 4.3 The four failures (preserved, not repaired)

| Check (as the brief states it) | Expected | Actual (both runs) |
|---|---|---|
| A mana at the identity rejection | 75 | **70** |
| B `manaBefore` seq1 | 75 | **70** |
| B `manaAfter` seq1 | 43 (75 − 32) | **38** (70 − 32) |
| B seq2 mana unchanged | 43 → 43 | **38 → 38** |

Diagnosis (read-only, no source touched, no tolerance altered):

1. The server's warrior pool is **not** the authored asset base. `EffectiveStatsSnapshot.cs:121-134` takes
   `attributes.MaxMana` when attributes are ready, and `AttributeRules.MaxMana(base, INTmodifier) =
   max(10, base + ManaPerIntelligenceModifier(5) × Modifier(INT))` with `Modifier(score) = floor((score−10)/2)`.
   `Class_Warrior.asset` authors `intelligence: 8` → modifier **−1** → **75 + 5×(−1) = 70**.
2. That 70 is the value the project itself asserts as correct in its own gates:
   `EmberdelveAttributeRulesGate.cs:242` (`"warrior max mana", 70, … "75+5*(-1)=70"`),
   `EmberdelveAttributeVitalsGate.cs:282` (`"warrior max mana 70 (75 + 5*-1)"`),
   `EmberdelveEffectiveStatsGate.cs:74` (`first.MaxMana == 70`), `EmberdelveAttributeCurveGate.cs:237`.
   `Class_Warrior.asset maxMana: 75` is the **base** handed to the rules, not the live pool.
3. So the brief's `75 → 43` is the authored base arithmetic; the actual class data yields **70 → 38**. Every
   *relational* requirement the brief cares about is satisfied: A's pool untouched at its full starting value,
   exactly one cast, exactly the authored 32 spent, positive cooldown, refused cast spends nothing, no extra casts.
4. Both runs produced byte-identical numbers, so this is deterministic rule behaviour, not a race, not a flush
   artefact, and not mana-regen drift (`manaRegenPerSecond 3`, `manaRegenDelay 3.5` — all five RPCs are sent in one
   client frame and processed within ~1 s, and the refused cast left 38 → 38 exactly).
5. **I did not change the expected values** to make the run pass: the same script with the brief's 75/43 checks was
   run twice, and both runs are recorded as FAIL with these exact numbers. Codex decides whether the brief's numbers
   or the derived pool is the intended contract.

---

## 5 Review findings (read-only; nothing repaired)

**P1 — identity really is checked before any state or sequence.** `NetPlayerRig.SkillIntentProofRpc`
(`NetPlayerRig.cs:205-240`) evaluates `IsServer → IsSpawned → table != null → TryGetByClient(sender) → row != null →
sender == OwnerClientId → row.ClientId == sender → row.Object == NetworkObject → row.Rig != null → row.Rig == Rig →
row.PlayerId == row.Rig.PlayerId → !row.Rig.IsDead → row.Rig.Skills != null` **before** `row.SkillIntents.Submit`
and before any cast. Over the wire the forged call was rejected with no gate verdict, no sequence movement and no
mana change on A. The RPC carries no identity (`slot`, `sequence`, `RpcParams`), and the gate's own source scan
check passed.

**P2 — the handler derives everything from the sender's row, never from the object the RPC was delivered on.**
`row` is looked up by `SenderClientId` and the cast runs on `row.Rig`. So even if the ownership test were later
weakened, a forged call could not act on the victim — good defence-in-depth. Caveat: the **rejection path reads the
victim's mana** (`target.Mana.CurrentMana`) purely to log it. Server-side only (nothing is sent back), but a rejected
request still causes a read of the victim's private state; a future seam should log the reject without touching the
victim.

**P3 — the clock is server-owned and monotonic.** `Submit(slot, sequence, Time.realtimeSinceStartupAsDouble)`
(line 224): unscaled seconds since this server process started, never a request-supplied value, and not `Time.time`
(which resets on scene load — the foundation report's F1 hazard). This closes F1 for the current seam. It is
**per-process**: if a gate ever outlives a clock rebase (a pooled/reused gate, or a host that restarts its clock),
F1's high-water-mark starvation returns; the current code cannot hit that because the gate is created with the row
(see P4).

**P4 — lifetime is correct per connection, but a reconnect resets it.** `NetIdentity.SkillIntents` is
`internal readonly … = new SkillIntentGate()` (`NetIdentityTable.cs:22-25`), one per row; `NetServer.RemoveActor` /
`OnShutdownBegin` call `_table.RemoveByClient`, the row (and its gate) is dropped, and `NetServer._lastRemovedBySlot`
logs that a returning slot is admitted as a **new actor with a new id**. Consequence: **a disconnect/reconnect hands
a hostile client a fresh gate — sequence back to 0 and a full 4-token burst** — so the gate bounds one connection's
lifetime only. There is no per-slot or per-player-account memory of abuse, and no `Reset()`/generation token.

**P5 — rejected traffic is not rate limited at all, and the forged path bypasses the gate.** The identity reject
returns **before** `Submit`, so a forged intent costs the attacker nothing the gate can see or count: no counter
(`ForgeRejectCount` exists for the movement/owner probes, nothing for skill-proof), no log throttling, no escalating
disconnect. One forged RPC = one reliable message + one full identity check + one server log line, repeatable at
client speed. The foundation report's F3 ("only well-formed, admitted requests are rate limited") is now demonstrated
over real connections, and it is worse for the identity path because the gate is never consulted.

**P6 — the slot bound is still a duplicated literal.** The gate's `slot >= 2` matches `SkillCaster.ActiveSlotCapacity
= 2` today, and the wire proof shows the gate (not the caster) rejecting slot 9 — the caster was never asked. The
assembly direction (`Emberdelve.Runtime → Emberdelve.NetRules`, never the reverse) still means the two constants can
drift silently.

**P7 — sequence semantics are per-connection and now have an observable client contract.** Verified over the wire:
an `Invalid` verdict leaves the sequence where it was, so the probe's later valid `seq=2` was accepted; a `Flood`
verdict consumes the sequence (foundation §3.1). Nothing in the API or comments tells the future real sender that it
must **reuse** a sequence after `Invalid` and **advance** after `Flood`, and the gate still has no `Redundant`
verdict, so a benign resend or an out-of-order arrival (two channels, `MoveInputAggregator`-style last-three-frames)
is classified as hostile `Replay` and lost permanently (foundation F6 — still unproven over the wire).

**P8 — deprecated NGO API usage grew.** `RequireOwnership` is obsolete in this NGO version; `NetPlayerRig.cs` now
has **three** such RPCs (lines 204, 244, 265) and the EditMode log shows the `CS0618` count rise 8 → 12 occurrences
(§2). NGO recommends `InvokePermission = RpcInvokePermission.Everyone` for exactly this "let the server decide"
pattern. When NGO removes the obsolete field, three development probes would change meaning at once.

**P9 — the release guard holds, with one cosmetic wart.** The RPC (198-284), the gate field (22-25) and every call
site (`NetClientProbe.cs:149-166`) are inside `#if DEVELOPMENT_BUILD || UNITY_EDITOR`; a grep over `Assets` finds
`SkillIntentProofRpc` / `SkillIntents` / `new SkillIntentGate` **only** inside guarded blocks and the gate's own
tests → a release build contains no skill RPC and no gate. Wart: `NetClientProbe`'s `#else` branch (line 167) logs
`script forge-owner unavailable (not a development build)` even when the requested script was `skill-proof`, and
`NetScriptSpec.TryParse("skill-proof")` is **not** guarded, so a release client still parses it, `NetInputSender`
still nulls `_spec` for it (`IsSkillProofOnly`), and in a **non-batch** client that makes `_useRealInput` true — real
keyboard input live on a client that was supposed to be a scripted probe. Not reachable in this proof (all three
processes logged `batch=True`), but it is a live footgun for a windowed run.

**P10 — nothing is replicated back.** There is no server→owner RPC for mana, cooldown or the visual. The client logs
contain **0** lines with a mana or cooldown state (the 49 `mana` substring hits in `client_b.log` are all
`Managed`/`Manager`), and the only `SendTo.Owner` RPC on `NetPlayerRig` is the movement `AckRpc`. So B's local pool
stays 70 while the server's is 38, and A's client learns nothing about the forged call. The brief's "client
mana/cooldown/visual replication is NOT solved" is confirmed by evidence, not just by absence of code.

**P11 — probe victim selection is inconsistent.** `NetClientProbe.RunScript` takes the **first** non-owned spawned
rig; `NetInputSender.FindVictim` takes the **highest server player id**. With more than two players the forged target
is not deterministic and the two probes would attack different victims. Harmless in a 2-player proof, misleading in a
3+ player one.

**P12 — an empty Battle Cry still costs 32 mana.** The server ran with `enemies=off` (`registry=0`), yet
`cast=True` and 32 mana were spent: `BattleCryBehavior.Cast` returns true regardless of how many victims it found.
That is pre-existing authored behaviour, not a U3B1 defect, but in a networked game it means a client can burn its own
pool on a cast that hits nothing — worth a deliberate rule (or a minimum-target check) before this becomes a real
channel.

---

## 6 What was not proven (limits of this evidence)

1. **Real process networking on loopback only**, three `-batchmode -nographics` players. No rendered capture, no
   graphical Editor, no image artefact, and **no subjective visual judgement** (this model is text-only). No real
   player input was involved (all processes logged `batch=True`), so **no full-multiplayer or real-input claim** is
   made.
2. Not covered by this job and still open for Codex: movement interleaved with skill intents, host and offline
   regressions, disconnect/death mid-proof, malformed-packet rate limiting and packet-level floods, the >2-player
   case, rejoin abuse (P4), out-of-order arrival (P7), client-side replication (P10).
3. The loopback proof is **single-shot**: 5 RPCs in one client frame, one connection each, one server. It proves the
   ordering and the verdict mapping, not sustained behaviour.
4. The gate run proves the **existing** admission + forged-owner regression is unbroken by U3B1 (109 checks, 0
   failures); it does not exercise the skill RPC.
5. Prior jobs' results (D4d1-R1 gate numbers, the 434-test foundation run) were used only as **comparison
   baselines** for identity and counts; they were not re-executed and are not re-claimed here.

---

## 7 Generated changes and settings drift (recorded, not fixed)

Guarded asset set (`Assets/Emberdelve/Data`, `Art/Materials`, `Scenes`, `ProjectSettings`): **231 before, 231 after**
every stage.

| Stage | Content changes | Notes |
|---|---|---|
| after EditMode (`asset-drift-editmode.txt`) | **1**: `ProjectSettings/ProjectSettings.asset` 28 173 B `4518c7ac…` → 28 149 B `9efd3702…` (git `M`) | the known batch-test churn: `[ScriptCompilation] Requested script compilation because: Define symbols changed` (log L3720) and `Emberdelve.Runtime.dll … 131 defines` (L979) → `130 defines` (L4481), i.e. Unity dropped the `SENTIS_ANALYTICS_ENABLED` Standalone define (`projectsettings-churn-editmode.diff`, one line) |
| after the gate (`asset-drift-gate.txt`) | **0** | the gate's Editor run re-added the define (`Define symbols changed` L274, `Emberdelve.Runtime.dll … 131 defines` L1575) → file content back to the job-start hash; `GraphicsSettings.asset` mtime-only |
| final (`asset-drift-final.txt`) | **0** content changes, 2 mtime-only (`ProjectSettings.asset`, `GraphicsSettings.asset`) | `SENTIS_ANALYTICS_ENABLED` present again; `git status --porcelain -- ProjectSettings` shows only the pre-existing `?? ProjectSettings/Packages/com.unity.pipeline/` |

`git status --porcelain`: **83 entries at job start, 84 after the EditMode run, 83 after all four Unity/player runs, 84 with this report saved** (`git-status-postreport.txt`: the extra entry is `?? Docs/Reviews/LOCAL_U3B1-proof-20261102.md`) — the only transient
difference was `M ProjectSettings/ProjectSettings.asset`, written by Unity and restored by Unity. I reverted nothing.
`Assets/Emberdelve/Data` 0 changes (no builder run by me; `AttributeCurve_Default.asset` untouched by me),
`Art/Materials` 0 changes, `Assets/Emberdelve/Scenes` 0 changes — the gate's `EnsureNetBootScene` / net-prefab
builders found their assets already present and rewrote nothing. Unity wrote
`AppData/LocalLow/DefaultCompany/DakaDaka/TestResults.xml` (outside the project). The gate build wrote
`Builds/net-admission/20261102-101720/` (player + logs + quit files), left in place, not deleted.

---

## 8 Commands (all local, sequential; never two Editors at once)

```
Logs/local-testing/U3B1-proof-20261102/snapshot-sources.ps1 -Out source-snapshot-before.json | -after-editmode | -after-gate | -final
Logs/local-testing/U3B1-proof-20261102/snapshot-assets.ps1  -Out asset-snapshot-before.json  | -after-editmode | -after-gate | -final
python Tools/LocalTesting/compare_sources.py <controller source-before.json> <local before>          # 274/274 identical, exit 0
python Tools/LocalTesting/compare_sources.py <controller U3B1-foundation before> <controller U3B1-proof before>   # 6 changed, exit 1
python Tools/LocalTesting/compare_sources.py <controller D4d1-R1 before> <controller U3B1-proof before>           # +2 added +6 changed, exit 1
python Tools/LocalTesting/compare_sources.py <local before> <local after-editmode|after-gate|final>  # identical, exit 0

& ./Tools/Run-EmberdelveEditModeTests.ps1 -ProjectPath '.' -TestFilter 'Emberdelve' `
    -OutputDirectory Logs/local-testing/U3B1-proof-20261102/editmode-20261102-101243 -MinimumTests 435 -TimeoutSeconds 900
python Tools/LocalTesting/summarize_results.py Logs/local-testing/U3B1-proof-20261102/editmode-20261102-101243/summary.json   # pass, exit 0
Logs/local-testing/U3B1-proof-20261102/analyze-results-xml.ps1 -New <results.xml> `
    -Old Logs/local-testing/U3B1-20261102/editmode-20261102-093744/results.xml -Out results-xml-analysis.txt

& ./Tools/Run-EmberdelveGates.ps1 -ProjectPath '.' -Methods 'Emberdelve.EditorTools.EmberdelveNetAdmissionGate.Run' `
    -OutputDirectory Logs/local-testing/U3B1-proof-20261102/gate-20261102-101633 -TimeoutSeconds 1800
python Tools/LocalTesting/summarize_results.py Logs/local-testing/U3B1-proof-20261102/gate-20261102-101633/summary.json        # pass, exit 0

& ./Logs/local-testing/U3B1-proof-20261102/run-loopback-proof.ps1 `
    -Exe Builds/net-admission/20261102-101720/player/EmberdelveNet.exe `
    -Out Logs/local-testing/U3B1-proof-20261102/loopback-20261102-102542          # exit 1 (4 numeric checks)
& … -Out Logs/local-testing/U3B1-proof-20261102/loopback-20261102-103117-R1       # exit 1 (same 4)
python Logs/local-testing/U3B1-proof-20261102/compare-assets.py <before> <after|final> --out asset-drift-*.txt
```

Only the runners' own `-TimeoutSeconds` were used (900 / 1800); **no timeout fired** (`timedOut=false` everywhere) and
**no process was killed** — every player process exited on its own quit file with exit 0. Pre-run state
(`pre-run-state.txt`): 0 Unity instances, no `Unity*` process, `Temp/UnityLockfile` **ABSENT**, 83 porcelain entries,
HEAD as in §1. Post-run: 0 Unity/player processes, `Temp/UnityLockfile` **ABSENT** (created and released by my own
batch processes, never deleted by me). No Editor was opened or closed, no Play mode, no scene load or save, no
capture, no auto-tick or Run-In-Background change, and **`settings.input` was never called** (no global input asset
or configuration touched).

---

## 9 Evidence

`Logs/local-testing/U3B1-proof-20261102/` — `pre-run-state.txt`, `head.txt`, `git-status-after-editmode.txt`,
`git-status-final.txt`, `snapshot-sources.ps1`, `snapshot-assets.ps1`, `compare-assets.py`, `analyze-results-xml.ps1`,
`run-loopback-proof.ps1`, 4 source snapshots (`before`, `after-editmode`, `after-gate`, `final`), 4 asset snapshots,
5 `compare_sources` outputs (`hash-comparison-controller-to-local-before.txt`,
`hash-comparison-U3B1foundation-to-U3B1proof.txt`, `hash-comparison-D4d1R1-to-U3B1proof.txt`,
`hash-comparison-before-to-after-editmode.txt`, `hash-comparison-before-to-after-gate.txt`,
`hash-comparison-controller-to-local-final.txt`), `editmode-20261102-101243/` (`results.xml`, `editor.log`,
`summary.json`) + `editmode-20261102-101243.runner.txt`, `summarize-editmode.txt`, `results-xml-analysis.txt`,
`editmode-log-scan.txt`, `gate-20261102-101633/` (gate log + `summary.json`) + `gate-20261102-101633.runner.txt`,
`summarize-gate.txt`, `proof-exe-sha256.txt`, `gate-scenario-logs-20261102-101720/` (7 logs),
`loopback-20261102-102542/` and `loopback-20261102-103117-R1/` (each: `logs/server.log`, `logs/client_a.log`,
`logs/client_b.log`, `server-skill-proof-lines.txt`, `proof-checks.txt`, three `.quit` files) + the two
`loopback-*.runner.txt`, `asset-drift-editmode.txt`, `asset-drift-gate.txt`, `asset-drift-final.txt`,
`projectsettings-churn-editmode.diff`, `source-snapshot-postreport.json` + `hash-comparison-controller-to-postreport.txt` (final guard: **274/274 identical to the controller `source-before.json`, exit 0**).

Outside `Logs/`: **`Docs/Reviews/LOCAL_U3B1-proof-20261102.md` (this report) is the only file I wrote.** No commit
was made and this job does **not** accept the U3B1 milestone.

---

## 10 Handover to Codex (one line each)

1. **Loopback numeric contract**: the server's warrior pool is 70 (INT −1), not 75 — decide whether the brief's
   75/43 or the derived 70/38 is the proof contract; the delta (32) and cooldown (14) already match the authored data.
2. Rejected/forged skill traffic is uncounted and unthrottled (P5) — the seam needs a reject counter and an
   escalation path before this becomes a real channel.
3. Reconnect resets a connection's gate (P4) — decide whether abuse memory should survive a rejoin.
4. `RequireOwnership` is obsolete and now used by three probes (P8); `InvokePermission` is the forward-compatible
   spelling.
5. Client mana/cooldown/visual replication (P10), malformed-packet rate limiting, host/offline regression and
   movement-vs-intent ordering remain unsolved, exactly as the brief states.
