# LOCAL_COMBO-20261103 — local tester report (COMBO: U3B3 + U3C1 + V2)

Role: local tester only. Compiled and ran the assigned checks, preserved evidence, reported defects.
No source, test, tool, settings or project-file fix was attempted. Nothing was committed.
The user's Unity Editor (PID 29648, main checkout) was never touched; all Unity batch work ran in the mirror.

## 1. Identity

| Item | Value |
|---|---|
| Main checkout | `C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka` |
| Main HEAD | `5311bb6f7762a86e90f922827aa6e2b99da0898e` |
| Main `git status --porcelain` entries | 105 (matches the brief's uncommitted U3B3/U3C1/V2 change set) |
| Mirror checkout | `C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_ci` |
| Mirror sync | `robocopy <main> <ci> /MIR /XD Library Temp Logs Builds UserSettings obj .git .vs /XF *.csproj *.sln ...` (+ `Logs/v2_assets`), exits 1 (< 8, acceptable) |
| Source snapshot (main vs mirror) | 319 source files, byte-identical (`source-main.json`, `source-mirror.json`) |
| Evidence root | `Logs/local-testing/COMBO-20261103/` |
| Main `Temp/UnityLockfile` | present, untouched (editor open) |
| Mirror `Temp/UnityLockfile` | created by BuildEverything, removed by the tester so later gates could run |

## 2. Verdict

**NOT CLEAN.** 3 gate failures across 3 gates (RetroVisual 1, BladeDefense 1, NetCombat 9). Everything else passed:
full EditMode suite, both visual gates' shader/asset checks, CombatFlow, all 8 combat regressions, all 6 network regressions.

No defect was found in the shipped gameplay rules themselves by the read-only review (§6); the failures below are
gate-side expectation defects plus one serialization gap, with one failure (§4.3) that the recorded evidence cannot reproduce.

## 3. Runs (all on the mirror)

| # | Step | Evidence dir | Result |
|---|---|---|---|
| 1 | EditMode tests (`-TestFilter Emberdelve -MinimumTests 487`) | `editmode-20261103-040406` | **PASS** tests=555 failures=0 skipped=0 inconclusive=0 (151.9 s) |
| 2 | `EmberdelveBootstrapMenu.BuildEverything` | `build-everything-20261103-040758` | ran; runner could not read the log (file held by the Unity process) → **no `summary.json`** (see §5.1) |
| 3 | `EmberdelveRetroVisualGate.Run` | `gates-visual-20261103-045425` | **FAIL** checks=90 failures=1 |
| 3 | `EmberdelveVisualGate.Run` | `gates-visual-20261103-045425` | **PASS** checks=193 failures=0 |
| 4 | `EmberdelveBladeDefenseGate.Run` | `gates-bladecombat-20261103-045601` | **FAIL** checks=279 failures=1 |
| 4 | `EmberdelveCombatFlowGate.Run` | `gates-bladecombat-20261103-045601` | **PASS** checks=584 failures=0 |
| 4 | `EmberdelveNetCombatGate.Run` | `gate-netcombat-20261103-050116` | **FAIL** checks=237 failures=9 (298 s) |
| 4 | `EmberdelveNetCombatGate.Run` (repeat) | `gate-netcombat-repeat-20261103-055553` | **FAIL** checks=237 failures=9 — identical 9 lines, identical numbers (130 s) |
| 5 | `SweptMeleeGate` 674/0 · `EnemySweepGate` 108/0 · `CombatAuthorityGate` 51/0 · `ProjectileBlockGate` 51/0 · `SkillAuthorityGate` 161/0 · `AttributeSkillGate` 1121/0 (authorityAccepted=64, authorityRejected=0) · `LootPipelineGate` 9954/0 · `RuntimeSmokeTest.RunCombatFlow` 234/0 | `gates-combatreg-20261103-052228` | **PASS** (all 8) |
| 5 | `NetSkillGate` 140/0 · `NetAdmissionGate` 109/0 · `NetMovementGate` 173/0 · `NetSeamsGate` 163/0 · `NetProofGate` 35/0 · `NetFinalGate` 170/0 | `gates-netreg-20261103-055040` | **PASS** (all 6) |

EditMode compile: no `error CS`. Warnings (2 distinct):
- `Assets/Emberdelve/Editor/EmberdelveLootRollerGate.cs(29,19): warning CS0168: The variable 'c' is declared but never used`
- `Assets/SharedAssets/Benchmark/PerformanceTest.cs(68,13): warning CS0618: 'EventSystem.SetUITookitEventSystemOverride(...)' is obsolete`

New U3B3/U3C1 fixtures present and green: `BladeDefenseRulesTests` 23, `CombatIntentGateTests` 5,
`CombatNetScriptAndArgsTests` 16, `CombatScriptParamsTests` 15, `CombatVitalsDataTests` 4, `ServerDrawTimerTests` 5.
Unity licensing noise (`[Licensing::Client] Error: Code 404 ...`, 88 lines) appears in the logs and did not fail any runner.

## 4. Defects

### 4.1 `EmberdelveRetroVisualGate.Run` — 1 failure: forbidden-token false positive on `OnDestroy()`

```
[Emberdelve][RetroVisual] FAIL: EnemyVisualDriver is presentation only: no damage, status, stun, movement or physics calls Destroy( (line 78)
```

Line 78 of `Assets/Emberdelve/Runtime/AI/EnemyVisualDriver.cs` is `private void OnDestroy()` — the gate's forbidden
token `Destroy(` matches the lifecycle method name. Independent read-only inspection of the whole file found no
gameplay call: the only `HealthComponent` use is event subscribe/unsubscribe (L75 `+= HandleDamaged`, L80 `-= HandleDamaged`),
plus `animator.applyRootMotion = false` (L62) and animator parameter writes (L118, L150). No damage application, no
movement, no physics, no `Destroy(...)` call, no timer write. `EmberdelveVisualGate.Run` (193 checks) passed the same file.
Classification: **gate-side test defect**, not a V2 gameplay defect.

### 4.2 `EmberdelveBladeDefenseGate.Run` — 1 failure: guard-break path not modelled by the check

```
[Emberdelve][BladeDefense] FAIL blade: held with the kit's reduction (expected 0,7000, got 0,2100)
```

Section totals: `data` 25/0 · `warning equals sweep` 110/0 · `evasion` 20/0 · **`front, back, height` 24/1** ·
`endurance (empty pool)` 11/0 · `damage through the resolver` 18/0 · `shields never parry` 16/0 · `wire` 55/0.

Mechanism (all values from the shipped data, no tuning invented):
- `EmberdelveBladeDefenseGate.cs:366-368` builds a frontal mid slash from `_sword` at bearing 0 and asserts
  `Near(kit.BlockReduction, ahead.Reduction, 1e-5f, "… held with the kit's reduction")` — i.e. it expects the full kit reduction.
- `EmberdelveBladeDefenseGate.cs:328-329` feeds `ImpactPower = attacker.impactPower * pattern.impactPowerScale` =
  `Weapon_AshenShortSword.impactPower 10 × Pattern_SwordSlashLR.impactPowerScale 1 = 10` and `ImpactResist = kit.ImpactResist`
  (blade kit `impactResist: 8`).
- `BladeDefenseRules.cs:139-144` `IsGuardBroken(10, 8)` → `10 >= 8` → true, so `Resolve` (L169-172) returns
  `Broken` with `reduction × BreakReductionScale` = `0.7 × 0.3 = 0.21` (`EmberdelveGameConfig.asset` serializes
  `guardBreakReductionScale: 0.3`).
- The same gate models this correctly elsewhere: `EmberdelveBladeDefenseGate.cs:632-635` computes
  `expectedReduction = BlockReduction × clamp01(guardBreakReductionScale)` when the guard breaks. Line 368 does not.
- The `shield` variant of the same check passes because the shield kit's resist is above the sword's impact power
  (the gate asserts that at line 600).

Classification: **gate expectation inconsistent with shipped tuning** (sword 10 vs blade-kit resist 8). Either the check
at line 368 must apply the break scale (as line 634 does), or the tuning must make a sword-vs-sword guard hold rather
than break. Product decision needed by the controller; the tester changed nothing.

### 4.3 `EmberdelveNetCombatGate.Run` — 9 failures, deterministic; 5 of them are not reproducible from the recorded evidence

Both runs (`gate-netcombat-20261103-050116`, `gate-netcombat-repeat-20261103-055553`) produced the identical 9 lines:

```
FAIL: hit 1: Arms open amount 25.160 matches the data-derived value (no pattern of the weapon explains 25.160 (base 26.000, step 1.000, out 1.344, loc 0.800, zone 0.900, reduction 0.000, armor 0.000))
FAIL: hit 2: Head BLOCKED amount 9.341 matches the data-derived value (no pattern of the weapon explains 9.341 (base 26.000, step 1.100, out 1.344, loc 1.500, zone 0.900, reduction 0.820, armor 0.000))
FAIL: hit 3: Arms open amount 25.160 ... (same factors as hit 1)
FAIL: hit 4: Head open amount 51.892 ... (base 26.000, step 1.100, out 1.344, loc 1.500, zone 0.900, reduction 0.000, armor 0.000)
FAIL: hit 5: Arms open amount 25.160 ... (same factors as hit 1)
FAIL: B saw every hit on itself as a server report
FAIL: A saw every hit it caused as a server report (no local resolution)
FAIL: no hit or swing happened (the abuser's guard toggles hurt nobody)
FAIL: traffic after the disconnect decision was dropped unseen (ignored=0)
```

Raw scenario logs (mirror): first run `DakaDaka_ci/Builds/net-combat/20261103-050126/logs/`, repeat `.../20261103-055604/logs/`.

**4.3.a — the 5 melee amount checks contradict the gate's own formula.** Replaying the gate's own regex
(`EmberdelveNetCombatGate.cs:719-737`) and the gate's own derivation (`EmberdelveNetCombatGate.cs:312-319`) against the
run's own `combat-hit` lines, with the pattern multipliers that are actually on disk, explains every logged amount
(script + output saved: `analysis/netcombat_replay.py`, `analysis/netcombat_replay_out.txt`):

| hit | logged factors | derived `max(1, base·p·step·out·loc·zone)` | logged amount | diff | gate tolerance `max(0.1, 0.01·amount)` |
|---|---|---|---|---|---|
| 1 Arms open | step 1.000 out 1.344 loc 0.800 zone 0.900 red 0.000 armor 0.000 flat 0.000 | 25.159680 | 25.160 | 0.000320 | 0.251600 → inside |
| 2 Head blocked | step 1.100 out 1.344 loc 1.500 zone 0.900 red 0.820 | 9.340531 | 9.341 | 0.000469 | 0.100000 → inside |
| 4 Head open | step 1.100 out 1.344 loc 1.500 zone 0.900 | 51.891840 | 51.892 | 0.000160 | 0.518920 → inside |

`p` = `SwingPatternData.damageMultiplier`, the only factor the FAIL message does not print. The mirror's data pins it to 1:
`Weapon_AshenShortSword.asset` → `swingPatterns` = `Pattern_SwordSlashLR` (guid 8fbc6866…), `Pattern_SwordSlashRL` (f92bd604…),
`Pattern_SwordOverhead` (b4dfd126…), each serialized `damageMultiplier: 1`; all six `Pattern_*.asset` files in the mirror are `damageMultiplier: 1`.
Two gates that PASS in this same run set assert exactly that on the same asset loaded the same way:
`EmberdelveSweptMeleeGate.cs:442` (`Mathf.Approximately(p.damageMultiplier, 1f)`, 674/0) and
`EmberdelveCombatFlowGate.cs:451-453` (`Near(1f, p.damageMultiplier, 1e-6f)`, 584/0).
The gate's own runtime view of the weapon is otherwise consistent with the asset: its ok-line
`derived sword=ashen_short_sword pattern=sword_slash_lr swing=0.840s reach=2.291` reproduces
`windup 0.24 + active 0.3 + recovery 0.3` and `EffectiveReach = (reach 2.3 + hitSphereRadius 0.6) × reachScale 0.79 = 2.291`
(`SwingPatternData.cs:147,160-161`). The gate's `warrior.primaryWeapon` resolves to `Weapon_AshenShortSword.asset`
(only two `CharacterClassData` assets exist: `Class_Warrior`, `Class_Archer`).
The other melee checks in the same session passed, including the health-sum check
`111.553 < 120 <= 136.713` (sum of the five logged amounts = 136.713), so the amounts themselves are coherent.

Classification: **the failure cannot be reproduced from the recorded data.** It is deterministic across two processes, so it is
not a flake. The only unprinted factor in the gate's message is `p.damageMultiplier`; the gate needs instrumentation
inside the `foreach (SwingPatternData p in sword.swingPatterns)` loop (log `p.patternId`, `p.damageMultiplier` and each
candidate) before this can be attributed. The tester did not add it. Alternative reading for the controller: the derivation
should be expressed the way `EmberdelveRuntimeSmokeTest` does it (base × outgoing × `comboSteps[step].damageMultiplier` ×
location multiplier × zone), which does not read the pattern's own multiplier at all.

**4.3.b — the 2 client-report checks fail because the client line labels the source with the raw wire byte.**
Gate expectation (`EmberdelveNetCombatGate.cs:285-286`): `[client] server-hit src=Melee attacker=1 target=2`.
Actual client lines (both runs): `server-hit src=0 attacker=1 target=2 enemy=False amount=25.160 loc=3 blocked=False …`.
Cause: `CombatHitWire.Source` is a `byte` (`NetCombatWire.cs:171-194`, sent as `Source = (byte)r.Source`, `NetCombatServer.cs:456-463`)
while the server's own line prints the enum name (`NetCombatServer.cs:443` `combat-hit src=Melee …`), and
`NetCombatFx.OnHit` prints the byte verbatim (`Assets/Emberdelve/Net/NetCombatFx.cs:103` `src=" + hit.Source`).
`loc` shows the same asymmetry (`loc=3` on the client vs `loc=Arms` on the server). All numeric fields the client reported
match the server (amount/loc/blocked/guardBroken/killed), and the neighbouring checks "B (observer) was told every swing of A (7)",
"clients logged no server-side hit line", "no client applied damage", `clientBlockedDamage=0` all passed.
Classification: **log-formatting inconsistency between server line and client report line** (presentation only);
either `NetCombatFx` maps the byte back to the same word the server prints, or the gate matches the byte.

**4.3.c — abuse session: the gate's "no hit or swing happened" contradicts its own scripted probe.**
Gate assertion (`EmberdelveNetCombatGate.cs:553`): `combat-hit count == 0 && combat-swing count == 0`.
`abuse_server.log` contains `combat-swing id=2 weapon=ashen_short_sword pattern=sword_slash_lr step=0 … committed=0`,
preceded by `combat-intent client=2 id=2 kind=AttackPress seq=1 outcome=Accepted` — i.e. the scenario's own
"1 valid" intent (U3B3.md §Scenarios: B "sends 1 valid, 1 replay, 1 malformed kind (99) and 40 guard toggles") was
accepted and executed on B's own rig, producing a swing with no target in range (no `combat-hit`).
Classification: **gate expectation inconsistent with its own scenario**. The identity/replay/invalid/flood/ledger/vitals
half of the abuse session all passed (`identity=3`, `reject=Identity client=2 … fault=not-your-object targetNet=1 count=3`,
`A's health picture never changed (1 vitals lines)`, `nothing was ever charged to A`).

**4.3.d — abuse session: `ignored=0` after the disconnect decision.**
Gate assertion (`EmberdelveNetCombatGate.cs:554-555`): `skill-conn final … ignored >= 1`.
Actual: `skill-conn final client=2 slot=abu-b accepted=0 invalid=0 replay=0 flood=0 cast=0 refused=0 rejects=16 identity=3 excess=8 ignored=0 strikes=0`
and `combat-conn final client=2 id=2 accepted=10 refused=0 gateAccepted=10 invalid=1 replay=1 flood=11` (no `ignored` field logged there).
No `combat-intent`/`combat reject` line appears after `skill-abuse disconnect client=2 …` in either run, so no post-decision
traffic was observed by the server to count. Classification: **either the scripted client sends nothing after the decision
(scenario gap) or the ignore counter is not incremented on the combat-intent path** — needs a Codex read of the drop path;
the tester changed nothing. B's own exit behaviour was correct (`B exited by itself with code UnexpectedDisconnect`,
`B was told 'skill abuse'`, `A stayed connected`).

