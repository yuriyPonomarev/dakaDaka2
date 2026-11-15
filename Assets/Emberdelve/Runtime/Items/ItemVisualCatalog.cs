using System;
using UnityEngine;

namespace Emberdelve.Items
{
    /// <summary>Authored cosmetic representations. Item identity and equipment rules remain in the item catalog.</summary>
    [CreateAssetMenu(menuName = "Emberdelve/Item visual catalog")]
    public sealed class ItemVisualCatalog : ScriptableObject
    {
        public const string ResourcePath = "Emberdelve/ItemVisualCatalog";
        [Serializable] public sealed class Entry
        {
            public string itemId;
            public GameObject worldPrefab;
            public Sprite icon;
            public WearPart[] worn = Array.Empty<WearPart>();
            public WearSkin skin;
        }
        [Serializable] public sealed class WearPart
        {
            public HumanBodyBones bone;
            public Mesh mesh;
            public Material[] materials = Array.Empty<Material>();
            public Vector3 localPosition;
            public Vector3 localEuler;
            public Vector3 localScale = Vector3.one;
        }
        /// <summary>A garment in the source body renderer's local space, preserving its skin weights and bind poses.</summary>
        [Serializable] public sealed class WearSkin
        {
            public string rendererName;
            public Mesh mesh;
            public string[] boneNames = Array.Empty<string>();
            public Material[] materials = Array.Empty<Material>();
            public Vector3 localPosition;
            public Vector3 localEuler;
            public Vector3 localScale = Vector3.one;
        }
        public Entry[] entries = Array.Empty<Entry>();
        private static ItemVisualCatalog _loaded;
        public static ItemVisualCatalog Load()
        {
            if (_loaded == null) _loaded = Resources.Load<ItemVisualCatalog>(ResourcePath);
            return _loaded;
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetCache() { _loaded = null; }
        public bool TryGet(string itemId, out Entry entry)
        {
            if (!string.IsNullOrEmpty(itemId) && entries != null)
                foreach (var candidate in entries)
                    if (candidate != null && string.Equals(candidate.itemId, itemId, StringComparison.Ordinal))
                    { entry = candidate; return true; }
            entry = null; return false;
        }
    }
}
