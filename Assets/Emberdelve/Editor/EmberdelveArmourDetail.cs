using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.EditorTools
{
    // Original metalwork, fitted to the existing bone-local shells; no rig/collider changes.
    public static class EmberdelveArmourDetail
    {
        const string Art = "Assets/Emberdelve/Art/Models/GrimPlayers/";
        const string Prefab = "Assets/Emberdelve/Resources/Emberdelve/PlayerVisuals/Warrior.prefab";
        const int Sides = 16, Stride = Sides + 1;

        public static void UpdateSavedCuirass()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Use the batch test mirror");
            var root = PrefabUtility.LoadPrefabContents(Prefab);
            try
            {
                RefineCuirass(root);
                var source = root.GetComponentsInChildren<MeshFilter>(true).Single(m => m.name == "Cuirass");
                var points = source.sharedMesh.vertices.Select(source.transform.TransformPoint).ToArray();
                var edges = new Geometry(source.transform);
                var rivets = new Geometry(source.transform);
                Ring(points, 0, 1, edges, rivets, .012f);
                Ring(points, 3, 2, edges, rivets, .012f);
                Save(source.transform, "Cuirass_PlateEdges", edges, AssetDatabase.LoadAssetAtPath<Material>(Art + "WornSteel.mat"));
                Save(source.transform, "Cuirass_Rivets", rivets, AssetDatabase.LoadAssetAtPath<Material>(Art + "OldBrass.mat"));
                PrefabUtility.SaveAsPrefabAsset(root, Prefab);
                AssetDatabase.SaveAssets();
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            Debug.Log("[ArmourDetail] Formed cuirass and fitted chest cloth authored");
        }

        public static void UpdateSavedShoulders()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Use the batch test mirror");
            var root = PrefabUtility.LoadPrefabContents(Prefab);
            try
            {
                RefineShoulder(root, "ShoulderL");
                RefineShoulder(root, "ShoulderR");
                PrefabUtility.SaveAsPrefabAsset(root, Prefab);
                AssetDatabase.SaveAssets();
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            Debug.Log("[ArmourDetail] Fitted shoulder plates authored");
        }

        public static void UpdateSavedWarrior()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Use the batch test mirror");
            var root = PrefabUtility.LoadPrefabContents(Prefab);
            try { Apply(root); PrefabUtility.SaveAsPrefabAsset(root, Prefab); AssetDatabase.SaveAssets(); }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            Debug.Log("[ArmourDetail] Warrior helmet crown, plate rims and rivets authored");
        }

        public static void Apply(GameObject root)
        {
            RefineCuirass(root);
            RefineShoulder(root, "ShoulderL");
            RefineShoulder(root, "ShoulderR");
            foreach (string part in new[] { "GreatHelm", "Cuirass" })
            {
                var source = root.GetComponentsInChildren<MeshFilter>(true).Single(m => m.name == part);
                int rows = part == "GreatHelm" ? 5 : 4;
                var vertices = source.sharedMesh.vertices;
                if (vertices.Length != rows * Stride) throw new InvalidOperationException("Unexpected armour topology: " + part);
                var points = vertices.Select(source.transform.TransformPoint).ToArray();
                var edges = new Geometry(source.transform);
                var rivets = new Geometry(source.transform);
                Ring(points, 0, 1, edges, rivets, .012f);
                Ring(points, rows - 1, rows - 2, edges, rivets, .012f);
                if (part == "GreatHelm")
                {
                    // Closing the original open crown follows the actual shell, even in a posed prefab.
                    Vector3 centre = Centre(points, rows - 1);
                    Vector3 up = (centre - Centre(points, rows - 2)).normalized;
                    centre += up * .006f;
                    for (int i = 0; i < Sides; i++)
                        edges.Triangle(points[(rows-1)*Stride+i], points[(rows-1)*Stride+i+1], centre, up);
                    Ring(points, 2, 1, edges, rivets, .008f);
                }
                Save(source.transform, part + "_PlateEdges", edges, AssetDatabase.LoadAssetAtPath<Material>(Art + "WornSteel.mat"));
                Save(source.transform, part + "_Rivets", rivets, AssetDatabase.LoadAssetAtPath<Material>(Art + "OldBrass.mat"));
            }
        }

        static void RefineCuirass(GameObject root)
        {
            var filter = root.GetComponentsInChildren<MeshFilter>(true).Single(m => m.name == "Cuirass");
            var original = AssetDatabase.LoadAssetAtPath<Mesh>(Art + "Warrior_Cuirass.asset");
            if (original == null || original.vertexCount != 4 * Stride)
                throw new InvalidOperationException("Unexpected cuirass source topology");
            // Always start from the unmodified shell: authoring twice must not shrink it twice.
            var points = original.vertices.Select(filter.transform.TransformPoint).ToArray();
            var upperOriginal = points.Skip(3 * Stride).ToArray();
            float[] width = { 1f, .96f, .90f, 1f };
            float[] ridge = { .002f, .014f, .014f, .002f };
            for (int row = 0; row < 4; row++)
            {
                Vector3 centre = Centre(points, row);
                for (int i = 0; i <= Sides; i++)
                {
                    float angle = i * Mathf.PI * 2f / Sides;
                    Vector3 p = points[row * Stride + i];
                    p.x = centre.x + (p.x - centre.x) * width[row];
                    // A shallow central breast ridge catches light without an inflated barrel profile.
                    p.z += ridge[row] * Mathf.Pow(Mathf.Max(0f, Mathf.Cos(angle)), 6f);
                    if (row == 3)
                        p.y -= .035f * Mathf.Pow(Mathf.Max(0f, Mathf.Cos(angle)), 4f)
                             + .065f * Mathf.Pow(Mathf.Abs(Mathf.Sin(angle)), 4f);
                    points[row * Stride + i] = p;
                }
            }
            var mesh = Object.Instantiate(original);
            mesh.name = "Warrior_Cuirass_Formed";
            mesh.vertices = points.Select(filter.transform.InverseTransformPoint).ToArray();
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            filter.sharedMesh = SaveDerivedMesh(mesh);

            // The lowered metal edge reveals the padded garment, not bare shoulders.
            var collar = new Geometry(filter.transform, true);
            Vector3 upperCentre = Vector3.zero;
            for (int i = 0; i < Sides; i++) upperCentre += upperOriginal[i] / Sides;
            for (int i = 0; i < Sides; i++)
            {
                Vector3 na = upperOriginal[i] - upperCentre; na.y = 0; na.Normalize();
                Vector3 nb = upperOriginal[i + 1] - upperCentre; nb.y = 0; nb.Normalize();
                collar.Quad(upperOriginal[i] - na * .001f, upperOriginal[i + 1] - nb * .001f,
                    points[3 * Stride + i + 1] - nb * .002f,
                    points[3 * Stride + i] - na * .002f, (na + nb).normalized);
            }
            Save(filter.transform, "Cuirass_PaddedCollar", collar,
                AssetDatabase.LoadAssetAtPath<Material>(EmberdelveFirstPersonArms.SleeveMaterialPath));

            // Fit the existing heraldic strip to the actual polygon surface instead of floating
            // a flat box in front of the breast. This is cosmetic and stays on the chest bone.
            var cloth = root.GetComponentsInChildren<MeshFilter>(true).Single(m => m.name == "TabardChest");
            var clothSource = AssetDatabase.LoadAssetAtPath<Mesh>(Art + "Warrior_TabardChest.asset");
            if (clothSource == null) throw new InvalidOperationException("Missing original chest cloth");
            var clothPoints = clothSource.vertices.Select(cloth.transform.TransformPoint).ToArray();
            float bottom = clothPoints.Min(p => p.y);
            float top = Mathf.Min(clothPoints.Max(p => p.y), points[3 * Stride].y - .015f);
            var geometry = new Geometry(cloth.transform, true);
            for (int band = 0; band < 4; band++)
            {
                float y0 = Mathf.Lerp(bottom, top, band / 4f), y1 = Mathf.Lerp(bottom, top, (band + 1) / 4f);
                // Separate centre column follows the formed ridge, retaining the cloth's red accent.
                for (int side = 0; side < 2; side++)
                {
                    float x0 = (side - 1) * .055f, x1 = side * .055f;
                    Vector3 a = ClothPoint(points, x0, y0), b = ClothPoint(points, x1, y0);
                    Vector3 c = ClothPoint(points, x1, y1), d = ClothPoint(points, x0, y1);
                    geometry.Quad(a, b, c, d, Vector3.forward);
                    geometry.Quad(a, d, c, b, Vector3.back);
                }
            }
            cloth.sharedMesh = SaveDerivedMesh(geometry.Mesh("Warrior_TabardChest_Fitted"));
        }

        static Vector3 ClothPoint(Vector3[] shell, float x, float y)
        {
            // Intersect the front shell's existing triangles with a vertical line at this x/y.
            float best = float.NegativeInfinity;
            for (int row = 0; row < 3; row++)
                for (int i = 0; i < Sides; i++)
                {
                    int a = row * Stride + i, b = a + Stride;
                    IntersectFront(shell[a], shell[a + 1], shell[b], x, y, ref best);
                    IntersectFront(shell[a + 1], shell[b + 1], shell[b], x, y, ref best);
                }
            if (float.IsNegativeInfinity(best)) throw new InvalidOperationException("Chest cloth lies outside cuirass");
            return new Vector3(x, y, best + .003f);
        }

        static void IntersectFront(Vector3 a, Vector3 b, Vector3 c, float x, float y, ref float best)
        {
            float determinant = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
            if (Mathf.Abs(determinant) < .000001f) return;
            float u = ((b.y - c.y) * (x - c.x) + (c.x - b.x) * (y - c.y)) / determinant;
            float v = ((c.y - a.y) * (x - c.x) + (a.x - c.x) * (y - c.y)) / determinant;
            float w = 1f - u - v;
            if (u >= -.0001f && v >= -.0001f && w >= -.0001f)
                best = Mathf.Max(best, u * a.z + v * b.z + w * c.z);
        }

        static Mesh SaveDerivedMesh(Mesh mesh)
        {
            string path = Art + mesh.name + ".asset";
            var saved = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (saved == null) { AssetDatabase.CreateAsset(mesh, path); return mesh; }
            EditorUtility.CopySerialized(mesh, saved); Object.DestroyImmediate(mesh); EditorUtility.SetDirty(saved);
            return saved;
        }

        static void RefineShoulder(GameObject root, string part)
        {
            var filter=root.GetComponentsInChildren<MeshFilter>(true).Single(m=>m.name==part);
            // Always derive from the preserved original, not the previous refined result.
            var original=AssetDatabase.LoadAssetAtPath<Mesh>(Art+"Warrior_"+part+".asset");
            if(original==null || original.vertexCount!=52) throw new InvalidOperationException("Unexpected shoulder source: "+part);
            var p=original.vertices.Select(filter.transform.TransformPoint).ToArray();
            const int sides=12,stride=13;
            var centre=new Vector3[4];
            for(int row=0;row<4;row++)
            {
                for(int i=0;i<sides;i++) centre[row]+=p[row*stride+i];
                centre[row]/=sides;
            }
            // Keep the shoulder socket, taper the distal opening around the upper arm.
            // Source geometry remains untouched, so repeated authoring cannot compound this fit.
            float[] fit = { 1f, .90f, .82f, .72f };
            for (int row=0;row<4;row++)
                for (int i=0;i<stride;i++)
                    p[row*stride+i]=centre[row]+(p[row*stride+i]-centre[row])*fit[row];
            Vector3 axis=(centre[3]-centre[0]).normalized;
            var geometry=new Geometry(filter.transform,true);
            for(int band=0;band<3;band++)
            {
                float raised=(2-band)*.002f;
                for(int i=0;i<sides;i++)
                {
                    Vector3 a=p[band*stride+i], b=p[band*stride+i+1];
                    Vector3 c=p[(band+1)*stride+i+1], d=p[(band+1)*stride+i];
                    Vector3 na=(a-centre[band]).normalized,nb=(b-centre[band]).normalized;
                    Vector3 nc=(c-centre[band+1]).normalized,nd=(d-centre[band+1]).normalized;
                    a+=na*raised;b+=nb*raised;c+=nc*raised;d+=nd*raised;
                    Vector3 ai=a-na*.003f,bi=b-nb*.003f,ci=c-nc*.003f,di=d-nd*.003f;
                    Vector3 outward=(na+nb+nc+nd).normalized;
                    geometry.Quad(a,b,c,d,outward);
                    geometry.Quad(ai,di,ci,bi,-outward);
                    geometry.Quad(ai,bi,b,a,-axis);
                    geometry.Quad(d,c,ci,di,axis);
                }
            }
            var mesh=geometry.Mesh("Warrior_"+part+"_Plated");
            string path=Art+mesh.name+".asset";
            var saved=AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if(saved==null) { AssetDatabase.CreateAsset(mesh,path);saved=mesh; }
            else { EditorUtility.CopySerialized(mesh,saved);Object.DestroyImmediate(mesh);EditorUtility.SetDirty(saved); }
            filter.sharedMesh=saved;
        }

        static Vector3 Centre(Vector3[] points, int row)
        {
            Vector3 value = Vector3.zero;
            for (int i = 0; i < Sides; i++) value += points[row * Stride + i];
            return value / Sides;
        }

        static void Ring(Vector3[] points, int row, int neighbour, Geometry edges, Geometry rivets, float width)
        {
            Vector3 centre = Centre(points, row);
            for (int i = 0; i < Sides; i++)
            {
                Vector3 a = points[row*Stride+i], b = points[row*Stride+i+1];
                Vector3 na = (a-centre).normalized, nb = (b-centre).normalized;
                Vector3 alongA = (points[neighbour*Stride+i]-a).normalized;
                Vector3 alongB = (points[neighbour*Stride+i+1]-b).normalized;
                // Two faces across the bead give a narrow highlight, not a painted line.
                Vector3 a0=a+na*.001f, b0=b+nb*.001f;
                Vector3 am=a+alongA*(width*.45f)+na*.004f, bm=b+alongB*(width*.45f)+nb*.004f;
                Vector3 a1=a+alongA*width+na*.001f, b1=b+alongB*width+nb*.001f;
                edges.Quad(a0,b0,bm,am,(na+nb).normalized);
                edges.Quad(am,bm,b1,a1,(na+nb).normalized);
                if (i % 2 == 0)
                    Rivet(a+alongA*(width+.008f)+na*.002f, na, alongA, rivets);
            }
        }

        static void Rivet(Vector3 centre, Vector3 normal, Vector3 tangent, Geometry target)
        {
            Vector3 x = Vector3.Cross(normal,tangent).normalized;
            Vector3 y = Vector3.Cross(normal,x).normalized;
            for (int i=0;i<8;i++)
            {
                float a=i*Mathf.PI/4f,b=(i+1)*Mathf.PI/4f;
                Vector3 p=centre+(x*Mathf.Cos(a)+y*Mathf.Sin(a))*.0035f;
                Vector3 q=centre+(x*Mathf.Cos(b)+y*Mathf.Sin(b))*.0035f;
                target.Triangle(p,q,centre+normal*.0025f,normal);
            }
        }

        static void Save(Transform source, string name, Geometry geometry, Material material)
        {
            if (material == null) throw new InvalidOperationException("Missing authored armour material");
            var old = source.Find(name);
            var mesh = geometry.Mesh(name);
            string path=Art+"Warrior_"+name+".asset";
            var saved=AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if(saved==null) { AssetDatabase.CreateAsset(mesh,path); saved=mesh; }
            else { EditorUtility.CopySerialized(mesh,saved); Object.DestroyImmediate(mesh); EditorUtility.SetDirty(saved); }
            var go=old != null ? old.gameObject : new GameObject(name,typeof(MeshFilter),typeof(MeshRenderer));
            if (old == null) go.transform.SetParent(source,false);
            go.layer=source.gameObject.layer;
            go.GetComponent<MeshFilter>().sharedMesh=saved;
            go.GetComponent<MeshRenderer>().sharedMaterial=material;
        }

        sealed class Geometry
        {
            readonly Transform _space;
            readonly List<Vector3> _vertices=new List<Vector3>();
            readonly List<int> _indices=new List<int>();
            readonly List<Vector2> _uv;
            public Geometry(Transform space, bool textured=false) { _space=space; if(textured) _uv=new List<Vector2>(); }
            public void Triangle(Vector3 a,Vector3 b,Vector3 c,Vector3 outward)
            {
                if(Vector3.Dot(Vector3.Cross(b-a,c-a),outward)<0f) { var swap=b;b=c;c=swap; }
                int start=_vertices.Count;
                _vertices.Add(_space.InverseTransformPoint(a)); _vertices.Add(_space.InverseTransformPoint(b)); _vertices.Add(_space.InverseTransformPoint(c));
                if(_uv!=null)
                {
                    _uv.Add(new Vector2((a.x+a.z)*5f,a.y*5f));
                    _uv.Add(new Vector2((b.x+b.z)*5f,b.y*5f));
                    _uv.Add(new Vector2((c.x+c.z)*5f,c.y*5f));
                }
                _indices.Add(start);_indices.Add(start+1);_indices.Add(start+2);
            }
            public void Quad(Vector3 a,Vector3 b,Vector3 c,Vector3 d,Vector3 outward)
            { Triangle(a,b,c,outward);Triangle(a,c,d,outward); }
            public Mesh Mesh(string name)
            {
                var result=new Mesh { name=name }; result.SetVertices(_vertices);result.SetTriangles(_indices,0);
                if(_uv!=null) result.SetUVs(0,_uv);
                result.RecalculateNormals();result.RecalculateBounds();return result;
            }
        }
    }
}
