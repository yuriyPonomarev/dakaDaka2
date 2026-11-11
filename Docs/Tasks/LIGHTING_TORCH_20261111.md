# LIGHT-01: source-based lighting and usable torch

User scope: real torch inventory item that can be drawn to illuminate, remove unexplained glowing hands/weapons, improve light depth, allow nearby players to extinguish/relight authored illumination. Moon is environment lighting, not an interactable object.

Plan: remove legacy invisible camera torch; preserve actual moon and warm world sources with shadows/falloff. Torch uses existing item catalog/equipment and replicated weapon presentation, carried/stowed light off, equipped visible light on. Local world sources use server-validated interaction with reach/LOS and synchronized lit state including latejoin. Rendering must match state (light, flame and any glow). Admission protocol11; mixed old builds refused.

Ownership: root integration/evidence/protocol; light_sources source/render polish; world_lights world interaction/state; inventory_torch held item/presentation/assets. Root sole Unity/mirror executor. Preserve inherited materials/import/log/config drift.

Checkpoint Logs/harness/before-lighting-torch.json. Acceptance: focused item/light authority tests, affected visual/inventory/combat/interaction gates; real host/client equipped/stowed torch, nearby source toggle, latejoin state, dark-vs-lit hand comparison. Evaluate final plan rather than blind full suite. No completion/commit until evidence recorded.

Integration evidence so far: harness routing15/0; focused lighting/visual40/0 in Logs/runs/lighting-focused; inventory initialization/hands7/0 in Logs/runs/lighting-inventory. First test filter matched40 only because combat namespace differs; the missing7 were run separately without repeating40. Planner selected70 from shared builders/protocol; narrowed to ItemCatalog, Hands, Visual, NetInventory, NetDoor, NetFinal, NetCombat plus live lighting acceptance.

Catalog gate exposed prior serialization drift: nine weapon assets missing null recoveredAmmo or storing existing ammo refs at end, plus NavGrid missing empty surface arrays. Verified differences contain only default serialization/order; adopted canonical builder output to preserve idempotent catalog checks. Repeat only ItemCatalog after integration.

Integration run Logs/runs/20261111-150908-697: Hands92/0, Visual207/0, NetInventory148/0, NetDoor37/0, NetFinal172/0, NetCombat312/0, build PASS. ItemCatalog143/1 only serialization drift described above; corrected canonical files, isolated rerun Logs/runs/20261111-151813-933 ItemCatalog143/0.

Live first pass caught two defects: torch flame above camera; client toggle packets rejected because NGO reader.Length includes consumed header. Fixed torch-only framing and used remaining bytes (Length-Position); new wire regression4/0 plus touched visual fixture35/0 in Logs/runs/lighting-request-fix. Hands92/0, Visual207/0 and build PASS in Logs/runs/20261111-152734-155.

Live seed11/protocol11 evidence Logs/acceptance/20261111-lighting-final: normal inventory right-click equips torch; Q removes its light; client E toggles fixture13976755945691059607 at(31.2,1.8,16.8), server logs accepted sender1, host/client/latejoin share off then on. Late client joined while fixture off and received false before relight. Close bowl correctly blocks chest-height line of sight; ordinary approach at1-2m works. No exceptions/shader errors in three players. Host toggle fixture16516207857994587358 tested earlier in Logs/acceptance/20261111-lighting. This pass exposed an extra camera weapon rendered on the headless server actor; resolved below.


## Completed acceptance

Final cosmetic correction: ClassLoadout hides camera blockout renderers for nonlocal rigs while retaining authority geometry. RemoteWeaponPresentation owns the world model. Torch cloth shortened and flame flicker pivot moved to the flame itself, removing the covered fire cap. Torch-only ready framing does not alter accepted sword/bow poses.

Final build: Builds/Playable/20261111-154101-549/EmberdelveNet.exe. Hands92/0, Visual207/0, build PASS in Logs/runs/20261111-154101-549. Live seed11/protocol11 in Logs/acceptance/20261111-lighting-polished: client equips the UID item with normal inventory right-click; owner flame stays in frame; host walks through the normal door and observes remote torch flame plus illumination. Q switches to the sword: both remote flame and warm floor illumination disappear, and return after Q again. Screenshots host-final/snapshots/remote-relit.png, remote-stowed.png; client-final/snapshots/torch-polished.png. No player exceptions/shader errors; all review windows closed. Main Editor untouched.

Combined acceptance uses the passed network baseline plus targeted packet regression/live client and latejoin proof, then final visual-only corrections and their checks. It does not claim another full network suite on the cosmetic final build. No broad suite was repeated. Completed LIGHT-01; ready to play.

Controls: I opens inventory; right-click Pitch Torch equips the active main-hand slot (can also select another weapon slot). Q swaps weapon sets and thereby draws/stows the torch. E near a world light extinguishes/relights it. Server validates actor, reach and line of sight. Both classes start with one torch. Normal inventory/drop/loot identity paths are reused.

Limits: original simple flame meshes and small depth-occluded local scattering, not full room-scale volumetric fog; moon remains global environment illumination. Torch occupies a weapon slot, not a new dedicated hotkey/offhand system; no fuel consumption. Existing unrelated material/import/log/Pipeline changes remain excluded. Canonical serialization of old weapon/NavGrid fields was included solely to close the catalog idempotence failure without changing their gameplay values.
