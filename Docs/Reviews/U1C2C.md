# U1C2C — partial; complete acceptance withheld

Worker settled38calls, controller24532/Pi26232 absent. Independent diff against before-U1C2B checkpoint reviewed: per-widget pivot overload retains old center default; corner HUD anchors corrected; skill label bounds corrected; inventory panel-local geometry, top-left equipment origin, wrapped instructions. Unrequested Rebuild visibility public->private observed, no external compile consumer found. No item transaction logic change in targeted diff.

Independent inventoryui gate157/0, exit0, unique PASS, no captured errors/timeout. Windows build exit0, exactly one PASS resultSucceeded, valid binary/Data layout, no captured errors. Preserved manager review-U1C2C-inventoryui.json and review-U1C2C-build.json. No need to rerun entire old combat/items gates for geometry-only change.

Real windowed Player3616 at1280x720: ordinary menu mouse descended, I opened inventory; inspected hud-1280.png and inventory-1280.png in validation/U1C2A-player-20261019-060230. Frame/time advanced, HUD no longer clips left; all five gear slots and inventory/instructions visible. Player21308 at1920x1080 independently launched and same ordinary input opened inventory; actual inventory-1920.png inspected in validation/U1C2A-player-20261019-060310; all current gear rows visible and panel contained. Both logs searched for Exception/Error/FAIL/No InputProcessor found none. Both quit acknowledgements received; actual Editor29704 and Player3616/21308 absent afterward. Input was queued normal InputSystem, not physical OS mouse.

Worker correctly reported legacy HUD inventory strip creates40 capacity chips x96 in a single row, putting first four hotkeys offscreen. Independent source HudController.RebuildInventory confirms capacity drives row, whereas Runtime/Items/HotbarController consumes only indices0..3. Full 10x4 satchel remains separately displayed. Manager decision: four actual hotkey stack slots only in raid HUD, plus compact bag usage/value/I hint. U1C2C-R1 implements this narrow continuation, no gameplay semantics change. Existing U1C2C fixes retained in checkpoint; no rollback.

Remaining: four-slot strip/readability and its empty/populated behavior, ordinary pointer equip/unequip in actual populated game, chest/death/extraction/result loop; full U1C2 aggregate remains OPEN. Duplicated MP description text/current locale key glyphs are visible UI polish findings, not accepted final U5 readiness. Six stats/UID/network/persistent stash still pending later milestones.


## Actual populated UI continuation after F
Same fresh Player evidence Docs/Reviews/U1C2F.md: realEentrysupply loot, pointer equip/unequip boots, actual potion consumption and live stack shift all inspected. Four-key HUD refresh behavior now established. R1 accepted in scope; parentC still needs worn-label contrastR3 (black text outside colored slot on dark panel). Prior720p/1080p geometry and157/0 handler gate remain valid. No fullU1C2/extraction acceptance.


## Final current-scope acceptance after R3
R3 freshbuild+actual pointerequipPNG verifies readable light wornlabel, preserving geometry. Accept U1C2C HUD/inventory containment and current live four-keybag UI with previously verified720p/1080p, handler157/0, normal loot/equip/unequip/potionuse/stackshift evidence. Scope/limits Docs/Reviews/U1C2C-R3.md. Does not accept fullU1C2/gameplay extraction or U5finalpolish.
