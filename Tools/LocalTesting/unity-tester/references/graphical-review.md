# Unity graphical verification in this project

Use only when the task requires it. Follow the task's named versioned helper and TESTER_CONTRACT; do not use an unknown or user-owned Editor. The current project already has Pipeline; do not install, update, or change packages.

1. Verify the actual Editor project, clean scene and Play state, then use the assigned saved scene. The installed CLI's current command catalog is authoritative. Prior working project commands include `unity status`, `unity command editor status --project-path <project> --format json`, `unity command editor set_auto_tick`, `unity command editor.playmode set_run_in_background`, and `unity command editor.playmode start` / `stop`. Check actual command parameters when needed; old flat command names in generic skill examples may not apply.
2. Save prior transient state. Enable only necessary session-only ticking/background overrides. Launch the assigned helper promptly after Play starts and wait for a living actor. Never revive an actor or bypass equipment/admission rules to satisfy a probe. If setup fails, report it; only retry a fresh Play session when appropriate and authorized by the brief.
3. Example for the existing D4c1 probe, as two separate calls, with frame/time advancement in between:

   `unity command script run --project-path <project> --format json -- --file AgentScripts/ReviewD4c1.cs --entry ReviewD4c1.Prepare`

   `unity command script run --project-path <project> --format json -- --file AgentScripts/ReviewD4c1.cs --entry ReviewD4c1.Measure`

   Store the complete JSON envelopes in fresh evidence files. Check transport success, script success, returned result, failures, actual viewport and measured bounds. Do not rebuild a dynamic UI immediately before counting its children: Unity can defer destruction until a frame boundary. For this specific helper only, `python Tools/LocalTesting/summarize_render.py <prepare.json> <measure.json>` checks its envelope and measurement invariants. The raw JSON and screenshot remain evidence.
4. Capture the screen-composited game view where required. Requested screenshot dimensions do not establish actual `Screen.width`/`Screen.height`; record both. Stop once the assigned viewport proof is complete; do not rediscover or force optional sizes when unavailable.
5. Restore the exact original transient settings and any window changes, stop Play, close only the verified owned Editor gracefully, and confirm process exit and lock absence. Never delete the lock file. Distinguish numeric results from subjective visual review.
