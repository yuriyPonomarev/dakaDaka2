using UnityEngine;
using UnityEngine.Rendering;

namespace Emberdelve.Level
{
    // Authored on the single entry module, so every shared layout receives the same night.
    public sealed class MoonlitRuinsEnvironment : MonoBehaviour
    {
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
            foreach(var light in FindObjectsByType<Light>(FindObjectsSortMode.None))
                if(light != moon && light.type == LightType.Directional) light.enabled=false;
            RenderSettings.skybox=sky;
            RenderSettings.ambientMode=AmbientMode.Trilight;
            RenderSettings.ambientSkyColor=new Color(.10f,.14f,.22f);
            RenderSettings.ambientEquatorColor=new Color(.028f,.04f,.065f);
            RenderSettings.ambientGroundColor=new Color(.009f,.012f,.02f);
            RenderSettings.fog=true;
            RenderSettings.fogMode=FogMode.Exponential;
            RenderSettings.fogColor=new Color(.035f,.055f,.095f);
            RenderSettings.fogDensity=.008f;
            if(moon!=null)
            {
                moon.transform.rotation=Quaternion.Euler(48,32,0);
                RenderSettings.sun=moon;
                if(sky!=null) sky.SetVector("_MoonDirection",-moon.transform.forward);
            }
            foreach(var camera in FindObjectsByType<Camera>(FindObjectsSortMode.None))
                if(camera.CompareTag("MainCamera")) camera.clearFlags=CameraClearFlags.Skybox;
        }
    }
}
