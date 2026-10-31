# Emberdelve — project status / handoff

Latest continuation (31 October 2026): see [Docs/Tasks/D3bc-20261030.md](Docs/Tasks/D3bc-20261030.md) and [Docs/ROADMAP.md](Docs/ROADMAP.md). The M1–M4 sections below are historical; D3 infrastructure passes the recorded60-group regression; [D3d playable content](Docs/Tasks/D3d.md) now passes13 selected groups /13,172 checks with real input and rendered proof. Continuing D4a immutable curves and DEX movement; six attributes and both mana pools remain.

Original first-person PvPvE dungeon-extraction prototype (dark fantasy / dungeon-synth).
Unity **6000.2.5f1**, URP, new Input System. Unity project root: `DakaDaka/`. Slice code: `Assets/Emberdelve/`.

## Roadmap (agreed milestones)

1. **Audit + fix WASD and player HP bar** — DONE (see below)
2. Enemy attack telegraphs + direction-aware **block**; capability model where shields block but never parry, later sword/axe can parry — DONE
3. Secondary weapons / loadout switching (Warrior → crossbow, Archer → rapier) — DONE (see Milestone 3)
4. Mana + exactly 2 active skills and 2 passives per class (Warrior: Dash + Battle Cry, Archer: Multi-shot + Piercing Shot) — DONE (see Milestone 4)
5. Diablo II-style grid inventory, equipment/clothing/armor/weapon items, several interactable chest types with loot — not started
6. Integration + regression checks — not started

## Milestone 1 — DONE

### Audit findings (reconfirmed against files, not the old audit)

- `Assets/Emberdelve/Data/EmberdelveInputActions.inputactions` and the generated fallback
  `Runtime/Input/EmberdelveInputFallback.cs` were **already correct**: `Move` is a `2DVector` composite with
  Up=`<Keyboard>/w`, Down=`<Keyboard>/s`, Left=`<Keyboard>/a`, Right=`<Keyboard>/d`, plus `<Gamepad>/leftStick`.
  No input-asset change was needed or made.
- The real WASD bug was in `PlayerMovement.Move` consuming raw `x/y` as **world X/Z**. That part had already
  been replaced by `PlayerMovement.ViewRelativeDirection(...)` by the interrupted attempt (file mtime 17:03,
  the only file it touched) but had **never been compiled or smoke-tested**. It is now verified.
- `GameEvents.RaisePlayerHealth` genuinely had **zero callers**; `HealthComponent.Changed` was only consumed
  by nothing on the player path. HUD bar therefore stayed at the built-in `100 / 100` placeholder and full
  width even for the 150 HP Warrior.
- Initial facing was already consistent: `EmberdelveLevelBuilder.BuildPlayer` spawns the rig at identity
  rotation (yaw 0, world +Z) and `PlayerLook._yaw` started at 0. From start cell (4,3), +Z leads along the
  walkable "Entry to Central" corridor, so the spawn faces into the dungeon. No spawn-facing change made.

### Changed files

- `Assets/Emberdelve/Runtime/Movement/PlayerMovement.cs`
  - `Move` now resolves the view transform explicitly: new serialized `viewTransform`, resolved in `Awake`
    from `GetComponentInChildren<Camera>()` when not assigned, falling back to `transform`.
  - `ViewRelativeDirection` flattens the view forward/right (pitch never shortens ground movement, a
    straight-up camera still walks), normalizes both axes, builds `right * raw.x + forward * raw.y`, and
    normalizes the result. Vertical handling is untouched: gravity, jump, `_verticalVelocity`, crouch capsule
    lerp, sprint/guard/speed-penalty tuning and `speedChangeRate` are unchanged.
  - `DesiredMoveIntent` is still the flattened camera-relative vector a future server would validate.
- `Assets/Emberdelve/Runtime/Movement/PlayerLook.cs`
  - `Awake` now seeds `_yaw` from `transform.localEulerAngles.y` (and resets pivot pitch) so a scene-set
    spawn rotation cannot snap back to yaw 0 on the first mouse move and desync WASD from where the player looks.
- `Assets/Emberdelve/Runtime/Player/PlayerRig.cs`
  - `Awake` subscribes `health.Changed += OnHealthChanged`; `OnDestroy` unsubscribes.
  - `OnHealthChanged(current, max)` calls `GameEvents.RaisePlayerHealth(current, max)`. Only the local
    player rig bridges to the player-health event; enemies keep their own `Changed` subscriptions.
- `Assets/Emberdelve/Runtime/UI/HudController.cs`
  - `OnRunState(RunState.Running)` now also calls `OnPlayerHealth(Player.CurrentHealth, Player.MaxHealth)`
    so the bar/label are seeded from live rig state even if class setup raised the first health change before
    the HUD subscribed. Presentation-only change; no direct combat reads added.

### Verified call path (damage → HP bar)

`LocalCombatAuthority` → `PlayerRig.ApplyDamage(DamageInfo)` → `HealthComponent.ApplyDamage`
→ `HealthComponent.Changed` → `PlayerRig.OnHealthChanged` → `GameEvents.RaisePlayerHealth`
→ `HudController.OnPlayerHealth` (fill width `BarWidth * current/max` + `HealthLabel` text).
Same path fires for `Configure` (class/loadout setup), `Heal` and regen ticks in `HealthComponent.Update`.

### Checks actually run

Unity editor: `C:/Program Files/Unity/Hub/Editor/6000.2.5f1/Editor/Unity.exe` (no other Unity instance was
running, so the project lock was free).

1. Script compile / project import:
   `-batchmode -quit -nographics -projectPath . -logFile Logs/m1_compile.log` → **exit 0**, no `error CS`,
   no "Scripts have compile errors". Only pre-existing unrelated warning:
   `Assets/SharedAssets/Benchmark/PerformanceTest.cs(68,13): warning CS0618` (obsolete EventSystem call).
2. Headless play-mode smoke, dungeon scene, full combat + loot flow:
   `-executeMethod Emberdelve.EditorTools.EmberdelveRuntimeSmokeTest.RunCombatFlow` → `Logs/m1_combat.log`
   → **exit 0**, `[Emberdelve][Smoke:combat] PASS checks=45 failures=0` (45 `ok`, 0 `FAIL`).
   Relevant assertions inside that pass: HUD canvas built, player alive at spawn, class applied,
   CharacterController height/radius match class, movement reports grounded, **player stays put with no
   input (drift < 0.3 m)**, no falling through the floor, melee swing damages an enemy, authority
   accept/reject rules, loot collection, inventory exposed to the HUD.

