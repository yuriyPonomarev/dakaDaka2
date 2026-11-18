using UnityEditor;
using UnityEngine;
using Emberdelve.Data;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Authors the four universal kit skills (two actives, two passives) and the SkillPool that lists them. They reuse existing effect kinds
    /// (AreaCry, PassiveStat), so no runtime code is involved. Idempotent: existing assets are updated, never duplicated.
    /// </summary>
    public static class EmberdelveSkillPoolBuilder
    {
        private const string DataFolder = "Assets/Emberdelve/Data";
        private const string PoolPath = "Assets/Emberdelve/Resources/Emberdelve/SkillPool.asset";

        [MenuItem("Emberdelve/Data/Build Skill Pool")]
        public static void Build()
        {
            SkillData earthshaker = Author("Skill_Earthshaker", s =>
            {
                s.skillId = "skill_earthshaker";
                s.displayName = "Earthshaker";
                s.description = "Bring your weapon down on the floor. A tight shockwave hurts and heavily slows everything close to you.";
                s.iconColor = new Color(.75f, .6f, .4f);
                s.slot = SkillSlot.Active;
                s.effect = SkillEffectKind.AreaCry;
                s.manaCost = 30;
                s.cooldownSeconds = 12f;
                s.radius = 3.5f;
                s.maxTargets = 6;
                s.flatDamage = 24f;
                s.targetStatus = StatusKind.Slow;
                s.targetStatusDuration = 3f;
                s.targetStatusMagnitude = 0.35f;
                s.affectsSelf = false;
            });
            SkillData adrenaline = Author("Skill_AdrenalineRush", s =>
            {
                s.skillId = "skill_adrenaline_rush";
                s.displayName = "Adrenaline Rush";
                s.description = "Your pulse hammers. For a few seconds everything you swing hits much harder.";
                s.iconColor = new Color(.85f, .3f, .25f);
                s.slot = SkillSlot.Active;
                s.effect = SkillEffectKind.AreaCry;
                s.manaCost = 22;
                s.cooldownSeconds = 18f;
                s.radius = 0.5f;
                s.maxTargets = 1;
                s.flatDamage = 0f;
                s.affectsSelf = true;
                s.selfStatus = StatusKind.Empowered;
                s.selfStatusDuration = 9f;
                s.selfStatusMagnitude = 1.4f;
            });
            SkillData iron = Author("Skill_IronConstitution", s =>
            {
                s.skillId = "skill_iron_constitution";
                s.displayName = "Iron Constitution";
                s.description = "A body that has been hit before. 15% more health and a little natural armour.";
                s.iconColor = new Color(.55f, .55f, .5f);
                s.slot = SkillSlot.Passive;
                s.effect = SkillEffectKind.PassiveStat;
                s.modifiers = new[]
                {
                    new StatModifier(SkillStat.MaxHealth, SkillModifierMode.Multiplier, 1.15f),
                    new StatModifier(SkillStat.Armor, SkillModifierMode.Flat, 3f),
                };
            });
            SkillData reservoir = Author("Skill_ArcaneReservoir", s =>
            {
                s.skillId = "skill_arcane_reservoir";
                s.displayName = "Arcane Reservoir";
                s.description = "A deeper well to draw from. 25% more mana, refilling 20% faster.";
                s.iconColor = new Color(.4f, .55f, .9f);
                s.slot = SkillSlot.Passive;
                s.effect = SkillEffectKind.PassiveStat;
                s.modifiers = new[]
                {
                    new StatModifier(SkillStat.MaxMana, SkillModifierMode.Multiplier, 1.25f),
                    new StatModifier(SkillStat.ManaRegen, SkillModifierMode.Multiplier, 1.2f),
                };
            });

            SkillPool pool = AssetDatabase.LoadAssetAtPath<SkillPool>(PoolPath);
            if (pool == null)
            {
                pool = ScriptableObject.CreateInstance<SkillPool>();
                AssetDatabase.CreateAsset(pool, PoolPath);
            }
            pool.universalActives = new[] { earthshaker, adrenaline };
            pool.universalPassives = new[] { iron, reservoir };
            EditorUtility.SetDirty(pool);
            AssetDatabase.SaveAssets();
            Debug.Log("[SkillPool] built: 2 universal actives, 2 universal passives");
        }

        private static SkillData Author(string name, System.Action<SkillData> fill)
        {
            string path = DataFolder + "/" + name + ".asset";
            SkillData skill = AssetDatabase.LoadAssetAtPath<SkillData>(path);
            if (skill == null)
            {
                skill = ScriptableObject.CreateInstance<SkillData>();
                AssetDatabase.CreateAsset(skill, path);
            }
            fill(skill);
            EditorUtility.SetDirty(skill);
            return skill;
        }
    }
}
