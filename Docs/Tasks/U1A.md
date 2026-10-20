# U1A — geometric grid/stack fixtures + failing smoke runs must exit nonzero

Status: **implemented (partial milestone)** — code and report saved, no engine run performed by the implementer.
Scope touched: `Assets/Emberdelve/Editor/EmberdelveRuntimeSmokeTest.cs` only. No production code, scenes,
prefabs, assets or packages were modified.

## Changed files

- `Assets/Emberdelve/Editor/EmberdelveRuntimeSmokeTest.cs`
  - `ExitCodeKey` session key + `StartRun` reset (~line 36–46, 63–66)
  - `WatchForPlayModeStart` records code 1 before its own `Exit(1)` (~line 96–101)
  - `HandlePlayModeChange` / `ExitingPlayMode` uses the stored code instead of unconditional `0`, and a new
    branch for play mode that ends before the harness finished (~line 116–137)
  - `Harness.Update` watchdog writes code 1 (~line 178–183)
  - `Harness.Run` completion writes `0` only when `_failures == 0`, otherwise `1`, and logs `exit=` in the
    PASS/FAIL summary line (~line 226–230)
  - `GridStorageFlow` overflow + partial-add section rewritten (~line 1676–1838)
- `Docs/Tasks/U1A.md` (this report)

## Behavior implemented

### 1. Grid overflow measured from geometry, not from an area division

Old fixture: `expectedFit = capacity / cellsPerCuirass` (40/6 = 6) and `FreeCells < cellsPerCuirass`.
Both are wrong expectations: equal-area capacity is not rectangular packing.

New fixture derives the expectation from the grid and the footprint axes, with no hard-coded count:

- `fitAcross = GridWidth / footprint.x`, `fitDown = GridHeight / footprint.y`,
  `expectedFit = fitAcross * fitDown`, `expectedUsed = expectedFit * footprint.x * footprint.y`,
  `expectedFree = Capacity - expectedUsed`. For the shipped 10x4 bag and the 2x3 cuirass this is
  `floor(10/2) * floor(4/3) = 5` items, 30 used cells, 10 free cells.
- Insertion loop is **bounded** (`attemptCap = expectedFit + 2`): a bag that keeps answering "yes" stops the
  loop and is reported by `placed == expectedFit` plus `placementAttempts < attemptCap`, so a broken
  implementation cannot spin the coroutine until the 150 s harness watchdog.
- Exact accounting asserted: `UsedCells == expectedUsed`, `FreeCells == expectedFree`,
  `UsedSlots == expectedFit` (equippable items never stack).
- One more rectangle is still refused, its reason must contain `no room`, and the refusal must leave
  `UsedCells`/`UsedSlots` unchanged.
- Fragmented free space preserved and strengthened: `FreeCells > 0` **and** `FindFreeOrigin(footprint).x < 0`,
  plus a second check (only asserted when the geometry actually leaves at least one footprint's worth of
  leftover area, which it does here: 10 ≥ 6) that the free cells outnumber one item yet contain no
  contiguous block. Production `FreeCells` vs `FindFreeOrigin(shape)` semantics were not changed.

### 2. Partial add measured against real remaining stack capacity

Old fixture asked for 40 coins, which actually fits (10 free 1x1 cells × coin stack limit 8 = 80 units), so
the "cannot all fit", `gained < 40` and "left …" assertions were all testing a success case.

New fixture computes the room that exists and then asks for **more than that**:

- `coinStackLimit = coin.StackLimitForFootprint` (Ember Coin `maxStack: 8`), existing coin stacks are counted
  (the bag is coin-free at this point, asserted explicitly), and
  `expectedCoinCapacity = existing stack headroom + FreeCells * coinStackLimit` → 0 + 10 × 8 = 80.
- `coinRequest = expectedCoinCapacity + 1` → 81, i.e. strictly greater than the room that exists.
- Required outcome asserted: `TryAdd` returns **false**, `gained == expectedCoinCapacity` (80 accepted),
  `gained > 0 && gained < coinRequest` (partial, neither a refusal nor a success),
  `leftover == coinRequest - expectedCoinCapacity && leftover > 0` (1 coin), and the reason must contain
  `left` **and** the leftover number.
- Aftermath asserted from measurements: coin units `== coinUnitsBefore + expectedCoinCapacity`, coin stacks
  `== coinStacksBefore + freeCellsBefore` with every stack within its limit, `UsedCells` grew by exactly the
  free cells it consumed and `FreeCells == 0`, `TotalUnits`/`UsedSlots` follow the accepted units.
