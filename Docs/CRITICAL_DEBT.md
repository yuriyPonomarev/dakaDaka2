# Critical debt register — 8 November 2026

Only coordinator updates closure. Priority is risk, not amount of code written. No new feature work until P0 is closed.

| ID | Priority | Problem / status | Closure evidence |
|---|---|---|---|
| NET-01 | P0 | Old map assumptions in movement/final fixtures; 5s telemetry raced by 3.5s wait. Fixes implemented, rerun pending. | Current NetMovement + NetFinal pass; unchanged movement thresholds, original spawns still tested |
| BASE-01 | P0 | No passing full regression for accumulated current changes | Current all + EditMode pass; exact run path, no concurrent source changes |
| ITEMS-01 | P1 | Two-handed equip may commit displaced offhand before main equip; atomicity not covered by drop fix | Inspect LocalInventoryAuthority transfer path; failure after displacement must restore all UIDs/slots/revision; regression test, NetInventory/Hands |
| UI-01 | P1 | Inventory/reconnect and full raid not accepted through actual client UI | Two clients: equip/drop/contention, disconnect/rejoin, corpse, extraction and result; no item duplication |
| ANIM-01 | P1 | Grip/guard/trigger changes not visually accepted; attack duration still guessed | Hands/Visual/NetCombat pass plus in-motion owner/peer review; phase timing agrees with server damage |
| QA-01 | P1 | Drop-publication rollback tested locally but not in a live network failure scenario | Authoritative injected failure preserves owner UID exactly once; client snapshots converge |
| HUD-01 | P2 | Timer disabled; map bridge/seams and HUD not visually reviewed | Legible HUD and intended central bridge confirmed before enabling production timer |
| SAVE-01 | Deferred | Persistent stash absent | Roadmap stage 5; not a current regression |

Historical task reports may contain superseded blockers or old pass counts. Use CURRENT_WORK for latest evidence and this register for unresolved risk.
