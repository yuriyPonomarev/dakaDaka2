using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// V3b: the grim look of the undead. The Quaternius mannequin has no texture (UAL1 has no UVs at all, UAL2's female mesh has
    /// tiling UVs), so this builds everything from code, deterministically (same bytes every run):
    ///   * a derived mesh per mannequin: identical skin weights / bind poses, ONE submesh, planar UVs (u = lateral X of the bind
    ///     pose, v = height) - the front and the back of the body share the texture, which is fine for rags and rot;
    ///   * one body texture + one URP Lit material per variant ("Undead_Minion_Body" ...): rotting grey-green flesh (dark, desaturated,
    ///     low smoothness) with the clothes painted by height / lateral position (ragged hems, tears showing flesh): trousers and
    ///     tatters for the Minion, tunic + rusty breastplate for the Warrior, dark leather for the Rogue, a long robe for the Mage;
    ///   * Undead_RustyMetal (weapons, shield, pauldrons) and Undead_EyeGlow (emissive ember, Mage eyes only).
    /// Nothing here touches gameplay. Textures are Point filtered like the rest of the retro pass.
    /// </summary>
    public static class EmberdelveUndeadMaterials
    {
        public const string Folder = "Assets/Emberdelve/Art/Materials/Undead";
        public const string MeshFolder = "Assets/Emberdelve/Art/Models/Undead";
        public const string RustyMetalPath = Folder + "/Undead_RustyMetal.mat";
        public const string RustTexturePath = Folder + "/Undead_Rust.png";
        public const string EyeGlowPath = Folder + "/Undead_EyeGlow.mat";

        /// <summary>Texture size in pixels; u covers lateral X in [-TextureHalfWidth, +TextureHalfWidth] (T-pose arms), v covers height [0, TextureHeight] metres of the bind pose.</summary>
        public const int TextureSize = 512;
        public const float TextureHalfWidth = 1.0f;
        public const float TextureHeight = 1.9f;

        public sealed class Look
        {
            public string Name;
            public int Seed;
            public Color Flesh;        // base skin
            public Color Rot;          // decay patches
            public Color Cloth;
            public Color Trim;         // belt, boots, hems
            public bool Pants;         // trousers from the hip to PantsHem
            public float PantsHem;
            public bool Tunic;         // short tunic over the torso
            public bool Robe;          // long robe + wide sleeves (replaces tunic and pants)
            public bool Breastplate;   // rusty plate on the chest
            public bool Tatters;       // random cloth patches on the torso instead of a tunic
            public bool Boots;
        }

        public static readonly Look[] Looks =
        {
            new Look { Name = "Minion", Seed = 11, Flesh = new Color(0.30f, 0.33f, 0.27f), Rot = new Color(0.17f, 0.20f, 0.12f), Cloth = new Color(0.13f, 0.11f, 0.09f), Trim = new Color(0.07f, 0.06f, 0.05f), Pants = true, PantsHem = 0.45f, Tatters = true },
            new Look { Name = "Warrior", Seed = 23, Flesh = new Color(0.33f, 0.33f, 0.29f), Rot = new Color(0.19f, 0.20f, 0.15f), Cloth = new Color(0.09f, 0.09f, 0.10f), Trim = new Color(0.12f, 0.08f, 0.05f), Pants = true, PantsHem = 0.13f, Tunic = true, Breastplate = true, Boots = true },
            new Look { Name = "Rogue", Seed = 37, Flesh = new Color(0.26f, 0.30f, 0.30f), Rot = new Color(0.14f, 0.17f, 0.18f), Cloth = new Color(0.07f, 0.06f, 0.07f), Trim = new Color(0.14f, 0.07f, 0.05f), Pants = true, PantsHem = 0.14f, Tunic = true, Boots = true },
            new Look { Name = "Mage", Seed = 53, Flesh = new Color(0.36f, 0.34f, 0.30f), Rot = new Color(0.22f, 0.20f, 0.16f), Cloth = new Color(0.15f, 0.09f, 0.11f), Trim = new Color(0.30f, 0.14f, 0.05f), Robe = true },
        };

        public static Look LookFor(string variant)
        {
            for (int i = 0; i < Looks.Length; i++) if (Looks[i].Name == variant) return Looks[i];
            return null;
        }

        public static string BodyMaterialPath(string variant) { return Folder + "/Undead_" + variant + "_Body.mat"; }
        public static string BodyTexturePath(string variant) { return Folder + "/Undead_" + variant + "_Body.png"; }
        public static string MeshPath(string name) { return MeshFolder + "/" + name + ".asset"; }

        // ---------------------------------------------------------------- entry points

        /// <summary>Builds every texture, material and derived mesh the undead need. Idempotent.</summary>
        public static void EnsureAll()
        {
            EmberdelveVisualSetup.EnsureFolder(Folder);
            EmberdelveVisualSetup.EnsureFolder(MeshFolder);

            EnsureRust();
            EnsureEyeGlow();
            for (int i = 0; i < Looks.Length; i++) EnsureBody(Looks[i]);
            AssetDatabase.SaveAssets();
        }

        public static Material Load(string path) { return AssetDatabase.LoadAssetAtPath<Material>(path); }

        private static Shader Lit()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) Debug.LogError("[Emberdelve][Undead] URP Lit shader not found.");
            return shader;
        }

        private static void SetShader(Material material)
        {
            Shader lit = Lit();
            if (lit != null && material.shader != lit) material.shader = lit; // re-assigning the same shader would reset the keywords
        }

        private static Material LoadOrCreate(string path)
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) return material;

            material = new Material(Lit());
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        // ---------------------------------------------------------------- materials

        private static void EnsureRust()
        {
            const int size = 128;
            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)size, v = y / (float)size;
                    Color steel = new Color(0.15f, 0.15f, 0.15f);
                    Color rust = new Color(0.25f, 0.13f, 0.06f);
                    float blotch = Fbm(u * 6f, v * 6f, 5);
                    float t = Mathf.SmoothStep(0.38f, 0.7f, blotch);
                    Color c = Color.Lerp(steel, rust, t);
                    float pit = Fbm(u * 30f, v * 30f, 6);
                    c *= Mathf.Lerp(0.7f, 1.1f, pit);
                    float scratch = Mathf.Abs(Mathf.Sin((u * 0.8f + v * 2.1f) * 40f + Fbm(u * 9f, v * 9f, 7) * 5f));
                    if (scratch > 0.97f) c *= 1.25f;
                    pixels[y * size + x] = ToColor32(c);
                }
            }

            Texture2D texture = WriteTexture(RustTexturePath, size, pixels, 128);
            Material material = LoadOrCreate(RustyMetalPath);
            SetShader(material);
            material.SetTexture("_BaseMap", texture);
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Metallic", 0.55f);
            material.SetFloat("_Smoothness", 0.28f);
            EditorUtility.SetDirty(material);
        }

        private static void EnsureEyeGlow()
        {
            Material material = LoadOrCreate(EyeGlowPath);
            SetShader(material);
            material.SetColor("_BaseColor", new Color(0.05f, 0.02f, 0.01f));
            material.SetFloat("_Smoothness", 0.1f);
            material.SetColor("_EmissionColor", new Color(1.0f, 0.30f, 0.04f) * 1.8f);
            material.EnableKeyword("_EMISSION");
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive; // like the project's other emissive materials
            EditorUtility.SetDirty(material);
        }

        private static void EnsureBody(Look look)
        {
            Color32[] pixels = new Color32[TextureSize * TextureSize];
            for (int y = 0; y < TextureSize; y++)
            {
                float z = (y + 0.5f) / TextureSize * TextureHeight;
                for (int x = 0; x < TextureSize; x++)
                {
                    float lateral = ((x + 0.5f) / TextureSize * 2f - 1f) * TextureHalfWidth;
                    pixels[y * TextureSize + x] = ToColor32(Shade(look, lateral, z));
                }
            }

            Texture2D texture = WriteTexture(BodyTexturePath(look.Name), TextureSize, pixels, TextureSize);
            Material material = LoadOrCreate(BodyMaterialPath(look.Name));
            SetShader(material);
            material.SetTexture("_BaseMap", texture);
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Metallic", 0f);
            material.SetFloat("_Smoothness", 0.1f);
            EditorUtility.SetDirty(material);
        }

        // ---------------------------------------------------------------- painting

        /// <summary>Colour of the bind pose at lateral position x and height z (metres).</summary>
        public static Color Shade(Look look, float x, float z)
        {
            float ax = Mathf.Abs(x);
            int s = look.Seed;

            // ---- flesh: dark, desaturated, mottled, with decay patches
            float n1 = Fbm(x * 5f, z * 5f, s);
            float n2 = Fbm(x * 21f, z * 21f, s + 1);
            Color flesh = look.Flesh * Mathf.Lerp(0.70f, 1.15f, n1);
            float rot = Mathf.SmoothStep(0.56f, 0.78f, Fbm(x * 3.5f + 5f, z * 3.5f, s + 2));
            flesh = Color.Lerp(flesh, look.Rot, rot * 0.85f);
            flesh *= Mathf.Lerp(0.88f, 1.08f, n2);
            flesh *= 1.25f; // the torch-lit dungeon is dim; the albedo stays dark and desaturated (see the EditMode test)
            if (z < 0.07f || ax > 0.82f) flesh *= 0.72f; // grime on feet and hands

            // ---- clothes
            float cloth = 0f;
            Color clothColor = look.Cloth;
            float hemNoise = Fbm(x * 13f, 3.3f, s + 3) - 0.5f;

            if (look.Robe)
            {
                float flare = 0.24f + Mathf.Clamp01((1.0f - z) * 0.5f) * 0.10f;
                float hem = 0.20f + hemNoise * 0.18f;
                if (ax < flare && z > hem && z < 1.52f) cloth = 1f;
                if (z > 1.28f && z < 1.56f && ax < 0.66f + hemNoise * 0.25f) cloth = 1f; // wide sleeves
                if (cloth > 0f && z > 0.94f && z < 1.02f && ax < 0.25f) clothColor = look.Trim; // sash
            }
            else
            {
                if (look.Pants)
                {
                    float hem = look.PantsHem + hemNoise * 0.16f;
                    if (ax < 0.30f && z > hem && z < 1.0f) cloth = 1f;
                    if (ax < 0.26f && z > 0.93f && z < 0.99f) { cloth = 1f; clothColor = look.Trim; } // belt
                }

                if (look.Tunic)
                {
                    float bottom = 0.78f + hemNoise * 0.14f;
                    float half = 0.215f + ((z > 1.36f && z < 1.52f) ? 0.10f : 0f);
                    if (ax < half && z > bottom && z < 1.52f) { cloth = 1f; clothColor = look.Cloth; }
                    if (ax < 0.07f && z > 1.47f) cloth = 0f; // neck
                }

                if (look.Tatters && ax < 0.21f && z > 1.0f && z < 1.5f)
                {
                    float patch = Fbm(x * 8f + 9f, z * 8f, s + 4);
                    if (patch > 0.62f) cloth = Mathf.Max(cloth, 1f);
                }

                if (look.Boots && z < 0.16f + hemNoise * 0.04f && ax < 0.30f) { cloth = 1f; clothColor = look.Trim; }
            }

            // cloth: weave, stains, tears (a tear shows the rotten flesh below)
            Color result = flesh;
            if (cloth > 0f)
            {
                float weave = 0.88f + 0.12f * Mathf.Sin(x * 700f) * Mathf.Sin(z * 700f);
                float stain = Mathf.Lerp(0.55f, 1.25f, Fbm(x * 7f, z * 7f, s + 6));
                Color c = clothColor * weave * stain;
                float tear = Fbm(x * 11f + 2f, z * 11f, s + 7);
                result = tear > 0.74f ? flesh * 0.55f : c;
            }

            if (look.Breastplate && ax < 0.20f && z > 1.12f && z < 1.47f && !(ax < 0.07f && z > 1.44f))
            {
                float plate = Fbm(x * 6f, z * 6f, s + 8);
                Color rust = Color.Lerp(new Color(0.15f, 0.15f, 0.15f), new Color(0.24f, 0.12f, 0.06f), Mathf.SmoothStep(0.4f, 0.7f, plate));
                result = rust * Mathf.Lerp(0.75f, 1.1f, Fbm(x * 25f, z * 25f, s + 9));
                if (z > 1.12f && z < 1.15f) result *= 0.5f; // plate edge
            }

            result.a = 1f;
            return result;
        }

        // ---------------------------------------------------------------- noise

        private static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1274126177);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }

        private static float ValueNoise(float x, float y, int seed)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float fx = x - xi, fy = y - yi;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float a = Hash(xi, yi, seed), b = Hash(xi + 1, yi, seed), c = Hash(xi, yi + 1, seed), d = Hash(xi + 1, yi + 1, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        public static float Fbm(float x, float y, int seed)
        {
            float sum = 0f, amplitude = 0.5f, norm = 0f;
            for (int o = 0; o < 4; o++)
            {
                sum += ValueNoise(x, y, seed + o * 17) * amplitude;
                norm += amplitude;
                x *= 2.03f; y *= 2.03f; amplitude *= 0.5f;
            }

            return sum / norm;
        }

        private static Color32 ToColor32(Color c)
        {
            return new Color32((byte)Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255), (byte)Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255), (byte)Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255), 255);
        }

        // ---------------------------------------------------------------- texture file

        private static Texture2D WriteTexture(string assetPath, int size, Color32[] pixels, int maxSize)
        {
            Texture2D temp = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
            temp.SetPixels32(pixels);
            byte[] png = temp.EncodeToPNG();
            Object.DestroyImmediate(temp);

            string absolute = EmberdelveQuaterniusImport.Absolute(assetPath);
            bool write = !File.Exists(absolute);
            if (!write)
            {
                byte[] existing = File.ReadAllBytes(absolute);
                write = existing.Length != png.Length;
                for (int i = 0; !write && i < existing.Length; i++) if (existing[i] != png[i]) write = true;
            }

            if (write) File.WriteAllBytes(absolute, png);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer != null)
            {
                bool changed = false;
                if (importer.filterMode != FilterMode.Point) { importer.filterMode = FilterMode.Point; changed = true; }
                if (importer.maxTextureSize != maxSize) { importer.maxTextureSize = maxSize; changed = true; }
                if (importer.mipmapEnabled) { importer.mipmapEnabled = false; changed = true; }
                if (importer.textureCompression != TextureImporterCompression.Uncompressed) { importer.textureCompression = TextureImporterCompression.Uncompressed; changed = true; }
                if (importer.wrapMode != TextureWrapMode.Clamp) { importer.wrapMode = TextureWrapMode.Clamp; changed = true; }
                if (!importer.sRGBTexture) { importer.sRGBTexture = true; changed = true; }
                if (changed) importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        // ---------------------------------------------------------------- derived mesh

        /// <summary>
        /// The mannequin mesh of <paramref name="fbxPath"/> with planar UVs and one submesh, saved as <paramref name="meshName"/>.asset
        /// (the same asset is updated in place on every run, so references survive). Skin weights and bind poses are copied.
        /// </summary>
        public static Mesh EnsureMesh(string fbxPath, string meshName)
        {
            GameObject fbx = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            SkinnedMeshRenderer source = fbx != null ? fbx.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
            if (source == null || source.sharedMesh == null || !source.sharedMesh.isReadable)
            {
                Debug.LogWarning("[Emberdelve][Undead] mannequin mesh of " + fbxPath + " is missing or not readable.");
                return null;
            }

            Mesh original = source.sharedMesh;
            Mesh mesh = Object.Instantiate(original);
            mesh.name = meshName;

            Vector3[] vertices = original.vertices;
            Vector2[] uv = new Vector2[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                // Mesh space of the mannequin: X lateral, Z up (the FBX node carries the 270 degree rotation).
                uv[i] = new Vector2((vertices[i].x / TextureHalfWidth + 1f) * 0.5f, vertices[i].z / TextureHeight);
            }

            List<int> triangles = new List<int>(original.triangles);
            mesh.subMeshCount = 1;
            mesh.SetTriangles(triangles, 0);
            mesh.uv = uv;
            mesh.RecalculateBounds();

            EmberdelveVisualSetup.EnsureFolder(MeshFolder);
            string path = MeshPath(meshName);
            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null) AssetDatabase.CreateAsset(mesh, path);
            else
            {
                EditorUtility.CopySerialized(mesh, existing);
                Object.DestroyImmediate(mesh);
                mesh = existing;
                EditorUtility.SetDirty(mesh);
            }

            return mesh;
        }
    }
}
