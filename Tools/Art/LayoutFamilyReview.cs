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
public static class LayoutFamilyReview
{
    private const string Pending = "Emberdelve.LayoutFamilyReview";
    static LayoutFamilyReview()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending, false)) return;
            SessionState.SetBool(Pending, false);
            var root = new GameObject("LayoutFamilyReview");
            Object.DontDestroyOnLoad(root);
            root.AddComponent<LayoutFamilyReviewRunner>();
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

public sealed class LayoutFamilyReviewRunner : MonoBehaviour
{
    private RecorderController recorder;
    private Emberdelve.Player.PlayerRig player;
    private Transform room;
    private string output;
    private float low, high;
    private int captures;
    private bool cornersOnly;
    private bool diagnosticLight, inspectionLightActive;
    private IEnumerator Start()
    {
        var stack = new System.Collections.Generic.Stack<IEnumerator>(); stack.Push(Review());
        while (stack.Count > 0)
        {
            var current = stack.Peek(); bool more;
            try { more = current.MoveNext(); }
            catch (Exception ex) { recorder?.StopRecording(); Debug.LogError("[LayoutFamilyReview] FAIL " + ex); EditorApplication.Exit(1); yield break; }
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
        cornersOnly=tag.StartsWith("corners-final",StringComparison.Ordinal);
        diagnosticLight=tag.StartsWith("geometry-final",StringComparison.Ordinal);
        output = Path.GetFullPath("Logs/acceptance/layout-families-20261122/live-" + seed + "-" + tag);
        Directory.CreateDirectory(output); yield return null;
        var movie = ScriptableObject.CreateInstance<MovieRecorderSettings>();
        movie.name="Traversal inspection"; movie.Enabled=true;
        movie.ImageInputSettings=new GameViewInputSettings { OutputWidth=1280, OutputHeight=720 };
        movie.EncoderSettings=new CoreEncoderSettings { Codec=CoreEncoderSettings.OutputCodec.MP4, EncodingQuality=CoreEncoderSettings.VideoEncodingQuality.High };
        movie.CaptureAudio=true; movie.OutputFile=Path.Combine(output,"layout-family-review").Replace('\\','/');
        var config=ScriptableObject.CreateInstance<RecorderControllerSettings>(); config.AddRecorderSettings(movie);
        config.SetRecordModeToManual(); config.FrameRate=30; config.CapFrameRate=true;
        recorder=new RecorderController(config); recorder.PrepareRecording();
        if(!recorder.StartRecording()) throw new Exception("Recorder failed");
        int start=Time.frameCount;
        inspectionLabel="LOBBY / EXISTING MESHY BRAZIERS";
        for(int i=0;i<75;i++)yield return null;
        ScreenCapture.CaptureScreenshot(Path.Combine(output,"lobby.png"));yield return null;
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
            .Where(x => x.Kind == DungeonModuleKind.Entry)
            .OrderBy(x => (x.transform.position-player.transform.position).sqrMagnitude).First().transform;
        inspectionLabel="PLAYER / ARRIVAL COURT AND DAIS";
        float health=player.CurrentHealth;
        bool doorsOnly=tag.StartsWith("door-final",StringComparison.Ordinal);
        if(!doorsOnly)
        {
        // Normal player input: arrival court -> raised welcome dais -> court.
        yield return Navigate(new Vector3(0,0,5));
        yield return Hold("arrival-court",30);
        yield return Walk(new Vector3(0,.9f,10.5f),20);
        yield return Hold("welcome-dais",30);
        yield return Walk(new Vector3(0,0,5),20);
        AssertNoAI();
        if(high<.8f || !player.IsInPlay || player.CurrentHealth<health-.01f)throw new Exception("Arrival dais traversal failed");
        }
        yield return InspectAssembly(!doorsOnly && seed == "12345", doorsOnly);
        AssertNoAI();
        recorder.StopRecording(); recorder=null;
        File.WriteAllText(Path.Combine(output,"result.txt"), "PASS seed="+seed+" proof="+(doorsOnly?"door-motion":"traversal-and-assembly")+" frames="+(Time.frameCount-start)+" viewport="+Screen.width+"x"+Screen.height+" minY="+low+" maxY="+high+" health="+player.CurrentHealth+" AI=0\nNormal new Solo raid, saved AI off. "+(doorsOnly?"Labelled free-camera door inspection; no traversal claim.":"Ordinary walking proof followed by labelled free-camera assembly inspection.")+" No player teleport or AI deactivation. "+(diagnosticLight?"An explicitly labelled camera light is used for seam/catalogue geometry inspection after ordinary gameplay and map views.":"No lighting override.")+"\n");
        Debug.Log("[LayoutFamilyReview] PASS seed="+seed);
    }
    private string inspectionLabel;
    private void OnGUI()
    {
        if(string.IsNullOrEmpty(inspectionLabel)) return;
        GUI.Box(new Rect(10,90,1000,30), (inspectionLightActive?"DIAGNOSTIC LIGHT | ":"")+inspectionLabel);
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
            for(int side=0;side<4;side++)
            {
                var yaw=Quaternion.Euler(0,side*90,0);
                yield return InspectView(camera,module.transform,yaw*new Vector3(0,1.75f,-14),yaw*new Vector3(0,2,0),"map-"+module.name+"-"+side,18);
            }
        }
        if(diagnosticLight)
        {
            var lamp=go.AddComponent<Light>();lamp.type=LightType.Point;lamp.color=Color.white;
            lamp.intensity=8;lamp.range=14;lamp.shadows=LightShadows.Soft;
            inspectionLightActive=true;
        }
        if(!doorsOnly) foreach(var seam in Object.FindObjectsByType<Emberdelve.Interaction.DungeonDoorInteractable>(FindObjectsSortMode.None).Where(x=>x.AllowsEnemyOpening).OrderBy(x=>x.name))
        {
            inspectionLabel="LIVE MODULE SEAM / "+seam.name;
            for(int face=0;face<2;face++)
            {
                float sign=face==0?-1:1;
                yield return InspectView(camera,seam.transform,new Vector3(1.8f*sign,1.7f,4.5f*sign),new Vector3(0,2.2f,0),"seam-"+seam.name+"-"+face,20);
                yield return InspectView(camera,seam.transform,new Vector3(-1.8f*sign,2.2f,4.5f*sign),new Vector3(0,1.8f,0),"seam-"+seam.name+"-"+face+"-opposite",20);
            }
        }
        foreach(var door in Object.FindObjectsByType<Emberdelve.Interaction.DungeonDoorInteractable>(FindObjectsSortMode.None)
            .Where(x=>x.AllowsEnemyOpening).OrderBy(x=>(x.transform.position-player.transform.position).sqrMagnitude)
            .GroupBy(x=>Mathf.RoundToInt(x.transform.eulerAngles.y)%180).Select(g=>g.First()))
        for(int state=0;state<4;state++)
        {
            bool open=state==1 || state==2;if(door.IsOpen!=open)door.ToggleAuthoritatively();
            inspectionLabel="LIVE DOOR / "+(open?"OPEN":"CLOSED")+" / "+(state<2?"FRONT":"BACK");
            yield return InspectView(camera,door.transform,new Vector3(state<2?-1.6f:1.6f,1.7f,state<2?-4.5f:4.5f),new Vector3(0,1.65f,0),"door-"+door.name+"-"+state,36);
        }
        if(!doorsOnly)
        {
        var gate=Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).First(x=>x.name=="Gate_Portal");
        inspectionLabel="LIVE EXTRACTION GATE / FLOOR-TO-LINTEL FIT";
        yield return InspectView(camera,gate.parent,new Vector3(-3,1.7f,-6),new Vector3(0,2,0),"extraction-gate",30);
        }
        if(!catalog) { original.enabled=true;Object.Destroy(go);inspectionLabel=null;inspectionLightActive=false;yield break; }
        // Catalog inspection uses the saved prefab itself in the live scene lighting, one at a time.
        // It is geometry inspection, not a claim that the player walked all 30 prefabs.
        foreach(var guid in AssetDatabase.FindAssets("t:Prefab",new[]{"Assets/Emberdelve/Prefabs/DungeonModules"}).OrderBy(x=>AssetDatabase.GUIDToAssetPath(x)))
        {
            string path=AssetDatabase.GUIDToAssetPath(guid);var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var instance=Object.Instantiate(prefab,new Vector3(500,0,500),Quaternion.identity);
            inspectionLabel="SAVED MODULE / "+prefab.name;
            var definition=instance.GetComponent<DungeonModuleDefinition>();
            foreach(var direction in new[]{DungeonModuleSockets.North,DungeonModuleSockets.East,DungeonModuleSockets.South,DungeonModuleSockets.West})
            {
                definition.Closure(direction).gameObject.SetActive(false);
                var socket=definition.Socket(direction);
                var rotation=Quaternion.Euler(0,direction==DungeonModuleSockets.North || direction==DungeonModuleSockets.South?0:90,0);
                var doorway=Object.Instantiate(Resources.Load<GameObject>(DungeonDoorwaySpawner.PrefabResource),socket.position,rotation,instance.transform);
                doorway.name="Inspection_Doorway_"+direction;
            }
            yield return InspectView(camera,instance.transform,new Vector3(0,7.4f,-12),new Vector3(0,0,3),"catalog-"+prefab.name+"-overview",18);
            if(diagnosticLight)yield return InspectCorners(camera,instance);
            if(cornersOnly)
            {
                yield return InspectCorners(camera,instance);
                foreach(var stairs in instance.GetComponentsInChildren<Transform>().Where(t=>t.name.StartsWith("Layout_Stairs_Pit") || t.name.StartsWith("Undercroft_") && t.name.EndsWith("Stairs")))
                {
                    var steps=stairs.Cast<Transform>().Where(t=>t.name.StartsWith("Step_") || t.name.StartsWith("Tread_")).ToArray();
                    if(steps.Length==0)continue;
                    float rise=steps.Length*.15f,run=steps.Length*.3f;
                    inspectionLabel="FINAL PIT JOINS / "+prefab.name+" / "+stairs.name;
                    yield return InspectView(camera,stairs,new Vector3(.5f,1.65f,-.8f),new Vector3(0,rise,run),"final-pit-"+prefab.name+"-"+stairs.name+"-up",30);
                    yield return InspectView(camera,stairs,new Vector3(-.5f,rise+1.65f,run+.5f),new Vector3(0,0,0),"final-pit-"+prefab.name+"-"+stairs.name+"-down",30);
                }
                if(prefab.name.EndsWith("Shrine",StringComparison.Ordinal))
                    yield return InspectView(camera,instance.transform,new Vector3(0,1.7f,1),new Vector3(0,2,5),"final-shrine-columns-"+prefab.name,24);
                Object.Destroy(instance);yield return null;continue;
            }
            for(int side=0;side<4;side++)
            {
                var yaw=Quaternion.Euler(0,side*90,0);
                yield return InspectView(camera,instance.transform,yaw*new Vector3(0,1.75f,-14),yaw*new Vector3(0,2,3),"catalog-"+prefab.name+"-room-"+side,15);
                // Both near jamb/floor joins and the inner side of each perimeter seam.
                yield return InspectView(camera,instance.transform,yaw*new Vector3(-2.7f,1.75f,-14),yaw*new Vector3(0,2,-18),"catalog-"+prefab.name+"-port-"+side,12);
            }
            foreach(var stairs in instance.GetComponentsInChildren<Transform>().Where(t=>t.name.StartsWith("Layout_Stairs_") || t.name.EndsWith("Stairs")))
            {
                var steps=stairs.Cast<Transform>().Where(t=>t.name.StartsWith("Step_") || t.name.StartsWith("Tread_")).ToArray();
                if(steps.Length==0)continue;
                float rise=steps.Length*.15f,run=steps.Length*.3f;
                inspectionLabel="SAVED MODULE / STAIR LANDINGS / "+prefab.name+" / "+stairs.name;
                yield return InspectView(camera,stairs,new Vector3(.5f,1.65f,-.8f),new Vector3(0,rise,run),"stairs-"+prefab.name+"-"+stairs.name+"-up",24);
                yield return InspectView(camera,stairs,new Vector3(-.5f,rise+1.65f,run+.5f),new Vector3(0,0,0),"stairs-"+prefab.name+"-"+stairs.name+"-down",24);
            }
            int detail=0;
            foreach(var arch in instance.GetComponentsInChildren<Transform>().Where(t=>t.name=="Reused_Stone_Arch"))
            {
                inspectionLabel="SAVED MODULE / ARCH BEARING / "+prefab.name;
                foreach(string side in new[]{"Left","Right"})
                {
                    var cap=arch.Find("Meshy_Architecture_Visual/Arch_Bearing_"+side);
                    if(cap==null)throw new Exception("Missing bearing "+prefab.name+" "+side);
                    Vector3 target=instance.transform.InverseTransformPoint(cap.GetComponent<Renderer>().bounds.center);
                    Vector3 inward=new Vector3(-target.x,0,-target.z).normalized;
                    Vector3 from=target+inward*3.2f+new Vector3(0,.25f,0);
                    from.x=Mathf.Clamp(from.x,-16,16);from.z=Mathf.Clamp(from.z,-16,16);
                    yield return InspectView(camera,instance.transform,from,target,"catalog-"+prefab.name+"-arch-"+detail+"-"+side,18);
                }
                detail++;
            }
            Object.Destroy(instance);yield return null;
        }
        original.enabled=true;Object.Destroy(go);inspectionLabel=null;inspectionLightActive=false;
    }
    private IEnumerator InspectCorners(Camera camera,GameObject instance)
    {
        var walls=instance.GetComponentsInChildren<MeshRenderer>().Where(r=>r.enabled && r.name=="Layout_Wall").ToArray();
        int index=0;
        for(int a=0;a<walls.Length;a++)for(int b=a+1;b<walls.Length;b++)
        {
            Bounds x=walls[a].bounds,y=walls[b].bounds;
            if(Mathf.Abs(x.max.y-y.max.y)>.001f)continue;
            Vector3 min=Vector3.Max(x.min,y.min),max=Vector3.Min(x.max,y.max),span=max-min;
            if(span.x<-.001f || span.z<-.001f || Mathf.Max(span.x,span.z)<.05f)continue;
            if(span.x>.001f && span.z>.001f)throw new Exception("Exposed wall tops overlap: "+instance.name);
            Vector3 target=instance.transform.InverseTransformPoint(new Vector3((min.x+max.x)*.5f,x.max.y,(min.z+max.z)*.5f));
            Vector3 inward=new Vector3(-target.x,0,-target.z).normalized;
            inspectionLabel="SAVED MODULE / WALL CORNER / "+instance.name+" / "+index;
            yield return InspectView(camera,instance.transform,target+inward*2.5f+Vector3.up*1.2f,target,"corner-"+instance.name+"-"+(index++),18);
        }
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
    private IEnumerator Navigate(Vector3 local)
    {
        // Follow actual baked room routes with normal movement input, including new partition detours.
        var grid=ScriptableObject.CreateInstance<Emberdelve.Core.NavGridData>();
        var waypoints=new System.Collections.Generic.List<Vector3>();
        try
        {
            grid.origin=room.position-new Vector3(18,0,18);grid.width=grid.height=18;grid.cellSize=2;grid.mask=new string('.',324);
            Physics.SyncTransforms();
            Emberdelve.AI.NavGridSurface.Bake(grid,room,LayerMask.GetMask(Emberdelve.Core.GameConfig.LevelLayerName));
            if(!new Emberdelve.AI.NavGridPathfinder(grid).TryFindPath(player.transform.position,room.TransformPoint(local),waypoints))
                throw new Exception("No real navigation route to inspection start");
        }
        finally { Object.Destroy(grid); }
        foreach(var point in waypoints)yield return Walk(room.InverseTransformPoint(point),16);
        yield return Walk(local,16);
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
