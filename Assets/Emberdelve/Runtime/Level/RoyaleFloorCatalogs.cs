using UnityEngine;

namespace Emberdelve.Level
{
    /// <summary>The floor number in the acknowledged map envelope selects its authored geometry.</summary>
    public static class RoyaleFloorCatalogs
    {
        public static string ResourceFor(int floor) => floor switch
        {
            1 => "Emberdelve/DungeonModuleCatalog",
            2 => "Emberdelve/CryptModuleCatalog",
            3 => "Emberdelve/InfernalModuleCatalog",
            _ => null
        };

        public static bool TryLoad(int floor, out DungeonModuleCatalog catalog, out string reason)
        {
            string resource = ResourceFor(floor);
            catalog = resource == null ? null : Resources.Load<DungeonModuleCatalog>(resource);
            bool valid = catalog != null && catalog.Modules.Length > 0;
            reason = valid ? null : "Missing or invalid Royale floor catalog: " + floor;
            return valid;
        }
    }
}
