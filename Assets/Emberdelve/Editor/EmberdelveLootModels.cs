using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Player;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.EditorTools
{
    /// <summary>Original treasure/ammunition/ranged ground models, plus the existing licensed melee models.
    /// Authoring only: saved assets, no runtime primitive construction or item-rule changes.</summary>
    public static class EmberdelveLootModels
    {
        const string Art = "Assets/Emberdelve/Art/Models/Loot";
        const string Prefabs = "Assets/Emberdelve/Prefabs/Items/Loot";
        const string Catalog = "Assets/Emberdelve/Resources/Emberdelve/ItemVisualCatalog.asset";
        const string Evidence = "Logs/acceptance/loot-models-20261113";
        static Material gold, bronze, steel, wood, cord, feather, gem, dark;
        static string itemId;
        static Dictionary<Material, Geometry> shapes;

        public static void Build()
        {
            if (!Application.isBatchMode || !Application.dataPath.Replace('\\', '/').Contains("DakaDaka_ci/"))
                throw new InvalidOperationException("Loot model authoring must run in graphics batch on DakaDaka_ci");
            EmberdelveVisualSetup.EnsureFolder(Art);
            EmberdelveVisualSetup.EnsureFolder(Prefabs);
            Directory.CreateDirectory(Evidence);
            gold = Surface("WornGold", new Color(.59f,.37f,.095f), .88f,.40f);
            bronze = Surface("AgedBronze", new Color(.28f,.16f,.060f), .75f,.28f);
            steel = Surface("ForgedIron", new Color(.24f,.265f,.27f), .85f,.36f);
            wood = Surface("AshWood", new Color(.25f,.115f,.045f), 0,.22f, true);
            cord = Surface("FlaxCord", new Color(.40f,.31f,.18f), 0,.13f);
            feather = Surface("GreyFeather", new Color(.38f,.40f,.37f), 0,.15f);
            gem = Surface("EmberGlass", new Color(.48f,.055f,.014f), .30f,.79f);
            dark = Surface("DarkLeather", new Color(.065f,.033f,.017f), 0,.22f);
            var catalog = AssetDatabase.LoadAssetAtPath<ItemVisualCatalog>(Catalog);
            if (catalog == null) throw new InvalidOperationException("Existing visual catalog is required");
            var entries = new List<ItemVisualCatalog.Entry>(catalog.entries);
            var items = AssetDatabase.FindAssets("t:ItemData",new[]{"Assets/Emberdelve/Data"})
                .Select(g=>AssetDatabase.LoadAssetAtPath<ItemData>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(i=>i!=null).OrderBy(i=>i.itemId).ToArray();
            var report = new List<string>();
            int authored = 0;
            foreach(var item in items)
            {
                var current=entries.FirstOrDefault(e=>e!=null && e.itemId==item.itemId);
                // Preserve armor, torch and any previously authored content outside this folder.
                if (current?.worldPrefab != null && !AssetDatabase.GetAssetPath(current.worldPrefab).StartsWith(Prefabs+"/",StringComparison.Ordinal)) continue;
                if(item.IsBandage || item.healAmount>0) { report.Add(item.itemId+": existing runtime bottle/linen model"); continue; }
                itemId=item.itemId;
                shapes=new Dictionary<Material,Geometry>();
                var root=new GameObject("Loot_"+itemId);
                try
                {
                    if(!ReuseWeapon(item,root))
                    {
                        switch(itemId)
                        {
                            case "ember_coin": Coins(); break;
                            case "ashen_relic": Relic(); break;
                            case "cinder_band_ring": Ring(); break;
                            case "emberbead_necklace": Necklace(); break;
                            case "arrow_bundle": Ammunition(false,true); break;
                            case "bolt_bundle": Ammunition(true,true); break;
                            case "recovered_arrow": Ammunition(false,false); break;
                            case "recovered_bolt": Ammunition(true,false); break;
                            case "hollow_bow": Bow(); break;
                            case "ember_crossbow": Crossbow(); break;
                            default: throw new InvalidOperationException("Missing explicit loot art for "+itemId);
                        }
                        foreach(var pair in shapes)
                        {
                            var part=new GameObject(pair.Key.name,typeof(MeshFilter),typeof(MeshRenderer));
                            part.transform.SetParent(root.transform,false);
                            part.GetComponent<MeshFilter>().sharedMesh=SaveMesh(pair.Value.Make(itemId+"_"+pair.Key.name));
                            part.GetComponent<MeshRenderer>().sharedMaterial=pair.Key;
                        }
                    }
                    var renderers=root.GetComponentsInChildren<Renderer>();
                    if(renderers.Length==0)throw new InvalidOperationException("Empty model: "+itemId);
                    var bounds=renderers[0].bounds;foreach(var r in renderers)bounds.Encapsulate(r.bounds);
                    foreach(Transform child in root.transform)child.localPosition-=bounds.center;
                    int triangles=root.GetComponentsInChildren<MeshFilter>().Sum(f=>f.sharedMesh.triangles.Length/3);
                    var prefab=PrefabUtility.SaveAsPrefabAsset(root,Prefabs+"/Loot_"+itemId+".prefab");
                    if(current==null){current=new ItemVisualCatalog.Entry{itemId=itemId};entries.Add(current);}
                    current.worldPrefab=prefab;
                    EmberdelveItemVisualBake.Render(prefab,Evidence+"/"+itemId+".png",800,false);
                    report.Add(itemId+": triangles="+triangles+", bounds="+bounds.size+", materials="+renderers.Length);
                    authored++;
                }
                finally{Object.DestroyImmediate(root);}
            }
            foreach(var item in items)
                if(!(item.IsBandage || item.healAmount>0) && !entries.Any(e=>e!=null && e.itemId==item.itemId && e.worldPrefab!=null))
                    throw new InvalidOperationException("Uncovered item: "+item.itemId);
            catalog.entries=entries.ToArray();EditorUtility.SetDirty(catalog);AssetDatabase.SaveAssets();
            foreach(var item in items)
            {
                var pickupRoot=GameObject.CreatePrimitive(PrimitiveType.Cube);
                try
                {
                    pickupRoot.name="GroundReview_"+item.itemId;
                    pickupRoot.transform.localScale=item.worldSize;
                    var pickup=pickupRoot.AddComponent<LootPickup>();
                    pickup.Configure(item,1,null);
                    int childCount=pickupRoot.transform.childCount;
                    pickup.Configure(item,1,null);
                    if(pickupRoot.GetComponent<Renderer>().enabled)
                        throw new InvalidOperationException("Visible proxy remains: "+item.itemId);
                    if(childCount!=1 || pickupRoot.transform.childCount!=1)
                        throw new InvalidOperationException("Duplicate/missing visual: "+item.itemId);
                    if(!pickupRoot.GetComponentsInChildren<MeshRenderer>().Any(r=>r.enabled && r.gameObject!=pickupRoot && r.GetComponent<MeshFilter>()?.sharedMesh!=null))
                        throw new InvalidOperationException("No visible loot mesh: "+item.itemId);
                    if(pickupRoot.transform.Find("RecoveredShaft")!=null)
                        throw new InvalidOperationException("Obsolete blocky ammo shaft remains: "+item.itemId);
                    report.Add(item.itemId+": Configure twice PASS, root proxy hidden, one visible model");
                    if(item.itemId=="ember_coin" || item.itemId=="ashen_relic" || item.itemId=="cinder_band_ring" || item.itemId=="emberbead_necklace")
                        EmberdelveItemVisualBake.Render(pickupRoot,Evidence+"/ground-"+item.itemId+".png",800,false,null,new Vector3(.65f,2f,2f));
                }
                finally{Object.DestroyImmediate(pickupRoot);}
            }
            File.WriteAllLines(Evidence+"/assets.txt",report);
            Debug.Log("[LootModels] PASS items="+items.Length+" authored="+authored+" catalog="+entries.Count+" (consumables retain existing runtime art)");
        }

        static bool ReuseWeapon(ItemData item,GameObject root)
        {
            if(item.grantedWeapon==null)return false;
            var entry=WeaponViewModelSet.Load()?.Find(item.grantedWeapon.weaponId);
            if(entry?.prefab==null)return false;
            var model=Object.Instantiate(entry.prefab,root.transform,false);
            // Ground art has no animation/gameplay/FX components. Meshes/materials retain their original provenance.
            foreach(var component in model.GetComponentsInChildren<Component>(true))
                if(component!=null && !(component is Transform) && !(component is MeshFilter) && !(component is MeshRenderer))
                    Object.DestroyImmediate(component);
            foreach(var renderer in model.GetComponentsInChildren<MeshRenderer>())renderer.enabled=true;
            return true;
        }
        static Geometry G(Material m)
        { if(!shapes.TryGetValue(m,out var value)){value=new Geometry();shapes.Add(m,value);}return value; }
        static Vector3 V(float x,float y,float z=0)=>new Vector3(x,y,z);
        static void Tube(Material m,IList<Vector3> line,float radius,int sides=8)=>G(m).Tube(line,radius,sides);
        static void Line(Material m,Vector3 a,Vector3 b,float radius,int sides=8)=>Tube(m,new[]{a,b},radius,sides);
        static void Loop(Material m,Vector3 center,float rx,float ry,float radius,int segments=48)
        {
            var points=new List<Vector3>();for(int i=0;i<=segments;i++){float t=i*Mathf.PI*2/segments;points.Add(center+V(Mathf.Cos(t)*rx,Mathf.Sin(t)*ry));}
            Tube(m,points,radius);
        }
        static void Bead(Material m,Vector3 center,Vector3 size,int sides=12,int rings=6)=>G(m).Ellipsoid(center,size,sides,rings);

        static void Coins()
        {
            // A small uneven purse spill: bevelled, struck faces and raised rim, not a yellow block.
            for(int i=0;i<7;i++)
            {
                float t=i*2.39996f,r=i==0?0:.022f+.012f*(i%3);
                var c=V(Mathf.Cos(t)*r,Mathf.Sin(t)*r,.0035f*i);
                Coin(c,.029f);
            }
        }
        static void Coin(Vector3 c,float radius)
        {
            G(gold).Disc(c,radius,.003f,40);
            Loop(bronze,c+Vector3.forward*.0035f,radius*.90f,radius*.90f,.0014f,40);
            // A raised split flame stamped into each face.
            var flame=new[]{V(-.008f,-.011f),V(-.011f,-.001f),V(-.004f,.007f),V(-.002f,.017f),V(.007f,.008f),V(.010f,-.001f),V(.005f,-.011f),V(-.008f,-.011f)};
            Tube(gold,flame.Select(p=>p+c+Vector3.forward*.004f).ToArray(),.0017f,6);
            for(int k=0;k<16;k++){float t=k*Mathf.PI/8;Bead(gold,c+V(Mathf.Cos(t)*radius*.77f,Mathf.Sin(t)*radius*.77f,.004f),V(.0012f,.0012f,.001f),6,3);}
        }
        static void Ring()
        {
            Loop(gold,Vector3.zero,.031f,.031f,.005f);
            Loop(bronze,V(0,0,-.002f),.031f,.031f,.002f);
            G(gold).Disc(V(0,.031f,.002f),.014f,.005f,16);
            G(gem).Gem(V(0,.031f,.008f),V(.011f,.012f,.009f),8);
            foreach(float side in new[]{-1f,1f})
            {
                Bead(gold,V(side*.014f,.025f,.004f),V(.006f,.004f,.004f));
                Line(gold,V(side*.010f,.023f,.007f),V(side*.006f,.032f,.015f),.0018f,6);
            }
        }
        static void Necklace()
        {
            Loop(dark,V(0,.015f),.095f,.115f,.002f,64);
            for(int i=0;i<32;i++)
            {
                float t=i*Mathf.PI/16;
                var c=V(Mathf.Cos(t)*.095f,Mathf.Sin(t)*.115f+.015f);
                Bead(i%4==0?gold:gem,c,V(.0065f,.0075f,.0065f),10,5);
            }
            Loop(gold,V(0,-.109f,.002f),.007f,.010f,.002f,20);
            G(gold).Disc(V(0,-.131f,.001f),.021f,.004f,32);
            G(gem).Gem(V(0,-.131f,.006f),V(.015f,.019f,.009f),10);
            for(int i=0;i<8;i++){float t=i*Mathf.PI/4;Bead(gold,V(Mathf.Cos(t)*.018f,-.131f+Mathf.Sin(t)*.018f,.008f),V(.0025f,.0025f,.002f),6,3);}
        }
        static void Relic()
        {
            // Sealed bronze reliquary with an inset ember stone, engraved cage and carry ring.
            G(bronze).Ellipsoid(V(0,0,0),V(.095f,.125f,.047f),24,12);
            G(gold).Disc(V(0,0,.044f),.055f,.006f,32);
            G(gem).Gem(V(0,0,.054f),V(.038f,.054f,.020f),10);
            Loop(gold,V(0,0,.047f),.069f,.099f,.004f,48);
            Loop(bronze,V(0,.151f),.028f,.032f,.006f,32);
            foreach(float side in new[]{-1f,1f})
            {
                Tube(gold,new[]{V(side*.075f,-.075f,.025f),V(side*.088f,0,.031f),V(side*.065f,.085f,.027f),V(side*.025f,.116f,.020f)},.004f);
                for(int j=0;j<4;j++)Bead(gold,V(side*(.063f+.007f*Mathf.Sin(j)),(j-1.5f)*.040f,.041f),V(.005f,.005f,.003f),8,4);
            }
            foreach(float side in new[]{-1f,1f})
                Tube(gold,new[]{V(-.04f,side*.058f,.043f),V(-.015f,side*.073f,.049f),V(0,side*.060f,.052f),V(.015f,side*.073f,.049f),V(.04f,side*.058f,.043f)},.002f,6);
            G(bronze).Disc(V(0,-.124f,0),.025f,.008f,16);
        }
        static void Ammunition(bool bolt,bool bundle)
        {
            float length=bolt?.40f:.72f;
            int count=bundle?5:1;
            for(int i=0;i<count;i++)
            {
                float x=(i-(count-1)*.5f)*.014f;
                var offset=V(x,(i%2)*.018f,(i%2)*.009f);
                Shaft(offset,length,bolt);
            }
            if(bundle)
                foreach(float y in new[]{-.10f,.09f})
                {
                    for(int j=0;j<3;j++)
                    {
                        var line=new List<Vector3>();
                        for(int k=0;k<=32;k++){float t=k*Mathf.PI/16;line.Add(V(Mathf.Cos(t)*.039f,y+j*.0035f,Mathf.Sin(t)*.016f+.005f));}
                        Tube(cord,line,.002f,6);
                    }
                    Tube(cord,new[]{V(.034f,y,.007f),V(.053f,y-.015f,.008f),V(.044f,y-.029f,.004f)},.0025f,6);
                }
        }
        static void Shaft(Vector3 o,float length,bool bolt)
        {
            float start=-length*.5f,end=length*.5f;
            Line(wood,o+V(0,start),o+V(0,end-.045f),bolt?.005f:.004f,10);
            Line(steel,o+V(0,end-.065f),o+V(0,end-.025f),.0055f,10);
            G(steel).Gem(o+V(0,end-.018f),V(.015f,.040f,.004f),4);
            for(int wing=0;wing<3;wing++)
            {
                float t=wing*Mathf.PI*2/3;
                Vector3 outwards=V(Mathf.Cos(t),0,Mathf.Sin(t));
                var a=o+V(0,start+.035f); var b=o+V(0,start+.12f);
                G(feather).Quad(a,b,b+outwards*.023f-Vector3.up*.018f,a+outwards*.018f);
                for(int n=0;n<5;n++)
                {
                    float f=n/5f;
                    Line(cord,Vector3.Lerp(a,b,f),Vector3.Lerp(a,b,f)+outwards*(.018f+f*.004f)-Vector3.up*.007f,.0006f,4);
                }
            }
            Line(cord,o+V(0,start+.018f),o+V(0,start+.030f),.0055f,8);
        }
        static void Bow()
        {
            var curve=new List<Vector3>();
            for(int i=0;i<=40;i++)
            {
                float t=-1+2*i/40f;
                curve.Add(V(.14f*(1-t*t)-.06f*Mathf.Pow(Mathf.Abs(t),8),t*.58f));
            }
            Tube(wood,curve,.018f,10);
            Tube(bronze,curve.Select(p=>p+Vector3.forward*.015f).ToArray(),.0025f,6);
            Line(cord,curve[0],curve[curve.Count-1],.0018f,6);
            for(int j=0;j<15;j++)
            {
                float y=-.07f+j*.010f;
                Loop(dark,V(.138f,y),.020f,.0035f,.003f,16);
            }
            foreach(var end in new[]{curve[0],curve[curve.Count-1]})Bead(bronze,end,V(.022f,.027f,.021f),10,6);
        }
        static void Crossbow()
        {
            // Carved tapered stock, split steel prod, bowstring, winding ring and visible bolt rail.
            Tube(wood,new[]{V(0,-.34f,0),V(0,-.22f,.005f),V(0,.06f,.01f),V(0,.28f,.008f)},.030f,8);
            Tube(dark,new[]{V(0,-.33f,.002f),V(0,-.30f,.003f)},.033f,8);
            Line(steel,V(-.011f,-.06f,.034f),V(-.011f,.28f,.034f),.003f);
            Line(steel,V(.011f,-.06f,.034f),V(.011f,.28f,.034f),.003f);
            var limb=new List<Vector3>();
            for(int i=0;i<=32;i++){float t=-1+2*i/32f;limb.Add(V(t*.35f,.20f-.08f*t*t,.012f));}
            Tube(steel,limb,.012f,8);
            Tube(bronze,limb.Select(p=>p+Vector3.forward*.009f).ToArray(),.002f,6);
            Tube(cord,new[]{limb[0],V(0,-.06f,.043f),limb[limb.Count-1]},.002f,6);
            Loop(steel,V(0,.335f),.043f,.060f,.006f,32);
            Loop(steel,V(.037f,-.14f,-.008f),.025f,.046f,.005f,24);
            for(int j=0;j<7;j++)Line(cord,V(-.034f,.17f+j*.007f,.020f),V(.034f,.17f+j*.007f,.020f),.002f,6);
            foreach(float x in new[]{-.026f,.026f})foreach(float y in new[]{-.22f,-.05f,.12f})Bead(bronze,V(x,y,.025f),V(.004f,.004f,.003f),8,4);
        }

        static Mesh SaveMesh(Mesh mesh)
        {
            string path=Art+"/"+mesh.name+".asset";
            var old=AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if(old==null){AssetDatabase.CreateAsset(mesh,path);return mesh;}
            old.Clear();old.indexFormat=mesh.indexFormat;old.vertices=mesh.vertices;old.normals=mesh.normals;old.uv=mesh.uv;
            old.triangles=mesh.triangles;old.bounds=mesh.bounds;EditorUtility.SetDirty(old);Object.DestroyImmediate(mesh);return old;
        }
        static Material Surface(string name,Color color,float metallic,float smooth,bool woodgrain=false)
        {
            string path=Art+"/"+name+".mat";
            var material=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(material==null){material=new Material(Shader.Find("Universal Render Pipeline/Lit"));AssetDatabase.CreateAsset(material,path);}
            string texturePath=Art+"/"+name+"_Albedo.asset";
            var texture=AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            bool created=texture==null;
            if(created)texture=new Texture2D(128,128,TextureFormat.RGBA32,true,false){name=name+" grain",wrapMode=TextureWrapMode.Repeat};
            var pixels=new Color[128*128];
            for(int y=0;y<128;y++)for(int x=0;x<128;x++)
            {
                float n=Mathf.PerlinNoise(x*(woodgrain?.17f:.13f)+31,y*(woodgrain?.013f:.13f)+17);
                float scratches=Mathf.PerlinNoise(x*.8f,y*.8f);
                pixels[y*128+x]=color*(.72f+.40f*n+.12f*scratches);pixels[y*128+x].a=1;
            }
            texture.SetPixels(pixels);texture.Apply();
            if(created)AssetDatabase.CreateAsset(texture,texturePath);else EditorUtility.SetDirty(texture);
            material.SetColor("_BaseColor",Color.white);material.SetTexture("_BaseMap",texture);
            material.SetFloat("_Metallic",metallic);material.SetFloat("_Smoothness",smooth);
            material.SetFloat("_Cull",0);EditorUtility.SetDirty(material);return material;
        }
        sealed class Geometry
        {
            readonly List<Vector3> vertices=new List<Vector3>();
            readonly List<Vector2> uvs=new List<Vector2>();
            readonly List<int> triangles=new List<int>();
            int Add(Vector3 p,Vector2 uv){int n=vertices.Count;vertices.Add(p);uvs.Add(uv);return n;}
            void Tri(int a,int b,int c){triangles.Add(a);triangles.Add(b);triangles.Add(c);}
            public void Quad(Vector3 a,Vector3 b,Vector3 c,Vector3 d)
            { int n=vertices.Count;Add(a,Vector2.zero);Add(b,Vector2.up);Add(c,Vector2.one);Add(d,Vector2.right);Tri(n,n+1,n+2);Tri(n,n+2,n+3); }
            public void Tube(IList<Vector3> path,float radius,int sides)
            {
                int start=vertices.Count;
                for(int i=0;i<path.Count;i++)
                {
                    var tangent=(path[Mathf.Min(path.Count-1,i+1)]-path[Mathf.Max(0,i-1)]).normalized;
                    var axis=Mathf.Abs(Vector3.Dot(tangent,Vector3.forward))<.9f?Vector3.forward:Vector3.up;
                    var right=Vector3.Cross(tangent,axis).normalized;var up=Vector3.Cross(tangent,right).normalized;
                    for(int j=0;j<=sides;j++){float a=j*Mathf.PI*2/sides;Add(path[i]+radius*(right*Mathf.Cos(a)+up*Mathf.Sin(a)),new Vector2(j/(float)sides,i/(float)(path.Count-1)));}
                }
                for(int i=0;i<path.Count-1;i++)for(int j=0;j<sides;j++)
                {int a=start+i*(sides+1)+j,b=a+sides+1;Tri(a,a+1,b);Tri(a+1,b+1,b);}
                int lo=Add(path[0],Vector2.zero),hi=Add(path[path.Count-1],Vector2.one);
                for(int j=0;j<sides;j++){Tri(lo,start+j+1,start+j);int e=start+(path.Count-1)*(sides+1)+j;Tri(hi,e,e+1);}
            }
            public void Disc(Vector3 c,float radius,float halfDepth,int sides)
            {
                // Face lies in XY, bevelled edge sections and shallow central face.
                float[] z={-halfDepth,-halfDepth*.75f,halfDepth*.75f,halfDepth};
                float[] r={radius*.92f,radius,radius,radius*.92f};
                int start=vertices.Count;
                for(int i=0;i<4;i++)for(int j=0;j<=sides;j++){float t=j*Mathf.PI*2/sides;Add(c+V(Mathf.Cos(t)*r[i],Mathf.Sin(t)*r[i],z[i]),new Vector2(j/(float)sides,i/3f));}
                for(int i=0;i<3;i++)for(int j=0;j<sides;j++){int a=start+i*(sides+1)+j,b=a+sides+1;Tri(a,a+1,b);Tri(a+1,b+1,b);}
                int front=Add(c+Vector3.forward*halfDepth,new Vector2(.5f,.5f)),back=Add(c-Vector3.forward*halfDepth,new Vector2(.5f,.5f));
                for(int j=0;j<sides;j++){Tri(front,start+3*(sides+1)+j,start+3*(sides+1)+j+1);Tri(back,start+j+1,start+j);}
            }
            public void Ellipsoid(Vector3 c,Vector3 radii,int sides,int rings)
            {
                int start=vertices.Count;
                for(int i=0;i<=rings;i++)for(int j=0;j<=sides;j++)
                {float p=i*Mathf.PI/rings,t=j*Mathf.PI*2/sides;Add(c+Vector3.Scale(radii,V(Mathf.Sin(p)*Mathf.Cos(t),Mathf.Cos(p),Mathf.Sin(p)*Mathf.Sin(t))),new Vector2(j/(float)sides,i/(float)rings));}
                for(int i=0;i<rings;i++)for(int j=0;j<sides;j++){int a=start+i*(sides+1)+j,b=a+sides+1;Tri(a,a+1,b);Tri(a+1,b+1,b);}
            }
            public void Gem(Vector3 c,Vector3 size,int sides)
            {
                // Separate face vertices deliberately keep gemstone facets crisp.
                var edge=new Vector3[sides];var table=new Vector3[sides];
                for(int j=0;j<sides;j++){float t=j*Mathf.PI*2/sides;edge[j]=c+V(Mathf.Cos(t)*size.x,Mathf.Sin(t)*size.y,0);table[j]=c+V(Mathf.Cos(t)*size.x*.55f,Mathf.Sin(t)*size.y*.55f,size.z);}
                for(int j=0;j<sides;j++)
                {
                    int k=(j+1)%sides;Quad(edge[j],edge[k],table[k],table[j]);
                    int a=Add(c+Vector3.forward*size.z,Vector2.zero),b=Add(table[j],Vector2.right),d=Add(table[k],Vector2.up);Tri(a,b,d);
                    a=Add(c-Vector3.forward*size.z*.5f,Vector2.zero);b=Add(edge[k],Vector2.right);d=Add(edge[j],Vector2.up);Tri(a,b,d);
                }
            }
            public Mesh Make(string name)
            {
                var mesh=new Mesh{name=name,indexFormat=vertices.Count>65535?UnityEngine.Rendering.IndexFormat.UInt32:UnityEngine.Rendering.IndexFormat.UInt16};
                mesh.SetVertices(vertices);mesh.SetUVs(0,uvs);mesh.SetTriangles(triangles,0);mesh.RecalculateNormals();mesh.RecalculateBounds();mesh.RecalculateTangents();return mesh;
            }
        }
    }
}
