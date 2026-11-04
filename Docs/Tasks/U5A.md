# U5A - weapons and shields as equipment (hands) (4 Nov 2026)

Status: implemented, uncommitted. Server authoritative; every move uses the existing sequenced inventory command path.

## Rules (as implemented)
- Weapons and shields are ItemInstances with UIDs, equipped, unequipped, swapped and dropped through the same UI and `LocalInventoryAuthority` path as clothing. The class kit is minted as starting items (uids) into Weapon, OffHand and AltWeapon at `ClassLoadout.Apply`. Hand slots per weapon set; the set swap (1/2) is kept.
- Item mode is on only when the config names `unarmedWeapon`, the bag has a catalog and every kit weapon has a catalog item granting it. Otherwise the old class-fallback hands stay (bare test fixtures unchanged).
- Handedness is data (`WeaponData.Hands`, derived: shield role = OffHandOnly, `twoHanded` = TwoHanded, else OneHanded) and validated on the server in `Equipment.TryCheckHandRules`: a one-handed weapon only to a main hand (no dual wield), a shield only to an off hand, nothing in the off hand while the same set's main hand holds a two-handed weapon. Equipping a two-handed weapon moves a worn off-hand item to the bag; if the bag is full the equip is refused and nothing moves. Saved containers are validated by the same rules on restore.
- Empty main hand = the `unarmed_fists` weapon (damage 6, not an item, cannot block or parry). Empty off hand = nothing. In set 2 an empty main hand is nothing.
- Drop (`InventoryCommandKind.Drop = 8`): bag stack or worn slot, `ExpectedSourceGearUid` mandatory. The server removes the item and spawns a ground pickup 1.1 m in front of the rig (through `IGroundItemSink`, the RunDirector), rolling the removal back if the ground refuses. Q while the satchel is open drops the item under the cursor.
- Pickup goes to the bag; a weapon or shield picked up is also seated in the empty matching hand of the live set (never displaces). `LootPickup` has a taken flag so two same-frame takers cannot both win.
- Combat, guard, riposte, FP arms and HUD read the current hands. `DefenseProfile` and `RiposteRules` are unchanged; a shield only contributes while the main hand is not two-handed.
- `Equipment.Clear()` now resets to the starting kit (fresh uids); `ClearToBare()` empties the hands too.

## Data
New: `Weapon_UnarmedFists`, `Pattern_FistPunch`, catalog items `ashen_short_sword`, `hollow_bow`, `ember_crossbow` (17 catalog items); `GameConfig.unarmedWeapon`; `WeaponData.Hands`, `isUnarmed`; unarmed_fists entry in the weapon view-model set.

## Files
- Runtime: `Data/WeaponData.cs`, `Core/GameConfig.cs`, `Items/Inventory.cs`, `Items/Equipment.cs`, `Items/InventoryCommand.cs`, `Items/LocalInventoryAuthority.cs`, `Items/LootPickup.cs`, `Flow/RunDirector.cs`, `Player/ClassLoadout.cs`, `Player/PlayerRig.cs`, `UI/InventoryScreenController.cs`.
- Editor: `EmberdelveAssetLibrary.cs`, `EmberdelveWeaponModels.cs`, new `EmberdelveHandsGate.cs`; `Tools/gate-sets.json` (Hands in combat and all).
- Tests: new `Tests/EditMode/Combat/HandsRulesTests.cs` (6).
- Existing gates adjusted only where the kit moved into items (command count 9, `ClearToBare()` for bare-slot fixtures, fists after unequipping the blade, class shield restored explicitly after `SetOffhandWeapon(null)`).
- `EmberdelveRuntimeSmokeTest`: the defense enemy is now chosen by data (nearest living enemy whose weapon the guard holds against) instead of nearest, see Checks.

## Checks
- EditMode 702/0 (696 + 6 `HandsRulesTests`: Hands derived from data, fists never an item, slot matrix, non-hand slots exempt, guard and riposte follow the hands, one uid one item).
- Gate `Hands` 92/0 (play mode, Level01): kit as items, fists, sword alone and sword+shield guard and riposte, drop shield and pickup (out of reach, auto-seat, two takers, uid census), forged commands (wrong owner, random uid, bad slot, no identity, unknown stack, wrong hand, replay, duplicate-uid ground item), bow with full bag refused without moving anything and with room stowing the shield, shield beside bow refused, drop main hand leaves fists, clear restores the kit.
- RuntimeSmokeTest flake: the defense probe took the nearest living enemy; when that was the Ashen Warden (maul impact 30 over the guard's resist 20) the guard broke by design, the 0.6 block expectation and the riposte real path failed and the player died, cascading into 42 failures. It passed alone and failed in the full run depending on chase timing. Now deterministic: the probe enemy is picked by the data rule, with an explicit precondition check.

## Compromises
- No network proof of a client dropping an item and another client seeing it: there is no network inventory or replicated ground item yet (U4A). Proposed: owner-sent Drop/Pickup RPC and a server-spawned NetworkObject world item; the authority code already takes a ground sink.
- The two new weapons (one-handed arming sword, two-handed sword) are U5B content; the hand rules and `Hands` already support them.
- Ranged weapons were already two-handed, so the warrior's crossbow set has no shield.
- FP arms and HUD follow the hands (`RefreshHands`) but were only gate-verified, not played.
- The name U5A was already used by the licensed-assets row in the roadmap; that row and U5B were renumbered U7A/U7B.
