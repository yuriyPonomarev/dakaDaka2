# LOCAL INVENTORY PANEL — 20261102

Focused **headless + rendered** verification of Codex's uncommitted satchel-background fix and of the
updated `ReviewD4c1` probe. Local tester only: no game/test/`Tools/`/`ProjectSettings/`/asset edit, no
commit, no controller resume, no engine/package/model change, no builder executed. Codex owns every fix.

## 0 Verdict

| Item | Result |
|---|---|
| Independent review of the live diff vs `Logs/inventory_header_draft/before.cs` | **as described by the brief** — exactly **4 added lines**, one statement + 3 comment lines; no existing line changed, no constant changed, no assertion touched |
| Headless `EmberdelveRuntimeSmokeTest.RunInventoryUiFlow` | **PASS** `checks=485 failures=0 exit=0` (60.72 s) |
| Headless `EmberdelveCloakSlotGate.Run` | **PASS** `checks=10167 failures=0` (19.67 s) |
| Graphical `ReviewD4c1.Prepare` (real Play, saved Level01) | **PASS** `prepared=True frame=1 gameTime=0` (4th attempt) |
| Graphical `Measure` @ **1280x720**, bag **10x6** | **PASS** `pass=True failures=[] 14/14/13 opening 1x == expected 1x`, 41/41 rows all-True, **panelHeight = 504** |
| `SmallBag` → `Measure` @ **1280x720**, bag **10x4** | **PASS** same invariants, **panelHeight = 430** (base size restored) |
| `TallBag` → `Measure` @ **1280x720**, bag **10x6** | **PASS** same invariants, **panelHeight = 504** again |
| Header + both tabs inside the opaque panel (new probe checks) | **PASS** at 10x6 and 10x4; `Header`, `TabBag`, `TabCharacter` containment failures: **none**; panel `Image.enabled=True`, `color.a = 1.000` |
| Panel centre / content coordinates preserved | **PASS** `anchoredPosition = (-40.00, 66.00)` identical in all three probes; 10x6 stat-row screen positions byte-identical before and after the 10x4 round-trip |
| Real frame progress between calls | **PASS** `Prepare frame 1` → `Measure 37130` → `SmallBag 60289` → `Measure 66378` → `TallBag 78569` → `Measure 87731`; independent probes at frames 11601 / 60854 / 79136 |
| Duplicate measurement rows | **NONE** — 41 rows, 41 unique names, in every `Measure` |
| Real viewport captured + hashed | `capture-1280x720-10x6.png`, `capture-1280x720-10x4.png`, `capture-1280x720-10x6-return.png` — all IHDR **1280x720** |
| Source guard | **NO DRIFT** — 269 guarded sources identical before/after, and identical to the controller's `source-before.json` |
| Local stance | **PASS for the inventory panel fix and the updated D4c1 probe.** No defect found. Two non-blocking observations for Codex (§8 items 2 and 3). |

## 1 Source identity

- HEAD `4a338d6911f130fc0ba3fbcd306d6fb8f999424d` — *"Complete frozen DEX draw timing and resilient local
  verification"* (2026-11-01T04:56:35+01:00). **Unchanged.** No commit made by me.
- Guarded source snapshot (`snapshot-sources.ps1`, guard set = `Assets/Emberdelve/**/*.cs` +
  `Tools/**/*.ps1|*.py` + `AgentScripts/**/*.cs` + `TESTER_CONTRACT.md` + `unity-tester/**/*.md`,
  exact-extension match, `__pycache__` excluded): **269 files before, 269 after**.
  - vs controller `Logs/local-testing/runs/Inventory-panel-20261102/source-before.json`:
    **269 shared, 0 added, 0 removed, 0 changed** (`hash-comparison-controller-to-local.txt`) — the tree I
    tested is exactly the tree the controller snapshotted.
  - before → after my run: **0 changed** (`hash-comparison-source-before-to-final.txt`);
    controller-before → my final: **0 changed** (`hash-comparison-controller-to-final.txt`).
    **I modified no source, test, tool or setting.**
