# Codebase health review: 20 Oct 2026

Reviewer: independent, fresh context. Read-only, static reading of `Assets/Emberdelve/**` plus the docs. I ran nothing: no Unity, no compile, no gates. Every claim below comes from source with file:line references. I ignored `Editor/EmberdelveAssetLibrary.cs`, because a worker is editing it right now.

## Verdict: REFACTOR-FIRST (no rewrite)

**Keep as is.** The item-identity core is strict and well tested:
- `ItemInstance`, `ItemSnapshot`, `ItemInstanceAuthority`, `LootRoller` and `AffixDefinition` validation.
- `DamageResolver` math (guard, then armor).
- The grid geometry in `Inventory` and its atomic equip/unequip transfer.
- `AttributeRules`.

The gates for these use hard-coded expected numbers and real rejection matrices. Examples: `EmberdelveItemInstanceGate` tampering cases, and `EmberdelveAttributeSkillGate` `ExpectedCryDamage = 18.816f`. They are not tautological.

**Not ready for networking.** The simulation layer assumes one local player. The "authority" also believes several fields the client chooses. Building networking on top would force rewrites of the same files anyway, so do them first as small, planned refactors.

**What can go ahead now:**
- **P3 cloak slot and tooltips:** yes, with the constraints in the "Order vs PLAN_CLAUDE" section.
- **P5 item snapshot persistence:** not yet. It needs R3 (one runtime item catalog) and R9 (container snapshot) first.
- **U3 networking code:** not yet. It needs R0–R7. R8 and R10 are strongly advised. The research half of the stack choice can run in parallel.

---

## A. Must fix before networking (ranked)

### F1 Critical: the combat authority believes values the client chooses
`LocalCombatAuthority` validates the sequence, alive state, team and line of sight (LOS). It still takes these inputs on trust:

| Trusted input | Where | How a client abuses it |
|---|---|---|
| Weapon object | `CombatContracts.cs:18,57`. `LocalCombatAuthority.cs:76` only null-checks it and never checks that the instigator holds it. | Submit an `AttackIntent` naming any `WeaponData` asset (the strongest one, or an enemy claw). Damage resolves at that weapon's stats. |
| `MaxRange` | `CombatContracts.cs:22,60`. Range check at `LocalCombatAuthority.cs:105-108,186-189`. | Claim `MaxRange=1000` and every distance passes. |
| `HitPoint` | `LocalCombatAuthority.cs:105`. Range is measured Origin→HitPoint. LOS (`:112`) uses the target's centre. Nothing ties HitPoint to the target. | Set HitPoint = Origin. Any hostile with LOS anywhere on the map is hit, with zero falloff (`DamageAtRange(0)`). |
| `Headshot` flag | `DamageAndScanning.cs:35,83` | Always claim a headshot for ×1.8 damage. |
| `Timestamp` | `LocalCombatAuthority.cs:93,98,171`. Cooldowns are measured on the client's own clock. | Send increasing fake timestamps and cooldowns no longer apply. A far-future stamp also blocks legitimate swings until real time catches up. |
| `SkillData` object, `ActivationId` | `SubmitSkill` (`:138` onward) never checks that the skill is in the caster's kit, nor mana or ammo. `ActivationId` is client-allocated (`CombatContracts.cs:192`). `sameActivation` (`:161`) skips the cooldown check. | Re-send an old activation id with a fresh sequence and the skill cooldown is never checked. |
| `EffectScale` | `CombatContracts.cs:84`, clamped in `DamageResolver.ClampEffectScale`. | Always claim the 1.4 maximum. |
| Self status | `LocalCombatAuthority.cs:239-240`. Any spec is accepted when source == target. | Grant yourself any buff within StatusSpec bounds. |

AGENTS.md says "input/client intent is not a trusted health/damage/item outcome". This table contradicts that today. Fix: **R1a/R1b**.

### F2 Critical: tests are built on the F1 hole
- `EmberdelveRuntimeSmokeTest.cs:432-496`: 16 "valid" attacks are submitted about 2 frames apart, stamped `baseTime + 5 + i*1.5`. They pass only because the authority trusts timestamps.
- `:302` and `:3410-3419` admit that these synthetic future stamps push the authority's clock forward. Later real swings then have to wait until the clock catches up.
- More forged stamps: `:727` (`Time.time + 30f`) and `:3026` (`Time.time + 60f + …`).
- There is no negative test for: a forged weapon, a forged `MaxRange`, a hit point that is not on the target, a claimed headshot, timestamp forgery, or a skill outside the kit.

