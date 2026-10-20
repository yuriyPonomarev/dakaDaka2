# U1C2C-R2 — hotbar seeded when the raid becomes Running (code saved; checks pending)

**Changed (only runtime file):** `Assets/Emberdelve/Runtime/UI/HudController.cs` — one added `RebuildInventory();` at the end of the existing `OnRunState(RunState.Running)` block, after the live director/player seeding, plus a comment. No geometry/summary/transaction change.

**Root cause (verified by file reads):** `RunDirector.Start` runs `playerRig.ApplyClass` (→ `ClassLoadout.Apply` → `Inventory.ConfigureGrid`/`GiveStartingItems`, which raise `InventoryChanged`) *before* assigning `_inventory = playerRig.Inventory`, so those notifications hit `RebuildInventory`'s `inventory == null` return; an empty starting bag raises nothing at all, and `OnRunState(Running)` seeded health/weapon/mana/skills but never the bag. `Awake`→`BuildHotbar`→`RebuildInventory` runs in the Awake phase, before any `Start`, so it can only see a null bag.

**Behavior:** the strip is built once from the live bag at the first state where the director's bag exists (Running) — empty and populated starting bags, class retry/`RestartRun` (scene reload re-runs the path), and unpause (`TogglePause` re-raises Running). No Update polling, no gameplay calls, no synthetic `InventoryChanged`/`ItemCollected`; the existing `InventoryChanged`→`RebuildInventory` refresh and its destroy/rebuild + null guards are unchanged, so no duplicate children and R1 chip/summary semantics are intact.

**Checks actually run:** none (file tools only). Pending manager: compile, fresh Windows build, ordinary Lobby DESCEND at 1280x720 — expect four `InventoryRow/Slot1..4` chips (empty-labelled) plus the bag summary line first, then loot pickup, hotkeys 1-4, a populated starting bag, and no doubled chips within one frame.