Not run in this milestone (deliberately, to stay focused): `RunEnemyAi`, `RunExtraction`, `RunDeath`,
`RunLobbyFlow`, `RunArcherFlow`, `RunResultLoop`, and `EmberdelveBootstrapMenu.ValidateSlice`. No new test
harness or test infrastructure was added. No scene/asset rebuild was run, so generated scenes and data
assets are untouched.

### Decisions

- Keep the input asset/fallback as the single source of truth; fix mapping in movement code only.
- Camera-relative movement is expressed through the flattened camera/body yaw, which is what the existing
  rig already produces (pitch lives only on `Look_Pivot`), so tuning and existing behaviour are preserved.
- Health presentation stays event-driven: `HealthComponent` never talks to the UI, `PlayerRig` is the single
  bridge for the local player, `HudController` still only listens to `GameEvents`.
- No git history exists yet (`DakaDaka/.git` has zero commits, everything untracked). Nothing was committed.

## Milestone 2 — DONE

### What was built

**Block / parry capability model (data-driven, parry timing not implemented)**
- New `Assets/Emberdelve/Runtime/Data/DefenseModel.cs`:
  - `[Flags] DefenseCapability { None, Block, Parry }` — Block and Parry are separate flags so a later
    sword/axe can declare Parry without touching the block path.
  - `DefenseRole { None, Weapon, Shield }` — Shield is the only role that hard-forbids Parry.
  - `DefenseProfile` — resolved kit (`Capabilities`, `BlockReduction`, `BlockArcDegrees`, `HasShield`,
    `CanBlock`, `CanParry`) built only from ScriptableObjects.
  - `DefenseProfile.Sanitize(capabilities, isShield)` is the single rule: `capabilities & ~Parry` when the
    item is a shield. Applied in `WeaponData.EffectiveCapabilities`, again in the `DefenseProfile`
    constructor, and in `WeaponData.OnValidate` so a shield cannot keep Parry even if it is ticked in the
    Inspector.
- `WeaponData` gained `defenseRole`, `capabilities`, `blockReductionOverride`, `blockArcOverride`
  (`-1` = use the class value) plus `IsShield`, `EffectiveCapabilities`, `CanBlock`, `CanParry`.
- `CharacterClassData` gained an optional `offhandShield` slot (the hook Milestone 5 equipment will fill).
- `PlayerRig` now resolves a `DefenseProfile` in `ApplyClass` and exposes `Defense`, `CanGuard`, `CanParry`.
  `IsGuarding`/`CanGuard` come from the profile instead of `CharacterClassData.CanGuard`, and
  `TryGetGuardReduction` uses `_defense.BlockArcDegrees` / `_defense.BlockReduction`.
- New data asset `Assets/Emberdelve/Data/Weapon_EmberbulwarkShield.asset` ("Emberbulwark Shield"),
  assigned to `Class_Warrior.offhandShield`. Archer has none.
- `EmberdelveAssetLibrary`: added the shield asset plus an idempotent `ApplyDefenseKit()` pass (called from
  `BuildAll` after `BuildClasses`). `LoadOrCreate` only configures brand-new assets, so without this pass the
  pre-existing sword/bow/claw assets would all still have no capabilities. It sets Sword = Weapon/Block,
  Bow + Claw + ClawElite = Weapon/None, Shield = Shield/Block|Parry (declares Parry on purpose; the shield
  rule strips it) and rewires `Warrior.offhandShield`, saving only what actually changed.

**Enemy attack telegraph whose direction is the hit that arrives**
- `EnemyBrain` commits `_telegraphDirection` (flattened, normalized) at the moment the windup starts and
  exposes `TelegraphDirection` and `WindupRemaining`.
- `EnemyAgent.SubmitAttack` now attacks **along that committed direction**: it computes the warned wedge,
  and if the target is outside `attackTelegraphCone` or beyond `attackRange + 0.4` it submits **no intent at
  all** (`LastAttackWhiffed = true`, short whiff hold). The `AttackIntent.Direction` and the `HitCandidate`
  point are both derived from the warned direction, so the damage the authority applies travels where the
  warning pointed. Authority validation, sequence/cooldown bookkeeping and LOS checks are unchanged.
- `EnemyData` gained `attackTelegraphCone` (default 120°, range 40-200) and `telegraphColor`.
- New `Assets/Emberdelve/Runtime/Combat/AttackTelegraphVisual.cs`: a procedurally generated wedge mesh
  (apex at the enemy, arc = cone angle, both facings) with a runtime-created material (URP Unlit → URP Lit
  → Standard → Diffuse fallback chain, `_EMISSION` keyword, `_Cull` off). No collider, layer
  `Ignore Raycast`, so it can never affect a combat query. It grows and brightens with windup progress,
  dims on a whiff, and converts the warned **world** direction into a local rotation
  (`Inverse(parent.rotation) * world`) so the enemy turning during its own windup cannot drag the warning
  off the committed direction. Existing body-raise telegraph is kept.

**Directional blocking** (already arc-based in `PlayerRig`; now driven by the resolved profile)
- Guard holds only inside the kit's arc towards `DamageInfo.SourcePosition`; outside it the reduction is
  not returned, so the hit lands in full. `PlayerMovement.SetGuardActive` / `guardSpeedMultiplier` unchanged.

### Changed files (Milestone 2)
- new `Assets/Emberdelve/Runtime/Data/DefenseModel.cs`
- new `Assets/Emberdelve/Runtime/Combat/AttackTelegraphVisual.cs`
- `Assets/Emberdelve/Runtime/Data/WeaponData.cs`
- `Assets/Emberdelve/Runtime/Data/CharacterClassData.cs`
- `Assets/Emberdelve/Runtime/Data/EnemyData.cs`
- `Assets/Emberdelve/Runtime/AI/EnemyBrain.cs`
- `Assets/Emberdelve/Runtime/AI/EnemyAgent.cs`
- `Assets/Emberdelve/Runtime/Player/PlayerRig.cs`
- `Assets/Emberdelve/Editor/EmberdelveAssetLibrary.cs`
- `Assets/Emberdelve/Editor/EmberdelveRuntimeSmokeTest.cs` (extended the existing `RunCombatFlow` harness
  with `DefenseFlow()` + `GuardReductionFlow()`; no new harness, no new entry point)
- generated data asset `Assets/Emberdelve/Data/Weapon_EmberbulwarkShield.asset` (+ `.meta`)

