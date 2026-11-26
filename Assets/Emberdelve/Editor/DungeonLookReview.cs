using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Emberdelve.Core;
using Emberdelve.Level;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace Emberdelve.EditorTools
{
    // Mirror-only batch review of how saved raid modules look with the game's own
    // environment: raid scene volume, the entry module's moon/ambient/fog, game FOV,
    // eye height, retro renderer features left on. Writes PNGs and luminance stats.
    public static class DungeonLookReview
    {
        const string Scene = "Assets/Emberdelve/Scenes/Emberdelve_ModularCrypts.unity";
        static readonly Vector3 Origin = new Vector3(3000, 0, 3000);
        const float Eye = 1.66f;
        static bool applyLook;

        public static void RenderBatch()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Use the isolated batch mirror");
            string outDir = Arg("-emberLookOut") ?? "Logs/review/dungeon-look";
            Directory.CreateDirectory(outDir);
            EditorSceneManager.OpenScene(Scene);
            // Mirror-only preview of unsaved look changes: rebuild the volume profile from
            // its builder and rebalance the instanced room lights before rendering.
            applyLook = Array.IndexOf(Environment.GetCommandLineArgs(), "-emberApplyLook") >= 0;
            if (applyLook) EmberdelveVisualSetup.EnsureDungeonVolumeProfile();
            var stats = new StringBuilder("view mean p50 p95 clipped>0.97 crushed<0.03 warmShare\n");
            for (int floor = 1; floor <= 3; floor++)
            {
                if (!RoyaleFloorCatalogs.TryLoad(floor, out var catalog, out var reason)) throw new InvalidOperationException(reason);
                foreach (var definition in catalog.Modules)
                    if (Arg("-emberLookOnly") == null || definition.name.Contains(Arg("-emberLookOnly")))
                    RenderModule(floor, catalog, definition, outDir, stats);
            }
            File.WriteAllText(Path.Combine(outDir, "stats.txt"), stats.ToString());
            Debug.Log("[DungeonLook] rendered to " + outDir + "\n" + stats);
        }

        static void RenderModule(int floor, DungeonModuleCatalog catalog, DungeonModuleDefinition definition, string outDir, StringBuilder stats)
        {
            var created = new List<GameObject>();
            try
            {
                var room = Object.Instantiate(definition.gameObject, Origin, Quaternion.identity);
                created.Add(room);
                if (applyLook) { DungeonLayoutLighting.Apply(room); DungeonLightPalette.ApplyToRoom(room); }
                var environment = room.GetComponentInChildren<MoonlitRuinsEnvironment>();
                if (environment == null)
                {
                    var source = catalog.Modules.FirstOrDefault(m => m.GetComponentInChildren<MoonlitRuinsEnvironment>() != null)
                        ?? Resources.Load<DungeonModuleCatalog>(RoyaleFloorCatalogs.ResourceFor(1)).Modules.First(m => m.GetComponentInChildren<MoonlitRuinsEnvironment>() != null);
                    var host = Object.Instantiate(source.gameObject, Origin + new Vector3(0, -400, 0), Quaternion.identity);
                    created.Add(host);
                    environment = host.GetComponentInChildren<MoonlitRuinsEnvironment>();
                }
                environment.Apply();
                if (float.TryParse(Arg("-emberAmbientBoost") ?? "", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float boost))
                    RenderSettings.ambientProbe = RenderSettings.ambientProbe * boost;
                Debug.Log("[DungeonLook] " + definition.name + " ambientMode=" + RenderSettings.ambientMode + " probeL0=" + RenderSettings.ambientProbe[0, 0] + "," + RenderSettings.ambientProbe[1, 0] + "," + RenderSettings.ambientProbe[2, 0] + " sun=" + (RenderSettings.sun ? RenderSettings.sun.name + " " + RenderSettings.sun.intensity + " en=" + RenderSettings.sun.enabled : "none"));
                var cameraObject = new GameObject("LookReviewCamera") { tag = "MainCamera" };
                created.Add(cameraObject);
                var camera = cameraObject.AddComponent<Camera>();
                var config = Resources.FindObjectsOfTypeAll<GameConfig>().FirstOrDefault();
                camera.fieldOfView = config != null ? config.fieldOfView : 68f;
                camera.nearClipPlane = .05f; camera.farClipPlane = 120f;
                camera.clearFlags = CameraClearFlags.Skybox;
                var urp = cameraObject.AddComponent<UniversalAdditionalCameraData>();
                urp.renderPostProcessing = true;
                Physics.SyncTransforms();

                string id = "f" + floor + "_" + definition.name;
                var views = new (string name, Vector3 from, Vector3 to)[]
                {
                    ("south_door", new Vector3(0, Eye, -16.5f), new Vector3(0, 1.4f, 4)),
                    ("center_nw", new Vector3(0, Eye, 0), new Vector3(-12, 1.2f, 10)),
                    ("center_se", new Vector3(0, Eye, 0), new Vector3(12, 1.2f, -10)),
                    ("east_door", new Vector3(16.5f, Eye, 0), new Vector3(-4, 1.4f, 0)),
                };
                foreach (var view in views)
                {
                    cameraObject.transform.position = Origin + view.from;
                    cameraObject.transform.LookAt(Origin + view.to);
                    string file = Path.Combine(outDir, id + "_" + view.name + ".png");
                    stats.Append(id + "_" + view.name + " " + Render(camera, file) + "\n");
                }
            }
            finally
            {
                foreach (var go in created) if (go != null) Object.DestroyImmediate(go);
            }
        }

        static string Render(Camera camera, string file)
        {
            const int w = 960, h = 540;
            var target = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = target;
            camera.Render();
            RenderTexture.active = target;
            var texture = new Texture2D(w, h, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            texture.Apply();
            File.WriteAllBytes(file, texture.EncodeToPNG());
            var pixels = texture.GetPixels();
            RenderTexture.active = null; camera.targetTexture = null;
            Object.DestroyImmediate(target); Object.DestroyImmediate(texture);
            var lum = pixels.Select(c => .2126f * c.r + .7152f * c.g + .0722f * c.b).OrderBy(v => v).ToArray();
            float warm = pixels.Count(c => c.r > c.b * 1.35f && c.r > .12f) / (float)pixels.Length;
            return string.Format("{0:0.000} {1:0.000} {2:0.000} {3:0.0000} {4:0.000} {5:0.000}",
                lum.Average(), lum[lum.Length / 2], lum[(int)(lum.Length * .95f)],
                lum.Count(v => v > .97f) / (float)lum.Length, lum.Count(v => v < .03f) / (float)lum.Length, warm);
        }

        static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
