using System;
using System.Collections.Generic;
using System.Linq;
using Emberdelve.Items;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.EditorTools
{
    /// <summary>Original fitted steel plate, leatherwork and cloth. Saved meshes are assets, not runtime primitives.</summary>
    public static class EmberdelveItemArmorModels
    {
        const string Art = "Assets/Emberdelve/Art/Models/Equipment";
        const string Prefabs = "Assets/Emberdelve/Prefabs/Visual/Equipment";
        const string CatalogPath = "Assets/Emberdelve/Resources/Emberdelve/ItemVisualCatalog.asset";
        static Animator rig;
        static Material leather, edge, metal, thread, ash, ember;
        static GameObject display;
        static readonly List<ItemVisualCatalog.WearPart> parts = new List<ItemVisualCatalog.WearPart>();
        static string id;
        static ItemVisualCatalog.WearSkin skin;

        public static void CreateAssets() => CreateAssets(false);

        /// <summary>Targeted cosmetic migration; other equipment and catalog entries stay intact.</summary>
        public static void CreateCuirassAssets() => CreateAssets(true);

        static void CreateAssets(bool cuirassOnly)
        {
            EmberdelveVisualSetup.EnsureFolder(Art);
            EmberdelveVisualSetup.EnsureFolder(Prefabs);
            if (!cuirassOnly)
            {
                leather = Surface("WaxedBrownLeather", new Color(.235f,.125f,.058f), 0, .23f, false);
                edge = Surface("DarkSeamLeather", new Color(.065f,.043f,.027f), 0, .16f, false);
                metal = Surface("OxidisedBrass", new Color(.39f,.255f,.10f), .72f, .33f, false);
                thread = Surface("FlaxStitch", new Color(.40f,.32f,.20f), 0, .1f, true);
                ash = Surface("AshWool", new Color(.095f,.113f,.105f), 0, .06f, true);
                ember = Surface("EmberWool", new Color(.26f,.065f,.037f), 0, .09f, true);
            }
            else
            {
                // Load existing accessory surfaces without rewriting shared glove/boot/cloth assets.
                edge = AssetDatabase.LoadAssetAtPath<Material>(Art+"/DarkSeamLeather.mat");
                ash = AssetDatabase.LoadAssetAtPath<Material>(Art+"/AshWool.mat");
                if (edge == null || ash == null) throw new InvalidOperationException("Existing armor accessory materials missing");
            }
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(EmberdelveUnifiedHuman.ModelPath);
            if (source == null) throw new InvalidOperationException("Existing Human rig is missing");
            var mannequin = Object.Instantiate(source);
            mannequin.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            try
            {
                rig = mannequin.GetComponentInChildren<Animator>();
                if (rig == null) rig = mannequin.AddComponent<Animator>();
                rig.avatar = AssetDatabase.LoadAllAssetsAtPath(EmberdelveUnifiedHuman.ModelPath).OfType<Avatar>().First(a=>a.isHuman);
                rig.Rebind();
                var catalog = AssetDatabase.LoadAssetAtPath<ItemVisualCatalog>(CatalogPath);
                if (catalog == null) { catalog = ScriptableObject.CreateInstance<ItemVisualCatalog>(); AssetDatabase.CreateAsset(catalog,CatalogPath); }
                var entries = new List<ItemVisualCatalog.Entry>(catalog.entries ?? Array.Empty<ItemVisualCatalog.Entry>());
                BuildItem("boiled_cuirass", Cuirass, entries);
                if (!cuirassOnly)
                {
                    BuildItem("graspwrap_gloves", Gloves, entries);
                    BuildItem("footpad_boots", Boots, entries);
                    BuildItem("ashen_cloak", ()=>Cloak(false), entries);
                    BuildItem("emberthread_mantle", ()=>Cloak(true), entries);
                }
                catalog.entries = entries.ToArray(); EditorUtility.SetDirty(catalog);
                AssetDatabase.SaveAssets();
                Debug.Log("[ItemArmorModels] PASS " + (cuirassOnly ? "targeted steel cuirass" : "five original item models") + " with fitted wearable parts");
            }
            finally { Object.DestroyImmediate(mannequin); rig = null; }
        }

        static void BuildItem(string key, Action build, List<ItemVisualCatalog.Entry> entries)
        {
            id=key; parts.Clear(); skin=null; display=new GameObject("IV_"+id);
            try
            {
                build();
                var renderers=display.GetComponentsInChildren<Renderer>();
                var bounds=renderers[0].bounds; foreach(var r in renderers)bounds.Encapsulate(r.bounds);
                // A shared centred pivot gives drops and thumbnail cameras predictable framing.
                foreach(Transform child in display.transform) child.localPosition-=bounds.center;
                var prefab=PrefabUtility.SaveAsPrefabAsset(display,Prefabs+"/IV_"+id+".prefab");
                var entry=entries.FirstOrDefault(e=>e!=null&&e.itemId==id);
                if(entry==null){entry=new ItemVisualCatalog.Entry{itemId=id};entries.Add(entry);}
                entry.worldPrefab=prefab; entry.worn=parts.ToArray(); entry.skin=skin;
                int triangles=parts.Sum(p=>p.mesh.triangles.Length/3)+(skin!=null?skin.mesh.triangles.Length/3:0);
                // The fitted pair retains the existing Human's 25,883 hand/cuff triangles and
                // deformation weights. A dedicated 27k ceiling preserves close-up knuckles while
                // reducing the old 32 rigid glove renderers to one skinned renderer (two materials).
                // Other authored items keep the smaller 18k guard; this is not a global budget raise.
                int budget=id=="graspwrap_gloves" && skin!=null?27000:18000;
                if(triangles>budget)throw new InvalidOperationException("Item mesh budget exceeded: "+id+" triangles="+triangles+" budget="+budget);
                Debug.Log("[ItemArmorModels] "+id+" triangles="+triangles+" parts="+parts.Count+" skin="+(skin!=null)+" bounds="+bounds.size);
            }
            finally{Object.DestroyImmediate(display);display=null;}
        }

        static Transform Bone(HumanBodyBones bone)
        {
            var t=rig.GetBoneTransform(bone);
            if(t==null)throw new InvalidOperationException("Missing humanoid bone: "+bone);
            return t;
        }
        static void Part(string name, HumanBodyBones bone, Geometry geo, Material[] materials, Matrix4x4 displayMatrix)
        {
            var anchor=Bone(bone);
            var worn=SaveMesh(geo.Mesh(anchor.worldToLocalMatrix),id+"_"+name+"_Worn");
            var shown=SaveMesh(geo.Mesh(displayMatrix),id+"_"+name+"_Display");
            var obj=new GameObject(name,typeof(MeshFilter),typeof(MeshRenderer));obj.transform.SetParent(display.transform,false);
            obj.GetComponent<MeshFilter>().sharedMesh=shown;obj.GetComponent<MeshRenderer>().sharedMaterials=materials;
            parts.Add(new ItemVisualCatalog.WearPart{bone=bone,mesh=worn,materials=materials,localScale=Vector3.one});
        }
        static Material[] LeatherPalette => new[]{leather,edge,metal,thread};
        static void Cuirass()
        {
            var steel = SteelSurface("TemperedSteel", new Color(.46f,.49f,.53f), .91f, .58f);
            var bright = SteelSurface("BurnishedSteelEdges", new Color(.61f,.64f,.67f), .94f, .67f);
            var palette = new[]{steel,bright,edge,ash};
            float waist=Bone(HumanBodyBones.Hips).position.y+.12f;
            float top=Bone(HumanBodyBones.Neck).position.y-.035f;
            float height=top-waist;
            var g=new Geometry();
            const int columns=32, rows=16;
            // Separate breast/back plates: a pinched waist, broad upper ribs and a flattened back.
            // The upper rim is a real scooped silhouette around each arm and the neck, not a decal.
            Vector3 Plate(float a,float t,bool front,float offset=0)
            {
                float side=Mathf.Abs(Mathf.Sin(a)), center=Mathf.Max(0,Mathf.Cos(a));
                float width=t<.62f?Mathf.Lerp(.183f,.229f,Mathf.SmoothStep(0,1,t/.62f)):
                    Mathf.Lerp(.229f,.202f,(t-.62f)/.38f);
                float armCut=.165f*Mathf.Pow(side,7);
                float neckCut=(front?.080f:.035f)*Mathf.Pow(center,10);
                float y=waist+t*(height-armCut-neckCut);
                float chest=Mathf.Sin(t*Mathf.PI*.84f);
                float depth=front?(.174f+.015f*chest):(.155f+.010f*chest);
                // Shallow medial arris catches a long highlight without sculpting separate breasts.
                float arris=front?.022f*Mathf.Pow(center,9)*Mathf.Sin(t*Mathf.PI*.85f):0;
                float z=(depth*center+arris+offset)*(front?1:-1);
                return new Vector3(Mathf.Sin(a)*(width+offset),y,z);
            }
            foreach(bool front in new[]{true,false})
            {
                g.Grid(rows,columns,(r,c)=>Plate((front?1:-1)*(c/(float)columns-.5f)*Mathf.PI,r/(float)rows,front),0,false);
                // Rolled neck/armhole perimeter and bottom edge are steel, without leather panel seams.
                foreach(float t in new[]{0f,1f})
                {
                    var rim=new List<Vector3>();
                    for(int c=0;c<=columns;c++)rim.Add(Plate((c/(float)columns-.5f)*Mathf.PI,t,front,.002f));
                    g.Tube(rim,.0042f,1,6);
                }
                foreach(float sign in new[]{-1f,1f})
                {
                    var seam=new List<Vector3>();
                    for(int r=0;r<=rows;r++)seam.Add(Plate(sign*Mathf.PI*.5f,r/(float)rows,front,.001f));
                    g.Tube(seam,.003f,1,5);
                    // Discreet paired steel rivets along the shoulder/lower edge.
                    foreach(float t in new[]{.08f,.87f})
                    {
                        Vector3 p=Plate(sign*.82f,t,front,.005f);
                        g.Ellipsoid(p,new Vector3(.0042f,.0042f,.0025f),1,8,4);
                    }
                }
            }
            foreach(float sign in new[]{-1f,1f})
            {
                // Leather shoulder suspension under narrow formed steel straps. Open neck and armhole.
                float x=sign*.138f;
                var strap=new[]{new Vector3(x,top-.062f,.122f),new Vector3(x,top-.002f,.065f),
                    new Vector3(x,top+.008f,-.015f),new Vector3(x,top-.025f,-.104f)};
                g.Ribbon(strap,Vector3.right*.024f,2);
                g.Ribbon(strap.Select(p=>p+Vector3.up*.003f).ToArray(),Vector3.right*.019f,0);
                g.Ellipsoid(new Vector3(x,top-.058f,.127f),new Vector3(.005f,.005f,.003f),1,8,5);
                // Two side closures follow the ribs rather than circling the entire waist like hoops.
                foreach(float t in new[]{.23f,.52f})
                {
                    var line=new List<Vector3>();
                    for(int c=0;c<=12;c++)
                    {
                        float a=sign*(1.02f+c/12f*.97f);
                        bool front=Mathf.Cos(a)>=0;
                        line.Add(Plate(front?a:sign*(Mathf.PI-Mathf.Abs(a)),t,front,.007f));
                    }
                    g.Ribbon(line,Vector3.up*.012f,2);
                    var buckle=Plate(sign*1.06f,t,true,.012f);
                    Buckle(g,buckle,.022f,.026f,1);
                }
            }
            Part("Cuirass",HumanBodyBones.Chest,g,palette,Matrix4x4.identity);

            // Three overlapping lames flare below the narrow waist; independently hips-bound to let
            // the torso lean without making the entire armor a rigid cylindrical dress.
            var fauld=new Geometry();
            for(int lame=0;lame<3;lame++)
            {
                float y=waist+.009f-lame*.044f;
                float w=.188f+lame*.012f, d=.180f+lame*.007f;
                foreach(bool front in new[]{true,false})
                {
                    Vector3 Lame(float a,float t)
                    {
                        float center=Mathf.Max(0,Mathf.Cos(a));
                        // Shallow central cusp and sloped hem; lower corners clear hip flexion.
                        float hem=.009f*center*center*(front?1:.3f);
                        return new Vector3(Mathf.Sin(a)*(w+t*.013f),y-t*.056f-hem,
                            (front?1:-1)*(d+t*.008f)*center);
                    }
                    fauld.Grid(3,columns,(r,c)=>Lame((front?-1:1)*(c/(float)columns-.5f)*Mathf.PI,r/3f),0,false);
                    var rim=new List<Vector3>();
                    for(int c=0;c<=columns;c++)rim.Add(Lame((c/(float)columns-.5f)*Mathf.PI,1));
                    fauld.Tube(rim,.003f,1,5);
                    foreach(float sign in new[]{-1f,1f})
                        fauld.Ellipsoid(Lame(sign*.9f,.55f)+Vector3.forward*(front?.003f:-.003f),new Vector3(.0035f,.0035f,.002f),1,8,4);
                }
            }
            Part("ArticulatedFauld",HumanBodyBones.Hips,fauld,palette,Matrix4x4.identity);

            // Low quilted neck lining covers the existing body boundary, without the previous tall tube.
            var lining=new Geometry();
            lining.Loft(new Vector3(0,top-.055f,0),Quaternion.identity,
                new[]{0f,.045f,.075f},new[]{.106f,.092f,.087f},new[]{.10f,.086f,.082f},3,32,false);
            Part("PaddedNeckLining",HumanBodyBones.Neck,lining,palette,Matrix4x4.identity);
        }

        static Material SteelSurface(string name,Color color,float metallic,float smoothness)
        {
            string path=Art+"/"+name+".mat";
            var material=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(material==null){material=new Material(Shader.Find("Universal Render Pipeline/Lit"));AssetDatabase.CreateAsset(material,path);}
            const int n=256;
            var albedo=new Texture2D(n,n,TextureFormat.RGBA32,true,false){name=name+"Albedo",wrapMode=TextureWrapMode.Repeat};
            var normal=new Texture2D(n,n,TextureFormat.RGBA32,true,true){name=name+"Normal",wrapMode=TextureWrapMode.Repeat};
            var colors=new Color[n*n];var normals=new Color[n*n];
            for(int y=0;y<n;y++)for(int x=0;x<n;x++)
            {
                float broad=Mathf.PerlinNoise(x*.028f+17,y*.028f+9);
                float brush=Mathf.PerlinNoise(x*.7f+3,y*.035f+11);
                float scratch=Mathf.Pow(Mathf.Max(0,Mathf.Sin(x*1.79f+y*.11f)),24)*.018f;
                float factor=.94f+.055f*broad+.025f*brush-scratch;
                colors[y*n+x]=new Color(color.r*factor,color.g*factor,color.b*factor,1);
                float dx=(Mathf.PerlinNoise((x+1)*.7f+3,y*.035f+11)-brush)*.025f;
                normals[y*n+x]=new Color(.5f+dx,.5f+scratch*.2f,1,1);
            }
            albedo.SetPixels(colors);albedo.Apply(true);normal.SetPixels(normals);normal.Apply(true);
            material.SetTexture("_BaseMap",SaveTexture(albedo,name+"Albedo"));material.SetColor("_BaseColor",Color.white);
            material.SetTexture("_BumpMap",SaveTexture(normal,name+"Normal"));material.EnableKeyword("_NORMALMAP");
            material.SetFloat("_BumpScale",.3f);material.SetFloat("_Metallic",metallic);material.SetFloat("_Smoothness",smoothness);
            material.SetFloat("_Cull",0);EditorUtility.SetDirty(material);return material;
        }
        static void Buckle(Geometry g,Vector3 center,float width,float height,int mat)
        {
            g.Tube(new[]{center+new Vector3(-width/2,-height/2,0),center+new Vector3(width/2,-height/2,0),center+new Vector3(width/2,height/2,0),center+new Vector3(-width/2,height/2,0),center+new Vector3(-width/2,-height/2,0)},.0025f,mat,6);
            g.Tube(new[]{center+Vector3.left*width*.45f,center+Vector3.right*width*.45f},.0017f,mat,5);
        }
        static void Gloves()
        {
            // Garment topology is derived from the project's licensed Human mesh. Original skin weights
            // and bind poses follow the same finger deformation as the first-person and world hands.
            // Independent rigid phalanx shells cannot reproduce the soft knuckle and palm transitions.
            var source=rig.GetComponentInChildren<SkinnedMeshRenderer>(true);
            var mesh=source.sharedMesh;
            var bones=source.bones;
            var originalVertices=mesh.vertices;
            var originalNormals=mesh.normals;
            var originalWeights=mesh.boneWeights;
            var triangles=mesh.triangles;
            var wrist=new Vector3[2];var direction=new Vector3[2];var displayMatrix=new Matrix4x4[2];
            for(int side=0;side<2;side++)
            {
                var hand=Bone(side==0?HumanBodyBones.LeftHand:HumanBodyBones.RightHand);
                var middle=Bone(side==0?HumanBodyBones.LeftMiddleProximal:HumanBodyBones.RightMiddleProximal);
                wrist[side]=source.transform.InverseTransformPoint(hand.position);
                direction[side]=(wrist[side]-source.transform.InverseTransformPoint(hand.parent.position)).normalized;
                var pose=Quaternion.FromToRotation((middle.position-hand.position).normalized,Vector3.down);
                displayMatrix[side]=Matrix4x4.TRS(new Vector3(side==0?-.065f:.065f,0,0),pose,Vector3.one)*Matrix4x4.Translate(-hand.position)*source.transform.localToWorldMatrix;
            }
            bool IsArm(int index)
            {
                var name=bones[EmberdelveFirstPersonArms.DominantBone(originalWeights[index])].name.ToLowerInvariant();
                return name.Contains("hand")||name.Contains("wrist")||name.Contains("finger")||name.Contains("metacarpal")||name.Contains("thumb")||name.Contains("forearm")||name.Contains("lowerarm")||name.Contains("upperarm")||name.Contains("upper_arm");
            }
            var vertices=new List<Vector3>();var normals=new List<Vector3>();var uv=new List<Vector2>();var weights=new List<BoneWeight>();
            var shownVertices=new List<Vector3>();var shownNormals=new List<Vector3>();
            var faces=new[]{new List<int>(),new List<int>()};
            var lookup=new Dictionary<(Vector3,Vector3,BoneWeight,int),int>();
            ArmWristGeometry.Vertex V(int i)=>new ArmWristGeometry.Vertex(originalVertices[i],originalNormals[i],originalWeights[i]);
            for(int t=0;t<triangles.Length;t+=3)
            {
                int a=triangles[t],b=triangles[t+1],c=triangles[t+2];
                if(!IsArm(a)||!IsArm(b)||!IsArm(c))continue;
                int side=bones[EmberdelveFirstPersonArms.DominantBone(originalWeights[a])].name.EndsWith(".L")?0:1;
                // Keep a five-centimetre soft cuff, clipping exact boundary vertices with interpolated weights.
                Vector3 cut=wrist[side]-direction[side]*.05f;
                var polygon=ArmWristGeometry.ClipTriangle(V(a),V(b),V(c),cut,direction[side],true);
                for(int k=1;k+1<polygon.Count;k++)
                {
                    var corners=new[]{polygon[0],polygon[k],polygon[k+1]};
                    if(Vector3.Cross(corners[1].Position-corners[0].Position,corners[2].Position-corners[0].Position).sqrMagnitude<1e-16f)continue;
                    float along=Vector3.Dot((corners[0].Position+corners[1].Position+corners[2].Position)/3-wrist[side],direction[side]);
                    int material=along<-.014f?1:0;
                    foreach(var point in corners)
                    {
                        var key=(point.Position,point.Normal,point.Weight,side);
                        if(!lookup.TryGetValue(key,out int index))
                        {
                            index=vertices.Count;lookup.Add(key,index);
                            Vector3 worldNormal=source.transform.TransformDirection(point.Normal).normalized;
                            Vector3 inflated=point.Position+source.transform.InverseTransformVector(worldNormal*.0042f);
                            vertices.Add(inflated);normals.Add(point.Normal.normalized);weights.Add(point.Weight);
                            Vector3 rel=point.Position-wrist[side];
                            Vector3 u=Vector3.Cross(direction[side],Vector3.up).normalized;
                            if(u.sqrMagnitude<.5f)u=Vector3.Cross(direction[side],Vector3.forward).normalized;
                            Vector3 v=Vector3.Cross(direction[side],u).normalized;
                            uv.Add(new Vector2(Vector3.Dot(rel,direction[side])*14f,Mathf.Atan2(Vector3.Dot(rel,v),Vector3.Dot(rel,u))*2/Mathf.PI));
                            shownVertices.Add(displayMatrix[side].MultiplyPoint3x4(inflated));
                            shownNormals.Add(displayMatrix[side].MultiplyVector(point.Normal).normalized);
                        }
                        faces[material].Add(index);
                    }
                }
            }
            if(vertices.Count<100)throw new InvalidOperationException("Fitted glove extraction did not find both hands");
            Mesh Make(bool shown)
            {
                var result=new Mesh();
                result.indexFormat=vertices.Count>65535?UnityEngine.Rendering.IndexFormat.UInt32:UnityEngine.Rendering.IndexFormat.UInt16;
                result.SetVertices(shown?shownVertices:vertices);result.SetNormals(shown?shownNormals:normals);result.SetUVs(0,uv);
                result.subMeshCount=2;result.SetTriangles(faces[0],0);result.SetTriangles(faces[1],1);
                if(!shown){result.boneWeights=weights.ToArray();result.bindposes=mesh.bindposes;}
                result.RecalculateTangents();result.RecalculateBounds();return result;
            }
            var garment=SaveMesh(Make(false),id+"_FittedSkin");
            var shownMesh=SaveMesh(Make(true),id+"_FittedDisplay");
            var materials=new[]{leather,edge};
            skin=new ItemVisualCatalog.WearSkin{rendererName=source.name,mesh=garment,boneNames=bones.Select(x=>x.name).ToArray(),materials=materials,localScale=Vector3.one};
            var obj=new GameObject("FittedLeatherGloves",typeof(MeshFilter),typeof(MeshRenderer));obj.transform.SetParent(display.transform,false);
            obj.GetComponent<MeshFilter>().sharedMesh=shownMesh;obj.GetComponent<MeshRenderer>().sharedMaterials=materials;
        }
        static void Boots()
        {
            foreach(bool left in new[]{true,false})
            {
                var footBone=left?HumanBodyBones.LeftFoot:HumanBodyBones.RightFoot;
                var calfBone=left?HumanBodyBones.LeftLowerLeg:HumanBodyBones.RightLowerLeg;
                Vector3 foot=Bone(footBone).position;
                Vector3 shoeCenter=new Vector3(foot.x,.025f,foot.z+.052f);
                Vector3 shaftCenter=new Vector3(foot.x,.025f,foot.z+.005f);
                Matrix4x4 show=Matrix4x4.Translate(new Vector3((left?-.088f:.088f)-foot.x,0,-foot.z));
                var shoe=new Geometry();
                // Shoe follows the foot; the open ankle reaches inside the separate calf piece.
                shoe.Loft(shoeCenter,Quaternion.identity,new[]{0f,.023f,.064f,.105f,.165f},
                    new[]{.067f,.073f,.069f,.059f,.061f},new[]{.148f,.149f,.14f,.083f,.067f},0,32,false,
                    r=>new Vector3(0,0,r>=3?-.047f:r==2?-.01f:.012f));
                shoe.Loft(shoeCenter,Quaternion.identity,new[]{-.012f,0f,.015f},new[]{.070f,.075f,.074f},new[]{.151f,.155f,.151f},1,32,true);
                shoe.Ribbon(new[]{shoeCenter+new Vector3(0,.075f,.117f),shoeCenter+new Vector3(0,.14f,.028f)},Vector3.right*.027f,1);
                for(int i=0;i<28;i++)
                {
                    float a=i*2*Mathf.PI/28;
                    shoe.Tube(new[]{shoeCenter+new Vector3(Mathf.Sin(a)*.072f,.025f,Mathf.Cos(a)*.150f),shoeCenter+new Vector3(Mathf.Sin(a+.025f)*.072f,.029f,Mathf.Cos(a+.025f)*.150f)},.0012f,3,4);
                }
                Part(left?"LeftShoe":"RightShoe",footBone,shoe,LeatherPalette,show);

                var shaft=new Geometry();
                // The boot shaft follows the shin, not the rotating foot. Six centimetres of
                // overlap with the shoe ankle avoid an exposed gap during toe-off and crouching.
                shaft.Loft(shaftCenter,Quaternion.identity,new[]{.105f,.17f,.25f,.295f},
                    new[]{.066f,.068f,.075f,.078f},new[]{.073f,.078f,.083f,.085f},0,32,false);
                shaft.Loft(shaftCenter,Quaternion.identity,new[]{.267f,.295f,.31f},
                    new[]{.079f,.082f,.079f},new[]{.087f,.090f,.087f},1,32,false);
                shaft.Ribbon(new[]{shaftCenter+new Vector3(0,.112f,.075f),shaftCenter+new Vector3(0,.20f,.083f),shaftCenter+new Vector3(0,.29f,.088f)},Vector3.right*.028f,1);
                for(int row=0;row<6;row++)
                {
                    float y=.122f+row*.026f,z=.079f+row*.0016f;
                    shaft.Tube(new[]{shaftCenter+new Vector3(-.022f,y,z),shaftCenter+new Vector3(.022f,y+.018f,z)},.0018f,3,5);
                    shaft.Ellipsoid(shaftCenter+new Vector3(-.026f,y,z),Vector3.one*.0037f,2,6,4);
                    shaft.Ellipsoid(shaftCenter+new Vector3(.026f,y+.018f,z),Vector3.one*.0037f,2,6,4);
                }
                Buckle(shaft,shaftCenter+new Vector3(left?-.071f:.071f,.19f,.045f),.022f,.027f,2);
                Part(left?"LeftCalf":"RightCalf",calfBone,shaft,LeatherPalette,show);
            }
        }
        static void Cloak(bool ornate)
        {
            float top=Bone(HumanBodyBones.Neck).position.y-.025f;
            float length=ornate?1.02f:1.14f;
            var g=new Geometry(); const int rows=22,cols=28;
            Vector3 Cloth(float u,float t)
            {
                float width=Mathf.Lerp(.22f,ornate?.43f:.38f,t);
                float hem=.028f*Mathf.Sin(u*15f)+.009f*Mathf.Sin(u*31f);
                float fold=(.012f+.025f*t)*Mathf.Sin(u*21f+.35f*Mathf.Sin(t*5f));
                return new Vector3(u*width,top-length*t+hem*Mathf.Pow(t,8),-.17f-.10f*t+fold-.035f*(1-u*u));
            }
            g.Grid(rows,cols,(r,c)=>Cloth(c/(float)cols*2-1,r/(float)rows),0,false,true);
            foreach(float side in new[]{-1f,1f})
            {
                var hem=new List<Vector3>();for(int i=0;i<=rows;i++)hem.Add(Cloth(side,i/(float)rows));g.Tube(hem,.004f,1,6);
                if(ornate)for(int i=1;i<rows;i++)
                {
                    Vector3 a=Cloth(side*.93f,(i-.2f)/rows),b=Cloth(side*.88f,(i+.2f)/rows);
                    g.Tube(new[]{a,b},.0017f,2,5);
                }
            }
            var bottom=new List<Vector3>();for(int i=0;i<=cols;i++)bottom.Add(Cloth(i/(float)cols*2-1,1));g.Tube(bottom,.004f,1,6);
            // Shoulder capelet wraps toward a circular clasp at the sternum, leaving the front body open.
            for(int side=-1;side<=1;side+=2)
            {
                var controls=new[]{new Vector3(side*.20f,top-.04f,-.17f),new Vector3(side*.205f,top-.025f,-.07f),new Vector3(side*.16f,top-.045f,.045f),new Vector3(side*.025f,top-.075f,.15f)};
                var line=new List<Vector3>();
                for(int i=0;i<=24;i++)
                {
                    float t=i/24f;
                    // Cubic Bezier removes angular shoulder spikes; narrow rounded ends meet the clasp.
                    Vector3 p=Mathf.Pow(1-t,3)*controls[0]+3*(1-t)*(1-t)*t*controls[1]+3*(1-t)*t*t*controls[2]+t*t*t*controls[3];
                    line.Add(p);
                }
                g.SoftRibbon(line,Vector3.right,.038f,0);
                var outer=new List<Vector3>();
                for(int i=0;i<line.Count;i++)outer.Add(line[i]+Vector3.right*(side*.034f*Mathf.Sin(Mathf.PI*(.1f+.8f*i/(line.Count-1f)))));
                g.Tube(outer,.0028f,1,6);
            }
            g.Ellipsoid(new Vector3(0,top-.075f,.157f),new Vector3(.032f,.027f,.008f),2,16,8);
            g.Ellipsoid(new Vector3(0,top-.075f,.165f),new Vector3(.014f,.014f,.005f),1,12,6);
            Part("DrapedCloth",HumanBodyBones.Chest,g,new[]{ornate?ember:ash,edge,metal,thread},Matrix4x4.identity);
        }

        static Mesh SaveMesh(Mesh mesh,string name)
        {
            mesh.name=name;string path=Art+"/"+name+".asset";
            var old=AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if(old==null){AssetDatabase.CreateAsset(mesh,path);return mesh;}
            // Update native buffers too: CopySerialized alone can leave previews drawing the old mesh.
            old.Clear(); old.indexFormat=mesh.indexFormat;
            old.vertices=mesh.vertices; old.normals=mesh.normals; old.tangents=mesh.tangents; old.uv=mesh.uv;
            old.boneWeights=mesh.boneWeights; old.bindposes=mesh.bindposes; old.colors=mesh.colors; old.uv2=mesh.uv2;
            old.subMeshCount=mesh.subMeshCount;
            for(int i=0;i<mesh.subMeshCount;i++)old.SetTriangles(mesh.GetTriangles(i),i);
            old.bounds=mesh.bounds; old.name=name;
            EditorUtility.SetDirty(old);Object.DestroyImmediate(mesh);return old;
        }
        static Material Surface(string name,Color baseColor,float metallic,float smooth,bool woven)
        {
            string path=Art+"/"+name+".mat";
            var material=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(material==null){material=new Material(Shader.Find("Universal Render Pipeline/Lit"));AssetDatabase.CreateAsset(material,path);}
            const int n=256;
            var albedo=new Texture2D(n,n,TextureFormat.RGBA32,true,false){name=name+"Albedo",wrapMode=TextureWrapMode.Repeat};
            var normal=new Texture2D(n,n,TextureFormat.RGBA32,true,true){name=name+"Normal",wrapMode=TextureWrapMode.Repeat};
            var colors=new Color[n*n];var normals=new Color[n*n];
            for(int y=0;y<n;y++)for(int x=0;x<n;x++)
            {
                float grain=Mathf.PerlinNoise(x*.43f+7,y*.43f+3);
                float broad=Mathf.PerlinNoise(x*.045f,y*.045f);
                float weave=woven?Mathf.Sin(x*Mathf.PI*.5f)*Mathf.Sin(y*Mathf.PI*.5f)*.08f:0;
                float factor=.78f+.24f*grain+.12f*broad+weave;
                colors[y*n+x]=new Color(baseColor.r*factor,baseColor.g*factor,baseColor.b*factor,1);
                float dx=(Mathf.PerlinNoise((x+1)*.43f+7,y*.43f+3)-grain)*.23f;
                float dy=(Mathf.PerlinNoise(x*.43f+7,(y+1)*.43f+3)-grain)*.23f;
                if(woven){dx+=Mathf.Cos(x*Mathf.PI*.5f)*.08f;dy+=Mathf.Cos(y*Mathf.PI*.5f)*.08f;}
                normals[y*n+x]=new Color(.5f+dx,.5f+dy,1,1);
            }
            albedo.SetPixels(colors);albedo.Apply(true);normal.SetPixels(normals);normal.Apply(true);
            material.SetTexture("_BaseMap",SaveTexture(albedo,name+"Albedo"));material.SetColor("_BaseColor",Color.white);
            material.SetTexture("_BumpMap",SaveTexture(normal,name+"Normal"));material.EnableKeyword("_NORMALMAP");
            material.SetFloat("_BumpScale",.45f);material.SetFloat("_Metallic",metallic);material.SetFloat("_Smoothness",smooth);
            material.SetFloat("_Cull",0);EditorUtility.SetDirty(material);return material;
        }
        static Texture2D SaveTexture(Texture2D texture,string name)
        {
            string path=Art+"/"+name+".asset";var old=AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if(old==null){AssetDatabase.CreateAsset(texture,path);return texture;}
            EditorUtility.CopySerialized(texture,old);EditorUtility.SetDirty(old);Object.DestroyImmediate(texture);return old;
        }
        sealed class Geometry
        {
            readonly List<Vector3> vertices=new List<Vector3>();
            readonly List<Vector2> uv=new List<Vector2>();
            readonly List<int>[] faces={new List<int>(),new List<int>(),new List<int>(),new List<int>()};
            int Add(Vector3 p,Vector2 tex){int i=vertices.Count;vertices.Add(p);uv.Add(tex);return i;}
            void Tri(int a,int b,int c,int mat){faces[mat].Add(a);faces[mat].Add(b);faces[mat].Add(c);}
            public void Grid(int rows,int cols,Func<int,int,Vector3> point,int mat,bool close,bool doubleSide=false)
            {
                int start=vertices.Count;
                for(int r=0;r<=rows;r++)for(int c=0;c<=cols;c++)Add(point(r,c),new Vector2(c/(float)cols*2,r/(float)rows*3));
                for(int r=0;r<rows;r++)for(int c=0;c<cols;c++)
                {
                    int a=start+r*(cols+1)+c,b=a+cols+1;
                    Tri(a,a+1,b,mat);Tri(a+1,b+1,b,mat);
                    // Materials render both sides; duplicate opposite faces would cancel smooth normals.
                }
            }
            public void Loft(Vector3 origin,Quaternion rotation,float[] heights,float[] widths,float[] depths,int mat,int sides,bool caps,Func<int,Vector3> shift=null)
            {
                int start=vertices.Count;
                for(int r=0;r<heights.Length;r++)for(int c=0;c<=sides;c++)
                {
                    float a=c*2*Mathf.PI/sides;
                    Add(origin+rotation*(new Vector3(Mathf.Sin(a)*widths[r],heights[r],Mathf.Cos(a)*depths[r])+(shift?.Invoke(r)??Vector3.zero)),new Vector2(c/(float)sides*2,heights[r]*5));
                }
                for(int r=0;r<heights.Length-1;r++)for(int c=0;c<sides;c++)
                {int a=start+r*(sides+1)+c,b=a+sides+1;Tri(a,a+1,b,mat);Tri(a+1,b+1,b,mat);}
                if(caps)for(int end=0;end<2;end++)
                {
                    int r=end==0?0:heights.Length-1;
                    int middle=Add(origin+rotation*(Vector3.up*heights[r]+(shift?.Invoke(r)??Vector3.zero)),Vector2.one*.5f);
                    for(int c=0;c<sides;c++){int a=start+r*(sides+1)+c;if(end==0)Tri(middle,a+1,a,mat);else Tri(middle,a,a+1,mat);}
                }
            }
            public void Tube(IReadOnlyList<Vector3> line,float radius,int mat,int sides)
            {
                int start=vertices.Count;
                for(int i=0;i<line.Count;i++)
                {
                    Vector3 tangent=(line[Mathf.Min(i+1,line.Count-1)]-line[Mathf.Max(i-1,0)]).normalized;
                    Vector3 u=Vector3.Cross(tangent,Mathf.Abs(tangent.y)>.9f?Vector3.right:Vector3.up).normalized;
                    Vector3 v=Vector3.Cross(tangent,u);
                    for(int c=0;c<=sides;c++){float a=c*2*Mathf.PI/sides;Add(line[i]+radius*(u*Mathf.Cos(a)+v*Mathf.Sin(a)),new Vector2(c/(float)sides,i*.1f));}
                }
                for(int i=0;i<line.Count-1;i++)for(int c=0;c<sides;c++)
                {int a=start+i*(sides+1)+c,b=a+sides+1;Tri(a,b,a+1,mat);Tri(a+1,b,b+1,mat);}
            }
            public void Ribbon(IReadOnlyList<Vector3> line,Vector3 halfWidth,int mat,bool doubleSide=false)
            {
                for(int i=0;i<line.Count-1;i++)
                {
                    int a=Add(line[i]-halfWidth,Vector2.zero),b=Add(line[i]+halfWidth,Vector2.right);
                    int c=Add(line[i+1]-halfWidth,Vector2.up),d=Add(line[i+1]+halfWidth,Vector2.one);
                    Tri(a,b,c,mat);Tri(b,d,c,mat);
                    // Two-sided material preserves lighting without coincident reverse triangles.
                }
            }
            public void SoftRibbon(IReadOnlyList<Vector3> line,Vector3 widthAxis,float width,int mat)
            {
                int start=vertices.Count;
                for(int i=0;i<line.Count;i++)
                {
                    float t=i/(line.Count-1f);
                    float half=width*Mathf.Sin(Mathf.PI*(.1f+.8f*t));
                    Add(line[i]-widthAxis*half,new Vector2(0,t));
                    Add(line[i]+widthAxis*half,new Vector2(1,t));
                }
                for(int i=0;i<line.Count-1;i++)
                {int a=start+i*2;Tri(a,a+1,a+2,mat);Tri(a+1,a+3,a+2,mat);}
            }
            public void Ellipsoid(Vector3 center,Vector3 size,int mat,int sides,int rings,Quaternion rotation=default)
            {
                if(rotation==default(Quaternion))rotation=Quaternion.identity;
                Quaternion q=rotation;
                Grid(rings,sides,(r,c)=>{
                    float a=c*2*Mathf.PI/sides,b=r*Mathf.PI/rings;
                    return center+q*Vector3.Scale(size,new Vector3(Mathf.Sin(b)*Mathf.Sin(a),Mathf.Cos(b),Mathf.Sin(b)*Mathf.Cos(a)));
                },mat,true);
            }
            public Mesh Mesh(Matrix4x4 matrix)
            {
                var mesh=new Mesh();mesh.SetVertices(vertices.Select(matrix.MultiplyPoint3x4).ToList());mesh.SetUVs(0,uv);mesh.subMeshCount=4;
                for(int i=0;i<4;i++)mesh.SetTriangles(faces[i],i);
                mesh.RecalculateNormals();mesh.RecalculateTangents();mesh.RecalculateBounds();return mesh;
            }
        }
    }
}
