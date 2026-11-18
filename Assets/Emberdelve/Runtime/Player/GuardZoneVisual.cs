using System.Collections.Generic;
using UnityEngine;

namespace Emberdelve.Player
{
    /// <summary>
    /// Owner-only blue overlay of the surface that really blocks: the raised blade, shield, bow or crossbow volume the combat
    /// authority sweeps incoming hits against (the same shape objects, so what is drawn is what can stop a hit). It shows only
    /// while the guard is up, carries no collider and never influences a combat query.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GuardZoneVisual : MonoBehaviour
    {
        private static readonly Color ZoneColor = new Color(0.25f, 0.55f, 1f, 0.42f);
        private const float Inflate = 0.012f;

        private PlayerRig _rig;
        private Mesh _cube;
        private Mesh _shieldMesh;
        private Material _material;

        private void Awake()
        {
            _rig = GetComponent<PlayerRig>();
            if (Application.isBatchMode) enabled = false;
        }

        private void LateUpdate()
        {
            if (_rig == null || !_rig.IsLocallyControlled || !_rig.IsGuarding || _rig.IsDead) return;
            if (!EnsureAssets()) return;

            if (_rig.TryGetGuardShield(out GuardShieldShape shield))
            {
                Draw(_shieldMesh, Matrix4x4.TRS(shield.Position, shield.Rotation, Vector3.one * shield.Scale));
            }
            else if (_rig.TryGetCrossbowGuard(out CrossbowGuardShape crossbow))
            {
                for (int i = 0; i < CrossbowGuardPose.PartCount; i++)
                {
                    CrossbowGuardPart part = CrossbowGuardPose.Part(i);
                    DrawBox(crossbow.Position + crossbow.Rotation * part.Center, crossbow.Rotation * part.Rotation, part.Size);
                }
            }
            else if (_rig.TryGetBowGuard(out BowGuardShape bow))
            {
                for (int i = 0; i < BowGuardPose.PartCount; i++)
                {
                    BowGuardPart part = BowGuardPose.Part(i);
                    DrawBox(bow.Position + bow.Rotation * part.Center, bow.Rotation, part.Size);
                }
            }
            else if (_rig.TryGetGuardBlade(out GuardBladeBox blade))
            {
                DrawBox(blade.Center, blade.Rotation, blade.HalfExtents * 2f);
            }
        }

        private void DrawBox(Vector3 center, Quaternion rotation, Vector3 size) =>
            Draw(_cube, Matrix4x4.TRS(center, rotation, size + Vector3.one * Inflate));

        private void Draw(Mesh mesh, Matrix4x4 matrix) =>
            Graphics.DrawMesh(mesh, matrix, _material, 0, null, 0, null, false, false);

        private bool EnsureAssets()
        {
            if (_material == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Sprites/Default");
                if (shader == null) return false;
                _material = new Material(shader) { name = "Emberdelve_GuardZone", hideFlags = HideFlags.HideAndDontSave };
                _material.SetColor("_BaseColor", ZoneColor);
                _material.SetColor("_Color", ZoneColor);
                // Transparent, double-sided, no depth write, drawn after opaque geometry.
                if (_material.HasProperty("_Surface")) _material.SetFloat("_Surface", 1f);
                if (_material.HasProperty("_Blend")) _material.SetFloat("_Blend", 0f);
                if (_material.HasProperty("_SrcBlend")) _material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                if (_material.HasProperty("_DstBlend")) _material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                if (_material.HasProperty("_ZWrite")) _material.SetFloat("_ZWrite", 0f);
                if (_material.HasProperty("_Cull")) _material.SetFloat("_Cull", 0f);
                _material.SetOverrideTag("RenderType", "Transparent");
                _material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                _material.renderQueue = 3100;
            }
            if (_cube == null) _cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            if (_shieldMesh == null) _shieldMesh = BuildShieldMesh();
            return _cube != null && _shieldMesh != null;
        }

        /// <summary>The shield's convex trim outline as a thin double-faced plate in the shield's own space (scaled by the shape's Scale).</summary>
        private static Mesh BuildShieldMesh()
        {
            IReadOnlyList<Vector2> outline = GuardShieldShape.OutlinePoints;
            if (outline == null || outline.Count < 3) return null;
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            Vector2 centroid = Vector2.zero;
            for (int i = 0; i < outline.Count; i++) centroid += outline[i];
            centroid /= outline.Count;
            float z = (GuardShieldShape.Front + GuardShieldShape.Back) * 0.5f;
            float half = (GuardShieldShape.Front - GuardShieldShape.Back) * 0.5f + 0.004f;
            for (int layer = 0; layer < 2; layer++)
            {
                float lz = layer == 0 ? z + half : z - half;
                int centre = vertices.Count;
                vertices.Add(new Vector3(centroid.x, centroid.y, lz));
                for (int i = 0; i < outline.Count; i++) vertices.Add(new Vector3(outline[i].x, outline[i].y, lz));
                for (int i = 0; i < outline.Count; i++)
                {
                    int a = centre + 1 + i, b = centre + 1 + (i + 1) % outline.Count;
                    triangles.Add(centre); triangles.Add(a); triangles.Add(b);
                }
            }
            var mesh = new Mesh { name = "Emberdelve_GuardShieldZone", hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private void OnDestroy()
        {
            if (_material != null) Destroy(_material);
            if (_shieldMesh != null) Destroy(_shieldMesh);
        }
    }
}
