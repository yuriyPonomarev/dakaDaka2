# Visual verification in Unity

## Before Play

- From the development checkout, verify the absolute project path of a separate, visible Editor for the synchronized mirror (`../DakaDaka_ci`). Prefer the `unity_ci` MCP connection for review commands. Confirm its reported path is the mirror and that the coordinator owns this review session, then bring its Game view forward. If a command requires the CLI, pass the same absolute mirror path with `--project-path`.
- Start the mirror Editor with `--ember-profile-dir` pointing to a fresh directory under its `Logs/acceptance/<run>/`. Confirm the actual argument before opening the review scene or entering Play. Keep personal saves and the user's working Editor outside the scenario.
- Check the assigned scene, Play state and transient settings. Use the installed Pipeline command catalog and the versioned helper for the task. Record the original settings so they can be restored.

## Record and deliver

1. Start Unity Recorder before the action. Record the visible Game view and Unity game audio through the outcome. Save the MP4 and evidence together under `Logs/acceptance/<run>/`.
2. Execute the assigned scenario with real frame progression. For a player scenario, wait for a living actor. Preserve equipment and admission rules. For viewport checks, measure actual `Screen.width` and `Screen.height` alongside the requested dimensions.
3. Open the MP4, verify that it shows the action and its result, and check the game audio track. Attach the video in the current chat after each run with the scenario and outcome. If display or recording fails, diagnose and retry on the mirror. Report the blocker if a usable recording cannot be produced.
4. Restore transient settings and window changes. Stop Play and gracefully close only the verified review Editor owned by the run. Confirm its exit and lock release. Leave Unity lock files intact. Report measured results and any visual or listening judgement actually performed.

## Existing D4c1 viewport probe

Call Prepare and Measure separately, allowing real frames to advance between them. Use the CLI examples below when the evidence checker requires its JSON envelopes or when the mirror MCP tool is unavailable.

```powershell
unity command script run --project-path <absolute-mirror> --format json -- --file AgentScripts/ReviewD4c1.cs --entry ReviewD4c1.Prepare
unity command script run --project-path <absolute-mirror> --format json -- --file AgentScripts/ReviewD4c1.cs --entry ReviewD4c1.Measure
```

Store both JSON envelopes. Check the command and script results, failures, actual viewport and measured bounds. Unity can defer UI child destruction until the frame boundary. For this helper, `python Tools/LocalTesting/summarize_render.py <prepare.json> <measure.json>` checks the recorded measurements. Retain the raw JSON and screenshot with the video.
