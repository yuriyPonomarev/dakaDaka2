# DakaDaka — current work

Updated 9 November 2026. Rules: ../AGENTS.md. Roadmap: ROADMAP.md.
User: autonomous work, minimal sufficient checks, commit only completed milestones.

## Latest completed slice: bow/sword hand grip correction

User rejected the previous bow left-hand and sword wrist pose. Calibrated weapon grips from actual finger bones, removed the sword rotation from upright bow grips, and rolled the sword palm so the forearm enters from the side. Shield attachment remains separate; weapon swaps recalibrate the grip.

Evidence: Tasks/GRIP_FIX_20261109.md. Hands92/0 and Visual207/0 passed before final cosmetic palm-roll/center tuning; final build and actual two-window input review cover that tuning. Bow draw/shot, sword guard/attack and Q swap return checked, seed11/protocol9. Build: Builds/Playable/20261109-164037-226/EmberdelveNet.exe. Windows closed; main Editor untouched.

## Next steps and limits

1. Trial a more anatomical CC0 arm mesh, author fingers for bow/string and sword, then align full-body anatomy and class outfits. Candidates: Design/CHARACTER_ASSET_CANDIDATES_20261109.md. Research only; no replacement imported. Existing low-poly fingers, bow-rest hand overlap and torch overexposure remain quality limits.
2. Previous bow/input slice c8e04229 remains: F/G prepares, LMB fires; ordinary arrows hold/release. Tasks/BOW_DRAW_20261109.md. Third-person draw-state replication and crossbow model/reload presentation remain separate.
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
