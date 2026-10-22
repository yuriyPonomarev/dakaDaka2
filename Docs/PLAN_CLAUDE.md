# Claude orchestration plan (2026-10-20)

Takes over from Codex/Pi (paused 2026-10-20 16:09 at U2B10). Authoritative design: `Docs/Design/*`, `Docs/ROADMAP.md`, `AGENTS.md`. Historical per-task evidence: `Docs/Tasks`, `Docs/Reviews`, `C:/Users/yrapa/.pi/agent/projects/unity-emberdelve/` (manager-state.json, validation/ logs).

## Working model
- Orchestrator (this session) plans, dispatches, checks evidence; does not write feature code.
- Implementer: Sonnet subagent per task (one coherent slice, brief states files, acceptance, forbidden areas).
- After each large task: fresh-context Opus reviewer subagent (reads the diff + the task report, runs nothing it can't verify, tries to find false-green tests).
- Unity gates run by the orchestrator/one worker at a time (project lock): `Unity.exe -batchmode -nographics -projectPath . -executeMethod ... -logFile Logs/<task>.log`; parse the log for `PASS checks=N failures=0`, never trust exit code alone.
- Rules carried over: never weaken a test; `Assets/Emberdelve` is the feature root; no engine/package upgrade; shared SO definitions immutable at runtime; client never owns damage/rolls.

## State found
- Done and accepted in scope: M1–M4 (movement, telegraphs/block, loadouts, mana/skills), U1 (grid inventory, equipment, chests, UI, windowed review loop), U2A (6 attributes, armor), U2B1–U2B9-R1 (affix defs, item instances with UID, chest gear instances). Last green gates: ChestInstance 21/0, ChestPartial 17/0, items 403/0, inventoryui 158/0.
- U2B10 (13 affix assets + Common/Uncommon/Rare chest profiles in `EmberdelveAssetLibrary.cs`): code written, **never compiled or gated**.
- Not started: cloak slot, rarity/fixed-vs-rolled tooltips, persistence, any networking (U3), corpse/raid timer/stash (U4), art/audio (U5), release (U6).

## Risks to fix first
1. **No safety net**: HEAD is `99dabd30` (M4); all M5–U2B work is uncommitted. There is **no `.gitignore`**, so `Library/`, `Logs/`, `Temp/` are tracked/noisy (8k changed paths). Add a Unity `.gitignore`, untrack Library/Logs/Temp, commit the working tree as a checkpoint. (Needs user OK — touches git state.)
2. Compile state unknown after U2B10 edits.
3. Full regression suite has not been run on the current tree since U2B9-R1.

## Task queue
| # | Task | Notes |
|---|------|-------|
| P0 | Git hygiene + checkpoint commit | after user OK |
| P1 | Compile + verify U2B10: BuildDataOnly twice (idempotent, 13 GUIDs), real TryRoll seeds, regressions (ChestInstance, ChestPartial, items, inventoryui, AffixDefinition) | Sonnet worker, then Opus review |
| P2 | Full regression sweep: Combat, Archer, AI, Death, Extraction, Lobby, ResultLoop + gates above | establishes baseline |
| P3 | U2B11: cloak/back slot + boots coverage, rarity colour + fixed-vs-rolled tooltip, character panel | |
| P4 | Windowed whole-game review (build -> review bridge): lobby -> raid -> chests -> full bag -> extract/death -> retry | closes U1C/U2B aggregates |
| P5 | U2B12: item snapshot save/restore (client-local, exact UID/rolls, tamper rejection) — prepares U4C | |
| P6 | U3A: network spike. Pick stack first (Netcode for GameObjects vs alternatives vs Unity MP Services; check compat with 6000.2.5f1 / URP). Dedicated server + 2 clients, keep offline mode | research task before code |
| P7 | U3B/U3C: server combat, committed blade trajectory + height/stamina defense | |
| P8 | U4A–C: contested chests, corpse/timer/extraction, persistent stash | |
| P9 | U5A/B: KayKit art integration (vendor path in ASSET_PLAN.md), audio, lighting | |
| P10 | U6: RC, launch scripts, README | |

Every row splits further if it cannot be reviewed as one change.

## Skills available (Unity plugin, already loaded)
unity-cli (drive Editor, batch builds), ui / ui-ugui / ui-uitk, urp-postprocessing, optimize-text-mesh-pro, optimize-audio / audio-setup-mixers, setup-multiplayer-services (for U3), build-live-game, physics-3d-collision, initialize-ai-navigation, manage-sprite-atlas. Project UI is code-built uGUI (`UiFactory`), so ui-ugui applies.

## Update after Opus health review (Docs/Reviews/CODEBASE_HEALTH_20261020.md) — verdict: refactor first, no rewrite
Done: git hygiene (5dab7b0e), U2B10 verified (d00a3617). P2 baseline sweep in progress (Docs/Tasks/P2-baseline.md).
Order (one Sonnet worker + one fresh Opus review + commit per item; Unity gates strictly sequential, GUI editor must stay closed):
1. R0 extraction bypass (live bug): Interactor/RunDirector re-check CanInteract/loot rule on hold completion; damage/movement interrupt hold; real hold test with zero loot.
2. R1a/R1b combat authority stops trusting client: resolves equipped weapon itself, range/hit point/headshot checks, own clock, skill kit/mana/ammo/activation checks, caster-side effect scale; rewrite smoke fixtures that relied on forged timestamps; add negative tests.
3. Cloak/back slot + rarity tooltips keyed on ItemInstance (reviewer says safe in parallel: append EquipmentSlot.Back, derive slot count from enum).
4. R3 runtime item catalog; Inventory stops minting gear for arbitrary ItemData.  R9 versioned container snapshot (persistence).
5. R2 actor on interactions; R4 single seeded loot pipeline (enemy drops, ground pickups keep instance); R5 per-raid seed + own RNG; R6 stable inventory ids + command layer, split InventoryScreenController; R7 per-player events, team at spawn, PvP; R8 asmdefs + NUnit; R10 one effective-stats snapshot.
6. Only then P6 networking (U3A).

## Pause checkpoint (2026-10-21, user stop)
Committed and verified: git hygiene, U2B10, R0, R1a, R1b, U2B11, P4a/P4b, P4c, R3, R9 (HEAD c112e97d, working tree was clean before R4+R5).
IN PROGRESS, UNVERIFIED, UNCOMMITTED: R4+R5 (seeded loot pipeline, per-raid seed, project RNG, enemy-drop instances, pickup instances). Worker was stopped mid-task; the working tree has partial edits (EnemyAgent, LootTable, LootRoller, LootPickup, RunDirector, RunContext, Inventory, ItemInstance, ChestInteractable, EnemyData, asset library, new Loot_Enemy_* assets, new Editor/EmberdelveLootPipelineGate.cs). Compile state unknown, nothing gated. On resume: resume worker ab035d3cb03137f65 (SendMessage) or review the diff first; do not commit before a full green regression + Opus review.
Next after that: R2+R7 (actor on interactions, per-player events, teams/PvP), R6, R8, R10, R1 follow-ups (KitEpoch ownership, caster id auth, server dash), then U3 networking spike.
Unity: GUI editor must stay closed during batch gates. Unity MCP registered for Claude Code (user scope) but only available after a session restart.

## User decisions on Dark and Darker parity (2026-10-22), see Docs/Design/DND_PARITY.md
Asked and answered: (1) attributes: KEEP six names (STR/DEX/CON/INT/WIS/CHA), move their effects toward DaD's stat roles via curves (CON~Vigor, DEX~Agility+Dexterity, INT~Knowledge, WIS~Will, CHA~Resourcefulness); keep save/affix keys. (2) stamina: REMOVE stamina from block and sprint (guard break instead); mana stays for Warrior/Archer for now (user chose "remove only stamina"). (3) players: parties of up to 3, 6-9 players per match. (4) order: combat rework D1 (swept melee, wall stops, hit locations, 3-hit combos, tip-vs-hilt damage, slow on hit, movement penalties) BEFORE the U3A networking spike.
Not asked, taking the analyst's recommendation unless the user objects: 4 classes eventually (Rogue-like + caster), closing zone as an optional config flag, economy = stash + 2 merchants + gold + starter kit (no quests/market), rarity ladder Poor..Unique (append integers), new slots append-only.
Active refactors before D1: finish R4+R5 (review pending), then R2+R7, R6, R8, R10, R1 follow-ups.
