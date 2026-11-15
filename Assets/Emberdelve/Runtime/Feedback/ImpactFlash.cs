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

        private float _age;
        private float _duration;
        private float _startSize;
        private Renderer _renderer;

        public static void Spawn(Vector3 point, Color color, float size, float duration = 0.18f)
        {
            GameObject root = GameObject.CreatePrimitive(PrimitiveType.Quad);
            root.name = "ImpactFlash";

            Collider collider = root.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);

            int ignoreLayer = LayerMask.NameToLayer("Ignore Raycast");
            if (ignoreLayer >= 0) root.layer = ignoreLayer;

            root.transform.position = point;
            root.transform.localScale = Vector3.one * Mathf.Max(0.02f, size);

            Renderer renderer = root.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material = UnityEngine.Object.Instantiate(MaterialFor(color));
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
            if (Camera.main != null) root.transform.rotation = Camera.main.transform.rotation;

            ImpactFlash flash = root.AddComponent<ImpactFlash>();
            flash.Configure(duration, size);
        }

        private void Configure(float duration, float size)
        {
            _duration = Mathf.Max(0.03f, duration);
            _startSize = size;
            _renderer = GetComponent<Renderer>();
        }

        private void Update()
        {
            _age += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(_age / _duration);

            float scale = Mathf.Lerp(_startSize, _startSize * 2.4f, t);
            transform.localScale = Vector3.one * scale;

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

        private void LateUpdate()
        {
            if (Camera.main != null) transform.rotation = Camera.main.transform.rotation;
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
