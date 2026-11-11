using Emberdelve.Data;
using UnityEngine;
using UnityEngine.Rendering;

namespace Emberdelve.Player
{
    /// <summary>Shared linen supply model; progress only affects its presentation.</summary>
    public sealed class BandageVisual : MonoBehaviour
    {
        private static Material _linen, _edge, _core;
        private static Mesh _rings;
        private Transform _roll, _tail;

        public static GameObject Create(ItemData item, Transform parent)
        {
            EnsureResources();
            var root = new GameObject("Held bandage " + item.itemId);
            root.transform.SetParent(parent, false);
            var view = root.AddComponent<BandageVisual>();
            view._roll = new GameObject("Linen roll").transform;
            view._roll.SetParent(root.transform, false);
            view._roll.localPosition = new Vector3(0, .055f, 0);
            Part(view._roll, "Rolled linen", PrimitiveType.Cylinder, Vector3.zero,
                new Vector3(.085f,.055f,.085f), _linen, new Vector3(0,0,90));
            foreach (float side in new[] { -1f, 1f })
            {
                Part(view._roll, "Dark winding core", PrimitiveType.Cylinder, new Vector3(side * .0555f,0,0),
                    new Vector3(.018f,.0008f,.018f), _core, new Vector3(0,0,90));
                var rings = new GameObject("Layered linen end", typeof(MeshFilter), typeof(MeshRenderer));
                rings.transform.SetParent(view._roll, false);
                rings.transform.localPosition = new Vector3(side * .056f,0,0);
                rings.GetComponent<MeshFilter>().sharedMesh = _rings;
                Dress(rings.GetComponent<MeshRenderer>(), _edge);
            }
            view._tail = Part(root.transform, "Loose cloth end", PrimitiveType.Cube,
                new Vector3(0,.018f,.045f), new Vector3(.105f,.003f,.055f), _linen, Vector3.zero).transform;
            return root;
        }

        public void SetApplyProgress(float progress, bool applying)
        {
            float p = Mathf.Clamp01(progress);
            if (_roll != null)
                _roll.localRotation = applying ? Quaternion.Euler(p * 240f,0,0) : Quaternion.identity;
            if (_tail != null)
            {
                float length = applying ? Mathf.Lerp(.055f,.14f,p) : .055f;
                _tail.localScale = new Vector3(.105f,.003f,length);
                _tail.localPosition = new Vector3(0,.018f,.0175f + length * .5f);
            }
        }

        private static GameObject Part(Transform parent, string name, PrimitiveType type, Vector3 position,
            Vector3 scale, Material material, Vector3 rotation)
        {
            var part = GameObject.CreatePrimitive(type);
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localScale = scale;
            part.transform.localRotation = Quaternion.Euler(rotation);
            var collider = part.GetComponent<Collider>();
            collider.enabled = false;
            if (Application.isPlaying) Destroy(collider); else DestroyImmediate(collider);
            Dress(part.GetComponent<Renderer>(), material);
            return part;
        }

        private static void Dress(Renderer renderer, Material material)
        {
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;
        }

        private static Material Cloth(string name, Color color)
        {
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", .06f);
            material.SetFloat("_Metallic", 0);
            return material;
        }

        private static void EnsureResources()
        {
            if (_linen != null) return;
            _linen = Cloth("Aged linen", new Color(.51f,.44f,.32f));
            _edge = Cloth("Linen winding seams", new Color(.30f,.25f,.17f));
            _core = Cloth("Bandage hollow core", new Color(.055f,.041f,.025f));
            // Three narrow concentric seam ridges around the exposed ends of the rolled cloth.
            const int sides = 24, tubeSides = 4, ringCount = 3;
            var vertices = new Vector3[ringCount * sides * tubeSides];
            var triangles = new int[vertices.Length * 6];
            for (int ring = 0; ring < ringCount; ring++)
                for (int segment = 0; segment < sides; segment++)
                    for (int tube = 0; tube < tubeSides; tube++)
                    {
                        int i = (ring * sides + segment) * tubeSides + tube;
                        float angle = segment * Mathf.PI * 2 / sides;
                        float around = tube * Mathf.PI * 2 / tubeSides;
                        float radius = .016f + ring * .010f + Mathf.Cos(around) * .0007f;
                        vertices[i] = new Vector3(Mathf.Sin(around) * .0007f, Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
                        int next = (ring * sides + (segment + 1) % sides) * tubeSides + tube;
                        int across = (ring * sides + segment) * tubeSides + (tube + 1) % tubeSides;
                        int diagonal = (ring * sides + (segment + 1) % sides) * tubeSides + (tube + 1) % tubeSides;
                        int t = i * 6;
                        triangles[t] = i; triangles[t+1] = next; triangles[t+2] = across;
                        triangles[t+3] = next; triangles[t+4] = diagonal; triangles[t+5] = across;
                    }
            _rings = new Mesh { name = "Bandage winding ridges", vertices = vertices, triangles = triangles };
            _rings.RecalculateNormals();
            _rings.RecalculateBounds();
        }
    }
}
