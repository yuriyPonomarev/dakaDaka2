# D3a rarity ladder, snapshot schema 2 and gear score

Continuation 31 October 2026: **RarityLadder 623/0 in real Unity; full regression 60 groups / 43,164 checks / 0 failures.** See [D3bc-20261030.md](D3bc-20261030.md) for fixes, evidence, current limitations and remaining acceptance. The original implementer status below records the state at handoff.

Original handoff status: IMPLEMENTED, NOT YET RUN. Written without a Unity editor or compiler available: nothing here has been compiled, no gate or NUnit test has been executed. Run the gates listed under "Gates to run" before accepting. Base: HEAD 1159ade.

Scope is D3a only. Slots, weapon sets, utility belt, move penalty and potions are D3b/D3c (plan at the end).

## What changed

| Area | Change |
| --- | --- |
| `Runtime/Rules/ItemRarityRules.cs` (new, Emberdelve.Rules) | Pure rules over the stored int: table of the seven tiers, ordinal (Poor 0 .. Unique 6), affix count (0/0/1/2/3/4/5), names, `TryCompare`, `IsAtLeast`. Unknown ints are refused (-1 / false), never clamped. Also `GearScoreRules`: points Poor 1, Common 2, Uncommon 3, Rare 5, Epic 8, Legendary 13, Unique 21; `TryScoreItem`, atomic `TryTotal`. |
| `Runtime/Data/AffixVocabulary.cs` | `AffixRarity` = Common 0, Uncommon 1, Rare 2, Epic 3, Legendary 4, Unique 5, Poor 6. `AffixRarities` is now a typed view over `ItemRarityRules` and adds `Ordinal`, `TryFromOrdinal`, `Compare`, `IsAtLeast`, `RollsAffixes`, `Name`, `ByRank`. |
| `Runtime/Data/LootTable.cs`, `Runtime/Items/LootTableRollService.cs` | Optional per-table `rarityLadder` (`RarityWeight[]`), own seeded stream `RarityStream = 4`, one draw per worn-gear unit. `RequestsRolledGear` now means "tier rolls affixes" (Poor and Common do not). New validation (table `Validate()` and roll-time preflight). |
| `Runtime/Items/ContainerSnapshot.cs`, `LoadoutSnapshot.cs` | `CurrentSchemaVersion` 2, `MinSupportedSchemaVersion` 1. Production migration 1 to 2 registered in `ContainerSchema` (v1 integrity verified under the v1 checksum rule, rarity must be 0..2, re-sealed as v2). |
| `Runtime/UI/ItemTooltip.cs` | Colours: Poor grey, Common white, Uncommon green, Rare blue, Epic purple, Legendary orange, Unique gold; names through `AffixRarities.Name`; unknown tier is painted neutral. Inventory worn-slot labels and tooltips already go through these. |
| `Runtime/Items/GearScore.cs` (new), `RunResult.GearScore`, `RunDirector.CompletePlayer`, `LobbyScreenView` | Slot weights (weapon 2, other worn slots 1), score of worn gear at the end of a run, shown top right in the lobby as "Gear score  N". |
| `Editor/EmberdelveRarityLadderGate.cs` (new) | `Emberdelve.EditorTools.EmberdelveRarityLadderGate.Run`. |
| `Tests/EditMode/ItemRarityRulesTests.cs` (new) | NUnit: `ItemRarityRulesTests`, `GearScoreRulesTests` (literal expectations). |

No data asset was touched (no `.asset` changed, so `EmberdelveAssetLibrary` needs no new Apply* step). No shipped loot table uses a ladder yet, which is what keeps the golden capture byte-identical for existing seeds.

## Decisions

