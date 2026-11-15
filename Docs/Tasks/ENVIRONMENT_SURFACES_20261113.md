# Environment surface pass — 13 November 2026

User priority: bring all work into the main Unity project and improve poor textures. Royale ordinary final extraction acceptance remains open; this task does not close it.

Checkpoint: Logs/harness/before-environment-surfaces-20261113.json.
Hash comparison of2806 shared Assets/Emberdelve files: all shared non-meta content already identical. Copied166 texture importer metas from mirror (ambientCG2048, PolyHaven1024, trilinear/aniso8) and4 missing script metas. Exact list: Logs/acceptance/environment-surfaces-20261113/copied-from-mirror.json. No runtime code or source art overwritten by mirror.

Main Editor had an external-scene-change modal. User's transfer/update request authorizes reloading the current scene from its saved disk version; clicked Reload, retained same Emberdelve_Level01 scene, no Play/close. Pipeline responded afterwards. Asset refresh is importing accumulated changes; initial compiler diagnostics referenced absent cached new types despite files existing on disk. Verify final compiler state before claiming complete main integration.

Implementation under validation:
- DungeonWorldStone shader: world-space triplanar albedo/normal/metallic-smoothness/AO, signed surface-slope normal blend, URP lighting/shadows/fog/depth normals. Static opaque rooms only;12 texture samples forward.
- DungeonSurfaceMaterials creates6 new materials under Art/Generated/DungeonSurfaces and replaces matching room renderer material references only. No collision, transforms, sockets, navigation or gameplay changes. Existing Art/Materials assets retained.
- DungeonModuleContentBuilder calls the same material binding for future authoring.
- Reuses existing licensed ambientCG source maps, no extracted game art or new download.

Required evidence: successful import/build, no shader errors, actual close/distant surface screenshots across floors and two layouts, main assets/import compilation confirmation. No new gameplay unit tests for cosmetic changes; planner and focused shader/render proof required. Work remains uncommitted until visual inspection.

Material generation on mirror32748 completed22103 terminal0;30 prefab changes verified by removing only YAML m_Materials blocks and comparing all remaining content byte-for-byte (normalized line endings). Copyback includes6 materials/metas and foldermeta. Main Editor refreshed, compilationFailed=false, shader messages empty and new material shader resolves (main-import-check.json). Initial stale compiler diagnostics resolved by refreshing the asset database; no code workaround added.
Planner conservatively selects71 gates for shared editor/shader files; narrowed to build plus actual rendered surface inspection because prefab invariants prove geometry/gameplay unchanged and no new rules exist. Active build1587/run20261113-133909-561.

Build133909-561 PASS1587 terminal0. Render21405 captured allthreefloors and both descents; helper then failed on file-sharing collision writing final request4 (after bot final exit). Allthreeimages inspected: shader renders correctly, no pink materials, consistent wall mortar; paving overly small/noisy. Corrected only paving worldtiling .4->.14 (period7.14m) in factory and two saved materials via mainEditor, retaining shader code and all room geometry. Rebuild35642; fresh helper adds bounded retry for its request file lock (no production/gameplay change).

Final evidence: build20261113-134339-678 PASS (run.json,60.1s, shell35642 terminal0). Final render helper review-final.ps1 produced floor1/floor2/floor3/extracted images under visible-final/review/snapshots. Host and client confirm Extracted; both owned players exited. Helper terminal output was not retained across context recovery, so completion is evidenced by images and log events, not a claimed collected exit code. No Exception:, shader errors or C# errors in final player logs.
All three final floor images visually inspected: corrected paving scale on ruins, material detail responds to warm/cool lighting, no pink shader fallback. Crypt/infernal lighting remains very dark; plain structural supports and broader art/lighting polish remain future work. This accepts the first stone-surface pass, not finished environment art. Main final check: compiling=false, compilationFailed=false, shaderErrors=[], paving tiling=.14 (main-final-check.json).
No gameplay suites repeated for this material-only pass. No commit: changed prefabs/editor builder overlap inherited uncommitted Royale work. Ordinary full-match Royale extraction acceptance remains open; this accelerated controlled route does not close it.
