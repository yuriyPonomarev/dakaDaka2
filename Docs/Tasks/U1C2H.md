# U1C2H — containers now conserve leftovers after an actual partial collection

## Accepted-count contract (the whole fix, in short)
1. `Inventory.TryAdd(ItemData, int, out string reason, out int accepted)` is the single add implementation; the old `TryAdd(item, amount)` and `TryAdd(item, amount, out reason)` are thin wrappers, so bool semantics and every existing caller/test are unchanged.
2. `accepted` is the exact number of units that call committed to the grid — the same number it added to `TotalUnits`/`CarriedValue`/`LifetimeCollected`/`RecoveredValue` — 0 on any refusal (null item, amount<=0, no room at all, refused gear mint that rolls its created stacks back out), and equal to `amount` exactly when the bool is true. No second placement algorithm, no "false means 0" guessing.
3. `ICountedLootHost : ILootHost { int CollectLootCounted(ItemData, int); }` (new, in LootPickup.cs) is the counted host path; `ILootHost` and its `bool CollectLoot` are untouched, so existing hosts and callers keep compiling and behaving.
4. `RunDirector` implements `ICountedLootHost`: `CollectLootCounted` clamps `Inventory.TryAdd`'s accepted count into the range offered; `CollectLoot` is now `CollectLootCounted(...) == Mathf.Max(1, amount)`, which reproduces the old bool exactly (true only when every unit landed).
5. `LootCollection.CollectAccepted(host, item, amount)` is the one rule both containers use: counted host → its own number, clamped; legacy bool-only host → the units its reported `ILootHost.Inventory.TotalUnits` actually gained during the call, clamped to the offer, with a `Debug.LogWarning` when the host's own bool disagrees (explicit, never a silent partial credit); a host with no bag at all falls back to the documented bool contract (true = all, false = none).
6. `ChestInteractable.Open`: per drop, `accepted` units are credited (value × accepted), the drop is removed only when `amount - accepted <= 0`, otherwise the drop stays in `_remaining` as `new LootDrop(item, unpaid)` — same item, same single roll, remainder only. `accepted == 0` leaves the drop byte-identical. `_rolled` still guarantees no re-roll, so a reopen repeats the remainder and nothing else.
7. Chest diagnostics are now actual outcomes: `TakenCount` = units handed over, new `TakenValue` = value of units handed over (not value attempted), new `RemainingUnits` = units still held, `LeftBehindCount` = units refused; the log/toast report taken units, value and units left.
8. `LootPickup.OnInteractInstant`: accepted 0 → pickup unchanged (same amount, same object); accepted == amount → `Destroy(gameObject)`; partial → `amount -= accepted` and one `[Emberdelve][LootPickup] took X of Y, Z left on the ground` log. No re-roll, no respawn, no second pickup.
9. Gear/UID metadata: the chest only ever holds `LootDrop(item, amount)` (definition + amount, `LootDrop` untouched) and instances are minted by the bag on accept, so shrinking the drop by the accepted count is what stops the old reopen from re-offering already-accepted gear and minting duplicate identities. No cloning, no metadata dropped, no equip-transfer path touched.
10. Full-bag planning, partial `Inventory` behavior, `TryAddAt`/merge/equip-transfer paths and the "starting kit is not loot" rule were not modified.

## Files changed
- `Assets/Emberdelve/Runtime/Items/Inventory.cs` — counted `TryAdd` overload becomes the implementation; 2- and 3-arg forms delegate; `accepted = 0` on every refusal path.
- `Assets/Emberdelve/Runtime/Items/LootPickup.cs` — `ICountedLootHost`, `LootCollection.CollectAccepted`, partial pickup keeps its remainder.
- `Assets/Emberdelve/Runtime/Interaction/ChestInteractable.cs` — counted collection per drop, remainder-preserving `_remaining`, `TakenValue`/`RemainingUnits`, coherent log/toast.
- `Assets/Emberdelve/Runtime/Flow/RunDirector.cs` — implements `ICountedLootHost`; `CollectLoot` derived from the counted path.
- `Docs/Tasks/U1C2H.md` — this report.

No instance/roller/stats/UI/builder/scene/asset/gate file was edited; no test expectation was changed; nothing was reset or cleaned.

## Behavior this patch produces for the reproducer (source-level, not executed here)
Gate fixture: 1x1 bag, currency cap 80, 79 coins already carried, table `guaranteeFirstEntry`, `maxRolls=1`, one entry min=max=5, real `RunDirector` host.
- Open 1: `TryAdd` places 1 coin onto the existing stack (cap headroom), finds no free cell, returns false with `accepted = 1`; the chest now holds `LootDrop(coin, 4)` instead of 5 → bag 80 + chest 4 = 84 (was 85), `TakenCount=1`, `LeftBehindCount=4`, `RemainingUnits=4`.
- Open 2 with the bag still 80/80: `accepted = 0` → drop untouched at 4, `IsEmpty` false, chest still openable.
- Remove one coin (bag 79) then reopen: `accepted = 1` → bag 80, chest 3. Repeating empties the chest at exactly 84 collected units of credit (`RecoveredValue`/`LifetimeCollected` count accepted only, so no over-credit and no re-credit of the same unit).
- Loose pickup with amount 5 and room for 1: stays in the world at `amount = 4`; with room for 5 it is destroyed; with room for none it is untouched.

## Checks actually run by this worker
None beyond reading source. File tools only: no Unity, no test/gate run, no screenshot, no log produced by me. The FAIL6/2-failures evidence I used is the manager's existing `validation/U1C2H-attributes-20261020-140933/editor.log` (`bag=80 chest=5 sum=85 expected84`, `taken=0 ... left=1`) plus `Editor/EmberdelveChestPartialGate.cs` and the existing chest section of `Editor/EmberdelveRuntimeSmokeTest.cs:3152-3227`.

## Pending for the manager (not executed, not claimed)
1. Re-run `EmberdelveChestPartialGate` (and its extended scenarios: full-bag second open unchanged, remove-one-coin reopen, eventual exact 84 with proper credit) — expect `PASS checks>=6 failures=0`, exit 0.
2. Existing suites unchanged in intent: items 403 / inventory-UI 158 / real-game leftovers, including the ember-chest partial→clear→reopen case at smoke:3186-3205 (its `TakenCount > 0`, `Remaining.Count > 0`, `LeftBehindCount > 0`, `Remaining.Count == 0` expectations are all still satisfiable under unit counting; no expectation was widened).
3. A loose-pickup partial scenario should be added to the gate by the manager (my path is `LootPickup.OnInteractInstant` partial/accepted0/full).
4. Compile check of the new `ICountedLootHost` / `LootCollection` types and the 4-arg `TryAdd` overload resolution across the editor harnesses.
5. Note for review: `Inventory.TryAdd`'s refused-gear-mint path rolls back only stacks created by that call; for equippable definitions no units can have been added to pre-existing stacks (every gear stack in this codebase is instance-backed and the loop skips instance-backed stacks), so `accepted = 0` there matches `TotalUnits`. If a future change ever lets a definition-only equippable stack exist, that path needs re-examination — flagged, not silently "fixed" here.
