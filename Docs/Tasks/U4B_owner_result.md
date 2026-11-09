# U4B owner result delivery

> Historical implementation note. Current acceptance and remaining work: [CURRENT_WORK](../CURRENT_WORK.md) and [raid acceptance](RAID_UI_ACCEPTANCE_20261108.md). Status statements below describe the original slice.


## Behavior
- The server converts its terminal `RunResult` to a validated network payload and sends it only through the owning player's `NetPlayerRig` RPC. The host uses the same server result through a trusted in-process path.
- The payload preserves outcome, class name, loot value, item count, kills, duration, player id and gear score. It carries display facts only; clients do not grant items or update a bank.
- The owner verifies the payload's player id, terminal outcome and numeric fields, then accepts only one result per spawned player object. Repeated deliveries and mismatched player ids do not publish UI events.
- The owner caches the received result in `RunContext` for its lobby display, updates local run state and raises the existing `RunEnded` event so `ResultScreenController` shows the result.
- `PlayerView` binds the client to the server-spawned owner id so the existing result screen filters the event to the correct player.
- Fixed `ExtractionHoldWindow` timeout bug: the grace deadline is inclusive for expiry. Tests cover just-before, exact-boundary and just-after timestamps.

## Limits
- This replicates the terminal summary to the owner. It does not create corpse loot, stash persistence or bank items, and does not complete U4B by itself.
- The countdown remains WIP/opt-in and disabled in the playable asset builder until timeout finalization is implemented.

## Checks
- Added exact result-wire round-trip/validation tests and one-shot owner receipt tests.
- Unity and mirror checks were not run during the active verification window.
