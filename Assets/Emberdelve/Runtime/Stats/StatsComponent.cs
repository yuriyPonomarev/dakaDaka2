using System;
using System.Collections.Generic;
using System.Text;
using Emberdelve.Data;
using Emberdelve.Items;

namespace Emberdelve.Stats
{
    /// <summary>
    /// The live six-attribute and derived-vital owner for one entity
    /// (Docs/Design/ATTRIBUTES.md, U2A3).
    ///
    /// It holds exactly three things: the pristine class base scores, the pristine class base vitals,
    /// and the bonus totals of the items currently equipped (their authored definition bonuses plus their
    /// rolled attribute affixes, since U2B5). Every effective score and every derived number is recomputed
    /// from the pristine bases on demand, so an equip/unequip/re-equip cycle replaces the bonus set instead
    /// of adding to a previously bonused score, and no shared class or item asset is ever written to.
    ///
    /// It is deliberately not a MonoBehaviour and deliberately not a replacement for
    /// <see cref="Emberdelve.Skills.CharacterStatStack"/>: that stack stays the passive/gear
    /// StatModifier service, and the rig combines the two exactly once (attribute-derived base first,
    /// legacy stack on top) when it writes a pool ceiling.
    ///
    /// Weapon and ability-scale queries are consumed by the live gameplay paths since U2A4: the combat
    /// authority reads the weapon multiplier through PlayerRig.GetOutgoingDamageMultiplier, and
    /// PlayerMovement reads the locomotion rule through <see cref="ResolveMoveSpeed"/>. Since U2A5 the
    /// ability scale is consumed too, once per accepted cast, by SkillCaster taking it into
    /// <see cref="Emberdelve.Skills.SkillContext.EffectScale"/>. The stack stays the passive/gear modifier
    /// source; this component supplies the attribute term exactly once.
    ///
    /// Since U2B5 the live equipment rebuild is the INSTANCE path (<see cref="RebuildFromInstances"/>): the
    /// bonus set is each worn slot's fixed definition bonuses PLUS that instance's own immutable rolled
    /// attribute affix records, summed once per slot and then replaced wholesale. The pristine class base is
    /// never a previous effective score, so a swap between two different rolls of the same base item is a
    /// replacement and not an accumulation. The definition-facing <see cref="Rebuild"/> and
    /// <see cref="SumBonuses"/> stay for the older definition-only fixtures and views. Only the six
    /// ability-score affix keys are applied; the functional equipment keys (armor, max_health, max_mana,
    /// move_speed, melee_damage, projectile_damage, mana_regen) are deliberately NOT reinterpreted as ability
    /// scores and are deferred to the next numeric-affix slice, named there rather than dropped in silence.
    /// </summary>
    public sealed class StatsComponent
    {
        private CharacterClassData _owner;
        private AttributeSnapshot _snapshot;
        private DerivedStatBases _bases;
        private DerivedStats _derived;
        private AttributeBonuses _rolledBonuses;
        private bool _ready;
        private PiecewiseLinearCurve _movementCurve = MovementCurveDefaults.Curve;

        /// <summary>Changes whenever the class or equipped-instance derivation is replaced.</summary>
        public int Version { get; private set; }

        /// <summary>True once a class has been applied and the first derivation pass has run.</summary>
        public bool IsReady => _ready;

        /// <summary>The class asset whose pristine numbers this component derives from.</summary>
        public CharacterClassData Owner => _owner;

        /// <summary>Pristine base + current bonus totals. Reading it never mutates anything.</summary>
        public AttributeSnapshot Snapshot => _snapshot;

        /// <summary>The class-authored base vitals every derivation reads.</summary>
        public DerivedStatBases Bases => _bases;

        /// <summary>The last full derivation pass, as one immutable value (UI/server read model).</summary>
        public DerivedStats Derived => _derived;

        public AttributeScores BaseAttributes => _snapshot.Base;
        public AttributeScores EffectiveAttributes => _snapshot.Effective;
        /// <summary>Fixed definition bonuses plus rolled attribute affixes of everything worn, as one total.</summary>
        public AttributeBonuses EquippedBonuses => _snapshot.Bonuses;

        /// <summary>
        /// The rolled half of <see cref="EquippedBonuses"/> from the last instance-based rebuild pass, kept
        /// separately so a check can prove the fixed part and the rolled part are both present and each counted
        /// once. Empty after <see cref="Initialize"/> or a definition-facing <see cref="Rebuild"/>, which cannot
        /// know any rolls.
        /// </summary>
        public AttributeBonuses RolledBonuses => _rolledBonuses;

        public int MaxHealth => _derived.MaxHealth;
        public int MaxMana => _derived.MaxMana;
        public float ManaRegen => _derived.ManaRegen;

