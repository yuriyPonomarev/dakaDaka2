using System.Collections.Generic;
using Emberdelve.AI;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using UnityEngine;

namespace Emberdelve.Level
{
    /// <summary>Authority content owned by one prepared floor. Existing authored monster visuals and item
    /// materials are reused; navigation, drops and pickups belong to this floor and the same raid host.</summary>
    public sealed class FloorContentSpawner : MonoBehaviour
    {
        private readonly List<EnemyAgent> _enemies = new List<EnemyAgent>();
        private readonly List<LootPickup> _loot = new List<LootPickup>();
        public IReadOnlyList<EnemyAgent> Enemies => _enemies;
        public IReadOnlyList<LootPickup> Loot => _loot;

        public static FloorContentSpawner CreateForFloor(DungeonLayoutData layout, NavGridData nav,
            GameConfig config, MonoBehaviour host, Transform floorRoot, int floor, out string reason)
        {
            reason = "Invalid destination content request";
            if (NetRole.IsClientOnly || layout == null || nav == null || !(host is IEnemyDropHost)
                || floorRoot == null || floorRoot.gameObject.activeInHierarchy || floor < 1 || floor > 3
                || nav.origin != layout.origin || nav.width != layout.gridWidth || nav.height != layout.gridHeight
                || floorRoot.GetComponentInChildren<FloorContentSpawner>(true) != null) return null;
            var visuals = new List<GameObject>();
            if (layout.enemySpawns != null)
                for (int i = 0; i < layout.enemySpawns.Count; i++)
                {
                    var spec = layout.enemySpawns[i];
                    if (spec == null || spec.enemy == null) { reason = "Missing floor monster definition"; return null; }
                    string variant = spec.enemy.IsCinderWarden ? EnemyData.CinderWardenVisual :
                        spec.enemy.isElite ? "Warrior" : (i & 1) == 0 ? "Minion" : "Rogue";
                    var prefab = Resources.Load<GameObject>("Emberdelve/NetEnemyVisuals/EnemyVisual_" + variant);
                    if (prefab == null) { reason = "Missing authored monster visual " + variant; return null; }
                    visuals.Add(prefab);
                }
            if (layout.lootSpawns != null)
                foreach (var spec in layout.lootSpawns)
                    if (spec == null || spec.item == null || spec.amount < 1)
                    { reason = "Invalid floor loot definition"; return null; }

            var root = new GameObject("Floor" + floor + "_Content");
            root.transform.SetParent(floorRoot, false);
            var content = root.AddComponent<FloorContentSpawner>();
            // Keep validating the authored content above even during inspection. Only population changes;
            // loot, navigation and the inactive staging/activation barrier remain identical.
            bool populateAi = GameConfig.ShouldPopulateRaidWithAi(config);
            for (int i = 0; populateAi && i < visuals.Count; i++)
            {
                var spec = layout.enemySpawns[i];
                var agent = EnemyAgent.CreateForFloor(spec.enemy, nav, config, host, root.transform,
                    layout.CellCenterWorld(spec.cell), visuals[i], "Floor" + floor + "_Enemy_" + i);
                if (agent == null)
                {
                    root.SetActive(false);
                    if (Application.isPlaying) Destroy(root); else DestroyImmediate(root);
                    reason = "Could not create floor monster"; return null;
                }
                content._enemies.Add(agent);
            }
            int lootLayer = LayerMask.NameToLayer(GameConfig.LootLayerName);
            if (layout.lootSpawns != null)
                for (int i = 0; i < layout.lootSpawns.Count; i++)
                {
                    var spec = layout.lootSpawns[i];
                    var item = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    item.name = "Floor" + floor + "_Loot_" + i;
                    item.transform.SetParent(root.transform, false);
                    item.transform.position = layout.CellCenterWorld(spec.cell) + Vector3.up * .45f;
                    item.transform.localScale = spec.item.worldSize;
                    if (lootLayer >= 0) item.layer = lootLayer;
                    if (spec.item.worldMaterial != null) item.GetComponent<Renderer>().sharedMaterial = spec.item.worldMaterial;
                    var pickup = item.AddComponent<LootPickup>();
                    pickup.Configure(spec.item, spec.amount, host);
                    pickup.PlaceAt(item.transform.position);
                    content._loot.Add(pickup);
                }
            reason = null;
            return content;
        }
    }
}
