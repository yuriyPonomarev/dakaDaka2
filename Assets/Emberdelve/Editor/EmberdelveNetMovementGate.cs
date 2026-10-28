using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Emberdelve.Data;
using Emberdelve.Level;
using Emberdelve.Net.Rules;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// U3A4a acceptance gate: server-simulated movement from client input frames, with real independent processes on loopback.
    /// U3A4b extends it with owner prediction and reconciliation (sections 6b and 8b; every U3A4a check is kept).
    ///
    ///   Unity.exe -batchmode -nographics -projectPath . -logFile Logs/net_movement.log
    ///             -executeMethod Emberdelve.EditorTools.EmberdelveNetMovementGate.Run
    ///
    /// (Do NOT pass -quit.) Optional: -ember-proof-dir ABS_DIR (default ProjectRoot/Builds/net-movement/yyyyMMdd-HHmmss),
    /// -ember-proof-reuse-exe ABS_EXE (skip the build; the exe must come from a U3A4a build). The Editor is only exited in batch
    /// mode through EmberdelveGateExit; from a live Editor the call just blocks and returns.
    ///
    /// Scenario (server --ember-net-no-enemies; players join in this order and so take layout spawn slots 0..3):
    ///   B (warrior, slot 0)  the cheater and an observer: nan-input, big-move, seq-jump, replay-seq, flood-input, forge-move
    ///   A (archer,  slot 1)  walk-forward 3 s, sprint-forward 1.5 s back, jump, flood-run 2 s
    ///   W (warrior, slot 2)  walk-forward 4 s into a wall chosen from the layout
    ///   V (warrior, slot 3)  idle: the victim of B's forged movement
    /// Directions come from the dungeon layout asset (walkable grid), never from constants: A's lane must be free for the whole
    /// walk, W's wall must be 3 to 9 m ahead. All scripts start together (start files) once every client sees four players.
    ///
    /// Positive checks (all numbers are the SERVER's own log lines): A's walk displacement is the logged resolved walk speed x 3 s
    /// within +-20 %; B's last view of A, W and V is within 0.15 m of the server's last position; sprint is faster than walk and not
    /// beyond the resolved sprint speed x 1.15; the jump leaves the ground (rise near the class jump height) and lands; W stops at the
    /// wall on the near side; no sampled server interval and no per-tick speed exceeds the resolved sprint speed x 1.15 for anyone.
    /// Negative checks: each cheat produces its own REJECT line and counter on B, B's position and the victim V's position do not
    /// change, honest clients (A, W, V) trip no replay / invalid / forge counter, and A's flood-run is not faster than one honest stream.
    ///
    /// U3A4b checks: the owner A predicts with the server-resolved speeds (its client log's "prediction started" numbers equal the
    /// server's "params" line); through walk, sprint and jump A's own reconcile statistics show no snap, a mean error of at most
    /// 0.05 m and no fight with the transform sync; A's predicted final position is within 0.15 m of the server's. Forced correction
    /// (section 8b): the server (-ember-net-dev-shove, development build) teleports A 2 m once; A must snap at least once, replay,
    /// and converge to the server position, and B's view of A follows too. A's flood-run (hostile traffic, extra frames are not
    /// predicted) is outside the honest-movement statistics, which are read at the end of the jump step.
    /// Final line: [Emberdelve][NetMovement] PASS|FAIL checks=N failures=M
    /// </summary>
    public static class EmberdelveNetMovementGate
    {
        private const string Tag = "[Emberdelve][NetMovement]";
        private const string S = "[Emberdelve][Net][server] ";
        private const string C = "[Emberdelve][Net][client] ";
        private const int DisconnectTimeoutMs = 4000;
        private const double TickSeconds = 1.0 / NetTuning.TickRate;
        private const string Num = @"(-?\d+(?:\.\d+)?)";
        private const string Vec = @"\(" + Num + "," + Num + "," + Num + @"\)";

        private static int _checks;
        private static int _failures;
        private static readonly List<Process> Started = new List<Process>();

        public static void Run()
        {
            int exit = 1;
            try { exit = Execute(); }
            catch (Exception e)
            {
                Check(false, "gate threw " + e);
                exit = 1;
            }
            finally { KillSurvivors(); }

            string verdict = _failures == 0 ? "PASS" : "FAIL";
            string line = Tag + " " + verdict + " checks=" + _checks + " failures=" + _failures;
            if (_failures == 0) Debug.Log(line); else Debug.LogError(line);

            if (Application.isBatchMode) EmberdelveGateExit.Exit(_failures == 0 && exit == 0 ? 0 : 1);
        }

        // ---- parsed server lines ------------------------------------------------------------------------------------

        private sealed class Reg
        {
            public int Id;
            public float Walk, Sprint, Crouch, Jump;
            public Vector3 Pos;
        }

        private sealed class Seg
        {
            public int Id, Ticks;
            public float Secs, Dist, Disp, Mean, Peak;
            public Vector3 From, To;
            public bool Open;
        }

        private sealed class MoveLine
        {
            public int Id;
            public Vector3 Pos;
            public int Acc, Rep, Flood, Inv, Forge, Air;
            public long Tick;
            public float VMax;
            public bool Final;
            public float VMaxAll;
            public int Nan, Big, SeqJ;
        }

        private sealed class AirLine
        {
            public int Id, Ticks;
            public float Rest, YMax, Rise;
            public long Tick;
        }

        private sealed class Recon
        {
            public int Acks, Blends, Snaps, Replays, ReplayFrames, Missing, Stale, Fights;
            public float MaxErr, MeanErr;
            public string At = "";
        }

        private static List<Recon> ParseRecon(string clientText)
        {
            List<Recon> list = new List<Recon>();
            foreach (Match m in Regex.Matches(clientText, @"\[client\] reconcile acks=(\d+) maxErr=" + Num + " meanErr=" + Num + @" blends=(\d+) snaps=(\d+) replays=(\d+) replayFrames=(\d+) missing=(\d+) stale=(\d+) fights=(\d+)(?: at=(\S+))?"))
            {
                list.Add(new Recon
                {
                    Acks = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    MaxErr = Fl(m.Groups[2].Value),
                    MeanErr = Fl(m.Groups[3].Value),
                    Blends = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
                    Snaps = int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture),
                    Replays = int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture),
                    ReplayFrames = int.Parse(m.Groups[7].Value, CultureInfo.InvariantCulture),
                    Missing = int.Parse(m.Groups[8].Value, CultureInfo.InvariantCulture),
                    Stale = int.Parse(m.Groups[9].Value, CultureInfo.InvariantCulture),
                    Fights = int.Parse(m.Groups[10].Value, CultureInfo.InvariantCulture),
                    At = m.Groups[11].Success ? m.Groups[11].Value : ""
                });
            }

            return list;
        }

        private static bool TryLastPredict(string clientText, out Vector3 pos)
        {
            pos = Vector3.zero;
            MatchCollection matches = Regex.Matches(clientText, @"\[client\] predict pos=" + Vec);
            if (matches.Count == 0) return false;
            pos = V3(matches[matches.Count - 1], 1);
            return true;
        }

        private static Vector3 V3(Match m, int first)
        {
            return new Vector3(Fl(m.Groups[first].Value), Fl(m.Groups[first + 1].Value), Fl(m.Groups[first + 2].Value));
        }

        private static float Fl(string text) { return float.Parse(text, CultureInfo.InvariantCulture); }

        private static Reg ParseReg(string serverText, int id)
        {
            Match m = Regex.Match(serverText, @"movement registered id=" + id + @" client=\d+ walk=" + Num + " sprint=" + Num + " crouch=" + Num +
                                              " jump=" + Num + " pos=" + Vec);
            if (!m.Success) return null;
            return new Reg { Id = id, Walk = Fl(m.Groups[1].Value), Sprint = Fl(m.Groups[2].Value), Crouch = Fl(m.Groups[3].Value), Jump = Fl(m.Groups[4].Value), Pos = V3(m, 5) };
        }

        private static List<Seg> ParseSegs(string serverText, int id)
        {
            List<Seg> list = new List<Seg>();
            foreach (Match m in Regex.Matches(serverText, @"seg id=" + id + @" kind=move startTick=\d+ endTick=\d+ ticks=(\d+) secs=" + Num + " dist=" + Num + " disp=" + Num +
                                                          " mean=" + Num + " peak=" + Num + " from=" + Vec + " to=" + Vec + @"( open=1)?"))
            {
                list.Add(new Seg
                {
                    Id = id,
                    Ticks = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    Secs = Fl(m.Groups[2].Value),
                    Dist = Fl(m.Groups[3].Value),
                    Disp = Fl(m.Groups[4].Value),
                    Mean = Fl(m.Groups[5].Value),
                    Peak = Fl(m.Groups[6].Value),
                    From = V3(m, 7),
                    To = V3(m, 10),
                    Open = m.Groups[13].Success
                });
            }

            return list;
        }

        private static List<MoveLine> ParseMoves(string serverText, int id)
        {
            List<MoveLine> list = new List<MoveLine>();
            foreach (Match m in Regex.Matches(serverText, @"move id=" + id + " pos=" + Vec + @" acc=(\d+) rep=(\d+) flood=(\d+) inv=(\d+) tick=(\d+) yaw=" + Num +
                                                          @" clamp=(\d+) qdrop=(\d+) forge=(\d+) vmax=" + Num + " ymax=" + Num + @" air=(\d+) depth=\d+( final=1 reason=\S+ vmaxAll=" + Num +
                                                          @" nan=(\d+) big=(\d+) seqj=(\d+))?"))
            {
                MoveLine line = new MoveLine
                {
                    Id = id,
                    Pos = V3(m, 1),
                    Acc = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
                    Rep = int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture),
                    Flood = int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture),
                    Inv = int.Parse(m.Groups[7].Value, CultureInfo.InvariantCulture),
                    Tick = long.Parse(m.Groups[8].Value, CultureInfo.InvariantCulture),
                    Forge = int.Parse(m.Groups[12].Value, CultureInfo.InvariantCulture),
                    VMax = Fl(m.Groups[13].Value),
                    Air = int.Parse(m.Groups[15].Value, CultureInfo.InvariantCulture),
                    Final = m.Groups[16].Success
                };
                if (line.Final)
                {
                    line.VMaxAll = Fl(m.Groups[17].Value);
                    line.Nan = int.Parse(m.Groups[18].Value, CultureInfo.InvariantCulture);
                    line.Big = int.Parse(m.Groups[19].Value, CultureInfo.InvariantCulture);
                    line.SeqJ = int.Parse(m.Groups[20].Value, CultureInfo.InvariantCulture);
                }

                list.Add(line);
            }

            return list;
        }

        private static List<AirLine> ParseAir(string serverText, int id)
        {
            List<AirLine> list = new List<AirLine>();
            foreach (Match m in Regex.Matches(serverText, @"air id=" + id + @" ticks=(\d+) rest=" + Num + " ymax=" + Num + " rise=" + Num + @" tick=(\d+)"))
            {
                list.Add(new AirLine
                {
                    Id = id,
                    Ticks = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    Rest = Fl(m.Groups[2].Value),
                    YMax = Fl(m.Groups[3].Value),
                    Rise = Fl(m.Groups[4].Value),
                    Tick = long.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture)
                });
            }

            return list;
        }

        private static bool TryLastView(string clientText, int id, out Vector3 pos)
        {
            pos = Vector3.zero;
            MatchCollection matches = Regex.Matches(clientText, @"\[client\] view id=" + id + " pos=" + Vec);
            if (matches.Count == 0) return false;
            pos = V3(matches[matches.Count - 1], 1);
            return true;
        }

        private static float Flat(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // ---- layout geometry ----------------------------------------------------------------------------------------

        private static bool Walkable(DungeonLayoutData layout, bool[,] grid, Vector3 p)
        {
            Vector2Int c = layout.CellFromWorld(p);
            return c.x >= 0 && c.y >= 0 && c.x < grid.GetLength(0) && c.y < grid.GetLength(1) && grid[c.x, c.y];
        }

        private static Vector3 Dir(float yawDegrees)
        {
            float r = yawDegrees * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(r), 0f, Mathf.Cos(r));
        }

        /// <summary>
        /// How far a capsule-wide lane (centre line and +-lateral) from <paramref name="start"/> along <paramref name="yaw"/> stays on
        /// walkable cells and 1 m clear of the given other players, up to <paramref name="max"/>.
        /// </summary>
        private static float FreeDistance(DungeonLayoutData layout, bool[,] grid, Vector3 start, float yaw, float max, float lateral, List<Vector3> others)
        {
            Vector3 dir = Dir(yaw);
            Vector3 right = Dir(yaw + 90f);
            float[] offsets = lateral > 0f ? new[] { 0f, lateral, -lateral } : new[] { 0f };

            for (float d = 0f; d <= max; d += 0.1f)
            {
                for (int i = 0; i < offsets.Length; i++)
                {
                    if (!Walkable(layout, grid, start + dir * d + right * offsets[i])) return Mathf.Max(0f, d - 0.1f);
                }

                Vector3 centre = start + dir * d;
                for (int i = 0; others != null && i < others.Count; i++)
                {
                    if (Flat(centre, others[i]) < 1.0f) return Mathf.Max(0f, d - 0.1f);
                }
            }

            return max;
        }

        // ---- the scenario -------------------------------------------------------------------------------------------

        private static int Execute()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string runDir = ArgValue("-ember-proof-dir");
            if (string.IsNullOrEmpty(runDir))
                runDir = Path.Combine(projectRoot, "Builds", "net-movement", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            runDir = Path.GetFullPath(runDir);
            if (runDir.StartsWith(Path.GetFullPath(Application.dataPath), StringComparison.OrdinalIgnoreCase))
            {
                Check(false, "run dir must be outside Assets: " + runDir);
                return 1;
            }

            string logs = Path.Combine(runDir, "logs");
            Directory.CreateDirectory(logs);
            Debug.Log(Tag + " runDir=" + runDir);

            CheckMovementRpcSignatures(projectRoot);

            // ---- layout + class data: where everybody spawns, which way is free, where the wall is --------------------------
            DungeonLayoutData layout = AssetDatabase.LoadAssetAtPath<DungeonLayoutData>(EmberdelvePaths.Data + "/DungeonLayout_AshenHalls.asset");
            CharacterClassData warrior = AssetDatabase.LoadAssetAtPath<CharacterClassData>(EmberdelvePaths.Data + "/Class_Warrior.asset");
            CharacterClassData archer = AssetDatabase.LoadAssetAtPath<CharacterClassData>(EmberdelvePaths.Data + "/Class_Archer.asset");
            Check(layout != null && warrior != null && archer != null, "layout and both class assets load");
            if (layout == null || warrior == null || archer == null) return 1;
            Check(layout.playerSpawns != null && layout.playerSpawns.Count >= 4, "the layout has at least 4 player spawns (" + (layout.playerSpawns != null ? layout.playerSpawns.Count : 0) + ")");
            if (layout.playerSpawns == null || layout.playerSpawns.Count < 4) return 1;

            bool[,] grid = layout.BuildWalkableGrid();
            Vector3[] spawn = new Vector3[4];
            for (int i = 0; i < 4; i++) spawn[i] = layout.PlayerSpawnWorld(i);
            // slot 0 = B, 1 = A, 2 = W, 3 = V
            List<Vector3> aOthers = new List<Vector3> { spawn[0], spawn[2], spawn[3] };
            List<Vector3> wOthers = new List<Vector3> { spawn[0], spawn[1], spawn[3] };

            float yawA = 0f, bestFree = -1f;
            foreach (float candidate in new[] { 0f, 90f, 180f, 270f })
            {
                float free = FreeDistance(layout, grid, spawn[1], candidate, 40f, 0.45f, aOthers);
                if (free > bestFree) { bestFree = free; yawA = candidate; }
            }

            float walkNeeded = archer.moveSpeed * 3f * 1.2f + 0.5f; // longest walk the +20 % check accepts, plus margin
            Check(bestFree >= walkNeeded, "A's lane is free for the whole walk: yaw " + yawA + " free " + bestFree.ToString("F1", CultureInfo.InvariantCulture) + " m >= " + walkNeeded.ToString("F1", CultureInfo.InvariantCulture));
            Vector3 aTurn = spawn[1] + Dir(yawA) * (archer.moveSpeed * 3f);
            float yawBack = yawA + 180f;
            float backFree = FreeDistance(layout, grid, aTurn, yawBack, 40f, 0.45f, aOthers);
            Check(backFree >= archer.sprintSpeed * 1.5f * 1.2f + 0.5f, "the way back for the sprint is free: " + backFree.ToString("F1", CultureInfo.InvariantCulture) + " m");

            float yawW = 0f, wallDist = 0f, bestScore = float.MaxValue;
            foreach (float candidate in new[] { 0f, 90f, 180f, 270f })
            {
                // The wall alone (no other players) decides the distance; the lane must be wall-wide free up to it and no player may stand in it.
                float boundary = FreeDistance(layout, grid, spawn[2], candidate, 40f, 0f, null);
                float lane = FreeDistance(layout, grid, spawn[2], candidate, 40f, 0.45f, null);
                float laneWithPlayers = FreeDistance(layout, grid, spawn[2], candidate, 40f, 0.45f, wOthers);
                if (boundary < 3f || boundary > 9f || lane < boundary - 1.0f || laneWithPlayers < lane - 0.3f) continue;
                float score = Mathf.Abs(boundary - 6f);
                if (score < bestScore) { bestScore = score; yawW = candidate; wallDist = boundary; }
            }

            Check(wallDist > 0f, "W has a wall 3 to 9 m ahead: yaw " + yawW + " boundary " + wallDist.ToString("F1", CultureInfo.InvariantCulture) + " m");
            if (bestFree < walkNeeded || wallDist <= 0f) return 1;

            // ---- 1. player + server ---------------------------------------------------------------
            string exe = ArgValue("-ember-proof-reuse-exe");
            if (string.IsNullOrEmpty(exe))
            {
                exe = Path.Combine(runDir, "player", "EmberdelveNet.exe");
                bool built = EmberdelveWindowsBuild.BuildNetProofPlayer(exe, out string buildError);
                Check(built, "player build" + (built ? "" : " failed: " + buildError));
                if (!built) return 1;
            }
            else
            {
                Check(File.Exists(exe), "reused player exists: " + exe);
                if (!File.Exists(exe)) return 1;
            }

            int port = FreeUdpPort();
            Check(port > 0, "free udp port " + port);
            if (port <= 0) return 1;

            string quitServer = Path.Combine(runDir, "server.quit");
            string[] names = { "b", "a", "w", "v" };
            string[] quit = new string[4];
            string[] start = new string[4];
            for (int i = 0; i < 4; i++)
            {
                quit[i] = Path.Combine(runDir, "client_" + names[i] + ".quit");
                start[i] = Path.Combine(runDir, "client_" + names[i] + ".start");
            }

            string shoveFile = Path.Combine(runDir, "server.shove");
            foreach (string f in new[] { quitServer, shoveFile, quit[0], quit[1], quit[2], quit[3], start[0], start[1], start[2], start[3] })
                if (File.Exists(f)) File.Delete(f);

            string serverLog = Path.Combine(logs, "server.log");
            Process server = Launch(exe, serverLog,
                "-ember-net server -ember-port " + port + " -ember-net-quit-file " + Q(quitServer) +
                " -ember-net-autoquit 600 -ember-net-disconnect-timeout-ms " + DisconnectTimeoutMs +
                " -ember-net-max-players 4 -ember-net-no-enemies" +
                " -ember-net-dev-shove mv-a:" + Yaw(yawBack) + ":2.0 -ember-net-dev-shove-file " + Q(shoveFile));
            Check(server != null, "server process started pid=" + (server != null ? server.Id.ToString() : "-"));
            if (server == null) return 1;

            Check(WaitForLog(serverLog, S + "listening", 60, server), "server logged 'listening'");
            Match ready = WaitForRegex(serverLog, @"\[server\] ready scene=\S+ players=0 senders=0 registry=(\d+)", 90, server);
            Check(ready != null && ready.Groups[1].Value == "0", "server is ready with players=0 and no enemies (registry=0)");
            if (ready == null) return 1;
            Check(LogContains(serverLog, "movement simulation attached tickRate=" + NetTuning.TickRate), "the server attached its movement simulation to the network tick at tickRate=" + NetTuning.TickRate);

            // ---- 2. clients in slot order ---------------------------------------------------------
            string scriptB = "nan-input:1.5,big-move:1.5,seq-jump:1.5,replay-seq:1.5,flood-input:1.5,forge-move:1.5";
            string scriptA = "walk-forward:3@" + Yaw(yawA) + ",sprint-forward:1.5@" + Yaw(yawBack) + ",jump:1.5@" + Yaw(yawBack) + ",flood-run:2@" + Yaw(yawA);
            string scriptW = "walk-forward:4@" + Yaw(yawW);
            string[] slots = { "mv-b", "mv-a", "mv-w", "mv-v" };
            string[] classes = { "warrior", "archer", "warrior", "warrior" };
            string[] scripts = { scriptB, scriptA, scriptW, "" };
            string[] clientLogs = new string[4];
            Process[] clients = new Process[4];
            int[] ids = new int[4];
            int[] clientIds = new int[4];

            for (int i = 0; i < 4; i++)
            {
                clientLogs[i] = Path.Combine(logs, "client_" + names[i] + ".log");
                string extra = scripts[i].Length > 0 ? "-ember-net-script " + scripts[i] + " -ember-net-script-start-file " + Q(start[i]) : "";
                clients[i] = Launch(exe, clientLogs[i], ClientArgs(port, slots[i], classes[i], Q(quit[i]), extra) + " -ember-net-autoquit 300");
                Check(clients[i] != null, "client " + names[i].ToUpperInvariant() + " started");
                if (clients[i] == null) return 1;

                Match adm = WaitForRegex(serverLog, @"admitted client=(\d+) id=(\d+) party=\d+ slot=" + slots[i] + " class=" + classes[i] + @" netId=\d+ players=" + (i + 1), 90, server);
                Check(adm != null, "server admitted " + names[i].ToUpperInvariant() + " as the " + (i + 1) + ". player");
                if (adm == null) return 1;
                clientIds[i] = int.Parse(adm.Groups[1].Value, CultureInfo.InvariantCulture);
                ids[i] = int.Parse(adm.Groups[2].Value, CultureInfo.InvariantCulture);
            }

            int idB = ids[0], idA = ids[1], idW = ids[2], idV = ids[3];
            Check(idB != idA && idA != idW && idW != idV && idB != idW && idB != idV && idA != idV, "four distinct server ids B=" + idB + " A=" + idA + " W=" + idW + " V=" + idV);

            string serverNow = ReadShared(serverLog);
            Reg regB = ParseReg(serverNow, idB), regA = ParseReg(serverNow, idA), regW = ParseReg(serverNow, idW), regV = ParseReg(serverNow, idV);
            Check(regB != null && regA != null && regW != null && regV != null, "the server registered movement for all four players");
            if (regB == null || regA == null || regW == null || regV == null) return 1;
            Reg[] regs = { regB, regA, regW, regV };
            for (int i = 0; i < 4; i++)
                Check(Flat(regs[i].Pos, spawn[i]) < 0.05f, names[i].ToUpperInvariant() + " spawned at layout slot " + i + " (" + Flat(regs[i].Pos, spawn[i]).ToString("F3", CultureInfo.InvariantCulture) + " m off)");

            Check(regA.Walk >= archer.moveSpeed * 0.75f && regA.Walk <= archer.moveSpeed * 1.35f,
                "A's resolved walk speed " + regA.Walk.ToString("F2", CultureInfo.InvariantCulture) + " is near the class value " + archer.moveSpeed);
            Check(regA.Sprint >= archer.sprintSpeed * 0.75f && regA.Sprint <= archer.sprintSpeed * 1.35f,
                "A's resolved sprint speed " + regA.Sprint.ToString("F2", CultureInfo.InvariantCulture) + " is near the class value " + archer.sprintSpeed);
            Check(regA.Sprint > regA.Walk * 1.15f, "A's sprint is clearly faster than its walk (data)");

            for (int i = 0; i < 4; i++)
                Check(WaitForRegex(clientLogs[i], @"\[client\] owner id=" + ids[i] + @" views=4 owned=1 ", 60, clients[i]) != null,
                    names[i].ToUpperInvariant() + " sees four players and owns exactly its own (id " + ids[i] + ")");

            // ---- 3. go ------------------------------------------------------------------------------
            for (int i = 0; i < 3; i++) File.WriteAllText(start[i], "go");

            Check(WaitForRegex(clientLogs[2], @"\[client\] script walk-forward done pos=" + Vec, 60, clients[2]) != null, "W finished its wall run");
            Check(WaitForLog(clientLogs[1], C + "script chain done steps=4", 90, clients[1]), "A finished walk, sprint, jump and flood-run");
            Check(WaitForLog(clientLogs[0], C + "script chain done steps=6", 90, clients[0]), "B finished all six cheats");
            Thread.Sleep(3500); // let the last positions settle, the observers' heartbeat view lines catch up and the server log its last move lines

            string serverText = ReadShared(serverLog);

            // ---- 4. A: walk / sprint / jump ---------------------------------------------------------
            List<Seg> segA = ParseSegs(serverText, idA);
            Check(segA.Count >= 3, "A has three movement segments on the server (walk, sprint, flood-run): " + segA.Count);
            if (segA.Count >= 3)
            {
                Seg walk = segA[0], sprint = segA[1], flood = segA[2];
                float expectedWalk = regA.Walk * 3f;
                Check(Flat(walk.From, regA.Pos) < 0.3f, "A's walk starts at its spawn");
                Check(walk.Disp >= expectedWalk * 0.8f && walk.Disp <= expectedWalk * 1.2f,
                    "A walked " + walk.Disp.ToString("F2", CultureInfo.InvariantCulture) + " m in 3 s; expected " + expectedWalk.ToString("F2", CultureInfo.InvariantCulture) + " m (walk speed " + regA.Walk.ToString("F2", CultureInfo.InvariantCulture) + " x 3 s) +-20%");
                Check(walk.Mean >= regA.Walk * 0.8f && walk.Mean <= regA.Walk * 1.2f, "A's mean walking speed " + walk.Mean.ToString("F2", CultureInfo.InvariantCulture) + " is the resolved walk speed +-20%");
                Check(walk.Peak <= regA.Walk * 1.15f, "A's walk never exceeds walk speed x 1.15 (peak " + walk.Peak.ToString("F2", CultureInfo.InvariantCulture) + ")");
                Vector3 walkDir = (walk.To - walk.From); walkDir.y = 0f;
                Check(Vector3.Dot(walkDir.normalized, Dir(yawA)) > 0.99f, "A walked along its view yaw " + yawA + " (movement is relative to the sent yaw)");

                Check(sprint.Peak > walk.Peak * 1.15f, "sprint peak " + sprint.Peak.ToString("F2", CultureInfo.InvariantCulture) + " is faster than walk peak " + walk.Peak.ToString("F2", CultureInfo.InvariantCulture));
                Check(sprint.Mean > walk.Mean * 1.15f, "sprint mean " + sprint.Mean.ToString("F2", CultureInfo.InvariantCulture) + " is faster than walk mean " + walk.Mean.ToString("F2", CultureInfo.InvariantCulture));
                Check(sprint.Peak <= regA.Sprint * 1.15f, "sprint never exceeds the resolved sprint speed x 1.15 (resolved " + regA.Sprint.ToString("F2", CultureInfo.InvariantCulture) + ", peak " + sprint.Peak.ToString("F2", CultureInfo.InvariantCulture) + ")");
                float expectedSprint = regA.Sprint * 1.5f;
                Check(sprint.Disp >= expectedSprint * 0.8f && sprint.Disp <= expectedSprint * 1.2f,
                    "A sprinted " + sprint.Disp.ToString("F2", CultureInfo.InvariantCulture) + " m in 1.5 s; expected " + expectedSprint.ToString("F2", CultureInfo.InvariantCulture) + " +-20%");
                Check(Vector3.Dot(new Vector3(sprint.To.x - sprint.From.x, 0f, sprint.To.z - sprint.From.z).normalized, Dir(yawBack)) > 0.99f, "the sprint went back along the lane (yaw " + yawBack + ")");

                float oneStream = regA.Walk * 2f;
                Check(flood.Disp <= oneStream * 1.2f, "flood-run (10 frame batches per tick) moved " + flood.Disp.ToString("F2", CultureInfo.InvariantCulture) + " m in 2 s: not more than one honest stream (" + oneStream.ToString("F2", CultureInfo.InvariantCulture) + " m +20%)");
                Check(flood.Disp >= oneStream * 0.5f, "flood-run still moved A (the accepted frames are real input), " + flood.Disp.ToString("F2", CultureInfo.InvariantCulture) + " m");
            }

            List<AirLine> air = ParseAir(serverText, idA);
            Check(air.Count >= 1, "the server logged A's jump (air line)");
            List<MoveLine> movesA = ParseMoves(serverText, idA);
            if (air.Count >= 1)
            {
                AirLine jump = air[0];
                Check(jump.Rise >= regA.Jump * 0.6f && jump.Rise <= regA.Jump * 1.3f + 0.2f,
                    "the jump left the ground: rise " + jump.Rise.ToString("F2", CultureInfo.InvariantCulture) + " m, class jump height " + regA.Jump.ToString("F2", CultureInfo.InvariantCulture));
                Check(jump.Ticks >= 8, "the jump stayed airborne for " + jump.Ticks + " ticks");
                MoveLine later = movesA.Find(m => m.Tick >= jump.Tick + 20 && !m.Final);
                Check(later != null && Mathf.Abs(later.Pos.y - jump.Rest) < 0.12f,
                    "and landed: y " + (later != null ? later.Pos.y.ToString("F2", CultureInfo.InvariantCulture) : "?") + " is back at rest " + jump.Rest.ToString("F2", CultureInfo.InvariantCulture));
            }

            // ---- 5. W: the wall ---------------------------------------------------------------------
            List<Seg> segW = ParseSegs(serverText, idW);
            Check(segW.Count >= 1, "W has a movement segment");
            if (segW.Count >= 1)
            {
                Seg run = segW[0];
                Check(!run.Open, "W's run ended against the wall (the segment closed itself)");
                Check(run.Disp <= wallDist + 0.02f, "W stayed on the near side: travelled " + run.Disp.ToString("F2", CultureInfo.InvariantCulture) + " m, wall plane at " + wallDist.ToString("F2", CultureInfo.InvariantCulture) + " m");
                Check(run.Disp >= wallDist - 1.3f, "and reached the wall (not stopped short)");
                Check(Walkable(layout, grid, run.To), "W's final cell is a walkable cell");
                List<MoveLine> movesW = ParseMoves(serverText, idW);
                MoveLine lastW = movesW.Count > 0 ? movesW[movesW.Count - 1] : null;
                Check(lastW != null && Flat(lastW.Pos, regW.Pos) <= wallDist + 0.02f && Walkable(layout, grid, lastW.Pos),
                    "W's last logged position is still on the near side of the wall and on a walkable cell");
                Check(run.Peak <= archer.moveSpeed * 1.5f, "W's run was an ordinary walk (peak " + run.Peak.ToString("F2", CultureInfo.InvariantCulture) + ")");
            }

            // ---- 6. observer: B sees what the server has -------------------------------------------------
            string textB = ReadShared(clientLogs[0]);
            foreach (int id in new[] { idA, idW, idV })
            {
                List<MoveLine> moves = ParseMoves(serverText, id);
                MoveLine last = moves.FindLast(m => !m.Final);
                bool seen = TryLastView(textB, id, out Vector3 viewed);
                Check(seen && last != null, "B logged a view of player " + id + " and the server logged its position");
                if (seen && last != null)
                {
                    float error = Vector3.Distance(viewed, last.Pos);
                    Check(error <= 0.15f, "B's last view of player " + id + " is within 0.15 m of the server's last position (" + error.ToString("F3", CultureInfo.InvariantCulture) + " m)");
                }
            }

            string textA = ReadShared(clientLogs[1]);
            Match aDone = Regex.Match(textA, @"script chain done steps=4 pos=" + Vec);
            MoveLine lastA = movesA.FindLast(m => !m.Final);
            Check(aDone.Success && lastA != null && Vector3.Distance(V3(aDone, 1), lastA.Pos) <= 0.3f,
                "the owner A's own position (predicted since U3A4b) at the end of its script is within 0.3 m of the server's final position");

            // ---- 6b. U3A4b: the owner predicted, with the server's numbers, and agreed with the server ------------------------
            Match paramsA = Regex.Match(serverText, @"params id=" + idA + " walk=" + Num + " sprint=" + Num + " crouch=" + Num);
            Match startedA = Regex.Match(textA, @"\[client\] prediction started walk=" + Num + " sprint=" + Num + " crouch=" + Num);
            Check(paramsA.Success && startedA.Success &&
                  Mathf.Abs(Fl(paramsA.Groups[1].Value) - Fl(startedA.Groups[1].Value)) < 0.002f &&
                  Mathf.Abs(Fl(paramsA.Groups[2].Value) - Fl(startedA.Groups[2].Value)) < 0.002f &&
                  Mathf.Abs(Fl(paramsA.Groups[3].Value) - Fl(startedA.Groups[3].Value)) < 0.002f,
                "A predicts with the server-resolved speeds (server params walk/sprint/crouch == the client's prediction numbers)");
            Check(paramsA.Success && Mathf.Abs(Fl(paramsA.Groups[1].Value) - regA.Walk) < 0.002f && Mathf.Abs(Fl(paramsA.Groups[2].Value) - regA.Sprint) < 0.002f,
                "the published params are the same resolved speeds the server registered (walk " + regA.Walk.ToString("F3", CultureInfo.InvariantCulture) + ")");

            List<Recon> reconA = ParseRecon(textA);
            Recon honest = reconA.Find(r => r.At == "step:jump");
            Check(honest != null, "A logged its reconcile statistics at the end of the jump step (" + reconA.Count + " reconcile lines)");
            if (honest != null)
            {
                Debug.Log(Tag + " A honest-phase " + C + "reconcile acks=" + honest.Acks + " maxErr=" + honest.MaxErr.ToString("F3", CultureInfo.InvariantCulture) +
                          " meanErr=" + honest.MeanErr.ToString("F3", CultureInfo.InvariantCulture) + " blends=" + honest.Blends + " snaps=" + honest.Snaps +
                          " replays=" + honest.Replays + " missing=" + honest.Missing + " stale=" + honest.Stale + " fights=" + honest.Fights);
                Check(honest.Acks >= 30, "A compared at least 30 Acks with its prediction through walk, sprint and jump (" + honest.Acks + ")");
                Check(honest.Snaps == 0 && honest.Replays == 0, "A had no snap (and no replay) during honest movement (snaps=" + honest.Snaps + " replays=" + honest.Replays + ")");
                Check(honest.MeanErr <= 0.05f, "A's mean reconcile error is at most 0.05 m (" + honest.MeanErr.ToString("F3", CultureInfo.InvariantCulture) + ")");
                Check(honest.Fights == 0, "the owner's NetworkTransform did not move the predicted body (fights=" + honest.Fights + ")");
            }

            Check(Regex.IsMatch(textA, @"\[client\] reconcile acks=\d+ .* at=script-end"), "A logged its reconcile statistics at the end of its script");
            bool seenPredicted = TryLastPredict(textA, out Vector3 predictedA);
            Check(seenPredicted && lastA != null && Vector3.Distance(predictedA, lastA.Pos) <= 0.15f,
                "A's predicted final position is within 0.15 m of the server's final position (" +
                (seenPredicted && lastA != null ? Vector3.Distance(predictedA, lastA.Pos).ToString("F3", CultureInfo.InvariantCulture) : "?") + " m)");

            // ---- 7. B's cheats: counters, REJECT lines, nothing moved ----------------------------------------
            foreach (string kind in new[] { "nan", "big-move", "seq-jump", "replay", "flood", "forge-move" })
                Check(Regex.IsMatch(serverText, @"REJECT " + kind + @" count=1 id=" + idB + @"\b"), "server logged REJECT " + kind + " for B (count=1)");
            int highestOther = Math.Max(idA, Math.Max(idW, idV));
            Check(idV == highestOther, "V has the highest server id, which is the object B's forge-move picks as its victim");
            Check(Regex.IsMatch(serverText, @"REJECT forge-move count=1 id=" + idB + " target=" + highestOther + @" "), "the forged movement named the highest-id object (V) as the target");

            List<MoveLine> movesB = ParseMoves(serverText, idB);
            List<MoveLine> movesV = ParseMoves(serverText, idV);
            MoveLine firstB = movesB.Count > 0 ? movesB[0] : null;
            MoveLine lastB = movesB.FindLast(m => !m.Final);
            Check(firstB != null && lastB != null && Flat(firstB.Pos, lastB.Pos) < 0.02f && Flat(lastB.Pos, regB.Pos) < 0.05f,
                "B (the cheater) did not move: " + (firstB != null && lastB != null ? Flat(firstB.Pos, lastB.Pos).ToString("F3", CultureInfo.InvariantCulture) : "?") + " m between its first and last position");
            MoveLine firstV = movesV.Count > 0 ? movesV[0] : null;
            MoveLine lastV = movesV.FindLast(m => !m.Final);
            Check(firstV != null && lastV != null && Flat(firstV.Pos, lastV.Pos) < 0.02f && Flat(lastV.Pos, regV.Pos) < 0.05f,
                "V (the forgery victim) did not move: " + (firstV != null && lastV != null ? Flat(firstV.Pos, lastV.Pos).ToString("F3", CultureInfo.InvariantCulture) : "?") + " m");
            Check(lastB != null && lastB.Forge >= 15, "B's forge counter is " + (lastB != null ? lastB.Forge : -1) + " (>= 15 forged batches)");
            Check(lastB != null && lastB.Flood >= 100, "B's flood counter is " + (lastB != null ? lastB.Flood : -1) + " (>= 100)");
            Check(lastB != null && lastB.Rep >= 15, "B's replay counter is " + (lastB != null ? lastB.Rep : -1) + " (>= 15)");
            Check(lastB != null && lastB.Inv >= 45, "B's invalid counter is " + (lastB != null ? lastB.Inv : -1) + " (>= 45: nan + big move + sequence jump)");

            // ---- 8. speed bound for everyone ------------------------------------------------------------
            for (int i = 0; i < 4; i++)
            {
                float bound = regs[i].Sprint * 1.15f; // the server-resolved sprint cap (DEX/gear folded in), not the authored class number
                List<MoveLine> moves = ParseMoves(serverText, ids[i]);
                float worst = 0f;
                float worstInterval = 0f;
                MoveLine prev = null;
                foreach (MoveLine m in moves)
                {
                    if (m.VMax > worst) worst = m.VMax;
                    if (prev != null && m.Tick - prev.Tick >= 5)
                    {
                        float v = Flat(prev.Pos, m.Pos) / (float)((m.Tick - prev.Tick) * TickSeconds);
                        if (v > worstInterval) worstInterval = v;
                    }

                    prev = m;
                }

                Check(moves.Count > 0 && worst <= bound, names[i].ToUpperInvariant() + ": fastest server tick " + worst.ToString("F2", CultureInfo.InvariantCulture) + " m/s <= resolved sprint speed x 1.15 = " + bound.ToString("F2", CultureInfo.InvariantCulture));
                Check(worstInterval <= bound, names[i].ToUpperInvariant() + ": fastest sampled server interval " + worstInterval.ToString("F2", CultureInfo.InvariantCulture) + " m/s <= " + bound.ToString("F2", CultureInfo.InvariantCulture));
            }

            // ---- 8b. U3A4b: forced correction ---------------------------------------------------------
            // The server teleports A once by 2 m along the lane (toward the spawn). A's prediction cannot know: it must snap, replay
            // and end up where the server says. (Done after the speed checks so the teleport is not read as speed.)
            {
                string textBefore = ReadShared(clientLogs[1]);
                List<Recon> reconBefore = ParseRecon(textBefore);
                int snapsBefore = reconBefore.Count > 0 ? reconBefore[reconBefore.Count - 1].Snaps : 0;
                int snapLinesBefore = Regex.Matches(textBefore, @"\[client\] reconcile SNAP ").Count;
                Check(Regex.Matches(ReadShared(serverLog), @"dev-shove applied").Count == 0, "the dev shove has not happened before the gate asks for it");

                File.WriteAllText(shoveFile, "go");
                Match shoved = WaitForRegex(serverLog, @"dev-shove applied id=" + idA + @" slot=mv-a from=" + Vec + " to=" + Vec + " meters=" + Num, 30, server);
                Check(shoved != null, "the server applied the dev shove to A");
                if (shoved != null)
                {
                    Vector3 shoveFrom = V3(shoved, 1), shoveTo = V3(shoved, 4);
                    Check(Mathf.Abs(Flat(shoveFrom, shoveTo) - 2f) < 0.05f, "the shove moved A " + Flat(shoveFrom, shoveTo).ToString("F2", CultureInfo.InvariantCulture) + " m (2 m asked)");

                    bool snapped = false;
                    DateTime snapDeadline = DateTime.UtcNow.AddSeconds(15);
                    while (DateTime.UtcNow < snapDeadline && !snapped)
                    {
                        snapped = Regex.Matches(ReadShared(clientLogs[1]), @"\[client\] reconcile SNAP ").Count > snapLinesBefore;
                        if (!snapped) Thread.Sleep(200);
                    }

                    Check(snapped, "A snapped after the server moved it (a SNAP line in A's log)");
                    Thread.Sleep(3500); // let the replay, the blend of what follows and the observers settle

                    string textAfter = ReadShared(clientLogs[1]);
                    string serverAfter = ReadShared(serverLog);
                    List<Recon> reconAfter = ParseRecon(textAfter);
                    Recon last = reconAfter.Count > 0 ? reconAfter[reconAfter.Count - 1] : null;
                    Check(last != null && last.Snaps >= snapsBefore + 1, "A's snap counter went up by at least one (" + snapsBefore + " -> " + (last != null ? last.Snaps : -1) + ")");
                    Check(last != null && last.Replays >= snapsBefore + 1 && last.Replays == last.Snaps, "every snap replayed the unacknowledged frames (replays=" + (last != null ? last.Replays : -1) + ")");

                    MatchCollection snapLines = Regex.Matches(textAfter, @"\[client\] reconcile SNAP error=" + Num);
                    Check(snapLines.Count > snapLinesBefore && Fl(snapLines[snapLines.Count - 1].Groups[1].Value) > 1.0f && Fl(snapLines[snapLines.Count - 1].Groups[1].Value) < 3.0f,
                        "the snap's measured error is about the 2 m shove (" + (snapLines.Count > 0 ? snapLines[snapLines.Count - 1].Groups[1].Value : "?") + " m)");

                    List<MoveLine> movesAfter = ParseMoves(serverAfter, idA);
                    MoveLine lastAfter = movesAfter.FindLast(m => !m.Final);
                    Check(lastAfter != null && Vector3.Distance(lastAfter.Pos, shoveTo) <= 0.1f,
                        "A stayed where it was shoved to on the server (" + (lastAfter != null ? Vector3.Distance(lastAfter.Pos, shoveTo).ToString("F3", CultureInfo.InvariantCulture) : "?") + " m from the target)");

                    bool predictedAfter = TryLastPredict(textAfter, out Vector3 predictedPos);
                    Check(predictedAfter && lastAfter != null && Vector3.Distance(predictedPos, lastAfter.Pos) <= 0.15f,
                        "after the snap A converged to the server position: " + (predictedAfter && lastAfter != null ? Vector3.Distance(predictedPos, lastAfter.Pos).ToString("F3", CultureInfo.InvariantCulture) : "?") + " m <= 0.15 m");
                    Check(predictedAfter && Vector3.Distance(predictedPos, shoveTo) <= 0.3f, "A's own position is at the shove target, not where it was predicted to be");

                    bool seenByB = TryLastView(ReadShared(clientLogs[0]), idA, out Vector3 viewedAfter);
                    Check(seenByB && lastAfter != null && Vector3.Distance(viewedAfter, lastAfter.Pos) <= 0.15f,
                        "B's view of A follows the shove: " + (seenByB && lastAfter != null ? Vector3.Distance(viewedAfter, lastAfter.Pos).ToString("F3", CultureInfo.InvariantCulture) : "?") + " m from the server position");
                    Debug.Log(Tag + " A after the forced correction: " + (last != null ? "snaps=" + last.Snaps + " replays=" + last.Replays + " blends=" + last.Blends + " acks=" + last.Acks : "no reconcile line"));
                }
            }

            // ---- 9. graceful end --------------------------------------------------------------------
            for (int i = 0; i < 4; i++) File.WriteAllText(quit[i], "quit");
            for (int i = 0; i < 4; i++)
            {
                bool exited = WaitForExit(clients[i], 30);
                Check(exited && clients[i].ExitCode == NetExitCodes.Ok, names[i].ToUpperInvariant() + " left by quit file with exit 0" + (exited ? " (was " + clients[i].ExitCode + ")" : ""));
            }

            Match lastRemoved = WaitForRegex(serverLog, @"removed client=\d+ id=\d+ slot=mv-\w reason=disconnect players=0 senders=0 registry=0", 30, server);
            Check(lastRemoved != null, "server removed all four players: players=0 senders=0 registry=0");
            Check(CountMatches(serverLog, @"move id=\d+ .* final=1 ") == 4, "the server logged a final movement line for each of the four players");

            serverText = ReadShared(serverLog);
            Dictionary<int, MoveLine> finals = new Dictionary<int, MoveLine>();
            for (int i = 0; i < 4; i++)
            {
                MoveLine fin = ParseMoves(serverText, ids[i]).Find(m => m.Final);
                if (fin != null) finals[ids[i]] = fin;
            }

            Check(finals.Count == 4, "four final lines parsed");
            if (finals.Count == 4)
            {
                MoveLine fb = finals[idB];
                Check(fb.Nan >= 15 && fb.Big >= 15 && fb.SeqJ >= 15, "B's per-reason counters: nan=" + fb.Nan + " big=" + fb.Big + " seqj=" + fb.SeqJ + " (each >= 15)");
                Check(fb.Rep >= 15 && fb.Flood >= 100 && fb.Forge >= 15, "B's counters: replay=" + fb.Rep + " flood=" + fb.Flood + " forge=" + fb.Forge);
                MoveLine fa = finals[idA];
                Check(fa.Rep == 0 && fa.Inv == 0 && fa.Forge == 0 && fa.Flood >= 100, "A (honest except flood-run): replay=" + fa.Rep + " invalid=" + fa.Inv + " forge=" + fa.Forge + " flood=" + fa.Flood);
                foreach (int id in new[] { idW, idV })
                {
                    MoveLine f = finals[id];
                    Check(f.Rep == 0 && f.Inv == 0 && f.Forge == 0 && f.Flood == 0, "honest client " + id + " tripped no counter: replay=" + f.Rep + " invalid=" + f.Inv + " forge=" + f.Forge + " flood=" + f.Flood);
                    Check(f.Acc >= 30, "honest client " + id + " had " + f.Acc + " frames accepted");
                }

                foreach (int id in finals.Keys)
                {
                    int classIndex = Array.IndexOf(ids, id);
                    Check(finals[id].VMaxAll <= regs[classIndex].Sprint * 1.15f, "player " + id + ": fastest tick of the whole run " + finals[id].VMaxAll.ToString("F2", CultureInfo.InvariantCulture) + " m/s <= resolved sprint speed x 1.15");
                }
            }

            // ---- 9b. U3A4b: what the server sent back, and A's final statistics --------------------------
            string textFinalA = ReadShared(clientLogs[1]);
            Recon finalA = ParseRecon(textFinalA).Find(r => r.At == "final");
            Check(finalA != null, "A logged its final reconcile statistics when it left");
            if (finalA != null)
            {
                Debug.Log(Tag + " A final " + C + "reconcile acks=" + finalA.Acks + " maxErr=" + finalA.MaxErr.ToString("F3", CultureInfo.InvariantCulture) +
                          " meanErr=" + finalA.MeanErr.ToString("F3", CultureInfo.InvariantCulture) + " blends=" + finalA.Blends + " snaps=" + finalA.Snaps +
                          " replays=" + finalA.Replays + " missing=" + finalA.Missing + " stale=" + finalA.Stale + " fights=" + finalA.Fights);
                Check(finalA.Acks >= 100 && finalA.Snaps >= 1 && finalA.Fights == 0,
                    "A's final statistics: acks " + finalA.Acks + " >= 100, snaps " + finalA.Snaps + " >= 1 (the shove), fights " + finalA.Fights + " == 0");
            }

            for (int i = 0; i < 4; i++)
            {
                Match fin = Regex.Match(serverText, @"move id=" + ids[i] + @" .* final=1 .* acks=(\d+) shoves=(\d+)");
                int acksSent = fin.Success ? int.Parse(fin.Groups[1].Value, CultureInfo.InvariantCulture) : -1;
                int shoves = fin.Success ? int.Parse(fin.Groups[2].Value, CultureInfo.InvariantCulture) : -1;
                Check(fin.Success && acksSent >= 100, names[i].ToUpperInvariant() + ": the server sent " + acksSent + " Acks to its owner (>= 100)");
                Check(fin.Success && shoves == (i == 1 ? 1 : 0), names[i].ToUpperInvariant() + ": shoves applied " + shoves + " (only A, once)");
            }

            File.WriteAllText(quitServer, "quit");
            bool sExited = WaitForExit(server, 30);
            Check(sExited, "server exited after the quit file");
            if (sExited) Check(server.ExitCode == NetExitCodes.Ok, "server exit code 0 (was " + server.ExitCode + ")");
            Check(LogContains(serverLog, S + "shutdown players=0 senders=0 registry=0"), "server logged 'shutdown players=0 senders=0 registry=0'");
            Check(LogContains(serverLog, S + "shutdown complete clean=True"), "server logged a clean shutdown");

            // ---- 10. forbidden patterns ----------------------------------------------------------------
            string[] forbidden = { "error CS", "NullReferenceException", "Exception:", "InvalidOperationException", "IndexOutOfRangeException" };
            foreach (string path in Directory.GetFiles(logs, "*.log"))
            {
                string text = ReadShared(path);
                foreach (string pattern in forbidden)
                    Check(text.IndexOf(pattern, StringComparison.Ordinal) < 0, Path.GetFileName(path) + " has no '" + pattern + "'");
            }

            // ---- 11. no process left ---------------------------------------------------------------
            foreach (Process p in Started) Check(SafeHasExited(p), "pid " + p.Id + " is gone");
            return 0;
        }

        // ---- static scan --------------------------------------------------------------------------------------------

        /// <summary>
        /// Source scan: the movement RPCs exist (SubmitMoveRpc, ForgeMoveTestRpc, plus the U3A3 probe) and no [Rpc] under Net/
        /// has a parameter list naming a player, combat, party, sender or actor id.
        /// </summary>
        private static void CheckMovementRpcSignatures(string projectRoot)
        {
            string dir = Path.Combine(projectRoot, "Assets", "Emberdelve", "Net");
            int rpcs = 0;
            int bad = 0;
            bool submit = false, forge = false, ack = false;
            if (Directory.Exists(dir))
            {
                foreach (string file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
                {
                    string text = File.ReadAllText(file);
                    int from = 0;
                    while (true)
                    {
                        int at = text.IndexOf("[Rpc(", from, StringComparison.Ordinal);
                        if (at < 0) break;
                        from = at + 5;
                        int close = text.IndexOf(']', at);
                        int open = close < 0 ? -1 : text.IndexOf('(', close);
                        int end = open < 0 ? -1 : text.IndexOf(')', open);
                        if (end < 0) continue;
                        rpcs++;
                        string head = text.Substring(close, open - close);
                        if (head.Contains("SubmitMoveRpc")) submit = true;
                        if (head.Contains("ForgeMoveTestRpc")) forge = true;
                        if (head.Contains("AckRpc")) ack = true;
                        string parameters = text.Substring(open, end - open + 1);
                        if (Regex.IsMatch(parameters, @"(?i)(player|combat|party|sender|actor)\s*_?id"))
                        {
                            bad++;
                            Debug.LogError(Tag + " RPC with an identity parameter in " + Path.GetFileName(file) + ": " + parameters);
                        }
                    }
                }
            }

            Check(rpcs >= 4 && submit && forge && ack, "source scan found the movement RPCs (SubmitMoveRpc, ForgeMoveTestRpc, and U3A4b's AckRpc) among " + rpcs + " [Rpc] methods");
            Check(bad == 0, "no [Rpc] signature under Net/ carries a player/combat/party id parameter");
        }

        // ---- helpers ------------------------------------------------------------------------------------------------

        private static string Yaw(float degrees) { return degrees.ToString("F1", CultureInfo.InvariantCulture); }

        private static string ClientArgs(int port, string slot, string classId, string quitFileQuoted, string extra)
        {
            string args = "-ember-net client -ember-address 127.0.0.1 -ember-port " + port +
                          " -ember-slot " + slot + " -ember-class " + classId +
                          " -ember-net-connect-timeout 60 -ember-net-disconnect-timeout-ms " + DisconnectTimeoutMs;
            if (!string.IsNullOrEmpty(quitFileQuoted)) args += " -ember-net-quit-file " + quitFileQuoted;
            if (!string.IsNullOrEmpty(extra)) args += " " + extra;
            return args;
        }

        private static void Check(bool ok, string what)
        {
            _checks++;
            if (!ok) _failures++;
            if (ok) Debug.Log(Tag + " ok: " + what); else Debug.LogError(Tag + " FAIL: " + what);
        }

        private static string Q(string path) { return "\"" + path + "\""; }

        private static Process Launch(string exe, string logPath, string gameArgs)
        {
            try
            {
                if (File.Exists(logPath)) File.Delete(logPath);
                ProcessStartInfo info = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "-batchmode -nographics -logFile " + Q(logPath) + " " + gameArgs,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process process = Process.Start(info);
                if (process != null) Started.Add(process);
                Debug.Log(Tag + " launched pid=" + (process != null ? process.Id.ToString() : "-") + " " + info.Arguments);
                return process;
            }
            catch (Exception e)
            {
                Debug.LogError(Tag + " launch failed: " + e.Message);
                return null;
            }
        }

        private static bool WaitForLog(string path, string needle, double seconds, Process owner)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(seconds);
            while (true)
            {
                if (LogContains(path, needle)) return true;
                if (DateTime.UtcNow >= end) return false;
                if (owner != null && SafeHasExited(owner)) return LogContains(path, needle);
                Thread.Sleep(200);
            }
        }

        private static Match WaitForRegex(string path, string pattern, double seconds, Process owner)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(seconds);
            while (true)
            {
                Match m = Regex.Match(ReadShared(path), pattern);
                if (m.Success) return m;
                if (DateTime.UtcNow >= end) return null;
                if (owner != null && SafeHasExited(owner))
                {
                    m = Regex.Match(ReadShared(path), pattern);
                    return m.Success ? m : null;
                }

                Thread.Sleep(200);
            }
        }

        private static int CountMatches(string path, string pattern)
        {
            return Regex.Matches(ReadShared(path), pattern).Count;
        }

        private static bool LogContains(string path, string needle)
        {
            return ReadShared(path).IndexOf(needle, StringComparison.Ordinal) >= 0;
        }

        private static string ReadShared(string path)
        {
            try
            {
                if (!File.Exists(path)) return "";
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader(fs, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        private static bool SafeHasExited(Process p)
        {
            try { return p == null || p.HasExited; }
            catch (Exception) { return true; }
        }

        /// <summary>Waits for a graceful exit. On timeout kills only this process (the caller's check then fails).</summary>
        private static bool WaitForExit(Process p, double seconds)
        {
            try
            {
                if (p.WaitForExit((int)(seconds * 1000))) return true;
                Debug.LogError(Tag + " pid " + p.Id + " did not exit within " + seconds + "s; killing it");
                _failures++;
                _checks++;
                p.Kill();
                p.WaitForExit(5000);
            }
            catch (Exception e)
            {
                Debug.LogError(Tag + " wait/kill failed: " + e.Message);
            }

            return false;
        }

        private static void KillSurvivors()
        {
            foreach (Process p in Started)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        Debug.LogError(Tag + " cleanup: killing own pid " + p.Id);
                        _failures++;
                        _checks++;
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                }
                catch (Exception) { /* already gone */ }
            }
        }

        private static int FreeUdpPort()
        {
            try
            {
                using (UdpClient udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                    return ((IPEndPoint)udp.Client.LocalEndPoint).Port;
            }
            catch (Exception) { return -1; }
        }

        private static string ArgValue(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1].Trim('"');
            return null;
        }
    }
}
