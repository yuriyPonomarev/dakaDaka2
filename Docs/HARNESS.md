# DakaDaka harness

The harness selects relevant checks and reuses verified player builds. It never reuses PASS verdicts.

## Everyday loop

From the project root (Python 3.9+ and existing PowerShell/Git; no pip packages):

```powershell
# Before editing: save a content checkpoint, especially in this inherited dirty tree.
python Tools/harness.py checkpoint Logs/harness/before-my-slice.json
# After editing: inspect the selection and reasons without launching Unity.
python Tools/harness.py plan --since Logs/harness/before-my-slice.json
python Tools/harness.py plan --since Logs/harness/before-my-slice.json --json
# Run precisely that plan; it is recorded with the test evidence.
python Tools/harness.py run --since Logs/harness/before-my-slice.json
```

Without --since, comparison is against HEAD: committed-base differences, staged/unstaged changes, deletes, renames and untracked files all count. A checkpoint records file content, not timestamps, Git cleanliness or test acceptance. Never create a new checkpoint simply to hide an untested relevant change.

For an explicitly reviewed slice in a mixed tree:

```powershell
python Tools/harness.py plan --files Assets/Emberdelve/Net/NetEnemyViews.cs
python Tools/harness.py run --files Assets/Emberdelve/Net/NetEnemyViews.cs
```

`--files` deliberately limits the claim to those files; it does not certify the whole tree. Unknown/shared dependencies fall back to all + EditMode. A full selection requires `--allow-full` on `run`; planning always remains read-only. Inspect and explain that expensive choice before launching. Empty/docs-only plans do not launch Unity. Test-only changes run EditMode without gameplay gates. An audio-only change to the cue bank, player action cues, event window, UI sounds or audio fixtures uses the `AudioTests` filter. A second change requiring broader EditMode coverage restores the `Emberdelve` filter. Written task reports, project skills and Cursor rules select zero Unity scenarios; an executable beside a document still follows its own routing.

Routing lives in Tools/harness.py, validated by Tools/tests/test_harness.py. It is an explicit conservative map, not a compiler dependency graph. Update routing and its tests whenever a new subsystem/cross-system dependency is introduced. Source/data changes not safely mapped must remain broad. Gate selection is deduplicated and retains suite order.

## Build cache

Tools/Run-All.ps1 enables EMBER_BUILD_CACHE=1 for its child Unity processes. Net proof and lobby-first NetFinal builds have separate keys. The key includes:

- Unity version, target/options, executable filename and ordered scene list;
- SHA-256 of paths and contents under Assets, Packages and ProjectSettings (including editor scripts, tests and metadata, conservatively);
- mutable resolved local/Git package contents outside Packages; registry/builtin identity comes from package manifest/lock and Unity version.

Cache location: mirror Builds/HarnessCache. A completed entry has an atomic manifest plus a uniquely named payload. The full output inventory and hashes are validated, including additions/deletions, before reuse; inputs and copied outputs are checked again on a hit. Tests use stable mirror Builds/HarnessPlayers/net-proof and net-final paths; logs remain isolated per run. The canonical cache payload is never launched. An unchanged stable payload stays in place; a changed payload swaps directories only when its executable is not running, retaining the previous generated payload for diagnosis. Profile/inputs changing means a miss. Failed builds publish no manifest. Dirty authored persistent assets bypass caching (internal Library editor-window state is not a build input); builds that change their own inputs are not stored. Cache errors fail explicitly. Linked/junction inputs are unsupported; use a normal mirrored checkout or disable caching. Cache is opt-in through the runner and is off for interactive/direct calls unless explicitly enabled in a batch process.

Before hashing, the batch mirror saves builder-produced resources. Unsaved native serialized assets/importer changes still bypass caching. Imported shader/texture editor dirty flags are not serialized source changes: their source files and importer metadata are hashed. The interactive Editor is never saved through this helper.

