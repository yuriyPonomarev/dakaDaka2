using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Level;

namespace Emberdelve.EditorTools
{
    // Authored in module-local coordinates. Door axes stay clear; all cover has matching collision.
    internal static class CryptRoomArchitecture
    {
        private const string ArchPath = "Assets/Emberdelve/Art/Generated/CryptArch.asset";

        internal static void Build(Transform root, DungeonModuleKind kind, int variant, Material stone, Material floor)
        {
            Mesh arch = ArchMesh();
            Arch(root, arch, new Vector3(0, 2.7f, 0), 10.5f, 2.8f, 23.2f, 0, stone);
            Transform fill = Group(root, "Vault_Reflected_Light", new Vector3(0, 4.4f, 0), 0);
            Light ambient = fill.gameObject.AddComponent<Light>();
            ambient.type = LightType.Point; ambient.range = 24f; ambient.intensity = 24f;
            ambient.cullingMask = LayerMask.GetMask(GameConfig.LevelLayerName);
            ambient.color = new Color(.57f, .66f, .8f); ambient.shadows = LightShadows.None;
            Material wood = AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Materials/Bow_Wood.mat");
            Material metal = AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Materials/Blade_Metal.mat");
            for (int side = -1; side <= 1; side += 2)
            {
                for (int end = -1; end <= 1; end += 2)
                {
                    Part(root, "Corner_Buttress", new Vector3(side * 10.1f, 3f, end * 10.1f), new Vector3(5f, 6f, .85f), stone, side * end * 45f);
                    Column(root, new Vector3(side * 8.5f, 0, end * 7f), 3.1f, stone);
                }
                Arch(root, arch, new Vector3(0, 3.1f, side * 7f), 8.5f, 2.4f, .6f, 0, stone);
                // Side-wall burial bays make the outer silhouette readable without obstructing door landings.
                for (int bay = -1; bay <= 1; bay += 2)
                {
                    Transform niche = Group(root, "Burial_Alcove", new Vector3(side * 10.8f, 0, bay * 5.5f), side * 90f);
                    Part(niche, "Recess_Shadow", new Vector3(0, 1.3f, .2f), new Vector3(3.4f, 2.6f, .25f), floor);
                    Arch(niche, arch, new Vector3(0, 1.8f, -.3f), 1.6f, 1.15f, .5f, 0, stone);
                    for (int edge = -1; edge <= 1; edge += 2)
                        Part(niche, "Niche_Jamb", new Vector3(edge * 1.65f, .9f, -.3f), new Vector3(.35f, 1.8f, .55f), stone);
                    Tomb(niche, new Vector3(0, 0, -.65f), 90f, stone, floor);
                }
                Brazier(root, new Vector3(side * 7.5f, 0, -7.5f), stone, metal, kind == DungeonModuleKind.Extraction);
            }
            // Curved lintels cover the square opening tops; the existing door and closure contract remains intact.
            for (int side = 0; side < 4; side++)
            {
                Quaternion turn = Quaternion.Euler(0, side * 90f, 0);
                Arch(root, arch, turn * new Vector3(0, 2.5f, 11.65f), 2f, 1.5f, .85f, side * 90f, stone);
            }

            switch (kind)
            {
                case DungeonModuleKind.Entry:
                    for (int side = -1; side <= 1; side += 2)
                    {
                        Tomb(root, new Vector3(side * 5f, 0, 2.5f), 0, stone, floor);
                        Column(root, new Vector3(side * 5f, 0, -5f), 3.2f, stone);
                    }
                    break;
                case DungeonModuleKind.Hall:
                    if (variant == 0)
                    {
                        for (int z = -6; z <= 6; z += 6)
                        {
                            Column(root, new Vector3(-4.5f, 0, z), 3.2f, stone);
                            Column(root, new Vector3(4.5f, 0, z), 3.2f, stone);
                            Arch(root, arch, new Vector3(0, 3.2f, z), 4.5f, 2.1f, .65f, 0, stone);
                        }
                    }
                    else if (variant == 1) BurialRows(root, stone, floor, false);
                    else
                    {
                        for (int side = -1; side <= 1; side += 2)
                            for (int z = -6; z <= 6; z += 12)
                            {
                                Transform shelf = Group(root, "Reliquary_Shelves", new Vector3(side * 6.5f, 0, z), 0);
                                for (int tier = 0; tier < 3; tier++)
                                {
                                    Part(shelf, "Shelf", new Vector3(0, .4f + tier * .85f, 0), new Vector3(3f, .16f, .85f), wood);
                                    for (int jar = -1; jar <= 1; jar++) Urn(shelf, new Vector3(jar * .85f, .5f + tier * .85f, 0), floor);
                                }
                                for (int edge = -1; edge <= 1; edge += 2)
                                    Part(shelf, "Upright", new Vector3(edge * 1.45f, 1.2f, 0), new Vector3(.18f, 2.4f, .85f), wood);
                            }
                    }
                    break;
                case DungeonModuleKind.Ossuary:
                    BurialRows(root, stone, floor, variant == 1);
                    break;
                case DungeonModuleKind.Shrine:
                    for (int z = -5; z <= 3; z += 4)
                        for (int side = -1; side <= 1; side += 2)
                        {
                            Part(root, "Chapel_Pew_Seat", new Vector3(side * 4.8f, .55f, z), new Vector3(4f, .22f, .8f), wood);
                            Part(root, "Chapel_Pew_Back", new Vector3(side * 4.8f, 1f, z - .38f), new Vector3(4f, .9f, .16f), wood);
                            for (int leg = -1; leg <= 1; leg += 2)
                                Part(root, "Pew_Foot", new Vector3(side * 4.8f + leg * 1.5f, .25f, z), new Vector3(.22f, .5f, .7f), wood);
                        }
                    Tomb(root, new Vector3(4.5f, 0, 7.5f), 90f, stone, floor);
                    Arch(root, arch, new Vector3(0, 3.1f, 7f), 4f, 2.5f, .8f, 0, stone);
                    break;
                case DungeonModuleKind.Vault:
                    for (int x = -1; x <= 1; x += 2)
                        for (int z = -1; z <= 1; z += 2)
                        {
                            Column(root, new Vector3(x * 4.5f, 0, z * 4.5f), 4.6f, stone);
                            Tomb(root, new Vector3(x * 6.5f, 0, z * 6.5f), x * z * 45f, stone, floor);
                        }
                    // Low central seal is crossed normally; columns offer four genuine flanking routes.
                    Cylinder(root, "Vault_Seal", new Vector3(0, .12f, 0), new Vector3(5f, .12f, 5f), floor);
                    break;
                case DungeonModuleKind.Bridge:
                    Part(root, "Skybridge_Deck", new Vector3(0, .12f, 0), new Vector3(8f, .24f, 23f), stone);
                    Part(root, "Skybridge_Crown", new Vector3(0, .38f, 0), new Vector3(2f, .18f, 21f), floor);
                    for (int side = -1; side <= 1; side += 2)
                    {
                        Part(root, "Skybridge_Parapet", new Vector3(side * 4f, .75f, 0), new Vector3(.35f, 1.25f, 21f), stone);
                        for (int z = -7; z <= 7; z += 7)
                        {
                            Column(root, new Vector3(side * 4f, 0, z), 3.2f, stone);
                            if (side == 1) Arch(root, arch, new Vector3(0, 3.2f, z), 4f, 2.2f, .7f, 0, stone);
                        }
                    }
                    break;
                case DungeonModuleKind.Extraction:
                    for (int side = -1; side <= 1; side += 2)
                    {
                        Column(root, new Vector3(side * 3.2f, 0, 4.5f), 3.2f, stone);
                        Tomb(root, new Vector3(side * 6f, 0, 4f), 0, stone, floor);
                    }
                    Arch(root, arch, new Vector3(0, 3.2f, 4.5f), 3.2f, 2.2f, .9f, 0, stone);
                    break;
            }
        }

