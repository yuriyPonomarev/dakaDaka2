using Emberdelve.Core;
using Emberdelve.Level;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    /// <summary>Saved destination architecture. The four doorway axes remain on a continuous floor.</summary>
    internal static class RoyaleRoomArchitecture
    {
        internal static void Build(Transform root, int floor, DungeonModuleKind kind, int variant, Material stone, Material paving)
        {
            var metal = AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Materials/Blade_Metal.mat");
            if (floor == 2) Crypt(root, kind, variant, stone, paving, metal);
            else Infernal(root, kind, variant, stone, paving, metal);
        }

        private static void Crypt(Transform root, DungeonModuleKind kind, int variant, Material stone, Material paving, Material metal)
        {
            // Burial galleries flank a broad nave; overhead ribs make an enclosed silhouette
            // without the outdoor terraces and broken-wall shapes of the first floor.
            for (int z = -8; z <= 8; z += 8)
            {
                foreach (int side in new[] { -1, 1 })
                {
                    // Keep the east/west doorway axis free.
                    if (z != 0) CryptRoomArchitecture.Column(root, new Vector3(side * 4.5f, 0, z), 7.3f, stone);
                }
                Box(root, "Nave_Vault_Rib", new Vector3(0, 7.55f, z), new Vector3(DungeonModulePlan.ModuleWidthCells * 2f - .4f, .6f, .7f), stone);
            }
            foreach (int x in new[] { -1, 1 }) foreach (int z in new[] { -1, 1 })
            {
                var burial = new Vector3(x * 8f, 0, z * 7.4f);
                CryptRoomArchitecture.Tomb(root, burial, variant % 2 == 0 ? 0 : 90, stone, paving);
                // Tall alcoves create cover and separate side chambers from the central nave.
                Box(root, "Burial_Alcove_Back", burial + new Vector3(0, 2.4f, z * 2.1f), new Vector3(4.8f, 4.8f, .5f), stone);
                Box(root, "Burial_Alcove_Canopy", burial + Vector3.up * 4.65f, new Vector3(4.8f, .4f, 4.3f), paving);
                if (kind == DungeonModuleKind.Ossuary || kind == DungeonModuleKind.Vault)
                    Box(root, "Burial_Gallery_Screen", new Vector3(x * 5.7f, 1.8f, z * 7.5f), new Vector3(.6f, 3.6f, 4), stone);
            }
            foreach (int side in new[] { -1, 1 })
                CryptRoomArchitecture.Brazier(root, new Vector3(side * 8.4f, 0, -side * 3.5f), stone, metal, true);
            if (kind == DungeonModuleKind.Shrine || kind == DungeonModuleKind.Extraction)
            {
                Box(root, "Sepulchral_Altar", new Vector3(-7, .8f, 3.8f), new Vector3(3, 1.6f, 1.5f), paving);
                Box(root, "Altar_Reredos", new Vector3(-7, 2.6f, 5), new Vector3(3.4f, 5.2f, .5f), stone);
            }
        }

        private static void Infernal(Transform root, DungeonModuleKind kind, int variant, Material stone, Material paving, Material metal)
        {
            // Four massive furnace bastions leave a cruciform combat route. Diagonal masonry,
            // iron teeth and glowing crucibles replace the crypt's rows of burial niches.
            foreach (int x in new[] { -1, 1 }) foreach (int z in new[] { -1, 1 })
            {
                var center = new Vector3(x * 8.2f, 0, z * 8.2f);
                Box(root, "Furnace_Bastion", center + Vector3.up * 1.2f, new Vector3(4.7f, 2.4f, 4.7f), stone, 45);
                Box(root, "Furnace_Crown", center + Vector3.up * 2.5f, new Vector3(4.3f, .35f, 4.3f), metal, 45);
                for (int tooth = -1; tooth <= 1; tooth++)
                    Box(root, "Iron_Furnace_Tooth", center + new Vector3(tooth * 1.1f, 3.3f, z * 1.6f), new Vector3(.25f, 1.6f, .35f), metal);
                CryptRoomArchitecture.Brazier(root, center + new Vector3(0, 2.7f, 0), stone, metal, false);
                Box(root, "Oblique_Vault_Brace", new Vector3(x * 6.8f, 7.7f, z * 6.8f), new Vector3(9, .9f, 1.2f), stone, -x * z * 45);
            }
            foreach (int side in new[] { -1, 1 })
            {
                Box(root, "Iron_Ritual_Pylon", new Vector3(side * 4.5f, 2.6f, side * 4.5f), new Vector3(1.2f, 5.2f, 1.2f), metal, 45);
                if (kind == DungeonModuleKind.Bridge || kind == DungeonModuleKind.Hall && variant == 1)
                    Box(root, "Furnace_Wing", new Vector3(side * 8f, 1.8f, -side * 4f), new Vector3(4.5f, 3.6f, .8f), stone);
            }
            if (kind == DungeonModuleKind.Shrine || kind == DungeonModuleKind.Extraction)
                Box(root, "Sacrificial_Anvil", new Vector3(-6, 1, 4.5f), new Vector3(3, 2, 1.6f), metal, 25);
        }

        private static void Box(Transform root, string name, Vector3 position, Vector3 scale, Material material, float yaw = 0)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name; go.layer = LayerMask.NameToLayer(GameConfig.LevelLayerName);
            go.transform.SetParent(root, false); go.transform.localPosition = position;
            go.transform.localRotation = Quaternion.Euler(0, yaw, 0); go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = material;
        }
    }
}