        /// <summary>
        /// The attribute-only walking number (passive multiplier 1, gear flat 0). It is the amendment's
        /// base term before Fleet Footing and gear, which is what a panel shows next to the score that
        /// produced it. Locomotion uses <see cref="ResolveMoveSpeed"/>, which is this same rule with the
        /// caller's passive/gear terms folded in.
        /// </summary>
        public float MoveSpeed => _derived.MoveSpeed;

        /// <summary>The DEX modifier currently in force, after equipment bonuses.</summary>
        public int DexterityModifier => _derived.Modifier(SixAttribute.Dexterity);

        /// <summary>Effective DEX score evaluated against the immutable class-initialization snapshot.</summary>
        public float MoveSpeedFactor => _movementCurve.Evaluate(GetAttribute(SixAttribute.Dexterity));

        /// <summary>
        /// The amendment's ordinary walking rule, in its exact order:
        /// clamp(authoredBase * captured DEX score curve * passiveMultiplier + itemFlat, 2, 8).
        /// The passive multiplier and the gear flat are inputs from <see cref="Emberdelve.Skills.CharacterStatStack"/>,
        /// deliberately NOT combined by <c>stack.Get</c>, because that helper computes (base + flat) * multiplier
        /// and the amendment specifies base * multiplier + flat. This is the one walking resolver; PlayerMovement
        /// calls it, so a query and the actual movement can never disagree or apply DEX twice.
        /// </summary>
        public float ResolveMoveSpeed(float passiveMultiplier, float itemFlat)
        {
            return AttributeRules.MoveSpeedFromScore(_bases.MoveSpeed, GetAttribute(SixAttribute.Dexterity),
                _movementCurve, passiveMultiplier, itemFlat);
        }

        /// <summary>
        /// The same DEX term applied to a class-authored speed that is not ordinary walking (sprint, crouch),
        /// with that speed's own passive/gear terms: authoredBase * factor * multiplier + flat.
        ///
        /// Deliberately not clamped to the 2..8 walking band: Docs/Design/ATTRIBUTES.md clamps the derived
        /// walking speed, and the amendment leaves sprint/crouch as their own authored numbers, so clamping
        /// them here would silently rewrite authored data. The movement component still applies its own
        /// guard/aim/status multipliers and its non-negative floor on top.
        /// </summary>
        public float ResolveAuthoredSpeed(float authoredBaseSpeed, float passiveMultiplier, float itemFlat)
        {
            return authoredBaseSpeed * MoveSpeedFactor * passiveMultiplier + itemFlat;
        }

        /// <summary>Effective score for one attribute. Read-only, clamped 1..30 by the value type.</summary>
        public int GetAttribute(SixAttribute attribute)
        {
            return _snapshot.EffectiveScore(attribute);
        }

        /// <summary>floor((effective score - 10) / 2.0) for one attribute.</summary>
        public int GetModifier(SixAttribute attribute)
        {
            return _snapshot.Modifier(attribute);
        }

        /// <summary>The untouched class-authored score for one attribute, for a panel that shows base and bonus.</summary>
        public int GetBaseAttribute(SixAttribute attribute)
        {
            return _snapshot.Base.Get(attribute);
        }

        /// <summary>
        /// Starts a run: pristine class scores, pristine class base vitals, no equipment bonuses at all.
        /// Calling it again is a fresh derivation, not an accumulation.
        /// </summary>
        public void Initialize(CharacterClassData classData)
        {
            _owner = classData;
            _movementCurve = AttributeCurveProfile.CaptureMovement(
                classData != null ? classData.attributeCurveProfile : null);

            if (classData == null)
            {
                _snapshot = default;
                _bases = default;
                _derived = default;
                _rolledBonuses = AttributeBonuses.None;
                _ready = false;
                Version++;
                return;
            }

            _bases = classData.AttributeBases;
            _snapshot = new AttributeSnapshot(classData.BaseAttributes);
            _rolledBonuses = AttributeBonuses.None;
            _derived = DeriveCurrent();
            _ready = true;
            Version++;
        }

        // The initialization and both equipment paths share the same curve-aware derivation.
        private DerivedStats DeriveCurrent() => AttributeRules.Derive(_snapshot, _bases, _movementCurve);

        /// <summary>
        /// Recomputes from scratch for the definitions currently equipped. The bonus set is REPLACED
        /// with the sum of the supplied items and the pristine base is carried over untouched, which is
        /// what makes unequipping exact and re-equipping non-stacking. A null list means "wearing
        /// nothing", i.e. the pristine class numbers again.
        /// </summary>
        /// <remarks>
        /// The definition-facing legacy path: it can only see authored bonuses, so it reports no rolled affixes.
        /// The live rig uses <see cref="RebuildFromInstances"/> instead.
        /// </remarks>
        public void Rebuild(IReadOnlyList<ItemData> equipped)
        {
            if (!_ready || _owner == null) return;

            _rolledBonuses = AttributeBonuses.None;
            _snapshot = _snapshot.WithBonuses(SumBonuses(equipped));
            _derived = DeriveCurrent();
            Version++;
        }

