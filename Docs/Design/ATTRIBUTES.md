# Attribute amendment — user instruction, 14 October 2026

This amendment extends DESIGN.md. Complete task M1C after M1A and before component implementation. Attributes affect real gameplay, equipment and displayed derived stats; not decorative labels.

Reference for the six ability scores and modifiers: https://www.dndbeyond.com/sources/dnd/br-2024/playing-the-game . We borrow the familiar names/modifier shape and use our own real-time extraction-game scaling. Speed, mana and cooldown formulas below are prototype game tuning, not literal D&D rules.

ClassDefinition gains inspector-exported strength, dexterity, constitution, intelligence, wisdom, charisma (int, 1..30). Warrior: 16/10/14/8/10/10. Archer: 10/16/12/10/14/8. Starting data is authored in .tres. ItemDefinition gains separate attribute_bonuses Dictionary of these six allowlisted keys with bounded integer values; existing base_modifiers keeps its documented three keys unchanged. At least two loot gear definitions have attribute bonuses (e.g. leather cap +1 dexterity, iron cuirass +1 constitution).

AttributeRules is a pure testable typed class under src/gameplay/stats/ or src/resources/definitions/. Modifier = floor((effective_score - 10) / 2.0), including negative odd scores. Effective scores = base + equipped-item bonuses, clamped 1..30, then derive from pristine class base values and passives every time (never accumulate bonuses during equip cycles).

ClassDefinition max_health/max_mana/etc are BASE values after amendment. Adjust warrior base max_health=100, max_mana=75, mana_regen=3, move_speed=4.3; archer base max_health=80, max_mana=90, mana_regen=3.5, move_speed=4.3. This gives warrior 120 HP/70 mana and archer 90 HP/90 mana before armour (stamina was removed in B3); movement differs through dexterity and Fleet Foot.

Derived values:
- max_health = max(25, base_max_health + 10 * constitution_modifier)
- max_mana = max(10, base_max_mana + 5 * intelligence_modifier)
- mana_regen = max(0.1, base_mana_regen + 0.25 * wisdom_modifier)
- move_speed = clamp(base_move_speed * movement_curve(effective_dexterity_score) * passive_move_multiplier + item_flat_move_speed, 2, 8) [D4a]
- sword/axe melee damage = base_damage * max(0.2, 1 + 0.04 * strength_modifier)
- rapier finesse damage uses max(strength_modifier, dexterity_modifier)
- bow/crossbow damage = base_damage * max(0.2, 1 + 0.04 * dexterity_modifier), followed by projectile passive/gear modifiers
- ability effect_scale = clamp(1 + 0.04 * charisma_modifier, 0.6, 1.4); scales beneficial magnitudes/durations (not mana cost or number of projectiles). Charisma represents personal force supporting active skills in this custom game.

StatsComponent owns effective_attributes and derived fields, recalculates with equipment snapshots, offers get_attribute(name), get_attribute_modifier(name), get_weapon_damage(item) and get_ability_effect_scale(). Gear/stat changes clamp current pools to new maxima but never award free healing/refilling. Server computes and replicates effective/derived values; clients cannot submit attributes as authoritative numbers.

Inventory character panel shows all six scores, modifiers and derived HP/mana/armour/speed/weapon damage; item tooltips show attribute bonuses. Re-equipping cannot stack permanent stat bonuses. Tests cover modifier 9=-1/11=0/16=3, both class baselines, STR vs DEX/finesse damage, CON->HP, INT->mana, WIS->regeneration, DEX->speed, CHA->effect_scale, equipment equip/unequip recalculation and no free healing. M4 and M5 must use these formulas in actual damage/skills/movement/equipment, not only tests or UI.


## D4a: authored DEX movement curve

The first stat-model slice migrates locomotion only. Keep STR/DEX/CON/INT/WIS/CHA, score bounds1..30, the displayed modifier floor((score-10)/2), both class score arrays, all HP/mana/regen/damage/ability rules and item/save schemas. Warrior and Archer retain mana; the later user decision supersedes the older DND_PARITY proposal to remove it. No timing, remap or new affix surface enters this slice.

`AttributeCurve_Default.asset` is an editable `AttributeCurveProfile`, generated once by the existing data builder and referenced by both class assets. The table is:

| Effective DEX score | Movement factor |
|---|---|
| 1 | 0.875 |
| 6 | 0.950 |
| 10 | 1.000 |
| 16 | 1.075 |
| 22 | 1.135 |
| 30 | 1.175 |

Interpolate linearly between points and hold each endpoint outside the authored range. Coordinates must be finite and strictly increasing; movement factors must be positive. NaN curve input selects the first endpoint safely. Infinite input selects the corresponding endpoint. Authoring data is defensively copied into an immutable, engine-free curve at `StatsComponent.Initialize`; equipment rebuilds use that capture. Changing an asset affects the next class initialization, never a running actor. Missing or invalid profiles use the same immutable default table; invalid profiles emit a warning. Builders preserve existing authored tables and explicit class references.

Walking retains its exact order and2..8 clamp. Sprint/crouch use their own authored bases with the same captured DEX factor and their own multiplier/flat channels, without the walking clamp. Guard, aim, status, held-weapon and worn-penalty rules still apply afterwards.

Literal anchors before held penalties: Warrior4.3; Archer4.6225, or5.315875 with Fleet Footing. DEX11 now gives factor1.0125 and DEX17 gives1.085 although their displayed modifiers remain0 and3. Warrior boots give4.3*1.0125*1.18=5.137425; Archer boots plus Fleet give4.3*1.085*1.15*1.18=6.3310835. Legacy integer-modifier math remains available under the original pure `AttributeRules.MoveSpeed`/two-argument `Derive` APIs for literal compatibility tests; live stats use the explicitly score-based curve API.
