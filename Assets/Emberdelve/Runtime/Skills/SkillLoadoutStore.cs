using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Data;

namespace Emberdelve.Skills
{
    /// <summary>
    /// The player's chosen skill kit per class: two active and two passive slots, remembered between sessions and applied to the class
    /// that goes into a raid. The pool a class may choose from is its own kit plus the universal <see cref="SkillPool"/>.
    /// A saved choice that no longer resolves (a skill was removed) falls back to the class default, slot by slot.
    /// </summary>
    public static class SkillLoadoutStore
    {
        public const int ActiveSlots = 2;
        public const int PassiveSlots = 2;
        private const string KeyPrefix = "emberdelve.kit.";

        /// <summary>Every skill of this slot type the class can pick: its own kit first, then the universal pool, without repeats.</summary>
        public static List<SkillData> Pool(CharacterClassData cls, SkillSlot slot)
        {
            var result = new List<SkillData>();
            void Add(SkillData skill)
            {
                if (skill != null && skill.slot == slot && !result.Contains(skill)) result.Add(skill);
            }
            SkillData[] own = cls != null ? (slot == SkillSlot.Active ? cls.activeSkills : cls.passiveSkills) : null;
            if (own != null) for (int i = 0; i < own.Length; i++) Add(own[i]);
            SkillPool pool = SkillPool.Load();
            SkillData[] extra = pool != null ? (slot == SkillSlot.Active ? pool.universalActives : pool.universalPassives) : null;
            if (extra != null) for (int i = 0; i < extra.Length; i++) Add(extra[i]);
            return result;
        }

        /// <summary>The chosen kit (saved, or the class default). Always returns exactly two actives and two passives when the class declares them.</summary>
        public static void Get(CharacterClassData cls, out SkillData[] actives, out SkillData[] passives)
        {
            actives = Resolve(cls, SkillSlot.Active, ActiveSlots);
            passives = Resolve(cls, SkillSlot.Passive, PassiveSlots);
        }

        public static void Save(CharacterClassData cls, SkillData[] actives, SkillData[] passives)
        {
            if (cls == null) return;
            try
            {
                PlayerPrefs.SetString(Key(cls, SkillSlot.Active), Join(actives));
                PlayerPrefs.SetString(Key(cls, SkillSlot.Passive), Join(passives));
                PlayerPrefs.Save();
            }
            catch (System.Exception e) { Debug.LogWarning("[Emberdelve][Kit] could not save the skill kit: " + e.Message); }
        }

        public static void Reset(CharacterClassData cls)
        {
            if (cls == null) return;
            try
            {
                PlayerPrefs.DeleteKey(Key(cls, SkillSlot.Active));
                PlayerPrefs.DeleteKey(Key(cls, SkillSlot.Passive));
            }
            catch (System.Exception) { }
        }

        /// <summary>
        /// The class to take into a raid: the asset itself when the chosen kit is the shipped one, otherwise a runtime copy of it whose kit
        /// is the chosen skills. The asset on disk is never touched. Everything downstream reads the kit from the class it is given.
        /// </summary>
        public static CharacterClassData Apply(CharacterClassData cls)
        {
            if (cls == null) return null;
            Get(cls, out SkillData[] actives, out SkillData[] passives);
            if (Same(actives, cls.activeSkills) && Same(passives, cls.passiveSkills)) return cls;
            CharacterClassData copy = Object.Instantiate(cls);
            copy.name = cls.name;
            copy.activeSkills = actives;
            copy.passiveSkills = passives;
            return copy;
        }

        private static SkillData[] Resolve(CharacterClassData cls, SkillSlot slot, int count)
        {
            SkillData[] defaults = cls != null ? (slot == SkillSlot.Active ? cls.activeSkills : cls.passiveSkills) : null;
            var result = new SkillData[count];
            string saved = null;
            try { if (cls != null) saved = PlayerPrefs.GetString(Key(cls, slot), string.Empty); } catch (System.Exception) { }
            string[] ids = string.IsNullOrEmpty(saved) ? new string[0] : saved.Split(',');
            List<SkillData> pool = Pool(cls, slot);
            for (int i = 0; i < count; i++)
            {
                SkillData pick = null;
                if (i < ids.Length && !string.IsNullOrEmpty(ids[i])) pick = pool.Find(s => s.skillId == ids[i]);
                if (pick == null && defaults != null && i < defaults.Length) pick = defaults[i];
                result[i] = pick;
            }
            // A kit may not list the same skill twice; the later duplicate returns to its class default.
            for (int i = 0; i < count; i++)
                for (int j = i + 1; j < count; j++)
                    if (result[i] != null && result[i] == result[j])
                        result[j] = defaults != null && j < defaults.Length && defaults[j] != result[i] ? defaults[j] : null;
            return result;
        }

        private static bool Same(SkillData[] a, SkillData[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static string Join(SkillData[] skills)
        {
            var ids = new string[skills != null ? skills.Length : 0];
            for (int i = 0; i < ids.Length; i++) ids[i] = skills[i] != null ? skills[i].skillId : string.Empty;
            return string.Join(",", ids);
        }

        private static string Key(CharacterClassData cls, SkillSlot slot) => KeyPrefix + cls.classId + (slot == SkillSlot.Active ? ".active" : ".passive");
    }
}
