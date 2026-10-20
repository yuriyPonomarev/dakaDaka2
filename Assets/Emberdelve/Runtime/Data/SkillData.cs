using UnityEngine;
using Emberdelve.Core;

namespace Emberdelve.Data
{
    public enum SkillSlot
    {
        Active = 0,
        Passive = 1,
    }

    /// <summary>
    /// Which runtime behavior consumes this asset. The behavior is looked up in SkillBehaviorCatalog,
    /// so a new skill that reuses an existing effect kind is a new asset with no new code, and a new
    /// effect kind is one small behavior class plus one catalog line. Nothing branches on the class,
    /// the skill id or the display name anywhere in the runtime.
    /// </summary>
    public enum SkillEffectKind
    {
        None = 0,
        Dash = 1,
        AreaCry = 2,
        SpreadShot = 3,
        PiercingShot = 4,
        PassiveStat = 5,
    }

    /// <summary>What a passive is allowed to change. Consumed through CharacterStatStack.</summary>
    public enum SkillStat
    {
        MoveSpeed = 0,
        SprintSpeed = 1,
        CrouchSpeed = 2,
        MeleeDamage = 3,
        RangedDamage = 4,
        BlockReduction = 5,
        MaxMana = 6,
        ManaRegen = 7,
        MaxHealth = 8,
        /// <summary>
        /// Physical armor POINTS (Docs/Design/ARMOR_RULES.md, U2B8). Appended after the existing 0..8 values so
        /// every already-authored modifier keeps its numeric meaning. It is a FLAT rating summed once over the
        /// passives and worn gear that declare it, read through the defender contract the rig implements and
        /// mitigated by <see cref="Emberdelve.Combat.DamageResolver"/> on Physical damage only. It is not a pool,
        /// not a multiplier and deliberately not <see cref="BlockReduction"/>: an armor affix never becomes guard.
        /// </summary>
        Armor = 9,
    }

    public enum SkillModifierMode
    {
        /// <summary>value = 1.2 means +20% of the base value.</summary>
        Multiplier = 0,
        /// <summary>value = 0.12 means add 0.12 to the base value.</summary>
        Flat = 1,
    }

    /// <summary>One stat change a passive makes. A passive may carry several of them.</summary>
    [System.Serializable]
    public struct StatModifier
    {
        public SkillStat stat;
        public SkillModifierMode mode;
        public float value;

        public StatModifier(SkillStat stat, SkillModifierMode mode, float value)
        {
            this.stat = stat;
            this.mode = mode;
            this.value = value;
        }

        public override string ToString()
        {
            return mode == SkillModifierMode.Multiplier
                ? $"{stat} x{value:F2}"
                : $"{stat} +{value:F2}";
        }
    }

    /// <summary>
    /// One skill of a class kit: its cost, its cooldown, what it requires, and the tuning its behavior
    /// reads. Active and passive skills share this asset so the kit is one list of data assets, and no
    /// component hard-codes a single skill.
    /// </summary>
    [CreateAssetMenu(menuName = "Emberdelve/07 Skill", fileName = "SkillData")]
    public class SkillData : ScriptableObject
    {
        [Header("Identity")]
        public string skillId = "skill";
        public string displayName = "Skill";
        [TextArea(2, 5)] public string description = "";
        public Color iconColor = new Color(0.85f, 0.6f, 0.3f);

        [Header("Kit slot")]
        public SkillSlot slot = SkillSlot.Active;
        [Tooltip("Selects the runtime behavior. Passive skills use PassiveStat.")]
        public SkillEffectKind effect = SkillEffectKind.None;

        [Header("Cost (active skills)")]
        public int manaCost = 0;
        [Range(0f, 60f)] public float cooldownSeconds = 6f;

        [Header("Requirements")]
        [Tooltip("Castable only while a ranged weapon is in the main hand.")]
        public bool requiresRangedWeapon = false;
        [Tooltip("Castable only while a melee weapon is in the main hand.")]
        public bool requiresMeleeWeapon = false;
        [Tooltip("Each projectile this skill fires also consumes one round of the active weapon's magazine.")]
        public bool consumesAmmoPerProjectile = false;

        [Header("Effect tuning (read only by the behavior this skill selects)")]
        [Tooltip("AreaCry: radius of the burst.")]
        public float radius = 5f;
        [Tooltip("AreaCry: how many hostile entities the burst may hit.")]
        public int maxTargets = 4;
        [Tooltip("Dash: ground distance covered.")]
        public float dashDistance = 4.5f;
        [Tooltip("Dash: how long the dash lasts.")]
        public float dashDuration = 0.22f;
        [Tooltip("SpreadShot: number of projectiles.")]
        public int projectileCount = 1;
        [Tooltip("SpreadShot: total angle covered by the spread.")]
        public float spreadDegrees = 24f;
        [Tooltip("PiercingShot/SpreadShot: how many valid targets one projectile may resolve.")]
        public int projectileMaxTargets = 1;

