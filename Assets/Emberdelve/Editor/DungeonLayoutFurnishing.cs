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
    // Deliberate furnishing bays for the saved layout families; no random scatter on circulation.
    public static class DungeonLayoutFurnishing
    {
        public const string GroupName = "Layout_Furnishings";
        private sealed class Furnisher
        {
            internal readonly GameObject Room;
            internal readonly Transform Root;
            internal readonly List<string> Rejected = new();
            private readonly List<Bounds> routes = new();
            private readonly List<Renderer> existing;
            internal int Count;
            internal Furnisher(GameObject room)
            {
                Room=room;
                var secret=room.transform.Find("Hidden_Reliquary");
                foreach(var t in room.GetComponentsInChildren<Transform>(true).ToArray())
                {
                    if(t==null || t==room.transform || secret!=null && t.IsChildOf(secret))continue;
                    if(t.name==GroupName || t.name=="Room_Props" || t.name=="Authored_Room_Furnishings" || t.name=="WoodenTable_01_Anchor")
                        Object.DestroyImmediate(t.gameObject);
                }
                Root=new GameObject(GroupName).transform;Root.SetParent(room.transform,false);
                existing=room.GetComponentsInChildren<Renderer>(true).Where(r=>r is MeshRenderer && r.enabled && r.name!="Worn_Flagstones").ToList();
                routes.AddRange(Reservations(room));
            }

            internal Transform Place(string id,float x,float z,float height,float yaw=0,float y=0,float maxSpan=3.2f,Transform support=null)
            {
                GameObject source=MeshyMapDressing.Load(id) ?? EmberdelvePolyHavenImport.LoadModel(id);
                if(source==null)throw new InvalidOperationException("Missing downloaded furnishing "+id);
                var anchor=new GameObject((support==null?"Floor_":"Top_")+id).transform;anchor.SetParent(Root,false);
                var model=Object.Instantiate(source,anchor,false);model.name="Model";
                foreach(var c in model.GetComponentsInChildren<Collider>(true))Object.DestroyImmediate(c);
                var renderers=model.GetComponentsInChildren<MeshRenderer>();
                if(renderers.Length==0)throw new InvalidOperationException("No visible mesh "+id);
                Bounds bounds=BoundsOf(renderers);
                float scale=Mathf.Min(height/bounds.size.y,maxSpan/Mathf.Max(bounds.size.x,bounds.size.z));
                model.transform.localScale*=scale;bounds=BoundsOf(renderers);
                model.transform.position-=new Vector3(bounds.center.x,bounds.min.y,bounds.center.z);
                anchor.localPosition=new Vector3(x,y,z);anchor.localRotation=Quaternion.Euler(0,yaw,0);
                bounds=BoundsOf(renderers);
                string reason=null;
                if(support==null && routes.Any(b=>Overlap(b,bounds,.005f)))reason="reserved approach";
                if(reason==null)
                    foreach(var r in existing)
                    {
                        if(support!=null && r.transform.IsChildOf(support))continue;
                        if(Overlap(r.bounds,bounds,.025f)){reason="occupied by "+r.name;break;}
                    }
                if(reason==null && support==null && HitsCollider(Room,bounds))reason="collision blocker";
                if(reason==null && support==null && !HasSupport(Room,bounds))reason="unsupported footprint";
                if(reason!=null){Rejected.Add(id+" "+anchor.localPosition+" "+reason);Object.DestroyImmediate(anchor.gameObject);return null;}
                if(support==null)
                {
                    anchor.gameObject.layer=LayerMask.NameToLayer(GameConfig.LevelLayerName);
                    // Local bounds before yaw; all imported colliders were removed above.
                    var local=new Bounds(anchor.InverseTransformPoint(bounds.center),Vector3.zero);
                    foreach(var r in renderers)foreach(var v in Corners(r.localBounds))local.Encapsulate(anchor.InverseTransformPoint(r.transform.TransformPoint(v)));
                    var box=anchor.gameObject.AddComponent<BoxCollider>();box.center=local.center;box.size=local.size;
                }
                if(support!=null)anchor.SetParent(support,true);
                existing.AddRange(renderers);Count++;return anchor;
            }
            internal void Table(float x,float z,float yaw=0,float y=0)
            {
                var t=Place("WoodenTable_01",x,z,.78f,yaw,y,2.65f);
                if(t==null)return;
                var b=BoundsOf(t.GetComponentsInChildren<MeshRenderer>());
                Place("wooden_candlestick",x,z,.27f,0,b.max.y,.4f,t);
                Vector3 side=Quaternion.Euler(0,yaw,0)*Vector3.forward*.95f;
                Place("wooden_stool_01",x+side.x,z+side.z,.46f,0,y,.55f);
                Place("wooden_stool_01",x-side.x,z-side.z,.46f,20,y,.55f);
            }
            internal void Study(float x,float z,float width,float depth)
            {
                Table(x-width*.5f+1.05f,z+.2f,90);
                Place("wooden_bookshelf_worn",x+1.25f,z+depth*.5f-.7f,2,180);
                Place("wooden_bucket_01",x+width*.5f-.8f,z-.7f,.5f);
                Place("wooden_crate_02",x+width*.5f-1.2f,z+1,.55f,0,0,1.4f);
            }
            internal void Stores(float x,float z,float yaw=0)
            {
                Place("wooden_bookshelf_worn",x,z,2,yaw,0,1.5f);
                Place("wine_barrel_01",x-1.25f,z,1.05f,0,0,1.05f);
                Place("wooden_crate_02",x+1.5f,z,.55f,0,0,1.4f);
                Place("wooden_bucket_01",x+1.6f,z-1,.48f);
            }
            internal void Shrine(float x,float z,float yaw=0)
            {
                Place("StandingShrine",x,z,1.9f,yaw,0,1.15f);
                Place("VotiveStand",x-.95f,z-.1f,.65f,0,0,.7f);
                Place("VotiveStand",x+.95f,z-.1f,.5f,30,0,.6f);
            }
        }
        internal static int Apply(GameObject room)
        {
            var d=room.GetComponent<DungeonModuleDefinition>();if(d==null)return 0;
            var f=new Furnisher(room);
            switch(d.Kind)
            {
                case DungeonModuleKind.Entry:
                    f.Study(-9,-8,7,7);f.Study(9,-8,7,7);
                    f.Stores(-9,15,180);f.Shrine(10,15,180);
                    f.Table(-15,5,90);f.Place("wooden_bookshelf_worn",16.5f,5,1.8f,270);
                    break;
                case DungeonModuleKind.Hall:
                    if(d.ModuleId.EndsWith("hall_01"))
                    {
                        f.Study(-9,-8,8,7);f.Study(9,8,8,7);
                        f.Stores(-9,10.7f,180);f.Table(-10,7,90);
                        f.Stores(9,-5.3f,180);f.Place("wooden_bookshelf_worn",11.8f,-8,1.8f,270);
                        f.Place("wine_barrel_01",-11.5f,-15,1.1f);f.Place("wooden_crate_02",11,-15,.6f,0,0,1.5f);
                    }
                    else if(d.ModuleId.EndsWith("hall_02"))
                    {
                        f.Table(-15.5f,-4.8f,90,3.9f);f.Place("VotiveStand",-15.5f,4.8f,.65f,0,3.9f,.75f);
                        f.Table(-7,14.5f);f.Stores(-8,-16,0);f.Stores(6,15,180);
                        f.Place("wooden_bookshelf_worn",-5.9f,5,1.85f,270);f.Table(7,-3.5f);
                        f.Place("wine_barrel_01",8,-15,1.05f);f.Place("wooden_crate_02",5,-15,.55f,0,0,1.5f);
                    }
                    else
                    {
                        f.Table(-12,11,90);f.Stores(-7,15,180);f.Stores(6,-15);
                        f.Table(12,-11,90);f.Place("wooden_bookshelf_worn",-8,-10,1.8f,90);
                        f.Place("wooden_bookshelf_worn",8,10,2,270);f.Shrine(-15,-10,90);f.Shrine(15,10,270);
                    }
                    break;
                case DungeonModuleKind.Ossuary:
                    if(d.ModuleId.EndsWith("ossuary_01"))
                    {
                        foreach(int x in new[]{-1,1})foreach(int z in new[]{-1,1})
                        {
                            f.Shrine(x*12,z*10,x<0?90:270);
                            f.Place("VotiveStand",x*7,z*10,.7f);
                        }
                        f.Stores(-8,-16);f.Place("wooden_bookshelf_worn",6,16,1.7f,180);
                    }
                    else
                    {
                        f.Shrine(8.5f,-5.5f,180);f.Place("VotiveStand",3.3f,-5.5f,.7f);
                        f.Stores(5,15,180);f.Table(10,-15);f.Shrine(-6,14,180);
                        f.Stores(14,-7,270);f.Place("wooden_bookshelf_worn",4,-4.7f,1.85f,180);
                    }
                    break;
                case DungeonModuleKind.Shrine:
                    f.Table(-12,5,90);f.Table(12,5,90);
                    foreach(int side in new[]{-1,1})
                    {
                        f.Place("wooden_bookshelf_worn",side*12,11,2.1f,180);
                        f.Shrine(side*11,-10,side<0?90:270);
                        f.Place("VotiveStand",side*2.6f,-7,.7f);
                        f.Place("VotiveStand",side*2.6f,-11,.7f);
                        f.Place("wooden_bucket_01",side*13,10,.48f);
                    }
                    break;
                case DungeonModuleKind.Vault:
                    f.Table(-4.7f,3.8f,90);f.Place("wooden_bookshelf_worn",4.8f,-3.3f,1.85f,270);
                    f.Place("wooden_bookshelf_worn",4.8f,3.6f,2,270);
                    f.Place("wooden_crate_02",-4.5f,-4.6f,.6f,0,0,1.5f);
                    f.Place("wine_barrel_01",4.6f,-4.9f,.9f);
                    f.Stores(-8,-15);f.Stores(6,-15);f.Table(12,-8,90);
                    f.Place("VotiveStand",-11,8.8f,.65f,0,1.8f,.7f);
                    f.Shrine(-6,15,180);
                    break;
                case DungeonModuleKind.Extraction:
                    f.Study(-9,-9,8,6);f.Study(9,8,7,8);
                    f.Stores(-10,14.5f,180);f.Stores(13,-13,270);
                    f.Table(-10,5);f.Shrine(6,-13);
                    break;
                case DungeonModuleKind.Bridge:
                    f.Table(-9,-8,90);f.Stores(-10,-15);f.Stores(10,-15);
                    f.Place("wooden_bookshelf_worn",8,-7,1.85f,90);f.Table(10,-11,0);
                    f.Shrine(-9,14,180);f.Stores(8,15,180);
                    break;
            }
            System.IO.Directory.CreateDirectory("Logs/acceptance/layout-furnishing-20261122");
            System.IO.File.WriteAllText("Logs/acceptance/layout-furnishing-20261122/"+room.name+"-placement.txt", "placed="+f.Count+"\n"+string.Join("\n",f.Rejected));
            Validate(room);
            Debug.Log("[LayoutFurnishing] "+room.name+" placed="+f.Count+" rejected="+f.Rejected.Count+"\n"+string.Join("\n",f.Rejected));
            return f.Count;
        }
        private static List<Bounds> Reservations(GameObject room)
        {
            var routes=new List<Bounds>();
                routes.Add(new Bounds(new Vector3(0,2,0),new Vector3(4.4f,12,35)));
                routes.Add(new Bounds(new Vector3(0,2,0),new Vector3(35,12,4.4f)));
                if(room.transform.Find("Hidden_Reliquary")!=null)
                    routes.Add(new Bounds(new Vector3(13.6f,2,12.7f),new Vector3(8,12,10.6f)));
                foreach(var stairs in room.GetComponentsInChildren<Transform>(true).Where(t=>t.name.StartsWith("Layout_Stairs_") || t.name.EndsWith("Stairs")))
                {
                    var rs=stairs.GetComponentsInChildren<MeshRenderer>();if(rs.Length==0)continue;
                    Bounds b=BoundsOf(rs);b.Expand(new Vector3(1.4f,5,1.8f));routes.Add(b);
                }
                foreach(var chamber in room.GetComponentsInChildren<Transform>().Where(t=>t.name=="Side_Chamber"))
                {
                    Bounds b=BoundsOf(chamber.GetComponentsInChildren<MeshRenderer>());
                    routes.Add(new Bounds(new Vector3(chamber.position.x,1,b.min.z+.5f),new Vector3(4.2f,5,3.1f)));
                }
            return routes;
        }
        private static bool HitsCollider(GameObject room,Bounds b)
        {
            var furnishings=room.transform.Find(GroupName);
            foreach(var box in room.GetComponentsInChildren<BoxCollider>(true))
            {
                if(box.isTrigger || !box.enabled || box.transform.IsChildOf(furnishings))continue;
                var bounds=new Bounds(box.transform.TransformPoint(box.center),Vector3.zero);
                foreach(var v in Corners(new Bounds(box.center,box.size)))bounds.Encapsulate(box.transform.TransformPoint(v));
                if(Overlap(bounds,b,.025f))return true;
            }
            return false;
        }
        private static bool HasSupport(GameObject room,Bounds b)
        {
            var colliders=room.GetComponentsInChildren<Collider>(true).Where(c=>!c.isTrigger && !c.transform.IsChildOf(room.transform.Find(GroupName))).ToArray();
            foreach(float x in new[]{b.min.x+.06f,b.max.x-.06f})foreach(float z in new[]{b.min.z+.06f,b.max.z-.06f})
            {
                var ray=new Ray(new Vector3(x,b.min.y+.08f,z),Vector3.down);
                if(!colliders.Any(c=>c.Raycast(ray,out var hit,.12f) && Mathf.Abs(hit.point.y-b.min.y)<.045f))return false;
            }
            return true;
        }
        private static bool Overlap(Bounds a,Bounds b,float tolerance)
            =>Mathf.Min(a.max.x,b.max.x)-Mathf.Max(a.min.x,b.min.x)>tolerance
            && Mathf.Min(a.max.y,b.max.y)-Mathf.Max(a.min.y,b.min.y)>tolerance
            && Mathf.Min(a.max.z,b.max.z)-Mathf.Max(a.min.z,b.min.z)>tolerance;
        private static IEnumerable<Vector3> Corners(Bounds b)
        {for(int i=0;i<8;i++)yield return b.center+Vector3.Scale(b.extents,new Vector3((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1));}
        private static Bounds BoundsOf(IEnumerable<Renderer> renderers)
        {var rs=renderers.ToArray();var b=rs[0].bounds;foreach(var r in rs)b.Encapsulate(r.bounds);return b;}
        public static int Validate(GameObject room)
        {
            var group=room.transform.Find(GroupName);if(group==null)throw new InvalidOperationException("Unfurnished "+room.name);
            int count=0;var reserved=Reservations(room);
            foreach(var item in group.GetComponentsInChildren<Transform>().Where(t=>t.name.StartsWith("Floor_") || t.name.StartsWith("Top_")))
            {
                var b=BoundsOf(item.Find("Model").GetComponentsInChildren<MeshRenderer>());
                if(item.name.StartsWith("Floor_"))
                {
                    if(reserved.Any(r=>Overlap(r,b,.005f)) || HitsCollider(room,b))throw new InvalidOperationException("Blocked furnishing approach "+room.name+"/"+item.name);
                    if(!HasSupport(room,b))throw new InvalidOperationException("Unsupported furnishing "+room.name+"/"+item.name);
                    if(item.GetComponent<BoxCollider>()==null)throw new InvalidOperationException("Missing furniture collision");
                }
                else
                {
                    if(!item.parent.name.StartsWith("Floor_"))throw new InvalidOperationException("Missing tabletop support "+item.name);
                    var table=BoundsOf(item.parent.Find("Model").GetComponentsInChildren<MeshRenderer>());
                    if(Mathf.Abs(table.max.y-b.min.y)>.006f || b.min.x<table.min.x+.02f || b.max.x>table.max.x-.02f || b.min.z<table.min.z+.02f || b.max.z>table.max.z-.02f)
                        throw new InvalidOperationException("Unseated tabletop decoration "+room.name+"/"+item.name);
                }
                foreach(var other in room.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if(!other.enabled || other.name=="Worn_Flagstones" || other.transform.IsChildOf(item))continue;
                    if(item.name.StartsWith("Top_") && other.transform.IsChildOf(item.parent.Find("Model")))continue;
                    if(Overlap(b,other.bounds,.025f))throw new InvalidOperationException("Furnishing intersects "+room.name+"/"+item.name+" with "+other.name);
                }
                count++;
            }
            if(count<12)throw new InvalidOperationException("Furnishing plan incomplete "+room.name+" count="+count);
            return count;
        }
        public static void ApplySaved()=>Saved(true);
        public static void ValidateSaved()=>Saved(false);
        private static void Saved(bool apply)
        {
            if(apply && !Application.dataPath.Replace('\\','/').EndsWith("/DakaDaka_clean_handoff/Assets",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Author only in clean_handoff");
            foreach(var guid in AssetDatabase.FindAssets("t:Prefab",new[]{"Assets/Emberdelve/Prefabs/DungeonModules"}).OrderBy(AssetDatabase.GUIDToAssetPath))
            {
                string path=AssetDatabase.GUIDToAssetPath(guid);var root=PrefabUtility.LoadPrefabContents(path);
                try{if(apply){Apply(root);PrefabUtility.SaveAsPrefabAsset(root,path);}else Validate(root);}
                finally{PrefabUtility.UnloadPrefabContents(root);}
            }
        }
    }
}
