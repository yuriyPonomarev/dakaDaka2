# Development review loop (U1C2A)

Two small development-only tools exist so a reviewer can look at the **actual rendered game** and drive
its **normal input paths**. Neither is player UX, neither is part of a release build, and neither changes
gameplay code, scenes or data.

| tool | file | active in |
| --- | --- | --- |
| Windows review build | `Assets/Emberdelve/Editor/EmberdelveWindowsBuild.cs` | Editor only (headless entrypoint) |
| Review bridge | `Assets/Emberdelve/Runtime/Review/EmberdelveReviewBridge.cs` | `DEVELOPMENT_BUILD` or `UNITY_EDITOR`, **only** with `--ember-review-dir` |

Without `--ember-review-dir` the bridge is never created: no host GameObject, no `runInBackground` change,
no virtual devices, no file access. Normal game behaviour is byte-for-byte unchanged.

---

## 1. Building the Windows development Player

Entry point (Editor assembly, synchronous, headless):

```
Emberdelve.EditorTools.EmberdelveWindowsBuild.BuildReview()
```

Full command (the manager supplies a **fresh output directory outside the project source**):

```bash
"C:/Program Files/Unity/Hub/Editor/6000.2.5f1/Editor/Unity.exe" \
  -batchmode -nographics -quit -projectPath . \
  -logFile Logs/u1c2_build.log \
  -executeMethod Emberdelve.EditorTools.EmberdelveWindowsBuild.BuildReview \
  --ember-build-output "C:/temp/emberdelve/u1c2/EmberdelveReview.exe"
```

Behaviour and guarantees:

* `--ember-build-output` (also accepted as `--ember-build-output=<path>`) must be an **absolute** path
  ending in `.exe` and must **not** be inside `Assets/`. The parent directory is created.
* Scenes are the saved ones, in load order: `EmberdelvePaths.LobbyScenePath` (`Assets/Emberdelve/Scenes/Lobby.unity`)
  then `EmberdelvePaths.DungeonScenePath` (`Assets/Emberdelve/Scenes/Emberdelve_Level01.unity`).
* `BuildTarget.StandaloneWindows64`, `BuildOptions.Development`. No other build options.
* It does **not** launch the Player, does **not** rebuild data assets/scenes/input assets/packages, does
  **not** include the template Cockpit scenes, shows **no** dialog and changes **no** project setting.
* Success requires `report.summary.result == BuildResult.Succeeded` **and** the output file existing.
  Only then is `[Emberdelve][BuildWindows] PASS ...` logged followed by `EditorApplication.Exit(0)`.
  Anything else logs `[Emberdelve][BuildWindows] FAIL ...` and exits 1. Build warnings/errors produced by
  the engine stay in the log; the result line reports `result`, `totalSize`, `totalTime`, `exeBytes` and
  whether the `<name>_Data` folder exists.

---

## 2. Launching the Player for review

```bash
"C:/temp/emberdelve/u1c2/EmberdelveReview.exe" \
  --ember-review-dir "C:/temp/emberdelve/u1c2/review" \
  -screen-fullscreen 0 -screen-width 1280 -screen-height 720
```

* `--ember-review-dir` must be an **absolute** directory. The bridge creates
  `<dir>/snapshots/`, reads `<dir>/command.json`, writes `<dir>/ack.json` and `<dir>/ready.json`.
* `-screen-fullscreen 0 -screen-width 1280 -screen-height 720` are the standard Unity standalone Player
  display arguments; the review bridge never changes resolution or any other player setting.
* The bridge sets `Application.runInBackground = true` **for that session only**, so the window keeps
  ticking when it is not focused.
* If the directory is unusable the bridge logs `[Emberdelve][Review] FAIL ...` and stays inert.

Startup output: after two real frames the bridge writes `ready.json` (id `0`, `action:"ready"`) and then
takes one automatic first snapshot `snapshots/000_first_<scene>.png`, answered in `ack.json` with id `0`.
That automatic snapshot is a diagnostic frame, **not** a claim that anything in the game works.

---

## 3. Command protocol

The reviewer replaces `command.json` with **exactly one JSON object** and increases `id` for every new
command (`1, 2, 3, ...`). Write it atomically (write a temp file, then move/replace it onto
`command.json`); the bridge opens the file with `FileShare.ReadWrite`.

