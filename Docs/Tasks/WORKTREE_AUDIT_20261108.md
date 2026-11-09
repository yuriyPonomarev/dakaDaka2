# Uncommitted work audit - 8 November 2026

User policy: commit only completed tasks, grouped by milestone. No WIP snapshots.
This is an inventory and acceptance audit, not a fresh review of every gameplay implementation.

## What accumulated

The initial file-level inventory found 390 changed/untracked paths (including .meta files):
38 tools/docs, 87 art/data/prefab/scene files, 36 editor/build/gate files,
77 network/rules files, 81 runtime/resources/other files, 68 tests, 3 local-only files.
Raw inventory: Logs/harness/worktree-audit.json (local evidence, not a repository dependency).
These counts include the new harness work; they do not represent 390 separate tasks.

| Milestone / slice | What is in the working tree | Acceptance / commit decision |
|---|---|---|
| Development infrastructure | Change-based test planner, verified player cache, stable test executable paths, localhost firewall setup, instructions | Completed: Tasks/HARNESS_20261108.md. Isolated infrastructure commit excludes older game changes |
| 1: Network raid and modular map | Dungeon module catalog/planner/codec/assembler, saved Crypts scenes and meshes, spawn clearance, plan ACK before spawn, doors/AI, prediction and movement fixtures | Existing combined automated evidence in CURRENT_WORK; map/gameplay migration is intertwined across NetServer, RunDirector, builders and resources. Bridge through-route accepted on original seed2043687868 and seed11; final integrated and targeted corrections accepted |
| 2: Inventory, corpse and extraction | Inventory protocol/authority/UI, floor drops, corpse ownership, extraction hold/result, loadout and countdown rules, live gates and tests | ITEMS-01 atomic equip completed separately. Real UI pickup, reconnect, combat/disconnect corpse transfer, extraction/result/retry and host restart now verified (RAID_UI_ACCEPTANCE_20261108.md). Exact-UID simultaneous contention accepted v15; publication rollback and retry passed equipment-drop21/0. Shared dependencies require an integrated raid milestone |
| 3: Hands, blocks and enemies | Grip solver/guard blend, attack/hit trigger ordering, enemy client visuals, blood, skeletons/materials/controllers and generated assets | Host/client grip and guard, client damage-aligned attack v20 and host contact recoil v21 accepted. Final integration and shield-renderer visibility correction accepted |
| 4: Readable raid | HUD/countdown integration, map geometry and extraction presentation | Production600s timer and readable host/client HUD accepted v16, short timeout v14; bridge and both doorway seams crossed on two layouts v22/v23 |
| Local-only drift | Stone_Ceiling.mat, Stone_Floor.mat, ProjectSettings/Packages/com.unity.pipeline/EditorPipelineConfig.json | Excluded by AGENTS. Preserve in place; do not reset/delete or include in milestone commits |

The new Grab3D skeleton source/optimized model is intentional content, not a build cache.
Its local LICENSE.txt and manifest travel with the asset; adding it to Git still does not imply visual/performance acceptance.
Generated level meshes and scene/prefab .meta files are source dependencies; do not delete them as temporary output.

## Why this cannot be split by folder alone

NetServer and NetClientProbe contain several tasks at once. Network inventory depends on runtime
item authority and shared rules; modular scenes depend on resources/builders and the network plan
handshake; enemy presentation depends on prefabs, controllers and assets. A folder-based commit
would misrepresent task completion and may omit required dependencies.

Infrastructure and atomic equip were already committed separately. Stages1–4 now share the completed raid implementation across these files. Accept and commit that coherent integrated milestone only after its remaining checks finish; a folder split would leave required dependencies absent. The explicit candidate manifest contains398 paths; six local-only paths remain excluded. Counts include Unity metadata and generated source assets, not398 separate tasks.

## Final acceptance — 9 November 2026

Stages1–4 are accepted as one coherent raid milestone. Full20261109-055711-479 (68/70 gates,924/925 tests) plus corrected shield1/1 and targeted20261109-062114-364 (NetMovement190/0 on original seed, NetFinal172/0) close all observed failures. Live evidence includes v24 real blocked hit with0 damage and weapon/guard return. See CURRENT_WORK for the compact handoff.

Explicit398-path manifest excludes six local-only material/Pipeline/screenshot changes. Infrastructure and atomic equip remain their own earlier commits. No folder-only split or WIP snapshot. Historical implementation notes are labeled; persistent stash and new content remain deferred.
