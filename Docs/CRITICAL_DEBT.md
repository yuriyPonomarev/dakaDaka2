# Critical debt register — 9 November 2026

Only coordinator updates closure. Priority is risk, not amount of code written. No new feature work until P0 is closed.

| ID | Priority | Problem / status | Closure evidence |
|---|---|---|---|
| NET-01 | Closed | Movement fixtures use measured lanes and complete telemetry segments; original failing seed-1799084838 repeated | Final targeted20261109-062114-364 NetMovement190/0 and NetFinal172/0; unchanged limits |
| BASE-01 | Closed | Final full20261109-055711-479:68/70 gates,924/925 tests. Shield pivot assertion, movement segment selection and denied build promotion resolved | Targeted shield1/1 + NetMovement190/0 + NetFinal172/0; historical full remains FAIL, combined evidence accepted |
| ITEMS-01 | P1 | Closed: silent combined transfer restores bag on refusal before changing equipment. 20261108-202148-890: 902 EditMode, NetInventory 146/0, Hands 92/0 | Inspect LocalInventoryAuthority transfer path; failure after displacement must restore all UIDs/slots/revision; regression test, NetInventory/Hands |
| UI-01 | Closed | UI scenarios accepted, including exact-UID contention, combat/disconnect corpse transfer, extraction/death/retry and host restart; shared dependencies accepted as integrated raid milestone | Two clients: equip/drop/contention, disconnect/rejoin, corpse, extraction and result; no item duplication |
| ANIM-01 | Closed | Grip/guard accepted host/client; authoritative phases align with actual client hit12. Baked pose travel fixed. Contact-based wall recoil21/0, live v21 contact/return accepted; current final NetCombat312/0, Hands92/0 and Visual207/0 passed; v24 actual zero-damage block and swap-return accepted | Hands/Visual/NetCombat pass plus in-motion owner/peer review; phase timing agrees with server damage |
| QA-01 | P1 | Closed: live publication failure preserves equipped UID/affixes/revision, no owner/peer floor duplicate; retry and late join pass. Run20261109-054809-916 equipment-drop21/0 | Authoritative injected failure preserves owner UID exactly once; client snapshots converge |
| HUD-01 | Closed | Timer600s enabled, readable host/client and short expiry accepted; seam/deck traversal reviewed v21; found bridge end facing a sealed wall. Planner15/0 fix; original seed2043687868 and seed11 traversed both doorway seams and full bridge in v22/v23 | Legible HUD and intended central bridge confirmed before enabling production timer |
| SAVE-01 | Deferred | Persistent stash absent | Roadmap stage 5; not a current regression |

Historical task reports may contain superseded blockers or old pass counts. Use CURRENT_WORK for latest evidence and this register for unresolved risk.
