---
name: meshy-unity-assets
description: Translate Unity game-asset briefs into Meshy prompts and compatible geometry/texture settings, with reference-image, topology, rig and export planning. Use when Meshy is the chosen provider, including prompt-only requests.
---

# Meshy for DakaDaka

Use [unity-3d-prompts](../unity-3d-prompts/SKILL.md) for the brief and Unity acceptance. This is a project adaptation, not a copy of the official plugin.

## Prompt and reference

Put the subject first, then materials, shape, style and essential constraints. Avoid conflicting adjectives and unnecessary microdetail. Use a clean reference when appearance matters; ensure all views depict the same object. A text prompt cannot force exact topology or a usable rig.

For image-to-3D, inspect the actual schema: never invent a general geometry `prompt` field. When the texture model has no text guidance, put the desired appearance in the reference image. Keep reference-image prompts distinct from model parameters.

## Choose compatible settings

- Inspect live tools and current model documentation first. Prefer Smart Topology for real-time assets; do not choose a dense cinematic model simply because the user wants quality.
- Snapshot checked 2026-11-17: `model_type: smart-topology`; `meshy-t2` for prop sets, `meshy-t2.5` for characters or sharper detail; `topology: triangle`. Supported target face ranges are 100–15,000 and 100–30,000 respectively. Measure the delivered result.
- Choose geometry and texture models separately. `meshy-n1` offers 2K base color from images, without PBR or `texture_prompt`. Use a compatible model such as `meshy-7` for requested PBR/text-guided textures; verify its current options.
- Do not use retired `model_type: lowpoly`. The retirement of Meshy 7 geometry does not by itself retire the Meshy 7 texture model.
- Smart Topology does not use the standard remesh controls. Do not send quad settings to it or assume `should_remesh` will change its mesh.

## Execute only when requested

Use available Meshy tools for supported steps. If they cannot express the needed topology/pose, inspect the Meshy CLI and its help before choosing a fallback; do not silently accept dense defaults. CLI examples and model pairs need a fresh capability check.

Preserve the approved scope and budget. Reuse tasks for retexture, remesh/LOD, rigging and conversion. Follow one submitted task to completion; retain its resource type and ID for recovery. For text workflows, a preview and texture/refine are separate stages. Do not infer API retirement from a change in the website's tabs.

Rig humanoids from a suitable neutral pose; verify hands, joint clearance, weights and Unity retargeting. Deliver files and inspect the mesh/preview; task success does not establish game readiness. Follow the shared license and Unity integration rules.

## Sources

Checked 2026-11-17: [prompting](https://docs.meshy.ai/en/webapp/guides/prompting), [models](https://docs.meshy.ai/en/api/models), [image API](https://docs.meshy.ai/en/api/image-to-3d), [text API](https://docs.meshy.ai/en/api/text-to-3d). Execution guidance also reviewed against the installed Meshy 3D Generation plugin skill and its `references/pipelines.md`; it is not a project dependency.
