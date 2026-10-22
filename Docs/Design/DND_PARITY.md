# Dark and Darker mechanics parity: gap analysis and roadmap proposal

Written 22 Oct 2026. Research and design only. No source was edited, no Unity was run.

**Constraint from the user:** Emberdelve must play like Dark and Darker (DaD). All main mechanics should match. Names, art, assets, lore and text stay our own.

**Status:** This is a proposal. It does not amend the approved design. Wherever it conflicts with `ATTRIBUTES.md`, `ITEM_AFFIXES.md` or `UNITY_DESIGN.md`, those files still win until the user decides the questions in section 5.

## How to read this

**Verification flags:**
- **[V]** means checked on 22 Oct 2026 against the community wiki at darkanddarker.wiki.spellsandguns.com (the de facto official wiki). Pages used: Dungeon, Stat, Damage, Weapons, Enchantments, Gear_Score, Beginners_Guide, Merchants, Fighter, Wizard and Classes.
- **[F]** means the only source was a forum post or a secondary guide.
- **[U]** means unverified. It comes from memory and must be checked before anyone tunes numbers against it.

DaD changes every season, so treat any number as "current shape", not as a spec.

**Our state** cites files under `Assets/Emberdelve/Runtime/` (written as `Runtime/...`), the design docs, and `Docs/Reviews/CODEBASE_HEALTH_20261020.md` (written as "Health Fn").

**Gap size:**
- **none:** already matches.
- **S:** small. Data or tuning, one slice.
- **M:** medium. One or two coherent slices.
- **L:** large. A new system or a contract change across several areas.

---

## 1. Parity table

### 1.1 Match and dungeon structure

| Mechanic | Dark and Darker | Emberdelve now | Gap | Deliver in |
|---|---|---|---|---|
| Party size | Solo, duo or trio queues on every dungeon [V]. Party members spawn together [V]. | No parties. Every player is `LocalPlayer` on one team (`Runtime/Player/PlayerRig.cs:55-57`, Health F4). | L | R7 (teams), then new **D9 Parties** |
| Players per match | About 18 in Normal (6 trios), about 20 in High Roller, per 2024 players [F]. No official figure found. | Design target 4–8. First proof is 2 clients (`UNITY_DESIGN.md`). | M | U3A proves 2. **D9** and U6 should prove 6–9. |
| Match length | Fixed timer per map. The High Roller HUD hides the number and shows a filling red bar [V]. Typical length is about 12–20 min depending on map [U]. | No raid timer yet. Planned at 10 min configurable (`UNITY_DESIGN.md`, U4B). | M | **D5**, then U4B |
| Closing zone | The "dark swarm" circle shrinks and damages anyone outside [F]. A 2024 forum thread says it was removed or reduced on some maps for a more dungeon-crawler feel [F]. Current state unclear. | None. | M | **D5** as a config toggle. Needs a user decision (Q5). |
| Dungeons and floors | Six dungeons. Multi-floor chains: Ruins, then Crypts, then Inferno, via red static down portals. The Ice Cavern void pit leads to the Abyss [V]. | One authored layout (`Data/DungeonLayout_AshenHalls.asset`, `Runtime/Level/DungeonLayoutData.cs`). | L | **D8** (one dungeon, two floors) |
| Layout variety | Maps are built from hand-made room modules arranged differently each match [U]. Fixed tile grid, randomized placement. | Static cell grid. Chest seeds are now per-raid (R4R5), but geometry never changes. | L | **D8** module generator, before U5A art |
| Modes | Normal and High Roller. High Roller costs 100 gold to enter, needs level 20, has only Elite/Nightmare monsters and better drop rates [V]. | One mode. | M | **D10** (late; needs economy + monster variants) |
| Spawn | Party spawns together at spread-out spawn points. Monsters near the spawn are killed on spawn [V]. | One player spawn. | S | **D9** |
| Shrines | Speed, health, protection and power shrines. Each gives a buff of about 60 s and recharges in 3 min [V]. | None. | S | **D8** |
| Void | Instant-kill lower boundary [V]. | Not needed (no pits yet). | none | Comes with the D8 floor-transition pit, if used. |

### 1.2 Extraction, death and recovery

