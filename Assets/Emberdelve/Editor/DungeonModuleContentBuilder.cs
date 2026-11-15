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
        private const float WallHeight = 6f;

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

        [MenuItem("Emberdelve/Build Crypt Rooms and Playable")]
        public static void BuildPlayable()
        {
            BuildCatalog();
            EmberdelveWindowsBuild.BuildNetPlayable();
        }

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
                DungeonSurfaceMaterials.Apply(root);
                DungeonFloorWear.Apply(root);
                DungeonRoomProps.Apply(root);
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

        /// <summary>Mirror-only destination generation; leaves the accepted ruins catalog and prefabs untouched.</summary>
        public static void BuildRoyaleDestinationCatalogs()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Build Royale destinations on the batch mirror only");
            foreach (int destination in new[] { 2, 3 })
            {
                string theme = destination == 2 ? "Crypt" : "Infernal";
                EnsureFolder(PrefabFolder, theme);
                var stone = AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Materials/Stone_Wall.mat");
                var floor = AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Materials/Stone_Floor.mat");
                var ceiling = AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Materials/Stone_Ceiling.mat");
                if (stone == null || floor == null || ceiling == null) throw new InvalidOperationException("Missing existing stone materials");
                if (destination == 3)
                {
                    EnsureFolder("Assets/Emberdelve/Art/Generated", "Royale");
                    stone = ThemeMaterial(stone, "Infernal_Basalt", new Color(.26f, .19f, .17f));
                    floor = ThemeMaterial(floor, "Infernal_Paving", new Color(.42f, .29f, .22f));
                    ceiling = ThemeMaterial(ceiling, "Infernal_Vault", new Color(.23f, .17f, .15f));
                }
                var definitions = new List<DungeonModuleDefinition>();
                foreach (var original in Specs)
                {
                    var spec = new Spec("royale_" + theme.ToLowerInvariant() + "_v1_" + original.Id,
                        theme + "_" + original.Name, original.Kind, original.Variant);
                    var root = CreateModule(spec, stone, floor, ceiling, destination);
                    DungeonSurfaceMaterials.Apply(root);
                DungeonFloorWear.Apply(root);
                DungeonRoomProps.Apply(root);
                    try
                    {
                        var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabFolder + "/" + theme + "/" + spec.Name + ".prefab");
                        var definition = prefab.GetComponent<DungeonModuleDefinition>();
                        if (!definition.HasValidSockets(out var issue)) throw new InvalidOperationException(issue);
                        definitions.Add(definition);
                    }
                    finally { UnityEngine.Object.DestroyImmediate(root); }
                }
                string path = CatalogFolder + "/" + theme + "ModuleCatalog.asset";
                var catalog = AssetDatabase.LoadAssetAtPath<DungeonModuleCatalog>(path);
                if (catalog == null)
                {
                    catalog = ScriptableObject.CreateInstance<DungeonModuleCatalog>();
                    AssetDatabase.CreateAsset(catalog, path);
                }
                var serialized = new SerializedObject(catalog);
                var array = serialized.FindProperty("modules"); array.arraySize = definitions.Count;
                for (int i = 0; i < definitions.Count; i++) array.GetArrayElementAtIndex(i).objectReferenceValue = definitions[i];
                serialized.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(catalog);
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[RoyaleMaps] Saved crypt and infernal catalogs (20 destination prefabs); ruins untouched");
        }

        private static Material ThemeMaterial(Material source, string name, Color tint)
        {
            string path = "Assets/Emberdelve/Art/Generated/Royale/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(source) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }
            else material.CopyPropertiesFromMaterial(source);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", tint);
            if (material.HasProperty("_Color")) material.SetColor("_Color", tint);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static GameObject CreateModule(Spec spec, Material wall, Material floor, Material ceiling, int destination = 1)
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
            if (destination > 1 || spec.Kind == DungeonModuleKind.Vault)
                Box(root.transform, "Roof", new Vector3(0,WallHeight+0.3f,0), new Vector3(Half*2,0.6f,Half*2), ceiling);
            if (destination > 1 || spec.Kind == DungeonModuleKind.Vault)
            {
                BuildSide(root.transform, true, false, wall);
                BuildSide(root.transform, true, true, wall);
                BuildSide(root.transform, false, false, wall);
                BuildSide(root.transform, false, true, wall);
            }
            else
                for (int side = 0; side < 4; side++)
                    RuinsWallProfile.Build(root.transform, side < 2, side % 2 == 1, wall, ((int)spec.Kind + spec.Variant + side) % 3);
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
            if (destination == 1)
            {
                AddRoomFeature(root.transform, spec, wall, floor);
                if (spec.Kind == DungeonModuleKind.Entry) MoonlitRuinsContent.AddEnvironment(root.transform);
            }
            else RoyaleRoomArchitecture.Build(root.transform, destination, spec.Kind, spec.Variant, wall, floor);
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
            CryptRoomArchitecture.Build(root, spec.Kind, spec.Variant, stone, floor);
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
