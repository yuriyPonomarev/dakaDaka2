# DakaDaka — current work

Updated 9 November 2026. Rules: ../AGENTS.md. Roadmap: ROADMAP.md.
User: autonomous work, minimal sufficient checks, commit only completed milestones.

## Latest accepted slice: bow, first-person hands and prepared archer shots

Curved recurve limbs, leather grip, anchored V-shaped string and nocked arrow replace the bow blockout. Existing human arms now hold the bow with the left hand and pull the string with the right; limbs flex and the string returns on release.

F/G selects Multi-shot/Piercing Shot, then left mouse press fires. Selection spends nothing; server applies costs/cooldown on fire. Repeat the selected key to cancel, choose the other to replace; inventory, death and weapon swap cancel. Ordinary arrows keep hold/release. Shared input suppression prevents an extra ordinary arrow during a skill click/hold. Client first click uses replicated weapon type instead of probing the server with WrongWeapon.

Evidence: Tasks/BOW_DRAW_20261109.md. Hands92/0, Visual207/0, NetSkill127/0, NetCombat312/0 passed before the documented input/presentation refinements; final refinements compiled and were verified through actual input in two windows. Zero requests before skill click, exact3/1 projectile counts, held-click suppression, first ordinary shot and inventory/swap cancellation confirmed. Seed11/protocol9. Final build: Builds/Playable/20261109-162234-464/EmberdelveNet.exe. Windows closed; main Editor untouched.

## Next steps and limits

1. User review of bow/hand silhouette and controls. This slice animates first-person draw and shares the new bow mesh; third-person draw-state replication is separate work.
2. Existing brief magenta client impact effect needs a focused material fix. Crossbow model/reload appearance unchanged.
3. Recovered ammunition remains server-owned floor loot: E to bag, right-click with matching weapon to replenish reserve, R reload. Class apparel remains fixed and low-poly; no unrelated full test reruns.

## Previous accepted work

- Ballistics/recovery35cc8833: shared gravity, typed recoverable ammunition; Tasks/RECOVERABLE_AMMO_20261109.md.

- Grim class models b9eb52ed: human rig, distinct fitted warrior/archer outfits; Tasks/GRIM_PLAYERS_20261109.md.

- Remote weapons017c0553: held equipment and accepted server swing/guard presentation; Tasks/REMOTE_WEAPONS_20261109.md.
- Original class models/icons0d2f78ef: warrior Knight and archer Rogue, eight original skill icons, shared inventory/world model. Tasks/CLASS_VISUALS_20261109.md.
- Character sidebar72fb6de9; inventory layout57557aad.
- LOOT-01 inventory/chest/mob/player corpse449c1522: server-owned transfers, paper-doll layouts, paired inventory/loot panes. Tasks/LOOT_UI_20261109.md. Deposits/drag-to-container, persistent stash and rendered item thumbnails remain deferred.
- Crypt modules facf8243: Tasks/CRYPT_CONTENT_20261109.md. Raid baseline7f203c9c: Tasks/RAID_UI_ACCEPTANCE_20261108.md.

Six inherited local-only paths remain outside commits: Stone_Ceiling/Stone_Floor materials; Assets/Logs.meta; pipeline-connection-check screenshot/meta; Pipeline EditorPipelineConfig.json. Default9 players/parser64 is not a measured capacity claim. Localhost review does not certify internet loss/latency or load.
