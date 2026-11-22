---
name: tripo-unity-assets
description: Translate Unity game-asset briefs into Tripo prompts and supported MCP or CLI settings; plan Tripo mesh, texture, parts, rig and export workflows. Use when Tripo is the chosen provider, including prompt-only requests.
---

# Tripo for DakaDaka

Use the brief and acceptance workflow in [unity-3d-prompts](../unity-3d-prompts/SKILL.md). This is a project adaptation, not an official Tripo plugin.

## Supported route

- Discover the tools actually exposed by `tripo`. Its configured server uses `tripo mcp --toolset full`. Do not substitute the older Blender-only `tripo-mcp` package.
- When using the CLI, inspect `tripo --version`, `tripo models`, the relevant `--help` and bundled `tripo docs --topic examples/game-asset`. Installed CLI at authoring: 0.11.2; refresh capabilities rather than pinning future tasks to this version.
- Prefer P-series for editable real-time assets; specify the current supported alias explicitly. Use a dense H-series source only when the task needs it, accounting for any later mesh processing.
- Inspect a supported `make --dry-run --json` plan or MCP planning tool before submission. Check normalized settings and warnings. A missing price is unknown, not zero; expand presets into their actual steps.
- PBR is off by default in the inspected CLI. Ask for it in settings only when requested; do not infer it from “realistic” or “Unity”. Do not send unsupported H-only options to a P-series model.

## Prompt and execution

Lead with recognizable structure and the silhouette. Keep only material and style details relevant to the asset. For image input, repair a poor reference before hoping text can correct it. Use the same design across multiple views.

Keep face limits, quads, rigging and export in supported settings. Count the final Unity triangles, including triangulated quads. Plan an articulated object as separate moving parts and inspect whether segmentation actually succeeded.

For execution, reuse the verified account region/session. Never store keys in this repository. Reuse task IDs for texture, export and LOD changes. Submit a paid task once and resume its status on timeout instead of blindly retrying. Inspect/download the existing source task before paying for a redundant conversion. Report actual charges when returned, separately from estimates.

For Unity, select FBX when this project has no verified GLB importer. Keep the source GLB if useful; conversion may be a separate paid step. Apply the shared Unity checks and existing prefab adapters before acceptance.

## Sources

Checked 2026-11-17: [official CLI](https://developers.tripo3d.ai/en/docs/cli), its locally installed `skill/examples/game-asset.md`, [reference preparation](https://www.tripo3d.ai/blog/25-image-to-3d-prompt-tips-for-cleaner-ai-3d-models), and [project source register](../../../Docs/Art/AI_3D_PROMPTING.md#источники).