1. Stored ints and ordinal: as decision C7. The ordinal is the only thing used for order. Audit: the only relational operators that touched `AffixRarity` were the range test in `AffixRarities.IsKnown/IsKnownValue` (replaced by a table lookup) and the `!= Common` test in `LootEntry.RequestsRolledGear` (replaced by `RollsAffixes`). No sort over rarity existed. `AffixRarities.Compare` ranks an unknown value below every known tier so a sort never throws.
2. Pool too small: REJECT, do not cap. The roller already refused a pool smaller than the count ("never lowered to fit"); that stays. The real catalog is gated to have >= 5 distinct keys per eligible family: Weapon 11 (9 shared + melee + projectile), Armor 10 (9 + armor), Boots 11 (9 + armor + move speed), Cloak 11 (same). A loot table whose catalog lists fewer definitions than its deepest tier fails `Validate()` and the roll preflight.
3. "Primary stats never roll": the fixed fields of `ItemData` (armour, weapon damage, authored modifiers and attribute bonuses) are never an affix target and never change with rarity. Rolled affixes are additive records on the instance, drawn from the allowlisted `AffixStatKey` set. The gate serialises every item and affix definition before and after all rolls and compares, and checks the tooltip's fixed lines are identical across all seven tiers of one item. The existing armor/melee/projectile affix keys stay as they were (they are allowlisted affixes, not the base value).
4. Loot: per-table ladder, replacing the entry's rarity for worn-gear units only (carried goods never roll). Rungs are looked up by tier, so authoring order cannot change an outcome. No ladder means zero new draws and zero new code on the path, so old seeds are unchanged by construction. The ladder draw happens after the shape loop and uses `Fork(4)`, so which items drop is identical with and without a ladder (gate-checked).
5. Schema: a version bump with an identity migration, plus a hard rule that a version 1 document may only name rarity 0, 1 or 2. A v1 document naming 3..6 was not written by v1 and is refused rather than reinterpreted. Version 2 documents accept 0..6 and the validator rejects 7 and above ("not a known rarity").
6. Gear score is computed from the worn instances' rarity. The lobby has no persistent stash or profile yet (U4C), so it shows the gear score of what the player wore when the last run ended (`RunResult.GearScore`, 0 before any run). Inventory header display was optional and is not done.
7. Colours: Common moved from blue-grey to white to make room for grey Poor. Uncommon and Rare keep their old values.

## Literal changes in existing gates (old to new, and why)

| Gate | Old | New | Reason |
| --- | --- | --- | --- |
| `EmberdelveItemInstanceGate` (unknown rarity loop) | `{-1, 3, 99}` | `{-1, 7, 99}` | 3 is Epic now; 7 is the first unknown int. |
| `EmberdelveAffixDefinitionGate` (unknown rarity loop) | `{-1, 3, 99}` | `{-1, 7, 99}` | same. The `0..2 -> count == i` check is untouched (still true for stored ints 0..2). |
| `EmberdelveLootGoldenCaptureGate` negative `invalid-rarity-profile` reason text | `... not a known rarity (0 Common, 1 Uncommon, 2 Rare); an unknown rarity ...` | `... not a known rarity (0 Common, 1 Uncommon, 2 Rare, 3 Epic, 4 Legendary, 5 Unique, 6 Poor); an unknown rarity ...` | The message lists the stored ints, and there are seven now. Only this one string changed. All six positive table cases, 24 drops and their UIDs/affix literals are untouched, because nothing on their path changed. |
| `EmberdelveContainerSnapshotGate` | schema version literal 1, `Rep(... "schemaVersion":1 ...)`, in-memory versions `{0,2,7,-3}`, newer = 2 | current version read from `ContainerSnapshot.CurrentSchemaVersion` (2); older/newer loops now `{0,1,3,7,-3}` and `{0,3,99,-1,int.Max}`; "version 2 is newer" became "version 3 is newer"; the v0 test migration now ends through the production 1 to 2 step; new `ProductionMigrationChecks` | The schema is 2. Same refusals, shifted by one. v1 documents are built by writing the current model at version 1 and sealing it (identical layout and checksum rule). |
| `EmberdelveInventoryUiChecks` rarity colour loop | Common, Uncommon, Rare | all seven tiers (`AffixRarities.ByRank`) | Same distance/contrast thresholds. |

Unchanged on purpose: `EmberdelveCloakSlotGate` colour checks compare to the `ItemTooltip` constants, so the new Common value passes without edits; `EmberdelveContainerSnapshotGate` "impossible rarity 7 / -1" stay unknown.

## Gates to run

New: `Emberdelve.EditorTools.EmberdelveRarityLadderGate.Run` (final line `[Emberdelve][RarityLadder] PASS|FAIL checks=N failures=M`) and the EditMode suite (`ItemRarityRulesTests`, `GearScoreRulesTests`).