- vs D4c1-R3's final snapshot (`hash-comparison-R3final-to-local-before.txt`): 257 → 269,
  **12 added, 12 changed**. The added files are the D4c2 skill-cooldown trio plus the newer
  `Tools/LocalTesting` modules; the changed set includes the assigned
  `InventoryScreenController.cs` and `AgentScripts/ReviewD4c1.cs`, **and ten other runtime sources**
  (`LocalCombatAuthority.cs`/`.Kit.cs`, `SkillCaster.cs`, `GameContracts.cs`, `PlayerRig.cs`,
  `StatsComponent.cs`, `EffectiveStatsSnapshot.cs`, `AttributeCurveProfile.cs`,
  `EmberdelveAttributeSkillGate.cs`, `local_tester.py`). Those hashes match the
  `Logs/local-testing/D4c2-*` snapshots, i.e. they are Codex's D4c2 integration that landed at
  2026-11-01T18:17Z, immediately after R3 ended (18:09Z). **Consequence: R2/R3 headless numbers were
  not reusable and were not reused** — the two assigned gates were re-executed here.

### 1.1 The assigned change (independent review)

`git diff --no-index Logs/inventory_header_draft/before.cs Assets/Emberdelve/Runtime/UI/InventoryScreenController.cs`
→ **1 file, 4 insertions, 0 deletions**, inside `Layout()` after `leftColumnHeight` is computed:

```csharp
// Keep the background behind the whole centered bag column when the grid is tall.
// Its centre and all existing child positions stay fixed; smaller grids restore the base size.
if (_panel != null)
    _panel.sizeDelta = new Vector2(PanelWidth, Mathf.Max(PanelHeight, leftColumnHeight + 2f * PanelInset));
```

`git diff HEAD -- <same file>` gives the identical hunk, so HEAD == `before.cs`
(`06357b23…1f9fcc86`) and the working tree == `Logs/inventory_header_draft/after-v2.cs`
(`85c7f893…59915401`, 78 467 B). **Note for Codex:** the earlier draft
`Logs/inventory_header_draft/after.cs` (`4fa0e512…3823db98`) is *not* what is integrated — it extended
the panel upward *and moved `anchoredPosition`*; the integrated `after-v2` grows symmetrically and
leaves the anchored position alone.

`AgentScripts/ReviewD4c1.cs` (`7c918819…ca518f49`) is byte-identical to
`Logs/inventory_header_draft/Review.after.cs`; `Review.before.cs` is `b1df3d45…7f5824c6` (the R3 helper).
The helper diff is additive only: `SmallBag`/`TallBag`/`SetBagSize`, the `Header`/`TabBag`/`TabCharacter`
panel-containment loop, the panel-`Image` opacity check, and `panelWidth`/`panelHeight` added to the
returned object. **Every pre-existing assertion is preserved verbatim** (14/14 labels+values, 13 equipment
labels, `StatLabel13 == "Chest opening"`, `StatValue13 == InteractionSpeedFactor + "x"`, nonzero font
metrics, `fits`, `onScreen`/`inPanel`/`inPane` with the same 2 px slack, `RequireGraphics()`). I changed
no assertion and no tolerance.

### 1.2 Why the geometry is right (read-only inspection, constants only)

`PanelWidth 760`, `PanelHeight 430`, `PanelCenterX -40`, `PanelCenterY 66`, `PanelInset 16`,
`ColumnGap 10`, `HeaderHeight 26`, `DetailHeight 56`, `CarryHeight 74`, `CellSize 46`, `CellGap 2`.
`gridHeight = h*(46+2) - 2`; `leftColumnHeight = 26+10+gridHeight+10+56+10+74 = 186 + gridHeight`.

| Bag | gridHeight | leftColumnHeight | `max(430, lch+32)` | measured `panel.rect` |
|---|---|---|---|---|
| 10x6 | 286 | 472 | **504** | **760x504** |
| 10x4 | 190 | 376 | max(430, 408) = **430** | **760x430** |

