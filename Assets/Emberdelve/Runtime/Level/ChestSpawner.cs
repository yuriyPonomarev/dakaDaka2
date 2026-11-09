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

        public IReadOnlyList<ChestInteractable> Chests => _chests;
        public int Count => _chests.Count;

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
                if (existing == null || !existing._presentationOnly) continue;
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

            int interactLayer = LayerMask.NameToLayer(GameConfig.InteractLayerName);
            bool[,] walkable = layout.BuildWalkableGrid();
            GameObject root = new GameObject("Chests");
            root.transform.SetParent(presentationOnly ? transform : lootHost.transform, false);

            for (int i = 0; i < layout.chestSpawns.Count; i++)
            {
                ChestSpawnSpec spec = layout.chestSpawns[i];
                if (spec == null || spec.lootTable == null) continue;

                Vector3 center = layout.CellCenterWorld(spec.cell);
                Vector3 size = SizeFor(spec.variant);

                GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
                body.name = $"Chest_{spec.variant}_{spec.cell.x}_{spec.cell.y}_{i}";
                body.transform.SetParent(root.transform, false);
                body.transform.position = center + Vector3.up * (size.y * 0.5f);
                body.transform.localScale = size;
                if (interactLayer >= 0) body.layer = interactLayer;

                Renderer bodyRenderer = body.GetComponent<Renderer>();
                if (bodyRenderer != null && spec.bodyMaterial != null) bodyRenderer.sharedMaterial = spec.bodyMaterial;

                // The lid is a separate block so opening a chest is visible, not just a toast.
                GameObject lid = GameObject.CreatePrimitive(PrimitiveType.Cube);
                lid.name = body.name + "_Lid";
                lid.transform.SetParent(body.transform, false);
                lid.transform.localPosition = new Vector3(0f, size.y * 0.5f, 0f);
                lid.transform.localScale = new Vector3(size.x, size.y * 0.28f, size.z);
                if (interactLayer >= 0) lid.layer = interactLayer;

                Collider lidCollider = lid.GetComponent<Collider>();
                if (lidCollider != null) lidCollider.isTrigger = false;

                Renderer lidRenderer = lid.GetComponent<Renderer>();
                if (lidRenderer != null && spec.lidMaterial != null) lidRenderer.sharedMaterial = spec.lidMaterial;

                ChestInteractable chest = body.AddComponent<ChestInteractable>();
                chest.Configure(spec.variant, spec.lootTable, spec.seed, presentationOnly ? null : lootHost);
                if (presentationOnly) chest.SetPresentationOnly();
                chest.ConfigureBody(size, lid.transform);

                // V2: dress the blockout cubes with a model (visual only; colliders, layers and wiring above are untouched).
                ChestVisualSet.Attach(chest, body, lid, spec.variant, size, FacingYaw(layout, walkable, spec.cell));

                _chests.Add(chest);
            }

            Debug.Log($"[Emberdelve][ChestSpawner] spawned {_chests.Count} chest(s) from layout '{layout.levelName}'");
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
