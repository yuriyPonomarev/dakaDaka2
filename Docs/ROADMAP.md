# Emberdelve — Unity roadmap

Updated19October2026. Continue the existing project. Codex orchestrates/reviews; local Pi/Strata high implements. Working title/namespaces stay Emberdelve. This replaces the old roadmap as the forward plan; PROJECT_STATUS.md remains historical evidence.

## Current continuation — 31 October 2026

The user resumed milestone development after Opus. Latest evidence and the executable next work item are in [D3bc-20261030.md](Tasks/D3bc-20261030.md). D3b/D3c are now compiled and stabilized: **60 groups / 43,164 checks / 0 failures**, including D3c 188/0, slots 442/0 and snapshots 1023/0. D2 ProjectileSim 108/0 and D3a RarityLadder 623/0 also ran on this tree. EditMode passed 242/0; the final inventory UI passed 485/0 after the text-fit fix, and the actual rendered thirteen-row screen was inspected with zero clipped labels. D3d must expose the new items and timed consumable/penalty rules in ordinary gameplay before D4; D3 is not accepted as a complete content milestone yet. The October 26 bounded scope below is historical and superseded by this continuation.

## Historical continuation checkpoint — 26 October 2026

The detailed continuation sequence and accepted task evidence are in [OPUS_COMPLETION_PLAN.md](Tasks/OPUS_COMPLETION_PLAN.md). Physical projectile/blade blocking is implemented and freshly verified (50/0). R10d snapshot consumer closure is accepted (seven groups / 11259 checks / 0 failures), and R6c1 inventory-screen identity/authority routing is accepted (four groups / 926 checks / 0 failures). Next narrow slice: R6c2 stable hotbar assignments. These are local Unity results; U3 dedicated-server/client proof remains pending.

Current user scope (26 October 2026): close only started changes and the nearest Opus refactors: R6c2, R6d, prepared R8a and started R8b1. Later gameplay/network/release stages below are retained as backlog.

## Historical state at original roadmap creation

Implemented code/scenes: lobby/class selection, FPS movement/HP HUD, enemy AI, local melee/ranged attacks and ground telegraphs, yaw block/shield no-parry data, secondary weapons, mana/two actives/two passives, loot/extraction/death/result/re-entry loop.62runtime C#files in modular areas. Historical M4 logs contain7passing scenarios/470checks, but predate unfinished M5 and are not fresh acceptance. Current M5 adds grid/equipment/chests/UI; current independent inventory run197checks/10failures, no C# compile errors, exit0despiteFAIL. No actual multiplayer/server,6attribute integration, per-instance UID/affixes, persistent stash or final visual/art acceptance yet.

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
| U3B | Server combat and skills | Real PvP/PvE damage/mana/cooldowns, forged/replayed/unauthorized commands refused, server chooses weapon/stats/outcomes |
| U3C | Committed blade trajectory and directional height/stamina defense | Warning matches actual sweep, horizontal/overhead/thrust, evasion, front/back/wrongheight/emptystamina, shields never parry; real client PNGs |
| U4A | Authoritative contested chest/inventory transactions | Two clients racing for one UID: one owner, exact rolls preserved; distance/LOS/death/disconnect/replay rejection |
| U4B | Corpse, raid timer, interrupted/successful extraction | Actual movement/damage/cancel/death interruptions, no dead/disconnected banking, exact-once result and corpse loot |
| U4C | Persistent server stash and next loadout | Atomic versioned save, restart restores exact UIDs/affixes, malformed/tampered data refused without partial loss |
| U5A | Licensed assets, animations and readable dungeon | Reuse downloaded packs; prefab adapters/materials/animations, inspected real frames, no gameplay/collider regression |
| U5B | Audio, mood and performance pass | Hear actual sound cues/music, readable dark lighting/UI, measure idle-machine performance and retain frame advancement |
| U6 | End-to-end Windows prototype release candidate | Dedicated+2clients full loot/death/extract/restart loop, actual visuals/input, targeted regression, launch scripts/README and known limitations |

Each row is split further if it cannot fit one coherent independently reviewable implementation. Do not dispatch aggregates. No date promises until measured throughput. U3 networking spike happens before expensive final art, so network risk is exposed early. Asset curation can proceed while local code is running; never read/edit the worker's intermediate source. Bring a playable loop back for review after U1, U3 and U4 rather than spending all effort on isolated harnesses.

## Operating rules

One controller/Pi task globally; shared OS lock also excludes the archived Godot controller. Same local model/config/high. Worker file tools only; manager executes Unity gates sequentially, reviews code after settlement and inspects actual PNGs. Preserve working code/dirty M5. Explicit checks on APIs, gameplay and negative cases; exit0or a summary is insufficient. Correct a demonstrated wrong test, never cement a defect in expected values. Failed tasks get a small reproducible repair; accepted slices advance immediately. Heartbeat stays quiet while the task is healthy, stops on user request, and is disabled after U6 acceptance.

Assets: dark stylized fantasy is the initial assumption consistent with the approved design and existing KayKit downloads. Prefer CC0; keep license/hash/provenance. Do not mix unrelated visual packs or download executable helpers. See Docs/ASSET_PLAN.md.
