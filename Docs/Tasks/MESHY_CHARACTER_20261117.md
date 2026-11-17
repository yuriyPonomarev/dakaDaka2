# Meshy warrior and first-person hands

## Status on 17 November 2026

The user requested a textured character, a Meshy rig and animations with bare hands suitable for first-person weapons and shields. The first physique was rejected as too thin and miniature-like. The revised full body is a broad-shouldered, muscular warrior in a dark sleeveless gambeson. Visual quality remains under user review.

The detailed Meshy 7.1 body, PBR textures and locally repaired finger rig are now in the development checkout. The active first-person mesh derives from this same detailed source. Work is uncommitted while the user's visual review continues.

## Owned changes

- Art/Models/Meshy/CryptDelver contains the generated source revisions, the detailed CryptWarriorHero.fbx, UV-preserving first-person geometry, seven generated clips, Humanoid controllers, materials, textures and task provenance.
- EmberdelveMeshyCharacter configures imports and derives the first-person mesh. EmberdelveFirstPersonArms reuses its prefab authoring for an explicit source, material array and output path.
- FirstPersonArmsDriver supports the generated rig's scale, grip roll and bounded elbow swivel. The sword retains its accepted lowered swing framing. The blade keeps the physical trajectory during committed attacks and guard.
- EmberdelveViewmodelPreview samples an explicit swing blend when reviewing authored attacks. Existing actual-path tests exercise the resulting grip targets and directions.
- Class_Warrior and WeaponViewModelSet select the new body and first-person prefab.
- WeaponViewModelTests validates the arm bone hierarchy through Humanoid mappings. A focused synthetic regression checks wrist alignment while retaining exact grip position, rotation and segment lengths.
- Tools/Repair-MeshyHeroHands.py preserves the generated surface and body rig, fits 30 finger joints, blends their weights and gives coincident UV-seam vertices identical weights. FpGripPose supports optional skeleton-specific closed-hand and bow-hook rotations; only the generated arms prefab selects those rotations.

## Generation and costs

The first generation used nano-banana-2-lite, meshy-t2.5, meshy-7 4K textures and Unity Humanoid rigging with fingers. It produced a 27,288-triangle source and five action clips, plus walking and running. Its charge was 38 credits.

The revised muscular body used nano-banana-pro, meshy-t2.5, meshy-7 textures and a new 1.9-metre Unity Humanoid rig. It contains 31,484 triangles and all 30 finger joints. Its charge was 29 credits. Session spending is 67 credits.

The revised Smart Topology body's fingers deform under strong curl. Its initial workaround used the first source's cleaner hands with radial forearm shaping. This workaround is superseded by the detailed source below; the prototype files remain as provenance and rollback sources.

A detailed Meshy 7.1 generation, reference and rig was approved at 44 additional credits, with priority on hands. Reference task 01a1eac5-bd98-7750-a4a5-22b4f6c70cd5 shows separated fingers. Model task 01a1eaca-26be-73ae-b9f4-955c77de586a produced 314,833 triangles and 4K color/normal, 2K metallic/roughness textures. Rig task 01a1ead0-a43d-7242-ab3a-9aba584a74d6 charged 5 credits but returned zero finger joints. The journal payload fingerprint exactly matches include_fingers=true and skeleton_template=unity_humanoid. Unity rejected that result (unity-hero-v15.log). The user approved one additional 5-credit rig-only retry, 01a1eae9-5df6-7064-aa38-6e46ae85e9d3. It also returned no finger joints. Both original results are preserved; no further paid task was submitted. Total session spending is 116 credits; verified balance is 979.

Local Blender 4.3.2 repair adds the 30 missing joints to the existing Meshy body rig and weights the separated fingers. It preserves 177,046 surface vertices, the 314,833 triangles and UVs. Coincident UV-seam vertices receive identical weights to prevent tearing. The authored first-person mesh has 28,022 vertices and 50,560 triangles. Both the body and first-person rig use the same repaired source and Hero PBR maps. Source and derived checksums, task IDs and costs are recorded in manifest.json. The project uses a Humanoid Avatar with all 30 finger mappings. The existing seven Meshy clips retarget to it; original project Block and Death states remain.

