# ITEMS-01: atomic offhand displacement

Milestone: network raid stabilization. Scope: one equipment-transfer defect, not acceptance of the complete inventory/UI milestone.

Previously a two-handed equip first performed a complete offhand unequip, including revision and notifications. If the main-hand bag exchange refused, it attempted another ordinary equip to restore the shield. That could change stack identities/revision even when restoration succeeded, and restoration itself could refuse.

The offhand stow and main-hand exchange now run as one silent bag operation. On refusal or exception, original stack identities, instances, positions, counts, next-ID/exhaustion state and carried counters are restored. Equipment slots/hands are changed only after this operation succeeds. Successful equipment publication advances revision once and includes the displaced item's equipment event.

The two regression cases reject the main exchange after stow (stale source and duplicate outgoing UID) and compare the canonical bag snapshot, allocator, counters and offhand absence against the original state. Existing Hands and NetInventory scenarios cover normal equipment use. The rollback helper uses existing silent transfers and does not modify lifetime-loot counters.

Validation is recorded in CURRENT_WORK. The reviewed change scope is Equipment.cs, Inventory.cs and EquipAtomicityTests.cs. The planner conservatively selects 51 scenarios for general item changes; this specific transfer correction uses the repository's inventory matrix: EditMode, Hands and NetInventory. Corpse/extraction and movement rules are unchanged.

Only these task-specific hunks may be committed. Other pre-existing changes in the two runtime files belong to unfinished milestones. Real two-client UI/reconnect/raid acceptance remains separate.

Completed validation: Logs/runs/20261108-202148-890 — EditMode 902 passed, NetInventory 146/0, Hands 92/0. No full gameplay-suite rerun. Validation used the existing mixed tree; the commit isolates only this completed task.
