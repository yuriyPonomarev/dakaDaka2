# U1C2E-R1 — two compile corrections (partial: compile-only)

- Changed file: `Assets/Emberdelve/Runtime/Review/EmberdelveReviewBridge.cs` (only file touched).
- Line 273: `_rig.activeInHierarchy` -> `_rig.gameObject.activeInHierarchy` (CS1061; `activeInHierarchy` belongs to GameObject, `PlayerRig` is a MonoBehaviour).
- Line 439 removed: `if ((flags & (int)CollisionFlags.CollidedSteps) != 0) builder.Append("steps|");` (CS0117; no such enum member). No replacement flag or custom mapping invented.
- Unchanged: above/below/sides decoding plus numeric `flags.ToString()` fallback (a steps-only flag value now decodes numerically), all telemetry fields, input loops, caps, cleanup/protocol.
- Checks actually run by me: none (file tools only). Pending manager: recompile, then actual W/D movement trace; cause of no actual movement still unknown and unrepaired.
