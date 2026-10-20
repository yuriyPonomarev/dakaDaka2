# U1B-R3 — corrected contradictory duplicate-definition assertion (partial: manager gates pending)

Changed file: Assets/Emberdelve/Editor/EmberdelveRuntimeSmokeTest.cs — one Check only (~line 2306, EquipmentTransferFlow section 3).
Old (wrong) assertion: `!seenAtNotification.Holds(cuirass)` — impossible after equipping the SECOND of two same-definition cuirasses, since the unselected copy is definition-equal and the next checks already require it at firstOrigin.
New assertion: snapshot nonnull AND `CountOf(cuirass) == 1` AND `StacksOf(cuirass) == 1` AND `OriginOf(cuirass) == firstOrigin` AND `OriginOf(cuirass) != secondOrigin`.
New message states: the selected bag footprint had already left the bag while the copy that was not selected remained as exactly one unit in one stack at its own cell, and notes both copies are one definition counted without UIDs until U2 (no UID-distinction claim).
Message prints actual units/stack/origin plus firstOrigin/secondOrigin for diagnosis.
Unchanged: all surrounding slot/filled-count/BagStacks/BagOrigins/BagCounts/BagCells/BagUnits/BagValue/MaxHealth/BlockReduction/notification-count checks and every other assertion; no helper, refactor or production change; CommitSnapshot APIs (CountOf/StacksOf/OriginOf) already exist in this file.
Checks actually run: none (file tools only). Compilation, smoke run (expect 400 checks / 0 FAIL / exit0) and visual review are pending manager execution.
Remaining work: manager re-run of the item suite; U2 per-item UID work still needed before copies of one definition can be distinguished per-instance.
