using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Level;

namespace Emberdelve.EditorTools
{
    /// <summary>Creates saved, reusable crypt room prefabs and their stable Resources catalog.</summary>
    internal static class DungeonModuleContentBuilder
    {
        private const string PrefabFolder = "Assets/Emberdelve/Prefabs/DungeonModules";
        private const string CatalogFolder = "Assets/Emberdelve/Resources/Emberdelve";
        private const float Half = 12f; // 12 cells * existing 2-unit DungeonLayoutData cell size.
        private const float DoorHalf = 2f;
        private const float WallHeight = 4f;

        private sealed class Spec
        {
            public string Id;
            public string Name;
            public DungeonModuleKind Kind;
            public int Variant;
            public Spec(string id, string name, DungeonModuleKind kind, int variant) { Id=id; Name=name; Kind=kind; Variant=variant; }
        }

        // Ordering is a wire/catalog contract. Do not reorder without a protocol version bump.
        private static readonly Spec[] Specs =
        {
            new Spec("crypt_entry_01", "Crypt_Entry", DungeonModuleKind.Entry, 0),
            new Spec("crypt_hall_01", "Crypt_Hall_01", DungeonModuleKind.Hall, 0),
            new Spec("crypt_hall_02", "Crypt_Hall_02", DungeonModuleKind.Hall, 1),
            new Spec("crypt_hall_03", "Crypt_Hall_03", DungeonModuleKind.Hall, 2),
            new Spec("crypt_ossuary_01", "Crypt_Ossuary_01", DungeonModuleKind.Ossuary, 0),
            new Spec("crypt_ossuary_02", "Crypt_Ossuary_02", DungeonModuleKind.Ossuary, 1),
            new Spec("crypt_shrine_01", "Crypt_Shrine", DungeonModuleKind.Shrine, 0),
            new Spec("crypt_vault_01", "Crypt_Vault", DungeonModuleKind.Vault, 0),
            new Spec("crypt_extraction_01", "Crypt_Extraction", DungeonModuleKind.Extraction, 0),
            // Appended to preserve all existing module indexes in the network catalog contract.
            new Spec("crypt_skybridge_01", "Crypt_Skybridge", DungeonModuleKind.Bridge, 0)
        };

        [MenuItem("Emberdelve/Build Dungeon Module Catalog (10 Prefabs)")]
        public static void BuildCatalog()
        {
            EnsureFolder("Assets/Emberdelve/Prefabs", "DungeonModules");
            EnsureFolder("Assets/Emberdelve/Resources", "Emberdelve");
            Material wall = AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Materials/Stone_Wall.mat");
            Material floor = AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Materials/Stone_Floor.mat");
            Material ceiling = AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Materials/Stone_Ceiling.mat");
            if (wall == null || floor == null || ceiling == null)
                throw new InvalidOperationException("Crypt module builder needs the existing Stone_Wall, Stone_Floor and Stone_Ceiling materials.");

            List<DungeonModuleDefinition> definitions = new List<DungeonModuleDefinition>(Specs.Length);
            foreach (Spec spec in Specs)
            {
                GameObject root = CreateModule(spec, wall, floor, ceiling);
                string path = PrefabFolder + "/" + spec.Name + ".prefab";
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
                UnityEngine.Object.DestroyImmediate(root);
                if (prefab == null) throw new InvalidOperationException("Prefab save failed: " + path);
                DungeonModuleDefinition definition = prefab.GetComponent<DungeonModuleDefinition>();
                if (definition == null) throw new InvalidOperationException("Generated prefab is missing DungeonModuleDefinition: " + spec.Id);
                if (!definition.HasValidSockets(out string issue))
                    throw new InvalidOperationException("Generated module failed validation: " + spec.Id + " " + issue);
                definitions.Add(definition);
            }

            string catalogPath = CatalogFolder + "/DungeonModuleCatalog.asset";
            DungeonModuleCatalog catalog = AssetDatabase.LoadAssetAtPath<DungeonModuleCatalog>(catalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<DungeonModuleCatalog>();
                AssetDatabase.CreateAsset(catalog, catalogPath);
            }
            SerializedObject serialized = new SerializedObject(catalog);
            SerializedProperty array = serialized.FindProperty("modules");
            array.arraySize = definitions.Count;
            for (int i = 0; i < definitions.Count; i++) array.GetArrayElementAtIndex(i).objectReferenceValue = definitions[i];
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("Built 10 crypt module prefabs and Resources/Emberdelve/DungeonModuleCatalog in stable catalog order.");
        }

