# NetFinal modular spawns and content clearance — 8 November 2026

> Historical implementation note. Current acceptance and remaining work: [CURRENT_WORK](../CURRENT_WORK.md) and [raid acceptance](RAID_UI_ACCEPTANCE_20261108.md). Status statements below describe the original slice.


The failing run `Logs/runs/20261108-142733` (NetFinal 168/4) used seed `3664304728` (signed map seed `-630662568`, hash `1764F4AA`). B was registered at `(39, 0.15, 13)` and moved to `(38.604, 0.129, 12.711)` on the first tick without input, producing 14.70 m/s. Reconstructing the plan showed the first Combat module at `(1,0)` and its first remapped scene loot at `(39, 0.45, 13)`. Scene loot has an enabled solid collider. The old spawn search examined module colliders before content was placed and missed the overlap.

Content placement now chooses deterministic supported cells clear of authored module colliders and all player spawn capsules, refusing the map if no safe cell exists. The existing loot, chest and enemy specs are retained; scene actors are placed at their corresponding remapped cells. The focused EditMode test `ContentPlacementAvoidsSpawnsAndObstaclesAndFailsWithoutSafeCell` passed 1/1 and checks overlap, deterministic selection, blocked geometry and voids. On the same seed, the next NetFinal `Logs/runs/20261108-143543` reported 168/2: B remained at `(39, 0.129, 13)` after the first tick with zero horizontal speed. The other two failures used the legacy static layout as expected spawn positions.

NetFinal now reconstructs the authored modular plan independently from the server seed, verifies the plan hash, instantiates authored rotated module prefabs and closures in a temporary additive editor scene, and computes expected spawn cells from their geometry. It checks A/B and the first unoccupied spawn for late C against those independent expectations. The online UI check now includes the current `map=server-selected` log field and matches the chosen class to the server admission. NetFinal passed **170/0** on the same seed in `Logs/runs/20261108-144056`.

The reconstructed spawns use the same production collision-search method, so this is independent of server-reported coordinates, but not an independent implementation of the algorithm. The gate still calculates A's route and yaw using the old static layout; this passing fixed-seed run does not establish that the route is clear for other maps. NetMovement, NetFinal route adaptation and a fresh full regression remain open. The NetFinal reconstruction helper was moved to `EmberdelveModularSpawnProof` after this run; that refactor compiled successfully but was not included in the recorded gate run. A subsequent guard refuses maps with missing or insufficient scene actor placement records; EditMode passed 883/883 after these changes. A final NetFinal rerun is still required.


## Follow-up: physical final walk fixture

NetFinal no longer chooses its walk yaw on the legacy Ashen Halls grid. It first verifies the unchanged production spawn expectations, then requests a development-only server fixture through `EMBER_FINAL_PROOF_PATH`. The server measures a supported capsule-clear +Z lane on the installed modular map, keeps it clear of stationary B, and stages A. The gate requires the matching map hash, enough distance for the existing resolved-speed bound, and convergence of both A's prediction and B's observer view before starting input. The real walk must begin at that replicated fixture position.

The three-second duration, ±20% displacement, 1.15× peak speed and zero prediction snap/fight requirements remain. Prediction checks now use the existing exact interval counters captured when the script starts, excluding the deliberate setup teleport without resetting lifetime diagnostics. `NetInputSender` emits the interval at script-end as well as the existing jump interval.

Changed: `EmberdelveNetFinalGate.cs`, `NetInputSender.cs`; coordinated server fixture and lane search in `NetMovementServer.cs` and `MovementProofGeometry.cs`. Parent integration owns compilation and mirror verification; no new passing runtime result is claimed here.
