using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Emberdelve.Data;
using Object=UnityEngine.Object;
namespace Emberdelve.EditorTools
{
    // Original fitted armour over the project's existing CC0 human rig. No gameplay components.
    public static class EmberdelveGrimClassVisuals
    {
        const string Folder="Assets/Emberdelve/Resources/Emberdelve/PlayerVisuals";
        const string Art="Assets/Emberdelve/Art/Models/GrimPlayers";
        static string current;
        static Material steel, leather, cloth, dark, trim, tabard;
        public static void BuildAndPackage()
        {
            BuildVisuals();
            EmberdelveWindowsBuild.BuildNetPlayable();
        }
        [MenuItem("Emberdelve/Visual/Rebuild Player Visuals")]
        public static void BuildVisuals()
        {
            EmberdelveVisualSetup.EnsureFolder(Art);
            steel=Material("WornSteel",new Color(.29f,.31f,.33f),.72f,.38f);
            leather=Material("WeatheredLeather",new Color(.24f,.17f,.105f),0,.20f);
            cloth=Material("CharcoalLinen",new Color(.16f,.17f,.145f),0,.08f);
            dark=Material("BlackenedLeather",new Color(.045f,.037f,.03f),0,.15f);
            trim=Material("OldBrass",new Color(.30f,.22f,.11f),.65f,.25f);
            tabard=Material("FadedOxblood",new Color(.16f,.055f,.04f),0,.08f);
            Build("Warrior"); Build("Archer"); AssetDatabase.SaveAssets();
        }
        static Material Material(string name,Color color,float metal,float smooth)
        {
            string path=Art+"/"+name+".mat";
            var m=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(m==null){m=new Material(Shader.Find("Universal Render Pipeline/Lit"));AssetDatabase.CreateAsset(m,path);}
            // Fine deterministic material grain, not a flat colour on a toy silhouette.
            var t=new Texture2D(128,128,TextureFormat.RGB24,false); var pixels=new Color[128*128];
            var random=new System.Random(19);
            for(int y=0;y<128;y++) for(int x=0;x<128;x++)
            {
                float grain=.75f+(float)random.NextDouble()*.35f;
                if(metal<.5f) grain*=((x+y)%4==0 ? .86f:1f);
                else if((x*13+y*3)%113==0) grain=1.35f;
                pixels[y*128+x]=new Color(grain,grain,grain);
            }
            t.SetPixels(pixels);t.Apply();
            string texPath=Art+"/"+name+"Grain.asset"; var existing=AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            if(existing==null){AssetDatabase.CreateAsset(t,texPath);existing=t;}else {EditorUtility.CopySerialized(t,existing);Object.DestroyImmediate(t);}
            m.mainTexture=existing;m.color=color;m.SetFloat("_Metallic",metal);m.SetFloat("_Smoothness",smooth);EditorUtility.SetDirty(m);return m;
        }
        static void Build(string className)
        {
            current=className;shells.Clear();
            string source=EmberdelveUnifiedHuman.ModelPath;
            var root=new GameObject(className+"Avatar");
            try
            {
                var model=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(source),root.transform);model.name="HumanModel";
                var animator=model.GetComponent<Animator>(); if(animator==null)animator=model.AddComponent<Animator>();
                animator.avatar=AssetDatabase.LoadAllAssetsAtPath(source).OfType<Avatar>().First(a=>a.isHuman);
                animator.applyRootMotion=false;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
                animator.runtimeAnimatorController=Controller(className);
                animator.Rebind(); animator.Play("Idle",0,0); animator.Update(0);
                var body=model.GetComponentInChildren<SkinnedMeshRenderer>();
                body.updateWhenOffscreen=true;
                var mesh=Object.Instantiate(body.sharedMesh);mesh.name=className+"ClothedBody";
                var verts=mesh.vertices;var uv=new Vector2[verts.Length];
                for(int i=0;i<verts.Length;i++)uv[i]=new Vector2(verts[i].x*3,verts[i].z*3);
                mesh.uv=uv;
                var groups=new[]{new List<int>(),new List<int>(),new List<int>()};var triangles=mesh.triangles;var weights=mesh.boneWeights;
                for(int i=0;i<triangles.Length;i+=3)
                {
                    int vi=triangles[i];var weightedBone=body.bones[EmberdelveFirstPersonArms.DominantBone(weights[vi])];string bone=weightedBone.name.ToLowerInvariant();
                    if(bone.Contains("foot")||bone.Contains("toe")) continue;
                    bool faceSkin=body.transform.TransformPoint(verts[vi]).y>1.49f;
                    int group=faceSkin||bone.Contains("hand")||bone.Contains("wrist")||bone.Contains("metacarpal")||bone.Contains("finger")||bone.Contains("thumb")||bone.Contains("head")?1:(bone.Contains("foot")||bone.Contains("toe")?2:0);
                    if(bone.StartsWith("eye.")) group=2;
                    groups[group].AddRange(new[]{triangles[i],triangles[i+1],triangles[i+2]});
                }
                mesh.subMeshCount=3;for(int i=0;i<3;i++)mesh.SetTriangles(groups[i],i);body.sharedMesh=SaveMesh(mesh,"Body");body.sharedMaterials=new[]{AssetDatabase.LoadAssetAtPath<Material>(EmberdelveFirstPersonArms.SleeveMaterialPath),AssetDatabase.LoadAssetAtPath<Material>(EmberdelveFirstPersonArms.SkinMaterialPath),dark};
                // All garment measurements derive from the unposed, human skeleton in metres.
                Transform hips=animator.GetBoneTransform(HumanBodyBones.Hips),chest=animator.GetBoneTransform(HumanBodyBones.Chest),head=animator.GetBoneTransform(HumanBodyBones.Head);
                Debug.Log("[GrimPlayers] "+className+" body="+body.bounds+" hips="+hips.position+" chest="+chest.position+" head="+head.position);
                bool warrior=className=="Warrior";
                float waist=hips.position.y+.09f,top=animator.GetBoneTransform(HumanBodyBones.Neck).position.y-.025f;
                Shell("Cuirass",chest,new Vector3(0,waist,0),Quaternion.identity,
                    new[]{0f,.09f,(top-waist)*.65f,top-waist},new[]{.155f,.19f,.245f,.16f},new[]{.105f,.135f,.15f,.105f},warrior?steel:leather,16);
                Shell("Belt",hips,new Vector3(0,waist,0),Quaternion.identity,new[]{-.045f,.015f},new[]{.18f,.175f},new[]{.127f,.125f},dark,16);
                Plate("Buckle",hips,new Vector3(0,waist-.015f,.137f),new Vector3(.065f,.052f,.017f),trim);
                for(int i=0;i<3;i++)
                    Shell("Fauld"+i,hips,new Vector3(0,waist-.07f-i*.07f,0),Quaternion.identity,new[]{-.065f,0f},new[]{.205f+i*.008f,.185f+i*.008f},new[]{.135f,.125f},warrior?steel:leather,16);
                foreach(bool left in new[]{false,true})
                {
                    string side=left?"L":"R";
                    var upper=animator.GetBoneTransform(left?HumanBodyBones.LeftUpperArm:HumanBodyBones.RightUpperArm);
                    var elbow=animator.GetBoneTransform(left?HumanBodyBones.LeftLowerArm:HumanBodyBones.RightLowerArm);
                    var hand=animator.GetBoneTransform(left?HumanBodyBones.LeftHand:HumanBodyBones.RightHand);
                    var knee=animator.GetBoneTransform(left?HumanBodyBones.LeftLowerLeg:HumanBodyBones.RightLowerLeg);
                    var foot=animator.GetBoneTransform(left?HumanBodyBones.LeftFoot:HumanBodyBones.RightFoot);
                    Vector3 axis=(elbow.position-upper.position).normalized;
                    Shell("Shoulder"+side,upper,upper.position+axis*.035f,Quaternion.FromToRotation(Vector3.up,axis),new[]{-.03f,.045f,.12f,.17f},new[]{.075f,.135f,.12f,.09f},new[]{.08f,.125f,.11f,.08f},warrior?steel:leather,12);
                    axis=(hand.position-elbow.position).normalized;
                    Shell("Bracer"+side,elbow,elbow.position+axis*.07f,Quaternion.FromToRotation(Vector3.up,axis),new[]{0f,.11f,.19f},new[]{.063f,.060f,.048f},new[]{.06f,.06f,.045f},warrior?steel:leather,12);
                    axis=(foot.position-knee.position).normalized;
                    Shell("Greave"+side,knee,knee.position+axis*.04f,Quaternion.FromToRotation(Vector3.up,axis),new[]{0f,.12f,.29f},new[]{.085f,.088f,.060f},new[]{.085f,.095f,.07f},warrior?steel:dark,12);
                    Shell("Boot"+side,foot,foot.position+new Vector3(0,-.065f,.055f),Quaternion.identity,new[]{0f,.06f,.14f,.25f},new[]{.065f,.07f,.058f,.06f},new[]{.145f,.15f,.075f,.07f},dark,16);
                    Plate("BeltPouch"+side,hips,new Vector3(left?-.17f:.17f,waist-.10f,.12f),new Vector3(.10f,.13f,.075f),leather);
                }
                Vector3 h=head.position-new Vector3(0,.09f,0);
                if(warrior)
                {
                    Plate("TabardChest",chest,new Vector3(0,waist+.22f,.155f),new Vector3(.13f,.30f,.012f),tabard);
                    Plate("TabardSkirt",hips,new Vector3(0,waist-.24f,.15f),new Vector3(.15f,.35f,.012f),tabard);
                    Shell("GreatHelm",head,h+new Vector3(0,-.02f,0),Quaternion.identity,new[]{0f,.07f,.16f,.24f,.28f},new[]{.12f,.135f,.125f,.105f,.025f},new[]{.12f,.135f,.13f,.105f,.03f},steel,16);
                    // Narrow visor, nasal bar and breathing slots read as a closed medieval helmet.
                    Plate("VisorSlit",head,h+new Vector3(0,.13f,.132f),new Vector3(.19f,.016f,.012f),dark);
                    Plate("Nasal",head,h+new Vector3(0,.09f,.145f),new Vector3(.022f,.13f,.016f),steel);
                    for(int side=-1;side<=1;side+=2)for(int row=0;row<3;row++)
                        Plate("Vent"+side+row,head,h+new Vector3(side*.055f,.025f+row*.022f,.136f),new Vector3(.035f,.006f,.009f),dark);
                }
                else
                {
                    // Open-front cowl: front panels stop beside the face; no cartoon eyes.
                    Shell("Hood",head,h+new Vector3(0,-.08f,-.025f),Quaternion.identity,new[]{0f,.12f,.25f,.33f,.36f},new[]{.17f,.15f,.135f,.08f,.015f},new[]{.15f,.16f,.155f,.10f,.025f},cloth,20,true);
                    Shell("Cowl",chest,new Vector3(0,top-.07f,0),Quaternion.identity,new[]{0f,.09f,.14f},new[]{.23f,.14f,.10f},new[]{.145f,.115f,.09f},cloth,20);
                    Plate("FaceWrap",head,h+new Vector3(0,.035f,.14f),new Vector3(.17f,.085f,.045f),cloth);
                    // Tall leather quiver and separate arrow shafts identify the archer from behind.
                    Shell("Quiver",chest,new Vector3(.13f,waist+.06f,-.19f),Quaternion.Euler(0,0,-12),new[]{0f,.40f,.44f},new[]{.065f,.075f,.08f},new[]{.06f,.07f,.075f},leather,12);
                    for(int i=0;i<5;i++)
                    {
                        Vector3 a=new Vector3(.08f+i*.025f,waist+.42f,-.19f);
                        Shell("Arrow"+i,chest,a,Quaternion.Euler(0,0,-12),new[]{0f,.25f},new[]{.004f,.004f},new[]{.004f,.004f},trim,6);
                        Plate("Fletching"+i,chest,a+new Vector3(.045f,.19f,0),new Vector3(.025f,.07f,.008f),cloth);
                    }
                }
                if (warrior) EmberdelveArmourDetail.Apply(root);
                SmoothShells();
                animator.Play("Idle",0,0);animator.Update(0);
                var face=root.GetComponentsInChildren<MeshRenderer>().First(r=>r.name==(warrior?"VisorSlit":"FaceWrap"));
                if(face.bounds.center.z<head.position.z)throw new Exception("Headwear faces away from live rig");
                var prefab=PrefabUtility.SaveAsPrefabAsset(root,Folder+"/"+className+".prefab");
                var data=AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_"+className+".asset");data.characterVisual=prefab;EditorUtility.SetDirty(data);
                if(root.GetComponentsInChildren<Collider>().Length!=0)throw new Exception("Cosmetic collider");
                foreach(string state in new[]{"Idle","Walk","Run","Block","Death"})if(!animator.HasState(0,Animator.StringToHash(state)))throw new Exception("Missing state "+state);
                Debug.Log("[GrimPlayers] PASS "+className+" bones="+body.bones.Length);
            }
            finally{Object.DestroyImmediate(root);}
        }
        static AnimatorController Controller(string name)
        {
            string path=Art+"/"+name+".controller";
            var controller=AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if(controller==null)controller=AnimatorController.CreateAnimatorControllerAtPath(path);
            var sm=controller.layers[0].stateMachine;
            foreach(var child in sm.states)sm.RemoveState(child.state);
            string[] states={"Idle","Walk","Run","Block","Death"};string[] clips={"Idle_Loop","Walk_Loop","Jog_Fwd_Loop","Idle_Shield_Loop","Death01"};
            for(int i=0;i<states.Length;i++)
            {
                var clip=EmberdelveEnemyVisualFactory.FindClip(clips[i]);if(clip==null)throw new Exception("Missing clip "+clips[i]);
                var state=sm.AddState(states[i]);state.motion=clip;if(i==0)sm.defaultState=state;
            }
            EditorUtility.SetDirty(controller);return controller;
        }
        static Mesh SaveMesh(Mesh mesh,string name)
        {
            string path=Art+"/"+current+"_"+name+".asset";var old=AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if(old==null){AssetDatabase.CreateAsset(mesh,path);return mesh;}
            EditorUtility.CopySerialized(mesh,old);old.UploadMeshData(false);EditorUtility.SetDirty(old);Object.DestroyImmediate(mesh);return old;
        }
        static void Plate(string name,Transform bone,Vector3 centre,Vector3 size,Material material)
        {
            Shell(name,bone,centre-Vector3.up*size.y*.5f,Quaternion.identity,new[]{0f,size.y},new[]{size.x*.5f,size.x*.5f},new[]{size.z*.5f,size.z*.5f},material,4,false,45);
        }
        static void Shell(string name,Transform bone,Vector3 origin,Quaternion rotation,float[] y,float[] x,float[] z,Material material,int sides,bool hood=false,float offset=0)
        {
            var vertices=new List<Vector3>();var uv=new List<Vector2>();var tris=new List<int>();
            for(int row=0;row<y.Length;row++)for(int i=0;i<=sides;i++)
            {
                float angle=(i*360f/sides+offset)*Mathf.Deg2Rad;
                vertices.Add(bone.InverseTransformPoint(origin+rotation*new Vector3(Mathf.Sin(angle)*x[row],y[row],Mathf.Cos(angle)*z[row])));
                uv.Add(new Vector2(i/(float)sides*2,row/(float)(y.Length-1)*2));
            }
            for(int row=0;row<y.Length-1;row++)for(int i=0;i<sides;i++)
            {
                float angle=(i+.5f)*360/sides;
                if(hood && row<2 && (angle<55||angle>305))continue;
                int a=row*(sides+1)+i,b=a+sides+1;
                tris.AddRange(new[]{a,a+1,b,a+1,b+1,b});
                if(hood)tris.AddRange(new[]{a,b,a+1,a+1,b,b+1});
            }
            var mesh=new Mesh{name=current+"_"+name};mesh.SetVertices(vertices);mesh.SetUVs(0,uv);mesh.SetTriangles(tris,0);mesh.RecalculateNormals();mesh.RecalculateBounds();
            var go=new GameObject(name,typeof(MeshFilter),typeof(MeshRenderer));go.transform.SetParent(bone,false);
            go.GetComponent<MeshFilter>().sharedMesh=SaveMesh(mesh,name);go.GetComponent<MeshRenderer>().sharedMaterial=material;
            if(sides>4)shells.Add(new ShellJob{name=name,bone=bone,origin=origin,rotation=rotation,y=y,x=x,z=z,sides=sides,hood=hood,offset=offset,filter=go.GetComponent<MeshFilter>()});
        }
        // Smooth pass: the coarse shells above stay as the stable source topology for EmberdelveArmourDetail; every shell
        // that is still shown as-is gets a spline-profiled, three-times-denser copy saved beside it as <part>_Smooth.
        struct ShellJob{public string name;public Transform bone;public Vector3 origin;public Quaternion rotation;public float[] y,x,z;public int sides;public bool hood;public float offset;public MeshFilter filter;}
        static readonly List<ShellJob> shells=new List<ShellJob>();
        static float Spline(float[] v,int row,float t)
        {
            float p0=v[Mathf.Max(row-1,0)],p1=v[row],p2=v[Mathf.Min(row+1,v.Length-1)],p3=v[Mathf.Min(row+2,v.Length-1)];
            return .5f*(2*p1+(-p0+p2)*t+(2*p0-5*p1+4*p2-p3)*t*t+(-p0+3*p1-3*p2+p3)*t*t*t);
        }
        static void SmoothShells()
        {
            const int Sub=5;
            foreach(var job in shells)
            {
                if(job.filter==null||job.filter.sharedMesh==null||job.filter.sharedMesh.name!=current+"_"+job.name)continue;
                int sides=job.sides*3,rows=(job.y.Length-1)*Sub+1,stride=sides+1;
                var vertices=new List<Vector3>();var uv=new List<Vector2>();var tris=new List<int>();
                for(int r=0;r<rows;r++)
                {
                    int row=Mathf.Min(r/Sub,job.y.Length-2);float t=(r-row*Sub)/(float)Sub;
                    float y=Mathf.Lerp(job.y[row],job.y[row+1],t),x=Mathf.Max(0,Spline(job.x,row,t)),z=Mathf.Max(0,Spline(job.z,row,t));
                    for(int i=0;i<=sides;i++)
                    {
                        float angle=(i*360f/sides+job.offset)*Mathf.Deg2Rad;
                        vertices.Add(job.bone.InverseTransformPoint(job.origin+job.rotation*new Vector3(Mathf.Sin(angle)*x,y,Mathf.Cos(angle)*z)));
                        uv.Add(new Vector2(i/(float)sides*2,r/(float)(rows-1)*2));
                    }
                }
                for(int r=0;r<rows-1;r++)for(int i=0;i<sides;i++)
                {
                    float angle=(i+.5f)*360f/sides;
                    if(job.hood && r/Sub<2 && (angle<55||angle>305))continue;
                    int a=r*stride+i,b=a+stride;
                    tris.AddRange(new[]{a,a+1,b,a+1,b+1,b});
                }
                var mesh=new Mesh{name=current+"_"+job.name+"_Smooth"};
                mesh.SetVertices(vertices);mesh.SetUVs(0,uv);mesh.SetTriangles(tris,0);mesh.RecalculateNormals();
                var normals=mesh.normals;
                for(int r=0;r<rows;r++){Vector3 n=(normals[r*stride]+normals[r*stride+sides]).normalized;normals[r*stride]=n;normals[r*stride+sides]=n;}
                if(job.hood)
                {
                    // Inside of the open hood: separate vertices, so the two faces keep opposite normals.
                    int count=vertices.Count,triCount=tris.Count;var all=new Vector3[count*2];
                    for(int i=0;i<count;i++){vertices.Add(vertices[i]);uv.Add(uv[i]);all[i]=normals[i];all[count+i]=-normals[i];}
                    for(int i=0;i<triCount;i+=3)tris.AddRange(new[]{tris[i]+count,tris[i+2]+count,tris[i+1]+count});
                    mesh.SetVertices(vertices);mesh.SetUVs(0,uv);mesh.SetTriangles(tris,0);normals=all;
                }
                mesh.normals=normals;mesh.RecalculateTangents();mesh.RecalculateBounds();
                job.filter.sharedMesh=SaveMesh(mesh,job.name+"_Smooth");
            }
            shells.Clear();
        }
    }
}