### Checks actually run (Milestone 2)
Unity `6000.2.5f1`, no other Unity instance running.

1. Compile: `-batchmode -quit -nographics -projectPath . -logFile Logs/m2_compile_final.log` → **exit 0**,
   `error CS` count **0**, no "Scripts have compile errors".
   (Earlier iterations: `Logs/m2_compile.log` failed with `CS0246 Flags` in `DefenseModel.cs` — fixed by
   adding `using System;`. `Logs/m2_compile2.log` failed with `CS0128` duplicate local `visualAngle` at
   `EmberdelveRuntimeSmokeTest.cs:387` — the duplicated declaration was removed; `Logs/m2_compile3.log` and
   `Logs/m2_compile_final.log` are both clean.)
2. `RunCombatFlow` → `Logs/m2_combat_final.log` → **exit 0**, `[Emberdelve][Smoke:combat] PASS checks=75
   failures=0` (was 45 checks before this milestone; 30 new checks, 0 failures).
   The first run of the new flow (`Logs/m2_combat.log`) reported **9 failures**; all nine were harness bugs,
   not gameplay bugs, and are fixed:
   - "shield data does declare Parry…" — `WeaponData.OnValidate` strips Parry from a shield at save time,
     so the assertion could not observe a Parry-carrying shield asset. Replaced with a direct assertion on
     the rule: `DefenseProfile.Sanitize(Block|Parry, isShield)` keeps Block and drops Parry.
   - "player damage was recorded on the rig" — the harness unsubscribed from `HealthComponent.Damaged`
     before the guard measurements. Subscription now spans the telegraph and guard phases.
   - "front-facing guard reports the hit as blocked", "guard reports a reduction (0,00)",
     "front-facing block reduced the damage (15,0 from a 15,0 weapon)",
     "guard does not hold when facing away from the source",
     "correct-facing block takes less than wrong-facing (15,0 < 15,0)",
     "guard does not hold ninety degrees off the source",
     "side-facing block takes more damage than a correct block (15,0 vs 15,0)" — two harness bugs:
     `MeleeCombat.Update` re-asserts guard from input every frame and headless batch mode has no mouse, so
     it cleared the guard the harness had set (now `MeleeCombat` is disabled for the measurement window and
     re-enabled afterwards); and the facing vector was inverted (`CombatCenter - origin` points from the
     source to the player, so it was being used as "facing the source"). A separate `towardSource` vector is
     now used for facing and the original for the intent direction.
3. `RunEnemyAi` → `Logs/m2_ai_final.log` → **exit 0**, `PASS checks=20 failures=0` (regression for the
   cone-gated enemy attack path: chase, close distance and damage the player still work).
4. `RunDeath` → `Logs/m2_death_final.log` → **exit 0**, `PASS checks=23 failures=0` (authority → player
   damage → death → result screen path unchanged).

Not run in this milestone: `RunExtraction`, `RunLobbyFlow`, `RunArcherFlow`, `RunResultLoop`,
`EmberdelveBootstrapMenu.ValidateSlice`.

### Evidence (from `Logs/m2_combat_final.log`)
- `ok  enemy entered a windup telegraph (state Attack)`
- `ok  telegraph points at the player it is winding up against (0,0 deg off)`
- `ok  telegraph is visible during the windup`
- `ok  telegraph visual holds the committed world direction even while the enemy turns (0,0 deg off)`
- `ok  telegraph carries no collider, so it cannot affect a combat query`
- `ok  the windup resolved into a real hit (135 -> 120)`
- `ok  the hit arrived along the warned direction (0,0 deg between telegraph and damage direction)`
- `ok  guard is active for the block tests`
- `ok  authority accepted the front-facing attack`
- `ok  front-facing guard reports the hit as blocked`
- `ok  guard reports a reduction (0,70)`
- `ok  front-facing block reduced the damage (4,5 from a 15,0 weapon)`
- `ok  wrong-facing block takes full damage (15,0 vs weapon 15,0)`
- `ok  correct-facing block takes less than wrong-facing (4,5 < 15,0)`
- `ok  guard does not hold when facing away from the source`
- `ok  guard does not hold ninety degrees off the source`
- `ok  side-facing block takes more damage than a correct block (15,0 vs 4,5)`
- `ok  shield asset carries the shield defense role` / `ok  shield declares block` / `ok  shield can never parry`
- `ok  the shield rule strips Parry from anything a shield declares`
- `ok  warrior kit resolves a block capability (block=True parry=False shield=True reduction=0,70 arc=140)`
- `ok  warrior kit resolves no parry capability yet (block=True parry=False shield=True reduction=0,70 arc=140)`
- diag line: `guard diag: blocked=4,5 behind=15,0 side=15,0 weapon=15,0 profile=block=True parry=False shield=True reduction=0,70 arc=140`

### Decisions (Milestone 2)
- Damage is still only applied by `LocalCombatAuthority`; the telegraph and the guard are inputs to the same
  `AttackIntent` / `HitCandidate` / `DamageInfo` pipeline, so the networking seam is untouched: a future
  server receives the same intent and can validate it against the same committed direction.
- A whiff submits no intent at all rather than submitting a miss: dodging out of the warned wedge cannot be
  overridden locally, and there is nothing for a server to second-guess.
- Parry exists only as a data flag plus `DefenseProfile.CanParry` / `PlayerRig.CanParry`. No timing window,
  no parry animation, no parry damage is implemented — that is deliberately deferred.
- The shield is a data asset in an off-hand slot, not yet an inventory item and not yet drawn on the player
  (`ClassLoadout` only builds a visual for `primaryWeapon`). Milestone 5 equipment should render and equip it.
- Block numbers are unchanged (Warrior 0.70 reduction, 140° arc) so Milestone 1 behaviour did not shift.

## Milestone 3 — DONE

### What was built

**Loadout slots and switching**
- `CharacterClassData` gained `secondaryWeapon`, `secondaryStartingReserveAmmo`, `secondaryMaxReserveAmmo`.
- `WeaponData` gained `ammoDisplayName` (HUD ammo wording) and `twoHanded`.
- New data assets: `Weapon_EmberCrossbow.asset` (ranged, Warrior secondary: 24 dmg Pierce, magazine 4,
  reload 2.2s, range 34, `twoHanded = true`) and `Weapon_EmberRapier.asset` (melee, Archer secondary:
  19 dmg Pierce, reach 2.7, arc 70, windup 0.16, cooldown 0.68, `capabilities = Block | Parry`).
