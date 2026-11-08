# Critical debt register вЂ” 8 November 2026

Only coordinator updates closure. Priority is risk, not amount of code written. No new feature work until P0 is closed.

| ID | Priority | Problem / status | Closure evidence |
|---|---|---|---|
| NET-01 | P0 | Old map assumptions in movement/final fixtures; 5s telemetry raced by 3.5s wait. Automated closure: full 190643 NetMovement 189/0, NetFinal 172/0. | Current NetMovement + NetFinal pass; unchanged movement thresholds, original spawns still tested |
| BASE-01 | P0 | 190643 had 68/69 passing; sole smoke fixture failure fixed (193032 249/0). New drop inclusion passed separately 17/0; Original seed passed 193253 249/0. Combined evidence closes observed failures; unified 70-gate release run deferred, redundant 193510 cancelled. | Current all + EditMode pass; exact run path, no concurrent source changes |
| ITEMS-01 | P1 | Closed: silent combined transfer restores bag on refusal before changing equipment. 20261108-202148-890: 902 EditMode, NetInventory 146/0, Hands 92/0 | Inspect LocalInventoryAuthority transfer path; failure after displacement must restore all UIDs/slots/revision; regression test, NetInventory/Hands |
| UI-01 | P1 | Inventory/reconnect and full raid not accepted through actual client UI | Two clients: equip/drop/contention, disconnect/rejoin, corpse, extraction and result; no item duplication |
| ANIM-01 | P1 | Grip/guard/trigger changes not visually accepted; attack duration still guessed | Hands/Visual/NetCombat pass plus in-motion owner/peer review; phase timing agrees with server damage |
| QA-01 | P1 | Drop-publication rollback tested locally but not in a live network failure scenario | Authoritative injected failure preserves owner UID exactly once; client snapshots converge |
| HUD-01 | P2 | Timer disabled; map bridge/seams and HUD not visually reviewed | Legible HUD and intended central bridge confirmed before enabling production timer |
| SAVE-01 | Deferred | Persistent stash absent | Roadmap stage 5; not a current regression |

Historical task reports may contain superseded blockers or old pass counts. Use CURRENT_WORK for latest evidence and this register for unresolved risk.
