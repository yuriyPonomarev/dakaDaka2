# D3b equipment slots, boots to Feet (schema 3), jewelry, atomic ring swap

Continuation 31 October 2026: **Slots 442/0 in real Unity; full regression 60 groups / 43,164 checks / 0 failures.** See [D3bc-20261030.md](D3bc-20261030.md) for fixes, evidence, current limitations and remaining acceptance. The original implementer status below records the state at handoff.

Original handoff status: IMPLEMENTED, NOT YET RUN. Written without a Unity editor or compiler: nothing was compiled and no gate was executed. Run the gates below first and expect to fix small compile errors.

Scope is D3b only (D3c: weapon sets, utility belt, move penalty, potions).

## What changed

| Area | Change |
| --- | --- |
| `Runtime/Data/ItemData.cs` | `EquipmentSlot` appended: Hands 7, Feet 8, Necklace 9, RingLeft 10, RingRight 11. `Legs` (5) stays so ints are stable. |
| `Runtime/Data/EquipmentSlots.cs` (new) | `IsRing`, `IsRealSlot`, `OtherRing`, `Accepts(authored, target)` (ring fits either ring slot), `CandidatesFor`. |
| `AffixVocabulary.cs`, `ItemInstanceAuthority.cs` | `AffixCategory.Jewelry = 1<<4`, `All` = 31; resolver: Hands to Armor, Feet to Boots, Necklace and rings to Jewelry. |
| `GearScore.cs` | Weight 1 for the five new slots. |
| `ContainerSnapshot.cs` | `CurrentSchemaVersion` 3; step 2 to 3 renames equipment `Legs` holding `footpad_boots` to `Feet` (a v2 equipment naming a new slot is refused); v1 chain runs 1 to 2 to 3; integrity is checked on the original under the old rule. |
| `ContainerSnapshotValidator.cs` | Uses `EquipmentSlots.Accepts`. |
| `Equipment.cs`, `InventoryCommand.cs`, `LocalInventoryAuthority.cs` | Equip with explicit target slot; with both rings full an explicit target is required. New `SwapEquipment = 6` command swaps the two ring slots atomically (uids conserved; all checks before the first write; refusal changes nothing), through sequence, ExpectedRevision, owner binding and uid preconditions. |
| UI (`InventoryScreenController.cs`, `ItemTooltip.cs`) | Paper doll shows 11 rows; slots shrink to 32px with gap 4 to fit; ring labels; CharacterStatsView picks the stats up through the stat source. |
| Data | `Item_FootpadBoots` equipSlot 5 to 8; new `Item_GraspwrapGloves`, `Item_EmberbeadNecklace`, `Item_CinderBandRing` (+ meta); catalog lists them; nine general affixes allowedCategories 15 to 31. |
| `EmberdelveAssetLibrary.cs` | Idempotent `ApplyFeetSlot()`, builders for the new items, jewelry-affix Apply, `BuildJewelerLoot()`. |
| `Editor/EmberdelveSlotsGate.cs` (new) | `Emberdelve.EditorTools.EmberdelveSlotsGate.Run`. |

## Decisions

- Legs kept in the enum (dead slot, still has a UI row).
- A ring is authored as RingLeft; `Accepts` lets it into RingRight.
- Jewelry affixes: the nine general stat affixes only (9 keys, enough for a Unique).
- New loot table `jeweler_cache` is referenced by no chest, so LootGoldenCapture parity is untouched.
- Data was hand-authored in YAML and the builder reproduces it idempotently (gate double-builds and compares bytes).
- Gate is two-phase: edit-mode pure checks plus a play-mode harness, because the command authority is bound to a live rig. One final line, exit only via EmberdelveGateExit.Exit.
- Networking untouched.

## Literal changes (old to new)

| Gate / file | Old | New |
| --- | --- | --- |
| AffixDefinitionGate bad category flags | 16, 17 | 32, 33 |
| AffixCategories.All; general affix allowedCategories | 15 | 31 |
| RarityLadderGate | schema 2 literals, boots Legs, "four families" | V3 literals, v1/v2 migrate to V3, Feet, "five families" |
| ContainerSnapshotGate | schema 2; newer version 3; old list {0,1,3,7,-3}; bad slot names "Hands", "Legs "; boots Legs | schema 3; newer version 4; {0,1,2,4,7,-3}; "Gloves", "Feet "; boots Feet; legacy docs built by renaming Feet to Legs; new 2 to 3 checks |
| CloakSlotGate | SlotCount 7, Legs | 12, Feet, Describe "Feet=Footpad Boots" |
| ItemCatalogGate | boots Legs | Feet plus new-slot item check |
| InventoryUiChecks | six rows | eleven rows, boots click Feet, extended gear list |
| RolledAttributes, InventoryStackId, ChestInstance, FlatDamageAffix, LootPipeline, RuntimeSmoke (+message text), AttributeAction, AttributeVitals, NumericAffix, LoadoutRevision, InventoryAuthority | real boots `EquipmentSlot.Legs` | `EquipmentSlot.Feet` |

## Gates to run

`EmberdelveSlotsGate.Run` (new), then the full regression: RarityLadder, ContainerSnapshot, AffixDefinition, CloakSlot, ItemCatalog, InventoryUiChecks, InventoryAuthority, LoadoutRevision, LootPipeline, LootGoldenCapture, NumericAffix, and the others listed above.

## Risks

- Nothing compiled or run. Checksum literals were computed offline with a Python replica of the canonical body.
- Hand-authored YAML must match the builder output; idempotency is gate-checked.
- Inventory layout at 32px slots not verified visually.
- No UI gesture for ring swap (unequip then equip); only the authority command.
- Schema 3 saves cannot be read by older builds.
- `Loot_JewelerCache` is not wired to any chest.
- Legs remains a dead slot with a UI row.

## D3c leftover

Two weapon sets with swap, utility belt quick slots, move penalty, potions.
