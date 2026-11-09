# Authoritative network floor loot — implementation in progress

> Historical implementation note. Current acceptance and remaining work: [CURRENT_WORK](../CURRENT_WORK.md) and [raid acceptance](RAID_UI_ACCEPTANCE_20261108.md). Status statements below describe the original slice.


## Implemented

- The server scans the already-authored `LootPickup` objects only after the modular `RunDirector` enters `Running`. This waits for the modular assembler to remap their cells and seeds the server registry once from the remapped scene objects. Runtime drops enter the same registry within 100 ms (or immediately before a pickup request/snapshot).
- Server-assigned IDs identify live server pickup objects. The server publishes complete, revisioned floor-loot snapshots reliably to every remote owner; an owner snapshot request also sends the current world state for late join and recovery.
- Client authored loot is hidden after the first valid snapshot and replaced with local visible replicas at server positions. These replicas carry presentation data and send only an ID plus per-player sequence; clients never grant themselves loot.
- Client prompts and server acceptance share the same stable pickup center, configured reach, and level-only LOS calculation. The bobbing/spin collider is not trusted as a range claim.
- The server resolves sender/object ownership, life/run admission, current item existence, authoritative reach and level-geometry LOS, then runs the existing `LootPickup` collection transaction against the admitted server rig. The source's existing reentrancy guard and instance transfer preserve exact gear UID/affixes and prevent a second claim. Partial stacks publish the remaining quantity.
- Added bounded wire validation and EditMode cases for replay, sequence/rate limits, duplicate IDs, invalid quantities and oversized snapshots.

## Verification status

Standalone dedicated-server/two-client floor-loot proof passed **26/0** at `Logs/runs/20261107-044115/gates/summary.json`. This run verifies the floor-loot gate only; it does not establish the full U4A inventory/drop flow. A broader NetInventory run `Logs/runs/20261107-040249/summary.txt` failed 146/6, including occluded pickup rejection, one-UID contention and client B convergence. Keep those failures open for follow-up.

## Known implementation limit

World snapshots are capped at 256 entries and 48 KB. If a scene exceeds those bounds, the server publishes a revisioned empty state until capacity recovers, preventing stale client replicas from appearing collectible. A client only changes its replica set after it can validate the complete snapshot and resolve its item catalog.
