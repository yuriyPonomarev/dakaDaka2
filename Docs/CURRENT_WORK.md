# DakaDaka — current work

Updated 9 November 2026. Rules: ../AGENTS.md. Roadmap: ROADMAP.md.
User: autonomous work, minimal sufficient checks, commit only completed milestones.

## Latest accepted slice: remote weapons and readable melee

Other players now show their actual main/off-hand weapons and effective guard. Sword LR/RL/overhead and rapier thrust follow the same accepted pattern, timing and aim frame as server hit resolution. Cosmetic shoulder/elbow posing restores the base animation each frame. Swapping to a two-handed weapon hides the off-hand. Existing block/parry rules are unchanged; shields never parry. Protocol8: matching builds required.

Evidence: Tasks/REMOTE_WEAPONS_20261109.md. New wire tests4/4; focused run20261109-134646-984 all6 gates PASS (NetCombat, SweptMelee, ProjectileBlock, Riposte, Hands, Visual). Playable build PASS at Builds/Playable/20261109-134646-984/EmberdelveNet.exe. Seed11/protocol8 two-window review: weapon swaps, guard, three-hit sword chain, client rapier thrust and recovered idle pose. Sessions closed client12/host14; process cleanup verified. Main Editor untouched.

## Current art limitation and next steps

1. User explicitly deferred replacing the cartoon-like class models to the next step. Keep this as a separate art milestone: human proportions, class silhouettes, grip/arm reach, then first/third-person review.
2. Current body proportions still make raised guard/reach poses stylized. Existing ranged blockout assets remain; no new draw/reload animation system was added. Do not describe this functional presentation slice as finished character art.
3. Continue only concrete requested work. No repeated green suites, broad refactors or persistent stash work without scope.

## Previous accepted work

- Class models/icons0d2f78ef: warrior Knight and archer Rogue, eight original skill icons, shared inventory/world model. Tasks/CLASS_VISUALS_20261109.md.
- Character sidebar72fb6de9; inventory layout57557aad.
- LOOT-01 inventory/chest/mob/player corpse449c1522: server-owned transfers, paper-doll layouts, paired inventory/loot panes. Tasks/LOOT_UI_20261109.md. Deposits/drag-to-container, persistent stash and rendered item thumbnails remain deferred.
- Crypt modules facf8243: Tasks/CRYPT_CONTENT_20261109.md. Raid baseline7f203c9c: Tasks/RAID_UI_ACCEPTANCE_20261108.md.

Six inherited local-only paths remain outside commits: Stone_Ceiling/Stone_Floor materials; Assets/Logs.meta; pipeline-connection-check screenshot/meta; Pipeline EditorPipelineConfig.json. Default9 players/parser64 is not a measured capacity claim. Localhost review does not certify internet loss/latency or load.
