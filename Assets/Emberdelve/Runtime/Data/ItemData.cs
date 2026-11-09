using UnityEngine;
using Emberdelve.Stats;

namespace Emberdelve.Data
{
    public enum ItemKind { Currency, Consumable, Relic, AmmoPack, Weapon, Armor }

    /// <summary>
    /// Which equipment slot an item belongs in, or None when it is carried rather than worn. The slot is
    /// data: Equipment never branches on an item id or a display name, and a new slot is one enum value plus
    /// one place in the equipment screen.
    /// </summary>
    public enum EquipmentSlot
    {
        None = 0,
        Weapon = 1,
        OffHand = 2,
        Head = 3,
        Chest = 4,
        Legs = 5,
        /// <summary>Back / cloak slot (U2B11). Appended, never reordered: assets store the integer.</summary>
        Back = 6,
        /// <summary>Gloves / gauntlets (D3b). Appended, never reordered: assets and saves store the integer.</summary>
        Hands = 7,
        /// <summary>Boots (D3b). Footpad Boots moved here from <see cref="Legs"/>; Legs stays for trousers and old saves.</summary>
        Feet = 8,
        /// <summary>Necklace (D3b). Jewelry: no armour, rolls the Jewelry affix family.</summary>
        Necklace = 9,
        /// <summary>
        /// First ring slot (D3b). A ring item authors RingLeft as its canonical slot and may be worn in either ring
        /// slot (<see cref="EquipmentSlots.Accepts"/>); the two ring slots are otherwise independent.
        /// </summary>
        RingLeft = 10,
        /// <summary>Second ring slot (D3b). See <see cref="RingLeft"/>.</summary>
        RingRight = 11,
        /// <summary>
        /// Main hand of weapon set 2 (D3c). Nothing authors this slot: a weapon item authors <see cref="Weapon"/> and
        /// <see cref="EquipmentSlots.Accepts"/> lets it stand in either main-hand slot, the way a ring fits either ring slot.
        /// Weapon (set 1) and this slot (set 2) are the two sets' main hands; the set that is NOT active contributes no stats.
        /// </summary>
        AltWeapon = 12,
        /// <summary>Off hand of weapon set 2 (D3c). A shield or off-hand item authors <see cref="OffHand"/> and fits either off-hand slot.</summary>
        AltOffHand = 13,
    }

    /// <summary>
    /// One authored ability-score bonus on an item definition: a typed attribute plus a bounded amount.
    /// Deliberately separate from StatModifier, which keeps its own documented vocabulary and is not
    /// reinterpreted here. The enum is typed so a definition cannot smuggle an unknown attribute name
    /// into the rules, and the amount band is the prototype authoring range for a fixed gear bonus.
    /// </summary>
    [System.Serializable]
    public struct AttributeBonusEntry
    {
        /// <summary>Prototype bounds for one authored item bonus. Rolled affix ranges are validated where they are rolled.</summary>
        public const int MinAmount = -10;
        public const int MaxAmount = 10;

        public SixAttribute attribute;
        public int amount;

        public AttributeBonusEntry(SixAttribute attribute, int amount)
        {
            this.attribute = attribute;
            this.amount = amount;
        }

        /// <summary>Only the six allowlisted enum values count; anything else is ignored, never coerced.</summary>
        public bool IsValidAttribute
        {
            get { return attribute >= SixAttribute.Strength && attribute <= SixAttribute.Charisma; }
        }

        /// <summary>The amount normalized into the authoring band, computed without touching the stored entry.</summary>
        public int NormalizedAmount
        {
            get
            {
                if (amount < MinAmount) return MinAmount;
                if (amount > MaxAmount) return MaxAmount;
                return amount;
            }
        }

        public override string ToString()
        {
            if (!IsValidAttribute) return "invalid attribute bonus";
            int value = NormalizedAmount;
            return $"{AttributeRules.Key(attribute)} {(value >= 0 ? "+" : "")}{value}";
        }
    }

    /// <summary>Loot and consumable definition. Value feeds the extraction score.</summary>
    [CreateAssetMenu(menuName = "Emberdelve/04 Item", fileName = "ItemData")]
    public class ItemData : ScriptableObject
    {
        [Header("Identity")]
        public string itemId = "item";
        public string displayName = "Item";
        [TextArea(2, 4)] public string description = "";

