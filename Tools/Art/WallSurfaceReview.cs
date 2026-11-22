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
public static class WallSurfaceReview
{
    private const string Pending = "Emberdelve.WallSurfaceReview";
    static WallSurfaceReview()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending, false)) return;
            SessionState.SetBool(Pending, false);
            var root = new GameObject("WallSurfaceReview");
            Object.DontDestroyOnLoad(root);
            root.AddComponent<WallSurfaceReviewRunner>();
        };
    }

    private static void RequireMirror()
    {
        if (!Application.dataPath.Replace('\\', '/').EndsWith("/DakaDaka_ci/Assets", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Review runs only on DakaDaka_ci");
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
        SessionState.SetBool(Pending, true);
        EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Lobby.unity");
        Application.runInBackground = true;
        var view = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView", true));
        view.Show(); view.Focus();
        EditorApplication.EnterPlaymode();
    }

}

public sealed class WallSurfaceReviewRunner : MonoBehaviour
{
    private RecorderController recorder;
    private IEnumerator Start()
    {
        var routine = Review();
        while (true)
        {
            bool more;
            try { more = routine.MoveNext(); }
            catch (Exception ex) { recorder?.StopRecording(); Debug.LogError("[Walls] FAIL " + ex); EditorApplication.Exit(1); yield break; }
            if (!more) break;
            yield return routine.Current;
        }
        EditorApplication.Exit(0);
    }
    private IEnumerator Review()
    {
        var args = Environment.GetCommandLineArgs();
        string seed = args[Array.IndexOf(args, "-emberdelveRaidSeed") + 1];
        string tag = args[Array.IndexOf(args, "-emberWallReviewTag") + 1];
        string output = Path.GetFullPath("Logs/acceptance/soft-walls-20261122/live-" + seed + "-" + tag);
        Directory.CreateDirectory(output);
        yield return null;
        Click("ModeSolo"); Click("DescendButton"); Click("EnterRaid");
        RunDirector director = null;
        float deadline = Time.realtimeSinceStartup + 90;
        while (Time.realtimeSinceStartup < deadline)
        {
            director = Object.FindFirstObjectByType<RunDirector>();
            if (director != null && director.Player != null && director.Player.IsInPlay) break;
            yield return null;
        }
        if (director == null || director.Player == null || !director.Player.IsInPlay) throw new Exception("No living player");
        var player = director.Player;
        int disabledBots = DisableBots();
        for (int i = 0; i < 8; i++) yield return null;
        var walls = Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None).Where(x => x.sharedMesh != null && x.sharedMesh.name.StartsWith("WeatheredMasonry_")).ToArray();
        if (walls.Length == 0) throw new Exception("No softened walls instantiated");
        int intact = walls.Count(x => x.GetComponent<MeshCollider>() != null && x.GetComponent<MeshCollider>().sharedMesh.name.StartsWith("FoldedMasonry_"));
        if (intact != walls.Length) throw new Exception("Original wall collision not preserved");
        var config = ScriptableObject.CreateInstance<RecorderControllerSettings>();
        var movie = ScriptableObject.CreateInstance<MovieRecorderSettings>();
        movie.name = "Wall surface review"; movie.Enabled = true;
        movie.ImageInputSettings = new GameViewInputSettings { OutputWidth = 1280, OutputHeight = 720 };
        movie.EncoderSettings = new CoreEncoderSettings { Codec = CoreEncoderSettings.OutputCodec.MP4, EncodingQuality = CoreEncoderSettings.VideoEncodingQuality.High };
        movie.CaptureAudio = true; movie.OutputFile = Path.Combine(output, "walls-gameplay").Replace('\\', '/');
        config.AddRecorderSettings(movie); config.SetRecordModeToManual(); config.FrameRate = 30; config.CapFrameRate = true;
        recorder = new RecorderController(config); recorder.PrepareRecording();
        if (!recorder.StartRecording()) throw new Exception("Recorder did not start");
        int first = Time.frameCount;
        var look = player.GetComponent<Emberdelve.Movement.PlayerLook>();
        float yaw = player.transform.eulerAngles.y;
        Vector3 start = player.transform.position;
        for (int frame = 0; frame < 300; frame++)
        {
            disabledBots += DisableBots();
            // Ordinary character movement, camera/HUD/lighting. The user explicitly
            // requested disabling bots during environment inspection.
            Input(player.Input, frame < 110 ? new Vector2(.35f, .7f) : Vector2.zero);
            if (look != null) look.ApplyAimPose(yaw + (frame < 110 ? 0 : (frame - 110) * .9f), -10f);
            if (frame == 45 || frame == 140 || frame == 245) ScreenCapture.CaptureScreenshot(Path.Combine(output, "frame-" + frame + ".png"));
            yield return null;
        }
        Input(player.Input, null);
        recorder.StopRecording(); recorder = null;
        if (Time.frameCount < first + 300 || !player.IsInPlay) throw new Exception("Review did not finish with living player and advancing frames");
        File.WriteAllText(Path.Combine(output, "result.txt"), "PASS seed=" + seed + " frames=" + (Time.frameCount-first) + " viewport=" + Screen.width + "x" + Screen.height + " walls=" + walls.Length + " originalColliders=" + intact + " distance=" + Vector3.Distance(start, player.transform.position) + " disabledBots=" + disabledBots + "\nOrdinary camera/HUD/lighting, bots disabled at user request, isolated save.\n");
        Debug.Log("[Walls] PASS " + seed);
        yield return null;
    }
    private static int DisableBots()
    {
        int count = 0;
        foreach (var bot in Object.FindObjectsByType<Emberdelve.AI.RoyaleBotController>(FindObjectsSortMode.None))
        { bot.gameObject.SetActive(false); count++; }
        foreach (var enemy in Object.FindObjectsByType<Emberdelve.AI.EnemyAgent>(FindObjectsSortMode.None))
        { enemy.gameObject.SetActive(false); count++; }
        return count;
    }
    private static void Input(object input, object value) => input.GetType().GetProperty("MoveOverride", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(input, value);
    private static void Click(string name) => Object.FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None).Single(x => x.name == name).onClick.Invoke();
}
