using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Emberdelve.Core;
using Object = UnityEngine.Object;

namespace Emberdelve.EditorTools
{
    internal static class DungeonRoomProps
    {
        private const string Folder = "Assets/Emberdelve/Art/Generated/RoomProps";
        private static Mesh vase;
        private static Material clay;
        [MenuItem("Emberdelve/Visual/Furnish Dungeon Rooms")]
        public static void ApplyAll()
        {
            int total = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[]{"Assets/Emberdelve/Prefabs/DungeonModules"}))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var root = PrefabUtility.LoadPrefabContents(path);
                try { int count=Apply(root); total+=count; PrefabUtility.SaveAsPrefabAsset(root,path); Debug.Log("[RoomProps] " + root.name + " clusters=" + count); }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[RoomProps] Saved " + total + " prop groups; all outside central routes and clear of existing masonry.");
        }
        public static int Apply(GameObject root)
        {
            EnsureAssets();
            var old = root.transform.Find("Room_Props");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var blocked = new List<Bounds>();
            foreach (var c in root.GetComponentsInChildren<BoxCollider>(true))
            {
                if (c.isTrigger) continue;
                Bounds b = TransformBounds(c.transform, new Bounds(c.center,c.size));
                if (b.max.y > .15f) blocked.Add(b);
            }
            // Also reserve existing decorative assets that intentionally have no collision.
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                Bounds b = r.bounds;
                if (b.size.y > .25f && b.min.y < 2f && b.max.y > .15f) blocked.Add(b);
            }
            var props = new GameObject("Room_Props"); props.transform.SetParent(root.transform,false);
            int hash=17; foreach(char c in root.name) hash=unchecked(hash*31+c);
            var random=new System.Random(hash & int.MaxValue);
            var candidates=new List<Vector3>();
            foreach(float along in new[]{-9.5f,-6.8f,-3.6f,3.6f,6.8f,9.5f})
                foreach(float side in new[]{-10.7f,10.7f}) { candidates.Add(new Vector3(side,.09f,along)); candidates.Add(new Vector3(along,.09f,side)); }
            for(int i=candidates.Count-1;i>0;i--){int j=random.Next(i+1); var t=candidates[i];candidates[i]=candidates[j];candidates[j]=t;}
            int count=0;
            foreach(var pos in candidates)
            {
                if(count>=8)break;
                var reservation=new Bounds(pos+Vector3.up*.8f,new Vector3(1.75f,1.8f,1.75f));
                bool occupied=false; foreach(var b in blocked) if(b.Intersects(reservation)){occupied=true;break;}
                if(occupied)continue;
                var group=new GameObject("Supplies_"+count);group.transform.SetParent(props.transform,false);group.transform.localPosition=pos;group.transform.localScale=Vector3.one*.78f;
                group.transform.localRotation=Quaternion.Euler(0,random.Next(-15,16),0);
                int style=(count+(hash & 7))%3;
                if(style==0)
                {
                    Model(group.transform,"wooden_crate_02",new Vector3(-.35f,0,0),.85f,0);
                    Model(group.transform,"wooden_bucket_01",new Vector3(.55f,0,.3f),.45f,25);
                    Vase(group.transform,new Vector3(.55f,0,-.45f),.75f);
                }
                else if(style==1)
                {
                    Vase(group.transform,new Vector3(-.45f,0,.1f),1.12f);
                    Vase(group.transform,new Vector3(.3f,0,.48f),.72f);
                    Model(group.transform,"wooden_crate_02",new Vector3(.35f,0,-.45f),.48f,10);
                }
                else
                {
                    Model(group.transform,"wine_barrel_01",new Vector3(-.4f,0,0),1.05f,0);
                    Model(group.transform,"wooden_crate_02",new Vector3(.45f,0,.15f),.62f,-8);
                    Vase(group.transform,new Vector3(.45f,.62f,.15f),.47f);
                }
                // Fit each mixed model group inside the reserved niche regardless of source-model proportions.
                var pieces=group.GetComponentsInChildren<Renderer>();
                Bounds combined=pieces[0].bounds;foreach(var r in pieces)combined.Encapsulate(r.bounds);
                float fit=Mathf.Min(1f,1.6f/Mathf.Max(combined.size.x,combined.size.z),1.6f/combined.size.y);
                group.transform.localScale*=fit;
                combined=pieces[0].bounds;foreach(var r in pieces)combined.Encapsulate(r.bounds);
                group.transform.position+=new Vector3(pos.x-combined.center.x,pos.y-combined.min.y,pos.z-combined.center.z);
                // Ensure imported source dimensions cannot silently intrude into adjacent cover/routes.
                bool fits=true;
                foreach(var r in group.GetComponentsInChildren<Renderer>())
                    if(!reservation.Contains(r.bounds.min)||!reservation.Contains(r.bounds.max)){fits=false;break;}
                if(!fits){Object.DestroyImmediate(group);continue;}
                blocked.Add(reservation);count++;
            }
            return count;
        }
        private static void Model(Transform parent,string id,Vector3 position,float height,float yaw)
        {
            var source=EmberdelvePolyHavenImport.LoadModel(id);
            if(source==null)throw new InvalidOperationException("Missing licensed prop " + id);
            var model=Object.Instantiate(source);model.name=id;
            foreach(var c in model.GetComponentsInChildren<Collider>())Object.DestroyImmediate(c);
            var renderers=model.GetComponentsInChildren<Renderer>();
            Bounds bounds=renderers[0].bounds;foreach(var r in renderers)bounds.Encapsulate(r.bounds);
            model.transform.localScale*=height/Mathf.Max(.01f,bounds.size.y);
            bounds=renderers[0].bounds;foreach(var r in renderers)bounds.Encapsulate(r.bounds);
            model.transform.position-=new Vector3(bounds.center.x,bounds.min.y,bounds.center.z);
            var anchor=new GameObject(id+"_Anchor");anchor.transform.SetParent(parent,false);
            anchor.transform.localPosition=position;anchor.transform.localRotation=Quaternion.Euler(0,yaw,0);
            model.transform.SetParent(anchor.transform,false);
            anchor.layer=LayerMask.NameToLayer(GameConfig.LevelLayerName);
            var collider=anchor.AddComponent<BoxCollider>();collider.center=new Vector3(0,height*.5f,0);collider.size=new Vector3(bounds.size.x,height,bounds.size.z);
        }
        private static void Vase(Transform parent,Vector3 position,float height)
        {
            var go=new GameObject("Clay_Vessel");go.transform.SetParent(parent,false);go.transform.localPosition=position;go.transform.localScale=Vector3.one*height;
            go.layer=LayerMask.NameToLayer(GameConfig.LevelLayerName);
            go.AddComponent<MeshFilter>().sharedMesh=vase;go.AddComponent<MeshRenderer>().sharedMaterial=clay;
            var c=go.AddComponent<CapsuleCollider>();c.center=Vector3.up*.5f;c.height=1;c.radius=.31f;
        }
        private static Bounds TransformBounds(Transform t,Bounds local)
        {
            var b=new Bounds(t.TransformPoint(local.min),Vector3.zero);
            for(int i=0;i<8;i++)b.Encapsulate(t.TransformPoint(local.center+Vector3.Scale(local.extents,new Vector3((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1))));
            return b;
        }
        private static void EnsureAssets()
        {
            if(vase!=null&&clay!=null)return;
            if(!AssetDatabase.IsValidFolder(Folder))AssetDatabase.CreateFolder("Assets/Emberdelve/Art/Generated","RoomProps");
            vase=AssetDatabase.LoadAssetAtPath<Mesh>(Folder+"/ClayVessel.asset");
            if(vase==null)
            {
                var profile=new[]{new Vector2(.2f,0),new Vector2(.24f,.08f),new Vector2(.34f,.35f),new Vector2(.32f,.6f),new Vector2(.19f,.81f),new Vector2(.14f,.9f),new Vector2(.19f,.95f),new Vector2(.19f,1f),new Vector2(.135f,1f),new Vector2(.11f,.93f),new Vector2(.13f,.83f),new Vector2(0,.78f)};
                var vs=new List<Vector3>();var ts=new List<int>();
                for(int row=0;row<profile.Length;row++)for(int i=0;i<=32;i++){float a=i*Mathf.PI*2/32;vs.Add(new Vector3(Mathf.Cos(a)*profile[row].x,profile[row].y,Mathf.Sin(a)*profile[row].x));}
                for(int row=0;row<profile.Length-1;row++)for(int i=0;i<32;i++){int a=row*33+i,b=a+33;ts.AddRange(new[]{a,b,a+1,a+1,b,b+1});}
                vase=new Mesh{name="Clay vessel with hollow rim"};vase.SetVertices(vs);vase.SetTriangles(ts,0);vase.RecalculateNormals();vase.RecalculateBounds();AssetDatabase.CreateAsset(vase,Folder+"/ClayVessel.asset");
            }
            clay=AssetDatabase.LoadAssetAtPath<Material>(Folder+"/WeatheredClay.mat");
            if(clay==null)
            {
                clay=new Material(AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Generated/DungeonSurfaces/World_Stone_Ceiling.mat"));
                clay.name="WeatheredClay";clay.SetColor("_BaseColor",new Color(.57f,.3f,.16f));clay.SetFloat("_WorldTiling",2f);clay.SetFloat("_BumpScale",.15f);clay.SetFloat("_Smoothness",.25f);AssetDatabase.CreateAsset(clay,Folder+"/WeatheredClay.mat");
            }
        }
    }
}