        private static void BurialRows(Transform root, Material stone, Material floor, bool divided)
        {
            for (int side = -1; side <= 1; side += 2)
                for (int z = -5; z <= 5; z += 10)
                {
                    Tomb(root, new Vector3(side * 5.2f, 0, z), divided ? 90f : 0f, stone, floor);
                    if (divided) Part(root, "Burial_Screen", new Vector3(side * 6f, 1.6f, z + 2f), new Vector3(5f, 3.2f, .5f), stone);
                    else Column(root, new Vector3(side * 4f, 0, z), 3f, stone);
                }
        }

        private static void Tomb(Transform root, Vector3 position, float yaw, Material stone, Material trim)
        {
            Transform tomb = Group(root, "Sarcophagus", position, yaw);
            Part(tomb, "Footing", new Vector3(0, .12f, 0), new Vector3(1.8f, .24f, 3.2f), trim);
            Part(tomb, "Casket", new Vector3(0, .65f, 0), new Vector3(1.5f, 1.05f, 2.85f), stone);
            Part(tomb, "Lid", new Vector3(0, 1.23f, 0), new Vector3(1.7f, .2f, 3f), trim);
            Part(tomb, "Effigy", new Vector3(0, 1.39f, 0), new Vector3(.55f, .18f, 1.75f), stone);
            Cylinder(tomb, "Head_Relief", new Vector3(0, 1.43f, .95f), new Vector3(.46f, .08f, .46f), stone);
        }

        private static void Column(Transform root, Vector3 position, float height, Material material)
        {
            Part(root, "Column_Base", position + Vector3.up * .16f, new Vector3(1.1f, .32f, 1.1f), material);
            Cylinder(root, "Column_Shaft", position + Vector3.up * (height * .5f), new Vector3(.7f, height * .5f, .7f), material);
            Part(root, "Column_Capital", position + Vector3.up * (height - .12f), new Vector3(1.2f, .24f, 1.2f), material);
        }

        private static void Urn(Transform root, Vector3 position, Material material)
        {
            Cylinder(root, "Funerary_Urn", position + Vector3.up * .23f, new Vector3(.42f, .23f, .42f), material);
            Cylinder(root, "Urn_Lip", position + Vector3.up * .48f, new Vector3(.48f, .035f, .48f), material);
        }

