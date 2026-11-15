using System;
using System.Collections.Generic;
using Emberdelve.Data;
using Emberdelve.Items;
using UnityEngine;

namespace Emberdelve.Player
{
    /// <summary>Read-only presentation of worn item definitions on an existing humanoid model.</summary>
    [DisallowMultipleComponent]
    public sealed class EquippedArmorView : MonoBehaviour
    {
        private sealed class SlotView
        {
            public string itemId;
            public readonly List<GameObject> parts = new List<GameObject>();
            public readonly Dictionary<GameObject, bool> hidden = new Dictionary<GameObject, bool>();
        }
        private Equipment _equipment;
        private bool _handsOnly;
        private int _handsLayer;
        private bool _hasVisualIds;
        private readonly string[] _visualIds = new string[6];
        private Animator _animator;
        private ItemVisualCatalog _catalog;
        private Transform[] _originalTransforms;
        private readonly Dictionary<EquipmentSlot, SlotView> _slots = new Dictionary<EquipmentSlot, SlotView>();
        private static readonly EquipmentSlot[] ArmorSlots =
        { EquipmentSlot.Head, EquipmentSlot.Chest, EquipmentSlot.Legs, EquipmentSlot.Hands, EquipmentSlot.Feet, EquipmentSlot.Back };

        public void Bind(Equipment equipment, bool handsOnly = false)
        {
            if (_equipment == equipment && _handsOnly == handsOnly && _originalTransforms != null) return;
            Clear();
            _equipment = equipment;
            _handsOnly = handsOnly;
            var body = GetComponentInChildren<SkinnedMeshRenderer>(true);
            _handsLayer = body != null ? body.gameObject.layer : gameObject.layer;
            _animator = GetComponentInChildren<Animator>(true);
            _originalTransforms = GetComponentsInChildren<Transform>(true);
            _catalog = ItemVisualCatalog.Load();
        }

        /// <summary>Cosmetic IDs only. Empty IDs explicitly mean no replacement, never a fallback to another actor's gear.</summary>
        public bool BindVisualIds(string head, string chest, string legs, string hands, string feet, string back)
        {
            if (_originalTransforms == null) Bind(null);
            _hasVisualIds = true;
            _visualIds[0] = head; _visualIds[1] = chest; _visualIds[2] = legs;
            _visualIds[3] = hands; _visualIds[4] = feet; _visualIds[5] = back;
            return Refresh();
        }

        /// <summary>Returns true only when the renderer set changes; callers may then refresh their visibility cache.</summary>
        public bool Refresh()
        {
            bool changed = false;
            if (_catalog == null) _catalog = ItemVisualCatalog.Load();
            for (int index = 0; index < ArmorSlots.Length; index++)
            {
                var slot = ArmorSlots[index];
                if (_handsOnly && slot != EquipmentSlot.Hands) continue;
                string itemId = _hasVisualIds ? _visualIds[index] : (_equipment != null ? _equipment.Equipped(slot)?.itemId : null);
                if (_slots.TryGetValue(slot, out var previous) && previous.itemId == itemId) continue;
                if (previous != null) { Remove(previous); _slots.Remove(slot); changed = true; }
                if (string.IsNullOrEmpty(itemId) || _catalog == null || _animator == null || !_animator.isHuman ||
                    !_catalog.TryGet(itemId, out var entry)) continue;
                var view = new SlotView { itemId = itemId };
                AddSkin(entry.skin, view);
                foreach (var part in entry.worn ?? Array.Empty<ItemVisualCatalog.WearPart>())
                {
                    if (part == null || part.mesh == null || (int)part.bone < (int)HumanBodyBones.Hips || (int)part.bone >= (int)HumanBodyBones.LastBone) continue;
                    var bone = _animator.GetBoneTransform(part.bone);
                    if (bone == null) continue;
                    var obj = new GameObject("Worn_" + itemId + "_" + view.parts.Count, typeof(MeshFilter), typeof(MeshRenderer));
                    obj.layer = _handsOnly ? _handsLayer : bone.gameObject.layer;
                    obj.transform.SetParent(bone, false);
                    obj.transform.localPosition = part.localPosition;
                    obj.transform.localRotation = Quaternion.Euler(part.localEuler);
                    obj.transform.localScale = part.localScale;
                    obj.GetComponent<MeshFilter>().sharedMesh = part.mesh;
                    var renderer = obj.GetComponent<MeshRenderer>();
                    renderer.sharedMaterials = part.materials;
                    if (_handsOnly) renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    view.parts.Add(obj);
                }
                // Missing/incomplete assets leave the original class silhouette intact.
                if (view.parts.Count == 0) continue;
                foreach (var original in _originalTransforms)
                {
                    if (original == null || !Replaces(slot, original.name) || original.GetComponent<Renderer>() == null) continue;
                    view.hidden[original.gameObject] = original.gameObject.activeSelf;
                    original.gameObject.SetActive(false);
                }
                _slots[slot] = view; changed = true;
            }
            return changed;
        }