`innerTop`, `leftColumnTop`, `headerTop`, `gridTop`, `detailTop`, `carryTop` all still use the **const**
`PanelHeight`, so no content coordinate moves — only the background grows. `UiFactory.CreatePanel` →
`Stretch()` (anchors 0–1, pivot 0.5) with `anchoredPosition = (PanelCenterX, PanelCenterY)`, so the growth
is symmetric about an unchanged centre. Panel-local header top = `199 - (398 - lch)/2`:
**236** at 10x6, **188** at 10x4 — both confirmed by measurement (§4).
Before the fix the panel half-height was 215, so at 10x6 the header top (236) and the tab band
(210…236) sat **21 px above the opaque background** — the reported screenshot fault. After the fix the
half-height is 252 and the header top sits **16 px below** the panel top, i.e. exactly one `PanelInset`.

## 2 Commands (all local, sequential, one owned Editor at a time)

```
# identity / snapshots
Logs/local-testing/Inventory-panel-20261102/prerun.ps1
…/snapshot-sources.ps1 -Out source-snapshot-before.json | source-snapshot-final.json   (+ .flat.json copies)
…/snapshot-assets.ps1  -Out asset-snapshot-before.json  | asset-snapshot-final.json
python Tools/LocalTesting/compare_sources.py <controller source-before.json> <local flat>      # 269/269 identical
python Tools/LocalTesting/compare_sources.py <before flat> <final flat>                        # 269/269 identical
python Tools/LocalTesting/compare_sources.py <R3 final flat> <before flat>                     # 12 added / 12 changed
…/compare-assets.ps1 -Left asset-snapshot-before.json -Right asset-snapshot-final.json -Out asset-drift-final.txt

# headless gates (Tools/Run-EmberdelveGates.ps1, unchanged)
& Tools/Run-EmberdelveGates.ps1 -ProjectPath '.' -Methods @('Emberdelve.EditorTools.EmberdelveRuntimeSmokeTest.RunInventoryUiFlow',
        'Emberdelve.EditorTools.EmberdelveCloakSlotGate.Run') -OutputDirectory Logs/local-testing/Inventory-panel-20261102/gates-20261102-032114 -TimeoutSeconds 1200
python Tools/LocalTesting/summarize_results.py Logs/local-testing/Inventory-panel-20261102/gates-20261102-032114/summary.json

# Editor lifecycle (one instance, port 7800 / PID 5060)
unity open . --editor-version 6000.2.5f1 --until-ready --timeout 420
unity command editor status / scene list_open
unity command editor set_auto_tick --enable true --interval_ms 16 --persist false
unity command editor.playmode set_run_in_background --enable true
unity command editor.playmode start | stop
unity command script run --file AgentScripts/ReviewD4c1.cs --entry ReviewD4c1.Prepare|Measure|SmallBag|TallBag
unity command script eval --code <file contents>          # read-only probes + Game-view window control
unity command capture game_view --source screen --width 1280 --height 720 --save_path …
unity close --timeout 180 .
python Tools/LocalTesting/summarize_render.py <prepare> <measure>   # additional evidence only
```

`unity command list` is not a valid invocation in CLI 1.0.0-beta.15; the catalog was probed with
`unity command --query tick|background` (`catalog-tick.txt`, `catalog-background.txt`). Only setters
exist — there is no `get_auto_tick` / `get_run_in_background` (`auto-tick-baseline.txt`,
`runbg-baseline.txt` record those `REMOTE_COMMAND_NOT_FOUND` replies).

## 3 Headless gates (real batch-mode Unity processes)

`gates-20261102-032114/summary.json` → `"passed": true`; `summarize_results.py` → `status: pass`,
`problems: []` for both rows. Raw logs inspected independently, not only the summary:

| Method | exit | elapsed | positive summary line | `error CS` / `FAIL` / `mismatch` / `Exception:` / `Aborting batchmode` lines in the raw log |
|---|---|---|---|---|
| `…EmberdelveRuntimeSmokeTest.RunInventoryUiFlow` | 0 | 60.72 s | `[Emberdelve][Smoke:inventoryui] PASS checks=485 failures=0 exit=0` | **0** (7 931 log lines) |
| `…EmberdelveCloakSlotGate.Run` | 0 | 19.67 s | `[Emberdelve][CloakSlot] PASS checks=10167 failures=0` | **0** (865 log lines) |

