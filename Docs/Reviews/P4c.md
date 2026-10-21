# P4c review: player-facing UI/readability fixes (uncommitted tree on HEAD e93566cb)

Independent read-only review. I read `git diff HEAD` (12 files), the untracked `Docs/Tasks/P4c.md`, `Docs/Reviews/P4a-windowed.md` and `AGENTS.md`, the `Logs/p4c_*` logs, and I opened the before/after PNGs myself. I did not run Unity, edit source or touch git.

## Verdict: ACCEPT-WITH-FOLLOWUPS, after 3 must-fix items

Most of the change is sound and the screenshots back it up: rarity colours, the Back slot label, opaque panel and tooltip, satchel closed on death, no stale "Satchel open" text, hold bar hidden until a hold starts, MP cost shown once, no "(hold)" twice, hotbar wrap, affix display names, layout at 720p/1080p, and the corridor sconce mounted on a wall. Combat, authority, extraction and the equip/transfer APIs are untouched. One inventory rule did change: right click with a selection pending (M1). The report also overclaims two things (M3): the "giant yellow quads" root cause, and the death evidence.

## Must fix before commit

**M1 (High, inventory behaviour change): right click with a selection pending now uses consumables and leaves stale UI.**
File: `Runtime/UI/InventoryScreenController.cs:558-569` (plus `:592-611`).
- The new branch takes the "wear or use" path whenever the pointer is over *any* bag item, including the selected stack itself. Then it falls through to `_inventory.TryUseSlot(index)` (`:610`).
- Scenario: left-click a Blood Draught to select it, change your mind, right-click the same cell. That is exactly the gesture the on-screen hint advertises (`:1447` "right click: let go"). The player drinks a draught and loses one from the stack (`Inventory.cs:1177,1195`). Before this change it cancelled.
- The same happens with a draught selected and a right click on another draught or arrow stack.
- Coins and other unusable items: the selection is dropped with `ClearSelection()` and no `Rebuild()`, and `TryUseSlot` returns false with a "cannot be used" toast. The highlight and the "Selected Ember Coin x8 ..." hint stay on screen with no selection behind them, until something else rebuilds.
- This contradicts both the task ("without changing gameplay logic") and the report ("selection is just a cursor mark ... spends nothing").
- Fix: take the override only when the item under the pointer `IsEquippable`, which is the P4a cloak case. Otherwise keep `CancelSelection()` (it rebuilds). Add a gate: with a draught selected, right-click the draught → stack count unchanged and selection cleared. The new right-click gate (`EmberdelveInventoryUiChecks.cs:883-910`) only covers gear with a *coin* selected, so it cannot catch this.

**M2 (Medium, evidence): the regression record does not match the tree being committed.**
- `Logs/p4c_exit.txt` still records `enemyvisual exit=1 FAIL`.
- Both `Runtime/Feedback/TorchFlicker.cs` and `Editor/EmberdelveRuntimeSmokeTest.cs` were modified at 05:56:02. That is after the whole batch (05:42-05:55) and after both review builds (`after` 05:32, `after2` 05:39).
- Only `enemyvisual` was re-run (log 05:56:36, `PASS checks=24 failures=0`).
- The smoke-test file holds every runtime flow (combat, death, extraction, resultloop, lobby, realcombat*, ai, archer). Strictly, 13 of the logged PASSes are for an older version of that file.
- Real risk is low: the sconce has no collider (builder destroys it, `EmberdelveLevelBuilder.cs:259-260`) and the light does not move. But AGENTS.md forbids claiming checks that were not run on this tree.
- Fix: re-run the batch once on the final tree and rewrite `p4c_exit.txt`. Minimum set if time-boxed: combat, ai, death, extraction, resultloop, realcombat, realcombatarcher, inventoryui, enemyvisual. Those load the level scene, so TorchFlicker.Start runs in them.