Once F1 is fixed, these fixtures go red. AGENTS.md allows rewriting a fixture that is proven wrong. Rewrite them to wait for the real cooldown, or to use an injectable authority clock. Do not keep the hole to keep the test green.

### F3 High (live bug): holding at the extraction gate ignores the loot requirement
1. `Interactor.cs:76` calls `_current.OnHoldComplete()` without checking `CanInteract` again.
2. `ExtractionPoint.cs:62` calls `RunDirector.BeginExtraction()` (`RunDirector.cs:174`).
3. That calls `Complete()` (`:179`), which only checks `State == Running`.

**Scenario:** `requireLootToExtract = true`, loot value 0, player holds E at the gate for 3 s. The run ends as **Extracted**.

**Why the test misses it:** `ExtractionFlow` (`smoke:974-1010`) picks up loot first, then calls `gate.OnHoldComplete()` directly. It never drives the hold through `Interactor` and has no negative case.

**Also missing:** damage and movement do not interrupt the hold. Progress decays (`Interactor.cs:79-83`) instead of resetting. Both are U4B design requirements.

### F4 Critical (architecture): the code assumes exactly one local player
- **Interactions have no actor.** `IInteractable.OnHoldComplete()/OnInteractInstant()` take no parameters (`GameContracts.cs:212-218`). Chests give loot to `ILootHost`, which is `RunDirector`, which holds a single `playerRig` (`RunDirector.cs:30`, `TryCollectLootInstance:276`). With two players, chest loot goes to that one rig no matter who opened the chest. Distance, LOS and alive checks for the actor can't be done because the actor isn't known.
- **Every player is "LocalPlayer" on the same team.**
  - `PlayerRig.Kind` is hard-coded `LocalPlayer` (`PlayerRig.cs:55`), and every player is on `PlayerTeam` (`:57`).
  - `CombatTeams.AreHostile` needs different teams, so **PvP is impossible**.
  - Enemies only target `EntityKind.LocalPlayer` (`EnemyAgent.cs:192`).
  - Kills are counted only through the LocalPlayer-gated `DamageDealt` event (`RunDirector.cs:139-144`).
- **Global events carry no owner id.**
  - `PlayerRig.cs:154` raises the global `PlayerHealthChanged` from every rig.
  - There are 90 `GameEvents.Raise*` call sites in 17 files (Inventory 22, InventoryScreenController 10, SkillCaster 9, RangedCombat 8, …).
  - The authority itself raises HUD events keyed on LocalPlayer (`LocalCombatAuthority.cs:130-131,226-227`).
  - A second rig in the process would drive the local HUD. On a dedicated server there is no LocalPlayer at all.
- **Process-wide static state.**
  - `RunContext.SelectedClass/LastResult` (`RunContext.cs:29-31`).
  - `CombatRegistry` ids and per-entity sequences (`CombatContracts.cs:166-205`) are allocated per process, with no mapping to network ids. Clients draw sequences from the same static allocator the authority validates against.
  - `GameServices` is a single-slot-per-type locator.
  - Only `CombatRegistry` resets on `SubsystemRegistration`. `GameEvents`, `GameServices`, `RunContext` and the `UiFactory` texture cache do not.

### F5 High: anything can create gear, and there is no runtime item catalog
- **Any `ItemData` becomes real gear.** `Inventory.TryAdd/TryAddAt` give any equippable definition a fresh identity (`Inventory.cs:314-325,411-422,592-613`). The trust check uses a catalog built from that same definition (`LocalCatalogFor`, `:622-639`), so it can never fail. It undoes the "must be the registered object" gate in `ItemInstance.TryValidateForCreation` (`ItemInstance.cs:569-577`). Today, any UI, test or future RPC handler that holds an asset can create a new UID.
- **No project-wide catalog exists at runtime.**
  - `LootTable` builds a per-table catalog from its own entries (`LootTable.cs:437-502`).
  - `Inventory` builds one per definition.
  - `ItemInstance.TryRestore` needs a single authority, so persistence has nothing to restore against.
