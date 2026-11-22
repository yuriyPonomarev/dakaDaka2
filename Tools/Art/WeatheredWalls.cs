using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Invoked through Unity CLI script run; never changes scenes or enters Play.
public static class WeatheredWalls
{
    const string Folder = "Assets/Emberdelve/Art/Generated/RuinsWalls";
    const string Data = "SourceArt/Environment/WeatheredWalls";
    [Serializable] public class Surface { public Vector3[] vertices; public Vector3[] normals; public Vector2[] uv; public int[] triangles; }
    public static void DisableBotsForCurrentInspection()
    {
        if (!EditorApplication.isPlaying) { Debug.Log("No active Play session; inspection runner disables bots on entry."); return; }
        int count = 0;
        foreach (var bot in UnityEngine.Object.FindObjectsByType<Emberdelve.AI.RoyaleBotController>(FindObjectsSortMode.None))
        { bot.gameObject.SetActive(false); count++; }
        foreach (var enemy in UnityEngine.Object.FindObjectsByType<Emberdelve.AI.EnemyAgent>(FindObjectsSortMode.None))
        { enemy.gameObject.SetActive(false); count++; }
        Debug.Log("Temporarily disabled " + count + " AI actors in current inspection session; prefab/spawn settings unchanged.");
    }
    public static void RefreshImportedStone()
    {
        if (!EditorApplication.ExecuteMenuItem("Emberdelve/Visual/Soften Downloaded Stone Surfaces"))
            throw new InvalidOperationException("Stone refresh menu unavailable");
    }
    public static void Export()
    {
        Directory.CreateDirectory(Data);
        foreach (string path in Directory.GetFiles(Folder, "FoldedMasonry_*.asset"))
        {
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            File.WriteAllText(Data + "/" + mesh.name + ".source.json", JsonUtility.ToJson(new Surface {
                vertices = mesh.vertices, normals = mesh.normals, uv = mesh.uv, triangles = mesh.triangles }));
        }
    }
    public static void Import()
    {
        foreach (string path in Directory.GetFiles(Data, "*.bevel.json"))
        {
            var surface = JsonUtility.FromJson<Surface>(File.ReadAllText(path));
            string name = Path.GetFileName(path).Replace(".bevel.json", "").Replace("FoldedMasonry", "WeatheredMasonry");
            var mesh = new Mesh { name = name, vertices = surface.vertices, normals = surface.normals, uv = surface.uv, triangles = surface.triangles };
            var source = AssetDatabase.LoadAssetAtPath<Mesh>(Folder + "/" + name.Replace("WeatheredMasonry", "FoldedMasonry") + ".asset");
            var exported = JsonUtility.FromJson<Surface>(File.ReadAllText(Data + "/" + name.Replace("WeatheredMasonry", "FoldedMasonry") + ".source.json"));
            if (!source.vertices.SequenceEqual(exported.vertices) || !source.triangles.SequenceEqual(exported.triangles))
                throw new InvalidOperationException("Source profile changed; rerun Export and Blender before Import: " + source.name);
            mesh.name += "_" + GeometryFingerprint(source);
            mesh.RecalculateBounds(); mesh.RecalculateTangents();
            string target = Folder + "/" + name + ".asset";
            var saved = AssetDatabase.LoadAssetAtPath<Mesh>(target);
            if (saved == null) AssetDatabase.CreateAsset(mesh, target);
            else { EditorUtility.CopySerialized(mesh, saved); UnityEngine.Object.DestroyImmediate(mesh); AssetDatabase.SaveAssetIfDirty(saved); }
            Debug.Log(name + " triangles=" + surface.triangles.Length / 3);
        }
        int changed = 0, surfaces = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Emberdelve/Prefabs/DungeonModules" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                int count = 0;
                foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
                {
                    string original = AssetDatabase.GetAssetPath(filter.sharedMesh);
                    if (!original.StartsWith(Folder + "/FoldedMasonry_", StringComparison.Ordinal)) continue;
                    var visual = AssetDatabase.LoadAssetAtPath<Mesh>(original.Replace("FoldedMasonry_", "WeatheredMasonry_"));
                    if (visual == null) throw new InvalidOperationException("Missing softened visual " + original);
                    filter.sharedMesh = visual;
                    count++;
                }
                if (count > 0) { PrefabUtility.SaveAsPrefabAsset(root, path); changed++; surfaces += count; }
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
        Debug.Log("Weathered walls: " + changed + " prefabs / " + surfaces + " renderers; original colliders untouched.");
    }
    private static string GeometryFingerprint(Mesh mesh)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        foreach (Vector3 p in mesh.vertices) { writer.Write(p.x); writer.Write(p.y); writer.Write(p.z); }
        foreach (int index in mesh.triangles) writer.Write(index);
        writer.Flush();
        using var sha = System.Security.Cryptography.SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes.ToArray())).Replace("-", "").Substring(0, 16);
    }
}
