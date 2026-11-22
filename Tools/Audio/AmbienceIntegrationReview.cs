// Mirror-only verification helper. Copy into the mirror's Assets/Editor before launching Unity.
using System;
using System.Collections;
using System.IO;
using System.Linq;
using Emberdelve.Feedback;
using Emberdelve.Flow;
using Emberdelve.Level;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Encoder;
using UnityEditor.Recorder.Input;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

[InitializeOnLoad]
public static class AmbienceIntegrationReview
{
    private const string Pending = "Emberdelve.AmbienceIntegrationReview";
    static AmbienceIntegrationReview()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending, false)) return;
            SessionState.SetBool(Pending, false);
            var root = new GameObject("AmbienceIntegrationReview");
            Object.DontDestroyOnLoad(root);
            root.AddComponent<AmbienceIntegrationReviewRunner>();
        };
    }

    private static void RequireMirror()
    {
        if (!Application.dataPath.Replace('\\', '/').EndsWith("/DakaDaka_ci/Assets", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Review runs only on DakaDaka_ci");
    }

    public static void Build()
    {
        RequireMirror();
        CaptureGeometryBudget("before");
        Emberdelve.EditorTools.DungeonRoomExpansionReview.RebuildAssetsBatch();
        CaptureGeometryBudget("after");
        Debug.Log("[AmbienceIntegration] Rebuilt door and all 30 expanded modules with merged dressing");
    }

    private static void CaptureGeometryBudget(string label)
    {
        var lines = new System.Collections.Generic.List<string> { "prefab,visible_renderers,visible_triangles" };
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Emberdelve/Prefabs/DungeonModules" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            int renderers = 0; long triangles = 0;
            foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>())
            {
                var renderer = filter.GetComponent<MeshRenderer>();
                if (renderer == null || !renderer.enabled || filter.sharedMesh == null) continue;
                renderers++;
                for (int submesh = 0; submesh < filter.sharedMesh.subMeshCount; submesh++)
                    triangles += filter.sharedMesh.GetIndexCount(submesh) / 3;
            }
            lines.Add(path + "," + renderers + "," + triangles);
        }
        string folder = "Logs/acceptance/ambience-20261122/architecture-budget";
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, label + ".csv"), lines);
    }

    public static void Run()
    {
        RequireMirror();
        string[] args = Environment.GetCommandLineArgs();
        int profile = Array.IndexOf(args, "--ember-profile-dir");
        string evidenceRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/acceptance")) + Path.DirectorySeparatorChar;
        if (profile < 0 || profile + 1 >= args.Length ||
            !Path.GetFullPath(args[profile + 1]).StartsWith(evidenceRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Live review requires an isolated --ember-profile-dir under mirror Logs/acceptance.");
        Emberdelve.EditorTools.DungeonRoomExpansionReview.RefreshDownloadedMaterials();
        RepairAuthoredListeners();
        SessionState.SetBool(Pending, true);
        EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Lobby.unity");
        Application.runInBackground = true;
        var view = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView", true));
        view.Show(); view.Focus();
        EditorApplication.EnterPlaymode();
    }

    // Migrate existing authored players; future players get this from the level builder.
    private static void RepairAuthoredListeners()
    {
        foreach (string name in new[] { "Emberdelve_Level01", "Emberdelve_Crypts", "Emberdelve_ModularCrypts" })
        {
            var scene = EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/" + name + ".unity");
            foreach (var root in scene.GetRootGameObjects())
                foreach (var rig in root.GetComponentsInChildren<Emberdelve.Player.PlayerRig>(true))
                    AddListener(rig.gameObject);
            EditorSceneManager.SaveScene(scene);
        }
        const string path = "Assets/Emberdelve/Prefabs/Net/Emberdelve_NetPlayer.prefab";
        var prefab = PrefabUtility.LoadPrefabContents(path);
        try { AddListener(prefab); PrefabUtility.SaveAsPrefabAsset(prefab, path); }
        finally { PrefabUtility.UnloadPrefabContents(prefab); }
        AssetDatabase.SaveAssets();
    }

    private static void AddListener(GameObject rig)
    {
        var camera = rig.GetComponentsInChildren<Camera>(true).Single(value => value.CompareTag("MainCamera"));
        if (camera.GetComponent<AudioListener>() == null) camera.gameObject.AddComponent<AudioListener>();
    }
}

public sealed class AmbienceIntegrationReviewRunner : MonoBehaviour
{
    private RecorderController recorder;
    private int checks;
    private static string Output
    {
        get
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-emberdelveRaidSeed");
            string seed = index >= 0 && index + 1 < args.Length && ulong.TryParse(args[index + 1], out ulong value)
                ? value.ToString() : "random";
            return Path.GetFullPath("Logs/acceptance/ambience-20261122/melodic-review-" + seed);
        }
    }

    private IEnumerator Start()
    {
        var stack = new System.Collections.Generic.Stack<IEnumerator>();
        stack.Push(Review());
        while (stack.Count > 0)
        {
            var current = stack.Peek();
            bool more;
            try { more = current.MoveNext(); }
            catch (Exception ex)
            {
                recorder?.StopRecording();
                Debug.LogError("[AmbienceIntegration] FAIL " + ex);
                EditorApplication.Exit(1);
                yield break;
            }
            if (!more) { stack.Pop(); continue; }
            if (current.Current is IEnumerator nested) stack.Push(nested);
            else yield return current.Current;
        }
        Debug.Log("[AmbienceIntegration] PASS checks=" + checks);
        EditorApplication.Exit(0);
    }

    private IEnumerator Review()
    {
        Directory.CreateDirectory(Output);
        yield return null;
        Click("ModeSolo"); Click("DescendButton"); Click("EnterRaid");
        RunDirector director = null;
        for (float until = Time.realtimeSinceStartup + 60; Time.realtimeSinceStartup < until;)
        {
            director = Object.FindFirstObjectByType<RunDirector>();
            if (director != null && director.LocalSession != null && director.LocalSession.IsRunning) break;
            yield return null;
        }
        Check(director != null && director.LocalSession != null && director.LocalSession.IsRunning, "ordinary Solo raid entered");
        var map = Object.FindFirstObjectByType<DungeonModuleSceneAssembler>();
        Check(map != null && map.RuntimeMapValidationComplete, "merged map validates");
        int startFrame = Time.frameCount;
        yield return new WaitForSeconds(2);
        Check(Time.frameCount > startFrame + 5, "simulation advances");
        var ambience = Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None)
            .Single(source => source.clip != null && source.clip.name == "DarkFantasyAmbience");
        Check(ambience.isPlaying && ambience.loop && ambience.spatialBlend == 0 && ambience.volume > .1f,
            "stereo ambience plays in real raid");
        Check(ambience.clip.channels == 2 && ambience.clip.frequency == 48000 && Mathf.Abs(ambience.clip.length - 48) < .01f,
            "48 second stereo asset loaded");
        Check(Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None).All(source =>
            source.clip == null || source.clip.name != "sfx_fire" && source.clip.name != "sfx_ruin_wind"),
            "old repeated fire and wind sources absent");
        Check(Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None).Count(listener => listener.isActiveAndEnabled) == 1,
            "one active listener");

        // Seek before capture so the video contains an actual uninterrupted loop boundary.
        ambience.time = 45;
        var config = ScriptableObject.CreateInstance<RecorderControllerSettings>();
        var movie = ScriptableObject.CreateInstance<MovieRecorderSettings>();
        movie.name = "Ordinary raid ambience and loop boundary"; movie.Enabled = true;
        movie.ImageInputSettings = new GameViewInputSettings { OutputWidth = 1280, OutputHeight = 720 };
        movie.EncoderSettings = new CoreEncoderSettings { Codec = CoreEncoderSettings.OutputCodec.MP4,
            EncodingQuality = CoreEncoderSettings.VideoEncodingQuality.High };
        movie.CaptureAudio = true;
        movie.OutputFile = Path.Combine(Output, "ambience-loop-in-raid").Replace('\\', '/');
        config.AddRecorderSettings(movie); config.SetRecordModeToManual(); config.FrameRate = 30; config.CapFrameRate = true;
        recorder = new RecorderController(config); recorder.PrepareRecording();
        Check(recorder.StartRecording(), "Game View recorder started with game audio");
        bool wrapped = false;
        float last = ambience.time;
        float initialYaw = director.Player.transform.eulerAngles.y;
        for (int frame = 0; frame < 180; frame++)
        {
            wrapped |= ambience.time < last - 1;
            last = ambience.time;
            // A normal first-person look sweep exposes both sides of the authored entry.
            director.Player.Look.ApplyAimPose(initialYaw + 55f * Mathf.Sin(frame * Mathf.PI * 2f / 180f), 0f);
            if (frame == 45 || frame == 90 || frame == 135)
                ScreenCapture.CaptureScreenshot(Path.Combine(Output, "raid-frame-" + frame + ".png"));
            yield return null;
        }
        recorder.StopRecording(); recorder = null;
        Check(wrapped && ambience.isPlaying, "loop boundary crossed without restarting playback");
        // Lifecycle assertions happen after the ordinary gameplay recording. Pause simulation
        // so combat cannot invalidate the live-player precondition halfway through this check.
        Check(!director.Player.IsDead, "player alive before isolated lifecycle check");
        float priorTimeScale = Time.timeScale;
        Time.timeScale = 0f;
        var owner = ambience.GetComponent<DungeonAmbience>();
        var clip = ambience.clip;
        owner.enabled = false;
        Check(!ambience.isPlaying && ambience.volume == 0, "disable stops and silences ambience");
        owner.enabled = true;
        yield return new WaitForSecondsRealtime(1.5f);
        Check(ambience.isPlaying && ambience.volume > .1f, "enable fades ambience back in");
        Object.Destroy(owner);
        yield return null;
        Check(ambience == null && clip != null && Resources.Load<AudioClip>(DungeonAmbience.ClipResource) == clip,
            "destroy releases owned source while preserving shared resource");
        Time.timeScale = priorTimeScale;
        string file = Path.Combine(Output, "ambience-loop-in-raid.mp4");
        Check(File.Exists(file) && new FileInfo(file).Length > 100000, "recorded MP4 exists");
        File.WriteAllText(Path.Combine(Output, "result.txt"), "PASS checks=" + checks + "\nActual game camera, HUD, AI and lighting preserved during recording. Recording starts near loop boundary. Post-recording component lifecycle checks pause simulation.\n");
    }

    private void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++; Debug.Log("[AmbienceIntegration] PASS " + message);
    }
    private static void Click(string name) => Object.FindObjectsByType<Button>(FindObjectsInactive.Include,
        FindObjectsSortMode.None).Single(button => button.name == name).onClick.Invoke();
}
