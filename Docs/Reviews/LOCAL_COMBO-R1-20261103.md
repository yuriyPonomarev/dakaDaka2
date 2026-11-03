# LOCAL_COMBO-R1-20261103 — local tester report (COMBO R1: re-verify the FIX1 gate repairs)

Role: local tester only. Ran the assigned checks on the mirror, preserved evidence, diagnosed the remaining failure.
No source, test, tool, settings or project file was changed. Nothing was committed. The user's Unity Editor
(PID 29648, main checkout, `Emberdelve_Level01`) was never touched; all Unity batch work ran in `DakaDaka_ci`.

## 1. Identity

| Item | Value |
|---|---|
| Main checkout | `C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka` |
| Main HEAD at run start | `5311bb6f7762a86e90f922827aa6e2b99da0898e` (U3B2); the whole U3B3+U3C1+V2+FIX1 change set was then **uncommitted** in the working tree (142 `git status` entries) |
| Commit made during this session | Codex committed `91da51d15eb61960f26aaa25394b6cc43c96e2c9` — "U3B3 + U3C1 + V2 + FIX1: network weapon combat, blade trajectory/guard rules, dark retro visuals" — at 2026-11-03 16:14:15, after every Unity step of this report had finished. `git diff HEAD` afterwards shows **0 tracked files differing**, and the 319-file source snapshot still equals the controller's `source-before.json` (`compare-source-drift-final.json`, `identical`), i.e. **the content tested here is exactly the content now committed** |
| Main `git status --porcelain` | 142 entries before and after this run |
| Mirror checkout | `C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_ci` |
| Mirror sync | `robocopy <main> <ci> /MIR /XD Library Temp Logs Builds UserSettings obj .git .vs /XF *.csproj *.sln *.user *.suo` → exit 1 (< 8, acceptable); then `robocopy <main>\Logs\v2_assets <ci>\Logs\v2_assets /MIR` → exit 0 |
| Source identity main vs mirror | 319 controller-scope source files (`Assets/Emberdelve/**/*.cs`, `Tools/**/*.ps1|*.py`, `AgentScripts/**/*.cs`, contract/profile md) — **identical hashes** (`compare-main-vs-mirror.json`, status `identical`); also `Assets/Emberdelve` 299 `.cs` and `Tools` 12 files identical |
| Source drift during the run | none: main re-hashed after the runs and again after the commit both equal the controller's `runs/COMBO-local-R1-20261103/source-before.json` (`compare-source-drift.json`, `compare-source-drift-final.json`, both `identical`) |
| FIX1 delta vs the COMBO run | 11 changed files, 0 added, 0 removed (`compare-controller-before-prev-vs-r1.json`): `EmberdelveAssetLibrary.cs`, `EmberdelveBladeDefenseGate.cs`, `EmberdelveCombatFlowGate.cs`, `EmberdelveNetCombatGate.cs`, `EmberdelveRetroVisualGate.cs`, `NetCombatFx.cs`, `NetCombatSender.cs`, `NetSkillServer.cs`, `CombatNetLimits.cs`, `LocalCombatAuthority.Reports.cs`, `LocalCombatAuthority.Swings.cs` |
| Evidence root | `Logs/local-testing/COMBO-R1-20261103/` |

## 2. Verdict

**NOT CLEAN — one gate still fails: `EmberdelveNetCombatGate.Run` 242 checks / 5 failures (identical in two separate processes).**
Everything else assigned is green: 9 gates + the full EditMode suite.

All four FIX1 items that were reported as defects in `Docs/Reviews/LOCAL_COMBO-20261103.md` §4.1, §4.2 and §4.3.b–d are **fixed and verified**. The five remaining failures are the same five melee amount checks of §4.3.a — and this run's new gate diagnostic finally identifies why they fail (§4 below).

