# U1C2A — Windows development build entrypoint + opt-in review bridge (implementation saved, not verified)

## Files (all new; nothing else touched)

* `Assets/Emberdelve/Editor/EmberdelveWindowsBuild.cs`
* `Assets/Emberdelve/Runtime/Review/EmberdelveReviewBridge.cs`
* `Docs/DEV_REVIEW.md`
* `Docs/Tasks/U1C2A.md` (this report)

No existing runtime/UI/editor/test file, scene, prefab, asset, package, ProjectSettings or metadata was
modified. No `.meta` files were written (Unity generates them on the manager's refresh).

## EmberdelveWindowsBuild.BuildReview()

`public static` in `Emberdelve.EditorTools`, synchronous, headless.

* Reads `--ember-build-output <path>` (also `--ember-build-output=<path>`, single-dash tolerated) from
  `Environment.GetCommandLineArgs`. Rejects: missing flag/value, relative path, non-`.exe`, and any path
  inside `Assets/` (a build output there would be imported). Creates the parent directory.
* Scenes: `EmberdelvePaths.LobbyScenePath` then `EmberdelvePaths.DungeonScenePath` (the saved scenes read
  from `Editor/EmberdelveProjectSetup.cs`). Missing scene files are reported as a blocker; nothing is
  regenerated. `BuildTarget.StandaloneWindows64`, `BuildOptions.Development`, no other options.
* No Player launch, no data/scene/input/package rebuild, no Cockpit scenes, no dialog, no project-setting
  change, no delete/cleanup (the manager supplies a fresh output directory).
* Logs the report as-is (`result`, `platform`, `options`, `totalSize`, `totalTime`), then
  `[Emberdelve][BuildWindows] PASS result=... output=... exeBytes=... dataFolder=... totalSize=... totalTime=...`
  and `EditorApplication.Exit(0)` **only** when `report.summary.result == BuildResult.Succeeded` **and**
  `File.Exists(output)`. Otherwise `[Emberdelve][BuildWindows] FAIL result=... outputExists=...` and Exit(1);
  an exception is logged and also exits 1. Engine build warnings/errors are left in the log untouched.

## EmberdelveReviewBridge (whole file inside `#if DEVELOPMENT_BUILD || UNITY_EDITOR`)

* `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)] Install()` parses `--ember-review-dir <absolute dir>`.
  **No flag → returns immediately: no GameObject, no `runInBackground`, no devices, no file access.**
  A non-absolute directory is logged as a failure and review is not initialised. A second install is
  ignored instead of creating a second host.
* With the flag: one `DontDestroyOnLoad` host `Emberdelve_ReviewBridge`, `<dir>/snapshots/` created,
  `command.json` / `ack.json` / `ready.json` in `<dir>`, `Application.runInBackground = true` for that
  session only.
* Polling: `command.json` is detected by timestamp+length change and read with `FileShare.ReadWrite`
  (atomic replace by the reviewer). One command at a time (`_busy`); `id` must be a strictly increasing
  positive int, otherwise the ack is `ok:false` with `stale or duplicate id` and nothing is executed — a
  repeated id cannot repeat a click. Unknown action / bad key name / bad button / out-of-screen pointer /
  unsafe snapshot name each produce a failed ack, never a shutdown. A malformed file produces
  `id:-1, action:"read", ok:false`.
* `wait` — plain frame advance (default 60, cap 900).
* `key` — `InputSystem.AddDevice<Keyboard>("EmberdelveReviewKeyboard")`, `MakeCurrent()`, then
  `InputSystem.QueueStateEvent` with `KeyboardState` (`Set(Key,bool)`) held for `frames` real frames
  (default 3, cap 120) and released, plus one extra frame so the release is consumed by the ordinary
  input update. No gameplay method, no reflection, no manual `InputSystem.Update()`.
* `mouse` — `InputSystem.AddDevice<Mouse>("EmberdelveReviewMouse")`, `MakeCurrent()`, queued `MouseState`
  (`displayIndex`, `position`, `delta`, `scroll`, `buttons` bit 0=left, 1=right): move first, one frame
  later press, hold, release. Position is validated against the live `Screen.width/height` with the
  bottom-left origin. Consumed by the existing `InputSystemUIInputModule`/`InputActionAsset` in their
  normal update — no `Button.onClick.Invoke`, no direct inventory/state calls.
* `snapshot` — `WaitForEndOfFrame` (≤3 attempts) then `ScreenCapture.CaptureScreenshotAsTexture` +
  `EncodeToPNG` (fallback `ScreenCapture.CaptureScreenshot`), file existence re-verified over ≤60 frames,
  real width/height read from the PNG IHDR, byte count reported. Name is sanitised and the final path is
  checked to stay under `<dir>/snapshots/`. A capture that cannot be verified on disk is a **failed** ack.
* `quit` — release held input, flush ack, one frame, `Application.Quit()` (Editor note logged).
* Every ack carries `id, ok, action, message, error, screenshot, scene, frameCount, realtime, time, width,
  height, reviewDir, utc`, so a frozen frame cannot masquerade as running gameplay. `ready.json` (id 0) is
  written after two real frames, followed by the automatic first snapshot `000_first_<scene>.png` (ack id 0)
  — a diagnostic only, no success/gameplay claim.
* Cleanup: held keys/buttons released after each action, on `OnDisable`, `OnApplicationQuit` and
  `OnDestroy`; only the two virtual devices this bridge created are removed. A 45 s real-time watchdog
  stops a never-finishing coroutine, releases input and writes a failed ack. File/parse/capture errors are
  caught in non-iterator helpers only; no `yield` appears inside `try`/`catch` or `finally`, and no
  exception is swallowed silently.

## Checks actually run by the worker

None executed (file tools only, per contract). Verification so far is static API confirmation against the
installed sources: `KeyboardState(params Key[])` and `Set(Key,bool)` (`Library/PackageCache/
com.unity.inputsystem@be6c4fd0abf5/InputSystem/Devices/Keyboard.cs:190,218`), `Key.Enter/Escape/Tab/Space/
LeftShift/LeftCtrl/LeftAlt/Digit0-9/UpArrow/DownArrow/LeftArrow/RightArrow` (Keyboard.cs:314,554,573,588,
643,653,658,663), `MouseState.position/delta/scroll/buttons` (Mouse.cs:36,49,88),
`InputSystem.AddDevice<TDevice>(name)` (InputSystem.cs:1455), `RemoveDevice` (InputSystem.cs:1558),
`InputDevice.MakeCurrent` (InputDevice.cs:505), `QueueStateEvent` usage pattern already proven in
`Editor/EmberdelveRuntimeSmokeTest.cs:1352-1363`, `InputSystemUIInputModule` + `ScreenSpaceOverlay` canvas
created by `Runtime/UI/UiFactory.cs:38-61`, and `Mouse.current` consumption in
`Runtime/UI/InventoryScreenController.cs:179`.

## Pending manager gates (not run here)

1. Compile/import: expect no `error CS` from the two new files (Unity will also create their `.meta`).
2. `-executeMethod Emberdelve.EditorTools.EmberdelveWindowsBuild.BuildReview --ember-build-output <fresh
   absolute .exe>` → expect `[Emberdelve][BuildWindows] PASS ...` and exit 0; a bad/relative/non-.exe flag
   must produce `FAIL` and exit 1.
3. Launch the real 1280x720 windowed Player with `--ember-review-dir <fresh absolute dir>`; confirm
   `ready.json`, `snapshots/000_first_Lobby.png` and the ack id 0, and that frameCount/realtime/screen size
   actually advance.
4. Exercise `wait`, `key` (e.g. `w`, `tab`, `e`), `mouse` (a real lobby button click at a documented
   bottom-left pixel), `snapshot` (lobby + dungeon + inventory UI PNGs) and `quit`; also confirm a stale id,
   an unknown key and an out-of-screen pointer each fail cleanly without closing the game.
5. Judge the actual PNGs and the whole local loop — U1C2 acceptance is the manager's visual/gameplay
   review, not this task.

## Remaining work / blockers

* Nothing else in this task's scope. U1C2 acceptance still requires the manager's real windowed run, PNG
  inspection and whole-loop review; no screenshot, input or gameplay behaviour has been verified here.
* Known limits to confirm on the first real run: `ScreenCapture.CaptureScreenshotAsTexture` support in this
  Player (the async fallback and on-disk verification cover the failure case), and the fact that the review
  mouse is `current` — moving the physical mouse during a session can hand pointer control back to the OS
  device (documented in `Docs/DEV_REVIEW.md`).
