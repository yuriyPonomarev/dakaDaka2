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
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// U3A5 final acceptance gate (the roadmap row "Real independent process connection/input/cleanup evidence"): the whole
    /// network scenario of Docs/Tasks/U3A_plan.md section 5.3 with real independent processes on loopback, plus the offline-retention
    /// block. It does not replace the earlier gates (NetProof, NetAdmission, NetMovement, NetSeams keep their checks); it is the single
    /// entry that ties them together.
    ///
    ///   Unity.exe -batchmode -nographics -projectPath . -logFile Logs/net_final.log
    ///             -executeMethod Emberdelve.EditorTools.EmberdelveNetFinalGate.Run
    ///
    /// (Do NOT pass -quit.) Optional: -ember-proof-dir ABS_DIR (default ProjectRoot/Builds/net-final/yyyyMMdd-HHmmss),
    /// -ember-proof-reuse-exe ABS_EXE (skip the build; the exe must be a U3A5 final player: scenes Lobby + Level01). A live Editor
    /// only blocks and returns; the Editor is exited only in batch mode, through EmberdelveGateExit.
    ///
    /// One player build (BuildNetFinalPlayer: Lobby, then Level01) serves every process:
    ///   1  server --ember-net-max-players 3 --ember-net-no-enemies
    ///   2  A (warrior) and B (archer) connect; each logs owner id views=2 owned=1
    ///   3  A walks forward 3 s: the server's displacement is the resolved walk speed x 3 s (+-20 %); B's view of A follows
    ///   4  B runs six cheats (NaN, big move, sequence jump, replay, flood, forged owner): one REJECT line each, A and B did not move
    ///   5  C LATE-JOINS through the lobby's Join code path (the lobby starts without -ember-net, the Online panel is driven by the
    ///      development hook -ember-lobby-autojoin): C sees A and B at their current server positions, unique ids, no duplicate view,
    ///      its spawn is the first spawn nobody stands on; A and B see C
    ///   6  D is refused "server full"
    ///   7  B is hard-killed: removal within the transport timeout with players=2 senders=2 registry=2, A and C log the despawn
    ///   8  D retries: admitted with a new id; D quits gracefully (removed, counts drop)
    ///   9  B comes back with the same slot: admitted as a NEW actor with a new id ("rejoin" line)
    ///  10  A, C and B quit gracefully (exit 0), the server quits by file with players=0 senders=0 registry=0 and exit 0
    ///  11  the UDP port is free again, every PID is gone, no forbidden log pattern in any log
    /// Offline retention: (a) the earlier gates are separate entries (see the run order in Docs/Tasks/U3A5.md); (b) the same
    /// build launched WITHOUT network arguments for 8 s reaches the lobby and writes zero "[Emberdelve][Net]" lines; (c) without play
    /// mode, the saved Level01 and Lobby scene files reference no NetworkObject / NetworkManager / any Unity.Netcode or Emberdelve.Net
    /// script (the same scanner finds them in the network player prefab, so it is not vacuous) and NetworkManager.Singleton is null.
    /// Also: the documented launch scripts under Tools/net parse with the real launch-argument parser.
    /// Only PIDs this gate started are ever killed. A kill forced by a timeout is a failure; the hard kill of B and the stop of the
    /// never-ending offline probe are the scenario. Final line: [Emberdelve][NetFinal] PASS|FAIL checks=N failures=M
    /// </summary>
    public static class EmberdelveNetFinalGate
    {
        private const string Tag = "[Emberdelve][NetFinal]";
        private const string S = "[Emberdelve][Net][server] ";
        private const string C = "[Emberdelve][Net][client] ";
        private const int DisconnectTimeoutMs = 4000;
        private const double RemovalDeadlineSeconds = 20.0;
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

        // ---- parsed server lines (same formats the movement gate verified) ------------------------------------------------

        private sealed class Reg
        {
            public int Id;
            public float Walk, Sprint;
            public Vector3 Pos;
        }

        private sealed class Seg
        {
            public float Disp, Peak;
            public Vector3 From, To;
        }

        private sealed class MoveLine
        {
            public Vector3 Pos;
            public long Tick;
            public float VMax;
            public bool Final;
            public int Nan, Big, SeqJ, Rep, Flood, Forge;
        }

        private static Reg ParseReg(string serverText, int id)
        {
            Match m = Regex.Match(serverText, @"movement registered id=" + id + @" client=\d+ walk=" + Num + " sprint=" + Num + " crouch=" + Num +
                                              " jump=" + Num + " pos=" + Vec);
            if (!m.Success) return null;
            return new Reg { Id = id, Walk = Fl(m.Groups[1].Value), Sprint = Fl(m.Groups[2].Value), Pos = V3(m, 5) };
        }

        private static List<Seg> ParseSegs(string serverText, int id)
        {
            List<Seg> list = new List<Seg>();
            foreach (Match m in Regex.Matches(serverText, @"seg id=" + id + @" kind=move startTick=\d+ endTick=\d+ ticks=(\d+) secs=" + Num + " dist=" + Num + " disp=" + Num +
                                                          " mean=" + Num + " peak=" + Num + " from=" + Vec + " to=" + Vec + @"( open=1)?"))
            {
                list.Add(new Seg { Disp = Fl(m.Groups[4].Value), Peak = Fl(m.Groups[6].Value), From = V3(m, 7), To = V3(m, 10) });
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
                    Pos = V3(m, 1),
                    Rep = int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture),
                    Flood = int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture),
                    Tick = long.Parse(m.Groups[8].Value, CultureInfo.InvariantCulture),
                    Forge = int.Parse(m.Groups[12].Value, CultureInfo.InvariantCulture),
                    VMax = Fl(m.Groups[13].Value),
                    Final = m.Groups[16].Success
                };
                if (line.Final)
                {
                    line.Nan = int.Parse(m.Groups[18].Value, CultureInfo.InvariantCulture);
                    line.Big = int.Parse(m.Groups[19].Value, CultureInfo.InvariantCulture);
                    line.SeqJ = int.Parse(m.Groups[20].Value, CultureInfo.InvariantCulture);
                }

                list.Add(line);
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

        private static Vector3 V3(Match m, int first)
        {
            return new Vector3(Fl(m.Groups[first].Value), Fl(m.Groups[first + 1].Value), Fl(m.Groups[first + 2].Value));
        }

        private static float Fl(string text) { return float.Parse(text, CultureInfo.InvariantCulture); }

        private static string F2(float v) { return v.ToString("F2", CultureInfo.InvariantCulture); }

        private static float Flat(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static string Yaw(float degrees) { return degrees.ToString("F1", CultureInfo.InvariantCulture); }

        // ---- layout geometry ----------------------------------------------------------------------------------------------

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

        /// <summary>How far a capsule-wide lane from <paramref name="start"/> along <paramref name="yaw"/> stays on walkable cells and 1 m clear of the given points.</summary>
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

        // ---- the scenario -------------------------------------------------------------------------------------------------

        private static int Execute()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string runDir = ArgValue("-ember-proof-dir");
            if (string.IsNullOrEmpty(runDir))
                runDir = Path.Combine(projectRoot, "Builds", "net-final", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            runDir = Path.GetFullPath(runDir);
            if (runDir.StartsWith(Path.GetFullPath(Application.dataPath), StringComparison.OrdinalIgnoreCase))
            {
                Check(false, "run dir must be outside Assets: " + runDir);
                return 1;
            }

            string logs = Path.Combine(runDir, "logs");
            Directory.CreateDirectory(logs);
            Debug.Log(Tag + " runDir=" + runDir);

            // ---- 0. static checks (no processes, no play mode) --------------------------------------------------------------
            CheckLaunchScripts(projectRoot);

            DungeonLayoutData layout = AssetDatabase.LoadAssetAtPath<DungeonLayoutData>(EmberdelvePaths.Data + "/DungeonLayout_AshenHalls.asset");
            CharacterClassData warrior = AssetDatabase.LoadAssetAtPath<CharacterClassData>(EmberdelvePaths.Data + "/Class_Warrior.asset");
            CharacterClassData archer = AssetDatabase.LoadAssetAtPath<CharacterClassData>(EmberdelvePaths.Data + "/Class_Archer.asset");
            Check(layout != null && warrior != null && archer != null, "layout and both class assets load");
            if (layout == null || warrior == null || archer == null) return 1;
            Check(layout.playerSpawns != null && layout.playerSpawns.Count >= 4, "the layout has at least 4 player spawns (" + (layout.playerSpawns != null ? layout.playerSpawns.Count : 0) + ")");
            if (layout.playerSpawns == null || layout.playerSpawns.Count < 4) return 1;

            bool[,] grid = layout.BuildWalkableGrid();
            int spawnCount = layout.playerSpawns.Count;
            Vector3[] spawn = new Vector3[spawnCount];
            for (int i = 0; i < spawnCount; i++) spawn[i] = layout.PlayerSpawnWorld(i);

            // A joins first (spawn 0), B second (spawn 1). A walks along the freest axis, clear of B's spawn.
            List<Vector3> aOthers = new List<Vector3> { spawn[1] };
            float yawA = 0f, bestFree = -1f;
            foreach (float candidate in new[] { 0f, 90f, 180f, 270f })
            {
                float free = FreeDistance(layout, grid, spawn[0], candidate, 40f, 0.45f, aOthers);
                if (free > bestFree) { bestFree = free; yawA = candidate; }
            }

            float walkNeeded = warrior.moveSpeed * 3f * 1.2f + 0.5f;
            Check(bestFree >= walkNeeded, "A's lane is free for the whole walk: yaw " + yawA + " free " + F2(bestFree) + " m >= " + F2(walkNeeded));
            if (bestFree < walkNeeded) return 1;

            // ---- 1. player ----------------------------------------------------------------------------------------------------
            string exe = ArgValue("-ember-proof-reuse-exe");
            if (string.IsNullOrEmpty(exe))
            {
                exe = Path.Combine(runDir, "player", "EmberdelveNet.exe");
                exe = EmberdelvePlayerBuildCache.TestPlayerPath(exe, "net-final");
                bool built = EmberdelveWindowsBuild.BuildNetFinalPlayer(exe, out string buildError);
                Check(built, "final player build (Lobby + Level01)" + (built ? "" : " failed: " + buildError));
                if (!built) return 1;
            }
            else
            {
                Check(File.Exists(exe), "reused player exists: " + exe);
                if (!File.Exists(exe)) return 1;
            }

            CheckOfflineScenesHaveNoNetwork();

            // ---- 2. server ----------------------------------------------------------------------------------------------------
            int port = FreeUdpPort();
            Check(port > 0, "free udp port " + port);
            if (port <= 0) return 1;

            string quitServer = Path.Combine(runDir, "server.quit");
            string quitA = Path.Combine(runDir, "client_a.quit");
            string quitC = Path.Combine(runDir, "client_c.quit");
            string quitD = Path.Combine(runDir, "client_d.quit");
            string quitB2 = Path.Combine(runDir, "client_b2.quit");
            string startA = Path.Combine(runDir, "client_a.start");
            string startB = Path.Combine(runDir, "client_b.start");
            foreach (string f in new[] { quitServer, quitA, quitC, quitD, quitB2, startA, startB }) if (File.Exists(f)) File.Delete(f);

            string serverLog = Path.Combine(logs, "server.log");
            Process server = Launch(exe, serverLog,
                "-ember-net server -ember-port " + port + " -ember-net-quit-file " + Q(quitServer) +
                " -ember-net-autoquit 900 -ember-net-disconnect-timeout-ms " + DisconnectTimeoutMs +
                " -ember-net-max-players 3 -ember-net-no-enemies");
            Check(server != null, "server process started pid=" + (server != null ? server.Id.ToString() : "-"));
            if (server == null) return 1;

            Check(WaitForLog(serverLog, S + "listening", 60, server), "server logged 'listening'");
            Match ready = WaitForRegex(serverLog, @"\[server\] ready scene=\S+ players=(\d+) senders=(\d+) registry=(\d+)", 90, server);
            Check(ready != null, "server (started from the lobby-first build) loaded the dungeon and logged 'ready'");
            if (ready == null) return 1;
            Check(ready.Groups[1].Value == "0" && ready.Groups[2].Value == "0" && ready.Groups[3].Value == "0", "ready with players=0 senders=0 registry=0");
            Check(LogContains(serverLog, "[Emberdelve][Lobby] skipped: process role is DedicatedServer"), "the server did not build the lobby screen (network role)");

            // ---- 3. A (warrior, walks) and B (archer, cheats) ------------------------------------------------------------------
            string logA = Path.Combine(logs, "client_a.log");
            string logB = Path.Combine(logs, "client_b.log");
            string scriptA = "walk-forward:3@" + Yaw(yawA);
            string scriptB = "nan-input:1.5,big-move:1.5,seq-jump:1.5,replay-seq:1.5,flood-input:1.5,forge-move:1.5";

            Process a = Launch(exe, logA, ClientArgs(port, "fin-a", "warrior", Q(quitA), "-ember-net-script " + scriptA + " -ember-net-script-start-file " + Q(startA)) + " -ember-net-autoquit 600");
            Check(a != null, "client A started");
            if (a == null) return 1;
            Match admA = WaitForRegex(serverLog, @"admitted client=(\d+) id=(\d+) party=\d+ slot=fin-a class=warrior netId=\d+ players=1 senders=1 registry=1", 90, server);
            Check(admA != null, "server admitted A (warrior): players=1 senders=1 registry=1");
            if (admA == null) return 1;

            Process b = Launch(exe, logB, ClientArgs(port, "fin-b", "archer", "", "-ember-net-script " + scriptB + " -ember-net-script-start-file " + Q(startB)) + " -ember-net-autoquit 600");
            Check(b != null, "client B started");
            if (b == null) return 1;
            Match admB = WaitForRegex(serverLog, @"admitted client=(\d+) id=(\d+) party=\d+ slot=fin-b class=archer netId=\d+ players=2 senders=2 registry=2", 90, server);
            Check(admB != null, "server admitted B (archer): players=2 senders=2 registry=2");
            if (admB == null) return 1;

            int idA = int.Parse(admA.Groups[2].Value, CultureInfo.InvariantCulture);
            int idB = int.Parse(admB.Groups[2].Value, CultureInfo.InvariantCulture);
            Check(idA > 0 && idB > 0 && idA != idB, "distinct server ids A=" + idA + " B=" + idB);

            string serverText = ReadShared(serverLog);
            Reg regA = ParseReg(serverText, idA), regB = ParseReg(serverText, idB);
            Check(regA != null && regB != null, "the server registered movement for A and B");
            if (regA == null || regB == null) return 1;
            Check(Flat(regA.Pos, spawn[0]) < 0.05f && Flat(regB.Pos, spawn[1]) < 0.05f, "A spawned at layout slot 0 and B at slot 1");

            string idsAB = SortedIds(idA, idB);
            Check(WaitForRegex(logA, @"\[client\] owner id=" + idA + @" views=2 owned=1 class=warrior party=\d+ ids=" + idsAB, 60, a) != null, "A logs owner id=" + idA + " views=2 owned=1 ids=" + idsAB);
            Check(WaitForRegex(logB, @"\[client\] owner id=" + idB + @" views=2 owned=1 class=archer party=\d+ ids=" + idsAB, 60, b) != null, "B logs owner id=" + idB + " views=2 owned=1 ids=" + idsAB);

            // ---- 4. go: A walks, B cheats --------------------------------------------------------------------------------------
            File.WriteAllText(startA, "go");
            File.WriteAllText(startB, "go");
            Check(WaitForLog(logA, C + "script chain done steps=1", 60, a), "A finished its 3 s walk");
            Check(WaitForLog(logB, C + "script chain done steps=6", 90, b), "B finished all six cheats");
            Thread.Sleep(3500); // let the observers' heartbeat view lines and the server's last move lines catch up

            serverText = ReadShared(serverLog);
            List<Seg> segA = ParseSegs(serverText, idA);
            Check(segA.Count >= 1, "A has a movement segment on the server (" + segA.Count + ")");
            Seg walk = segA.Count > 0 ? segA[0] : null;
            if (walk != null)
            {
                float expected = regA.Walk * 3f;
                Check(walk.Disp >= expected * 0.8f && walk.Disp <= expected * 1.2f,
                    "A walked " + F2(walk.Disp) + " m in 3 s; expected " + F2(expected) + " m (resolved walk speed " + F2(regA.Walk) + " x 3 s) +-20%");
                Check(walk.Peak <= regA.Walk * 1.15f, "A's walk never exceeds walk speed x 1.15 (peak " + F2(walk.Peak) + ")");
                Vector3 walkDir = walk.To - walk.From; walkDir.y = 0f;
                Check(Vector3.Dot(walkDir.normalized, Dir(yawA)) > 0.99f, "A walked along its view yaw " + yawA);
            }

            List<MoveLine> movesA = ParseMoves(serverText, idA);
            List<MoveLine> movesB = ParseMoves(serverText, idB);
            MoveLine lastA = movesA.FindLast(m => !m.Final);
            MoveLine lastB = movesB.FindLast(m => !m.Final);
            Check(lastA != null && lastB != null, "the server logged positions for A and B");
            if (lastA == null || lastB == null) return 1;
            Check(walk != null && Flat(lastA.Pos, walk.To) < 0.05f, "A is where its walk ended: B's cheats (forged movement named A) did not move it");

            string textB = ReadShared(logB);
            bool seenAbyB = TryLastView(textB, idA, out Vector3 viewedAbyB);
            Check(seenAbyB && Vector3.Distance(viewedAbyB, lastA.Pos) <= 0.15f,
                "B's last view of A is within 0.15 m of the server position (" + (seenAbyB ? F2(Vector3.Distance(viewedAbyB, lastA.Pos)) : "?") + " m)");

            string textA = ReadShared(logA);
            Match reconA = Regex.Match(textA, @"\[client\] reconcile acks=(\d+) maxErr=" + Num + " meanErr=" + Num + @" blends=(\d+) snaps=(\d+) replays=(\d+) replayFrames=\d+ missing=\d+ stale=\d+ fights=(\d+) at=script-end");
            Check(reconA.Success && reconA.Groups[5].Value == "0" && reconA.Groups[7].Value == "0",
                "A's own walk had no prediction snap and no fight with the transform sync" + (reconA.Success ? " (snaps=" + reconA.Groups[5].Value + ")" : " (no script-end reconcile line)"));

            // B's six cheats: one REJECT each, B itself did not move, nobody went faster than the bound
            foreach (string kind in new[] { "nan", "big-move", "seq-jump", "replay", "flood", "forge-move" })
                Check(Regex.IsMatch(serverText, @"REJECT " + kind + @" count=1 id=" + idB + @"\b"), "server logged REJECT " + kind + " for B");
            Check(Regex.IsMatch(serverText, @"REJECT forge-move count=1 id=" + idB + " target=" + idA + " "), "the forged movement named A's object (the only other player) and was rejected");
            MoveLine firstB = movesB.Count > 0 ? movesB[0] : null;
            Check(firstB != null && Flat(firstB.Pos, lastB.Pos) < 0.02f && Flat(lastB.Pos, regB.Pos) < 0.05f, "B (the cheater) did not move");
            Check(lastB.Forge >= 1 && lastB.Flood >= 1 && lastB.Rep >= 1, "B's counters rose: forge=" + lastB.Forge + " flood=" + lastB.Flood + " replay=" + lastB.Rep);
            float worstA = 0f, worstB = 0f;
            foreach (MoveLine m in movesA) if (m.VMax > worstA) worstA = m.VMax;
            foreach (MoveLine m in movesB) if (m.VMax > worstB) worstB = m.VMax;
            Check(worstA <= regA.Sprint * 1.15f && worstB <= regB.Sprint * 1.15f,
                "speed bound: fastest server tick A " + F2(worstA) + " / B " + F2(worstB) + " m/s <= resolved sprint x 1.15 (" + F2(regA.Sprint * 1.15f) + " / " + F2(regB.Sprint * 1.15f) + ")");

            Vector3 posA = lastA.Pos, posB = lastB.Pos; // positions of the two existing players just before the late join

            // ---- 5. C late-joins through the lobby's Join path ------------------------------------------------------------------
            string logC = Path.Combine(logs, "client_c.log");
            Process c = Launch(exe, logC,
                "-ember-lobby-autojoin 127.0.0.1:" + port + " -ember-slot fin-c -ember-net-connect-timeout 60 -ember-net-disconnect-timeout-ms " + DisconnectTimeoutMs +
                " -ember-net-quit-file " + Q(quitC) + " -ember-net-autoquit 600");
            Check(c != null, "client C started WITHOUT -ember-net (lobby + autojoin hook)");
            if (c == null) return 1;
            Match admC = WaitForRegex(serverLog, @"admitted client=(\d+) id=(\d+) party=\d+ slot=fin-c class=(\w+) netId=\d+ players=3 senders=3 registry=3", 120, server);
            Check(admC != null, "server admitted C (late join, through the lobby Join): players=3 senders=3 registry=3");
            if (admC == null) return 1;
            int idC = int.Parse(admC.Groups[2].Value, CultureInfo.InvariantCulture);
            Check(idC > 0 && idC != idA && idC != idB, "C has a unique id " + idC);

            string textC0 = ReadShared(logC);
            int lobbyReady = textC0.IndexOf("[Emberdelve][Lobby] ready", StringComparison.Ordinal);
            int firstNet = textC0.IndexOf("[Emberdelve][Net]", StringComparison.Ordinal);
            Check(lobbyReady >= 0 && firstNet > lobbyReady, "C reached the lobby first and wrote its first network line only after that");
            Check(textC0.Contains("[Emberdelve][Lobby] autojoin armed address=127.0.0.1:" + port) &&
                  textC0.Contains("[Emberdelve][Lobby] online join address=127.0.0.1 port=" + port + " class="),
                "C's Online panel parsed the address and started a Join with the lobby's class");
            Check(textC0.Contains("[Emberdelve][Net][client] boot ") && textC0.Contains("slot=fin-c"), "C booted the client role from the panel (same bootstrap as the command line)");

            string idsABC = SortedIds(idA, idB, idC);
            Match ownerC = WaitForRegex(logC, @"\[client\] owner id=" + idC + @" views=3 owned=1 class=\w+ party=\d+ ids=(" + idsABC + ")", 60, c);
            Check(ownerC != null, "C logs owner id=" + idC + " views=3 owned=1 ids=" + idsABC + " (A, B and itself, each once)");
            Thread.Sleep(3500);
            string textC = ReadShared(logC);
            Check(!Regex.IsMatch(textC, @"views=([4-9]|\d\d)\b"), "C never saw more than 3 views (no duplicate)");
            Check(!Regex.IsMatch(textC, @"owner id=" + idA + @" ") && !Regex.IsMatch(textC, @"owner id=" + idB + @" "), "C never claims A's or B's id as its own");

            serverText = ReadShared(serverLog);
            Reg regC = ParseReg(serverText, idC);
            Check(regC != null, "the server registered movement for C");
            if (regC == null) return 1;
            int expectedSlot = -1;
            for (int i = 0; i < spawnCount && expectedSlot < 0; i++)
                if (Flat(spawn[i], posA) >= 1.2f && Flat(spawn[i], posB) >= 1.2f) expectedSlot = i;
            Check(expectedSlot >= 0 && Flat(regC.Pos, spawn[expectedSlot]) < 0.05f,
                "C spawned on the first free layout spawn (slot " + expectedSlot + "), not on top of anybody");
            Check(Flat(regC.Pos, posA) >= 1.2f && Flat(regC.Pos, posB) >= 1.2f, "C's spawn is clear of A and B (" + F2(Flat(regC.Pos, posA)) + " m / " + F2(Flat(regC.Pos, posB)) + " m)");

            bool seenA = TryLastView(textC, idA, out Vector3 viewA);
            bool seenB = TryLastView(textC, idB, out Vector3 viewB);
            Check(seenA && seenB, "C logged a view of A and of B");
            if (seenA && seenB)
            {
                MoveLine nowA = ParseMoves(serverText, idA).FindLast(m => !m.Final);
                MoveLine nowB = ParseMoves(serverText, idB).FindLast(m => !m.Final);
                Check(nowA != null && Vector3.Distance(viewA, nowA.Pos) <= 0.15f, "C sees A at its current server position (" + (nowA != null ? F2(Vector3.Distance(viewA, nowA.Pos)) : "?") + " m off)");
                Check(nowB != null && Vector3.Distance(viewB, nowB.Pos) <= 0.15f, "C sees B at its current server position (" + (nowB != null ? F2(Vector3.Distance(viewB, nowB.Pos)) : "?") + " m off)");
                Check(Flat(viewA, posA) < 0.2f && Flat(viewB, posB) < 0.2f, "and those are where A and B stood before C joined (the late join did not reset anybody)");
            }

            Check(WaitForRegex(logA, @"\[client\] owner id=" + idA + @" views=3 owned=1 class=warrior party=\d+ ids=" + idsABC, 40, a) != null, "A sees C: views=3, ids=" + idsABC);
            Check(WaitForRegex(logB, @"\[client\] owner id=" + idB + @" views=3 owned=1 class=archer party=\d+ ids=" + idsABC, 40, b) != null, "B sees C: views=3, ids=" + idsABC);

            // ---- 6. D refused: server full -----------------------------------------------------------------------------------
            RefusedClient(exe, logs, serverLog, port, "d_full", "fin-d", "archer", "server full");
            Check(CountMatches(serverLog, @"approval id=\d+ approved=True") == 3 && CountMatches(serverLog, @"admitted client=") == 3,
                "still exactly three approvals and three actors (A, B, C)");

            // ---- 7. B hard-killed ---------------------------------------------------------------------------------------------
            Stopwatch killWatch = Stopwatch.StartNew();
            try { b.Kill(); b.WaitForExit(5000); } catch (Exception e) { Check(false, "could not kill B: " + e.Message); }
            Match removedB = WaitForRegex(serverLog, @"removed client=\d+ id=" + idB + @" slot=fin-b reason=disconnect players=(\d+) senders=(\d+) registry=(\d+)", RemovalDeadlineSeconds + 10, server);
            killWatch.Stop();
            Check(removedB != null, "server removed B's actor after the hard kill");
            if (removedB != null)
            {
                Check(killWatch.Elapsed.TotalSeconds <= RemovalDeadlineSeconds, "removal took " + F2((float)killWatch.Elapsed.TotalSeconds) + " s (<= " + RemovalDeadlineSeconds + " s; transport timeout " + DisconnectTimeoutMs + " ms)");
                Check(removedB.Groups[1].Value == "2" && removedB.Groups[2].Value == "2" && removedB.Groups[3].Value == "2",
                    "counts dropped to players=2 senders=2 registry=2 (was " + removedB.Groups[1].Value + "/" + removedB.Groups[2].Value + "/" + removedB.Groups[3].Value + ")");
            }

            string idsAC = SortedIds(idA, idC);
            Check(WaitForLog(logA, C + "view despawned id=" + idB + " owned=False", 30, a), "A logged the despawn of B's view");
            Check(WaitForLog(logC, C + "view despawned id=" + idB + " owned=False", 30, c), "C logged the despawn of B's view");
            Check(WaitForRegex(logA, @"\[client\] owner id=" + idA + @" views=2 owned=1 class=warrior party=\d+ ids=" + idsAC + @"\s", 30, a) != null, "A settles on views=2 (A, C)");
            Check(!SafeHasExited(a) && !SafeHasExited(c), "A and C stayed connected and running");

            // ---- 8. D retries and is admitted; D leaves gracefully ------------------------------------------------------------------
            string logD = Path.Combine(logs, "client_d.log");
            Process d = Launch(exe, logD, ClientArgs(port, "fin-d", "archer", Q(quitD), "") + " -ember-net-autoquit 600");
            Check(d != null, "client D (retry) started");
            if (d == null) return 1;
            Match admD = WaitForRegex(serverLog, @"admitted client=(\d+) id=(\d+) party=\d+ slot=fin-d class=archer netId=\d+ players=3 senders=3 registry=3", 90, server);
            Check(admD != null, "server admitted D on the retry (the killed B's seat was really freed): players=3 senders=3 registry=3");
            if (admD == null) return 1;
            int idD = int.Parse(admD.Groups[2].Value, CultureInfo.InvariantCulture);
            Check(idD != idA && idD != idB && idD != idC, "D has a new id " + idD);
            Check(CountMatches(serverLog, @"approval id=\d+ approved=False slot=fin-d class=archer reason=server full") == 1 &&
                  CountMatches(serverLog, @"approval id=\d+ approved=True slot=fin-d") == 1, "slot fin-d: one refusal (full), then one approval");
            string idsACD = SortedIds(idA, idC, idD);
            Check(WaitForRegex(logD, @"\[client\] owner id=" + idD + @" views=3 owned=1 class=archer party=\d+ ids=" + idsACD, 60, d) != null, "D logs owner id=" + idD + " views=3 ids=" + idsACD);
            serverText = ReadShared(serverLog);
            Reg regD = ParseReg(serverText, idD);
            MoveLine nowAd = ParseMoves(serverText, idA).FindLast(m => !m.Final);
            MoveLine nowCd = ParseMoves(serverText, idC).FindLast(m => !m.Final);
            Vector3 posC = regC.Pos;
            Check(regD != null && Flat(regD.Pos, posC) >= 1.2f && (nowAd == null || Flat(regD.Pos, nowAd.Pos) >= 1.2f), "D's spawn is clear of A and C");

            File.WriteAllText(quitD, "quit");
            bool dExited = WaitForExit(d, 30);
            Check(dExited && d.ExitCode == NetExitCodes.Ok, "D left gracefully by quit file with exit 0" + (dExited ? " (was " + d.ExitCode + ")" : ""));
            Check(LogContains(logD, C + "shutdown complete clean=True"), "D logged a clean shutdown");
            Match removedD = WaitForRegex(serverLog, @"removed client=\d+ id=" + idD + @" slot=fin-d reason=disconnect players=(\d+) senders=(\d+) registry=(\d+)", 30, server);
            Check(removedD != null && removedD.Groups[1].Value == "2" && removedD.Groups[2].Value == "2" && removedD.Groups[3].Value == "2",
                "server removed D (graceful): players=2 senders=2 registry=2");

            // ---- 9. B comes back with the same slot: a NEW actor ---------------------------------------------------------------------------
            string logB2 = Path.Combine(logs, "client_b2.log");
            Process b2 = Launch(exe, logB2, ClientArgs(port, "fin-b", "archer", Q(quitB2), "") + " -ember-net-autoquit 600");
            Check(b2 != null, "client B2 (same slot fin-b as the killed B) started");
            if (b2 == null) return 1;
            Match admB2 = WaitForRegex(serverLog, @"admitted client=(\d+) id=(\d+) party=\d+ slot=fin-b class=archer netId=\d+ players=3 senders=3 registry=3", 90, server);
            Check(admB2 != null, "server admitted the returning slot fin-b: players=3 senders=3 registry=3");
            if (admB2 == null) return 1;
            int idB2 = int.Parse(admB2.Groups[2].Value, CultureInfo.InvariantCulture);
            Check(idB2 != idB && idB2 != idA && idB2 != idC && idB2 != idD, "the returning slot is a NEW actor: id " + idB2 + " (not B's old " + idB + ")");
            Check(WaitForLog(serverLog, "rejoin slot=fin-b newId=" + idB2 + " previousId=" + idB + " (", 20, server), "server logged the rejoin (new id, previous id)");
            string idsACB2 = SortedIds(idA, idC, idB2);
            Check(WaitForRegex(logB2, @"\[client\] owner id=" + idB2 + @" views=3 owned=1 class=archer party=\d+ ids=" + idsACB2, 60, b2) != null, "B2 logs owner id=" + idB2 + " views=3 ids=" + idsACB2 + " (the old B is gone)");
            Check(WaitForRegex(logA, @"\[client\] owner id=" + idA + @" views=3 owned=1 class=warrior party=\d+ ids=" + idsACB2, 40, a) != null, "A sees B2 and not the old B: ids=" + idsACB2);

            // ---- 10. graceful end ----------------------------------------------------------------------------------------------------
            File.WriteAllText(quitA, "quit");
            File.WriteAllText(quitC, "quit");
            File.WriteAllText(quitB2, "quit");
            bool aExited = WaitForExit(a, 30), cExited = WaitForExit(c, 30), b2Exited = WaitForExit(b2, 30);
            Check(aExited && a.ExitCode == NetExitCodes.Ok, "A left gracefully with exit 0" + (aExited ? " (was " + a.ExitCode + ")" : ""));
            Check(cExited && c.ExitCode == NetExitCodes.Ok, "C (lobby-started client) left gracefully with exit 0" + (cExited ? " (was " + c.ExitCode + ")" : ""));
            Check(b2Exited && b2.ExitCode == NetExitCodes.Ok, "B2 left gracefully with exit 0" + (b2Exited ? " (was " + b2.ExitCode + ")" : ""));
            Check(LogContains(logA, C + "shutdown complete clean=True") && LogContains(logC, C + "shutdown complete clean=True") && LogContains(logB2, C + "shutdown complete clean=True"),
                "A, C and B2 logged a clean shutdown");

            Match lastRemoved = WaitForRegex(serverLog, @"removed client=\d+ id=\d+ slot=fin-\w reason=disconnect players=0 senders=0 registry=0", 30, server);
            Check(lastRemoved != null, "server removed everybody: players=0 senders=0 registry=0");
            Check(CountMatches(serverLog, @"removed client=") == 5 && CountMatches(serverLog, @"admitted client=") == 5, "five admissions (A, B, C, D, B2) and five removals in total");

            File.WriteAllText(quitServer, "quit");
            bool sExited = WaitForExit(server, 30);
            Check(sExited, "server exited after the quit file");
            if (sExited) Check(server.ExitCode == NetExitCodes.Ok, "server exit code 0 (was " + server.ExitCode + ")");
            Check(LogContains(serverLog, S + "shutdown players=0 senders=0 registry=0"), "server logged 'shutdown players=0 senders=0 registry=0'");
            Check(LogContains(serverLog, S + "shutdown complete clean=True"), "server logged a clean shutdown");

            // ---- 11. port reusable ------------------------------------------------------------------------------------------------------
            Check(CanBindUdp(port), "the UDP port " + port + " is free again after the server exited");

            // ---- 12. offline retention (b): the same build without network arguments ---------------------------------------------------------
            string logOffline = Path.Combine(logs, "offline.log");
            Process offline = Launch(exe, logOffline, "");
            Check(offline != null, "offline probe started WITHOUT network arguments");
            if (offline != null)
            {
                Stopwatch watch = Stopwatch.StartNew();
                Check(WaitForLog(logOffline, "[Emberdelve][Lobby] ready", 60, offline), "the offline run reached the lobby (marker '[Emberdelve][Lobby] ready')");
                while (watch.Elapsed.TotalSeconds < 8.0 && !SafeHasExited(offline)) Thread.Sleep(200);
                bool alive = !SafeHasExited(offline);
                string off = ReadShared(logOffline);
                Check(alive, "the offline run was still running after 8 s (did not crash or quit)");
                Check(off.IndexOf("[Emberdelve][Net]", StringComparison.Ordinal) < 0, "the offline log has ZERO '[Emberdelve][Net]' lines");
                Check(off.IndexOf("[Netcode]", StringComparison.Ordinal) < 0, "the offline log has no '[Netcode]' line");
                Check(off.Contains("online=True"), "the lobby offered the Online panel (online=True) without starting anything");
                try
                {
                    if (!offline.HasExited) { offline.Kill(); offline.WaitForExit(5000); }
                }
                catch (Exception e) { Debug.LogWarning(Tag + " stopping the offline probe: " + e.Message); }
                Check(SafeHasExited(offline), "the offline probe was stopped (it never ends by itself)");
            }

            // ---- 13. forbidden patterns -------------------------------------------------------------------------------------------------
            string[] forbidden = { "error CS", "NullReferenceException", "Exception:", "InvalidOperationException", "IndexOutOfRangeException" };
            foreach (string path in Directory.GetFiles(logs, "*.log"))
            {
                string text = ReadShared(path);
                foreach (string pattern in forbidden)
                    Check(text.IndexOf(pattern, StringComparison.Ordinal) < 0, Path.GetFileName(path) + " has no '" + pattern + "'");
            }

            // ---- 14. no process left ------------------------------------------------------------------------------------------------------
            foreach (Process p in Started) Check(SafeHasExited(p), "pid " + p.Id + " is gone");
            return 0;
        }

        // ---- scenario pieces ----------------------------------------------------------------------------------------------------

        /// <summary>Starts a client that must be refused with <paramref name="reason"/> and checks both sides.</summary>
        private static void RefusedClient(string exe, string logs, string serverLog, int port, string name, string slot, string classId, string reason)
        {
            string log = Path.Combine(logs, "client_" + name + ".log");
            Process p = Launch(exe, log, ClientArgs(port, slot, classId, "", "") + " -ember-net-connect-timeout 45");
            Check(p != null, "refused client '" + name + "' started");
            if (p == null) return;

            bool exited = WaitForExit(p, 60);
            Check(exited, "client '" + name + "' exited by itself");
            if (exited) Check(p.ExitCode == NetExitCodes.ConnectFailed, "client '" + name + "' exit code " + NetExitCodes.ConnectFailed + " (was " + p.ExitCode + ")");
            Check(LogContains(log, C + "rejected reason='" + reason + "'"), "client '" + name + "' logged rejected reason='" + reason + "'");
            Check(!LogContains(log, C + "connected id="), "client '" + name + "' never logged 'connected'");
            Check(Regex.IsMatch(ReadShared(serverLog), @"approval id=\d+ approved=False slot=" + Regex.Escape(slot) + @" class=" + Regex.Escape(classId) + @" reason=" + Regex.Escape(reason)),
                "server refused '" + name + "' with '" + reason + "'");
        }

        private static string ClientArgs(int port, string slot, string classId, string quitFileQuoted, string extra)
        {
            string args = "-ember-net client -ember-address 127.0.0.1 -ember-port " + port +
                          " -ember-slot " + slot + " -ember-class " + classId +
                          " -ember-net-connect-timeout 60 -ember-net-disconnect-timeout-ms " + DisconnectTimeoutMs;
            if (!string.IsNullOrEmpty(quitFileQuoted)) args += " -ember-net-quit-file " + quitFileQuoted;
            if (!string.IsNullOrEmpty(extra)) args += " " + extra;
            return args;
        }

        private static string SortedIds(params int[] ids)
        {
            int[] copy = (int[])ids.Clone();
            Array.Sort(copy);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < copy.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(copy[i].ToString(CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        // ---- static checks ----------------------------------------------------------------------------------------------------

        /// <summary>
        /// The documented launch scripts (Tools/net/*.bat) carry commands the real parser accepts: each line that starts the game with
        /// -ember-net is cut at the mode switch, %VARS% are filled from the script's own set lines, and the result goes through
        /// NetLaunchArgs.Parse (valid, expected mode and port). Docs/NETWORK_PLAY.md must show the same switches.
        /// </summary>
        private static void CheckLaunchScripts(string projectRoot)
        {
            string dir = Path.Combine(projectRoot, "Tools", "net");
            string[] files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.bat") : new string[0];
            Check(files.Length >= 3, "Tools/net has the launch script samples (" + files.Length + " .bat files)");

            bool server = false, client = false, host = false;
            foreach (string file in files)
            {
                string[] lines = File.ReadAllLines(file);
                Dictionary<string, string> vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in lines)
                {
                    Match set = Regex.Match(raw.Trim(), "^set\\s+\"?([A-Za-z0-9_]+)=([^\"]*)\"?$", RegexOptions.IgnoreCase);
                    if (set.Success) vars[set.Groups[1].Value] = set.Groups[2].Value;
                }

                foreach (string raw in lines)
                {
                    int at = raw.IndexOf("-ember-net ", StringComparison.Ordinal);
                    if (at < 0 || raw.TrimStart().StartsWith("rem", StringComparison.OrdinalIgnoreCase)) continue;
                    string tail = raw.Substring(at);
                    tail = Regex.Replace(tail, "%([A-Za-z0-9_]+)%", mm => vars.TryGetValue(mm.Groups[1].Value, out string v) ? v : "x");
                    tail = Regex.Replace(tail, "%~?[A-Za-z0-9]+", "x");
                    string[] tokens = Regex.Split(tail.Trim(), "\\s+");
                    NetLaunchArgs parsed = NetLaunchArgs.Parse(tokens);
                    string name = Path.GetFileName(file);
                    Check(parsed.IsValid, name + ": the documented command line parses (" + string.Join("; ", parsed.Errors) + ")");
                    if (parsed.Mode == NetLaunchMode.Server) { server = true; Check(parsed.Port == NetTuning.DefaultPort, name + ": server port is the default " + NetTuning.DefaultPort); }
                    if (parsed.Mode == NetLaunchMode.Client) { client = true; Check(parsed.Address == NetTuning.DefaultAddress, name + ": client targets 127.0.0.1 by default"); }
                    if (parsed.Mode == NetLaunchMode.Host) host = true;
                }
            }

            Check(server && client, "the script samples include a dedicated server and a client launch");
            Check(host, "the script samples include a host launch");

            string doc = Path.Combine(projectRoot, "Docs", "NETWORK_PLAY.md");
            string text = File.Exists(doc) ? File.ReadAllText(doc) : "";
            Check(text.Contains("-ember-net server") && text.Contains("-ember-net client") && text.Contains("-ember-net-max-players") && text.Contains("Join"),
                "Docs/NETWORK_PLAY.md documents the server and client switches and the lobby Join flow");
        }

        /// <summary>
        /// Offline retention (c), without play mode: the saved Level01 and Lobby scene files reference no NetworkObject, NetworkManager,
        /// NetworkBehaviour or Emberdelve.Net script and no instance of the network player prefab. The scanner reads the scene YAML,
        /// resolves every script GUID to its class and tests the type; the same scanner must find network components in the network
        /// player prefab (a control, so a broken scanner cannot pass), and the scene must show non-network Emberdelve scripts (so
        /// an empty read cannot pass).
        /// </summary>
        private static void CheckOfflineScenesHaveNoNetwork()
        {
            Check(NetworkManager.Singleton == null, "NetworkManager.Singleton is null in the Editor (no play mode, no live session)");

            bool prefabOk = EmberdelveNetBuilder.EnsureNetPlayerPrefab(out string prefabError);
            Check(prefabOk, "the network player prefab exists for the scanner control" + (prefabOk ? "" : ": " + prefabError));
            string prefabGuid = AssetDatabase.AssetPathToGUID(EmberdelveNetBuilder.PlayerPrefabPath);
            int controlHits = ScanForNetworkScripts(EmberdelveNetBuilder.PlayerPrefabPath, null, out int controlScripts, out _);
            Check(controlScripts > 5 && controlHits >= 3, "scanner control: the network player prefab references " + controlHits + " network scripts among " + controlScripts + " (NetworkObject, NetworkTransform, NetPlayerRig)");

            foreach (string scenePath in new[] { EmberdelvePaths.DungeonScenePath, EmberdelvePaths.LobbyScenePath })
            {
                int hits = ScanForNetworkScripts(scenePath, prefabGuid, out int scripts, out string offenders);
                string name = Path.GetFileName(scenePath);
                Check(scripts >= 3, name + ": the scan resolved " + scripts + " script references (not vacuous)");
                Check(hits == 0, name + ": zero NetworkObject / NetworkManager / Unity.Netcode / Emberdelve.Net components or net prefab instances" + (hits == 0 ? "" : " (found: " + offenders + ")"));
            }
        }

        private static int ScanForNetworkScripts(string assetPath, string forbiddenPrefabGuid, out int resolvedScripts, out string offenders)
        {
            resolvedScripts = 0;
            offenders = "";
            if (!File.Exists(assetPath)) { offenders = "missing file"; return -1; }
            string text = File.ReadAllText(assetPath);
            if (!text.StartsWith("%YAML", StringComparison.Ordinal)) { offenders = "not text-serialized"; return -1; }

            HashSet<string> seen = new HashSet<string>();
            int hits = 0;
            StringBuilder names = new StringBuilder();
            foreach (Match m in Regex.Matches(text, @"m_Script: \{fileID: 11500000, guid: ([0-9a-f]{32}), type: 3\}"))
            {
                string guid = m.Groups[1].Value;
                if (!seen.Add(guid)) continue;
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                Type type = script != null ? script.GetClass() : null;
                if (type == null) continue;
                resolvedScripts++;
                bool network = typeof(NetworkBehaviour).IsAssignableFrom(type) || type == typeof(NetworkManager) ||
                               (type.Namespace != null && type.Namespace.StartsWith("Unity.Netcode", StringComparison.Ordinal)) ||
                               type.Assembly.GetName().Name == "Emberdelve.Net";
                if (network)
                {
                    hits++;
                    if (names.Length > 0) names.Append(", ");
                    names.Append(type.Name);
                }
            }

            if (!string.IsNullOrEmpty(forbiddenPrefabGuid) && text.Contains("guid: " + forbiddenPrefabGuid))
            {
                hits++;
                if (names.Length > 0) names.Append(", ");
                names.Append("instance of the network player prefab");
            }

            offenders = names.ToString();
            return hits;
        }

        // ---- helpers ----------------------------------------------------------------------------------------------------------

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
                    Arguments = ("-batchmode -nographics -logFile " + Q(logPath) + " " + gameArgs).TrimEnd(),
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

        private static bool CanBindUdp(int port)
        {
            try
            {
                using (UdpClient udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port))) return true;
            }
            catch (Exception) { return false; }
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
