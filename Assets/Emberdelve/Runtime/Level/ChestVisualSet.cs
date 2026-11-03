using System;
using UnityEngine;
using Emberdelve.Interaction;

namespace Emberdelve.Level
{
    /// <summary>
    /// V2: which model dresses which chest variant. A data asset at Resources/Emberdelve/ChestVisualSet (written by the
    /// editor builder, EmberdelvePropFactory) so the runtime ChestSpawner can dress the chests it builds without referencing
    /// the editor. Purely visual: the spawner's cube body and lid keep their colliders, layers, names and ChestInteractable
    /// wiring; only their MeshRenderers are switched off and a model is added next to them.
    /// </summary>
    public sealed class ChestVisualSet : ScriptableObject
    {
        public const string ResourcePath = "Emberdelve/ChestVisualSet";

        [Serializable]
        public sealed class Entry
        {
            public ChestVariant variant;
            [Tooltip("The model (an imported FBX). Instantiated per chest; its colliders, if any, are removed.")]
            public GameObject model;
            [Tooltip("Multiplied into the model's colours (white = untouched).")]
            public Color tint = Color.white;
            [Tooltip("Child whose name contains this text is the lid. Empty/no match: the chest has no lid animation.")]
            public string lidNameContains = "lid";
            [Tooltip("+1 when the model's front (where the lid opens towards) is local +Z, -1 when it is -Z.")]
            public float frontSign = 1f;
            [Tooltip("Degrees the lid swings open.")]
            public float openAngle = 105f;
        }

        public Entry[] entries = new Entry[0];

        public Entry Find(ChestVariant variant)
        {
            if (entries == null) return null;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i] != null && entries[i].variant == variant && entries[i].model != null) return entries[i];
            }

            return null;
        }

        public static ChestVisualSet Load()
        {
            return Resources.Load<ChestVisualSet>(ResourcePath);
        }

        /// <summary>
        /// Dresses one spawned chest. <paramref name="body"/> and <paramref name="lid"/> are the spawner's cubes (kept, with
        /// their colliders; only their renderers are disabled). <paramref name="size"/> is the body's blockout size,
        /// <paramref name="yawDegrees"/> turns the model (0 or 180: the collider box stays axis aligned). Returns the model
        /// root, or null when no model is configured (the blockout cubes then stay visible).
        /// </summary>
        public static GameObject Attach(ChestInteractable chest, GameObject body, GameObject lid, ChestVariant variant, Vector3 size, float yawDegrees)
        {
            if (chest == null || body == null) return null;

            ChestVisualSet set = Load();
            Entry entry = set != null ? set.Find(variant) : null;
            if (entry == null) return null;

            GameObject visual = Instantiate(entry.model);
            visual.name = body.name + "_Visual";
            visual.transform.SetParent(body.transform.parent, false);

            // Pure visuals: no collider on the model, ever.
            Collider[] colliders = visual.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++) DestroyAny(colliders[i]);

            // Fit: the model's long horizontal side matches the blockout's width, then it stands on the floor under the body.
            visual.transform.SetPositionAndRotation(body.transform.position - Vector3.up * (size.y * 0.5f), Quaternion.Euler(0f, yawDegrees, 0f));
            Renderer[] renderers = visual.GetComponentsInChildren<Renderer>(true);
            if (!TryGetBounds(renderers, out Bounds bounds)) { DestroyAny(visual); return null; }

            float width = Mathf.Max(bounds.size.x, bounds.size.z);
            float scale = Mathf.Clamp(size.x / Mathf.Max(0.01f, width), 0.25f, 4f);
            visual.transform.localScale = Vector3.one * scale;

            TryGetBounds(renderers, out bounds);
            float floorY = body.transform.position.y - size.y * 0.5f;
            Vector3 anchor = body.transform.position;
            visual.transform.position += new Vector3(anchor.x - bounds.center.x, floorY - bounds.min.y, anchor.z - bounds.center.z);

            if (entry.tint != Color.white)
            {
                MaterialPropertyBlock block = new MaterialPropertyBlock();
                for (int i = 0; i < renderers.Length; i++)
                {
                    Material shared = renderers[i].sharedMaterial;
                    if (shared == null || !shared.HasProperty("_BaseColor")) continue;
                    renderers[i].GetPropertyBlock(block);
                    block.SetColor("_BaseColor", shared.GetColor("_BaseColor") * entry.tint);
                    renderers[i].SetPropertyBlock(block);
                }
            }

            // The blockout cubes keep colliding and animating (the lid collider still swings); they are just not drawn.
            SetRendererEnabled(body, false);
            SetRendererEnabled(lid, false);

            Transform lidTransform = FindLid(visual.transform, entry.lidNameContains);
            ChestVisualDriver driver = visual.AddComponent<ChestVisualDriver>();
            driver.Configure(chest, lidTransform, entry.frontSign, entry.openAngle);
            return visual;
        }

        private static Transform FindLid(Transform root, string nameContains)
        {
            if (string.IsNullOrEmpty(nameContains)) return null;
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != root && all[i].name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0) return all[i];
            }

            return null;
        }

        private static bool TryGetBounds(Renderer[] renderers, out Bounds bounds)
        {
            bounds = new Bounds();
            bool any = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null) continue;
                if (!any) { bounds = renderers[i].bounds; any = true; }
                else bounds.Encapsulate(renderers[i].bounds);
            }

            return any;
        }

        private static void SetRendererEnabled(GameObject go, bool enabled)
        {
            if (go == null) return;
            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer != null) renderer.enabled = enabled;
        }

        private static void DestroyAny(UnityEngine.Object target)
        {
            if (target == null) return;
            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }
    }
}
