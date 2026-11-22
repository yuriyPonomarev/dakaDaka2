using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Interaction;

namespace Emberdelve.Level
{
    /// <summary>
    /// Builds the containers described by the layout asset at run start, the same way the level geometry is
    /// built from data. It exists at runtime rather than only in the editor builder so a level built before
    /// this milestone still gets its chests, and so a future level streamed from a server can spawn the same
    /// specs without an editor step.
    /// </summary>
    public sealed class ChestSpawner : MonoBehaviour
    {
        private readonly List<ChestInteractable> _chests = new List<ChestInteractable>();
        private bool _presentationOnly;
        private DungeonLayoutData _builtLayout;
        private MonoBehaviour _floorLootHost;
        private GameConfig _floorConfig;
        private Transform _floorRoot;
        private int _floorNumber;

        public IReadOnlyList<ChestInteractable> Chests => _chests;
        public int Count => _chests.Count;
        public DungeonLayoutData BuiltLayout => _builtLayout;

        /// <summary>
        /// Builds only the authored chest views on a network client. These components have no loot host and
        /// are guarded against local opening; they exist so server chest IDs and interaction geometry resolve.
        /// </summary>
        public static ChestSpawner EnsureClientPresentation(DungeonLayoutData layout)
        {
            if (!NetRole.IsClientOnly || layout == null) return null;
            ChestSpawner[] existingSpawners = FindObjectsByType<ChestSpawner>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            ChestSpawner matching = null;
            for (int i = 0; i < existingSpawners.Length; i++)
            {
                ChestSpawner existing = existingSpawners[i];
                if (existing == null || !existing._presentationOnly || existing._floorRoot != null) continue;
                if (existing._builtLayout == layout && matching == null)
                {
                    matching = existing;
                    continue;
                }

                // Only this system's isolated presentation root is eligible for cleanup. Authored chest objects and
                // server spawners are never removed when a client changes layouts.
                existing.gameObject.SetActive(false);
                if (Application.isPlaying) Destroy(existing.gameObject);
                else DestroyImmediate(existing.gameObject);
            }

            if (matching != null) return matching;

            GameObject root = new GameObject("NetworkChestPresentation");
            ChestSpawner spawner = root.AddComponent<ChestSpawner>();
            spawner._presentationOnly = true;
            spawner._builtLayout = layout;
            spawner.BuildInternal(layout, null, null, true);
            return spawner;
        }

        /// <summary>
        /// Creates the spawner on a host (the RunDirector) and builds every chest the layout declares. Safe to
        /// call more than once: an existing spawner is reused and not re-populated.
        /// </summary>
        public static ChestSpawner Ensure(DungeonLayoutData layout, GameConfig config, MonoBehaviour lootHost)
        {
            if (layout == null || lootHost == null) return null;

            ChestSpawner existing = lootHost.GetComponent<ChestSpawner>();
            if (existing != null && existing.Count > 0) return existing;

            ChestSpawner spawner = existing != null ? existing : lootHost.gameObject.AddComponent<ChestSpawner>();
            spawner.Build(layout, config, lootHost);
            return spawner;
        }

        /// <summary>Builds authority-owned chests below a caller-owned floor root, retaining the raid's loot host.</summary>
        public static ChestSpawner CreateForFloor(DungeonLayoutData layout, GameConfig config,
            MonoBehaviour lootHost, Transform floorRoot, int floorNumber)
        {
            if (NetRole.IsClientOnly || lootHost == null) return null;
            return CreateFloorScoped(layout, config, lootHost, floorRoot, floorNumber, false);
        }

        /// <summary>Builds only this floor's client views without replacing any other floor's chests.</summary>
        public static ChestSpawner CreateClientPresentationForFloor(DungeonLayoutData layout,
            Transform floorRoot, int floorNumber)
        {
            if (!NetRole.IsClientOnly) return null;
            return CreateFloorScoped(layout, null, null, floorRoot, floorNumber, true);
        }

        private static ChestSpawner CreateFloorScoped(DungeonLayoutData layout, GameConfig config,
            MonoBehaviour lootHost, Transform floorRoot, int floorNumber, bool presentationOnly)
        {
            if (layout == null || floorRoot == null || floorNumber < 1 || floorNumber > 3) return null;
            foreach (ChestSpawner existing in floorRoot.GetComponentsInChildren<ChestSpawner>(true))
            {
                if (existing._floorRoot != floorRoot) continue;
                return existing._builtLayout == layout && existing._floorNumber == floorNumber &&
                       existing._presentationOnly == presentationOnly && existing._floorLootHost == lootHost
                       && existing._floorConfig == config ? existing : null;
            }

            // Validate all published names before creating any objects; the network and loot seed use this same ID.
            if (layout.chestSpawns != null)
                for (int i = 0; i < layout.chestSpawns.Count; i++)
                {
                    ChestSpawnSpec spec = layout.chestSpawns[i];
                    if (spec != null && spec.lootTable != null &&
                        !ChestInteractable.IsValidNetworkChestId(ChestName(spec, i, floorNumber))) return null;
                }

            GameObject root = new GameObject("Floor" + floorNumber + "_Chests");
            root.transform.SetParent(floorRoot, false);
            ChestSpawner spawner = root.AddComponent<ChestSpawner>();
            spawner._floorRoot = floorRoot;
            spawner._floorNumber = floorNumber;
            spawner._presentationOnly = presentationOnly;
            spawner._builtLayout = layout;
            spawner._floorLootHost = lootHost;
            spawner._floorConfig = config;
            spawner.BuildInternal(layout, config, lootHost, presentationOnly);
            return spawner;
        }