- **Enemy drops use a second loot path.** `EnemyAgent.DropLoot` uses `UnityEngine.Random` with `EnemyData.lootTable : ItemData[]` (`EnemyAgent.cs:162-179`). That bypasses seeds, rarity and instances. Gear only becomes an item when picked up, through the minting path above.
- **Ground pickups can't hold an instance.** `LootPickup` carries only definition + amount. Dropped gear and corpse gear cannot keep their UID or rolls, which blocks U4B corpse loot.

### F6 High: inventory commands use positions and are dispatched from the UI
- **Stack indices are list positions.** They shift whenever a stack is removed (`Inventory.cs:890,1011,1100`).
- **The UI addresses stacks by `(ItemData, origin cell)`** (`InventoryScreenController.cs:695-752`). The comment at `:405-408` still says copies "have no uid".
- **The hotbar uses raw stack indices 0..3** (`HotbarController.cs:30-33`). Keys 1–4 hit the first four stacks in insertion order, gear included.
- **The UI calls mutators directly:** `TryMove`, `TrySwap`, `TryMergeStacks`, `TryEquipFromInventory`, `TryUseSlot` (`InventoryScreenController.cs:388-544`). A network command needs stable ids (UID for gear, a stack id for carried goods) and one command entry point.
- **Potions heal inside the bag.** `Inventory.TryUseSlot` calls `Health.Heal` directly (`:1159-1197`), so the client decides the outcome.

### F7 High: loot is the same every raid
- The per-chest seed is authored in the layout (`ChestInteractable.cs:47-48`, `DungeonLayoutData.cs:52-60`). The same chest gives the same loot every run.
- ITEM_AFFIXES requires different normal raid seeds, owned by the server.
- `System.Random` is deterministic on Mono/IL2CPP. The project still depends on an unspecified algorithm and a hand-made stream derivation (`LootTable.cs:333,361-376,511-514`).
- One positive: `LootRoller` takes its RNG from the caller and sorts pools by id, which is correct.

### F8 Medium-High: no assembly boundaries, circular dependencies, and a test harness that doesn't scale
- **No `.asmdef` anywhere.** All runtime code compiles into Assembly-CSharp, and editor code is isolated only by the `Editor/` folder. I found no `UnityEditor` usage in Runtime.
- **Namespaces depend on each other in cycles:**
  - Data ↔ Items: `LootTable` (Data) uses `ItemInstance`/`LootRoller` (Items), and Items uses Data.
  - Items ↔ Player: `Inventory` uses `PlayerRig`/`RangedCombat`, and `PlayerRig` uses `Inventory`/`Equipment`.
  - Combat → Player: `MeleeCombat`/`RangedCombat` `[RequireComponent(PlayerRig)]`.
  - Combat → Stats.
- **Effect:** the pure rules can't be split out for a headless server simulation or for EditMode unit tests.
- **Tests:**
  - `com.unity.test-framework` 1.5.1 is installed but unused.
  - There are 11 copy-pasted play-mode harnesses, each with `[InitializeOnLoad]` + SessionState + `EditorApplication.Exit` (18 subscriptions in total).
  - Four gates are minified one-liners and practically unreviewable: ItemInstance, LootRoller, ArmorAffix, FlatDamageAffix.
  - Two gates inject private config by reflection (`ArmorAffixGate.cs:29`, `FlatDamageAffixGate.cs:29`).
  - `smoke:70` calls `EmberdelveAssetLibrary.BuildAll()` on every run, so every test run rewrites the shipped data assets.

### F9 Medium: gaps at the snapshot boundary (for the persistence work)
`ItemSnapshot.TryFromJson` handles errors properly: it catches exceptions, refuses null, and validation is all-or-nothing. Still missing:
1. No schema version.
2. No container format for bag cells, carried stacks without a UID, or equipment slots.
3. No duplicate-UID rejection across a set of snapshots.
4. JSON round-trip is only tested with whole-number affixes (`ItemInstanceGate`). Fractional-step affixes (move_speed 0.03–0.12, mana_regen 0.1–0.4) are never sent through JsonUtility and the step validator.
5. A missing `rarity` field silently becomes Common (0).

