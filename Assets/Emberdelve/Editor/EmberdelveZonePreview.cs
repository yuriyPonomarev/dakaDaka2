using System;
using System.IO;
using Emberdelve.Flow;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace Emberdelve.EditorTools
{
    public static class EmberdelveZonePreview
    {
        public static void Run()
        {
            if (!Application.isBatchMode || !Application.dataPath.Replace("\\", "/").EndsWith("/DakaDaka_ci/Assets", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Preview runs only on the CI mirror in batch mode");
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-ember-zone-preview-dir");
            if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Missing preview output directory");
            string output = args[index + 1]; Directory.CreateDirectory(output);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SceneManager.SetActiveScene(scene);
            Material ground = null;
            try
            {
                ground = new Material(Shader.Find("Universal Render Pipeline/Unlit")); ground.SetColor("_BaseColor", new Color(.18f,.15f,.12f));
                var floor = GameObject.CreatePrimitive(PrimitiveType.Cube); floor.transform.position = new Vector3(0,-.3f,0);
                floor.transform.localScale = new Vector3(40,.5f,40); floor.GetComponent<Renderer>().sharedMaterial = ground;
                var obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube); obstacle.transform.position = new Vector3(-1.5f,1.2f,9);
                obstacle.transform.localScale = new Vector3(1,2.4f,1); obstacle.GetComponent<Renderer>().sharedMaterial = ground;
                var boundary = new GameObject("Zone").AddComponent<RoyaleZoneBoundary>();
                if (!boundary.Apply(Vector2.zero, 10, 1.7f)) throw new Exception("Boundary material unavailable");
                if (ShaderUtil.ShaderHasError(Resources.Load<Shader>("Emberdelve/RoyaleZoneBoundary"))) throw new Exception("Zone shader compilation failed");
                if (boundary.GetComponent<Collider>() != null) throw new Exception("Visual boundary must not collide");
                var camera = new GameObject("PreviewCamera").AddComponent<Camera>(); camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.025f,.03f,.04f); camera.nearClipPlane = .05f; camera.farClipPlane = 50;
                Capture(camera, new Vector3(0,1.7f,7), Vector3.forward, Path.Combine(output,"inside.png"));
                Capture(camera, new Vector3(0,1.7f,13), Vector3.back, Path.Combine(output,"outside.png"));
                boundary.Hide();
                Capture(camera, new Vector3(0,1.7f,7), Vector3.forward, Path.Combine(output,"hidden.png"));
                Debug.Log("ZONE_PREVIEW_OK " + output);
            }
            finally
            {
                foreach (var root in scene.GetRootGameObjects()) UnityEngine.Object.DestroyImmediate(root);
                if (ground != null) UnityEngine.Object.DestroyImmediate(ground);
            }
        }
        private static void Capture(Camera camera, Vector3 position, Vector3 direction, string path)
        {
            camera.transform.SetPositionAndRotation(position,Quaternion.LookRotation(direction));
            var target = new RenderTexture(960,540,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);
            var old = RenderTexture.active; var texture = new Texture2D(960,540,TextureFormat.RGB24,false);
            try
            {
                camera.targetTexture = target; camera.Render(); RenderTexture.active = target;
                texture.ReadPixels(new Rect(0,0,960,540),0,0); texture.Apply(); File.WriteAllBytes(path,texture.EncodeToPNG());
            }
            finally { camera.targetTexture = null; RenderTexture.active = old; UnityEngine.Object.DestroyImmediate(target); UnityEngine.Object.DestroyImmediate(texture); }
        }
    }
}
