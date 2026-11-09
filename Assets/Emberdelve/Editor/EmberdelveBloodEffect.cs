using UnityEditor;
using UnityEngine;
using Emberdelve.AI;

namespace Emberdelve.EditorTools
{
    /// <summary>Creates the authored enemy hit effect and updates existing visual prefabs.</summary>
    public static class EmberdelveBloodEffect
    {
        private const string MaterialPath = "Assets/Emberdelve/Art/Materials/BloodParticles.mat";

        public static ParticleSystem Create(Transform parent)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
                if (shader == null) shader = Shader.Find("Particles/Standard Unlit");
                material = new Material(shader) { name = "BloodParticles" };
                AssetDatabase.CreateAsset(material, MaterialPath);
            }

            // The default particle texture gives each droplet a soft edge instead of an opaque square.
            var texture = AssetDatabase.GetBuiltinExtraResource<Texture2D>("Default-Particle.psd");
            if (texture != null) material.SetTexture("_BaseMap", texture);
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            EditorUtility.SetDirty(material);

            var go = new GameObject("BloodHit");
            go.transform.SetParent(parent, false);
            var particles = go.AddComponent<ParticleSystem>();
            var main = particles.main;
            main.playOnAwake = false;
            main.loop = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            // The hit point can sit on or just inside a capsule. Give each droplet enough size and hang time to
            // read clearly at normal combat camera distances instead of disappearing as tiny dark pixels.
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.42f, 0.78f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.8f, 3.8f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.09f, 0.18f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.48f, 0.012f, 0.018f, 1f), new Color(0.82f, 0.045f, 0.025f, 1f));
            main.gravityModifier = 0.65f;
            main.maxParticles = 144;
            var emission = particles.emission;
            emission.enabled = false;
            var shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 55f;
            shape.radius = 0.07f;
            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = material;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            return particles;
        }

        [MenuItem("Emberdelve/Visual/Update Enemy Blood Effects")]
        public static void UpdatePrefabs()
        {
            foreach (var variant in EmberdelveEnemyVisualFactory.Variants)
            {
                string path = EmberdelveEnemyVisualFactory.PrefabPath(variant.Name);
                var root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    var old = root.transform.Find("BloodHit");
                    if (old != null) Object.DestroyImmediate(old.gameObject);
                    var effect = Create(root.transform);
                    var serialized = new SerializedObject(root.GetComponent<EnemyVisualDriver>());
                    serialized.FindProperty("bloodEffect").objectReferenceValue = effect;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            AssetDatabase.SaveAssets();
        }
    }
}
