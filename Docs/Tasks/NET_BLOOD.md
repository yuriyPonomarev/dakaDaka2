# Network enemy hit blood

> Historical implementation note. Current acceptance and remaining work: [CURRENT_WORK](../CURRENT_WORK.md) and [raid acceptance](RAID_UI_ACCEPTANCE_20261108.md). Status statements below describe the original slice.


The existing server-reported CombatHit RPC carries a server-session-wide event id and the authoritative hit point. Client-only presentation emits the authored blood particles from a standalone Resources prefab, independently of whether an enemy proxy exists or has already been removed. Because the packet has no impact direction, the network burst uses a spherical emitter so it remains visible from every camera angle. Droplets are larger, brighter, and longer lived. Events with zero or non-finite damage, non-enemy targets, non-finite coordinates, duplicate ids or ids outside the bounded replay window are ignored. The window clears when the local owner's network object despawns. Host presentation remains on its normal local damage path.

The prefab is created by EmberdelveNetBloodBuilder and is also rebuilt by BuildEverything. This is presentation only; no health or authority path is changed. Event ids are global to the lifetime of one NetCombatServer, not per RPC carrier. A window of the most recent 1024 sequence numbers tolerates packet reordering; older events are dropped. It does not replay historical hits to late joiners. Impact direction is not in CombatHitReport, so the burst currently uses a neutral direction. The packet format changed: all server and client builds must be updated together.

Validation: mirror EditMode 796/796 passed; Visual gate 193/0 passed; NetCombat gate 290/0 passed, including delivery of the same authoritative blood event and point to both clients. A broad full gate run was stopped after 16/68 gates to avoid spending excessive time on unrelated checks. The mirror GUI inspection timed out before a graphical result; no Unity terms were accepted. The current client enemy proxy is still a capsule.

Follow-up: the instrumented dedicated-server/two-client run `Logs/quick/netblood_reuse.log` recorded server hit target 5 at (8.517, 1.420, 13.572) and both clients emitted blood event 1 at that same position. The overall NetCombat gate FAILED 297/31 due to other failures including process shutdown timeouts; it is not a clean network acceptance and no graphical review was performed.

Fresh proof build and rerun `Logs/runs/20261105-120712`: NetSeams 163/0, expanded NetCombat 289/0, including two-client blood event delivery and clean process shutdown. Graphical review is still outstanding.
