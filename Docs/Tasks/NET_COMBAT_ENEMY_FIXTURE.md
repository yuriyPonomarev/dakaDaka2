# NetCombat enemy-hit fixture

> Historical implementation note. Current acceptance and remaining work: [CURRENT_WORK](../CURRENT_WORK.md) and [raid acceptance](RAID_UI_ACCEPTANCE_20261108.md). Status statements below describe the original slice.


## Change

The enemy-hit proof now opts into a development-only server fixture. Once both proof clients are admitted, the server moves `ene-a` to a clear staging point 1.25 m from a live enemy. It finds a point with no blocking geometry between the melee eye and enemy center and logs the placement. The gate waits for that placement before releasing the attack script.

The old failure was a test-driver reachability problem: its direct-line steering hit level geometry, so the script timed out with zero `AttackPress` intents. The fixture fixes the starting geometry without manufacturing a hit. The client still sends normal movement and `AttackPress` traffic; the server still runs the ordinary combat authority, swept hit geometry and wall checks, and both clients must receive the same real hit and blood event.

## Files

- `Assets/Emberdelve/NetRules/NetLaunchArgs.cs`
- `Assets/Emberdelve/Net/NetServer.cs`
- `Assets/Emberdelve/Editor/EmberdelveNetCombatGate.cs`
- `Assets/Emberdelve/Tests/EditMode/NetRules/CombatNetScriptAndArgsTests.cs`

## Verification

- Static diff and argument-path review completed.
- `git diff --check` passed for the changed files.
- Unity compilation, EditMode, and mirror runs were not started because a full mirror run was active.
