# DakaDaka — roadmap

Updated 9 November 2026. Live handoff: CURRENT_WORK.md. Rules: ../AGENTS.md.
Status vocabulary: implemented / automated proof / visual proof / accepted.
A passing historical test is evidence for that version, not acceptance of the current tree.

| Order | Deliverable | Current state | Exit criteria |
|---|---|---|---|
| Development infrastructure — complete | Efficient autonomous checks | Change-based selection, verified player cache, stable localhost paths, instructions; Tasks/HARNESS_20261108.md | Planner/cache unit tests and real MISS/STORED/HIT process proof passed; no cached PASS verdicts |
| 1 — accepted | Stabilize the existing network raid | Final integrated68/70 gates and924/925 tests plus targeted NetMovement190/0, NetFinal172/0 and shield1/1 close all observed failures; coherent stages1–4 milestone | Full mirror coverage plus focused corrections; no unresolved observed failures or reduced thresholds |
| 2 — accepted | Real two-client acceptance | Host/join, equipment/drop/pickup, rejoin, combat/deconnect corpse transfer, extraction/result/retry and host restart verified in real UI; simultaneous same-UID contention also verified; included in the accepted integrated raid milestone | Host/join, equip/drop/loot, disconnect/rejoin, death/loot, extract/result/retry exercised through real UI; no duplicates or ownership leak |
| 3 — accepted | Hands, block, enemy attacks | Host/client grip and guard accepted; client attack phases agree with actual12 damage. Baked forward travel removed. Contact-based wall recoil accepted v21, wire/admission21/0; current full NetCombat also passed | Correct grips; readable guard and impact; visual windup/contact/recovery aligned with server damage on both clients |
| 4 — accepted | Complete one readable raid | Normal600s timer enabled and readable on host/client; short timeout/result verified. Bridge and both doorway seams traversed on original regression seed and seed11 (v22/v23) | Lobby -> raid -> death or extraction -> result -> next raid works; legible timer enabled only after HUD review |
| 5 | Persistent server stash (U4C) | Not implemented | Atomic versioned save, exact UID/affix restore after restart, rejected corrupt data, no item loss/duplication |
| 6 | Content, sound, balance and release (U7/U6) | Deferred | Licensed assets, measured performance, repeatable packaged two-client release acceptance |

## Existing foundation

U0–U2: local rules, attributes, item UIDs/affixes. U3A/B: server movement, admission, combat and skills.
U3C1/3: blade defense/riposte implemented; shields never parry. U5A/B: hand equipment and swords implemented.
U4A/B: network inventory, corpse, extraction and timer have real two-client UI acceptance. Integrated milestone accepted using full and focused evidence; persistent stash is separate future scope.
Do not rebuild these systems from scratch. Consult linked task reports only for the next concrete slice.

## Scope control

One active implementation slice. Keep at most three next actions in CURRENT_WORK.
Stages1–4 are accepted as one coherent raid milestone. New systems require a separate milestone; load/loss coverage and new art remain explicit future work.
Detailed earlier milestone definitions remain in Archive/ROADMAP_before_process_reset_20261108.md.
