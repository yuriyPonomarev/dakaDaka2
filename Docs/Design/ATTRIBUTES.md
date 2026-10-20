# Attribute amendment — user instruction, 14 October 2026

This amendment extends DESIGN.md. Complete task M1C after M1A and before component implementation. Attributes affect real gameplay, equipment and displayed derived stats; not decorative labels.

Reference for the six ability scores and modifiers: https://www.dndbeyond.com/sources/dnd/br-2024/playing-the-game . We borrow the familiar names/modifier shape and use our own real-time extraction-game scaling. Speed, mana and cooldown formulas below are prototype game tuning, not literal D&D rules.

ClassDefinition gains inspector-exported strength, dexterity, constitution, intelligence, wisdom, charisma (int, 1..30). Warrior: 16/10/14/8/10/10. Archer: 10/16/12/10/14/8. Starting data is authored in .tres. ItemDefinition gains separate attribute_bonuses Dictionary of these six allowlisted keys with bounded integer values; existing base_modifiers keeps its documented three keys unchanged. At least two loot gear definitions have attribute bonuses (e.g. leather cap +1 dexterity, iron cuirass +1 constitution).

AttributeRules is a pure testable typed class under src/gameplay/stats/ or src/resources/definitions/. Modifier = floor((effective_score - 10) / 2.0), including negative odd scores. Effective scores = base + equipped-item bonuses, clamped 1..30, then derive from pristine class base values and passives every time (never accumulate bonuses during equip cycles).

ClassDefinition max_health/max_mana/etc are BASE values after amendment. Adjust warrior base max_health=100, max_mana=75, mana_regen=3, max_stamina=90, move_speed=4.3; archer base max_health=80, max_mana=90, mana_regen=3.5, max_stamina=95, move_speed=4.3. This gives warrior 120 HP/70 mana/100 stamina and archer 90 HP/90 mana/100 stamina before armour; movement differs through dexterity and Fleet Foot.

Derived values:
- max_health = max(25, base_max_health + 10 * constitution_modifier)
- max_stamina = max(25, base_max_stamina + 5 * constitution_modifier)
- max_mana = max(10, base_max_mana + 5 * intelligence_modifier)
- mana_regen = max(0.1, base_mana_regen + 0.25 * wisdom_modifier)
- move_speed = clamp(base_move_speed * max(0.5, 1 + 0.025 * dexterity_modifier) * passive_move_multiplier + item_flat_move_speed, 2, 8)
- stamina regeneration = max(1, base_stamina_regen + 0.5 * constitution_modifier)
- sword/axe melee damage = base_damage * max(0.2, 1 + 0.04 * strength_modifier)
- rapier finesse damage uses max(strength_modifier, dexterity_modifier)
- bow/crossbow damage = base_damage * max(0.2, 1 + 0.04 * dexterity_modifier), followed by projectile passive/gear modifiers
- ability effect_scale = clamp(1 + 0.04 * charisma_modifier, 0.6, 1.4); scales beneficial magnitudes/durations (not mana cost or number of projectiles). Charisma represents personal force supporting active skills in this custom game.

StatsComponent owns effective_attributes and derived fields, recalculates with equipment snapshots, offers get_attribute(name), get_attribute_modifier(name), get_weapon_damage(item) and get_ability_effect_scale(). Gear/stat changes clamp current pools to new maxima but never award free healing/refilling. Server computes and replicates effective/derived values; clients cannot submit attributes as authoritative numbers.

Inventory character panel shows all six scores, modifiers and derived HP/mana/stamina/armour/speed/weapon damage; item tooltips show attribute bonuses. Re-equipping cannot stack permanent stat bonuses. Tests cover modifier 9=-1/11=0/16=3, both class baselines, STR vs DEX/finesse damage, CON->HP/stamina, INT->mana, WIS->regeneration, DEX->speed, CHA->effect_scale, equipment equip/unequip recalculation and no free healing. M4 and M5 must use these formulas in actual damage/skills/movement/equipment, not only tests or UI.
