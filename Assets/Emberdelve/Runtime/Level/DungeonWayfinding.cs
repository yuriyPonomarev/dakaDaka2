using UnityEngine;
using Emberdelve.Level;

namespace Emberdelve.Level
{
    /// <summary>Local exploration information only: no enemy, loot or unexplored-room markers.</summary>
    public static class DungeonWayfinding
    {
        private static readonly string[] Headings = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        public static string Heading(float yaw) => Headings[Mathf.FloorToInt((Mathf.Repeat(yaw, 360f) + 22.5f) / 45f) % 8];
        public static string RoomAt(DungeonLayoutData layout, Vector3 position)
        {
            if (layout == null) return string.Empty;
            Vector2Int cell = layout.CellFromWorld(position);
            foreach (var room in layout.rooms)
            {
                if (cell.x < room.minCell.x || cell.x > room.maxCell.x || cell.y < room.minCell.y || cell.y > room.maxCell.y) continue;
                return DisplayName(room.roomName);
            }
            return "Passage";
        }
        public static string DisplayName(string id)
        {
            switch (id)
            {
                case "crypt_entry_01": return "Watch Gate";
                case "crypt_hall_01": return "Abandoned Stores";
                case "crypt_hall_02": return "Broken Cloister";
                case "crypt_hall_03": return "Fallen Effigy";
                case "crypt_ossuary_01": return "Burial Niches";
                case "crypt_ossuary_02": return "Ossuary";
                case "crypt_shrine_01": return "Ruined Chapel";
                case "crypt_vault_01": return "Sealed Treasury";
                case "crypt_extraction_01": return "Wayfarer's Refuge";
                case "crypt_skybridge_01": return "Broken Crossing";
                default: return string.IsNullOrEmpty(id) ? "Ruins" : id.Replace('_', ' ');
            }
        }
    }
}
