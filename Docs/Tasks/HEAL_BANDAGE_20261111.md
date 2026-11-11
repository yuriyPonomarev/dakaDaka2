# HEAL-01 — gradual potions and interruptible bandages

11 November 2026; baseline dd8441c2. Status: accepted after focused proof and standalone host/client review.

User rule: potions heal gradually; bandages apply slowly, heal immediately on completion, and may be interrupted. Initial authored tuning:

| Item | Use time | Healing | Interruption |
|---|---|---|---|
| Blood Draught |1.8s|45HP over4s afterward|No player cancellation|
| Smoldering Tonic |1.8s|45HP over4s afterward|No player cancellation|
| Linen Bandage |5s|25HP at completion|Movement above0.6 walk-speed ratio, damage, switching hands or explicit cancellation|

No early consumption or healing; cancelled bandages retain their unit and pay nothing. Death/leaving play/teardown stop use safely. Potions continue their already-committed gradual heal after the bottle is consumed and weapons return. Server owns all outcomes. The existing protocol13 channel clock drives presentation; ItemData adds a default-compatible Potion/Bandage kind.

Bandage is registered in the catalog and canonical builder, with one in each class's starter inventory. A matte linen roll replaces potion visuals for bandages in hand and on the ground. Both views show the right hand winding near the left wrist. No bandage glow.

Planner on shared runtime/data files conservatively selected70 gates; scope narrowed to affected LoadoutD3c, D3Content, quick-accessUI, Hands, Visual, InventoryAuthority and NetInventory. Tests extend existing fixtures rather than adding a duplicate suite.

Logs/runs/heal-bandage: D3Content53/0, QuickAccessUi143/0, Hands92/0, Visual207/0, InventoryAuthority116/0. Initial LoadoutD3c244/1: a test used a malformed gear-only Drop command on a consumable. Fixed to a valid Move command and retained rejection reason, revision, position, count and active-use assertions; only that gate is repeated. NetInventory148/0 passed in the same run. Corrected LoadoutD3c244/0 passed in Logs/runs/heal-bandage-final. Build PASS: Builds/Playable/20261111-190017-299/EmberdelveNet.exe.

Inherited material/import/log/Pipeline changes excluded. No package changes. Main Editor untouched.

## Standalone acceptance

Seed11, protocol13, host warrior/client archer, ordinary review keyboard/mouse commands. Evidence Logs/acceptance/20261111-bandage/{host-polish,client-polish}/snapshots.

- held.png shows the linen roll; applying.png and observer-applying.png show both hands working at the left wrist in first/third person.
- canceled.png: movement interrupts at76/120HP, bandage remains; no payout.
- before-bandage-heal.png at4.4s shows65/120; bandage-finished.png shows90/120, one bandage consumed and weapons restored.
- Potion use then shows93/120 shortly after drinking,114/120 during healing and120/120 afterward, with the bottle consumed and weapons already returned. Live regeneration remains enabled; exact45HP over4s is asserted in the deterministic fixture.
- Both review windows closed. Shared ground bandage model is implemented but not separately inspected in this review. The wrap is a procedural hand/cloth motion, not a persistent dressing or cloth simulation.
