# MAP-03 — room identity and ruined architecture

11 November 2026; baseline6f8387a1. Accepted after focused proof and two-layout live traversal.

Core loop: read a room, choose an exposed direct route or a sheltered approach, fight/avoid its enemies, loot, then use landmarks to choose the next room or return to extraction. Existing enemies/PvP and carried loot supply uncertainty and stakes. Distinct silhouettes improve spatial memory; clear stair/door approaches give immediate feedback about traversable routes. This adds context to the existing combat/extraction loop rather than new progression rules. Playability check: extraction goal, existing dynamic opposition, route choice, visible geometry/collision feedback, inventory loss/reward all remain present. No collectible checklist, new economy or unrelated mechanics.

Bounded art/content pass: saved10 modules retain IDs/order/sockets, open sky and existing18 terraces. Non-vault perimeter walls gain uneven fractured crowns and masonry details; base remains continuous and visible collision matches it. Room-specific wall landmarks distinguish watch entrance, stores, cloister, fallen effigy, burial niches, ossuary wheel, chapel rose, treasury, extraction waymark and bridge pier. Worn paving marks ground approaches; imported props grouped by purpose. Existing lights remain interactive; no new light system.

New geometry is confined to perimeter strips and existing prop quadrants; axes and NE/SW stairs remain clear. Vault remains sheltered. Current heights/stairs, dormant enemies and breakable barrel logic preserved. Protocol14 prevents old geometry clients joining the changed build.

Acceptance: rebuild/import in mirror; validate all door approaches and terrace navigation; inspect eye-level previews; scoped level/movement tests and NetMovement/NetFinal/NetDoor/NetInventory; live traversal on two seeds. Root sole Unity executor. Preserve unrelated materials/import/log/Pipeline drift; commit only after acceptance.

## Evidence so far

- Logs/runs/map03-import/final.log: rebuild and40 clear door approaches, all18 terraces reachable; five eye-level previews generated. Initial review harness caught an unrelated Lobby wall, so validation now uses an isolated empty scene; previews then reopen a named scene to satisfy Unity's additive-preview restriction. Only affected import/preview steps repeated. Courtyard arches gained matching2.6m stone supports after visual inspection found their original floating ends.
- Logs/runs/map03-level/results.xml:43/43 EditMode level/navigation tests passed.
- Planner selected70 due shared content inputs; bounded scope is these existing level tests plus NetMovement/NetFinal/NetDoor/NetInventory. AI/combat rules and barrel logic are unchanged. No new cosmetic tests.
- Generated wall meshes are shared across nine open room prefabs; Vault keeps its enclosed walls. Source helpers/canonical builder and generated prefabs will be committed together.

## Final acceptance

- Logs/runs/20261111-200854-084: NetMovement190/0, NetFinal172/0, NetDoor37/0, NetInventory148/0; build PASS. The proof player was built once and reused by the later scenarios (verified build-cache hits, never cached test passes). Playable: Builds/Playable/20261111-200854-084/EmberdelveNet.exe, protocol14.
- Seed11: Logs/acceptance/20261111-map03-11. Host took a torch, ascended/descended the entry terrace and crossed east doorway; client crossed the opened seam back into entry. terrace-overlook.png, next-room.png and client-entry.png show the changed wall silhouettes, supported arches, paving and landmarks.
- Seed2043687868: Logs/acceptance/20261111-map03-2043687868. East entry closure correctly blocked that absent route; host used north door into a different courtyard. Client navigated the rotated stair, reached server y1.830 and descended to y0.030. terrace-overlook-final.png and north-room.png record the second layout. Ordinary keyboard/mouse input only; enemies disabled for these traversal reviews. No new claim about combat/dormant timing (unchanged from prior accepted slice).
- Both host/client pairs closed normally; no exceptions/errors in review logs. Main Editor untouched. Previews under mirror Logs/map-03 are lit editor views; standalone screenshots retain actual game darkness/retro rendering.

Limits: this improves the existing ten-module ruin kit, not a replacement hand-authored campaign or final environment art. Room connectivity, solid terraces, ground enemy spawn positions and loot distribution are unchanged. No new roofs, overlapping floors, secrets, enemies or barrel drops. Geometry/collision and visual route readability are accepted; performance is not a new benchmark claim.
