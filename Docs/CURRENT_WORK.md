# DakaDaka — current work

Updated11 November2026. Rules: ../AGENTS.md. Roadmap: ROADMAP.md.
User priorities: finish existing features, autonomous work, minimal sufficient checks, milestone-only commits.

## Completed: MAP-03 room identity and ruined architecture

Continue the open moonlit ruins with fractured wall silhouettes, distinct landmarks for10 modules and purpose-grouped props/paving. Preserve existing sockets, clear axes, stairs, interactive lighting, dormant enemies and breakable barrels. Protocol14 for geometry compatibility. Baseline6f8387a1. Report Tasks/MAP_IDENTITY_20261111.md; 40 door approaches and18 terraces validated;43 level tests, NetMovement190/0, NetFinal172/0, NetDoor37/0, NetInventory148/0 passed. Build Builds/Playable/20261111-200854-084. Live host/client traversal on seeds11 and2043687868 accepted; windows closed. Main Editor untouched; root owns mirror.

## Accepted foundation

- MAP-02: mostly open sky,1.8/3.6m terraces and stairs, dormant undead and breakable barrels. Tasks/MOONLIT_RUINS_20261109.md. Solid platforms, not overlapping walkable floors; ground enemy spawns.
- HEAL-01: potion1.8s then45HP/4s, no cancellation; bandage5s then25HP instantly, interruptible. Model/owner-observer animation, starter inventory, focused/network/live proof passed. Commit6f8387a1; build Builds/Playable/20261111-190017-299. Tasks/HEAL_BANDAGE_20261111.md.
- Controls: Q/E skills,1/2 weapons, hold3/4 for two3-slot quick wheels, F interaction, LMB held-item use. Ammo replenishes drawn matching weapon directly. Tasks/QUICK_HANDS_20261111.md and AMMO_DRAWN_20261111.md.
- LIGHT-01: held inventory torch, source-based hands lighting, synchronized extinguish/relight. Tasks/LIGHTING_TORCH_20261111.md.
- Loot/inventory: paired equipment/bag and container/corpse panels; exact server transfers. Whole CC0 human source supplies class bodies and first-person hands; lowered sword hilt and bow accepted.

## Next actions and limits

1. Next map slice can deepen room layouts and encounter/loot placement; current work is the accepted visual identity pass, not final campaign art. Follow the next user direction.
2. Keep separate: sleeve seams, observer bow draw, crossbow/reload, brief magenta impact, persistent stash. No new unrelated systems or broad test reruns.
3. Preserve/review inherited Art/Materials and Human.fbx import drift, Assets/Logs and Pipeline settings separately; never blanket-add.
