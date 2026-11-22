# Weathered traversal surfaces

Original project-owned traversal boxes from the 30 saved room prefabs. No external game mesh is imported.

The committed dimensions manifest contains 2,802 surfaces grouped into 70 shared variants. Step edges use a 25 mm bevel; landings, parapets and retaining edges use 40 mm. Blender works in physical metres, then normalizes the render mesh back to the original unit box. Each shared mesh has 188 triangles. Existing transforms, materials, flat walk-plane height and box colliders remain intact.

Regenerate in the primary checkout with the user's Editor left in its current scene/state:

1. Unity CLI `script run --file Tools/Art/TraversalSurfaces.cs --entry TraversalSurfaces.Export --timeout 240`, explicit primary `--project-path`.
2. Blender4.3.2 `--background --factory-startup --python Tools/Art/BevelTraversal.py` from the project root.
3. Unity CLI `script run --file Tools/Art/TraversalSurfaces.cs --entry TraversalSurfaces.Import --timeout 240` against the same primary project.

The bridge calls the editor APIs directly so validation exceptions reach the CLI. Export/import load isolated prefab contents and save only selected assets. `.bevel.json` and `numeric-qa.json` are reproducible intermediates. Future room builders call `WeatheredTraversalSurfaces.Apply(root)` followed by `StairSurfaceSeparation.Apply(root)` before saving. Missing dimension variants fail closed. `WeatheredTraversalSurfaces.ValidateSaved` verifies original bevel quality plus fitted geometry; it is read-only and can inspect the test mirror.

Selection covers terrace/gallery stairs and decks, parapets, and the undercroft floor/retaining sides. Broad adjoining floor slabs, decorative floor wear, furnishing and cover remain outside this surface pass.

## Non-overlapping masonry joints

The original collision boxes intentionally overlap. Rounded edges alone do not remove overlapping visual faces: gallery top treads extend into their decks, and undercroft retaining walls duplicate the top and inner face of adjoining ground slabs.

`StairSurfaceSeparation` derives shared render meshes in `Assets/Emberdelve/Art/Generated/TraversalSeparation` from the original bevel meshes. It fits 2,400 treads into disjoint run intervals with 4 mm joints and at least 2 mm clearance to landings. Retaining render faces stop at room Y = -0.6 m, where the ground slab supplies the upper wall; their original full-height colliders remain intact. Tread top heights, object transforms, materials and gameplay components remain unchanged.

For existing saved prefabs, invoke `Emberdelve.EditorTools.StairSurfaceSeparation.RepairSaved()` in the primary Editor. It uses isolated prefab contents, preflights all 30 rooms, saves only affected derivatives/prefabs and validates the saved result. `ValidateSaved()` checks actual fitted mesh bounds, preserved tread heights and the floor/retaining boundary.

The standard bevel import now refreshes derivative assets while preserving their GUIDs, reapplies separation after assigning base bevels and validates the combined result. Dimension export resolves fitted derivatives back to their original bevels, so the 70-variant manifest remains reproducible. Do not save a room after calling the base `Apply` alone: that API intentionally assigns only the original bevels; the separation hook completes the visual fit.

## Layout families (22 November 2026)

After the physical layout redesign, the original gallery/undercroft bevel kit is used only by Hall 02 in each destination. Other families author shared rounded meshes through `DungeonLayoutFamilies` under `Art/Generated/LayoutFamilies`; their tread visual run is0.296m on0.300m pitch, with0.305m collision run. `StairSurfaceSeparation` dispatches validation to the appropriate family instead of requiring every room to contain the old gallery. ExportDimensions records zero selected legacy surfaces for new families; regenerate the manifest before intentionally rebuilding the legacy kit.


`DungeonPitJointBacking.Apply` runs last, after base bevels and tread separation. It widens only the lower-floor render mesh0.10m per X side/0.06m per Z side under adjoining masonry, and adds closed render-only backings beneath below-ground tread joints. This prevents paired convex bevels from exposing the background. Original walking heights, colliders and transforms are preserved. Base-bevel export/validation unwraps these floor derivatives to their original source; source dependency changes produce a new derivative. `Tools/Art/LayoutFamilies.cs` exposes PitJoints for the six saved affected prefabs. Do not omit this final hook when rebuilding traversal visuals.
