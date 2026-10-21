# P4a: first windowed visual review (HEAD f013d10f, Windows development review player)

Observation only. No source or data edited, nothing committed. Everything below is what was seen in the PNGs or read from the acks/logs. All PNGs are under `C:/temp/emberdelve/p4a/`.

## Commands
- Build (batch, one Unity process): `Unity.exe -batchmode -nographics -quit -projectPath . -logFile C:/temp/emberdelve/p4a/build.log -executeMethod Emberdelve.EditorTools.EmberdelveWindowsBuild.BuildReview --ember-build-output C:/temp/emberdelve/p4a/EmberdelveReview.exe`. Log has `[Emberdelve][BuildWindows] PASS result=Succeeded ... exeBytes=667648`, "Build Finished, Result: Success", exe exists, no `error CS`, exit 0. (A `unity.exe mcp` helper process from the Unity CLI was already running; no Editor.)
- Player 720p: `EmberdelveReview.exe --ember-review-dir C:/temp/emberdelve/p4a/r720 -screen-fullscreen 0 -screen-width 1280 -screen-height 720`. 1080p: same with `r1080` / `r1080b`, `-screen-width 1920 -screen-height 1080`.
- Driven only through `command.json` (script `C:/temp/emberdelve/p4a/tools/c.sh`: write tmp file, `mv` onto command.json, increasing ids, poll `ack.json` until the id matches). Every ack was `ok:true`. 720p session ids 1..145, 1080 session ids 1..31. Mouse coordinates bottom-left origin.
- Quit via `{"action":"quit"}` at the end of each session; `tasklist` shows 0 `EmberdelveReview.exe` after each.

## Frames / liveness
Acks show `frameCount` rising (about 330 to 400 fps, unlimited), `realtime` rising, the dungeon timer on the death screen advancing (00:22 .. 03:00), HP regenerating, enemies hitting. The game keeps rendering and simulating; the world does NOT pause while the satchel is open (an enemy hit me while I read the Character tab).

## Player.log (copies: `Player_720.log`, `Player_1080a.log`, `Player_1080b.log`)
- 0 matches for Exception / NullReference / error in all three. The only warnings: `d3d12: failed to query info queue interface (0x80004002)` and `[App UI] Unable to find an AppUISettings instance, creating a default one...` (Unity.AppUI package warning). The rest is the bridge's own Debug.Log lines with stack traces. Log path: `%USERPROFILE%/AppData/LocalLow/DefaultCompany/DakaDaka/Player.log`.

## Screenshots (720p unless noted) and what I actually see
Lobby
- `r720/snapshots/000_first_Lobby.png`, `01_lobby_warrior_click.png`: title EMBERDELVE, Warrior and Archer cards, class stats readable. The DESCEND button (480-800 x, 599-660 y) overlaps the bottom 30 px of both cards (cards end at y=630). Clicking a card produced no visible selection change (the two PNGs are byte-identical, 104271 bytes). Warrior card is brighter than the Archer card from the start. Footer controls text is small but readable. "Development Build" watermark bottom right.
- `r1080b/snapshots/` (lobby shot is `r1080/snapshots/01_lobby_1080.png`): at 1920x1080 the UI does not scale (no scaler): cards stay 420 px wide, the layout is spread out, and DESCEND no longer overlaps the cards (it sits at y 959-1020, cards end 810). So the overlap is a 720p-only defect.

