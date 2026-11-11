# POTION-02 — uninterrupted drinking

User correction, 11 November 2026. Supersedes the cancellation rule from POTION-01.

Once begun, drinking continues through movement, nonlethal hits and repeated input. Inventory authority rejects changes during the channel, including weapon/quick-item selection and dropping. Direct hand/weapon selection also retains the held potion. Death, leaving play and component teardown can still abort lifecycle work without healing a dead player. Completion consumes one potion even if regeneration fills health before the deadline; starting at full health remains refused. Tooltip and authored tonic description updated. Animation and protocol13 unchanged.

Verification scope: planner for the five runtime files conservatively selected70 gates. Narrowed to existing consumable timing/real-input content, inventory authority and NetInventory. No cosmetic tests or full suite repeated. Checkpoint took time to finish; HEAD8ff7ebdd and explicit changed paths are the scope, not the checkpoint.

Logs/runs/potion-lock: LoadoutD3c211/0, InventoryAuthority116/0. Initial D3Content53/1 had one stale assertion expecting the Blood Draught to remain instant from before POTION-01; movement/damage non-interruption on the real game clock passed. Corrected that assertion to1.8 seconds; only D3Content is repeated.

NetInventory148/0 passed in the same run. Corrected D3Content53/0 passed in Logs/runs/potion-lock-content, including actual LMB/W input and damage with completion on the real game clock. No separate cosmetic acceptance run: model/poses are unchanged from POTION-01. Final playable build PASS: Builds/Playable/20261111-185112-164/EmberdelveNet.exe. Inherited material/import/log/Pipeline changes excluded.
