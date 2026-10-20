# U1C2A-R1 — missing LowLevel namespace (CS0246)
- Changed file: `Assets/Emberdelve/Runtime/Review/EmberdelveReviewBridge.cs` — header only.
- Fix: added `using UnityEngine.InputSystem.LowLevel;` after the existing `using UnityEngine.InputSystem;` inside the file-wide `#if DEVELOPMENT_BUILD || UNITY_EDITOR` block (same import set as `Editor/EmberdelveRuntimeSmokeTest.cs`).
- Resolves CS0246 for `KeyboardState` (now line 606) and `MouseState` (now line 636); `InputSystem.QueueStateEvent` calls, virtual-device setup, keyboard/mouse/capture paths and all diagnostics are untouched.
- No other source, scene, prefab, package or docs changes; no behaviour, refactor or test changes.
- Engine checks pending manager: compilation, BuildReview, then smoke/review-harness runs and log inspection.