| # | Step | Evidence dir | Result |
|---|---|---|---|
| 1 | `EmberdelveBootstrapMenu.BuildEverything` (mirror) | `build-everything-20261103-141112` | ran to completion in the log; **no `summary.json`** (runner hit its 3600 s timeout, killed its own Unity PID 28088, then threw in `ReadAllText`) — see §5.1 |
| 2 | `EmberdelveRetroVisualGate.Run` | `gates-visual-20261103-151855` | **PASS** checks=91 failures=0 (was 90/1) |
| 2 | `EmberdelveVisualGate.Run` | same | **PASS** checks=193 failures=0 |
| 2 | `EmberdelveBladeDefenseGate.Run` | `gates-bladecombat-20261103-152016` | **PASS** checks=293 failures=0 (was 279/1) |
| 2 | `EmberdelveCombatFlowGate.Run` | same | **PASS** checks=584 failures=0 |
| 2 | `EmberdelveSweptMeleeGate.Run` | same | **PASS** 674/0 |
| 2 | `EmberdelveCombatAuthorityGate.Run` | same | **PASS** 51/0 |
| 2 | `EmberdelveRuntimeSmokeTest.RunCombatFlow` | same | **PASS** 234/0 |
| 3 | `EmberdelveNetCombatGate.Run` (TimeoutSeconds 2400) | `gate-netcombat-20261103-152358` | **FAIL** checks=242 failures=5 (124.0 s) |
| 3 | `EmberdelveNetCombatGate.Run` (repeat) | `gate-netcombat-repeat-20261103-154226` | **FAIL** checks=242 failures=5 — byte-identical 5 lines (105.8 s) |
| 3 | `EmberdelveNetSkillGate.Run` (disconnect path changed) | `gates-netreg-20261103-153526` | **PASS** 140/0 |
| 3 | `EmberdelveNetFinalGate.Run` | same | **PASS** 170/0 |
| 4 | EditMode `-TestFilter Emberdelve -MinimumTests 555` | `editmode-20261103-154829` | **PASS** tests=555 failed=0 skipped=0 inconclusive=0 (78.2 s) |

EditMode compile: no `error CS`. Two warnings, unchanged from the COMBO run:
`Assets/Emberdelve/Editor/EmberdelveLootRollerGate.cs(29,19): warning CS0168` and
`Assets/SharedAssets/Benchmark/PerformanceTest.cs(68,13): warning CS0618`.
Fixture set is identical to the COMBO run (no suite added/removed/changed — `analysis/editmode_suites_diff.txt`); the combat/net fixtures are green:
`BladeDefenseRulesTests` 23, `CombatIntentGateTests` 5, `CombatNetScriptAndArgsTests` **17** (the COMBO report wrote 16; both XMLs say 17),
`CombatScriptParamsTests` 15, `CombatVitalsDataTests` 4, `ServerDrawTimerTests` 5.

## 3. FIX1 items — re-verified