Regression, in this order: `EmberdelveContainerSnapshotGate`, `EmberdelveItemInstanceGate`, `EmberdelveAffixDefinitionGate`, `EmberdelveLootRollerGate`, `EmberdelveLootGoldenCaptureGate` (literal parity, only the one negative reason line changed), `EmberdelveLootPipelineGate`, `EmberdelveChestInstanceGate`, `EmberdelveItemCatalogGate`, `EmberdelveAffixProfileGate`, `EmberdelveGearInstanceGate`, `EmberdelveCloakSlotGate`, `EmberdelveInventoryUiChecks` (inventoryui smoke), `EmberdelveRuntimeSmokeTest` (lobby shows the run summary), then the full sweep.

## What the new gate covers

Stored ints, ordinals and counts as literals; no Artifact; Compare/IsAtLeast over all 49 pairs; raw int sort shown to be wrong; for every real gear piece x every tier x 24 seeds: exact affix count, distinct ids and keys, allowlisted keys, accepted values, same seed same roll, no draws for Poor/Common, rolls vary; minted instance, snapshot int, exact restore, fixed tooltip lines identical across tiers; pool size >= 5 per family; a four-affix pool refuses Unique and mints nothing; instance-level count/duplicate/unknown-tier refusals; literal v1 bag, equipment and loadout documents (checksums computed outside Unity) migrate to literal v2 documents and pass the full validator; edited v1 refused; unknown tiers 7, 8, 99, -1 rejected; one item of each tier round-trips container, JSON and restore; wrong counts rejected in a snapshot; ladder tables (300 seeds: all seven tiers appear, determinism, order independence, shape unchanged, bad ladders refused with nothing minted); colours; gear score literals (0, 14, 7, 147, 35) and the lobby label.

## Risks

1. Not compiled or run. The gate assumes the real catalog has gear for all four families (it has, by the asset list) and that the literal v1/v2 documents (boots, cloak, mantle, coins) pass the validator against real item footprints; if the validator disagrees the gate prints the reason.
2. `LobbyScreenView.Build` is called in edit mode by the gate (builtin font, no EventSystem). If that proves environment-sensitive, drop that block from the gate; the runtime path is unaffected.
3. A v2 build cannot be read by an older build (schema 2). Expected.
4. Tooltip height: a Unique item shows 5 rolled lines; the tooltip panel sizes from the line count and is clamped into the satchel panel, but it was not checked at small resolutions.
5. Gear score weights and points are a tuning proposal (Fibonacci points, weapon x2), not parity data.
6. No shipped table uses a ladder. Enabling one on a chest changes drops for that table and needs a deliberate golden re-baseline.

## D3 split plan (not started)

**D3b slots and rings.** Append `Hands`, `Feet`, `Necklace`, `RingLeft`, `RingRight` to `EquipmentSlot` (append-only; add GearScore weights 1 each). Move boots to `Feet`: `Item_FootpadBoots.asset` is `equipSlot: 5` (Legs) and snapshots store the slot NAME, so this needs schema 3 with a migration that rewrites an equipment entry `Legs` holding `footpad_boots` to `Feet` (plus a gate with a literal v2 save). Extend affix categories (new jewelry flag bit, an idempotent Apply* on the 13 affix assets so existing assets get it), `ItemInstanceAuthority.TryFromEquipmentSlot`, `Equipment` slot table and hands/feet in `InventoryScreenController` and `CharacterStatsView`. Gates: ring swap is atomic across both ring slots (UID conserved, refused swap changes nothing), each new slot equips/unequips with no stat accumulation, migration literal.

**D3c weapon sets, belt, penalty, potions.** Two weapon sets with a swap key in `ClassLoadout`/`Equipment` (state saved in the loadout snapshot, so another schema bump); utility belt bound to item ids (replaces the Health F6 hotbar stack-index binding in `HotbarController`); per-item move-speed penalty as authored data on `ItemData`/`WeaponData` feeding the existing move-speed stack (D1b multiplier); potions heal over time with a use channel (cancel on move/hit). Gates: speed changes with gear, belt survives restore by item id, swap is atomic, potion channel cancel and exact-once heal.
