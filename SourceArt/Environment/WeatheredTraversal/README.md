# Weathered traversal surfaces

Original project-owned traversal boxes from the 30 saved room prefabs. No external game mesh is imported.

The committed dimensions manifest contains 2,802 surfaces grouped into 70 shared variants. Step edges use a 25 mm bevel; landings, parapets and retaining edges use 40 mm. Blender works in physical metres, then normalizes the render mesh back to the original unit box. Each shared mesh has 188 triangles. Existing transforms, materials, flat walk-plane height and box colliders remain intact.

Regenerate in the primary checkout with the user's Editor left in its current scene/state:

1. Unity CLI `script run --file Tools/Art/TraversalSurfaces.cs --entry TraversalSurfaces.Export --timeout 240`, explicit primary `--project-path`.
2. Blender4.3.2 `--background --factory-startup --python Tools/Art/BevelTraversal.py` from the project root.
3. Unity CLI `script run --file Tools/Art/TraversalSurfaces.cs --entry TraversalSurfaces.Import --timeout 240` against the same primary project.

The bridge calls the editor APIs directly so validation exceptions reach the CLI. Export/import load isolated prefab contents and save only selected assets. `.bevel.json` and `numeric-qa.json` are reproducible intermediates. Future room builders call WeatheredTraversalSurfaces.Apply and refuse missing dimension variants rather than silently restoring sharp cubes. ValidateSaved is read-only and can inspect the test mirror.

Selection covers terrace/gallery stairs and decks, parapets, and the undercroft floor/retaining sides. Broad adjoining floor slabs, decorative floor wear, furnishing and cover remain outside this surface pass.
