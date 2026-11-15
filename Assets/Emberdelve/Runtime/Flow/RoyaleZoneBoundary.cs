using UnityEngine;
using UnityEngine.Rendering;

namespace Emberdelve.Flow
{
    /// <summary>Local visual only. No collider, simulation, damage or shared material mutation.</summary>
    public sealed class RoyaleZoneBoundary : MonoBehaviour
    {
        public const int Segments = 128;
        private Mesh _mesh;
        private Material _material;
        private MeshRenderer _renderer;
        public bool IsVisible => _renderer != null && _renderer.enabled;
        public Mesh BoundaryMesh => _mesh;
        public void Hide() { if (_renderer != null) _renderer.enabled = false; }
        public bool Apply(Vector2 center, float radius, float viewerHeight)
        {
            if (!float.IsFinite(center.x) || !float.IsFinite(center.y) || !float.IsFinite(radius)
                || !float.IsFinite(viewerHeight) || radius <= 0 || radius > 100000)
            { Hide(); return false; }
            if (_renderer == null)
            {
                var shader = Resources.Load<Shader>("Emberdelve/RoyaleZoneBoundary");
                if (shader == null) return false;
                _material = new Material(shader) { name = "Local Royale Zone", hideFlags = HideFlags.DontSave };
                _mesh = CreateMesh();
                gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
                _renderer = gameObject.AddComponent<MeshRenderer>(); _renderer.sharedMaterial = _material;
                _renderer.shadowCastingMode = ShadowCastingMode.Off; _renderer.receiveShadows = false;
                _renderer.lightProbeUsage = LightProbeUsage.Off; _renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }
            transform.position = new Vector3(center.x, viewerHeight, center.y);
            transform.localScale = new Vector3(radius, 24, radius);
            _renderer.enabled = true; return true;
        }
        public static Mesh CreateMesh()
        {
            var vertices = new Vector3[(Segments + 1) * 2];
            var uv = new Vector2[vertices.Length]; var indices = new int[Segments * 6];
            for (int i = 0; i <= Segments; i++)
            {
                float t = i / (float)Segments, a = t * Mathf.PI * 2;
                vertices[i * 2] = new Vector3(Mathf.Cos(a), -.5f, Mathf.Sin(a));
                vertices[i * 2 + 1] = new Vector3(Mathf.Cos(a), .5f, Mathf.Sin(a));
                uv[i * 2] = new Vector2(t, 0); uv[i * 2 + 1] = new Vector2(t, 1);
                if (i == Segments) continue;
                int n = i * 6, v = i * 2;
                indices[n] = v; indices[n + 1] = v + 1; indices[n + 2] = v + 2;
                indices[n + 3] = v + 2; indices[n + 4] = v + 1; indices[n + 5] = v + 3;
            }
            var mesh = new Mesh { name = "Royale Zone Curtain", hideFlags = HideFlags.DontSave };
            mesh.vertices = vertices; mesh.uv = uv; mesh.triangles = indices; mesh.RecalculateBounds(); return mesh;
        }
        private void OnDisable() => Hide();
        private void OnDestroy()
        {
            if (Application.isPlaying) { if (_mesh != null) Destroy(_mesh); if (_material != null) Destroy(_material); }
            else { if (_mesh != null) DestroyImmediate(_mesh); if (_material != null) DestroyImmediate(_material); }
        }
    }
}
