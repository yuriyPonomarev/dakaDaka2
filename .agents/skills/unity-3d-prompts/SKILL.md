---
name: unity-3d-prompts
description: Write or review 3D game-asset prompts and reference-image briefs for DakaDaka in Unity, including Tripo and Meshy. Use for props, weapons, armor, modular crypt pieces and characters; not gameplay implementation or physical printing.
---

# Unity 3D asset prompts

Read [project art defaults](../../../Docs/Art/AI_3D_PROMPTING.md) and the relevant existing asset entry in `Docs/ASSET_PLAN.md`. Keep existing visual feedback and equipment conventions authoritative.

## Build a useful asset brief

- Identify the item, gameplay use, camera distance, dimensions, moving parts and existing attachment/prefab it must fit. Infer from the current asset before asking.
- Write a concise visual prompt: object and silhouette, construction, main materials, project style, essential exclusions. Use English for portable examples unless the user prefers otherwise; explain decisions in the user's language.
- For references, show the whole object in neutral diffuse light on a plain background. Use consistent views of the same design when hidden sides or depth matter. Keep reference-image composition instructions separate from the 3D object description.
- Keep palette, proportions and wear consistent across a set. Make variants by changing named features, not by rewriting the entire style.
- For moving lids, bow limbs or equipment layers, specify the part requirement in the brief and choose a supported parts/editing workflow. Text alone cannot guarantee segmentation.

For architecture, make join geometry part of the brief: solid masonry spans, exact arch springline/support contact, stair tread/riser/landing transitions, door leaf/frame clearance through motion, and adjacent module seams in both connector orientations. For overlapping opaque meshes require removal of duplicate faces or real geometric depth separation; renderQueue/sorting priority is not a repair. Follow the [architecture acceptance checks](../../../Tools/LocalTesting/unity-tester/references/graphical-review.md#architecture-depth-and-joins) on integration; failed image evidence blocks game-ready claims even with passing tests.

## Return three distinct deliverables

1. **Visual prompt** and, when needed, a reference-image prompt. Keep exclusions as plain language unless the selected API exposes a negative-prompt field.
2. **Generation settings**: provider, supported model, count, mesh/texture target, material maps, pose/parts/export and any additional steps. Mark unknowns; never invent API fields.
3. **Unity checks**: measured triangle count, size/orientation, pivot/attachment, materials, UVs, rig and in-game view. Treat requested budgets as targets until measured.

For original examples and candidate budgets, read [prompt-library.md](references/prompt-library.md). They are starting points, not accepted specifications or automatic generation requests.

## Route and finish

- Tripo: read [tripo-unity-assets](../tripo-unity-assets/SKILL.md).
- Meshy: read [meshy-unity-assets](../meshy-unity-assets/SKILL.md).
- User asks only for prompts: deliver the brief and stop without submitting tasks.
- User asks for generation: continue within the already approved scope/budget; clarify only missing decisions that affect cost or result. Installation of these instructions itself grants no spending authority.
- Keep source downloads in `SourceArt/AI/<provider>/<asset-id>/<task-id>/`; import reviewed deliverables into `Assets/Emberdelve/Art/Models/AI/<provider>/<asset-id>/` only when integration is requested. Preserve existing asset paths and Unity GUIDs when revising an import.
- Preserve original downloads and task lineage. Apply the [project asset sourcing rules](../../../Docs/ASSET_PLAN.md#asset-sources-and-usage-rights) to the specific download and intended use before integration.
- For Unity integration, prefer `unity` to inspect the imported asset and its prefab in the working Editor. Use `unity_ci` for an assigned in-game review under the [Unity pipeline rule](../../../AGENTS.md#unity-pipeline).
- Inspect a preview if available and the actual mesh before calling it game-ready. A successful provider task establishes generation, not Unity acceptance.
