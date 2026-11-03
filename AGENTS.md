# Emberdelve (DakaDaka) — agent instructions

Unity 6000.2.5f1, URP 17.2, NGO 2.7, C#, new Input System. Game root: `Assets/Emberdelve` (namespaces `Emberdelve.*`). D&D-style PvPvE extraction game. Design target: `Docs/Design/UNITY_DESIGN.md`. Plan: `Docs/ROADMAP.md`. Current state: `Docs/CURRENT_WORK.md` (one page, overwritten, not appended).

## Roles (user, 3 Nov 2026)
- **Claude (orchestrator):** splits work into slices, writes briefs, integrates, runs the fast check loop, commits.
- **Sonnet sub-agent (executor):** implements a slice and must compile + run the fast loop itself before handing back.
- **Run-All.ps1 on the mirror:** deterministic full verification (no LLM). The local Pi/Strata model is optional: only to analyse failures when the cause is unclear.
- **User:** plays builds, makes design decisions.

## Hard rules
- NEVER close the user's Unity Editor. Never `EditorApplication.Exit` interactively. Batch Unity runs only on the mirror `..\DakaDaka_ci`.
- One slice = one feature = one commit. No combined slices.
- No engine/package upgrades, no resetting the working tree, no monolithic runtime-generated game: wire scenes/prefabs with the existing editor builders (`EmberdelveBootstrapMenu.BuildEverything`).
- Server authority: client input is intent only; the server decides damage/items/outcomes. Shields never parry. Shared ScriptableObjects immutable at runtime; item UID/rolls are per instance.
- Never weaken a test to make it pass. Fix a proven wrong expectation and say why. Expectations are derived from data, never hardcoded numbers.
- New checks go in NUnit EditMode tests (`Assets/Emberdelve/Tests/EditMode`) when possible. No new source-text grep checks in gates; architecture lint lives in `EmberdelveSourceLint` and only warns.
- Assets: free/CC0 only, record license + source in `Docs/ASSET_PLAN.md`.

## Fast loop (minutes, in the user's open Editor via Unity MCP)
1. Write files (`asset_write_text` or System.IO in `script_eval`), then `AssetDatabase.Refresh()`.
2. Wait `editor_status.compiling=false`; `console_get level=error` must be empty.
3. EditMode tests: `test_run mode=editor filter=Emberdelve filter_type=assembly` (≈5 s, 555 tests). Its output is large — read only `summary`.
4. Logic gates: `script_eval` → `Emberdelve.EditorTools.EmberdelveGateSuite.StartInteractive("fast", "<project>/Logs/quick/<stamp>")`, then poll `EmberdelveGateSuite.Status()` / `summary.txt` (≈7 s). Sets live in `Tools/gate-sets.json`.
5. Gates that open scenes / enter Play mode / spawn processes (sets `combat`, `net`, `visual`) are NOT run in the user's editor — use Run-All.

## Full verification (mirror, no LLM)
`powershell -ExecutionPolicy Bypass -File Tools\Run-All.ps1 -Set all -EditMode [-Build]`
- Syncs main → `DakaDaka_ci` (robocopy), runs EditMode + all gates in ONE batch Unity, optional Windows player build.
- Output: `Logs/runs/<stamp>/summary.txt` + `run.json`. Exit 0 = green. Refuses if the mirror is locked.
- Launch it detached from `script_eval` (`cmd /c powershell ... > log 2>&1`) and poll the output file.
- Run before every commit of a slice that touches combat/net/scenes; the fast loop is enough for pure-logic slices.

## Delivering code
Write files directly in the project through Unity MCP. No zip/SendUserFile round trips. git via `script_eval` → `cmd /c git ...`. Do not commit `Art/Materials/*.mat` drift or `ProjectSettings/Packages/com.unity.pipeline/`.

## Playable builds
Every 2–3 slices: `Run-All.ps1 -Build` → `Builds/Playable/<stamp>/EmberdelveNet.exe`; tell the user what to try. Network launch: `Docs/NETWORK_PLAY.md`.

## Docs
- `Docs/CURRENT_WORK.md`: ≤ 1 page, rewritten each slice (state, next, blockers).
- `Docs/Tasks/<ID>.md`: short slice report (changed files, behaviour, checks actually run).
- History: `Docs/Archive/`. Don't read it unless needed.
