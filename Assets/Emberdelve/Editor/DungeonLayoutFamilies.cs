using System;
using System.Collections.Generic;
using System.Linq;
using Emberdelve.Core;
using Emberdelve.Level;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.EditorTools
{
    // Original playable adaptations of the inspected local Crypt room families.
    // Evidence and unknown reference units: Tasks/DARKER_MODULE_REFERENCE_20261122.md.
    public static class DungeonLayoutFamilies
    {
        private const string Folder = "Assets/Emberdelve/Art/Generated/LayoutFamilies";
        private static readonly Dictionary<string, Mesh> Meshes = new();
        internal static bool Gallery(DungeonModuleKind kind, int variant) => kind == DungeonModuleKind.Hall && variant == 1;
        internal static string Family(DungeonModuleKind kind, int variant) => kind switch
        {
            DungeonModuleKind.Entry => "Atrium_Courtyard",
            DungeonModuleKind.Hall => variant == 0 ? "FourRooms_DividedHall" : variant == 1 ? "GreatWalkway_Galleries" : "MadCorridors_OffsetPassages",
            DungeonModuleKind.Ossuary => variant == 0 ? "Catacomb_BurialAisles" : "LargeRoomPit_SunkenCrypt",
            DungeonModuleKind.Shrine => "Chapel_ProcessionalDais",
            DungeonModuleKind.Vault => "CenterTower_InnerKeep",
            DungeonModuleKind.Extraction => "CrossRoad_Antechambers",
            DungeonModuleKind.Bridge => "HBridge_RaisedCrossing",
            _ => throw new InvalidOperationException("Unmapped module kind " + kind)
        };
        internal static void BuildFloor(Transform root, DungeonModuleKind kind, int variant, Material floor, Material stone)
        {
            if (Gallery(kind, variant)) { DungeonRoomExpansion.BuildFloor(root, floor, stone); return; }
            if (kind == DungeonModuleKind.Ossuary && variant == 1)
            {
                // A true cut-out: no invisible flat collider bridging the sunken crypt.
                Solid(root,"Floor",new Vector3(4,-.3f,0),new Vector3(28,.6f,36),floor);
                Solid(root,"Floor_PitWest",new Vector3(-16,-.3f,0),new Vector3(4,.6f,36),floor);
                Solid(root,"Floor_PitNorth",new Vector3(-12,-.3f,10.5f),new Vector3(4,.6f,15),floor);
                Solid(root,"Floor_PitSouth",new Vector3(-12,-.3f,-16.5f),new Vector3(4,.6f,3),floor);
                Solid(root,"Floor_SunkenCrypt",new Vector3(-12,-1.8f,-6),new Vector3(4,.6f,12),floor);
                Stairs(root,"PitSouth",new Vector3(-12,-1.5f,-12),180,1.5f,4,stone);
                Stairs(root,"PitNorth",new Vector3(-12,-1.5f,0),0,1.5f,4,stone);
                // Lower exposed retaining faces stop at the upper slab underside.
                Solid(root,"Pit_Retaining_W",new Vector3(-14.15f,-1.05f,-6),new Vector3(.3f,.9f,18),stone);
                Solid(root,"Pit_Retaining_E",new Vector3(-9.85f,-1.05f,-6),new Vector3(.3f,.9f,18),stone);
            }
            else Solid(root,"Floor",new Vector3(0,-.3f,0),new Vector3(36,.6f,36),floor);
        }
        internal static void Dress(Transform root, DungeonModuleKind kind, int variant, int destination, Material stone, Material floor)
        {
            var group = Group(root,"Layout_" + Family(kind,variant),Vector3.zero);
            Material metal=AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Materials/Blade_Metal.mat");
            if (Gallery(kind,variant))
            {
                DungeonRoomExpansion.Dress(root,kind,variant,destination,stone,floor);
                // The two old raised wings now belong to this family only.
                Wall(group,new Vector3(-5,2,7),new Vector3(.65f,4,8),stone);
                Wall(group,new Vector3(5,2,-7),new Vector3(.65f,4,8),stone);
            }
            else switch(kind)
            {
                case DungeonModuleKind.Entry:
                    // Open arrival court with two side chambers and a shallow north dais.
                    Chamber(group,new Vector3(-9,0,-8),7,7,stone);
                    Chamber(group,new Vector3(9,0,-8),7,7,stone);
                    Platform(group,"WelcomeDais",new Vector3(0,0,11),6,4,.9f,stone,floor);
                    Table(group,new Vector3(-9,0,-9),90); Table(group,new Vector3(9,0,-9),-90);
                    break;
                case DungeonModuleKind.Hall:
                    if(variant==0)
                    {
                        foreach(int x in new[]{-1,1}) foreach(int z in new[]{-1,1})
                            Chamber(group,new Vector3(x*9,0,z*8),8,7,stone);
                        Table(group,new Vector3(-9,0,-9),0); Table(group,new Vector3(9,0,7),90);
                    }
                    else
                    {
                        // Opposite offset exits break the direct north/south sightline.
                        Wall(group,new Vector3(-4.5f,2,-6),new Vector3(19,4,.7f),stone);
                        Wall(group,new Vector3(4.5f,2,6),new Vector3(19,4,.7f),stone);
                        Wall(group,new Vector3(-10,2,-11),new Vector3(.7f,4,6),stone);
                        Wall(group,new Vector3(10,2,11),new Vector3(.7f,4,6),stone);
                        Table(group,new Vector3(-12,0,11),90);
                    }
                    break;
                case DungeonModuleKind.Ossuary:
                    if(variant==0)
                    {
                        foreach(int x in new[]{-1,1}) foreach(int z in new[]{-1,1})
                        {
                            Wall(group,new Vector3(x*5,2,z*8.15f),new Vector3(.6f,4,10.3f),stone);
                            Wall(group,new Vector3(x*8.15f,2,z*13),new Vector3(5.7f,4,.6f),stone);
                            CryptRoomArchitecture.Tomb(group,new Vector3(x*9,0,z*9),0,stone,floor);
                        }
                        Arch(group,new Vector3(0,0,7),3.6f,3,stone);
                        Arch(group,new Vector3(0,0,-7),3.6f,3,stone);
                    }
                    else
                    {
                        Wall(group,new Vector3(-7,2,-6),new Vector3(.7f,4,10),stone);
                        Chamber(group,new Vector3(6,0,-8),8,8,stone);
                        CryptRoomArchitecture.Tomb(group,new Vector3(6,0,-9),90,stone,floor);
                        Arch(group,new Vector3(-12,-1.5f,-6),1.8f,3.1f,stone);
                    }
                    break;
                case DungeonModuleKind.Shrine:
                    // Long nave, lateral aisles, elevated altar reached by a broad stair.
                    foreach(int side in new[]{-1,1})
                    {
                        Wall(group,new Vector3(side*7,2,5),new Vector3(.7f,4,14),stone);
                        foreach(float z in new[]{-10f,-4f,2f}) CryptRoomArchitecture.Column(group,new Vector3(side*4.3f,0,z),3.6f,stone);
                    }
                    Platform(group,"ChapelDais",new Vector3(0,0,10),9,6,1.8f,stone,floor);
                    var altar=Group(group,"Ruined_Altar",new Vector3(0,1.8f,11));
                    Solid(altar,"Altar_Table",new Vector3(0,.6f,0),new Vector3(3,1.2f,1.3f),stone);
                    break;
                case DungeonModuleKind.Vault:
                    // Central room with two gates and a perimeter bypass, not corner stairs.
                    Wall(group,new Vector3(-6,2.5f,0),new Vector3(.7f,5,12.7f),stone);
                    Wall(group,new Vector3(6,2.5f,0),new Vector3(.7f,5,12.7f),stone);
                    foreach(int side in new[]{-1,1}) foreach(int half in new[]{-1,1})
                        Wall(group,new Vector3(half*4.325f,2.5f,side*6),new Vector3(2.65f,5,.7f),stone);
                    Arch(group,new Vector3(0,0,-6),2.4f,3.2f,stone);
                    Arch(group,new Vector3(0,0,6),2.4f,3.2f,stone);
                    Platform(group,"VaultSideGallery",new Vector3(-11,0,6.5f),4,7,1.8f,stone,floor);
                    break;
                case DungeonModuleKind.Extraction:
                    // A broad crossroad surrounded by offset waiting chambers.
                    Chamber(group,new Vector3(-9,0,-9),8,6,stone);
                    Chamber(group,new Vector3(9,0,8),7,8,stone);
                    Wall(group,new Vector3(-9,2,8),new Vector3(8,4,.7f),stone);
                    Wall(group,new Vector3(9,2,-8),new Vector3(.7f,4,8),stone);
                    Arch(group,new Vector3(0,0,-8),3.2f,3.2f,stone);
                    Table(group,new Vector3(-9,0,-10),0);
                    break;
                case DungeonModuleKind.Bridge:
                    // Raised transverse crossing; the solid causeway has no false walkable underpass.
                    Solid(group,"Crossing_Deck",new Vector3(0,.9f,6),new Vector3(14,1.8f,4.5f),floor);
                    Stairs(group,"CrossingWest",new Vector3(-10.6f,0,6),90,1.8f,4.5f,stone);
                    Stairs(group,"CrossingEast",new Vector3(10.6f,0,6),270,1.8f,4.5f,stone);
                    foreach(int side in new[]{-1,1}) Solid(group,"Crossing_Parapet",new Vector3(0,2.35f,6+side*2.1f),new Vector3(14,1.1f,.25f),stone);
                    Wall(group,new Vector3(-6,2,-7),new Vector3(.7f,4,10),stone);
                    Wall(group,new Vector3(6,2,-7),new Vector3(.7f,4,10),stone);
                    var crossingArch=Group(group,"Crossing_Archway",new Vector3(0,1.8f,6));
                    crossingArch.localRotation=Quaternion.Euler(0,90,0);
                    Arch(crossingArch,Vector3.zero,1.95f,3,stone);
                    break;
            }
            Material wood=AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Materials/Bow_Wood.mat");
            foreach(int side in new[]{-1,1})
                MoonlitRuinsContent.Barrel(group,"Supply_Barrel_"+(side<0?"West":"East"),
                    Gallery(kind,variant)?new Vector3(side*6,0,side*14.5f):new Vector3(side*15,0,-3),wood);
            if(root.Find("Hidden_Reliquary")==null && (kind==DungeonModuleKind.Ossuary || kind==DungeonModuleKind.Vault))
                DungeonRoomExpansion.SecretChamber(root,stone,floor,metal,AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Materials/Bow_Wood.mat"));
            // Light the internal circulation from visible fire sources, offset from every socket axis.
            foreach(float z in new[]{-14f,15f})
                CryptRoomArchitecture.Brazier(group,new Vector3(3,0,z),stone,metal,destination==2);
            if(!Gallery(kind,variant))
            {
                // Place lights at the perimeter, away from circulation and protected cache entrance.
                foreach(int side in new[]{-1,1})
                    CryptRoomArchitecture.Brazier(group,new Vector3(side*15,0,side<0?13:-13),stone,metal,destination==2);
            }
        }
        private static void Chamber(Transform root,Vector3 center,float width,float depth,Material stone)
        {
            var t=Group(root,"Side_Chamber",center);
            Wall(t,new Vector3(-width*.5f,2,0),new Vector3(.6f,4,depth+.6f),stone);
            Wall(t,new Vector3(width*.5f,2,0),new Vector3(.6f,4,depth+.6f),stone);
            Wall(t,new Vector3(0,2,depth*.5f),new Vector3(width-.6f,4,.6f),stone);
            // Butt perpendicular spans against the side walls; overlapping coplanar tops flicker.
            float wing=(width-.6f-3.6f)*.5f;
            foreach(int side in new[]{-1,1}) Wall(t,new Vector3(side*(1.8f+wing*.5f),2,-depth*.5f),new Vector3(wing,4,.6f),stone);
        }
        private static void Platform(Transform root,string name,Vector3 center,float width,float depth,float height,Material stone,Material floor)
        {
            var t=Group(root,name,center);
            Solid(t,"Raised_Deck",new Vector3(0,height*.5f,0),new Vector3(width,height,depth),floor);
            Stairs(t,"Approach",new Vector3(0,0,-depth*.5f-height*2),0,height,width,stone);
            foreach(int side in new[]{-1,1}) Solid(t,"Side_Parapet",new Vector3(side*(width*.5f-.15f),height+.5f,0),new Vector3(.25f,1,depth),stone);
        }
        private static void Stairs(Transform root,string name,Vector3 start,float yaw,float height,float width,Material stone)
        {
            var t=Group(root,"Layout_Stairs_"+name,start);t.localRotation=Quaternion.Euler(0,yaw,0);
            int count=Mathf.RoundToInt(height/.15f);
            for(int i=0;i<count;i++)
            {
                float h=(i+1)*.15f;
                // Physically separate visible treads by 4mm; collision stays continuous.
                var step=Solid(t,"Step_"+i,new Vector3(0,h*.5f,(i+.5f)*.3f),new Vector3(width,h,.296f),stone);
                step.GetComponent<BoxCollider>().size=new Vector3(1,1,.305f/.296f);
            }
        }
        private static void Arch(Transform root,Vector3 feet,float halfWidth,float spring,Material stone)
        {
            foreach(int side in new[]{-1,1}) CryptRoomArchitecture.Column(root,feet+new Vector3(side*halfWidth,0,0),spring,stone);
            var t=Group(root,"Reused_Stone_Arch",feet+Vector3.up*spring);
            t.localScale=new Vector3(halfWidth/1.05f,1.4f,.65f);
            t.gameObject.layer=LayerMask.NameToLayer(GameConfig.LevelLayerName);
            var mesh=AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Emberdelve/Art/Generated/CryptArch.asset");
            t.gameObject.AddComponent<MeshFilter>().sharedMesh=mesh;t.gameObject.AddComponent<MeshRenderer>().sharedMaterial=stone;
            t.gameObject.AddComponent<MeshCollider>().sharedMesh=mesh;
        }
        private static void Table(Transform t,Vector3 p,float yaw)=>DungeonRoomProps.Model(t,"WoodenTable_01",p,.85f,yaw);
        private static void Wall(Transform t,Vector3 p,Vector3 size,Material stone)=>Solid(t,"Layout_Wall",p,size,stone);
        private static Transform Group(Transform parent,string name,Vector3 p)
        { var t=new GameObject(name).transform;t.SetParent(parent,false);t.localPosition=p;return t; }
        private static GameObject Solid(Transform parent,string name,Vector3 position,Vector3 size,Material material)
        {
            var t=Group(parent,name,position);t.localScale=size;t.gameObject.layer=LayerMask.NameToLayer(GameConfig.LevelLayerName);
            string key=string.Join("_",new[]{size.x,size.y,size.z}.Select(x=>Mathf.RoundToInt(x*10000).ToString()));
            if(!Meshes.TryGetValue(key,out var mesh))
            {
                if(!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets/Emberdelve/Art/Generated","LayoutFamilies");
                string path=Folder+"/Stone_"+key+".asset";mesh=AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if(mesh==null){mesh=ArchitecturalJoinRepair.CreateRoundedBox(size);mesh.name="Layout masonry "+key;AssetDatabase.CreateAsset(mesh,path);}
                Meshes.Add(key,mesh);
            }
            t.gameObject.AddComponent<MeshFilter>().sharedMesh=mesh;t.gameObject.AddComponent<MeshRenderer>().sharedMaterial=material;
            t.gameObject.AddComponent<BoxCollider>();return t.gameObject;
        }
        internal static bool UsesNewTraversal(GameObject root) => root.transform.Cast<Transform>().Any(t=>t.name.StartsWith("Layout_",StringComparison.Ordinal)) && root.transform.Find("Western_Gallery")==null;
        public static int ValidateGeometry(GameObject root)
        {
            // Equal-height wall boxes must meet at an edge, never share an exposed top area.
            var walls=root.GetComponentsInChildren<MeshRenderer>(true).Where(r=>r.enabled && r.name=="Layout_Wall").ToArray();
            for(int a=0;a<walls.Length;a++) for(int b=a+1;b<walls.Length;b++)
            {
                Bounds x=walls[a].bounds,y=walls[b].bounds;
                if(Mathf.Abs(x.max.y-y.max.y)>.0001f)continue;
                float overlapX=Mathf.Min(x.max.x,y.max.x)-Mathf.Max(x.min.x,y.min.x);
                float overlapZ=Mathf.Min(x.max.z,y.max.z)-Mathf.Max(x.min.z,y.min.z);
                if(overlapX>.0001f && overlapZ>.0001f)
                    throw new InvalidOperationException("Coplanar wall tops overlap in "+root.name);
            }
            int steps=0;
            foreach(var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                string path=AssetDatabase.GetAssetPath(filter.sharedMesh);
                if(!path.StartsWith(Folder+"/",StringComparison.Ordinal))continue;
                var bounds=filter.sharedMesh.bounds;
                if((bounds.size-Vector3.one).sqrMagnitude>1e-8f || bounds.center.sqrMagnitude>1e-8f)
                    throw new InvalidOperationException("Unexpected layout mesh bounds "+filter.name);
                if(filter.name.StartsWith("Step_",StringComparison.Ordinal))
                {
                    int index=int.Parse(filter.name.Substring(5));var t=filter.transform;
                    float min=t.localPosition.z-.5f*t.localScale.z,max=t.localPosition.z+.5f*t.localScale.z;
                    if(Mathf.Abs(min-(index*.3f+.002f))>.0001f || Mathf.Abs(max-((index+1)*.3f-.002f))>.0001f)
                        throw new InvalidOperationException("Overlapping layout tread "+t.parent.name+"/"+t.name);
                    if(Mathf.Abs(t.localPosition.y+.5f*t.localScale.y-(index+1)*.15f)>.0001f)
                        throw new InvalidOperationException("Incorrect rise "+t.name);
                    steps++;
                }
            }
            return steps;
        }
        public static void RebuildSaved()
        {
            string project=System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath,"..")).Replace('\\','/');
            if(!project.EndsWith("/DakaDaka_clean_handoff",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Author only in clean_handoff");
            foreach(string guid in AssetDatabase.FindAssets("t:Prefab",new[]{"Assets/Emberdelve/Prefabs/DungeonModules"}).OrderBy(x=>AssetDatabase.GUIDToAssetPath(x)))
            {
                string path=AssetDatabase.GUIDToAssetPath(guid);var root=PrefabUtility.LoadPrefabContents(path);
                try
                {
                    var definition=root.GetComponent<DungeonModuleDefinition>();
                    int variant=definition.ModuleId.EndsWith("hall_02")||definition.ModuleId.EndsWith("ossuary_02")?1:definition.ModuleId.EndsWith("hall_03")?2:0;
                    int destination=path.Contains("/Infernal/")?3:path.Contains("/Crypt/")?2:1;
                    // Preserve serialized metadata, sockets, closures, perimeter and server-owned secret mechanism.
                    foreach(Transform child in root.transform.Cast<Transform>().ToArray())
                    {
                        string n=child.name;
                        bool keep=n=="PlayerSpawn"||n.StartsWith("Socket_")||n.StartsWith("Closure_")||n.StartsWith("Arch_Jamb_")||n.StartsWith("Arch_Lintel_")||n.StartsWith("WallRun_")||n.StartsWith("RuinedWall_")||n.StartsWith("ChamferedCorner_")||n=="Roof"||n=="Hidden_Reliquary";
                        if(!keep)Object.DestroyImmediate(child.gameObject);
                    }
                    string palette=destination==3?"Infernal_":"Stone_";
                    var stone=AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Generated/DungeonSurfaces/World_"+palette+(destination==3?"Basalt":"Wall")+".mat");
                    var floor=AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Generated/DungeonSurfaces/World_"+palette+(destination==3?"Paving":"Floor")+".mat");
                    if(floor==null)floor=AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Materials/Stone_Floor.mat");
                    BuildFloor(root.transform,definition.Kind,variant,floor,stone);
                    Dress(root.transform,definition.Kind,variant,destination,stone,floor);
                    DungeonFloorWear.Apply(root);
                    MeshyMapDressing.ApplyRoom(root);DownloadedCryptArchitecture.Apply(root);
                    WeatheredTraversalSurfaces.Apply(root);
                    if(Gallery(definition.Kind,variant))StairSurfaceSeparation.Apply(root);
                    DungeonPitJointBacking.Apply(root);
                    ValidateGeometry(root);
                    if(!definition.HasValidSockets(out string issue))throw new InvalidOperationException(issue);
                    PrefabUtility.SaveAsPrefabAsset(root,path);
                    Debug.Log("[LayoutFamilies] "+path+" -> "+Family(definition.Kind,variant));
                }
                finally{PrefabUtility.UnloadPrefabContents(root);}
            }
        }
    }
}
