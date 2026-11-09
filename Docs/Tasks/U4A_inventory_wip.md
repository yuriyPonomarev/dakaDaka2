# U4A network inventory — work in progress

> Historical implementation note. Current acceptance and remaining work: [CURRENT_WORK](../CURRENT_WORK.md) and [raid acceptance](RAID_UI_ACCEPTANCE_20261108.md). Status statements below describe the original slice.


Server inventory intents validate sender ownership and admit sequenced commands through the player's authority. Owner clients request and receive session snapshots carrying the server's stack IDs and revision. The client queues one request, waits for its confirmed revision, and retries synchronization on refusal or timeout. Client inventory UI and hotbar input are wired to the network authority. A modal clears queued movement history; a packet already in flight cannot be recalled.

Unity compilation succeeded; EditMode 755/755 and fast gates 12/12 passed after integration. Full Run-All is pending on the integrated tree. These checks do not establish end-to-end correctness: the dedicated server and two clients must exercise the real UI, ownership, stale/replayed requests, snapshot/reconnect and a contested chest or pickup. Contested containers, network drop/pickup and final U4A acceptance remain unfinished. Old and new network builds must not be mixed.

Follow-up `Logs/runs/20261105-115642`: EditMode 757/757, NetSeams 163/0, NetInventory 28/0. A dedicated server and two clients accepted A's real move, delivered the resulting revision/snapshot to A, and rejected B's RPC against A's object without changing either inventory. This scripted test does not prove the graphical UI, reconnect, contested chest, or network drop/pickup.

## Authoritative chest interaction implementation

Online clients now send chest-open intents through their owned player `NetworkObject`; they no longer roll or grant chest loot locally. The server resolves the sender/object through the admitted identity table, validates a bounded unique chest ID, then calls the existing chest interaction path with the server-side `PlayerRig`, which re-checks admission, authoritative distance and LOS. A successful grant consumes the shared server chest instance before another request can run. Server-authored open/remaining-unit state is snapshotted to joining owners and broadcast to all owners after requests; clients block interaction until the complete chest-ID snapshot matches their loaded scene and show owner-only request results.

Follow-up `Logs/runs/20261105-163453`: EditMode 773/773, NetSeams 163/0, NetInventory 51/0. Two admitted clients attempted the same reachable chest through production RPC. The server accepted one and rejected the other because the chest was empty; both clients converged on empty state and the exact fixture UID appeared only in the winning owner's snapshot. The development fixture positions actors and injects the known item, but production admission, range and LOS remain active. Chest content/UID details are not replicated to other clients. Actual UI, interrupted hold, reconnect and network ground pickup remain to verify. The subsequent layout-switch fix passed 774/774 local EditMode tests.

Follow-up status (7 Nov): standalone network floor-loot proof passed 26/0 at `Logs/runs/20261107-044115/gates/summary.json`. A broader NetInventory run `Logs/runs/20261107-040249/summary.txt` failed 146/6: server rejected an occluded pickup incorrectly, concurrent owners did not yield the expected single successful UID transaction, and client B did not converge. These keep full U4A acceptance open; do not infer drop/reconnect/visual UI coverage from the standalone floor-loot PASS.