        private static GameObject CreateModule(Spec spec, Material wall, Material floor, Material ceiling)
        {
            GameObject root = new GameObject(spec.Name);
            DungeonModuleDefinition definition = root.AddComponent<DungeonModuleDefinition>();
            Transform spawn = Child(root.transform, "PlayerSpawn", Vector3.zero);
            SerializedObject so = new SerializedObject(definition);
            so.FindProperty("moduleId").stringValue = spec.Id;
            so.FindProperty("kind").enumValueIndex = (int)spec.Kind;
            so.FindProperty("availableSockets").intValue = (int)DungeonModuleSockets.All;
            so.FindProperty("widthCells").intValue = 12;
            so.FindProperty("heightCells").intValue = 12;
            so.FindProperty("playerSpawnAnchor").objectReferenceValue = spawn;
            Transform[] sockets = new Transform[4];
            string[] names = { "North", "East", "South", "West" };
            Vector3[] positions = { new Vector3(0,0,Half), new Vector3(Half,0,0), new Vector3(0,0,-Half), new Vector3(-Half,0,0) };
            string[] fields = { "northSocket", "eastSocket", "southSocket", "westSocket" };
            for (int i = 0; i < 4; i++)
            {
                sockets[i] = Child(root.transform, "Socket_" + names[i], positions[i]);
                so.FindProperty(fields[i]).objectReferenceValue = sockets[i];
            }
            so.ApplyModifiedPropertiesWithoutUndo();

            Box(root.transform, "Floor", new Vector3(0,-0.3f,0), new Vector3(Half*2,0.6f,Half*2), floor);
            if (spec.Kind != DungeonModuleKind.Bridge)
                Box(root.transform, "Roof", new Vector3(0,WallHeight+0.3f,0), new Vector3(Half*2,0.6f,Half*2), ceiling);
            BuildSide(root.transform, true, false, wall);  // north
            BuildSide(root.transform, true, true, wall);   // south
            BuildSide(root.transform, false, false, wall); // east
            BuildSide(root.transform, false, true, wall);  // west
            for (int i = 0; i < 4; i++)
            {
                bool horizontal = i == 0 || i == 2;
                Vector3 closurePos = positions[i] + Vector3.up * (WallHeight * .5f);
                Vector3 size = horizontal ? new Vector3(DoorHalf*2, WallHeight, .65f) : new Vector3(.65f, WallHeight, DoorHalf*2);
                Box(root.transform, "Closure_" + names[i], closurePos, size, wall);
                // Stone jambs and lintel visually frame each doorway even while the closure is active.
                Vector3 axis = horizontal ? Vector3.right : Vector3.forward;
                Box(root.transform, "Arch_Jamb_"+names[i]+"_A", positions[i] + axis * -DoorHalf + Vector3.up*2f, new Vector3(horizontal ? .55f : .7f,4.3f,horizontal ? .7f : .55f), wall);
                Box(root.transform, "Arch_Jamb_"+names[i]+"_B", positions[i] + axis * DoorHalf + Vector3.up*2f, new Vector3(horizontal ? .55f : .7f,4.3f,horizontal ? .7f : .55f), wall);
                Box(root.transform, "Arch_Lintel_"+names[i], positions[i]+Vector3.up*4f, horizontal ? new Vector3(4.6f,.55f,.8f) : new Vector3(.8f,.55f,4.6f), wall);
            }
            AddRoomFeature(root.transform, spec, wall, floor);
            return root;
        }

        private static void BuildSide(Transform root, bool horizontal, bool negative, Material material)
        {
            float edge = negative ? -Half : Half;
            float span = Half - DoorHalf;
            float center = negative ? -(Half + DoorHalf)*.5f : (Half + DoorHalf)*.5f;
            Vector3 firstPos = horizontal ? new Vector3(center,WallHeight*.5f,edge) : new Vector3(edge,WallHeight*.5f,center);
            Vector3 secondPos = horizontal ? new Vector3(-center,WallHeight*.5f,edge) : new Vector3(edge,WallHeight*.5f,-center);
            Vector3 size = horizontal ? new Vector3(span,WallHeight,.7f) : new Vector3(.7f,WallHeight,span);
            Box(root,"WallRun_"+(horizontal ? "H" : "V")+"_"+(negative ? "N" : "P")+"_A",firstPos,size,material);
            Box(root,"WallRun_"+(horizontal ? "H" : "V")+"_"+(negative ? "N" : "P")+"_B",secondPos,size,material);
        }

