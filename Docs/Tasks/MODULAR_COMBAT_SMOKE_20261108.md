# Modular combat smoke РІР‚вЂќ 8 November 2026

> Historical implementation note. Current acceptance and remaining work: [CURRENT_WORK](../CURRENT_WORK.md) and [raid acceptance](RAID_UI_ACCEPTANCE_20261108.md). Status statements below describe the original slice.


The full run `Logs/runs/20261108-042911` failed RuntimeSmokeTest.RunCombatFlow 236/15. The harness loaded the legacy Ashen Halls layout and nav grid while playing the modular scene, selected logical cells with no physical LOS, and assumed a blocked nav neighbour was a physical wall.

The combat smoke uses the installed modular runtime layout and nav grid, and finds physically clear lanes before staging the opening, defense, passive swing and open Dash. Target candidates require clear volume and production LOS from both aim and muzzle. The wall Dash candidate requires an actual level collider within the unchanged blocked-distance threshold, with a walkable landing margin. The wall collision, walkability, damage, authority, contact location and damage-value checks remain intact.

A run at `Logs/runs/20261108-143245` showed gravity settling changed the rig's position after the independent passive-hit prediction. The harness now temporarily freezes player movement for that one real swing, then restores the original state; it still predicts hit location and zone before BeginSwing and asserts the actual authority damage and hit location. `Logs/runs/20261108-143819` showed the wall fixture could choose a collider beyond a logically blocked boundary; its candidate search was corrected rather than removing the walkability assertion.

The latest targeted `Logs/runs/20261108-144342/summary.txt` passed **248/0**. This is one generated layout, not exhaustive validation over all seeds. Full suite still requires a fresh run after integration; earlier full run is not green. No production combat logic changed by this slice.

Full 190643: 248/1 on map seed -1609012308 (raid seed 9409529268801451688, hash CAEA4955). Loot fixture placed player on Vault_Plinth: controller rose to y=1.03, pickup y=0.41, chest probe hit nothing. PlacePlayerForLoot now searches supported, clear capsules at floor height and checks probe reach plus production LOS before placement. Original CurrentTarget and loot collection assertions preserved. Focused 193032 passed 249/0 on a different map; exact-seed rerun pending.

193253: smoke 249/0 passed with original raid seed 9409529268801451688 / map hash CAEA4955. The selected pickup differs in runtime object order, so this repeats the map, not an identical actor trace. Redundant full rerun 193510 was cancelled, not a regression failure. Keep raw evidence and cancellation marker.
