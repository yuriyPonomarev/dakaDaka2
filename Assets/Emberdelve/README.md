# Emberdelve — first-person PvPvE dungeon-extraction prototype

Original dark-fantasy extraction prototype built from scratch in this project. No art, audio,
text or level data from any existing game was copied: everything under `Assets/Emberdelve` is
generated placeholder geometry, procedurally synthesised audio and original names/wording.

Unity version used: **6000.2.5f1** (URP, new Input System).

---

## Play it

1. Open `Assets/Emberdelve/Scenes/Lobby.unity`.
2. Press Play. Pick **Warrior** or **Archer**, then click **DESCEND**.
3. In the Ashen Halls: kill Hollow Servitors / Ashen Wardens, take loot, then stand on the
   **Ember Gate** and hold **E** for 3 seconds to extract.

The build settings already list `Lobby` first and `Emberdelve_Level01` second, so *Play from
browser* / a standalone build starts in the lobby too.

### Controls

| Action | Keyboard & mouse | Gamepad |
| --- | --- | --- |
| Move | `W A S D` | left stick |
| Look | mouse | right stick |
| Sprint | `Left Shift` | left stick press |
| Jump | `Space` | A / Cross |
| Crouch (hold) | `Left Ctrl` | B / Circle |
| Attack / draw bow | mouse left | right trigger |
| Guard / aim (hold) | mouse right | left trigger |
| Interact / hold gate | `E` | X / Square |
| Use inventory slot 1–4 | `1 2 3 4` | — |
| Inventory | `I` or `Tab` | — |
| Reload | `R` | — |
| Pause | `Esc` | Start |

Warrior: 150 HP, tankier, sword swing with a 110° arc, can guard (blocks ~65 % from the front).
Archer: 100 HP, faster, no guard, bow with a 5-arrow magazine, 20 reserve arrows, draw-and-release
charge affects damage.

---

## Menu tooling (`Emberdelve/` in the Unity menu bar)

| Menu item | What it does |
| --- | --- |
| Build Vertical Slice (all) | Re-creates data assets, materials, lobby scene and dungeon scene |
| Build Data Assets Only | ScriptableObject classes / weapons / items / enemies / layout / nav grid |
| Build Dungeon Scene Only | Regenerates `Emberdelve_Level01` from the layout asset |
| Build Lobby Scene Only | Regenerates `Lobby` |
| Open Lobby / Open Dungeon Scene | Loads the generated scene |
| Validate Slice | Headless check: nav grid matches the layout, every cell reachable from spawn, A* route to the gate, and every gameplay reference in the scene resolves |

Command-line equivalents (used to verify this build):

```bash
UNITY="/c/Program Files/Unity/Hub/Editor/6000.2.5f1/Editor/Unity.exe"
"$UNITY" -batchmode -quit -nographics -projectPath . \
  -logFile Logs/build.log \
  -executeMethod Emberdelve.EditorTools.EmberdelveBootstrapMenu.BuildEverything

"$UNITY" -batchmode -quit -nographics -projectPath . \
  -logFile Logs/validate.log \
  -executeMethod Emberdelve.EditorTools.EmberdelveBootstrapMenu.ValidateSlice
```

---

## Headless gameplay verification

`Assets/Emberdelve/Editor/EmberdelveRuntimeSmokeTest.cs` enters **real play mode** headless,
lets the actual systems run (Start/Update, physics, AI, damage, UI), then asserts on observable
state. It only moves transforms and calls the same public entry points input and UI call.

```bash
for m in RunCombatFlow RunEnemyAi RunExtraction RunDeath RunLobbyFlow RunArcherFlow RunResultLoop; do
  "$UNITY" -batchmode -nographics -projectPath . -logFile "Logs/smoke_$m.log" \
    -executeMethod "Emberdelve.EditorTools.EmberdelveRuntimeSmokeTest.$m"
done
```

Each run prints `[Emberdelve][Smoke:<mode>] PASS|FAIL checks=N failures=N` and exits Unity itself
(no `-quit` needed).

| Mode | What it proves |
| --- | --- |
| `RunCombatFlow` | Real melee swing through `MeleeCombat` (windup → arc scan → authority → damage), authority rejects out-of-range hits / replayed command sequences / faster-than-cooldown spam, enemy dies, kill counter increments, enemy-dropped loot is spawned on the loot layer with a usable collider, pre-placed and dropped loot are inside the interaction probe, inventory and carried value update |
| `RunEnemyAi` | Enemy detects the player (`Patrol/Investigate → Chase/Attack`), closes distance using the nav grid, and damages the player |
| `RunExtraction` | Loot requirement enforced, interactor locks onto the gate when standing on it, hold-to-extract completes, run state becomes `Extracted`, result screen builds, cursor released |
| `RunDeath` | Enemy attacks accepted, player health reaches zero, run state becomes `Dead`, result screen builds, result recorded |
| `RunLobbyFlow` | Clicking the real class card selects the Archer, `DESCEND` loads the dungeon, class health/ammo/melee-disabled applied to the run |
| `RunArcherFlow` | A shot through `RangedCombat.Fire` consumes an arrow, is accepted by the authority and damages the enemy; projectile visual spawns |
| `RunResultLoop` | Extract → result screen → *Descend again* (fresh run, full health, empty record) → die → result screen → *Return to lobby* → lobby shows the previous run summary |

