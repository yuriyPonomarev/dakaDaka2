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

        public static void BuildAll()
        {
            StoneFloor = Create("Stone_Floor", new Color(0.155f, 0.148f, 0.138f), 0.22f, null);
            StoneWall = Create("Stone_Wall", new Color(0.205f, 0.196f, 0.18f), 0.18f, null);
            StoneCeiling = Create("Stone_Ceiling", new Color(0.115f, 0.11f, 0.105f), 0.15f, null);
            LobbyFloor = Create("Lobby_StoneFloor", new Color(0.13f, 0.125f, 0.12f), 0.3f, null);

            BowWood = Create("Bow_Wood", new Color(0.32f, 0.2f, 0.1f), 0.4f, null);
            Blade = Create("Blade_Metal", new Color(0.7f, 0.72f, 0.76f), 0.78f, null);
            Grip = Create("Grip_Leather", new Color(0.17f, 0.11f, 0.07f), 0.3f, null);

            EnemyBody = Create("Enemy_Body", new Color(0.23f, 0.22f, 0.26f), 0.25f, null);
            EnemyHead = Create("Enemy_Head", new Color(0.42f, 0.37f, 0.32f), 0.3f, new Color(0.12f, 0.03f, 0.02f));
            EliteBody = Create("Enemy_EliteBody", new Color(0.34f, 0.15f, 0.13f), 0.3f, new Color(0.18f, 0.04f, 0.02f));

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

        private static void SetPropertyColor(Material material, string name, Color value)
        {
            if (material.HasProperty(name)) material.SetColor(name, value);
        }

        private static void SetPropertyFloat(Material material, string name, float value)
        {
            if (material.HasProperty(name)) material.SetFloat(name, value);
        }
    }
}