| Mechanic | Dark and Darker | Emberdelve now | Gap | Deliver in |
|---|---|---|---|---|
| Escape methods | Escape portals, escape rope, static extracts [V]. Blue single-use portals rise from marked floor spots mid or late match, sometimes in sets of three [F]. A rumbling audio cue plays nearby [F]. Some static exits (stairs, lifts) take more than one player [F]. | One fixed "Ember Gate", hold E for 3 s (`Runtime/Interaction/ExtractionPoint.cs`, `GameConfig.extractionHoldDuration`). | L | **D5**, then U4B (authoritative) |
| Loot needed to extract | No requirement [V/U]. Escaping always keeps your inventory. | `requireLootToExtract = true` (`Runtime/Core/GameConfig.cs:23`). This came with the R0 bug fix. | S | **D5**: default off. Keep the flag for tutorials. |
| Escape channel and interrupt | Using a portal is short and can be contested. Exact channel time and interrupt rules not verified [U]. | 3 s hold. R0 made damage and movement interrupt it. Progress resets (U4B requirement). | S | U4B, tuned in **D5** |
| On escape | Keep inventory and 100% XP, earn Adventure Points [V]. | Run ends Extracted. Items are banked only in memory (`Runtime/Core/RunContext.cs`). | M | U4C |
| On death | Lose all equipment, tools and inventory. Keep 50% XP, lose the run's AP [V]. Drop a lootable body [V/U]. After the timer, a merchant may recover random leftover items [V]. | Run ends Dead. No corpse yet (Health F5; pickups now carry instances after R4R5). | M | U4B (corpse). Item recovery is optional, **D6**. |
| Spectate after death | Dead players spectate surviving teammates [U]. | None. | M | **D9** |
| Resurrection | A dead player's soul heart sits in their chest slot. A teammate carries it to a one-use altar and channels; moving or being hit cancels. The revived player returns at low HP [V]. A healer class also has a revive spell [V]. | None. | M | **D9** (altar revive only) |
| Disconnect | Not free extraction. | Same rule in our design (`UNITY_DESIGN.md`). | none | U4B |

### 1.3 Loot, items and inventory

| Mechanic | Dark and Darker | Emberdelve now | Gap | Deliver in |
|---|---|---|---|---|
| Rarity tiers | Poor, Common, Uncommon, Rare, Epic, Legend, Unique, plus Artifact (weapons only) [V]. Colours grey/white/green/blue/purple/orange/gold/red [U]. | Common, Uncommon, Rare (`Runtime/Data/AffixVocabulary.cs:268`). | M | **D3** |
| Random modifiers per rarity | Poor and Common get 0. Uncommon 1, Rare 2, Epic 3, Legend 4, Unique 5 [V]. Craftable gear starts at 2 [V]. Primary (base) stats never roll, and no modifier repeats on one item [V]. | 0/1/2 with no duplicate stat key (`ITEM_AFFIXES.md`). Fixed plus rolled already separated. This matches DaD's shape. | S | **D3**: extend the ladder |
| Modifier pool | Attributes, physical/magical power and bonus, flat and true damage, penetration, headshot bonus, armor rating, PDR/MDR, projectile reduction, magic resistance, action/cast/interaction/swap speed, move speed, max HP, healing, buff/debuff duration, memory, luck [V]. | 13 keys: six attributes, armor, HP, mana, move speed, melee/projectile damage, mana regen (`Runtime/Data/AffixVocabulary.cs`). | M | **D4** (new stat keys), **D3** |
| Base stats vary within rarity | Base damage and armor roll inside a range per rarity [V]. | Fixed per definition. | S | **D3** (optional) |
| Equipment slots | Head, chest, hands, legs, feet, back, necklace, two rings [V, from the GS table]. Two weapon sets (main + off hand) with a swap [V/U]. Utility belt quick slots [V]. Soul heart in the chest slot [V]. | Weapon, OffHand, Head, Chest, Legs, Back (`Runtime/Data/ItemData.cs:13`). Boots use the Legs slot (`Item_FootpadBoots.asset equipSlot: 5`). Hotbar is stack indices 0..3 (Health F6). | M | **D3** |
| Bag | Grid backpack with sized items [V/U]. Exact size not verified [U]. | 10x4 grid with sized items. Validated move, swap, equip and unequip, plus atomic transfer (accepted U1/U2B). | none / S | Keep. Resize is data. |
| Weight and encumbrance | No weight stat [U]. Each weapon and armor piece has a move-speed penalty instead. Weapons take about -10 to -55 off a base of 300 [V], and blocking or attacking multiplies it further [V]. | Item flat move-speed modifiers exist (`ATTRIBUTES.md` move_speed). No per-item penalty, no attack or guard move multiplier beyond `guardSpeedMultiplier`. | M | **D1b**, **D3** |
| Containers | Many container types, each with a different loot table and open time. Open time scales with interaction speed [V/U]. | Three chest variants: Supply (instant), Ember (hold) and Sealed (needs loot value) (`Runtime/Interaction/ChestInteractable.cs:13`). Seeded per raid (R4R5). | S | **D8** (more variants), **D4** (open time from interaction speed) |
| Gold | Gold coins are physical items with stack and slot limits. Gold bags exist [V/U]. | Ember Coin item exists (`Data/Item_EmberCoin.asset`). No economy. | M | **D6** |
| Gear score | Computed from item rarity per slot [V]. Since patch 8.5 it no longer affects matchmaking brackets [V]. | None. | S | **D3**: compute and display only. Skip GS matchmaking. |

### 1.4 Classes, stats and skills

