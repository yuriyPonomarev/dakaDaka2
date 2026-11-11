using Emberdelve.Data;
using UnityEngine;
using UnityEngine.Rendering;

namespace Emberdelve.Player
{
    /// <summary>A hand-sized vial shared by owner hands and remote avatars. No gameplay state lives here.</summary>
    public sealed class PotionBottleVisual : MonoBehaviour
    {
        private static Mesh _glassMesh, _liquidMesh, _corkMesh, _bandMesh, _rimMesh;
        private static Material _glass, _liquid, _cork, _leather, _brass;
        private Transform _stopper, _contents;
        private const float LiquidBottom = .013f;

        public static GameObject Create(ItemData item, Transform parent)
        {
            EnsureResources();
            var root = new GameObject("Held potion " + item.itemId);
            root.transform.SetParent(parent, false);
            var visual = root.AddComponent<PotionBottleVisual>();
            Part(root.transform, "Smoked glass vial", _glassMesh, _glass);
            visual._contents = Part(root.transform, "Dim luminous draught", _liquidMesh, _liquid).transform;
            Color tint = item.iconColor;
            // Concentrated dark liquid; emission exposes the contents without lighting the hands.
            tint = Color.Lerp(new Color(.30f, .015f, .025f), tint, .25f);
            var properties = new MaterialPropertyBlock();
            properties.SetColor("_BaseColor", new Color(tint.r * .55f, tint.g * .55f, tint.b * .55f, 1f));
            properties.SetColor("_EmissionColor", new Color(tint.r * .75f, tint.g * .75f, tint.b * .75f, 1f));
            visual._contents.GetComponent<Renderer>().SetPropertyBlock(properties);
            visual._stopper = Part(root.transform, "Weathered cork", _corkMesh, _cork).transform;
            Part(root.transform, "Leather neck binding", _bandMesh, _leather);
            Part(root.transform, "Lower brass binding", _rimMesh, _brass).transform.localPosition = new Vector3(0, .151f, 0);
            Part(root.transform, "Upper brass binding", _rimMesh, _brass).transform.localPosition = new Vector3(0, .168f, 0);
            return root;
        }

        /// <summary>Normalized authoritative drink progress; animation never consumes an inventory item.</summary>
        public void SetDrinkProgress(float progress, bool drinking)
        {
            float p = Mathf.Clamp01(progress);
            if (_stopper != null)
            {
                _stopper.gameObject.SetActive(!drinking || p < .13f);
                _stopper.localPosition = drinking ? Vector3.up * (.045f * Mathf.SmoothStep(0, 1, p / .13f)) : Vector3.zero;
            }
            if (_contents != null)
            {
                float fill = drinking ? Mathf.Lerp(1f, .12f, Mathf.Clamp01((p - .18f) / .82f)) : 1f;
                _contents.localScale = new Vector3(1f, fill, 1f);
                _contents.localPosition = new Vector3(0, LiquidBottom * (1f - fill), 0);
            }
        }

        private static GameObject Part(Transform parent, string name, Mesh mesh, Material material)
        {
            var part = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            part.transform.SetParent(parent, false);
            part.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = part.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            return part;
        }

        private static void EnsureResources()
        {
            if (_glassMesh != null) return;
            // Radius/height profiles in metres: a beveled octagonal flask, tapered shoulders and thick lip.
            _glassMesh = Lathe("Potion glass", new[] {
                new Vector2(0,.004f), new Vector2(.028f,.004f), new Vector2(.036f,.014f),
                new Vector2(.039f,.028f), new Vector2(.038f,.105f), new Vector2(.034f,.122f),
                new Vector2(.018f,.145f), new Vector2(.016f,.174f), new Vector2(.021f,.178f),
                new Vector2(.021f,.184f), new Vector2(.014f,.184f), new Vector2(.013f,.173f)
            }, 12);
            _liquidMesh = Lathe("Potion liquid", new[] {
                new Vector2(0,LiquidBottom), new Vector2(.026f,LiquidBottom), new Vector2(.032f,.023f),
                new Vector2(.033f,.092f), new Vector2(.031f,.105f), new Vector2(0,.105f)
            }, 12);
            _corkMesh = Lathe("Potion cork", new[] {
                new Vector2(0,.173f), new Vector2(.013f,.173f), new Vector2(.015f,.193f),
                new Vector2(.013f,.198f), new Vector2(0,.198f)
            }, 10);
            _bandMesh = Lathe("Potion leather", new[] {
                new Vector2(.017f,.153f), new Vector2(.019f,.156f), new Vector2(.018f,.167f), new Vector2(.016f,.170f)
            }, 12);
            _rimMesh = Lathe("Potion brass", new[] {
                new Vector2(.016f,0), new Vector2(.0195f,.001f), new Vector2(.0195f,.003f), new Vector2(.016f,.004f)
            }, 12);
            // Resource materials retain transparent/emissive URP variants in standalone builds.
            Material glassAsset = Resources.Load<Material>("Emberdelve/PotionGlass");
            _glass = glassAsset != null ? new Material(glassAsset) : Surface("Potion smoked glass", new Color(.20f,.27f,.24f,.22f), .05f, .78f);
            _glass.SetFloat("_Surface", 1f);
            _glass.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            _glass.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            _glass.SetFloat("_ZWrite", 0f);
            _glass.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            _glass.SetOverrideTag("RenderType", "Transparent");
            _glass.renderQueue = (int)RenderQueue.Transparent;
            Material liquidAsset = Resources.Load<Material>("Emberdelve/PotionLiquid");
            _liquid = liquidAsset != null ? new Material(liquidAsset) : Surface("Potion low luminance liquid", new Color(.16f,.008f,.014f), .08f, .62f);
            _liquid.EnableKeyword("_EMISSION");
            _liquid.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            _cork = Surface("Potion worn cork", new Color(.19f,.12f,.065f), 0, .12f);
            _leather = Surface("Potion dark leather", new Color(.055f,.033f,.018f), 0, .2f);
            _brass = Surface("Potion aged brass", new Color(.27f,.18f,.065f), .7f, .36f);
        }

        private static Material Surface(string name, Color color, float metallic, float smoothness)
        {
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            return material;
        }

        private static Mesh Lathe(string name, Vector2[] profile, int sides)
        {
            var vertices = new Vector3[profile.Length * (sides + 1)];
            var uv = new Vector2[vertices.Length];
            var triangles = new int[(profile.Length - 1) * sides * 6];
            for (int row = 0; row < profile.Length; row++)
                for (int side = 0; side <= sides; side++)
                {
                    int index = row * (sides + 1) + side;
                    float angle = side * Mathf.PI * 2f / sides;
                    vertices[index] = new Vector3(Mathf.Cos(angle) * profile[row].x, profile[row].y, Mathf.Sin(angle) * profile[row].x);
                    uv[index] = new Vector2((float)side / sides, profile[row].y * 5f);
                    if (row == profile.Length - 1 || side == sides) continue;
                    int next = index + sides + 1;
                    int t = (row * sides + side) * 6;
                    triangles[t] = index; triangles[t + 1] = next; triangles[t + 2] = index + 1;
                    triangles[t + 3] = index + 1; triangles[t + 4] = next; triangles[t + 5] = next + 1;
                }
            var mesh = new Mesh { name = name, vertices = vertices, uv = uv, triangles = triangles };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
