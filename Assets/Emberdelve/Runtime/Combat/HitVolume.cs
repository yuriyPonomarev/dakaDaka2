using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;

namespace Emberdelve.Combat
{
    /// <summary>
    /// One body part of a target in world space: a capsule (segment A-B plus radius; a sphere when A == B)
    /// tagged with the location it counts as. This plain value is all the authority's sweep needs, so a server
    /// or a fixture can supply shapes without any GameObject.
    /// </summary>
    public readonly struct HitShape
    {
        public readonly HitLocation Location;
        public readonly Vector3 A;
        public readonly Vector3 B;
        public readonly float Radius;

        public HitShape(HitLocation location, Vector3 a, Vector3 b, float radius)
        {
            Location = location;
            A = a;
            B = b;
            Radius = radius;
        }
    }

    /// <summary>
    /// A combat entity that exposes its hit volumes. The swept melee can only strike entities that implement
    /// this: the volumes ARE the hit boxes (the old physics colliders are untouched and keep serving movement,
    /// ranged raycasts and interaction).
    /// </summary>
    public interface IHitShapeProvider
    {
        /// <summary>Appends the entity's CURRENT world-space hit shapes.</summary>
        void CollectHitShapes(List<HitShape> shapes);
    }

    /// <summary>
    /// A hit volume on a combat entity: a capsule/sphere in the local space of this transform, with the body
    /// location it counts as. It is deliberately NOT a Collider: it has no layer, joins no physics query, cannot
    /// disturb a CharacterController, a loot raycast or an interaction check, and is evaluated analytically by
    /// the authority's sweep (which is also what lets a server run it without a physics scene).
    /// </summary>
    public sealed class HitVolume : MonoBehaviour
    {
        public HitLocation location = HitLocation.Body;
        public Vector3 localA;
        public Vector3 localB;
        [Min(0.01f)] public float radius = 0.2f;

        public void Configure(HitLocation where, Vector3 a, Vector3 b, float r)
        {
            location = where;
            localA = a;
            localB = b;
            radius = Mathf.Max(0.01f, r);
        }

        public HitShape ToWorld()
        {
            Vector3 scale = transform.lossyScale;
            float uniform = Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            return new HitShape(location, transform.TransformPoint(localA), transform.TransformPoint(localB),
                radius * uniform);
        }

        private void OnDrawGizmosSelected()
        {
            HitShape s = ToWorld();
            Gizmos.color = location == HitLocation.Head ? Color.red : Color.yellow;
            Gizmos.DrawWireSphere(s.A, s.Radius);
            if ((s.B - s.A).sqrMagnitude > 1e-6f) Gizmos.DrawWireSphere(s.B, s.Radius);
        }
    }

    /// <summary>Builds and reads the standard humanoid hit-volume layout (our own tunable body proportions).</summary>
    public static class HitVolumeLayout
    {
        public const string ContainerName = "HitVolumes";

        /// <summary>
        /// Gives <paramref name="root"/> a head, torso, two arms, two legs and two feet, sized from a body scale
        /// (1 = a 1.85 m humanoid with its head centre at 1.85 m above the root; a positive
        /// <paramref name="headCentreHeight"/> places the head centre at that absolute height instead). An entity that already has
        /// HitVolume children (authored in a scene or prefab) keeps them. Volumes live in root-local space, so a
        /// moving or turning root carries them. Returns the volumes, which the caller should cache.
        /// </summary>
        public static HitVolume[] EnsureHumanoid(GameObject root, float bodyScale = 1f, float headCentreHeight = 0f)
        {
            HitVolume[] existing = root.GetComponentsInChildren<HitVolume>(true);
            if (existing.Length > 0) return existing;

            float s = Mathf.Max(0.2f, bodyScale);
            Transform container = new GameObject(ContainerName).transform;
            container.SetParent(root.transform, false);
            int ignoreLayer = LayerMask.NameToLayer("Ignore Raycast");
            if (ignoreLayer >= 0) container.gameObject.layer = ignoreLayer;

            List<HitVolume> made = new List<HitVolume>(9);
            float headY = headCentreHeight > 0f ? headCentreHeight : 1.85f * s;

            Add(container, made, HitLocation.Head, "Head", new Vector3(0f, headY, 0f), new Vector3(0f, headY, 0f), 0.24f * s);
            Add(container, made, HitLocation.Body, "Torso", new Vector3(0f, 1.00f * s, 0f), new Vector3(0f, 1.45f * s, 0f), 0.33f * s);

            for (int side = -1; side <= 1; side += 2)
            {
                string tag = side < 0 ? "_L" : "_R";
                Add(container, made, HitLocation.Arms, "Arm" + tag, new Vector3(side * 0.43f * s, 1.05f * s, 0f), new Vector3(side * 0.43f * s, 1.45f * s, 0f), 0.10f * s);
                Add(container, made, HitLocation.Legs, "Leg" + tag, new Vector3(side * 0.16f * s, 0.50f * s, 0f), new Vector3(side * 0.16f * s, 0.80f * s, 0f), 0.13f * s);
                Add(container, made, HitLocation.Feet, "Foot" + tag, new Vector3(side * 0.16f * s, 0.12f * s, 0f), new Vector3(side * 0.16f * s, 0.25f * s, 0f), 0.12f * s);
            }

            return made.ToArray();
        }

        private static void Add(Transform container, List<HitVolume> into, HitLocation where, string name, Vector3 a, Vector3 b, float radius)
        {
            GameObject go = new GameObject("Hit_" + name);
            go.transform.SetParent(container, false);
            if (container.gameObject.layer != 0) go.layer = container.gameObject.layer;
            HitVolume volume = go.AddComponent<HitVolume>();
            volume.Configure(where, a, b, radius);
            into.Add(volume);
        }

        public static void Collect(HitVolume[] volumes, List<HitShape> shapes)
        {
            if (volumes == null) return;
            for (int i = 0; i < volumes.Length; i++)
            {
                if (volumes[i] != null && volumes[i].isActiveAndEnabled) shapes.Add(volumes[i].ToWorld());
            }
        }
    }
}
