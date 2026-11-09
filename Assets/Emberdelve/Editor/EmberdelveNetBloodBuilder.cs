using UnityEditor;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    /// <summary>Builds a standalone hit effect so it survives proxy removal and works before a proxy exists.</summary>
    public static class EmberdelveNetBloodBuilder
    {
        private const string Path = "Assets/Emberdelve/Resources/Emberdelve/NetBloodHit.prefab";

        [MenuItem("Emberdelve/Visual/Build Network Blood Effect")]
        public static void Build()
        {
            EmberdelveVisualSetup.EnsureFolder("Assets/Emberdelve/Resources/Emberdelve");
            var root = new GameObject("NetBloodHit");
            try
            {
                ParticleSystem particles = EmberdelveBloodEffect.Create(root.transform);
                // CombatHitWire carries the authoritative point but no approach vector; a radial burst makes
                // the effect visible from every client camera and does not guess which way is outward.
                var shape = particles.shape;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.12f;
                PrefabUtility.SaveAsPrefabAsset(root, Path);
                AssetDatabase.SaveAssets();
            }
            finally { Object.DestroyImmediate(root); }
        }
    }
}
