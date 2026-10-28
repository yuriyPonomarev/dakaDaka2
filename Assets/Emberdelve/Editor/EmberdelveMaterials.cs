using UnityEditor;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Placeholder materials for the blockout. They are real .mat assets in the project, so a artist
    /// can re-skin them (or replace the references on the data assets) without touching code.
    /// </summary>
    internal static class EmberdelveMaterials
    {
        public static Material StoneFloor;
        public static Material StoneWall;
        public static Material StoneCeiling;
        public static Material BowWood;
        public static Material Blade;
        public static Material Grip;
        public static Material EnemyBody;
        public static Material EnemyHead;
        public static Material EliteBody;
        public static Material RelicMat;
        public static Material CoinMat;
        public static Material DraughtMat;
        public static Material ArrowMat;
        public static Material PortalMat;
        public static Material TorchMat;
        public static Material LobbyFloor;
        public static Material ArmorMat;
        public static Material ShieldMat;
        public static Material ChestSupply;
        public static Material ChestSupplyLid;
        public static Material ChestEmber;
        public static Material ChestEmberLid;
        public static Material ChestSealed;
        public static Material ChestSealedLid;
        public static Material SwingTrailMat;
        public static Material Wood;
        public static Material IronDark;
        public static Material FlameMat;

        public static void BuildAll()
        {
            StoneFloor = Create("Stone_Floor", new Color(0.155f, 0.148f, 0.138f), 0.22f, null);
            StoneWall = Create("Stone_Wall", new Color(0.205f, 0.196f, 0.18f), 0.18f, null);
            StoneCeiling = Create("Stone_Ceiling", new Color(0.115f, 0.11f, 0.105f), 0.15f, null);
            LobbyFloor = Create("Lobby_StoneFloor", new Color(0.13f, 0.125f, 0.12f), 0.3f, null);
            Wood = Create("Wood_Planks", new Color(0.32f, 0.22f, 0.13f), 0.3f, null);
            IronDark = Create("Iron_Dark", new Color(0.075f, 0.075f, 0.08f), 0.5f, null);

            // V1: photographic PBR sets. Applied on every build, so projects that already hold the old flat .mat files
            // are upgraded in place. A set that fails to load keeps the flat colour (warning only).
            EmberdelveVisualSetup.EnsureTextureImportSettings();
            ApplyPbr(StoneFloor, "PavingStones142", new Color(0.58f, 0.57f, 0.55f, 1f), 0.85f);
            ApplyPbr(StoneWall, "Bricks100", new Color(0.62f, 0.60f, 0.58f, 1f), 0.85f);
            ApplyPbr(StoneCeiling, "Rock030", new Color(0.48f, 0.47f, 0.45f, 1f), 0.8f);
            ApplyPbr(LobbyFloor, "PavingStones142", new Color(0.56f, 0.55f, 0.53f, 1f), 0.9f);
            ApplyPbr(Wood, "Planks023A", new Color(0.8f, 0.72f, 0.62f, 1f), 0.8f);

            if (IronDark != null)
            {
                SetPropertyColor(IronDark, "_BaseColor", new Color(0.075f, 0.075f, 0.08f, 1f));
                SetPropertyFloat(IronDark, "_Metallic", 0.85f);
                SetPropertyFloat(IronDark, "_Smoothness", 0.45f);
                EditorUtility.SetDirty(IronDark);
            }

            BowWood = Create("Bow_Wood", new Color(0.32f, 0.2f, 0.1f), 0.4f, null);
            Blade = Create("Blade_Metal", new Color(0.7f, 0.72f, 0.76f), 0.78f, null);
            Grip = Create("Grip_Leather", new Color(0.17f, 0.11f, 0.07f), 0.3f, null);

            // V1b: viewmodel materials were blown out by the hand torch + ACES/bloom. Re-applied so existing .mat assets update.
            ApplyViewmodel(BowWood, new Color(0.24f, 0.15f, 0.075f, 1f), 0.05f, 0.35f);
            ApplyViewmodel(Blade, new Color(0.38f, 0.39f, 0.41f, 1f), 0.85f, 0.55f);
            ApplyViewmodel(Grip, new Color(0.12f, 0.078f, 0.05f, 1f), 0.05f, 0.28f);

            EnemyBody = Create("Enemy_Body", new Color(0.4f, 0.36f, 0.42f), 0.25f, new Color(0.1f, 0.06f, 0.09f));
            EnemyHead = Create("Enemy_Head", new Color(0.42f, 0.37f, 0.32f), 0.3f, new Color(0.12f, 0.03f, 0.02f));
            EliteBody = Create("Enemy_EliteBody", new Color(0.5f, 0.22f, 0.18f), 0.3f, new Color(0.3f, 0.08f, 0.04f));

            RelicMat = Create("Loot_Relic", new Color(0.85f, 0.66f, 0.22f), 0.6f, new Color(0.5f, 0.34f, 0.06f));
            CoinMat = Create("Loot_Coin", new Color(0.62f, 0.45f, 0.16f), 0.65f, null);
            DraughtMat = Create("Loot_Draught", new Color(0.55f, 0.1f, 0.12f), 0.55f, new Color(0.16f, 0.02f, 0.02f));
            ArrowMat = Create("Loot_Arrow", new Color(0.55f, 0.5f, 0.42f), 0.35f, null);

            PortalMat = Create("EmberGate_Glow", new Color(1f, 0.55f, 0.18f), 0.4f, new Color(1.6f, 0.62f, 0.14f));
            TorchMat = Create("Torch_Brush", new Color(0.9f, 0.55f, 0.2f), 0.3f, new Color(1.3f, 0.5f, 0.08f));

            ArmorMat = Create("Gear_Armor", new Color(0.42f, 0.3f, 0.16f), 0.35f, null);
            ShieldMat = Create("Gear_Shield", new Color(0.5f, 0.52f, 0.6f), 0.6f, null);

            ChestSupply = Create("Chest_Supply", new Color(0.3f, 0.22f, 0.12f), 0.3f, null);
            ChestSupplyLid = Create("Chest_SupplyLid", new Color(0.42f, 0.32f, 0.17f), 0.35f, null);
            ChestEmber = Create("Chest_Ember", new Color(0.34f, 0.17f, 0.1f), 0.35f, new Color(0.5f, 0.2f, 0.04f));
            ChestEmberLid = Create("Chest_EmberLid", new Color(0.62f, 0.36f, 0.14f), 0.4f, new Color(0.8f, 0.4f, 0.08f));
            ChestSealed = Create("Chest_Sealed", new Color(0.2f, 0.2f, 0.28f), 0.5f, null);
            ChestSealedLid = Create("Chest_SealedLid", new Color(0.34f, 0.34f, 0.46f), 0.6f, new Color(0.2f, 0.2f, 0.5f));
            SwingTrailMat = CreateAdditive("Swing_Trail", new Color(1f, 0.74f, 0.32f, 0.7f));
            FlameMat = CreateFlame("Torch_Flame");
        }

        /// <summary>
        /// Assigns an ambientCG set (BaseColor / Normal / MaskMS / AO) to a URP Lit material. Missing textures are skipped
        /// individually with a warning; without a BaseColor map nothing is touched and the flat colour stays.
        /// </summary>
        private static void ApplyPbr(Material material, string id, Color tint, float smoothnessScale)
        {
            if (material == null) return;

            Texture2D baseMap = EmberdelveVisualSetup.LoadTexture(EmberdelveVisualSetup.TexturePath(id, "BaseColor", "jpg"));
            if (baseMap == null)
            {
                Debug.LogWarning("[Emberdelve][Visual] " + id + " BaseColor texture missing; '" + material.name + "' keeps its flat colour.");
                return;
            }

            Texture2D normalMap = EmberdelveVisualSetup.LoadTexture(EmberdelveVisualSetup.TexturePath(id, "Normal", "png"));
            Texture2D maskMap = EmberdelveVisualSetup.LoadTexture(EmberdelveVisualSetup.TexturePath(id, "MaskMS", "png"));
            Texture2D aoMap = EmberdelveVisualSetup.LoadTexture(EmberdelveVisualSetup.TexturePath(id, "AO", "jpg"));

            SetPropertyTexture(material, "_BaseMap", baseMap);
            SetPropertyTexture(material, "_MainTex", baseMap);
            SetPropertyColor(material, "_BaseColor", tint);
            SetPropertyColor(material, "_Color", tint);

            if (normalMap != null)
            {
                SetPropertyTexture(material, "_BumpMap", normalMap);
                SetPropertyFloat(material, "_BumpScale", 1f);
                material.EnableKeyword("_NORMALMAP");
            }
            else
            {
                Debug.LogWarning("[Emberdelve][Visual] " + id + " Normal texture missing.");
                SetPropertyTexture(material, "_BumpMap", null);
                material.DisableKeyword("_NORMALMAP");
            }

            if (maskMap != null)
            {
                SetPropertyTexture(material, "_MetallicGlossMap", maskMap);
                SetPropertyFloat(material, "_Metallic", 0f);
                SetPropertyFloat(material, "_Smoothness", smoothnessScale); // scales the smoothness in the map's alpha
                SetPropertyFloat(material, "_SmoothnessTextureChannel", 0f);
                material.EnableKeyword("_METALLICSPECGLOSSMAP");
            }
            else
            {
                Debug.LogWarning("[Emberdelve][Visual] " + id + " MaskMS texture missing.");
                SetPropertyTexture(material, "_MetallicGlossMap", null);
                material.DisableKeyword("_METALLICSPECGLOSSMAP");
            }

            if (aoMap != null)
            {
                SetPropertyTexture(material, "_OcclusionMap", aoMap);
                SetPropertyFloat(material, "_OcclusionStrength", 1f);
                material.EnableKeyword("_OCCLUSIONMAP");
            }
            else
            {
                Debug.LogWarning("[Emberdelve][Visual] " + id + " AO texture missing.");
                SetPropertyTexture(material, "_OcclusionMap", null);
                material.DisableKeyword("_OCCLUSIONMAP");
            }

            // UVs are generated in tile units (EmberdelveVisualSetup.ApplyWorldUv), so the material tiles exactly once.
            if (material.HasProperty("_BaseMap"))
            {
                material.SetTextureScale("_BaseMap", Vector2.one);
                material.SetTextureOffset("_BaseMap", Vector2.zero);
            }

            EditorUtility.SetDirty(material);
        }

        private static void ApplyViewmodel(Material material, Color color, float metallic, float smoothness)
        {
            if (material == null) return;
            SetPropertyColor(material, "_BaseColor", color);
            SetPropertyColor(material, "_Color", color);
            SetPropertyFloat(material, "_Metallic", metallic);
            SetPropertyFloat(material, "_Smoothness", smoothness);
            SetPropertyFloat(material, "_Glossiness", smoothness);
            EditorUtility.SetDirty(material);
        }

        /// <summary>
        /// Torch flame: additive, unlit, no depth write, HDR colour so the bloom picks it up. Re-applied on every build.
        /// </summary>
        private static Material CreateFlame(string name)
        {
            string path = EmberdelvePaths.Materials + "/" + name + ".mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool created = material == null;
            if (created)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Sprites/Default");
                if (shader == null)
                {
                    Debug.LogError("[Emberdelve] No usable shader found; material '" + name + "' was skipped.");
                    return null;
                }
                material = new Material(shader) { name = name };
            }

            Color hdr = new Color(3.12f, 1.43f, 0.455f, 1f);
            SetPropertyColor(material, "_BaseColor", hdr);
            SetPropertyColor(material, "_Color", hdr);
            SetPropertyColor(material, "_EmissionColor", Color.black);

            Texture2D soft = EmberdelveVisualSetup.LoadTexture(EmberdelveTextureImport.FlameTexturePath);
            if (soft != null)
            {
                SetPropertyTexture(material, "_BaseMap", soft);
                SetPropertyTexture(material, "_MainTex", soft);
            }
            else
            {
                Debug.LogWarning("[Emberdelve][Visual] Flame_Soft.png missing; flame particles will be hard quads.");
            }

            SetPropertyFloat(material, "_Surface", 1f);
            SetPropertyFloat(material, "_Blend", 2f);
            SetPropertyFloat(material, "_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            SetPropertyFloat(material, "_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            SetPropertyFloat(material, "_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            SetPropertyFloat(material, "_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            SetPropertyFloat(material, "_ZWrite", 0f);
            SetPropertyFloat(material, "_Cull", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            material.SetShaderPassEnabled("ShadowCaster", false);

            if (created) AssetDatabase.CreateAsset(material, path);
            else EditorUtility.SetDirty(material);
            return material;
        }

        private static Material Create(string name, Color baseColor, float smoothness, Color? emission)
        {
            string path = EmberdelvePaths.Materials + "/" + name + ".mat";

            Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Diffuse");

            if (shader == null)
            {
                Debug.LogError("[Emberdelve] No usable shader found; material '" + name + "' was skipped.");
                return null;
            }

            Material material = new Material(shader);
            material.name = name;
            material.color = baseColor;

            SetPropertyColor(material, "_BaseColor", baseColor);
            SetPropertyColor(material, "_Color", baseColor);
            SetPropertyFloat(material, "_Smoothness", smoothness);
            SetPropertyFloat(material, "_Glossiness", smoothness);
            SetPropertyFloat(material, "_Metallic", 0.05f);

            if (emission.HasValue)
            {
                SetPropertyColor(material, "_EmissionColor", emission.Value);
                material.EnableKeyword("_EMISSION");
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }

            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        /// <summary>
        /// Additive, unlit, double sided, no depth write: the swing afterimage and sweep edge. Re-applied on every build so
        /// the transparent state is always right (a material that only exists as a runtime fallback would be opaque).
        /// </summary>
        private static Material CreateAdditive(string name, Color color)
        {
            string path = EmberdelvePaths.Materials + "/" + name + ".mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool created = material == null;
            if (created)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Sprites/Default");
                if (shader == null)
                {
                    Debug.LogError("[Emberdelve] No usable shader found; material '" + name + "' was skipped.");
                    return null;
                }
                material = new Material(shader) { name = name };
            }

            SetPropertyColor(material, "_BaseColor", color);
            SetPropertyColor(material, "_Color", color);
            SetPropertyFloat(material, "_Surface", 1f);
            SetPropertyFloat(material, "_Blend", 2f);
            SetPropertyFloat(material, "_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            SetPropertyFloat(material, "_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            SetPropertyFloat(material, "_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            SetPropertyFloat(material, "_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            SetPropertyFloat(material, "_ZWrite", 0f);
            SetPropertyFloat(material, "_Cull", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            material.SetShaderPassEnabled("ShadowCaster", false);

            if (created) AssetDatabase.CreateAsset(material, path);
            else EditorUtility.SetDirty(material);
            return material;
        }

        private static void SetPropertyColor(Material material, string name, Color value)
        {
            if (material.HasProperty(name)) material.SetColor(name, value);
        }

        private static void SetPropertyTexture(Material material, string name, Texture value)
        {
            if (material.HasProperty(name)) material.SetTexture(name, value);
        }

        private static void SetPropertyFloat(Material material, string name, float value)
        {
            if (material.HasProperty(name)) material.SetFloat(name, value);
        }
    }
}
