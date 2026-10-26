# R8a - first assembly split (Rules / Runtime / UI / Review / Editor + EditMode tests)

Status (26 Oct 2026): DONE. Applied by Claude orchestrator (git mv for the two moves), compiled clean on first try. Evidence Logs/r8a_20261026_123856: EditMode Emberdelve.Rules.Tests 14/0; R8aSavedSerialization 135/0; full regression 44 groups PASS (40327 checks / 0), incl. InventoryUI 420, MultiPlayer 134, LootPipeline 9954, AttributeSkill 925.

Uncompiled, untested (no Unity available to the implementer). Root must run the gates below.

## Moves (apply/MOVES.txt; GUIDs and namespaces unchanged)
- Runtime/Stats/AttributeRules.cs(+.meta) -> Runtime/Rules/ (ns Emberdelve.Stats, GUID e1a58df9...)
- Runtime/Flow/LobbyController.cs(+.meta) -> Runtime/UI/ (ns Emberdelve.Flow, GUID 9da546d2...)

## Added / overwritten
- asmdefs + metas: Runtime/Emberdelve.Runtime, Runtime/Rules/Emberdelve.Rules, Runtime/UI/Emberdelve.UI, Runtime/Review/Emberdelve.Review, Editor/Emberdelve.Editor, Tests/EditMode/Emberdelve.Rules.Tests.EditMode
- Folder metas: Runtime/Rules.meta, Tests.meta, Tests/EditMode.meta
- Runtime/Combat/LocalCombatAuthority.cs (overwrite): adds IVT "Emberdelve.Editor" (keeps "Assembly-CSharp-Editor"), comment refresh
- Runtime/UI/AssemblyInfo.cs and Runtime/Review/AssemblyInfo.cs (+.meta, new GUIDs): IVT to Emberdelve.Editor
- Tests/EditMode/AttributeRulesTests.cs (+.meta)
- Editor/R8aSavedSerializationGate.cs (+.meta), adapted (see below)
- No Rules/AssemblyInfo.cs: AttributeRules.cs has zero `internal` members.
- NOT included: the 58 m_EditorClassIdentifier YAML edits (deserialization is by script GUID; the gate proves load).

## Assembly graph
- Rules: no refs, noEngineReferences (System only)
- Runtime -> Rules, Unity.InputSystem
- UI -> Runtime, Rules, Unity.InputSystem, UnityEngine.UI
- Review -> Runtime, Unity.InputSystem; defineConstraint `DEVELOPMENT_BUILD || UNITY_EDITOR`
- Editor (Editor-only) -> Runtime, Rules, UI, Review, Unity.InputSystem, UnityEngine.UI  (Review added vs draft: EmberdelveLootPipelineGate uses EmberdelveReviewBridge)
- Tests.EditMode (Editor-only, UNITY_INCLUDE_TESTS, nunit.framework.dll) -> Rules, TestRunner refs
- Runtime must not reference UI/Review/Editor (no cycle).

## Tests
14 NUnit cases in AttributeRulesTests (namespace Emberdelve.Rules.Tests): ClampScore 4 TestCases, Modifier 6 TestCases, plus 4 [Test] (default scores, snapshot replace-not-accumulate, Derive literals, key parser) = 14.

Literal audit (hand-computed against live AttributeRules.cs; cross-checked with EmberdelveAttributeRulesGate expectations such as "score 30 -> 10", "(3-10)/2 floor -4", "14+2=16"). No literal needed fixing.
- ClampScore: 0->1, 1->1, 30->30, 31->30 OK.
- Modifier floor((s-10)/2): 1->-5, 9->-1, 10->0, 11->0, 16->3, 30->10 OK.
- default AttributeScores.ToArray = six 1s (read-side clamp) OK.
- Snapshot base (10,14,8,12,16,10), +(2,0,3,..) => STR12 CON11; replaced +(1,0,1) => STR11 CON9; base STR10 CON8 OK.
- Derive, mods STR0 DEX+2 CON-1 INT+1 WIS+3 CHA0, bases (100,50,80,3,2,4): health 100-10=90; mana 50+5=55; stamina 80-5=75; manaRegen 3+0.75=3.75; staminaRegen 2-0.5=1.5; speed 4*(1+0.05)=4.2 OK. (DerivedStatBases ctor order health,mana,stamina,manaRegen,staminaRegen,moveSpeed matches.)
- TryParseKey(" Dexterity ")=Dexterity; "armor" and null false OK.