InventoryUiFlow coverage preserved: its own closing lines are intact
(`ok an instant target gets no suffix`, `ok the rig survived the pointer tests (90/90)`,
`ok the pointer fixtures left the bag and the worn kit empty for the flows that follow`,
`ok the satchel screen ends closed, with no ghost and no selection`, `PASS checks=485 failures=0 exit=0`,
`Play mode finished, exiting with stored code 0`). CloakSlot ran its 3 tables x 400 seeds drop tables and
tooltip check. **No full EditMode suite was run** — the assigned diff is 4 lines of UI layout and the brief
scoped the regressions to these two gates; the ten other changed runtime sources are D4c2's, already
verified by `LOCAL_D4c2-R1-20261101`.

A first runner invocation failed before starting Unity (`Invalid execute method` — PowerShell `-File`
does not bind a comma list to `[string[]]`); its log is kept as `gates-20261102-032104.runner.txt` and the
empty output directory it created was removed. No Unity process was started by that attempt.

## 4 Graphical session (real, non-batch, Direct3D12)

Pre-open state (`pre-run-state.txt`): `unity status` → no instances, no Unity process,
`Temp/UnityLockfile` **ABSENT**.

| Fact | Value |
|---|---|
| Open | `unity open . --editor-version 6000.2.5f1 --until-ready --timeout 420` → ready in **23.39 s** |
| Instance | port **7800**, PID **5060**, Unity **6000.2.5f1**, `role=main`, `instanceName="Main Editor"` |
| Scene at open | `Assets/Emberdelve/Scenes/Emberdelve_Level01.unity`, `isDirty=false`, `rootCount=10` — the actual saved Level01; no `scene open` needed |
| Play proof | `editor status` → `status=playing`, `playMode=playing`; probes report `playing=True batch=False device=Direct3D12` |
| Ordinary Play | no cheats, no revive, no admission/equipment bypass, no input override, no fixture saved |

### 4.1 Prepare — the known setup race, handled as in R3 (no bypass)

`playmode start` → `"Entered play mode"`. `Prepare` was fired immediately, 8 attempts allowed:

| Attempt | Result | Evidence |
|---|---|---|
| 1 | `COMMAND_FAILED: … 'script': Network error: socket hang up` | `render-prepare-attempt-1.txt` |
| 2, 3 | `EDITOR_NOT_RUNNING: No Pipeline instance found …` | `render-prepare-attempt-2/3.txt` |
| 4 | **OK** `{"prepared": true, "frame": 1, "gameTime": 0}` (compileMs 1191, executeMs 48, `diagnostics: []`) | `render-prepare-attempt-4.txt` |

