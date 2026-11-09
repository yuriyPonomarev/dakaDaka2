using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Emberdelve.Combat
{
    /// <summary>
    /// Ground wedge that warns an incoming enemy swing. It is built from a generated mesh and a
    /// runtime material, so it needs no art assets, and it carries no collider and sits on
    /// Ignore Raycast, so it can never influence a combat query.
    ///
    /// The wedge is oriented along the direction the brain committed when the windup started, which is
    /// the same direction EnemyAgent's swept swing is aimed along. D1c4: its angular extent is the yaw range of the
    /// swing pattern the enemy really swings and its radius is that pattern's effective reach, so the ground
    /// projection of the sweep and the warning are one thing. What the player sees is what can land.
    /// </summary>
    public sealed class AttackTelegraphVisual : MonoBehaviour
    {
        private const int ArcSegments = 12;

        private MeshFilter _filter;
        private MeshRenderer _renderer;
        private Mesh _mesh;
        private Material _material;
        private Mesh _directionMesh;
        private MeshRenderer _directionRenderer;
        private Material _directionMaterial;
        private Color _baseColor = new Color(1f, 0.32f, 0.18f, 1f);

        /// <summary>Lowest blade yaw of the drawn wedge (degrees from the committed direction, positive to the right).</summary>
        public float MinYawDegrees { get; private set; }

        /// <summary>Highest blade yaw of the drawn wedge.</summary>
        public float MaxYawDegrees { get; private set; }

        /// <summary>Half of the drawn angular extent.</summary>
        public float HalfAngleDegrees => (MaxYawDegrees - MinYawDegrees) * 0.5f;

        /// <summary>Radius (metres) the wedge was last asked to draw at full growth (the length passed to <see cref="SetState"/>).</summary>
        public float LastLength { get; private set; }

        /// <summary>Legacy wedge: a symmetric cone of <paramref name="coneDegrees"/> (clamped to 40..200).</summary>
        public static AttackTelegraphVisual Create(Transform owner, float coneDegrees, Color color)
        {
            float half = Mathf.Clamp(coneDegrees, 40f, 200f) * 0.5f;
            return Create(owner, -half, half, color);
        }

        /// <summary>D1c4: the wedge between two blade yaws (the yaw range of the enemy's swing pattern).</summary>
        public static AttackTelegraphVisual Create(Transform owner, float minYawDegrees, float maxYawDegrees, Color color)
        {
            if (owner == null) return null;

            GameObject go = new GameObject("Attack_Telegraph");
            go.transform.SetParent(owner, false);
            go.transform.localPosition = new Vector3(0f, 0.07f, 0f);

            int ignoreLayer = LayerMask.NameToLayer("Ignore Raycast");
            if (ignoreLayer >= 0) go.layer = ignoreLayer;

            AttackTelegraphVisual visual = go.AddComponent<AttackTelegraphVisual>();
            visual.Configure(minYawDegrees, maxYawDegrees, color);
            visual.gameObject.SetActive(false);
            return visual;
        }

        private void Configure(float minYawDegrees, float maxYawDegrees, Color color)
        {
            _baseColor = color;
            MinYawDegrees = minYawDegrees;
            MaxYawDegrees = maxYawDegrees;

            _filter = gameObject.AddComponent<MeshFilter>();
            _renderer = gameObject.AddComponent<MeshRenderer>();
            _mesh = BuildWedge(minYawDegrees, maxYawDegrees);
            _filter.sharedMesh = _mesh;

            _material = CreateMaterial(_baseColor);
            if (_material != null) _renderer.sharedMaterial = _material;

            // The wedge communicates the danger area; a narrow arrow inside it makes the committed
            // attack direction legible at a glance, especially on wide horizontal swings.
            GameObject direction = new GameObject("Attack_Direction");
            direction.transform.SetParent(transform, false);
            MeshFilter directionFilter = direction.AddComponent<MeshFilter>();
            _directionRenderer = direction.AddComponent<MeshRenderer>();
            _directionMesh = BuildDirectionArrow();
            directionFilter.sharedMesh = _directionMesh;
            _directionMaterial = CreateMaterial(new Color(1f, 0.88f, 0.58f, 1f));
            if (_directionMaterial != null) _directionRenderer.sharedMaterial = _directionMaterial;

            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = LightProbeUsage.Off;
            _renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        }

        /// <summary>Drives the wedge from the enemy's windup state every frame.</summary>
        public void SetState(bool active, float progress, Vector3 flatDirection, float length, bool whiffed)
        {
            if (_renderer == null) return;

            LastLength = length;

            if (!active)
            {
                gameObject.SetActive(false);
                return;
            }

            gameObject.SetActive(true);

            Vector3 direction = new Vector3(flatDirection.x, 0f, flatDirection.z);
            if (direction.sqrMagnitude < 0.0001f)
            {
                direction = transform.parent != null
                    ? Flatten(transform.parent.forward)
                    : Vector3.forward;
            }

            Quaternion world = Quaternion.LookRotation(direction.normalized);

            // The wedge warns a world-space area, not a body-relative one: undo the parent's own rotation
            // so an enemy turning during its windup cannot drag the warning off the committed direction.
            transform.localRotation = transform.parent != null
                ? Quaternion.Inverse(transform.parent.rotation) * world
                : world;
            float t = Mathf.Clamp01(progress);
            float grow = whiffed ? 1f : Mathf.Lerp(0.3f, 1f, t);
            float scale = Mathf.Max(0.1f, Mathf.Max(0.2f, length) * grow);
            transform.localScale = new Vector3(scale, scale, scale);

            if (_material != null)
            {
                Color lit = whiffed
                    ? Color.Lerp(_baseColor * 0.18f, _baseColor * 0.4f, 0.5f)
                    : Color.Lerp(_baseColor * 0.22f, _baseColor, t);

                _material.color = lit;
                SetColor(_material, "_BaseColor", lit);
                SetColor(_material, "_Color", lit);
                SetColor(_material, "_EmissionColor", lit);
            }

            if (_directionMaterial != null)
            {
                Color arrow = Color.Lerp(new Color(0.7f, 0.45f, 0.2f, 1f), new Color(1f, 0.92f, 0.68f, 1f), t);
                SetColor(_directionMaterial, "_BaseColor", arrow);
                SetColor(_directionMaterial, "_Color", arrow);
                SetColor(_directionMaterial, "_EmissionColor", arrow);
            }
        }

        /// <summary>Unit wedge: apex at the origin, opening along +Z, radius 1, both facings so it reads from above and below.</summary>
        private static Mesh BuildWedge(float minYawDegrees, float maxYawDegrees)
        {

            List<Vector3> vertices = new List<Vector3>(ArcSegments + 2);
            List<int> triangles = new List<int>(ArcSegments * 6);
            List<Vector3> normals = new List<Vector3>(ArcSegments + 2);

            vertices.Add(Vector3.zero);
            normals.Add(Vector3.up);

            for (int i = 0; i <= ArcSegments; i++)
            {
                float angle = Mathf.Deg2Rad * Mathf.Lerp(minYawDegrees, maxYawDegrees, i / (float)ArcSegments);
                vertices.Add(new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)));
                normals.Add(Vector3.up);
            }

            for (int i = 0; i < ArcSegments; i++)
            {
                int apex = 0;
                int a = 1 + i;
                int b = 2 + i;

                triangles.Add(apex); triangles.Add(b); triangles.Add(a);
                triangles.Add(apex); triangles.Add(a); triangles.Add(b);
            }

            Mesh mesh = new Mesh
            {
                name = "Emberdelve_TelegraphWedge"
            };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh BuildDirectionArrow()
        {
            const float halfWidth = 0.018f;
            var mesh = new Mesh { name = "Emberdelve_TelegraphDirection" };
            mesh.vertices = new[]
            {
                new Vector3(-halfWidth, 0.004f, 0.08f), new Vector3(halfWidth, 0.004f, 0.08f),
                new Vector3(-halfWidth, 0.004f, 0.78f), new Vector3(halfWidth, 0.004f, 0.78f),
                new Vector3(-0.075f, 0.004f, 0.76f), new Vector3(0f, 0.004f, 0.98f), new Vector3(0.075f, 0.004f, 0.76f)
            };
            mesh.triangles = new[] { 0, 2, 1, 1, 2, 3, 4, 5, 6 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Material CreateMaterial(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Diffuse");
            if (shader == null) return null;

            Material material = new Material(shader)
            {
                name = "Emberdelve_AttackTelegraph",
                hideFlags = HideFlags.HideAndDontSave,
                color = color
            };

            SetColor(material, "_BaseColor", color);
            SetColor(material, "_Color", color);
            SetColor(material, "_EmissionColor", color);
            material.EnableKeyword("_EMISSION");

            // Show both facings so the warning is readable whatever the camera is doing.
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);

            return material;
        }

        private static void SetColor(Material material, string property, Color value)
        {
            if (material.HasProperty(property)) material.SetColor(property, value);
        }

        private static Vector3 Flatten(Vector3 direction)
        {
            Vector3 flat = new Vector3(direction.x, 0f, direction.z);
            return flat.sqrMagnitude < 0.0001f ? Vector3.forward : flat;
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
            if (_material != null) Destroy(_material);
            if (_directionMesh != null) Destroy(_directionMesh);
            if (_directionMaterial != null) Destroy(_directionMaterial);
        }
    }
}