        private static void AddRoomFeature(Transform root, Spec spec, Material stone, Material floor)
        {
            switch (spec.Kind)
            {
                case DungeonModuleKind.Entry:
                    Box(root,"Entry_Sarcophagus",new Vector3(0,.8f,3.5f),new Vector3(3.5f,1.6f,6f),stone);
                    break;
                case DungeonModuleKind.Ossuary:
                    for(int side=-1;side<=1;side+=2)
                    {
                        Box(root,"Ossuary_Niche_"+side,new Vector3(side*7f,1.5f,0),new Vector3(2f,3f,5.2f),stone);
                        for(int n=-1;n<=1;n++) Box(root,"Ossuary_Bone_"+side+"_"+n,new Vector3(side*7f,1.55f,n*1.25f),new Vector3(1.7f,.24f,.34f),floor);
                    }
                    Box(root,"Ossuary_Altar",new Vector3(0,.65f,0),new Vector3(2.2f,1.3f,2.2f),stone);
                    break;
                case DungeonModuleKind.Shrine:
                    Box(root,"Shrine_Dais",new Vector3(0,.35f,0),new Vector3(5f,.7f,5f),stone);
                    Box(root,"Shrine_Stela",new Vector3(0,2.1f,0),new Vector3(1.3f,3.5f,.55f),stone);
                    break;
                case DungeonModuleKind.Vault:
                    Box(root,"Vault_Plinth",new Vector3(0,.5f,0),new Vector3(4f,1f,4f),stone);
                    Box(root,"Vault_Seal",new Vector3(0,1.5f,0),new Vector3(2f,1.4f,2f),floor);
                    break;
                case DungeonModuleKind.Extraction:
                    Box(root,"Extraction_PortalBase",new Vector3(0,.3f,0),new Vector3(7f,.6f,7f),stone);
                    Box(root,"Extraction_EmberAltar",new Vector3(0,1.2f,0),new Vector3(2.5f,1.8f,2.5f),stone);
                    break;
                case DungeonModuleKind.Bridge:
                    // The room has a continuous safe floor for the legacy grid mask. This broad bridge
                    // spans it at grade, so every planned doorway has a physical, collider-backed path.
                    Box(root,"Skybridge_Deck",new Vector3(0,.12f,0),new Vector3(8f,.24f,23f),stone);
                    for (int side=-1;side<=1;side+=2)
                    {
                        Box(root,"Skybridge_Parapet_"+side,new Vector3(side*4f,.75f,0),new Vector3(.35f,1.25f,21f),stone);
                        for (int post=-1;post<=1;post++)
                            Box(root,"Skybridge_Pier_"+side+"_"+post,new Vector3(side*4f,1.5f,post*7f),new Vector3(.8f,3f,.8f),stone);
                    }
                    Box(root,"Skybridge_Crown",new Vector3(0,.38f,0),new Vector3(2f,.18f,21f),floor);
                    break;
                default:
                    // Hall variants change the central landmark and side reliquary for a visibly different room silhouette.
                    if (spec.Variant == 0) Box(root,"Hall_CentralPillar",new Vector3(0,2f,0),new Vector3(1.5f,4f,1.5f),stone);
                    if (spec.Variant == 1) Box(root,"Hall_BrokenTomb",new Vector3(0,.7f,1.5f),new Vector3(5f,1.4f,2f),stone);
                    if (spec.Variant == 2) { Box(root,"Hall_Obelisk",new Vector3(-3,2f,1),new Vector3(1.2f,4f,1.2f),stone); Box(root,"Hall_Reliquary",new Vector3(3,.8f,-1),new Vector3(2.8f,1.6f,2.8f),stone); }
                    break;
            }
        }

        private static GameObject Box(Transform parent, string name, Vector3 position, Vector3 size, Material material)
        {
            int levelLayer = LayerMask.NameToLayer(GameConfig.LevelLayerName);
            if (levelLayer < 0) throw new InvalidOperationException("Missing level collision layer: " + GameConfig.LevelLayerName);
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.layer = levelLayer;
            go.name = name; go.transform.SetParent(parent,false); go.transform.localPosition=position; go.transform.localScale=size;
            go.GetComponent<Renderer>().sharedMaterial=material;
            return go;
        }

        private static Transform Child(Transform parent, string name, Vector3 localPosition)
        {
            GameObject child = new GameObject(name); child.transform.SetParent(parent,false); child.transform.localPosition=localPosition; return child.transform;
        }

        private static void EnsureFolder(string parent, string name)
        {
            string path = parent + "/" + name;
            if (!AssetDatabase.IsValidFolder(path)) AssetDatabase.CreateFolder(parent,name);
        }
    }
}
