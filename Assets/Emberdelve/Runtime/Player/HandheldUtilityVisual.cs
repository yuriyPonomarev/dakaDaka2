using Emberdelve.Data;
using UnityEngine;
using UnityEngine.Rendering;

namespace Emberdelve.Player
{
    // Shared visible model for the exact utility selected from a live bag stack.
    public static class HandheldUtilityVisual
    {
        private static Material _fallback;
        public static ItemData Find(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return null;
            foreach (var item in Resources.FindObjectsOfTypeAll<ItemData>())
                if (item.itemId == itemId) return item;
            return null;
        }
        public static GameObject Create(ItemData item, Transform parent)
        {
            if (item == null) return null;
            if (item.itemId == "pitch_torch")
            {
                var set = WeaponViewModelSet.Load();
                var entry = set != null ? set.Find(HandheldTorchVisual.WeaponId) : null;
                if (entry != null && entry.prefab != null) return Object.Instantiate(entry.prefab, parent, false);
            }
            var root = new GameObject("Held utility " + item.itemId);
            root.transform.SetParent(parent, false);
            if (item.healAmount > 0)
            {
                Part(root.transform, "Bottle", PrimitiveType.Sphere, new Vector3(0,.055f,0), new Vector3(.105f,.14f,.105f), item);
                Part(root.transform, "Bottle neck", PrimitiveType.Cylinder, new Vector3(0,.14f,0), new Vector3(.043f,.035f,.043f), item);
                Part(root.transform, "Stopper", PrimitiveType.Cylinder, new Vector3(0,.18f,0), new Vector3(.047f,.012f,.047f), item);
            }
            else
            {
                for (int i=0;i<3;i++)
                    Part(root.transform, "Ammo shaft", PrimitiveType.Cylinder, new Vector3((i-1)*.025f,.13f,0), new Vector3(.012f,.20f,.012f), item);
            }
            return root;
        }
        private static void Part(Transform parent, string name, PrimitiveType shape, Vector3 position, Vector3 scale, ItemData item)
        {
            var part = GameObject.CreatePrimitive(shape); part.name = name;
            part.transform.SetParent(parent,false); part.transform.localPosition=position; part.transform.localScale=scale;
            var collider=part.GetComponent<Collider>(); collider.enabled=false;
            if (Application.isPlaying) Object.Destroy(collider); else Object.DestroyImmediate(collider);
            var renderer=part.GetComponent<Renderer>();
            if (item.worldMaterial != null) renderer.sharedMaterial=item.worldMaterial;
            else
            {
                if (_fallback == null) _fallback=new Material(Shader.Find("Universal Render Pipeline/Lit")) {name="Utility fallback"};
                renderer.sharedMaterial=_fallback;
                var properties=new MaterialPropertyBlock(); properties.SetColor("_BaseColor",item.iconColor); renderer.SetPropertyBlock(properties);
            }
            renderer.shadowCastingMode=ShadowCastingMode.Off; renderer.receiveShadows=true;
        }
    }
}
