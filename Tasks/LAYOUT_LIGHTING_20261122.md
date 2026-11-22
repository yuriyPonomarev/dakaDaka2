# Layout-aware local lighting - 22 November 2026

Active slice after6f00729f8. Primary clean_handoff only. User requests visible furniture in dark side rooms, candles/torches fitted to layout, retained dark-fantasy atmosphere and additional Meshy search. Preserve geometry, furniture, collisions, doors, secret mechanics, global environment and bot-off settings. Owned: local-light authoring and its furnishing-builder hook,30saved module light additions, review helper and reports. No new paid generation.

# Local reference audit: chamber lighting, 22 November 2026

Read-only source inspection; no Unity, mirror, game files, reference assets or gameplay changes. No source assets were copied. The observations below concern serialized packages, not a rendered reconstruction.

## Exact source evidence

P = C:/Users/yrapa/Downloads/DarkAndDarker-extracted/pakchunk2802-Windows_0_P/DungeonCrawler/Content/DungeonCrawler/Maps/Dungeon/Modules/Crypt/

1. P/Center/FourRooms/Crypt_FourRooms_A.uexp contains six wall-candle actor labels: BP_CandleWall11 at 0x1d34, BP_CandleWall12 at 0x1e1a, BP_CandleWall13 at 0x1f00, BP_CandleWall14 at 0x1fe6, BP_CandleWall8 at 0x20cc, BP_CandleWall9 at 0x21b1. Offsets identify the first ASCII character. Each is null-terminated with matching preceding little-endian string length.
2. Its matching .uheader export map starts at byte 28848, with 1844-49, respectively, with common outer index 430 and subordinate component exports. Thus these wall-candle records are actual serialized actors rather than only asset references. Example: actor44 owns component exports109,128,181,189,195,203,222,254,255,268,392,411,441,502,528,553,1819-1822. Component values and transforms were not decoded.
3. The same FourRooms .uexp has 12 GameSpawner_Id_Spawner_New_Props_BoldTorch_02_On labels (first at0x2b74), 5 Roaster01On labels (first0x3c26), 2 Roaster02On labels (first0x3f36), 19 PointLight labels (first PointLight22 at0xb7d3) and4 SpotLight labels (first0xdaed). These are serialized labels, not counts of active lights. PointLight22 lies inside export483; its child export508 has name-map index291 and77 bytes. No interpretation of its numeric photometry was attempted.
4. P/Edge/Chapel/Crypt_Chapel_A.uexp has BP_Candle_A at0x3510 and BP_Candle_A5/6/2/3/4 at0x3602/0x36f5/0x37e8/0x38db/0x39ce, alongside TorchOn and Roaster spawner labels. This supports varied source types, not their exact spatial distribution.
5. P/Center/LargeRoomPit/Crypt_LargeRoomPit_A.uexp has BP_Candle_A6 at0x8d23 and seven further BP_Candle_A variants through0x93c7, plus BP_Candles02 at0x947e. TorchOn and Roaster01On spawner labels also occur.
6. C:/Users/yrapa/Downloads/DarkAndDarker-extracted/pakchunk1400-Windows_0_P/DungeonCrawler/Content/DungeonCrawler/Props/Common/LightSource/BP_Candle_A.uheader contains PointLight at0xdc3 and PointLight_GEN_VARIABLE at0x10f3. The corresponding BP_CandleWallOn.uheader in that same directory contains PointLight at0xda8 and PointLight_GEN_VARIABLE at0x10f0. These are blueprint name/import entries, not decoded instance values. BP_CandleWall.uheader exists in the equivalent pakchunk1400-Windows base directory and also contains PointLight entries.

## Safe conclusion and limits

The relevant reference room uses a mixture of instantiated wall candles and torch/roaster-labelled spawners; candle blueprints expose point-light components. The reference therefore supports visibly motivated local light sources, including candles in room furnishing. It does not establish candle-to-room assignments, source heights, light intensity, color, radius, shadows, runtime on/off state or exact placement. No numerical lighting values should be described as copied or matched to Dark and Darker from this audit.

## Original adaptation to our current modules

Our DungeonLayoutFurnishing.Table currently places a downloaded wooden_candlestick on the measured table top; that method adds no flame or Light. Use that authored support as a local light anchor and inspect the imported mesh for the actual wick position. Add a compact candle cluster at study/table/shrine furnishing, with visible flame/emission and a modest warm local light. Give an enclosed side chamber a wall-mounted torch only where its entrance or interior turn remains unreadable. Keep floor routes free and wall mounts off door swing, hidden panel and lever areas.

