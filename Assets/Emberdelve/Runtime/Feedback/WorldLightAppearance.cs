using UnityEngine;
using UnityEngine.Rendering;

namespace Emberdelve.Feedback
{
    // Small, depth-occluded scattering around a physical flame; never a camera-following fill.
    public static class WorldLightAppearance
    {
        private static Material _hazeMaterial;

        public static void Configure(Light light)
        {
            if (light == null || light.type == LightType.Directional) return;
            if (Application.isPlaying && light.GetComponent<FlameAudio>() == null) light.gameObject.AddComponent<FlameAudio>();
            // Appearance/interaction must preserve the authored light budget and reach.
            // Decorative braziers intentionally have no shadows; torches opt into them.
            var flicker = light.GetComponent<TorchFlicker>();
            if (flicker != null) flicker.SetSourceIntensity(light.intensity);
            if (light.transform.Find("LocalLightHaze") != null) return;
            Shader shader = Resources.Load<Shader>("Emberdelve/LocalLightHaze");
            if (shader == null) return;
            if (_hazeMaterial == null) _hazeMaterial = new Material(shader) { name = "Local flame scattering" };
            GameObject haze = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            haze.name = "LocalLightHaze";
            haze.transform.SetParent(light.transform, false);
            haze.transform.localScale = Vector3.one * 1.4f;
            Collider collider = haze.GetComponent<Collider>();
            collider.enabled = false;
            if (Application.isPlaying) Object.Destroy(collider); else Object.DestroyImmediate(collider);
            var renderer = haze.GetComponent<Renderer>();
            renderer.sharedMaterial = _hazeMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            var properties = new MaterialPropertyBlock();
            properties.SetColor("_Tint", light.color * .045f);
            renderer.SetPropertyBlock(properties);
        }
    }
}