        /// <summary>
        /// Sums the attribute bonuses of every supplied definition into one unclamped total. Null
        /// entries are skipped, an item that declares no bonuses contributes nothing, and the shared
        /// ItemData definitions are only read, never written.
        /// </summary>
        public static AttributeBonuses SumBonuses(IReadOnlyList<ItemData> equipped)
        {
            if (equipped == null || equipped.Count == 0) return AttributeBonuses.None;

            AttributeBonuses total = AttributeBonuses.None;

            for (int i = 0; i < equipped.Count; i++)
            {
                ItemData item = equipped[i];
                if (item == null) continue;
                total = total.Plus(item.AttributeBonusTotals);
            }

            return total;
        }

        /// <summary>
        /// The live rebuild (U2B5): the bonus set is recomputed from scratch out of the item INSTANCES actually
        /// worn. Each worn slot contributes its definition's fixed authored bonuses plus its own immutable
        /// rolled attribute affix records, and the whole set REPLACES the previous one while the pristine class
        /// base is carried over untouched - never the last effective scores, never a previously bonused score.
        /// Wearing a piece, swapping it for a different roll of the same base, unequipping and re-equipping are
        /// therefore all exact and none of them accumulates. A null list means "wearing nothing", i.e. the
        /// pristine class numbers again. Nothing is rerolled, copied, minted or written: the instances and the
        /// shared definitions are only read.
        ///
        /// Only the six ability-score affix keys are applied here. The seven functional equipment keys (armor,
        /// max_health, max_mana, move_speed, melee_damage, projectile_damage, mana_regen) are not ability scores
        /// and are not applied by this slice: they are deferred to the next numeric-affix slice with their own
        /// semantics, and are named there instead of being ignored in silence.
        /// </summary>
        public void RebuildFromInstances(IReadOnlyList<ItemInstance> equipped)
        {
            if (!_ready || _owner == null) return;

            AttributeBonuses fixedBonuses = SumFixedBonusesFromInstances(equipped);
            AttributeBonuses rolled = SumRolledAttributeAffixBonuses(equipped);

            _rolledBonuses = rolled;
            _snapshot = _snapshot.WithBonuses(fixedBonuses.Plus(rolled));
            _derived = DeriveCurrent();
            Version++;
        }

        /// <summary>
        /// The authored half of the worn gear, read through each instance's own definition so a rolled piece of
        /// gear never loses its fixed properties. Null entries and instances with no definition are skipped.
        /// </summary>
        public static AttributeBonuses SumFixedBonusesFromInstances(IReadOnlyList<ItemInstance> equipped)
        {
            if (equipped == null || equipped.Count == 0) return AttributeBonuses.None;

            AttributeBonuses total = AttributeBonuses.None;

            for (int i = 0; i < equipped.Count; i++)
            {
                ItemInstance worn = equipped[i];
                if (worn == null || worn.Definition == null) continue;
                total = total.Plus(worn.Definition.AttributeBonusTotals);
            }

            return total;
        }

        /// <summary>The rolled ability-score affixes of every supplied instance, summed unclamped here: the
        /// 1..30 bound belongs to the effective score and the 1..3 whole-number band was enforced when the
        /// affix was rolled, so this pass neither clamps nor re-rolls anything.</summary>
        public static AttributeBonuses SumRolledAttributeAffixBonuses(IReadOnlyList<ItemInstance> equipped)
        {
            if (equipped == null || equipped.Count == 0) return AttributeBonuses.None;

            AttributeBonuses total = AttributeBonuses.None;

            for (int i = 0; i < equipped.Count; i++) total = total.Plus(SumRolledAttributeAffixBonuses(equipped[i]));

            return total;
        }

        /// <summary>
        /// One instance's rolled records mapped onto the six scores. The key is the record's own typed
        /// <see cref="AffixStatKey"/> converted through the checked <see cref="AffixStatKeys.TryAsAttribute"/>
        /// mapping - never a string lookup, never a cast a caller is asked to trust. A record whose key is one
        /// of the seven functional equipment keys contributes nothing to the scores in this slice (deferred),
        /// and a key outside the allowlist contributes nothing at all instead of becoming a guessed attribute.
        /// Attribute-key amounts are whole numbers by the catalog's own integrality rule, so the whole-number
        /// step below is exact for any record a validated instance can hold.
        /// </summary>
        public static AttributeBonuses SumRolledAttributeAffixBonuses(ItemInstance instance)
        {
            if (instance == null) return AttributeBonuses.None;

            AttributeBonuses total = AttributeBonuses.None;
            IReadOnlyList<AffixRecord> records = instance.Affixes;

            for (int i = 0; i < records.Count; i++)
            {
                AffixRecord record = records[i];

                if (!AffixStatKeys.TryAsAttribute(record.StatKey, out SixAttribute attribute)) continue;
                if (!TryRolledAmount(record.Value, out int amount)) continue;

                total = AddAttributeBonus(total, attribute, amount);
            }

            return total;
        }

