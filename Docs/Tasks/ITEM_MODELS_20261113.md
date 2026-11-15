# Item armor models — 13 November 2026

Active user request: create item models, starting with armor. This slice supplies original meshes for the five existing armor definitions: boiled cuirass, graspwrap gloves, footpad boots, ashen cloak and emberthread mantle. Saved geometry/material assets, model-derived inventory icons and visible ground loot; wearing updates avatar/portrait, first-person gloves and observer cosmetics. No new item statistics, loot tables or paid/extracted assets.

Checkpoint: Logs/harness/before-item-models-20261113.json. Main source edits preserve prior work; main Editor stays open/no Play. Root sole mirror executor. Prior physical-belt demonstration player31844 closed through review bridge before this slice's testing.

First five-item slice implemented and visually reviewed. New ItemVisualCatalog is cosmetic only; saved worn meshes attach to existing humanoid bones. Owner equipment remains server controlled; observers receive only six cosmetic armor IDs (protocol41). Missing assets preserve existing fallback visuals.

Acceptance required: mesh/material import clean, recognizable item close-ups, no obvious wearer clipping in idle/walk/guard on both classes, first-person gloves, matching inventory/groundloot presentation and observer equip/remove. Focused wire tests, existing Visual/Hands proof and relevant network scenario; inspect planner before execution. The targeted acceptance below is now complete; broader catalog/cloth/performance work remains separate.

## Authoring and initial review
Graphics mirror generation v5 PASS: five entries, 40,595 total world-model triangles. Breakdown: cuirass4,572; gloves25,883; boots3,384; cloak3,168; mantle3,588. Six original textured URP materials. Glove topology/weights derive from existing CC0 Innerscene Human; other item geometry is original. Provenance in ASSET_PLAN.

Review corrections: separate boot shoes/calf pieces follow their respective bones; collar covers the old body material boundary; glove pair changed from32 rigid pieces to one skinned renderer/two materials after observed palm clipping. Final screenshots under Logs/acceptance/item-models-20261113/final-model-review include both classes in Idle/Walk/Block, close-ups, first-person rest/guard. No obvious bare-palm protrusion remains in the reviewed poses. Cloaks remain rigid bone-attached apparel, without cloth simulation.

Generated final assets and catalog copied to main. Main recompile completed with no errors. Earlier generation v2 failed on preview scene setup; v4 exceeded the initial glove mesh budget; both retained in logs, followed by corrected v5. Glove-specific27k cap retains the existing detailed hand deformation, other item18k guard unchanged. No 16-player performance acceptance claimed.

Planner selected all71 gates conservatively for231 changed/new files. Bounded selection: Visual and Hands for avatar/arms; NetCombat, NetInventory and NetFinal for changed observer wire/shared player presentation and pickup representation; AvatarArmorWireTests and Visual EditMode fixtures. No inventory ownership, corpse flow, extraction or Royale logic changes, so their previously passing suites are not repeated. Visual216/0 PASS in Logs/runs/20261113-155802-342. Remaining proof/build/runtime review in progress.

Integration run Logs/runs/20261113-155840-925: Hands92/0, NetCombat397/0, NetInventory148/0, NetFinal172/0, playable Build PASS. Follow-up after this source sync: armor-only ground visual uses a scale-cancelling parent and a front-up resting rotation; it does not change the pickup collider/identity/authority. A fresh build and actual drop/recollect review validate that cosmetic follow-up; no automatic rerun of unrelated green gates.

Focused EditMode proof: AvatarArmorWireTests9/9 and Visual186/186 PASS; summaries in acceptance/proof-AvatarArmorWireTests and proof-VisualTests. No full-suite result inferred from these fixtures.

## Final runtime review
Final playable: Builds/Playable/20261113-161307-136/EmberdelveNet.exe, PASS in the corresponding Logs/runs folder. Previous160722-159 build added the ground orientation wrapper; final161307-136 incorporates corrected icon assets. No runtime gameplay changes between these two builds.

Live icon defect corrected: PreviewRenderUtility allocated a693x693 linear target for the requested512 preview. Reading only512 cropped the image and writing linear RGB directly darkened it. Bake now reads the full target and converts linear RGB to display RGB while preserving alpha. Five corrected icons copied to main, imported at max512. Evidence asset-generation-icons-v6.log; final-bag.png shows the complete readable silhouettes.

Actual two-client review on seed1344668317, isolated explicit development fixture; evidence Logs/acceptance/item-models-20261113/final-seed-1344668317. Five items were minted into owner contest-a's bag, not automatically equipped; subsequent changes used ordinary review keyboard/mouse input and server inventory commands. Both clients rendered with graphics (the initial hidden observer window produced black screenshots, so it was restarted visibly).
- RMB equip cuirass/gloves/boots/cloak; owner portrait matches equipped items. Final icon UI: owner/snapshots/final-worn.png.
- Replace ashen cloak with emberthread mantle; owner portrait and observer update. observer/player.log records all six cosmetic slots and the back-slot change.
- Unequip/re-equip cuirass restores the original class chest then the new model on observer: observer-chest-removed.png / observer-chest-restored.png.
- Shift+RMB drops ashen cloak; owner/snapshots/cloak-ground.png shows the recognizable full-size model with pickup prompt. F recollect accepted by server with original UID bdb1e31ec0ad40e5b31c5bf147abf36e; bag screenshot cloak-recollected.png. Collider/authority behavior preserved.
- First-person glove guard reviewed in owner/snapshots/live-glove-guard.png. Both classes' Idle/Walk/Block previews are retained in final-model-review; actual server-replicated movement in observer/snapshots/observer-moving-armor.png, owner walk-telemetry.json records0.8324m/138sampledframes/movingObserved=true.

Known unrelated blocker to claiming a fully clean engine session: pre-existing native ThreadsafeLinearAllocator::Deallocate invalid-pointer error recurred once in the owner log and opened the Development Console; input/rendering/network review continued. It predates this slice, but its cause is still undiagnosed, not asserted unrelated by proof. User deferred that investigation; no Unity/package upgrade or memory fix included.

This is a first set for the five existing armor definitions, not a full equipment catalogue. Existing helmet/trouser costume remains; no new helmet/leg items, cloth physics or16-player performance claim. Final code/assets are in main; all test players closed, main Editor remains open without Play. Work remains uncommitted in the inherited mixed tree; no prior task changes bundled into a new commit.
