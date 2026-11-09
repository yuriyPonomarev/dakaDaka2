# U4B server extraction hold

> Historical implementation note. Current acceptance and remaining work: [CURRENT_WORK](../CURRENT_WORK.md) and [raid acceptance](RAID_UI_ACCEPTANCE_20261108.md). Status statements below describe the original slice.


## Behavior
- Networked owners begin, heartbeat, cancel and complete a hold through reliable RPCs on their own player object. Host-local extraction enters the same server lease directly.
- The server uses its own monotonic realtime clock and configured extraction duration. Client timing requests completion only; an early request waits for the server duration.
- The server validates sender/object ownership, monotonic per-connection sequence, alive/admitted state, range and level line-of-sight when the hold begins, throughout the lease and again at commit.
- Any server-observed displacement beyond 5 cm, damage, stale heartbeat, invalid geometry/state or explicit cancel removes the lease. A successful commit seals the lease before calling `RunDirector.BeginExtraction`, so duplicate completion RPCs cannot extract twice.
- Offline holds retain the existing local timing and `RunDirector` path.
- Added NetRules tests for server-clock duration, early/valid completion, wrong sequence, movement interruption, stale lease/heartbeat and one-shot commit.
- Terminal server results are now sent owner-only and rendered by the existing result screen; see `Docs/Tasks/U4B_owner_result.md`.

## Limits
- Death/disconnect corpse creation and corpse loot are not implemented. This is not a complete U4B raid loop and must not be described as such.
- The raid countdown remains opt-in and disabled in the playable asset builder until timeout finalization is implemented.

## Checks
- Added `ExtractionHoldWindowTests` to the existing NetRules EditMode test assembly.
- Added `EmberdelveNetExtractionGate` for separate server/A/B processes: successful hold exactly once, foreign extraction RPC rejection, owner result field equality and unchanged client inventory. The expanded gate, including movement, nonlethal/lethal damage interruptions and timeout/corpse custody, passed 136/0; see `Docs/Tasks/U4B_two_client_gate.md`.
- The gate's damage stimuli use server-created damage through the regular health event path, not opponent attack RPC validation. Visual timer/HUD review remains outstanding.
- Not run by this slice: Unity and the CI mirror were not launched while the full verification was in progress.
