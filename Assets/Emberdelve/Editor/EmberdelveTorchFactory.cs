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
    ///
    /// V2/V3a: when the Poly Haven Lantern_01 is imported (V3a; V2 used the KayKit torch_mounted) it replaces the procedural cubes/cylinders (same child name
    /// "Sconce", same frame, no colliders, no shadow casting); the model is fitted from its renderer bounds (back against the
    /// wall plane, 0.62 m tall, centred on the arm) and the flame is placed at its top. Without the model the V1 procedural
    /// sconce is built exactly as before. The flame was retuned: warmer, smaller, softer (see BuildFlame).
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

            Vector3 flamePosition = new Vector3(0f, 0.37f, 0.262f);
            GameObject lantern = EmberdelvePolyHavenImport.LoadModel("Lantern_01");
            if (lantern == null || !TryBuildLanternSconce(root, lantern, out flamePosition))
            {
                flamePosition = new Vector3(0f, 0.37f, 0.262f);
                BuildProceduralSconce(root);
            }

            Transform flame = BuildFlame(root, EmberdelveMaterials.FlameMat, flamePosition);

            if (flicker != null) WireFlicker(flicker, flame);
        }

        /// <summary>Reuse the authored soft flame without adding a sconce or another light.</summary>
        public static Transform BuildHandheldFlame(Transform parent, Vector3 localPosition)
        {
            return EmberdelveHandTorchFlame.Build(parent, localPosition);
        }

        public const string ModelName = "Torch_Model";
        public static Transform BuildBrazierFlame(Transform light, bool blue)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(EmberdelvePaths.Materials + "/Torch_Flame.mat");
            if (material == null) throw new System.InvalidOperationException("Authored particle flame material missing");
            if (blue)
            {
                const string path = "Assets/Emberdelve/Art/Generated/CryptFlame_BlueParticle.mat";
                var cold = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (cold == null) { cold = new Material(material); AssetDatabase.CreateAsset(cold, path); }
                cold.SetColor("_BaseColor", new Color(.5f, 1.15f, 1.8f, 1f));
                cold.SetColor("_Color", new Color(.5f, 1.15f, 1.8f, 1f));
                EditorUtility.SetDirty(cold);
                material = cold;
            }
            var flame = BuildFlame(light, material, new Vector3(0f, -.24f, 0f));
            flame.name = "Brazier_ParticleFlame";
            flame.localScale = Vector3.one * 1.6f;
            foreach (var particles in flame.GetComponentsInChildren<ParticleSystem>())
            {
                var main = particles.main;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                if (!blue) continue;
                main.startColor = new Color(.75f, .9f, 1f, .6f);
                var colour = particles.colorOverLifetime;
                var gradient = new Gradient();
                gradient.SetKeys(new[] { new GradientColorKey(new Color(.7f,.95f,1f),0f),
                    new GradientColorKey(new Color(.15f,.45f,1f),1f) },
                    new[] { new GradientAlphaKey(0f,0f), new GradientAlphaKey(.9f,.12f),
                        new GradientAlphaKey(.5f,.55f), new GradientAlphaKey(0f,1f) });
                colour.enabled = true;
                colour.color = gradient;
            }
            BuildBrazierLayers(flame, material, blue);
            return flame;
        }

        // Brazier-only pass: handheld flames remain small enough not to obscure melee.
        private static void BuildBrazierLayers(Transform flame, Material material, bool blue)
        {
            var core = flame.GetComponent<ParticleSystem>();
            var main = core.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(.4f, .75f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(.3f, .65f);
            main.startSize = new ParticleSystem.MinMaxCurve(.10f, .19f);
            main.maxParticles = 80;
            main.cullingMode = ParticleSystemCullingMode.PauseAndCatchup;
            var emission = core.emission; emission.rateOverTime = 90f;
            var shape = core.shape; shape.radius = .085f; shape.angle = 12f;

            var embers = flame.Find(EmbersName).GetComponent<ParticleSystem>();
            main = embers.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1f, 2.2f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(.5f, 1.1f);
            main.startSize = new ParticleSystem.MinMaxCurve(.012f, .03f);
            main.maxParticles = 40;
            main.cullingMode = ParticleSystemCullingMode.PauseAndCatchup;
            emission = embers.emission; emission.rateOverTime = 14f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(.3f, 4, 8, 1, 0f) });
            var emberNoise = embers.noise;
            emberNoise.strength = .25f; emberNoise.scrollSpeed = .8f;

            var tongues = CreateBrazierLayer(flame, "FlameTongues", material);
            main = tongues.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(.35f, .65f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(.65f, 1f);
            main.startSize = new ParticleSystem.MinMaxCurve(.065f, .12f);
            main.startColor = blue ? new Color(.5f,.8f,1f,.55f) : new Color(1f,.75f,.32f,.5f);
            main.maxParticles = 36;
            emission = tongues.emission; emission.rateOverTime = 34f;
            shape = tongues.shape; shape.radius = .07f; shape.angle = 8f;
            var colour = tongues.colorOverLifetime;
            colour.enabled = true;
            colour.color = FadeGradient(blue ? new Color(.18f,.5f,1f) : new Color(1f,.4f,.08f), .85f);
            var size = tongues.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0,.65f), new Keyframe(.2f,1), new Keyframe(1,0)));
            var noise = tongues.noise;
            noise.enabled = true; noise.strength = .16f; noise.frequency = 2f; noise.scrollSpeed = 1.2f;
            var renderer = tongues.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.velocityScale = .12f;
            renderer.lengthScale = 2.3f;

            var smoke = CreateBrazierLayer(flame, "BrazierSmoke", SmokeMaterial());
            // Parent's +Z is vertical; smoke begins just above the fire and dissolves before ceiling height.
            smoke.transform.localPosition = new Vector3(0,0,.38f);
            main = smoke.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.3f, 2f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(.25f, .45f);
            main.startSize = new ParticleSystem.MinMaxCurve(.22f, .38f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0, Mathf.PI*2);
            main.startColor = Color.white;
            main.maxParticles = 16;
            emission = smoke.emission; emission.rateOverTime = 6f;
            shape = smoke.shape; shape.radius = .09f; shape.angle = 15f;
            colour = smoke.colorOverLifetime;
            colour.enabled = true; colour.color = FadeGradient(new Color(.24f,.23f,.22f), .18f);
            size = smoke.sizeOverLifetime; size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0,.55f,1,2.2f));
            noise = smoke.noise; noise.enabled = true; noise.strength = .12f; noise.frequency = .7f;
            var rotation = smoke.rotationOverLifetime; rotation.enabled = true; rotation.z = .25f;
        }

        private static ParticleSystem CreateBrazierLayer(Transform parent, string name, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var particles = go.AddComponent<ParticleSystem>();
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = particles.main;
            main.loop = true; main.playOnAwake = true; main.duration = 1f;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.cullingMode = ParticleSystemCullingMode.PauseAndCatchup;
            var shape = particles.shape; shape.enabled = true; shape.shapeType = ParticleSystemShapeType.Cone;
            ConfigureRenderer(go.GetComponent<ParticleSystemRenderer>(), material);
            return particles;
        }

        private static Gradient FadeGradient(Color colour, float alpha)
        {
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(colour,0), new GradientColorKey(colour,1) },
                new[] { new GradientAlphaKey(0,0), new GradientAlphaKey(alpha,.2f), new GradientAlphaKey(alpha*.5f,.6f), new GradientAlphaKey(0,1) });
            return gradient;
        }

        private static Material SmokeMaterial()
        {
            const string path = "Assets/Emberdelve/Art/Generated/Brazier_Smoke.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(AssetDatabase.LoadAssetAtPath<Material>(EmberdelvePaths.Materials + "/Torch_Flame.mat"));
                material.name = "Brazier_Smoke";
                AssetDatabase.CreateAsset(material, path);
            }
            material.SetColor("_BaseColor", Color.white);
            material.SetColor("_Color", Color.white);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            material.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.DisableKeyword("_ALPHAMODULATE_ON");
            EditorUtility.SetDirty(material);
            return material;
        }

        // Targeted migration preserves every room transform/collider and light setting.
        public static void RefreshSavedBrazierFlames()
        {
            int count = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Emberdelve/Prefabs/DungeonModules" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    bool changed = false;
                    foreach (var light in root.GetComponentsInChildren<Light>(true))
                    {
                        var flame = light.transform.Find("Brazier_ParticleFlame");
                        if (flame == null) continue;
                        var renderer = flame.GetComponent<ParticleSystemRenderer>();
                        bool blue = renderer != null && renderer.sharedMaterial != null && renderer.sharedMaterial.name.Contains("Blue");
                        Object.DestroyImmediate(flame.gameObject);
                        BuildBrazierFlame(light.transform, blue);
                        changed = true; count++;
                    }
                    if (changed) PrefabUtility.SaveAsPrefabAsset(root, path);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[BrazierParticles] Refreshed " + count + " authored braziers (core, tongues, embers, smoke).");
        }

        private const float ModelHeight = 0.62f;

        /// <summary>The V1 sconce: wall plate, arm, collar, wooden handle, iron band, glowing coal.</summary>
        private static void BuildProceduralSconce(Transform root)
        {
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
        }

        private const float LanternHeight = 0.5f;
        private const float ArmY = 0.36f;

        /// <summary>
        /// V3a: the Poly Haven Lantern_01 (real size 0.29 m, scaled to 0.38 m) hangs from a dark iron wall bracket (plate + arm, plain
        /// primitives). Same child name "Sconce", same frame (wall at z = -0.12, +Z into the room), no colliders, no shadow casting. The
        /// lantern is fitted from its renderer bounds (top at the arm, centred on x, hanging 0.17 m out of the wall) and the flame
        /// sits in the middle of its glass. The sconce root is at identity rotation/scale and the torch object has none either at build
        /// time, so world bounds minus the root position are sconce-local bounds. Returns false (-> procedural V1 sconce) without the model.
        /// </summary>
        private static bool TryBuildLanternSconce(Transform root, GameObject asset, out Vector3 flamePosition)
        {
            flamePosition = new Vector3(0f, 0.37f, 0.262f);

            GameObject model = (GameObject)PrefabUtility.InstantiatePrefab(asset, root);
            model.name = ModelName;
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            model.transform.localScale = Vector3.one;

            Collider[] colliders = model.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++) Object.DestroyImmediate(colliders[i]);

            Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0 || !TryGetBounds(renderers, out Bounds bounds) || bounds.size.y < 0.01f)
            {
                Object.DestroyImmediate(model);
                return false;
            }

            // The parts sit right around the light; their shadows would only be streaks and acne.
            for (int i = 0; i < renderers.Length; i++) renderers[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            model.transform.localScale = Vector3.one * (LanternHeight / bounds.size.y);
            TryGetBounds(renderers, out bounds);

            Vector3 min = bounds.min - root.position;
            Vector3 max = bounds.max - root.position;
            Vector3 shift = new Vector3(-(min.x + max.x) * 0.5f, (ArmY - 0.015f) - max.y, 0.17f - (min.z + max.z) * 0.5f);
            model.transform.localPosition += shift;

            // dark iron bracket: wall plate, arm to the lantern's ring
            Material iron = EmberdelveMaterials.IronDark;
            AddPart(root, "Plate", PrimitiveType.Cube, new Vector3(0f, ArmY - 0.14f, -0.105f), Vector3.zero, new Vector3(0.12f, 0.26f, 0.03f), iron);
            AddPart(root, "Arm", PrimitiveType.Cube, new Vector3(0f, ArmY, 0.04f), Vector3.zero, new Vector3(0.03f, 0.03f, 0.30f), iron);

            // flame: centre of the glass pane (or of the whole lantern)
            Vector3 centre = bounds.center + shift;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i].name.ToLowerInvariant().Contains("glass")) { centre = renderers[i].bounds.center; break; }
            }

            flamePosition = (renderers.Length > 0 ? centre : bounds.center) - root.position;
            return true;
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

        /// <summary>
        /// V2 retune of the V1 flame (it read as a red/pink blob up close): smaller, shorter-lived soft quads (0.09-0.17 m,
        /// 0.35-0.6 s), a yellow-white core that cools through orange to a still-orange tip (V1 ended in dark red, which ACES +
        /// bloom turned pink), lower alpha, and an HDR material colour of 1.8 max instead of 3.1. Still additive with the soft
        /// radial sprite, 55/s, max 40 particles.
        /// </summary>
        private static Transform BuildFlame(Transform parent, Material flameMaterial, Vector3 localPosition)
        {
            GameObject flameGo = new GameObject(FlameName);
            flameGo.transform.SetParent(parent, false);
            flameGo.transform.localPosition = localPosition;
            flameGo.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f); // cone axis (+Z) points up

            ParticleSystem ps = flameGo.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = ps.main;
            main.loop = true;
            main.playOnAwake = true;
            main.duration = 1f;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.35f, 0.6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.2f, 0.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.09f, 0.17f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.9f, 0.55f, 0.6f));
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

            // yellow-white core -> yellow-orange -> orange, fading in and out (never through red)
            ParticleSystem.ColorOverLifetimeModule colour = ps.colorOverLifetime;
            colour.enabled = true;
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.95f, 0.7f), 0f),
                    new GradientColorKey(new Color(1f, 0.7f, 0.22f), 0.4f),
                    new GradientColorKey(new Color(0.95f, 0.45f, 0.08f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.9f, 0.12f),
                    new GradientAlphaKey(0.5f, 0.55f),
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
                    new GradientColorKey(new Color(1f, 0.8f, 0.4f), 0f),
                    new GradientColorKey(new Color(0.95f, 0.45f, 0.08f), 1f)
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
