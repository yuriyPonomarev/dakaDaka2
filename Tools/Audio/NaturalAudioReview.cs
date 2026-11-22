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
public static class NaturalAudioReview
{
    private const string Pending = "Emberdelve.NaturalAudioReview";
    static NaturalAudioReview()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending, false)) return;
            SessionState.SetBool(Pending, false);
            var root = new GameObject("NaturalAudioReview");
            Object.DontDestroyOnLoad(root);
            root.AddComponent<NaturalAudioReviewRunner>();
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

public sealed class NaturalAudioReviewRunner : MonoBehaviour
{
    private RecorderController recorder;
    private int checks;
    private string acousticLabel;
    private readonly AcousticCases acousticCases = new AcousticCases();
    private int controlledStartFrame;
    private WorldAudio controlledWorld;
    private Transform emitter;
    private AudioClip referenceClip;
    private AcousticMeasurement lastMeasurement;

    [Serializable]
    private sealed class AcousticMeasurement
    {
        public string name;
        public int startFrame, endFrame, eventFrame;
        public float Gain, Cutoff;
        public float minSourceX, maxSourceX, fixedPositionError;
    }
    [Serializable]
    private sealed class AcousticCases
    {
        public int schema = 1, fps = 30, captureEndFrame;
        public string wav = "CONTROLLED-mix.wav";
        public string scope = "Controlled fixed-take acoustic scene; separate from ordinary raid evidence. Actual output assertions require VerifySpatialCapture.py.";
        public bool sourceGeometryPassed;
        public System.Collections.Generic.List<AcousticMeasurement> cases = new System.Collections.Generic.List<AcousticMeasurement>();
    }
    private static string Output
    {
        get
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-emberdelveRaidSeed");
            string seed = index >= 0 && index + 1 < args.Length && ulong.TryParse(args[index + 1], out ulong value)
                ? value.ToString() : "random";
            int tagIndex = Array.IndexOf(args, "-emberAudioReviewTag");
            string tag = tagIndex >= 0 && tagIndex + 1 < args.Length ? args[tagIndex + 1] : "";
            if (tag.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(tag, "^[a-z0-9-]{1,32}$"))
                throw new InvalidOperationException("Invalid audio review evidence tag");
            return Path.GetFullPath("Logs/acceptance/natural-audio-20261122/live-" + seed + (tag.Length > 0 ? "-" + tag : ""));
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
                Debug.LogError("[NaturalAudio] FAIL " + ex);
                EditorApplication.Exit(1);
                yield break;
            }
            if (!more) { stack.Pop(); continue; }
            if (current.Current is IEnumerator nested) stack.Push(nested);
            else yield return current.Current;
        }
        Debug.Log("[NaturalAudio] PASS ordinary/source/geometry checks=" + checks + "; captured-output verification still required: VerifySpatialCapture.py");
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
        Debug.Log("[NaturalAudio] actual Game View=" + Screen.width + "x" + Screen.height + " frame=" + Time.frameCount);
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
        Debug.Log("[NaturalAudio] observed steps=" + (footsteps.StepRequests - steps) + " jumps=" + (footsteps.JumpRequests - jumps) + " landings=" + (footsteps.LandingRequests - lands) + " attacks=" + meleeStarts + " voices=" + (world.PlayedCount - initialPlayed));
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
        yield return ControlledAcoustics();
    }

    private IEnumerator ControlledAcoustics()
    {
        // This is a separate measurement scene AFTER the ordinary gameplay evidence and lifecycle checks.
        // Include persistent game systems, but leave this isolated DontDestroyOnLoad runner alive.
        var roots = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            // Recorder reuses this persistent host for the next recording session. Disabling it prevents
            // both video frames and AudioRenderer from advancing even though StartRecording returns true.
            .Where(x => x.parent == null && x != transform && x.name != "Unity-RecorderSessions")
            .Select(x => x.gameObject).ToArray();
        foreach (var root in roots) if (root != null) root.SetActive(false);
        Time.timeScale = 1f;
        AudioListener.pause = false;
        AudioListener.volume = 1f;
        yield return null;
        var stage = new GameObject("CONTROLLED acoustic measurement stage");
        var cameraObject = new GameObject("Controlled overview camera");
        cameraObject.transform.SetParent(stage.transform);
        var camera = cameraObject.AddComponent<Camera>();
        camera.transform.position = new Vector3(0f, 24f, -17f);
        camera.transform.LookAt(new Vector3(0f, 0f, 7f));
        camera.orthographic = true; camera.orthographicSize = 16f;
        camera.backgroundColor = new Color(.035f, .045f, .06f);
        camera.clearFlags = CameraClearFlags.SolidColor;
        Marker("Ground", PrimitiveType.Cube, new Vector3(0f, -.25f, 8f), new Vector3(34f, .25f, 34f), new Color(.13f, .17f, .2f), stage.transform);
        var listener = Marker("Listener (cyan, facing forward)", PrimitiveType.Sphere, new Vector3(0f, 1f, 0f), Vector3.one * .65f, Color.cyan, stage.transform);
        listener.AddComponent<AudioListener>();
        emitter = Marker("Recorded impact emitter (gold)", PrimitiveType.Sphere, new Vector3(0f, 1f, 2f), Vector3.one * .6f, new Color(1f, .7f, .15f), stage.transform).transform;
        for (int distance = 2; distance <= 18; distance += distance == 2 ? 6 : 10)
            Marker(distance + "m marker", PrimitiveType.Cube, new Vector3(0f, .02f, distance), new Vector3(3f, .04f, .08f), Color.gray, stage.transform);
        var audioHost = new GameObject("Moving audio pool owner");
        audioHost.transform.SetParent(stage.transform);
        controlledWorld = audioHost.AddComponent<WorldAudio>();
        WorldAudio.BindListener(listener.transform);
        Check(Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None).Count(x => x.isActiveAndEnabled) == 1, "controlled scene has exactly one listener");
        referenceClip = GameSoundBank.Get(WorldSound.Impact, 0);
        Check(referenceClip != null && referenceClip.channels == 1 && referenceClip.length > .12f && referenceClip.length < 1.8f,
            "one fixed imported mono impact fits each two-second measurement window");

        var config = ScriptableObject.CreateInstance<RecorderControllerSettings>();
        var movie = ScriptableObject.CreateInstance<MovieRecorderSettings>();
        movie.name = "CONTROLLED natural sound distance and stereo"; movie.Enabled = true;
        movie.ImageInputSettings = new GameViewInputSettings { OutputWidth = 1280, OutputHeight = 720 };
        movie.EncoderSettings = new CoreEncoderSettings { Codec = CoreEncoderSettings.OutputCodec.MP4,
            EncodingQuality = CoreEncoderSettings.VideoEncodingQuality.High };
        movie.CaptureAudio = true; movie.OutputFile = Path.Combine(Output, "CONTROLLED-acoustics").Replace('\\', '/');
        // Installed com.unity.recorder@979a3db2a781 AudioRecorderSettings/AudioInput sources:
        // Audio and Movie recorders share AudioInput's buffer manager and the same rendered mix.
        var audio = ScriptableObject.CreateInstance<AudioRecorderSettings>();
        audio.name = "CONTROLLED shared stereo mix"; audio.Enabled = true;
        audio.OutputFile = Path.Combine(Output, "CONTROLLED-mix").Replace('\\', '/');
        config.AddRecorderSettings(movie); config.AddRecorderSettings(audio);
        config.SetRecordModeToManual(); config.FrameRate = 30; config.CapFrameRate = true;
        recorder = new RecorderController(config); recorder.PrepareRecording();
        Check(recorder.StartRecording(), "separate CONTROLLED acoustic recording started");
        controlledStartFrame = Time.frameCount;
        WriteCases();

        yield return Measure("near 2m", new Vector3(0f, 1f, 2f)); var near = lastMeasurement;
        yield return Measure("mid 8m", new Vector3(0f, 1f, 8f)); var mid = lastMeasurement;
        yield return Measure("far 18m", new Vector3(0f, 1f, 18f)); var far = lastMeasurement;
        Check(near.Gain > mid.Gain * 1.7f && mid.Gain > far.Gain * 1.7f, "managed source gains decrease near > mid > far; output verification pending");
        yield return Measure("left", new Vector3(-6f, 1f, 2f));
        yield return Measure("right", new Vector3(6f, 1f, 2f));
        var wall = Marker("Obstruction wall", PrimitiveType.Cube, new Vector3(0f, 1.5f, 4f), new Vector3(4f, 3f, .4f), new Color(.65f, .25f, .22f), stage.transform);
        int level = LayerMask.NameToLayer(Emberdelve.Core.GameConfig.LevelLayerName);
        Check(level >= 0, "controlled obstruction uses the real Level collision layer");
        wall.layer = level;
        Physics.SyncTransforms();
        yield return Measure("blocked 8m", new Vector3(0f, 1f, 8f)); var blocked = lastMeasurement;
        Check(blocked.Gain < mid.Gain * .5f && blocked.Cutoff < 2000f && mid.Cutoff > 20000f, "wall applies gain and low-pass filtering");
        wall.SetActive(false);
        yield return Measure("clear again 8m", new Vector3(0f, 1f, 8f)); var clear = lastMeasurement;
        Check(clear.Gain > blocked.Gain * 1.5f && clear.Cutoff > 20000f, "removing wall restores source gain and filter; output verification pending");
        yield return Measure("attached moving left to right", new Vector3(-6f, 1f, 4f), true);
        acousticLabel = "CONTROLLED source geometry checked. Recorded-output verification follows separately.";
        for (int frame = 0; frame < 15; frame++) yield return null;
        acousticCases.captureEndFrame = Time.frameCount - controlledStartFrame;
        recorder.StopRecording(); recorder = null;
        Check(new FileInfo(Path.Combine(Output, "CONTROLLED-acoustics.mp4")).Length > 1024, "controlled acoustic movie is nonempty");
        Check(new FileInfo(Path.Combine(Output, acousticCases.wav)).Length > 44, "shared-mix WAV is nonempty");
        acousticCases.sourceGeometryPassed = true;
        WriteCases();
        File.WriteAllText(Path.Combine(Output, "CONTROLLED-result.txt"), "PASS source/geometry cumulative checks=" + checks +
            "\nFixed imported take; managed gains/filter and attached/fixed source geometry checked. Actual recorded-output assertions PENDING VerifySpatialCapture.py.\nOrdinary gameplay evidence remains walk-jump-combat.mp4 and result.txt.\n");
    }

    private IEnumerator Measure(string label, Vector3 position, bool moving = false)
    {
        acousticLabel = "CONTROLLED | " + label + " | same recorded Impact take";
        emitter.position = position;
        for (int frame = 0; frame < 15; frame++) yield return null;
        var measurement = new AcousticMeasurement { name = label, startFrame = Time.frameCount - controlledStartFrame };
        // Three-frame lead plus fifteen-frame gaps allow the postprocessor one frame of capture alignment tolerance.
        for (int frame = 0; frame < 3; frame++) yield return null;
        measurement.eventFrame = Time.frameCount - controlledStartFrame;
        if (moving) WorldAudio.PlayAttached(WorldSound.Impact, emitter, Vector3.zero);
        else WorldAudio.Play(WorldSound.Impact, position);
        AudioSource source = controlledWorld.GetComponentsInChildren<AudioSource>().Single(x => x.isPlaying);
        // Keep the production emitter, distance, filter and panning path; only select an identical take for comparison.
        source.Stop(); source.clip = referenceClip; source.Play();
        float gain = source.volume, cutoff = source.GetComponent<AudioLowPassFilter>().cutoffFrequency;
        float minX = source.transform.position.x, maxX = minX;
        float fixedPositionError = 0f;
        for (int frame = 0; frame < 60; frame++)
        {
            if (label == "near 2m" && source.isPlaying)
            {
                fixedPositionError = Mathf.Max(fixedPositionError, Vector3.Distance(source.transform.position, position));
                controlledWorld.transform.position = new Vector3(frame / 30f * 4f, 0f, 0f);
            }
            if (moving && source.isPlaying)
            {
                minX = Mathf.Min(minX, source.transform.position.x); maxX = Mathf.Max(maxX, source.transform.position.x);
                float progress = Mathf.Clamp01(frame / 30f / Mathf.Min(.4f, referenceClip.length * .7f));
                emitter.position = Vector3.Lerp(position, new Vector3(6f, 1f, 4f), progress);
            }
            yield return null;
        }
        Check(new FileInfo(Path.Combine(Output, acousticCases.wav)).Length > 44,
            label + ": Recorder is advancing and writing the actual mixed audio");
        measurement.endFrame = Time.frameCount - controlledStartFrame;
        controlledWorld.transform.position = Vector3.zero;
        if (label == "near 2m")
            Check(fixedPositionError < .001f, "fixed contact remains at its event point while the audio-pool owner moves");
        measurement.Gain = gain; measurement.Cutoff = cutoff;
        measurement.minSourceX = minX; measurement.maxSourceX = maxX; measurement.fixedPositionError = fixedPositionError;
        if (moving) Check(maxX - minX > 6f, "attached voice follows the moving emitter during playback");
        lastMeasurement = measurement;
        acousticCases.cases.Add(measurement);
        WriteCases();
        Debug.Log("[NaturalAudio] source case=" + label + " frames=" + measurement.startFrame + ".." + measurement.endFrame + " gain=" + gain + " cutoff=" + cutoff);
    }

    private void WriteCases() => File.WriteAllText(Path.Combine(Output, "CONTROLLED-cases.json"), JsonUtility.ToJson(acousticCases, true));

    private static GameObject Marker(string name, PrimitiveType type, Vector3 position, Vector3 scale, Color color, Transform parent)
    {
        var marker = GameObject.CreatePrimitive(type); marker.name = name;
        marker.transform.SetParent(parent); marker.transform.position = position; marker.transform.localScale = scale;
        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
        var material = new Material(shader); material.color = color;
        marker.GetComponent<Renderer>().sharedMaterial = material;
        return marker;
    }

    private void OnGUI()
    {
        if (string.IsNullOrEmpty(acousticLabel)) return;
        GUI.Box(new Rect(12, 12, Screen.width - 24, 78), GUIContent.none);
        GUI.Label(new Rect(28, 24, Screen.width - 56, 36), acousticLabel);
        GUI.Label(new Rect(28, 50, Screen.width - 56, 30), "Cyan: listener facing +Z. Gold: emitter. Red: Level obstruction. Measurements use actual listener stereo output.");
    }

    private static void Input(object input, string name, object value) => input.GetType().GetProperty(name,
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(input, value);

    private void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++; Debug.Log("[NaturalAudio] PASS " + message);
    }
    private static void Click(string name) => Object.FindObjectsByType<Button>(FindObjectsInactive.Include,
        FindObjectsSortMode.None).Single(button => button.name == name).onClick.Invoke();
}