| Mechanic | Dark and Darker | Emberdelve now | Gap | Deliver in |
|---|---|---|---|---|
| Classes | Ten classes: Fighter, Barbarian, Rogue, Ranger, Wizard, Cleric, Bard, Warlock, Druid, Sorcerer [V]. | Warrior and Archer (`Data/Class_*.asset`). | L | **D7** (adds 2) |
| Kit structure | Choose 2 skills from a class pool of about 12 [V]. Equip up to 4 perks from a pool of about 15; slots unlock at levels 5, 10 and 15 [V]. Casters also memorize spells [V]. | Fixed 2 actives and 2 passives per class (`Runtime/Data/CharacterClassData.cs:71-73`). | M | **D7** |
| Attributes | Seven attributes: STR, VIG, AGI, DEX, WIL, KNO, RES. Each class's base values sum to 105 [V]. | Six D&D-style scores, 1..30, modifier floor((s-10)/2) (`Runtime/Stats/AttributeRules.cs`, `ATTRIBUTES.md`). | L | **D4**. Conflict C1. |
| Derived stats | Piecewise curves, not linear modifiers [V]. Physical power (STR). Max HP from 0.25 STR + 0.75 VIG, 70 to 207.5 HP. Health recovery (VIG). Move speed 300 + (-30..+51) from AGI, cap 500. Action speed from 0.25 AGI + 0.75 DEX, -38%..+68%. Equip speed (DEX). Interaction speed (DEX/RES). Magic power, magic resistance and buff/debuff duration (WIL). Cast speed and memory (KNO). Cooldown reduction up to 65% (RES). | HP, stamina, mana, regen, speed, weapon damage and effect scale from linear modifiers. No action speed, interaction speed, cooldown reduction or magic resistance. | L | **D4** |
| Mana and spells | No mana [V]. Casters memorize a set of spells, each with charges, limited by memory capacity. Charges come back by resting, meditating or potions [V]. Martial classes have cooldown skills only [V]. | One mana pool plus cooldowns for both martial classes (`Runtime/Combat/ManaComponent.cs`). | M | **D4/D7**. Conflict C3. |
| Stamina | No stamina bar [U, strong memory; forum says blocking is effectively unlimited, F]. Sprint is a class skill. Heavy hits break or stagger a block via impact power against impact resist [V/F]. | `StaminaComponent` exists but nothing spends it (`Runtime/Combat/StaminaComponent.cs:6-11`). The design wants stamina-gated defense (`UNITY_DESIGN.md`, U3C). Sprint is free. | M | **D1b**. Conflict C2. |
| Leveling and progression | XP from kills, chests, shrines and escapes. Perk slots at levels 5/10/15 [V]. Merchant quests and affinity, Adventure Points and season rank [V]. | None. | M | **D7** (level, perk unlock only). Quests and AP are out of scope. |

### 1.5 Combat model

