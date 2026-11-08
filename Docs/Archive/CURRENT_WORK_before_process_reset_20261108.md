# CURRENT WORK - 8 Nov 2026

Rewrite this file each slice; history is in `Docs/Archive/`.

## State

- The modular 3×3 crypt is the only gameplay mode. The catalog includes the central `Crypt_Skybridge`; planner/network layout proof for seeds 12345 and 12346 is documented in `Docs/Tasks/MODULAR_DUNGEON_NETWORK_PROOF.md`. Bridge-at-center remains an explicit acceptance check. The map and HUD have not had visual review.
- Door replication and server AI doorway flow passed NetDoor **37/0** at `DakaDaka_ci/Logs/gate-netdoor-hearing-20261107-230745/summary.json`; focused `DungeonDoorInteractableTests` passed **11/11**. A real server EnemyAgent moved **2.187 m** toward the correct live target, opened the closed door, and advanced revision to 3. Both clients received matching open state/revision. Closed panels block movement until authority opens them. See `Docs/Tasks/MODULAR_ENEMY_DOORS.md` and `Docs/Tasks/MODULAR_DUNGEON_WIP.md`.
- The dedicated equipment-drop live proof passed **17/0** at run `20261108-001556`: a real owner dropped an equipped Rare chest item with affixes; owner, peer and late join each saw its exact UID and affix snapshot once. Focused equipment rollback tests passed **10/10**, and Hands tests **6/6**. Forced publication failure is covered by focused tests only, not by a network scenario. Visual review is still blocked by app approval. Broader NetInventory passed **146/0** at `Logs/runs/20261108-042623/summary.txt`, but the subsequent full run failed **42/4** after a client was displaced by a pillar. After adding clearance and production LOS checks to fixture placement, both previously failing seeds passed **146/0**: `Logs/runs/20261108-050856` and `Logs/runs/20261108-051115`. Coverage across other layouts remains unproven; see `Docs/Tasks/NET_INVENTORY_CLEARANCE_20261108.md`. Real client UI and reconnect remain to be reviewed. See `Docs/Tasks/NET_INVENTORY_DROP_STATUS.md`.
- U4B extraction/corpse/timer runtime proof passed **NetExtraction 136/0** at `DakaDaka_ci/Logs/focused-net-extraction-r5-20261106/summary.json`. Corpse gear is session-only; persistent stash is deferred. Timer remains disabled pending HUD visual review. See `Docs/Tasks/U4B_two_client_gate.md` and `Docs/Tasks/U4B_corpse_loot.md`.
- Skeleton/enemy network presentation passed **NetCombat 312/0** at `Logs/runs/animator-state-proof-20261107/fixed/NetCombat/summary.json`; both clients entered Attack/Hit/Death states, including late join. Rendered animation quality and blood still need in-motion visual review. See `Docs/Tasks/NET_ENEMY_ACTIONS.md` and `Docs/Tasks/V5_skeletons.md`.
- U5A/U5B weapons and hand-slot behavior are implemented but uncommitted; feel and first-person visuals need play review. Equipment-drop success and owner/peer/late visibility are proven; network forced-publication rollback and visual review remain open. See `Docs/Tasks/U5A.md`, `Docs/Tasks/U5B.md` and `Docs/Tasks/NET_INVENTORY_DROP_STATUS.md`.
- Prior full Run-All PASS (`Logs/runs/20261105-111228`) predates later changes and does not verify the current tree.

## Next

- Visually inspect the modular map, bridge, module seams, doors, extraction, and timer/corpse UI; keep the production timer disabled until the HUD is legible.
- Verify real client inventory UI and reconnect; complete visual review when available.
- Resolve NetFinal, NetMovement and RuntimeSmokeTest failures from `Logs/runs/20261108-042911/summary.txt`; rerun full mirror verification after current integration changes. Keep persistent stash deferred (U4C).
- Review skeleton/blood animation and sword feel in play.

## Known limits

- The door AI gate proves server movement, authoritative open and client state delivery; it does not prove visual quality or automatic alternate-route handling.
- NetInventory passed a targeted rerun after the fixture began rejecting occupied player positions; its stability across layouts remains unproven. Real client UI and reconnect remain unverified; U4A stays open.
- Map, HUD, corpse UI, enemy animation, sword feel and equipment-drop visuals still need review. Equipment-drop replication has targeted runtime proof; forced-failure rollback has focused tests only, not a network proof.

- Full run `20261108-042911` failed: NetFinal 168/3, NetInventory 42/4, NetMovement 171/15, RuntimeSmokeTest combat 236/15, DrawSpeed 191/8 and EditMode 877/878. Follow-up DrawSpeed passed 203/0 at `20261108-045120`; final NetInventory passed 146/0 on both previously failing seeds at `20261108-050856` and `20261108-051115`; EditMode passed 878/878 in the open Editor after correcting the test protocol version. These targeted results do not make the full run green.

- Modular spawn clearance now searches deterministic supported, unoccupied player positions and refuses an invalid layout. EditMode 879/0 and NetDoor 37/0 passed on the observed problematic seed (`Logs/runs/20261108-052059`); NetInventory 146/0 passed on another seed (`Logs/runs/20261108-052347`). NetMovement remains unaccepted (171/8 before the support check): its old route and spawn expectations do not match modular maps. See `Docs/Tasks/MODULAR_SPAWN_CLEARANCE_20261108.md`.