        /// <summary>Fixed plus rolled, i.e. the amendment's "effective equipment bonus = base fixed
        /// bonuses/modifiers + rolled affixes", with each part summed exactly once per worn slot.</summary>
        public static AttributeBonuses SumBonusesFromInstances(IReadOnlyList<ItemInstance> equipped)
        {
            return SumFixedBonusesFromInstances(equipped).Plus(SumRolledAttributeAffixBonuses(equipped));
        }

        /// <summary>Adds one amount to one score of an otherwise untouched bonus set.</summary>
        private static AttributeBonuses AddAttributeBonus(AttributeBonuses total, SixAttribute attribute, int amount)
        {
            switch (attribute)
            {
                case SixAttribute.Strength: return total.Plus(new AttributeBonuses(amount, 0, 0, 0, 0, 0));
                case SixAttribute.Dexterity: return total.Plus(new AttributeBonuses(0, amount, 0, 0, 0, 0));
                case SixAttribute.Constitution: return total.Plus(new AttributeBonuses(0, 0, amount, 0, 0, 0));
                case SixAttribute.Intelligence: return total.Plus(new AttributeBonuses(0, 0, 0, amount, 0, 0));
                case SixAttribute.Wisdom: return total.Plus(new AttributeBonuses(0, 0, 0, 0, amount, 0));
                case SixAttribute.Charisma: return total.Plus(new AttributeBonuses(0, 0, 0, 0, 0, amount));
                default: return total;
            }
        }

        /// <summary>
        /// A rolled amount is a whole number or it is not a legal roll: a non-finite or unrepresentable value
        /// is refused rather than turned into a score, and an already integral float converts exactly.
        /// </summary>
        private static bool TryRolledAmount(float value, out int amount)
        {
            amount = 0;
            if (float.IsNaN(value) || float.IsInfinity(value)) return false;

            double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
            if (rounded < int.MinValue || rounded > int.MaxValue) return false;

            amount = (int)rounded;
            return true;
        }

        /// <summary>
        /// The amendment's weapon rule for a weapon definition, decided by the definition's own data
        /// (<see cref="WeaponData.DamageScale">): sword/axe scale off STR, bow/crossbow off DEX, and a
        /// definition marked finesse (the rapier) off the better of the two. No class, item or weapon name
        /// is consulted anywhere.
        /// </summary>
        public float GetWeaponDamage(WeaponData weapon)
        {
            if (weapon == null) return 0f;
            return GetWeaponDamage(weapon.baseDamage, weapon.DamageScale);
        }

        /// <summary>The same rule with the scale chosen by the caller (Melee STR, Ranged DEX, Finesse best of both).</summary>
        public float GetWeaponDamage(float baseDamage, WeaponDamageScale scale)
        {
            return AttributeRules.WeaponDamage(baseDamage, scale, _derived.Effective);
        }

        /// <summary>
        /// clamp(1 + 0.04 * CHA modifier, 0.6, 1.4): the beneficial magnitude/duration scale for active
        /// skills. Since U2A5 this is the number SkillCaster snapshots into SkillContext.EffectScale for
        /// every accepted cast, and the number the projectile skills carry into their SkillIntent for the
        /// authority to bound and apply once. Costs, cooldowns and projectile counts never come from it.
        /// </summary>
        public float GetAbilityEffectScale()
        {
            return AttributeRules.AbilityEffectScale(_derived.Effective);
        }

        /// <summary>One-line read model for a panel or a log: scores with modifiers, then derived vitals.</summary>
        public string Describe()
        {
            if (!_ready) return "no class applied";
            return _derived.Effective.Describe() + "  " + _derived.Describe();
        }

        /// <summary>The equipment contribution as text, e.g. "constitution +1 dexterity +1".</summary>
        public string DescribeBonuses()
        {
            AttributeBonuses bonuses = _snapshot.Bonuses;
            if (bonuses.IsEmpty) return "no attribute bonuses";

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < AttributeRules.AttributeCount; i++)
            {
                SixAttribute attribute = (SixAttribute)i;
                int value = bonuses.Total(attribute);
                if (value == 0) continue;

                if (builder.Length > 0) builder.Append(' ');
                builder.Append(AttributeRules.Key(attribute)).Append(' ')
                    .Append(value >= 0 ? "+" : "").Append(value);
            }

            return builder.Length > 0 ? builder.ToString() : "no attribute bonuses";
        }
    }
}
