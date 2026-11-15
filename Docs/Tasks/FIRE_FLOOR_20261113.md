# Fire and worn floors â€” 13 November 2026

User asks for richer fire particles and relief that breaks the flat box-like floor. Checkpoint: Logs/harness/before-fire-floor-20261113.json.

Bounded changes: authored brazier particles (core, tongues, rising embers, smoke); saved shallow chipped flagstone meshes, uneven heights and missing patches, applied to existing room prefabs. Door thresholds left clear. Original collision floor remains authoritative; relief stays under 10cm and has no colliders. Geometry of walls, doors, stairs and gameplay routes is unchanged. No broad regeneration of room catalogs.

Owned files: Editor/EmberdelveTorchFactory.cs, DungeonFloorWear.cs, two builder integration calls in DungeonModuleContentBuilder.cs, generated FloorWear assets and Brazier_Smoke material, existing room prefab surface/flame children. Reuses existing stone and flame texture assets; no new external licenses.

Validation pending: collider invariants during migration, main compilation/import, focused build and actual three-floor render/motion inspection. Pure cosmetic surface relief does not alter navigation or movement collision; no repeat full gameplay suite. Existing Royale ordinary final extraction acceptance stays open. Preserve inherited uncommitted changes.

Completed first pass: 30 saved floor meshes, 80 refreshed braziers. Main Editor migration passed, collider counts/serialized collider properties and world matrices checked before/after every floor migration. FloorWear assets total39.2MB serialized; one combined renderer per room, no per-stone GameObjects or colliders. Relief height approx2–9cm. Main scene and Play state unchanged.

Initial migration failed on Unity fake-null with null-coalescing component creation; fixed to explicit Unity null comparison before adding components. Final migration succeeded (migration-final.json). Planner selected71 gates due shared editor/content inputs; narrowed to build/render because no runtime rules, collision geometry, navigation or socket layout changed.

Build20261113-141439-598 PASS,60.2s, shell28868 terminal0. Controlled network three-floor review shell54837 terminal0 PASS; visible/review/snapshots contains floor1, floor1-motion, floor2, floor3 and extracted. All floor screenshots visually inspected: shallow edges/settled slabs break the flat surface, warm and cold flames render; sequential floor1 frames show flame changes and advancing timer. Smoke remains deliberately subtle at gameplay distance. This is cosmetic acceptance only, not ordinary Royale full-match acceptance. No new broad gameplay tests.

Changes remain in main and mirror; all owned review players exited. Work uncommitted because room prefabs/builders overlap inherited uncommitted Royale work. Larger architectural silhouette variation and dark destination lighting remain separate next art steps.
