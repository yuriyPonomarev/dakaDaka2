# U1B-R2 — one-line value-type compilation repair

Changed file: Assets/Emberdelve/Editor/EmberdelveRuntimeSmokeTest.cs (CaptureCommit, line 2775 only).
Change: `snapshot.BlockReduction = rig.Defense != null ? rig.Defense.BlockReduction : 0f;` -> `snapshot.BlockReduction = rig.Defense.BlockReduction;`
Reason: PlayerRig.Defense is `DefenseProfile` (Runtime/Data/DefenseModel.cs:33 `public readonly struct`), so `!= null` was invalid CS0019; the struct is always present inside the existing `if (rig != null)` guard, so the 0f fallback was dead code and default-profile reduction is already 0.
No other edits: snapshot fields, all assertions, and production code untouched; no other `rig.Defense` null comparison exists in the harness.
Checks actually run: none (file tools only). Pending manager: Unity compilation (CS0019 gone), items-mode smoke run, no FAIL lines in smoke log.
Status: patch saved; not test-verified.