**M3 (Medium, report accuracy): correct `Docs/Tasks/P4c.md` before it is committed.**
- (a) Defect 9 root cause.
  - The sconce explains the small yellow bar over the enemy's head at distance (`p4c/before720/after_descend.png` vs `after720/hud.png`: bar gone, sconce on the right wall). Verified.
  - It does not explain the screen-filling quads in `p4a/21_pre_fight.png` / `22_swing.png`.
  - Sconces sit at 2.3-2.6 m (`DungeonLayout_AshenHalls.asset` torch heights). The eye is at 1.66 m (`EmberdelveLevelBuilder.cs:360`), so the camera cannot walk into one without jumping.
  - The quads' geometry matches the enemy at point-blank range. Lower quad: body top 1.675 m (`p4c_enemyvisual.log:996`), seen as a vertical face with its top edge at the horizon. Upper quad: head bottom 1.63 m, its lower edge just under the horizon. The radial hotspot fits the camera-mounted `Player_Torch` (intensity 9, 0.3 m from the camera, `EmberdelveLevelBuilder.cs:373-383`) blowing out a grey surface 0.2-0.4 m away.
  - No after-shot reproduces contact range. `melee1-7` all have the enemy about 2-3 m away at full player HP. The brighter `Enemy_Body` (0.23→0.40 plus emissive) would make a point-blank blow-out brighter, not darker.
  - Report this as: floating sconce fixed; screen-filling quads not reproduced, root cause unconfirmed (likely enemy at contact plus hand torch).
- (b) Defect 5 evidence.
  - The report says it "died twice with the satchel open (items in bag, loot value 274)". `after720/death_with_satchel_open.png` shows "Loot value: 0 Items: 0 Time: 00:26".
  - The satchel shots 25 s earlier (`inv_loot.png`, `inv_worn.png`) show an empty 0/40 bag, taken on the first build (`after/`, translucent panel), not `after2`.
  - The close-on-death behaviour itself is plausible and gated, but the windowed claim as written is not supported.
- (c) `r720/e_back_tooltip.png` is cited as the worn-slot tooltip beside the column. It actually shows the *selected bag item's* tooltip while the pointer is over an *empty* Back slot ("worn 0"). It proves the slot-hover positioning, not a worn-item tooltip. The report admits the latter was not photographed; drop the implication.
- (d) Name the remaining unfixed P4a text items instead of leaving them out:
  - decimal commas in the character pane and lobby ("regen 1,5/s", "4,39 m/s", "34,9", "4,3 m/s");
  - fixed attribute lines still lower-case ("+1 intelligence", `ItemTooltip.cs:215`), right next to "+0.09 Move speed";
  - the stale "Warrior" status label under the objective.

## Findings, ranked (beyond M1-M3)

1. **Medium: inventory refusal toasts are fully hidden at 720p.**
   - The toast sits at y 140 from the top (`HudController.cs:324-326`), on the same canvas as the satchel, which draws later. The opaque panel now spans y 79-509 at 720p.
   - Every refusal reason raised while the satchel is open is therefore invisible at 720p: "does not fit the Back slot", a swap refused, a full bag, "cannot be used" (`InventoryScreenController.cs:439,453,517,547,606`).
   - At 0.94 alpha it was already barely legible (`after720/inv_loot.png` shows a ghost of "Hit -15 HP"). Alpha 1.0 removes it entirely; the report counts this as a win. At 1080p the toast is above the panel and still visible.
   - Follow-up: move the toast above the panel, or into the panel's status band, while the satchel is open.

2. **Medium-low (TorchFlicker): wrong-surface and room-torch mounting.**
   File: `TorchFlicker.cs:56`.
   - The raycast uses `Physics.DefaultRaycastLayers` and excludes only `PlayerRig`. Any non-trigger collider closer than the wall (enemy capsule, chest, future props or doors) can capture the sconce.
   - At 2.3-2.6 m most current props are below the ray, so today's risk is low. Use the level layer (`LayerMask.GetMask(GameConfig.LevelLayerName)`, as `EmberdelveLevelBuilder.cs:392` does).
   - The 2.5→6 m range raise is legitimate: the first batch run failed because 3 room-centre torches found no wall within 2.5 m. But it moves the glowing sconce up to 6 m from its light, so a room is lit from a point with no visible source. This was never screenshotted (`after2` predates the change).
   - With no hit, the sconce silently stays floating. The gate catches that today.
   - The sconce ends up with a 3 cm gap from the wall: inset 0.12 m minus half-depth 0.09 m. Cosmetic.
   - Not affected: lighting (the light does not move, no GI or lightmaps, `LightShadows.None`), colliders, NavMesh (the sconce has no collider; it moves at runtime after any bake), Lobby torches (no "Sconce" child) and `Player_Torch` (`Find` returns null).

