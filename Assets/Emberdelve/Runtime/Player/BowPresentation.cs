using UnityEngine;
using Emberdelve.Data;
namespace Emberdelve.Player
{
    // Presentation only: the authority still owns launch origin, charge and collisions.
    public sealed class BowPresentation : MonoBehaviour
    {
        const int Rings = 17, Sides = 6;
        Mesh _mesh;
        Vector3[] _vertices = new Vector3[Rings * Sides * 2];
        Transform _upperString, _lowerString, _arrow;
        public float Draw { get; private set; }
        public Vector3 Nock => new Vector3(.024f + .15f * Draw, .015f, -.16f - .22f * Draw);
        public static BowPresentation Build(Transform parent, WeaponData weapon)
        {
            var root = new GameObject("CarvedRecurveBow");
            root.transform.SetParent(parent, false);
            var bow = root.AddComponent<BowPresentation>();
            bow._mesh = new Mesh { name = "RecurveLimbs" };
            var triangles = new int[2 * (Rings - 1) * Sides * 6];
            int k = 0;
            for (int side = 0; side < 2; side++)
                for (int r = 0; r < Rings - 1; r++)
                    for (int j = 0; j < Sides; j++)
                    {
                        int a = side * Rings * Sides + r * Sides + j;
                        int b = side * Rings * Sides + r * Sides + (j + 1) % Sides;
                        triangles[k++] = a; triangles[k++] = b; triangles[k++] = a + Sides;
                        triangles[k++] = b; triangles[k++] = b + Sides; triangles[k++] = a + Sides;
                    }
            for (int i = 0; i < triangles.Length / 2; i += 3)
            { int swap = triangles[i]; triangles[i] = triangles[i + 1]; triangles[i + 1] = swap; }
            var uv = new Vector2[bow._vertices.Length];
            for (int i = 0; i < uv.Length; i++) uv[i] = new Vector2((i % Sides) / (float)Sides, (i / Sides % Rings) / (float)(Rings - 1));
            bow._mesh.vertices = bow._vertices; bow._mesh.uv = uv; bow._mesh.triangles = triangles;
            root.AddComponent<MeshFilter>().sharedMesh = bow._mesh;
            root.AddComponent<MeshRenderer>().sharedMaterial = weapon.shaftMaterial;
            for (int i = 0; i < 9; i++)
                Part(root.transform, "LeatherWrap", new Vector3(0, -.065f + i * .016f, 0), new Vector3(.054f, .014f, .07f), weapon.gripMaterial);
            bow._upperString = Part(root.transform, "UpperString", Vector3.zero, Vector3.one, weapon.gripMaterial);
            bow._lowerString = Part(root.transform, "LowerString", Vector3.zero, Vector3.one, weapon.gripMaterial);
            bow._arrow = new GameObject("NockedArrow").transform; bow._arrow.SetParent(root.transform, false);
            Part(bow._arrow, "Shaft", new Vector3(0, 0, .32f), new Vector3(.009f, .009f, .64f), weapon.shaftMaterial);
            Part(bow._arrow, "SteelTip", new Vector3(0, 0, .66f), new Vector3(.018f, .012f, .055f), weapon.edgeMaterial);
            Part(bow._arrow, "Feather", new Vector3(0, 0, .055f), new Vector3(.038f, .003f, .075f), weapon.gripMaterial);
            Part(bow._arrow, "FeatherCross", new Vector3(0, 0, .055f), new Vector3(.003f, .038f, .075f), weapon.gripMaterial);
            foreach (var t in root.GetComponentsInChildren<Transform>()) t.gameObject.layer = parent.gameObject.layer;
            bow.SetDraw(0); return bow;
        }
        static Transform Part(Transform root, string name, Vector3 position, Vector3 size, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = name;
            go.transform.SetParent(root, false); go.transform.localPosition = position; go.transform.localScale = size;
            var c = go.GetComponent<Collider>(); c.enabled = false;
            if (Application.isPlaying) Destroy(c); else DestroyImmediate(c);
            go.GetComponent<Renderer>().sharedMaterial = material; return go.transform;
        }
        Vector3 Curve(float t, int sign)
        {
            return new Vector3(0, sign * (.075f + .48f * t - .035f * Draw * t),
                .12f * Mathf.Sin(t * Mathf.PI) - .055f * t - .085f * Draw * t * t);
        }
        public void SetDraw(float value)
        {
            Draw = Mathf.Clamp01(value);
            for (int side = 0; side < 2; side++)
                for (int r = 0; r < Rings; r++)
                {
                    float t = (float)r / (Rings - 1); var center = Curve(t, side == 0 ? 1 : -1);
                    for (int j = 0; j < Sides; j++)
                    {
                        float angle = j * Mathf.PI * 2 / Sides;
                        _vertices[side * Rings * Sides + r * Sides + j] = center + new Vector3(Mathf.Cos(angle) * Mathf.Lerp(.025f, .008f, t), 0, Mathf.Sin(angle) * Mathf.Lerp(.028f, .012f, t));
                    }
                }
            _mesh.vertices = _vertices; _mesh.RecalculateNormals(); _mesh.RecalculateBounds();
            Segment(_upperString, Curve(1, 1), Nock); Segment(_lowerString, Curve(1, -1), Nock);
            _arrow.localPosition = Nock;
            _arrow.localRotation = Quaternion.LookRotation(new Vector3(.024f, .015f, .14f) - Nock, Vector3.up);
            _arrow.gameObject.SetActive(Draw > .01f);
        }
        static void Segment(Transform part, Vector3 a, Vector3 b)
        {
            part.localPosition = (a + b) * .5f;
            part.localRotation = Quaternion.FromToRotation(Vector3.up, b - a);
            part.localScale = new Vector3(.003f, Vector3.Distance(a, b), .003f);
        }
        void OnDestroy() { if (_mesh != null) { if (Application.isPlaying) Destroy(_mesh); else DestroyImmediate(_mesh); } }
    }
}