| FIX1 item | Evidence |
|---|---|
| RetroVisual whole-identifier forbidden-call scan | `[Emberdelve][RetroVisual] ok: the forbidden-call scan matches Destroy( calls and not the OnDestroy method name` and `ok: EnemyVisualDriver is presentation only: no damage, status, stun, movement or physics calls`; 91 checks (one more than before = the self-proving check), 0 failures. Shader checks still ok (`retro shader loads as Emberdelve/RetroFullscreen`, `compiles without errors`); no `Shader error` in any log; the only `is not supported` line is the engine sample `Hidden/ProbeVolume/VoxelizeScene` (conservative rasterization), unchanged |
| Sword-vs-sword guard holds by authored data | `Weapon_AshenShortSword.asset` carries `impactPower: 10`, `impactResist: 15` in **main and mirror**; BladeDefense sections now `data 25/0`, `warning equals sweep 110/0`, `evasion 20/0`, **`front, back, height 38/0`** (was 24/1), `endurance (empty pool) 11/0`, `damage through the resolver 18/0`, `shields never parry 16/0`, `wire 55/0` |
| Invariant-culture display numbers | **Not observable in a passing run**: `EmberdelveBladeDefenseGate` logs only section summaries, so no FAIL message was produced. The comma-decimal strings still in the logs (`amount=18,200`, `Killing ADB server in 0,1559225 seconds`) are Unity's own runtime logs, not gate messages. No claim either way |
| Client `server-hit` prints enum names | Gate checks `B saw every hit on itself as a server report` and `A saw every hit it caused as a server report (no local resolution)` now **pass**. Raw client lines (both clients): `[Emberdelve][Net][client] server-hit src=Melee attacker=1 target=2 enemy=False amount=25.160 loc=Arms blocked=False guardBroken=False killed=False` |
| Abuse expectation counts B's valid swing | `ok: B's script reports the 1 valid intent(s) the gate derives its swings from`, `ok: the server accepted exactly B's valid AttackPress intent(s) (1 of 1)`, `ok: the swings are exactly the ones B's valid intents caused (1 swings for 1 accepted)`, `ok: A (the forged-on victim) never swung`, `ok: no other swing happened on the server`, `ok: no hit happened (the abuser's swing reached nobody, its guard toggles hurt nobody)`. Server: `combat-swing id=2 weapon=ashen_short_sword pattern=sword_slash_lr step=0 … committed=0`; B's client: `script combat-abuse sent target=1 forged=3 valid=1 replay=1 invalid=1 flood=40`; abuse log has 10 `combat-intent`, 1 `combat-swing`, 0 `combat-hit` |
| Deferred transport disconnect → post-decision traffic counted | `ok: traffic after the disconnect decision was dropped unseen (ignored=20)`. Server lines verbatim: `[Emberdelve][Net][server] skill-abuse disconnect client=2 slot=abu-b reason='skill abuse' strikes=1 priorStrikes=0 identity=3 invalid=1 replay=1 flood=11 total=16 excess=8 threshold=8 memorySeconds=600.000` and `[Emberdelve][Net][server] skill-conn final client=2 slot=abu-b accepted=0 invalid=0 replay=0 flood=0 cast=0 refused=0 rejects=16 identity=3 excess=8 ignored=20 strikes=0` (was `ignored=0`). Zero `combat-intent` / `combat reject` / `skill reject` / `combat-swing` / `combat-hit` lines appear after the decision line (1603) — they were dropped unseen, which is the rule being asserted. `combat-conn final client=2 id=2 accepted=10 refused=0 gateAccepted=10 invalid=1 replay=1 flood=11` |
| NetSkill disconnect path regression | `EmberdelveNetSkillGate.Run` 140/0 (unchanged), `EmberdelveNetFinalGate.Run` 170/0 |
| `EmberdelveGameConfig.asset` carries the U3C1 flow fields | Main's working-tree asset now contains `horizontalHeight: 2`, `bladeHeightTolerance: 1`, `guardEnduranceMax: 0`, `commitSwingAim: 0`, `guardBreakReductionScale: 0.3` …; the mirror's `BuildEverything` produced a **byte-identical** file (it is not in the differing-asset list of §6) |

## 4. Remaining failure — `EmberdelveNetCombatGate.Run`, 5 melee amount checks (deterministic)

Both runs printed `FAIL checks=242 failures=5`; the 5 lines are identical in both runs and identical in numbers to the COMBO run.

Full new FAIL messages, verbatim (run `gate-netcombat-20261103-152358`, log lines 12036/12050/12064/12078/12092):

```
[Emberdelve][NetCombat] FAIL: hit 1: Arms open amount 25.160 matches the data-derived value (no pattern of the weapon explains 25.160 (0 patterns tried; base 26.000 flat 0.000, step 1.000, out 1.344, loc 0.800, zone 0.900, blocked False reduction 0.000, armor 0.000;))
[Emberdelve][NetCombat] FAIL: hit 2: Head BLOCKED amount 9.341 matches the data-derived value (no pattern of the weapon explains 9.341 (0 patterns tried; base 26.000 flat 0.000, step 1.100, out 1.344, loc 1.500, zone 0.900, blocked True reduction 0.820, armor 0.000;))
[Emberdelve][NetCombat] FAIL: hit 3: Arms open amount 25.160 matches the data-derived value (no pattern of the weapon explains 25.160 (0 patterns tried; base 26.000 flat 0.000, step 1.000, out 1.344, loc 0.800, zone 0.900, blocked False reduction 0.000, armor 0.000;))
[Emberdelve][NetCombat] FAIL: hit 4: Head open amount 51.892 matches the data-derived value (no pattern of the weapon explains 51.892 (0 patterns tried; base 26.000 flat 0.000, step 1.100, out 1.344, loc 1.500, zone 0.900, blocked False reduction 0.000, armor 0.000;))
[Emberdelve][NetCombat] FAIL: hit 5: Arms open amount 25.160 matches the data-derived value (no pattern of the weapon explains 25.160 (0 patterns tried; base 26.000 flat 0.000, step 1.000, out 1.344, loc 0.800, zone 0.900, blocked False reduction 0.000, armor 0.000;))
```

The decisive new fact is **`0 patterns tried`** and an empty candidate list: the gate enumerated **no** `SwingPatternData` at all.

Matching `combat-hit` server lines, verbatim (`DakaDaka_ci/Builds/net-combat/20261103-152418/logs/melee_server.log`):

```
[Emberdelve][Net][server] combat-hit src=Melee attacker=1 attackerCombat=1 target=2 targetCombat=2 targetKind=Player weapon=ashen_short_sword amount=25.160 loc=Arms locMult=0.800 blocked=False blockReduction=0.000 guardBroken=False killed=False outMult=1.344 flat=0.000 armor=0.000 step=1.000 zone=0.900 contactT=0.478 charge=1.000 air=0.000 airScale=-1.000 dist=1.395 px=5.220 py=1.430 pz=4.620
[Emberdelve][Net][server] combat-hit src=Melee attacker=1 attackerCombat=1 target=2 targetCombat=2 targetKind=Player weapon=ashen_short_sword amount=9.341 loc=Head locMult=1.500 blocked=True blockReduction=0.820 guardBroken=False killed=False outMult=1.344 flat=0.000 armor=0.000 step=1.100 zone=0.900 contactT=0.443 charge=1.000 air=0.000 airScale=-1.000 dist=1.395 px=4.988 py=1.931 pz=5.182
[Emberdelve][Net][server] combat-hit src=Melee attacker=1 attackerCombat=1 target=2 targetCombat=2 targetKind=Player weapon=ashen_short_sword amount=25.160 loc=Arms locMult=0.800 blocked=False blockReduction=0.000 guardBroken=False killed=False outMult=1.344 flat=0.000 armor=0.000 step=1.000 zone=0.900 contactT=0.478 charge=1.000 air=0.000 airScale=-1.000 dist=1.395 px=5.220 py=1.430 pz=4.620
[Emberdelve][Net][server] combat-hit src=Melee attacker=1 attackerCombat=1 target=2 targetCombat=2 targetKind=Player weapon=ashen_short_sword amount=51.892 loc=Head locMult=1.500 blocked=False blockReduction=0.000 guardBroken=False killed=False outMult=1.344 flat=0.000 armor=0.000 step=1.100 zone=0.900 contactT=0.443 charge=1.000 air=0.000 airScale=-1.000 dist=1.395 px=4.988 py=1.931 pz=5.182
[Emberdelve][Net][server] combat-hit src=Melee attacker=1 attackerCombat=1 target=2 targetCombat=2 targetKind=Player weapon=ashen_short_sword amount=25.160 loc=Arms locMult=0.800 blocked=False blockReduction=0.000 guardBroken=False killed=True outMult=1.344 flat=0.000 armor=0.000 step=1.000 zone=0.900 contactT=0.478 charge=1.000 air=0.000 airScale=-1.000 dist=1.395 px=5.220 py=1.430 pz=4.620
```

These five lines are byte-identical to the COMBO run's, i.e. the FIX1 "report the same values the damage used" change did not alter the reported factors, and the amounts remain self-consistent:
`ok: health lost equals the reported amounts: 111.553 < 120 <= 136.713` (sum of the five amounts).

### 4.1 The logged factors do explain the amounts (replay of the gate's own formula)

`analysis/netcombat_amount_replay.py` re-applies `EmberdelveNetCombatGate.cs:315-327` to the run's own `combat-hit` lines, using the pattern multipliers read from the mirror's `.asset` files (`Weapon_AshenShortSword.swingPatterns` → `Pattern_SwordSlashLR` 1, `Pattern_SwordSlashRL` 1, `Pattern_SwordOverhead` 1):

| hit | factors (step / out / loc / zone / reduction) | derived `max(1, base·p·step·out·loc·zone)` → guard | logged amount | diff | tolerance `max(0.1, 0.01·amount)` |
|---|---|---|---|---|---|
| 1 Arms open | 1.000 / 1.344 / 0.800 / 0.900 / — | 25.159680 | 25.160 | 0.000320 | 0.251600 → inside |
| 2 Head blocked | 1.100 / 1.344 / 1.500 / 0.900 / 0.820 | 51.891840 → 9.340531 | 9.341 | 0.000469 | 0.100000 → inside |
| 3 Arms open | as hit 1 | 25.159680 | 25.160 | 0.000320 | 0.251600 → inside |
| 4 Head open | 1.100 / 1.344 / 1.500 / 0.900 / — | 51.891840 | 51.892 | 0.000160 | 0.518920 → inside |
| 5 Arms open | as hit 1 | 25.159680 | 25.160 | 0.000320 | 0.251600 → inside |

`allInsideTolerance: true`. So the runtime data and the server's report are coherent; the gate simply never got a candidate to compare against.

### 4.2 Why the gate sees zero patterns (reproduction for Codex)

Sequence inside one `EmberdelveNetCombatGate.Run` process (line numbers of `gate-netcombat-20261103-152358/Emberdelve.EditorTools.EmberdelveNetCombatGate.Run.log`):

1. **750** — `[Emberdelve][NetCombat] derived sword=ashen_short_sword pattern=sword_slash_lr swing=0.840s reach=2.291 -> interval=1.240 stop=1.375`. The gate has just read `sword.swingPatterns` (`EmberdelveNetCombatGate.cs:128`, `:144-151`) and a **live pattern object** produced the correct timings (`windup 0.24 + active 0.30 + recovery 0.30`) and reach (`(2.3 + 0.6) × 0.79`).
2. **762 → 11657** — the gate builds the process-networking player: `EmberdelveNetCombatGate.cs:159` calls `EmberdelveWindowsBuild.BuildNetProofPlayer`; the log shows `net-proof start …`, `BuildPlayer: start building target 19`, and `[Emberdelve][BuildWindows] net-proof report result=Succeeded totalSize=222896027 totalTime=00:00:40.4455829`. During this build Unity runs its asset-unload passes: **1206/1213/1219/1226** (`Unloading 2 / 137 / 145 / 2105 unused Assets …`), **1570-1581**, **11639**.
3. **12036+** — the melee amount checks run *after* the build, in `MeleeMismatch` (`:315` `foreach (SwingPatternData p in sword.swingPatterns) { if (p == null) continue; considered++; … }`), and report `0 patterns tried` with an empty candidate list.
4. The array itself is still intact: the same method's earlier `sword.HasComboChain` test (`WeaponData.cs:207` → `UsesSweptMelee` → `swingPatterns.Length > 0`) must have been **true**, otherwise hit 2 (`step 1.100`) would have failed with `combo step multiplier 1,100 is not in the weapon's chain` instead of reaching the pattern loop. So `swingPatterns.Length > 0` while **every element fails the `p == null` test** — the classic Unity "fake null": the `WeaponData` wrapper and its managed array survived, but the asset-backed `SwingPatternData` objects it referenced were unloaded/destroyed by the build's cleanup and are no longer live objects in this editor process.
5. Corroborating asymmetry inside the same failing check: the location test at `:304` **passes** after the build because `HitLocationTable.Default` is a `CreateInstance` with `HideFlags.HideAndDontSave` (`HitLocationTable.cs:39-49`), i.e. not an asset, so the build's unload cannot touch it; the ranged session's amount checks (`ok: arrow hit 1: Body amount 37.453 at air 0.411 s (scale 0.834) matches the data-derived value`, log 13012/13054) also pass because they need no pattern objects; plain serialized fields of the same weapon (`weaponId`, `baseDamage 26.000`, `comboSteps`) are still readable.
6. The data on disk is fine: `Weapon_AshenShortSword.asset` (main and mirror) serializes three pattern references (`8fbc6866…`, `f92bd604…`, `b4dfd126…`), and all six `Pattern_*.asset` files in the mirror exist with `damageMultiplier: 1` (`Pattern_SwordOverhead` `impactPowerScale: 1.2`). No `MissingReferenceException`, no `NullReferenceException`, no `error CS` appears anywhere in the gate log.
7. The COMBO run's log has the **same structure** (`Logs/local-testing/COMBO-20261103/gate-netcombat-20261103-050116/…log`: derived at 750 → `net-proof start` 762 → build report 11710 → the same 5 FAILs at 12089-12145), which is the answer to that report's §4.3.a: the old message simply did not print the candidate count.
8. Deterministic: two independent processes, identical 5 lines, identical numbers, `checks=242 failures=5` both times. Not a flake.

**Classification: gate/harness-side defect, not a gameplay or data defect.** `EmberdelveNetCombatGate` captures `CharacterClassData`/`WeaponData` (and through it asset-referenced `SwingPatternData` objects) *before* `BuildNetProofPlayer` and dereferences those objects *after* it. Directions for Codex (none applied by the tester): re-load the class/weapon data after the player build (call `LoadClass` again and re-derive `pattern0`/`interval`/`stop`), or capture the pattern multipliers as plain values before the build, or build the player before reading the data. The server-side amounts themselves are consistent and were not challenged.

## 5. Environment / process notes

### 5.1 `BuildEverything` evidence gap (unchanged behaviour from the COMBO run)
`build-everything-20261103-141112/`: `runner.txt` records `START … pid=28088`; `runner.err.txt` (written 15:11:13 = start + 3601 s) shows the runner's `ReadAllText` throwing because the log file was still held by another process, so **no `summary.json` was produced**. The runner had already reached its `TimeoutSeconds=3600` and force-killed the process **it started** (pid 28088, recorded in `runner.txt`) — no other process was touched. The log was read independently (`log-copy.txt`, 1935 lines): **0 `error CS`, 0 `Exception:`, 0 `] FAIL`, 0 `Aborting batchmode`**, completion marker `[Emberdelve] Vertical slice built.` (line 1651), `[Emberdelve][VendorImport] KayKit assets in place (37 files, copied=False, importers fixed=0)` (already in main→mirror), `[Emberdelve][Props] placed 13 decorative props`, `[Emberdelve][Retro] retro pass present and active on 5 renderer(s)`. A builder prints no `PASS checks=… failures=0` line, so this runner can never mark `BuildEverything` as PASS; its success here is evidenced by the log markers plus every downstream gate that depends on generated assets.

### 5.2 Lockfiles and processes
`DakaDaka_ci/Temp/UnityLockfile` was created by the batch editor of §5.1 and removed by the tester before the next step (required by the runner's lock check); it is absent at the end. `DakaDaka/Temp/UnityLockfile` (the user's open Editor) was never touched. At the end only the user's Editor (PID 29648, started 03.11.2026 3:00:13, title `DakaDaka - Emberdelve_Level01 … 6000.2.5f1 <DX12>`) and Unity Hub/licensing/ILPP helpers are running; no batch Unity process from this run survives.

### 5.3 Runner invocation form
Array binding was verified before spending a Unity run: `analysis/argbind-probe.ps1` run through `Start-Process powershell -Command "& '<runner>' -Method @('a','b') …"` gives `MethodsType=String[] Count=2`; the `-File` form with a comma token binds **one** 33-character string (which the runner's method-name validation would reject). All multi-method batches in this report used the `&` + `@(...)` form.

## 6. Generated asset changes (main vs mirror, after all runs)

Main already contained the generated V2/KayKit assets, the updated `EmberdelveGameConfig.asset` and the renderer assets (Codex ran `BuildEverything` in the user's Editor on main); the mirror received them by robocopy and then ran its own `BuildEverything`.

`analysis/compare_assets.py` (`.meta`, `.cs`, `.csproj`, `.sln`, `.user`, `.suo` and `Library Temp Logs Builds UserSettings obj .git .vs` excluded):

| | files | bytes |
|---|---|---|
| main `Assets/` | 1840 | 1,108,711,296 |
| mirror `Assets/` | 1840 | 1,108,711,301 |
| main-only | 0 | — |
| mirror-only | 0 | — |
| content differs | 7 | — |

The 7 differing files and what actually differs (`analysis/line_multiset_diff.py`, `analysis/asset-diffs.txt`):

| File | Measured |
|---|---|
| `Emberdelve/Art/ThirdParty/KayKit/Materials/KayKit_Dungeon.mat` | 1 line: `_Color` `0.84` (main) vs `0.8399999` (mirror) — float round-off, the known expected material noise |
| `Emberdelve/Prefabs/Visual/EnemyVisual_{Mage,Minion,Rogue,Warrior}.controller` | 48 differing lines each side for Warrior; 25 contain a Unity `fileID`, the other 23 are the `--- !u!NNNN &<fileID>` anchor lines. Counts identical: 11 `AnimatorStateTransition`, 6 `AnimatorState`, 11 `m_ConditionEvent`, 2 empty `m_Conditions`, 0 parameters |
| `Emberdelve/Scenes/Emberdelve_Level01.unity` | 2728 differing lines each side; 1201 contain a `fileID`, 1527 are anchor lines (1201+1527 = 2728, i.e. nothing else differs). Both scenes hold 898 `PrefabInstance` blocks over the same 13 distinct source prefabs with the same multiplicities |
| `Emberdelve/Scenes/Lobby.unity` | 317 differing lines each side; 154 contain a `fileID`, 163 are anchor lines (154+163 = 317) |

So the mirror's `BuildEverything` re-serialized these seven files (internal fileID/order churn plus one float round-off); no authored value changed in the measured sense. `EmberdelveGameConfig.asset`, `Weapon_AshenShortSword.asset`, all `Pattern_*.asset`, all renderer assets and all materials except `KayKit_Dungeon.mat` are byte-identical between main and mirror. **Nothing was copied from the mirror to main.**

## 7. Limits — what this report does NOT claim

- No graphical review, no PNG, no visual-quality acceptance, no subjective visual inspection of any kind (text-only model). All "visual" gate results are structural (asset/prefab/controller/material/shader presence and compile).
- No real-input or full-multiplayer acceptance: every networked session used scripted probes (`melee-duel`, `ranged-shot`, `combat-abuse`, `NetInputSender.DriveFromCombatScript`), no real input device.
- `BuildEverything` is not marked PASS by the runner (no positive summary line exists for a builder); §5.1 states exactly what was and was not recorded.
- Scope was the brief's list only. Not re-run this time (not assigned): `EnemySweepGate`, `ProjectileBlockGate`, `SkillAuthorityGate`, `AttributeSkillGate`, `LootPipelineGate`, `NetAdmissionGate`, `NetMovementGate`, `NetSeamsGate`, `NetProofGate`. Prior COMBO evidence for them is not reused as proof for this run.
- §4.2 attributes the failure to the gate's use of pre-build asset references; the tester did not instrument the gate and does not assert which single line Codex will change. No tolerance was altered.
- No fix, no commit, no source/test/tool/settings change; the user's Editor and main `Temp/UnityLockfile` were untouched.

## 8. Evidence index (`Logs/local-testing/COMBO-R1-20261103/`)

```
git-head.txt / git-status.txt                 main HEAD + 142 status entries
robocopy-1.txt / robocopy-2.txt               mirror sync (exit 1 / exit 0)
source-main-emberdelve.json / source-mirror-emberdelve.json   299 .cs each, identical
source-main-tools.json   / source-mirror-tools.json           12 files each, identical
source-main-controller-scope.json / source-mirror-controller-scope.json / source-main-after-run.json   319 files each
compare-main-vs-mirror.json                   identical
compare-source-drift.json                     identical (no drift during the run)
source-main-final-post-commit.json / source-mirror-final.json   319 files, re-hashed after the commit
compare-source-drift-final.json               identical (tested content == committed content of 91da51d1)
compare-main-vs-mirror-final.json             identical
compare-controller-before-prev-vs-r1.json     FIX1 delta: 11 changed, 0 added, 0 removed
build-everything-20261103-141112/             BuildEverything.log (174,897 B), log-copy.txt, runner.txt, runner.err.txt, no summary.json (§5.1)
gates-visual-20261103-151855/                 RetroVisual PASS 91/0, VisualGate PASS 193/0, summary.json
gates-bladecombat-20261103-152016/            BladeDefense 293/0, CombatFlow 584/0, SweptMelee 674/0, CombatAuthority 51/0, RuntimeSmokeTest.RunCombatFlow 234/0, summary.json
gate-netcombat-20261103-152358/               NetCombat FAIL 242/5, summary.json, runner.txt
gate-netcombat-repeat-20261103-154226/        NetCombat repeat FAIL 242/5, summary.json, runner.txt
gates-netreg-20261103-153526/                 NetSkill 140/0, NetFinal 170/0, summary.json
editmode-20261103-154829/                     editor.log, results.xml, summary.json   PASS 555/0/0/0
assets-main-vs-mirror.json                    1840 vs 1840 files, 7 content-differing
analysis/snapshot_against_controller.py       hashes the controller's 319-file scope in a given checkout
analysis/argbind-probe.ps1                    proves the `&` + @() array binding
analysis/netcombat_amount_replay.py / _out.txt   §4.1 replay (allInsideTolerance true)
analysis/editmode_suites.py / editmode_suites_diff.txt   fixture counts + diff vs the COMBO run
analysis/compare_assets.py                    main/mirror Assets scan
analysis/line_multiset_diff.py                §6 line-multiset characterization
analysis/asset-diffs.txt, scene-level01-diff.txt
*.cmd.txt / *.pid                             exact runner command lines and the runner PIDs started
```

Mirror scenario logs (not copied to main): `DakaDaka_ci/Builds/net-combat/20261103-152418/logs/` (first run) and the matching directory of the repeat run — `melee_server.log`, `melee_A/B.log`, `ranged_*.log`, `abuse_*.log`, `enemy_*.log`.

## 9. Commands used (for reproduction)

```powershell
$main='C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka'; $ci='C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_ci'
$ev="$main/Logs/local-testing/COMBO-R1-20261103"

