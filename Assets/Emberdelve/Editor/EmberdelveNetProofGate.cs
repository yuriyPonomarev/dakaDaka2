using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Emberdelve.Net.Rules;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// U3A1 acceptance gate: real independent processes over loopback.
    ///
    ///   Unity.exe -batchmode -nographics -projectPath . -logFile Logs/net_proof.log
    ///             -executeMethod Emberdelve.EditorTools.EmberdelveNetProofGate.Run
    ///
    /// (Do NOT pass -quit: the gate exits the Editor itself.) Optional: -ember-proof-dir ABS_DIR (default
    /// ProjectRoot/Builds/net-proof/yyyyMMdd-HHmmss), -ember-proof-reuse-exe ABS_EXE (skip the build).
    ///
    /// Flow: build a Development Windows player from the NetBoot scene; start a headless server; start a
    /// client that connects, then quits itself; start a client announcing a wrong protocol and expect the
    /// server to refuse it; ask the server to quit through a file; check handshake lines, exit codes and
    /// forbidden patterns in every child log. Only the PIDs this gate started are ever killed, and a kill
    /// forced by a timeout is a failure.
    /// Final line: [Emberdelve][NetProof] PASS|FAIL checks=N failures=M
    /// </summary>
    public static class EmberdelveNetProofGate
    {
        private const string Tag = "[Emberdelve][NetProof]";
        private const string ServerPrefix = "[Emberdelve][Net][server] ";
        private const string ClientPrefix = "[Emberdelve][Net][client] ";

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
            // U3A3: the server now loads the dungeon scene and clients synchronise it before "connected", hence the longer
            // connect windows below (client A, the refused client). The checks themselves are unchanged.
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string runDir = ArgValue("-ember-proof-dir");
            if (string.IsNullOrEmpty(runDir))
                runDir = Path.Combine(projectRoot, "Builds", "net-proof", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            runDir = Path.GetFullPath(runDir);
            string assets = Path.GetFullPath(Application.dataPath);
            if (runDir.StartsWith(assets, StringComparison.OrdinalIgnoreCase))
            {
                Check(false, "run dir must be outside Assets: " + runDir);
                return 1;
            }

            string logs = Path.Combine(runDir, "logs");
            Directory.CreateDirectory(logs);
            Debug.Log(Tag + " runDir=" + runDir);

            // ---- 1. player ------------------------------------------------------------------
            string exe = ArgValue("-ember-proof-reuse-exe");
            if (string.IsNullOrEmpty(exe))
            {
                exe = Path.Combine(runDir, "player", "EmberdelveNet.exe");
                exe = EmberdelvePlayerBuildCache.TestPlayerPath(exe, "net-proof");
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

            string quitFile = Path.Combine(runDir, "server.quit");
            if (File.Exists(quitFile)) File.Delete(quitFile);
            string serverLog = Path.Combine(logs, "server.log");
            Process server = Launch(exe, serverLog,
                "-ember-net server -ember-port " + port + " -ember-net-quit-file " + Q(quitFile) +
                " -ember-net-autoquit 120 -ember-net-disconnect-timeout-ms 4000 -ember-net-max-players 4");
            Check(server != null, "server process started pid=" + (server != null ? server.Id.ToString() : "-"));
            if (server == null) return 1;

            bool listening = WaitForLog(serverLog, ServerPrefix + "listening", 60, server);
            Check(listening, "server logged 'listening'");
            if (!listening) return 1;

            // ---- 3. accepted client ---------------------------------------------------------
            string clientLog = Path.Combine(logs, "client_a.log");
            Process clientA = Launch(exe, clientLog,
                "-ember-net client -ember-address 127.0.0.1 -ember-port " + port +
                " -ember-slot proof-a -ember-class Warrior -ember-net-autoquit 3 -ember-net-connect-timeout 40" +
                " -ember-net-disconnect-timeout-ms 4000");
            Check(clientA != null, "client A process started pid=" + (clientA != null ? clientA.Id.ToString() : "-"));
            if (clientA == null) return 1;

            Check(WaitForLog(clientLog, ClientPrefix + "connected id=", 60, clientA), "client A logged 'connected id='");
            Check(WaitForLog(serverLog, ServerPrefix + "client connected id=", 10, server), "server logged 'client connected'");
            Check(LogContains(serverLog, "approval id=") && LogContains(serverLog, "approved=True"),
                "server approved client A (protocol " + NetTuning.ProtocolVersion + ")");

            bool aExited = WaitForExit(clientA, 40);
            Check(aExited, "client A quit by itself (autoquit)");
            if (aExited) Check(clientA.ExitCode == NetExitCodes.Ok, "client A exit code 0 (was " + clientA.ExitCode + ")");
            Check(WaitForLog(clientLog, ClientPrefix + "shutdown complete clean=True", 5, null), "client A logged clean shutdown");
            Check(WaitForLog(serverLog, ServerPrefix + "client disconnected id=", 15, server), "server logged 'client disconnected'");

            // ---- 4. refused client (wrong protocol) -----------------------------------------
            string badLog = Path.Combine(logs, "client_bad.log");
            Process clientBad = Launch(exe, badLog,
                "-ember-net client -ember-address 127.0.0.1 -ember-port " + port +
                " -ember-slot proof-bad -ember-net-protocol-override 999 -ember-net-connect-timeout 40" +
                " -ember-net-disconnect-timeout-ms 4000");
            Check(clientBad != null, "client B (wrong protocol) started");
            if (clientBad != null)
            {
                bool badExited = WaitForExit(clientBad, 40);
                Check(badExited, "refused client exited by itself");
                if (badExited) Check(clientBad.ExitCode == NetExitCodes.ConnectFailed,
                    "refused client exit code " + NetExitCodes.ConnectFailed + " (was " + clientBad.ExitCode + ")");
                Check(LogContains(serverLog, "approved=False") && LogContains(serverLog, "protocol mismatch"),
                    "server refused client B with 'protocol mismatch'");
                Check(!LogContains(badLog, ClientPrefix + "connected id="), "refused client never logged 'connected'");
            }

            // ---- 5. graceful server end -----------------------------------------------------
            File.WriteAllText(quitFile, "quit");
            bool sExited = WaitForExit(server, 30);
            Check(sExited, "server exited after quit file");
            if (sExited) Check(server.ExitCode == NetExitCodes.Ok, "server exit code 0 (was " + server.ExitCode + ")");
            Check(LogContains(serverLog, ServerPrefix + "shutdown complete clean=True"), "server logged clean shutdown");

            // ---- 6. forbidden patterns ------------------------------------------------------
            string[] forbidden = { "error CS", "NullReferenceException", "Exception:", "InvalidOperationException" };
            foreach (string path in new[] { serverLog, clientLog, badLog })
            {
                string text = ReadShared(path);
                foreach (string pattern in forbidden)
                    Check(text.IndexOf(pattern, StringComparison.Ordinal) < 0,
                        Path.GetFileName(path) + " has no '" + pattern + "'");
            }

            // ---- 7. no process left ---------------------------------------------------------
            foreach (Process p in Started)
                Check(SafeHasExited(p), "pid " + p.Id + " is gone");

            return 0;
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

        /// <summary>Polls a log file for a substring. Aborts early when the owning process has exited and the text is absent.</summary>
        private static bool WaitForLog(string path, string needle, double seconds, Process owner)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(seconds);
            while (true)
            {
                if (LogContains(path, needle)) return true;
                if (DateTime.UtcNow >= end) return false;
                if (owner != null && SafeHasExited(owner))
                    return LogContains(path, needle);
                Thread.Sleep(200);
            }
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

        /// <summary>Waits for a graceful exit. On timeout kills only this process (counted as a failure by the caller).</summary>
        private static bool WaitForExit(Process p, double seconds)
        {
            try
            {
                if (p.WaitForExit((int)(seconds * 1000))) return true;
                Debug.LogError(Tag + " pid " + p.Id + " did not exit within " + seconds + "s; killing it");
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
