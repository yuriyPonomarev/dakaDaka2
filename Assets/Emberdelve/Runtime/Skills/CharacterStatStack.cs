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

        /// <summary>
        /// Where a set of modifiers came from. Passives and equipment both feed this stack, and keeping them
        /// separate is what makes unequipping an item exact instead of "rebuild and hope".
        /// </summary>
        private sealed class Source
        {
            public string Key;
            public readonly List<StatModifier> Modifiers = new List<StatModifier>();
            public readonly List<string> Summaries = new List<string>();
        }

        private readonly List<Source> _sources = new List<Source>();

        /// <summary>Changes whenever a direct stack mutation changes its resolved values.</summary>
        public int Version { get; private set; }

        public IReadOnlyList<SkillData> Passives => _passives;
        public bool IsBuilt => _passives.Count > 0 || _sources.Count > 0;

        public void Clear()
        {
            _multipliers.Clear();
            _flats.Clear();
            _passives.Clear();
            _sources.Clear();
            Version++;
        }

        public void Build(CharacterClassData classData)
        {
            Clear();
            if (classData == null) return;

            SkillData[] declared = classData.passiveSkills;
            if (declared == null) return;

            Source passives = new Source { Key = PassiveSourceKey };

            for (int i = 0; i < declared.Length; i++)
            {
                SkillData passive = declared[i];
                if (passive == null) continue;

                _passives.Add(passive);
                passives.Summaries.Add($"{passive.displayName} ({passive.ShortSummary})");

                StatModifier[] modifiers = passive.modifiers;
                if (modifiers == null) continue;

                for (int m = 0; m < modifiers.Length; m++)
                {
                    passives.Modifiers.Add(modifiers[m]);
                }
            }

            _sources.Add(passives);
            Recompute();
            Version++;
        }

        public const string PassiveSourceKey = "passives";
        public const string EquipmentSourceKey = "equipment";

        /// <summary>
        /// Replaces everything one source contributes and re-resolves the stack. Passing null or an empty
        /// list clears that source, which is how unequipping removes an item's contribution exactly.
        /// </summary>
        public void SetSource(string key, IReadOnlyList<StatModifier> modifiers, IReadOnlyList<string> summaries)
        {
            if (string.IsNullOrEmpty(key)) return;

            Source source = FindSource(key);
            if (source == null)
            {
                source = new Source { Key = key };
                _sources.Add(source);
            }

            source.Modifiers.Clear();
            source.Summaries.Clear();

            if (modifiers != null)
            {
                for (int i = 0; i < modifiers.Count; i++) source.Modifiers.Add(modifiers[i]);
            }

            if (summaries != null)
            {
                for (int i = 0; i < summaries.Count; i++) source.Summaries.Add(summaries[i]);
            }

            Recompute();
            Version++;
        }

        public void ClearSource(string key) => SetSource(key, null, null);

        public bool HasSourceWithModifiers(string key)
        {
            Source source = FindSource(key);
            return source != null && source.Modifiers.Count > 0;
        }

        private Source FindSource(string key)
        {
            for (int i = 0; i < _sources.Count; i++) if (_sources[i].Key == key) return _sources[i];
            return null;
        }

        private void Recompute()
        {
            _multipliers.Clear();
            _flats.Clear();

            for (int s = 0; s < _sources.Count; s++)
            {
                List<StatModifier> modifiers = _sources[s].Modifiers;

                for (int m = 0; m < modifiers.Count; m++)
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
            System.Text.StringBuilder builder = new System.Text.StringBuilder();

            for (int s = 0; s < _sources.Count; s++)
            {
                List<string> summaries = _sources[s].Summaries;
                for (int i = 0; i < summaries.Count; i++)
                {
                    if (builder.Length > 0) builder.Append("  |  ");
                    builder.Append(summaries[i]);
                }
            }

            return builder.Length > 0 ? builder.ToString() : "no modifiers";
        }
    }
}