## Verification

The comparison checkpoint is Logs/harness/before-meshy-character.json. The source revision at the start was 46443348. The starting checkout contained material and project-settings edits recorded by the checkpoint.

Unity import and rendering succeeded in the owned DakaDaka_ci project. All 30 finger joints resolve. Logs/acceptance/meshy-character-20261117 contains the actual Unity equipment views and sampled attack poses. The v13 log records the final runtime and asset configuration before close-up review.

Visual EditMode v13 completed with 187 cases, 186 passed and one failed. The remaining case is EnemyVisualQuaterniusTests.DerivedMeshes_KeepTheSkinning_AndHaveUvsInTheTexture. It compares the existing derived enemy mesh with a legacy Quaternius source and reports 8462 versus 30577 vertices. Its source files are outside this slice. The earlier v11 failures in weapon direction, lowered framing, guard and crossbow grip are resolved in v13.

The planner conservatively selected all gates for new asset inputs. The reviewed bounded selection covers the arm driver and its physical guard/trajectory interactions. Run Logs/runs/20261117-190233-466 finished successfully in 302.2 seconds.

- Hands passed 92 checks.
- Visual passed 216 checks.
- NetCombat passed 396 checks.
- ProjectileBlock passed 191 checks.
- SweptMelee passed 674 checks.

All five gates passed. The build cache recorded an input-change bypass during generation, and the actual gate results were collected. Evidence applies to these cases and gates.

All 28 enemy/Quaternius inputs selected for comparison matched the pre-task content checkpoint, including the derived enemy meshes and their importers. The single remaining Visual EditMode failure therefore tracks the existing legacy enemy-derived-mesh comparison.

## Remaining review

Close-up review of the Smart Topology version completed with the v14 editor helper. The side/front sword grips and torch grip are saved in Logs/acceptance/meshy-character-20261117. Coarse palm contours are visible at that distance. The revised full-body fingers also deform under strong curl. The user approved the additional 44-credit high-detail generation, which will receive fresh import, grip and motion review.

## Detailed revision verification

Checkpoint Logs/harness/before-meshy-hero.json isolates this revision from the previously verified prototype. unity-hero-v21.log records successful import, 30/30 finger mappings and fresh actual Unity equipment/attack/body captures. Open-hand views and close-up grips are saved beside it. Earlier v18-v20 captures exposed skin tearing at split UV vertices; v21 corrects that defect.

Visual EditMode v22 completed in 65.7 seconds: 187 cases, 186 passed and the same single pre-existing enemy-derived-mesh mismatch. There were no additional failures. Runtime changes are confined to optional authored finger rotations, selected only by the new arms prefab. The planner selected all gates conservatively for the new asset inputs; the bounded reviewed selection covers the arms, source body presentation, physical blade/guard and shield interactions. Run Logs/runs/20261117-202622-322 finished PASS in 258.8 seconds: Hands 92/0, Visual 216/0, NetCombat 396/0, ProjectileBlock 191/0, SweptMelee 674/0. The player build cache missed and was not stored because generated/imported inputs changed; the actual five gate verdicts were collected. No full-suite or unrelated legacy-failure resolution is claimed.

The final v23 batch only adds/runs the editor review helper, without changing gameplay code or authored rig data. It compiled and exited successfully. It rendered 24 frames of the actual 1.5-second Humanoid sword clip and 24 frames of the actual 0.84-second first-person attack pattern. Both motion sequences and the equipment key poses were inspected; GIF previews hero-body-motion.gif and hero-fp-motion.gif are assembled from those Unity renders. No generated image was substituted for gameplay evidence. The new material and all rig/prefab/config references are present in the primary checkout.

The owned batch Editor and proof players have exited. The primary Editor was never closed or put into Play. All batch rendering and automated checks use DakaDaka_ci. User acceptance of the final appearance is still pending; no completed-task commit is claimed.