3. **Medium-low (tests): existing tooltip assertions no longer test what the player sees.**
   - `ToPlainText()` and `RolledLines` keep raw keys ("+4 armor", "+9 max_health"), while the screen renders `RolledDisplayLines` (`InventoryScreenController.cs:818-820`).
   - `CloakSlotGate.cs:584-624` and `InventoryUiChecks.cs:754-789` stay green but now assert a string that is never displayed.
   - The class doc (`ItemTooltip.cs:20`) still says the gate asserts on `ToPlainText` "without any UI", as if it were the screen text.
   - Nothing was weakened and keeping the canonical keys is reasonable. But add one gate that `_tipBody.text` (or a display-plain-text) contains `DisplayLine` for each rolled record, otherwise a display or roll mismatch goes undetected.

4. **Low-medium (death gate is mostly vacuous).** File: `EmberdelveRuntimeSmokeTest.cs:1342-1346,1382-1385`.
   - The close itself goes through the real path: `Health.Died → RunDirector.Complete → SetState(Dead) → RunStateChanged → Close(false)`. Good.
   - But the satchel is opened with an empty selection and no hover: in batch mode `HandlePointer` never builds a tooltip. So "tooltip and selection cleared" is true before death too.
   - The "click does nothing" check is tautological: `HandleClick` returns at `!IsOpen`.
   - `Open()` is called directly, bypassing `HudController.UpdateInventoryScreen` and `director.SetInventoryOpen`.
   - Extraction and lobby closes are not gated.
   - Strengthen it: place an item, select it, call `ShowTooltipAt` on it, then die. Assert bag contents and uids are unchanged after the post-death click.

5. **Low: `RunDirector.InventoryOpen` stays true after a death-close.** `Close(false)` does not call `SetInventoryOpen(false)` (`RunDirector.cs:119`). Nothing reads the flag today (grep), and the run scene is torn down. On the next run, `Open()`/`Close()` handle cursor lock again, time scale is untouched, and the director re-gates input. No restore problem found.

6. **Low (HUD cost label): fragile.** `HudController.cs:756` now shows `snapshot.Summary`, which formats the *base* `manaCost`/`cooldownSeconds` (`SkillData.cs:232`) with `F0`. Today `EffectiveManaCost == manaCost` (`SkillData.cs:183-184`), so it is correct now. It drifts once effective costs are modified, and sub-second cooldowns print as "0s"/"1s". Prefer building from `snapshot.ManaCost`/`EffectiveCooldown`.

7. **Low (Interactor):**
   - `BuildPrompt` runs every frame while a target is aimed at (`Interactor.cs:70`). It allocates a concatenated string per frame for hold or blocked targets and calls `CanInteract` per frame. It is small, but cache by (label, canInteract, reason) if the profiler shows it.
   - The `"hold"` substring test (`:129`) also suppresses the suffix for words like "Threshold" or "Stronghold".
   - `_runEnded` resets on Running and Paused, so "Descend again" without a scene reload still works.
   - The hold bar logic is correct for the extraction hold: `OnHold` toggles both bar halves, and completing the hold → `Complete(Extracted)` → `ClearTarget` happens inside `OnHoldComplete`, with no later `_current` dereference.

8. **Low (AffixVocabulary.DisplayLine):** an integral key with a value in (-0.5, 0) prints "-0 Strength". Unknown keys now render "Unknown effect" (the id is lost on screen, kept in `RolledLines`). Fine for now.

