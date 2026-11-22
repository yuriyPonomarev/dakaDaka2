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
public static class TraversalReview
{
    private const string Pending = "Emberdelve.TraversalReview";
    static TraversalReview()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending, false)) return;
            SessionState.SetBool(Pending, false);
            var root = new GameObject("TraversalReview");
            Object.DontDestroyOnLoad(root);
            root.AddComponent<TraversalReviewRunner>();
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

public sealed class TraversalReviewRunner : MonoBehaviour
{
    private RecorderController recorder;
    private Emberdelve.Player.PlayerRig player;
    private Transform room;
    private string output;
    private float low, high;
    private int captures;
    private IEnumerator Start()
    {
        var stack = new System.Collections.Generic.Stack<IEnumerator>(); stack.Push(Review());
        while (stack.Count > 0)
        {
            var current = stack.Peek(); bool more;
            try { more = current.MoveNext(); }
            catch (Exception ex) { recorder?.StopRecording(); Debug.LogError("[TraversalReview] FAIL " + ex); EditorApplication.Exit(1); yield break; }
            if (!more) { stack.Pop(); continue; }
            if (current.Current is IEnumerator nested) stack.Push(nested); else yield return current.Current;
        }
        EditorApplication.Exit(0);
    }
    private IEnumerator Review()
    {
        var args = Environment.GetCommandLineArgs();
        string seed = args[Array.IndexOf(args, "-emberdelveRaidSeed") + 1];
        string tag = args[Array.IndexOf(args, "-emberWallReviewTag") + 1];
        output = Path.GetFullPath("Logs/acceptance/soft-traversal-20261122/live-" + seed + "-" + tag);
        Directory.CreateDirectory(output); yield return null;
        Click("ModeSolo"); Click("DescendButton"); Click("EnterRaid");
        RunDirector director = null;
        for (float end = Time.realtimeSinceStartup + 90; Time.realtimeSinceStartup < end;)
        {
            director = Object.FindFirstObjectByType<RunDirector>();
            if (director != null && director.Player != null && director.Player.IsInPlay) break;
            yield return null;
        }
        if (director == null || director.Player == null || !director.Player.IsInPlay) throw new Exception("No admitted living player");
        player = director.Player;
        for (int i=0;i<12;i++) yield return null;
        AssertNoAI();
        room = Object.FindObjectsByType<DungeonModuleDefinition>(FindObjectsSortMode.None)
            .Where(x => x.GetComponentsInChildren<Transform>().Any(t => t.name == "Gallery_SouthStairs"))
            .OrderBy(x => (x.transform.position-player.transform.position).sqrMagnitude).First().transform;
        var movie = ScriptableObject.CreateInstance<MovieRecorderSettings>();
        movie.name="Traversal inspection"; movie.Enabled=true;
        movie.ImageInputSettings=new GameViewInputSettings { OutputWidth=1280, OutputHeight=720 };
        movie.EncoderSettings=new CoreEncoderSettings { Codec=CoreEncoderSettings.OutputCodec.MP4, EncodingQuality=CoreEncoderSettings.VideoEncodingQuality.High };
        movie.CaptureAudio=true; movie.OutputFile=Path.Combine(output,"stairs-and-undercroft").Replace('\\','/');
        var config=ScriptableObject.CreateInstance<RecorderControllerSettings>(); config.AddRecorderSettings(movie);
        config.SetRecordModeToManual(); config.FrameRate=30; config.CapFrameRate=true;
        recorder=new RecorderController(config); recorder.PrepareRecording();
        if(!recorder.StartRecording()) throw new Exception("Recorder failed");
        int start=Time.frameCount; float health=player.CurrentHealth;
        // Approach behind the perimeter columns, then enter each stair at its foot.
        yield return Walk(new Vector3(-10,0,-14), 14);
        yield return Walk(new Vector3(-11.3f,0,-16.65f), 14);
        yield return Walk(new Vector3(-13,0,-16.65f), 24);
        yield return Hold("gallery-foot", 45);
        yield return Walk(new Vector3(-13,3.9f,-4.2f), 18);
        if(high<3.8f) throw new Exception("Upper gallery not reached: " + high);
        yield return Hold("gallery-top", 60);
        yield return Walk(new Vector3(-13,0,-16.65f), 27);
        yield return Walk(new Vector3(-11.3f,0,-16.65f), 14);
        yield return Walk(new Vector3(-10,0,-14), 14);
        yield return Walk(new Vector3(10,0,-14), 14);
        yield return Walk(new Vector3(10,0,-16.2f), 14);
        yield return Walk(new Vector3(13.5f,0,-16.2f), 22);
        yield return Walk(new Vector3(13.5f,-1.5f,-10), 28);
        yield return Hold("undercroft-bottom", 60);
        yield return Walk(new Vector3(13.5f,0,-4), 18);
        yield return Hold("undercroft-exit", 45);
        AssertNoAI();
        if(low> -1.35f || !player.IsInPlay || player.CurrentHealth<health-.01f) throw new Exception("Traversal/inspection state failed");
        recorder.StopRecording(); recorder=null;
        File.WriteAllText(Path.Combine(output,"result.txt"), "PASS seed="+seed+" frames="+(Time.frameCount-start)+" viewport="+Screen.width+"x"+Screen.height+" minY="+low+" maxY="+high+" health="+player.CurrentHealth+" AI=0\nNormal new Solo raid; no AI deactivation helper or teleport; ordinary player movement/camera/HUD/lighting.\n");
        Debug.Log("[TraversalReview] PASS seed="+seed);
    }
    private IEnumerator Walk(Vector3 local, float pitch)
    {
        var target=room.TransformPoint(local); float deadline=Time.time+20, wallDeadline=Time.realtimeSinceStartup+120;
        Vector3 lastProgress=player.transform.position; float progressTime=Time.time;
        while(Time.time<deadline && Time.realtimeSinceStartup<wallDeadline)
        {
            Vector3 delta=target-player.transform.position; delta.y=0;
            if(delta.magnitude<.18f) { Input(null); yield break; }
            if(!player.IsInPlay) throw new Exception("Player died on route");
            if(Vector3.Distance(lastProgress,player.transform.position)>.15f) { lastProgress=player.transform.position; progressTime=Time.time; }
            if(Time.time-progressTime>3) break;
            player.Look.ApplyAimPose(Mathf.Atan2(delta.x,delta.z)*Mathf.Rad2Deg,pitch);
            Input(new Vector2(0,delta.magnitude<.5f?.4f:1));
            float y=room.InverseTransformPoint(player.transform.position).y;
            low=Mathf.Min(low,y); high=Mathf.Max(high,y);
            yield return null;
        }
        Input(null); throw new Exception("Walk stalled toward "+local+" at "+room.InverseTransformPoint(player.transform.position));
    }
    private IEnumerator Hold(string label,int frames)
    {
        Input(null);
        Vector3 direction = room.TransformDirection(label == "gallery-top" ? Vector3.back : Vector3.forward);
        player.Look.ApplyAimPose(Mathf.Atan2(direction.x,direction.z)*Mathf.Rad2Deg, label == "gallery-top" ? 30 : 16);
        yield return null;
        ScreenCapture.CaptureScreenshot(Path.Combine(output,(++captures)+"-"+label+".png"));
        for(int i=0;i<frames;i++) yield return null;
    }
    private static void AssertNoAI()
    {
        int bots=Object.FindObjectsByType<Emberdelve.AI.RoyaleBotController>(FindObjectsSortMode.None).Length;
        int monsters=Object.FindObjectsByType<Emberdelve.AI.EnemyAgent>(FindObjectsSortMode.None).Length;
        if(bots!=0 || monsters!=0) throw new Exception("New raid AI policy failed: "+bots+" bots / "+monsters+" monsters");
    }
    private void Input(object value) => player.Input.GetType().GetProperty("MoveOverride",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(player.Input,value);
    private static void Click(string name) => Object.FindObjectsByType<Button>(FindObjectsInactive.Include,FindObjectsSortMode.None).Single(x=>x.name==name).onClick.Invoke();
}
