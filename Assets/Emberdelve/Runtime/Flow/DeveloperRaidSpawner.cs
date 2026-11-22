#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Emberdelve.AI;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Level;
using UnityEngine;

namespace Emberdelve.Flow
{
    public sealed class DeveloperRaidSpawner
    {
        private readonly RunDirector _director;
        private int _serial;

        public DeveloperRaidSpawner(RunDirector director) => _director = director;

        public static bool CanSpawn(RunDirector director) => NetRole.IsOffline && director != null
            && director.State == RunState.Running && director.LocalSession is { IsRunning: true }
            && director.Player != null && !director.Player.IsDead && director.Config != null && director.Layout != null;

        public string SpawnEnemy(EnemyData enemy, string appearance)
        {
            if (!TryPlacement(new Vector3(1f, 2.45f, 1f), out var map, out var point, out var reason)) return reason;
            var visual = Resources.Load<GameObject>("Emberdelve/NetEnemyVisuals/EnemyVisual_" + appearance);
            var agent = EnemyAgent.CreateForFloor(enemy, map.RuntimeNavGrid, _director.Config, _director, map.transform,
                point, visual, "Dev_Enemy_" + ++_serial);
            if (agent == null) return "Enemy could not be spawned.";
            return "Spawned " + enemy.displayName + ".";
        }

        public string SpawnChest(ChestSpawnSpec source, bool alternate)
        {
            Vector3 size = ChestSpawner.SizeFor(source.variant);
            size.y *= 1.3f;
            if (!TryPlacement(size, out var map, out var point, out var reason)) return reason;
            var spec = new ChestSpawnSpec
            {
                variant = source.variant, seed = unchecked(source.seed + ++_serial),
                lootTable = source.lootTable, bodyMaterial = source.bodyMaterial, lidMaterial = source.lidMaterial
            };
            float yaw = _director.Player.transform.position.z < point.z ? 180f : 0f;
            ChestSpawner.CreateChest(spec, map.transform, point, "Dev_Chest_" + spec.variant + "_" + _serial,
                _director, false, yaw, alternate);
            return "Spawned " + spec.variant + " chest" + (alternate ? " (alternate)." : ".");
        }

        public string SpawnLoot(ItemData item)
        {
            Vector3 size = Vector3.Max(item.worldSize, Vector3.one * .2f) * 1.9f;
            if (!TryPlacement(size, out _, out var point, out var reason)) return reason;
            _director.SpawnLoot(item, 1, point + Vector3.up * (size.y * .5f + .04f));
            return "Spawned " + item.displayName + ".";
        }

        private bool TryPlacement(Vector3 size, out DungeonModuleSceneAssembler map, out Vector3 ground, out string reason)
        {
            map = null;
            ground = default;
            reason = "Enter an offline raid to spawn objects.";
            if (!CanSpawn(_director)) return false;

            var player = _director.Player;
            Vector3 position = player.transform.position;
            foreach (var candidate in Object.FindObjectsByType<DungeonModuleSceneAssembler>(FindObjectsSortMode.None))
            {
                var nav = candidate.RuntimeNavGrid;
                if (nav == null) continue;
                var cell = nav.CellFromWorld(position);
                if (!nav.IsInside(cell.x, cell.y)) continue;
                map = candidate;
                break;
            }
            reason = "The current room is still loading.";
            if (map == null || DungeonModuleSceneAssembler.IsPositionOnRetiredFloor(position)) return false;

            Vector3 forward = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up).normalized;
            int levelMask = _director.Config.levelMask;
            Physics.SyncTransforms();
            reason = "No clear floor ahead. Move into an open area.";
            for (float distance = 2.8f; distance >= 1.6f; distance -= .35f)
            {
                Vector3 ahead = position + forward * distance;
                if (Physics.SphereCast(position + Vector3.up * .8f, .16f, forward, out _, distance,
                        levelMask, QueryTriggerInteraction.Ignore)) continue;
                if (!Physics.Raycast(ahead + Vector3.up * .8f, Vector3.down, out var floorHit, 1.8f,
                        levelMask, QueryTriggerInteraction.Ignore) || floorHit.normal.y < .75f) continue;
                var cell = map.RuntimeNavGrid.CellFromWorld(floorHit.point);
                if (!map.RuntimeNavGrid.IsWalkable(cell.x, cell.y)) continue;
                if (Physics.CheckBox(floorHit.point + Vector3.up * (size.y * .5f + .07f), size * .5f,
                        Quaternion.identity, Physics.AllLayers, QueryTriggerInteraction.Ignore)) continue;
                ground = floorHit.point;
                return true;
            }
            return false;
        }
    }
}
#endif