| Mechanic | Dark and Darker | Emberdelve now | Gap | Deliver in |
|---|---|---|---|---|
| Melee hit detection | The weapon's swept hitbox along the animation decides hits. Swings stop when they hit walls [U, signature mechanic]. Reach depends on the animation, not weapon length [V]. | Overlap sphere plus angle arc during an active window (`Runtime/Combat/MeleeCombat.cs`, `HitScanner` in `Runtime/Combat/DamageAndScanning.cs`). LOS check to the target centre. No wall stop. | L | **D1a** (pulls U3C forward, offline) |
| Swing patterns and combos | Most weapons chain 3 attacks (some 4). Each step has its own multiplier and type, e.g. 100/105/110% slash/slash/pierce [V]. Riposte specials on some weapons [V]. | One swing per weapon (windup, active, recovery, cooldown). | M | **D1b** |
| Impact zones | Tip hits do full damage. Hits lower on the weapon do 90% or 70% (sword); spears lose more [V]. | None. | S | **D1b** |
| Hit locations | Head +150% (×1.5), body ×1.0, arms ×0.8, hands ×0.7, legs about ×0.6, feet ×0.5 [V]. Headshot bonus from gear is additive, max ×1.8 [V]. AoE and DoT ignore location [V]. | Binary headshot ×1.8 (`DamageAndScanning.cs:12`). The flag is client-claimed (Health F1; R1a/R1b fixed the trust). | M | **D1a** |
| Hit slow | Each weapon hit slows the target, e.g. -10% for 0.6 s, or -25% for 1.5 s on a maul [V]. | None, except the skill Slow status. | S | **D1b** |
| Move penalties while fighting | Holding a weapon subtracts its penalty. Blocking ×0.65, hit frame ×0.70, other swing phases ×0.85 [V]. | Guard speed ×0.45 (class data). Nothing during swings. | S | **D1b** |
| Blocking and parry | Shields and some weapons block within a frontal arc, with no stamina [F/U]. Heavy weapons can break or stagger a block [F]. Fighter has a "perfect block" skill [V]. Some weapons parry/riposte (longsword) [F]. | Yaw-arc block with reduction. Shields never parry (`Runtime/Data/DefenseModel.cs`). No guard break. | M | **D1b**. Keep "shields never parry". |
| Damage formula | (base × combo × impact-zone + gear damage) × (1 + power bonus) + additional; then × hit location; × (1 − DR × (1 − penetration)); × projectile reduction and falloff; then + true damage [V]. | Base × attribute scale + flat affix, × headshot, × guard, × armor (`ARMOR_RULES.md`). | M | **D4** (power bonus, penetration, true damage), **D1** (combo, zone, location) |
| Armor and resistance | AR maps to PDR on a curve: -595% at -300 AR to 93.9% at 500 AR, capped 95% [V]. Magic resistance (from WIL) maps to MDR on the same curve shape [V]. Plate also gives projectile reduction [V]. | 100/(100+AR), physical only. Fire and holy unmitigated (`ARMOR_RULES.md`, a manager decision, not a user one). | S | **D4** |
| Damage types | Physical vs magical drive mitigation. Slash/blunt/pierce currently do nothing [V]. Elemental sub-types (fire, evil, …) and race bonuses (undead, demon) [V]. | Physical, Pierce, Fire, Holy (`Runtime/Core/GameContracts.cs:9`). | S | **D4**: map to Physical/Magical plus element tag |
| Friendly fire | On. Party members can hurt each other [U]. Karma votes exist for team-killing [V]. | Not possible: one team, hostility needs different teams (Health F4). | M | R7, then **D9** |
| Ranged | Physical projectiles with gravity and arrow drop. Damage falloff by air time, e.g. 0.7× after 0.5 s for a recurve bow [V]. Bows draw; crossbows reload [U]. | Hitscan ray from the muzzle with a visual projectile. Falloff by range, not air time (`Runtime/Combat/RangedCombat.cs`, `WeaponData.projectileSpeed` is visual). | M | **D2** |
| Action speed | Scales swing, draw and reload speed (from AGI/DEX) [V]. | None. Weapon timings are fixed. | S | **D4** |
| Status effects | Burn, poison, bleed, slows, stuns, invisibility and hide, many class buffs [V partial]. | Slow and Empowered only (`Runtime/Data/StatusModel.cs`). | M | **D7** (what new kits need), **D8** (monster DoTs) |
| Healing | Potions and bandages heal over time, with a use time. Potions glow and reveal you [V]. | Potion heals instantly inside the bag (`Inventory.TryUseSlot`, Health F6). | S | **D3/R6**: heal over time with a use channel |
| Doors, traps, stealth | Doors open, close and break; door play is central. Spike/pressure traps, mimic chests, crouch-walk is quieter, footsteps carry far [U]. | Crouch exists (`Runtime/Movement/PlayerMovement.cs`). No doors, traps or footstep audio rules. | M | **D8** (doors, traps), U5B (footstep audibility as a gameplay rule) |

### 1.6 PvE

| Mechanic | Dark and Darker | Emberdelve now | Gap | Deliver in |
|---|---|---|---|---|
| Roster | Dozens of monster types (skeletons, zombies, goblins, spiders, mummies, mimics and so on) [U]. Each comes in Common, Elite and Nightmare variants [V]. | Two enemies: Hollow Servitor and Ashen Warden (`Data/Enemy_*.asset`). `isElite` flag only. | L | **D8** (+2 types, variant scaling) |
| Bosses | One boss room per dungeon with good loot [U]. Sub-bosses exist [V, wiki has Bosses/Subbosses]. | None. | M | **D8** (one boss) |
| AI | Patrol, aggro, melee and ranged monsters, telegraphed attacks [U]. | Patrol, investigate, chase, attack, stunned, dead (`Runtime/AI/EnemyBrain.cs`). Ground telegraphs. | S | **D8** |

### 1.7 Lobby, stash and economy

