using System;
using UnityEngine;

namespace Emberdelve.Level
{
    [Flags]
    public enum DungeonModuleSockets : byte
    {
        None = 0,
        North = 1 << 0,
        East = 1 << 1,
        South = 1 << 2,
        West = 1 << 3,
        All = North | East | South | West
    }

    public enum DungeonModuleKind : byte
    {
        Passage,
        Entry,
        Hall,
        Ossuary,
        Shrine,
        Vault,
        Extraction,
        Bridge
    }

    /// <summary>
    /// Authored metadata placed on a room-module prefab. Socket directions are in the prefab's local frame;
    /// the planner rotates the module to make its available doorways agree with the generated graph.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DungeonModuleDefinition : MonoBehaviour
    {
        [SerializeField] private string moduleId = "crypt-room";
        [SerializeField] private DungeonModuleKind kind = DungeonModuleKind.Hall;
        [SerializeField] private DungeonModuleSockets availableSockets = DungeonModuleSockets.All;
        [SerializeField, Min(1)] private int widthCells = DungeonModulePlan.ModuleWidthCells;
        [SerializeField, Min(1)] private int heightCells = DungeonModulePlan.ModuleHeightCells;
        [SerializeField] private Transform northSocket;
        [SerializeField] private Transform eastSocket;
        [SerializeField] private Transform southSocket;
        [SerializeField] private Transform westSocket;
        [SerializeField] private Transform playerSpawnAnchor;

        public string ModuleId => moduleId;
        public DungeonModuleKind Kind => kind;
        public DungeonModuleSockets AvailableSockets => availableSockets;
        public int WidthCells => Mathf.Max(1, widthCells);
        public int HeightCells => Mathf.Max(1, heightCells);
        public Transform PlayerSpawnAnchor => playerSpawnAnchor != null ? playerSpawnAnchor : FindChild("PlayerSpawn");

        public Transform Socket(DungeonModuleSockets direction)
        {
            switch (direction)
            {
                case DungeonModuleSockets.North: return northSocket != null ? northSocket : FindChild("Socket_North");
                case DungeonModuleSockets.East: return eastSocket != null ? eastSocket : FindChild("Socket_East");
                case DungeonModuleSockets.South: return southSocket != null ? southSocket : FindChild("Socket_South");
                case DungeonModuleSockets.West: return westSocket != null ? westSocket : FindChild("Socket_West");
                default: return null;
            }
        }

        public bool HasValidSockets(out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(moduleId)) { reason = "module id is empty"; return false; }
            if (widthCells < 1 || heightCells < 1) { reason = "module footprint must be positive"; return false; }

            if (Has(DungeonModuleSockets.North) && Socket(DungeonModuleSockets.North) == null) { reason = "north socket transform is missing"; return false; }
            if (Has(DungeonModuleSockets.East) && Socket(DungeonModuleSockets.East) == null) { reason = "east socket transform is missing"; return false; }
            if (Has(DungeonModuleSockets.South) && Socket(DungeonModuleSockets.South) == null) { reason = "south socket transform is missing"; return false; }
            if (Has(DungeonModuleSockets.West) && Socket(DungeonModuleSockets.West) == null) { reason = "west socket transform is missing"; return false; }
            if (PlayerSpawnAnchor == null) { reason = "PlayerSpawn anchor transform is missing"; return false; }
            if (Closure(DungeonModuleSockets.North) == null || Closure(DungeonModuleSockets.East) == null ||
                Closure(DungeonModuleSockets.South) == null || Closure(DungeonModuleSockets.West) == null)
            { reason = "all four Closure_* panels are required to close unused sockets"; return false; }
            return true;
        }

        public Transform Closure(DungeonModuleSockets direction)
        {
            string suffix = direction == DungeonModuleSockets.North ? "North" : direction == DungeonModuleSockets.East ? "East" :
                direction == DungeonModuleSockets.South ? "South" : direction == DungeonModuleSockets.West ? "West" : string.Empty;
            return suffix.Length == 0 ? null : FindChild("Closure_" + suffix);
        }

        private Transform FindChild(string childName)
        {
            Transform[] children = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++) if (children[i].name == childName) return children[i];
            return null;
        }

        private bool Has(DungeonModuleSockets direction) => (availableSockets & direction) != 0;
    }

}
