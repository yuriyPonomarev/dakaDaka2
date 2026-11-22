using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emberdelve.Level;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.EditorTools
{
    /// <summary>Continuous masonry, joined arch springlines and room-owned boundary faces.</summary>
    public static class ArchitecturalJoinRepair
    {
        private const string Folder = "Assets/Emberdelve/Art/Generated/ArchitecturalJoins";
        private const string Visual = "Meshy_Architecture_Visual";
        private const string Rooms = "Assets/Emberdelve/Prefabs/DungeonModules";
        private static readonly Dictionary<string, Mesh> Cache = new();
        private static bool Wall(string n) => n.StartsWith("WallRun_", StringComparison.Ordinal) || n.StartsWith("Closure_", StringComparison.Ordinal)
            || n.StartsWith("Arch_Jamb_", StringComparison.Ordinal) || n.StartsWith("Arch_Lintel_", StringComparison.Ordinal)
            || n == "Burial_Screen" || n == "Burial_Alcove_Back" || n == "Burial_Gallery_Screen"
            || n == "Cache_WestWall" || n == "Cache_FrontLeft" || n == "Cache_FrontRight"
            || n == "Concealed_Stone_Panel" || n == "Altar_Reredos" || n == "Infilled_Wall";
        private static bool Boundary(Transform t) => t.name.StartsWith("WallRun_", StringComparison.Ordinal)
            || t.name.StartsWith("Closure_", StringComparison.Ordinal) || t.name.StartsWith("Arch_Jamb_", StringComparison.Ordinal)
            || t.name.StartsWith("Arch_Lintel_", StringComparison.Ordinal) || t.name.StartsWith("BrokenMasonry_", StringComparison.Ordinal)
            || t.name.StartsWith("ChamferedCorner_", StringComparison.Ordinal);

        public static void Apply(GameObject root)
        {
            if (root.GetComponent<DungeonModuleDefinition>() == null) return;
            var stone = AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Generated/DungeonSurfaces/World_Stone_Wall.mat");
            if (stone == null) throw new InvalidOperationException("Missing masonry material");
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter == null || !Wall(filter.name) || !filter.TryGetComponent<BoxCollider>(out var box)) continue;
                var old = filter.transform.Find(Visual);
                if (old != null) Object.DestroyImmediate(old.gameObject);
                Vector3 size = Vector3.Scale(box.size, Abs(filter.transform.lossyScale));
                string key = "Masonry_" + Key(size);
                filter.sharedMesh = Saved(key, () => RoundedBox(size, box.center, box.size));
                var renderer = filter.GetComponent<MeshRenderer>();
                renderer.enabled = true; renderer.sharedMaterial = stone;
            }
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!Boundary(filter.transform) || filter.sharedMesh == null) continue;
                if (filter.TryGetComponent<MeshCollider>(out var boundaryCollider))
                {
                    string originalPath=AssetDatabase.GetAssetPath(boundaryCollider.sharedMesh);
                    var weathered=AssetDatabase.LoadAssetAtPath<Mesh>(originalPath.Replace("/FoldedMasonry_", "/WeatheredMasonry_"));
                    filter.sharedMesh=weathered!=null?weathered:boundaryCollider.sharedMesh;
                }
                // Both neighbours own only their half of the boundary thickness. Their exterior faces
                // no longer occupy identical depth, even after quarter-turn module rotation.
                Matrix4x4 matrix = root.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                bool outside = filter.sharedMesh.vertices.Any(v => { Vector3 p = matrix.MultiplyPoint3x4(v); return Mathf.Abs(p.x)>18.00001f || Mathf.Abs(p.z)>18.00001f; });
                if (!outside) continue;
                Mesh source = filter.sharedMesh;
                string key = "Boundary_v3_" + RuinsWallProfile.GeometryFingerprint(source) + "_" + MatrixKey(matrix);
                filter.sharedMesh = Saved(key, () => FitClosedBoundary(source, matrix));
            }
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
                if (filter != null && (filter.name == "Stone_Arch" || filter.name == "Reused_Stone_Arch")) FitSupportedArch(root, filter);
            Validate(root);
        }

        private static void FitSupportedArch(GameObject room, MeshFilter arch)
        {
            Transform t = arch.transform;
            // The authored semicircle starts at y=0. Match actual support capitals, not its outer AABB.
            var columns = room.GetComponentsInChildren<Transform>(true).Where(x => x.name == "Column_Shaft")
                .Select(x => new { shaft=x, p=t.InverseTransformPoint(x.position), top=t.InverseTransformPoint(x.TransformPoint(Vector3.up)).y })
                .Where(x => Mathf.Abs(x.p.z) < .55f && Mathf.Abs(x.top) < .025f && Mathf.Abs(x.p.x) > .6f && Mathf.Abs(x.p.x)<1.4f).ToArray();
            var left=columns.Where(x=>x.p.x<0).OrderBy(x=>Mathf.Abs(x.p.x+1.05f)).FirstOrDefault();
            var right=columns.Where(x=>x.p.x>0).OrderBy(x=>Mathf.Abs(x.p.x-1.05f)).FirstOrDefault();
            if(left==null || right==null)
            {
                // Two legacy ornaments (the enormous vault barrel and extraction arc) had no supports.
                // Retain node identities, but omit unsupported geometry; the extraction gate has its own frame.
                var visual=t.Find(Visual); if(visual!=null) Object.DestroyImmediate(visual.gameObject);
                arch.GetComponent<MeshRenderer>().enabled=false;
                if(arch.TryGetComponent<MeshCollider>(out var collider)) collider.enabled=false;
                return;
            }
            var crown=AssetDatabase.LoadAssetAtPath<Mesh>(DownloadedCryptArchitecture.Folder+"/OvergrownArchCrown.asset");
            if(crown==null) throw new InvalidOperationException("Missing imported crown");
            Vector3[] feet=crown.vertices.Where(v=>v.y<crown.bounds.min.y+.003f).ToArray();
            var lf=feet.Where(v=>v.x<0).ToArray(); var rf=feet.Where(v=>v.x>0).ToArray();
            if(lf.Length==0 || rf.Length==0) throw new InvalidOperationException("Cannot measure crown springline");
            float lx=(lf.Min(v=>v.x)+lf.Max(v=>v.x))*.5f, rx=(rf.Min(v=>v.x)+rf.Max(v=>v.x))*.5f;
            float sx=(right.p.x-left.p.x)/(rx-lx);
            Transform group=t.Find(Visual);
            if(group==null) { group=new GameObject(Visual).transform; group.SetParent(t,false); }
            if(group.childCount==0)
            {
                var rib=new GameObject("Imported_Arch_Crown").transform;rib.SetParent(group,false);
                rib.gameObject.AddComponent<MeshFilter>().sharedMesh=crown;
                rib.gameObject.AddComponent<MeshRenderer>().sharedMaterial=AssetDatabase.LoadAssetAtPath<Material>(DownloadedCryptArchitecture.Folder+"/ImportedStone.mat");
                rib.localScale=new Vector3(1,1,arch.sharedMesh.bounds.size.z/crown.bounds.size.z);
                rib.localPosition=new Vector3(0,0,-crown.bounds.center.z*rib.localScale.z);
                rib.gameObject.layer=t.gameObject.layer;
            }
            foreach(Transform rib in group)
            {
                if(!rib.TryGetComponent<MeshFilter>(out var mf)) continue;
                mf.sharedMesh=crown;
                Vector3 scale=rib.localScale, position=rib.localPosition;
                scale.x=sx; position.x=left.p.x-lx*sx;
                scale.y=arch.sharedMesh.bounds.size.y/crown.bounds.size.y;
                position.y=-crown.bounds.min.y*scale.y;
                rib.localScale=scale; rib.localPosition=position;
                // Exact joins at the source cut; neighbouring caps support the full stone springline.
                if(Mathf.Abs((lx*sx+position.x)-left.p.x)>.001f || Mathf.Abs((rx*sx+position.x)-right.p.x)>.001f)
                    throw new InvalidOperationException("Unsupported arch springline");
            }
            arch.GetComponent<MeshRenderer>().enabled=false;
        }

        public static void RepairSaved()
        {
            RequirePrimary(); Cache.Clear(); int count=0;
            foreach(string guid in AssetDatabase.FindAssets("t:Prefab",new[]{Rooms}))
            {
                string path=AssetDatabase.GUIDToAssetPath(guid); var root=PrefabUtility.LoadPrefabContents(path);
                try { Apply(root); PrefabUtility.SaveAsPrefabAsset(root,path); count++; }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            Debug.Log("[ArchitecturalJoins] saved="+count+" continuous masonry and supported arches; closed boundary render meshes fitted inside their owning room.");
        }
        public static void ValidateSaved()
        {
            int count=0;
            foreach(string guid in AssetDatabase.FindAssets("t:Prefab",new[]{Rooms}))
            {
                var root=PrefabUtility.LoadPrefabContents(AssetDatabase.GUIDToAssetPath(guid));
                try { Validate(root); count++; } finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            Debug.Log("[ArchitecturalJoins] validated rooms="+count);
        }
        public static void Validate(GameObject root)
        {
            foreach(var f in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if(f.name.StartsWith("Downloaded_Stone_",StringComparison.Ordinal)) throw new InvalidOperationException("Column fragments remain as wall tiles");
                if(Wall(f.name) && f.GetComponent<MeshRenderer>().enabled && (f.sharedMesh==null || f.sharedMesh.vertexCount<24))
                    throw new InvalidOperationException("Incomplete solid wall "+f.name);
                if(!Boundary(f.transform) || f.sharedMesh==null)continue;
                Matrix4x4 m=root.transform.worldToLocalMatrix*f.transform.localToWorldMatrix;
                foreach(var v in f.sharedMesh.vertices) { Vector3 p=m.MultiplyPoint3x4(v); if(Mathf.Abs(p.x)>18.0001f || Mathf.Abs(p.z)>18.0001f)throw new InvalidOperationException("Boundary ownership failed: "+f.name); }
            }
        }

        private static Mesh RoundedBox(Vector3 physical, Vector3 center, Vector3 size)
        {
            float bevel=Mathf.Min(.035f,Mathf.Min(physical.x,Mathf.Min(physical.y,physical.z))*.12f);
            var vertices=new List<Vector3>(); var normals=new List<Vector3>(); var uv=new List<Vector2>(); var indices=new List<int>();
            Vector3 half=physical*.5f, core=half-Vector3.one*bevel;
            for(int axis=0;axis<3;axis++) for(int sign=-1;sign<=1;sign+=2)
            {
                int u=(axis+1)%3,w=(axis+2)%3;
                float[] us={-half[u],-core[u],core[u],half[u]}, ws={-half[w],-core[w],core[w],half[w]};
                for(int a=0;a<3;a++)for(int b=0;b<3;b++)
                {
                    int start=vertices.Count;
                    foreach(var ab in new[]{new Vector2Int(a,b),new Vector2Int(a+1,b),new Vector2Int(a+1,b+1),new Vector2Int(a,b+1)})
                    {
                        Vector3 p=Vector3.zero;p[axis]=sign*half[axis];p[u]=us[ab.x];p[w]=ws[ab.y];
                        Vector3 c=new Vector3(Mathf.Clamp(p.x,-core.x,core.x),Mathf.Clamp(p.y,-core.y,core.y),Mathf.Clamp(p.z,-core.z,core.z));
                        Vector3 n=(p-c).normalized;p=c+n*bevel;
                        vertices.Add(center+Vector3.Scale(new Vector3(p.x/physical.x,p.y/physical.y,p.z/physical.z),size));
                        normals.Add(Vector3.Scale(n,new Vector3(physical.x/size.x,physical.y/size.y,physical.z/size.z)).normalized); uv.Add(new Vector2(p[u],p[w]));
                    }
                    if(sign>0) indices.AddRange(new[]{start,start+1,start+2,start,start+2,start+3});
                    else indices.AddRange(new[]{start,start+2,start+1,start,start+3,start+2});
                }
            }
            var mesh=new Mesh();mesh.SetVertices(vertices);mesh.SetNormals(normals);mesh.SetUVs(0,uv);mesh.SetTriangles(indices,0);mesh.RecalculateBounds();return mesh;
        }
        private static Mesh FitClosedBoundary(Mesh source, Matrix4x4 matrix)
        {
            Vector3[] vertices=source.vertices;
            var bounds=new Bounds(matrix.MultiplyPoint3x4(vertices[0]),Vector3.zero);
            foreach(var v in vertices) bounds.Encapsulate(matrix.MultiplyPoint3x4(v));
            Vector3 min=bounds.min,max=bounds.max;
            min.x=Mathf.Max(min.x,-18);max.x=Mathf.Min(max.x,18);
            min.z=Mathf.Max(min.z,-18);max.z=Mathf.Min(max.z,18);
            Vector3 size=max-min;
            if(Mathf.Min(size.x,Mathf.Min(size.y,size.z))<=0)throw new InvalidOperationException("Boundary envelope is empty");
            Vector3 ratio=new Vector3(size.x/bounds.size.x,size.y/bounds.size.y,size.z/bounds.size.z);
            Vector3 offset=(min+max)*.5f-Vector3.Scale(bounds.center,ratio);
            // Affine compression retains every original closed face and the inner envelope. Clipping
            // the overlapping ornamental shells would require a boolean union to cap them correctly.
            Matrix4x4 fit=matrix.inverse*Matrix4x4.TRS(offset,Quaternion.identity,ratio)*matrix;
            Matrix4x4 normal=fit.inverse.transpose;
            var mesh=Object.Instantiate(source);
            mesh.vertices=vertices.Select(fit.MultiplyPoint3x4).ToArray();
            mesh.normals=source.normals.Select(n=>normal.MultiplyVector(n).normalized).ToArray();
            mesh.RecalculateBounds();mesh.RecalculateTangents();return mesh;
        }
        private static Mesh Saved(string key,Func<Mesh> create)
        {
            if(Cache.TryGetValue(key,out var found))return found;
            if(!AssetDatabase.IsValidFolder(Folder))AssetDatabase.CreateFolder("Assets/Emberdelve/Art/Generated","ArchitecturalJoins");
            string path=Folder+"/"+key+".asset";found=AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if(found==null){found=create();found.name=key;AssetDatabase.CreateAsset(found,path);}return Cache[key]=found;
        }
        private static Vector3 Abs(Vector3 v)=>new(Mathf.Abs(v.x),Mathf.Abs(v.y),Mathf.Abs(v.z));
        private static string Key(Vector3 v)=>Mathf.RoundToInt(v.x*10000)+"_"+Mathf.RoundToInt(v.y*10000)+"_"+Mathf.RoundToInt(v.z*10000);
        private static string MatrixKey(Matrix4x4 m)
        {
            using var sha=System.Security.Cryptography.SHA256.Create();
            var text=string.Join(",",Enumerable.Range(0,16).Select(i=>Mathf.RoundToInt(m[i]*10000)));
            return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text))).Replace("-","").Substring(0,16);
        }
        private static void RequirePrimary()
        {
            if(!Path.GetFullPath(Application.dataPath).Replace('\\','/').Equals("C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff/Assets",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Author only in clean_handoff");
        }
    }
}
