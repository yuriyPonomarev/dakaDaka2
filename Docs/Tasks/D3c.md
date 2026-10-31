# D3c weapon sets, utility belt by identity, move penalty, timed potions (schema 4)

Continuation 31 October 2026: **LoadoutD3c 188/0 in real Unity; full regression 60 groups / 43,164 checks / 0 failures.** See [D3bc-20261030.md](D3bc-20261030.md) for fixes, evidence, current limitations and remaining acceptance. The original implementer status below records the state at handoff.

Original handoff status: IMPLEMENTED, NOT YET RUN. Written without a Unity editor or compiler: nothing was compiled and no gate was executed. Run the gates below first and expect to fix small compile errors.

Closes slice D3 (PLAN_CLAUDE row 5, decision C8, DND_PARITY rows Equipment slots, Weight, Healing).

## What changed

| Area | Change |
| --- | --- |
| `Runtime/Data/ItemData.cs` | `EquipmentSlot` appended: AltWeapon 12, AltOffHand 13. New authored fields `moveSpeedPenalty` (0..100 points), `useChannelSeconds` (0..10), `healOverTimeSeconds` (0..60), all default 0. `HasTimedUse`. |
| `Runtime/Data/EquipmentSlots.cs` | `WeaponSetCount = 2`, `MainHandOf`, `OffHandOf`, `SetOf`, `IsMainHand`, `IsOffHand`. `Accepts`: a Weapon item fits Weapon and AltWeapon, an OffHand item fits OffHand and AltOffHand, an item can never AUTHOR an Alt slot. |
| `Runtime/Items/Equipment.cs` | Active weapon set (`ActiveWeaponSet`); stats of the inactive set are masked out (`IsSlotLive`, masked `WornInstances`); `MoveSpeedPenaltyPoints`; `TrySwapWeaponSet` (all checks before the first write); restore hooks (`WouldHoldWeapon`, `ApplyRestoredWeaponSet`); a swap cancels a drink. |
| `Runtime/Player/ClassLoadout.cs` | Set N main hand is ClassLoadout slot N. The swap key calls `RequestWeaponSetSwap`, which submits a `SwapWeaponSet` command through the bag's authority (direct `SwitchWeapon` only when no authority exists). `WeaponInSlot`, `ClearSecondaryWeapon`. `SwitchToSlot`, `ForceSwitchToSlot`, `TryReplaceWeaponSlot` keep their meaning. |
| `Runtime/Items/InventoryCommand.cs`, `LocalInventoryAuthority.cs` | New command kind `SwapWeaponSet = 7` (sequenced, owner-bound, ExpectedRevision, uid preconditions on the active and the other main hand). `UseStack` gets the timed branch. |
| `Runtime/Items/ConsumableChannel.cs` (new), `Runtime/Rules/ConsumableRules.cs` (new) | The use channel and heal over time; pure arithmetic. |
| `Runtime/Items/HotbarController.cs`, `Inventory.cs` | `BeltIdentity`; the belt captures identities before a restore clears the bag and rebinds them afterwards. Live bindings stay StackId based, so a retired id is never revived. |
| `Runtime/Items/ContainerSnapshot.cs`, `LoadoutSnapshot.cs` | `CurrentSchemaVersion` 4; migration step 3 to 4; the loadout wrapper gains `weaponSet` and `belt`. |
| `Runtime/Combat/CombatFlowRules.cs`, `Runtime/Player/PlayerRig.cs` | New `HeldFactor(main, off, offContributes, wornPenaltyPoints)`; the 3-argument form delegates with 0. `GetMoveFlowFactor` passes the worn points, so gear reaches `ResolveGroundSpeed` through the one existing factor. |
| UI (`ItemTooltip.cs`, `HudController.cs`) | Slot labels "Main hand II" and "Off hand II"; "Move speed -X%" and timed-potion lines; the HUD weapon label shows "(II)" when set 2 is live. The paper doll enumerates slots, so it shows 13 rows. |
| `Editor/EmberdelveLoadoutD3cGate.cs` (new) | `Emberdelve.EditorTools.EmberdelveLoadoutD3cGate.Run`. |

No input action was added: the existing SwapWeapon action (Q, right shoulder) is the weapon-set swap. No data asset was authored or changed.

## Decisions

