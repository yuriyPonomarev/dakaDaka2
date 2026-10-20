# U1A — accepted within fixture/exit scope

Independent review: Codex, 19 October 2026. U1A is accepted. This does **not** accept the complete inventory/equipment milestone or its visual usability.

Only production-tree change versus preserved U0 checkpoint: `Assets/Emberdelve/Editor/EmberdelveRuntimeSmokeTest.cs`. Runtime inventory, equipment, data, scenes and packages remain unchanged. Reviewed the actual diff, Inventory APIs and the worker report; did not accept from the report alone.

The empty 10x4 grid correctly holds five upright 2x3 cuirasses: 30 occupied cells and 10 fragmented free cells, with no sixth rectangular placement. The bounded insertion and exact used/free accounting preserve this distinction. Coin overflow requests 81 units against 10 remaining cells at stack limit8, requires exactly80 accepted and1 left, and verifies existing cuirass origins/counts/occupancy survive. No equipment assertions were skipped. The stored result crosses the play-mode transition: normal completion, watchdog and premature play-mode termination cannot silently report an ordinary passing result after a failure.

## Independent engine evidence

Unity6000.2.5f1, original project, sequential manager-owned runs with fresh raw logs and a 240-second outer timeout:

| Run | Actual result | Interpretation |
| --- | --- | --- |
| U1A items | **212 checks / 5 failures, process exit1**, no captured compile/runtime exceptions or timeout | All five former grid/stack failures are gone. Five existing defense-measurement failures remain visible; entire suite is still failing. |
| U1A combat | **201 checks / 0 failures, process exit0**, exactly one PASS summary, no captured errors/timeout | Existing combat regression preserved. |
| U1A negative control | Restore old area-only packing expectation: **211 checks / 10 failures, process exit1** | Five additional geometry/accounting assertions reject the wrong six-item expectation. Mutation caught. |

Evidence: manager `review-U1A-items.json`, `review-U1A-combat.json`, `review-U1A-MUTATION-items.json`, `review-U1A-mutation-proof.json`; their `evidence` fields identify full editor/stdout/stderr logs and exact commands under `validation/`. Mutation restored exact source bytes in `finally`; restored SHA256 `176750bfb7c62cb2405aca5cba2e86a06b7a712b13b46d1a49107780b070a8c2`. Preserved bytes: `checkpoints/U1A-before-mutation.cs.bytes` in the manager directory. The observed FAIL/exit1 and PASS/exit0 independently confirm actual process exit propagation on this editor version. Watchdog branches were source-reviewed, not individually forced in an engine run.

## Remaining findings and next task

The five remaining failures alone do not establish a runtime block defect. Source review found that GuardWithGearFlow compares facing against reversed attack travel direction, never subscribes its damage capture, and compares two hits with the same cuirass still worn. Actual recorded HP loss13.8 is not sufficient to distinguish these two identical conditions. U1B must vary real equipment, capture fresh matching damage and compare actual authority/HP results, retaining front/rear/side protection checks.

EquipmentFlow also compares WeaponData objects against BOOL `GrantsWeapon` through Unity object truthiness instead of exact `grantedWeapon` references. Production Equipment accepts absent bag definitions, consumes the first matching item instead of a selected duplicate, pre-unequips before freeing incoming space, and credits owned transfers through loot-collection APIs. These are explicit U1B ownership/atomicity/counter requirements; correcting only the old five failures will not suffice.

U1C real windowed input, HUD/container images and complete usable local loop remain open; multiplayer and U2–U6 are not certified by these headless checks.