### F10 Medium: stats are computed by two systems
- `CharacterStatStack` (legacy modifiers) and `StatsComponent` (attributes) are combined by rules spread across:
  - `Equipment.TryNumericStatTarget` (`Equipment.cs:558-569`)
  - `PlayerRig.GetFlatWeaponDamageBonus` (`PlayerRig.cs:441-459`)
  - `PlayerRig.ResolveVitalLimits` (`:224-257`)
  - `PlayerRig.GetArmorRating` (`:478-484`)
- The "don't count this twice" rules exist only in comments. A new affix key means editing three or four places.
- Server-computed, replicated stats need one effective-stats snapshot.

---

## B. Nice to have (not blocking)

- **N1 Per-frame allocations and side effects.**
  - `SkillCaster.Update` calls `CanCastSlot` twice per frame. Each call runs `BuildContext`, which uses up one `CombatRegistry.NextActivationId()`, so a read-only query changes global state (`SkillCaster.cs:135-161, 372`).
  - It also formats reason strings every frame during cooldown or low mana (`:208,220`).
  - Mana and health regen raise `Changed` every frame (`ManaComponent.cs:148-149`, `HealthComponent.cs:115`). The HUD rebuilds the mana string every frame (`HudController.cs:663`).
  - A `Comparer.Create` lambda is allocated per shot (`RangedCombat`, `HitScanner`). Projectile and impact visuals spawn GameObjects and material instances per hit.
- **N2 Dead or test-only APIs to delete:**
  - `Inventory.TryAddToSlot` has no callers.
  - `Inventory.TryAddAt` is used only by tests and a probe. It creates gear and inflates `RecoveredValue`, so tests use a path real gameplay can't reach.
  - `Equipment.TryEquipItem(ItemData)` is used only by tests.
  - Also: `StatsComponent.Rebuild(IReadOnlyList<ItemData>)`, `LootTable.Roll`, `Inventory.Configure(int)`.
  - The three-interface loot host (`ILootHost`/`ICountedLootHost`/`IInstanceLootHost`) plus the bag-delta fallback in `LootCollection` (`LootPickup.cs:8-147`) has only one implementer.
- **N3** `Inventory.ConfigureGrid` **deletes** items that don't fit, although the comment says "left behind rather than silently deleted" (`Inventory.cs:184-205`). `Recompute` silently skips overlapping stacks (`:1259`), which hides corruption. It should assert.
- **N4 RecoveredValue is a lifetime counter.** It is the value ever picked up, not what is carried now. Yet it drives the run result's `LootValue` (`RunDirector.cs:45`), the extraction gate and the Sealed-chest unlock. Banking (U4) must count carried instances, not this number.
- **N5** `GameConfig.EnsureValid` writes to the shared config asset at runtime (`GameConfig.cs:55-66`, called from three `Awake`s). This breaks the "shared definitions are immutable at runtime" rule.
- **N6 UI fragility.**
  - `UiFactory.EnsureCanvas` takes over and renames whatever `FindFirstObjectByType<Canvas>()` returns (`UiFactory.cs:36-46`).
  - `Equipment.SlotCount = 6` is tied to the enum's integer values (`Equipment.cs:50,571-575`), and the UI hard-codes 5 worn rows.
  - `EquipmentSlot.Legs` is used as boots (`ItemInstanceAuthority.cs:59-61`).
- **N7 Comments.** About half of each large file is comment history ("U2B5…U2B8"). That inflates the god-class sizes (Inventory is about 1270 lines) and the comments go stale (`InventoryScreenController.cs:405-408`). Remove task history whenever a file is touched.
- **N8** `EmberdelveReviewBridge` (63 KB) is correctly guarded with `#if DEVELOPMENT_BUILD || UNITY_EDITOR`. Move it into its own asmdef during R8.
- **God classes, split as part of R6/R7:**
  - `InventoryScreenController`: rendering, hit-testing, selection state machine, tooltip, tabs, command dispatch.
  - `HudController`: also owns the inventory toggle input and run-state gating (`:206-224`).
  - `Inventory`: grid, gear creation, consumable effects, loot accounting, events.
  - `PlayerRig`: composition root, defense, three damage providers, stat combination.

---

