# Emberdelve / Cryptbound — Unity project instructions

The user authorized continued development in this existing Unity project on19October2026. Codex is architect, independent reviewer and asset curator. Local Pi/Strata with the existing model and high thinking is primary implementer. Godot development is superseded; do not resume or edit that project.

Unity version6000.2.5f1, URP17.2.0, C#, new Input System. Preserve existing M1–M4 functionality and unfinished M5 working-tree changes. No engine/package upgrade, resetting/cleaning the working tree, or modifying unrelated template assets. Keep Assets/Emberdelve as the game feature root. The user prefers a playable complete loop and editable scenes/prefabs, with focused independent verification.

Current task and manager state are C:/Users/yrapa/.pi/agent/projects/unity-emberdelve/active-task.json and manager-state.json; queue/tasks/ORCHESTRATION.md are in that directory. Current roadmap: Docs/ROADMAP.md. Functional target: Docs/Design/UNITY_DESIGN.md; six attributes and item-affix amendments there remain required. Read the sections relevant to the assigned task; a narrow fixture repair is not permission to implement the entire roadmap.

The original project calls itself Emberdelve. Keep names/namespaces intact unless assigned a rename. Shared ScriptableObject definitions are immutable at runtime; item UID/rolls are per-instance data. Final gameplay authority belongs to the server: input/client intent is not a trusted health/damage/item outcome. The present LocalCombatAuthority is an offline implementation, not proof of networking. Shields never parry. Combat, inventories, extraction/death and stash must be integrated and independently tested before final acceptance.

Assigned worker invocations currently use file tools only. Codex runs Unity commands, checks logs, reviews actual screenshots and manages downloads/packages. Do not start Unity, kill processes, create scratch projects, edit global Pi/Strata settings, install dependencies or send messages. Verify C# APIs in nearby source. Source changes should be saved before prolonged investigation. Use coherent small edits; on an exact-text mismatch reread the affected function.

Use existing editor builders to wire saved scenes/data; do not replace the project with a monolithic runtime-generated game. Do not weaken or remove a test to make it green. Correct a proven wrong fixture expectation with an explanation and preserved coverage. Engine exit0 alone is not success: smoke logs currently may contain FAIL despite exit0. Do not claim unexecuted checks passed.

Write a concise Docs/Tasks/<TASK_ID>.md report with changed files, actual behavior, pending checks and blockers. No remote publication, payments or external messages. Licensed assets may be curated by Codex; keep license/source/hash records and reuse already downloaded assets.