Attempts 1–3 are the Pipeline transport restarting through the play-mode domain reload — the same class
R3 recorded. Attempt 4 reached the fixture on the **first game frame**, before any `EnemyAgent` could land
damage. Post-`Prepare` probe (`eval-state-after-prepare.txt`, frame 11601 / gameTime 69.18107):
`playerDead=False currentHealth=150 maxHealth=120 interactionFactor=1`, `directorState=Running`,
`controllers=1 open=True`, `enemiesTotal=6 enemiesEnabled=0` (the fixture's own disable),
`equipActive=13 equipInactiveOrPendingDestroy=0 statLabelsActive=14 statValuesActive=14`,
`grid=10x6 totalUnits=0 usedCells=0 capacity=60`. No revive, no admission bypass, no check weakened.

### 4.2 Real viewport (actual `Screen`, not just the requested capture size)

Baseline found and later restored exactly: `selectedSizeIndex=5`, `minSize (200,200)`,
`maxSize (4000,4000)`, `targetSize 2560x1440`, window `(x:420.92, y:79.02, width:1315.20, height:708.77)`,
`Screen 2560x1440`. The Pipeline catalog still has **no** resolution command, so the R2/R3 mechanism was
reused: `SizeSelectionCallback(0)` (free aspect) + `minSize = maxSize = forced` + `EditorWindow.position`.

| Action | window (GUI) | `targetSize` | `Screen` |
|---|---|---|---|
| baseline | 1315.20 x 708.77 | 2560x1440 | 2560x1440 |
| index 0, min=max=1280x741 | 1279.75 x 741.26 | 1279.75x720.26 | **1280x720** (settles one poll later) |

GUI→Screen factor in this session was **1.0** (as in R3; R2 saw 1.25). 1280x720 was reached in one set +
one poll. All three `Measure` calls and all three captures report `Screen 1280x720` /
`width=1280 height=720`. No other resolution was attempted.

### 4.3 Panel geometry — helper output cross-checked by an independent read-only probe

`Measure` (`panelWidth x panelHeight`) versus the separate `script eval` probe
(`panel.rect`, `sizeDelta`, `anchoredPosition`, `pivot`, anchors, screen-space bounds, `Image` colour):

| Bag | `Measure` panel | probe `panel.rect` | probe `sizeDelta` | probe `anchoredPos` | panel screen bounds |
|---|---|---|---|---|---|
| 10x6 (after Prepare) | **760x504** | 760x504 | (760.00, 504.00) | **(-40.00, 66.00)** | x=860 y=534 w=760 h=504 @2560x1440 |
| 10x4 (after `SmallBag`) | **760x430** | 760x430 | (760.00, 430.00) | **(-40.00, 66.00)** | x=219.877 y=211.1308 w=760 h=430 @1280x720 |
| 10x6 (after `TallBag`) | **760x504** | 760x504 | (760.00, 504.00) | **(-40.00, 66.00)** | x=219.877 y=174.1308 w=760 h=504 @1280x720 |

Header / tab screen bounds (probe) against the panel bounds:

| Bag | panel y-range | `Header` | `TabBag` | `TabCharacter` | inset above header |
|---|---|---|---|---|---|
| 10x6 @2560x1440 | 534 → 1038 | x=1084 y=996 271x26 | x=876 y=996 94x26 | x=980 y=996 94x26 | 1038-1022 = **16 px** |
| 10x4 @1280x720 | 211.13 → 641.13 | x=443.877 y=588.13 271x26 | x=235.877 y=588.13 | x=339.877 y=588.13 | 641.13-614.13 = **27 px** |
| 10x6 @1280x720 | 174.13 → 678.13 | x=443.877 y=636.13 271x26 | x=235.877 y=636.13 | x=339.877 y=636.13 | 678.13-662.13 = **16 px** |

Panel-local header top measured = **236** (10x6) and **188** (10x4) — exactly the values §1.2 predicts,
confirming the layout formula was not moved. Panel `Image`: `enabled=True`,
`color = RGBA(0.055, 0.050, 0.045, 1.000)` in all three probes (opacity check satisfied).

### 4.4 `Measure` results

| Call | frame / gameTime | viewport | `pass` | `failures` | labels/values/equip | `StatValue13` vs expected | rows | rows with `realMetrics && fits && onScreen && inPanel && inPane` all True | panel |
|---|---|---|---|---|---|---|---|---|---|
| 10x6 | 37130 / 244.949722 | 1280x720 | **True** | *(empty)* | 14 / 14 / 13 | `1x` == `1x` | 41 (41 unique) | **41 / 41** | **760x504** |
| 10x4 | 66378 / 413.3486 | 1280x720 | **True** | *(empty)* | 14 / 14 / 13 | `1x` == `1x` | 41 (41 unique) | **41 / 41** | **760x430** |
| 10x6 return | 87731 / 537.2806 | 1280x720 | **True** | *(empty)* | 14 / 14 / 13 | `1x` == `1x` | 41 (41 unique) | **41 / 41** | **760x504** |

Raw envelopes: `scriptRunSuccess=True`, `diagnostics=[]`, `error=None` in all three; per-row tables in the
matching `*-summary.txt`. Widest preferred widths (all inside a 348 px rectangle): `StatValue11` 256x16
(`Ember Rapier  19 -> 25,5 before armour`), `StatValue8` 245x16, `StatValue12` 201x16,
`EquipLabel_OffHand` / `EquipLabel_AltOffHand` 112x25 inside 118x26.

`SmallBag` returned `{"width":10,"height":4,"frame":60289}`; `TallBag` returned
`{"width":10,"height":6,"frame":78569}`; both `success=True`, `diagnostics=[]`. The probes between them
(`eval-state-after-smallbag.txt` frame 60854 / gameTime 381.5071; `eval-state-after-tallbag.txt`
frame 79136 / gameTime 487.8461) show the grid and panel actually changed in the live actor's inventory
(`grid=10x4 capacity=40` → `grid=10x6 capacity=60`, `totalUnits=0` throughout, so the helper's
"living player with an empty carried bag" precondition was genuinely met, not faked).

**Content coordinates preserved:** every 10x6 row landed at the same screen position in the first and the
returning call (e.g. `StatLabel0` screenY `577.130737`, `StatValue13` `280.130768`,
`EquipLabel_Weapon` `593.130737` in both). The 10x4 rows sit 48 px lower (`StatLabel0` `529.130737`) —
that is the untouched centring formula reacting to the shorter grid, not the panel change.

`python Tools/LocalTesting/summarize_render.py <prepare> <measure>` returned `status: pass`, `issues: []`
for all three pairs (`summarize-render-10x6.txt`, `-10x4.txt`, `-10x6-return.txt`). It was used only as
additional evidence; `panelHeight`, every `failures` list, every row flag and every envelope error/diagnostic
was checked manually against the raw JSON.

## 5 Captures (real screen-composited Game view)

`capture game_view --source screen` (includes Screen Space – Overlay UI). Requested and actual both
**1280x720**; IHDR verified from the file bytes:

| File | IHDR | bytes | sha256 |
|---|---|---|---|
| `capture-1280x720-10x6.png` | **1280x720** | 725 904 | `0de60a1855263a6d40ca8f2772478a9edbf445d2d38fbedc48fa74dd00316f4a` |
| `capture-1280x720-10x4.png` | **1280x720** | 816 801 | `157a1137378af920cf7a09c082889e26b484232dbb9faa8833eae83ff0e75206` |
| `capture-1280x720-10x6-return.png` | **1280x720** | 745 799 | `d94399f40dabc781a291e26d5948a212201e7a086c4e3fcd9b9c837f04b2cc60` |

Actual viewport for all three: `Screen 1280x720`, `SystemInfo.graphicsDeviceType = Direct3D12`,
`Application.isBatchMode = False`, `Application.isPlaying = True`. The Pipeline command roots a relative
`save_path` under `Assets/Logs/…`; the PNGs were moved to this evidence directory, their `.meta` import
sidecars deleted, and `Assets/Logs` + `Assets/Logs.meta` removed (`Test-Path` → `False` for both; no
`Assets/Logs` entry in `git status --porcelain`).

These are byte artefacts. **No subjective visual inspection is claimed** — this model is text-only; every
geometry statement above comes from the harness's numeric rectangles and the PNG IHDR.

## 6 Cleanup state

- Game-view window restored to this session's own baseline (`eval-gameview-restore.txt`,
  `eval-gameview-after-restore.txt`): `selectedSizeIndex=5`, `minSize (200,200)`, `maxSize (4000,4000)`,
  `targetSize 2560x1440`, window `(x:420.92, y:79.02, width:1315.20, height:708.77)`, `Screen 2560x1440` —
  byte-identical to `eval-gameview-baseline.txt` at the recorded 2-decimal precision.
