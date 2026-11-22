# Weathered wall surfaces

These are exports of the project's six authored FoldedMasonry profiles, not new third-party assets. Original Unity meshes remain the collision source. Only rendered surfaces receive a 3.5 cm, three-segment bevel in the placement Z metric (0.25 scale), with weighted corner normals and retained broad faces.

Regenerate from the development checkout with its working Editor:

1. Unity CLI `script run --file Tools/Art/WeatheredWalls.cs --entry WeatheredWalls.Export` with explicit development `--project-path`.
2. Blender 4.3.2 `--background --factory-startup --python Tools/Art/BevelWeatheredWalls.py` from the project root.
3. Unity CLI `script run --file Tools/Art/WeatheredWalls.cs --entry WeatheredWalls.Import` with the same explicit project path.

Intermediate `.bevel.json` files are reproducible and ignored. Committed source JSON preserves the exact input. Imported visual mesh names contain the source geometry fingerprint; future builders fall back to the original surface if the profile has changed until this chain is rerun. Imported Meshy pier/crown shading is separately refreshed by `WeatheredWalls.RefreshImportedStone`; source downloads and their existing provenance are unchanged.

All wall prefab transforms, colliders, room dimensions, doors and secret mechanisms retain their prior values. Planar UVs are reconstructed on the bevel output; the active stone material uses world projection.
