# DakaDaka — current work

Updated 9 November 2026. Rules: ../AGENTS.md. Roadmap: ROADMAP.md.
User: autonomous work, minimal sufficient checks, commit only completed milestones.

## Latest accepted slice: ballistic and recoverable ammunition

Bow and crossbow use shared9.81m/s2 gravity with42/54m/s launch speeds; faster bolts have a flatter trajectory. Paid player shots produce one recoverable arrow/bolt on the floor near terminal impact. Server-owned pickup puts it in the bag; right-click with the matching ranged weapon selected adds one reserve round, then R reloads. Wrong weapon/full reserve preserves the item. Protocol9 requires matching builds.

Evidence: Tasks/RECOVERABLE_AMMO_20261109.md. RecoveredAmmoTests3/3; ProjectileSim109/0, ProjectileBlock51/0, NetCombat312/0, NetSkill127/0, NetInventory148/0 PASS. Two-window seed11 review confirmed client arrow and host bolt pickup/use; repeated pickup did not duplicate the bolt. Playable: Builds/Playable/20261109-143435-327/EmberdelveNet.exe. Review windows closed. Main Editor untouched.

## Next steps and limits

1. User play review of trajectory and ammo loop. Floor recovery is not embedding in bodies/walls; void shots are lost. Recovery stops at224 live pickups to preserve snapshot headroom.
2. Existing impact effect briefly appears magenta on client; repair its material in a focused presentation pass. Ranged weapon blockouts/draw/reload appearance remains unchanged.
3. Class apparel remains fixed, low-poly and human-proportioned. Equipment-driven apparel/cloth/facial animation are separate work. Avoid unrelated test reruns.

## Previous accepted work

- Grim class models b9eb52ed: human rig, distinct fitted warrior/archer outfits; Tasks/GRIM_PLAYERS_20261109.md.

- Remote weapons017c0553: held equipment and accepted server swing/guard presentation; Tasks/REMOTE_WEAPONS_20261109.md.
- Original class models/icons0d2f78ef: warrior Knight and archer Rogue, eight original skill icons, shared inventory/world model. Tasks/CLASS_VISUALS_20261109.md.
- Character sidebar72fb6de9; inventory layout57557aad.
- LOOT-01 inventory/chest/mob/player corpse449c1522: server-owned transfers, paper-doll layouts, paired inventory/loot panes. Tasks/LOOT_UI_20261109.md. Deposits/drag-to-container, persistent stash and rendered item thumbnails remain deferred.
- Crypt modules facf8243: Tasks/CRYPT_CONTENT_20261109.md. Raid baseline7f203c9c: Tasks/RAID_UI_ACCEPTANCE_20261108.md.

Six inherited local-only paths remain outside commits: Stone_Ceiling/Stone_Floor materials; Assets/Logs.meta; pipeline-connection-check screenshot/meta; Pipeline EditorPipelineConfig.json. Default9 players/parser64 is not a measured capacity claim. Localhost review does not certify internet loss/latency or load.
