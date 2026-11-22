# Weathered wall edges — 22 November 2026

## Request and scope

The user found wall models too sharp. Work stays in `DakaDaka_clean_handoff`; legacy assets are read-only. Existing stone sources are reused. During review the user explicitly requested temporarily disabling bots so the environment could be inspected without combat. The current primary Play session had21 AI actors deactivated through the inspection helper; no spawn configuration or persistent saves were changed. Fresh mirror reviews disable adventurer bots and monsters and state that limitation explicitly.

## Changes

- Six visual-only WeatheredMasonry derivatives of the authored FoldedMasonry profiles:3.5cm, three-segment bevels, coincident internal end faces removed before welding, weighted normals. Broad planes and broken crown silhouettes stay intact. Original six meshes remain collision sources.
- Nine outdoor module prefabs replace exactly90 MeshFilter mesh references. Independent comparison confirms every other serialized property is unchanged: colliders, transforms, navigation, doors, stairs, secret switches and room extents.
- Meshes have6880/7384/7228 triangles per profile pair, versus612/636/624 original collision triangles. No additional renderers/materials. The added geometry is shared; no room-wide Meshy rebake. Max22,152vertices remains within16-bit indices. Long wall, raised wall and compressed corner placements receive outward-facing corner normals.
- Source exports and reproducible Blender4.3.2 recipe: `SourceArt/Environment/WeatheredWalls`, `Tools/Art/BevelWeatheredWalls.py`, `Tools/Art/WeatheredWalls.cs`. Derived mesh names store a source geometry fingerprint. A future builder falls back to matching original geometry if profiles change until derivatives are regenerated.
- Existing Meshy-derived OvergrownArchStonePier/Crown retain exact vertex positions, indices, UVs, GUIDs and682/3760triangle budgets. Reconstructed angle-weighted normals repair smoothing after clustering. Backwards corners fall159→0 and492→0; ImportedStone smoothness.46→.25 and bump.82→.55. Source downloads, original OvergrownArch and other prop LODs are unchanged.

## Verification

Checkpoint: `Logs/harness/before-soft-walls-20261122.json`. Planner conservatively selects71gates for unknown editor/art inputs. Reviewed narrower cosmetic slice: no movement/collision/gameplay/network changes, so no network suite or full integration rerun. Primary compilation and focused imports succeeded; primary Play state/scene were not changed by automation.

`DungeonModulePlannerTests`: NUnit21/21 passed,0failures/0skips, clean batch exit. The wrapper summary saysFAIL because the command mistakenly supplied minimum146 (the previous broader module count). Raw XML confirms the complete selected fixture passed. This is a runner expectation mismatch, not146 executed tests. Evidence preserved unchanged in `Logs/acceptance/soft-walls-20261122/editmode`; no redundant rerun for a green aggregate.

An intermediate seed12345 review (before the final compressed-corner normal guard and bot disable) recorded300frames,1280×720,80rendered walls with80original colliders,14.46m movement. MP4:9.97s,30fps,stereo48kHz,mean−25.9dBFS. The player survived but combat obscured inspection; delivered as intermediate evidence, not final acceptance.

Final no-bot reviews: seeds12345 and67890 each pass300frames at1280×720,80softened wall renderers/80original colliders,14.46m walking and21disabled AI actors. Both MP4s are9.97s/30fps with stereo48k audio; complete FFmpeg decode succeeds (means−25.0/−25.2dBFS). Captured frames inspected: no combat overlay, player remains120/120, wall/arch surfaces visible with ordinary lighting. Videos delivered in-chat. Paths: `live-12345-no-bots` and `live-67890-no-bots` under the evidence root. Both owned mirror Editors exited gracefully and released their locks; primary Editor8352 remains open. Independent6meshes×8placement-scale audit finds0backwards normals, minimumdot≈.10025; topology has0open/nonmanifoldedges/degeneratefaces.

Final Unity source/asset byte comparison against the reviewed mirror passes. The last importer change adds source-export freshness validation; executing it in the primary Editor succeeded and did not change the reviewed asset bytes.

Independent topology/placement reports and neutral Blender close-up comparison live under `Logs/acceptance/soft-walls-20261122`. The grey comparison uses identical camera/material/light and actual1.6,1,.25 placement; it demonstrates bevel geometry, not in-game material acceptance. Final in-game videos retain ordinary camera/HUD/lighting, with AI disabled at the user's request and fresh isolated profile directories. No personal save used.

## Limits

This is a visual surface pass, not a redesign of module topology. Compression at diagonal corners makes the bevel narrower in X; source scale and footprint intentionally remain unchanged. Cosmetic geometry increases GPU triangle work; no performance benchmark is claimed. Prior broad integration failures and inherited material/Oasis/settings drift remain excluded. No engine/package upgrades or push.