        [Header("Damage")]
        [Tooltip("Multiplies the weapon's own damage. Ignored when Flat Damage is used.")]
        public float damageMultiplier = 1f;
        [Tooltip("-1 = use the weapon in hand. 0 or more = this skill's own damage, applied whatever is held.")]
        public float flatDamage = -1f;

        [Header("Status applied to targets that were hit")]
        public StatusKind targetStatus = StatusKind.None;
        public float targetStatusDuration = 4f;
        [Tooltip("Multiplier the status means: Slow -> speed, Empowered -> outgoing damage.")]
        public float targetStatusMagnitude = 0.6f;

        [Header("Status applied to the caster")]
        public bool affectsSelf = false;
        public StatusKind selfStatus = StatusKind.None;
        public float selfStatusDuration = 6f;
        public float selfStatusMagnitude = 1.2f;

        [Header("Passive modifiers (slot = Passive)")]
        public StatModifier[] modifiers = new StatModifier[0];

        public bool IsPassive => slot == SkillSlot.Passive;
        public bool IsActive => slot == SkillSlot.Active;

        /// <summary>True when this skill brings its own damage number instead of using the weapon held.</summary>
        public bool UsesFlatDamage => flatDamage >= 0f;

        public StatusSpec TargetStatusSpec => targetStatus == StatusKind.None
            ? StatusSpec.None
            : new StatusSpec(targetStatus, targetStatusDuration, targetStatusMagnitude);

        public StatusSpec SelfStatusSpec => selfStatus == StatusKind.None
            ? StatusSpec.None
            : new StatusSpec(selfStatus, selfStatusDuration, selfStatusMagnitude);

        public float EffectiveCooldown => Mathf.Max(0f, cooldownSeconds);
        public int EffectiveManaCost => Mathf.Max(0, manaCost);

        private void OnValidate()
        {
            manaCost = Mathf.Max(0, manaCost);
            cooldownSeconds = Mathf.Max(0f, cooldownSeconds);
            radius = Mathf.Max(0.5f, radius);
            maxTargets = Mathf.Max(1, maxTargets);
            dashDistance = Mathf.Max(0.5f, dashDistance);
            dashDuration = Mathf.Max(0.05f, dashDuration);
            projectileCount = Mathf.Max(1, projectileCount);
            spreadDegrees = Mathf.Clamp(spreadDegrees, 0f, 170f);
            projectileMaxTargets = Mathf.Max(1, projectileMaxTargets);
            damageMultiplier = Mathf.Max(0.05f, damageMultiplier);
            targetStatusDuration = Mathf.Max(0f, targetStatusDuration);
            selfStatusDuration = Mathf.Max(0f, selfStatusDuration);

            if (slot == SkillSlot.Passive && (modifiers == null || modifiers.Length == 0))
            {
                Debug.LogWarning($"[SkillData] '{displayName}' [{skillId}] is a passive with no stat modifiers, so it changes nothing.", this);
            }
        }

        /// <summary>Short human summary used by the HUD and the lobby card.</summary>
        public string ShortSummary
        {
            get
            {
                if (IsPassive)
                {
                    if (modifiers == null || modifiers.Length == 0) return "No effect";
                    System.Text.StringBuilder builder = new System.Text.StringBuilder();
                    for (int i = 0; i < modifiers.Length; i++)
                    {
                        if (i > 0) builder.Append(", ");
                        StatModifier modifier = modifiers[i];
                        if (modifier.mode == SkillModifierMode.Multiplier)
                        {
                            builder.Append($"+{Mathf.RoundToInt((modifier.value - 1f) * 100f)}% {HumanStat(modifier.stat)}");
                        }
                        else
                        {
                            builder.Append($"+{modifier.value:F2} {HumanStat(modifier.stat)}");
                        }
                    }
                    return builder.ToString();
                }

                return $"{manaCost} MP  |  {cooldownSeconds:F0}s cooldown";
            }
        }

        private static string HumanStat(SkillStat stat)
        {
            switch (stat)
            {
                case SkillStat.MoveSpeed: return "move speed";
                case SkillStat.SprintSpeed: return "sprint speed";
                case SkillStat.CrouchSpeed: return "crouch speed";
                case SkillStat.MeleeDamage: return "melee damage";
                case SkillStat.RangedDamage: return "ranged damage";
                case SkillStat.BlockReduction: return "block reduction";
                case SkillStat.MaxMana: return "max mana";
                case SkillStat.ManaRegen: return "mana regen";
                case SkillStat.MaxHealth: return "max health";
                case SkillStat.Armor: return "armor";
                default: return stat.ToString();
            }
        }
    }
}
