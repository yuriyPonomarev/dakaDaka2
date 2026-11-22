# Original DakaDaka prompt examples

These are original project examples, not copied vendor prompts. Use the matching provider skill to map settings. They do not submit tasks or establish an approved spending plan.

## Shared art direction

Grim medieval crypt; practical construction; believable adult proportions; muted gray stone, aged iron, walnut, worn leather and sparse desaturated moss. Shape and value separation should remain readable in torchlight. Match current assets, rather than generating a chibi or exaggerated cartoon set. Express this through observable features, not a request to copy another game's assets.

## Brief template

```text
Asset ID and role:
Existing asset to reuse/replace, if any:
Provider and route: text / approved image / consistent multiple views
Camera and approximate display size: first-person / world / inventory
Dimensions and required attachment:
Visual prompt:
Reference-image prompt, if needed:
Generation settings: verified model, target mesh count and face type,
  texture model/resolution/maps, pose, parts, export, number of variants
Unity acceptance: measured triangles, bounds, pivot, materials, motion
Cost/scope: authorized stages, estimate or unknown, retry limit if specified
Provenance: source/reference rights, task lineage, actual output license
```

Do not embed the entire brief in the generator's prompt. Provider settings and Unity work remain separate.

## Candidate mesh budgets

Local starting proposals, not platform limits or accepted performance budgets:

- Repeated small loot: 500–2,000 triangles, usually one material.
- Ordinary barrel/chest/furniture: 2,000–6,000 triangles.
- First-person weapon: 3,000–10,000 triangles; prioritize outline and grip details visible to the player.
- Humanoid: 10,000–25,000 triangles before equipment, subject to actual deformation and crowd cost.
- Modular wall/arch: start at 1,000–5,000 triangles; flat matching boundaries matter more than fine relief.

Replace these proposals with the actual task budget. Tripo face counts may represent quads; do not equate them to Unity triangle counts. Provider targets are approximate. Preserve the existing 512px/Point retro convention where its importer applies; source texture resolution and Unity imported resolution are different decisions.

## Static crypt prop: iron-bound barrel

Visual prompt:

```text
One medieval storage barrel, stout oval oak staves, two broad dark iron hoops,
recessed wooden top, practical hand-built construction, muted brown wood and
patchy iron oxidation, restrained surface wear, believable proportions,
grim crypt game prop, strong simple silhouette, no symbols or attached tools.
```

Settings proposal: 3,000 triangles, one material, textured; select map requirements explicitly. Acceptance: correct floor contact and bounds, top/bottom closed, no detached hoops, readable next to the current Poly Haven props. Add scratches through textures rather than thousands of modeled grooves.

## Animated container: reliquary chest

```text
One narrow medieval reliquary chest, heavy walnut body, shallow arched lid,
wide forged iron straps, a single central latch, restrained hammered metal
detail, muted dark wood with lighter worn edges, plausible medieval joinery,
grim crypt aesthetic, sturdy readable silhouette, no jewels or lettering.
```

Settings proposal: 5,000 triangles total. If opening is required, plan a separate lid and base through supported segmentation or local editing. Acceptance: real lid object, rear hinge pivot, opening clearance and collider fit. A closed fused mesh fails the moving-lid requirement even if the preview looks correct.

## First-person weapon: arming sword

```text
One practical medieval arming sword, straight double-edged blade with a
shallow central fuller, compact straight crossguard, dark wrapped leather
grip and small wheel pommel, worn steel with restrained edge polish,
believable functional proportions, grim grounded fantasy, clean outline,
no magical glow, engravings or oversized ornaments.
```

Settings proposal: 6,000 triangles; no rig for a rigid weapon. Acceptance: fit the existing item dimensions and prefab mount; current weapon-view convention is grip at origin, tip +Y, face +Z. Check both owner hands and observer view. Do not change damage reach to fit the generated blade.

## Humanoid: crypt sentinel

```text
One adult human crypt sentinel, lean believable anatomy, plain weathered
gambeson and simple iron breastplate, distinct forearms and lower legs,
five separated fingers on each hand, empty hands, practical boots,
muted charcoal cloth and worn gray metal, grounded grim fantasy,
symmetrical neutral A-pose, unobstructed joints, no cloak or equipment props.
```

Settings proposal: 18,000 triangles, supported neutral-pose setting, compatible texture model, rig as a separate stage only if requested. Acceptance: fingers and limbs actually separate; elbows/knees deform; feet do not slide; Unity Humanoid maps correctly. Prompted anatomy is a goal, not evidence of a correct skeleton. Check whether existing CC0 bodies/animations already satisfy the task first.

## Modular environment: stone arch

```text
One freestanding medieval crypt doorway arch, rough gray limestone blocks,
thick rectangular side piers, a shallow rounded stone vault, restrained chips
and sparse dark moss in joints, believable load-bearing masonry, readable
large stone shapes, flat outer joining faces, no door, ground slab or debris.
```

Settings proposal: 4,000 triangles. Put exact opening width/height, wall thickness and grid size in the brief. Snap joining faces and pivot during local mesh preparation; verify doorway clearance in the existing map. Do not trust image reconstruction for precise modular dimensions.

## Reference image for an existing design

```text
Asset reference of [the selected design], one complete object centered with
all extremities visible, plain neutral gray background, soft diffuse studio
lighting, restrained reflections, readable surface color, eye-level view
with minimal perspective distortion, no scenery, pedestal, text or watermark.
```

For multiview, request front, side and rear of the same design with consistent dimensions and lighting. Inspect each view before conversion. Do not upload a contact sheet as one single image unless the selected tool specifically accepts it. For a posed character, keep pose and accessories identical across views.

## Texture-only revision

```text
Preserve the existing construction and silhouette. Weathered walnut with
subtle pale wear along exposed edges, dark iron fittings with restrained
rust near joints, low saturation and readable material separation,
surface color without painted cast shadows or torch highlights.
```

Use a retexture workflow on the existing task/model. Preserve geometry, UV requirements and mount; verify whether the provider preserves or repacks UVs. This is not a reason to generate another mesh.