HUD
- `02_after_descend.png`: top-left "Warrior" + objective text, "Equipped Ashen Short Sword" toast, crosshair, red HP bar `120 / 120`, blue mana `70 / 70`, green stamina `100 / 190`, hotbar 1-4 "empty", passives text at left, skill cards "A Dash 24 MP 24 MP | 7s cooldown" and "Pi Battle Cry 32 MP 32 MP | 14s cooldown". Defects seen: the MP cost is printed twice ("24 MP  24 MP"); the skill key hint is a glyph ("A", "Pi") not a key name; the first-person sword blade crosses the skill cards; the stamina text ("Stamina 100 / 190" at 720p) is cut by the watermark/weapon model (reads "100 / 190" but the character pane says 100/100, so the digit is likely clipped "100 / 100"); an extra stale label "Warrior" under the objective.
- Stale HUD text: `32_after_close_inv.png`, `33_ember_try.png` show "Satchel open" top-left after the satchel was closed (it stays for the whole session after the first open). `32_after_close_inv.png` also shows "Open chest" prompt still displayed with the chest not in view.
- Hotbar with loot (`24_supply_chest.png`): slots show "Ashen Cloak", "Ember Coin x5", "Blood Draught"; a worn-gear item (the cloak) occupies a "use item" hotbar slot. `35_ember_try3.png`: label "Emberthre / ad Mantle" wraps in the middle of the word. Prompt text: "Hold to open chest  (hold)" (word hold twice).

Inventory, BAG tab
- `03_inventory_open.png` (empty): panel 760x430 at x 220-980, y 125-555, tabs BAG/CHARACTER, "SATCHEL 0/40 worn 0", 10x4 grid, "grid 10x4 free 40 cells value 0". Equipment column on the right has SIX slots with labels Weapon, OffHand, Head, Chest, Legs, Back, each about 54 px, no overlap between labels or slots, whole column (y 163-517) inside the panel. No clipping of the six rows. Defects: the Weapon slot is empty although the Warrior wields the Ashen Short Sword and the HUD says it is equipped; the hint line at the bottom is cut off at the panel's inner edge ("right click a" ...), at both 720p and 1080p; the skill card "A Dash" sits half under the panel's lower-right corner (card y 514-590, panel ends at 555) and shows through the translucent panel.
- `25_inv_with_loot.png`: grid with Ashen Cloak as a flat grey 2x3 block (no icon or label), Ember Coin stack "5" in yellow, Blood Draught red. Footer "grid 10x4 free 32 cells value 110".

Tooltip (cloak, Common): `26_cloak_tooltip.png`
- Floating panel at x 299-548, y 274-383 beside the selected cloak. Lines: bold "Ashen Cloak", "Common   Back", yellow-ish header "FIXED PROPERTIES", "Armour +2", "+1 wisdom", dim "uid 620fcfb9". No ROLLED section for Common (as designed). The strip below also reads "Common  Ashen Cloak  [620fcfb9]" and "Selected Ashen Cloak x1 at cell (0, 0) (Armor +2,00)".
- Colour check (brightest pixel sampled from the PNG): rarity word "Common" = RGB 209,209,204; body text "Back"/"Armour +2" = 224,214,199; header = 214,184,128; uid = 153,143,128. Common is a slightly cooler/darker grey than the cream body text, with a difference of about 15 in R and B. At normal viewing it reads as the same white-ish text: Common is not usefully distinguishable by colour. Wording is "Armour +2" in fixed lines but "Armor +2,00" in the selection strip (inconsistent spelling and decimal comma).
- Hover on worn slot (`29_cloak_worn.png`): the tooltip is semi-transparent and the Back-slot label "Ashen Cloak" shows through the tooltip text (visible in the zoom `tools/zoom_back.png`); the tooltip box also covers the Legs/Back rows of the column and the lower part of the panel edge.

Uncommon with rolled affixes: `r720/snapshots/40_mantle_tip.png` (item from the Ember chest; the death overlay was already on top, so the text is dimmed but legible)
- "Emberthread Mantle" in green, "Uncommon   Back" in green, FIXED PROPERTIES: "Armour +1", "Max mana +15", "+1 intelligence", then header "ROLLED AFFIXES" and one green line "+0.09 move_speed" (raw key, not a display name or percent), then uid. FIXED before ROLLED, separate sections, as specified. `41_brown_tip.png`: "Boiled Cuirass" Uncommon Chest, fixed "Max health +30", ... "+1 constitution", ROLLED "+12 max_health" (raw key). The Uncommon green is clearly distinguishable from body text. A Rare (blue) item was never obtained, so the Rare colour and a two-affix tooltip are UNVERIFIED.

