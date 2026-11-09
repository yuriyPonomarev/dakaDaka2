# Network client enemy skeletons

> Historical implementation note. Current acceptance and remaining work: [CURRENT_WORK](../CURRENT_WORK.md) and [raid acceptance](RAID_UI_ACCEPTANCE_20261108.md). Status statements below describe the original slice.


## Scope

Client enemy proxies now instantiate the existing Minion skeleton visual from `Resources/Emberdelve/NetEnemyVisual.prefab`. Their movement animation speed is estimated from consecutive authoritative position snapshots; the proxy root still eases toward the newest sample. If the resource is unavailable, the proxy uses the previous capsule presentation.

The pose wire currently carries position and yaw only, so client views use the same Minion appearance for every enemy and cannot reproduce enemy-specific attack, hit, death, or health presentation. Proxies have no collider, AI, health, or active blood feedback; gameplay remains server-authoritative. The visual uses the existing Grab3D CC0 skeleton and Quaternius CC0 animation assets documented in `Docs/ASSET_PLAN.md`.

## Checks

Unity was not launched and no compile or test run was performed. The existing dirty `TryGetNearestAliveProxy` change in `NetEnemyViews.cs` was preserved.
