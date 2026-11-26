using UnityEngine;
using UnityEngine.Rendering;

namespace Emberdelve.Level
{
    // Authored on the single entry module, so every shared layout receives the same night.
    // Dark but readable: a dim, desaturated cool fill so masonry silhouettes stay visible
    // between the warm fire pools instead of collapsing to black or to a blue cast.
    public sealed class MoonlitRuinsEnvironment : MonoBehaviour
    {
        public const string SceneMoonName = "Moon_Slit_Directional";
        public static readonly Color AmbientSky = new Color(.12f, .125f, .19f);
        public static readonly Color AmbientEquator = new Color(.075f, .078f, .108f);
        public static readonly Color AmbientGround = new Color(.038f, .036f, .046f);
        public static readonly Color FogColor = new Color(.045f, .05f, .062f);
        public const float FogDensity = .024f;
        public static readonly Color MoonColor = new Color(.58f, .62f, .90f);
        public const float MoonIntensity = .45f;

        public Material sky;
        public Light moon;
        private void OnEnable() { RenderPipelineManager.beginCameraRendering+=PrepareCamera; }
        private void OnDisable() { RenderPipelineManager.beginCameraRendering-=PrepareCamera; }
        private void PrepareCamera(ScriptableRenderContext context,Camera camera)
        {
            // Network-owned cameras can spawn after the entry module's Start.
            if(camera!=null && camera.CompareTag("MainCamera")) camera.clearFlags=CameraClearFlags.Skybox;
        }
        private void Start() { Apply(); }
        public void Apply()
        {
            // The saved entry module carries no moon of its own; adopt the scene's moon slit
            // instead of switching every directional light off (which left only ambient).
            Light active=moon;
            if(active==null)
            {
                var named=GameObject.Find(SceneMoonName);
                if(named!=null) active=named.GetComponent<Light>();
            }
            foreach(var light in FindObjectsByType<Light>(FindObjectsSortMode.None))
                if(light != active && light.type == LightType.Directional) light.enabled=false;
            RenderSettings.skybox=sky;
            RenderSettings.ambientMode=AmbientMode.Trilight;
            RenderSettings.ambientSkyColor=AmbientSky;
            RenderSettings.ambientEquatorColor=AmbientEquator;
            RenderSettings.ambientGroundColor=AmbientGround;
            RenderSettings.fog=true;
            RenderSettings.fogMode=FogMode.Exponential;
            RenderSettings.fogColor=FogColor;
            RenderSettings.fogDensity=FogDensity;
            // Changing ambient colours at runtime does not refresh the ambient probe that
            // URP shades with (the scene's baked probe stays), so set the probe directly:
            // a flat fill weighted like the trilight gradient.
            var probe=new SphericalHarmonicsL2();
            probe.AddAmbientLight(AmbientSky*.25f+AmbientEquator*.5f+AmbientGround*.25f);
            // Unity keeps its stored probe unless the mode is Custom; only then is ours used.
            RenderSettings.ambientMode=AmbientMode.Custom;
            RenderSettings.ambientProbe=probe;
            if(active!=null)
            {
                active.enabled=true;
                active.color=MoonColor;
                active.intensity=MoonIntensity;
                active.shadows=LightShadows.Soft;
                active.shadowStrength=.8f;
                active.transform.rotation=Quaternion.Euler(48,32,0);
                RenderSettings.sun=active;
                if(sky!=null) sky.SetVector("_MoonDirection",-active.transform.forward);
            }
            foreach(var camera in FindObjectsByType<Camera>(FindObjectsSortMode.None))
                if(camera.CompareTag("MainCamera")) camera.clearFlags=CameraClearFlags.Skybox;
        }
    }
}