        [Header("Grid and carrying")]
        [Tooltip("Cells this item occupies in the inventory grid. A 2x3 sword needs a 2x3 hole.")]
        public Vector2Int gridFootprint = Vector2Int.one;
        [Tooltip("Only items that are not worn may stack, and this is the cap per stack.")]
        public int maxStack = 3;

        [Header("Equipment")]
        public EquipmentSlot equipSlot = EquipmentSlot.None;
        [Tooltip("For Weapon / OffHand items: the weapon this item actually puts in your hands.")]
        public WeaponData grantedWeapon;
        [Tooltip("Stat changes this item makes while it is equipped, using the same vocabulary as passive skills.")]
        public StatModifier[] statModifiers;

        [Header("Attribute bonuses (Docs/Design/ATTRIBUTES.md)")]
        [Tooltip("Fixed ability-score bonuses this item declares. Read-only until the attribute integration slice wires " +
                 "equipment recalculation; these are NOT applied to the live stats yet.")]
        public AttributeBonusEntry[] attributeBonuses;

        [Header("Behaviour")]
        public ItemKind kind = ItemKind.Relic;
        public int value = 15;
        [Tooltip("Health restored when used from the inventory (0 = not healable).")]
        public int healAmount = 0;
        [Tooltip("Ammo granted when used (0 = none).")]
        public int ammoGrant = 0;
        [Tooltip("Empty accepts the legacy ammo supply; otherwise only this weapon can use it.")]
        public string ammoWeaponId = "";
        public bool IsRecoveredAmmo => ammoGrant == 1 && !string.IsNullOrEmpty(ammoWeaponId);
        public bool MatchesAmmo(WeaponData weapon) => weapon != null && weapon.IsRanged &&
            (string.IsNullOrEmpty(ammoWeaponId) || ammoWeaponId == weapon.weaponId);

        [Header("Move penalty and timed use (D3c)")]
        [Tooltip("Move-speed penalty of WEARING this item, in reference points on a 300 base (30 = -10 percent of the final ground speed), " +
                 "summed with the held weapons' own penalty by CombatFlowRules.HeldFactor. 0 = none. Armour, jewelry and any worn piece that is " +
                 "not a weapon; a weapon item's penalty is its WeaponData.heldMoveSpeedPenalty (counted once), so this field is ignored on an item that grants a weapon.")]
        [Range(0f, 100f)] public float moveSpeedPenalty = 0f;
        [Tooltip("Seconds the player must stand and drink before the item is consumed (0 = used at once). Moving fast, being hit or attacking cancels the drink and keeps the item.")]
        [Range(0f, 10f)] public float useChannelSeconds = 0f;
        [Tooltip("Seconds over which healAmount is delivered after the item is consumed (0 = all at once). The total is exactly healAmount.")]
        [Range(0f, 60f)] public float healOverTimeSeconds = 0f;

        [Header("Blockout presentation")]
        public Color iconColor = new Color(0.85f, 0.72f, 0.35f);
        public Material worldMaterial;
        public Vector3 worldSize = new Vector3(0.28f, 0.28f, 0.28f);

        /// <summary>Cells this item needs in the grid, never smaller than one cell.</summary>
        public Vector2Int EffectiveFootprint => new Vector2Int(
            Mathf.Max(1, gridFootprint.x), Mathf.Max(1, gridFootprint.y));

        public int EffectiveMaxStack => Mathf.Max(1, maxStack);

        public bool IsEquippable => equipSlot != EquipmentSlot.None;

        /// <summary>D3c: true when using this item takes a channel and/or heals over time instead of acting at once.</summary>
        public bool HasTimedUse => healAmount > 0 && (useChannelSeconds > 0f || healOverTimeSeconds > 0f);

        /// <summary>Only carried items stack. A worn breastplate is one item in its own shape.</summary>
        public bool CanStack => !IsEquippable && EffectiveMaxStack > 1;

        /// <summary>How many units of this item may share one stack, given its shape.</summary>
        public int StackLimitForFootprint
        {
            get
            {
                Vector2Int footprint = EffectiveFootprint;
                if (!CanStack) return 1;
                // A stack shares the item's shape, so the cap is the declared cap.
                return Mathf.Max(1, EffectiveMaxStack);
            }
        }

