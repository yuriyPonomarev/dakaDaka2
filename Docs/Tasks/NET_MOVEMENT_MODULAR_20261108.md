# Modular movement proof — 8 November 2026

> Historical implementation note. Current acceptance and remaining work: [CURRENT_WORK](../CURRENT_WORK.md) and [raid acceptance](RAID_UI_ACCEPTANCE_20261108.md). Status statements below describe the original slice.


The prior NetMovement run `Logs/runs/20261108-144728` completed 189 checks with two failures: the wall runner stopped at physically solid geometry whose capsule centre fell outside the logical walkable grid. The existing follow-up change in `MovementProofGeometry` rejects these unsuitable wall lanes; it was written after that run and still needs live verification. A new regression test supplies a physical wall beyond the logical room boundary, requires rejection, then extends the logical room and requires success.

The existing movement fixture verifies real spawn slots from the actual modular plan on the server and clients before staging the actors on supported, collision-free paths. Owner and observer convergence is required before movement begins. Walk, sprint, jump, wall collision, cheat rejection and speed limits retain their tolerances. Staging is excluded from the measured speed interval; exact honest-script prediction statistics cover the interval after staging.

For NetFinal, a development-only `EMBER_FINAL_PROOF_PATH` file channel now stages `fin-a` after `fin-b` joins, using the resolved walk speed to require a full three-second supported +Z lane and avoiding the stationary observer. The matching gate owns the original-spawn and position-convergence checks. This channel is absent from release compilation and accepts no network request. Tests cover supported lane selection, observer clearance and missing ground.

Changed in this continuation: `MovementProofGeometry.cs`, `NetMovementServer.cs`, `MovementProofGeometryTests.cs`. No production movement speed or reconciliation rules changed. Compilation, focused tests and mirror gates are coordinated by the main agent; no new passing result is claimed here.

Continuation: 185648 failed on stale periodic counters (5s reporting vs 3.5s wait). 190122 then exposed observer reporting lag. Bounded polling now waits for new snap counters and original owner/observer convergence tolerances. Full 190643 passed NetMovement 189/0; no production movement rules changed.