- Play stopped (`playmode-stop.txt` → `"Exited play mode"`; `editor-status-after-stop.txt` →
  `status=ready`, `playMode=stopped`).
- Scene clean and **never saved**: `scene-list-after-stop.txt` → `Emberdelve_Level01`, `isDirty=false`,
  `rootCount=10`; the scene file is `2 957 535 B`, sha256 `903f4e57…26174a`, mtime `2026-10-28 15:30:31` —
  identical before and after my run.
- Transient overrides removed: `editor set_auto_tick --enable false --persist false` → *"Auto-tick disabled
  (session-only…)"*; `editor.playmode set_run_in_background --enable false` → *"Run In Background is on (the
  project's own value; nothing is overriding it)"* — the project value remains `on` exactly as found.
  Auto-tick had to be re-armed once after the play-mode domain reload (`auto-tick-on-after-reload.txt`).
- **No input configuration touched** — the `settings.input` command group was never called; no global input
  asset or project setting was written. `ProjectSettings/ProjectSettings.asset` unchanged
  (28 173 B, sha `4518c7ac…229a407`, mtime `2026-11-01 19:21:53` — not mine).
- Editor closed by identity-verified graceful close: `unity-status-before-close.txt` (single instance,
  port 7800, PID 5060, project path matched) → `unity-close.txt` `method:"graceful" closed:true pid:5060`.
  **No `--force`, no kill, no unrelated process closed.** After close: `unity status` → `count 0`, PID 5060
  gone, no `Unity` process, `Temp/UnityLockfile` → `Test-Path False`. The lockfile was **never deleted**; it
  disappeared because the Editor exited cleanly.
- No commit made. `Docs/Reviews/LOCAL_INVENTORY_PANEL_20261102.md` is the only report written; everything
  else is under `Logs/local-testing/Inventory-panel-20261102/`.

## 7 Generated asset changes / churn (recorded, not fixed)

Guarded asset set: `Assets/Emberdelve/Data`, `Assets/Emberdelve/Art/Materials`, `Assets/Emberdelve/Scenes`,
`ProjectSettings` — **231 files before and after**.
`asset-drift-final.txt`: `shared=231 contentChanged=5 guidChanged=0 added=0 removed=0`

| File | before → after |
|---|---|
| `Blade_Metal.mat` | 3 829 → 3 847 B |
| `Grip_Leather.mat` | 3 833 → 3 851 B |
| `Stone_Ceiling.mat` | 4 159 → 4 170 B |
| `Stone_Floor.mat` | 4 158 → 4 170 B |
| `Torch_Flame.mat` | 2 673 → 2 683 B |

`git diff` (`material-churn-diff-final.txt`) is **one line per material, float decimal round-off only**,
e.g. `- _Color: {r: 0.48, g: 0.47, b: 0.45, a: 1}` → `+ _Color: {r: 0.47999996, g: 0.4699999, b: 0.45, a: 1}`.
No GUID, shader, keyword or property change. Mechanism: my headless gate runs (03:22) normalised these five
to the batch-serialized form, then my graphical Editor (03:44) re-serialized them back to the
Editor-serialized form. Net git effect: 5 new `M` entries in `git-status-final.txt` (56 porcelain lines vs
51 pre-run; the apparent change of `AttributeCurve_Default.asset` from `M ` to ` M ` is only the leading
space that `prerun.ps1`'s `.Trim()` stripped from the first porcelain line). Codex may
`git checkout -- Assets/Emberdelve/Art/Materials` or accept it; nothing authored is lost.

`Assets/Emberdelve/Data/AttributeCurve_Default.asset`: **content unchanged**
(sha `619bd1d8…21cab00a`, 1 124 B in both snapshots); only its mtime moved to `2026-11-02 03:22:28` because
the headless gate runs re-serialized it with identical bytes. **No builder was executed by me.**
`ProjectSettings` content: 0 changes (the untracked
`ProjectSettings/Packages/com.unity.pipeline/` remains untracked, as pre-run).

## 8 Evidence classification and what remains for Codex

1. **Verdict: PASS, no defect found.** The 4-line change makes the opaque satchel background contain the
   header and both tabs for a tall bag (measured 16 px inset at 10x6), restores 430 px for a 10x4 bag, and
   leaves the panel centre and every content coordinate untouched. Original coverage is preserved
   (InventoryUiFlow 485/0, CloakSlot 10167/0, 14/14/13 rows, 41/41 containment-true rows, `1x` opening).
   Nothing was repaired, skipped or relaxed by me.
2. **`Prepare` setup race is still open** (unchanged from R2 §5.2 / R3 §4.1): the fixture only disables the
   `EnemyAgent`s *after* it has resolved the rig and begun equipping, so a normal Play session can kill the
   actor before `Prepare` runs. Here it was avoided the same way as R3 — re-enter Play and fire `Prepare`
   immediately (landed `frame=1 gameTime=0` after 3 transport failures). A deterministic fix (disable the
   hostile agents, or grant a spawn grace, before the fixture's equip step) belongs to Codex. I did not
   revive, bypass admission, or weaken any check.
3. **Pipeline transport during the play-mode domain reload**: `script run` right after
   `editor.playmode start` fails `socket hang up`, then `EDITOR_NOT_RUNNING` twice, until the reload
   completes. The bounded retry in `play-and-prepare.ps1` is the working pattern; worth encoding in the
   harness. Also `unity command` has no `list` subcommand and no auto-tick/run-in-background getters — only
   setters — so "restore exactly as found" depends on the CLI's own restore wording.
4. **Filesystem mtimes in this checkout are not trustworthy.** Several D4c2-era sources
   (`LocalCombatAuthority.cs`, `.Kit.cs`, `SkillCaster.cs`, `GameContracts.cs`, `PlayerRig.cs`,
   `StatsComponent.cs`, `EffectiveStatsSnapshot.cs`, `AttributeCurveProfile.cs`) carry mtimes of
   `2026-10-31 20:42–20:44` although their content matches snapshots taken `2026-11-01T18:17Z` and their
   git status changed between R3 (clean) and D4c2 (modified); `git reflog` shows no checkout/stash. Content
   hashes are therefore the only reliable identity signal here (OneDrive hydration is the likely cause). Not
   a QA defect; flagged so Codex does not read mtimes as change evidence.
5. **`Logs/inventory_header_draft/after.cs` is a superseded draft** (it moved the panel's
   `anchoredPosition`); the integrated file matches `after-v2.cs`. Worth pruning or labelling so a later
   review cannot compare against the wrong candidate.
6. **Material serialization churn** (§7) is a tool artifact of alternating headless/graphical Editor runs.
7. **This is LOCAL evidence only.** GUI containment and font metrics do not establish server authority;
   U4A/U3B server-clock authorization remains pending. The conditional compact-CHA skill-row candidate is
   still not integrated (`CharacterStatsView.cs` unchanged in this job's snapshot), so the compact-CHA
   re-render remains unrun — no clipping was found at 1280x720 with the full outfit.

## 9 Evidence directory

`Logs/local-testing/Inventory-panel-20261102/` — 84 files (including the post-report guard-snapshot pair and its comparison) + `gates-20261102-032114/` (2 raw Unity logs +
`summary.json`): pre-run state; source snapshots (before/final, nested + flat) and the four `compare_sources` outputs (controller-vs-local, before-vs-final, before-vs-postreport, R3-final-vs-before); asset snapshots (before/final) and `asset-drift-final.txt`; the material churn
stat/diff; the runner transcript `gates-20261102-032114.runner.txt` (and `…032104.runner.txt` for the
aborted invocation); Editor open/status/scene/close records; catalog probes; Play start/stop records;
auto-tick and run-in-background records; four `Prepare` attempt envelopes (3 transport failures + 1
success); three read-only state probes; five Game-view records; three raw `Measure` envelopes + three
per-row summaries; `SmallBag`/`TallBag` envelopes; three `summarize_render` digests; three capture records;
the three 1280x720 PNGs; `git-status-final.txt`; and the helper scripts (`prerun.ps1`,
`snapshot-sources.ps1`, `snapshot-assets.ps1`, `compare-assets.ps1`, `compare-snapshots.ps1`,
`run-script.ps1`, `eval.ps1`, `gameview.ps1`, `play-and-prepare.ps1`, `summarize-measure.ps1`,
`code-probe-state.cs`).

