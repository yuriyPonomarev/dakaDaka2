# Ballistic arrows/bolts and recovery — 9 November 2026

## Scope and implementation

User requested ballistic arrows/bolts and picking spent ammunition back up. Existing flights already used the server-owned fixed1/60s ballistic integrator, swept collisions, charge/speed, range/time caps and air-time damage falloff. This slice reuses that simulation rather than adding a second physics system.

- Standard bow/bolt gravity is now9.81m/s2; launch speeds remain42/54m/s respectively, giving bolts the flatter trajectory from speed. Existing special-shot gravity modifiers, damage, range and lifetime caps remain. Asset builder and authored weapon assets agree.
- An accepted ammo-paid player flight can publish at most one recovered unit at its terminal point. A downward level-geometry query settles the spent round on the floor near impact. Piercing shots recover once at terminal impact, not at every pierced body. Paid multi-shot slots recover one each; free/non-player projectiles do not mint ammunition. Skill flights also publish the same launch/end visual events as ordinary shots.
- New catalog items: recovered_arrow / recovered_bolt, stack20, grant1. They are ordinary server-owned ground loot with narrow shaft/fletching presentation, no bob/spin. Pickup uses existing range/LOS/admission, partial-capacity and spent-source checks; every admitted client and late join receives the floor snapshot.
- Picking up places a round in the bag. Use it with the matching ranged weapon selected to add one reserve round; R reloads using the existing system. Wrong weapon or full reserve refuses consumption. Legacy untyped ammo bundles retain their prior behaviour.
- Protocol9 guards new catalog compatibility. No new wire layout or client-authored damage/items.

## Bounds and limits

Recovery is a floor drop near impact, not a persistent arrow embedded in a moving body or wall. A round falling outside level geometry is not recoverable. No drop is spawned when live floor pickups reach224, leaving headroom under the existing256-entry snapshot cap; ordinary loot remains available. This is a bounded raid implementation, not an unbounded projectile-debris simulation.

## Verification and acceptance

Checkpoint Logs/harness/before-recoverable-ammo.json. Broad planner selection reviewed and narrowed to new RecoveredAmmoTests plus ProjectileSim, ProjectileBlock, NetCombat, NetSkill, NetInventory and two-window play acceptance. No unrelated gates.

- Initial compile caught use of EntityKind.Player; corrected to the project's EntityKinds.IsPlayer. Removed only the stale mirror lock after confirming no mirror Editor was running; main Editor untouched.
- First executable test pass had2 fixture errors: RangedCombat already required PlayerRig, so adding another returned null. Corrected fixture creation order. editmode-v3:3/3 PASS (both typed ammo inventory transactions, refusal preservation, exact one-unit grant/replay rejection, actual floor placement/void refusal).
- Initial ProjectileSim expectations still encoded old gravity6/3 and old drop ratio. Update assertions to9.81 and squared speed ratio; only this affected gate will be repeated after the network run. No production simulation changes to satisfy assertions.
- Run20261109-143435-327: ProjectileBlock51/0, NetCombat312/0, NetSkill127/0, NetInventory148/0 PASS; playable build PASS. Aggregate initially FAIL solely from five obsolete ProjectileSim expectations. Focused rerun20261109-144036-256: ProjectileSim109/0 PASS; no other gates repeated.
- Actual two-window seed11/protocol9 acceptance: client archer fired into floor, saw Take Arrow, E transferred one unit to the third bag slot, right-click consumed it. Host warrior switched to crossbow, fired, saw Take Bolt, E collected once; repeated E produced no extra unit. Bag2/40 became1/40 after right-click, HUD reserve6 became7, toast Used Bolt (+1 ammo). Evidence screenshots and both logs: Logs/acceptance/20261109-recoverable-ammo. One recovery log for each of the two paid shots. Windows closed via client11/host14.
- A transient magenta impact effect is visible in arrow-floor-2.png; recovered shaft itself renders correctly. Existing impact-effect material presentation is separate follow-up work.
- Playable: Builds/Playable/20261109-143435-327/EmberdelveNet.exe. Main Editor untouched. Internet latency/load and cross-client contested pickup were not manually re-certified; existing NetInventory authority proof passed.