## Saved serialization gate (adapted)
Method `Emberdelve.EditorTools.R8aSavedSerializationGate.Run` (namespace changed from Emberdelve.Editor to EditorTools to match other gates and avoid shadowing UnityEditor.Editor; class now public). Menu: Emberdelve/Verification/R8a Saved Serialization Gate.
- Assets: enumerated via FindAssets("t:ScriptableObject", {Assets/Emberdelve/Data}) (.asset only, recursive); each must load, be a concrete ScriptableObject, and its type must be in Emberdelve.Runtime. No 56-count assertion (only >0); no m_EditorClassIdentifier text checks.
- Assembly identity checks: Rules, Runtime, UI (found by name), Editor.
- Scenes Lobby/Level01 opened additively: no missing MonoBehaviours; LobbyController, HudController, ResultScreenController resolve from Emberdelve.UI and are present; PlayerRig resolves from Emberdelve.Runtime and is present.
- Aborts with FAIL if open scenes are dirty (never prompts/saves). Restores scene setup (EmptyScene if none).
- Output: exactly one `[Emberdelve][R8aSavedSerialization] PASS|FAIL checks=N failures=M` line; on failure extra `... detail: <label>` lines precede it. Batch mode exits 0/1.

## Root must run
1. Apply MOVES first (preserve .meta), then copy apply/ over the project. Open/compile: expect zero CS errors; check Console for asmdef cycle or "type not found" errors.
2. EditMode tests: `powershell -File Tools/Run-EmberdelveEditModeTests.ps1 -ProjectPath <project> -MinimumTests 14` (default filter `Emberdelve.Rules.Tests`; Editor must be closed - script refuses if Temp/UnityLockfile exists). Success line: `PASS EditMode tests=14 failures=0 exit=0` (script exits 0; requires status Passed, no skipped/inconclusive, no CS errors in log). Evidence in Logs/editmode-*/summary.json.
3. Saved gate: `Unity.exe -batchmode -nographics -projectPath <project> -logFile Logs/r8a-saved.log -executeMethod Emberdelve.EditorTools.R8aSavedSerializationGate.Run` expect `PASS checks=N failures=0`, exit 0. (-nographics is fine; scenes open without rendering.)
4. Regression gates: existing Emberdelve*Gate executeMethods, at minimum EmberdelveAttributeRulesGate.Run ([Emberdelve][AttributeRules] PASS), EmberdelveLootPipelineGate, and the combat/UI gates.

## Risks
- Internals: Runtime internals used by Emberdelve.UI/Review/Editor (previously same assembly) are not exposed except the LocalCombatAuthority IVT for Editor; any `internal` Runtime member used by UI or Review will give CS0122. Fix by making public or adding IVT. Editor use of UI/Review internals is covered by the new AssemblyInfo files; Editor use of other Runtime internals is only via the existing IVT in LocalCombatAuthority.cs.
- Asmdef boundaries are discovered at compile: any Runtime code referencing UI types, or tests/editor referencing types in assemblies not listed, fails to compile. Asmdef-less folders under Runtime (Combat, Core, ...) all fall into Emberdelve.Runtime.
- Editor code that used Assembly-CSharp-Editor-only conveniences (e.g. `Emberdelve.Editor` namespace collisions) untested.
- Scenes/assets keep stale `Assembly-CSharp::` in m_EditorClassIdentifier until next re-save by Unity (harmless; Unity rewrites on save, producing churn then).
- Unity may regenerate Packages/ or csproj files; tests asmdef uses `precompiledReferences` nunit.framework.dll (valid for TF 1.5.1).