- `EmberdelveAssetLibrary.ApplyDefenseKit()` now also assigns `Warrior.secondaryWeapon = Crossbow`
  (reserve 6 / cap 16) and `Archer.secondaryWeapon = Rapier`, and sets `twoHanded` on Bow and Crossbow.
  It stays idempotent because `LoadOrCreate` only configures brand-new assets.
- `ClassLoadout` is now the loadout owner: two slots (`Primary`, `Secondary`, `ActiveWeapon`,
  `ActiveSlot`, `UsingSecondary`, `EquippedCount`), `SwitchWeapon()` / `SwitchToSlot(int)`, an `Update()`
  that polls the new swap action, and `Equip(slot)` which enables exactly the combat component the active
  weapon can use, rebuilds the first-person visual for the active weapon, and tells the rig its new main hand.
- New input action `SwapWeapon`: `<Keyboard>/q` and `<Gamepad>/rightShoulder`, added to
  `EmberdelveInputFallback` (the single source of truth) and consumed as `EmberdelveInput.SwapPressed`.
  `EmberdelveProjectSetup.RebuildInputAsset()` regenerates
  `Assets/Emberdelve/Data/EmberdelveInputActions.inputactions` from the fallback in place (the `.meta` is
  deliberately kept so the dungeon scene's GUID reference survives). Verified: the asset now contains
  `SwapWeapon` with `<Keyboard>/q`.
- `PlayerRig` gained `Loadout`, `MainHandWeapon` and `ApplyMainHandWeapon(WeaponData)`, which re-resolves
  the `DefenseProfile` from the weapon actually held.
- `DefenseProfile.Resolve` gained the two-handed rule: a `twoHanded` main hand occupies both hands, so an
  off-hand shield contributes nothing while it is held. Warrior on the sword → block; Warrior on the
  crossbow → no block. Data-driven, no special cases in components.
- `RangedCombat.Initialize` gained an optional `startingMagazine` (-1 = full) so a swap can restore a
  partially spent magazine.
- Ammunition is per weapon, not per component: `ClassLoadout.RangedAmmoState` (weaponId, magazine, reserve,
  maxReserve) is seeded from the class at `Apply`, stored back on unequip, and restored on equip, so a swap
  cannot quietly refill a half-empty magazine.
- HUD: new `WeaponLabel` fed by a new `GameEvents.ActiveWeaponChanged` event (seeded from the rig on
  `RunState.Running`), and the ammo readout now uses the active weapon's `ammoDisplayName` instead of the
  hardcoded "Arrows". `RangedCombat`'s out-of-ammo toast uses the same field.

### Bug found and fixed during this milestone (runtime, not harness)
`ClassLoadout.Apply` used `_activeSlot = 0` before the first `Equip(0)`, so the first equip treated the bow
as "previously equipped" and copied `RangedCombat`'s uninitialized ammo (0/0) over the seeded state. The
Archer started with 0 arrows. Fixed by starting at `_activeSlot = -1` and only storing ammo when the
component is actually holding that same weapon (`rangedCombat.Weapon == previous`). Verified fixed:
`ok  archer starts with arrows (magazine 5)`.

### Changed files (Milestone 3)
- `Assets/Emberdelve/Runtime/Data/CharacterClassData.cs`
- `Assets/Emberdelve/Runtime/Data/WeaponData.cs`
- `Assets/Emberdelve/Runtime/Data/DefenseModel.cs` (two-handed rule)
- `Assets/Emberdelve/Runtime/Player/ClassLoadout.cs` (rewritten as the loadout owner)
- `Assets/Emberdelve/Runtime/Player/PlayerRig.cs`
- `Assets/Emberdelve/Runtime/Combat/RangedCombat.cs`
- `Assets/Emberdelve/Runtime/Input/EmberdelveInputFallback.cs`
- `Assets/Emberdelve/Runtime/Input/EmberdelveInput.cs`
- `Assets/Emberdelve/Runtime/Core/GameEvents.cs` (`ActiveWeaponChanged`)
- `Assets/Emberdelve/Runtime/UI/HudController.cs`
- `Assets/Emberdelve/Editor/EmberdelveAssetLibrary.cs`
- `Assets/Emberdelve/Editor/EmberdelveProjectSetup.cs` (`RebuildInputAsset`)
- `Assets/Emberdelve/Editor/EmberdelveRuntimeSmokeTest.cs` (extended existing flows only)
- generated data: `Assets/Emberdelve/Data/Weapon_EmberCrossbow.asset`, `Weapon_EmberRapier.asset` (+`.meta`),
  regenerated `EmberdelveInputActions.inputactions`, updated `Class_Warrior.asset` / `Class_Archer.asset` /
  `Weapon_HollowBow.asset` (`twoHanded`)

### Smoke coverage added (existing flows, no new harness or entry point)
- `RunCombatFlow` → new `LoadoutFlow()` (Warrior): both slots present and correct ids, start on primary,
  melee live / ranged off, sword can block and cannot parry, switch to crossbow (ranged live, melee off,
  no block capability), reserve bolts seeded, magazine full, real `RangedCombat.Fire` damaging an enemy
  through the authority, switch back (block capability returns), switch again with ammo persisted,
  switch back to slot 0.
- `RunArcherFlow` → new `ArcherLoadoutFlow()`: both slots present, switch to rapier (melee live, ranged
  off), rapier declares Parry and the rig reports it, archer still cannot block, real `MeleeCombat.BeginSwing`
  thrust, switch back to the bow with the partially spent magazine restored, Parry capability leaves with
  the rapier.
- Harness setup corrections made while diagnosing (assertions unchanged, none weakened): melee tests now aim
  from `_rig.AimOrigin` (where the melee scan actually originates) instead of from the rig feet; new
  `PlaceCombatCenterInRange(...)` places the enemy by the distance the swing actually measures
  (AimOrigin → enemy `CombatCenter`) rather than root-to-root; the enemy agent is frozen for the duration of
  a harness-driven swing so a stale A* path left over from teleporting cannot walk it out of the weapon
  probe, and so an enemy's own attack cannot inflate the shared `AcceptedCount`.

### Compile status (Milestone 3, final)
- `Logs/m3_compile_final4.log` → exit 0, `error CS` count 0.
- Earlier iterations that failed and were fixed: `Logs/m3_compile.log` (`CS0103 'ranged' out of scope in
  HudController.OnAmmo`), `Logs/m3_combat5.log` (`CS1729 Vector3 1-arg constructor in the harness helper`),
  `Logs/m3_compile_final2.log` (`CS8361 conditional inside a string interpolation in the rapier diag`).

### Command-sequence fix (the root cause above, now fixed in runtime code)
- `CombatRegistry.NextSequence(int combatId)` is the single per-entity sequence allocator (reset in the
  existing `ResetStatic`). `MeleeCombat`, `RangedCombat` and `EnemyAgent` all take their next sequence from
  it instead of private counters, so one entity cannot run two command streams a server would have to
  reconcile.
- The harness takes its synthetic player/enemy sequences from the same allocator instead of hand-rolling
  1000+/9000+, so the replay test reuses a genuinely consumed sequence.
- Evidence: `ok  every intended-valid attack claimed a sequence greater than the last one consumed`,
  `ok  enemy validation sequences strictly increase (2 -> 3 -> 4)`,
  `ok  melee swing damaged the enemy`, `ok  rapier thrust damaged the enemy`.

### Milestone 3 final smoke results — GREEN
| flow | log | result |
| --- | --- | --- |
| `RunCombatFlow` | `Logs/m3_final_combat.log` | `PASS checks=110 failures=0` |
| `RunArcherFlow` | `Logs/m3_final_archer.log` | `PASS checks=44 failures=0` |
| `RunEnemyAi` | `Logs/m3_final_ai.log` | `PASS checks=20 failures=0` |
| `RunDeath` | `Logs/m3_final_death.log` | `PASS checks=24 failures=0` |

At the sequence-allocator step itself (before the harness setup corrections were folded in):
`Logs/m3_combat_seq3.log` 109/0, `Logs/m3_archer_seq.log` 44/0, `Logs/m3_ai_seq.log` 20/0,
`Logs/m3_death_seq.log` 23/0.

## Milestone 4 — DONE (mana + exactly 2 active and 2 passive skills per class)

### What was built

**Data (everything about a skill is an asset, not a code branch)**
- New `Assets/Emberdelve/Runtime/Data/SkillData.cs`: `SkillSlot`, `SkillEffectKind`, `SkillStat`,
  `SkillModifierMode`, `StatModifier`, plus cost/cooldown/radius/distance/projectile count/per-projectile
  target cap/damage multiplier/ammo consumption/target+self status. `OnValidate` warns when a passive
  declares no modifiers (i.e. changes nothing).
- New `Assets/Emberdelve/Runtime/Data/StatusModel.cs`: `StatusKind` + `StatusSpec`.
- Eight assets created by `EmberdelveAssetLibrary.BuildSkills()`:
  `Skill_WarriorDash` (24 MP / 7 s / 4.6 m), `Skill_WarriorBattleCry` (32 MP / 14 s / 6 m radius / 4 targets
  / 14 flat damage / Slow 4 s x0.55 on targets / Empowered 6 s x1.25 on self),
  `Skill_WarriorAshenMight` (+20 % melee), `Skill_WarriorBulwarkTraining` (+0.12 block reduction),
  `Skill_ArcherMultiShot` (26 MP / 9 s / 3 projectiles / 26° spread / 0.7x damage / 1 arrow each),
  `Skill_ArcherPiercingShot` (22 MP / 11 s / 1 projectile / cap 2 targets / 1.35x),
  `Skill_ArcherFarshot` (+18 % ranged), `Skill_ArcherFleetFooting` (+15 % move and sprint).
- `CharacterClassData` gained `maxMana`, `manaRegenPerSecond`, `manaRegenDelay`, `activeSkills[2]`,
  `passiveSkills[2]`, and a static `ValidateKit()` + `OnValidate()` that enforce the exactly-two rule at
  edit time. `EmberdelveAssetLibrary.ApplySkillKit()` writes the kits and mana numbers onto
  `Class_Warrior.asset` / `Class_Archer.asset` (Warrior 120 MP, 8/s after 3.5 s; Archer 100 MP, 10/s after 3 s).

**Runtime**
- `Combat/ManaComponent.cs` — pool, spend, restore, regen with a post-spend delay, `Changed` event,
  `SetRegenerationActive` (death suspends it), `RegenerationAllowed` / `RegenDelayRemaining` observability.
- `Combat/StatusComponent.cs` — `IStatusTarget` + expiring temporary multipliers (Slow, Empowered).
- `Skills/CharacterStatStack.cs` — passives resolved as `(base + flat) x multiplier`, consumed by movement,
  defense, damage resolution, max health/mana and regen.
- `Skills/SkillContracts.cs` — `ISkillBehavior`, `SkillContext`, `SkillBehaviorCatalog`: the only place that
  maps a skill asset to code. A new skill reusing an existing effect is a new asset; a new effect is one
  small class plus one catalog line. Nothing branches on a class id, skill id or display name.
- Behaviors: `DashBehavior`, `BattleCryBehavior`, `MultiShotBehavior`, `PiercingShotBehavior`, plus
  `RangedSkillUtility` for shared projectile resolution.
- `Skills/SkillCaster.cs` — kit validation, per-slot cooldown clocks, mana gate, weapon-requirement gate,
  `TryCastSlot` as the single entry point, death/reset handling, `SkillSnapshot` for the HUD.
- `Core/GameContracts.cs`: `TeamId` on `ICombatant`, `IDamageOutgoingProvider`, `SkillSnapshot`,
  `DamageInfo.Invalid/IsValid`. `Core/GameEvents.cs`: `PlayerManaChanged`, `SkillStateChanged`,
  `SkillCastRejected`, `StatusChanged`.
- `Combat/CombatContracts.cs`: `SkillIntent` (with `ActivationId`), `SubmitSkill` + `ApplyStatus` on
  `ICombatAuthority`, `CombatTeams`, `CombatRegistry.NextActivationId()`.
- `Combat/LocalCombatAuthority.cs`: team hostility for both weapon and skill intents, `SubmitSkill`
  validated against the skill's own cooldown clock keyed per activation, `ApplyStatus`, outgoing multipliers
  read from the instigator (never from the caller), `LastAttackTimeFor` / `WeaponCooldownRemaining`.
- `Combat/DamageAndScanning.cs`: `DamageResolver.ResolveSkill`, `HitScanner.CollectRangedHits` (level
  geometry stops the shot, one hit per entity per projectile), `HitScanner.CollectRadialHits`.
- Wiring: `PlayerRig` (mana/status/skill components, `TeamId`, `IStatusTarget`, `IDamageOutgoingProvider`,
  death handling, passive block bonus), `ClassLoadout` (kit stats, mana config, health/movement from
  passives), `PlayerMovement` (dash as a real `CharacterController.Move` mode that stops on side collisions,
  `ResolveGroundSpeed`, status slow applied), `RangedCombat` (`HasAmmo(int)`, `TryConsumeAmmo(int)`,
  `CancelDraw()`), `EnemyAgent` (monster team, status component, slow applied to its chase step, statuses
  cleared on death).
- Input: `Skill1` = `<Keyboard>/f` / `<Gamepad>/leftShoulder`, `Skill2` = `<Keyboard>/g` /
  `<Gamepad>/buttonWest`, in `EmberdelveInputFallback` (source of truth) and consumed by `EmberdelveInput`
  with `SkillKeyLabel(int)` for the HUD. `EmberdelveBootstrapMenu.RegenerateInputAsset()` rewrites the
  authored `.inputactions` asset headless; verified `Skill1 -> F`, `Skill2 -> G`.
- HUD (`HudController`): mana bar + `ManaLabel`, a two-slot `SkillBar` (cooldown fill, key label, name,
  mana cost, reject flash + reason), and a `KitLabel` naming both passives. All built in code, no prefab.

### Runtime bugs found and fixed during Milestone 4 (runtime, not harness)
1. `ManaComponent.RegenDelayRemaining` returned 0 whenever regeneration was not currently running, which
   hid the whole post-spend delay window. Now reports the real countdown while configured, non-full and
   allowed.
2. `SubmitSkill` stamped the skill cooldown clock before the range and line-of-sight checks, so a shot that
   never connected still spent the cooldown; and an entity that had never cast was treated as `lastCast = 0`,
   which could refuse its very first cast. The clock is now stamped only when a hit of that activation is
   accepted, and "never cast" is no longer a cooldown violation.
3. A single activation that legitimately submits several intents — a three-arrow volley, or one arrow that
   pierces two bodies — was rejected as `cast faster than its 9/11s cooldown`. Fixed with an `ActivationId`
   carried by `SkillIntent`/`SkillContext`: hits sharing the activation id pass, a genuinely new button press
   carries a new id and is still refused. Asserted both ways:
   `ok  the authority accepted both hits of the one activation (2 -> 4, cap 2)` and
   `ok  the authority refuses a new activation of the same skill while its cooldown runs`.
4. `SubmitAttack` stamped the weapon cooldown clock for intents that were later rejected for range, which
   made the Milestone 1 synthetic validation tests push the player's clock into the future. Added
   `LastAttackTimeFor` / `WeaponCooldownRemaining` so the harness can wait for the authority's real clock
   instead of assuming a swing should be accepted.

### Harness setup corrections (assertions unchanged, none weakened)
- `CorridorIsClear` / `ReportObstaclesAhead` ray-scan a test lane at several heights and are asserted
  **before** the movement or projectile assertions; when a lane precondition fails, the flow logs that it is
  skipping those assertions instead of reporting a movement or projectile bug.
- Loot pickups lying in a test lane are deactivated (colliders included), moved, and restored afterwards;
  enemies and corpses standing in a lane are parked on a far walkable cell.
- `PlaceEnemyAhead` also turns the target to face the player, so its collider footprint is predictable.
- The volley lane is derived from the skill's own spread angle (`depth x tan(spread/2)`), and the volley
  targets are placed on the arrows' actual rays.
- Mana-refusal and weapon-requirement tests wait for genuine cooldown readiness first, because the runtime
  reports a cooldown before mana; the low-mana assertions then assert the refusal names mana and leaves no
  cooldown behind.
- The archer flow parks every enemy agent and heals the archer before its skill tests, and asserts the
  caster is alive before attributing a refusal to the rules.

### Milestone 4 smoke coverage (new flows in the existing harness)
- `WarriorSkillFlow()` (runs at the end of `RunCombatFlow`): kit shape (2 active + 2 passive, distinct
  effects, named passives), mana pool sourced from the class asset and full at start, HUD skill bar / mana
  bar / passive readout / real key binding, Ashen Might measured as the authority's own damage number
  (multiplier 1.20 melee, 1.00 ranged), Dash (mana spent exactly, cooldown started, 4.6 m of real
  collision-respecting movement, lands on walkable ground, cooldown refusal reported to the HUD with a
  reason, cooldown expiry), Dash into a wall (blocked, 0.42 m of 4.6 m asked for, still costs mana, player
  stays inside walkable bounds), Battle Cry (mana spent, damage delivered as an authority `DamageInfo` with
  a fresh sequence, target inside the radius damaged, far enemy untouched, caster not self-damaged, Slow on
  the target with a measured chase-distance reduction, Empowered on the caster raising outgoing damage),
  Bulwark Training (guard reduction 0.82 vs class 0.70), mana refusal (refused, spends nothing, moves
  nothing, submits nothing, starts no cooldown), mana regen (paused by a spend, resumes after the delay,
  never overshoots).
- `ArcherSkillFlow()` (runs at the end of `RunArcherFlow`): kit shape, Fleet Footing through the same speed
  rule a server would validate (`ResolveGroundSpeed` walk and sprint), Farshot multiplier, ammo refusal with
  an empty magazine, mana refusal, Multi-shot (3 arrows from the magazine, 3 arrow visuals, 3 separate
  targets, one hit per arrow with per-target damage matching a single arrow, each arrow claiming its own
  sequence), Piercing Shot (one arrow spent, first and second bodies hit once each, third body untouched at
  the 2-target cap, both hits accepted inside one activation, an immediate new activation refused), weapon
  requirement (both skills refused on the rapier, mana untouched), mana regen.
- `DeathFlow()` additions: skills refused while dead with reason `dead`, dash cancelled, statuses cleared,
  regeneration suspended and the pool unchanged over 2 s.
- `ResultLoopFlow()` additions: a fresh run reconfigures the pool, starts full, resolves a valid kit, both
  skills off cooldown, no lingering statuses.

### Milestone 4 results — GREEN
| step | command | log | result |
| --- | --- | --- | --- |
| compile | `"C:/Program Files/Unity/Hub/Editor/6000.2.5f1/Editor/Unity.exe" -batchmode -quit -nographics -projectPath . -logFile Logs/m4_compile_final.log` | `Logs/m4_compile_final.log` | exit 0, `error CS` count 0 |
| input asset | `... -executeMethod Emberdelve.EditorTools.EmberdelveBootstrapMenu.RegenerateInputAsset` | `Logs/m4_input3.log` | exit 0, `Skill1 -> F`, `Skill2 -> G`, all 8 required actions present |
| data assets | `... -executeMethod Emberdelve.EditorTools.EmberdelveBootstrapMenu.BuildDataOnly` | `Logs/m4_data1.log` | exit 0, 8 `Skill_*.asset` imported, kits written to both class assets |
| Warrior M4 + M1-M3 combat regression | `... -executeMethod Emberdelve.EditorTools.EmberdelveRuntimeSmokeTest.RunCombatFlow` | `Logs/m4_RunCombatFlow.log` | `PASS checks=200 failures=0` |
| Archer M4 + archer regression | `... -executeMethod Emberdelve.EditorTools.EmberdelveRuntimeSmokeTest.RunArcherFlow` | `Logs/m4_archer3.log` | `PASS checks=115 failures=0` |
| enemy AI regression | `... -executeMethod Emberdelve.EditorTools.EmberdelveRuntimeSmokeTest.RunEnemyAi` | `Logs/m4_RunEnemyAi.log` | `PASS checks=20 failures=0` |
| death regression + M4 death handling | `... -executeMethod Emberdelve.EditorTools.EmberdelveRuntimeSmokeTest.RunDeath` | `Logs/m4_RunDeath.log` | `PASS checks=32 failures=0` |
| extraction regression | `... -executeMethod Emberdelve.EditorTools.EmberdelveRuntimeSmokeTest.RunExtraction` | `Logs/m4_RunExtraction.log` | `PASS checks=24 failures=0` |
| lobby regression | `... -executeMethod Emberdelve.EditorTools.EmberdelveRuntimeSmokeTest.RunLobbyFlow` | `Logs/m4_RunLobbyFlow.log` | `PASS checks=18 failures=0` |
| result-screen loop regression + M4 reset handling | `... -executeMethod Emberdelve.EditorTools.EmberdelveRuntimeSmokeTest.RunResultLoop` | `Logs/m4_RunResultLoop.log` | `PASS checks=61 failures=0` |

Iterated runs that failed and were fixed on the way (each failure is listed above under the bug or setup
heading it produced): `Logs/m4_combat1.log` 193/6, `Logs/m4_combat2.log` 199/2, `Logs/m4_combat3.log` 200/2,
`Logs/m4_combat5.log` 201/3, `Logs/m4_archer1.log` 105/10, `Logs/m4_archer2.log` 111/6.

Representative evidence lines:
- `[Emberdelve][Smoke:combat] ok  Dash moved the player 4,60m (asked for 4,6m)` /
  `ok  Dash did not pass through the wall (0,42m of 4,6m asked for)`
- `ok  the slow measurably reduces the distance it closes (2,23m < 4,05m unslowed)`
- `ok  Bulwark Training adds exactly its declared 0.12 (0,82)`
- `ok  melee damage is weapon base x passive (31,2 vs expected 31,2, headshot=True)`
- `ok  the spread hit three separate targets (3/3)` /
  `ok  no entity was hit twice by the same arrow (A 1, B 1)`
- `ok  the shot stopped at its target cap (2) and did not reach the third body`
- `warrior skill diag: kit=True mana=120/120 defense=block=True ... reduction=0,82 arc=140
  passives=Ashen Might (+20% melee damage)  |  Bulwark Training (+0,12 block reduction)`
- `archer skill diag: kit=True ... walk=... sprint=... passives=Farshot (+18% ranged damage)  |
  Fleet Footing (+15% movement speed, +15% sprint speed)`

### Design choices worth keeping in mind
- Skills produce their own `SkillIntent` type but share the authority's per-entity sequence allocator and
  validation pipeline, and get their own per-(combat id, skill id, activation) cooldown clock separate from
  the weapon cooldown clock. Damage always goes through the authority; nothing writes health directly.
- `TeamId` (player 0, monster 1) and `CombatTeams.AreHostile` exist now so hostility rules are already in
  place before networking.
- Outgoing multipliers are read from the instigator entity (`IDamageOutgoingProvider`), never passed in by
  the caller — the same shape a server-authoritative resolver would need.
- Permanent passives (`CharacterStatStack`) and temporary statuses (`StatusComponent`) are separate systems
  that feed the same consumption points, so a buff cannot silently become permanent.
- Dash is a movement mode through `CharacterController.Move`, not a teleport, so collision is enforced by
  the same component that enforces it for walking.

### Known limitations
- The dungeon scene was **not** rebuilt. `ManaComponent`, `StatusComponent` and `SkillCaster` are `Ensure()`d
  at runtime by `PlayerRig`, and `EmberdelveLevelBuilder` also adds them, so a future
  `EmberdelveBootstrapMenu.BuildDungeonOnly()` run produces the same rig — but the current scene file still
  predates Milestone 4.
- Cooldowns, mana and activation ids are enforced by the **local** authority. They are shaped for server
  authority (per-entity sequences, per-activation ids, authority-owned damage resolution and multipliers) but
  no server exists yet.
- Batch mode has no keyboard or mouse (`input devices: mouse=none keyboard=none`), so the harness drives
  public entry points (`TryCastSlot`, `BeginSwing`, `Fire`, `SwitchWeapon`) rather than simulating key presses.
  The F/G bindings are asserted through `SkillKeyLabel` and the regenerated asset, not through a key event.
- HUD skill widgets are built in code; there is no prefab, no skill tooltip, no drag-to-reorder, and no
  controller glyph art.
- Multi-shot spread is 26 degrees total; the harness derives its lane from that number rather than assuming
  a fixed lateral offset.
- Loot parking and enemy parking are harness bookkeeping, not a gameplay feature.
- No unit-test framework: verification is the batch-mode smoke harness plus these logs. Project Auditor has
  not been run for this milestone.

## Milestone 5 — IN PROGRESS (grid inventory, equipment, chests)

Committed baseline for this milestone: `99dabd30 Milestone 4: add mana and class skill kits`.

### Sub-milestones (the order this milestone is built and verified in)

**M5.1 Item grid model** — `ItemData` gains a grid footprint, an equipment slot, an optional granted
`WeaponData`, and its own `StatModifier[]` (reusing the Milestone 4 stat vocabulary so equipment feeds the
same `CharacterStatStack` consumption points as passives). Stack rule: only non-equippable items may stack,
and `maxStack` is the stack cap; equippable items are forced to `maxStack = 1` in `OnValidate`.

**M5.2 Grid storage** — `Inventory` becomes a real `width x height` cell grid instead of a flat list of
stacks, keeping its existing public surface (`Capacity`, `Slots`, `TryAdd`, `TryUseSlot`, `RecoveredValue`,
`CarriedValue`, `Clear`) so every Milestone 1-4 assertion keeps meaning the same thing. Adds placement
validation (`CanPlace`, `FindFreeOrigin`, `TryAddAt`, `TryMove`, `TryRemoveAt`), used-cells accounting, and
refusal reasons. An item occupies `footprint.x x footprint.y` cells; a multi-cell item cannot be placed where
it would overlap another item or leave the grid, and a stack only accepts units of the same item up to
`maxStack`.

**M5.3 Equipment slots** — new `Equipment` component with Weapon / OffHand / Head / Chest / Legs slots.
Equipping a weapon item replaces the loadout's primary weapon through `ClassLoadout`, which already re-resolves
the `DefenseProfile`; an off-hand item feeds the shield path that `CharacterClassData.offhandShield` already
models but nothing could fill before. Armor and clothing contribute `StatModifier`s through a new named
source on `CharacterStatStack`, so health, mana, regen, movement, block reduction and damage multipliers all
update from the same rule passives use. Equipping out of the grid removes the item from storage; unequipping
puts it back (and is refused, with a reason, when the grid has no room for it).

**M5.4 Inventory + equipment UI** — a visible `InventoryScreen` panel: the cell grid drawn to scale with
each item's footprint, an equipment column, click to pick an item up and click again to place or drop it,
right-click to equip or use, and a close button. Toggled with **I** or **Tab**, which are already bound in
the input asset and were unused before, so nothing collides with WASD, mouse buttons, `E` interact, `R`
reload, `Q` swap, `F`/`G` skills or `1`-`4` hotbar.

**M5.5 Chests and loot tables** — new `LootTable` asset (entries of item / min / max / weight) rolled with an
explicit seed so the same chest in the same layout always yields the same loot, and `ChestInteractable` with
at least two variants driven by that data: a **Supply Chest** (instant open, common table) and an **Ember
Chest** (must be held open, richer table). Chests are placed from `DungeonLayoutData.chestSpawns` so content
stays data-driven, and they hand loot to the existing `ILootHost` path, which means a full bag is a real
outcome and leftovers stay in the chest.

**M5.6 Verification** — new smoke coverage for grid fit / overflow / partial acceptance, move and remove,
equip and unequip effects on real combat and movement numbers, chest rolls and variants, plus the existing
combat / archer / AI / death / extraction / lobby / result-loop regressions, all in batch mode.

### Controls decided for this milestone
- `I` or `Tab` — open / close the inventory + equipment screen (existing unused bindings).
- Left click an item, left click a destination cell — move it; left click on the grid background — drop it
  back into storage.
- Right click an item — equip it (equippable) or use it (consumable / ammo).
- `1`-`4` hotbar, `E` interact, `Q` swap, `F`/`G` skills, mouse buttons: unchanged.

### Scene/bootstrap policy for this milestone
No hand setup and no scene rebuild required: chest content is spawned at run start from the layout asset by
an ensured `ChestSpawner`, and `EmberdelveLevelBuilder` / `EmberdelveAssetLibrary` produce the same objects and
assets through the existing `EmberdelveBootstrapMenu` paths so a future rebuild produces identical content.

## Pause snapshot — 2026-10-13 (M5 unfinished)

### Current checkpoint

- The local Pi process was stopped on the user's request. Its terminal session ended; no Unity batch-mode process for this project was running at the stop.
- HEAD remains 99dabd30 — Milestone 4: add mana and class skill kits. M5 changes are present in the working tree and intentionally uncommitted.
- M5 work exists across ItemData, grid Inventory, Equipment, InventoryScreenController, LootTable, ChestInteractable, and ChestSpawner; the level/config data and Emberdelve_Level01 scene were wired with chest spawns. The asset library/bootstrap creates sample gear, three loot-table assets, and chest/gear materials.
- The M5 smoke entry point is Emberdelve.EditorTools.EmberdelveRuntimeSmokeTest.RunInventoryFlow (Smoke:items).
- The inventory smoke harness was added to Assets/Emberdelve/Editor/EmberdelveRuntimeSmokeTest.cs. It exercises grid storage, equipment, armor/block, and chest containers.

### Latest verification evidence

- Logs/m5_compile5.log — Unity batch compile exited successfully.
- Logs/m5_data3.log and Logs/m5_wire1.log — batch runs exited successfully.
- Logs/m5_items1.log — FAIL checks=189 failures=22 (superseded by the next run).
- Logs/m5_items2.log — latest inventory/chest smoke: FAIL checks=203 failures=15. M5 is not verified or complete.
- The latest 15 failures cluster in:
  1. Grid/stack test setup: five 2x3 items were accepted into a 40-cell grid; the assertion assumed 10 remaining cells cannot fit another 6-cell footprint. Verify geometric first-fit, and make the partial-stack test exceed the configured stack/item capacity.
  2. Equipment defense path: shield/block results report no reduction, cuirass does not reduce damage, and guard is inactive in one phase. Trace equipped DefenseProfile, damage mitigation, and harness guard activation/state. Do not weaken assertions; correct either runtime behavior or a proven harness setup issue.
- Earlier compile errors in m5_compile1 through m5_compile4 were followed by clean m5_compile5; no M5 smoke run is green yet.
- M4 regression logs are from before the M5 edits. Rerun the full combat, Archer, AI, death, extraction, lobby, and result-loop flows after fixing M5.

### Resume checklist

1. Read this snapshot and the M5 sub-milestone plan above; inspect m5_items2.log plus the current Inventory.cs, Equipment.cs, PlayerRig.cs, CharacterStatStack.cs, and the ItemFlow harness before editing.
2. Fix the 15 remaining assertions using diagnostics and direct state checks; preserve coverage for grid placement/partial acceptance, equip/unequip stats and defense, chest open/loot persistence, and inventory UI interactions.
3. Run the clean compile, RunInventoryFlow, then all listed M4 regressions. Record the final command, log, and pass count for each.
4. Review the complete M5 diff, update this handoff with remaining limitations, then commit M5 only after the milestone is green. Do not include generated Library/ or Logs/ files in the commit.


## 19October2026 — Unity orchestration resumed
Current authoritative forward plan: Docs/ROADMAP.md. Codex manager: C:/Users/yrapa/.pi/agent/projects/unity-emberdelve. Prior M5 work preserved in checkpoint. Fresh combat201/0; inventory197/10fail despiteexit0, U1A fixture/exit then U1B equipment. Full prototype/online/persistence/visuals not accepted. Historical entries above remain evidence, not the current queue.
