# Network enemy identity and animation state

> Historical implementation note. Current acceptance and remaining work: [CURRENT_WORK](../CURRENT_WORK.md) and [raid acceptance](RAID_UI_ACCEPTANCE_20261108.md). Status statements below describe the original slice.


## Behaviour

- `EnemyPresentationWire` version 1 carries server-authored visual kind, windup/swing state, hit sequence and death over a reliable owner RPC. Connection approval uses protocol 2, so older clients cannot silently accept the new payload contract.
- The server reads the actual `EnemyVisualDriver.Animator.runtimeAnimatorController` name (`EnemyVisual_<Variant>`). Only when that binding is absent does it fall back to elite/Minion. The client selects matching Resources prefabs for Minion, Rogue, Warrior and Mage.
- Client proxies have no AI, colliders, health or local blood driver. Network blood remains on the existing `NetCombatFx` path. Their locomotion uses real pose deltas. Editor and Development builds use `AlwaysAnimate` for deterministic offscreen state-entry diagnostics; production builds retain the Animator's normal culling mode.
- Attack starts on the server windup rising edge (or an accepted swing edge without a sampled windup). Hit edges wait until windup/attack playback suppression ends, then play once; death discards queued flinches. The Entry owns the hit queue before a proxy exists: its first reliable presentation seeds the historical baseline and later sequence changes share the queue transferred to the proxy. A late join does not replay baseline hits. Multiple hits during a busy attack intentionally coalesce into one flinch because the controller has one non-overlapping Hit trigger.
- Death proxies remain through their death clip. Four Resources prefab copies reference the existing licensed assets; source/license details remain in `Docs/ASSET_PLAN.md`.

## Verification

- Latest verified NetCombat/PvE gate: **312/0** at `Logs/runs/animator-state-proof-20261107/fixed/NetCombat/summary.json`; focused `EnemyHitPresentationQueue` EditMode tests: **4/4**. Both clients received multiple server-authored visual kinds, including late-join kind replication, and the gate confirmed actual AnimatorStateInfo entry into Attack, Hit and Death for both clients. Existing combat, authority and blood assertions remained enabled and passed.
- Runtime state-entry probes and the related action/presentation logs are limited to Editor and Development builds. `AlwaysAnimate` is likewise development-only to support deterministic gate observation.
- No rendered animation review is claimed: state-machine entry does not establish how the animation looks in motion. A graphical review of skeletons, impact timing and blood remains pending.
