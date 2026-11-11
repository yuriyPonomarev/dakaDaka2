# QUICK-01: explicit hand state and two utility wheels

Completed 11 November 2026. Supersedes LIGHT-01 controls. Protocol 12; snapshot schema 5.

Q/E activate abilities (archer prepares on Q/E, fires on LMB); 1/2 directly select weapon sets. Hold 3/4 for separate three-entry wheels, choose with mouse and release to hold. F interacts. Inventory hover/selection + hold 3/4 assigns a slot; right-click a utility takes it in hand and closes the bag without consuming it. LMB uses the held item. Both weapons disappear while a utility is held, locally and for observers. The held stack stays in the bag with its identity; the server rejects unheld use and weapon attacks. Changing hands cancels drinking; consumption cannot trigger a same-frame weapon attack.

Six belt assignments preserve old four-slot snapshots and checksum validation. Owner snapshots preserve local wheel assignments across updates. Held state is session-only. The selected weapon ammo pool remains seeded while weapons are stowed; returning to weapons cannot refill or lose ammo. Utility models reuse the torch and add simple potion/ammo models. HUD names the held item and displays current controls.

## Evidence

- Focused EditMode: 20/20, Logs/runs/quick-hands-focused-fixed (migration, exact held authority, owner snapshots and avatar wire).
- Logs/runs/20261111-161311-895: Slots442, RarityLadder917, InventoryStackId56, Hands92, Visual207, NetCombat312, NetSkill127 all passed. Its initial failures led to fixture updates and the ammo fix; this is not a green aggregate.
- Logs/runs/20261111-161951-102: ContainerSnapshot1026, LoadoutD3c202, InventoryAuthority114, D3Content53, InteractionSpeed192, NetInventory148 all passed. InventoryAuthority includes held-ammo kit seeding/use/return regression. A transient Windows proof-file replacement conflict was fixed with bounded atomic retries in the gate.
- Logs/runs/20261111-162703-147: SkillCooldown253/0 and final playable build passed. Quick UI had139/0 assertions but Null graphics failed rendering the existing portrait; it is not counted as a gate pass.
- Logs/runs/quick-hands-graphical: standalone RunQuickAccessUiFlow with -batchmode and an actual graphics device (omit -nographics), exit0,139/0 and no exception. The same actual-input fixture assigns all six sectors, uses LMB, validates identity and HUD.
- Live seed11/protocol12 host warrior + client archer: Logs/acceptance/20261111-quick-hands. Both wheels, second-wheel torch assignment, held potion/full-health refusal, direct1/2 including repeated1, E BattleCry, Q prepared MultiShot, F door, observer torch/light and stowing verified. This first visual run predates only ammo bookkeeping and final label polish.
- Final build Builds/Playable/20261111-162703-147/EmberdelveNet.exe visually checked in Logs/acceptance/20261111-quick-hands-final: potion HUD, F objective, wheel spacing, client torch selection. Both final windows closed cleanly. Main Editor untouched.

## Scope and remaining issues

Planner selected70 gates because schema/input are shared; only affected proof above was run, not the full suite. The old full RunInventoryUiFlow still contains pre-paper-doll row/label/layout assumptions (initial473/42 run); focused quick-access coverage is separate. Updating those unrelated old layout assertions is future test maintenance, not claimed fixed here. The portrait-dependent UI flow needs a graphics device. Simple bottle/ammo geometry is intentional; fuel and further model polish are outside this slice. Client wheel assignment retention covers owner updates; rejoin follows existing new-actor/session semantics.

Inherited five material files, Human.fbx.meta formatting, Assets/Logs and Pipeline project config remain outside this milestone. Checkpoint: Logs/harness/before-quick-hands.json.
