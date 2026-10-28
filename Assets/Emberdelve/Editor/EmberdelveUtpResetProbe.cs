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
    /// UTP_RESET probe (Docs/Tasks/UTP_RESET.md): a deterministic repro of "the dedicated server stops receiving from ALL peers after
    /// one client leaves" on the transport level, independent of game logic, and an A/B of the two mitigations.
    ///
    ///   Unity.exe -batchmode -nographics -projectPath . -logFile Logs/utp_probe.log
    ///             -executeMethod Emberdelve.EditorTools.EmberdelveUtpResetProbe.Run
    ///
    /// (Do NOT pass -quit; give the runner -TimeoutSeconds 3600, the default 900 is too short for 2 arms x 8 trials.)
    /// Optional: -ember-proof-dir ABS_DIR, -ember-proof-reuse-exe ABS_EXE (skip the build, the exe must come from a build that has
    /// the UTP_RESET switches), -ember-probe-trials N (default 8), -ember-probe-arms LIST (default "baseline,scan,managed,ws"; names below),
    /// -ember-probe-window SECONDS (default 8).
    ///
    /// Arms (every process of the arm gets the same -ember-net-udp-path and -ember-net-client-linger-ms):
    ///   baseline  stock socket, linger 0                          (the control: the fault as it was)
    ///   scan      stock socket + SIO_UDP_CONNRESET found by handle scan and switched off (the default path of the game), linger 0
    ///   managed   custom INetworkInterface over System.Net.Sockets (needs the EMBER_UTP_MANAGED_SOCKET define; skipped, loudly, without it)
    ///   ws        WebSockets (TCP)
    ///   b         stock socket, linger 250      full  scan + linger 250 (the shipped defaults)      a  alias of scan
    /// Every session logs "session <stem> socket-path: ..." (what the server really ran on) and fails by name when it is not the arm's path.
    ///
    /// One session = a fresh server (max players 3, no enemies, 4000 ms disconnect timeout) + client A that never leaves. Trial n
    /// (1-based) inside a session:
    ///   x-connect : start client X, wait until the server admitted it and X logged "connected".
    ///   x-leave   : odd n = GRACEFUL (quit file, X exits by itself), even n = HARD (Process.Kill). Wait for the server's
    ///               "removed client=.. reason=disconnect"; a graceful leave must be noticed in <= 2500 ms (otherwise the server did
    ///               not hear the disconnect packet).
    ///   a-acked   : for the next window (default 8 s) after the removal A must keep being acked by the server (the ack inside
    ///               A's "[client] predict .. ack=N" lines must advance, also in the last 3 s) and A must not be dropped.
    ///   z-connect : a NEW client Z must be admitted by the same server.
    ///   z-leave   : Z quits gracefully; the server must notice it in <= 2500 ms and A must still be acked for 5 s.
    /// A trial that fails ends the session (the fault is permanent); the next trial starts a fresh session. The first failing
    /// stage is reported. Per trial: "[Emberdelve][UtpProbe] trial arm=.. n=.. kind=hard|graceful result=OK|FAIL stage=.. ...".
    /// Per arm: "arm=.. trials=N ok=K fail=F rate=P%". Then "verdict ..." and the final line
    ///   [Emberdelve][UtpProbe] PASS|FAIL checks=N failures=M
    /// PASS means: infrastructure worked and every trial of every NON-baseline arm passed. The baseline arm is evidence, not a
    /// check: its failures are the repro; "baseline repro=no" means the fault did not show in this run (inconclusive A/B).
    /// Only PIDs this probe started are ever killed.
    /// </summary>
    public static class EmberdelveUtpResetProbe
    {
        private const string Tag = "[Emberdelve][UtpProbe]";
        private const string S = "[Emberdelve][Net][server] ";
        private const string C = "[Emberdelve][Net][client] ";
        private const int DisconnectTimeoutMs = 4000;
        private const int GracefulRemovalLimitMs = 2500;

        private sealed class Arm
        {
            public string Name;
            public string Path = "stock";   // stock|scan|managed|ws
            public int LingerMs;
            public bool IsControl { get { return Path == "stock" && LingerMs == 0; } }
        }

        private sealed class Session
        {
            public Arm Arm;
            public int Index;
            public int Port;
            public Process Server;
            public Process A;
            public string ServerLog;
            public string LogA;
            public string QuitServer;
            public string QuitA;
        }

        private sealed class TrialResult
        {
            public bool Ok = true;
            public string Stage = "-";
            public string Detail = "";
            public long XRemovalMs = -1;
            public long ZRemovalMs = -1;
            public string Acks = "";
            public string ZAcks = "";

            public void Problem(string stage, string detail)
            {
                if (Ok) { Ok = false; Stage = stage; }
                Detail += (Detail.Length > 0 ? "; " : "") + stage + ": " + detail;
            }
        }

        private static int _checks;
        private static int _failures;
        private static int _sessionCounter;
        private static readonly List<Process> Started = new List<Process>();
        private static readonly HashSet<int> Handled = new HashSet<int>();

        public static void Run()
        {
            int exit = 1;
            try { exit = Execute(); }
            catch (Exception e)
            {
                Check(false, "probe threw " + e);
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
                runDir = Path.Combine(projectRoot, "Builds", "utp-probe", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            runDir = Path.GetFullPath(runDir);
            if (runDir.StartsWith(Path.GetFullPath(Application.dataPath), StringComparison.OrdinalIgnoreCase))
            {
                Check(false, "run dir must be outside Assets: " + runDir);
                return 1;
            }

            string logs = Path.Combine(runDir, "logs");
            Directory.CreateDirectory(logs);

            int trials = 8;
            string trialsArg = ArgValue("-ember-probe-trials");
            if (!string.IsNullOrEmpty(trialsArg) && (!int.TryParse(trialsArg, out trials) || trials < 1 || trials > 100))
            {
                Check(false, "-ember-probe-trials must be 1..100: " + trialsArg);
                return 1;
            }

            int windowSeconds = 8;
            string windowArg = ArgValue("-ember-probe-window");
            if (!string.IsNullOrEmpty(windowArg) && (!int.TryParse(windowArg, out windowSeconds) || windowSeconds < 4 || windowSeconds > 60))
            {
                Check(false, "-ember-probe-window must be 4..60: " + windowArg);
                return 1;
            }

            List<Arm> arms = ParseArms(ArgValue("-ember-probe-arms") ?? "baseline,scan,managed,ws");
            Check(arms.Count > 0, "arms parsed: " + (arms.Count > 0 ? string.Join(",", arms.ConvertAll(a => a.Name).ToArray()) : "none"));
            if (arms.Count == 0) return 1;
            Debug.Log(Tag + " runDir=" + runDir + " trials=" + trials + " window=" + windowSeconds + "s platform=" + Application.platform);

            // ---- player ----------------------------------------------------------------------------
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

            // ---- arms ------------------------------------------------------------------------------
            Dictionary<string, int> armFailures = new Dictionary<string, int>();
            Dictionary<string, int> armTrials = new Dictionary<string, int>();
            List<string> skipped = new List<string>();
            foreach (Arm arm in arms)
            {
                if (arm.Path == "managed" && Type.GetType("Emberdelve.Net.Managed.ManagedUdpDriver, Emberdelve.Net.Managed", false) == null)
                {
                    // Not a failure (the assembly is opt-in), but loud: this arm produced NO evidence.
                    Debug.LogWarning(Tag + " arm=" + arm.Name + " SKIPPED: assembly Emberdelve.Net.Managed is not compiled (scripting define EMBER_UTP_MANAGED_SOCKET missing; use the menu Emberdelve/Net/Toggle managed UDP socket define, let Unity recompile, run again)");
                    skipped.Add(arm.Name);
                    continue;
                }

                int ok = 0, fail = 0;
                Session session = null;
                for (int n = 1; n <= trials; n++)
                {
                    if (session == null)
                    {
                        session = StartSession(exe, logs, runDir, arm);
                        if (session == null)
                        {
                            Check(false, "arm " + arm.Name + ": session could not be started; arm aborted at trial " + n);
                            break;
                        }
                    }

                    bool hard = n % 2 == 0;
                    TrialResult r = RunTrial(exe, logs, runDir, session, n, hard, windowSeconds);
                    string kind = hard ? "hard" : "graceful";
                    Debug.Log(Tag + " trial arm=" + arm.Name + " n=" + n + " kind=" + kind + " result=" + (r.Ok ? "OK" : "FAIL") +
                              " stage=" + r.Stage + " xRemovalMs=" + r.XRemovalMs + " zRemovalMs=" + r.ZRemovalMs +
                              " acksAfterX=" + (r.Acks.Length > 0 ? r.Acks : "-") + " acksAfterZ=" + (r.ZAcks.Length > 0 ? r.ZAcks : "-") +
                              " server-rx-quiet=" + CountMatches(session.ServerLog, "rx-quiet") +
                              (r.Detail.Length > 0 ? " detail=[" + r.Detail + "]" : ""));
                    if (r.Ok) ok++; else fail++;
                    if (!arm.IsControl) Check(r.Ok, "arm " + arm.Name + " trial " + n + " (" + kind + ") " + (r.Ok ? "keeps receiving" : "failed at " + r.Stage));

                    if (!r.Ok)
                    {
                        ScanLogs(session, arm, n);
                        StopSession(session, true);
                        session = null;
                    }
                }

                if (session != null)
                {
                    ScanLogs(session, arm, trials);
                    StopSession(session, false);
                }

                armFailures[arm.Name] = fail;
                armTrials[arm.Name] = ok + fail;
                int rate = ok + fail == 0 ? 0 : (100 * fail) / (ok + fail);
                Debug.Log(Tag + " arm=" + arm.Name + " path=" + arm.Path + " lingerMs=" + arm.LingerMs + " trials=" + (ok + fail) + " ok=" + ok + " fail=" + fail + " rate=" + rate + "%");
            }

            // ---- verdict ---------------------------------------------------------------------------
            bool hasBaseline = armFailures.ContainsKey("baseline");
            string repro = !hasBaseline ? "n/a (no baseline arm)" : (armFailures["baseline"] > 0 ? "yes" : "no (fault did not show in this run; A/B inconclusive)");
            string mitigation = "n/a";
            foreach (Arm arm in arms)
            {
                if (arm.IsControl || skipped.Contains(arm.Name)) continue;
                string state = armFailures[arm.Name] == 0 ? "clean" : "still-failing";
                string against = !hasBaseline ? "" : (armFailures["baseline"] == 0 ? "-vs-baseline-inconclusive" : (armFailures[arm.Name] == 0 ? "-effective" : (armFailures[arm.Name] < armFailures["baseline"] ? "-partial" : "-ineffective")));
                mitigation = (mitigation == "n/a" ? "" : mitigation + " ") + arm.Name + "=" + state + against;
            }

            Debug.Log(Tag + " verdict baseline-repro=" + repro + " mitigations: " + mitigation + (skipped.Count > 0 ? " skipped: " + string.Join(",", skipped.ToArray()) : ""));

            // Preference order for the default path: scan (keeps the package's transport), managed, ws (TCP, different latency profile).
            string recommend = "none";
            if (hasBaseline && armFailures["baseline"] > 0)
            {
                foreach (string candidate in new[] { "scan", "managed", "ws", "full", "a" })
                    if (armFailures.ContainsKey(candidate) && armFailures[candidate] == 0 && armTrials[candidate] > 0) { recommend = candidate; break; }
            }

            Debug.Log(Tag + " recommend default-path=" + recommend + (recommend == "none" ? " (no arm was clean while the baseline reproduced the fault)" : ""));
            return 0;
        }

        // ---- session ---------------------------------------------------------------------------

        private static List<Arm> ParseArms(string text)
        {
            List<Arm> result = new List<Arm>();
            foreach (string raw in text.Split(','))
            {
                string name = raw.Trim().ToLowerInvariant();
                switch (name)
                {
                    case "baseline": result.Add(new Arm { Name = name, Path = "stock", LingerMs = 0 }); break;
                    case "a": case "scan": result.Add(new Arm { Name = name, Path = "scan", LingerMs = 0 }); break;
                    case "managed": result.Add(new Arm { Name = name, Path = "managed", LingerMs = 0 }); break;
                    case "ws": result.Add(new Arm { Name = name, Path = "ws", LingerMs = 0 }); break;
                    case "b": result.Add(new Arm { Name = name, Path = "stock", LingerMs = 250 }); break;
                    case "full": result.Add(new Arm { Name = name, Path = "scan", LingerMs = 250 }); break;
                    case "": break;
                    default: Debug.LogError(Tag + " unknown arm '" + name + "' (baseline|scan|managed|ws|a|b|full)"); return new List<Arm>();
                }
            }

            return result;
        }

        private static string ArmArgs(Arm arm)
        {
            return "-ember-net-udp-path " + arm.Path + " -ember-net-client-linger-ms " + arm.LingerMs;
        }

        private static Session StartSession(string exe, string logs, string runDir, Arm arm)
        {
            Session s = new Session { Arm = arm, Index = ++_sessionCounter };
            string stem = arm.Name + "-s" + s.Index;
            s.Port = FreeUdpPort();
            if (s.Port <= 0) { Check(false, "free udp port"); return null; }

            s.QuitServer = Path.Combine(runDir, stem + "-server.quit");
            s.QuitA = Path.Combine(runDir, stem + "-a.quit");
            foreach (string f in new[] { s.QuitServer, s.QuitA }) if (File.Exists(f)) File.Delete(f);

            s.ServerLog = Path.Combine(logs, stem + "-server.log");
            s.Server = Launch(exe, s.ServerLog,
                "-ember-net server -ember-port " + s.Port + " -ember-net-quit-file " + Q(s.QuitServer) +
                " -ember-net-autoquit 3000 -ember-net-disconnect-timeout-ms " + DisconnectTimeoutMs +
                " -ember-net-max-players 3 -ember-net-no-enemies -ember-net-udp-path " + arm.Path);
            if (s.Server == null) { Check(false, stem + ": server started"); return null; }

            bool listening = WaitForLog(s.ServerLog, S + "listening", 60, s.Server);
            Check(listening, stem + ": server logged 'listening'");
            Match ready = listening ? WaitForRegex(s.ServerLog, @"\[server\] ready scene=\S+ players=(\d+) senders=(\d+) registry=(\d+)", 90, s.Server) : null;
            Check(ready != null, stem + ": server loaded the dungeon and logged 'ready'");
            if (ready == null) { StopSession(s, true); return null; }

            // What the server reports about the socket path it really runs on.
            string pathLine = FirstLine(s.ServerLog, S + "socket-path ");
            Debug.Log(Tag + " session " + stem + " socket-path: " + (pathLine.Length > 0 ? pathLine : "NO 'socket-path' line in the server log (player without the UTP_RESET switches?)"));
            bool pathOk = pathLine.IndexOf("effective=" + arm.Path, StringComparison.Ordinal) >= 0;
            Check(pathOk, stem + ": server runs on socket path '" + arm.Path + "' (" + (pathLine.Length > 0 ? pathLine : "no line") + ")");
            if (!pathOk) { StopSession(s, true); return null; }

            if (arm.Path == "scan")
            {
                // The scan runs on a background thread; give it time to finish.
                Match scanned = WaitForRegex(s.ServerLog, @"udp-connreset (disabled sockets=\d+|NOT applied[^\r\n]*)", 20, s.Server);
                string fixLine = scanned != null ? scanned.Value : "no result line within 20 s";
                Check(fixLine.StartsWith("udp-connreset disabled sockets=", StringComparison.Ordinal),
                    stem + ": server applied the UDP connreset fix (" + fixLine + ")");
                string detail = FirstLine(s.ServerLog, S + "udp-connreset detail");
                if (detail.Length > 0) Debug.Log(Tag + " " + stem + ": " + detail);
            }
            else if (arm.Path == "managed")
            {
                string detail = FirstLine(s.ServerLog, S + "socket-path managed detail");
                Debug.Log(Tag + " " + stem + ": " + detail);
                Check(detail.IndexOf("bound=", StringComparison.Ordinal) >= 0, stem + ": managed interface bound a socket (" + detail + ")");
            }

            s.LogA = Path.Combine(logs, stem + "-a.log");
            s.A = Launch(exe, s.LogA, ClientArgs(s.Port, "probe-a", "warrior", Q(s.QuitA), ArmArgs(arm) + " -ember-net-autoquit 3000"));
            if (s.A == null) { Check(false, stem + ": client A started"); StopSession(s, true); return null; }

            Match adm = WaitForRegex(s.ServerLog, @"admitted client=(\d+) id=(\d+) party=\d+ slot=probe-a class=warrior", 90, s.Server);
            Check(adm != null, stem + ": server admitted A");
            if (adm == null) { StopSession(s, true); return null; }
            bool connected = WaitForLog(s.LogA, C + "connected id=" + adm.Groups[1].Value, 30, s.A);
            bool acked = connected && WaitForAckBeyond(s.LogA, 30, 30, s.A);
            Check(acked, stem + ": A connected and is acked (ack>=30)");
            if (!acked) { StopSession(s, true); return null; }
            if (arm.Path == "scan")
            {
                string clientFix = FirstLine(s.LogA, C + "udp-connreset ");
                if (clientFix.Length > 0) Debug.Log(Tag + " " + stem + ": A " + clientFix);
            }

            Debug.Log(Tag + " session " + stem + " ready port=" + s.Port + " serverPid=" + s.Server.Id + " aPid=" + s.A.Id);
            return s;
        }

        /// <summary>Graceful stop (quit files), or a deliberate kill of this session's own processes (a dead session cannot quit).</summary>
        private static void StopSession(Session s, bool force)
        {
            if (!force)
            {
                try { File.WriteAllText(s.QuitA, "quit"); } catch (Exception) { /* ignore */ }
                WaitForExitQuiet(s.A, 20);
                try { File.WriteAllText(s.QuitServer, "quit"); } catch (Exception) { /* ignore */ }
                WaitForExitQuiet(s.Server, 30);
            }

            KillOwn(s.A);
            KillOwn(s.Server);
        }

        // ---- one trial -------------------------------------------------------------------------

        private static TrialResult RunTrial(string exe, string logs, string runDir, Session s, int n, bool hard, int windowSeconds)
        {
            TrialResult r = new TrialResult();
            string stem = s.Arm.Name + "-s" + s.Index + "-t" + n;
            string slotX = "px" + s.Index + "t" + n;
            string logX = Path.Combine(logs, stem + "-x.log");
            string quitX = Path.Combine(runDir, stem + "-x.quit");
            if (File.Exists(quitX)) File.Delete(quitX);

            // ---- x-connect
            Process x = Launch(exe, logX, ClientArgs(s.Port, slotX, "archer", Q(quitX), ArmArgs(s.Arm) + " -ember-net-autoquit 600"));
            if (x == null) { r.Problem("x-connect", "launch failed"); return r; }
            Match admX = WaitForRegex(s.ServerLog, @"admitted client=(\d+) id=(\d+) party=\d+ slot=" + slotX + @" class=archer", 60, s.Server);
            if (admX == null) { r.Problem("x-connect", "server never admitted X"); KillOwn(x); return r; }
            if (!WaitForLog(logX, C + "connected id=" + admX.Groups[1].Value, 30, x)) { r.Problem("x-connect", "X never logged connected"); KillOwn(x); return r; }
            Thread.Sleep(1500);

            // ---- x-leave
            string removedX = @"removed client=" + admX.Groups[1].Value + @" id=" + admX.Groups[2].Value + @" slot=" + slotX + @" reason=disconnect";
            Stopwatch sw = Stopwatch.StartNew();
            if (hard)
            {
                try { x.Kill(); x.WaitForExit(5000); } catch (Exception e) { r.Problem("x-leave", "kill failed " + e.Message); }
                Handled.Add(x.Id);
            }
            else
            {
                File.WriteAllText(quitX, "quit");
                bool exited = WaitForExitQuiet(x, 20);
                if (!exited) r.Problem("x-leave", "X did not exit after the quit file");
                else if (x.ExitCode != NetExitCodes.Ok) r.Problem("x-leave", "X exit code " + x.ExitCode);
            }

            Match gone = WaitForRegex(s.ServerLog, removedX, 25, s.Server);
            r.XRemovalMs = gone != null ? sw.ElapsedMilliseconds : -1;
            if (gone == null) r.Problem("x-removal", "server never removed X within 25 s");
            else if (!hard && r.XRemovalMs > GracefulRemovalLimitMs)
                r.Problem("x-removal-late", "graceful leave noticed after " + r.XRemovalMs + " ms (the disconnect packet never reached the server; it was timed out)");

            // ---- a-acked (the check that matters)
            string acks;
            string aProblem = AckWindow(s, windowSeconds, windowSeconds * 12, 20, out acks);
            r.Acks = acks;
            if (aProblem.Length > 0) r.Problem("a-acked", aProblem);

            // ---- z-connect
            string slotZ = "pz" + s.Index + "t" + n;
            string logZ = Path.Combine(logs, stem + "-z.log");
            string quitZ = Path.Combine(runDir, stem + "-z.quit");
            if (File.Exists(quitZ)) File.Delete(quitZ);
            Process z = Launch(exe, logZ, ClientArgs(s.Port, slotZ, "archer", Q(quitZ), ArmArgs(s.Arm) + " -ember-net-autoquit 600 -ember-net-connect-timeout 30"));
            if (z == null) { r.Problem("z-connect", "launch failed"); return r; }
            Match admZ = WaitForRegex(s.ServerLog, @"admitted client=(\d+) id=(\d+) party=\d+ slot=" + slotZ + @" class=archer", 45, s.Server);
            if (admZ == null)
            {
                r.Problem("z-connect", "a NEW client was never admitted (server receives nothing from new peers)");
                WaitForExitQuiet(z, 40);
                KillOwn(z);
                return r;
            }

            if (!WaitForLog(logZ, C + "connected id=" + admZ.Groups[1].Value, 20, z)) r.Problem("z-connect", "Z admitted but never logged connected");
            Thread.Sleep(1000);

            // ---- z-leave (always graceful)
            string removedZ = @"removed client=" + admZ.Groups[1].Value + @" id=" + admZ.Groups[2].Value + @" slot=" + slotZ + @" reason=disconnect";
            sw.Restart();
            File.WriteAllText(quitZ, "quit");
            if (!WaitForExitQuiet(z, 20)) r.Problem("z-leave", "Z did not exit after the quit file");
            Match goneZ = WaitForRegex(s.ServerLog, removedZ, 25, s.Server);
            r.ZRemovalMs = goneZ != null ? sw.ElapsedMilliseconds : -1;
            if (goneZ == null) r.Problem("z-removal", "server never removed Z within 25 s");
            else if (r.ZRemovalMs > GracefulRemovalLimitMs)
                r.Problem("z-removal-late", "graceful leave noticed after " + r.ZRemovalMs + " ms");

            string zAcks;
            string zProblem = AckWindow(s, 5, 40, 10, out zAcks);
            r.ZAcks = zAcks;
            if (zProblem.Length > 0) r.Problem("a-acked-after-z", zProblem);
            KillOwn(x);
            KillOwn(z);
            return r;
        }

        /// <summary>
        /// A must be acked by the server for the whole window: the highest ack seen in A's predict lines must rise by
        /// <paramref name="minProgress"/> over the window and by <paramref name="tailProgress"/> over its last 3 s, and A must not exit.
        /// (ack is the last input sequence the server processed, about 30 per second; A's log line is written every 2 s.)
        /// Returns "" when fine, else the reason.
        /// </summary>
        private static string AckWindow(Session s, int seconds, int minProgress, int tailProgress, out string acks)
        {
            int start = MaxPredictAck(s.LogA);
            int atTail = start;
            bool tailTaken = false;
            Stopwatch sw = Stopwatch.StartNew();
            string problem = "";
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                if (SafeHasExited(s.A)) { problem = "A exited (code " + SafeExitCode(s.A) + ") during the window"; break; }
                if (!tailTaken && sw.Elapsed.TotalSeconds >= seconds - 3) { atTail = MaxPredictAck(s.LogA); tailTaken = true; }
                Thread.Sleep(200);
            }

            int end = MaxPredictAck(s.LogA);
            acks = start + "->" + end;
            if (problem.Length > 0) return problem;
            if (end - start < minProgress) return "A's ack rose only " + (end - start) + " in " + seconds + " s (need " + minProgress + ")";
            if (end - atTail < tailProgress) return "A's ack stalled in the last 3 s of the window (+" + (end - atTail) + ", need " + tailProgress + ")";
            if (LogContains(s.LogA, "disconnected unexpectedly")) return "A logged 'disconnected unexpectedly'";
            return "";
        }

        private static void ScanLogs(Session s, Arm arm, int n)
        {
            try
            {
                Regex pattern = new Regex(@"(?im)^.*(?:exception|\berror\b|transport failure|connreset|wsaecon).*$");
                int shown = 0;
                foreach (string path in new[] { s.ServerLog, s.LogA })
                {
                    foreach (Match m in pattern.Matches(ReadShared(path)))
                    {
                        string text = m.Value.Trim();
                        if (text.StartsWith("UnityEngine.", StringComparison.Ordinal) || text.StartsWith("Unity.", StringComparison.Ordinal)) continue;
                        if (text.IndexOf("udp-connreset", StringComparison.Ordinal) >= 0) continue;
                        if (shown++ >= 4) return;
                        Debug.Log(Tag + " logscan arm=" + arm.Name + " after-trial=" + n + " " + Path.GetFileName(path) + ": " + (text.Length > 200 ? text.Substring(0, 200) : text));
                    }
                }
            }
            catch (Exception) { /* diagnostics only */ }
        }

        // ---- helpers (the same patterns as EmberdelveNetAdmissionGate; kept private there) -------

        private static void Check(bool ok, string what)
        {
            _checks++;
            if (!ok) _failures++;
            if (ok) Debug.Log(Tag + " ok: " + what); else Debug.LogError(Tag + " FAIL: " + what);
        }

        private static string Q(string path) { return "\"" + path + "\""; }

        private static string ClientArgs(int port, string slot, string classId, string quitFileQuoted, string extra)
        {
            string args = "-ember-net client -ember-address 127.0.0.1 -ember-port " + port +
                          " -ember-slot " + slot + " -ember-class " + classId +
                          " -ember-net-connect-timeout 60 -ember-net-disconnect-timeout-ms " + DisconnectTimeoutMs;
            if (!string.IsNullOrEmpty(quitFileQuoted)) args += " -ember-net-quit-file " + quitFileQuoted;
            if (!string.IsNullOrEmpty(extra)) args += " " + extra;
            return args;
        }

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

        private static int CountMatches(string path, string needle)
        {
            string text = ReadShared(path);
            int count = 0;
            int at = 0;
            while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
            return count;
        }

        private static string FirstLine(string path, string needle)
        {
            string text = ReadShared(path);
            int at = text.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0) return "";
            int end = text.IndexOf('\n', at);
            string line = end < 0 ? text.Substring(at) : text.Substring(at, end - at);
            return line.Trim();
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

        private static int SafeExitCode(Process p)
        {
            try { return p != null && p.HasExited ? p.ExitCode : -1; }
            catch (Exception) { return -1; }
        }

        /// <summary>Waits for an exit; on timeout it only returns false (the caller decides whether a kill is the scenario).</summary>
        private static bool WaitForExitQuiet(Process p, double seconds)
        {
            try { return p == null || p.WaitForExit((int)(seconds * 1000)); }
            catch (Exception) { return true; }
        }

        /// <summary>Deliberate kill of one of this probe's own processes (a dead session, a stuck client). Not counted as a failure.</summary>
        private static void KillOwn(Process p)
        {
            if (p == null) return;
            try
            {
                Handled.Add(p.Id);
                if (!p.HasExited)
                {
                    p.Kill();
                    p.WaitForExit(5000);
                }
            }
            catch (Exception) { /* already gone */ }
        }

        private static void KillSurvivors()
        {
            foreach (Process p in Started)
            {
                try
                {
                    if (p.HasExited) continue;
                    Debug.LogError(Tag + " cleanup: killing own pid " + p.Id);
                    if (!Handled.Contains(p.Id)) { _failures++; _checks++; }
                    p.Kill();
                    p.WaitForExit(5000);
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
