# R6a — runtime inventory stack identity

R6a adds an inventory-owned, nonserialized `ulong StackId` to live inventory stacks and exposes it through `InventorySlot`. The old constructors remain source-compatible and create detached views with ID zero. `IndexOfStack` and `TryGetStack` reject zero and return no result for unknown or retired IDs.

The stack ID stays with the same carried stack through moves, swaps, compaction, partial removals/adds, partial merges, and grid resize/reseat. A merge keeps the target ID; a source ID remains live while units remain and retires when the source empties. A newly created stack gets a monotonic nonzero ID. `Clear` and successful restore retire live IDs without resetting the allocation counter. A successful snapshot restore assigns fresh IDs while the existing container snapshot JSON/checksum and item instance UIDs/rarity/rolls remain unchanged. Restore and add paths preflight ID capacity before mutating inventory; an exhausted ID space therefore cannot leave a partial restore/add. At the terminal ID boundary, add preflight is deliberately fail closed based on the maximum number of requested new stacks and may refuse an otherwise partial add that could consume existing headroom. Normal partial-add behavior is unchanged while enough IDs remain. A failed restore leaves both data and IDs untouched.

When equipment replacement reuses the incoming stack object to carry the outgoing exact gear instance, it now assigns that stack a new ID before the item changes. The incoming stack ID retires. Unequip creates a fresh stack ID as the instance returns to the bag. Gear identity data itself is not modified.

The focused gate `Emberdelve.EditorTools.EmberdelveInventoryStackIdGate.Run` exercises the public bag/equipment APIs with the production level rig and catalog. It covers uniqueness and lookups, legacy detached-view ID zero, stack refill, partial/full merge and removal, move/swap, resize, compaction, Clear/add monotonicity, valid restore with byte-identical snapshot JSON, failed restore without ID changes, and exact rolled gear replacement/unequip.

No Unity or test process was run for this draft. Parent should run the new gate together with inventory, UI, snapshot, gear, and loot gates after integrating the reviewed files. The new runtime identity is intentionally not serialized; no command revision, save schema, UI binding, or replay behavior changes are included in R6a.


## Root verification

Unity batch Logs/opus_finish_20261023_12_R6a/summary.json: all7 groups PASS, exit0. StackId56/0; inventory415/0; inventoryUI300/0; ContainerSnapshot954/0; GearInstance62/0; LootPipeline9954/0; MultiPlayer134/0. Total11,875 checks, zero failures. No unexpected exceptions or C# errors; ContainerSnapshot retains its exact intentional subscriber exception. Codex and an independent Luna reviewer inspected runtime identity/fixture diffs before integration. R6b revisions/commands and R6c UI/hotbar migration remain separate.