Logs distinguish MISS / STORED / HIT / BYPASS / NOT_STORED and include key/profile/time. No source timestamp shortcut. No reuse of an arbitrary old exe with -ember-proof-reuse-exe.

Escape hatch:

```powershell
python Tools/harness.py run --files Assets/Emberdelve/Net/NetEnemyViews.cs --no-build-cache
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Run-All.ps1 -Set NetProof -NoBuildCache
```

Cache has no automatic eviction yet. Invalid/old payloads remain for diagnosis; manual cleanup must target only mirror Builds/HarnessCache when no build/client uses it. Reuse avoids compilation/build processing, but still hashes inputs/outputs and materializes a stable player directory when its content differs. Windows uses native CNG streaming SHA-256 (tested against standard SHA-256); other editor platforms use the managed implementation. Final local measurement: fresh/store 36.32s, HIT 8.18s. Warm Unity incremental builds may sometimes be cheaper; these numbers are not whole-suite speedups. Evidence: Tasks/HARNESS_20261108.md.

## Existing direct interface

`Tools/Run-All.ps1 -Set <gate-list-or-set> [-EditMode] [-Build] [-NoBuildCache]` stays available.
`-SkipGates -EditMode` runs only NUnit. Plans are copied to each run's selection-plan.json; run.json also records cache/selection options. `Tools/Get-WorkStatus.ps1` prints concise results and distinguishes explicit cancellation from a discovered regression.

Start PowerShell in the development Git root and use the scripts from that checkout. All Windows batch entry points validate the source and sibling mirror. They share a process lock across synchronization, tests and builds, and refuse a busy Editor or a remaining Unity lock. Keep evidence under the primary or mirror Logs directory. The network build shortcut also uses this runner and keeps its existing default player location. A focused verification can pass `-EditMode -EditModeFilter <fixture>`.

Only one coordinator owns the mirror. Do not launch overlapping runs. No package upgrades, changes to game assertions, or main-Editor Play mode are required for this harness.

## Tools / skills

Check the configured Unity MCP project path before use. If it points to the read-only legacy copy, use the CLI with the explicit absolute project path. For visual review follow the [shared capture procedure](../Tools/LocalTesting/unity-tester/references/graphical-review.md). Use the installed Unity Pipeline for compile/status and focused editor access; the mirror runner owns process/playmode checks. Use the existing computer-use tooling for actual visual/input acceptance and installed Unity UI/animation-specific skills when their tasks arise. The installed Memory Profiler and Recorder packages are useful during measured performance/visual passes, not every edit. Git + this document + AGENTS are sufficient for task handoff; no new MCP server or external service is needed for this optimization.

A separate project skill would duplicate these instructions today. Consider one only when a reusable workflow spans multiple projects. Do not install plugins or add background services merely because they exist.

## Windows Firewall (one-time local setup)

The old timestamped executable paths created repeated program identities (538 matching application filter entries observed). Tests now keep two fixed executable paths across builds. Existing old firewall entries are left untouched.

`Tools/Configure-TestFirewall.ps1` previews four rules. `-Apply` requires one administrator confirmation and allows only IPv4 localhost 127.0.0.1 for those exact programs (TCP/UDP, all network profiles); no LAN/Internet inbound access or global notification change. `-Remove` removes only those script-owned rules. Windows rejected explicit IPv6 loopback in this rule type, so IPv6 is not configured. Current test transport uses IPv4 localhost.

Rules were installed on 8 November 2026. Project moves require reconfiguration; the script refuses silently replacing rules owned by another path. Multiplayer LAN/release permissions are a separate decision. See Microsoft Windows Firewall rules documentation: https://learn.microsoft.com/en-us/windows/security/operating-system-security/network-security/windows-firewall/rules

Review input: opt-in --ember-review-dir uses synthetic Keyboard/Mouse layouts that run in the background. This permits independent input for two player windows without taking OS focus. Normal launches and native input devices retain their focus policy. Inspect matching ack id and deviceKeyFramesPressed plus the rendered outcome; ok=true alone only confirms command delivery, not gameplay success.
