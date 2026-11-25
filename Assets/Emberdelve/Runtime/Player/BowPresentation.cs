using UnityEngine;
using Emberdelve.Data;
namespace Emberdelve.Player
{
    // Presentation only: the authority still owns launch origin, charge and collisions.
    public sealed class BowPresentation : MonoBehaviour
    {
        const int Rings = BowSolidGeometry.Rings, Sides = BowSolidGeometry.Sides;
        Mesh _mesh;
        Vector3[] _vertices = new Vector3[Rings * Sides * 2];
        Transform _upperString, _lowerString, _arrow;
        public float Draw { get; private set; }
        // How far a full draw moves the nock in bow space: sideways and straight back. The defaults are the shape the avatars and
        // the skeleton archer's baked clip were made for; the first-person bow draws deeper and straight back (review 25 Nov 2026:
        // the default read as the string being pushed 15 cm to the side).
        public const float FirstPersonDrawSide = 0f, FirstPersonDrawDepth = .30f;
        float _drawSide = .15f, _drawDepth = .22f;
        public Vector3 Nock => new Vector3(.024f + _drawSide * Draw, .015f, -.16f - _drawDepth * Draw);
        /// <summary>
        /// Fractions of the draw by which the string hand has taken an arrow from the quiver and has set it on the string; the
        /// string is pulled over the rest. Timing after the reference recurve's 1.5 s draw section: its ammunition count drops
        /// at 0.11 s (the arrow is taken) and its second sound cue is at 0.38 s. The motion itself is ours.
        /// </summary>
        public const float ArrowTakenAt = .07f, ArrowNockedAt = .25f;
        /// <summary>How far the string is pulled at a draw progress (the first part of the draw is the nocking).</summary>
        public static float StringDraw(float progress) => Mathf.InverseLerp(ArrowNockedAt, 1f, Mathf.Clamp01(progress));
        Transform _bundle;
        /// <summary>Rotation (world) of an arrow lying on the string as drawn now.</summary>
        public Quaternion ArrowOnStringRotation => transform.rotation * Quaternion.LookRotation(new Vector3(.024f, .015f, .14f) - Nock, Vector3.up);
        /// <summary>The arrow in the string hand on its way to the string: its nock end and its direction (world).</summary>
        public void PlaceArrow(Vector3 nockEnd, Quaternion rotation) { _arrow.SetPositionAndRotation(nockEnd, rotation); _arrow.gameObject.SetActive(true); }
        public void ShowArrow(bool visible) { if (_arrow.gameObject.activeSelf != visible) _arrow.gameObject.SetActive(visible); }
        /// <summary>A handful of arrows carried in the string hand (a reload): nock ends and direction (world).</summary>
        public void PlaceBundle(Vector3 nockEnds, Quaternion rotation, bool visible)
        {
            if (_bundle == null) return;
            if (visible) _bundle.SetPositionAndRotation(nockEnds, rotation);
            if (_bundle.gameObject.activeSelf != visible) _bundle.gameObject.SetActive(visible);
        }
        public void SetDrawShape(float side, float depth) { _drawSide = side; _drawDepth = depth; SetDraw(Draw); }
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
            for (int i = 0; i < BowSolidGeometry.GripCount; i++)
                Part(root.transform, "LeatherWrap", BowSolidGeometry.GripCenter(i), BowSolidGeometry.GripSize, weapon.gripMaterial);
            bow._upperString = Part(root.transform, "UpperString", Vector3.zero, Vector3.one, weapon.gripMaterial);
            bow._lowerString = Part(root.transform, "LowerString", Vector3.zero, Vector3.one, weapon.gripMaterial);
            bow._arrow = new GameObject("NockedArrow").transform; bow._arrow.SetParent(root.transform, false);
            Part(bow._arrow, "Shaft", new Vector3(0, 0, .32f), new Vector3(.009f, .009f, .64f), weapon.shaftMaterial);
            Part(bow._arrow, "SteelTip", new Vector3(0, 0, .66f), new Vector3(.018f, .012f, .055f), weapon.edgeMaterial);
            Part(bow._arrow, "Feather", new Vector3(0, 0, .055f), new Vector3(.038f, .003f, .075f), weapon.gripMaterial);
            Part(bow._arrow, "FeatherCross", new Vector3(0, 0, .055f), new Vector3(.003f, .038f, .075f), weapon.gripMaterial);
            bow._bundle = new GameObject("ArrowBundle").transform; bow._bundle.SetParent(root.transform, false);
            for (int i = 0; i < 4; i++)
            {
                var one = new GameObject("Arrow" + i).transform; one.SetParent(bow._bundle, false);
                one.localPosition = new Vector3((i - 1.5f) * .013f, (i % 2) * .009f, 0f);
                one.localRotation = Quaternion.Euler((i - 1.5f) * 1.5f, (i - 1.5f) * 2.5f, 0f);
                Part(one, "Shaft", new Vector3(0, 0, .32f), new Vector3(.009f, .009f, .64f), weapon.shaftMaterial);
                Part(one, "SteelTip", new Vector3(0, 0, .66f), new Vector3(.018f, .012f, .055f), weapon.edgeMaterial);
                Part(one, "Feather", new Vector3(0, 0, .055f), new Vector3(.038f, .003f, .075f), weapon.gripMaterial);
            }
            foreach (var t in root.GetComponentsInChildren<Transform>()) t.gameObject.layer = parent.gameObject.layer;
            bow._bundle.gameObject.SetActive(false);
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
        Vector3 Curve(float t, int sign) => BowSolidGeometry.Curve(t, sign, Draw);
        public void SetDraw(float value)
        {
            Draw = Mathf.Clamp01(value);
            for (int side = 0; side < 2; side++)
                for (int r = 0; r < Rings; r++)
                {
                    for (int j = 0; j < Sides; j++)
                        _vertices[side * Rings * Sides + r * Sides + j] = BowSolidGeometry.RingVertex(side, r, j, Draw);
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
