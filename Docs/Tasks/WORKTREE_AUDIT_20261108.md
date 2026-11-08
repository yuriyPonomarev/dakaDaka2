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
| 1: Network raid and modular map | Dungeon module catalog/planner/codec/assembler, saved Crypts scenes and meshes, spawn clearance, plan ACK before spawn, doors/AI, prediction and movement fixtures | Existing combined automated evidence in CURRENT_WORK; map/gameplay migration is intertwined across NetServer, RunDirector, builders and resources. Keep pending milestone acceptance; do not present the whole migration as a completed isolated fix |
| 2: Inventory, corpse and extraction | Inventory protocol/authority/UI, floor drops, corpse ownership, extraction hold/result, loadout and countdown rules, live gates and tests | Real two-client UI/reconnect/full-raid acceptance missing; ITEMS-01 atomic equip risk and QA-01 failure injection remain open. Do not commit as completed U4A/U4B |
| 3: Hands, blocks and enemies | Grip solver/guard blend, attack/hit trigger ordering, enemy client visuals, blood, skeletons/materials/controllers and generated assets | Automated Hands/Visual/NetCombat evidence exists. Motion review and authoritative attack phase timing are missing; keep uncommitted |
| 4: Readable raid | HUD/countdown integration, map geometry and extraction presentation | Production timer is disabled; HUD, seams and bridge need review; keep uncommitted |
| Local-only drift | Stone_Ceiling.mat, Stone_Floor.mat, ProjectSettings/Packages/com.unity.pipeline/EditorPipelineConfig.json | Excluded by AGENTS. Preserve in place; do not reset/delete or include in milestone commits |

The new Grab3D skeleton source/optimized model is intentional content, not a build cache.
Its local LICENSE.txt and manifest travel with the asset; adding it to Git still does not imply visual/performance acceptance.
Generated level meshes and scene/prefab .meta files are source dependencies; do not delete them as temporary output.

## Why this cannot be split by folder alone

NetServer and NetClientProbe contain several tasks at once. Network inventory depends on runtime
item authority and shared rules; modular scenes depend on resources/builders and the network plan
handshake; enemy presentation depends on prefabs, controllers and assets. A folder-based commit
would misrepresent task completion and may omit required dependencies.

Only the harness-specific call-site changes can be separated cleanly from those mixed files.
Stage those exact hunks; leave the surrounding map/gameplay changes uncommitted. New gameplay
gate files remain with their unfinished milestone, including their stable-path integration.

## Next completion order

1. Infrastructure slice complete: real cache HIT and focused process evidence recorded; commit it alone.
2. Close ITEMS-01, then perform the milestone 2 two-client acceptance scenarios. Commit coherent completed tasks with their rules, resources, tests and current evidence.
3. Finish authoritative attack timing and review hands/block/enemy motion for milestone 3 before committing that slice.

Historical task reports include superseded statements such as 'not compiled' or 'floor loot not replicated'.
CURRENT_WORK and CRITICAL_DEBT own current status; those older reports are not new blockers or acceptance claims.
No new full test run is required solely to inventory or commit unchanged files.