Equip
- Right click on a bag cloak (`27_cloak_worn.png`, ids 83-84): nothing was worn, selection was cleared. Left click cloak then left click Back slot worked (`29_cloak_worn.png`): toast "Equipped Ashen Cloak", "worn 1", bag 2/40, footer "worn: Back=Ashen Cloak", `30_cloak_worn_nohover.png`: Back row shows a grey block with label "Ashen Cloak" (the label replaces "Back"; a new player can no longer see which slot it is). Note the doc string says "right click a bag item to wear or use it"; right click did not wear it in my single trial (3-frame hold), so that is a possible defect or an input-timing artifact of the review bridge (not confirmed).
- 1080p: same left-click wear worked (`r1080b/snapshots/05_cloak_worn_1080.png`).

Character tab: `04_character_tab.png` (no cloak), `31_character_with_cloak.png` (cloak worn)
- ABILITY SCORES: STRENGTH 16 (+3), DEXTERITY 10 (+0), CONSTITUTION 14 (+2), INTELLIGENCE 8 (-1), WISDOM 10 (+0), CHARISMA 10 (+0): six attributes. VITALS: Health 75/120 regen 1,5/s (hit by enemy), Mana 70/70 regen 3/s, Stamina 100/100 regen 6/s. GUARD: "blocks 82% within 140 degrees  shield", new "Armour 0". SPEED, WEAPON AND SKILLS: Walk speed 4,30 m/s, "Ashen Short Sword 26 -> 34,9 before armour", "Skill power 1x Charisma scales skill effects".
- The last row "Skill power" ends at y 524 and the pane background ends at about y 543, panel at 555: the bottom row is inside the pane (U2B11 M1 fix confirmed visually). With the cloak (31): WISDOM 11 (+0) "worn +1", Armour "2   -2% physical", Health back to 120/120. Numbers match the cloak's data. The pane sits directly against the equipment column (right edge 782 = column left edge 782), no gap but no overlap.
- In the Character tab the title becomes "CHARACTER"; the equipment column stays visible, so the cloak slot can be checked there.

Chest / container UI
- There is no container window: opening a chest hands every item to the bag at once and a toast reports "Took 110 value from Supply chest" (`24_supply_chest.png`). Items that do not fit stay in the chest (not tested). The chest itself is not visible after opening in that shot (it is outside the view). The Ember chest needed three 1.7 s holds because every enemy hit resets the hold; it worked on the third try (`35_ember_try3.png`, `39_ember_run3.png`).
- Loot seen: Supply chest (cell 5,4): Ashen Cloak, Ember Coin x5, Blood Draught. Ember chest (cell 3,12): Emberthread Mantle (Uncommon), Boiled Cuirass (Uncommon), coins, draughts ("Items: 17/24", value 274-384).

Death screen / overlays
- `06_retreat.png`, `38_ember_run2_b.png`: "YOU DIED IN THE DELVE", class, loot value, items, slain, time, buttons "Descend again" and "Return to lobby". `40_mantle_tip.png`/`41_brown_tip.png`: the satchel panel and the tooltip stay on screen under the death overlay and still react to clicks (my clicks selected items while dead).
- `22_swing.png`, `21_pre_fight.png`: when standing at an enemy, huge flat yellow/orange quads fill the left half of the screen (the yellow marker above enemies, seen as a small bar at distance in `02_after_descend.png`, is a world object that becomes screen-filling when the camera is next to it) and hide everything.

1920x1080 (`r1080b/snapshots`): `04_tooltip_1080.png`, `05_cloak_worn_1080.png`
- The satchel panel keeps its 760x430 pixel size (x 540-1300, y 305-735), now about 40% of the screen width, centred; text is small. Six equipment slots and labels are all inside the panel, no overlap. The tooltip (x 689-938, y 452-562) is inside the panel and fully readable; the worn-slot tooltip (x 1046-1296) again overlaps the Legs/Back rows and the Back label bleeds through. The weapon slot is again empty. HUD elements anchor to the screen corners (HP bottom left, skills bottom right) correctly. Hint line cut off again. The Character tab at 1080p could not be opened: see below.

