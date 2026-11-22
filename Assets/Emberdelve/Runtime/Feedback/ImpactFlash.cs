using System.Collections.Generic;
using UnityEngine;

namespace Emberdelve.Feedback
{
    /// <summary>
    /// Short-lived camera-facing contact spark. A transparent procedural quad replaces the old solid cube.
    /// </summary>
    public sealed class ImpactFlash : MonoBehaviour
    {
        private static readonly Dictionary<Color, Material> _materials = new Dictionary<Color, Material>();
        private static readonly Color ParryColor = new Color(1f, .74f, .26f);

        private float _age;
        private float _duration;
        private float _startSize;
        private float _widthScale = 1f;
        private Vector3 _velocity;
        private Renderer _renderer;

        public static void Spawn(Vector3 point, Color color, float size, float duration = 0.18f)
            => SpawnFlash(point, color, size, duration, Vector3.zero, 1f);

        public static void SpawnParry(Vector3 point, float size)
        {
            var camera = Camera.main;
            if (camera == null) return;
            Spawn(point, ParryColor, size * 1.15f, .22f);
            var plane = camera.transform;
            const int sparks = 5;
            for (int i = 0; i < sparks; i++)
            {
                float angle = (i + .25f) * (2f * Mathf.PI / sparks);
                Vector3 direction = plane.right * Mathf.Cos(angle) + plane.up * Mathf.Sin(angle);
                SpawnFlash(point, ParryColor, size * .48f, .23f, direction * (i % 2 == 0 ? 2.3f : 1.7f), .3f);
            }
        }

        private static void SpawnFlash(Vector3 point, Color color, float size, float duration, Vector3 velocity, float widthScale)
        {
            GameObject root = GameObject.CreatePrimitive(PrimitiveType.Quad);
            root.name = widthScale < 1f ? "ParrySpark" : "ImpactFlash";

            Collider collider = root.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);

            int ignoreLayer = LayerMask.NameToLayer("Ignore Raycast");
            if (ignoreLayer >= 0) root.layer = ignoreLayer;

            root.transform.position = point;

            Renderer renderer = root.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material = UnityEngine.Object.Instantiate(MaterialFor(color));
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
            ImpactFlash flash = root.AddComponent<ImpactFlash>();
            flash.Configure(duration, size, velocity, widthScale);
        }

        private void Configure(float duration, float size, Vector3 velocity, float widthScale)
        {
            _duration = Mathf.Max(0.03f, duration);
            _startSize = Mathf.Max(.02f, size);
            _velocity = velocity;
            _widthScale = widthScale;
            transform.localScale = new Vector3(_widthScale, 1f, 1f) * _startSize;
            _renderer = GetComponent<Renderer>();
            FaceCamera();
        }

        private void Update()
        {
            _age += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(_age / _duration);

            float scale = Mathf.Lerp(_startSize, _startSize * 2.4f, t);
            transform.localScale = new Vector3(_widthScale, 1f, 1f) * scale;
            transform.position += _velocity * Time.unscaledDeltaTime;

            if (_renderer != null)
            {
                Color color = _renderer.material.HasProperty("_BaseColor")
                    ? _renderer.material.GetColor("_BaseColor")
                    : Color.white;
                color.a = 1f - t;
                _renderer.material.SetColor("_BaseColor", color);
            }

            if (t >= 1f) Destroy(gameObject);
        }

        private void LateUpdate() => FaceCamera();

        private void FaceCamera()
        {
            var camera = Camera.main;
            if (camera == null) return;
            var forward = camera.transform.forward;
            var screenVelocity = Vector3.ProjectOnPlane(_velocity, forward);
            transform.rotation = screenVelocity.sqrMagnitude > .001f
                ? Quaternion.LookRotation(forward, screenVelocity) : camera.transform.rotation;
        }

        private void OnDestroy()
        {
            if (_renderer != null && _renderer.material != null) Destroy(_renderer.material);
        }

        private static Material MaterialFor(Color color)
        {
            if (_materials.TryGetValue(color, out Material existing) && existing != null) return existing;

            // Resources inclusion retains this tiny URP pass in players; Shader.Find-only fallbacks can be stripped.
            Shader shader = Resources.Load<Shader>("Emberdelve/ImpactFlash");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Sprites/Default");

            Material material = new Material(shader);
            material.hideFlags = HideFlags.HideAndDontSave;

            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);

            _materials[color] = material;
            return material;
        }
    }
}