Maintain dark gaps between local light pools, readable furniture edges and visible entry landmarks; do not raise the entire scene ambient illumination to compensate. Candle illumination should remain subordinate to larger torch/brazier pools. Use separate candle-scale flame/flicker, not a torch-sized flame or large haze ball. All suggested placement and photometry are original choices to be tuned in native lighting, including views from both room entrance and interior and checking wall leakage/shadow acne.

## Meshy search

Connected tools expose own-task listing/generation, not public-gallery search; used Meshy public pages. Candidates (catalogue evidence, not accepted/imported meshes):
- Ornate Gothic Lantern by Illustros, page marked CC0: https://www.meshy.ai/zh/3d-models/Ornate-Gothic-Lantern-v2-019c6605-49c9-7e12-8948-491c2fc43780
- Medieval torch by Soloo, indexed page marked CC0: https://www.meshy.ai/3d-models/medieval-torch-v2-019449ca-6f56-715b-9acf-9310685359e9
- Fantasy candle by _CGD_, page marked CC0, stylized: https://www.meshy.ai/3d-models/a-fantasy-candle-low-poly-game-asset-v2-019659dd-b213-7e9a-b419-65a2332527b3
- Plain candle listing reports316654faces; too dense for repeated small lighting props without reviewed optimization: https://www.meshy.ai/posts/candle-01a19113-4eb8-7172-bc89-243fae8b36d0

Public page previews failed through the web fetcher; no visual/topology acceptance or downloaded-model claim. Existing Meshy WallTorch and VotiveStand plus PolyHaven tabletop candlesticks already match the room kit. Integrate those for this lighting pass; keep discovered candidates linked for future variety.

## Acceptance plan

Compile/bake saved visual-only light groups. Verify existing physical/gameplay component documents and transforms remain unchanged. Review native lighting from entrances and furniture inside all30prefabs, two live generated seeds with ordinary traversal and recorded video/audio; no diagnostic fill. Check readable furniture, lit wick/mount contact, smooth falloff, dark gaps, no obvious wall leakage/blowout. Exercise existing light interaction checks because added lights register as extinguishable sources. Select bounded checks after planner inspection; do not repeat unrelated networking/geometry suite solely for lighting.

## Initial saved implementation

DungeonLayoutLighting adds wick-mounted flames/lights to the existing tabletop candlesticks and fixed existing Meshy WallSconce models to actual chamber back walls and suitable nearby shelf bays. Each sconce uses a bounded warm spotlight with256px soft shadows, aimed inward/down, instead of an unoccluded fill crossing walls. Candle particles reuse the existing flame material at candle size, with gentle flicker and short3.6m range; an authored empty LocalLightHaze marker prevents the generic torch-sized scattering ball. Original source assets/materials remain untouched. No new colliders; fixtures are above standing clearance. Rebuilding furniture reapplies the lighting pass.

Planner selected71 gates conservatively for shared/new authoring inputs. Actual changes are visual-only additive sources: component-preservation.json proves every original document and original transform across all30prefabs preserved, except appended child lists. Bound checks to existing WorldLightInteractableTests and recorded native candle off/on plus room walks; geometry/network gameplay code is unchanged. No new test duplicating cosmetic placement. Broaden only if runtime evidence exposes a functional problem.

First focused run20261122-175721-219 executed zero tests because the requested fixture namespace was wrong (Emberdelve.Level.Tests instead of Emberdelve.Tests.Level). Harness correctly failed empty selection; no gameplay failure was reported. Corrected selection and reran the existing fixture without weakening assertions.

Corrected focused run20261122-180118-761:6/6 WorldLightInteractableTests passed,0failures,174.31s including Editor startup/import. Native seed67890 lighting-v1 follows with actual chamber-candle extinguish/relight via player authority and ordinary walking.

## Native v1 findings

Seed67890 lighting-v1 completed2770 reported/2769 encoded frames (1m32.30s),1280x720,120health,AI0. Both tabletop candles were actually extinguished/relit via player-authorized interaction. Ordinary chamber/dais walk and video motion samples reviewed; tabletops/legs/masonry became readable with dark corners preserved. Full video decoded with non-silent stereo48kHz AAC (mean-25.4dB,peak-12dB), delivered as intermediate evidence.

Two improvements follow: shelves still looked dark along the wall, so existing PolyHaven candlesticks are seated on actual top shelf boards by temporary mesh ray probes (removed before saving), not on outer bounds/side posts. Warm shelf pools use0.65intensity/2.8m range. Chapel side aisles receive supported wall torches on the partition faces. URP ignored Light.shadowCustomResolution by itself and chose its default High tier, causing shadow-atlas downsizing warnings; explicitly author UniversalAdditionalLightData Low tier (256px in existing PC profiles). No global shadow settings changed. These visual changes require final native review, not a replay of unchanged interaction rules.

