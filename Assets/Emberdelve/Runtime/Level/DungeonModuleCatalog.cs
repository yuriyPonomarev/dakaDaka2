using System;
using UnityEngine;

namespace Emberdelve.Level
{
    [CreateAssetMenu(menuName = "Emberdelve/08 Dungeon Module Catalog", fileName = "DungeonModuleCatalog")]
    public sealed class DungeonModuleCatalog : ScriptableObject
    {
        [SerializeField] private DungeonModuleDefinition[] modules = Array.Empty<DungeonModuleDefinition>();
        public DungeonModuleDefinition[] Modules => modules ?? Array.Empty<DungeonModuleDefinition>();
    }
}
