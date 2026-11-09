# Character sidebar — 9 November 2026

User supplied the full reference and requested the missing left panel: character miniature, stats, selected active and passive abilities.

## Delivered

Left read-only character sidebar, own inventory centre, container/dead-player inventory right. Solo inventory keeps both character and bag visible. Shared root scaling fits three panels at1280x720. Existing detailed CHARACTER tab remains separate.

Shows applied class, six effective attributes, health, mana, armour, two active abilities with authored cost/cooldown, two passives with concise effect summaries. Host reads live rig/caster data. Client uses the applied class skill arrays (current kit selection is class-owned), a separate StatsComponent read model over replicated worn instances, and the existing owner skill stream through ICharacterManaView. No UI initialization of gameplay components or authority mutations.

3D preview uses existing CC0 Quaternius mannequin, static sampled folded-arms pose, class tint and isolated render texture camera. Preview stage disables with the sidebar and releases texture/material/stage on destruction. It does not depict equipped armour/weapons; full wearable avatar art is still absent. No new downloaded assets; provenance in ASSET_PLAN.md.

## Evidence and corrections

Logs/acceptance/20261109-character-sidebar, seed11/protocol7, real visible host/client windows with review-bridge mouse/key input.

- build.log PASS: first visual review exposed empty client skill/attribute data, local default mana and unsuitable idle pose.
- build-final.log failed compilation: attempted dependency on Net assembly from Runtime. Corrected to the Core role flag and a runtime read-only interface implemented by NetOwnerSkillView; no assembly dependency changes.
- build-v3.log PASS: final-host/snapshots/solo-final.png and final-client/snapshots/client-final.png show matching Warrior16/10/14/8/10/10 attributes,120/120 health,70/70 mana, Dash/Battle Cry and Ashen Might/Bulwark Training. Client chest transfer followed by Escape and I reopening verified in final-client/snapshots/reopened.png (bag occupancy1 ->2, acquired stack2).
- build-v4.log PASS: only portrait orientation changed after v3. v4-host/snapshots/character-inventory-loot.png verifies front-facing miniature and all three panels alongside actual player-death inventory.
- Final host log inspected for exceptions/errors: none found. Final sessions closed via bridge (client1, host2); main Editor untouched.

Planner checkpoint Logs/harness/before-character-sidebar.json selected70 gates conservatively. Narrowed to compile and actual host/client display/input review: the changes add read-only presentation and an asset, with no item-rule or network-protocol mutations. No repeat of previously green gameplay suites.

Six inherited materials/Pipeline/log paths remain outside the milestone. Full avatar equipment presentation and higher-detail portraits are future art work; this slice does not claim them complete.
