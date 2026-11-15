# Room props and night sky â€” 13 November 2026

User request: furnish empty rooms with vases/wooden crates/etc and add moon, stars and clouds. Checkpoint Logs/harness/before-room-props-sky-20261113.json.

Implementation: targeted prefab decoration (DungeonRoomProps), saved original hollow-rim ceramic mesh/material, existing licensed PolyHaven crates/barrels/buckets, bounded groups outside central routes with clearance checks against existing geometry. Physical props have Level colliders. MoonlitSky shader adds cratered disc, antialiased point stars and two drifting cloud layers. Builder integrations preserve decoration on regeneration. No catalogs regenerated or package changes.

Required checks: MovementProofGeometryTests, NetMovement/NetFinal/NetDoor/NetInventory due added colliders; build; actual rendered room and upward sky views. Root sole mirror executor. Main remains out of Play. Planner conservatively selects71 gates, narrowed to above impacted map checks; no combat/economy changes.

Initial migration yielded zero groups because floor renderer entered occupancy reservation despite excluding floor collider. Correcting filter to reserve only renderers above floor height; no acceptance claim for initial empty migration.

Migration refined to fit groups into reserved niches, normalize imported model proportions, and leave floor clearance tolerance. Final236 groups (6–8 per module),708 individual props across30 saved modules. Model vertex counts: crate3716, barrel10031, bucket3991. No extra copies of source meshes/textures. Initial empty migrations retained as diagnostics only.

Run20261113-142410-909 tests/build uses pre-fix sky shader snapshot. Main sky import found reserved HLSL token `point`; renamed local variable to starPoint, main shader diagnostics now empty (shader-final.json). Pending final build must sync this shader-only correction; successful gameplay checks need not repeat for variable rename. Root owns all mirror work.

Run142410-909 finished terminal0: NetMovement198/0, NetFinal172/0, NetDoor37/0, NetInventory148/0; build also returnedPASS but its sky shader snapshot had compiler diagnostics, so it is NOT the visual candidate. Shader correction build143007-800 PASS51s; MovementProofGeometryTests5/5 PASS (movement-tests). Controlled three-floor review3581 terminal0, all floor images inspected; props visible, no runtime exceptions or shader errors in that route.

First moon camera was occluded by nearby wall. Separate sky-view walk into courtyard then lookSW/elevation48 exposed cratered moon and stars. Cloud layers visually too faint; increased coverage and cloud colour only. New build143513-195 pending. No gameplay rerun for shader colour/density correction.

Sky-view emitted native `Invalid memory pointer was detected in ThreadsafeLinearAllocator::Deallocate!` before review actions. This predates current changes: matching logs in allocator-raid-no-review-20261112/client.log, stationary-flame-offline-20261112/fresh-prep/player.log, stash-ui-final/visible-player.log; documented in PERSISTENT_STASH_20261111 and RENDER_READABILITY_20261112. It remains unresolved, not asserted harmless or fixed. Final sky review enables existing leak-validation launch diagnostic; no engine/render setting changes and no repeating unrelated green suites.

Final build20261113-143513-195 PASS81.8s, shell66330 terminal0. Final sky review72521 terminal0 PASS; sky-final/review/snapshots/moon-sky.png and sky-motion.png actually inspected: moon visible above courtyard, crater detail and star field render, cloud banks now perceptible with slow movement. No Invalid-memory-pointer, managed exception or shader-error message in final diagnostic client log. This clean repeat does NOT establish the old intermittent allocator defect fixed. Both owned review players exited.

Visual slice complete in main project:236 prop groups/708 individual props, moon/stars/two cloud layers. Final change after gameplay checks is sky coverage/colour only, so those passes remain applicable to unchanged prop collision/layout. Main Editor remains out of Play; no scene replaced. No commit due inherited room/builder modifications from unfinished Royale work. Ordinary Royale full-run/profile-restart acceptance remains open independently.
