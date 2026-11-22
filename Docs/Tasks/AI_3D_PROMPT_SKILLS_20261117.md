# Tripo / Meshy Unity prompt instructions — 17 November 2026

## Scope and acceptance

User requested finding rules and skills for prompting 3D game assets for Unity with Tripo and Meshy, and adding them to the project. The main project is `DakaDaka`; the CI mirror and clean handoff are not independent destinations for this change.

Acceptance: discoverable project instructions for both providers, original task-relevant prompt examples, current official source links, consistent existing art/Unity conventions, and valid skill structure. No model generation or integration is part of this task.

## Changes

- `.agents/skills/unity-3d-prompts/SKILL.md`: shared brief, prompt/settings/acceptance separation and provider routing.
- `.agents/skills/tripo-unity-assets/SKILL.md`: Tripo MCP/CLI capability checks and real-time asset workflow.
- `.agents/skills/meshy-unity-assets/SKILL.md`: geometry/texture pairing, Smart Topology and supported-input checks.
- `.agents/skills/unity-3d-prompts/references/prompt-library.md`: original examples for current crypt props, articulated chest, weapon, humanoid, modular arch and texture revision.
- `.cursor/rules/unity-ai-3d-assets.mdc`: thematic rule; `AGENTS.md` provides explicit Codex routing.
- `Docs/Art/AI_3D_PROMPTING.md`: Russian user guide, project conventions, Unity checklist and dated source register.
- `Docs/CURRENT_WORK.md`: records this documentation slice without superseding runtime status or next priorities.

The provider skills are concise project adaptations of reviewed official references, not vendor-authored skills or a third-party code installation. Full official manuals were not copied into the repository. Suggested asset budgets and future output paths are clearly identified as proposals.

## Verification

- Official `skill-creator/scripts/quick_validate.py`: all three skills valid (3/3).
- Reviewed all nine instruction/document files; all ten relative Markdown file links resolve; no trailing whitespace or credential-like values found. `git diff --check` passed.
- `python Tools/harness.py plan --since Logs/harness/before-ai-3d-prompt-skills-20261117.json --json` identified exactly these nine files. It conservatively selected full coverage for the five `.agents`/`.cursor` files because those paths are unmapped. Manual review confirms they contain only Markdown/YAML instructions and examples, with no code, assets, packages or settings consumed by Unity; a gameplay run would not validate their behavior. No harness routing was changed to bypass this decision.
- Bounded `python Tools/harness.py run --files AGENTS.md Docs/CURRENT_WORK.md Docs/Art/AI_3D_PROMPTING.md Docs/Tasks/AI_3D_PROMPT_SKILLS_20261117.md` returned `EditMode=False; gates=0; harnessTests=False`, with no Unity launch. Its scope is those four documents; the five remaining instruction files were validated/reviewed as described above. Plan: `Logs/harness/20261117-203633-239524-plan.json`.

Checkpoint: `Logs/harness/before-ai-3d-prompt-skills-20261117.json`. Existing material drift, asset logs, pipeline settings and chat-context file were present before this task and remain outside its scope.

## Limits and next use

These instructions do not grant a license exception or a paid-generation budget. Validate provider capabilities again at execution time. Generated models still require actual mesh inspection and in-game acceptance; the examples are not proof of output quality. The skill picker may need project reopening; `AGENTS.md` gives direct file paths independently of picker discovery.

Next: use the relevant brief/provider skill for the user's next requested asset. Existing runtime priorities remain unchanged.
