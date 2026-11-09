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
            if (kind == DungeonModuleKind.Vault)
                Arch(root, arch, new Vector3(0, 2.7f, 0), 10.5f, 2.8f, 23.2f, 0, stone);
            Material wood = AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Materials/Bow_Wood.mat");
            Material metal = AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Materials/Blade_Metal.mat");
            // Four doorway axes stay on ground level. Elevated corners have broad diagonal stairs.
            bool high = kind == DungeonModuleKind.Shrine || kind == DungeonModuleKind.Bridge;
            Terrace(root, new Vector3(3,0,3), 45, high ? 3.6f : 1.8f, stone, floor);
            if(kind != DungeonModuleKind.Entry && kind != DungeonModuleKind.Extraction)
                Terrace(root, new Vector3(-3,0,-3), 225, 1.8f, stone, floor);
            Tomb(root,new Vector3(-7,0,7), variant%2==0?15:75,stone,floor);
            Tomb(root,new Vector3(7,0,-7),-20,stone,floor);
            for(int side=-1;side<=1;side+=2)
            {
                Column(root,new Vector3(side*10.5f,0,-side*9.5f),4.5f,stone);
                Column(root,new Vector3(side*9f,0,-side*10.5f),2.1f,stone);
                Arch(root,arch,new Vector3(side*7f,2.6f,-side*7f),2f,1.7f,.55f,side*20,stone);
                Brazier(root,new Vector3(side*4.8f,0,-side*4.8f),stone,metal,false);
                Urn(root,new Vector3(side*8.5f,0,-side*5.5f),floor);
            }
            if(kind == DungeonModuleKind.Shrine)
            {
                Transform altar=Group(root,"Ruined_Altar",new Vector3(-7,0,7),0);
                Part(altar,"Altar_Table",new Vector3(0,1.1f,0),new Vector3(3,.35f,1.4f),stone);
            }
            if(kind == DungeonModuleKind.Extraction)
                Arch(root,arch,new Vector3(0,3.2f,4.5f),3.2f,2.2f,.9f,0,stone);
            MoonlitRuinsContent.Dress(root, kind, variant, wood, stone);
        }

        private static void Terrace(Transform root, Vector3 start, float yaw, float height, Material stone, Material floor)
        {
            Transform terrace=Group(root,"Terrace_"+yaw,start,yaw);
            int steps=Mathf.RoundToInt(height/.15f);
            const float run=.30f, width=3.6f;
            for(int i=0;i<steps;i++)
            {
                float rise=(i+1)*.15f;
                Part(terrace,"Stair_"+i,new Vector3(0,rise*.5f,(i+.5f)*run),new Vector3(width,rise,run+.01f),stone);
            }
            float length=steps*run;
            float depth=height>2?3f:4f;
            Part(terrace,"Upper_Deck",new Vector3(0,height*.5f,length+depth*.5f),new Vector3(4,height,depth),floor);
            for(int side=-1;side<=1;side+=2)
            {
                Part(terrace,"Parapet",new Vector3(side*1.95f,height+.5f,length+depth*.5f),new Vector3(.22f,1,depth),stone);
                Column(terrace,new Vector3(side*1.8f,height,length+depth-.3f),1.45f,stone);
            }
            Part(terrace,"Rear_Parapet",new Vector3(0,height+.5f,length+depth-.12f),new Vector3(4,1,.24f),stone);
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
