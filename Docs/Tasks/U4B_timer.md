# U4B timer slice

> Historical implementation note. Current acceptance and remaining work: [CURRENT_WORK](../CURRENT_WORK.md) and [raid acceptance](RAID_UI_ACCEPTANCE_20261108.md). Status statements below describe the original slice.


## Behavior
- Added a configurable raid duration to `GameConfig`, defaulting to the design's 10 minutes (600 seconds); the asset builder writes the same default.
- `RunDirector` exposes remaining seconds and checks the deadline on its host clock. Extraction rechecks the deadline at the decision point, so a hold completed at/after the deadline cannot become `Extracted`, even before the next `Update`.
- Client-local time never closes extraction; the server rechecks against its own clock. Offline pause freezes elapsed raid time, while a network player's local pause remains presentation-only and does not pause the hosted raid.
- At expiry, the host latches `RaidExpired`; extraction and new admission are denied. Every admitted player still running receives lethal server-authored timeout damage through the normal health/death path and gets the ordinary `Dead` result.
- Network deaths still require corpse custody before publishing the result. Timeout does not bank loot or write durable state; corpses and loot are scoped to this server process and disappear when it stops.
- A development-only NetExtraction process scenario now installs a five-second server-local deadline after two clients join. It verifies each exact seeded gear UID moves to that owner's distinct corpse (and not the other owner's), then expects two Dead owner results, deadline-based extraction refusal, and refusal of a third late join. The override does not mutate GameConfig; the production countdown stays disabled.
- The feature remains opt-in: `enableRaidCountdown` defaults to false, including in the playable asset builder. Network replication is runtime-gate verified, but graphical HUD review is still pending.
- When enabled, the server publishes an owner-only clock sample every 250 ms: enabled state, remaining server seconds, and expiry. Remote owner HUD interpolates only between those received samples using local unscaled time; that display never grants or denies extraction. Listen-host/offline HUD reads the `RunDirector` clock directly. The HUD changes to `EXTRACT SOON` at 60 seconds and `FINAL SECONDS` at 10 seconds. A network client receives a server-authored warning/expired state as well as the remaining time.
- Pure EditMode behavior tests cover configured duration, countdown, inclusive deadline, invalid elapsed values, server-only extraction closure and elapsed-time accounting across offline pauses.

## Scope and limits
- Timeout currently maps to `Dead`, using the established result and corpse-custody model. The timeout damage source is neutral, and no per-hit PvP credit is assigned.
- Persistent stash banking remains deferred to U4C. Countdown presentation/replication is implemented, but its visual layout has not had a live Unity review. The open Unity window is on `Emberdelve_Level01`; the screenshot/read-only visual inspection attempt stalled, and the editor was left untouched.
- Network display is based on the latest authoritative sample plus client-side interpolation. It can lag the server by transport latency plus up to one 250 ms publish interval; extraction and expiry decisions remain server-authoritative. Offline pause freezes raid elapsed time; a network player's local pause is presentation-only.

## Checks
- Added `RaidCountdownRulesTests` in the existing EditMode rules assembly.
- Timer rules passed 5/5 and countdown checks passed 8/8. Expanded NetExtraction including both owners' server clock/warning/expiry assertions passed 136/0. Production countdown remains disabled until a graphical HUD review confirms legibility and placement; network gate success alone does not establish UX readiness.
