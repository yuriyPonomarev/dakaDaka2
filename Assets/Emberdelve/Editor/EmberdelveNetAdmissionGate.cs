using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Emberdelve.Net.Rules;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// U3A3 acceptance gate: admission, ownership and cleanup over the wire, with real independent processes on loopback.
    ///
    ///   Unity.exe -batchmode -nographics -projectPath . -logFile Logs/net_admission.log
    ///             -executeMethod Emberdelve.EditorTools.EmberdelveNetAdmissionGate.Run
    ///
    /// (Do NOT pass -quit.) Optional: -ember-proof-dir ABS_DIR (default ProjectRoot/Builds/net-admission/yyyyMMdd-HHmmss),
    /// -ember-proof-reuse-exe ABS_EXE (skip the build; the exe must come from a U3A3 build). The Editor is only exited in batch
    /// mode; from a live Editor the call just blocks and returns.
    ///
    /// Scenario (server --ember-net-max-players 2 --ember-net-no-enemies):
    ///   A (warrior) and B (archer, script forge-owner) are admitted with distinct server ids and each client logs two views with
    ///   exactly one owned, whose replicated id equals the server's id for that connection; C is refused "server full"; a duplicate
    ///   slot is refused "duplicate slot"; an unknown class id is refused "unknown class"; B's forged-owner RPCs on A's object are
    ///   rejected and counted while A's state is untouched; B is hard-killed and the server removes its actor within the disconnect
    ///   timeout (counts drop, A logs the view despawn); D is admitted (the cap really freed); A and D quit by file, the server quits
    ///   by file with players=0 senders=0 registry=0 and exit code 0.
    /// Only the PIDs this gate started are ever killed. A kill forced by a timeout is a failure; the one deliberate Process.Kill of
    /// B is the scenario. Final line: [Emberdelve][NetAdmission] PASS|FAIL checks=N failures=M
    /// </summary>
    public static class EmberdelveNetAdmissionGate
    {
        private const string Tag = "[Emberdelve][NetAdmission]";
        private const string S = "[Emberdelve][Net][server] ";
        private const string C = "[Emberdelve][Net][client] ";
        private const int DisconnectTimeoutMs = 4000;
        private const double RemovalDeadlineSeconds = 20.0;

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

        private static int Execute()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string runDir = ArgValue("-ember-proof-dir");
            if (string.IsNullOrEmpty(runDir))
                runDir = Path.Combine(projectRoot, "Builds", "net-admission", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            runDir = Path.GetFullPath(runDir);
            if (runDir.StartsWith(Path.GetFullPath(Application.dataPath), StringComparison.OrdinalIgnoreCase))
            {
                Check(false, "run dir must be outside Assets: " + runDir);
                return 1;
            }

            string logs = Path.Combine(runDir, "logs");
            Directory.CreateDirectory(logs);
            Debug.Log(Tag + " runDir=" + runDir);

            // ---- static: no RPC carries a player / combat id -----------------------------------------
            CheckNoIdentityRpcParameters(projectRoot);

            // ---- 1. player ------------------------------------------------------------------
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

            // ---- 2. server ------------------------------------------------------------------
            int port = FreeUdpPort();
            Check(port > 0, "free udp port " + port);
            if (port <= 0) return 1;

            string quitServer = Path.Combine(runDir, "server.quit");
            string quitA = Path.Combine(runDir, "client_a.quit");
            string quitD = Path.Combine(runDir, "client_d.quit");
            foreach (string f in new[] { quitServer, quitA, quitD }) if (File.Exists(f)) File.Delete(f);

            string serverLog = Path.Combine(logs, "server.log");
            Process server = Launch(exe, serverLog,
                "-ember-net server -ember-port " + port + " -ember-net-quit-file " + Q(quitServer) +
                " -ember-net-autoquit 400 -ember-net-disconnect-timeout-ms " + DisconnectTimeoutMs +
                " -ember-net-max-players 2 -ember-net-no-enemies");
            Check(server != null, "server process started pid=" + (server != null ? server.Id.ToString() : "-"));
            if (server == null) return 1;

            Check(WaitForLog(serverLog, S + "listening", 60, server), "server logged 'listening'");
            Match ready = WaitForRegex(serverLog, @"\[server\] ready scene=\S+ players=(\d+) senders=(\d+) registry=(\d+)", 90, server);
            Check(ready != null, "server loaded the dungeon and logged 'ready'");
            if (ready == null) return 1;
            Check(ready.Groups[1].Value == "0" && ready.Groups[2].Value == "0", "ready with players=0 senders=0");
            Check(ready.Groups[3].Value == "0", "no enemies: registry=0 at ready (was " + ready.Groups[3].Value + ")");
            Check(LogContains(serverLog, "enemies=off"), "server reports enemies=off");

            // ---- 3. A (warrior) --------------------------------------------------------------
            string logA = Path.Combine(logs, "client_a.log");
            Process a = Launch(exe, logA, ClientArgs(port, "adm-a", "warrior", Q(quitA), "") + " -ember-net-autoquit 300");
            Check(a != null, "client A started");
            if (a == null) return 1;

            Match admA = WaitForRegex(serverLog, @"admitted client=(\d+) id=(\d+) party=(\d+) slot=adm-a class=warrior netId=(\d+) players=(\d+) senders=(\d+) registry=(\d+)", 90, server);
            Check(admA != null, "server admitted A (warrior)");
            if (admA == null) return 1;
            string idA = admA.Groups[2].Value;
            string clientIdA = admA.Groups[1].Value;
            Check(admA.Groups[5].Value == "1" && admA.Groups[6].Value == "1" && admA.Groups[7].Value == "1",
                "A admission counts players=1 senders=1 registry=1");
            Check(WaitForLog(logA, C + "connected id=" + clientIdA, 30, a), "client A connected with the server's client id " + clientIdA);
            Check(WaitForRegex(logA, @"\[client\] owner id=" + idA + @" views=1 owned=1 class=warrior ", 30, a) != null,
                "A logs owner id=" + idA + " views=1 (its own server-issued id)");

            // ---- 4. B (archer, forge-owner script) ------------------------------------------------
            string logB = Path.Combine(logs, "client_b.log");
            Process b = Launch(exe, logB, ClientArgs(port, "adm-b", "archer", "", "") + " -ember-net-script forge-owner -ember-net-autoquit 300");
            Check(b != null, "client B started");
            if (b == null) return 1;

            Match admB = WaitForRegex(serverLog, @"admitted client=(\d+) id=(\d+) party=(\d+) slot=adm-b class=archer netId=(\d+) players=(\d+) senders=(\d+) registry=(\d+)", 90, server);
            Check(admB != null, "server admitted B (archer)");
            if (admB == null) return 1;
            string idB = admB.Groups[2].Value;
            string clientIdB = admB.Groups[1].Value;
            Check(idA != idB && idA != "0" && idB != "0", "server issued distinct player ids A=" + idA + " B=" + idB);
            Check(clientIdA != clientIdB, "distinct client ids A=" + clientIdA + " B=" + clientIdB);
            Check(admB.Groups[5].Value == "2" && admB.Groups[6].Value == "2" && admB.Groups[7].Value == "2",
                "B admission counts players=2 senders=2 registry=2 (was " + admB.Groups[5].Value + "/" + admB.Groups[6].Value + "/" + admB.Groups[7].Value + ")");

            string idsAB = SortedIds(idA, idB);
            Check(WaitForRegex(logA, @"\[client\] owner id=" + idA + @" views=2 owned=1 class=warrior party=\d+ ids=" + idsAB, 40, a) != null,
                "A logs owner id=" + idA + " views=2 owned=1, ids=" + idsAB);
            Check(WaitForRegex(logB, @"\[client\] owner id=" + idB + @" views=2 owned=1 class=archer party=\d+ ids=" + idsAB, 40, b) != null,
                "B logs owner id=" + idB + " views=2 owned=1, ids=" + idsAB);
            Check(!Regex.IsMatch(ReadShared(logA), @"owner id=" + idB + @" "), "A never claims B's id as its own");
            Check(!Regex.IsMatch(ReadShared(logB), @"owner id=" + idA + @" "), "B never claims A's id as its own");

            // ---- 5. forged owner -----------------------------------------------------------------
            Check(WaitForLog(logB, C + "script forge-owner sent target=" + idA + " forged=2 self=1", 40, b), "B ran forge-owner against A's object");
            Match rej = WaitForRegex(serverLog, @"REJECT forge-owner count=2 target=" + idA + @" owner=" + clientIdA + @" sender=" + clientIdB +
                                                @" senderPlayer=player#" + idB + @" probe=0", 30, server);
            Check(rej != null, "server rejected and counted both forged calls (count=2, target=A, sender=B, A's probe still 0)");
            Check(LogContains(serverLog, "REJECT forge-owner count=1 target=" + idA), "first rejection logged with count=1");
            Check(!LogContains(serverLog, "REJECT forge-owner count=3"), "no third rejection (exactly the two forged calls)");
            Check(WaitForLog(serverLog, "forge-owner self-call accepted target=" + idB + " sender=" + clientIdB + " probe=5", 30, server),
                "B's call on its own object WAS accepted (the check discriminates)");
            Check(!LogContains(serverLog, "forge-owner self-call accepted target=" + idA), "no forged call was accepted as A's");
            Check(CountMatches(serverLog, @"removed client=") == 0 && !SafeHasExited(a) && !SafeHasExited(b),
                "A and B are still in the run after the forgery (nothing removed)");

            // ---- 6. refusals --------------------------------------------------------------------
            RefusedClient(exe, logs, serverLog, port, "c", "adm-c", "warrior", "server full");
            RefusedClient(exe, logs, serverLog, port, "dup", "adm-a", "warrior", "duplicate slot");
            RefusedClient(exe, logs, serverLog, port, "badclass", "adm-x", "necromancer", "unknown class");
            Check(CountMatches(serverLog, @"approval id=\d+ approved=True") == 2, "exactly two approvals so far (A, B)");
            Check(CountMatches(serverLog, @"approval id=\d+ approved=False") == 3, "exactly three refusals (full, duplicate, class)");
            Check(CountMatches(serverLog, @"admitted client=") == 2 && CountMatches(serverLog, @"removed client=") == 0,
                "refused clients got no actor and removed nobody");

            // ---- 7. B hard-killed ----------------------------------------------------------------
            Stopwatch killWatch = Stopwatch.StartNew();
            try { b.Kill(); b.WaitForExit(5000); } catch (Exception e) { Check(false, "could not kill B: " + e.Message); }
            Match removedB = WaitForRegex(serverLog, @"removed client=" + clientIdB + @" id=" + idB + @" slot=adm-b reason=disconnect players=(\d+) senders=(\d+) registry=(\d+)",
                RemovalDeadlineSeconds + 10, server);
            killWatch.Stop();
            Check(removedB != null, "server removed B's actor after the hard kill");
            if (removedB != null)
            {
                Check(killWatch.Elapsed.TotalSeconds <= RemovalDeadlineSeconds,
                    "removal took " + killWatch.Elapsed.TotalSeconds.ToString("F1") + "s (<= " + RemovalDeadlineSeconds + "s; transport timeout " + DisconnectTimeoutMs + "ms)");
                Check(removedB.Groups[1].Value == "1" && removedB.Groups[2].Value == "1" && removedB.Groups[3].Value == "1",
                    "counts dropped to players=1 senders=1 registry=1 (was " + removedB.Groups[1].Value + "/" + removedB.Groups[2].Value + "/" + removedB.Groups[3].Value + ")");
            }

            Check(WaitForLog(logA, C + "view despawned id=" + idB + " owned=False", 20, a), "A logged 'view despawned id=" + idB + "'");
            Check(WaitForRegex(logA, @"\[client\] owner id=" + idA + @" views=1 owned=1 class=warrior party=\d+ ids=" + idA + @"\s", 20, a) != null,
                "A settles on views=1 after B left");
            Check(!SafeHasExited(a) && !LogContains(logA, "disconnected unexpectedly"), "A stayed connected and running");

            // U3A4b: the transport only drops A after the 4 s disconnect timeout, so "stayed connected" above can pass while the server has
            // already stopped hearing A. A still being ACKED after B's removal proves the server's receive path is alive right now and,
            // if it ever fails, names the real fault (server stopped receiving everybody, see Docs/Tasks/U3A4b.md) instead of the later
            // symptoms (D not admitted, exit codes).
            int ackAtRemoval = MaxPredictAck(logA);
            Check(WaitForAckBeyond(logA, ackAtRemoval + 30, 20, a),
                "A is still acked by the server after B's removal (ack > " + ackAtRemoval + "; a stall here is the server receive path dying, not the gate)");

            // ---- 8. D takes the freed seat -------------------------------------------------------
            string logD = Path.Combine(logs, "client_d.log");
            Process d = Launch(exe, logD, ClientArgs(port, "adm-d", "archer", Q(quitD), "") + " -ember-net-autoquit 300");
            Check(d != null, "client D started");
            if (d == null) return 1;

            Match admD = WaitForRegex(serverLog, @"admitted client=(\d+) id=(\d+) party=(\d+) slot=adm-d class=archer netId=(\d+) players=(\d+) senders=(\d+) registry=(\d+)", 90, server);
            Check(admD != null, "server admitted D after B's seat was freed");
            string idD = "";
            if (admD != null)
            {
                idD = admD.Groups[2].Value;
                Check(idD != idA && idD != idB, "D has a new id (" + idD + "), not A's or the removed B's");
                Check(admD.Groups[5].Value == "2" && admD.Groups[6].Value == "2" && admD.Groups[7].Value == "2", "D admission counts players=2 senders=2 registry=2");
                string idsAD = SortedIds(idA, idD);
                Check(WaitForRegex(logD, @"\[client\] owner id=" + idD + @" views=2 owned=1 class=archer party=\d+ ids=" + idsAD, 40, d) != null,
                    "D logs owner id=" + idD + " views=2 owned=1, ids=" + idsAD);
                Check(WaitForRegex(logA, @"\[client\] owner id=" + idA + @" views=2 owned=1 class=warrior party=\d+ ids=" + idsAD, 40, a) != null,
                    "A sees D: views=2, ids=" + idsAD);
            }

            // ---- 9. graceful end -----------------------------------------------------------------
            File.WriteAllText(quitA, "quit");
            File.WriteAllText(quitD, "quit");
            bool aExited = WaitForExit(a, 30);
            bool dExited = WaitForExit(d, 30);
            Check(aExited && a.ExitCode == NetExitCodes.Ok, "A left by quit file with exit 0" + (aExited ? " (was " + a.ExitCode + ")" : ""));
            Check(dExited && d.ExitCode == NetExitCodes.Ok, "D left by quit file with exit 0" + (dExited ? " (was " + d.ExitCode + ")" : ""));
            Check(LogContains(logA, C + "shutdown complete clean=True") && LogContains(logD, C + "shutdown complete clean=True"),
                "A and D logged a clean shutdown");

            Match lastRemoved = WaitForRegex(serverLog, @"removed client=\d+ id=(?:" + idA + "|" + idD + @") slot=adm-[ad] reason=disconnect players=0 senders=0 registry=0", 30, server);
            Check(lastRemoved != null, "server removed A and D: players=0 senders=0 registry=0");
            Check(CountMatches(serverLog, @"removed client=") == 3, "three removals in total (B, A, D)");

            File.WriteAllText(quitServer, "quit");
            bool sExited = WaitForExit(server, 30);
            Check(sExited, "server exited after the quit file");
            if (sExited) Check(server.ExitCode == NetExitCodes.Ok, "server exit code 0 (was " + server.ExitCode + ")");
            Check(LogContains(serverLog, S + "shutdown players=0 senders=0 registry=0"), "server logged 'shutdown players=0 senders=0 registry=0'");
            Check(LogContains(serverLog, S + "shutdown complete clean=True"), "server logged a clean shutdown");

            // ---- 10. forbidden patterns ------------------------------------------------------
            string[] forbidden = { "error CS", "NullReferenceException", "Exception:", "InvalidOperationException" };
            foreach (string path in Directory.GetFiles(logs, "*.log"))
            {
                string text = ReadShared(path);
                foreach (string pattern in forbidden)
                    Check(text.IndexOf(pattern, StringComparison.Ordinal) < 0, Path.GetFileName(path) + " has no '" + pattern + "'");
            }

            // ---- 11. no process left ---------------------------------------------------------
            foreach (Process p in Started) Check(SafeHasExited(p), "pid " + p.Id + " is gone");
            return 0;
        }

        // ---- scenario pieces -------------------------------------------------------------------

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

        private static string SortedIds(string x, string y)
        {
            int a = int.Parse(x), b = int.Parse(y);
            return a < b ? a + "," + b : b + "," + a;
        }

        /// <summary>
        /// Source scan (plan: "no RPC signature contains a player/combat id parameter"): every [Rpc( attribute under Net/ is
        /// followed by a parameter list that must not name a player, combat or party id. Also non-vacuous: at least one RPC exists.
        /// </summary>
        private static void CheckNoIdentityRpcParameters(string projectRoot)
        {
            string dir = Path.Combine(projectRoot, "Assets", "Emberdelve", "Net");
            int rpcs = 0;
            int bad = 0;
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
                        string parameters = text.Substring(open, end - open + 1);
                        if (Regex.IsMatch(parameters, @"(?i)(player|combat|party|sender|actor)\s*_?id"))
                        {
                            bad++;
                            Debug.LogError(Tag + " RPC with an identity parameter in " + Path.GetFileName(file) + ": " + parameters);
                        }
                    }
                }
            }

            Check(rpcs >= 1, "source scan found the test RPC(s) (" + rpcs + ")");
            Check(bad == 0, "no [Rpc] signature under Net/ carries a player/combat/party id parameter");
        }

        // ---- helpers ---------------------------------------------------------------------------

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

        private static int MaxPredictAck(string path)
        {
            int max = 0;
            foreach (Match m in Regex.Matches(ReadShared(path), @"\[client\] predict pos=\([^)]*\) seq=\d+ ack=(\d+)"))
            {
                int v;
                if (int.TryParse(m.Groups[1].Value, out v) && v > max) max = v;
            }
            return max;
        }

        private static bool WaitForAckBeyond(string path, int ack, double seconds, Process owner)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(seconds);
            while (true)
            {
                if (MaxPredictAck(path) >= ack) return true;
                if (DateTime.UtcNow >= end) return false;
                if (owner != null && SafeHasExited(owner)) return MaxPredictAck(path) >= ack;
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
