# Persistent quiet raids and softer traversal — 22 November 2026

## User request

Keep bots disabled in newly entered raids until requested back; soften all ascents/descents; inspect module construction using the local extracted Dark and Darker reference. Work only in clean_handoff. The reference findings are in [DARKER_MODULE_REFERENCE_20261122](DARKER_MODULE_REFERENCE_20261122.md); this slice reviews construction, without claiming decoded original dimensions or rebuilding the module graph.

## Implemented

`GameConfig.populateRaidWithAi` is saved false in the existing game config. New ordinary Solo/host raids use a nonempty admitted human cohort, without automatic rival bots. Initial authored monsters retain validated placement/data but stay inactive before runtime initialization; subsequent floors skip monster creation. Loot, chests, carried equipment and normal admission/map/landing checks remain. Client visibility remains server-controlled. No inactive bot roster, fake deaths, inventory reset or personal-save changes.

To restore normal population later, set this one asset field true before the next raid. New config instances still default true for compatibility. `EMBER_RAID_AI=on/off` is an explicit process-only override; test wrappers set on and restore the previous environment. Interactive reviews have no override and must prove the saved false setting. No static toggle cache survives a lobby/raid reload.

All30 saved module prefabs now use rounded traversal surfaces:2,802 render references,70 dimension variants. Existing stair noses use25mm bevels, decks/parapets/retaining edges40mm, each with3segments and188triangles. Geometry is made in physical dimensions and normalized back to the existing unit-box placement; weighted normals recover correctly under nonuniform scale. External bounds and horizontal tread cores stay exact. Shared variants total13,160triangles; instanced traversal surfaces526,776triangles versus33,624before, without added renderers. No frame-rate benchmark is claimed.

Independent saved-prefab comparison confirms only MeshFilter mesh references changed. All collider properties, transforms, materials and gameplay components remain byte-equivalent after newline normalization. This covers terrace/skybridge approaches, both gallery stairs and the undercroft descent/ascent in every theme. Decorative thin floor wear and broad adjoining floor slabs remain excluded to preserve their seams.

Reproduction: `SourceArt/Environment/WeatheredTraversal/README.md`. Future room builders apply the shared variants and fail on missing dimensions instead of reverting to sharp cubes. Existing source kit retained; no external game art imported.

## Reference findings

The local packages contain distinct bridge, pit, subdivided-room, corridor and tower families. Six inspected patched Crypt modules each contain eight serialized connector labels, with different secret-wall/lever populations. These counts do not establish active entrances, elevations or a traversable graph. Original metre dimensions and stair proportions remain unverified.

Our30 modules share the same west+3.9m gallery/east−1.5m undercroft circulation template despite different furnishing. The report proposes bounded original internal variants (divided hall, pit with bypass, connected raised route) preserving our four ground-level external ports. Those structural variants are review recommendations, not represented as implemented here.

## Verification and evidence

Checkpoint: `Logs/harness/before-traversal-20261122.json`. Planner selected71gates conservatively for shared editor/art/runtime inputs. Bounded selection: Level fixtures (movement, staged maps, traversal and new inspection lifecycle), NetMovement and NetFinal. Normal-AI process override validates compatibility; separate fresh-profile live runs validate saved AI-off behavior. No repeated full integration suite.

- Harness routing tests:22/22 pass.
- Primary compilation/import succeeded. Blender numeric QA:70variants, finite outward normals, original bounds, flat tread cores; Unity saved-mesh validation passes. Exact2802MeshFilter-only prefab diff recorded in `Logs/acceptance/soft-traversal-20261122/prefab-invariants.json`.
- Initial test run `Logs/runs/20261122-070923-532` failed compilation because bundled NUnit lacks NonParallelizableAttribute; removed unsupported attribute. No tests executed in that run. Its unlocked sentinel was preserved and released by an owned normal Unity recovery launch, exit0. No lock file was deleted.
- Main run `Logs/runs/20261122-071424-043`: Level EditMode 149/150 passed; the only failure was the new Host fixture opening a raid before installing its authoritative plan. Fixed the fixture to match normal host startup, without changing production code or reducing assertions. The original aggregate FAIL is retained.
- Focused correction run `Logs/acceptance/soft-traversal-20261122/inspection-fixture-final`: all four inspection cases pass (override lifecycle, Offline/Host three-floor population, inspection → normal → inspection scene reload). Combined evidence covers 146 existing Level cases and four new cases. No full suite rerun solely for a green aggregate.
- NetMovement 198/198 and NetFinal 172/172 pass in the main run, with normal AI explicitly enabled for those processes. No runtime changes followed these passes. The net-proof build cache declined storage because inputs changed during build; independent comparison of the 179 affected runtime/editor/assets between primary and mirror found zero mismatches. Cache storage is not acceptance evidence.
- Visible fresh-profile Solo raids, seeds 12345 and 67890: both PASS, 756 progressed frames, measured viewport 1280 × 720, local player height −1.47 to +3.93 m, health 120/120 and zero active rival bots/monsters before and after the route. The helper uses ordinary player movement and the saved setting, with no AI deactivation helper, teleport, invulnerability, lighting change or environment override. Gallery ascent/descent and both undercroft flights are traversed.
- Recordings: `Logs/acceptance/soft-traversal-20261122/live-12345-inspection/stairs-and-undercroft.mp4` and `live-67890-inspection/stairs-and-undercroft.mp4`. Both fully decoded: 755 encoded frames, approximately 25.17 s, stereo 48 kHz audio, mean −25.4 dB; peaks −12.8/−13.3 dB respectively. Both videos were attached in the current chat. Four saved views per seed and extracted video contact sheets were inspected. This is visual traversal and audio-track verification, not a new audio listening acceptance.
- Both owned review Editors exited normally and released the mirror lock; the user's primary Editor PID 8352 remains open. Final SHA-256 comparison includes the corrected fixture: 181 affected files, zero primary/mirror mismatches, `final-mirror-input-comparison.json`.
- Visual limits: close views show rounded stair noses and landing edges. Strong existing lighting contrast and visible dither during one moving gallery view remain in the recordings; no claim of a full lighting/material pass or frame-rate benchmark. All 30 prefab variants have numeric/serialized proof; the live routes sample two generated layouts rather than every room.

Neutral Blender before/after images and exact input hashes live in `Logs/acceptance/soft-traversal-20261122`. They show real gallery dimensions with identical camera/light/material; they are geometry close-ups, not substitutes for in-game validation.

Prior broad integration debt, old stash and unrelated material/Oasis/settings drift remain excluded. No user Editor closure, engine/package upgrade or push.
