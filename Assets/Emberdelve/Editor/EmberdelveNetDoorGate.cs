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
    /// Development-only three-client wire proof for modular doorway authority and late-join state.
    /// Entry point: -executeMethod Emberdelve.EditorTools.EmberdelveNetDoorGate.Run.
    /// </summary>
    public static class EmberdelveNetDoorGate
    {
        private const string Tag = "[Emberdelve][NetDoor]";
        private const int TimeoutSeconds = 90;
        private static readonly List<Process> Started = new List<Process>();
        private static int _checks;
        private static int _failures;

        public static void Run()
        {
            int exit = 1;
            try
            {
                if (!Application.isBatchMode) Check(false, "must run in batch mode because this gate starts real network processes");
                else exit = Execute();
            }
            catch (Exception e) { Check(false, "gate threw " + e); exit = 1; }
            finally { Cleanup(); }
            string verdict = _failures == 0 && exit == 0 ? "PASS" : "FAIL";
            string line = Tag + " " + verdict + " checks=" + _checks + " failures=" + _failures;
            if (_failures == 0) Debug.Log(line); else Debug.LogError(line);
            if (Application.isBatchMode) EmberdelveGateExit.Exit(_failures == 0 && exit == 0 ? 0 : 1);
        }

        private static int Execute()
        {
            string root = Directory.GetParent(Application.dataPath).FullName;
            string runDir = ArgValue("-ember-proof-dir");
            if (string.IsNullOrEmpty(runDir)) runDir = Path.Combine(root, "Builds", "net-door", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            runDir = Path.GetFullPath(runDir);
            if (runDir.StartsWith(Path.GetFullPath(Application.dataPath), StringComparison.OrdinalIgnoreCase))
            { Check(false, "proof directory must be outside Assets"); return 1; }
            string logs = Path.Combine(runDir, "logs");
            Directory.CreateDirectory(logs);
            string exe = ArgValue("-ember-proof-reuse-exe");
            if (string.IsNullOrEmpty(exe))
            {
                exe = Path.Combine(runDir, "player", "EmberdelveNet.exe");
                exe = EmberdelvePlayerBuildCache.TestPlayerPath(exe, "net-proof");
                bool built = EmberdelveWindowsBuild.BuildNetProofPlayer(exe, out string error);
                Check(built, "development proof player built" + (built ? "" : ": " + error));
                if (!built) return 1;
            }
            else Check(File.Exists(exe), "reused proof player exists");
            if (!File.Exists(exe)) return 1;

            int port = FreeUdpPort();
            Check(port > 0, "loopback UDP port allocated");
            if (port <= 0) return 1;
            string serverLog = Path.Combine(logs, "server.log");
            string aLog = Path.Combine(logs, "client_a.log");
            string bLog = Path.Combine(logs, "client_b.log");
            string lateLog = Path.Combine(logs, "client_late.log");
            string serverQuit = Path.Combine(runDir, "server.quit");
            string aQuit = Path.Combine(runDir, "a.quit");
            string bQuit = Path.Combine(runDir, "b.quit");
            string lateQuit = Path.Combine(runDir, "late.quit");
            string serverStage = Path.Combine(runDir, "server-stage.txt");
            string aCommand = Path.Combine(runDir, "a-command.txt");
            string bCommand = Path.Combine(runDir, "b-command.txt");
            string lateCommand = Path.Combine(runDir, "late-command.txt");
            foreach (string path in new[] { serverQuit, aQuit, bQuit, lateQuit, serverStage, aCommand, bCommand, lateCommand,
                serverStage + ".tmp", aCommand + ".tmp", bCommand + ".tmp", lateCommand + ".tmp" })
                if (File.Exists(path)) File.Delete(path);
            foreach (string path in new[] { serverLog, aLog, bLog, lateLog })
                if (File.Exists(path)) File.Delete(path);

            Process server = Launch(exe, serverLog, "-ember-net server -ember-port " + port +
                " -ember-net-max-players 4 -ember-net-party-size 1 -ember-net-no-enemies -ember-net-door-proof-server" +
                " -ember-net-door-proof-server-action-file " + Q(serverStage) + " -ember-net-quit-file " + Q(serverQuit));
            Check(server != null, "dedicated server started");
            if (server == null || !WaitFor(serverLog, @"\[server\] ready scene=", TimeoutSeconds, server))
            { Check(false, "server reached ready state"); return 1; }
            Match applied = WaitMatch(serverLog, @"\[modular-proof\] scene-applied hash=[^\s]+ modules=9 doors=(?<doors>\d+) closuresConsistent=true", TimeoutSeconds, server);
            Check(applied != null && int.Parse(applied.Groups["doors"].Value) > 0,
                "server proof marker reports the actual nonzero spawned doorway count");
            if (applied == null) return 1;

            Process a = Launch(exe, aLog, ClientArgs(port, "door-a", "a", aCommand, aQuit));
            Check(a != null, "door client A started");
            if (a == null) return 1;
            Match admittedA = WaitMatch(serverLog, @"admitted client=(?<client>\d+) id=(?<id>\d+) party=\d+ slot=door-a class=warrior netId=\d+", TimeoutSeconds, server);
            if (admittedA == null) { Check(false, "server admitted door A"); return 1; }

            Process b = Launch(exe, bLog, ClientArgs(port, "door-b", "b", bCommand, bQuit));
            Check(b != null, "door client B started");
            if (b == null) return 1;
            Match admittedB = WaitMatch(serverLog, @"admitted client=(?<client>\d+) id=(?<id>\d+) party=\d+ slot=door-b class=warrior netId=\d+", TimeoutSeconds, server);
            if (admittedB == null) { Check(false, "server admitted door B"); return 1; }
            Check(admittedA.Groups["client"].Value != admittedB.Groups["client"].Value, "A and B have distinct authenticated transport senders");

            Match fixture = WaitMatch(serverLog,
                @"door-proof fixture id=(?<door>door_[A-Za-z0-9_-]+) clientA=" + admittedA.Groups["client"].Value +
                @" playerA=" + admittedA.Groups["id"].Value + @" clientB=" + admittedB.Groups["client"].Value +
                @" playerB=" + admittedB.Groups["id"].Value + @" canA=True canB=True open=False blocker=True revision=(?<revision>\d+)", TimeoutSeconds, server);
            Check(fixture != null, "server staged both admitted actors at the same real closed door");
            if (fixture == null) return 1;
            string doorId = fixture.Groups["door"].Value;
            string revision = fixture.Groups["revision"].Value;

            WriteAtomic(aCommand, "observe|" + doorId + "|0");
            WriteAtomic(bCommand, "observe|" + doorId + "|0");
            string initialPattern = @"door-proof state role=(?<role>a|b) source=observe id=" + doorId +
                @" open=False revision=" + revision + @" ready=True replicaOpen=False blocker=True";
            Check(WaitFor(aLog, initialPattern.Replace("role=(?<role>a|b)", "role=a"), TimeoutSeconds, a), "client A received the authoritative initial closed snapshot and blocker");
            Check(WaitFor(bLog, initialPattern.Replace("role=(?<role>a|b)", "role=b"), TimeoutSeconds, b), "client B received the authoritative initial closed snapshot and blocker");

            WriteAtomic(aCommand, "toggle|" + doorId + "|1");
            Match opened = WaitMatch(serverLog, @"door-proof request sender=" + admittedA.Groups["client"].Value + @" id=" + doorId +
                @" accepted=True reason=- open=True revision=(?<revision>\d+) blocker=False", TimeoutSeconds, server);
            Check(opened != null, "real A client RPC opened the door through server authority and disabled the blocker");
            if (opened == null) return 1;
            revision = opened.Groups["revision"].Value;
            Check(uint.Parse(revision) > uint.Parse(fixture.Groups["revision"].Value), "opening advanced the authoritative door revision");
            string openPattern = @"door-proof state role=(?<role>a|b) source=replica id=" + doorId + @" open=True revision=" + revision +
                @" ready=True replicaOpen=True blocker=False";
            Check(WaitFor(aLog, openPattern.Replace("role=(?<role>a|b)", "role=a"), TimeoutSeconds, a), "client A observed the same open state, revision and disabled collider");
            Check(WaitFor(bLog, openPattern.Replace("role=(?<role>a|b)", "role=b"), TimeoutSeconds, b), "client B observed A's open state, revision and disabled collider");

            WriteAtomic(bCommand, "toggle|" + doorId + "|2");
            Match closed = WaitMatch(serverLog, @"door-proof request sender=" + admittedB.Groups["client"].Value + @" id=" + doorId +
                @" accepted=True reason=- open=False revision=(?<revision>\d+) blocker=True", TimeoutSeconds, server);
            Check(closed != null, "real B client RPC closed the door through server authority and enabled the blocker");
            if (closed == null) return 1;
            Check(uint.Parse(closed.Groups["revision"].Value) == uint.Parse(revision) + 1u, "closing advanced the authoritative door revision exactly once");
            revision = closed.Groups["revision"].Value;
            string closedPattern = @"door-proof state role=(?<role>a|b) source=replica id=" + doorId + @" open=False revision=" + revision +
                @" ready=True replicaOpen=False blocker=True";
            Check(WaitFor(aLog, closedPattern.Replace("role=(?<role>a|b)", "role=a"), TimeoutSeconds, a), "client A observed the same closed state and enabled collider");
            Check(WaitFor(bLog, closedPattern.Replace("role=(?<role>a|b)", "role=b"), TimeoutSeconds, b), "client B observed the same closed state and enabled collider");

            WriteAtomic(serverStage, "far");
            Check(WaitFor(serverLog, @"door-proof staged stage=far id=" + doorId + @" canInteract=False reason=Too_far_away open=False revision=" + revision, TimeoutSeconds, server), "server staged an out-of-reach actor");
            // Door requests are rate-limited per sender to 300 ms. Give the fixture time
            // to leave the previous toggle's window so this proves the distance rule.
            Thread.Sleep(350);
            WriteAtomic(aCommand, "toggle|" + doorId + "|3");
            Check(WaitFor(serverLog, @"door-proof request sender=" + admittedA.Groups["client"].Value + @" id=" + doorId +
                @" accepted=False reason=Too_far_away open=False revision=" + revision + @" blocker=True", TimeoutSeconds, server), "server rejected the real far-away client request without changing state");

            WriteAtomic(serverStage, "los");
            Match losStage = WaitMatch(serverLog, @"door-proof staged stage=los id=" + doorId + @" canInteract=False reason=No_line_of_sight open=False revision=" + revision +
                @" rayDistance=(?<distance>[\d.]+)", TimeoutSeconds, server);
            Check(losStage != null && double.Parse(losStage.Groups["distance"].Value, System.Globalization.CultureInfo.InvariantCulture) > 1.0,
                "server staged a level-geometry occluder across a sight ray longer than one meter");
            if (losStage == null) return 1;
            // Preserve the production throttle while ensuring the next rejection tests LOS.
            Thread.Sleep(350);
            WriteAtomic(aCommand, "toggle|" + doorId + "|4");
            Check(WaitFor(serverLog, @"door-proof request sender=" + admittedA.Groups["client"].Value + @" id=" + doorId +
                @" accepted=False reason=No_line_of_sight open=False revision=" + revision + @" blocker=True", TimeoutSeconds, server), "server rejected the real occluded client request without changing state");
            WriteAtomic(serverStage, "near");

            Process late = Launch(exe, lateLog, ClientArgs(port, "door-late", "late", lateCommand, lateQuit));
            Check(late != null, "late-joining door client started");
            if (late == null) return 1;
            Match admittedLate = WaitMatch(serverLog, @"admitted client=(?<client>\d+) id=(?<id>\d+) party=\d+ slot=door-late class=warrior netId=\d+", TimeoutSeconds, late);
            Check(admittedLate != null, "server admitted the late observer after the door state changed");
            if (admittedLate == null) return 1;
            Check(WaitFor(serverLog, @"door-proof snapshot client=" + admittedLate.Groups["client"].Value + @" doors=\d+ revision=" + revision, TimeoutSeconds, server), "server sent the revisioned late-join door snapshot");
            WriteAtomic(lateCommand, "observe|" + doorId + "|5");
            Check(WaitFor(lateLog, @"door-proof snapshot role=late phase=end revision=" + revision, TimeoutSeconds, late), "late client completed the door snapshot");
            Check(WaitFor(lateLog, @"door-proof state role=late source=observe id=" + doorId + @" open=False revision=" + revision +
                @" ready=True replicaOpen=False blocker=True", TimeoutSeconds, late), "late client reconstructed the same closed state and blocker");

            WriteAtomic(serverStage, "ai");
            Match aiStarted = WaitMatch(serverLog, @"door-proof enemy-ai started id=" + doorId +
                @" enemy=.+ targetCombatId=(?<target>\d+) closed=True revision=" + revision, 20, server);
            Check(aiStarted != null, "server staged a real authored EnemyAgent and live target across the closed door");
            if (aiStarted == null) return 1;
            Match aiOpened = WaitMatch(serverLog, @"door-proof enemy-ai opened accepted=True targetMatched=True targetCombatId=" +
                aiStarted.Groups["target"].Value + @" moved=(?<moved>[\d.]+) revision=(?<revision>\d+) blocker=False", 20, server);
            Check(aiOpened != null, "the actual server EnemyAgent approached, opened the door and left its blocker disabled");
            if (aiOpened == null) return 1;
            float aiMoved = float.Parse(aiOpened.Groups["moved"].Value, System.Globalization.CultureInfo.InvariantCulture);
            uint aiRevision = uint.Parse(aiOpened.Groups["revision"].Value);
            Check(aiMoved > 0.25f, "enemy movement advanced before the authoritative door opened");
            Check(aiRevision == uint.Parse(revision) + 1u, "AI opening advanced the same authoritative revision exactly once");
            revision = aiOpened.Groups["revision"].Value;
            WriteAtomic(aCommand, "observe|" + doorId + "|6");
            WriteAtomic(bCommand, "observe|" + doorId + "|6");
            string aiOpenPattern = @"door-proof state role=(?<role>a|b) source=observe id=" + doorId + @" open=True revision=" + revision +
                @" ready=True replicaOpen=True blocker=False";
            Check(WaitFor(aLog, aiOpenPattern.Replace("role=(?<role>a|b)", "role=a"), TimeoutSeconds, a),
                "client A received the AI-opened state and same revision");
            Check(WaitFor(bLog, aiOpenPattern.Replace("role=(?<role>a|b)", "role=b"), TimeoutSeconds, b),
                "client B received the AI-opened state and same revision");

            WriteAtomic(serverStage, "secret-far");
            Match secretStage = WaitMatch(serverLog, @"secret-proof staged stage=secret-far id=(?<door>secret_[A-Za-z0-9_-]+)" +
                @" canInteract=False reason=Too_far_away open=False revision=" + revision, 20, server);
            Check(secretStage != null, "an authored secret rejects interaction from its remote stone wall");
            if (secretStage == null) return 1;
            string secretId = secretStage.Groups["door"].Value;
            Check(WaitFor(serverLog, @"secret-proof enemy id=" + secretId +
                @" accepted=False open=False blocker=True revisionUnchanged=True", 10, server),
                "the actual server enemy-open authority rejects the secret without changing revision");
            WriteAtomic(aCommand, "observe|" + secretId + "|7");
            WriteAtomic(bCommand, "observe|" + secretId + "|7");
            WriteAtomic(lateCommand, "observe|" + secretId + "|7");
            Match secretClosed = WaitMatch(aLog, @"door-proof state role=a source=observe id=" + secretId +
                @" open=False revision=\d+ ready=True replicaOpen=False blocker=True secret=True panelLocalY=(?<height>[-\d.]+)", 20, a);
            Check(secretClosed != null, "client A has the authored secret's closed wall snapshot before activation");
            if (secretClosed == null) return 1;
            Check(WaitFor(bLog, @"door-proof state role=b source=observe id=" + secretId +
                @" open=False revision=\d+ ready=True replicaOpen=False blocker=True secret=True", 20, b),
                "client B is observing the same closed cache");
            Check(WaitFor(lateLog, @"door-proof state role=late source=observe id=" + secretId +
                @" open=False revision=\d+ ready=True replicaOpen=False blocker=True secret=True", 20, late),
                "the late observer has the same closed cache from its initial snapshot");
            Thread.Sleep(350);
            WriteAtomic(aCommand, "toggle|" + secretId + "|8");
            Check(WaitFor(serverLog, @"door-proof request sender=" + admittedA.Groups["client"].Value + @" id=" + secretId +
                @" accepted=False reason=Too_far_away open=False revision=" + revision + @" blocker=True", 20, server),
                "a real client RPC from the remote wall cannot unlock the secret");
            WriteAtomic(serverStage, "secret-near");
            Check(WaitFor(serverLog, @"secret-proof staged stage=secret-near id=" + secretId +
                @" canInteract=True reason=- open=False revision=" + revision, 20, server),
                "server places the actor beside the actual visible lever");
            Thread.Sleep(350);
            WriteAtomic(aCommand, "toggle|" + secretId + "|9");
            Match secretOpened = WaitMatch(serverLog, @"door-proof request sender=" + admittedA.Groups["client"].Value + @" id=" + secretId +
                @" accepted=True reason=- open=True revision=(?<revision>\d+) blocker=False", 20, server);
            Check(secretOpened != null, "the real lever RPC unlocks the secret through production server authority");
            if (secretOpened == null) return 1;
            Check(uint.Parse(secretOpened.Groups["revision"].Value) == uint.Parse(revision) + 1u,
                "secret activation advances the shared door revision exactly once");
            revision = secretOpened.Groups["revision"].Value;
            string secretOpenPattern = @"door-proof state role=(?<role>a|b|late) source=replica id=" + secretId +
                @" open=True revision=" + revision + @" ready=True replicaOpen=True blocker=False secret=True panelLocalY=(?<height>[-\d.]+)";
            Match secretReplicaA = WaitMatch(aLog, secretOpenPattern.Replace("role=(?<role>a|b|late)", "role=a"), 20, a);
            Match secretReplicaB = WaitMatch(bLog, secretOpenPattern.Replace("role=(?<role>a|b|late)", "role=b"), 20, b);
            Match secretReplicaLate = WaitMatch(lateLog, secretOpenPattern.Replace("role=(?<role>a|b|late)", "role=late"), 20, late);
            Check(secretReplicaA != null && secretReplicaB != null && secretReplicaLate != null,
                "all three real clients replicate the secret unlock and disabled wall collider");
            if (secretReplicaA == null || secretReplicaB == null || secretReplicaLate == null) return 1;
            float closedHeight = float.Parse(secretClosed.Groups["height"].Value, System.Globalization.CultureInfo.InvariantCulture);
            float openHeight = float.Parse(secretReplicaA.Groups["height"].Value, System.Globalization.CultureInfo.InvariantCulture);
            Check(openHeight > closedHeight + .5f && secretReplicaA.Groups["height"].Value == secretReplicaB.Groups["height"].Value &&
                secretReplicaA.Groups["height"].Value == secretReplicaLate.Groups["height"].Value,
                "the remote stone panel actually slides upward identically on all clients");
            Thread.Sleep(350);
            WriteAtomic(aCommand, "toggle|" + secretId + "|10");
            Check(WaitFor(serverLog, @"door-proof request sender=" + admittedA.Groups["client"].Value + @" id=" + secretId +
                @" accepted=False reason=Already_activated open=True revision=" + revision + @" blocker=False", 20, server),
                "a repeated real lever request cannot close the cache or trap a player");

            File.WriteAllText(aQuit, "quit"); File.WriteAllText(bQuit, "quit"); File.WriteAllText(lateQuit, "quit");
            Check(WaitExit(a, 35), "client A shut down cleanly");
            Check(WaitExit(b, 35), "client B shut down cleanly");
            Check(WaitExit(late, 35), "late client shut down cleanly");
            File.WriteAllText(serverQuit, "quit");
            Check(WaitExit(server, 35), "door proof server shut down cleanly");
            return _failures == 0 ? 0 : 1;
        }

        private static string ClientArgs(int port, string slot, string role, string actionFile, string quitFile) =>
            "-ember-net client -ember-address 127.0.0.1 -ember-port " + port + " -ember-slot " + slot +
            " -ember-class warrior -ember-net-door-proof-role " + role + " -ember-net-door-proof-action-file " + Q(actionFile) +
            " -ember-net-quit-file " + Q(quitFile);

        private static Process Launch(string exe, string log, string args)
        {
            try
            {
                var info = new ProcessStartInfo { FileName = exe, Arguments = "-batchmode -nographics -logFile " + Q(log) + " " + args,
                    WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false, CreateNoWindow = true };
                Process process = Process.Start(info);
                if (process != null) Started.Add(process);
                return process;
            }
            catch (Exception e) { Debug.LogError(Tag + " launch failed: " + e.Message); return null; }
        }

        private static bool WaitFor(string path, string pattern, double seconds, Process process) =>
            Wait(() => Regex.IsMatch(Read(path), pattern), seconds, process);
        private static Match WaitMatch(string path, string pattern, double seconds, Process process)
        {
            Match match = null;
            Wait(() => { match = Regex.Match(Read(path), pattern); return match.Success; }, seconds, process);
            return match != null && match.Success ? match : null;
        }
        private static bool Wait(Func<bool> check, double seconds, Process process)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                if (check()) return true;
                if (Exited(process)) return check();
                Thread.Sleep(200);
            }
            return check();
        }
        private static bool WaitExit(Process process, int seconds)
        {
            try
            {
                if (process != null && process.WaitForExit(seconds * 1000)) return process.ExitCode == 0;
                Check(false, "process did not exit within " + seconds + " seconds");
                if (!Exited(process)) process.Kill();
            }
            catch (Exception e) { Check(false, "process shutdown failed: " + e.Message); }
            return false;
        }
        private static bool Exited(Process process) { try { return process == null || process.HasExited; } catch { return true; } }
        private static void Cleanup()
        {
            foreach (Process process in Started)
                try { if (process != null && !process.HasExited) { _failures++; process.Kill(); process.WaitForExit(5000); } }
                catch { }
        }
        private static int FreeUdpPort()
        {
            using (var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) return ((IPEndPoint)udp.Client.LocalEndPoint).Port;
        }
        private static void WriteAtomic(string path, string value)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, value, Encoding.UTF8);
            if (!File.Exists(path)) { File.Move(temp, path); return; }
            // A Windows scanner/reader can briefly hold the destination. Keep the old
            // complete command visible until replacement succeeds; never publish a partial one.
            for (int attempt = 0; ; attempt++)
            {
                try { File.Replace(temp, path, null); return; }
                catch (IOException) when (attempt < 20 && File.Exists(temp) && File.Exists(path))
                { System.Threading.Thread.Sleep(25); }
            }
        }
        private static string Read(string path)
        {
            try
            {
                if (!File.Exists(path)) return string.Empty;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
            catch { return string.Empty; }
        }
        private static string ArgValue(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == key) return args[i + 1];
            return string.Empty;
        }
        private static string Q(string value) => "\"" + value + "\"";
        private static void Check(bool success, string message)
        {
            _checks++;
            if (success) Debug.Log(Tag + " ok: " + message);
            else { _failures++; Debug.LogError(Tag + " FAIL: " + message); }
        }
    }
}
