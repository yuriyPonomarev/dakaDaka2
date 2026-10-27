using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Stats;

namespace Emberdelve.Data
{
    /// <summary>
    /// Class stats and kit. The player controller never hard-codes class behaviour: it applies this
    /// asset through ClassLoadout, so new classes only need a new asset plus a weapon asset.
    /// </summary>
    [CreateAssetMenu(menuName = "Emberdelve/02 Character Class", fileName = "CharacterClassData")]
    public class CharacterClassData : ScriptableObject
    {
        [Header("Identity")]
        public string classId = "class";
        public string displayName = "Class";
        [TextArea(3, 6)] public string description = "";
        public Color accentColor = Color.white;

        [Header("Attributes (Docs/Design/ATTRIBUTES.md)")]
        [Tooltip("Ability score, legal band 1..30. Modifier = floor((score - 10) / 2.0). Base values only: " +
                 "equipment bonuses are applied by the rules, never written back into these fields.")]
        public int strength = 10;
        public int dexterity = 10;
        public int constitution = 10;
        public int intelligence = 10;
        public int wisdom = 10;
        public int charisma = 10;

        [Header("Vitals")]
        public int maxHealth = 120;
        [Tooltip("Passive regeneration per second once out of danger.")]
        public float healthRegenPerSecond = 0f;
        [Tooltip("Seconds without damage before regeneration starts.")]
        public float regenDelay = 6f;

        [Header("Mana")]
        public int maxMana = 100;
        [Tooltip("Mana restored per second once the regen delay has passed.")]
        public float manaRegenPerSecond = 7f;
        [Tooltip("Seconds after spending mana before regeneration resumes.")]
        public float manaRegenDelay = 3f;

        [Header("Movement")]
        public float moveSpeed = 3.6f;
        public float sprintSpeed = 5.4f;
        public float crouchSpeed = 1.7f;
        public float jumpHeight = 1.15f;
        public float standHeight = 1.8f;
        public float crouchHeight = 1.15f;
        public float standEyeHeight = 1.68f;
        public float crouchEyeHeight = 1.05f;

        [Header("Kit")]
        public WeaponData primaryWeapon;
        [Tooltip("Optional off-hand shield. A shield can block but can never parry; see DefenseProfile.")]
        public WeaponData offhandShield;
        [Tooltip("Off-hand / secondary weapon the class can switch to at runtime (Warrior crossbow, Archer rapier).")]
        public WeaponData secondaryWeapon;

        [Tooltip("Exactly two active skills, in the order they appear on the skill bar.")]
        public SkillData[] activeSkills = new SkillData[2];
        [Tooltip("Exactly two passive skills. Each one must actually change something.")]
        public SkillData[] passiveSkills = new SkillData[2];

        public ItemData[] startingItems;

        [Header("Ranged reserve (ignored by classes without a ranged weapon)")]
        public int startingReserveAmmo = 0;
        public int maxReserveAmmo = 0;

        [Header("Secondary weapon reserve (used when the secondary weapon is ranged)")]
        public int secondaryStartingReserveAmmo = 0;
        public int secondaryMaxReserveAmmo = 0;

        [Header("Guard (melee classes)")]
        [Tooltip("0 disables the block ability for this class.")]
        [Range(0f, 0.95f)] public float blockReduction = 0.65f;
        [Tooltip("Maximum angle (degrees) between facing and the incoming hit for the guard to hold.")]
        [Range(30f, 180f)] public float blockArc = 130f;
        [Tooltip("Movement speed multiplier while guarding.")]
        [Range(0.1f, 1f)] public float guardSpeedMultiplier = 0.45f;

        public bool CanGuard => blockReduction > 0f;

        public int EffectiveMaxMana => Mathf.Max(1, maxMana);

        /// <summary>
        /// The six authored scores as one immutable AttributeScores value. Read-only: it is built fresh
        /// from the inspector fields on every call, and AttributeScores re-clamps each score into 1..30,
        /// so a value written programmatically (or a zero-initialized asset) can never expose an illegal
        /// score. Nothing here derives HP/mana/speed and nothing branches on a class id or name.
        /// </summary>
        public AttributeScores BaseAttributes
        {
            get
            {
                return new AttributeScores(strength, dexterity, constitution,
                    intelligence, wisdom, charisma);
            }
        }

