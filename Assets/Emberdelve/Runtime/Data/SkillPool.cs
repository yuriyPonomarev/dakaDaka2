using UnityEngine;

namespace Emberdelve.Data
{
    /// <summary>
    /// Skills any class may slot in the lobby kit screen, on top of the skills its own kit ships with. A skill that reuses an existing
    /// effect kind is just a SkillData asset listed here: nothing in the runtime knows about individual skills.
    /// </summary>
    [CreateAssetMenu(menuName = "Emberdelve/08 Skill Pool", fileName = "SkillPool")]
    public sealed class SkillPool : ScriptableObject
    {
        public const string ResourcePath = "Emberdelve/SkillPool";

        public SkillData[] universalActives = new SkillData[0];
        public SkillData[] universalPassives = new SkillData[0];

        private static SkillPool _cached;

        public static SkillPool Load()
        {
            if (_cached == null) _cached = Resources.Load<SkillPool>(ResourcePath);
            return _cached;
        }
    }
}
