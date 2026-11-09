# DakaDaka — current work

Updated 9 November 2026. Rules: ../AGENTS.md. Roadmap: ROADMAP.md.
User: autonomous work, minimal sufficient checks, commit only completed milestones.

## Latest accepted slice: human-proportioned grim class models

Warrior/Archer now share the existing CC0 human rig with distinct original fitted outfits. Warrior: closed helmet, steel armour, faded red tabard. Archer: hood/mask, leather, quiver. Chibi proportions removed; muted materials and readable inventory lighting. Models remain low-poly, not photoreal. Same class prefab drives inventory and network world presentation.

PlayerAvatar supports Humanoid animations; weapon presentation maps the human hand/arm bones. No combat rules, colliders, root motion or protocol changes (still8). Existing class-art build entry delegates to the new builder.

Evidence: Tasks/GRIM_PLAYERS_20261109.md. Build-v3 PASS and two-window review (seed11/protocol8): both inventories, remote bodies, movement, sword chain/overhead/shield guard, client rapier thrust. Sessions closed client12/host14. Hands92/0 and Visual207/0 PASS (run20261109-141624-941). Scoped source/assets match mirror; no test players remain. Playable: Builds/Playable/20261109-grim-models/EmberdelveNet.exe. Main Editor untouched.

## Next steps and limits

1. User review of the new silhouettes/materials. Refine concrete visual findings without reopening unrelated systems.
2. Outfits are fixed by class. Equipment-driven apparel, cloth simulation and facial animation remain separate work.
3. Ranged weapon blockouts/draw/reload remain unchanged. No broad test reruns or infrastructure work without a related change.

## Previous accepted work

- Remote weapons017c0553: held equipment and accepted server swing/guard presentation; Tasks/REMOTE_WEAPONS_20261109.md.
- Original class models/icons0d2f78ef: warrior Knight and archer Rogue, eight original skill icons, shared inventory/world model. Tasks/CLASS_VISUALS_20261109.md.
- Character sidebar72fb6de9; inventory layout57557aad.
- LOOT-01 inventory/chest/mob/player corpse449c1522: server-owned transfers, paper-doll layouts, paired inventory/loot panes. Tasks/LOOT_UI_20261109.md. Deposits/drag-to-container, persistent stash and rendered item thumbnails remain deferred.
- Crypt modules facf8243: Tasks/CRYPT_CONTENT_20261109.md. Raid baseline7f203c9c: Tasks/RAID_UI_ACCEPTANCE_20261108.md.

Six inherited local-only paths remain outside commits: Stone_Ceiling/Stone_Floor materials; Assets/Logs.meta; pipeline-connection-check screenshot/meta; Pipeline EditorPipelineConfig.json. Default9 players/parser64 is not a measured capacity claim. Localhost review does not certify internet loss/latency or load.