        public bool GrantsWeapon => IsEquippable &&
            (equipSlot == EquipmentSlot.Weapon || equipSlot == EquipmentSlot.OffHand);

        /// <summary>
        /// This item's authored attribute bonuses summed into one immutable AttributeBonuses value.
        /// Pure and read-only: it never sorts, clamps or rewrites the shared definition's array. Entries
        /// with an attribute outside the six allowlisted enum values are ignored, out-of-band amounts are
        /// normalized to the authoring range before summing, and a null or empty array is simply no
        /// bonuses. Callers keep the pristine class scores and add this total on top per derivation.
        /// </summary>
        public AttributeBonuses AttributeBonusTotals
        {
            get
            {
                if (attributeBonuses == null || attributeBonuses.Length == 0) return AttributeBonuses.None;

                int strength = 0;
                int dexterity = 0;
                int constitution = 0;
                int intelligence = 0;
                int wisdom = 0;
                int charisma = 0;

                for (int i = 0; i < attributeBonuses.Length; i++)
                {
                    AttributeBonusEntry entry = attributeBonuses[i];
                    if (!entry.IsValidAttribute) continue;

                    int amount = entry.NormalizedAmount;
                    switch (entry.attribute)
                    {
                        case SixAttribute.Strength: strength += amount; break;
                        case SixAttribute.Dexterity: dexterity += amount; break;
                        case SixAttribute.Constitution: constitution += amount; break;
                        case SixAttribute.Intelligence: intelligence += amount; break;
                        case SixAttribute.Wisdom: wisdom += amount; break;
                        case SixAttribute.Charisma: charisma += amount; break;
                    }
                }

                return new AttributeBonuses(strength, dexterity, constitution,
                    intelligence, wisdom, charisma);
            }
        }

        /// <summary>True when this item declares at least one usable attribute bonus.</summary>
        public bool HasAttributeBonuses => !AttributeBonusTotals.IsEmpty;

        /// <summary>
        /// The item's own contribution to one stat, flattened: a multiplier contributes its excess and a flat
        /// contributes its value. Used by tests and by the inventory screen to show what an item really adds.
        /// </summary>
        public float StatTotal(SkillStat stat)
        {
            if (statModifiers == null) return 0f;

            float total = 0f;
            for (int i = 0; i < statModifiers.Length; i++)
            {
                StatModifier modifier = statModifiers[i];
                if (modifier.stat != stat) continue;

                total += modifier.mode == SkillModifierMode.Multiplier ? modifier.value - 1f : modifier.value;
            }

            return total;
        }

        /// <summary>The item's multiplicative contribution to one stat, 1 when it contributes nothing.</summary>
        public float MultiplierFor(SkillStat stat)
        {
            if (statModifiers == null) return 1f;

            float multiplier = 1f;
            for (int i = 0; i < statModifiers.Length; i++)
            {
                StatModifier modifier = statModifiers[i];
                if (modifier.stat != stat) continue;

                multiplier *= modifier.mode == SkillModifierMode.Multiplier ? modifier.value : 1f + modifier.value;
            }

            return multiplier;
        }

        public string ShortSummary
        {
            get
            {
                if (statModifiers == null || statModifiers.Length == 0)
                {
                    if (GrantsWeapon && grantedWeapon != null) return grantedWeapon.displayName;
                    return kind.ToString().ToLower();
                }

                System.Text.StringBuilder builder = new System.Text.StringBuilder();
                for (int i = 0; i < statModifiers.Length; i++)
                {
                    if (builder.Length > 0) builder.Append(", ");
                    builder.Append(statModifiers[i]);
                }
                return builder.Length > 0 ? builder.ToString() : kind.ToString().ToLower();
            }
        }

        private void OnValidate()
        {
            gridFootprint = new Vector2Int(Mathf.Max(1, gridFootprint.x), Mathf.Max(1, gridFootprint.y));
            value = Mathf.Max(0, value);

            // The stack rule is enforced on the asset, not argued about at runtime: an item you wear
            // cannot be stacked, and a stackable item needs a cap above one to mean anything.
            if (IsEquippable) maxStack = 1;
            else maxStack = Mathf.Max(1, maxStack);
        }
    }
}
