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
public static class ModuleJoinReview
{
    private const string Pending = "Emberdelve.ModuleJoinReview";
    static ModuleJoinReview()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending, false)) return;
            SessionState.SetBool(Pending, false);
            var root = new GameObject("ModuleJoinReview");
            Object.DontDestroyOnLoad(root);
            root.AddComponent<ModuleJoinReviewRunner>();
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

public sealed class ModuleJoinReviewRunner : MonoBehaviour
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
            catch (Exception ex) { recorder?.StopRecording(); Debug.LogError("[ModuleJoinReview] FAIL " + ex); EditorApplication.Exit(1); yield break; }
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
        output = Path.GetFullPath("Logs/acceptance/module-joins-20261122/live-" + seed + "-" + tag);
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
        movie.CaptureAudio=true; movie.OutputFile=Path.Combine(output,"module-assembly-review").Replace('\\','/');
        var config=ScriptableObject.CreateInstance<RecorderControllerSettings>(); config.AddRecorderSettings(movie);
        config.SetRecordModeToManual(); config.FrameRate=30; config.CapFrameRate=true;
        recorder=new RecorderController(config); recorder.PrepareRecording();
        if(!recorder.StartRecording()) throw new Exception("Recorder failed");
        int start=Time.frameCount; float health=player.CurrentHealth;
        bool doorsOnly=tag.StartsWith("door-final",StringComparison.Ordinal);
        if(!doorsOnly)
        {
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
        }
        yield return InspectAssembly(!doorsOnly && seed == "12345", doorsOnly);
        AssertNoAI();
        recorder.StopRecording(); recorder=null;
        File.WriteAllText(Path.Combine(output,"result.txt"), "PASS seed="+seed+" proof="+(doorsOnly?"door-motion":"traversal-and-assembly")+" frames="+(Time.frameCount-start)+" viewport="+Screen.width+"x"+Screen.height+" minY="+low+" maxY="+high+" health="+player.CurrentHealth+" AI=0\nNormal new Solo raid, saved AI off. "+(doorsOnly?"Labelled free-camera door inspection; no traversal claim.":"Ordinary walking proof followed by labelled free-camera assembly inspection.")+" No player teleport, AI deactivation or lighting override.\n");
        Debug.Log("[ModuleJoinReview] PASS seed="+seed);
    }
    private string inspectionLabel;
    private void OnGUI()
    {
        if(string.IsNullOrEmpty(inspectionLabel)) return;
        GUI.Box(new Rect(10,90,650,30), inspectionLabel);
    }
    private IEnumerator InspectAssembly(bool catalog, bool doorsOnly)
    {
        var original=player.GetComponentsInChildren<Camera>().First(x=>x.enabled);
        var go=new GameObject("AssemblyInspectionCamera"); var camera=go.AddComponent<Camera>(); camera.CopyFrom(original);
        camera.nearClipPlane=.08f; camera.farClipPlane=150; camera.enabled=true;
        var data=go.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();data.renderPostProcessing=true;
        original.enabled=false;
        // Free-camera views are explicitly marked inspection, separate from the preceding walking proof.
        if(!doorsOnly) foreach(var module in Object.FindObjectsByType<DungeonModuleDefinition>(FindObjectsSortMode.None).OrderBy(x=>x.name))
        {
            inspectionLabel="LIVE MAP / "+module.name;
            yield return InspectView(camera,module.transform,new Vector3(-6,1.75f,-12),new Vector3(3,3,6),"map-"+module.name,18);
        }
        var door=Object.FindObjectsByType<Emberdelve.Interaction.DungeonDoorInteractable>(FindObjectsSortMode.None).Where(x=>x.AllowsEnemyOpening).OrderBy(x=>(x.transform.position-player.transform.position).sqrMagnitude).First();
        for(int state=0;state<4;state++)
        {
            bool open=state==1 || state==2;if(door.IsOpen!=open)door.ToggleAuthoritatively();
            inspectionLabel="LIVE DOOR / "+(open?"OPEN":"CLOSED")+" / "+(state<2?"FRONT":"BACK");
            yield return InspectView(camera,door.transform,new Vector3(state<2?-1.6f:1.6f,1.7f,state<2?-4.5f:4.5f),new Vector3(0,1.65f,0),"door-"+state,36);
        }
        if(!doorsOnly)
        {
        var gate=Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).First(x=>x.name=="Gate_Portal");
        inspectionLabel="LIVE EXTRACTION GATE / FLOOR-TO-LINTEL FIT";
        yield return InspectView(camera,gate.parent,new Vector3(-3,1.7f,-6),new Vector3(0,2,0),"extraction-gate",30);
        }
        if(!catalog) { original.enabled=true;Object.Destroy(go);inspectionLabel=null;yield break; }
        // Catalog inspection uses the saved prefab itself in the live scene lighting, one at a time.
        // It is geometry inspection, not a claim that the player walked all 30 prefabs.
        foreach(var guid in AssetDatabase.FindAssets("t:Prefab",new[]{"Assets/Emberdelve/Prefabs/DungeonModules"}).OrderBy(x=>AssetDatabase.GUIDToAssetPath(x)))
        {
            string path=AssetDatabase.GUIDToAssetPath(guid);var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var instance=Object.Instantiate(prefab,new Vector3(500,0,500),Quaternion.identity);
            inspectionLabel="SAVED MODULE / "+prefab.name;
            yield return InspectView(camera,instance.transform,new Vector3(0,1.75f,-13),new Vector3(-10,3,3),"catalog-"+prefab.name+"-gallery",15);
            yield return InspectView(camera,instance.transform,new Vector3(10,1.75f,-15.8f),new Vector3(14.5f,-.3f,-8),"catalog-"+prefab.name+"-descent",15);
            yield return InspectView(camera,instance.transform,new Vector3(0,1.75f,10),new Vector3(0,2,-18),"catalog-"+prefab.name+"-port",15);
            yield return InspectView(camera,instance.transform,new Vector3(-8,1.75f,6),new Vector3(17,3,0),"catalog-"+prefab.name+"-wall",15);
            Object.Destroy(instance);yield return null;
        }
        original.enabled=true;Object.Destroy(go);inspectionLabel=null;
    }
    private IEnumerator InspectView(Camera camera,Transform basis,Vector3 from,Vector3 to,string name,int frames)
    {
        camera.transform.position=basis.TransformPoint(from);
        camera.transform.rotation=Quaternion.LookRotation(basis.TransformPoint(to)-camera.transform.position);
        for(int i=0;i<frames;i++)
        {
            Vector3 offset=Vector3.right*((float)i/Mathf.Max(1,frames-1)-.5f)*.35f;
            camera.transform.position=basis.TransformPoint(from+offset);
            camera.transform.rotation=Quaternion.LookRotation(basis.TransformPoint(to)-camera.transform.position);
            yield return null;
        }
        ScreenCapture.CaptureScreenshot(Path.Combine(output,name+".png"));yield return null;
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
