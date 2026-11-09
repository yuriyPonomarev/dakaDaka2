# Modular 3x3 crypt map — work in progress

> Historical implementation note. Current acceptance and remaining work: [CURRENT_WORK](../CURRENT_WORK.md) and [raid acceptance](RAID_UI_ACCEPTANCE_20261108.md). Status statements below describe the original slice.


## Current slice

### Door and central-bridge update (7 Nov 2026)

- The current modular mode remains the only gameplay mode. Its catalog now has 10 prefabs, including the central `Crypt_Skybridge` bridge module. Module generation and scene application are validated against the plan; existing planner and content-builder changes are documented in their own integration notes.
- Modular doorway interactions are server-authoritative online and local offline. Each planned shared boundary creates one door. Closed panels block player movement and LOS; replicated open/close state updates the hinge and blocker. The development NetDoor run passed gameplay assertions for shared A/B state and revision, far and LOS rejection, blocker agreement, and a late-join snapshot.
- A clean, uninstrumented NetDoor run passed **31/0** at `DakaDaka_ci/Logs/module-tests/net-door-shutdownprobe-clean-editor.log`, including process shutdown. The new AI doorway proof passed **37/0** at `DakaDaka_ci/Logs/gate-netdoor-hearing-20261107-230745/summary.json`; focused `DungeonDoorInteractableTests` passed **11/11**.
- AI movement uses a sphere sweep against closed door panels. Enemies remain blocked until an authoritative open. A chasing enemy may open a door beside it only for a live current player target, within range and after cooldown. Networked opens use the existing revision and broadcast stream. The AI proof recorded 2.187 m of server EnemyAgent movement toward its live target, door revision 3, and matching open state/revision at both clients. Sound memory is bounded and does not continuously track a hidden target. See `Docs/Tasks/MODULAR_ENEMY_DOORS.md`.
- No in-Unity visual review was performed; map, doors, enemy movement and HUD remain awaiting visual review.

- Added an authored module contract (12×12 cells, local `Socket_*` transforms, four `Closure_*` panels and a `PlayerSpawn` anchor), a deterministic 3×3 planner and a bounded network snapshot codec.
- The planner builds a random spanning tree with optional loops, balances repeated Hall/Ossuary/Shrine/Vault modules, and requires one Entry at slot 0, one connected Extraction at slot 8 and nine separated player spawn anchors.
- Added a runtime scene assembler that instantiates the catalog prefabs, opens only the planned sockets, builds a matching 36×36 `DungeonLayoutData` and navigation grid, relocates the existing enemy/loot/extraction actors and hides the old blockout geometry.
- Modular Crypts is the only gameplay map. Offline play, Host, and dedicated servers always create a fresh raid seed and load `Emberdelve_ModularCrypts`; Join follows the server's synchronized scene and plan. No classic-map switch is supported. The server waits for the client to apply the same catalog fingerprint/hash before spawning its player actor.
- Added an additive `Build Modular Crypts Scene` menu path. It refuses to overwrite an existing/open modular scene, saves a separate `Emberdelve_ModularCrypts` scene, adds it to Build Settings, closes only the generated scene and restores the previously active scene.
- The lobby has one `DESCEND` action. Host always opens the same modular map; Join follows the server. Dedicated server launch uses that map by default.
- Player build helpers require the saved modular scene and fail if it is missing. Build Settings retain Lobby first and Modular Crypts second; the old Level01 asset is kept for editing/reference but removed from the player scene list.
- The first raid may use a development seed override; it is consumed once per process. Subsequent raids use fresh entropy, including a result-screen retry that reloads the modular scene.
- Development/editor builds record an offline run only after RunDirector starts with the exact installed layout hash and the assembler has passed geometry, navigation and extraction validation. Loading and completing two modular raids in the same process emits `TWO-RAID PASS` only when both raid seeds, map seeds and layout hashes differ.
- In that new scene, clients keep the extraction interactable and see read-only floor-loot objects; their pickup colliders are disabled.

## Not finished / known limits

- The authored module catalog and generated modular scene are present in the current project snapshot; the currently open scene and Level01 were not rewritten or deleted. Editor build/open actions now target Modular Crypts for ordinary play.
- Network floor loot, dropped loot and chest presentation are not replicated or interactable on clients. The visible floor pickups are read-only until a server-authoritative world-item protocol is added.
- Network equipment drops and peer visibility remain unproven/open; the AI door gate does not cover equipment behavior.
- A two-client server-authoritative plan/hash proof has passed for seeds 12345 and 12346, including plan ACK before remote actor spawn and scene closure/spawn/extraction/navigation validation. The latest single-mode UI/build changes, bounded startup retry for extraction role activation, and offline two-raid proof have not been recompiled/run in Unity; the latter requires two scene loads in one process. The earlier offline attempt failed because the extraction interactable was not active during the assembler's first `Awake`; the assembler now logs the reason and retries only that transient condition for 16 seconds while keeping RunDirector gated. Network proof does not verify world-loot pickup on clients or every full raid interaction.

## Changed code

`DungeonModuleDefinition`, `DungeonModuleCatalog`, `DungeonModulePlan`, `DungeonModulePlanner`, `DungeonModulePlanCodec`, `DungeonModulePlanSession`, `DungeonModuleSceneAssembler`, `ModularOfflineRunProof`, `NetDungeonModulePlanClient`, `NetServer`, `NetBootstrap`, `NetLaunchArgs`, `NetSceneRoles`, `RunDirector`, `RunContext`, `EnemyAgent`, `LobbyController`, `LobbyScreenView`, `LobbyOnlinePanel`, `NetLobbyBridge`, `EmberdelveLevelBuilder`, `EmberdelveProjectSetup`, `EmberdelveWindowsBuild` and `DungeonModulePlannerTests`.
