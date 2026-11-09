# GateSuite domain-reload startup fix

> Historical implementation note. Current acceptance and remaining work: [CURRENT_WORK](../CURRENT_WORK.md) and [raid acceptance](RAID_UI_ACCEPTANCE_20261108.md). Status statements below describe the original slice.


## Change
- `EmberdelveGateSuite` now separates gate preparation from gate invocation. It waits until a subsequent editor update, after the editor's compile/update guard, before invoking the gate method.
- The `invoke` phase is serialized in `SessionState`; if Unity reloads the scripting domain between preparation and invocation, the suite resumes the pending gate rather than timing out or dropping it.
- This keeps BuildPlayer success validation unchanged. `BuildResult.Unknown` and missing outputs still fail.

## Evidence and limits
- Investigated `Logs/runs/20261106-045438/gates/unity.log`: startup asset refresh completed, then NetInventory invoked `BuildPipeline.BuildPlayer` synchronously from `EditorApplication.update`; Unity returned `A domain reload is pending` and `BuildResult.Unknown`.
- Unity was not launched for this change. The mirror should rerun NetInventory to confirm the startup timing fix and retain the full BuildPlayer and two-client gate checks.