| Mechanic | Dark and Darker | Emberdelve now | Gap | Deliver in |
|---|---|---|---|---|
| Lobby | Character list, stash, merchants, party forming, queue selection (dungeon, mode, party size) [V]. | Class select only (`Runtime/Flow/LobbyController.cs`, `Runtime/UI/LobbyScreenView.cs`). | L | **D6** |
| Stash | Persistent grid stash for extracted items [V]. | Not yet. Planned U4C (server JSON). | L | U4C (already planned) |
| Merchants | About 19 merchants. Buy and sell for gold; restock every 30 min; affinity from quests unlocks stock, discounts and crafting [V]. | None. | L | **D6** (2 merchants, no quests) |
| Free starter kit | A loadout NPC gives free basic gear so a broke player can always queue [V]. | Class `startingItems` is applied every run. | S | **D6** (formalize) |
| Player trading | Marketplace and trading post at level 20, with fees [V]. | None. | — | Out of scope |
| Character slots | Several characters per account [V]. | One implicit profile. | S | U4C/**D6** |

---

## 2. Ranking: what most makes it "feel like DaD"

Ordered by how strongly a DaD player would notice the gap in the first five minutes, weighted by how much later work depends on it.

1. **Melee feel (D1a/D1b).** Swept weapon hitboxes, wall stops, 3-hit combos, hit-location multipliers, hit slow, move penalties while swinging or blocking, and stamina-free blocking with guard break. This is the single biggest identity marker, and we have the least of it. Our arc-overlap melee feels like a generic action game.
2. **Extraction tension (D5).** Late-spawning, random, single-use portals, a timer you can feel, and (optionally) a closing zone. No loot requirement. One fixed gate that is always open removes the core "where is the exit, who is camping it" tension.
3. **Death and loss and recovery (U4B + D9).** Lootable corpse, total loss, spectating teammates, soul-heart revive. Total loss is planned. Spectate and revive are not.
4. **Ranged projectiles (D2).** Arrow drop and air-time falloff. Hitscan bows feel wrong immediately.
5. **Rarity ladder and slots (D3).** Seven colours, 0–5 rolled modifiers, rings, necklace, hands and feet, two weapon sets, utility belt. Loot excitement depends on the colour ladder.
6. **Stat model (D4).** Action speed, interaction speed, cooldown reduction, magic resistance, AR→PDR curve, no mana for martial classes. Less visible, but every class and item number hangs on it, so decide it early.
7. **Lobby economy (D6).** Sell loot for gold, buy gear, free starter kit, stash. This closes the risk-reward loop: extraction only matters if loot has value.
8. **Dungeon structure (D8).** Module-randomized layout, doors, traps, more monsters, elite variants, a boss, two floors via a down portal.
9. **Class variety (D7).** At least one stealth/mobility class and one spell-memory caster prove the kit model. Ten classes is not realistic here.
10. **Parties and scale (D9).** Trios, friendly fire, 6–9 players.

Out of scope for a prototype: High Roller mode and leaderboards, quests and affinity, player marketplace, gear-score matchmaking (DaD itself dropped GS brackets in patch 8.5 [V]), and cosmetics.

---

## 3. Conflicts with approved decisions and recommendations

| # | Approved decision | DaD | Recommendation | Why |
|---|---|---|---|---|
| C1 | Six D&D 5e attributes (STR DEX CON INT WIS CHA, 1..30, floor((s-10)/2)) with fixed Warrior/Archer baselines (`ATTRIBUTES.md`, user amendment 14 Oct) | Seven attributes, summing to 105 per class, feeding curve-based derived stats | **Partial alignment.** Keep the six names (generic, legally clean, user-approved, already implemented and gated). Remap what each one drives to cover DaD's derived-stat surface (table below), and switch the formulas to authored piecewise curves. | DaD's feel comes from its *derived* stats (action speed, interaction speed, cooldown reduction, magic resistance, HP from STR/vitality), not the attribute names. Folding Agility into DEX loses almost nothing. Copying the exact seven-attribute set would also be the most recognizable non-asset borrowing. Needs a user OK: it rewrites a user-authored formula set. |
| C2 | Stamina-gated directional defense; "empty stamina" makes a block fail (`UNITY_DESIGN.md`, U3C) | No stamina. Blocks are unlimited inside the arc. Heavy weapons break blocks via impact power against impact resist. | **Full alignment** (user decision). Drop stamina spending from blocking and sprint, and replace "empty stamina" with **guard break**: a weapon's impact power beats the shield's impact resist and staggers. Keep front/back/height checks. Leave `StaminaComponent` unused or remove it later. | Stamina management is a Souls-like feel, not DaD. Nothing spends stamina today, so the code cost is near zero. |
| C3 | Mana pool plus cooldowns for every class; exactly 2 actives and 2 passives (`UNITY_DESIGN.md`) | Martial classes: cooldown skills only. Casters: spell memory with charges restored by rest. Choose 2 skills from about 12 and up to 4 perks from about 15. | **Partial alignment.** Martial classes become cooldown-only; drop mana from Warrior and Archer. Keep `ManaComponent` code for now and retire it when the caster lands. Move to "pick 2 skills from a pool of 4–5, and 2–4 perks from a pool of 5–6" per class. Spell memory arrives with the caster class in D7. | Choosing skills and perks is a core DaD loop. A full 12/15 pool per class is out of reach, so a small pool proves the mechanic. |
| C4 | Two classes (Warrior, Archer) | Ten classes | **Partial alignment.** Warrior maps to a Fighter-like role and Archer to a Ranger-like role. Add a stealth/mobility class (Rogue-like) and a spell-memory caster (Wizard- or Cleric-like) in D7. Four total for the prototype. | Four classes cover the melee, ranged, stealth and magic axes that make DaD's PvP rock-paper-scissors. Each class is about two to three reviewable slices. |
| C5 | 4–8 players; proof with 2 clients | Trios, about 18 players per match | **Partial alignment.** Parties of up to 3 with friendly fire. Prototype match size 6–9 (2–3 trios). Network proof stays at 2 clients for U3A and grows to 6 in U6. | 18 players needs real server and bandwidth work that a one-orchestrator team can't verify. 6–9 already gives third-party tension. |
| C6 | 10-minute configurable raid, always-open exit, `requireLootToExtract` | Map timer, portals that spawn late, no loot requirement, (maybe) a closing circle | **Full alignment** on portals and timer. Default raid 12 min on our small map, tunable. Portals appear at about 50% elapsed at 2–3 of N marked sites, single use. Static multi-use exit optional. Loot requirement off by default. Closing zone behind a config flag, user decision Q5. | Extraction tension is the second-biggest feel item. The R0 fix stays valid: the hold still re-checks rules on completion. |
| C7 | Rarity Common/Uncommon/Rare with 0/1/2 affixes (`ITEM_AFFIXES.md`) | Poor..Unique with 0/0/1/2/3/4/5; Artifact | **Full alignment** on the ladder: Poor, Common, Uncommon, Rare, Epic, Legendary, Unique (skip Artifact). Keep stored enum ints stable: Common=0, Uncommon=1, Rare=2, append Epic=3, Legendary=4, Unique=5, Poor=6, with an explicit tier ordinal for sorting. | Our rule is already DaD's lower half, so this is an extension, not a reversal. Saves never reinterpret old ints. The validator already rejects unknown rarities. |
| C8 | Inventory 10x4 Diablo-II style; slots Weapon, OffHand, Head, Chest, Legs, Back | Grid backpack; hands, feet, necklace, two rings; two weapon sets; utility belt | **Partial alignment.** Keep the grid (size is data). Append slots Hands, Feet, Necklace, RingLeft, RingRight; move boots to Feet. Two weapon sets with a swap key. Utility belt bound to item ids, which fixes the Health F6 hotbar. | The slot enum is append-only by design (`ItemData.cs:13` comment), so this is cheap. Rings and necklace carry a large share of DaD's loot value. |
| C9 | Headshot ×1.8 binary; armor 100/(100+AR), physical only (`ARMOR_RULES.md`, manager decision) | Location table (head ×1.5 … feet ×0.5); AR→PDR curve capped at 95%; magic resistance | **Full alignment, with our own numbers.** Hit-location multiplier table as data. Armor and magic-resistance curves as authored tables with the same shape (diminishing returns, cap) but our own breakpoints. | `ARMOR_RULES.md` is a manager decision, not the user's, so it may change with a note. |
| C10 | Shields never parry (AGENTS.md, user) | Shields block; weapons may parry or riposte | **Keep ours.** It already matches DaD's shields. Weapon parry stays a later, separate capability. | No conflict. |
| C11 | Final visual pass on KayKit/Kenney assets before the module generator | DaD-like play needs module-randomized layouts | **Reorder.** Build the D8 module/room-graph generator before U5A, so art is made as reusable modules. | Art made for one hand-built map would be redone. |

### C1 detail: proposed attribute remap (names kept, effects moved)

| Our attribute | Takes over DaD's role | Drives (curves) |
|---|---|---|
| Strength | Strength | Physical power bonus, small share of max HP |
| Constitution | Vigor | Most of max HP, health recovery |
| Dexterity | Agility + Dexterity | Move speed, action speed (swing, draw, reload), equip/swap speed |
| Intelligence | Knowledge | Spell cast speed, memory capacity (caster) |
| Wisdom | Will | Magic power, magic resistance, buff/debuff duration, magical interaction speed |
| Charisma | Resourcefulness | Cooldown reduction (cap about 60%), interaction speed (chests, revive, doors) |

Class baselines would then sum to a constant per class, as DaD's 105 does, in our own range (for example 60 across six at 1..30). Gear-bonus allowlist and snapshot keys stay unchanged, because the names don't change. That is a big reason to prefer this over renaming.

---

## 4. Proposed roadmap suffix

**Principles:**
- Settle every *contract* (stats, rarity, slots, kit model, combat model) before it is networked in U3B or persisted in U4C.
- Test combat feel offline first. It is the highest-risk "is this DaD?" item, and offline iteration is cheapest.
- Keep U3A (network spike) early and unchanged, so network risk is still exposed before expensive content.
- Keep U3 and U4 as planned. Only U3C is absorbed (by D1) and U4B gains spectate and revive in D9.

**Already queued (unchanged):** finish R4+R5, then R2+R7 (actor on interactions, per-player events, teams, PvP), R6 (stable inventory ids, command layer), R8 (asmdefs + NUnit) and R10 (one effective-stats snapshot). R10 matters even more now, because D4 adds about 8 new derived stats.

| Order | Stage | Scope (one reviewable slice each unless split) | Acceptance evidence |
|---|---|---|---|
| 0 | **D0 Parity decision** (docs only) | User decides Q1–Q8. Amend `ATTRIBUTES.md`, `ITEM_AFFIXES.md`, `UNITY_DESIGN.md` and `ARMOR_RULES.md`. Mark U3C as absorbed by D1. | Signed-off doc diff |
| 1 | **D1a Swept melee** (offline) | Authored swing paths per pattern (slash left/right, overhead, thrust) as data curves. A server-runnable sweep tests the weapon segment frame by frame against hit volumes. A level-geometry hit stops the swing with a recoil. Per-entity head, torso and limb hit volumes with the location multiplier table. The telegraph shows the actual path. | Gate: a wall between attacker and target means no damage and the swing stops. A head volume hit gives ×1.5, a leg ×0.6. The swing path matches the telegraph within tolerance. Windowed PNGs of the swing arc. |
| 2 | **D1b Combat flow** | 3-step combos with per-step multiplier and pattern. Impact zones (tip vs base). Hit slow. Weapon move penalty plus attack/block multipliers. Stamina-free block with impact power vs impact resist, giving guard break and stagger. Remove stamina from sprint and guard if C2 is approved. | Gate: combo step 3 damage = base × 1.10. A base-of-blade hit = 0.7×. Maul vs buckler = guard break; sword vs heater = blocked. Back hit never blocked. Speeds measured in play mode. |
| 3 | **U3A Network spike** (unchanged) | Stack choice, dedicated server + 2 clients, walking, ownership, offline mode retained | As in ROADMAP |
| 4 | **D2 Projectiles** | Simulated arrows and bolts: speed, gravity, air-time falloff curve, wall impact, deterministic step so the server can rerun them. The visual follows the sim. Hit locations from D1a. | Gate: a 40 m shot drops below the aim line by the expected amount. Falloff at 0.5 s matches the curve. No hit through a wall. |
| 5 | **D3 Rarity + slots** | Rarity ladder Poor..Unique with 0..5 affixes and stable ints. Slots Hands, Feet, Necklace, Ring×2. Weapon set swap. Utility belt by item id. Per-item move penalty. Rarity colours. GS computed and shown in the lobby. Snapshot schema version bump with migration of old Common/Uncommon/Rare saves. Potions heal over time with a use channel. | Gate: affix counts per tier, no duplicates, old snapshot loads unchanged, an unknown tier is rejected. Ring swaps are atomic. Speed changes with gear. |
| 6 | **D4 Stat model** | C1 remap with curve tables. Action speed, cast speed, interaction speed, cooldown reduction, magic power and resistance, AR→PDR and MR→MDR curves with cap. Physical/Magical plus element tag. Penetration and true damage. Martial classes lose mana. New affix keys. Depends on R10. | Gate: hard-coded curve points. Swing time scales with action speed. Chest open time scales with interaction speed. No free refill on regear. Existing gates rebaselined with a written explanation. |
| 7 | **U3B Server combat** | As planned, but networking the D1, D2 and D4 model, not the old arc | As in ROADMAP plus the D1/D2 negative cases over the network |
| 8 | **D5 Extraction loop rules** | Raid timer (default 12 min). Portal sites: N marked spots, k activate at about 50%, single use, with an audio cue. Optional static multi-use exit. Loot requirement default off. Optional closing zone with a damage tick (flag). Written as server-agnostic rules in RunDirector, so U4B can make them authoritative. | Gate: no portal before the threshold, a used portal closes, the timer expiring kills or ends the raid, zone damage stops inside the safe radius. |
| 9 | **U4A** contested containers (unchanged) | | |
| 10 | **U4B** corpse, timer, extraction (authoritative D5) | Corpse holds the exact instances | As planned |
| 11 | **U4C** persistent stash (unchanged) | Plus several character slots in the profile schema | As planned |
| 12 | **D6 Lobby economy** | Lobby hub: stash grid, sell to a merchant for gold, buy a fixed stock from 2 merchants with restock, free starter kit, gold as a stack item with a cap. All server-validated against the U4C profile (no client-chosen prices). | Gate: sell then buy round trip conserves gold. A forged price or item is refused. Starter kit is always available with zero gold. |
| 13 | **D7 Classes and kits** | Kit model: skill pool and perk pool per class, perk slots by level, XP from kills, chests and escape. Add a Rogue-like class (stealth, daggers, lockpick) and a caster with spell memory (charges, rest/meditate restore, cast while slowed). Statuses each kit needs (bleed/poison/burn/invisible). Split per class. | Per-class gates on real skills, as M4 did. Spell charges refuse at 0 and restore by rest. |
| 14 | **D8 Dungeon modules + PvE** | Room-module set (about 8–12 grey-box modules on our cell grid). Seeded room-graph placement per raid. Doors (open, close, break). 2 trap types. Mimic container. +2 monster types, Elite/Nightmare scaling, 1 boss room. Shrines. Two floors linked by a down portal. Split: generator; doors and traps; monsters and boss; floors. | Gate: same seed gives the same layout, different seeds differ, every module reachable, portal sites always valid. A door blocks LOS and projectiles. |
| 15 | **D9 Parties + scale** | Parties of up to 3, shared spawn, friendly fire on, spectate a living teammate after death, soul-heart altar revive (channel, cancel on hit or move, low HP on revive). 6-client test. | Network gate with 6 clients. Team-damage numbers. Revive refused while moving or for a non-party member. |
| 16 | **U5A/U5B** art, audio, mood | As planned, applied to D8 modules. Footstep audibility and crouch quieting as gameplay rules. | As planned |
| 17 | **U6** RC | As planned, plus a 6-player run | As planned |
| (later) | **D10** | High Roller-like mode (entry fee, elite-only), quests, more classes | — |

**Why this order:**
- D1 comes right after the refactors and before U3A, because combat feel is the riskiest parity item and offline is where it iterates cheapest.
- U3A then exposes network risk before more content is built.
- D2–D4 finish the contracts before U3B serializes them.
- D5 lands before U4B, so authoritative extraction is built once with the right rules.
- D6 follows U4C, because the economy needs persistence.
- D7–D9 are content and scale on a stable core.

If throughput is lower than expected, the cut line is after D6. That build already gives a DaD-like loop with two classes.

---

## 5. Open questions for the user

1. **Attributes (C1):** keep our six names with remapped DaD-style effects, as recommended? Or literally switch to seven DaD-like attributes under our own names?
2. **Stamina (C2):** remove stamina from defense and sprint, and use guard break from impact power, as DaD does? This overrides the U3C wording in `UNITY_DESIGN.md`.
3. **Mana (C3):** make Warrior and Archer cooldown-only, and add spell memory only with a caster class?
4. **Classes (C4):** are four classes the prototype target? Which two to add: Rogue-like plus Wizard-like, or Rogue-like plus Cleric-like (a healer enables the revive spell)?
5. **Closing zone:** DaD's circle appears to have been removed or reduced in 2024 (forum only, not confirmed). Implement it as an optional flag, or skip it?
6. **Match size:** is 6–9 players in parties of up to 3 enough for the prototype? Or is matching DaD's roughly 18 a hard requirement (it changes the networking stack choice in U3A)?
7. **Roadmap:** accept the order D1 → U3A → D2–D4 → U3B → …, which delays the networking spike by two slices (D1a/D1b)?
8. **Economy depth:** two merchants plus stash plus starter kit, no quests and no player market. Is that enough?

---

## 6. What NOT to copy

Mechanics, rules and number *shapes* are game systems. The items below are expression or brand. They must stay original. (This is engineering caution, not legal advice.)

- **Names:**
  - The game title, studio and publisher names.
  - Dungeon and floor names (the caves, crypts, inferno and frost-mountain maps).
  - Mode names (the "high roller" mode).
  - NPC and merchant names (the squire NPC, goblin merchant, collector, and so on).
  - System names: "soul heart", "altar of sacrifice", "dark swarm", "Adventure Points", "Trades Guild", karma wording.
  - Named unique and artifact items, boss and monster names, and specific skill, perk and spell names. Avoid even generic-sounding ones copied as a set (e.g. the Fighter's full skill list).
  - Our classes keep our own names (Warrior, Archer, and new names for the Rogue- and caster-like classes), not the ten DaD class names as a roster.
- **Art, audio and UI:**
  - No models, textures, icons, sounds, music or animations from DaD, including extracted, datamined or "fan" rips.
  - No copying of its HUD or inventory screen layout pixel for pixel. Keep our `UiFactory` look.
  - Rarity colours are a genre convention (Diablo-lineage), so the colour ladder is fine.
- **Text and data:**
  - No wiki text, item descriptions or lore copied into the game.
  - Don't bulk-import DaD's numeric tables (item stats, curve breakpoints). Use the same *shape* (diminishing returns, caps, ±% bands) with our own authored values, tuned in our own playtests.
- **Code and leaks:** never use leaked builds, server code or decompiled DaD files. The DaD/Nexon dispute concerned trade secrets and project materials, not game mechanics [U]. Public wiki-level mechanics knowledge is the boundary.
- **Lore and setting:** keep Emberdelve's ember/ash crypt identity. Don't recreate DaD's specific locations, factions or story.

---

## Sources (accessed 22 Oct 2026)

- Dark and Darker Wiki: Dungeon, Stat, Damage, Weapons, Enchantments, Gear_Score, Beginners_Guide, Merchants, Classes, Fighter, Wizard (https://darkanddarker.wiki.spellsandguns.com/)
- Steam community discussions on player counts and circle changes (2024), blocking and parry (2022–2024); flagged [F]
- Gamepressure, Dot Esports, TheGamer and Deltia's extraction guides (portal behaviour); flagged [F]
- Everything marked [U] is from memory and needs checking before tuning.