## C. Refactor tasks (each one reviewable change, ordered by how much they block networking)

### Must-do
| # | Task | Files touched | Acceptance |
|---|---|---|---|
| **R0** | Extraction bypass fix: `Interactor` re-checks `CanInteract` before `OnHoldComplete`/`OnInteractInstant`. `RunDirector.BeginExtraction` re-checks `ExtractionAllowed`. | Interactor.cs, RunDirector.cs, smoke ExtractionFlow | New negative check: a real multi-frame hold through `Interactor` with 0 loot leaves the run `Running`. The existing happy path also goes through `Interactor`. **Do now (tiny).** |
| **R1a** | Authority resolves the weapon itself: intent carries a weapon slot/id, and the authority reads the instigator's equipped weapon through a new `ICombatLoadoutProvider` and rejects a mismatch. Range comes from the resolved weapon. HitPoint must lie within target radius + tolerance, and falloff uses the authority's own target distance. Headshot is re-derived by the authority (head collider ray) or ignored. | CombatContracts.cs, LocalCombatAuthority.cs, DamageAndScanning.cs, Melee/RangedCombat, PlayerRig, EnemyAgent | Negative checks for each forged field. Existing valid damage numbers unchanged. |
| **R1b** | Authority clock and skill rules: cooldowns use the authority's own clock at receipt (injectable for tests), and `Timestamp` is kept for diagnostics only. Skill must be in the caster's kit. Activation ids are issued by the authority when it accepts a cast. `EffectScale` is read from the instigator's `StatsComponent`. A self-status may only come from the caster's validated skill. | same + SkillCaster, skill behaviors | **Rewrite** `smoke:432-496, 727, 3026, 3410-3419` fixtures (proven-wrong fixtures). Negative checks: replayed activation, forged timestamp, out-of-kit skill. |
| **R2** | Actor-aware interaction: `IInteractable` methods receive the acting player (or combat id). Chest, pickup and gate re-validate alive, ≤3 m distance and LOS at completion, and deliver to *that* actor's inventory. Damage and movement interrupt the hold. | GameContracts.cs, Interactor, ChestInteractable, LootPickup, ExtractionPoint, RunDirector | Two rigs in one scene: B opens a chest and B receives the loot. Interrupted hold resets to 0. |
| **R3** | One runtime `ItemCatalog` asset (all `ItemData` + `AffixDefinition`), built by the asset library. Create `ItemInstanceAuthority` once per run. `LootTable.TryRoll(seed, authority, issuer, …)` stops building its own catalog. `Inventory` stops creating gear: delete `LocalCatalogFor`, and gear enters only through `TryAddInstance`. The starting kit is created by the run or server through the issuer. Tests switch to an explicit test-issuer helper. | ItemInstanceAuthority, LootTable, Inventory, RunDirector/ClassLoadout, gates | Passing an unregistered or cloned `ItemData` to the bag is refused. UID count is unchanged across equip/transfer. **Prerequisite for P5.** |
| **R4** | One loot pipeline: `EnemyData` uses a seeded `LootTable`. `LootPickup` can hold an `ItemInstance`. Merge the three loot-host interfaces into one counted + instance interface. | EnemyAgent, EnemyData, LootPickup, RunDirector, ChestInteractable | Enemy gear drop keeps its UID from drop to bag. No `UnityEngine.Random` in loot. |
| **R5** | Raid seed: the run (later the server) owns a per-raid seed, and each chest's seed = hash(raidSeed, chestId). Replace `System.Random` with a small project PRNG (SplitMix64/PCG32) that has explicit stream derivation. Tests inject a fixed raid seed. | LootTable, ChestInteractable/Spawner, RunDirector, new Rules/Prng.cs, gates | Same raid seed gives identical loot. Different raid seeds give different loot. Existing roller gates are re-baselined, with the change explained. |
| **R6** | Stable inventory addressing and a command layer: stack ids (monotonic) and UIDs. An `InventoryCommand` (Move/Swap/Merge/Equip/Unequip/Use) goes through an `IInventoryAuthority` (local implementation for now). UI and hotbar only emit commands. Consumable effects move to the authority. Split `InventoryScreenController` into View and SelectionController. | Inventory, Equipment, HotbarController, InventoryScreenController, new InventoryAuthority | inventoryui/items gates green. Grep shows no mutator calls from UI. |
| **R7** | Events scoped to a player: per-entity events plus a `LocalPlayerPresenter` that binds the HUD to the one locally controlled rig. Keep `GameEvents` only for run-wide facts, and add owner/combat id there. The authority stops raising HUD events. `Kind`/`TeamId` are assigned when a player spawns (FFA or party), and enemies target any player kind. | GameEvents, PlayerRig, LocalCombatAuthority, HudController, Inventory/Equipment/SkillCaster/RangedCombat, EnemyAgent, RunDirector (adds `PlayerSession` list), RunContext | Two rigs in one scene: the HUD shows only its own rig, PvP damage is accepted, and the enemy picks the nearest of either player. |
| **R8** | Asmdefs and EditMode tests: `Emberdelve.Rules` (pure: Data defs, AttributeRules, Affix*, ItemInstance/Snapshot, LootRoller, PRNG, DamageResolver math), `Emberdelve.Runtime`, `Emberdelve.Presentation`, `Emberdelve.Review` (defineConstraints), `Emberdelve.Editor`, `Emberdelve.Tests.EditMode`. Port the pure gates (ItemInstance, LootRoller, AffixDefinition, AttributeRules) to readable NUnit. Break the Data→Items cycle. | asmdefs, move LootTable roll logic, gates → tests | `-runTests -testPlatform EditMode` passes. Runtime does not reference Presentation. Tests stop calling `BuildAll`. |
| **R9** | Versioned container snapshot: `{schemaVersion, gear[uid snapshot + cell], stacks[itemId,count,cell], equipment[slot→snapshot]}`. Atomic restore, duplicate-UID rejection across bag + equipment, fractional-affix JSON round-trip tests. (This is P5 itself, after R3.) | Items/InventorySnapshot.cs, Inventory, Equipment | Tampered, duplicated or unknown-version input is refused with no partial state. Exact UIDs and rolls are restored. |
| **R10** | `EffectiveStats` snapshot: one pure function (class, worn instances, passives, statuses) produces scores, pools, speed, armor, per-channel flat damage and multipliers. All affix-key routing lives in one table. | Stats/, PlayerRig, Equipment | Existing attribute/affix gates unchanged. Becomes the replication payload in U3B. |

