# DakaDaka — roadmap

Updated 8 November 2026. Live handoff: CURRENT_WORK.md. Rules: ../AGENTS.md.
Status vocabulary: implemented / automated proof / visual proof / accepted.
A passing historical test is evidence for that version, not acceptance of the current tree.

| Order | Deliverable | Current state | Exit criteria |
|---|---|---|---|
| Development infrastructure — complete | Efficient autonomous checks | Change-based selection, verified player cache, stable localhost paths, instructions; Tasks/HARNESS_20261108.md | Planner/cache unit tests and real MISS/STORED/HIT process proof passed; no cached PASS verdicts |
| 1 — active | Stabilize the existing network raid | Observed critical failures closed by full + targeted evidence in CURRENT_WORK; inherited migration remains uncommitted pending milestone acceptance | Current EditMode + full mirror run pass; unresolved failures explained and fixed, no reduced thresholds |
| 2 | Real two-client acceptance | Inventory UI, reconnect, corpse and extraction UI not accepted | Host/join, equip/drop/loot, disconnect/rejoin, death/loot, extract/result/retry exercised through real UI; no duplicates or ownership leak |
| 3 | Hands, block, enemy attacks | Grip/guard/trigger fixes implemented, quality unverified; enemy phase timing still approximate | Correct grips; readable guard and impact; visual windup/contact/recovery aligned with server damage on both clients |
| 4 | Complete one readable raid | Modular crypt/doors exist; HUD/map seams/bridge need review; production timer disabled | Lobby -> raid -> death or extraction -> result -> next raid works; legible timer enabled only after HUD review |
| 5 | Persistent server stash (U4C) | Not implemented | Atomic versioned save, exact UID/affix restore after restart, rejected corrupt data, no item loss/duplication |
| 6 | Content, sound, balance and release (U7/U6) | Deferred | Licensed assets, measured performance, repeatable packaged two-client release acceptance |

## Existing foundation

U0–U2: local rules, attributes, item UIDs/affixes. U3A/B: server movement, admission, combat and skills.
U3C1/3: blade defense/riposte implemented; shields never parry. U5A/B: hand equipment and swords implemented.
U4A/B: network inventory, corpse, extraction and timer implemented with focused runtime evidence; not fully accepted.
Do not rebuild these systems from scratch. Consult linked task reports only for the next concrete slice.

## Scope control

One active implementation slice. Keep at most three next actions in CURRENT_WORK.
Freeze new systems until stages 1 and 2 are accepted. Visual fixes already in the tree receive regression coverage but no implicit visual approval.
Detailed earlier milestone definitions remain in Archive/ROADMAP_before_process_reset_20261108.md.
