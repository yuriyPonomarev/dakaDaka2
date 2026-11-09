# Modular crypt — enemy door opening

> Historical implementation note. Current acceptance and remaining work: [CURRENT_WORK](../CURRENT_WORK.md) and [raid acceptance](RAID_UI_ACCEPTANCE_20261108.md). Status statements below describe the original slice.


## Behavior

Enemies remain physically blocked by closed door panels. When an enemy's movement sweep is stopped by a closed doorway, it may open that door only while pursuing a live player target and within 1.5 m of the door. Attempts are throttled by a 1.25 s cooldown. Offline play applies the local state; networked play requires the server-owned door service and publishes through the existing revision and door-state broadcast path. Enemy AI does not request a close, and clients cannot mutate door state.

## Checks

- Focused `DungeonDoorInteractableTests` passed **11/11**.
- Development NetDoor gate passed **37/0** at `DakaDaka_ci/Logs/gate-netdoor-hearing-20261107-230745/summary.json`. Its bounded server stage enabled one authored EnemyAgent, put a live target across a closed door, and observed **2.187 m** of actual movement toward the correct target before opening. Server revision advanced exactly once to **3** and the blocker disabled.
- Both connected clients observed the replicated open state with the same revision through the gate's client receive checks. This confirms state delivery; it is not a visual review.
- The first live AI gate exposed a real perception bug: an occluded but heard target entered Chase, then immediately fell into Investigate because no last-heard point or timestamp existed. A new sound acquisition now records one search point and starts a bounded awareness window. Continuous hearing does not refresh the exact hidden position; after memory and investigation expire, the brain returns to patrol.
- Added an EditMode brain case with a live target behind an actual occluder and inside hearing range. It asserts movement to the heard point, no per-frame tracking of the hidden target's changed position, and end of chase/investigation.
- No map, door animation, enemy movement quality or HUD visual review was performed. Network equipment drops and peer visibility remain open work.

## Files

- `Assets/Emberdelve/Runtime/AI/EnemyAgent.cs`
- `Assets/Emberdelve/Runtime/AI/DoorwayMovementBlocker.cs`
- `Assets/Emberdelve/Runtime/Interaction/DungeonDoorInteractable.cs`
- `Assets/Emberdelve/Net/NetInventoryServer.cs`
- `Assets/Emberdelve/Tests/EditMode/Level/DungeonDoorInteractableTests.cs`