        private static void Brazier(Transform root, Vector3 position, Material stone, Material metal, bool blue)
        {
            Column(root, position, 1.3f, stone);
            Cylinder(root, "Brazier_Bowl", position + Vector3.up * 1.4f, new Vector3(.85f, .13f, .85f), metal);
            Transform lamp = Group(root, "Brazier_Light", position + Vector3.up * 1.8f, 0);
            Light light = lamp.gameObject.AddComponent<Light>();
            light.type = LightType.Point; light.range = 14f; light.intensity = 12f;
            light.cullingMask = LayerMask.GetMask(GameConfig.LevelLayerName);
            light.color = blue ? new Color(.3f, .65f, 1f) : new Color(1f, .49f, .19f);
            light.shadows = LightShadows.None;
            string path = "Assets/Emberdelve/Art/Generated/CryptFlame_" + (blue ? "Blue" : "Amber") + ".mat";
            Material flameMaterial = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (flameMaterial == null)
            {
                flameMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                flameMaterial.SetColor("_BaseColor", blue ? new Color(.3f, 1.1f, 2f) : new Color(2.5f, .8f, .08f));
                AssetDatabase.CreateAsset(flameMaterial, path);
            }
            GameObject flame = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            flame.name = "Brazier_Flame"; flame.transform.SetParent(root, false);
            flame.transform.localPosition = position + Vector3.up * 1.8f;
            flame.transform.localScale = new Vector3(.28f, .65f, .28f);
            Object.DestroyImmediate(flame.GetComponent<Collider>());
            flame.GetComponent<Renderer>().sharedMaterial = flameMaterial;
        }

        private static Transform Group(Transform parent, string name, Vector3 position, float yaw)
        {
            Transform t = new GameObject(name).transform;
            t.SetParent(parent, false); t.localPosition = position; t.localRotation = Quaternion.Euler(0, yaw, 0);
            return t;
        }

        private static GameObject Part(Transform parent, string name, Vector3 position, Vector3 size, Material material, float yaw = 0)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name; go.layer = LayerMask.NameToLayer(GameConfig.LevelLayerName);
            go.transform.SetParent(parent, false); go.transform.localPosition = position;
            go.transform.localRotation = Quaternion.Euler(0, yaw, 0); go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = material;
            return go;
        }

        private static void Cylinder(Transform parent, string name, Vector3 position, Vector3 size, Material material)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name; go.layer = LayerMask.NameToLayer(GameConfig.LevelLayerName);
            go.transform.SetParent(parent, false); go.transform.localPosition = position; go.transform.localScale = size;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.AddComponent<MeshCollider>().sharedMesh = go.GetComponent<MeshFilter>().sharedMesh;
            go.GetComponent<Renderer>().sharedMaterial = material;
        }

        private static void Arch(Transform root, Mesh mesh, Vector3 position, float radius, float rise, float depth, float yaw, Material material)
        {
            Transform t = Group(root, "Stone_Arch", position, yaw);
            t.gameObject.layer = LayerMask.NameToLayer(GameConfig.LevelLayerName);
            t.localScale = new Vector3(radius, rise, depth);
            t.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            t.gameObject.AddComponent<MeshRenderer>().sharedMaterial = material;
            t.gameObject.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        private static Mesh ArchMesh()
        {
            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(ArchPath);
            if (existing != null) return existing;
            var vertices = new List<Vector3>(); var triangles = new List<int>(); var uv = new List<Vector2>();
            const int segments = 20;
            for (int i = 0; i < segments; i++)
            {
                float a = Mathf.PI * i / segments, b = Mathf.PI * (i + 1) / segments;
                Vector3[] ring = new Vector3[8];
                for (int end = 0; end < 2; end++)
                    for (int edge = 0; edge < 2; edge++)
                        for (int face = 0; face < 2; face++)
                        {
                            float angle = end == 0 ? a : b, r = edge == 0 ? 1f : 1.12f;
                            ring[end * 4 + edge * 2 + face] = new Vector3(Mathf.Cos(angle) * r, Mathf.Sin(angle) * r, face == 0 ? -.5f : .5f);
                        }
                int[,] quads = { {0,4,6,2}, {1,3,7,5}, {0,1,5,4}, {2,6,7,3}, {0,2,3,1}, {4,5,7,6} };
                for (int q = 0; q < 6; q++)
                {
                    int start = vertices.Count;
                    for (int k = 0; k < 4; k++) { vertices.Add(ring[quads[q,k]]); uv.Add(new Vector2(k == 1 || k == 2 ? 1 : 0, k >= 2 ? 1 : 0)); }
                    triangles.AddRange(new[] { start, start+1, start+2, start, start+2, start+3 });
                }
            }
            Mesh mesh = new Mesh { name = "Crypt stone arch" };
            mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0); mesh.SetUVs(0, uv); mesh.RecalculateNormals(); mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, ArchPath);
            return mesh;
        }
    }
}
