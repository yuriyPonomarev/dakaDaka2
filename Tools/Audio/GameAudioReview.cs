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
public static class GameAudioReview
{
    private const string Pending = "Emberdelve.GameAudioReview";
    static GameAudioReview()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending, false)) return;
            SessionState.SetBool(Pending, false);
            var root = new GameObject("GameAudioReview");
            Object.DontDestroyOnLoad(root);
            root.AddComponent<GameAudioReviewRunner>();
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

public sealed class GameAudioReviewRunner : MonoBehaviour
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
            return Path.GetFullPath("Logs/acceptance/game-audio-20261122/live-" + seed);
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
                Debug.LogError("[GameAudio] FAIL " + ex);
                EditorApplication.Exit(1);
                yield break;
            }
            if (!more) { stack.Pop(); continue; }
            if (current.Current is IEnumerator nested) stack.Push(nested);
            else yield return current.Current;
        }
        Debug.Log("[GameAudio] PASS checks=" + checks);
        EditorApplication.Exit(0);
    }

    private IEnumerator Review()
    {
        Directory.CreateDirectory(Output);
        yield return null;
        Click("ModeSolo"); Click("DescendButton"); Click("EnterRaid");
        RunDirector director = null;
        for (float until = Time.realtimeSinceStartup + 90; Time.realtimeSinceStartup < until;)
        {
            director = Object.FindFirstObjectByType<RunDirector>();
            if (director != null && director.Player != null && director.Player.IsInPlay) break;
            yield return null;
        }
        Check(director != null && director.Player != null && director.Player.IsInPlay, "ordinary Solo raid entered");
        var player = director.Player;
        for (int i = 0; i < 8; i++) yield return null;
        var footsteps = player.GetComponent<WorldFootsteps>();
        var world = Object.FindFirstObjectByType<WorldAudio>();
        Check(footsteps != null && world != null && player.GetComponent<PlayerActionAudio>() != null, "all player sound presenters bound");
        Check(Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None).Count(x => x.isActiveAndEnabled) == 1, "exactly one active listener");
        var config = ScriptableObject.CreateInstance<RecorderControllerSettings>();
        var movie = ScriptableObject.CreateInstance<MovieRecorderSettings>();
        movie.name = "Game sound integration"; movie.Enabled = true;
        movie.ImageInputSettings = new GameViewInputSettings { OutputWidth = 1280, OutputHeight = 720 };
        movie.EncoderSettings = new CoreEncoderSettings { Codec = CoreEncoderSettings.OutputCodec.MP4,
            EncodingQuality = CoreEncoderSettings.VideoEncodingQuality.High };
        movie.CaptureAudio = true; movie.OutputFile = Path.Combine(Output, "walk-jump-combat").Replace('\\', '/');
        config.AddRecorderSettings(movie); config.SetRecordModeToManual(); config.FrameRate = 30; config.CapFrameRate = true;
        recorder = new RecorderController(config); recorder.PrepareRecording();
        Check(recorder.StartRecording(), "Game View recording with game audio started");
        int initialFrame = Time.frameCount, initialPlayed = world.PlayedCount;
        int steps = footsteps.StepRequests, jumps = footsteps.JumpRequests, lands = footsteps.LandingRequests;
        var input = player.Input;
        int meleeStarts = 0;
        var melee = player.GetComponent<Emberdelve.Combat.MeleeCombat>();
        Action<Emberdelve.Combat.MeleeSwingIntent> onAttack = _ => meleeStarts++;
        if (melee != null) melee.AttackStarted += onAttack;
        for (int frame = 0; frame < 210; frame++)
        {
            Input(input, "MoveOverride", frame < 100 ? new Vector2(0f, 1f) : new Vector2(.7f, -.5f));
            Input(input, "JumpPressedOverride", frame == 25);
            Input(input, "PrimaryPressedOverride", frame == 95 || frame == 145);
            Input(input, "PrimaryHeldOverride", frame >= 95 && frame < 110 || frame >= 145 && frame < 160);
            Input(input, "PrimaryReleasedOverride", frame == 110 || frame == 160);
            if (frame == 35 || frame == 100 || frame == 175)
                ScreenCapture.CaptureScreenshot(Path.Combine(Output, "frame-" + frame + ".png"));
            yield return null;
        }
        foreach (string name in new[] { "MoveOverride", "JumpPressedOverride", "PrimaryPressedOverride", "PrimaryHeldOverride", "PrimaryReleasedOverride" }) Input(input, name, null);
        recorder.StopRecording(); recorder = null;
        if (melee != null) melee.AttackStarted -= onAttack;
        Debug.Log("[GameAudio] observed steps=" + (footsteps.StepRequests - steps) + " jumps=" + (footsteps.JumpRequests - jumps) + " landings=" + (footsteps.LandingRequests - lands) + " attacks=" + meleeStarts + " voices=" + (world.PlayedCount - initialPlayed));
        Check(meleeStarts > 0, "real player melee input started attacks");
        Check(Time.frameCount >= initialFrame + 210, "real gameplay frames advanced");
        Check(footsteps.StepRequests > steps, "walking requested surface footsteps");
        Check(footsteps.JumpRequests > jumps, "real movement takeoff requested jump");
        Check(footsteps.LandingRequests > lands, "real movement landing requested landing");
        Check(world.PlayedCount > initialPlayed + 3, "spatial pool played gameplay cues");
        Check(Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None).Any(x => x.clip != null && x.clip.name == "DarkFantasyAmbience" && x.isPlaying), "music and effects coexist");
        Check(Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None).Count(x => x.isActiveAndEnabled) == 1, "single listener preserved during raid");
        Check(File.Exists(Path.Combine(Output, "walk-jump-combat.mp4")), "recording exists");
        // Separate post-recording lifecycle/menu check; ordinary combat was active throughout the video.
        Time.timeScale = 0f;
        var bag = Object.FindFirstObjectByType<Emberdelve.UI.InventoryScreenController>();
        Check(bag != null, "inventory menu exists");
        var beforeMenu = Object.FindFirstObjectByType<LocalGameAudio>();
        int beforeMenuCount = beforeMenu != null ? beforeMenu.PlayedCount : 0;
        bag.Open(); yield return null;
        var localAudio = Object.FindFirstObjectByType<LocalGameAudio>();
        Check(localAudio != null && localAudio.PlayedCount > beforeMenuCount, "paused inventory emits local audio");
        int localPlayed = localAudio.PlayedCount;
        bag.Close(); yield return null;
        Check(localAudio.PlayedCount > localPlayed, "closing paused inventory emits a separate cue");
        var shared = GameSoundBank.Get(WorldSound.Step, 0);
        var ownedVoices = world.GetComponentsInChildren<AudioSource>().Where(x => x.name.StartsWith("WorldVoice")).ToArray();
        world.enabled = false;
        Check(ownedVoices.All(x => !x.isPlaying), "disabling world audio stops its voices");
        int played = world.PlayedCount;
        Object.Destroy(world); yield return null; yield return null;
        Check(shared != null && GameSoundBank.Get(WorldSound.Step, 0) == shared, "shared imported clip survives world teardown");
        Check(ownedVoices.All(x => x == null), "world teardown releases owned sources");
        Time.timeScale = 1f;
        File.WriteAllText(Path.Combine(Output, "result.txt"), "PASS checks=" + checks + "\nsteps=" + (footsteps.StepRequests - steps) + " jumps=" + (footsteps.JumpRequests - jumps) + " lands=" + (footsteps.LandingRequests - lands) + " voices=" + (played - initialPlayed) + "\nOrdinary camera/HUD/AI/lighting, real player input path. Isolated save.\n");
    }

    private static void Input(object input, string name, object value) => input.GetType().GetProperty(name,
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(input, value);

    private void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++; Debug.Log("[GameAudio] PASS " + message);
    }
    private static void Click(string name) => Object.FindObjectsByType<Button>(FindObjectsInactive.Include,
        FindObjectsSortMode.None).Single(button => button.name == name).onClick.Invoke();
}