        private static string ChestName(ChestSpawnSpec spec, int index, int floorNumber)
        {
            string name = $"Chest_{spec.variant}_{spec.cell.x}_{spec.cell.y}_{index}";
            return floorNumber > 0 ? "Floor" + floorNumber + "_" + name : name;
        }

        public void Build(DungeonLayoutData layout, GameConfig config, MonoBehaviour lootHost)
        {
            _presentationOnly = false;
            _builtLayout = layout;
            BuildInternal(layout, config, lootHost, false);
        }

        private void BuildInternal(DungeonLayoutData layout, GameConfig config, MonoBehaviour lootHost, bool presentationOnly)
        {
            _chests.Clear();

            if (layout == null || layout.chestSpawns == null || layout.chestSpawns.Count == 0) return;

            bool[,] walkable = layout.BuildWalkableGrid();
            GameObject root = new GameObject("Chests");
            root.transform.SetParent(presentationOnly || _floorRoot != null ? transform : lootHost.transform, false);
            var visualCounts = new Dictionary<ChestVariant, int>();

            for (int i = 0; i < layout.chestSpawns.Count; i++)
            {
                ChestSpawnSpec spec = layout.chestSpawns[i];
                if (spec == null || spec.lootTable == null) continue;

                visualCounts.TryGetValue(spec.variant, out int visualIndex);
                _chests.Add(CreateChest(spec, root.transform, layout.CellCenterWorld(spec.cell), ChestName(spec, i, _floorNumber),
                    lootHost, presentationOnly, FacingYaw(layout, walkable, spec.cell), (visualIndex & 1) != 0));
                visualCounts[spec.variant] = visualIndex + 1;
            }

            Debug.Log($"[Emberdelve][ChestSpawner] spawned {_chests.Count} chest(s) from layout '{layout.levelName}'");
        }

        internal static ChestInteractable CreateChest(ChestSpawnSpec spec, Transform parent, Vector3 ground, string name,
            MonoBehaviour lootHost, bool presentationOnly, float yaw, bool useAlternate)
        {
            int interactLayer = LayerMask.NameToLayer(GameConfig.InteractLayerName);
            Vector3 size = SizeFor(spec.variant);

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = name;
            body.transform.SetParent(parent, false);
            body.transform.position = ground + Vector3.up * (size.y * .5f);
            body.transform.localScale = size;
            if (interactLayer >= 0) body.layer = interactLayer;
            if (spec.bodyMaterial != null) body.GetComponent<Renderer>().sharedMaterial = spec.bodyMaterial;

            GameObject lid = GameObject.CreatePrimitive(PrimitiveType.Cube);
            lid.name = name + "_Lid";
            lid.transform.SetParent(body.transform, false);
            lid.transform.localPosition = new Vector3(0f, size.y * .5f, 0f);
            lid.transform.localScale = new Vector3(size.x, size.y * .28f, size.z);
            if (interactLayer >= 0) lid.layer = interactLayer;
            if (spec.lidMaterial != null) lid.GetComponent<Renderer>().sharedMaterial = spec.lidMaterial;

            ChestInteractable chest = body.AddComponent<ChestInteractable>();
            chest.Configure(spec.variant, spec.lootTable, spec.seed, presentationOnly ? null : lootHost);
            if (presentationOnly) chest.SetPresentationOnly();
            chest.ConfigureBody(size, lid.transform);
            ChestVisualSet.Attach(chest, body, lid, spec.variant, size, yaw, useAlternate);
            return chest;
        }

        /// <summary>
        /// V2: which way a chest model faces. The box collider stays axis aligned, so the model only turns by 0 or 180 degrees:
        /// 180 when its back is against a wall on the +Z side, 0 otherwise.
        /// </summary>
        private static float FacingYaw(DungeonLayoutData layout, bool[,] walkable, Vector2Int cell)
        {
            bool north = IsBlocked(walkable, cell.x, cell.y + 1);
            bool south = IsBlocked(walkable, cell.x, cell.y - 1);
            return north && !south ? 180f : 0f;
        }

        private static bool IsBlocked(bool[,] walkable, int x, int y)
        {
            if (x < 0 || y < 0 || x >= walkable.GetLength(0) || y >= walkable.GetLength(1)) return true;
            return !walkable[x, y];
        }

        public static Vector3 SizeFor(ChestVariant variant)
        {
            switch (variant)
            {
                case ChestVariant.Ember: return new Vector3(1.25f, 0.85f, 0.85f);
                case ChestVariant.Sealed: return new Vector3(1.35f, 1.05f, 0.95f);
                default: return new Vector3(1.05f, 0.7f, 0.7f);
            }
        }
    }
}
