using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Data;

namespace Emberdelve.Skills
{
    /// <summary>
    /// The resolved stat picture for one entity: the class asset's own numbers, plus whatever its
    /// passive skills declare. Built once from data at class-apply time and read by movement, defense
    /// and the combat authority, so no component ever needs to know which passive produced a number.
    ///
    /// Order is fixed: base + flat, then multipliers. Two passives on the same stat therefore stack in a
    /// predictable way instead of depending on asset creation order.
    /// </summary>
    public sealed class CharacterStatStack
    {
        private readonly Dictionary<SkillStat, float> _multipliers = new Dictionary<SkillStat, float>();
        private readonly Dictionary<SkillStat, float> _flats = new Dictionary<SkillStat, float>();
        private readonly List<SkillData> _passives = new List<SkillData>(4);

        public IReadOnlyList<SkillData> Passives => _passives;
        public bool IsBuilt => _passives.Count > 0;

        public void Clear()
        {
            _multipliers.Clear();
            _flats.Clear();
            _passives.Clear();
        }

        public void Build(CharacterClassData classData)
        {
            Clear();
            if (classData == null) return;

            SkillData[] declared = classData.passiveSkills;
            if (declared == null) return;

            for (int i = 0; i < declared.Length; i++)
            {
                SkillData passive = declared[i];
                if (passive == null) continue;

                _passives.Add(passive);

                StatModifier[] modifiers = passive.modifiers;
                if (modifiers == null) continue;

                for (int m = 0; m < modifiers.Length; m++)
                {
                    StatModifier modifier = modifiers[m];
                    if (modifier.mode == SkillModifierMode.Multiplier)
                    {
                        _multipliers.TryGetValue(modifier.stat, out float current);
                        _multipliers[modifier.stat] = (current > 0f ? current : 1f) * Mathf.Max(0f, modifier.value);
                    }
                    else
                    {
                        _flats.TryGetValue(modifier.stat, out float current);
                        _flats[modifier.stat] = current + modifier.value;
                    }
                }
            }
        }

        public float Multiplier(SkillStat stat)
        {
            _multipliers.TryGetValue(stat, out float value);
            return value > 0f ? value : 1f;
        }

        public float Flat(SkillStat stat)
        {
            _flats.TryGetValue(stat, out float value);
            return value;
        }

        public bool Modifies(SkillStat stat) => _multipliers.ContainsKey(stat) || _flats.ContainsKey(stat);

        public float Get(SkillStat stat, float baseValue)
        {
            return (baseValue + Flat(stat)) * Multiplier(stat);
        }

        /// <summary>
        /// How much this stack adds to a base value, as a delta. Used where the consumer already reads the
        /// base value itself, such as the block reduction a defense profile resolves from class and shield
        /// assets.
        /// </summary>
        public float Bonus(SkillStat stat, float baseValue)
        {
            return Get(stat, baseValue) - baseValue;
        }

        public string Describe()
        {
            if (_passives.Count == 0) return "no passives";

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            for (int i = 0; i < _passives.Count; i++)
            {
                if (i > 0) builder.Append("  |  ");
                builder.Append(_passives[i].displayName).Append(" (").Append(_passives[i].ShortSummary).Append(')');
            }
            return builder.ToString();
        }
    }
}