## Ranked defects
1. High (gameplay, observed): the two enemies near spawn/corridor kill an idle or hold-E player in 22-46 s (-15 HP every 1-5 s from the Entry Hall, 120 HP gone). Six of seven runs ended in death; the player's left-click swings (about 10 tries) never produced a visible hit or a kill ("Slain: 0"). Whether combat input was merely not registering through the bridge or the Warrior cannot realistically fight is not established.
2. Medium: Weapon slot in the equipment column is empty while a weapon is wielded (inconsistency, would confuse the "worn" count too: "worn 0" with a sword in hand).
3. Medium: the Back slot label is replaced by the item name when worn; tooltip is translucent so the label text bleeds through; the hover tooltip for the worn slot covers other slot rows.
4. Medium: Common colour (209,209,204) is practically the body text colour (224,214,199); only Uncommon (green) is clearly distinct.
5. Medium: right click on a bag cloak did not wear it (one trial); left-click select + click slot works.
6. Medium: stale HUD strings: "Satchel open" persists after closing, "Open chest" persists after the chest is out of view.
7. Medium: satchel/tooltip remain open and interactive under the death screen; world keeps simulating while the satchel is open.
8. Low-medium: hint line at the bottom of the satchel is clipped; skill card overlaps the panel corner at 720p.
9. Low: lobby DESCEND overlaps both class cards at 720p; clicking a class card shows no selection feedback.
10. Low: HUD text: skill cost printed twice, glyph key hints, "Hold to open chest (hold)", hotbar label wraps mid-word, tooltip raw keys ("+0.09 move_speed", "+12 max_health"), "Armour" vs "Armor" spelling, decimal commas.
11. Low: cloak has no icon (flat grey block); the cloak occupies a use-item hotbar slot.
12. Low: stamina text on the HUD collides with the watermark/weapon model.

## Could not verify
- Rare rarity colour, a two-affix tooltip, rolled-affix tooltip on a Common (none exist), the Emberthread Mantle worn, Back slot with a rolled item, the Sealed chest (needs run value).
- Character tab and the Bag tab round-trip at 1920x1080 (and the "tooltip does not return after tab round-trip" finding of the U2B11 review): at 1080p, after the Back-slot click, the inventory pointer stopped following my injected moves (the hover tooltip stayed on the Back slot for three further commands) and two attempts to click the CHARACTER tab did nothing; the lobby DESCEND click also failed in the first 1080p instance (six tries) and worked first time in the second. Measured at the time: the OS window client area was 1417x797 while the game reports 1920x1080 (the display work area is 2540x1015), so the review machine cannot show an unscaled 1080p client; I cannot tell whether this is the window scaling, the bridge or the game.
- Enemy combat (no hit ever registered), skills (Dash, Battle Cry), the extraction gate, the Archer class, weapon swapping, Escape/pause, sound.
- At 1080p the player did not start at the 720p spawn (first key ack began at (8.2, 21.8) instead of (9, 7)); I did not find out why.

## Summary
Build PASS, player runs windowed at 720p and 1080p with no exceptions or errors in Player.log and with live frames. The new Back/cloak slot, six 54 px equipment rows (no clipping or overlap), the rarity tooltip (name and colour, FIXED PROPERTIES then ROLLED AFFIXES, none for Common), and the Character pane with the Armour row (last row inside the pane, numbers change with the cloak) are visually confirmed. Top defects: enemies kill the player within about 30 s and no player hit was ever seen to land; empty Weapon slot while armed; Common rarity colour indistinguishable from body text; Back label replaced by item name with translucent tooltip bleed-through; stale "Satchel open"/"Open chest" HUD text; inventory stays active under the death screen; Character tab at 1080p unverified because injected pointer input stopped working there.
