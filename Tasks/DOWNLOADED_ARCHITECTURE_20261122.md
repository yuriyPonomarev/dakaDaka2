# Downloaded crypt architecture - 22 November 2026

User requested downloaded Meshy models while preserving expanded rooms, stairs and lever caches. Primary checkout only; no paid generation.

## Sources

`Assets/Emberdelve/Art/Models/AI/Meshy/DownloadedArchitecture/manifest.json` records archive and file hashes. StonePillar uses owner-supplied `Meshy_AI_A_ancient_stone_pilla_1121223009_texture_fbx.zip`; duplicate 1121222950 has identical topology/albedo at a different scale/pivot. RusticBarrel uses `Meshy_AI_Rustic_Barrel_1120195018_texture_fbx.zip`. Original FBXs/albedos remain intact; barrel normal map included. No asset-specific license is in these archives: do not label them CC0.

Reuses imported `CommunityRuins/OvergrownArch`. Crown and solid stone pier are cropped from original triangles with their UVs/normals. There was no jar/coffin/table archive in Downloads: clay vessels and small funerary urns become downloaded barrels. Poly Haven tables remain. Sarcophagi use the existing SourceArt CryptProps/SealedChest textured closed reliquary as a static funerary coffer, with no loot/opening behavior. Existing lid support height and colliders stay unchanged. Its source preview was inspected: dark wooden panels, iron fittings and shallow lid. It is called a reliquary, not a downloaded stone coffin; a 90-degree turn and uniform fit preserve its proportions (about 1.54m wide, 2.40m long, 1.33m high). Floors, stairs and other forms without a suitable downloaded replacement remain authored geometry.

## Integration

New `Editor/DownloadedCryptArchitecture.cs` fits child visual meshes and hides superseded renderers. Original colliders, sockets, moving secret parents, interactables and light sibling identities remain unchanged. Children inherit the Level layer for world-light masks.

Columns replace shaft/base/capital visually. The coordinator changed StonePillar/Surface.mat to a copy of `Art/Generated/DungeonSurfaces/World_Stone_Wall.mat` (`StonePillar_CryptStone`, instancing enabled). `Surface("StonePillar", ...)` and `RefreshMaterials()` reproduce this shared world-space stone treatment. The downloaded carved geometry and original albedo remain preserved, but the blue/black baked source albedo is no longer the column surface. Arch crowns omit downloaded jambs to keep passages clear. Flat walls and secret panels use repeated actual stone-pier sections with maximum 1.5m by 2m cells and small overlaps. No timber wall infill. Outdoor folded/chamfered walls retain their original broken silhouette and mesh-matched physics instead of rectangular fitting. The courtyard timber panel remains a deliberate separate wooden decoration.

Both catalog builders apply the pass to all 30 rooms. `DungeonDoorAuthoring.Build` also fits the separate dynamic door frame; leaf, hinge, trigger and width are untouched.

## Rebuild evidence and geometry budget

The coordinator successfully imported and rebuilt on the test mirror. `Logs/acceptance/ambience-20261122/melodic/rebuild.log` records the dynamic Resources door and all 30 room modules, ending with successful batch exit and return code 0. Generated materials, derived meshes, saved prefab references and Unity metadata are now present; they are no longer pending import. This is rebuild evidence, not final visual/gameplay acceptance.

Actual shared display LOD results in that log: pillar 15,346 -> 2,972 triangles; barrel 87,574 -> 4,291; reliquary 4,576 -> 3,887; cropped crown 12,443 -> 3,760; cropped stone pier 7,916 -> 682. Clustering retains corner UV/normal attributes and preserves the original sources, but it is not lossless. Close-up quality still requires final review. Materials enable instancing.

The saved-prefab CSVs each contain 30 rows:

| Metric | Before | After |
| --- | ---: | ---: |
| Median visible triangles per prefab | 151,702 | 602,806 |
| Visible triangle range | 123,832-540,588 | 501,708-967,717 |
| Median visible renderers per prefab | 293 | 638 |
| Visible renderer range | 260-675 | 353-826 |
| Sum of visible triangles across 30 prefab assets | 7,303,981 | 18,327,601 |
| Sum of visible renderers across 30 prefab assets | 10,353 | 17,648 |

Sources: `Logs/acceptance/ambience-20261122/architecture-budget/before.csv` and `after.csv`, inspected directly. The largest after-pass prefab is `Assets/Emberdelve/Prefabs/DungeonModules/Crypt_Vault.prefab`: 967,717 visible triangles and 826 renderers. These are saved-prefab counts; intermediate builder logs can include renderers subsequently disabled. The 30-prefab sums are not a claim about simultaneously visible gameplay geometry, draw calls, FPS or GPU cost. The increase is material and remains part of the coordinator's visual/performance review.

The initial real Solo recording on seed12345 exposed patchy pillar shading. Inspection found strong blue/black/light variation in the source albedo, with some additional LOD normal deviations; the coordinator adopted the common stone material described above. That recording ended when the stationary player died and remains failed evidence. Corrected visible reviews on seeds12345/67890 each passed14 playback/lifecycle checks with actual game audio. The two entry-area sweeps and their frames were inspected; they do not constitute a manual close-up inspection of every interior/cache or an FPS benchmark. Recordings: `Logs/acceptance/ambience-20261122/melodic-review-{seed}`. The complete146-case module suite subsequently passed, including expanded traversal, height navigation and staged placement. Independent source review found no actionable collider/layer/cache-parent/reapplication defect. Network verification is recorded in the shared integration report.

Rebuild entry: `Emberdelve.EditorTools.DownloadedCryptArchitecture.RebuildAll`, plus `Emberdelve.EditorTools.DungeonDoorAuthoring.Build` when the integrated build does not already author that Resources prefab. Subjective art acceptance and GPU profiling remain separate from the recorded geometry/playback checks. Final aggregate results: `Tasks/HANDOFF_INTEGRATION_20261122.md`.