Final source bake totals across30 catalog prefabs:60 tabletop candles,99 shelf candles,81 wall sconces;477 total point/spot sources including237 original sources. Maximum19 sources in one module (nine-room map upper bound171 plus scene sources), comfortably below existing512 network snapshot capacity. Final-component-preservation.json proves all original component documents/transforms retained after the shelf/URP changes.

Visual gate20261122-181156-553:216checks,0failures, passed. This also checks existing scene/environment/held/lobby lighting pipeline invariants; no global settings were changed. Final native lighting-v2 seed12345 begins from synchronized final sources; read-only code review found no interaction/extinction blocker. New sconce flame is25%smaller than original due nested scale; native wick/mount review will assess actual fit rather than assuming scale alone is a defect.


## Final point-source correction

Native v2 seed12345 completed7882 reported/7881 encoded frames,4m22.70s,120health,AI0. Candles made furniture readable, but the outward spot cone left the sconce body and backing wall almost black. V2 remains intermediate evidence, delivered to the user. Final sources are warm point lights (1.8intensity,6m range), with soft wall-occluding shadows and custom128px per cubemap face. Candles retain their small local pools. No global light/exposure change.

The first point bake stopped before saving any prefab because Unity rejects a pixel count through the shadowResolution C# enum setter. The corrected bake uses the same serialized m_Shadows.m_Resolution field as the installed official URP Light inspector (UniversalRenderPipelineLightUI.Drawers.cs455-459); URP runtime1744 reads that value when additionalLightsShadowResolutionTier is Custom. Corrected bake succeeded across30 prefabs. final-point-component-preservation.json verifies every original component and transform again. Repeat Visual only for the changed shadow configuration and final mirror synchronization; existing six light interaction tests remain applicable, with native off/on exercised again.

Final Visual run20261122-182559-515:216checks,0failures,112.7s elapsed. This run synchronized and verified the saved point/custom128 source version used by final native reviews.


## Final native catalogue proof

Seed12345 lighting-v3:7882reported/7881encodedframes,4m22.70s,1280x720,H264/stereo48kHzAAC,120health,AI0. Ordinary player walk into both Entry side chambers, candle off/on and dais up/down completed; subsequent labelled free-camera review covers all30 saved modules under native scene lighting. No diagnostic fill. Video fully decoded, audio mean-25.4dB/peak-12dB; delivered in this chat. No additional punctual shadow-atlas reduction warnings in this run. Root reviewed145 Ruins furnishing/chamber/mount/circulation frames, direct native closeups,15 walking samples and24 consecutive motion samples from2m40-42s. Crypt/Infernal reports are stored beside the evidence.

Final point sources reveal the sconce metal and backing masonry. Furniture remains readable locally, with dark circulation gaps and distant statuary silhouettes retained. No observed new floating candles, unsupported mounts, broad source blowout or torch leakage in reviewed views. This is scoped lighting acceptance, not a claim that every reference room has been reconstructed or every distant surface is lit.

Extraction shelf5 initially appears superimposed over an old brazier. Saved-transform audit proves perspective alignment: shelf X12.71828..13.28171; carved pedestal X14.4..15.6; bowl X14.49688..15.50312. Minimum actual gap1.118m. Camera looks along +X through the open shelf; no model intersection and no furniture move required.

Crypt final review:155 native views and96 temporal samples at12fps across four2s segments; no actionable lighting/contact defect. Infernal final review:155 native views and6 full-resolution checks, no blocking finding. Reports: Logs/acceptance/layout-lighting-20261122/crypt-native-v3-review.md and infernal-review/v3/review-v3.md. Final30 prefab hashes match both component-preservation evidence and the synchronized review mirror.


## Completion

Second final seed67890 lighting-final passed:2770 reported/2769 encoded frames,1m32.30s,1280x720,health120,AI0. Ordinary chamber/dais walk and actual candle extinguish/relight completed. Reviewed15 walking samples,24 chamber-motion samples and12 map/seam/door samples. No new source-contact or light-pop issue observed. Full video decoded with stereo48kHzAAC,mean-25.4dB,peak-12dB; recording delivered. Both final reviews use the same saved point-light assets and no lighting override. Owned review Editors exited and mirror lease released; primary Editor remains open.

Scoped acceptance complete: saved local illumination across30 modules, existing light interaction6/6 and final Visual216/0, native all-family review plus two generated seeds. All original physical/gameplay components and transforms remain unchanged. Only this lighting slice is committed; inherited15 material/model/settings edits and old stash are excluded. No push. Reload Play/start a new raid to load saved prefabs; bots remain disabled. Meshy candidates were researched and linked, not imported, generated or claimed game-ready. Broader repository readiness remains as recorded in earlier reports.
