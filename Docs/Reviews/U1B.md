# U1B — accepted for local equipment/defense core

Final independent acceptance: Codex, 19October2026, after U1B-R1/R2/R3. Local equipment API and actual armor measurement only; mouse inventory, U1C visuals/loop, six-stat no-free-healing rules and real networking remain open.

## Final evidence and review

Sequential manager-owned Unity6000.2.5f1 original-project runs: U1B-R3 items **400/0 PASS exit0**, combat **201/0 PASS exit0**. Each has one positive summary, no captured errors or timeout. Negative control intentionally restores early unequip notification and wrong recovered-loot credit: **400checks/8failures/exit1**, both fault classes caught by specific assertions. Exact Inventory/Equipment source bytes restored in finally against backups. Raw commands/logs: manager review-U1B-R3-items.json, review-U1B-R3-combat.json, review-U1B-MUTATION-items.json and review-U1B-mutation-proof.json (evidence paths under validation/).

Restored SHA256: Inventory `6969e56de80ea089196840378ca5d0318e8cf0c7fbd9742f8bd63e8acc7b6125`, Equipment `32d8608269e7dbabc50a56c96cded11d7e8a35c3003ce67fe3f4c1cad847293e`.

Actual accepted authority/HP measurements at the same1.61m: naked front **15 unblocked**, cuirass front **13.8 blocked0.08**, cuirass rear/side **15 unblocked** at45deg half-arc. Real equipment changes between hits; fresh matching damage capture and exact accepted count/sequence verified. This proves current yaw protection, not network/height/stamina behavior.

Reviewed selected-slot ownership, exact grantedWeapon references, outgoing-shape preflight using incoming freed cells, unchanged state on impossible replacement, silent internal bag transfers without collection credit, and Equipment notification after bag/slot/hand/stat commit. Inside-notification snapshots verify observer state. Tests cover duplicate origin, full-bag valid swap, impossible geometry/refused unequip, conserving return/cycles and stat-source refresh/class-hand restoration. U1A preserved. Dead/uninitialized/null-bag guards are source-reviewed, not every branch independently forced. Definition/count/origin identity remains until U2 UIDs; existing health-pool healing remains for U2A; Equipment.Clear is a reset, not conserving unequip.

R1 fixed the initial compile error, notification order and false full-bag expectation, then exposed a new struct/null compiler error fixed by R2. R3 corrected the only remaining contradictory duplicate assertion: the unselected first copy must remain exactly once at its own origin. No production deletion or skipped failure accepted as green.

Next U1C1 repairs cursor ownership and real click routing. Current cursor removes an item before checked equip; hit testing mixes screen/local coordinates; stale-index swapping and partial return/loot-add paths can lose, duplicate or credit rearranged items. U1C stays aggregate-only until actual windowed mouse/HUD/container and complete-loop review.

## Historical initial rejection (superseded by final acceptance above)

Independent review19October2026. Worker settled89tool calls with code saved. Existing work retained. Controller5936/Pi23580 were confirmed absent before reading sources or starting the engine; same-config Strata6340/5064 remains healthy.

The patch adds useful selected-slot ownership checks, a preflight placement plan, transfers without collection credit, and a substantially improved actual armor measurement. It is not accepted from its summary.

## Reproducible blockers

1. Actual Unity6000.2.5f1 inventory gate fails compilation: `EmberdelveRuntimeSmokeTest.cs(2103,91): error CS0165: Use of unassigned local variable 'highIndexReason'`. The second `out string` declaration is behind `&&`; interpolating it later is not definitely assigned. Process exit1, no summary, no timeout. Evidence: manager `review-U1B-items.json`, raw logs under `validation/U1B-items-20261019-012110`. No combat rerun while compilation is already known broken.
2. Production `Inventory.TryEquipTransfer` and `TryUnequipTransfer` raise synchronous InventoryChanged **before** Equipment updates its slot/hand/stats. A subscriber sees incoming absent but old equipment still worn, or outgoing simultaneously in bag and equipped. This contradicts U1B's explicit committed-state requirement despite the worker comment claiming no half-state. Defer notification until the caller has committed both bag and equipment state; add a subscriber that checks actual state inside the notification.
3. New EquipmentFlow immediately expects unequip success after asserting the same full bag rejects it, without changing any state in between. Worn equipment occupies no bag cells; it frees no destination by being taken off. Preserve refusal and deliberately free an actual 2x3 bag hole before requiring success, checking exact conservation. Never change production placement to satisfy this false expectation. Bound the remaining unbounded fill loop.

The worker also correctly reports a cursor-to-equipment UI path needing integration: the screen removes a cursor item from the bag and then calls the now ownership-checked definition API. This belongs to immediate U1C integration and must remain an explicit blocker to complete local-loop acceptance. No UI, network or final defense/height/stamina acceptance has been granted.

Next: U1B-R1 repairs only the three concrete blockers above, then manager reruns items/combat sequentially and reviews observable damage/ownership and event state. Other saved improvements and U1A remain intact.
