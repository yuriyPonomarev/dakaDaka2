using System;
using System.Collections.Generic;
using System.Linq;
using Emberdelve.AI;
using Emberdelve.Core;
using Emberdelve.Interaction;
using Emberdelve.Level;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.EditorTools
{
    // Deliberate furnishing bays for the saved layout families; no random scatter on circulation.
    // Every module kind has its own recipe; candidate spots come from the real wall/floor colliders of each prefab and are
    // ordered by a deterministic seed derived from the room name.
    public static class DungeonLayoutFurnishing
    {
        public const string GroupName = "Layout_Furnishings";
        private const int MinItems = 30;
        private const int MaxLights = 8;

        private enum M { W, F, H } // against a wall on the floor, free on the floor, hung on a wall

        // size = footprint/height used for cheap pre-checks, height/span = natural scale passed to Place.
        private static readonly Dictionary<string, (Vector3 size, float height, float span)> Info = new()
        {
            { "PH_Sarcophagus", (new Vector3(2.45f, 1.11f, 1.20f), 1.11f, 3f) },
            { "PH_SarcophagusBroken", (new Vector3(3.15f, 1.21f, 1.34f), 1.21f, 3.7f) },
            { "PH_CoffinWood", (new Vector3(2.04f, .60f, .67f), .60f, 2.5f) },
            { "PH_CoffinOpen", (new Vector3(2.04f, .56f, .92f), .56f, 2.5f) },
            { "PH_BonePile", (new Vector3(1.31f, .46f, 1.16f), .46f, 1.8f) },
            { "PH_SkullHeap_Small", (new Vector3(.60f, .27f, .56f), .27f, 1f) },
            { "PH_OssuaryShelf_Wall", (new Vector3(2.40f, 2.40f, .50f), 2.40f, 2.9f) },
            { "PH_IronBrazier", (new Vector3(.76f, 1.06f, .75f), 1.06f, 1.3f) },
            { "PH_CandleCluster", (new Vector3(.60f, .44f, .60f), .44f, 1f) },
            { "PH_Candelabrum", (new Vector3(.69f, 1.63f, .46f), 1.63f, 1.2f) },
            { "PH_Altar", (new Vector3(3.00f, 1.55f, 1.83f), 1.55f, 3.5f) },
            { "PH_Pew", (new Vector3(2.40f, .98f, .67f), .98f, 2.9f) },
            { "PH_StoneBench", (new Vector3(1.98f, .54f, .64f), .54f, 2.5f) },
            { "PH_WeaponRack", (new Vector3(1.66f, 2.35f, .52f), 2.35f, 2.2f) },
            { "PH_Cage", (new Vector3(1.40f, 2.10f, 1.30f), 2.10f, 1.9f) },
            { "PH_Chains_Wall", (new Vector3(.93f, 1.42f, .13f), 1.42f, 1.4f) },
            { "PH_Banner_Wall", (new Vector3(1.26f, 2.41f, .12f), 2.41f, 1.8f) },
            { "PH_BrokenPillar", (new Vector3(2.78f, 1.44f, 2.88f), 1.44f, 3.4f) },
            { "PH_RubblePile", (new Vector3(1.39f, .77f, 1.10f), .77f, 1.9f) },
            { "PH_Cot", (new Vector3(1.91f, .87f, .79f), .87f, 2.4f) },
            { "PH_Cauldron", (new Vector3(1.10f, .74f, .92f), .74f, 1.6f) },
            { "PH_Lectern", (new Vector3(.58f, 1.20f, .50f), 1.20f, 1f) },
            { "PH_BookPile", (new Vector3(.30f, .30f, .29f), .30f, .8f) },
            { "PH_Buttress_Wall", (new Vector3(1.08f, 5.00f, .56f), 5.00f, 1.6f) },
            { "wooden_bookshelf_worn", (new Vector3(1.5f, 2f, .6f), 2f, 1.5f) },
            { "wine_barrel_01", (new Vector3(1.05f, 1.05f, 1.05f), 1.05f, 1.05f) },
            { "wooden_crate_02", (new Vector3(1.4f, .55f, 1f), .55f, 1.4f) },
            { "WoodenTable_01", (new Vector3(2.65f, .78f, 1.3f), .78f, 2.65f) },
            { "Stores", (new Vector3(3.4f, 2f, .7f), 2f, 1.5f) },
        };
        private static readonly HashSet<string> DecorIds = new() { "PH_BonePile", "PH_SkullHeap_Small", "PH_BookPile", "PH_CandleCluster", "PH_RubblePile" };
        private static readonly HashSet<string> LightIds = new() { "PH_IronBrazier", "PH_CandleCluster", "PH_Candelabrum", "PH_Altar" };

        private static bool IsItem(string n) => n.StartsWith("Floor_") || n.StartsWith("Decor_") || n.StartsWith("Wall_") || n.StartsWith("Top_");

        private static int Hash(string s) { unchecked { int h = 17; foreach (char c in s) h = h * 31 + c; return h; } }

        // Vertical masonry a prop can hang on: non-trigger enabled colliders at least 2.8 m tall (box or mesh; the module perimeter is mesh masonry).
        private static List<Collider> WallColliders(GameObject room)
        {
            var group = room.transform.Find(GroupName);
            return room.GetComponentsInChildren<Collider>(true).Where(c => !c.isTrigger && c.enabled && c.bounds.size.y >= 2.8f
                && !(group != null && c.transform.IsChildOf(group)) && !c.name.StartsWith("Tread") && !c.name.StartsWith("Step")).ToList();
        }

        // Back of a wall prop: ray from 0.3 m in front of its anchor straight back must reach masonry at 0.31 m.
        private static bool WallBacked(Transform item, List<Collider> walls, out Collider wall)
        {
            wall = null;
            var ray = new Ray(item.position + Vector3.up * 1.6f + item.forward * .3f, -item.forward);
            float best = .7f;
            foreach (var c in walls)
                if (c.Raycast(ray, out var h, .7f) && h.distance < best) { best = h.distance; wall = c; }
            return wall != null && Mathf.Abs(best - .31f) < .05f;
        }

        private static Transform WallOwner(Collider wall)
        {
            var t = wall.transform;
            if (t.parent != null && t.parent.name.StartsWith("RuinedWall")) t = t.parent;
            return t;
        }

        private sealed class Furnisher
        {
            internal readonly GameObject Room;
            internal readonly Transform Root;
            internal readonly List<string> Rejected = new();
            private readonly List<Bounds> routes = new();
            // Navigation lanes only colliding furniture must leave free (walk-over decor and hangings may dress them).
            private readonly List<Bounds> solidRoutes = new();
            private readonly List<Renderer> existing;
            private readonly List<Collider> walls;
            private readonly List<Bounds> placedBounds = new();
            private readonly List<Collider> floors = new();
            private readonly List<(Vector2 p, Vector2 n)> wallSpots = new();
            private readonly List<Vector2> freeSpots = new();
            private readonly Dictionary<string, List<Vector2>> placedAt = new();
            private readonly System.Random rng;
            internal int Count, Planned, Lights;
            internal bool Quiet;
            internal Furnisher(GameObject room)
            {
                Room=room;
                var secret=room.transform.Find("Hidden_Reliquary");
                foreach(var t in room.GetComponentsInChildren<Transform>(true).ToArray())
                {
                    if(t==null || t==room.transform || secret!=null && t.IsChildOf(secret))continue;
                    if(t.name==GroupName || t.name=="Room_Props" || t.name=="Authored_Room_Furnishings" || t.name=="WoodenTable_01_Anchor" || t.name==DungeonLayoutLighting.GroupName)
                        Object.DestroyImmediate(t.gameObject);
                }
                Root=new GameObject(GroupName).transform;Root.SetParent(room.transform,false);
                existing=room.GetComponentsInChildren<Renderer>(true).Where(r=>r is MeshRenderer && r.enabled && r.name!="Worn_Flagstones").ToList();
                routes.AddRange(Reservations(room));
                solidRoutes.AddRange(SolidReservations(room));
                rng=new System.Random(Hash(room.name));
                walls=WallColliders(room);
                foreach(var c in room.GetComponentsInChildren<Collider>(true))
                {
                    if(c.isTrigger || !c.enabled || c.transform.IsChildOf(Root))continue;
                    var s=c.bounds.size;
                    if(Mathf.Min(s.x,s.z)>=2f && !c.name.Contains("Parapet"))floors.Add(c);
                }
                BuildSpots();
            }

            private void BuildSpots()
            {
                var seen=new HashSet<int>();
                var dirs=new[]{new Vector2(1,0),new Vector2(-1,0),new Vector2(0,1),new Vector2(0,-1)};
                for(int ix=-17;ix<=17;ix++)for(int iz=-17;iz<=17;iz++)
                {
                    var o=new Vector3(ix,1f,iz);
                    var near=walls.Where(c=>c.bounds.SqrDistance(o)<2.8f).ToArray();
                    if(near.Length==0)continue;
                    for(int di=0;di<4;di++)
                    {
                        var dir=new Vector3(dirs[di].x,0,dirs[di].y);float best=1.6f;RaycastHit bh=default;bool found=false;
                        foreach(var c in near)
                            if(c.Raycast(new Ray(o,dir),out var h,1.6f) && h.distance>.02f && h.distance<best && Mathf.Abs(h.normal.y)<.1f){best=h.distance;bh=h;found=true;}
                        if(!found)continue;
                        var n=-dirs[di]; // wall face points back at the probe
                        int key=((Mathf.RoundToInt(bh.point.x*2)+100)*400+Mathf.RoundToInt(bh.point.z*2)+100)*4+di;
                        if(seen.Add(key))wallSpots.Add((new Vector2(bh.point.x,bh.point.z),n));
                    }
                }
                for(int ix=-11;ix<=11;ix++)for(int iz=-11;iz<=11;iz++)freeSpots.Add(new Vector2(ix*1.5f,iz*1.5f));
            }

            // Top of the real floor/deck at x,z (NaN when there is none).
            private float FloorY(float x,float z)
            {
                float best=float.NaN;var ray=new Ray(new Vector3(x,4.4f,z),Vector3.down);
                foreach(var c in floors)
                {
                    var b=c.bounds;
                    if(x<b.min.x || x>b.max.x || z<b.min.z || z>b.max.z)continue;
                    if(c.Raycast(ray,out var h,12) && h.normal.y>.7f && (float.IsNaN(best) || h.point.y>best))best=h.point.y;
                }
                return best;
            }

            private bool Fits(Vector3 size,float cx,float cz,float yaw,float y,bool solid,bool collides)
            {
                bool swap=Mathf.Abs(Mathf.Sin(yaw*Mathf.Deg2Rad))>.5f;
                var b=new Bounds(new Vector3(cx,y+size.y*.5f,cz),new Vector3(swap?size.z:size.x,size.y,swap?size.x:size.z));
                if(routes.Any(r=>Overlap(r,b,.005f)))return false;
                if(collides && solidRoutes.Any(r=>Overlap(r,b,.005f)))return false;
                return !solid || !HitsCollider(Room,b);
            }

            private void AddCollider(Transform anchor,Bounds bounds,MeshRenderer[] renderers)
            {
                anchor.gameObject.layer=LayerMask.NameToLayer(GameConfig.LevelLayerName);
                // Local bounds before yaw; all imported colliders were removed above.
                var local=new Bounds(anchor.InverseTransformPoint(bounds.center),Vector3.zero);
                foreach(var r in renderers)foreach(var v in Corners(r.localBounds))local.Encapsulate(anchor.InverseTransformPoint(r.transform.TransformPoint(v)));
                var box=anchor.gameObject.AddComponent<BoxCollider>();box.center=local.center;box.size=local.size;
            }

            internal Transform Place(string id,float x,float z,float height,float yaw=0,float y=0,float maxSpan=3.2f,Transform support=null,bool decor=false)
            {
                bool ph=id.StartsWith("PH_",StringComparison.Ordinal);
                GameObject source=ph?DungeonPrimitiveProps.Load(id):(MeshyMapDressing.Load(id) ?? EmberdelvePolyHavenImport.LoadModel(id));
                if(source==null)throw new InvalidOperationException("Missing downloaded furnishing "+id);
                if(!Quiet && support==null)Planned++;
                var anchor=new GameObject((support!=null?"Top_":decor?"Decor_":"Floor_")+id).transform;anchor.SetParent(Root,false);
                var model=Object.Instantiate(source,anchor,false);model.name="Model";
                foreach(var c in model.GetComponentsInChildren<Collider>(true))Object.DestroyImmediate(c);
                var renderers=model.GetComponentsInChildren<MeshRenderer>();
                if(renderers.Length==0)throw new InvalidOperationException("No visible mesh "+id);
                Bounds bounds=BoundsOf(renderers);
                // Placeholder props already have real-world size: never stretch them.
                float scale=ph?1f:Mathf.Min(height/bounds.size.y,maxSpan/Mathf.Max(bounds.size.x,bounds.size.z));
                model.transform.localScale*=scale;bounds=BoundsOf(renderers);
                model.transform.position-=new Vector3(bounds.center.x,bounds.min.y,bounds.center.z);
                anchor.localPosition=new Vector3(x,y,z);anchor.localRotation=Quaternion.Euler(0,yaw,0);
                bounds=BoundsOf(renderers);
                string reason=null;
                if(support==null && routes.Any(b=>Overlap(b,bounds,.005f)))reason="reserved approach";
                if(reason==null && support==null && !decor && solidRoutes.Any(b=>Overlap(b,bounds,.005f)))reason="reserved navigation lane";
                if(reason==null)
                    foreach(var r in existing)
                    {
                        if(support!=null && r.transform.IsChildOf(support))continue;
                        if(Overlap(r.bounds,bounds,.025f)){reason="occupied by "+r.name;break;}
                    }
                // Validate compares a whole placed item against every other renderer part, so do the same here.
                if(reason==null && support==null && placedBounds.Any(ub=>renderers.Any(r=>Overlap(ub,r.bounds,.025f))))reason="inside placed footprint";
                if(reason==null && support==null && HitsCollider(Room,bounds))reason="collision blocker";
                if(reason==null && support==null && !HasSupport(Room,bounds))reason="unsupported footprint";
                if(reason!=null){if(!Quiet)Rejected.Add(id+" "+anchor.localPosition+" "+reason);Object.DestroyImmediate(anchor.gameObject);return null;}
                if(support==null)
                {
                    anchor.gameObject.layer=LayerMask.NameToLayer(GameConfig.LevelLayerName);
                    if(!decor)AddCollider(anchor,bounds,renderers);
                }
                if(support!=null)anchor.SetParent(support,true);
                if(support==null)placedBounds.Add(bounds);
                existing.AddRange(renderers);Count++;return anchor;
            }

            // Wall prop: back (local z=0) 0.01 m off the masonry the probe ray finds behind (or, failing that, in front of) the given yaw.
            internal Transform Wall(string id,float x,float z,float yaw)
            {
                if(!Quiet)Planned++;
                var source=DungeonPrimitiveProps.Load(id);
                if(source==null)throw new InvalidOperationException("Missing placeholder prop "+id);
                float fy=FloorY(x,z);
                if(float.IsNaN(fy)){if(!Quiet)Rejected.Add(id+" ("+x+","+z+") no floor");return null;}
                var origin=new Vector3(x,fy+1.6f,z);
                Vector3 back=Quaternion.Euler(0,yaw,0)*Vector3.back;
                RaycastHit best=default;Collider wall=null;
                foreach(var dir in new[]{back,-back})
                {
                    float bd=4;
                    foreach(var c in walls)
                        if(c.Raycast(new Ray(origin,dir),out var h,4) && h.distance>.05f && h.distance<bd && Mathf.Abs(h.normal.y)<=.1f){bd=h.distance;best=h;wall=c;}
                    if(wall!=null)break;
                }
                if(wall==null){if(!Quiet)Rejected.Add(id+" ("+x+","+z+") no vertical wall");return null;}
                var n=new Vector3(best.normal.x,0,best.normal.z).normalized;
                var anchor=new GameObject("Wall_"+id).transform;anchor.SetParent(Root,false);
                anchor.position=new Vector3(best.point.x,fy,best.point.z)+n*.01f;
                anchor.rotation=Quaternion.LookRotation(n,Vector3.up);
                var model=Object.Instantiate(source,anchor,false);model.name="Model";
                foreach(var c in model.GetComponentsInChildren<Collider>(true))Object.DestroyImmediate(c);
                var renderers=model.GetComponentsInChildren<MeshRenderer>();
                if(renderers.Length==0)throw new InvalidOperationException("No visible mesh "+id);
                var bounds=BoundsOf(renderers);
                string reason=null;
                var owner=WallOwner(wall);
                if(routes.Any(b=>Overlap(b,bounds,.005f)))reason="reserved approach";
                if(reason==null && bounds.max.y>wall.bounds.max.y-.02f)reason="above wall top";
                if(reason==null)
                    foreach(var r in existing)
                    {
                        if(r.transform.IsChildOf(owner))continue;
                        if(Overlap(r.bounds,bounds,.025f)){reason="occupied by "+r.name;break;}
                    }
                if(reason==null && placedBounds.Any(ub=>renderers.Any(r=>Overlap(ub,r.bounds,.025f))))reason="inside placed footprint";
                bool solid=id=="PH_Buttress_Wall";
                if(reason==null && solid && solidRoutes.Any(b=>Overlap(b,bounds,.005f)))reason="reserved navigation lane";
                if(reason==null && solid && HitsCollider(Room,bounds))reason="collision blocker";
                if(reason==null && !WallBacked(anchor,walls,out _))reason="not backed by wall";
                if(reason!=null){if(!Quiet)Rejected.Add(id+" "+anchor.localPosition+" "+reason);Object.DestroyImmediate(anchor.gameObject);return null;}
                anchor.gameObject.layer=LayerMask.NameToLayer(GameConfig.LevelLayerName);
                if(solid)AddCollider(anchor,bounds,renderers);
                placedBounds.Add(bounds);
                existing.AddRange(renderers);Count++;return anchor;
            }

            private Transform PlaceAt(string id,float x,float z,float yaw,float y)
            {
                if(LightIds.Contains(id) && Lights>=MaxLights){if(!Quiet){Planned++;Rejected.Add(id+" light budget");}return null;}
                Transform t;
                if(id=="WoodenTable_01")t=Table(x,z,yaw,y);
                else if(id=="Stores")t=Stores(x,z,yaw,y);
                else{var i=Info[id];t=Place(id,x,z,i.height,yaw,y,i.span,null,DecorIds.Contains(id));}
                if(t!=null && LightIds.Contains(id))Lights++;
                return t;
            }

            // Explicit position (used where the plan needs an exact bay); y comes from the real floor.
            internal Transform At(string id,float x,float z,float yaw)
            {
                float fy=FloorY(x,z);
                if(float.IsNaN(fy)){Planned++;Rejected.Add(id+" ("+x+","+z+") no floor");return null;}
                return PlaceAt(id,x,z,yaw,fy);
            }

            // Candidate search: the first `count` spots (nearest the preferred point, else seeded order) that pass every check.
            internal int Put(string id,int count,M mode,float sep=2f,float nx=99,float nz=99,float jitter=100f,bool perp=false)
            {
                Planned+=count;
                var size=Info[id].size;
                bool hasNear=nx<90;
                var near=new Vector2(nx,nz);
                var cands=new List<(float s,Vector2 p,Vector2 n)>();
                if(mode==M.F)foreach(var p in freeSpots)cands.Add((Score(p,hasNear,near,jitter),p,Vector2.zero));
                else foreach(var w in wallSpots)cands.Add((Score(w.p,hasNear,near,jitter),w.p,w.n));
                cands.Sort((a,b)=>a.s.CompareTo(b.s));
                if(!placedAt.TryGetValue(id,out var prev))placedAt[id]=prev=new List<Vector2>();
                int done=0,tries=0;bool wasQuiet=Quiet;Quiet=true;
                try
                {
                    foreach(var c in cands)
                    {
                        if(done>=count || tries>=70)break;
                        if(prev.Any(q=>Vector2.Distance(q,c.p)<sep))continue;
                        float yaw;Vector2 center;
                        if(mode==M.F){yaw=90f*rng.Next(4);center=c.p;}
                        else if(mode==M.W)
                        {
                            yaw=perp?Mathf.Atan2(-c.n.y,c.n.x)*Mathf.Rad2Deg:Mathf.Atan2(c.n.x,c.n.y)*Mathf.Rad2Deg;
                            center=c.p+c.n*((perp?size.x:size.z)*.5f+.06f);
                        }
                        else{yaw=Mathf.Atan2(c.n.x,c.n.y)*Mathf.Rad2Deg;center=c.p+c.n*(size.z*.5f+.01f);}
                        float fy=FloorY(center.x,center.y);
                        if(float.IsNaN(fy))continue;
                        // Colliding furniture stays on the ground floor: daises, decks, bridges and the sunken crypt are
                        // narrow family routes (walk-over decor may still dress them).
                        bool solidItem=mode!=M.H && !DecorIds.Contains(id);
                        if(solidItem && Mathf.Abs(fy-Room.transform.position.y)>.05f)continue;
                        if(!Fits(size,center.x,center.y,yaw,fy,mode!=M.H || id=="PH_Buttress_Wall",solidItem || id=="PH_Buttress_Wall"))continue;
                        tries++;
                        Transform t=mode==M.H?Wall(id,c.p.x+c.n.x*.5f,c.p.y+c.n.y*.5f,yaw):PlaceAt(id,center.x,center.y,yaw,fy);
                        if(t!=null){done++;prev.Add(c.p);}
                    }
                }
                finally{Quiet=wasQuiet;}
                if(done<count)Rejected.Add(id+" "+mode+" placed "+done+"/"+count+" (tries "+tries+")");
                return done;
            }

            // Removes a placed item (with anything seated on it) that the navigation contract identified as a blocker.
            internal void Remove(Transform item,string reason)
            {
                Rejected.Add(item.name+" "+item.localPosition+" removed: "+reason);
                Count-=1+item.Cast<Transform>().Count(c=>c.name.StartsWith("Top_"));
                Object.DestroyImmediate(item.gameObject);
                existing.RemoveAll(r=>r==null);
            }

            internal int Items()=>Root.Cast<Transform>().Where(t=>IsItem(t.name)).Sum(t=>1+t.Cast<Transform>().Count(c=>c.name.StartsWith("Top_")));

            private float Score(Vector2 p,bool hasNear,Vector2 near,float jitter)
                =>(hasNear?Vector2.Distance(p,near):0f)+(float)rng.NextDouble()*jitter;

            internal Transform Table(float x,float z,float yaw=0,float y=0)
            {
                var t=Place("WoodenTable_01",x,z,.78f,yaw,y,2.65f);
                if(t==null)return null;
                var b=BoundsOf(t.GetComponentsInChildren<MeshRenderer>());
                Place("wooden_candlestick",x,z,.27f,0,b.max.y,.4f,t);
                bool wasQuiet=Quiet;Quiet=true;
                Vector3 side=Quaternion.Euler(0,yaw,0)*Vector3.forward*.95f;
                Place("wooden_stool_01",x+side.x,z+side.z,.46f,0,y,.55f);
                Place("wooden_stool_01",x-side.x,z-side.z,.46f,20,y,.55f);
                Quiet=wasQuiet;
                return t;
            }
            // Bookshelf at the spot (back to the wall for yaw facing out), barrel/crate/bucket spread along the wall.
            internal Transform Stores(float x,float z,float yaw=0,float y=0)
            {
                var rot=Quaternion.Euler(0,yaw,0);
                Vector2 P(float dx,float dz){var v=rot*new Vector3(dx,0,dz);return new Vector2(x+v.x,z+v.z);}
                var shelf=Place("wooden_bookshelf_worn",x,z,2,yaw,y,1.5f);
                if(shelf==null)return null;
                var a=P(-1.25f,0);Place("wine_barrel_01",a.x,a.y,1.05f,0,y,1.05f);
                a=P(1.5f,0);Place("wooden_crate_02",a.x,a.y,.55f,0,y,1.4f);
                a=P(1.6f,1);Place("wooden_bucket_01",a.x,a.y,.48f,0,y);
                return shelf;
            }
        }

        internal static int Apply(GameObject room)
        {
            var d=room.GetComponent<DungeonModuleDefinition>();if(d==null)return 0;
            var f=new Furnisher(room);
            bool infernal=room.name.StartsWith("Infernal"),crypt=room.name.StartsWith("Crypt_Crypt");
            const string Shelf="wooden_bookshelf_worn",Barrel="wine_barrel_01",Crate="wooden_crate_02",Table="WoodenTable_01";
            int buttress=3;
            switch(d.Kind)
            {
                case DungeonModuleKind.Entry: // guard post
                    f.Put("PH_WeaponRack",2,M.W,3,-14,-12,3);
                    f.Put("PH_StoneBench",2,M.W,3,14,-12,3);
                    f.Put("PH_IronBrazier",2,M.W,8);
                    f.Put("PH_Cot",2,M.W,2.2f,-14,13,2);
                    f.Put("Stores",1,M.W,3,-10,14,3);
                    f.Put(Barrel,2,M.W,1.5f,14,14,2);f.Put(Crate,2,M.W,1.5f,14,14,2);
                    f.Put("PH_Cauldron",1,M.F,3,12,12,3);
                    f.Put("PH_Banner_Wall",3,M.H,5);
                    f.Put("PH_CandleCluster",2,M.W,4);
                    break;
                case DungeonModuleKind.Hall:
                    if(d.ModuleId.EndsWith("hall_01")) // barracks
                    {
                        f.Put("PH_Cot",4,M.W,2.1f,-14,9,2);
                        f.Put("PH_WeaponRack",2,M.W,3,14,-9,3);
                        f.Put(Table,1,M.F,4,-8,-10,3);
                        f.Put("PH_IronBrazier",2,M.W,8);
                        f.Put("PH_Chains_Wall",2,M.H,4);
                        f.Put("PH_Banner_Wall",3,M.H,5);
                        f.Put("PH_StoneBench",2,M.W,3,10,12,3);
                        f.Put("Stores",1,M.W,3,10,-14,3);
                    }
                    else if(d.ModuleId.EndsWith("hall_02")) // library / scriptorium
                    {
                        f.Put("PH_Lectern",3,M.F,3,-8,-8,4);
                        f.Put(Shelf,4,M.W,4);
                        f.Put("PH_BookPile",6,M.W,1.8f);
                        f.Put("PH_Candelabrum",2,M.W,5);
                        f.Put("PH_CandleCluster",3,M.W,4);
                        f.Put(Table,1,M.F,4,8,8,3);
                        f.Put("PH_Banner_Wall",2,M.H,5);
                        f.Put("PH_StoneBench",1,M.W,3);
                    }
                    else // prison / armory
                    {
                        f.Put("PH_Cage",3,M.W,2.5f,12,10,3);
                        f.Put("PH_Chains_Wall",4,M.H,3);
                        f.Put("PH_WeaponRack",2,M.W,3,-12,-10,3);
                        f.Put("PH_SkullHeap_Small",4,M.W,1.5f);
                        f.Put("PH_RubblePile",2,M.W,3);
                        f.Put("PH_Cot",1,M.W,3,-12,12,3);
                        f.Put("PH_IronBrazier",1,M.W,8);
                        f.Put("PH_Banner_Wall",2,M.H,5);
                    }
                    break;
                case DungeonModuleKind.Ossuary:
                    if(d.ModuleId.EndsWith("ossuary_01")) // catacomb
                    {
                        f.Put("PH_OssuaryShelf_Wall",5,M.H,4);
                        f.Put("PH_BonePile",4,M.W,2);
                        f.Put("PH_SkullHeap_Small",6,M.W,1.2f);
                        f.Put("PH_CoffinWood",2,M.W,3,0,0,100,true);
                        f.Put("PH_CoffinOpen",1,M.W,3);
                        f.Put("PH_CandleCluster",3,M.W,4);
                        f.Put("PH_Banner_Wall",1,M.H,5);
                    }
                    else // sunken pit
                    {
                        f.Put("PH_BonePile",4,M.F,2,-12,-6,4);
                        f.Put("PH_CoffinOpen",2,M.F,3,-12,-8,4);
                        f.Put("PH_SarcophagusBroken",1,M.W,4);
                        f.Put("PH_RubblePile",4,M.F,2,-12,-6,4);
                        f.Put("PH_SkullHeap_Small",4,M.F,1.5f,-12,-6,4);
                        f.Put("PH_CoffinWood",1,M.W,3);
                        f.Put("PH_CandleCluster",3,M.W,4);
                        f.Put("PH_OssuaryShelf_Wall",2,M.H,4);
                    }
                    break;
                case DungeonModuleKind.Shrine: // chapel
                    buttress=4;
                    foreach(var a in new[]{(3.2f,10.2f,90f),(-3.2f,10.2f,90f),(5f,15.8f,180f),(-5f,15.8f,180f)})
                        if(f.At("PH_Altar",a.Item1,a.Item2,a.Item3)!=null)break;
                    {
                        int pews=0;
                        foreach(float side in new[]{-1f,1f})
                            foreach(float z in new[]{-3.2f,-4.4f,-5.6f,-6.8f,-8f,-9.2f,-10.4f,-11.6f,-12.8f})
                            {
                                if(pews>=8)break;
                                if(f.At("PH_Pew",side*3.9f,z,0)!=null)pews++;
                            }
                    }
                    f.Put("PH_Candelabrum",2,M.F,3,3.6f,8.5f,2);f.Put("PH_Candelabrum",2,M.F,3,-3.6f,8.5f,2);
                    f.Put("PH_CandleCluster",2,M.F,3,5.8f,-5,2);
                    f.Put("PH_Banner_Wall",4,M.H,5);
                    f.Put("PH_StoneBench",2,M.W,3);
                    break;
                case DungeonModuleKind.Vault: // treasury / keep
                    f.Put("PH_Sarcophagus",1,M.W,3);f.Put("PH_SarcophagusBroken",1,M.W,3);
                    f.Put("PH_Cage",2,M.W,3);
                    f.Put("PH_IronBrazier",2,M.W,4);
                    f.Put("PH_BrokenPillar",1,M.F,3);
                    f.Put(Crate,3,M.W,1.5f);f.Put(Barrel,2,M.W,1.5f);
                    f.Put("Stores",1,M.W,3);
                    f.Put("PH_Banner_Wall",3,M.H,4);
                    f.Put("PH_Chains_Wall",2,M.H,3);
                    f.Put("PH_CandleCluster",1,M.W,4);
                    break;
                case DungeonModuleKind.Extraction: // ritual antechamber
                    foreach(float sx in new[]{-1f,1f})foreach(float sz in new[]{-1f,1f})
                        f.Put("PH_IronBrazier",1,M.F,3,sx*6,sz*6,1.5f);
                    f.Put("PH_StoneBench",2,M.W,3);
                    f.Put("PH_CandleCluster",3,M.W,4);
                    f.Put("PH_RubblePile",3,M.W,3);
                    f.Put(Table,1,M.F,4,-10,10,3);
                    f.Put("Stores",1,M.W,3,10,-14,3);
                    f.Put("PH_Banner_Wall",2,M.H,5);
                    break;
                case DungeonModuleKind.Bridge: // ruin
                    f.Put("PH_RubblePile",4,M.F,3);
                    f.Put("PH_BrokenPillar",2,M.F,5);
                    f.Put("PH_Chains_Wall",3,M.H,4);
                    f.Put("PH_CoffinWood",2,M.W,3);
                    f.Put("PH_BonePile",3,M.W,3);
                    f.Put("PH_SkullHeap_Small",2,M.W,2);
                    f.Put(Table,1,M.F,4,-10,-10,3);
                    break;
            }
            // Theme flavour.
            if(infernal){f.Put("PH_IronBrazier",2,M.W,6);f.Put("PH_Cage",1,M.W,4);f.Put("PH_Chains_Wall",2,M.H,4);}
            if(crypt){f.Put("PH_CoffinWood",1,M.W,3);f.Put("PH_CoffinOpen",1,M.W,3);f.Put("PH_SkullHeap_Small",3,M.W,2);}
            // Every module: buttresses on long walls, floor debris at wall feet, a couple of hangings.
            f.Put("PH_Buttress_Wall",buttress,M.H,7);
            f.Put("PH_RubblePile",3,M.W,3);
            f.Put("PH_SkullHeap_Small",3,M.W,2);
            f.Put("PH_BonePile",1,M.W,3);
            f.Put("PH_Banner_Wall",2,M.H,5);
            f.Put("PH_Chains_Wall",1,M.H,5);

            // Measure the real navigation contracts and take out the colliding furniture that breaks them.
            int navRemoved=0;string nav="";
            for(int round=0;;round++)
            {
                var failures=NavContract.Check(room,out nav);
                if(failures.Count==0)break;
                if(round>=24)throw new InvalidOperationException("Navigation contract still broken after repair "+room.name+": "+NavContract.Describe(failures));
                var blockers=NavContract.Blockers(room,failures);
                if(blockers.Count==0)throw new InvalidOperationException("Navigation contract broken without a furnishing cause "+room.name+": "+NavContract.Describe(failures));
                foreach(var b in blockers){f.Remove(b.item,b.reason);navRemoved++;}
            }
            // Walk-over dressing restores the density lost to navigation repairs (no collision, cannot affect navigation).
            foreach(var (id,sep) in new[]{("PH_SkullHeap_Small",1.2f),("PH_RubblePile",2f),("PH_BonePile",2f),(d.Kind==DungeonModuleKind.Hall && d.ModuleId.EndsWith("hall_02")?"PH_BookPile":"PH_SkullHeap_Small",1f)})
            {
                int missing=MinItems+2-f.Items();
                if(missing<=0)break;
                f.Put(id,missing,M.W,sep);
            }

            System.IO.Directory.CreateDirectory("Logs/acceptance/layout-furnishing-20261122");
            string breakdown=string.Join(", ",f.Root.Cast<Transform>().GroupBy(t=>t.name).OrderBy(g=>g.Key).Select(g=>g.Key+"="+g.Count()));
            string header="placed="+f.Items()+" planned="+f.Planned+" rejected="+f.Rejected.Count+" lights="+f.Lights+" navRemoved="+navRemoved+" nav: "+nav+"\nitems: "+breakdown;
            Validate(room);
            DungeonLayoutLighting.Apply(room);
            int removed=RemoveTorchClashes(room,f.Rejected);
            System.IO.File.WriteAllText("Logs/acceptance/layout-furnishing-20261122/"+room.name+"-placement.txt",header+" torchRemoved="+removed+"\n"+string.Join("\n",f.Rejected));
            Validate(room);
            Debug.Log("[LayoutFurnishing] "+room.name+" "+header+" torchRemoved="+removed+"\n"+string.Join("\n",f.Rejected));
            return f.Items();
        }

        // Lighting places sconces after furnishing; anything of ours that clips a sconce is removed (shelves are what the sconces are mounted by).
        private static int RemoveTorchClashes(GameObject room,List<string> log)
        {
            var lighting=room.transform.Find(DungeonLayoutLighting.GroupName);var group=room.transform.Find(GroupName);
            if(lighting==null || group==null)return 0;
            var torches=lighting.GetComponentsInChildren<MeshRenderer>();int removed=0;
            foreach(var item in group.Cast<Transform>().ToArray())
            {
                if(!IsItem(item.name) || item.name.Contains("bookshelf"))continue;
                var rs=item.GetComponentsInChildren<MeshRenderer>();if(rs.Length==0)continue;
                var b=BoundsOf(rs);
                if(torches.Any(t=>Overlap(t.bounds,b,.025f))){log.Add(item.name+" "+item.localPosition+" removed: clips wall torch");Object.DestroyImmediate(item.gameObject);removed++;}
            }
            return removed;
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

        // Physical height routes of the layout families (DungeonLayoutFamilies), room-local, walked by a standing controller.
        private static readonly Dictionary<string, Vector3[]> FamilyRoutes = new()
        {
            { "Layout_Atrium_Courtyard", new[] { new Vector3(0, 0, 6), new Vector3(0, .9f, 10) } },
            { "Layout_Chapel_ProcessionalDais", new[] { new Vector3(0, 0, 2), new Vector3(0, 1.8f, 9) } },
            { "Layout_CenterTower_InnerKeep", new[] { new Vector3(-11, 0, -3), new Vector3(-11, 1.8f, 5) } },
            { "Layout_HBridge_RaisedCrossing", new[] { new Vector3(-12, 0, 6), new Vector3(0, 1.8f, 6), new Vector3(12, 0, 6) } },
            { "Layout_LargeRoomPit_SunkenCrypt", new[] { new Vector3(-12, 0, -16), new Vector3(-12, -1.5f, -9), new Vector3(-12, 0, 4) } },
        };
        private static IEnumerable<Vector3[]> RoutesOf(GameObject room)
            => FamilyRoutes.Where(r => room.transform.Find(r.Key) != null).Select(r => r.Value);

        private static Bounds WorldBox(Transform space, Vector3 min, Vector3 max)
        {
            var local = new Bounds((min + max) * .5f, max - min);
            var b = new Bounds(space.TransformPoint(local.center), Vector3.zero);
            foreach (var v in Corners(local)) b.Encapsulate(space.TransformPoint(v));
            return b;
        }

        // Lanes colliding furniture must leave open; walk-over decor and hung props are still allowed in them.
        private static List<Bounds> SolidReservations(GameObject room)
        {
            var lanes = new List<Bounds>();
            // Every stair keeps a 3 m landing at its foot and 1.5 m at its head along its run, slightly wider than the treads.
            foreach (var stairs in room.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("Layout_Stairs_") || t.name.EndsWith("Stairs")))
            {
                var rs = stairs.GetComponentsInChildren<MeshRenderer>(); if (rs.Length == 0) continue;
                var local = new Bounds(stairs.InverseTransformPoint(rs[0].bounds.center), Vector3.zero);
                foreach (var r in rs) foreach (var v in Corners(r.bounds)) local.Encapsulate(stairs.InverseTransformPoint(v));
                lanes.Add(WorldBox(stairs, local.min - new Vector3(.4f, 1, 3), local.max + new Vector3(.4f, 3, 1.5f)));
            }
            // Family height routes: the controller walks straight between waypoints (radius .34 m).
            foreach (var route in RoutesOf(room))
                for (int i = 1; i < route.Length; i++)
                    lanes.Add(WorldBox(room.transform, Vector3.Min(route[i - 1], route[i]) - new Vector3(.8f, 2, .8f),
                        Vector3.Max(route[i - 1], route[i]) + new Vector3(.8f, 3, .8f)));
            // Side chambers: the doorway, its outside approach and the aisle to the chamber centre.
            foreach (var chamber in room.GetComponentsInChildren<Transform>().Where(t => t.name == "Side_Chamber"))
            {
                float front = ChamberFront(chamber);
                if (float.IsInfinity(front)) continue;
                lanes.Add(WorldBox(chamber, new Vector3(-1.4f, -.5f, front - 2.5f), new Vector3(1.4f, 4, 1.2f)));
            }
            // Secret cache: lever approach and the cell in front of the concealed panel.
            foreach (var secret in room.GetComponentsInChildren<DungeonDoorInteractable>(true).Where(s => s.IsSecret))
            {
                Vector3 lever = room.transform.InverseTransformPoint(secret.transform.position);
                lanes.Add(WorldBox(room.transform, new Vector3(lever.x - 1.2f, -.5f, lever.z - 2.6f), new Vector3(lever.x + 1.2f, 4, lever.z + .2f)));
            }
            return lanes;
        }

        // Chamber-local z of the doorway wall face (same derivation as the access fixture: from the saved wall collision).
        private static float ChamberFront(Transform chamber)
        {
            float front = float.PositiveInfinity;
            foreach (var wall in chamber.GetComponentsInChildren<BoxCollider>())
                for (int corner = 0; corner < 8; corner++)
                {
                    var sign = new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1);
                    front = Mathf.Min(front, chamber.InverseTransformPoint(wall.transform.TransformPoint(wall.center + Vector3.Scale(wall.size * .5f, sign))).z);
                }
            return front;
        }

        // Navigation contracts of a furnished module, measured with the runtime baker (NavGridSurface) on a hidden temporary
        // copy in the editor physics scene. Cells are 2 m centred on odd room-local metres, as the assembler (18-cell modules)
        // and the level fixtures bake them. The baseline is the same copy with the furnishing group disabled, so every
        // difference is caused by furnishing:
        //  - furniture never becomes (or changes) a walking surface;
        //  - ports, centre, family route waypoints, side-chamber doorways/interior cells, the secret lever approach and cache
        //    anchor keep their cell, height, 4-neighbour reachability and standing clearance, and the cache keeps chest room;
        //  - no furniture-made pockets: every cell still walkable and reachable before stays reachable;
        //  - most spawn/chest-clear reachable ground cells survive (content, chest and party spawn placement).
        internal static class NavContract
        {
            private const int N = 18;
            private const float MinClearShare = .8f;
            private static readonly Vector3 Far = new Vector3(-21000, 0, 21000);
            private static readonly Vector3 SealedChest = new Vector3(1.35f, 1.05f, .95f);
            private static int LevelMask => LayerMask.GetMask(GameConfig.LevelLayerName);

            internal sealed class Failure { internal string Message; internal readonly List<Vector3> Path = new(); internal bool Chain; internal float Radius = .3f; }
            private sealed class Snap { internal NavGridData Grid; internal readonly HashSet<int> Reach = new(); internal readonly HashSet<int> Clear = new(); }
            private readonly struct Key { internal readonly Vector3 P; internal readonly string Label; internal readonly bool Chest;
                internal Key(Vector3 p, string label, bool chest = false) { P = p; Label = label; Chest = chest; } }

            private static int Cell(float v) => Mathf.FloorToInt((v + 18f) / 2f);
            private static int Index(Vector3 local) { int x = Cell(local.x), z = Cell(local.z); return x < 0 || z < 0 || x >= N || z >= N ? -1 : z * N + x; }
            private static Vector3 Centre(Snap s, int i) => new Vector3(-17 + 2 * (i % N), s.Grid.IsWalkable(i % N, i / N) ? s.Grid.surfaceHeights[i] : 0, -17 + 2 * (i / N));
            private static bool Walk(Snap s, int i) => i >= 0 && s.Grid.IsWalkable(i % N, i / N);

            private static GameObject Spawn(GameObject room, bool furnished)
            {
                var clone = Object.Instantiate(room);
                var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (clone.scene != active) UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(clone, active);
                foreach (var t in clone.GetComponentsInChildren<Transform>(true)) t.gameObject.hideFlags = HideFlags.HideAndDontSave;
                clone.transform.SetPositionAndRotation(Far, Quaternion.identity);
                var definition = clone.GetComponent<DungeonModuleDefinition>();
                foreach (var socket in new[] { DungeonModuleSockets.North, DungeonModuleSockets.East, DungeonModuleSockets.South, DungeonModuleSockets.West })
                { var closure = definition != null ? definition.Closure(socket) : null; if (closure != null) closure.gameObject.SetActive(false); }
                var group = clone.transform.Find(GroupName);
                if (group != null) group.gameObject.SetActive(furnished);
                Physics.SyncTransforms();
                return clone;
            }

            private static bool Inside(Collider c, GameObject clone) => c != null && !c.isTrigger && c.transform.IsChildOf(clone.transform);
            private static bool HitsAny(Collider[] hits, GameObject clone, Transform only = null)
                => hits.Any(c => Inside(c, clone) && (only == null || c.transform.IsChildOf(only)));

            private static Snap Bake(GameObject clone)
            {
                var s = new Snap { Grid = ScriptableObject.CreateInstance<NavGridData>() };
                var g = s.Grid; g.hideFlags = HideFlags.HideAndDontSave;
                g.origin = Far - new Vector3(18, 0, 18); g.width = g.height = N; g.cellSize = 2;
                // Runtime mask (DungeonLayoutData.BuildWalkableGrid): the module room is cells 1..16; the outer ring is only
                // open where a two-cell socket corridor (cells 8 and 9) crosses it, so no detour may use the wall ring.
                var mask = new char[N * N];
                for (int z = 0; z < N; z++) for (int x = 0; x < N; x++)
                {
                    bool ring = x == 0 || z == 0 || x == N - 1 || z == N - 1;
                    bool corridor = (x == 8 || x == 9) && (z == 0 || z == N - 1) || (z == 8 || z == 9) && (x == 0 || x == N - 1);
                    mask[z * N + x] = !ring || corridor ? '.' : '#';
                }
                g.mask = new string(mask);
                NavGridSurface.Bake(g, clone.transform, LevelMask);
                var queue = new Queue<int>();
                // One seed (the centre, which every family keeps connected to all ports), so a room split in two by
                // furniture is caught even when each part still touches some port.
                foreach (var p in new[] { Vector3.zero, new Vector3(0, 0, 17), new Vector3(17, 0, 0), new Vector3(0, 0, -17), new Vector3(-17, 0, 0) })
                { int i = Index(p); if (Walk(s, i)) { s.Reach.Add(i); queue.Enqueue(i); break; } }
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue(), x = i % N, z = i / N;
                    foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        int nx = x + dx, nz = z + dz, n = nz * N + nx;
                        if (!g.IsInside(nx, nz) || s.Reach.Contains(n) || !g.CanTraverse(x, z, nx, nz)) continue;
                        s.Reach.Add(n); queue.Enqueue(n);
                    }
                }
                // Runtime spawn/content candidates: interior module cells, reachable ground, player capsule and a sealed chest fit.
                var secrets = clone.GetComponentsInChildren<DungeonDoorInteractable>(true);
                foreach (int i in s.Reach)
                {
                    int x = i % N, z = i / N;
                    if (x < 1 || z < 1 || x > N - 2 || z > N - 2 || Mathf.Abs(g.surfaceHeights[i]) > .1f) continue;
                    Vector3 ground = Far + new Vector3(-17 + 2 * x, 0, -17 + 2 * z);
                    if (secrets.Any(d => d.ContainsSecretRoomPoint(ground + Vector3.up * .15f))) continue;
                    Vector3 root = ground + Vector3.up * .15f;
                    if (HitsAny(Physics.OverlapCapsule(root + Vector3.up * .38f, root + Vector3.up * 1.44f, .36f, Physics.AllLayers, QueryTriggerInteraction.Ignore), clone)) continue;
                    if (HitsAny(Physics.OverlapBox(ground + Vector3.up * (SealedChest.y * .5f + .05f), SealedChest * .5f, Quaternion.identity, Physics.AllLayers, QueryTriggerInteraction.Ignore), clone)) continue;
                    s.Clear.Add(i);
                }
                return s;
            }

            private static List<Key> Keys(GameObject clone)
            {
                var keys = new List<Key> { new(new Vector3(0, 0, 17), "north port"), new(new Vector3(17, 0, 0), "east port"),
                    new(new Vector3(0, 0, -17), "south port"), new(new Vector3(-17, 0, 0), "west port"), new(Vector3.zero, "room centre") };
                foreach (var route in RoutesOf(clone)) for (int i = 0; i < route.Length; i++) keys.Add(new Key(route[i], "family route waypoint " + i));
                foreach (var chamber in clone.GetComponentsInChildren<Transform>().Where(t => t.name == "Side_Chamber"))
                {
                    float front = ChamberFront(chamber);
                    if (float.IsInfinity(front)) continue;
                    keys.Add(new Key(chamber.TransformPoint(new Vector3(0, 0, front + 1.5f)) - Far, "side chamber doorway"));
                    keys.Add(new Key(chamber.position - Far, "side chamber centre"));
                    var walls = chamber.GetComponentsInChildren<BoxCollider>().Select(c => c.bounds).ToArray();
                    var area = walls[0]; foreach (var w in walls) area.Encapsulate(w);
                    for (int x = 0; x < N; x++) for (int z = 0; z < N; z++)
                    {
                        var p = new Vector3(-17 + 2 * x, 0, -17 + 2 * z);
                        if (area.Contains(new Vector3(p.x + Far.x, area.center.y, p.z + Far.z))) keys.Add(new Key(p, "side chamber interior"));
                    }
                }
                foreach (var secret in clone.GetComponentsInChildren<DungeonDoorInteractable>(true).Where(s => s.IsSecret))
                {
                    Vector3 approach = secret.transform.position - clone.transform.forward * 1.5f - Far; approach.y = 0;
                    keys.Add(new Key(approach, "secret lever approach"));
                    var anchor = secret.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "SecretChestAnchor");
                    if (anchor != null) { var p = anchor.position - Far; p.y = 0; keys.Add(new Key(p, "secret cache chest", true)); }
                }
                return keys;
            }

            private static bool StandingClear(GameObject clone, Vector3 local, Transform only)
            {
                Vector3 p = Far + local;
                return !HitsAny(Physics.OverlapCapsule(p + Vector3.up * .55f, p + Vector3.up * 1.5f, .34f, LevelMask, QueryTriggerInteraction.Ignore), clone, only);
            }

            // Shortest baseline 4-neighbour route from an unreachable cell back to the furnished reachable set.
            private static List<int> BaselinePath(Snap b, Snap f, int start)
            {
                var from = new Dictionary<int, int> { { start, -1 } }; var queue = new Queue<int>(); queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue();
                    if (f.Reach.Contains(i)) { var path = new List<int>(); for (int c = i; c >= 0; c = from[c]) path.Add(c); return path; }
                    int x = i % N, z = i / N;
                    foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        int nx = x + dx, nz = z + dz, n = nz * N + nx;
                        if (!b.Grid.IsInside(nx, nz) || from.ContainsKey(n) || !b.Grid.CanTraverse(x, z, nx, nz)) continue;
                        from[n] = i; queue.Enqueue(n);
                    }
                }
                return new List<int> { start };
            }

            private static Failure Unreachable(Snap b, Snap f, int cell, string message)
            {
                var fail = new Failure { Message = message, Chain = true };
                foreach (int i in BaselinePath(b, f, cell)) fail.Path.Add(Centre(b, i));
                return fail;
            }

            internal static List<Failure> Check(GameObject room, out string summary)
            {
                var fails = new List<Failure>();
                GameObject clone = null; Snap b = null, f = null;
                try
                {
                    clone = Spawn(room, false);
                    b = Bake(clone);
                    var keys = Keys(clone).Where(k => { int i = Index(k.P);
                        return Walk(b, i) && b.Reach.Contains(i) && Mathf.Abs(b.Grid.surfaceHeights[i] - k.P.y) < .035f && StandingClear(clone, k.P, null); }).ToList();
                    Object.DestroyImmediate(clone); clone = null;
                    clone = Spawn(room, true);
                    var furniture = clone.transform.Find(GroupName);
                    f = Bake(clone);
                    // A cell whose centre is covered by furniture reports the furniture top as its surface; that is an occupied
                    // cell unless the top joins the walk graph (then players and enemies would route over furniture).
                    for (int i = 0; i < N * N; i++)
                    {
                        if (!Walk(f, i) || !f.Reach.Contains(i)) continue;
                        if (!Walk(b, i) || Mathf.Abs(f.Grid.surfaceHeights[i] - b.Grid.surfaceHeights[i]) > .035f)
                        { var fail = new Failure { Message = "furniture top joined the walk graph at " + Centre(f, i), Radius = .35f }; fail.Path.Add(Centre(b, i)); fails.Add(fail); }
                    }
                    foreach (var k in keys)
                    {
                        int i = Index(k.P);
                        if (!Walk(f, i) || Mathf.Abs(f.Grid.surfaceHeights[i] - b.Grid.surfaceHeights[i]) > .035f)
                        { var fail = new Failure { Message = k.Label + " cell blocked at " + k.P, Radius = .45f }; fail.Path.Add(k.P); fail.Path.Add(Centre(b, i)); fails.Add(fail); }
                        else if (!f.Reach.Contains(i)) fails.Add(Unreachable(b, f, i, k.Label + " unreachable at " + k.P));
                        else if (furniture != null && !StandingClear(clone, k.P, furniture))
                        { var fail = new Failure { Message = k.Label + " has no standing clearance at " + k.P, Radius = .5f }; fail.Path.Add(k.P); fails.Add(fail); }
                        if (k.Chest && furniture != null && HitsAny(Physics.OverlapBox(Far + k.P + Vector3.up * (SealedChest.y * .5f + .05f), SealedChest * .5f + Vector3.one * .1f,
                                Quaternion.identity, Physics.AllLayers, QueryTriggerInteraction.Ignore), clone, furniture))
                        { var fail = new Failure { Message = "secret cache chest obstructed at " + k.P, Radius = 1.1f }; fail.Path.Add(k.P); fails.Add(fail); }
                    }
                    // Pockets: still walkable, reachable before furnishing, unreachable now (one failure per pocket).
                    var pocket = new HashSet<int>(Enumerable.Range(0, N * N).Where(i => Walk(f, i) && b.Reach.Contains(i) && !f.Reach.Contains(i)
                        && Mathf.Abs(f.Grid.surfaceHeights[i] - b.Grid.surfaceHeights[i]) <= .035f));
                    while (pocket.Count > 0)
                    {
                        int seed = pocket.First();
                        fails.Add(Unreachable(b, f, seed, "furniture cut off reachable floor at " + Centre(b, seed)));
                        var stack = new Stack<int>(); stack.Push(seed); pocket.Remove(seed);
                        while (stack.Count > 0)
                        {
                            int i = stack.Pop();
                            foreach (int n in new[] { i + 1, i - 1, i + N, i - N }) if (pocket.Remove(n)) stack.Push(n);
                        }
                    }
                    int need = Mathf.CeilToInt(b.Clear.Count * MinClearShare);
                    summary = "reach " + f.Reach.Count + "/" + b.Reach.Count + " clearGround " + f.Clear.Count + "/" + b.Clear.Count + " keys " + keys.Count;
                    if (f.Clear.Count < need)
                    {
                        var fail = new Failure { Message = "only " + f.Clear.Count + "/" + b.Clear.Count + " spawn/chest-clear ground cells remain (need " + need + ")", Radius = .9f };
                        foreach (int i in b.Clear) if (!f.Clear.Contains(i)) fail.Path.Add(Centre(b, i));
                        fails.Add(fail);
                    }
                }
                finally
                {
                    if (clone != null) Object.DestroyImmediate(clone);
                    if (b != null) Object.DestroyImmediate(b.Grid);
                    if (f != null) Object.DestroyImmediate(f.Grid);
                }
                return fails;
            }

            internal static string Describe(List<Failure> fails)
                => string.Join("; ", fails.Take(6).Select(x => x.Message)) + (fails.Count > 6 ? " (+" + (fails.Count - 6) + " more)" : "");

            // Colliding furnishing items near each failure (room-local XZ distance from the collider box, matching height band).
            internal static List<(Transform item, string reason)> Blockers(GameObject room, List<Failure> fails)
            {
                var group = room.transform.Find(GroupName);
                var result = new List<(Transform, string)>();
                if (group == null) return result;
                var solids = new List<(Transform t, Bounds b)>();
                foreach (Transform item in group)
                {
                    var cs = item.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger && c.enabled).ToArray();
                    if (cs.Length == 0) continue;
                    var lb = new Bounds(room.transform.InverseTransformPoint(cs[0].bounds.center), Vector3.zero);
                    foreach (var c in cs) foreach (var v in Corners(c.bounds)) lb.Encapsulate(room.transform.InverseTransformPoint(v));
                    solids.Add((item, lb));
                }
                foreach (var fail in fails)
                {
                    var points = new List<Vector3>();
                    if (fail.Chain && fail.Path.Count > 1)
                        for (int i = 1; i < fail.Path.Count; i++)
                            for (int s = 0; s <= 20; s++) points.Add(Vector3.Lerp(fail.Path[i - 1], fail.Path[i], s / 20f));
                    else points.AddRange(fail.Path);
                    bool capacity = fail.Message.StartsWith("only ");
                    var hits = solids.Where(s => !result.Any(r => r.Item1 == s.t))
                        .Select(s => (s, n: points.Count(p => p.y < s.b.max.y && p.y + 1.8f > s.b.min.y
                            && Mathf.Max(0, Mathf.Abs(p.x - s.b.center.x) - s.b.extents.x) * Mathf.Max(0, Mathf.Abs(p.x - s.b.center.x) - s.b.extents.x)
                             + Mathf.Max(0, Mathf.Abs(p.z - s.b.center.z) - s.b.extents.z) * Mathf.Max(0, Mathf.Abs(p.z - s.b.center.z) - s.b.extents.z) < fail.Radius * fail.Radius)))
                        .Where(h => h.n > 0).OrderByDescending(h => h.n).ToList();
                    // A capacity shortfall removes the two items that cover the most lost cells; the rest fix every touching item.
                    foreach (var h in capacity ? hits.Take(2) : hits) result.Add((h.s.t, fail.Message));
                    if (hits.Count == 0 && !capacity && fail.Path.Count > 0)
                    {
                        // Fallback: nearest colliding item within 3 m of the failing cell.
                        var p = fail.Path[0];
                        var near = solids.Where(s => !result.Any(r => r.Item1 == s.t)).OrderBy(s => (new Vector2(s.b.center.x, s.b.center.z) - new Vector2(p.x, p.z)).sqrMagnitude).FirstOrDefault();
                        if (near.t != null && (new Vector2(near.b.center.x, near.b.center.z) - new Vector2(p.x, p.z)).magnitude < 3f + near.b.extents.magnitude)
                            result.Add((near.t, fail.Message + " (nearest)"));
                    }
                }
                return result;
            }
        }

        public static string NavReportSaved()
        {
            var lines = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Emberdelve/Prefabs/DungeonModules" }).OrderBy(AssetDatabase.GUIDToAssetPath))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid); var root = PrefabUtility.LoadPrefabContents(path);
                try { var fails = NavContract.Check(root, out string summary); lines.Add(root.name + " " + summary + (fails.Count > 0 ? " FAIL " + fails.Count + ": " + NavContract.Describe(fails) : " ok")); }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            return string.Join("\n", lines);
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
            int count=0;var reserved=Reservations(room);var lanes=SolidReservations(room);var walls=WallColliders(room);
            // Items are direct children of the group, plus tabletop pieces seated on a Floor_ item.
            var items=new List<Transform>();
            foreach(Transform c in group)
            {
                if(IsItem(c.name))items.Add(c);
                foreach(Transform g in c)if(g.name.StartsWith("Top_"))items.Add(g);
            }
            foreach(var item in items)
            {
                var b=BoundsOf(item.Find("Model").GetComponentsInChildren<MeshRenderer>());
                Transform owner=null;
                if(item.name.StartsWith("Floor_") || item.name.StartsWith("Decor_"))
                {
                    if(reserved.Any(r=>Overlap(r,b,.005f)) || HitsCollider(room,b))throw new InvalidOperationException("Blocked furnishing approach "+room.name+"/"+item.name);
                    if(!HasSupport(room,b))throw new InvalidOperationException("Unsupported furnishing "+room.name+"/"+item.name);
                    if(item.name.StartsWith("Floor_") && item.GetComponent<BoxCollider>()==null)throw new InvalidOperationException("Missing furniture collision");
                    if(item.name.StartsWith("Decor_") && item.GetComponent<Collider>()!=null)throw new InvalidOperationException("Walk-over decor must not collide "+room.name+"/"+item.name);
                }
                else if(item.name.StartsWith("Wall_"))
                {
                    if(reserved.Any(r=>Overlap(r,b,.005f)))throw new InvalidOperationException("Blocked furnishing approach "+room.name+"/"+item.name);
                    if(!WallBacked(item,walls,out var wall))throw new InvalidOperationException("Wall prop not on a wall face "+room.name+"/"+item.name);
                    owner=WallOwner(wall);
                    bool solid=item.name=="Wall_PH_Buttress_Wall";
                    if(solid && item.GetComponent<BoxCollider>()==null)throw new InvalidOperationException("Missing buttress collision "+room.name);
                    if(!solid && item.GetComponent<Collider>()!=null)throw new InvalidOperationException("Hung prop must not collide "+room.name+"/"+item.name);
                }
                else
                {
                    if(!item.parent.name.StartsWith("Floor_"))throw new InvalidOperationException("Missing tabletop support "+item.name);
                    var table=BoundsOf(item.parent.Find("Model").GetComponentsInChildren<MeshRenderer>());
                    if(Mathf.Abs(table.max.y-b.min.y)>.006f || b.min.x<table.min.x+.02f || b.max.x>table.max.x-.02f || b.min.z<table.min.z+.02f || b.max.z>table.max.z-.02f)
                        throw new InvalidOperationException("Unseated tabletop decoration "+room.name+"/"+item.name);
                }
                if(item.GetComponent<Collider>()!=null && lanes.Any(r=>Overlap(r,b,.005f)))
                    throw new InvalidOperationException("Colliding furnishing in a reserved navigation lane "+room.name+"/"+item.name+" "+item.localPosition);
                foreach(var other in room.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if(!other.enabled || other.name=="Worn_Flagstones" || other.transform.IsChildOf(item))continue;
                    if(owner!=null && other.transform.IsChildOf(owner))continue;
                    if(item.name.StartsWith("Top_") && other.transform.IsChildOf(item.parent.Find("Model")))continue;
                    if(Overlap(b,other.bounds,.025f))throw new InvalidOperationException("Furnishing intersects "+room.name+"/"+item.name+" with "+other.name);
                }
                count++;
            }
            if(count<MinItems)throw new InvalidOperationException("Furnishing plan incomplete "+room.name+" count="+count);
            var navFailures=NavContract.Check(room,out string nav);
            if(navFailures.Count>0)throw new InvalidOperationException("Furnishing breaks navigation contract "+room.name+" ("+nav+"): "+NavContract.Describe(navFailures));
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
