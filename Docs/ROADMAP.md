# Emberdelve — Unity roadmap

Updated 3 Nov 2026. Roles and verification: AGENTS.md (fast loop in the open Editor + Tools/Run-All.ps1 on the mirror). Current state: CURRENT_WORK.md. Older roadmap text: Docs/Archive/ROADMAP_until_20261103.md.

Done: U0-U2 (local loop, attributes, item instances/affixes), D3-D4 (content, DEX/CHA/WIS rules), U3A network movement/lobby, U3B server skills + weapon combat, U3C1 blade trajectory/guard, V2 dark-retro visual slice.
Next: U4A -> U4B -> playable build -> U4C -> U3C2.

## Sequence and acceptance

| Stage | Deliverable | Evidence required before acceptance |
| --- | --- | --- |
| U0 | Preserve current M5; fresh audit and reproducible gate runner | Source/data/scene checkpoint, current inventory+combat runs, honest raw error/FAIL parsing; record unverified features |
| U1A | Repair wrong grid/stack fixture expectations and smoke exit status | Geometric2x3packing/fragmentation, actual partial stack overflow, failed scenario returns nonzero; remaining defense failures stay visible |
| U1B | Finish actual equipment/armor/block and atomic swap behavior | Real damage differs with gear, front/back block and unequip restore, rejected swap loses/duplicates nothing; no bypasses to satisfy fixture |
| U1C | Complete usable local inventory/chest loop and first visual checkpoint | Drag/move/equip/tooltips,3containers with preserved leftovers; actual windowed input/HUD/container screenshots and lobby->raid->result/retry |
| U2A | Six real attributes and derived stat rules | Exact amendment baselines, actual movement/damage/skills effects, recomputation/unequip without free healing |
| U2B | Item instances, stable UID, fixed+rolled affixes, cloak/back slot | Seeded rolls/count/allowlist validation, independent item copies, no rerolls or merge of unlike gear, exact snapshot roundtrip |
| U3A DONE | Minimal real network walking/lobby spike | NGO 2.7.0 + UTP 2.6.0 chosen; dedicated process + clients, ownership/input validation, server-simulated movement with owner prediction, late join, disconnect/rejoin, lobby Join/Host panel, offline retained. Evidence: Docs/Tasks/U3A1..U3A5.md, launch guide Docs/NETWORK_PLAY.md, final gate EmberdelveNetFinalGate |
| U3B DONE (U3B2 skills 5311bb6f, U3B3 weapon combat 91da51d1) | Server combat and skills | Real PvP/PvE damage/mana/cooldowns, forged/replayed/unauthorized commands refused, server chooses weapon/stats/outcomes |
| U3C (U3C1 done 91da51d1; U3C3 sword parry -> riposte done, uncommitted; U3C2 swing-aim visual pending) | Committed blade trajectory and directional height/stamina defense | Warning matches actual sweep, horizontal/overhead/thrust, evasion, front/back/wrongheight/emptystamina, shields never parry; U3C3: a sword block then block again within the data window = server-validated 2-strike riposte x1.5, never breaks the block; real client PNGs |
| U4A | Authoritative contested chest/inventory transactions | Two clients racing for one UID: one owner, exact rolls preserved; distance/LOS/death/disconnect/replay rejection |
| U4B | Corpse, raid timer, interrupted/successful extraction | Actual movement/damage/cancel/death interruptions, no dead/disconnected banking, exact-once result and corpse loot |
| U4C | Persistent server stash and next loadout | Atomic versioned save, restart restores exact UIDs/affixes, malformed/tampered data refused without partial loss |
| U5A | Licensed assets, animations and readable dungeon | Reuse downloaded packs; prefab adapters/materials/animations, inspected real frames, no gameplay/collider regression |
| U5B | Audio, mood and performance pass | Hear actual sound cues/music, readable dark lighting/UI, measure idle-machine performance and retain frame advancement |
| U6 | End-to-end Windows prototype release candidate | Dedicated+2clients full loot/death/extract/restart loop, actual visuals/input, targeted regression, launch scripts/README and known limitations |

Each row is split further if it cannot fit one coherent independently reviewable implementation. Do not dispatch aggregates. No date promises until measured throughput. U3 networking spike happens before expensive final art, so network risk is exposed early. Asset curation can proceed while local code is running; never read/edit the worker's intermediate source. Playable build for the user every 2-3 slices (Run-All.ps1 -Build).

## Operating rules

One slice = one feature = one commit. Executor compiles and runs the fast loop before handing back; Run-All before committing combat/net/scene slices. Fix a proven wrong expectation, never cement a defect. Prefer CC0 assets with license/hash records (Docs/ASSET_PLAN.md).
