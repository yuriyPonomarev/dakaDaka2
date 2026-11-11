# DakaDaka — current work

Updated 11 November 2026. Rules: ../AGENTS.md. Roadmap: ROADMAP.md.
User: autonomous work, minimal sufficient checks, commit only completed milestones.

## Completed: POTION-02 uninterrupted drinking

User correction: once started, drinking cannot be cancelled by movement, damage, switching hands or inventory operations. Death/leaving play/component teardown still abort safely. Completion consumes once even if regeneration fills health meanwhile. Existing animation/protocol13 unchanged. LoadoutD3c211/0, corrected D3Content53/0 (real game-clock input), InventoryAuthority116/0 and NetInventory148/0 passed. Build PASS: Builds/Playable/20261111-185112-164/EmberdelveNet.exe. Report Tasks/POTION_LOCK_20261111.md. This supersedes POTION-01 cancellation behavior below.

## Completed: POTION-01 model, glow and drinking

Shared glass/cork/leather/brass vial for hands and ground pickups; dim emissive liquid without a Light. Owner and observer uncork/raise/sip/lower poses follow the authoritative1.8s drinking channel. Successful completion consumes/heals; cancellation retains the item. Protocol13.

Build: Builds/Playable/20261111-170116-778/EmberdelveNet.exe. Wire6/6; Hands92/0, Visual207/0, InventoryAuthority116/0, LoadoutD3c204/0, NetCombat312/0, NetInventory148/0; corrected graphical QuickAccessUi140/0. Live seed11 host/client: dark-scene vial, drinking from both views, movement cancellation without loss and successful completion with weapon restoration. Windows closed. Report Tasks/POTION_20261111.md includes the initial UI fixture failure and material polish.

## Completed: AMMO-01 replenishment with weapon drawn

User correction: ammo is exempt from held-item use. Choosing arrows/bolts in a quick wheel or right-clicking them replenishes the matching drawn ranged weapon directly. No weapon stow/swap or ammo model is involved. Potions still require holding; full/wrong-ammo requests consume nothing. Protocol12/schema5 unchanged.

Build: Builds/Playable/20261111-163521-097/EmberdelveNet.exe. RecoveredAmmoTests3/3, InventoryAuthority116/0, NetInventory148/0; build PASS. Live seed11 host/client: arrow wheel selection consumed the bundle, bow remained drawn, replicated bag updated. Windows closed. Report Tasks/AMMO_DRAWN_20261111.md.

## Previous: QUICK-01 controls and held utility items

Q/E abilities;1/2 direct weapon sets; hold3/4 for two three-entry wheels, release to hold. F interaction. Any held utility stows both weapons; LMB uses only the held stack. Inventory hover/selection +3/4 assigns slots; right-click holds without consuming. Server enforces exact held identity and rejects weapon attacks. Changing hands cancels drinking. Owner/observer visuals and six-slot HUD are implemented; ammo pools survive utility use. Protocol12, saved belt schema5 migrates old4 entries into6.

Report: Tasks/QUICK_HANDS_20261111.md. Final build: Builds/Playable/20261111-162703-147/EmberdelveNet.exe. Focused EditMode20/20; affected schema/authority/visual/input/network proofs passed across recorded runs. Final SkillCooldown253/0; graphical quick-access UI139/0. Initial failures and their fixes are recorded honestly in the report, not presented as a green aggregate. Seed11 live host/client wheels, held potion/torch, remote light, Q/E,1/2,F verified; final HUD/wheel polish rechecked on final build. All review windows closed; main Editor untouched.

## Previous accepted baseline

LIGHT-01: inventory torch, source-based hand lighting and synchronized extinguish/relight of world lights; prior controls are superseded above. Simple flame/local scattering, no fuel or room-scale volumetrics. See Tasks/LIGHTING_TORCH_20261111.md.

Sword hilt lowered, upright35-degree ready retained, bow accepted. Whole CC0 Innerscene/MakeHuman source supplies class bodies and first-person hands. Open moonlit ruins have terraces/stairs, dormant-to-aggro undead and server-owned breakable barrels. See Tasks/LOW_SWORD_20261111.md, UNIFIED_HUMAN_20261109.md and MOONLIT_RUINS_20261109.md.

## Next actions and limits

1. Follow the next user-selected slice; no background full-suite reruns. Potion model and drinking are complete; ammo remains a direct replenishment action.
2. Separate existing work: sleeve seams, third-person bow draw state, crossbow model/reload, brief magenta impact material. Old full inventory UI tests still assume pre-paper-doll text rows; quick-access UI has its own focused proof and requires real graphics for the portrait.
3. Preserve/review inherited material/import/log/Pipeline drift separately. Never blanket-add it to commits.