### 4.5 Serialization gap: the U3C1 tuning fields are not in the committed `EmberdelveGameConfig.asset`

`git diff --no-index` between main and the mirror after BuildEverything:

```
--- a/Assets/Emberdelve/Data/EmberdelveGameConfig.asset
+++ b/Assets/Emberdelve/Data/EmberdelveGameConfig.asset
@@ -37,2 +37,15 @@
     swingTurnCeilingDegPerSec: 720
+    horizontalHeight: 2
+    overheadHeight: 3
+    thrustHeight: 2
+    bladeHeightTolerance: 1
+    shieldHeightTolerance: 2
+    guardHighPitchDegrees: 25
+    guardLowPitchDegrees: 25
+    guardEnduranceMax: 0
+    guardEnduranceRegenPerSecond: 20
+    guardEnduranceRegenDelay: 1
+    guardBlockCostBase: 10
+    guardBlockCostPerImpact: 0.5
+    commitSwingAim: 0
```

Main's committed **and** working-tree copy contain neither these 14 fields nor `guardBreakReductionScale`'s neighbours
(`guardBreakReductionScale: 0.3`, `guardStaggerSeconds: 0.8`, `guardStaggerMoveMultiplier: 0.6`, `swingTurnCeilingDegPerSec: 720`
are present). The mirror's copy gained the 14 fields because `BuildEverything` re-serialized it. Values written match the
C# initializers in `CombatFlowTuning.cs` (`bladeHeightTolerance 1`, `shieldHeightTolerance 2`, `guardHigh/LowPitchDegrees`
from `BladeGuardRules.DefaultHigh/LowPitchDegrees`, `guardEnduranceMax 0`, regen 20, delay 1, costBase 10, perImpact 0.5,
`commitSwingAim false`, `horizontalHeight = BladeHeight.Mid` → 2, `overheadHeight = BladeHeight.High` → 3, `thrustHeight = Mid` → 2),
so no behavioural difference was observed in this run set — but U3C.md risk #3 is confirmed as a real gap in the committed asset:
in any checkout that has not run `BuildEverything`, those fields resolve through field initializers rather than serialized data.
Note that every gate in §3 ran against the mirror's regenerated asset, not main's.