The bridge notices the change by file timestamp+length, executes the command over real frames, and writes
one `ack.json`. It processes **one command at a time** and never re-executes an id: an id that is `<=` the
last processed id is rejected as `stale or duplicate id`.

### Command fields (all optional except `id` and `action`)

| field | type | meaning |
| --- | --- | --- |
| `id` | int | strictly increasing positive integer |
| `action` | string | `wait` \| `key` \| `mouse` \| `snapshot` \| `quit` |
| `frames` | int | `wait`: frames to advance (default 60, cap 900). `key`/`mouse`: hold frames (default 3, cap 120) — ignored by `key`/`mouse` when `seconds` is positive |
| `seconds` | float | optional **real-time** hold for `key`/`mouse`, in seconds of unscaled real time (cap 10). Omitted/0 keeps the `frames` behaviour; negative, NaN or infinite is a failed ack |
| `key` | string | key name(s), comma separated (`"w"`, `"w,leftShift"`) |
| `keys` | string[] | alternative to `key` (`["w","leftShift"]`) |
| `x`, `y` | float | pointer position in **screen pixels, origin at the BOTTOM-LEFT** of the game window |
| `dx`, `dy` | float | optional pointer delta sent with the first (moving) state event |
| `button` | string | `left` (default) or `right` |
| `name` | string | optional snapshot file name; must be a bare `*.png` name that stays inside `snapshots/` |

Key names are `UnityEngine.InputSystem.Key` values, case-insensitive (`w`, `a`, `s`, `d`, `e`, `f`, `g`,
`q`, `r`, `i`, `space`, `enter`, `escape`, `tab`, `leftShift`, `rightShift`, `leftCtrl`, `upArrow`,
`digit1`...). Convenience aliases: `shift`, `ctrl`/`control`, `alt`, `esc`, `enter`, `up`, `down`,
`left`, `right`, `0`–`9`. Unknown names produce a **failed ack**, not a partial press.

### Examples

```json
{ "id": 1, "action": "wait", "frames": 30 }
```

```json
{ "id": 2, "action": "key", "key": "w", "frames": 40 }
```

```json
{ "id": 3, "action": "key", "keys": ["leftShift", "w"], "frames": 30 }
```

```json
{ "id": 31, "action": "key", "key": "w", "seconds": 2.5 }
```

```json
{ "id": 32, "action": "key", "key": "e", "seconds": 3.0, "frames": 40 }
```

The last two hold by **wall clock**: `seconds` is measured with `Time.realtimeSinceStartup` (unscaled real
time) while the coroutine yields ordinary frames, so a 3 s hold is 3 s of gameplay whether the Player runs at
30 FPS or at 1000 FPS. `frames` is still accepted and is what actually happens when `seconds` is omitted or 0;
when `seconds` is positive it is ignored. Values above 10 s are clamped to 10 s and the ack says so.

```json
{ "id": 4, "action": "mouse", "x": 640, "y": 360, "button": "left", "frames": 4 }
```

```json
{ "id": 33, "action": "mouse", "x": 640, "y": 360, "button": "left", "seconds": 0.25 }
```

```json
{ "id": 5, "action": "snapshot", "name": "lobby_after_descend_hover.png" }
```

```json
{ "id": 6, "action": "quit" }
```

### Ack (`ack.json`, overwritten once per processed id)

```json
{
  "id": 4,
  "ok": true,
  "action": "mouse",
  "message": "pointer moved to (640,360) delta(0,0) and held left for 4 frames then released",
  "error": "",
  "screenshot": "",
  "scene": "Lobby",
  "frameCount": 214,
  "realtime": 12.84,
  "time": 12.84,
  "width": 1280,
  "height": 720,
  "reviewDir": "C:\\temp\\emberdelve\\u1c2\\review",
  "utc": "2026-10-19T03:41:07.1120000Z"
}
```

