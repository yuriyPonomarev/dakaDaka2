# Modular dungeon network proof (development only)

> Historical implementation note. Current acceptance and remaining work: [CURRENT_WORK](../CURRENT_WORK.md) and [raid acceptance](RAID_UI_ACCEPTANCE_20261108.md). Status statements below describe the original slice.


## Latest run status — 7 Nov 2026

The current modular mode is the only gameplay mode; the catalog includes the central `Crypt_Skybridge` module. A clean, uninstrumented NetDoor run passed **31/0** at `DakaDaka_ci/Logs/module-tests/net-door-shutdownprobe-clean-editor.log`, including clean process shutdown. The latest AI-enabled NetDoor gate passed **37/0** at `DakaDaka_ci/Logs/gate-netdoor-hearing-20261107-230745/summary.json`; focused `DungeonDoorInteractableTests` passed **11/11**. A/B clients observed matching open state and revision after an authored server EnemyAgent moved 2.187 m toward its live target and opened the closed door. The gate also retains player authority, far/LOS rejection and late-join snapshot checks. No visual review was performed.

AI stops against closed door panels and can retreat from an overlap. A chasing enemy can open a closed door only beside it, for a live current player target and after a cooldown; the network path publishes through the server door revision and broadcast stream. Hearing provides one bounded last-known search point and does not continuously track an occluded target. Navigation still treats closed doors as passable, so an enemy has no automatic alternate route if it cannot open/reach the door.

Use a Development Build that contains the required `Emberdelve_ModularCrypts` scene and generated `Resources/Emberdelve/DungeonModuleCatalog`. Start three independent player processes. The modular map is the only gameplay path.

## Server

```text
-ember-net server -ember-net-modular-proof-server -ember-net-no-enemies -ember-net-max-players 3 -ember-port 7789 -emberdelveRaidSeed 12345
```

## Client A and client B

```text
-ember-net client -ember-address 127.0.0.1 -ember-port 7789 -ember-slot module-a
-ember-net client -ember-address 127.0.0.1 -ember-port 7789 -ember-slot module-b
```

The server checks at planner level that a nearby seed produces a different placement, blocks any remote actor spawn without a matching readiness ACK, and emits `TWO-CLIENT PASS` only after two remote actors have spawned. The alternate candidate is not applied to a scene by that check. Server and each client should log the same map seed and layout hash. The scene assembler reports closure, socket-pair, nine-spawn, extraction, and navigation connectivity checks before it ACKs the plan.

Repeat the full three-process run with a different fixed seed, for example `-emberdelveRaidSeed 12346`. Each run must independently reach `TWO-CLIENT PASS` and the scene-applied marker. Compare the server's `modular plan seed=... hash=...` line and both clients' `map plan received seed=... hash=...` lines between runs; the layout hash and at least one module/socket/rotation placement should change. This second run is what proves the alternate seed's assembled geometry and nav checks.

## Expected proof markers

- Server: `[modular-proof] planner seed variation PASS (candidate not scene-applied) ... layoutChanged=true`.
- Server and each client: `[modular-proof] scene-applied ... modules=9 ... closuresConsistent=true playerSpawns=9 extraction=active-at-cell:... navConnected=true`.
- Each client: `map ready hash=... extractionActive=true` matching the server's layout hash.
- Server: `modular plan ready client=... hash=...` appears before that client's `[modular-proof] actor spawned after ACK ...` line.
- Server: `[modular-proof] TWO-CLIENT PASS remotes=2 ... everySpawnAfterMatchingACK=true`.

The proof flag is development/editor only and does not change the legacy scene path. This runbook describes a proof to execute; it is not itself evidence that the live session has passed.
