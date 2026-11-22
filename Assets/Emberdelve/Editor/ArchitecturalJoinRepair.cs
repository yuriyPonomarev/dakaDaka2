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
            || t.name.StartsWith("Socket_UpperInfill_", StringComparison.Ordinal)
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
            RepairSocketHeaders(root, stone);
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
                if (filter != null && (filter.name == "Stone_Arch" || filter.name == "Reused_Stone_Arch")) FitSupportedArch(root, filter);
            Validate(root);
        }

        private static void RepairSocketHeaders(GameObject root, Material stone)
        {
            Transform roof = root.transform.Find("Roof");
            if (roof == null || !roof.TryGetComponent<BoxCollider>(out var ceiling)) return;
            float top = root.transform.InverseTransformPoint(roof.TransformPoint(
                ceiling.center + Vector3.down * ceiling.size.y * .5f)).y;
            Mesh unit = Saved("SocketJoinedBox_v1", ClosedJoinedBox);
            foreach (string side in new[] { "North", "East", "South", "West" })
            {
                Transform closure = root.transform.Find("Closure_" + side), lintel = root.transform.Find("Arch_Lintel_" + side);
                if (closure == null || lintel == null) throw new InvalidOperationException("Missing roofed socket frame: " + side);
                var closureFilter = closure.GetComponent<MeshFilter>();
                Bounds panel = MeshBoundsIn(closureFilter, root.transform);
                Bounds bridge = MeshBoundsIn(lintel.GetComponent<MeshFilter>(), root.transform);
                if (top <= bridge.max.y || bridge.min.y <= panel.min.y)
                    throw new InvalidOperationException("Invalid socket/lintel vertical span: " + side);

                // Opening a route disables Closure_* completely. Its upper half therefore
                // cannot also serve as the wall above the doorway. This independent solid
                // fills the exact lintel-to-roof span in both socket states.
                string name = "Socket_UpperInfill_" + side;
                Transform header = root.transform.Find(name);
                if (header == null) { header = new GameObject(name).transform; header.SetParent(root.transform, false); }
                Vector3 min = panel.min, max = panel.max; min.y = bridge.max.y; max.y = top;
                header.localPosition = (min + max) * .5f;
                header.localRotation = Quaternion.identity; header.localScale = max - min;
                header.gameObject.layer = closure.gameObject.layer;
                header.gameObject.SetActive(true);
                if (!header.TryGetComponent<MeshFilter>(out var filter)) filter = header.gameObject.AddComponent<MeshFilter>();
                if (!header.TryGetComponent<MeshRenderer>(out var renderer)) renderer = header.gameObject.AddComponent<MeshRenderer>();
                if (!header.TryGetComponent<BoxCollider>(out var collider)) collider = header.gameObject.AddComponent<BoxCollider>();
                filter.sharedMesh = unit; renderer.sharedMaterial = stone; renderer.enabled = true;
                collider.center = Vector3.zero; collider.size = Vector3.one; collider.isTrigger = false; collider.enabled = true;

                // The old closed-state render panel covered all eight metres. End its
                // visible surface at the lintel underside, avoiding duplicate opaque faces
                // behind the permanent header. Preserve its full original blocker collider.
                min = panel.min; max = panel.max; max.y = bridge.min.y;
                Matrix4x4 fit = closure.worldToLocalMatrix * root.transform.localToWorldMatrix
                    * Matrix4x4.TRS((min + max) * .5f, Quaternion.identity, max - min);
                closureFilter.sharedMesh = Saved("SocketClosure_v1_" + MatrixKey(fit), () =>
                {
                    var mesh = Object.Instantiate(unit);
                    mesh.vertices = unit.vertices.Select(fit.MultiplyPoint3x4).ToArray();
                    Matrix4x4 normal = fit.inverse.transpose;
                    mesh.normals = unit.normals.Select(n => normal.MultiplyVector(n).normalized).ToArray();
                    mesh.RecalculateBounds(); mesh.RecalculateTangents(); return mesh;
                });
            }
        }

        private static Bounds MeshBoundsIn(MeshFilter filter, Transform space)
        {
            if (filter == null || filter.sharedMesh == null) throw new InvalidOperationException("Socket has no render mesh");
            Matrix4x4 matrix = space.worldToLocalMatrix * filter.transform.localToWorldMatrix;
            Vector3[] points = filter.sharedMesh.vertices;
            var bounds = new Bounds(matrix.MultiplyPoint3x4(points[0]), Vector3.zero);
            foreach (Vector3 point in points) bounds.Encapsulate(matrix.MultiplyPoint3x4(point));
            return bounds;
        }

        private static Mesh ClosedJoinedBox()
        {
            var vertices = new List<Vector3>(); var normals = new List<Vector3>();
            var uv = new List<Vector2>(); var triangles = new List<int>();
            for (int axis = 0; axis < 3; axis++) for (int sign = -1; sign <= 1; sign += 2)
            {
                int u = (axis + 1) % 3, v = (axis + 2) % 3, start = vertices.Count;
                foreach (var corner in new[] { new Vector2(-.5f,-.5f), new Vector2(.5f,-.5f), new Vector2(.5f,.5f), new Vector2(-.5f,.5f) })
                {
                    Vector3 point = Vector3.zero, normal = Vector3.zero;
                    point[axis] = sign * .5f; point[u] = corner.x; point[v] = corner.y; normal[axis] = sign;
                    vertices.Add(point); normals.Add(normal); uv.Add(corner + Vector2.one * .5f);
                }
                if (sign > 0) triangles.AddRange(new[] { start,start+1,start+2,start,start+2,start+3 });
                else triangles.AddRange(new[] { start,start+2,start+1,start,start+3,start+2 });
            }
            var mesh = new Mesh { name = "Closed masonry box with exact joint planes" };
            mesh.SetVertices(vertices); mesh.SetNormals(normals); mesh.SetUVs(0,uv); mesh.SetTriangles(triangles,0);
            mesh.RecalculateBounds(); mesh.RecalculateTangents(); return mesh;
        }

        private static void ValidateSocketHeaders(GameObject root)
        {
            Transform roof = root.transform.Find("Roof");
            if (roof == null || !roof.TryGetComponent<BoxCollider>(out var ceiling)) return;
            float top = root.transform.InverseTransformPoint(roof.TransformPoint(ceiling.center + Vector3.down * ceiling.size.y * .5f)).y;
            foreach (string side in new[] { "North", "East", "South", "West" })
            {
                Transform header = root.transform.Find("Socket_UpperInfill_" + side), closure = root.transform.Find("Closure_" + side);
                if (header == null || !header.gameObject.activeSelf || !header.TryGetComponent<BoxCollider>(out var collider)
                    || collider.isTrigger || !collider.enabled || header.parent != root.transform
                    || !header.TryGetComponent<MeshRenderer>(out var renderer) || !renderer.enabled)
                    throw new InvalidOperationException("Socket upper wall is not permanent solid masonry: " + side);
                Bounds infill = MeshBoundsIn(header.GetComponent<MeshFilter>(), root.transform);
                Bounds bridge = MeshBoundsIn(root.transform.Find("Arch_Lintel_" + side).GetComponent<MeshFilter>(), root.transform);
                Bounds panel = MeshBoundsIn(closure.GetComponent<MeshFilter>(), root.transform);
                if (Mathf.Abs(infill.min.y - bridge.max.y) > .0001f || Mathf.Abs(infill.max.y - top) > .0001f
                    || Mathf.Abs(panel.max.y - bridge.min.y) > .0001f
                    || Mathf.Abs(infill.min.x - panel.min.x) > .0001f || Mathf.Abs(infill.max.x - panel.max.x) > .0001f
                    || Mathf.Abs(infill.min.z - panel.min.z) > .0001f || Mathf.Abs(infill.max.z - panel.max.z) > .0001f
                    || collider.center != Vector3.zero || collider.size != Vector3.one)
                    throw new InvalidOperationException("Socket has an upper sky gap or overlapping closure presentation: " + side);
            }
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
            var crown=SeatedCrown();
            Vector3[] feet=crown.vertices.Where(v=>v.y<crown.bounds.min.y+.003f).ToArray();
            var lf=feet.Where(v=>v.x<0).ToArray(); var rf=feet.Where(v=>v.x>0).ToArray();
            if(lf.Length==0 || rf.Length==0) throw new InvalidOperationException("Cannot measure crown springline");
            float lx=(lf.Min(v=>v.x)+lf.Max(v=>v.x))*.5f, rx=(rf.Min(v=>v.x)+rf.Max(v=>v.x))*.5f;
            // Nominal shafts identify the intended pair only. Actual rendered capitals
            // determine the bearing positions: imported LOD bounds are not collision tops.
            Vector3[] leftPoints = RenderedPoints(left.shaft, t), rightPoints = RenderedPoints(right.shaft, t);
            Bounds leftTop = TopCourse(leftPoints, t), rightTop = TopCourse(rightPoints, t);
            float sx=(rightTop.center.x-leftTop.center.x)/(rx-lx);
            float contactY = Mathf.Max(leftTop.max.y, rightTop.max.y) + .22f / Mathf.Abs(t.lossyScale.y);
            float rise = crown.bounds.size.y * sx * Mathf.Abs(t.lossyScale.x / t.lossyScale.y);
            Transform roof = room.transform.Find("Roof");
            if (roof != null && roof.TryGetComponent<BoxCollider>(out var ceiling))
            {
                Vector3 underside = roof.TransformPoint(ceiling.center + Vector3.down * ceiling.size.y * .5f);
                float ceilingY = t.InverseTransformPoint(underside).y - .10f / Mathf.Abs(t.lossyScale.y);
                rise = Mathf.Min(rise, ceilingY - contactY);
            }
            if (rise <= .5f / Mathf.Abs(t.lossyScale.y))
                throw new InvalidOperationException("Insufficient roof clearance for the supported stone arch.");
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
                if (rib.name.StartsWith("Arch_Bearing_", StringComparison.Ordinal)) continue;
                if(!rib.TryGetComponent<MeshFilter>(out var mf)) continue;
                mf.sharedMesh=crown;
                Vector3 scale=rib.localScale, position=rib.localPosition;
                scale.x=sx; position.x=leftTop.center.x-lx*sx;
                // Match the source's horizontal proportions instead of flattening every
                // broad arch to a fixed rise. A roof limits only the remaining headroom.
                scale.y=rise/crown.bounds.size.y;
                position.y=contactY-crown.bounds.min.y*scale.y;
                rib.localScale=scale; rib.localPosition=position;
            }
            FitBearing(group, t, "Arch_Bearing_Left", lf, leftTop, contactY);
            FitBearing(group, t, "Arch_Bearing_Right", rf, rightTop, contactY);
            arch.GetComponent<MeshRenderer>().enabled=false;
            ValidateArchBearing(arch);
        }

        private static Mesh SeatedCrown()
        {
            var source = AssetDatabase.LoadAssetAtPath<Mesh>(DownloadedCryptArchitecture.Folder + "/OvergrownArchCrown.asset");
            if (source == null) throw new InvalidOperationException("Missing imported crown");
            return Saved("SeatedCrown_v1_" + RuinsWallProfile.GeometryFingerprint(source), () =>
            {
                var mesh = Object.Instantiate(source);
                Vector3[] points = mesh.vertices;
                float bottom = source.bounds.min.y;
                // Position clustering warped the original cut plane by up to 8cm. Seat
                // the complete lower stone course, including both inner and outer rims.
                for (int i = 0; i < points.Length; i++)
                    if (points[i].y < bottom + .08f) points[i].y = bottom;
                var triangles = new List<int>();
                int[] original = mesh.triangles;
                for (int i = 0; i < original.Length; i += 3)
                    if (Vector3.Cross(points[original[i + 1]] - points[original[i]], points[original[i + 2]] - points[original[i]]).sqrMagnitude > 1e-16f)
                    { triangles.Add(original[i]); triangles.Add(original[i + 1]); triangles.Add(original[i + 2]); }
                mesh.vertices = points; mesh.triangles = triangles.ToArray(); mesh.RecalculateBounds();
                DownloadedCryptArchitecture.RebuildWeatheredNormals(mesh);
                return mesh;
            });
        }

        private static Vector3[] RenderedPoints(Transform source, Transform space)
        {
            var points = new List<Vector3>();
            foreach (var filter in source.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null || !filter.TryGetComponent<MeshRenderer>(out var renderer) || !renderer.enabled) continue;
                Matrix4x4 matrix = space.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                points.AddRange(filter.sharedMesh.vertices.Select(matrix.MultiplyPoint3x4));
            }
            if (points.Count == 0) throw new InvalidOperationException("Supporting column has no visible mesh: " + source.name);
            return points.ToArray();
        }

        private static Bounds TopCourse(Vector3[] points, Transform space)
        {
            float top = points.Max(p => p.y), depth = .10f / Mathf.Abs(space.lossyScale.y);
            var band = points.Where(p => p.y >= top - depth).ToArray();
            var result = new Bounds(band[0], Vector3.zero);
            foreach (var point in band) result.Encapsulate(point);
            return result;
        }

        private static void FitBearing(Transform group, Transform arch, string name, Vector3[] foot, Bounds support, float top)
        {
            var bounds = new Bounds(support.center, Vector3.zero);
            bounds.Encapsulate(support.min); bounds.Encapsulate(support.max);
            foreach (Transform rib in group)
            {
                if (rib.name != "Imported_Arch_Crown") continue;
                foreach (var point in foot) bounds.Encapsulate(arch.InverseTransformPoint(rib.TransformPoint(point)));
            }
            Vector3 margin = new Vector3(.02f / Mathf.Abs(arch.lossyScale.x), 0, .02f / Mathf.Abs(arch.lossyScale.z));
            Vector3 min = bounds.min - margin, max = bounds.max + margin;
            min.y = support.max.y - .04f / Mathf.Abs(arch.lossyScale.y); max.y = top;
            Transform cap = group.Find(name);
            if (cap == null) { cap = new GameObject(name).transform; cap.SetParent(group, false); }
            cap.gameObject.layer = arch.gameObject.layer;
            cap.localPosition = (min + max) * .5f; cap.localRotation = Quaternion.identity; cap.localScale = max - min;
            Vector3 physical = Vector3.Scale(max - min, Abs(arch.lossyScale));
            Vector3 lowerMin = new Vector3(
                (support.min.x - margin.x * .5f - cap.localPosition.x) / cap.localScale.x, -.5f,
                (support.min.z - margin.z * .5f - cap.localPosition.z) / cap.localScale.z);
            Vector3 lowerMax = new Vector3(
                (support.max.x + margin.x * .5f - cap.localPosition.x) / cap.localScale.x, -.5f,
                (support.max.z + margin.z * .5f - cap.localPosition.z) / cap.localScale.z);
            if (!cap.TryGetComponent<MeshFilter>(out var filter)) filter = cap.gameObject.AddComponent<MeshFilter>();
            if (!cap.TryGetComponent<MeshRenderer>(out var renderer)) renderer = cap.gameObject.AddComponent<MeshRenderer>();
            string key = "ArchCapital_v2_" + Key(physical) + "_" + Key(lowerMin) + "_" + Key(lowerMax);
            filter.sharedMesh = Saved(key, () => TaperedCapital(physical, lowerMin, lowerMax));
            renderer.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Generated/DungeonSurfaces/World_Stone_Wall.mat");
        }

        private static Mesh TaperedCapital(Vector3 physical, Vector3 lowerMin, Vector3 lowerMax)
        {
            // A solid spreading capital, not a thin shelf: the lower ring seats onto
            // the actual column, the shoulder carries the crown, and a 10mm bevel
            // softens the upper rim without opening any face or touching collision.
            var vertices = new List<Vector3>(); var triangles = new List<int>(); var uv = new List<Vector2>();
            Vector3[] Ring(float left, float right, float back, float front, float y) => new[]
            {
                new Vector3(left,y,back), new Vector3(left,y,front),
                new Vector3(right,y,front), new Vector3(right,y,back)
            };
            Vector3[][] rings =
            {
                Ring(lowerMin.x,lowerMax.x,lowerMin.z,lowerMax.z,-.5f),
                Ring(-.5f,.5f,-.5f,.5f,.3f),
                Ring(-.5f+.01f/physical.x,.5f-.01f/physical.x,-.5f+.01f/physical.z,.5f-.01f/physical.z,.5f)
            };
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                int first = vertices.Count; vertices.AddRange(new[] { a, b, c, d });
                float u = Vector3.Scale(b-a,physical).magnitude, v = Vector3.Scale(d-a,physical).magnitude;
                uv.AddRange(new[] { Vector2.zero, new Vector2(u,0), new Vector2(u,v), new Vector2(0,v) });
                triangles.AddRange(new[] { first,first+1,first+2,first,first+2,first+3 });
            }
            for (int ring = 0; ring < rings.Length - 1; ring++)
                for (int i = 0; i < 4; i++)
                    Quad(rings[ring][i],rings[ring][(i+1)%4],rings[ring+1][(i+1)%4],rings[ring+1][i]);
            Quad(rings[0][0],rings[0][3],rings[0][2],rings[0][1]);
            Quad(rings[2][0],rings[2][1],rings[2][2],rings[2][3]);
            var mesh = new Mesh { name = "Joined tapered stone capital" };
            mesh.SetVertices(vertices); mesh.SetTriangles(triangles,0); mesh.SetUVs(0,uv);
            mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds();
            return mesh;
        }

        private static void ValidateArchBearing(MeshFilter arch)
        {
            Transform group = arch.transform.Find(Visual);
            if (group == null) return; // The two intentionally unsupported legacy ornaments are omitted.
            foreach (Transform rib in group)
            {
                if (rib.name != "Imported_Arch_Crown" || !rib.TryGetComponent<MeshFilter>(out var filter)) continue;
                float bottom = filter.sharedMesh.bounds.min.y;
                foreach (var point in filter.sharedMesh.vertices)
                {
                    if (point.y > bottom + .0001f) continue;
                    Transform cap = group.Find(point.x < 0 ? "Arch_Bearing_Left" : "Arch_Bearing_Right");
                    if (cap == null) throw new InvalidOperationException("Arch has no physical bearing cap");
                    float capitalHeight = cap.GetComponent<MeshFilter>().sharedMesh.bounds.size.y * Mathf.Abs(cap.lossyScale.y);
                    if (capitalHeight < .24f || capitalHeight > .32f)
                        throw new InvalidOperationException("Arch capital is not a solid stone bearing course.");
                    Vector3 local = cap.InverseTransformPoint(rib.TransformPoint(point));
                    if (Mathf.Abs(local.y - .5f) > .001f || Mathf.Abs(local.x) > .499f || Mathf.Abs(local.z) > .499f)
                        throw new InvalidOperationException("Arch foot does not meet its bearing surface: " + arch.name);
                }
            }
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
            ValidateSocketHeaders(root);
            DownloadedCryptArchitecture.ValidatePropAdapters(root);
            foreach (var arch in root.GetComponentsInChildren<MeshFilter>(true))
                if (arch.name == "Stone_Arch" || arch.name == "Reused_Stone_Arch") ValidateArchBearing(arch);
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

        internal static Mesh CreateRoundedBox(Vector3 size)
        {
            if (Mathf.Min(size.x, Mathf.Min(size.y, size.z)) <= 0f)
                throw new ArgumentOutOfRangeException(nameof(size));
            return RoundedBox(size, Vector3.zero, Vector3.one);
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
