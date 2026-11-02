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
- ability effect_scale = clamp(captured_skill_power_curve(effective_wisdom_score), 0.6, 1.4) [D4d1]; scales beneficial active-skill magnitudes/durations. Costs, projectile counts, ordinary weapon damage and the separate CHA cooldown multiplier do not come from it.

StatsComponent owns effective_attributes and derived fields, recalculates with equipment snapshots, offers get_attribute(name), get_attribute_modifier(name), get_weapon_damage(item) and get_ability_effect_scale(). Gear/stat changes clamp current pools to new maxima but never award free healing/refilling. Server computes and replicates effective/derived values; clients cannot submit attributes as authoritative numbers.

Inventory character panel shows all six scores, modifiers and derived HP/mana/armour/speed/weapon damage; item tooltips show attribute bonuses. Re-equipping cannot stack permanent stat bonuses. Tests cover modifier 9=-1/11=0/16=3, both class baselines, STR vs DEX/finesse damage, CON->HP, INT->mana, WIS->regeneration, DEX->speed, WIS->effect_scale, CHA->chest opening/cooldown, equipment equip/unequip recalculation and no free healing. M4 and M5 must use these formulas in actual damage/skills/movement/equipment, not only tests or UI.


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

## D4d1: WIS active-skill power, after C1/C2

Implementation draft; acceptance evidence belongs to the D4d1 task/local report. This supersedes the former live CHA effect-magnitude query only when this slice is integrated. Keep STR/DEX/CON/INT/WIS/CHA, all original class scores/save keys, score modifiers, HP/mana formulas and BOTH class mana pools. The earlier .tres/src references are historical: the existing Unity implementation uses CharacterClassData/ItemData ScriptableObjects under Assets/Emberdelve and the current typed C# StatsComponent.

The shared editable AttributeCurveProfile gains skillPower points: (1,.70),(6,.90),(10,1),(16,1.12),(22,1.24),(30,1.40), our own tuning. Interpolate the effective WIS score linearly, hold endpoints, copy positive finite points at class initialization, clamp the resulting effect factor.6..1.4. Invalid/missing profile falls back to the same immutable default table with a diagnostic for invalid data; uninitialized/no attribute owner remains neutral1. Rebuilds and equipment changes never reread mutable authored points; explicit new class initialization may capture edits.

Original Warrior WIS10 gives1; Archer WIS14 gives1.08. WIS+3 gives13/1.06 and17/1.14. CHA-only gear no longer changes beneficial effect power. Existing WIS mana regeneration remains separate and unchanged. C1 CHA chest opening and C2 accepted frozen cooldown remain separate consumers of Charisma, with their own captured curves. Do not combine or apply these factors twice.

Each accepted skill captures the WIS factor once through existing SkillContext/authority activation state. Dash scales distance and duration together at unchanged trajectory speed; BattleCry scales status deltas from neutral and status durations while keeping its authored flat damage/radius/target cap; projectile skills apply magnitude once to their own skill damage, preserving count/spread/ammo/mana. Gear equipped afterwards changes subsequent casts only. Ordinary physical weapon damage and incoming enemy damage do not gain a WIS factor. Magic resistance, separate magic damage channels, caster memory and server networking remain later work.

The character pane keeps its existing Skill power row and shows the effective multiplier plus (WIS). Tests must retain actual saved Archer WIS14 and include below-neutral, neutral and above-neutral cases rather than renaming old CHA8 assumptions. Legacy pure AttributeRules.AbilityEffectScale overloads remain historical compatibility helpers; live rig queries use the captured WIS curve. Local Pi/Strata reviews and runs all compilation/units/gameplay/UI checks; authored fixtures are not evidence of execution.