### Nice to have (any time, small)
Per-frame allocation cleanup (N1); delete dead APIs (N2); `ConfigureGrid` deletion fix and an overlap assert in `Recompute` (N3); `CarriedValue` vs `RecoveredValue` semantics (N4); `GameConfig.EnsureValid` moved editor-side (N5); fix the `UiFactory` canvas takeover (N6); remove comment history (N7); one shared `PlayModeGateRunner` replacing the 11 harness copies.

---

## D. Order vs PLAN_CLAUDE
1. **R0 now.** It's a live bug with a tiny diff.
2. **P3 cloak slot and tooltips can proceed now**, with these constraints:
   - Append `EquipmentSlot.Back = 6`. Never reorder: assets store the integer.
   - Derive `SlotCount` from the enum.
   - Map Back → `AffixCategory.Cloak` in `ItemCategoryRules`.
   - Key tooltips and selection on `InventorySlot.Instance`/UID (rarity and affixes come from the instance), not on `ItemData` (`InventoryScreenController.cs:624-663, 695-752`).
3. **R3, then R9 (P5 persistence).**
4. **R1a, R1b, R2, R6, R7, R4, R5 before any U3A code.** R8 can start in parallel at any time and makes the dedicated-server build clean. Do R10 before U3B.

## E. Test-health summary
**Not false-green in general:** most gates assert hard-coded values and include negative and tampering cases.

**Actual false-green risks:**
1. Happy-path shortcuts that skip the real input or hold path (ExtractionFlow, F3).
2. Fixtures that pass only because the authority trusts forged timestamps (F2).
3. Test-only mutators (`TryAddAt`, `TryEquipItem(ItemData)`) that create gear in ways gameplay can't.
4. Every smoke run rebuilds the data assets first, so builder changes and test results can't be told apart.

**Accepted with a caveat:** `InventoryUiChecks` call `HandleClick` directly. That is acceptable because the windowed review bridge covers real pointer input.
