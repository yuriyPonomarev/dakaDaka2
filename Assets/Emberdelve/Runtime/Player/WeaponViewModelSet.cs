using System;
using UnityEngine;

namespace Emberdelve.Player
{
    /// <summary>
    /// V3c: which model dresses which weapon in the player's hands, plus the first-person arms prefab. A data asset at
    /// Resources/Emberdelve/WeaponViewModelSet (written by the editor tool EmberdelveWeaponModels) so the runtime can dress
    /// the weapon the local player holds without referencing the editor. Purely visual: it never reads or writes combat data.
    ///
    /// Model convention of every <see cref="Entry.prefab"/> (built by the editor from the Poly Haven FBX, measured from the mesh):
    /// the grip is at the prefab origin, the blade / head points along +Y, the flat face (blade side) looks along +Z, the edge
    /// is along X; the length from the grip to the far end (the tip or the head) is <see cref="Entry.length"/> metres at scale 1.
    /// Shields: face along +Z, top along +Y, origin at the centre of the boss; <see cref="Entry.length"/> is the height.
    /// </summary>
    public sealed class WeaponViewModelSet : ScriptableObject
    {
        public const string ResourcePath = "Emberdelve/WeaponViewModelSet";

        [Serializable]
        public sealed class Entry
        {
            [Tooltip("WeaponData.weaponId this model dresses.")]
            public string weaponId;
            [Tooltip("The normalised model (see the class summary). No colliders.")]
            public GameObject prefab;
            [Tooltip("Metres from the grip to the far end (shield: height) at scale 1: the size in the player's hands.")]
            public float length = 1f;
            [Tooltip("The Poly Haven asset id the prefab was built from.")]
            public string sourceId;
        }

        [Serializable]
        public sealed class Unmapped
        {
            public string weaponId;
            [Tooltip("Why no model: the weapon keeps its blockout / old visual.")]
            public string reason;
        }

        public Entry[] entries = new Entry[0];
        [Tooltip("WeaponData ids that deliberately have no model and keep their current visual.")]
        public Unmapped[] unmapped = new Unmapped[0];
        [Tooltip("Imported weapon models no WeaponData uses yet (kept for later weapons), by Poly Haven id.")]
        public string[] spareModels = new string[0];
        [Tooltip("The first-person arms (Humanoid rig, arms-only skinned mesh, Animator with the seven viewmodel states).")]
        public GameObject armsPrefab;

        public Entry Find(string weaponId)
        {
            if (entries == null || string.IsNullOrEmpty(weaponId)) return null;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i] != null && entries[i].weaponId == weaponId && entries[i].prefab != null) return entries[i];
            }

            return null;
        }

        public bool IsUnmapped(string weaponId)
        {
            if (unmapped == null || string.IsNullOrEmpty(weaponId)) return false;
            for (int i = 0; i < unmapped.Length; i++)
            {
                if (unmapped[i] != null && unmapped[i].weaponId == weaponId) return true;
            }

            return false;
        }

        public static WeaponViewModelSet Load()
        {
            return Resources.Load<WeaponViewModelSet>(ResourcePath);
        }
    }
}