`frameCount` (`Time.frameCount`), `realtime` (`Time.realtimeSinceStartup`), `scene` and `width`/`height`
(actual `Screen.width/height`, or the PNG's own dimensions for a snapshot) are included in **every** ack,
including failed ones, so a frozen or wrong-size frame cannot be mistaken for running gameplay. A failed
command answers with `ok:false` and the reason in both `message` and `error`; it never shuts the game down.
A malformed `command.json` answers with `id:-1, action:"read", ok:false`.

Poll `ack.json` until `ack.id` equals the id you wrote (or `-1` for a read error). `ready.json` has the
same shape with `id:0, action:"ready"`.

### Action semantics

* `wait` — advances frames naturally; nothing is invoked.
* `key` — queues a `KeyboardState` on a dedicated virtual keyboard (`EmberdelveReviewKeyboard`), holds it
  for `frames` real frames (or for `seconds` of real time), then queues the release and waits one more frame
  so the release is consumed by the ordinary input update. `WasPressedThisFrame` therefore becomes visible on
  the following frame. A positive `seconds` always holds at least one frame, so press and release can never
  land in the same input update.
* `mouse` — queues `MouseState` on a dedicated virtual mouse (`EmberdelveReviewMouse`): first a **move**
  (with optional `dx/dy`), one frame later the **button press**, then the **release** after `frames` frames
  (or `seconds` of real time). The uGUI `EventSystem`/`InputSystemUIInputModule` and the game's own
  `InputActionAsset` consume those events in their normal frame update. No `Button.onClick.Invoke`, no direct
  inventory calls, and the bottom-left pixel coordinate rule is unchanged.
* `snapshot` — waits for a real rendered frame (`WaitForEndOfFrame`), captures with
  `ScreenCapture.CaptureScreenshotAsTexture` (fallback: `ScreenCapture.CaptureScreenshot`), writes the PNG
  under `snapshots/`, then **verifies the file exists** and reads its real width/height from the PNG header
  before reporting success. Up to 3 capture attempts, then a failed ack.
* `quit` — releases held input, flushes the ack, waits one frame, then `Application.Quit()`. In Editor play
  mode `Application.Quit` does not exit play mode; stop play mode manually.

### Limits and safety

* Frame caps: `wait` ≤ 900, `key`/`mouse` hold ≤ 120, real-time hold ≤ 10 s, snapshot ≤ 3 attempts + 60
  verification frames. Oversized requests are clamped and the ack says so. A `key`/`mouse` ack that used
  `seconds` reports the duration that actually elapsed and how many frames that was, e.g.
  `held W for 3.004 s of real time (2981 frames) then released` — the frame count is the evidence that the
  Player really advanced, not that time was faked. A real-time hold never touches `Time.timeScale`,
  `Time.fixedDeltaTime` or `Application.targetFrameRate`.
* A command whose coroutine has not finished within **45 s of real time** is aborted by the bridge: the
  coroutine is stopped, held input is released and a failed ack (`ok:false`, timeout reason) is written so
  the session never stays stuck.
* Held keys/buttons are released after every input action, on `OnDisable`, `OnApplicationQuit` and
  `OnDestroy`; only the two virtual devices this bridge created are removed. OS devices are never modified.
* No network, no server, no shell/process control, no paid services, no runtime editor API, no gameplay
  method or reflection is called, and `InputSystem.Update()` is never invoked by hand.
* The virtual keyboard/mouse are made `current` before each input action, because `Mouse.current` is what
  the inventory pointer and the UI module follow. **Do not move the physical mouse during a review
  session** — a real pointer event can make the OS mouse current again and the next virtual click would then
  land on a different pointer.
* In the dungeon the game locks the hardware cursor; that does not affect the virtual pointer position.
  To click dungeon UI, first open the inventory with `key` (`tab` or `i`), which unlocks the cursor.

### Manager-verified telemetry note
Key acks include per-command read-only movement telemetry sampled during hold. Inspect numeric JSON fields (`maxMoveMagnitude`, `maxIntentMagnitude`, positions/displacement, velocity, minMoveDistance, deltaTime and device-pressed samples); valid gameplay samples require `action=key`, `framesSampled>0` and `rigFound=true`. JsonUtility may serialize an empty inline telemetry object for non-key acks instead of null; do not interpret those zeros as a player measurement. Ack prose uses current decimal locale; JSON numbers are authoritative.
