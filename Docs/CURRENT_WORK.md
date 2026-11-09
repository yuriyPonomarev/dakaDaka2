# DakaDaka — current work

Updated 9 November 2026. Rules: ../AGENTS.md. Roadmap: ROADMAP.md.
User: autonomous work, minimal sufficient checks, commit only completed milestones.

## Latest accepted slice: LOOT-01 inventory reference layout

Follow-up requested by user: taller paired weapon slots, central large torso/legs, smaller jewelry, empty-slot etchings, metallic corners/dividers and less text over items. Own and fallen-player panels share geometry.

Evidence: Tasks/INVENTORY_ART_20261109.md; final build PASS, real host/client chest transfer, corpse worn-slot transfer, own unequip and Escape closure. Cosmetic-only: no repeated network suites.

## Accepted milestone: LOOT-01 paired inventory and loot

Task/evidence: Tasks/LOOT_UI_20261109.md. User authorized own inventory left, container right, dead player loadout in the same layout as own.

- Paper-doll equipment above the bag, original cached silhouette icons, rarity borders, readable tooltips/value/free space. Existing selection, equip/use/drop/hotbar commands retained.
- Chest opening rolls once and opens server-owned contents rather than collecting immediately. Enemy death drop route publishes one loot container; registry refusal preserves pickups as a fallback. Player corpses preserve bag coordinates and worn slots.
- One existing server transfer path keeps revision/UID/range/LOS/capacity checks. Sealed-chest admission is also rechecked for state/transfers. Protocol7 / container wire schema2.
- Click takes a stack; Shift+click takes one. Own inventory remains usable. I/Escape closes both panes; empty contents stay visible. Tooltip above both panes; client HUD compact while open.

## Verification

- Focused EditMode21/21: corpse model, corpse wire, hands rules. New checks cover original layout retention and exact single transfer/stack splitting. Initial failed fixture setup and its correction are recorded honestly in the task report.
- Run20261109-114747-273: NetInventory148/0, NetExtraction136/0. Contest now verifies two simultaneous chest inspections followed by one winner for the exact UID. Proof build reused between gates.
- Final build-v8 PASS; seed11/protocol7. Two real graphical windows: remote chest click, host mob-loot transfers, empty state, tooltip affixes, real player-death capture and worn-slot transfer, I/Escape closure and resumed movement. Screenshots/logs: Logs/acceptance/20261109-loot-ui.
- Main user Editor untouched. Final review sessions closed at host16/client7; process cleanup verified before commit. Scoped source/assets match mirror. Six inherited local-only paths remain outside the milestone: two Stone materials, Assets/Logs.meta, connection-check screenshot/meta, Pipeline EditorPipelineConfig.json.

## Previous accepted work and limits

MAP-01 facf8243: ten crypt modules and two-layout traversal; Tasks/CRYPT_CONTENT_20261109.md.
Raid baseline7f203c9c: network inventory/corpse/extraction/result/retry, hands/guard/enemy phases and timer; Tasks/RAID_UI_ACCEPTANCE_20261108.md.

This milestone targets the network/host playable raid; legacy offline rule-fixture collection fallback remains. No persistent stash, deposits/drag-to-container or rendered3D item thumbnails. Mob graphical review invokes production DropEnemyLoot; it is not a new combat-encounter acceptance claim. Default9 players/parser64 are not measured capacity.

## Next steps

1. User playtest inventory and looting in the packaged raid; address concrete findings as one slice.
2. Continue encounter/loot placement in the accepted crypt modules if requested.
3. Keep stash, larger load/loss coverage and broader art production separate; do not repeat green suites without a related change.