        private void AddSkin(ItemVisualCatalog.WearSkin definition, SlotView view)
        {
            if (definition == null || definition.mesh == null || definition.boneNames == null || definition.boneNames.Length == 0) return;
            SkinnedMeshRenderer source = null;
            Transform[] bones = null;
            // The body renderer's space already includes the world-avatar or first-person model offset/scale.
            // Parenting to it, rather than the actor root, keeps the same authored bind poses valid in both views.
            foreach (var original in _originalTransforms)
            {
                if (original == null) continue;
                var candidate = original.GetComponent<SkinnedMeshRenderer>();
                if (candidate == null || candidate.sharedMesh == null || !TrySkinBones(candidate, definition.boneNames, out var mapped)) continue;
                if (source == null) { source = candidate; bones = mapped; }
                if (!string.IsNullOrEmpty(definition.rendererName) && candidate.name == definition.rendererName)
                { source = candidate; bones = mapped; break; }
            }
            if (source == null) return;
            var obj = new GameObject("Worn_" + view.itemId + "_Skin", typeof(SkinnedMeshRenderer));
            obj.layer = _handsOnly ? _handsLayer : source.gameObject.layer;
            obj.transform.SetParent(source.transform, false);
            obj.transform.localPosition = definition.localPosition;
            obj.transform.localRotation = Quaternion.Euler(definition.localEuler);
            obj.transform.localScale = definition.localScale;
            var skin = obj.GetComponent<SkinnedMeshRenderer>();
            skin.sharedMesh = definition.mesh;
            skin.sharedMaterials = definition.materials;
            skin.bones = bones;
            skin.rootBone = source.rootBone;
            skin.quality = source.quality;
            skin.updateWhenOffscreen = true;
            var bounds = source.localBounds;
            bounds.Encapsulate(definition.mesh.bounds); bounds.Expand(.08f);
            skin.localBounds = bounds;
            skin.shadowCastingMode = _handsOnly ? UnityEngine.Rendering.ShadowCastingMode.Off : source.shadowCastingMode;
            skin.receiveShadows = source.receiveShadows;
            skin.renderingLayerMask = source.renderingLayerMask;
            skin.motionVectorGenerationMode = source.motionVectorGenerationMode;
            view.parts.Add(obj);
        }
        private static bool TrySkinBones(SkinnedMeshRenderer source, string[] names, out Transform[] mapped)
        {
            mapped = new Transform[names.Length];
            var available = source.bones;
            for (int index = 0; index < names.Length; index++)
            {
                if (string.IsNullOrEmpty(names[index])) return false;
                foreach (var bone in available)
                    if (bone != null && bone.name == names[index]) { mapped[index] = bone; break; }
                if (mapped[index] == null) return false;
            }
            return true;
        }

        private static bool Replaces(EquipmentSlot slot, string name)
        {
            switch (slot)
            {
                case EquipmentSlot.Head: return Prefix(name, "GreatHelm", "Visor", "Nasal", "Vent", "Hood", "FaceWrap");
                case EquipmentSlot.Chest: return Prefix(name, "Cuirass", "Shoulder", "Tabard", "Fauld", "Cowl");
                case EquipmentSlot.Legs: return Prefix(name, "Trouser", "LegArmor");
                case EquipmentSlot.Hands: return Prefix(name, "Bracer", "Gauntlet", "Glove");
                case EquipmentSlot.Feet: return Prefix(name, "Boot", "Greave");
                case EquipmentSlot.Back: return Prefix(name, "Cloak", "Mantle");
                default: return false;
            }
        }
        private static bool Prefix(string name, params string[] prefixes)
        {
            foreach (string prefix in prefixes) if (name.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }
        private static void Remove(SlotView view)
        {
            foreach (var pair in view.hidden) if (pair.Key != null) pair.Key.SetActive(pair.Value);
            foreach (var part in view.parts) if (part != null)
            {
                part.SetActive(false);
                if (Application.isPlaying) Destroy(part); else DestroyImmediate(part);
            }
            view.parts.Clear(); view.hidden.Clear();
        }
        private void Clear() { foreach (var view in _slots.Values) Remove(view); _slots.Clear(); }
        private void OnDestroy() { Clear(); }
    }
}
