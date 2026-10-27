using System;
using System.Text;

namespace Emberdelve.Stats
{
    /// <summary>
    /// The six ability scores named in Docs/Design/ATTRIBUTES.md. Typed instead of string-keyed so a
    /// definition or a server packet cannot smuggle an unknown attribute name into the rules.
    /// </summary>
    public enum SixAttribute
    {
        Strength = 0,
        Dexterity = 1,
        Constitution = 2,
        Intelligence = 3,
        Wisdom = 4,
        Charisma = 5
    }

    /// <summary>
    /// Which score a weapon's damage scale reads: STR for sword/axe, DEX for bow/crossbow, and the
    /// better of STR/DEX for a finesse weapon such as the rapier.
    /// </summary>
    public enum WeaponDamageScale
    {
        Melee = 0,
        Ranged = 1,
        Finesse = 2
    }

    /// Legal bounds shared by every attribute value in the project.
    public static class AttributeLimits
    {
        public const int MinScore = 1;
        public const int MaxScore = 30;
        /// <summary>Score whose modifier is exactly zero, i.e. floor((10 - 10) / 2.0) = 0.</summary>
        public const int ModifierAnchor = 10;
    }

    /// <summary>
    /// Immutable six-score value. Scores are clamped to 1..30 when the value is created AND every
    /// public read re-applies the same bound: default(AttributeScores), new AttributeScores[1] and
    /// any other zero-initialized storage (a field of another value type, an array, a deserialized
    /// buffer) bypass the constructor and leave 0 in the backing fields. Re-clamping an already
    /// legal score is a no-op, so explicitly constructed values are unaffected while a default
    /// value can never expose an illegal 0 - it reads as the legal minimum 1.
    /// </summary>
    public readonly struct AttributeScores
    {
        private readonly int _strength;
        private readonly int _dexterity;
        private readonly int _constitution;
        private readonly int _intelligence;
        private readonly int _wisdom;
        private readonly int _charisma;

        public AttributeScores(int strength, int dexterity, int constitution,
            int intelligence, int wisdom, int charisma)
        {
            _strength = AttributeRules.ClampScore(strength);
            _dexterity = AttributeRules.ClampScore(dexterity);
            _constitution = AttributeRules.ClampScore(constitution);
            _intelligence = AttributeRules.ClampScore(intelligence);
            _wisdom = AttributeRules.ClampScore(wisdom);
            _charisma = AttributeRules.ClampScore(charisma);
        }

        /// <summary>
        /// Single read-side normalization used by EVERY accessor of this value. It is the same
        /// clamp the constructor applies, which is what makes a zero-initialized instance (default
        /// struct, array element, default snapshot base) report the legal minimum instead of 0.
        /// </summary>
        private static int Read(int stored)
        {
            return AttributeRules.ClampScore(stored);
        }

        public int Strength => Read(_strength);
        public int Dexterity => Read(_dexterity);
        public int Constitution => Read(_constitution);
        public int Intelligence => Read(_intelligence);
        public int Wisdom => Read(_wisdom);
        public int Charisma => Read(_charisma);

        public int Get(SixAttribute attribute)
        {
            switch (attribute)
            {
                case SixAttribute.Strength: return Read(_strength);
                case SixAttribute.Dexterity: return Read(_dexterity);
                case SixAttribute.Constitution: return Read(_constitution);
                case SixAttribute.Intelligence: return Read(_intelligence);
                case SixAttribute.Wisdom: return Read(_wisdom);
                case SixAttribute.Charisma: return Read(_charisma);
                default: return AttributeLimits.MinScore;
            }
        }

        /// <summary>floor((score - 10) / 2.0) of this value's own score for one attribute.</summary>
        public int Modifier(SixAttribute attribute)
        {
            return AttributeRules.Modifier(Get(attribute));
        }

        /// <summary>
        /// Returns a NEW value with the bonus totals added to these scores and re-clamped. These
        /// scores stay untouched, which is what makes "recompute from the pristine base, never
        /// accumulate" possible: callers keep the pristine snapshot and derive a fresh value. The
        /// base term is the NORMALIZED score, so a bonus on a default value adds to 1 (1+1=2) and
        /// never to the raw zero-initialized field.
        /// </summary>
        public AttributeScores WithBonuses(AttributeBonuses bonuses)
        {
            return new AttributeScores(
                Read(_strength) + bonuses.Total(SixAttribute.Strength),
                Read(_dexterity) + bonuses.Total(SixAttribute.Dexterity),
                Read(_constitution) + bonuses.Total(SixAttribute.Constitution),
                Read(_intelligence) + bonuses.Total(SixAttribute.Intelligence),
                Read(_wisdom) + bonuses.Total(SixAttribute.Wisdom),
                Read(_charisma) + bonuses.Total(SixAttribute.Charisma));
        }

        /// <summary>Fresh array in enum order, for a panel/tooltip that wants all six at once.</summary>
        public int[] ToArray()
        {
            return new int[]
            {
                Read(_strength), Read(_dexterity), Read(_constitution),
                Read(_intelligence), Read(_wisdom), Read(_charisma)
            };
        }

        public string Describe()
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < AttributeRules.AttributeCount; i++)
            {
                SixAttribute attribute = (SixAttribute)i;
                if (builder.Length > 0) builder.Append(' ');
                builder.Append(AttributeRules.Key(attribute)).Append('=')
                    .Append(Get(attribute)).Append('(').Append(Modifier(attribute)).Append(')');
            }
            return builder.ToString();
        }
    }

    /// <summary>
    /// Immutable per-attribute bonus totals (fixed item bonuses plus rolled affixes, summed by the
    /// caller). Individual contributions are intentionally NOT clamped here: score bounds belong to
    /// the effective score, and affix value ranges are validated where the affix is rolled.
    /// </summary>
    public readonly struct AttributeBonuses
    {
        private readonly int _strength;
        private readonly int _dexterity;
        private readonly int _constitution;
        private readonly int _intelligence;
        private readonly int _wisdom;
        private readonly int _charisma;

        public AttributeBonuses(int strength, int dexterity, int constitution,
            int intelligence, int wisdom, int charisma)
        {
            _strength = strength;
            _dexterity = dexterity;
            _constitution = constitution;
            _intelligence = intelligence;
            _wisdom = wisdom;
            _charisma = charisma;
        }

        /// <summary>No bonus on any score.</summary>
        public static AttributeBonuses None
        {
            get { return new AttributeBonuses(0, 0, 0, 0, 0, 0); }
        }

        public int Total(SixAttribute attribute)
        {
            switch (attribute)
            {
                case SixAttribute.Strength: return _strength;
                case SixAttribute.Dexterity: return _dexterity;
                case SixAttribute.Constitution: return _constitution;
                case SixAttribute.Intelligence: return _intelligence;
                case SixAttribute.Wisdom: return _wisdom;
                case SixAttribute.Charisma: return _charisma;
                default: return 0;
            }
        }

        /// <summary>Sum of two bonus sets (e.g. two equipped pieces), still unclamped.</summary>
        public AttributeBonuses Plus(AttributeBonuses other)
        {
            return new AttributeBonuses(
                _strength + other._strength,
                _dexterity + other._dexterity,
                _constitution + other._constitution,
                _intelligence + other._intelligence,
                _wisdom + other._wisdom,
                _charisma + other._charisma);
        }

        public bool IsEmpty
        {
            get
            {
                return _strength == 0 && _dexterity == 0 && _constitution == 0 &&
                       _intelligence == 0 && _wisdom == 0 && _charisma == 0;
            }
        }

        public int[] ToArray()
        {
            return new int[] { _strength, _dexterity, _constitution, _intelligence, _wisdom, _charisma };
        }
    }

    /// <summary>
    /// A pristine base snapshot plus the currently applied bonus set. Effective scores are computed
    /// on demand from the untouched base every time, so an equip/unequip cycle can never stack a
    /// bonus onto a previously bonused score.
    /// </summary>
    public readonly struct AttributeSnapshot
    {
        private readonly AttributeScores _base;
        private readonly AttributeBonuses _bonuses;

        public AttributeSnapshot(AttributeScores pristineBase, AttributeBonuses bonuses)
        {
            _base = pristineBase;
            _bonuses = bonuses;
        }

        public AttributeSnapshot(AttributeScores pristineBase)
            : this(pristineBase, AttributeBonuses.None)
        {
        }

        /// <summary>
        /// The class-authored scores, never modified by equipment. A default(AttributeSnapshot) base
        /// is a default(AttributeScores), whose accessors report the legal minimum 1.
        /// </summary>
        public AttributeScores Base { get { return _base; } }

        /// <summary>The bonus totals currently applied on top of the pristine base.</summary>
        public AttributeBonuses Bonuses { get { return _bonuses; } }

        /// <summary>
        /// clamp(normalizedBase + bonusTotal, 1, 30), always derived from the pristine base after its
        /// scores are normalized, so a default snapshot plus a bonus still lands on a legal score.
        /// </summary>
        public AttributeScores Effective { get { return _base.WithBonuses(_bonuses); } }

        public int EffectiveScore(SixAttribute attribute)
        {
            return Effective.Get(attribute);
        }

        public int Modifier(SixAttribute attribute)
        {
            return AttributeRules.Modifier(EffectiveScore(attribute));
        }

        /// <summary>
        /// Returns a NEW snapshot whose bonuses are REPLACED (not added) while the pristine base is
        /// carried over unchanged. Unequipping is therefore "recompute with a smaller bonus set",
        /// never "subtract from a mutated score".
        /// </summary>
        public AttributeSnapshot WithBonuses(AttributeBonuses newBonuses)
        {
            return new AttributeSnapshot(_base, newBonuses);
        }
    }

    /// <summary>
    /// The caller-supplied BASE numbers ATTRIBUTES.md says a class definition owns after the
    /// amendment (max health/mana, mana regeneration, move speed). The rules never
    /// look up a class asset or guess a class: every derivation takes these values as arguments.
    /// </summary>
    public readonly struct DerivedStatBases
    {
        public readonly int MaxHealth;
        public readonly int MaxMana;
        public readonly float ManaRegen;
        public readonly float MoveSpeed;

        public DerivedStatBases(int maxHealth, int maxMana,
            float manaRegen, float moveSpeed)
        {
            MaxHealth = maxHealth;
            MaxMana = maxMana;
            ManaRegen = manaRegen;
            MoveSpeed = moveSpeed;
        }
    }

    /// <summary>Immutable result of one derivation pass.</summary>
    public readonly struct DerivedStats
    {
        public readonly AttributeScores Effective;
        public readonly int MaxHealth;
        public readonly int MaxMana;
        public readonly float ManaRegen;
        public readonly float MoveSpeed;

        public DerivedStats(AttributeScores effective, int maxHealth, int maxMana,
            float manaRegen, float moveSpeed)
        {
            Effective = effective;
            MaxHealth = maxHealth;
            MaxMana = maxMana;
            ManaRegen = manaRegen;
            MoveSpeed = moveSpeed;
        }

        public int Modifier(SixAttribute attribute)
        {
            return AttributeRules.Modifier(Effective.Get(attribute));
        }

        public string Describe()
        {
            return "hp=" + MaxHealth + " mana=" + MaxMana +
                   " manaRegen=" + ManaRegen.ToString("0.###") +
                   " moveSpeed=" + MoveSpeed.ToString("0.####");
        }
    }

    /// <summary>
    /// Pure six-attribute math (Docs/Design/ATTRIBUTES.md). No UnityEngine types, no
    /// ScriptableObject/MonoBehaviour/scene/asset lookups, no static mutable state: every function
    /// is a total function of its arguments, so the same inputs always give the same numbers and a
    /// server can run the identical code the client displays.
    ///
    /// Later work, deliberately NOT invented here: sprint/crouch/guard speed multipliers feeding
    /// MoveSpeed's passive multiplier, item flat move-speed from gear,
    /// current-pool clamping on equip, and the StatsComponent/item-definition plumbing that supplies
    /// these base values from CharacterClassData/ItemData.
    /// </summary>
    public static class AttributeRules
    {
        public const int AttributeCount = 6;

        // Derived-pool floors from ATTRIBUTES.md.
        public const int MinDerivedHealth = 25;
        public const int MinDerivedMana = 10;
        public const float MinManaRegen = 0.1f;
        public const float MinMoveSpeed = 2f;
        public const float MaxMoveSpeed = 8f;
        public const float MinMoveSpeedFactor = 0.5f;
        public const float MoveSpeedPerModifier = 0.025f;
        public const int HealthPerConstitutionModifier = 10;
        public const int ManaPerIntelligenceModifier = 5;
        public const float ManaRegenPerWisdomModifier = 0.25f;
        public const float DamagePerModifier = 0.04f;
        public const float MinDamageFactor = 0.2f;
        public const float EffectScalePerCharismaModifier = 0.04f;
        public const float MinEffectScale = 0.6f;
        public const float MaxEffectScale = 1.4f;

        /// <summary>Clamps a score into the legal 1..30 band.</summary>
        public static int ClampScore(int score)
        {
            if (score < AttributeLimits.MinScore) return AttributeLimits.MinScore;
            if (score > AttributeLimits.MaxScore) return AttributeLimits.MaxScore;
            return score;
        }

        /// <summary>
        /// floor((score - 10) / 2.0) with true floor semantics, so negative odd scores round away
        /// from zero: 9 -> -1, 5 -> -3, 3 -> -4, while 11 -> 0 and 16 -> 3.
        /// </summary>
        public static int Modifier(int score)
        {
            return (int)Math.Floor((ClampScore(score) - AttributeLimits.ModifierAnchor) / 2.0);
        }

        /// <summary>clamp(baseScore + bonusTotal, 1, 30) - one attribute, from a pristine base.</summary>
        public static int EffectiveScore(int pristineBaseScore, int bonusTotal)
        {
            return ClampScore(pristineBaseScore + bonusTotal);
        }

        public static int MaxHealth(int baseMaxHealth, int constitutionModifier)
        {
            return Math.Max(MinDerivedHealth, baseMaxHealth + HealthPerConstitutionModifier * constitutionModifier);
        }

        public static int MaxMana(int baseMaxMana, int intelligenceModifier)
        {
            return Math.Max(MinDerivedMana, baseMaxMana + ManaPerIntelligenceModifier * intelligenceModifier);
        }

        public static float ManaRegen(float baseManaRegen, int wisdomModifier)
        {
            return Math.Max(MinManaRegen, baseManaRegen + ManaRegenPerWisdomModifier * wisdomModifier);
        }

        /// <summary>
        /// clamp(base * max(0.5, 1 + 0.025 * dexModifier) * passiveMoveMultiplier + itemFlatMoveSpeed,
        /// 2, 8). The passive multiplier and the flat item term are explicit inputs; sprint/crouch and
        /// gear are later integration work, not rules invented here.
        /// </summary>
        public static float MoveSpeed(float baseMoveSpeed, int dexterityModifier,
            float passiveMoveMultiplier, float itemFlatMoveSpeed)
        {
            float factor = Math.Max(MinMoveSpeedFactor, 1f + MoveSpeedPerModifier * dexterityModifier);
            float speed = baseMoveSpeed * factor * passiveMoveMultiplier + itemFlatMoveSpeed;
            return ClampMoveSpeed(speed);
        }

        /// <summary>Move speed with no passive multiplier and no flat item term (1 and 0).</summary>
        public static float MoveSpeed(float baseMoveSpeed, int dexterityModifier)
        {
            return MoveSpeed(baseMoveSpeed, dexterityModifier, 1f, 0f);
        }

        public static float ClampMoveSpeed(float speed)
        {
            if (speed < MinMoveSpeed) return MinMoveSpeed;
            if (speed > MaxMoveSpeed) return MaxMoveSpeed;
            return speed;
        }

        /// <summary>base * max(0.2, 1 + 0.04 * modifier) - the shared weapon damage shape.</summary>
        public static float ScaleDamage(float baseDamage, int modifier)
        {
            return baseDamage * Math.Max(MinDamageFactor, 1f + DamagePerModifier * modifier);
        }

        public static float MeleeDamage(float baseDamage, int strengthModifier)
        {
            return ScaleDamage(baseDamage, strengthModifier);
        }

        public static float RangedDamage(float baseDamage, int dexterityModifier)
        {
            return ScaleDamage(baseDamage, dexterityModifier);
        }

        /// <summary>Finesse weapon: the better of the STR and DEX modifiers.</summary>
        public static float FinesseDamage(float baseDamage, int strengthModifier, int dexterityModifier)
        {
            return ScaleDamage(baseDamage, Math.Max(strengthModifier, dexterityModifier));
        }

        /// <summary>Damage scale chosen by weapon type, read from an effective score set.</summary>
        public static float WeaponDamage(float baseDamage, WeaponDamageScale scale, AttributeScores effective)
        {
            switch (scale)
            {
                case WeaponDamageScale.Melee:
                    return MeleeDamage(baseDamage, effective.Modifier(SixAttribute.Strength));
                case WeaponDamageScale.Ranged:
                    return RangedDamage(baseDamage, effective.Modifier(SixAttribute.Dexterity));
                case WeaponDamageScale.Finesse:
                    return FinesseDamage(baseDamage,
                        effective.Modifier(SixAttribute.Strength),
                        effective.Modifier(SixAttribute.Dexterity));
                default:
                    return ScaleDamage(baseDamage, 0);
            }
        }

        /// <summary>
        /// clamp(1 + 0.04 * chaModifier, 0.6, 1.4). Scales beneficial magnitudes/durations only -
        /// never mana cost or projectile count.
        /// </summary>
        public static float AbilityEffectScale(int charismaModifier)
        {
            float scale = 1f + EffectScalePerCharismaModifier * charismaModifier;
            if (scale < MinEffectScale) return MinEffectScale;
            if (scale > MaxEffectScale) return MaxEffectScale;
            return scale;
        }

        public static float AbilityEffectScale(AttributeScores effective)
        {
            return AbilityEffectScale(effective.Modifier(SixAttribute.Charisma));
        }

        /// <summary>One full derivation pass from a snapshot plus explicit base values.</summary>
        public static DerivedStats Derive(AttributeSnapshot snapshot, DerivedStatBases bases)
        {
            return Derive(snapshot.Effective, bases);
        }

        /// <summary>One full derivation pass from effective scores plus explicit base values.</summary>
        public static DerivedStats Derive(AttributeScores effective, DerivedStatBases bases)
        {
            return new DerivedStats(
                effective,
                MaxHealth(bases.MaxHealth, effective.Modifier(SixAttribute.Constitution)),
                MaxMana(bases.MaxMana, effective.Modifier(SixAttribute.Intelligence)),
                ManaRegen(bases.ManaRegen, effective.Modifier(SixAttribute.Wisdom)),
                MoveSpeed(bases.MoveSpeed, effective.Modifier(SixAttribute.Dexterity)));
        }

        /// <summary>
        /// Lowers a current pool to a new maximum without ever raising it: shrinking capacity clamps
        /// the value down, growing capacity does not hand out free healing/refill. Wiring this to the
        /// live pools on equip is later integration work.
        /// </summary>
        public static int ClampCurrentToMax(int current, int newMax)
        {
            return current > newMax ? newMax : current;
        }

        public static float ClampCurrentToMax(float current, float newMax)
        {
            return current > newMax ? newMax : current;
        }

        /// <summary>Canonical lowercase key, matching the allowlisted item/affix stat keys.</summary>
        public static string Key(SixAttribute attribute)
        {
            switch (attribute)
            {
                case SixAttribute.Strength: return "strength";
                case SixAttribute.Dexterity: return "dexterity";
                case SixAttribute.Constitution: return "constitution";
                case SixAttribute.Intelligence: return "intelligence";
                case SixAttribute.Wisdom: return "wisdom";
                case SixAttribute.Charisma: return "charisma";
                default: return string.Empty;
            }
        }

        /// <summary>Allowlist parse: only the six canonical keys (case-insensitive) are accepted.</summary>
        public static bool TryParseKey(string key, out SixAttribute attribute)
        {
            attribute = SixAttribute.Strength;
            if (string.IsNullOrEmpty(key)) return false;

            string normalized = key.Trim().ToLowerInvariant();
            for (int i = 0; i < AttributeCount; i++)
            {
                if (Key((SixAttribute)i) != normalized) continue;
                attribute = (SixAttribute)i;
                return true;
            }
            return false;
        }
    }
}
