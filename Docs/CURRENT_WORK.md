# DakaDaka — current work

Updated 11 November 2026. Rules: ../AGENTS.md. Roadmap: ROADMAP.md.
User: autonomous work, minimal sufficient checks, commit only completed milestones.

## Latest completed: lower sword framing

User accepted bow and requested most sword hilt below frame. Sword/rapier hand target lowered0.20m after view clamp, preserving35-degree angle; offset fades out for raised guard. Bow unchanged. WeaponViewModelTests34/0, Hands92/0, Visual207/0 and build PASS; live low ready/guard/return inspected on seed11/protocol10, windows closed. Report Tasks/LOW_SWORD_20261111.md. Build Builds/Playable/20261111-144837-286/EmberdelveNet.exe.

## Previous completed: palm and finger grip

Explicit finger curls wrap the sword/held shield and bow handle; bow draw uses a separate index/middle distal-joint string attachment and thumb clearance. Two-handed support uses its own palm grip. Finger retargeting preserves root and all non-finger animated transforms. Report Tasks/FINGER_GRIP_20261111.md. WeaponViewModelTests34/0, Hands92/0, Visual207/0; live sword windup/block, bow draw/release and prepared multishot reviewed on seed11/protocol10. Windows closed, user Editor untouched. Build Builds/Playable/20261111-142107-771/EmberdelveNet.exe. Next polish: sleeve seams and skin lighting. Finger contacts are authored poses, not physics-based finger collision. Existing materials/import drift excluded.

## Previous completed: weapon transition polish

First-person windup starts from the current visible grip; recovery and wall recoil return smoothly to upright ready/held guard. Active damage samples and timings unchanged. Preview uses the runtime sampler. Report Tasks/WEAPON_TRANSITIONS_20261111.md. WeaponViewModelTests34/0, Hands92/0, Visual207/0; live windup/recovery/combo/guard/stone contact and return reviewed, seed11/protocol10. Windows closed. Build Builds/Playable/20261111-140730-196/EmberdelveNet.exe. Next hand work: palm/finger contact and sleeve seams; no claim of new finger animation in this slice. Additional inherited Bow_Walnut_Textured/Crossbow_Iron_Textured/Grip_Leather material and Human.fbx.meta drift excluded.

## Previous completed: upright sword ready pose

Sword idle grip now points upward,35 degrees from vertical toward screen centre (previously almost horizontal). Viewmodel rig, fallback hands and preview share ReadyRotation; authored guard collision and active swing paths unchanged. Hands92/0, Visual207/0 and build PASS in Logs/runs/20261109-174832-380; WeaponViewModelTests34/0 in Logs/runs/sword-ready-editmode. Live seed11/protocol10: ready pose, real LMB swing, RMB guard and upright return verified in Logs/acceptance/20261109-sword-ready. Windows closed. Build Builds/Playable/20261109-174832-380/EmberdelveNet.exe. Three source files only; broad planner70 gates narrowed to visual/hand checks because combat authority and map did not change.

## Previous completed: MAP-02 moonlit open ruins

Mostly open sky (Vault retains roof), moon/stars and cool directional light; saved room terraces at1.8/3.6m with broad stairs; tombs/arches/columns/props and breakable barrels. Undead lies fallen until aggro, rises before attacking. Height-aware navigation follows stairs and prevents attacks from directly below a terrace. Barrel destruction is server-owned and replicated to late joiners. Protocol10.

Evidence: Tasks/MOONLIT_RUINS_20261109.md. All18 terrace paths pass;63 focused tests plus final height fixture3/0. Network run20261109-172904-542: NetMovement190/0, NetFinal172/0, NetDoor37/0, NetInventory148/0, NetCombat313/0; build PASS. Live seeds11 and2043687868: stairs up/down, moon, sword barrel break/client/latejoin; separate ordinary host approach verifies fallen-to-awake enemy. Review windows closed; main Editor untouched.

Build: Builds/Playable/20261109-172904-542/EmberdelveNet.exe. Limits: solid terraces, existing materials/simple flame visuals, reverse-collapse rise, no barrel loot drops. Inherited materials/logs/Pipeline config excluded from milestone.

## Previous completed: one whole character source for body and hands

Replaced player anatomy with the CC0 Innerscene/MakeHuman full weighted body. Both Warrior and Archer use it; first-person hands derive from this exact same skin and finger skeleton. Existing class armour retained and fitted, headwear lowered, closed boots added. Inventory portraits use the same class prefabs. No separate FPS-arm asset imported.

Evidence: Tasks/UNIFIED_HUMAN_20261109.md. WeaponViewModelTests34/0; Hands92/0; Visual207/0; build PASS. Live host/client input and observer review: bow draw/shot, sword block/attack, weapon swap return, remote movement/attack and inventory portrait, seed11/protocol9. Build: Builds/Playable/20261109-170031-385/EmberdelveNet.exe. Both windows closed; main Editor untouched.

## Next steps and limits

1. Visual polish on the new shared anatomy: clean sleeve cuffs, skin response near torches, authored finger hook for bow/string; current armour and procedural materials remain simple. This is a completed source/rig replacement, not final photoreal art. Source/license in ASSET_PLAN.md. Unity emits an in-between metacarpal warning but avatar/fingers are valid and reviewed.
2. Previous grip fix915a38a5 and bow/input c8e04229 remain: F/G prepares, LMB fires; ordinary arrows hold/release. Third-person bow draw-state replication and crossbow model/reload presentation remain separate.
3. Existing brief magenta client impact effect needs a focused material fix. No unrelated full test reruns.

## Previous accepted work

- Ballistics/recovery35cc8833: shared gravity, typed recoverable ammunition; Tasks/RECOVERABLE_AMMO_20261109.md.

- Grim class models b9eb52ed: human rig, distinct fitted warrior/archer outfits; Tasks/GRIM_PLAYERS_20261109.md.

- Remote weapons017c0553: held equipment and accepted server swing/guard presentation; Tasks/REMOTE_WEAPONS_20261109.md.
- Original class models/icons0d2f78ef: warrior Knight and archer Rogue, eight original skill icons, shared inventory/world model. Tasks/CLASS_VISUALS_20261109.md.
- Character sidebar72fb6de9; inventory layout57557aad.
- LOOT-01 inventory/chest/mob/player corpse449c1522: server-owned transfers, paper-doll layouts, paired inventory/loot panes. Tasks/LOOT_UI_20261109.md. Deposits/drag-to-container, persistent stash and rendered item thumbnails remain deferred.
- Crypt modules facf8243: Tasks/CRYPT_CONTENT_20261109.md. Raid baseline7f203c9c: Tasks/RAID_UI_ACCEPTANCE_20261108.md.

Six inherited local-only paths remain outside commits: Stone_Ceiling/Stone_Floor materials; Assets/Logs.meta; pipeline-connection-check screenshot/meta; Pipeline EditorPipelineConfig.json. Default9 players/parser64 is not a measured capacity claim. Localhost review does not certify internet loss/latency or load.