## 5. Environment / process notes

### 5.1 BuildEverything evidence gap
`build-everything-20261103-040758/Emberdelve.EditorTools.EmberdelveBootstrapMenu.BuildEverything.log` (447,694 B) exists and
contains the generation/import activity, but the runner could not read the log because the Unity process still held the file,
so **no `summary.json` was produced for that step** and the runner reported it as unreadable rather than PASS/FAIL.
An empty second directory `build-everything-20261103-045003` was created by a re-invocation that produced no log.
The log tail is `[Licensing::Client] Error: Code 404 …` noise; no `error CS`, no exception, no abort marker was found in it.

### 5.2 Shader environment
`EmberdelveRetroVisualGate` reported `retro shader loads as Emberdelve/RetroFullscreen` and `retro shader compiles without errors`.
Environment warning present in the same log: `Shader Hidden/ProbeVolume/VoxelizeScene is not supported: GPU does not support conservative rasterization`
(engine sample shader, unrelated to the project's retro shader).

### 5.3 Generated assets are mirror-only (do not copy to main)
Final main-vs-mirror asset scan (`.meta`, `.cs`, `.csproj`, `.sln` excluded; `Library Temp Logs Builds UserSettings obj .git .vs` excluded):

| | files | bytes |
|---|---|---|
| main `Assets/` | 1789 | 1,017,872,609 |
| mirror `Assets/` | 1840 | 1,108,711,283 |
| mirror-only | 51 | — |
| main-only | 0 | — |
| content differs | 17 | — |

Mirror-only (51): `Art/ThirdParty/KayKit/**` (26 Dungeon `.fbx` + `texture/dungeon_texture.png` + 9 Skeleton `.fbx` +
`skeleton_texture.png` + 2 `LICENSE.txt` + `Materials/KayKit_*.mat` ×3 + `manifest.json`),
`Prefabs/Visual/EnemyVisual_{Mage,Minion,Rogue,Warrior}.{prefab,controller}` (8),
`Resources/Emberdelve/ChestVisualSet.asset`, `Art/Materials/Retro_Fullscreen.mat`.
Content-differing (17): `EmberdelveGameConfig.asset` (§4.5), 8 `Art/Materials/*.mat`
(`Blade_Metal`, `Grip_Leather`, `Lobby_StoneFloor`, `Stone_Ceiling`, `Stone_Floor`, `Stone_Wall`, `Torch_Flame`, `Wood_Planks`),
`Scenes/Emberdelve_Level01.unity`, `Scenes/Lobby.unity`, `Settings/Emberdelve_Dungeon_Volume.asset`,
`Scenes/Cockpit/Settings/Cockpit_Renderer.asset`, `Settings/PC/PC_High_Renderer.asset`, `Settings/PC/PC_High_ScreenRenderer.asset`,
`Settings/PC/PC_Low_Renderer.asset`, `Settings/PC/PC_Low_ScreenRenderer.asset`.
Immediately after BuildEverything the generated groups were: `Art/ThirdParty/KayKit` 91 files / 90,979,985 B,
`Prefabs/Visual` 16 files / 398,552 B, `Resources/Emberdelve` 2 files / 1,265 B, `Art/Materials` 58 files / 112,547 B.
These are the product of a mirror-only `BuildEverything`; they are listed, not promoted.

## 6. Read-only review (no fixes, code read against the task briefs)

**U3B3 — identity before state on `CombatIntentRpc`, shared reject ledger, no client-side damage, host not double-presenting**
- Gate-verified in `gate-netcombat-20261103-050116` (all ok): `the server checks identity, then the connection's gate, then executes (in that order)`;
  `rejects are charged to the shared ledger of NetSkillServer`; `the forged intents were charged to the SENDER (B), through the shared ledger (log tag 'combat')`;
  `nothing was ever charged to A`; `A (the victim) received no intent processing at all`; `A's health picture never changed (1 vitals lines)`.
- No client-side damage: static checks `NetCombatSender.cs / NetCombatFx.cs / NetEnemyViews.cs / NetOwnerCombatView.cs never applies damage or talks to the combat authority`,
  `HealthComponent refuses damage in a client-only process`, plus runtime `no client applied damage` and both clients'
  `combat-sender final … clientBlockedDamage=0`.
- Host not double-presenting: the six client-side RPC handlers in `NetPlayerRig.cs` (L341, 349, 357, 365, 373, 381) all begin
  `if (!NetRole.IsClientOnly) return;`, matching U3B3.md L24 ("handlers act only in a client-only process, so the host never double-presents");
  the gate additionally verified `clients logged no server-side hit line` and that the hostile probe is inside the development guard
  while `CombatSwingRpc`/`EnemyVitalsRpc` exist outside it.
- Server-authoritative amounts confirmed from raw logs: the five `combat-hit` lines carry the full factor set
  (`locMult`, `outMult`, `flat`, `armor`, `step`, `zone`, `blockReduction`) and the victim's health sum matches them (§4.3.a table).
- Caveat: the client-side report line's `src`/`loc` labels are raw wire bytes (§4.3.b).

**U3C1 — single-source warning == sweep, shields never parry**
- `warning equals sweep` section: 110 checks, 0 failures (`gates-bladecombat-20261103-045601`); `data` 25/0 also pins
  authored kind == kind measured from the pattern keys.
- `shields never parry` section: 16 checks, 0 failures; `BladeDefenseRules.cs:153-154` is the single written-down rule
  (`CanParry(hasShield, capability) => !hasShield && capability`), `DefenseProfile` strips the Parry bit for a shield kit,
  and `GuardVerdict` (L62) has no parry/riposte member at all.
- `EmberdelveCombatFlowGate.Run` 584/0 and `EmberdelveSweptMeleeGate.Run` 674/0 corroborate the same data from another direction.
- The one BladeDefense failure (§4.2) is in `front, back, height`, not in the warning/sweep or parry sections.

**V2 — `EnemyVisualDriver` writes no gameplay state**
- Read-only inspection: presentation only. Subscribes to `HealthComponent.Damaged` (L75) and unsubscribes in `OnDestroy` (L78-80);
  `animator.applyRootMotion = false` (L62) so a clip cannot move the root the agent owns; locomotion reads measured ground speed (L118);
  `HandleDamaged` (L150) only reacts. No damage/movement/physics/timer write. See §4.1 for the gate's false positive.
- `EmberdelveVisualGate.Run` 193/0 covers the generated prefabs/controllers/materials path.

**Product decision respected:** guard endurance ships OFF (`guardEnduranceMax = 0` in `CombatFlowTuning` and in the mirror's
regenerated `EmberdelveGameConfig.asset`); the empty-pool rules were exercised only through the gate's enabling-tuning section
(`endurance (empty pool)` 11 checks, 0 failures). No endurance-on claim is made.

## 7. Limits — what this report does NOT claim

- No graphical review and no ReviewV2 PNG visual-quality acceptance: the model is text-only, no PNG was opened or judged.
  Gate results about visuals are structural (asset/prefab/controller/material/shader presence and compile) only.
- No subjective visual inspection claim of any kind.
- No real-input or full-multiplayer acceptance: every networked session used scripted clients
  (`melee-duel`, `ranged-shot`, `combat-abuse` scripts, `NetInputSender.DriveFromCombatScript`), no real input device.
- No claim that the mirror's generated assets (§5.3) exist in, or were validated in, the user's main checkout.
- No fix, no commit, no source/test/tool/settings change; the user's editor and main `Temp/UnityLockfile` were untouched.
- §4.3.a is reported as an unreproducible-from-evidence gate failure; the tester does not assert which side (gate or runtime data) is wrong.

## 8. Evidence index (`Logs/local-testing/COMBO-20261103/`)

```
git-status.txt                       main git status --porcelain (105 entries)
source-main.json / source-mirror.json  319 source files, identical
editmode-20261103-040406/            editor.log, results.xml, summary.json        PASS 555/0
build-everything-20261103-040758/    BuildEverything.log (no summary.json, §5.1)
build-everything-20261103-045003/    empty (§5.1)
gates-visual-20261103-045425/        RetroVisual (FAIL 90/1), VisualGate (PASS 193/0), summary.json
gates-bladecombat-20261103-045601/   BladeDefense (FAIL 279/1), CombatFlow (PASS 584/0), summary.json
gate-netcombat-20261103-050116/      NetCombat (FAIL 237/9), summary.json
gate-netcombat-repeat-20261103-055553/ NetCombat repeat (FAIL 237/9), runner.txt, summary.json
gates-combatreg-20261103-052228/     8 combat regressions, all PASS, summary.json
gates-netreg-20261103-055040/        6 network regressions, all PASS, runner.txt, summary.json
analysis/netcombat_replay.py         replay of the gate's own regex + formula
analysis/netcombat_replay_out.txt    replay output (§4.3.a table source)
```

Mirror scenario logs (not copied to main): `DakaDaka_ci/Builds/net-combat/20261103-050126/logs/` (first run) and
`DakaDaka_ci/Builds/net-combat/20261103-055604/logs/` (repeat) — `melee_server.log`, `abuse_server.log`, `melee_A/B.log`,
`abuse_A/B.log`, ranged and enemy session logs.

## 9. Commands used (for reproduction)

```powershell
# mirror refresh
robocopy <main> <ci> /MIR /XD Library Temp Logs Builds UserSettings obj .git .vs /XF *.csproj *.sln *.user *.suo
robocopy "<main>\Logs\v2_assets" "<ci>\Logs\v2_assets" /MIR

# 1. EditMode
Tools\Run-EmberdelveEditModeTests.ps1 -ProjectPath <ci> -TestFilter Emberdelve -MinimumTests 487 `
  -TimeoutSeconds 900 -OutputDirectory <main>\Logs\local-testing\COMBO-20261103\editmode-20261103-040406

# 2. BuildEverything
Tools\Run-EmberdelveGates.ps1 -ProjectPath <ci> -Method 'Emberdelve.EditorTools.EmberdelveBootstrapMenu.BuildEverything' `
  -OutputDirectory <main>\Logs\local-testing\COMBO-20261103\build-everything-20261103-040758

# 3. visual gates
Tools\Run-EmberdelveGates.ps1 -ProjectPath <ci> -Method @('Emberdelve.EditorTools.EmberdelveRetroVisualGate.Run','Emberdelve.EditorTools.EmberdelveVisualGate.Run') `
  -OutputDirectory <main>\Logs\local-testing\COMBO-20261103\gates-visual-20261103-045425

# 4. new combat gates
Tools\Run-EmberdelveGates.ps1 -ProjectPath <ci> -Method @('Emberdelve.EditorTools.EmberdelveBladeDefenseGate.Run','Emberdelve.EditorTools.EmberdelveCombatFlowGate.Run') `
  -OutputDirectory <main>\Logs\local-testing\COMBO-20261103\gates-bladecombat-20261103-045601
Tools\Run-EmberdelveGates.ps1 -ProjectPath <ci> -Method 'Emberdelve.EditorTools.EmberdelveNetCombatGate.Run' `
  -OutputDirectory <main>\Logs\local-testing\COMBO-20261103\gate-netcombat-20261103-050116

# 5. combat regressions
Tools\Run-EmberdelveGates.ps1 -ProjectPath <ci> -Method @('…SweptMeleeGate.Run','…EnemySweepGate.Run','…CombatAuthorityGate.Run',
  '…ProjectileBlockGate.Run','…SkillAuthorityGate.Run','…AttributeSkillGate.Run','…LootPipelineGate.Run','…RuntimeSmokeTest.RunCombatFlow') `
  -OutputDirectory <main>\Logs\local-testing\COMBO-20261103\gates-combatreg-20261103-052228

# 5. network regressions
Tools\Run-EmberdelveGates.ps1 -ProjectPath <ci> -Method @('…NetSkillGate.Run','…NetAdmissionGate.Run','…NetMovementGate.Run',
  '…NetSeamsGate.Run','…NetProofGate.Run','…NetFinalGate.Run') -TimeoutSeconds 3300 `
  -OutputDirectory <main>\Logs\local-testing\COMBO-20261103\gates-netreg-20261103-055040

# 4. repeat of the failing new gate
Tools\Run-EmberdelveGates.ps1 -ProjectPath <ci> -Method 'Emberdelve.EditorTools.EmberdelveNetCombatGate.Run' -TimeoutSeconds 1500 `
  -OutputDirectory <main>\Logs\local-testing\COMBO-20261103\gate-netcombat-repeat-20261103-055553
```