- The cuirasses are snapshotted (origin, count, size, stack count) before the coin request and re-checked
  afterwards, and every cell under every cuirass footprint is re-verified as occupied — so the partial add
  cannot be paid for by moving or dropping existing items.

Check count in this section went from 9 to 24 (11 conditional-path checks are unconditional in the shipped
10x4/2x3 configuration). No assertion was removed, no scenario skipped, no equipment/container assertion
touched; `EquipmentFlow`, `ChestFlow` and every `Run*Flow` entry point are unchanged.

### 3. Failure survives the editor state transition

- `StartRun` resets the stored code to 0 for each run.
- Completion writes `_failures == 0 ? 0 : 1`; the summary line now reads
  `PASS|FAIL checks=N failures=M exit=0|1`.
- Harness watchdog writes 1 before stopping play mode; the never-started watchdog writes 1 before `Exit(1)`.
- `ExitingPlayMode` reads `SessionState.GetInt(ExitCodeKey, 1)` (default 1 so an unwritten code cannot become
  a silent success) and the delayed exit uses that captured value across the transition.
- Added branch: if play mode ends while a mode is still recorded but the harness never completed, the run is
  logged as an error and exits 1 instead of leaving the batch process hanging. This cannot turn a completed
  passing run into a failure (`DoneKey` is only set by the completion pass and the watchdog, both of which
  also write the code).

## Checks actually run by the implementer

None executed — file tools only, per the task contract. Verification performed was source-level:

- `Inventory` API confirmed in `Assets/Emberdelve/Runtime/Items/Inventory.cs`: `GridWidth`, `GridHeight`,
  `Capacity`, `UsedCells`, `FreeCells`, `UsedSlots`, `Slots[].Origin/Count/Size`, `TotalUnits`,
  `IndexOfItem`, `IsCellOccupied`, `FindFreeOrigin`, `TryAdd(item, amount, out reason)` (fills existing stacks
  first, then new stacks of `min(stackLimit, remaining)` per hole, returns false with
  `no room …` / `bag full, left N … behind`).
- `ItemData` confirmed in `Assets/Emberdelve/Runtime/Data/ItemData.cs`: `EffectiveFootprint`, `CanStack`,
  `StackLimitForFootprint`.
- Configuration confirmed: `EmberdelveGameConfig.asset` / `EmberdelveAssetLibrary` → grid 10x4 (40 cells);
  `Item_EmberCoin.asset` → `maxStack: 8`, 1x1; cuirass 2x3, equippable (non-stacking).
- Hand simulation of the new fixtures against that data: 5 cuirasses placed by row-major `FindFreeOrigin`
  (origins (0,0)…(8,0)), 30 used / 10 free, no 2x3 hole in the 1-cell-tall remainder, next cuirass refused
  with "no room", 81 coins → 10 stacks × 8 = 80 accepted, 1 left behind, grid 40/40 used.
- Local-name collision check across `GridStorageFlow` (the pre-existing `cuirassStacksBefore` at the
  stacking step forced the new snapshot locals to be named `fullBagCuirass*`).

## Pending manager verification

1. Compile the edited harness (no engine run was performed here).
2. Rerun the inventory smoke: expect the 5 grid/stack failures gone, remaining equipment-defense failures
   still logged as `FAIL`, and the process exit code now **nonzero** for that run.
3. Rerun combat: expect the same 201 checks and a **0** exit code (the summary line now carries `exit=0`).
4. Confirm Unity propagates `EditorApplication.Exit(1)` from the delayed call to the process exit code in this
   6000.2.5f1 batch invocation; if the harness log says `exit=1` but the process still returns 0, that is a
   propagation problem to solve in the runner, not a reason to relax the checks.
5. Mutation proof (checkpointed copy): changing `expectedFit` back to `capacity / cellsPerCuirass`, or
   forcing the completion exit to 0, must produce visible `FAIL` lines and a nonzero exit.

## Remaining work / blockers

- Equipment defense failures (armor/shield numbers) are untouched by design and belong to **U1B**; the
  inventory suite is therefore **not** green after this slice and must not be reported as green.
- U1C (stash/container integration acceptance) still pending.
- Residual, pre-existing and out of scope here: if a flow's coroutine is destroyed mid-run the harness
  `Update` watchdog dies with it; the new ExitingPlayMode branch converts that into exit 1 when play mode
  ends, but a run that never ends at all still relies on the manager's outer process timeout.
- No Unity/editor commands were run; nothing here is a passed test, only a saved patch.
