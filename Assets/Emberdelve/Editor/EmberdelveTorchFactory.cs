using UnityEditor;
using UnityEngine;
using Emberdelve.Feedback;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// V1: the procedural wall sconce (iron bracket, wood handle, glowing coal) with a particle flame and embers.
    /// The root is still a child called "Sconce" of the torch light object: TorchFlicker finds it by that name at
    /// runtime and mounts it on the nearest wall (position + rotation, +Z pointing away from the wall).
    /// Local frame of the sconce: wall surface at z = -0.12, +Z out into the room, +Y up.
    /// </summary>
    internal static class EmberdelveTorchFactory
    {
        public const string SconceName = "Sconce";
        public const string FlameName = "Flame";
        public const string EmbersName = "Embers";

        /// <summary>Builds the sconce under <paramref name="torch"/> and wires the TorchFlicker to it.</summary>
        public static void Build(GameObject torch, TorchFlicker flicker)
        {
            GameObject sconce = new GameObject(SconceName);
            sconce.transform.SetParent(torch.transform, false);
            sconce.transform.localPosition = Vector3.zero;
            sconce.transform.localRotation = Quaternion.identity;
            Transform root = sconce.transform;

            Material iron = EmberdelveMaterials.IronDark;
            Material wood = EmberdelveMaterials.Wood;
            Material coal = EmberdelveMaterials.TorchMat;

            // Wall plate, arm out of the wall, collar the handle sits in.
            AddPart(root, "Plate", PrimitiveType.Cube, new Vector3(0f, 0f, -0.105f), Vector3.zero, new Vector3(0.14f, 0.28f, 0.03f), iron);
            AddPart(root, "Arm", PrimitiveType.Cube, new Vector3(0f, 0.01f, 0.045f), Vector3.zero, new Vector3(0.035f, 0.035f, 0.27f), iron);
            AddPart(root, "Collar", PrimitiveType.Cylinder, new Vector3(0f, 0.022f, 0.184f), new Vector3(12f, 0f, 0f), new Vector3(0.085f, 0.012f, 0.085f), iron);

            // Handle leans slightly out of the wall; a second iron band near the head.
            AddPart(root, "Handle", PrimitiveType.Cylinder, new Vector3(0f, 0.12f, 0.205f), new Vector3(12f, 0f, 0f), new Vector3(0.05f, 0.22f, 0.05f), wood);
            AddPart(root, "Band", PrimitiveType.Cylinder, new Vector3(0f, 0.237f, 0.23f), new Vector3(12f, 0f, 0f), new Vector3(0.065f, 0.01f, 0.065f), iron);
            AddPart(root, "Coal", PrimitiveType.Sphere, new Vector3(0f, 0.34f, 0.255f), Vector3.zero, new Vector3(0.08f, 0.08f, 0.08f), coal);

            Transform flame = BuildFlame(root, EmberdelveMaterials.FlameMat);

            if (flicker != null) WireFlicker(flicker, flame);
        }

        private static GameObject AddPart(Transform parent, string name, PrimitiveType type, Vector3 localPosition, Vector3 euler, Vector3 localScale, Material material)
        {
            GameObject part = GameObject.CreatePrimitive(type);
            part.name = name;

            // Pure visuals: no collider (the sconce must never affect movement, combat or projectiles).
            Collider collider = part.GetComponent<Collider>();
            if (collider != null) Object.DestroyImmediate(collider);

            part.transform.SetParent(parent, false);
            part.transform.localPosition = localPosition;
            part.transform.localRotation = Quaternion.Euler(euler);
            part.transform.localScale = localScale;

            Renderer renderer = part.GetComponent<Renderer>();
            if (renderer != null)
            {
                if (material != null) renderer.sharedMaterial = material;
                // The parts sit right next to the light; their shadows would only be streaks and acne.
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            return part;
        }

        private static Transform BuildFlame(Transform parent, Material flameMaterial)
        {
            GameObject flameGo = new GameObject(FlameName);
            flameGo.transform.SetParent(parent, false);
            flameGo.transform.localPosition = new Vector3(0f, 0.37f, 0.262f);
            flameGo.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f); // cone axis (+Z) points up

            ParticleSystem ps = flameGo.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = ps.main;
            main.loop = true;
            main.playOnAwake = true;
            main.duration = 1f;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.45f, 0.8f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.25f, 0.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.16f, 0.288f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.78f, 0.4f, 0.8f));
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.05f);
            main.maxParticles = 40;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = new ParticleSystem.MinMaxCurve(55f);

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 7f;
            shape.radius = 0.025f;

            // yellow-white core -> orange -> dark red, fading in and out
            ParticleSystem.ColorOverLifetimeModule colour = ps.colorOverLifetime;
            colour.enabled = true;
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.9f, 0.55f), 0f),
                    new GradientColorKey(new Color(1f, 0.45f, 0.1f), 0.4f),
                    new GradientColorKey(new Color(0.5f, 0.06f, 0.02f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.12f),
                    new GradientAlphaKey(0.6f, 0.6f),
                    new GradientAlphaKey(0f, 1f)
                });
            colour.color = new ParticleSystem.MinMaxGradient(gradient);

            ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
            size.enabled = true;
            AnimationCurve curve = new AnimationCurve(new Keyframe(0f, 0.6f), new Keyframe(0.25f, 1f), new Keyframe(1f, 0.15f));
            size.size = new ParticleSystem.MinMaxCurve(1f, curve);

            ParticleSystem.NoiseModule noise = ps.noise;
            noise.enabled = true;
            noise.strength = new ParticleSystem.MinMaxCurve(0.12f);
            noise.frequency = 1.5f;
            noise.scrollSpeed = new ParticleSystem.MinMaxCurve(0.6f);

            ParticleSystemRenderer psRenderer = flameGo.GetComponent<ParticleSystemRenderer>();
            ConfigureRenderer(psRenderer, flameMaterial);

            BuildEmbers(flameGo.transform, flameMaterial);
            return flameGo.transform;
        }

        private static void BuildEmbers(Transform parent, Material material)
        {
            GameObject go = new GameObject(EmbersName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;

            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = ps.main;
            main.loop = true;
            main.playOnAwake = true;
            main.duration = 1f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.25f, 0.6f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.012f, 0.025f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.6f, 0.2f, 1f));
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.03f);
            main.maxParticles = 12;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = new ParticleSystem.MinMaxCurve(6f);

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 30f;
            shape.radius = 0.04f;

            ParticleSystem.ColorOverLifetimeModule colour = ps.colorOverLifetime;
            colour.enabled = true;
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.7f, 0.3f), 0f),
                    new GradientColorKey(new Color(0.8f, 0.15f, 0.03f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.8f, 0.5f),
                    new GradientAlphaKey(0f, 1f)
                });
            colour.color = new ParticleSystem.MinMaxGradient(gradient);

            ParticleSystem.NoiseModule noise = ps.noise;
            noise.enabled = true;
            noise.strength = new ParticleSystem.MinMaxCurve(0.2f);
            noise.frequency = 0.8f;

            ConfigureRenderer(go.GetComponent<ParticleSystemRenderer>(), material);
        }

        private static void ConfigureRenderer(ParticleSystemRenderer renderer, Material material)
        {
            if (renderer == null) return;

            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            if (material != null) renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        private static void WireFlicker(TorchFlicker flicker, Transform flameAnchor)
        {
            SerializedObject serialized = new SerializedObject(flicker);

            SerializedProperty anchor = serialized.FindProperty("flameAnchor");
            if (anchor != null) anchor.objectReferenceValue = flameAnchor;

            SerializedProperty follows = serialized.FindProperty("lightFollowsFlame");
            if (follows != null) follows.boolValue = true;

            SerializedProperty jitter = serialized.FindProperty("lightJitter");
            if (jitter != null) jitter.floatValue = 0.02f;

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