9. **Low (gate quality):**
   - The rarity gate hard-codes copies of the panel and tooltip colours (`InventoryUiChecks.cs:848-849`) instead of reading the live `Image.color`, so it can drift.
   - The thresholds are meaningful, not tuned:
     - Old Common was RGB distance 0.066 from body text and fails 0.2; new Common is 0.385 and Rare 0.60.
     - The 4.5:1 contrast check is WCAG AA. It would also have passed the old colours, so it guards readability rather than distinctness.
   - `enemyvisual`:
     - The sconce test (`CheckSphere` 0.3 m, `:1103`) is real: sconces at 2.3-2.6 m cannot touch floor or ceiling (3.6 m).
     - The body check (`:1115`) only checks a material constant (gamma-space luminance) for the regular enemy, not the elite. It is a regression guard, not a visibility measurement.
     - The telegraph "flat" check uses bounds height, not elevation. The log confirms y=0.07 (`p4c_enemyvisual.log:1156`); add `b.center.y < 0.3` to make it meaningful.
     - The `reach = 4f` bound is hard-coded, not read from data.

10. **Low (visual, not regressions, for later):**
    - The large orange slab at bottom centre of `after720/chest_opened.png`, `inv_loot.png` and every `r720/*` shot is the opened chest at the player's feet: the camera is adjacent and the hand torch is right on it. It was never the sconce and it is unchanged. The HUD prompt ("Chest (empty)  -  This chest is empty", also redundant wording) and the hotbar caption sit on it with very low contrast.
    - The tall red rectangle over Legs/Back in `r720/e_back_tooltip.png` / `b_worn_back_hover.png` is the **drag ghost** of the selected 2x3 Mantle (`CarryGhost`, sized from the shape, `:1494-1497`), centred on the pointer. It is not a layout defect. It hides the slot being aimed at and pokes about 9 px below the panel.
    - The truncated "Uncommon Emberthread Mantle [bc6e290d…" and "Selected … (Armour +1, Max…" lines in that shot are *occluded by the tooltip*, which now docks left of the column and so sits over the right half of the detail and hint bands. They are not overflowing: `a_loot_selected.png` shows both lines complete.

## What I could and could not confirm from the PNGs

Confirmed:
- Uncommon green in the title, rarity and rolled line, clearly distinct (`r720/a_loot_selected.png`).
- Opaque panel and tooltip in the `after2` shots.
- Back label "Back / Emberthread Mantle" and empty slots named Main hand / Off hand / Head (`c_worn_label.png`).
- Hint on one line idle, two lines with a selection, inside the panel, at 720p and 1080p (`inv_empty_1080.png`).
- Panel bottom (about y 509) clears the Dash card (y 514).
- Lobby cards end at y 586, above DESCEND at y 599 (`after720/lobby.png`); 1080p lobby fine.
- No "Satchel open".
- No idle hold bar (`after720/hud.png` vs `before720/after_descend.png`).
- "24 MP | 7s cooldown".
- "Hold to open chest" without "(hold)" (`inv_loot.png`).
- Hotbar "Emberthread / Mantle" wraps on a word.
- "+0.09 Move speed".
- Corridor sconce on the wall, with lighting visually identical before and after (`before720/after_descend.png` vs `after720/hud.png`).
- Enemy body grey-violet and readable at 2-5 m (`melee4/6/7`, `c1`).

Not confirmed:
- The screen-filling quads at contact range (not reproduced; see M3a).
- Room-centre sconces at the 6 m range (no build).
- Death with a loaded satchel (see M3b).
- Rare and Common colours in game.
- A worn-item hover tooltip.
- Right-click equip of cuirass, boots and blade with the real mouse.
- The tab round-trip tooltip (no gate, no visual).
- Extraction or lobby satchel close.

## Summary
Good, focused UI work with real gates and honest limits for most items. Before committing:
- (M1) restrict the new right-click-with-selection path to equippable items, so a "let go" right click can no longer drink a potion or leave a stale selection;
- (M2) re-run the regression batch on the final tree, since TorchFlicker and the smoke-test file changed after it;
- (M3) correct the report: the giant quads are not shown fixed and probably are not the sconce; the death screenshot had an empty bag.

Later: refusal toasts hidden by the opaque panel at 720p, level-layer mask for sconce mounting, a gate on the displayed tooltip text, and a stronger death gate.
