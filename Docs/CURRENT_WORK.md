# CURRENT WORK - 4 Nov 2026

Rewrite this file each slice; history is in `Docs/Archive/`.

## State
- U5B (arming sword + two-handed sword: data, catalog, loot, CC0 OpenGameArt models, two-handed first-person grip) is done, uncommitted: see `Docs/Tasks/U5B.md`. Loot only, no kit choice exists. The two-hander does not break a sword guard (impactPower 12, documented deviation from the brief).
- U5A (weapons and shields as equipment: hand slots, drop, pickup, fists) is done, uncommitted: see `Docs/Tasks/U5A.md`. V3d is also uncommitted; V3c bc7a6c28, U3C3 de67cce2 and FIX2 a03c1885 are committed.
- Weapons and shields are ItemInstances in hand slots (per weapon set), moved by the same server-authoritative commands as clothing, droppable (Q in the satchel) as server-spawned ground items. Handedness is data: one-handed main hand only, shield off hand only, two-handed blocks the off hand. Empty main hand = unarmed fists.
- Evidence: gate `Hands`, `HandsRulesTests`, Run-All all + EditMode (see the U5A task file).
- RuntimeSmokeTest flake fixed by data-derived probe enemy selection (task file).

## Next
- Play-test the new swords (two-hander timing, FP pommel size); a better longsword model if Quaternius Modular Weapons becomes downloadable.
- Networked drop/pickup needs a network inventory (U4A).

## Known compromises
- No network proof of a client drop seen by another client. FP arms and HUD not played, only gate-verified. Block pose follows the true guard blade; unused controller states kept.
