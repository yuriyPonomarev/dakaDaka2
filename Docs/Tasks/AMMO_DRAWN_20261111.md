# AMMO-01: replenish ammunition without stowing the weapon

Completed 11 November 2026. This supersedes QUICK-01's held-only requirement for pure ammo supplies (ammoGrant > 0, no healing). Q/E,1/2,3/4 and R mappings remain unchanged.

Wheel release and inventory right-click now use RequestQuickStack: ammo submits the existing authoritative Use command; potions/torch still request holding. The authority permits unheld ammo only with no held utility and the matching enabled ranged weapon drawn. Existing quantity, capacity, identity, revision and owner checks remain. Wrong ammunition, full reserve and trying from a held potion do not consume items. Mixed heal/ammo definitions do not bypass held-use requirements. Legacy explicit held-ammo commands remain supported; normal UI no longer takes ammo into hands.

Evidence:
- Logs/runs/ammo-drawn-focused: RecoveredAmmoTests3/3, includes arrows and bolts, wrong/full refusal, stale command refusal, potion unheld refusal and no held-stack change on replenishment.
- Logs/runs/20261111-163521-097: InventoryAuthority116/0 (includes a real archer loadout retaining active weapon and consuming exactly one bundle), NetInventory148/0, playable build PASS.
- Final build: Builds/Playable/20261111-163521-097/EmberdelveNet.exe.
- Logs/acceptance/20261111-ammo-drawn: seed11/protocol12, host warrior/client archer. Client selected Arrow Bundle in wheel3; on release bow stayed drawn, and reopening inventory showed the bundle removed with potion/torch unchanged. Screenshots ammo-wheel.png, ammo-used-bow-held.png, inventory-after-ammo.png. Both windows closed cleanly.

Planner chose70 because inventory is shared; reviewed scope was the ammo-use exception and two UI routes, so only the focused fixture and affected authority/network gates ran. No schema/wire or combat changes. Inherited material/import/log/Pipeline changes excluded. Checkpoint Logs/harness/before-ammo-hands.json.