- **Sets are ClassLoadout slots 0 and 1.** Set 1 is the original Weapon + OffHand, so every v3 save already means "set 1, set 2 empty". An empty hand falls back to the class asset (primary for set 1, secondary for set 2, `offhandShield` for the off hand), exactly as an empty Weapon slot always did.
- **Alt slots are enum slots with masking** (append-only ints), so the snapshot, validator, paper doll and the authority needed no parallel structure. Worn gear in the inactive set simply does not count.
- **Swap time: none.** The swap is instant and atomic; the existing action lock (swing, draw, reload) is the only gate. A swap commits one revision and one notification.
- **A cancelled drink KEEPS the potion.** The unit is consumed at channel completion, in one revision; an interrupted drink consumes nothing and heals nothing. A drink also ends without consuming when the player is already at full health. One drink at a time; heals over time may overlap.
- **Heal over time:** linear, exactly `healAmount` in total once, paid out once consumed; a hit after the drink does not stop it. Default authoring (channel 0, heal over time 0) is the old instant heal, unchanged.
- **Channel cancel causes:** moving faster than 0.6 of walking speed (`ConsumableRules.MoveCancelRatio`), being hit, an attack press, a swing/draw/reload in progress, a weapon-set swap, death or leaving play.
- **Penalty:** worn non-weapon items add `moveSpeedPenalty` points to the held weapons' points before the single `/300` and the single 0.5 floor. A weapon item's own number is its WeaponData's and is not double counted. Default 0, nothing authored.
- **Belt identity:** the uid for an instance-backed stack, otherwise the item definition (carried stacks are fungible; two keys of one definition take two different stacks in bag order). Snapshot spelling `""`, `"i:" + itemId`, `"u:" + 32 hex`. Full-loadout restore preserves the saved belt exactly, including an explicitly empty belt (also the default produced by legacy migration). Standalone bag restore keeps its separate identity/default seeding behavior. This corrects the six exact-roundtrip failures found during the continuation.
- **Schema 4 is wrapper-level.** Containers do not change; the loadout gains `weaponSet` and `belt`. A v3 equipment naming an Alt slot is refused. A v3 loadout is migrated to weaponSet 0 and an empty belt. Legacy integrity checks run on the original document (a loadout under schema 4 serializes without the two new members).
- Networking untouched.

## Literal changes (old to new)

| Gate | Old | New |
| --- | --- | --- |
| RarityLadderGate | schema 3 literals `BagV3`, `EquipmentV3`, `LoadoutV3`; current schema 3; v1/v2 migrate to V3 bytes | `BagV4`, `EquipmentV4`, `LoadoutV4` (checksums computed offline: bag `949612a0...aa81`, equipment `cb0c2f7b...db5c46`, loadout `13859d65...ed11c`); schema 4; v1/v2 migrate to V4 bytes (weaponSet 0, empty belt) |
| SlotsGate | `EquipmentV3`/checksum; slot count 12; unknown slot value `(EquipmentSlot)12`; command kinds 7; snapshot schema 3 | `EquipmentV4`/checksum recomputed; 14 (+ AltWeapon 12 and AltOffHand 13 asserted); unknown slot `(EquipmentSlot)14` (3 places); kinds 8 with `SwapWeaponSet == 7`; schema 4; wrong-slot attempts for the Alt slots |
| CloakSlotGate | `SlotCount == 12` | 14 |
| ContainerSnapshotGate | current 3; refusal lists "4", `{0,1,2,4,7,-3}`; loadout in-memory 4; equipment newer 4; "3.0", `"3"`; migrated schemaVersion 3; "a version 4 document is still newer" | current 4; "5", `{0,1,2,3,5,7,-3}`; 5; 5; "4.0", `"4"`; 4; version 5. The loadout fixture forces `weaponSet 0` and an empty belt then re-seals; synthetic v0 loadouts omit the two members |
| InventoryUiChecks | restore retires the old hotbar binding and binds the defaults (key 1 = draught) | restore follows the draught to its restored stack by identity (key 4 follows it, key 1 is not auto-bound); paper doll row count 11 to 13 and the gear list gains AltWeapon, AltOffHand |

## Gates to run

`EmberdelveLoadoutD3cGate.Run` (new; final line `[Emberdelve][LoadoutD3c] PASS|FAIL checks=N failures=M`), then the regression set: RarityLadder, Slots, CloakSlot, ContainerSnapshot, InventoryUiChecks, InventoryAuthority, InventoryStackId, LoadoutRevision, KitState, CombatFlow, GearInstance, ItemCatalog, LootPipeline, AffixDefinition and the rest of the D3b list.

The new gate covers: speed with gear (exact multiplier, unequip restores it, floor); the swap (atomic, refused while swinging with every observable unchanged, replay and stale identity refused, the active set drives `ActiveWeapon`, the inactive set's stats are not counted, snapshot and restore carry the set); the belt surviving a bag reorder and a restore by identity; the literal v3 to v4 migration; a drink cancelled by hit, moving, attack, a swing and a swap leaving no heal with the potion kept; a completed drink consuming exactly one and healing the exact total once.

## Risks

- Nothing compiled or run. Checksum literals were computed offline with a Python replica of the canonical body.
- The paper doll now has 13 rows at a 26px slot size; not verified visually.
- A swap now commits a revision, so a swap is visible to the shared revision observers.
- The swap key goes through the authority and may be refused where the old direct swap worked (for example when the run director does not admit the actor).
- No UI gesture for the swap beyond the key; no UI to assign the belt except the existing hotbar assign path.
- The gate's swing check needs the level's class to hold a swept-melee primary and a second weapon; it reports a failure rather than skipping otherwise.
- Penalty and potion content is not authored; the gate sets values in memory and restores them.
- Schema 4 saves cannot be read by older builds.