Every dungeon mode also checks the baseline: combat authority registered, run state `Running`, HUD
canvas built, first-person camera present, player alive at spawn, `CharacterController` enabled with
height/radius matching the class, movement reports grounded, and the player neither drifts nor falls
through the floor while idle for two seconds.

Last recorded run of the suite: **7/7 modes PASS, 201 checks, 0 failures**.

---

## Layout of the code

```
Assets/Emberdelve/
  Runtime/
    Core/       contracts (ICombatEntity, AttackIntent, HitCandidate, RunState), GameServices,
                GameEvents, GameConfig, NavGridData, RunContext
    Combat/     HealthComponent, DamageAndScanning (HitScanner, DamageResolver),
                LocalCombatAuthority, MeleeCombat, RangedCombat, ProjectileVisual
    Player/     PlayerRig, ClassLoadout, WeaponVisualRig
    Movement/   PlayerMovement (CharacterController), PlayerLook
    Input/      EmberdelveInput (action maps), EmberdelveInputFallback (builds the same maps in code)
    AI/         NavGridPathfinder (A*), EnemyBrain (state machine), EnemyAgent
    Items/      Inventory, LootPickup, HotbarController
    Interaction/ Interactor, ExtractionPoint
    Level/      DungeonLayoutData (cells → geometry, nav mask, spawns)
    Flow/       RunDirector (run state, loot host, extraction sink), LobbyController
    UI/         UiFactory, HudController, LobbyScreenView, ResultScreenController
    Feedback/   ProceduralAudio, FeedbackDirector, ImpactFlash, TorchFlicker
  Editor/       project setup, materials, asset library, level builder, lobby builder,
                bootstrap menu, runtime smoke test
  Data/         ScriptableObjects (classes, weapons, items, enemies, layout, nav grid, config)
  Art/Material/ generated placeholder materials
  Scenes/       Lobby.unity, Emberdelve_Level01.unity
```

### Multiplayer seams already in place

* Clients/AI only send **intent** (`AttackIntent` + `HitCandidate`); no damage numbers. Damage is
  computed by `ICombatAuthority` from weapon data plus geometry it re-validates (registry lookup by
  combat id, sequence anti-replay, cooldown, origin attachment, range, line of sight).
* `CombatRegistry` owns combat ids, so a target reference handed over by a client is never trusted.
* `LocalCombatAuthority` is a drop-in stand-in for a server authority; `GameServices` resolves it by
  interface, so swapping in a networked implementation does not touch `MeleeCombat`, `RangedCombat`
  or `EnemyAgent`.
* `RunContext` is the single place a session/class choice is recorded — a real session service can
  fill it instead of the local lobby.
* Enemy navigation is a grid A* (`NavGridPathfinder`) rather than a baked NavMesh, so the same grid
  can be validated server-side.

---

## Placeholder status / known limits

* Geometry is primitives + generated materials; torches are point lights with a flicker script.
  No post-processing volume is added, so emissive parts read as flat bright surfaces until a bloom
  volume is added.
* Audio is generated at runtime (`ProceduralAudio` builds clips with `AudioSource` + samples); no
  audio assets exist. Audio could not be verified headless (no audio device in batch mode).
* UI uses legacy `UnityEngine.UI.Text` (no TextMeshPro font-asset dependency). Canvas presence is
  verified headless; actual pixels are not.
* Headless batch mode exposes no keyboard/mouse device, so the raw *input bindings* could not be
  driven in the automated tests; the tests call the same entry points the input handler calls
  (`MeleeCombat.BeginSwing`, `RangedCombat.Fire`, `Button.onClick.Invoke`).
* One intent resolves one hit: a swing damages the single closest valid target in its arc. This is
  deliberate (a command sequence must not fan out into unlimited damage) and is documented in
  `MeleeCombat.EnterActive`.
* `Enter Play Mode Options` is disabled in `ProjectSettings/EditorSettings.asset`. With it enabled
  the project's static registries (`GameServices`, `CombatRegistry`, `RunContext`, `GameEvents`)
  keep editor state between play sessions and scene objects are not re-initialised.
