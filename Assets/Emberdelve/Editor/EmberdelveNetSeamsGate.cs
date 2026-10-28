using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.InputLayer;
using Emberdelve.Level;
using Emberdelve.Movement;
using Emberdelve.Net;
using Emberdelve.Net.Rules;
using Emberdelve.Player;
using Emberdelve.UI;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// U3A2 gate: the offline-preserving seams for networking, with NO transport and no NetworkManager. Real Level01 play mode.
    /// Proves: PlayerMovement.Step reproduces the Update path frame for frame (same dt, same intent, tolerance 1e-4) and
    /// ExternalDriven switches Update off; ViewRelativeDirection(yaw) equals the Transform overload; MoveInputFrame
    /// quantization; the layout's player spawns; RunDirector hosted-run join and remove (counts, sender table, party seats,
    /// combat registry, death hook, zero players keeps the run Running); pause leaves Time.timeScale alone online and
    /// restart/lobby are inert online; offline Start is unchanged; NetSceneRoles; the client-only PlayerRig view; and the
    /// generated net player prefab + catalog. Entry: <c>Emberdelve.EditorTools.EmberdelveNetSeamsGate.Run</c>.
    /// Final line: [Emberdelve][NetSeams] PASS|FAIL checks=N failures=M
    ///
    /// Needs the regenerated slice (data assets + Level01 scene built by this slice's builders) - see Docs/Tasks/U3A2.md.
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveNetSeamsGate
    {
        private const string Pending = "Emberdelve.NetSeams.Pending";
        private const string PrefabError = "Emberdelve.NetSeams.PrefabError";

        static EmberdelveNetSeamsGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("NetSeams_Gate").AddComponent<NetSeamsHarness>();
                }
            };
        }

        public static void Run()
        {
            // The prefab builder reads the saved Level01 scene; do it before the scene is opened for play.
            string error;
            SessionState.SetString(PrefabError, EmberdelveNetBuilder.EnsureNetPlayerPrefab(out error) ? "" : (error ?? "unknown"));

            SessionState.SetBool(Pending, true);
            EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_Level01.unity");
            EditorApplication.EnterPlaymode();
        }

        internal static string PrefabBuildError => SessionState.GetString(PrefabError, "");
    }

    public sealed class NetSeamsHarness : MonoBehaviour
    {
        private const float StepTolerance = 1e-4f;

        private int _n, _fail;
        private RunDirector _director;
        private PlayerRig _p1;
        private readonly List<GameObject> _temp = new List<GameObject>();
        private readonly List<KeyValuePair<string, int>> _events = new List<KeyValuePair<string, int>>();
        private int _pauseEvents;

        private void Check(bool ok, string text)
        {
            _n++;
            if (!ok)
            {
                _fail++;
                Debug.LogError("[NetSeams] mismatch " + text);
            }
        }

        private IEnumerator Start()
        {
            yield return null;
            yield return null;

            IEnumerator run = Execute();
            while (true)
            {
                bool more;
                try { more = run.MoveNext(); }
                catch (Exception e) { _fail++; Debug.LogException(e); break; }
                if (!more) break;
                yield return run.Current;
            }

            Cleanup();
            Debug.Log("[Emberdelve][NetSeams] " + (_fail == 0 ? "PASS" : "FAIL") + " checks=" + _n + " failures=" + _fail);
            EmberdelveGateExit.Exit(_fail == 0 ? 0 : 1);
        }

        private void Cleanup()
        {
            GameEvents.PlayerRunStateChanged -= OnPlayerRunState;
            GameEvents.RunEnded -= OnRunEnded;
            GameEvents.PauseChanged -= OnPause;
            Time.captureDeltaTime = 0f;
            Time.timeScale = 1f;
            NetRole.ResetToOffline();
            foreach (GameObject g in _temp) if (g != null) Destroy(g);
        }

        private void OnPlayerRunState(int id, RunState s) { _events.Add(new KeyValuePair<string, int>("runstate:" + s, id)); }
        private void OnRunEnded(int id, RunResult r) { _events.Add(new KeyValuePair<string, int>("runended", id)); }
        private void OnPause(bool paused) { _pauseEvents++; }

        private int EventsFor(string name, int id)
        {
            int count = 0;
            for (int i = 0; i < _events.Count; i++) if (_events[i].Key == name && _events[i].Value == id) count++;
            return count;
        }

        private static float Flat(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return (a - b).magnitude;
        }

        private IEnumerator Execute()
        {
            _p1 = FindFirstObjectByType<PlayerRig>();
            _director = FindFirstObjectByType<RunDirector>();
            Check(_p1 != null && _director != null, "scene has a rig and a run director");
            if (_p1 == null || _director == null) yield break;

            foreach (Emberdelve.AI.EnemyAgent a in FindObjectsByType<Emberdelve.AI.EnemyAgent>(FindObjectsSortMode.None)) a.enabled = false;

            GameEvents.PlayerRunStateChanged += OnPlayerRunState;
            GameEvents.RunEnded += OnRunEnded;
            GameEvents.PauseChanged += OnPause;

            yield return OfflineStartUnchanged();
            yield return PrefabAndCatalog();
            yield return LayoutSpawns();
            yield return FrameHelpers();
            yield return StepParity();
            yield return PauseAndSceneFlow();
            yield return SceneRoles();
            yield return ClientView();
            yield return HostedRunJoinRemove();
        }

        // ---- 1. offline Start is exactly the old Start ----------------------------------------------------

        private IEnumerator OfflineStartUnchanged()
        {
            Check(NetRole.IsOffline && NetRole.Current == NetRoleKind.Offline, "the process role is Offline");
            Check(!_director.IsServerMode, "an offline RunDirector is not in server mode");
            Check(_director.Players.Count == 1 && _director.LocalSession != null && _director.LocalSession.Rig == _p1,
                "offline Start joined the scene rig as the one local player");
            Check(_p1.IsLocallyControlled && _p1.Kind == EntityKind.LocalPlayer && _p1.PartyId == 0, "the scene rig is the local player of party 0");
            Check(_director.State == RunState.Running, "the run is Running after Start");
            Check(_director.Player == _p1 && _director.LocalPlayerId == _p1.PlayerId, "Player / LocalPlayerId name the scene rig");
            Check(_director.Chests != null, "offline Start built the containers");
            Check(Mathf.Approximately(Time.timeScale, 1f), "Time.timeScale is 1");
            Check(CombatRegistry.TryGet(_p1.CombatId, out ICombatEntity entity) && ReferenceEquals(entity, _p1), "the scene rig is registered in the combat registry");
            Check(CombatSenders.Owns(CombatSenderId.ForPlayer(_p1.PlayerId), _p1.CombatId), "the scene rig's sender is bound");
            Check(!_p1.Movement.ExternalDriven, "the scene rig's movement is self driven");
            Check(NetworkManager.Singleton == null, "no NetworkManager exists offline");
            Check(FindObjectsByType<NetworkObject>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length == 0, "no NetworkObject exists in the offline scene");
            Check(!_p1.SetNetworkPlayerId(55) && _p1.PlayerId != 55, "an offline rig refuses a network player id");
            yield return null;
        }

        // ---- 2. the generated prefab and its catalog ----------------------------------------------------------------

        private IEnumerator PrefabAndCatalog()
        {
            Check(string.IsNullOrEmpty(EmberdelveNetSeamsGate.PrefabBuildError), "the net player prefab builder succeeded (" + EmberdelveNetSeamsGate.PrefabBuildError + ")");

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(EmberdelveNetBuilder.PlayerPrefabPath);
            Check(prefab != null, "Emberdelve_NetPlayer.prefab exists");
            if (prefab != null)
            {
                NetworkObject networkObject = prefab.GetComponent<NetworkObject>();
                NetworkTransform networkTransform = prefab.GetComponent<NetworkTransform>();
                Check(networkObject != null, "prefab has a NetworkObject");
                Check(networkTransform != null, "prefab has a NetworkTransform");
                Check(prefab.GetComponent<NetPlayerRig>() != null, "prefab has a NetPlayerRig");
                Check(prefab.GetComponent<PlayerRig>() != null && prefab.GetComponent<PlayerMovement>() != null
                      && prefab.GetComponent<CharacterController>() != null && prefab.GetComponent<EmberdelveInput>() != null,
                    "prefab carries the whole Level01 rig (PlayerRig, PlayerMovement, CharacterController, EmberdelveInput)");
                Check(prefab.GetComponentInChildren<Camera>(true) != null, "prefab keeps the rig's camera child");
                Check(prefab.GetComponentsInChildren<NetworkObject>(true).Length == 1, "prefab has exactly one NetworkObject");

                if (networkTransform != null)
                {
                    Check(!networkTransform.SyncScaleX && !networkTransform.SyncScaleY && !networkTransform.SyncScaleZ, "NetworkTransform does not sync scale");
                    Check(networkTransform.Interpolate, "NetworkTransform interpolates");
                    Check(networkTransform.SyncPositionX && networkTransform.SyncPositionY && networkTransform.SyncPositionZ, "NetworkTransform syncs position");
                    Check(networkTransform.SyncRotAngleY && !networkTransform.SyncRotAngleX && !networkTransform.SyncRotAngleZ, "NetworkTransform syncs yaw only");

                    // Server authority is the NetworkTransform default; read it by name so this gate does not depend on the member.
                    FieldInfo authority = typeof(NetworkTransform).GetField("AuthorityMode", BindingFlags.Instance | BindingFlags.Public);
                    if (authority != null) Check(authority.GetValue(networkTransform).ToString() == "Server", "NetworkTransform is server authoritative");
                    else Debug.Log("[NetSeams] note: NetworkTransform has no public AuthorityMode field in this NGO version; server authority not asserted");
                }

                NetPrefabCatalog catalog = AssetDatabase.LoadAssetAtPath<NetPrefabCatalog>(EmberdelveNetBuilder.CatalogPath);
                Check(catalog != null && catalog.PlayerPrefab == prefab, "the prefab catalog asset points at the player prefab");
                Check(Resources.Load<NetPrefabCatalog>(NetPrefabCatalog.ResourcePath) != null, "the catalog loads through Resources (NetBootstrap's path)");
            }

            yield return null;
        }

        // ---- 3. layout player spawns -----------------------------------------------------------------------------------------

        private IEnumerator LayoutSpawns()
        {
            DungeonLayoutData layout = _director.Layout;
            Check(layout != null, "the run director has the layout");
            if (layout == null) yield break;

            List<Vector2Int> spawns = layout.playerSpawns;
            Check(spawns != null && spawns.Count >= DungeonLayoutData.MinPlayerSpawns, "the layout has at least " + DungeonLayoutData.MinPlayerSpawns + " player spawns (" + (spawns != null ? spawns.Count : 0) + ")");
            if (spawns == null) yield break;

            HashSet<Vector2Int> seen = new HashSet<Vector2Int>();
            bool allWalkable = true;
            bool distinct = true;
            int minSeparation = int.MaxValue;
            for (int i = 0; i < spawns.Count; i++)
            {
                Vector2Int c = spawns[i];
                if (!layout.IsWalkableCell(c)) allWalkable = false;
                if (!seen.Add(c)) distinct = false;
                for (int k = 0; k < i; k++)
                    minSeparation = Mathf.Min(minSeparation, Mathf.Max(Mathf.Abs(c.x - spawns[k].x), Mathf.Abs(c.y - spawns[k].y)));
            }

            Check(allWalkable, "every player spawn is walkable");
            Check(distinct, "player spawns are distinct");
            Check(minSeparation >= DungeonLayoutData.MinPlayerSpawnSeparationCells, "player spawns are spread (min separation " + minSeparation + " cells)");
            Check(spawns.Count > 0 && spawns[0] == layout.playerStartCell, "the first spawn is the single-player start cell");

            bool noContentCollision = true;
            foreach (EnemySpawnSpec e in layout.enemySpawns) if (seen.Contains(e.cell)) noContentCollision = false;
            foreach (ChestSpawnSpec c in layout.chestSpawns) if (seen.Contains(c.cell)) noContentCollision = false;
            foreach (LootSpawnSpec l in layout.lootSpawns) if (seen.Contains(l.cell)) noContentCollision = false;
            Check(noContentCollision, "no player spawn shares a cell with an enemy, chest or loot spawn");

            int levelMask = LayerMask.GetMask(GameConfig.LevelLayerName);
            bool allOnFloor = true;
            for (int i = 0; i < spawns.Count; i++)
            {
                Vector3 centre = layout.PlayerSpawnWorld(i);
                if (!Physics.Raycast(centre + Vector3.up * 2f, Vector3.down, out RaycastHit hit, 4f, levelMask) || Mathf.Abs(hit.point.y - centre.y) > 0.05f) allOnFloor = false;
            }
            Check(allOnFloor, "every spawn has level floor under its cell centre");

            List<string> problems = layout.Validate();
            bool spawnProblems = false;
            foreach (string p in problems) if (p.IndexOf("spawn", StringComparison.OrdinalIgnoreCase) >= 0 && p.IndexOf("player", StringComparison.OrdinalIgnoreCase) >= 0) spawnProblems = true;
            Check(!spawnProblems, "DungeonLayoutData.Validate reports no player spawn problem");

            DungeonLayoutData probe = ScriptableObject.CreateInstance<DungeonLayoutData>();
            probe.rooms.Add(new RoomSpec { minCell = new Vector2Int(0, 0), maxCell = new Vector2Int(8, 8) });
            probe.gridWidth = 10;
            probe.gridHeight = 10;
            probe.playerSpawns.Add(new Vector2Int(1, 1));
            probe.playerSpawns.Add(new Vector2Int(1, 1));
            probe.playerSpawns.Add(new Vector2Int(2, 1));
            probe.playerSpawns.Add(new Vector2Int(9, 9)); // outside every room
            string joined = string.Join("|", probe.Validate());
            Check(joined.Contains("duplicates") && joined.Contains("stacked") && joined.Contains("not walkable") && joined.Contains("at least"),
                "Validate flags a short, duplicated, stacked and unwalkable spawn list (" + joined + ")");
            Destroy(probe);

            Check(_director.TryResolveClass("warrior", out CharacterClassData warrior) && warrior != null && warrior.classId == "warrior", "the approved class list resolves 'warrior'");
            Check(_director.TryResolveClass("ARCHER", out CharacterClassData archer) && archer != null && archer.classId == "archer", "the approved class list resolves 'ARCHER' case-insensitively");
            Check(!_director.TryResolveClass("necromancer", out _) && !_director.TryResolveClass("", out _) && !_director.TryResolveClass(null, out _), "unknown, empty and null class ids resolve to nothing");
            yield return null;
        }

        // ---- 4. MoveInputFrame + direction helpers ----------------------------------------------------------------------------------

        private IEnumerator FrameHelpers()
        {
            Check(MoveInputFrame.QuantizeAxis(1f) == 127 && MoveInputFrame.QuantizeAxis(-1f) == -127 && MoveInputFrame.QuantizeAxis(0f) == 0, "axis quantization hits +-127 and 0");
            Check(MoveInputFrame.QuantizeAxis(float.NaN) == 0 && MoveInputFrame.QuantizeAxis(float.PositiveInfinity) == 0, "non-finite axes quantize to 0");
            Check(MoveInputFrame.QuantizeAxis(7f) == 127 && MoveInputFrame.QuantizeAxis(-7f) == -127, "out of range axes clamp");
            Check(Mathf.Abs(MoveInputFrame.DequantizeAxis(MoveInputFrame.QuantizeAxis(0.5f)) - 0.5f) < 0.005f, "axis round trip is within half a step");
            Check(Mathf.Abs(MoveInputFrame.DequantizeAngle(MoveInputFrame.QuantizeAngle(123.4f)) - 123.4f) < 0.01f, "angle round trip is within 0.01 degrees");
            Check(Mathf.Abs(MoveInputFrame.WrapDegrees(540f) - -180f) < 1e-3f && Mathf.Abs(MoveInputFrame.WrapDegrees(-190f) - 170f) < 1e-3f, "WrapDegrees wraps into [-180, 180)");

            MoveInputFrame bad = MoveInputFrame.Create(5, float.NaN, 0f, 0f, 0f, false, false, false);
            Check(!bad.IsWellFormed, "a NaN axis is malformed");
            MoveInputFrame unknownFlag = new MoveInputFrame { Flags = 0x80 };
            Check(!unknownFlag.IsWellFormed && (unknownFlag.Quantized().Flags & 0x80) == 0, "an unknown flag bit is malformed and masked by Quantized()");
            MoveInputFrame good = MoveInputFrame.Create(9, 0.7071f, 0.7071f, 200f, 120f, true, true, false);
            MoveInputFrame q = good.Quantized();
            Check(good.IsWellFormed && q.Seq == 9 && q.Sprint && q.Jump && !q.CrouchToggle && q.Pitch <= MoveInputFrame.DefaultMaxPitch + 1e-3f && Mathf.Abs(q.Yaw - -160f) < 0.01f,
                "Quantized() keeps seq and flags, clamps pitch and wraps yaw");

            float[] yaws = { 0f, 37f, 90f, -120f, 270f };
            Vector2[] axes = { Vector2.up, Vector2.right, new Vector2(-0.7f, -0.7f), new Vector2(0.3f, 0.9f), Vector2.zero };
            bool allSame = true;
            GameObject view = new GameObject("NetSeams_View");
            _temp.Add(view);
            for (int i = 0; i < yaws.Length; i++)
            {
                view.transform.rotation = Quaternion.Euler(20f, yaws[i], 0f); // pitched: the Transform overload flattens it
                for (int k = 0; k < axes.Length; k++)
                {
                    Vector3 a = PlayerMovement.ViewRelativeDirection(axes[k], view.transform);
                    Vector3 b = PlayerMovement.ViewRelativeDirection(axes[k], yaws[i]);
                    if ((a - b).magnitude > 1e-5f) allSame = false;
                }
            }
            Check(allSame, "ViewRelativeDirection(raw, yaw) equals the Transform overload for every sampled yaw and axis");
            yield return null;
        }

        // ---- 5. Step parity ----------------------------------------------------------------------------------------------------------

        private struct FrameSpec
        {
            public float X, Y;
            public bool Sprint, Jump, Crouch;
        }

        private static FrameSpec SpecAt(int i)
        {
            FrameSpec f = new FrameSpec();
            if (i < 30) { f.X = 0f; f.Y = 1f; }
            else if (i < 60) { f.X = 0.7071f; f.Y = 0.7071f; f.Sprint = true; }
            else if (i < 90) { f.X = 1f; f.Y = 0f; f.Jump = i == 60; }
            else if (i < 120) { f.X = -0.7071f; f.Y = -0.7071f; f.Crouch = i == 90; }
            else { f.Crouch = i == 120; }
            return f;
        }

        private IEnumerator StepParity()
        {
            PlayerMovement mv = _p1.Movement;
            EmberdelveInput input = _p1.Input;
            DungeonLayoutData layout = _director.Layout;
            const int Frames = 150;
            const float Yaw = 30f;
            Vector3 start = layout.CellCenterWorld(layout.playerStartCell) + Vector3.up * 0.15f;

            _p1.SetInputEnabled(true);
            mv.enabled = true;
            Time.captureDeltaTime = 1f / 60f;

            // ---- run A: the real Update path, device input replaced by the input seams, the render frame's dt recorded
            BeginRun(mv, start, Yaw);
            mv.ExternalDriven = false;
            List<float> dts = new List<float>();
            List<Vector3> posA = new List<Vector3>();
            List<bool> crouchA = new List<bool>();
            for (int i = 0; i < Frames; i++)
            {
                FrameSpec f = SpecAt(i);
                input.MoveOverride = new Vector2(f.X, f.Y);
                input.SprintHeldOverride = f.Sprint;
                input.JumpPressedOverride = f.Jump;
                input.CrouchPressedOverride = f.Crouch;
                yield return null;
                dts.Add(Time.deltaTime);
                posA.Add(_p1.transform.position);
                crouchA.Add(mv.IsCrouched);
            }
            input.MoveOverride = null;
            input.SprintHeldOverride = null;
            input.JumpPressedOverride = null;
            input.CrouchPressedOverride = null;
            Time.captureDeltaTime = 0f;

            // ---- run B: the same intent frames through Step with the recorded dts, from the same start state
            BeginRun(mv, start, Yaw);
            List<Vector3> posB = new List<Vector3>();
            List<bool> crouchB = new List<bool>();
            for (int i = 0; i < Frames; i++)
            {
                FrameSpec f = SpecAt(i);
                MoveInputFrame frame = MoveInputFrame.Create((uint)(i + 1), f.X, f.Y, Yaw, 0f, f.Sprint, f.Jump, f.Crouch);
                mv.Step(in frame, dts[i]);
                posB.Add(_p1.transform.position);
                crouchB.Add(mv.IsCrouched);
            }

            float maxDiff = 0f;
            bool crouchSame = true;
            float maxHeight = 0f;
            bool wasCrouched = false;
            for (int i = 0; i < Frames; i++)
            {
                maxDiff = Mathf.Max(maxDiff, (posA[i] - posB[i]).magnitude);
                if (crouchA[i] != crouchB[i]) crouchSame = false;
                maxHeight = Mathf.Max(maxHeight, posA[i].y - start.y);
                if (crouchA[i]) wasCrouched = true;
            }

            Check(dts.Count == Frames && posA.Count == Frames && posB.Count == Frames, "both runs recorded " + Frames + " frames");
            Check(maxDiff <= StepTolerance, "Step reproduces the Update path: max position difference " + maxDiff.ToString("0.000000") + " <= " + StepTolerance);
            Check(crouchSame && wasCrouched, "the crouch toggle edge reproduces and was exercised");
            Check(maxHeight > 0.05f, "the jump edge was exercised (peak " + maxHeight.ToString("0.00") + " m)");
            Check(Flat(posA[Frames - 1], start) > 0.5f || Flat(posA[59], start) > 0.5f, "the run actually travelled (" + Flat(posA[59], start).ToString("0.00") + " m after 60 frames)");

            // ---- ExternalDriven turns Update off
            BeginRun(mv, start, Yaw);
            mv.ExternalDriven = true;
            Vector3 before = _p1.transform.position;
            input.MoveOverride = Vector2.up;
            yield return null; yield return null; yield return null;
            Check(Flat(before, _p1.transform.position) < 1e-5f, "with ExternalDriven set, Update does not move the character");
            input.MoveOverride = null;
            mv.ExternalDriven = false;

            // ---- Step refuses garbage
            mv.ExternalDriven = true;
            Vector3 p = _p1.transform.position;
            MoveInputFrame nan = MoveInputFrame.Create(1, float.NaN, float.PositiveInfinity, float.NaN, 0f, true, false, false);
            mv.Step(in nan, 1f / 60f);
            mv.Step(in nan, float.NaN);
            mv.Step(in nan, -1f);
            Check(IsFinite(_p1.transform.position) && Flat(p, _p1.transform.position) < 0.01f, "a non-finite frame and non-finite dt leave a finite position");
            mv.ExternalDriven = false;

            Time.captureDeltaTime = 0f;
            BeginRun(mv, start, 0f);
            mv.ExternalDriven = false;
            yield return null;
        }

        private static bool IsFinite(Vector3 v) => MoveInputFrame.IsFinite(v.x) && MoveInputFrame.IsFinite(v.y) && MoveInputFrame.IsFinite(v.z);

        /// <summary>Teleports to the start, faces the yaw, and settles with 30 zero-intent Step frames so both runs begin from the same steady state.</summary>
        private void BeginRun(PlayerMovement mv, Vector3 start, float yaw)
        {
            mv.ExternalDriven = true;
            mv.SetCrouched(false);
            mv.TeleportTo(start);
            _p1.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            Physics.SyncTransforms();
            MoveInputFrame zero = new MoveInputFrame { Yaw = yaw };
            for (int i = 0; i < 40; i++) mv.Step(in zero, 1f / 60f);
        }

        // ---- 6. pause and scene flow ----------------------------------------------------------------------------------------------

        private IEnumerator PauseAndSceneFlow()
        {
            // offline: unchanged
            _pauseEvents = 0;
            _director.TogglePause();
            Check(Mathf.Approximately(Time.timeScale, 0f) && _director.State == RunState.Paused, "offline pause stops the clock and sets the run state Paused");
            _director.TogglePause();
            Check(Mathf.Approximately(Time.timeScale, 1f) && _director.State == RunState.Running, "offline unpause restores the clock and Running");
            Check(_pauseEvents == 2, "offline pause raised its UI events");
            Check(_director.SceneFlowAllowed, "offline restart / lobby are allowed");

            foreach (NetRoleKind role in new[] { NetRoleKind.Host, NetRoleKind.DedicatedServer, NetRoleKind.Client })
            {
                NetRole.Set(role);
                _pauseEvents = 0;
                _director.TogglePause();
                bool clockUntouched = Mathf.Approximately(Time.timeScale, 1f);
                bool stateUntouched = _director.State == RunState.Running;
                _director.TogglePause();
                Check(clockUntouched && stateUntouched && Mathf.Approximately(Time.timeScale, 1f) && _director.State == RunState.Running,
                    role + ": pause leaves Time.timeScale and the run state alone");
                Check(_pauseEvents == 2, role + ": pause is still a local UI event");
                Check(!_director.SceneFlowAllowed, role + ": restart / lobby are not allowed");
            }

            NetRole.Set(NetRoleKind.Host);
            string sceneBefore = SceneManager.GetActiveScene().name;
            _director.RestartRun();
            _director.ReturnToLobby();
            yield return null; yield return null;
            Check(SceneManager.GetActiveScene().name == sceneBefore && _director != null && Mathf.Approximately(Time.timeScale, 1f),
                "online RestartRun / ReturnToLobby are ignored (the scene stays loaded)");
            NetRole.ResetToOffline();
            _p1.SetInputEnabled(true);
        }

        // ---- 7. NetSceneRoles ---------------------------------------------------------------------------------------------------------

        private IEnumerator SceneRoles()
        {
            NetSceneRoles roles = FindFirstObjectByType<NetSceneRoles>(FindObjectsInactive.Include);
            Check(roles != null, "Level01 has a NetSceneRoles object (rebuild the dungeon scene if not)");
            if (roles != null)
            {
                bool rigListed = Array.IndexOf(roles.DeactivateWhenNetworked ?? new GameObject[0], _p1.gameObject) >= 0;
                bool masterListed = Array.IndexOf(roles.DeactivateOnClient ?? new GameObject[0], _director.gameObject) >= 0;
                bool hudListed = false;
                foreach (Behaviour b in roles.DisableOnDedicatedServer ?? new Behaviour[0]) if (b is HudController) hudListed = true;
                Check(rigListed, "NetSceneRoles lists the scene rig for every networked role");
                Check(masterListed, "NetSceneRoles lists the game master (director, authority, HUD) for clients");
                Check(hudListed, "NetSceneRoles lists the HUD for the dedicated server");
                Check(_p1.gameObject.activeSelf && _director.gameObject.activeSelf, "offline: nothing was deactivated by NetSceneRoles");
            }

            // Apply on fresh objects, per role.
            for (int pass = 0; pass < 4; pass++)
            {
                NetRoleKind role = (NetRoleKind)pass;
                GameObject networked = new GameObject("NS_networked");
                GameObject client = new GameObject("NS_client");
                GameObject serverHost = new GameObject("NS_serverhost");
                _temp.Add(networked); _temp.Add(client); _temp.Add(serverHost);
                Light disableMe = serverHost.AddComponent<Light>();

                GameObject holder = new GameObject("NS_holder");
                _temp.Add(holder);
                NetSceneRoles probe = holder.AddComponent<NetSceneRoles>(); // Awake applies the CURRENT role (Offline): a no-op
                Check(networked.activeSelf && client.activeSelf && disableMe.enabled, "NetSceneRoles.Awake offline changes nothing");
                probe.Configure(new[] { networked }, new[] { client }, new Behaviour[] { disableMe });
                probe.Apply(role);

                bool expectNetworked = role != NetRoleKind.Offline;
                bool expectClient = role == NetRoleKind.Client;
                bool expectDisable = role == NetRoleKind.DedicatedServer;
                Check(networked.activeSelf == !expectNetworked, role + ": networked list " + (expectNetworked ? "deactivated" : "untouched"));
                Check(client.activeSelf == !expectClient, role + ": client list " + (expectClient ? "deactivated" : "untouched"));
                Check(disableMe.enabled == !expectDisable, role + ": dedicated-server component list " + (expectDisable ? "disabled" : "untouched"));
            }

            yield return null;
        }

        // ---- 8. a client-only rig is a view, not a combat entity ------------------------------------------------------------------

        private IEnumerator ClientView()
        {
            int registryBefore = CombatRegistry.All.Count;
            int sendersBefore = CombatSenders.BoundCount;

            NetRole.Set(NetRoleKind.Client);
            GameObject clone = Instantiate(_p1.gameObject, new Vector3(3000f, 0.15f, 3000f), Quaternion.identity);
            _temp.Add(clone);
            PlayerRig view = clone.GetComponent<PlayerRig>();
            Check(view != null, "a client process can instantiate the rig");
            if (view != null)
            {
                Check(CombatRegistry.All.Count == registryBefore && CombatSenders.BoundCount == sendersBefore, "a client-only rig is not registered as a combat entity");
                Check(view.PlayerId == 0 && view.CombatId == 0, "a client-only rig has no id until the server names it");
                Check(!view.SetNetworkPlayerId(0) && !view.SetNetworkPlayerId(-3), "a client rig refuses a non-positive id");
                Check(view.SetNetworkPlayerId(4242) && view.PlayerId == 4242, "a client rig takes the server-issued id");
                Check(!view.SetNetworkPlayerId(7) && view.PlayerId == 4242, "the server-issued id cannot be replaced");
                Check(view.SetNetworkPlayerId(4242), "setting the same id again is a no-op success");
                Check(CombatRegistry.All.Count == registryBefore, "setting the id did not register the view");
                NetRole.ResetToOffline();
                Check(!view.SetNetworkPlayerId(9) && view.PlayerId == 4242, "outside a client process the setter is refused");
            }
            NetRole.ResetToOffline();

            Destroy(clone);
            yield return null;
            Check(CombatRegistry.All.Count == registryBefore && CombatSenders.BoundCount == sendersBefore, "destroying the view leaves the registry and sender table alone");
        }

        // ---- 9. a hosted run: join, remove, zero players -----------------------------------------------------------------------------

        private IEnumerator HostedRunJoinRemove()
        {
            DungeonLayoutData layout = _director.Layout;
            GameObject template = _p1.gameObject;

            // P1 stands on spawn 0, as at the start of a run.
            _p1.Movement.ExternalDriven = true;
            _p1.Movement.TeleportTo(layout.PlayerSpawnWorld(0) + Vector3.up * 0.15f);
            yield return null;

            int basePlayers = _director.Players.Count;
            int baseSenders = CombatSenders.BoundCount;
            int baseRegistry = CombatRegistry.All.Count;
            Check(basePlayers == 1, "baseline: one player");

            // refusals leave nothing behind
            PlayerRig none;
            string reason;
            Check(!_director.TrySpawnNetworkPlayer(template, "necromancer", out none, out reason) && none == null && reason.Contains("not approved"), "an unapproved class id is refused (" + reason + ")");
            Check(!_director.TrySpawnNetworkPlayer(null, "warrior", out none, out reason) && reason == "no player prefab", "a missing prefab is refused (" + reason + ")");
            Check(!_director.TrySpawnNetworkPlayer(template, (CharacterClassData)null, false, out none, out reason) && reason == "no class selected", "a missing class is refused (" + reason + ")");
            GameObject noRig = new GameObject("NetSeams_NoRig");
            _temp.Add(noRig);
            Check(!_director.TrySpawnNetworkPlayer(noRig, "warrior", out none, out reason) && reason == "prefab has no PlayerRig", "a prefab without a PlayerRig is refused (" + reason + ")");
            yield return null;
            Check(_director.Players.Count == basePlayers && CombatSenders.BoundCount == baseSenders && CombatRegistry.All.Count == baseRegistry, "refused spawns changed no count");

            // three joins
            PlayerRig n1, n2, n3;
            bool ok1 = _director.TrySpawnNetworkPlayer(template, "warrior", out n1, out reason);
            bool ok2 = _director.TrySpawnNetworkPlayer(template, "Archer", out n2, out reason);
            bool ok3 = _director.TrySpawnNetworkPlayer(template, "warrior", out n3, out reason);
            Check(ok1 && ok2 && ok3 && n1 != null && n2 != null && n3 != null, "three network players were admitted");
            if (!(ok1 && ok2 && ok3)) yield break;
            _temp.Add(n1.gameObject); _temp.Add(n2.gameObject); _temp.Add(n3.gameObject);

            Check(_director.Players.Count == basePlayers + 3, "the run holds four players");
            Check(n1.PlayerId > 0 && n1.PlayerId != _p1.PlayerId && n2.PlayerId != n1.PlayerId && n3.PlayerId != n2.PlayerId && n3.PlayerId != n1.PlayerId, "server ids are distinct");
            Check(n1.PartyId == 0 && n2.PartyId == 0 && n3.PartyId == 1, "auto parties of three: P1+n1+n2 in party 0, n3 opens party 1 (" + n1.PartyId + "," + n2.PartyId + "," + n3.PartyId + ")");
            Check(n2.Class != null && n2.Class.classId == "archer" && n1.Class.classId == "warrior", "the approved class was applied to each rig");
            Check(!n1.IsLocallyControlled && n1.Kind == EntityKind.RemotePlayer && n1.Movement.ExternalDriven, "a network player is remote controlled and externally driven");
            Camera cam = n1.GetComponentInChildren<Camera>(true);
            Check(cam == null || !cam.enabled, "a network player has no live camera");
            Check(CombatRegistry.TryGet(n1.CombatId, out _) && CombatRegistry.All.Count == baseRegistry + 3, "the new rigs are in the combat registry");
            Check(CombatSenders.BoundCount == baseSenders + 3 && CombatSenders.Owns(CombatSenderId.ForPlayer(n1.PlayerId), n1.CombatId) && !CombatSenders.Owns(CombatSenderId.ForPlayer(n2.PlayerId), n1.CombatId),
                "each new rig is owned by its own sender only");
            Check(Flat(n1.transform.position, layout.PlayerSpawnWorld(1)) < 0.05f && Flat(n2.transform.position, layout.PlayerSpawnWorld(2)) < 0.05f && Flat(n3.transform.position, layout.PlayerSpawnWorld(3)) < 0.05f,
                "joiners take the first free spawns in order (1, 2, 3)");

            // a second local player is refused
            Check(!_director.TrySpawnNetworkPlayer(template, "warrior", true, out none, out reason) && reason.Contains("local player"), "a second local player is refused (" + reason + ")");

            // the death hook works for a member, and not for a removed one
            _events.Clear();
            LethalHit(n2);
            Check(EventsFor("runstate:Dead", n2.PlayerId) == 1, "a member's death raises its run state once");

            Check(_director.RemovePlayer(n1.PlayerId, "test disconnect"), "RemovePlayer removes a member");
            Check(_director.Players.Count == basePlayers + 2 && !_director.TryGetSession(n1.PlayerId, out _), "the session is gone");
            Check(!CombatRegistry.TryGet(n1.CombatId, out _) && CombatRegistry.All.Count == baseRegistry + 2, "the rig left the combat registry");
            bool stale = false;
            for (int i = 0; i < CombatRegistry.All.Count; i++) if (ReferenceEquals(CombatRegistry.All[i], n1)) stale = true;
            Check(!stale, "the combat registry holds no stale player");
            Check(!CombatSenders.TryGetOwner(n1.CombatId, out _) && CombatSenders.BoundCount == baseSenders + 2, "the sender binding is gone");
            Check(!_director.RemovePlayer(n1.PlayerId, "again") && !_director.RemovePlayer(987654, "unknown"), "removal is idempotent and ignores unknown ids");

            _events.Clear();
            LethalHit(n1);
            Check(EventsFor("runstate:Dead", n1.PlayerId) == 0 && EventsFor("runended", n1.PlayerId) == 0, "a removed player's death no longer reaches the run");
            Check(_director.State == RunState.Running, "the run is still Running");

            // the freed seat and the freed spawn are reused
            PlayerRig n4;
            Check(_director.TrySpawnNetworkPlayer(template, "warrior", out n4, out reason) && n4 != null, "a new player is admitted after a removal");
            if (n4 != null)
            {
                _temp.Add(n4.gameObject);
                Check(n4.PartyId == 0, "the freed party seat was reused (party " + n4.PartyId + ")");
                Check(Flat(n4.transform.position, layout.PlayerSpawnWorld(1)) < 0.05f, "the freed spawn was reused");
            }

            // destroyRig removes the object too
            Check(_director.RemovePlayer(n3.PlayerId, "test", true), "RemovePlayer(destroyRig) removes a member");
            yield return null;
            Check(n3 == null, "the rig object was destroyed");
            Destroy(n1.gameObject);

            // back to the baseline
            _director.RemovePlayer(n2.PlayerId, "cleanup");
            if (n4 != null) _director.RemovePlayer(n4.PlayerId, "cleanup");
            yield return null;
            Check(_director.Players.Count == basePlayers && CombatSenders.BoundCount == baseSenders && CombatRegistry.All.Count == baseRegistry,
                "after removing every network player the counts are back to the baseline (" + _director.Players.Count + "/" + CombatSenders.BoundCount + "/" + CombatRegistry.All.Count + ")");
            Check(_director.State == RunState.Running, "the run is Running with only the original player");

            // zero players does not end the run, and the hosted run takes a late local (host) player
            _events.Clear();
            Check(_director.RemovePlayer(_p1.PlayerId, "last player leaves"), "the last (local) player can be removed");
            _director.SetServerMode(true);
            yield return null; yield return null;
            Check(_director.Players.Count == 0 && _director.LocalSession == null, "no players, no local session");
            Check(_director.State == RunState.Running && EventsFor("runended", _p1.PlayerId) == 0, "an empty hosted run stays Running");
            Check(_director.Player == null && _director.LocalPlayerId == GameEvents.NoPlayer, "a server with no local player has no Player and no LocalPlayerId");
            Check(CombatSenders.BoundCount == 0 && !CombatRegistry.TryGet(_p1.CombatId, out _), "no sender and no scene rig remain registered");

            PlayerRig late;
            Check(_director.TrySpawnNetworkPlayer(template, "warrior", false, out late, out reason) && late != null, "a late joiner enters the empty run (" + reason + ")");
            if (late != null)
            {
                _temp.Add(late.gameObject);
                Check(_director.Players.Count == 1 && late.PartyId == 0 && Flat(late.transform.position, layout.PlayerSpawnWorld(0)) < 0.05f, "the late joiner takes party 0 and spawn 0");
                _director.RemovePlayer(late.PlayerId, "cleanup");
            }

            PlayerRig host;
            Check(_director.TrySpawnNetworkPlayer(template, "archer", true, out host, out reason) && host != null, "the host's own player is admitted as the local player (" + reason + ")");
            if (host != null)
            {
                _temp.Add(host.gameObject);
                Check(_director.LocalSession != null && _director.LocalSession.Rig == host && _director.Player == host && host.IsLocallyControlled, "the host player is the local session");
                Check(!host.Movement.ExternalDriven, "the host player moves through its own input path");
                Camera hostCam = host.GetComponentInChildren<Camera>(true);
                Check(hostCam != null && hostCam.enabled, "the host player keeps its camera");
                _director.RemovePlayer(host.PlayerId, "cleanup");
            }

            _director.SetServerMode(false);
            yield return null;
        }

        private static void LethalHit(PlayerRig rig)
        {
            rig.ApplyDamage(new DamageInfo(100000f, rig.transform.position, Vector3.forward, rig.transform.position + Vector3.forward, 0,
                EntityKind.Enemy, DamageType.Physical, false, false, 0f));
        }
    }
}
