using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Grim placeholder dungeon props composed from Unity primitives and saved as prefabs (PH_*.prefab). Every prefab is a clean drop-in for a
    /// generated model: root pivot at floor centre (bottom of the prop at y=0, footprint centred on x/z), front towards +Z, metres. Wall props
    /// ("_Wall") have their back face at z=0 and extend towards +Z; their bottom sits at the height listed in <see cref="WallBottoms"/>.
    /// No colliders, no scripts; props that carry light have an empty "LightSocket" child where the lighting system adds light and flame.
    /// </summary>
    public static class DungeonPrimitiveProps
    {
        public const string Folder = "Assets/Emberdelve/Art/Generated/PrimitiveProps";

        public static readonly string[] Ids =
        {
            "PH_Sarcophagus", "PH_SarcophagusBroken", "PH_CoffinWood", "PH_CoffinOpen", "PH_BonePile", "PH_SkullHeap_Small",
            "PH_OssuaryShelf_Wall", "PH_IronBrazier", "PH_CandleCluster", "PH_Candelabrum", "PH_Altar", "PH_Pew",
            "PH_StoneBench", "PH_WeaponRack", "PH_Cage", "PH_Chains_Wall", "PH_Banner_Wall", "PH_BrokenPillar",
            "PH_RubblePile", "PH_Cot", "PH_Cauldron", "PH_Lectern", "PH_BookPile", "PH_Buttress_Wall",
        };

        private static readonly HashSet<string> LightIds = new HashSet<string> { "PH_IronBrazier", "PH_CandleCluster", "PH_Candelabrum", "PH_Altar" };

        private static readonly Dictionary<string, float> WallBottoms = new Dictionary<string, float>
        {
            { "PH_OssuaryShelf_Wall", 0f }, { "PH_Chains_Wall", 1.4f }, { "PH_Banner_Wall", 1.2f }, { "PH_Buttress_Wall", 0f },
        };

        private const string LightSocketName = "LightSocket";
        private const string ReviewDir = "Logs/review/primitive-props";

        public static GameObject Load(string id)
        {
            if (string.IsNullOrEmpty(id) || !id.StartsWith("PH_", StringComparison.Ordinal)) return null;
            string path = PathFor(id);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                BuildAll();
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            }
            return prefab;
        }

        private static string PathFor(string id) { return Folder + "/" + id + ".prefab"; }

        // ------------------------------------------------------------------ build

        [MenuItem("Emberdelve/Visual/Build Primitive Placeholder Props")]
        public static void BuildAll()
        {
            EnsureFolder(Folder);
            EnsureFolder(Folder + "/Materials");
            Mats mats = LoadMats();

            for (int i = 0; i < Ids.Length; i++)
            {
                string id = Ids[i];
                GameObject root = new GameObject(id);
                try
                {
                    Ctx c = new Ctx(root.transform, new System.Random(1000 + 37 * i), mats);
                    BuildProp(id, c);
                    bool wall = id.EndsWith("_Wall", StringComparison.Ordinal);
                    float bottom;
                    if (!WallBottoms.TryGetValue(id, out bottom)) bottom = 0f;
                    Normalize(root.transform, wall, bottom);
                    FinalizeRenderers(root);
                    bool ok;
                    PrefabUtility.SaveAsPrefabAsset(root, PathFor(id), out ok);
                    if (!ok) throw new InvalidOperationException("[PrimitiveProps] failed to save " + id);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            ValidateAll();
            Debug.Log("[PrimitiveProps] built=" + Ids.Length);
        }

        public static void ValidateAll()
        {
            for (int i = 0; i < Ids.Length; i++)
            {
                string id = Ids[i];
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PathFor(id));
                if (prefab == null) throw new InvalidOperationException("[PrimitiveProps] missing prefab " + id);
                if (prefab.GetComponentsInChildren<Collider>(true).Length > 0) throw new InvalidOperationException("[PrimitiveProps] collider found in " + id);
                if (prefab.GetComponentsInChildren<MonoBehaviour>(true).Length > 0) throw new InvalidOperationException("[PrimitiveProps] MonoBehaviour found in " + id);
                if (prefab.GetComponentsInChildren<Light>(true).Length > 0) throw new InvalidOperationException("[PrimitiveProps] Light found in " + id);

                int sockets = 0;
                Transform[] all = prefab.GetComponentsInChildren<Transform>(true);
                for (int t = 0; t < all.Length; t++) if (all[t].name == LightSocketName) sockets++;
                int expected = LightIds.Contains(id) ? 1 : 0;
                if (sockets != expected) throw new InvalidOperationException("[PrimitiveProps] " + id + " has " + sockets + " LightSocket(s), expected " + expected);

                Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0) throw new InvalidOperationException("[PrimitiveProps] no renderers in " + id);
                for (int r = 0; r < renderers.Length; r++)
                {
                    if (renderers[r].shadowCastingMode != ShadowCastingMode.On) throw new InvalidOperationException("[PrimitiveProps] shadow casting off in " + id);
                }

                Bounds b = CalcBounds(prefab.transform);
                bool wall = id.EndsWith("_Wall", StringComparison.Ordinal);
                float bottom;
                if (!WallBottoms.TryGetValue(id, out bottom)) bottom = 0f;
                if (Mathf.Abs(b.min.y - bottom) > 0.01f) throw new InvalidOperationException("[PrimitiveProps] " + id + " bottom y=" + b.min.y.ToString("F3") + ", expected " + bottom.ToString("F2"));
                if (Mathf.Abs(b.center.x) >= 0.05f) throw new InvalidOperationException("[PrimitiveProps] " + id + " not centred on x (" + b.center.x.ToString("F3") + ")");
                if (wall)
                {
                    if (Mathf.Abs(b.min.z) > 0.01f) throw new InvalidOperationException("[PrimitiveProps] " + id + " back face not at z=0 (" + b.min.z.ToString("F3") + ")");
                }
                else if (Mathf.Abs(b.center.z) >= 0.05f)
                {
                    throw new InvalidOperationException("[PrimitiveProps] " + id + " not centred on z (" + b.center.z.ToString("F3") + ")");
                }
            }
        }

        private static void FinalizeRenderers(GameObject root)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                renderers[i].shadowCastingMode = ShadowCastingMode.On;
                renderers[i].receiveShadows = true;
            }
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++) GameObjectUtility.SetStaticEditorFlags(all[i].gameObject, 0);
        }

        private static void Normalize(Transform root, bool wall, float bottom)
        {
            Bounds b = CalcBounds(root);
            Vector3 offset = wall
                ? new Vector3(-b.center.x, bottom - b.min.y, -b.min.z)
                : new Vector3(-b.center.x, -b.min.y, -b.center.z);
            for (int i = 0; i < root.childCount; i++) root.GetChild(i).localPosition += offset;
        }

        private static readonly Dictionary<Mesh, Vector3[]> VertexCache = new Dictionary<Mesh, Vector3[]>();

        /// <summary>Exact bounds of all meshes in the root's local space (uses the mesh vertices, so rotated round primitives are not over-estimated).</summary>
        private static Bounds CalcBounds(Transform root)
        {
            MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
            Matrix4x4 inv = root.worldToLocalMatrix;
            bool has = false;
            Bounds bounds = new Bounds();
            for (int i = 0; i < filters.Length; i++)
            {
                Mesh mesh = filters[i].sharedMesh;
                if (mesh == null) continue;
                Vector3[] verts;
                if (!VertexCache.TryGetValue(mesh, out verts)) { verts = mesh.vertices; VertexCache[mesh] = verts; }
                Matrix4x4 m = inv * filters[i].transform.localToWorldMatrix;
                for (int v = 0; v < verts.Length; v++)
                {
                    Vector3 p = m.MultiplyPoint3x4(verts[v]);
                    if (!has) { bounds = new Bounds(p, Vector3.zero); has = true; }
                    else bounds.Encapsulate(p);
                }
            }
            return bounds;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int slash = path.LastIndexOf('/');
            string parent = path.Substring(0, slash);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, path.Substring(slash + 1));
        }

        // ------------------------------------------------------------------ materials

        private sealed class Mats
        {
            public Material StoneWall, StoneFloor, StoneCeiling, Wood, Iron, Clay;
            public Material Bone, Wax, Cloth, ClothDark, Rust, Coal, Straw;
        }

        private static Material Existing(string path)
        {
            Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) throw new InvalidOperationException("[PrimitiveProps] required material missing: " + path);
            return m;
        }

        private static Mats LoadMats()
        {
            Mats m = new Mats();
            m.StoneWall = Existing("Assets/Emberdelve/Art/Generated/DungeonSurfaces/World_Stone_Wall.mat");
            m.StoneFloor = Existing("Assets/Emberdelve/Art/Generated/DungeonSurfaces/World_Stone_Floor.mat");
            m.StoneCeiling = Existing("Assets/Emberdelve/Art/Generated/DungeonSurfaces/World_Stone_Ceiling.mat");
            m.Wood = Existing("Assets/Emberdelve/Art/Materials/Wood_Planks.mat");
            m.Iron = Existing("Assets/Emberdelve/Art/Materials/Iron_Dark.mat");
            m.Clay = Existing("Assets/Emberdelve/Art/Generated/RoomProps/WeatheredClay.mat");
            m.Bone = Own("PH_Bone", new Color(0.62f, 0.57f, 0.47f), 0.25f, 0f, null);
            m.Wax = Own("PH_Wax", new Color(0.78f, 0.72f, 0.58f), 0.45f, 0f, null);
            m.Cloth = Own("PH_Cloth", new Color(0.22f, 0.07f, 0.06f), 0.05f, 0f, null);
            m.ClothDark = Own("PH_ClothDark", new Color(0.09f, 0.08f, 0.07f), 0.05f, 0f, null);
            m.Rust = Own("PH_Rust", new Color(0.25f, 0.14f, 0.09f), 0.2f, 0.4f, null);
            m.Coal = Own("PH_Coal", new Color(0.05f, 0.04f, 0.035f), 0.1f, 0f, new Color(0.35f, 0.09f, 0.02f) * 0.8f);
            m.Straw = Own("PH_Straw", new Color(0.42f, 0.36f, 0.22f), 0.05f, 0f, null);
            return m;
        }

        /// <summary>Loads the project-owned placeholder material; created once (existing ones keep manual tweaks).</summary>
        private static Material Own(string name, Color color, float smoothness, float metallic, Color? emission)
        {
            string path = Folder + "/Materials/" + name + ".mat";
            Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) throw new InvalidOperationException("[PrimitiveProps] URP Lit shader not found");
            m = new Material(shader);
            m.name = name;
            m.SetColor("_BaseColor", color);
            m.SetColor("_Color", color);
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_Metallic", metallic);
            if (emission.HasValue)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", emission.Value);
            }
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        // ------------------------------------------------------------------ construction helpers

        private static Vector3 V(float x, float y, float z) { return new Vector3(x, y, z); }

        private sealed class Ctx
        {
            public readonly Transform Root;
            public readonly System.Random Rng;
            public readonly Mats M;

            public Ctx(Transform root, System.Random rng, Mats mats) { Root = root; Rng = rng; M = mats; }

            public float R(float a, float b) { return a + (float)Rng.NextDouble() * (b - a); }
            public int RI(int a, int bExclusive) { return Rng.Next(a, bExclusive); }

            private GameObject Prim(PrimitiveType type, string name, Transform parent, Vector3 pos, Quaternion rot, Vector3 scale, Material mat)
            {
                GameObject go = GameObject.CreatePrimitive(type);
                go.name = name;
                Collider col = go.GetComponent<Collider>();
                if (col != null) UnityEngine.Object.DestroyImmediate(col);
                go.transform.SetParent(parent != null ? parent : Root, false);
                go.transform.localPosition = pos;
                go.transform.localRotation = rot;
                go.transform.localScale = scale;
                go.GetComponent<Renderer>().sharedMaterial = mat;
                return go;
            }

            public GameObject Box(Vector3 pos, Vector3 size, Material m, Vector3 euler = default(Vector3), Transform p = null)
            {
                return Prim(PrimitiveType.Cube, "Box", p, pos, Quaternion.Euler(euler), size, m);
            }

            /// <summary>Cylinder of diameter d and height h centred at pos.</summary>
            public GameObject Cyl(Vector3 pos, float d, float h, Material m, Vector3 euler = default(Vector3), Transform p = null)
            {
                return Prim(PrimitiveType.Cylinder, "Cyl", p, pos, Quaternion.Euler(euler), V(d, h * 0.5f, d), m);
            }

            /// <summary>Capsule of diameter d and total length len (caps included) centred at pos.</summary>
            public GameObject Cap(Vector3 pos, float d, float len, Material m, Vector3 euler = default(Vector3), Transform p = null)
            {
                return Prim(PrimitiveType.Capsule, "Cap", p, pos, Quaternion.Euler(euler), V(d, len * 0.5f, d), m);
            }

            public GameObject Sph(Vector3 pos, Vector3 size, Material m, Vector3 euler = default(Vector3), Transform p = null)
            {
                return Prim(PrimitiveType.Sphere, "Sph", p, pos, Quaternion.Euler(euler), size, m);
            }

            /// <summary>Square beam / round bar / capsule bar between two points (thickness t).</summary>
            public GameObject Between(PrimitiveType type, Vector3 a, Vector3 b, float t, Material m, Transform p = null)
            {
                Vector3 d = b - a;
                float len = d.magnitude;
                Vector3 scale = type == PrimitiveType.Cube ? V(t, len, t) : V(t, len * 0.5f, t);
                return Prim(type, "Bar", p, (a + b) * 0.5f, Quaternion.FromToRotation(Vector3.up, d / len), scale, m);
            }

            /// <summary>Horizontal plank in the xz plane from a to b (only x/z used), with height h and thickness t, bottom at y0.</summary>
            public GameObject Plank(Vector3 a, Vector3 b, float y0, float h, float t, Material m, Transform p = null)
            {
                float dx = b.x - a.x, dz = b.z - a.z;
                float len = Mathf.Sqrt(dx * dx + dz * dz);
                float yaw = -Mathf.Atan2(dz, dx) * Mathf.Rad2Deg;
                return Prim(PrimitiveType.Cube, "Plank", p, V((a.x + b.x) * 0.5f, y0 + h * 0.5f, (a.z + b.z) * 0.5f), Quaternion.Euler(0f, yaw, 0f), V(len, h, t), m);
            }

            public Transform Group(string name, Vector3 pos, Vector3 euler, Transform p = null)
            {
                GameObject go = new GameObject(name);
                go.transform.SetParent(p != null ? p : Root, false);
                go.transform.localPosition = pos;
                go.transform.localRotation = Quaternion.Euler(euler);
                return go.transform;
            }

            public void Socket(Vector3 pos)
            {
                Group(LightSocketName, pos, Vector3.zero);
            }

            /// <summary>Skull sitting on its base at pos (about 0.2 m long at s=1).</summary>
            public Transform Skull(Vector3 pos, Vector3 euler, float s, Transform p = null)
            {
                Transform g = Group("Skull", pos, euler, p);
                Sph(V(0f, 0.085f * s, 0f), V(0.16f, 0.17f, 0.19f) * s, M.Bone, default(Vector3), g);
                Box(V(0f, 0.025f * s, 0.055f * s), V(0.095f, 0.045f, 0.085f) * s, M.Bone, V(-6f, 0f, 0f), g);
                Sph(V(-0.032f * s, 0.09f * s, 0.085f * s), V(0.04f, 0.045f, 0.03f) * s, M.ClothDark, default(Vector3), g);
                Sph(V(0.032f * s, 0.09f * s, 0.085f * s), V(0.04f, 0.045f, 0.03f) * s, M.ClothDark, default(Vector3), g);
                return g;
            }

            public GameObject Bone(Vector3 pos, float len, float d, Vector3 euler, Transform p = null)
            {
                GameObject go = Cap(pos, d, len, M.Bone, euler, p);
                go.name = "Bone";
                return go;
            }

            /// <summary>A bone lying roughly flat with a random heading and a little pitch.</summary>
            public Vector3 LyingEuler() { return V(0f, R(0f, 360f), 90f + R(-25f, 25f)); }

            /// <summary>Candle with base at b; returns the wick tip.</summary>
            public Vector3 Candle(Vector3 b, float h, float d, Transform p = null)
            {
                GameObject wax = Cyl(b + V(0f, h * 0.5f, 0f), d, h, M.Wax, V(R(-2.5f, 2.5f), 0f, R(-2.5f, 2.5f)), p);
                wax.name = "Candle";
                Cyl(b + V(0f, h + 0.013f, 0f), 0.008f, 0.03f, M.ClothDark, default(Vector3), p).name = "Wick";
                if (h > 0.12f) Sph(b + V(d * 0.45f, h * 0.7f, 0f), V(0.018f, 0.05f, 0.018f), M.Wax, default(Vector3), p).name = "Drip";
                return b + V(0f, h + 0.03f, 0f);
            }
        }

        // ------------------------------------------------------------------ prop recipes

        private static void BuildProp(string id, Ctx c)
        {
            switch (id)
            {
                case "PH_Sarcophagus": Sarcophagus(c, false); break;
                case "PH_SarcophagusBroken": Sarcophagus(c, true); break;
                case "PH_CoffinWood": Coffin(c, false); break;
                case "PH_CoffinOpen": Coffin(c, true); break;
                case "PH_BonePile": BonePile(c, 1.3f, 5, 22); break;
                case "PH_SkullHeap_Small": BonePile(c, 0.6f, 3, 7); break;
                case "PH_OssuaryShelf_Wall": OssuaryShelf(c); break;
                case "PH_IronBrazier": IronBrazier(c); break;
                case "PH_CandleCluster": CandleCluster(c); break;
                case "PH_Candelabrum": Candelabrum(c); break;
                case "PH_Altar": Altar(c); break;
                case "PH_Pew": Pew(c); break;
                case "PH_StoneBench": StoneBench(c); break;
                case "PH_WeaponRack": WeaponRack(c); break;
                case "PH_Cage": Cage(c); break;
                case "PH_Chains_Wall": ChainsWall(c); break;
                case "PH_Banner_Wall": BannerWall(c); break;
                case "PH_BrokenPillar": BrokenPillar(c); break;
                case "PH_RubblePile": RubblePile(c); break;
                case "PH_Cot": Cot(c); break;
                case "PH_Cauldron": Cauldron(c); break;
                case "PH_Lectern": Lectern(c); break;
                case "PH_BookPile": BookPile(c); break;
                case "PH_Buttress_Wall": Buttress(c); break;
                default: throw new InvalidOperationException("[PrimitiveProps] no recipe for " + id);
            }
        }

        private static void Sarcophagus(Ctx c, bool broken)
        {
            Mats M = c.M;
            c.Box(V(0f, 0.1f, 0f), V(2.45f, 0.2f, 1.2f), M.StoneFloor);
            c.Box(V(0f, 0.27f, 0f), V(2.28f, 0.14f, 1.03f), M.StoneWall).name = "Skirt";
            c.Box(V(0f, 0.55f, 0f), V(2.2f, 0.7f, 0.95f), M.StoneWall).name = "Chest";
            c.Box(V(1.12f, 0.6f, 0.2f), V(0.03f, 0.3f, 0.06f), M.ClothDark, V(0f, 0f, 14f)).name = "Crack";

            if (!broken)
            {
                Transform lid = c.Group("Lid", V(0.05f, 0.99f, 0.01f), V(0f, 1.5f, 0f));
                c.Box(V(0f, 0f, 0f), V(2.32f, 0.18f, 1.04f), M.StoneWall, default(Vector3), lid).name = "LidSlab";
                c.Box(V(0f, 0.105f, 0f), V(1.7f, 0.03f, 0.14f), M.StoneWall, default(Vector3), lid).name = "CarvedBand";
                c.Box(V(0.55f, 0.105f, 0f), V(0.14f, 0.03f, 0.6f), M.StoneWall, default(Vector3), lid).name = "CarvedCross";
                c.Box(V(-1.1f, 0.0f, 0.47f), V(0.12f, 0.2f, 0.12f), M.ClothDark, V(0f, 25f, 0f), lid).name = "Chip";
                return;
            }

            c.Box(V(0f, 0.9f, 0f), V(2.0f, 0.02f, 0.73f), M.ClothDark).name = "Interior";
            Transform onChest = c.Group("LidHalfOnChest", V(-0.45f, 0.99f, 0.14f), V(2f, 8f, 5f));
            c.Box(V(0f, 0f, 0f), V(1.15f, 0.18f, 1.04f), M.StoneWall, default(Vector3), onChest);
            c.Box(V(-0.2f, 0.105f, 0f), V(0.7f, 0.03f, 0.14f), M.StoneWall, default(Vector3), onChest);
            Transform leaning = c.Group("LidHalfLeaning", V(1.5f, 0.49f, 0.1f), V(2f, 12f, 27f));
            c.Box(V(0f, 0f, 0f), V(0.18f, 1.1f, 1.04f), M.StoneWall, default(Vector3), leaning);
            c.Box(V(0.105f, -0.1f, 0f), V(0.03f, 0.5f, 0.14f), M.StoneWall, default(Vector3), leaning);
            for (int i = 0; i < 6; i++)
            {
                float s = c.R(0.06f, 0.16f);
                c.Box(V(c.R(0.9f, 1.7f), s * 0.4f, c.R(-0.6f, 0.9f)), V(s * c.R(0.8f, 1.6f), s, s * c.R(0.8f, 1.4f)), M.StoneWall, V(c.R(-25f, 25f), c.R(0f, 360f), c.R(-25f, 25f))).name = "Chunk";
            }
        }

        private static void CoffinShell(Ctx c)
        {
            Mats M = c.M;
            float y0 = 0.05f;
            c.Box(V(0.78f, 0.03f, 0f), V(0.46f, 0.04f, 0.38f), M.Wood);
            c.Box(V(-0.2f, 0.03f, 0f), V(1.6f, 0.04f, 0.28f), M.Wood);
            // hexagonal outline: head (+x), shoulders, foot (-x)
            for (int s = -1; s <= 1; s += 2)
            {
                c.Plank(V(1.0f, 0f, s * 0.19f), V(0.55f, 0f, s * 0.3f), y0, 0.5f, 0.04f, M.Wood);
                c.Plank(V(0.55f, 0f, s * 0.3f), V(-1.0f, 0f, s * 0.14f), y0, 0.5f, 0.04f, M.Wood);
            }
            c.Box(V(1.0f, 0.3f, 0f), V(0.04f, 0.5f, 0.4f), M.Wood);
            c.Box(V(-1.0f, 0.3f, 0f), V(0.04f, 0.5f, 0.3f), M.Wood);
            c.Box(V(0.25f, 0.26f, 0.285f), V(0.06f, 0.46f, 0.02f), M.Iron).name = "Band";
        }

        private static void Coffin(Ctx c, bool open)
        {
            Mats M = c.M;
            CoffinShell(c);
            if (!open)
            {
                Transform lid = c.Group("Lid", V(0f, 0.57f, 0f), V(0f, 2f, 0f));
                c.Box(V(0.78f, 0f, 0f), V(0.46f, 0.03f, 0.5f), M.Wood, default(Vector3), lid);
                c.Box(V(0.18f, 0f, 0f), V(0.78f, 0.03f, 0.64f), M.Wood, V(0f, 0f, 0f), lid);
                c.Box(V(-0.62f, 0f, 0f), V(0.78f, 0.03f, 0.36f), M.Wood, V(0f, 0f, 0f), lid);
                c.Box(V(0.25f, 0.025f, 0f), V(0.06f, 0.02f, 0.6f), M.Iron, default(Vector3), lid).name = "Band";
                return;
            }

            c.Box(V(-0.1f, 0.09f, 0f), V(1.6f, 0.07f, 0.26f), M.ClothDark, V(0f, 3f, 0f)).name = "Shroud";
            c.Skull(V(0.82f, 0.1f, 0f), V(0f, 100f, 0f), 0.9f);
            c.Bone(V(0.35f, 0.17f, 0.1f), 0.36f, 0.04f, V(0f, 10f, 90f));
            c.Bone(V(0.35f, 0.17f, -0.1f), 0.36f, 0.04f, V(0f, -8f, 90f));
            for (int i = 0; i < 4; i++) c.Bone(V(0.45f - i * 0.1f, 0.2f, c.R(-0.02f, 0.02f)), 0.22f, 0.03f, V(0f, 90f, 90f + c.R(-8f, 8f)));
            c.Bone(V(-0.45f, 0.15f, 0.07f), 0.5f, 0.045f, V(0f, 4f, 90f));
            c.Bone(V(-0.45f, 0.15f, -0.07f), 0.5f, 0.045f, V(0f, -3f, 90f));
            c.Box(V(0f, 0.27f, 0.43f), V(1.9f, 0.6f, 0.035f), M.Wood, V(-25f, 1.5f, 0f)).name = "LidPropped";
            c.Box(V(0f, 0.27f, 0.395f), V(0.06f, 0.6f, 0.02f), M.Iron, V(-25f, 1.5f, 0f)).name = "Band";
        }

        private static void BonePile(Ctx c, float width, int skulls, int bones)
        {
            float k = width / 1.3f;
            c.Sph(V(0f, 0.04f * k, 0f), V(1.2f * k, 0.16f * k, 0.95f * k), c.M.ClothDark).name = "Dirt";
            Func<float, float> heap = r => 0.22f * k * Mathf.Clamp01(1f - r / (0.7f * k));
            for (int i = 0; i < skulls; i++)
            {
                float ang = (i * 360f / skulls + c.R(-15f, 15f)) * Mathf.Deg2Rad;
                float r = i == 0 ? 0.02f : c.R(0.15f, 0.42f) * k;
                c.Skull(V(Mathf.Cos(ang) * r, heap(r) + 0.02f, Mathf.Sin(ang) * r * 0.8f), V(c.R(-25f, 25f), c.R(0f, 360f), c.R(-25f, 25f)), 1f * Mathf.Max(k, 0.8f));
            }
            for (int i = 0; i < bones; i++)
            {
                float ang = c.R(0f, 360f) * Mathf.Deg2Rad;
                float r = c.R(0f, 0.55f) * k;
                float len = c.R(0.28f, 0.5f) * Mathf.Max(k, 0.6f);
                c.Bone(V(Mathf.Cos(ang) * r, heap(r) + 0.06f, Mathf.Sin(ang) * r * 0.8f), len, c.R(0.035f, 0.05f), c.LyingEuler());
            }
        }

        private static void OssuaryShelf(Ctx c)
        {
            Mats M = c.M;
            c.Box(V(-1.125f, 1.14f, 0.25f), V(0.15f, 2.28f, 0.5f), M.StoneWall).name = "UprightL";
            c.Box(V(1.125f, 1.14f, 0.25f), V(0.15f, 2.28f, 0.5f), M.StoneWall).name = "UprightR";
            c.Box(V(0f, 1.2f, 0.03f), V(2.1f, 2.4f, 0.06f), M.StoneWall).name = "Back";
            c.Box(V(0f, 2.34f, 0.25f), V(2.4f, 0.12f, 0.5f), M.StoneWall).name = "Cap";
            float[] boardY = { 0.05f, 0.78f, 1.5f };
            float[] boardH = { 0.1f, 0.08f, 0.08f };
            for (int s = 0; s < 3; s++)
            {
                c.Box(V(0f, boardY[s], 0.25f), V(2.1f, boardH[s], 0.5f), M.StoneWall).name = "Shelf" + s;
                float floorY = boardY[s] + boardH[s] * 0.5f;
                int count = s == 2 ? 4 : 6;
                for (int i = 0; i < count; i++)
                {
                    if (c.R(0f, 1f) < 0.15f) continue;
                    float x = -0.85f + i * (1.7f / (count - 1)) + c.R(-0.05f, 0.05f);
                    float z = (i % 2 == 0) ? 0.3f : 0.17f;
                    c.Skull(V(x, floorY, z), V(c.R(-6f, 6f), 180f + c.R(-30f, 30f), c.R(-6f, 6f)), 1f);
                }
                for (int b = 0; b < 3; b++)
                {
                    c.Bone(V(c.R(-0.7f, 0.7f), floorY + 0.03f, 0.41f), c.R(0.35f, 0.5f), 0.045f, V(0f, c.R(-12f, 12f), 90f + c.R(-3f, 3f)));
                }
            }
        }

        private static void IronBrazier(Ctx c)
        {
            Mats M = c.M;
            Vector3[] foot = new Vector3[3];
            for (int i = 0; i < 3; i++)
            {
                float a = (90f + i * 120f + c.R(-6f, 6f)) * Mathf.Deg2Rad;
                foot[i] = V(Mathf.Cos(a) * 0.4f, 0f, Mathf.Sin(a) * 0.4f);
                Vector3 top = V(Mathf.Cos(a) * 0.22f, 0.74f, Mathf.Sin(a) * 0.22f);
                c.Between(PrimitiveType.Cylinder, foot[i] + V(0f, 0.02f, 0f), top, 0.045f, M.Iron);
            }
            for (int i = 0; i < 3; i++)
            {
                Vector3 a = Vector3.Lerp(foot[i], V(0f, 0f, 0f), 0.2f) + V(0f, 0.34f, 0f);
                Vector3 b = Vector3.Lerp(foot[(i + 1) % 3], V(0f, 0f, 0f), 0.2f) + V(0f, 0.34f, 0f);
                c.Between(PrimitiveType.Cube, a, b, 0.03f, M.Rust);
            }
            c.Sph(V(0f, 0.8f, 0f), V(0.62f, 0.3f, 0.62f), M.Iron).name = "Bowl";
            c.Cyl(V(0f, 0.945f, 0f), 0.66f, 0.05f, M.Rust).name = "Rim";
            c.Cyl(V(0f, 0.975f, 0f), 0.5f, 0.02f, M.Coal).name = "CoalBed";
            for (int i = 0; i < 9; i++)
            {
                float a = c.R(0f, 360f) * Mathf.Deg2Rad;
                float r = c.R(0f, 0.2f);
                float s = c.R(0.06f, 0.11f);
                c.Box(V(Mathf.Cos(a) * r, 0.985f + s * 0.3f - (r * 0.1f), Mathf.Sin(a) * r), V(s, s * 0.8f, s * c.R(0.8f, 1.2f)), M.Coal, V(c.R(0f, 60f), c.R(0f, 360f), c.R(0f, 60f))).name = "Coal";
            }
            c.Socket(V(0f, 1.03f + 0.15f, 0f));
        }

        private static void CandleCluster(Ctx c)
        {
            Mats M = c.M;
            c.Cyl(V(0f, 0.02f, 0f), 0.6f, 0.04f, M.StoneFloor, V(0f, 0f, 0f)).name = "Slab";
            c.Sph(V(0.02f, 0.05f, -0.01f), V(0.5f, 0.05f, 0.46f), M.Wax).name = "WaxBase";
            List<Vector2> placed = new List<Vector2>();
            Vector3 tallestTip = V(0f, 0f, 0f);
            int count = 9;
            for (int i = 0; i < count; i++)
            {
                Vector2 p = Vector2.zero;
                for (int attempt = 0; attempt < 30; attempt++)
                {
                    float a = c.R(0f, 360f) * Mathf.Deg2Rad;
                    float r = c.R(0f, 0.24f);
                    p = new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
                    bool free = true;
                    for (int q = 0; q < placed.Count; q++) if ((placed[q] - p).sqrMagnitude < 0.0064f) { free = false; break; }
                    if (free) break;
                }
                placed.Add(p);
                float h = i == 0 ? 0.35f : c.R(0.08f, 0.3f);
                Vector3 tip = c.Candle(V(p.x, 0.06f, p.y), h, c.R(0.04f, 0.065f));
                if (i == 0) tallestTip = tip;
            }
            c.Socket(tallestTip + V(0f, 0.05f, 0f));
        }

        private static void Candelabrum(Ctx c)
        {
            Mats M = c.M;
            c.Cyl(V(0f, 0.03f, 0f), 0.46f, 0.06f, M.Iron).name = "Base";
            c.Cyl(V(0f, 0.09f, 0f), 0.3f, 0.06f, M.Iron).name = "BaseStep";
            c.Cyl(V(0f, 0.75f, 0f), 0.045f, 1.35f, M.Iron).name = "Stem";
            c.Sph(V(0f, 0.45f, 0f), V(0.09f, 0.09f, 0.09f), M.Iron);
            c.Sph(V(0f, 0.95f, 0f), V(0.08f, 0.08f, 0.08f), M.Iron);
            c.Cyl(V(0f, 1.395f, 0f), 0.1f, 0.03f, M.Iron).name = "CupMid";
            Vector3 tip = c.Candle(V(0f, 1.41f, 0f), 0.19f, 0.052f);
            for (int s = -1; s <= 1; s += 2)
            {
                Vector3 a = V(0f, 1.0f, 0f);
                Vector3 b = V(s * 0.24f, 1.1f, 0f);
                Vector3 d = V(s * 0.3f, 1.24f, 0f);
                c.Between(PrimitiveType.Cylinder, a, b, 0.03f, M.Iron);
                c.Between(PrimitiveType.Cylinder, b, d, 0.03f, M.Iron);
                c.Cyl(V(d.x, 1.255f, 0f), 0.09f, 0.03f, M.Iron).name = "Cup";
                c.Candle(V(d.x, 1.27f, 0f), s < 0 ? 0.13f : 0.16f, 0.048f);
            }
            c.Socket(tip + V(0f, 0.05f, 0f));
        }

        private static void CandleGroup(Ctx c, Vector3 origin, float maxH)
        {
            float[] hs = { 1f, 0.7f, 0.5f };
            Vector2[] offs = { new Vector2(0f, 0f), new Vector2(0.1f, 0.07f), new Vector2(-0.08f, 0.1f) };
            c.Sph(origin + V(0f, 0.012f, 0.03f), V(0.3f, 0.03f, 0.26f), c.M.Wax).name = "WaxBase";
            for (int i = 0; i < 3; i++) c.Candle(origin + V(offs[i].x, 0.02f, offs[i].y), maxH * hs[i] * c.R(0.9f, 1.05f), c.R(0.045f, 0.06f));
        }

        private static void Altar(Ctx c)
        {
            Mats M = c.M;
            c.Box(V(0f, 0.09f, 0f), V(3.0f, 0.18f, 1.8f), M.StoneFloor).name = "Step1";
            c.Box(V(0.02f, 0.27f, 0f), V(2.5f, 0.18f, 1.45f), M.StoneFloor, V(0f, 0.8f, 0f)).name = "Step2";
            c.Box(V(0f, 0.735f, 0f), V(1.9f, 0.75f, 0.82f), M.StoneWall).name = "Block";
            c.Box(V(0f, 1.15f, 0f), V(2.1f, 0.08f, 0.95f), M.StoneWall).name = "TopSlab";
            c.Box(V(0f, 1.196f, 0f), V(0.6f, 0.012f, 0.98f), M.Cloth).name = "Runner";
            c.Box(V(0f, 0.89f, 0.481f), V(0.6f, 0.62f, 0.012f), M.Cloth).name = "RunnerDrop";
            c.Box(V(0f, 0.7f, 0.42f), V(0.07f, 0.4f, 0.02f), M.Iron).name = "CrossV";
            c.Box(V(0f, 0.78f, 0.42f), V(0.26f, 0.07f, 0.02f), M.Iron).name = "CrossH";
            c.Box(V(0.95f, 0.1f, 0.78f), V(0.2f, 0.2f, 0.18f), M.StoneWall, V(10f, 35f, 8f)).name = "Chip";
            Vector3 left = V(-0.7f, 1.19f, 0.0f);
            Vector3 right = V(0.7f, 1.19f, 0.05f);
            CandleGroup(c, left, 0.22f);
            CandleGroup(c, right, 0.3f);
            c.Socket(right + V(0f, 0.3f + 0.03f + 0.07f, 0f));
        }

        private static void Pew(Ctx c)
        {
            Mats M = c.M;
            c.Box(V(0f, 0.45f, 0.02f), V(2.4f, 0.06f, 0.42f), M.Wood).name = "Seat";
            for (int s = -1; s <= 1; s += 2)
            {
                c.Box(V(s * 1.16f, 0.475f, 0f), V(0.08f, 0.95f, 0.5f), M.Wood).name = "EndPanel";
            }
            float[] ys = { 0.6f, 0.73f, 0.86f };
            for (int i = 0; i < 3; i++)
            {
                float len = i == 1 ? 1.6f : 2.24f;
                float x = i == 1 ? -0.3f : 0f;
                c.Box(V(x, ys[i], -0.2f), V(len, 0.11f, 0.035f), M.Wood, V(-6f, 0f, i == 1 ? 3f : 0f)).name = "BackPlank";
            }
            c.Box(V(0f, 0.95f, -0.22f), V(2.4f, 0.05f, 0.07f), M.Wood).name = "TopRail";
            c.Box(V(0f, 0.15f, 0.31f), V(2.2f, 0.06f, 0.2f), M.Wood).name = "Kneeler";
            c.Box(V(0f, 0.28f, 0.0f), V(2.24f, 0.05f, 0.05f), M.Wood).name = "Stretcher";
        }

        private static void StoneBench(Ctx c)
        {
            Mats M = c.M;
            c.Box(V(0f, 0.45f, 0f), V(1.8f, 0.12f, 0.45f), M.StoneWall).name = "Seat";
            c.Box(V(-0.65f, 0.195f, 0f), V(0.14f, 0.39f, 0.38f), M.StoneWall).name = "SupportL";
            c.Box(V(0.65f, 0.195f, 0f), V(0.14f, 0.39f, 0.38f), M.StoneWall, V(0f, 4f, 0f)).name = "SupportR";
            c.Box(V(0.97f, 0.05f, 0.3f), V(0.18f, 0.1f, 0.14f), M.StoneWall, V(8f, 30f, 6f)).name = "Chunk";
            c.Box(V(0.75f, 0.512f, 0.12f), V(0.35f, 0.012f, 0.02f), M.ClothDark, V(0f, 25f, 0f)).name = "Crack";
        }

        private static void WeaponRack(Ctx c)
        {
            Mats M = c.M;
            for (int f = -1; f <= 1; f += 2)
            {
                float z = f * 0.22f;
                c.Between(PrimitiveType.Cube, V(-0.8f, 0f, z), V(-0.08f, 1.78f, z), 0.07f, M.Wood);
                c.Between(PrimitiveType.Cube, V(0.8f, 0f, z), V(0.08f, 1.78f, z), 0.07f, M.Wood);
                c.Box(V(0f, 0.45f, z), V(1.2f, 0.06f, 0.06f), M.Wood);
                c.Box(V(0f, 1.1f, z), V(0.7f, 0.06f, 0.06f), M.Wood, V(0f, 0f, 1.5f));
            }
            c.Box(V(0f, 1.78f, 0f), V(0.24f, 0.07f, 0.5f), M.Wood).name = "Ridge";
            c.Box(V(-0.78f, 0.04f, 0f), V(0.08f, 0.07f, 0.52f), M.Wood).name = "FootL";
            c.Box(V(0.78f, 0.04f, 0f), V(0.08f, 0.07f, 0.52f), M.Wood).name = "FootR";

            float[] xs = { -0.55f, -0.3f, 0.3f, 0.55f };
            for (int i = 0; i < 4; i++)
            {
                Vector3 euler = V(c.R(-4f, 4f), 0f, c.R(-7f, 7f));
                Vector3 basePos = V(xs[i], 0.03f, c.R(-0.08f, 0.08f));
                Quaternion q = Quaternion.Euler(euler);
                float len = i % 2 == 0 ? 2.0f : 1.9f;
                Vector3 mid = basePos + q * V(0f, len * 0.5f, 0f);
                GameObject shaft = c.Cyl(mid, 0.032f, len, M.Wood, euler);
                shaft.name = "Shaft";
                Transform head = c.Group("Head", basePos + q * V(0f, len, 0f), euler);
                if (i % 2 == 0)
                {
                    c.Cyl(V(0f, 0.02f, 0f), 0.05f, 0.1f, M.Iron, default(Vector3), head).name = "Socket";
                    c.Box(V(0f, 0.15f, 0f), V(0.06f, 0.22f, 0.012f), M.Iron, V(0f, 0f, 0f), head).name = "SpearHead";
                    c.Box(V(0f, 0.27f, 0f), V(0.04f, 0.06f, 0.012f), M.Iron, V(0f, 0f, 45f), head);
                }
                else
                {
                    c.Cyl(V(0f, 0f, 0f), 0.05f, 0.08f, M.Iron, default(Vector3), head).name = "Socket";
                    c.Box(V(0.1f, -0.06f, 0f), V(0.2f, 0.2f, 0.015f), M.Iron, V(0f, 0f, -8f), head).name = "AxeBlade";
                    c.Box(V(0f, 0.12f, 0f), V(0.035f, 0.2f, 0.015f), M.Iron, default(Vector3), head).name = "Spike";
                    c.Box(V(-0.06f, -0.02f, 0f), V(0.12f, 0.03f, 0.015f), M.Iron, V(0f, 0f, 25f), head).name = "Hook";
                }
            }

            Transform sword = c.Group("Sword", V(0.0f, 0.03f, 0.04f), V(0f, 12f, -9f));
            c.Box(V(0f, 0.48f, 0f), V(0.05f, 0.9f, 0.012f), M.Iron, default(Vector3), sword).name = "Blade";
            c.Box(V(0f, 0.95f, 0f), V(0.22f, 0.035f, 0.035f), M.Rust, default(Vector3), sword).name = "Guard";
            c.Cyl(V(0f, 1.06f, 0f), 0.03f, 0.2f, M.ClothDark, default(Vector3), sword).name = "Grip";
            c.Sph(V(0f, 1.18f, 0f), V(0.055f, 0.055f, 0.055f), M.Iron, default(Vector3), sword).name = "Pommel";
        }

        private static void Cage(Ctx c)
        {
            Mats M = c.M;
            Material[] mix = { M.Iron, M.Iron, M.Rust };
            float h = 0.62f;
            // frames
            for (int s = -1; s <= 1; s += 2)
            {
                c.Box(V(0f, 0.03f, s * h), V(1.3f, 0.06f, 0.06f), M.Iron).name = "BottomBeam";
                c.Box(V(s * h, 0.03f, 0f), V(0.06f, 0.06f, 1.18f), M.Iron).name = "BottomBeam";
                c.Box(V(0f, 2.05f, s * h), V(1.3f, 0.06f, 0.06f), M.Iron).name = "TopBeam";
                c.Box(V(s * h, 2.05f, 0f), V(0.06f, 0.06f, 1.18f), M.Iron).name = "TopBeam";
                for (int t = -1; t <= 1; t += 2) c.Cyl(V(s * h, 1.05f, t * h), 0.055f, 2.1f, M.Iron).name = "Post";
            }
            c.Box(V(0f, 0.07f, 0f), V(1.2f, 0.03f, 1.2f), M.Rust).name = "Floor";
            c.Box(V(0f, 2.05f, 0f), V(1.2f, 0.04f, 0.05f), M.Iron).name = "TopCross";
            c.Box(V(0f, 2.05f, 0f), V(0.05f, 0.04f, 1.2f), M.Iron).name = "TopCross";
            c.Sph(V(0f, 2.05f, 0f), V(0.22f, 0.1f, 0.22f), M.Iron).name = "TopRing";
            // hoops (not on the door side)
            c.Box(V(0f, 1.0f, -h), V(1.2f, 0.03f, 0.03f), M.Iron).name = "Hoop";
            c.Box(V(-h, 1.0f, 0f), V(0.03f, 0.03f, 1.2f), M.Iron).name = "Hoop";
            c.Box(V(h, 1.0f, 0f), V(0.03f, 0.03f, 1.2f), M.Iron).name = "Hoop";

            float[] ts = { -0.5f, -0.3f, -0.1f, 0.1f, 0.3f, 0.5f };
            for (int i = 0; i < ts.Length; i++)
            {
                Material m = mix[c.RI(0, mix.Length)];
                c.Cyl(V(ts[i], 1.05f, -h), 0.022f, 1.95f, m).name = "Bar";
                c.Cyl(V(-h, 1.05f, ts[i]), 0.022f, 1.95f, m).name = "Bar";
                if (i == 2) c.Cyl(V(h + 0.03f, 1.05f, ts[i]), 0.022f, 1.95f, m, V(0f, 0f, 5f)).name = "BentBar";
                else c.Cyl(V(h, 1.05f, ts[i]), 0.022f, 1.95f, m).name = "Bar";
            }
            c.Cyl(V(-0.5f, 1.05f, h), 0.022f, 1.95f, M.Iron).name = "Bar";
            c.Cyl(V(0.5f, 1.05f, h), 0.022f, 1.95f, M.Iron).name = "Bar";

            // door hinged at the left front post, ajar inwards so the footprint stays centred
            Transform door = c.Group("Door", V(-0.4f, 0f, h), V(0f, 22f, 0f));
            c.Cyl(V(0f, 1.0f, 0f), 0.04f, 1.9f, M.Iron, default(Vector3), door);
            c.Cyl(V(0.8f, 1.0f, 0f), 0.04f, 1.9f, M.Iron, default(Vector3), door);
            c.Box(V(0.4f, 1.9f, 0f), V(0.8f, 0.04f, 0.04f), M.Iron, default(Vector3), door);
            c.Box(V(0.4f, 0.14f, 0f), V(0.8f, 0.04f, 0.04f), M.Iron, default(Vector3), door);
            for (int i = 1; i <= 3; i++) c.Cyl(V(i * 0.2f, 1.02f, 0f), 0.02f, 1.76f, M.Iron, default(Vector3), door).name = "Bar";

            // remains inside
            c.Skull(V(-0.2f, 0.085f, -0.2f), V(0f, 40f, 0f), 1f);
            c.Bone(V(0.15f, 0.12f, -0.25f), 0.45f, 0.045f, c.LyingEuler());
            c.Bone(V(0.25f, 0.12f, 0.1f), 0.4f, 0.04f, c.LyingEuler());
            c.Bone(V(-0.3f, 0.12f, 0.15f), 0.35f, 0.04f, c.LyingEuler());
        }

        private static void ChainsWall(Ctx c)
        {
            Mats M = c.M;
            c.Box(V(0f, 1.3f, 0.025f), V(0.9f, 0.14f, 0.05f), M.Iron).name = "Plate";
            c.Sph(V(-0.38f, 1.3f, 0.055f), V(0.05f, 0.05f, 0.05f), M.Rust).name = "Bolt";
            c.Sph(V(0.38f, 1.3f, 0.055f), V(0.05f, 0.05f, 0.05f), M.Rust).name = "Bolt";
            float[] xs = { -0.28f, 0.0f, 0.3f };
            float[] lens = { 1.15f, 0.62f, 0.85f };
            float z = 0.08f;
            float lastY = 0f;
            for (int k = 0; k < 3; k++)
            {
                c.Box(V(xs[k], 1.2f, 0.065f), V(0.045f, 0.09f, 0.03f), M.Iron).name = "Eye";
                int n = Mathf.RoundToInt(lens[k] / 0.07f);
                float x = xs[k];
                float y = 1.16f;
                for (int i = 0; i < n; i++)
                {
                    bool big = i % 2 == 0;
                    c.Cap(V(x, y, z), big ? 0.04f : 0.026f, 0.09f, M.Iron, V(0f, big ? 0f : 90f, c.R(-4f, 4f))).name = "Link";
                    x += (k - 1) * 0.004f + c.R(-0.002f, 0.002f);
                    y -= 0.07f;
                }
                lastY = y;
                if (k == 0)
                {
                    float yb = y - 0.03f;
                    c.Cyl(V(x - 0.075f, yb, z), 0.11f, 0.03f, M.Iron, V(90f, 0f, 0f)).name = "ShackleRingL";
                    c.Cyl(V(x + 0.075f, yb, z), 0.11f, 0.03f, M.Iron, V(90f, 0f, 0f)).name = "ShackleRingR";
                    c.Box(V(x, yb + 0.075f, z), V(0.2f, 0.04f, 0.04f), M.Rust).name = "ShackleBar";
                    c.Sph(V(x, yb + 0.07f, z + 0.03f), V(0.04f, 0.04f, 0.04f), M.Iron).name = "Pin";
                }
                else if (k == 2)
                {
                    c.Cyl(V(x, y - 0.02f, z), 0.1f, 0.025f, M.Iron, V(90f, 0f, 0f)).name = "EndRing";
                }
            }
        }

        private static void BannerWall(Ctx c)
        {
            Mats M = c.M;
            c.Cyl(V(0f, 2.385f, 0.07f), 0.03f, 1.2f, M.Iron, V(0f, 0f, 90f)).name = "Rod";
            c.Sph(V(-0.6f, 2.385f, 0.07f), V(0.055f, 0.055f, 0.055f), M.Iron).name = "FinialL";
            c.Sph(V(0.6f, 2.385f, 0.07f), V(0.055f, 0.055f, 0.055f), M.Iron).name = "FinialR";
            c.Box(V(-0.5f, 2.385f, 0.035f), V(0.05f, 0.05f, 0.07f), M.Iron).name = "BracketL";
            c.Box(V(0.5f, 2.385f, 0.035f), V(0.05f, 0.05f, 0.07f), M.Iron).name = "BracketR";
            float z = 0.1f;
            c.Box(V(0f, 1.935f, z), V(1.1f, 0.87f, 0.012f), M.Cloth).name = "ClothTop";
            c.Box(V(0f, 1.52f, z + 0.002f), V(1.1f, 0.05f, 0.012f), M.ClothDark).name = "Hem";
            c.Box(V(0f, 2.0f, z + 0.01f), V(0.3f, 0.3f, 0.012f), M.ClothDark, V(0f, 0f, 45f)).name = "Emblem";
            int strips = 9;
            float w = 1.1f / strips;
            int longest = c.RI(0, strips);
            for (int i = 0; i < strips; i++)
            {
                float len = i == longest ? 1.5f : c.R(0.3f, 1.35f);
                float x = -0.55f + w * (i + 0.5f);
                c.Box(V(x, 1.5f - len * 0.5f, z + (i % 2 == 0 ? 0.006f : -0.006f)), V(w * 0.94f, len, 0.012f), i % 3 == 1 ? M.ClothDark : M.Cloth, V(0f, 0f, c.R(-3f, 3f))).name = "Strip";
            }
        }

        private static void BrokenPillar(Ctx c)
        {
            Mats M = c.M;
            c.Cyl(V(0f, 0.075f, 0f), 0.98f, 0.15f, M.StoneFloor).name = "Plinth";
            c.Cyl(V(0f, 0.6f, 0f), 0.8f, 1.2f, M.StoneWall).name = "Stump";
            c.Box(V(0.18f, 1.24f, 0.08f), V(0.3f, 0.2f, 0.28f), M.StoneWall, V(8f, 25f, 12f)).name = "Shard";
            c.Box(V(-0.2f, 1.2f, -0.1f), V(0.26f, 0.16f, 0.3f), M.StoneWall, V(-10f, -20f, -14f)).name = "Shard";
            c.Cyl(V(1.25f, 0.4f, 0.3f), 0.8f, 0.6f, M.StoneWall, V(0f, 25f, 90f)).name = "DrumA";
            c.Cyl(V(0.3f, 0.4f, -1.25f), 0.8f, 0.5f, M.StoneWall, V(0f, -50f, 90f)).name = "DrumB";
            for (int i = 0; i < 8; i++)
            {
                float s = c.R(0.07f, 0.2f);
                float a = c.R(0f, 360f) * Mathf.Deg2Rad;
                float r = c.R(0.6f, 1.2f);
                c.Box(V(Mathf.Cos(a) * r, s * 0.4f, Mathf.Sin(a) * r), V(s * c.R(0.8f, 1.5f), s, s * c.R(0.8f, 1.3f)), M.StoneWall, V(c.R(-20f, 20f), c.R(0f, 360f), c.R(-20f, 20f))).name = "Rubble";
            }
        }

        private static void RubblePile(Ctx c)
        {
            Mats M = c.M;
            int count = c.RI(12, 17);
            for (int i = 0; i < count; i++)
            {
                float a = c.R(0f, 360f) * Mathf.Deg2Rad;
                float r = c.R(0f, 0.75f) * (i < 5 ? 1f : 0.7f);
                Vector3 size = V(c.R(0.22f, 0.5f), c.R(0.14f, 0.34f), c.R(0.2f, 0.42f));
                float heap = 0.35f * Mathf.Clamp01(1f - r / 0.8f);
                float y = i < 5 ? size.y * 0.35f : heap * 0.7f + size.y * 0.4f;
                Material m = i % 5 == 0 ? M.StoneFloor : M.StoneWall;
                c.Box(V(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r), size, m, V(c.R(-30f, 30f), c.R(0f, 360f), c.R(-30f, 30f))).name = "Block";
            }
        }

        private static void Cot(Ctx c)
        {
            Mats M = c.M;
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    c.Box(V(sx * 0.92f, 0.19f, sz * 0.36f), V(0.07f, 0.38f, 0.07f), M.Wood).name = "Leg";
            c.Box(V(0f, 0.34f, -0.36f), V(1.9f, 0.06f, 0.05f), M.Wood).name = "RailBack";
            c.Box(V(0f, 0.34f, 0.36f), V(1.9f, 0.06f, 0.05f), M.Wood).name = "RailFront";
            c.Box(V(-0.92f, 0.34f, 0f), V(0.05f, 0.06f, 0.65f), M.Wood).name = "RailHead";
            c.Box(V(0.92f, 0.34f, 0f), V(0.05f, 0.06f, 0.65f), M.Wood).name = "RailFoot";
            c.Box(V(-0.93f, 0.62f, 0f), V(0.05f, 0.5f, 0.74f), M.Wood).name = "Headboard";
            c.Box(V(0f, 0.37f, 0f), V(1.84f, 0.03f, 0.68f), M.Wood).name = "Slats";
            c.Box(V(0.02f, 0.455f, 0f), V(1.76f, 0.13f, 0.64f), M.Straw, V(0f, 1f, 0f)).name = "Mattress";
            for (int i = 0; i < 3; i++)
                c.Sph(V(c.R(-0.7f, 0.6f), 0.52f, c.R(-0.2f, 0.2f)), V(c.R(0.3f, 0.5f), 0.08f, c.R(0.2f, 0.3f)), M.Straw).name = "Lump";
            c.Box(V(0.15f, 0.53f, 0.02f), V(1.0f, 0.045f, 0.7f), M.ClothDark, V(0f, -3f, 0f)).name = "Blanket";
            c.Box(V(0.65f, 0.52f, -0.1f), V(0.4f, 0.04f, 0.12f), M.ClothDark, V(0f, 20f, 8f)).name = "BlanketFold";
            c.Box(V(-0.68f, 0.55f, 0f), V(0.34f, 0.08f, 0.38f), M.Straw, V(0f, 6f, 0f)).name = "Pillow";
        }

        private static void Cauldron(Ctx c)
        {
            Mats M = c.M;
            for (int i = 0; i < 3; i++)
            {
                float a = (i * 120f + 20f) * Mathf.Deg2Rad;
                c.Between(PrimitiveType.Cylinder, V(Mathf.Cos(a) * 0.36f, 0.02f, Mathf.Sin(a) * 0.36f), V(Mathf.Cos(a) * 0.3f, 0.22f, Mathf.Sin(a) * 0.3f), 0.07f, M.Iron).name = "Leg";
            }
            c.Sph(V(0f, 0.4f, 0f), V(0.9f, 0.56f, 0.9f), M.Iron).name = "Body";
            c.Cyl(V(0f, 0.64f, 0f), 0.8f, 0.12f, M.Iron).name = "Collar";
            c.Cyl(V(0f, 0.72f, 0f), 0.92f, 0.04f, M.Rust).name = "Rim";
            c.Cyl(V(0f, 0.741f, 0f), 0.74f, 0.012f, M.ClothDark).name = "Contents";
            c.Box(V(0.5f, 0.6f, 0f), V(0.1f, 0.06f, 0.08f), M.Iron).name = "EarR";
            c.Box(V(-0.5f, 0.6f, 0f), V(0.1f, 0.06f, 0.08f), M.Iron).name = "EarL";
            c.Box(V(0.28f, 0.5f, 0.38f), V(0.1f, 0.2f, 0.01f), M.Rust, V(0f, 30f, 10f)).name = "Patch";
        }

        private static void Lectern(Ctx c)
        {
            Mats M = c.M;
            c.Box(V(0f, 0.04f, 0f), V(0.55f, 0.08f, 0.5f), M.Wood).name = "Base";
            c.Box(V(0f, 0.58f, 0f), V(0.14f, 1.0f, 0.14f), M.Wood).name = "Post";
            c.Box(V(0f, 0.62f, 0f), V(0.22f, 0.1f, 0.22f), M.Wood).name = "Collar";
            Transform top = c.Group("Desk", V(0f, 1.1f, 0f), V(20f, 0f, 0f));
            c.Box(V(0f, 0f, 0f), V(0.58f, 0.04f, 0.46f), M.Wood, default(Vector3), top).name = "Board";
            c.Box(V(0f, 0.03f, 0.22f), V(0.58f, 0.04f, 0.035f), M.Wood, default(Vector3), top).name = "Ledge";
            c.Box(V(0f, 0.03f, 0f), V(0.46f, 0.02f, 0.32f), M.Cloth, default(Vector3), top).name = "Cover";
            c.Box(V(-0.105f, 0.045f, 0f), V(0.2f, 0.015f, 0.28f), M.Bone, V(0f, 0f, 4f), top).name = "PageL";
            c.Box(V(0.105f, 0.045f, 0f), V(0.2f, 0.015f, 0.28f), M.Bone, V(0f, 0f, -4f), top).name = "PageR";
        }

        private static void BookPile(Ctx c)
        {
            Mats M = c.M;
            Material[] covers = { M.Cloth, M.ClothDark, M.Wood, M.Rust, M.ClothDark };
            float y = 0f;
            int n = c.RI(6, 9);
            for (int i = 0; i < n; i++)
            {
                float w = c.R(0.18f, 0.28f), d = c.R(0.13f, 0.21f), h = c.R(0.028f, 0.05f);
                float yaw = c.R(-28f, 28f);
                Vector3 pos = V(c.R(-0.03f, 0.03f), y + h * 0.5f, c.R(-0.03f, 0.03f));
                Transform g = c.Group("Book", pos, V(0f, yaw, 0f));
                c.Box(V(0f, 0f, 0f), V(w, h, d), covers[i % covers.Length], default(Vector3), g).name = "Cover";
                c.Box(V(0.004f, 0f, 0f), V(w - 0.012f, h - 0.012f, d + 0.006f), M.Bone, default(Vector3), g).name = "Pages";
                y += h;
            }
        }

        private static void Buttress(Ctx c)
        {
            Mats M = c.M;
            c.Box(V(0f, 0.175f, 0.275f), V(1.04f, 0.35f, 0.55f), M.StoneWall).name = "Plinth";
            c.Box(V(0f, 2.525f, 0.225f), V(0.9f, 4.35f, 0.45f), M.StoneWall).name = "Shaft";
            c.Box(V(0f, 4.76f, 0.24f), V(0.98f, 0.12f, 0.48f), M.StoneWall).name = "CapLower";
            c.Box(V(0f, 4.91f, 0.28f), V(1.08f, 0.18f, 0.56f), M.StoneWall).name = "CapUpper";
        }

        // ------------------------------------------------------------------ review render (batch mode only)

        public static void RenderSheetBatch()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("[PrimitiveProps] RenderSheetBatch runs in batch mode only.");

            Directory.CreateDirectory(ReviewDir);
            List<ScriptableRendererFeature> switched = new List<ScriptableRendererFeature>();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try
            {
                Vector3 origin = new Vector3(5000f, 0f, 5000f);
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.26f, 0.26f, 0.3f);

                GameObject sceneRoot = new GameObject("PrimitivePropsReview");
                sceneRoot.transform.position = origin;

                GameObject plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
                plane.transform.SetParent(sceneRoot.transform, false);
                plane.transform.localScale = new Vector3(3.6f, 1f, 2.8f);
                Collider pc = plane.GetComponent<Collider>();
                if (pc != null) UnityEngine.Object.DestroyImmediate(pc);
                plane.GetComponent<Renderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Generated/DungeonSurfaces/World_Stone_Floor.mat");

                AddLight(sceneRoot.transform, "Key", new Color(1f, 0.74f, 0.5f), 1.6f, Quaternion.Euler(40f, 210f, 0f));
                AddLight(sceneRoot.transform, "Fill", new Color(0.55f, 0.62f, 0.85f), 0.6f, Quaternion.Euler(25f, 30f, 0f));

                GameObject camObject = new GameObject("ReviewCamera");
                camObject.transform.SetParent(sceneRoot.transform, false);
                Camera camera = camObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.06f, 0.06f, 0.07f);
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = 120f;

                const int cols = 6;
                const float cellX = 3.6f, cellZ = 4.2f;
                int rows = (Ids.Length + cols - 1) / cols;
                List<GameObject> instances = new List<GameObject>();
                for (int i = 0; i < Ids.Length; i++)
                {
                    GameObject prefab = Load(Ids[i]);
                    if (prefab == null) throw new InvalidOperationException("[PrimitiveProps] missing prefab " + Ids[i]);
                    GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    inst.transform.SetParent(sceneRoot.transform, false);
                    int col = i % cols, row = i / cols;
                    inst.transform.localPosition = new Vector3((col - (cols - 1) * 0.5f) * cellX, 0f, ((rows - 1) * 0.5f - row) * cellZ);
                    instances.Add(inst);
                }

                SwitchOffRetro(switched);

                Vector3 centre = origin + new Vector3(0f, 1.2f, 0f);
                camera.fieldOfView = 38f;
                camera.transform.position = centre + Quaternion.Euler(0f, 0f, 0f) * new Vector3(0f, 17f, 25f);
                camera.transform.LookAt(centre);
                RenderPng(camera, 2048, 1024, ReviewDir + "/sheet.png");

                camera.fieldOfView = 40f;
                for (int i = 0; i < instances.Count; i++)
                {
                    for (int k = 0; k < instances.Count; k++) instances[k].SetActive(k == i);
                    Bounds b = RendererBounds(instances[i]);
                    float extent = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                    float dist = Mathf.Max(1.2f, extent * 0.5f / Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.55f + b.size.z * 0.5f);
                    Vector3 dir = Quaternion.Euler(0f, 35f, 0f) * Vector3.forward;
                    Vector3 pos = b.center + dir * dist;
                    pos.y = 1.6f;
                    camera.transform.position = pos;
                    camera.transform.LookAt(b.center);
                    RenderPng(camera, 512, 512, ReviewDir + "/" + Ids[i] + ".png");
                }
            }
            finally
            {
                for (int i = 0; i < switched.Count; i++) if (switched[i] != null) switched[i].SetActive(true);
            }
            Debug.Log("[PrimitiveProps] review renders written to " + ReviewDir);
        }

        private static Bounds RendererBounds(GameObject go)
        {
            Renderer[] rs = go.GetComponentsInChildren<Renderer>();
            Bounds b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            return b;
        }

        private static void AddLight(Transform parent, string name, Color color, float intensity, Quaternion rotation)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            Light light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = color;
            light.intensity = intensity;
            light.shadows = LightShadows.Soft;
            go.transform.rotation = rotation;
        }

        private static void RenderPng(Camera camera, int width, int height, string path)
        {
            RenderTexture target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = null;
                camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static void SwitchOffRetro(List<ScriptableRendererFeature> switched)
        {
            UniversalRenderPipelineAsset asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (asset == null) return;
            System.Reflection.FieldInfo field = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            ScriptableRendererData[] datas = field != null ? field.GetValue(asset) as ScriptableRendererData[] : null;
            if (datas == null) return;
            for (int d = 0; d < datas.Length; d++)
            {
                if (datas[d] == null) continue;
                for (int f = 0; f < datas[d].rendererFeatures.Count; f++)
                {
                    ScriptableRendererFeature feature = datas[d].rendererFeatures[f];
                    if (feature != null && feature.isActive && (feature.name.Contains("Retro") || feature.name == "FullscreenMask" || feature.name == "ScreenStencil"))
                    {
                        switched.Add(feature);
                        feature.SetActive(false);
                    }
                }
            }
        }
    }
}