robocopy $main $ci /MIR /XD Library Temp Logs Builds UserSettings obj .git .vs /XF *.csproj *.sln *.user *.suo /NFL /NDL /NJH /NJS /NP /R:1 /W:1
robocopy "$main\Logs\v2_assets" "$ci\Logs\v2_assets" /MIR /NFL /NDL /NJH /NJS /NP /R:1 /W:1

# every Unity step was launched detached and polled, with the runner called through & so -Method @(...) binds:
$cmd = "& '$main\Tools\Run-EmberdelveGates.ps1' -ProjectPath '$ci' -Method @('<A>','<B>') -TimeoutSeconds <n> -OutputDirectory '<dir>'"
Start-Process powershell -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-Command',$cmd -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput '<dir>/runner.txt' -RedirectStandardError '<dir>/runner.err.txt'

# 1  -Method 'Emberdelve.EditorTools.EmberdelveBootstrapMenu.BuildEverything' -TimeoutSeconds 3600  -> build-everything-20261103-141112
# 2  -Method @('…EmberdelveRetroVisualGate.Run','…EmberdelveVisualGate.Run')                          -TimeoutSeconds 1200 -> gates-visual-20261103-151855
# 2  -Method @('…EmberdelveBladeDefenseGate.Run','…EmberdelveCombatFlowGate.Run','…EmberdelveSweptMeleeGate.Run',
#              '…EmberdelveCombatAuthorityGate.Run','…EmberdelveRuntimeSmokeTest.RunCombatFlow')      -TimeoutSeconds 1200 -> gates-bladecombat-20261103-152016
# 3  -Method '…EmberdelveNetCombatGate.Run' -TimeoutSeconds 2400                                      -> gate-netcombat-20261103-152358
# 3  -Method @('…EmberdelveNetSkillGate.Run','…EmberdelveNetFinalGate.Run') -TimeoutSeconds 3300     -> gates-netreg-20261103-153526
# 3  repeat of the failing gate                                                                      -> gate-netcombat-repeat-20261103-154226
# 4  & '…\Tools\Run-EmberdelveEditModeTests.ps1' -ProjectPath $ci -TestFilter Emberdelve -MinimumTests 555 `
#      -TimeoutSeconds 900 -OutputDirectory "$ev\editmode-20261103-154829"

python Tools/LocalTesting/summarize_results.py <each summary.json>
python "$ev/analysis/netcombat_amount_replay.py" "$ci/Builds/net-combat/20261103-152418/logs/melee_server.log" $ci
```