        /// <summary>
        /// The authored BASE vitals in the order <see cref="DerivedStatBases"/> takes them. Built fresh from
        /// the inspector fields on every call and never cached, so the rules always receive the pristine
        /// class numbers and equipment can only be added on top inside a derivation, never written back
        /// into this asset.
        /// </summary>
        public DerivedStatBases AttributeBases
        {
            get
            {
                return new DerivedStatBases(
                    Mathf.Max(1, maxHealth),
                    EffectiveMaxMana,
                    Mathf.Max(0f, manaRegenPerSecond),
                    moveSpeed);
            }
        }

        /// <summary>
        /// The kit contract every class must satisfy, checked once at class-apply time and reported as
        /// data problems instead of being patched around in code. Fills the caller's slot arrays so the
        /// caller never has to guess how many skills were declared.
        /// </summary>
        public static void ValidateKit(CharacterClassData data, SkillData[] activeOut, SkillData[] passiveOut,
            int activeCapacity, int passiveCapacity, List<string> problems)
        {
            if (data == null)
            {
                problems.Add("no CharacterClassData to validate");
                return;
            }

            Collect(data.activeSkills, activeOut, activeCapacity, SkillSlot.Active, "active", problems);
            Collect(data.passiveSkills, passiveOut, passiveCapacity, SkillSlot.Passive, "passive", problems);

            for (int i = 0; i < activeOut.Length; i++)
            {
                for (int j = i + 1; j < activeOut.Length; j++)
                {
                    if (activeOut[i] == null || activeOut[j] == null) continue;
                    if (activeOut[i] == activeOut[j])
                    {
                        problems.Add($"{data.displayName} lists '{activeOut[i].displayName}' twice in its active kit");
                    }
                }
            }

            for (int i = 0; i < passiveOut.Length; i++)
            {
                for (int j = i + 1; j < passiveOut.Length; j++)
                {
                    if (passiveOut[i] == null || passiveOut[j] == null) continue;
                    if (passiveOut[i] == passiveOut[j])
                    {
                        problems.Add($"{data.displayName} lists '{passiveOut[i].displayName}' twice in its passive kit");
                    }
                }
            }
        }

        private static void Collect(SkillData[] declared, SkillData[] target, int capacity, SkillSlot expected,
            string label, List<string> problems)
        {
            if (declared == null || declared.Length != capacity)
            {
                int actual = declared != null ? declared.Length : 0;
                problems.Add($"a kit must declare exactly {capacity} {label} skills, found {actual}");
            }

            int slots = target != null ? target.Length : 0;
            for (int i = 0; i < slots; i++)
            {
                if (declared == null || i >= declared.Length) continue;

                SkillData skill = declared[i];
                if (skill == null)
                {
                    problems.Add($"{label} skill slot {i + 1} is empty");
                    continue;
                }

                if (skill.slot != expected)
                {
                    problems.Add($"'{skill.displayName}' [{skill.skillId}] is a {skill.slot} skill but sits in a {label} slot");
                    continue;
                }

                target[i] = skill;
            }
        }

        private void OnValidate()
        {
            maxMana = Mathf.Max(1, maxMana);
            manaRegenPerSecond = Mathf.Max(0f, manaRegenPerSecond);
            manaRegenDelay = Mathf.Max(0f, manaRegenDelay);

            maxHealth = Mathf.Max(1, maxHealth);

            strength = AttributeRules.ClampScore(strength);
            dexterity = AttributeRules.ClampScore(dexterity);
            constitution = AttributeRules.ClampScore(constitution);
            intelligence = AttributeRules.ClampScore(intelligence);
            wisdom = AttributeRules.ClampScore(wisdom);
            charisma = AttributeRules.ClampScore(charisma);

            if (activeSkills == null || activeSkills.Length != 2)
                Debug.LogWarning($"[CharacterClassData] {displayName} must declare exactly 2 active skills.", this);
            if (passiveSkills == null || passiveSkills.Length != 2)
                Debug.LogWarning($"[CharacterClassData] {displayName} must declare exactly 2 passive skills.", this);
        }
    }
}
