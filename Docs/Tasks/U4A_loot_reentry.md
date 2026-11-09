# U4A preparation: loot source re-entry

> Historical implementation note. Current acceptance and remaining work: [CURRENT_WORK](../CURRENT_WORK.md) and [raid acceptance](RAID_UI_ACCEPTANCE_20261108.md). Status statements below describe the original slice.


Chest and ground pickup refuse synchronous re-entry while a transfer is in progress. A fully collected ground stack is marked spent before Unity's deferred destruction, like an instance pickup. Inventory notifications are deferred until the source has recorded the accepted quantity; subscriber exceptions are logged by the existing transaction dispatcher. This closes a duplication window during callbacks; it does not implement network inventory or multi-client contention.

Six EditMode cases exercise nested calls, refusal, exceptions before commitment and throwing inventory observers for both sources; all pass. Full EditMode: 738/738. Fast gate results are in `Logs/quick/loot_reentry_20261105/summary.txt`. No multi-process network proof has been run.

Limit: `ILootHost` cannot report a result if an arbitrary host mutates a bag and then itself throws before returning. The notification fix covers the existing game event dispatcher, not that unsupported host behavior. A network transfer protocol must define an atomic commit/result boundary and disconnect recovery.
