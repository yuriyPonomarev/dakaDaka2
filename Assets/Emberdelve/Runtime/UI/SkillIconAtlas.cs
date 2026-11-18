using UnityEngine;
namespace Emberdelve.UI
{
    /// <summary>Skill icons: one 4-column atlas, row-major from the top-left, in the order of <see cref="Ids"/>.</summary>
    public static class SkillIconAtlas
    {
        private const int Columns = 4;
        private const int Rows = 3;
        private static readonly string[] Ids =
        {
            "warrior_dash", "warrior_battle_cry", "warrior_ashen_might", "warrior_bulwark_training",
            "archer_multi_shot", "archer_piercing_shot", "archer_farshot", "archer_fleet_footing",
            "skill_earthshaker", "skill_adrenaline_rush", "skill_iron_constitution", "skill_arcane_reservoir"
        };
        private static readonly Sprite[] Icons = new Sprite[Ids.Length];
        private static Texture2D _atlas;
        public static Sprite Get(string id)
        {
            int index = System.Array.IndexOf(Ids, id); if (index < 0) return null;
            if (Icons[index] != null) return Icons[index];
            if (_atlas == null) _atlas = Resources.Load<Texture2D>("Emberdelve/SkillIcons/Abilities");
            if (_atlas == null) return null;
            float w = _atlas.width / (float)Columns, h = _atlas.height / (float)Rows;
            return Icons[index] = Sprite.Create(_atlas, new Rect(index % Columns * w, (Rows - 1 - index / Columns) * h, w, h), Vector2.one * .5f, 100);
        }
    }
}
