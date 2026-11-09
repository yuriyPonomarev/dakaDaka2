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
using Emberdelve.Net.Rules;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Emberdelve.EditorTools
{
    /// <summary>U4B two-client process proof for successful extraction, foreign RPC rejection and owner-only result delivery.</summary>
    public static class EmberdelveNetExtractionGate
    {
        private const string Tag = "[Emberdelve][NetExtraction]";
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
            if (string.IsNullOrEmpty(runDir)) runDir = Path.Combine(root, "Builds", "net-extraction", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
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
                bool built = EmberdelveWindowsBuild.BuildNetProofPlayer(exe, out string buildError);
                Check(built, "development proof player built" + (built ? "" : ": " + buildError));
                if (!built) return 1;
            }
            else Check(File.Exists(exe), "reused proof player exists");
            if (!File.Exists(exe)) return 1;

            int port = FreeUdpPort();
            Check(port > 0, "loopback UDP port allocated");
            if (port <= 0) return 1;
            string serverQuit = Path.Combine(runDir, "server.quit");
            string aQuit = Path.Combine(runDir, "a.quit");
            string bQuit = Path.Combine(runDir, "b.quit");
            string go = Path.Combine(runDir, "go");
            foreach (string file in new[] { serverQuit, aQuit, bQuit, go }) if (File.Exists(file)) File.Delete(file);

            string serverLog = Path.Combine(logs, "server.log");
            string aLog = Path.Combine(logs, "client_a.log");
            string bLog = Path.Combine(logs, "client_b.log");
            Process server = Launch(exe, serverLog, "-ember-net server -ember-port " + port +
                " -ember-net-max-players 3 -ember-net-party-size 1 -ember-net-no-enemies -ember-net-extraction-proof-server -ember-net-quit-file " + Q(serverQuit));
            Check(server != null, "dedicated server process started");
            if (server == null || !WaitFor(serverLog, @"\[server\] ready scene=", TimeoutSeconds, server))
            { Check(false, "server reached ready state"); return 1; }
            Check(true, "server reached ready state");

            Process a = Launch(exe, aLog, ClientArgs(port, "extract-a", "extraction-owner", go, aQuit));
            Check(a != null, "extraction owner client A started");
            if (a == null) return 1;
            Match admittedA = WaitMatch(serverLog, @"admitted client=(?<client>\d+) id=(?<id>\d+) party=\d+ slot=extract-a class=warrior netId=\d+", TimeoutSeconds, server);
            Check(admittedA != null, "server admitted owner A");
            if (admittedA == null) return 1;

            Process b = Launch(exe, bLog, ClientArgs(port, "extract-b", "extraction-forge", go, bQuit));
            Check(b != null, "foreign RPC client B started");
            if (b == null) return 1;
            Match admittedB = WaitMatch(serverLog, @"admitted client=(?<client>\d+) id=(?<id>\d+) party=\d+ slot=extract-b class=warrior netId=\d+", TimeoutSeconds, server);
            Check(admittedB != null, "server admitted client B");
            if (admittedB == null) return 1;
            string clientA = admittedA.Groups["client"].Value, playerA = admittedA.Groups["id"].Value;
            string clientB = admittedB.Groups["client"].Value, playerB = admittedB.Groups["id"].Value;
            Check(clientA != clientB && playerA != playerB, "network sender and player identities are distinct");

            Match fixture = WaitMatch(serverLog, @"extraction-proof fixture player=" + playerA + @" client=" + clientA + @" .*canInteract=True recovered=(?<loot>\d+) revision=(?<revision>\d+) hold=(?<hold>[\d.]+)", TimeoutSeconds, server);
            Check(fixture != null, "server fixture passed production extraction CanInteract with carried loot");
            if (fixture == null) return 1;
            string playerId = playerA;
            Match readyA = WaitMatch(aLog, @"\[client\] extraction-inventory-state player=" + playerId + @" inventory=(?<inventory>r=" + fixture.Groups["revision"].Value + @";[^\s]*)", TimeoutSeconds, a);
            Match readyB = WaitMatch(bLog, @"\[client\] owner id=" + playerB + @" views=2 owned=1", TimeoutSeconds, b);
            Check(readyA != null && readyB != null, "both independent clients have their owner view and replicated scene");
            if (readyA == null || readyB == null) return 1;
            Match baselineRevision = Regex.Match(readyA.Groups["inventory"].Value, @"^r=(?<revision>\d+);");
            Check(baselineRevision.Success && baselineRevision.Groups["revision"].Value == fixture.Groups["revision"].Value,
                "owner baseline includes the fixture inventory revision before extraction starts");
            if (!baselineRevision.Success || baselineRevision.Groups["revision"].Value != fixture.Groups["revision"].Value) return 1;

            File.WriteAllText(go, fixture.Groups["hold"].Value);
            Match begin = WaitMatch(serverLog, @"extract-hold begin sender=" + clientA + @" player=" + playerA + @" seq=1 duration=(?<duration>[\d.]+)", TimeoutSeconds, server);
            Check(begin != null, "A began the production server-authorized extraction lease");
            if (begin == null) return 1;
            Match forged = WaitMatch(bLog, @"\[client\] extraction-forge sent sender=" + playerB + @" target=" + playerA + @" .*", TimeoutSeconds, b);
            Check(forged != null, "B called production extraction begin/heartbeat/complete/cancel RPCs on A's NetworkObject");
            if (forged == null) return 1;
            Match rejected = WaitMatch(serverLog, @"extract-hold reject=Identity sender=" + clientB + @" op=begin seq=1", TimeoutSeconds, server);
            Match rejectedComplete = WaitMatch(serverLog, @"extract-hold reject=Identity sender=" + clientB + @" op=complete seq=1", TimeoutSeconds, server);
            Check(rejected != null && rejectedComplete != null, "server rejected B's foreign begin and complete by transport sender identity");

            Match complete = WaitMatch(serverLog, @"extract-hold complete sender=" + clientA + @" player=" + playerA + @" seq=1 accepted=True", TimeoutSeconds, server);
            Match sent = WaitMatch(serverLog, @"run-result sent owner=" + clientA + @" player=" + playerA + @" outcome=Extracted class=(?<class>[^ ]+) loot=(?<loot>[^ ]+) items=(?<items>\d+) kills=(?<kills>\d+) duration=(?<duration>[^ ]+) gear=(?<gear>\d+)", TimeoutSeconds, server);
            Check(complete != null, "A's completed server-time hold extracted exactly once");
            Check(sent != null, "server sent its exact terminal RunResult to owner A");
            if (sent == null || complete == null) return 1;

            Match ownerResult = WaitMatch(aLog, @"\[client\] extraction-result owner=" + playerA + @" count=1 outcome=Extracted class=(?<class>[^ ]+) loot=(?<loot>[^ ]+) items=(?<items>\d+) kills=(?<kills>\d+) duration=(?<duration>[^ ]+) gear=(?<gear>\d+) inventoryBefore=(?<before>[^ ]+) inventoryAfter=(?<after>[^ ]+) cache=True", TimeoutSeconds, a);
            Check(ownerResult != null, "A received one exact owner result and populated its result-screen cache");
            if (ownerResult != null)
            {
                foreach (string field in new[] { "class", "loot", "items", "kills", "duration", "gear" })
                    Check(ownerResult.Groups[field].Value == sent.Groups[field].Value, "owner result matches server field " + field);
                Check(ownerResult.Groups["before"].Value == readyA.Groups["inventory"].Value,
                    "owner presentation started from the confirmed fixture snapshot");
                Check(ownerResult.Groups["before"].Value == ownerResult.Groups["after"].Value,
                    "client extraction presentation did not mutate or bank the carried inventory");
            }

            Match duplicate = WaitMatch(aLog, @"\[client\] extraction-duplicate-complete-sent player=" + playerA + @" seq=1", TimeoutSeconds, a);
            Check(duplicate != null, "A replayed completion after receiving the result");
            Thread.Sleep(1200);
            Check(Count(serverLog, @"extract-hold complete sender=" + clientA + @" player=" + playerA + @" seq=1 accepted=True") == 1,
                "duplicate completion did not commit a second extraction");
            Check(Count(serverLog, @"run-result sent owner=" + clientA + @" player=" + playerA + @" ") == 1,
                "server emitted exactly one owner RunResult");
            Check(Count(aLog, @"\[client\] extraction-result owner=" + playerA + @" ") == 1,
                "owner observed exactly one result event");
            Check(Count(bLog, @"\[client\] extraction-result-leak receiver=" + playerB + @" resultPlayer=" + playerA + @" ") == 0,
                "foreign client B observed no RunEnded event for A's result");

            File.WriteAllText(aQuit, "quit");
            File.WriteAllText(bQuit, "quit");
            Check(WaitExit(a, 20), "client A shut down cleanly");
            Check(WaitExit(b, 20), "client B shut down cleanly");
            File.WriteAllText(serverQuit, "quit");
            Check(WaitExit(server, 20), "server shut down cleanly");
            if (_failures != 0) return 1;
            return ExecuteInterruptionScenarios(exe, runDir);
        }

        private static int ExecuteInterruptionScenarios(string exe, string runRoot)
        {
            foreach (string mode in new[] { "movement", "damage", "death" })
                if (ExecuteInterruptionScenario(exe, runRoot, mode) != 0) return 1;
            if (ExecuteTimeoutScenario(exe, runRoot) != 0) return 1;
            return _failures == 0 ? 0 : 1;
        }

        private static int ExecuteTimeoutScenario(string exe, string runRoot)
        {
            string runDir = Path.Combine(runRoot, "timeout");
            string logs = Path.Combine(runDir, "logs");
            Directory.CreateDirectory(logs);
            int port = FreeUdpPort();
            Check(port > 0, "timeout loopback UDP port allocated");
            if (port <= 0) return 1;
            string serverQuit = Path.Combine(runDir, "server.quit");
            string aQuit = Path.Combine(runDir, "a.quit");
            string bQuit = Path.Combine(runDir, "b.quit");
            string lateQuit = Path.Combine(runDir, "late.quit");
            string start = Path.Combine(runDir, "start");
            foreach (string file in new[] { serverQuit, aQuit, bQuit, lateQuit, start }) if (File.Exists(file)) File.Delete(file);

            string serverLog = Path.Combine(logs, "server.log");
            string aLog = Path.Combine(logs, "client_a.log");
            string bLog = Path.Combine(logs, "client_b.log");
            string lateLog = Path.Combine(logs, "client_late.log");
            Process server = Launch(exe, serverLog, "-ember-net server -ember-port " + port +
                " -ember-net-max-players 3 -ember-net-party-size 1 -ember-net-no-enemies -ember-net-timeout-proof-server" +
                " -ember-net-quit-file " + Q(serverQuit));
            Check(server != null, "timeout dedicated server started with development-only fixture flag");
            if (server == null || !WaitFor(serverLog, @"\[server\] ready scene=", TimeoutSeconds, server))
            { Check(false, "timeout server reached ready state"); return 1; }
            Check(true, "timeout server reached ready state");

            Process a = Launch(exe, aLog, ClientArgs(port, "extract-a", "none", start, aQuit));
            Check(a != null, "timeout owner A started");
            if (a == null) return 1;
            Match admittedA = WaitMatch(serverLog, @"admitted client=(?<client>\d+) id=(?<id>\d+) party=\d+ slot=extract-a class=warrior netId=\d+", TimeoutSeconds, server);
            Check(admittedA != null, "timeout server admitted owner A");
            if (admittedA == null) return 1;

            Process b = Launch(exe, bLog, ClientArgs(port, "extract-b", "none", start, bQuit));
            Check(b != null, "timeout owner B started");
            if (b == null) return 1;
            Match admittedB = WaitMatch(serverLog, @"admitted client=(?<client>\d+) id=(?<id>\d+) party=\d+ slot=extract-b class=warrior netId=\d+", TimeoutSeconds, server);
            Check(admittedB != null, "timeout server admitted owner B");
            if (admittedB == null) return 1;
            string clientA = admittedA.Groups["client"].Value, playerA = admittedA.Groups["id"].Value;
            string clientB = admittedB.Groups["client"].Value, playerB = admittedB.Groups["id"].Value;

            Match fixture = WaitMatch(serverLog,
                @"timeout-proof fixture duration=(?<duration>\d+) productionCountdown=(?<production>(?i:true|false)) players=" + playerA + @"," + playerB +
                @" uidA=(?<uidA>[A-Fa-f0-9]{32}) uidB=(?<uidB>[A-Fa-f0-9]{32})",
                TimeoutSeconds, server);
            Check(fixture != null, "server installed short process-local timer after both actors were admitted");
            if (fixture == null) return 1;
            Check(fixture.Groups["duration"].Value == "5", "proof deadline is short and fixture-only");
            Check(bool.TryParse(fixture.Groups["production"].Value, out bool productionCountdown) && productionCountdown,
                "short fixture preserves the enabled production countdown setting");
            Match clientClockA = WaitMatch(aLog, @"\[client\] raid-clock enabled=True remaining=[\d.]+ warning=True expired=False source=server", TimeoutSeconds, a);
            Match clientClockB = WaitMatch(bLog, @"\[client\] raid-clock enabled=True remaining=[\d.]+ warning=True expired=False source=server", TimeoutSeconds, b);
            Check(clientClockA != null && clientClockB != null,
                "both owners received the server countdown and pre-expiry warning");

            Match outcome = WaitMatch(serverLog,
                @"timeout-proof outcome expired=True productionCountdown=(?<production>(?i:true|false)) playerA=" + playerA +
                @" resultA=Dead extractionA=False reasonA=The_raid_timer_has_expired playerB=" + playerB +
                @" resultB=Dead extractionB=False reasonB=The_raid_timer_has_expired corpses=(?<corpses>\d+)" +
                @" uidA=(?<uidA>[A-Fa-f0-9]{32}) uidAInCorpseA=True corpseA=(?<corpseA>\d+) uidAInCorpseB=False" +
                @" uidB=(?<uidB>[A-Fa-f0-9]{32}) uidBInCorpseB=True corpseB=(?<corpseB>\d+) uidBInCorpseA=False",
                TimeoutSeconds, server);
            Check(outcome != null, "server deadline killed both live actors, refused extraction and retained both session corpses");
            if (outcome == null) return 1;
            Check(bool.TryParse(outcome.Groups["production"].Value, out productionCountdown) && productionCountdown,
                "server deadline proof did not disable production countdown");
            Check(outcome.Groups["corpses"].Value == "2", "both player loadouts reached in-memory corpse custody");
            Check(outcome.Groups["uidA"].Value == fixture.Groups["uidA"].Value &&
                  outcome.Groups["uidB"].Value == fixture.Groups["uidB"].Value &&
                  outcome.Groups["corpseA"].Value != outcome.Groups["corpseB"].Value,
                "each player's exact seeded UID exists only in that player's distinct corpse");
            Match expiredClockA = WaitMatch(aLog, @"\[client\] raid-clock enabled=True remaining=[\d.]+ warning=True expired=True source=server", TimeoutSeconds, a);
            Match expiredClockB = WaitMatch(bLog, @"\[client\] raid-clock enabled=True remaining=[\d.]+ warning=True expired=True source=server", TimeoutSeconds, b);
            Check(expiredClockA != null && expiredClockB != null, "both owners received the server's expired clock state");
            Match resultA = WaitMatch(serverLog, @"run-result sent owner=" + clientA + @" player=" + playerA + @" outcome=Dead ", TimeoutSeconds, server);
            Match resultB = WaitMatch(serverLog, @"run-result sent owner=" + clientB + @" player=" + playerB + @" outcome=Dead ", TimeoutSeconds, server);
            Check(resultA != null && resultB != null, "server sent one Dead result to each owner after timeout custody");
            if (resultA == null || resultB == null) return 1;
            Match corpseA = WaitMatch(serverLog, @"death corpse committed id=\d+ source=" + playerA + @" entries=\d+", TimeoutSeconds, server);
            Match corpseB = WaitMatch(serverLog, @"death corpse committed id=\d+ source=" + playerB + @" entries=\d+", TimeoutSeconds, server);
            Check(corpseA != null && corpseB != null && corpseA.Index < resultA.Index && corpseB.Index < resultB.Index,
                "each timed-out actor's loadout reached its own corpse before its owner result");
            Match ownerA = WaitMatch(aLog, @"\[client\] extraction-result owner=" + playerA + @" count=1 outcome=Dead ", TimeoutSeconds, a);
            Match ownerB = WaitMatch(bLog, @"\[client\] extraction-result owner=" + playerB + @" count=1 outcome=Dead ", TimeoutSeconds, b);
            Check(ownerA != null && ownerB != null, "both owners received their single Dead result");
            Check(Count(serverLog, @"run-result sent owner=" + clientA + @" player=" + playerA + @" outcome=Dead ") == 1 &&
                  Count(serverLog, @"run-result sent owner=" + clientB + @" player=" + playerB + @" outcome=Dead ") == 1,
                "server sent exactly one timeout result to each player owner");
            Check(Count(aLog, @"\[client\] extraction-result owner=" + playerA + @" count=1 outcome=Dead ") == 1 &&
                  Count(bLog, @"\[client\] extraction-result owner=" + playerB + @" count=1 outcome=Dead ") == 1,
                "each owner observed exactly one timeout result");

            Process late = Launch(exe, lateLog, ClientArgs(port, "timeout-late", "none", start, lateQuit));
            Check(late != null, "late join client started after server deadline");
            if (late == null) return 1;
            Match refused = WaitMatch(serverLog, @"spawn refused client=(?<client>\d+) reason='the raid timer has expired'", TimeoutSeconds, server);
            Check(refused != null, "server refused a new actor after the raid deadline");
            if (refused == null) return 1;
            Match lateRefusal = WaitMatch(lateLog, @"\[client\] disconnected unexpectedly reason='the raid timer has expired'", TimeoutSeconds, late);
            Match lateShutdown = WaitMatch(lateLog, @"\[client\] shutdown complete clean=True players=0 exit=13", TimeoutSeconds, late);
            Check(lateRefusal != null, "late client recorded the exact server timer refusal after connection approval");
            Check(lateShutdown != null, "late client completed network shutdown cleanly with the documented refusal disconnect code");
            File.WriteAllText(aQuit, "quit");
            File.WriteAllText(bQuit, "quit");
            File.WriteAllText(lateQuit, "quit");
            Check(WaitExit(a, 20), "timeout client A shut down cleanly");
            Check(WaitExit(b, 20), "timeout client B shut down cleanly");
            bool lateProcessExpected = WaitForExitCode(late, 20, NetExitCodes.UnexpectedDisconnect, out int lateExitCode);
            Check(lateProcessExpected,
                "late join process terminated with the expected post-approval refusal code 13 (was " + lateExitCode + ")");
            File.WriteAllText(serverQuit, "quit");
            Check(WaitExit(server, 20), "timeout proof server shut down cleanly");
            return _failures == 0 ? 0 : 1;
        }

        private static int ExecuteInterruptionScenario(string exe, string runRoot, string mode)
        {
            string runDir = Path.Combine(runRoot, "interrupt-" + mode);
            string logs = Path.Combine(runDir, "logs");
            Directory.CreateDirectory(logs);
            int port = FreeUdpPort();
            Check(port > 0, mode + " loopback UDP port allocated");
            if (port <= 0) return 1;
            string serverQuit = Path.Combine(runDir, "server.quit");
            string aQuit = Path.Combine(runDir, "a.quit");
            string bQuit = Path.Combine(runDir, "b.quit");
            string go = Path.Combine(runDir, "go");
            string action = Path.Combine(runDir, "action");
            foreach (string file in new[] { serverQuit, aQuit, bQuit, go, action }) if (File.Exists(file)) File.Delete(file);

            string serverLog = Path.Combine(logs, "server.log");
            string aLog = Path.Combine(logs, "client_a.log");
            string bLog = Path.Combine(logs, "client_b.log");
            Process server = Launch(exe, serverLog, "-ember-net server -ember-port " + port +
                " -ember-net-max-players 3 -ember-net-party-size 1 -ember-net-no-enemies -ember-net-extraction-proof-server" +
                " -ember-net-extraction-proof-action-file " + Q(action) + " -ember-net-quit-file " + Q(serverQuit));
            Check(server != null, mode + " dedicated server started");
            if (server == null || !WaitFor(serverLog, @"\[server\] ready scene=", TimeoutSeconds, server))
            { Check(false, mode + " server reached ready state"); return 1; }
            Check(true, mode + " server reached ready state");

            string ownerScript = mode == "movement"
                ? "walk-forward:0.7@0,walk-forward:0.7@90,walk-forward:0.7@180,walk-forward:0.7@270"
                : "extraction-interrupt";
            Process a = Launch(exe, aLog, ClientArgs(port, "extract-a", ownerScript, go, aQuit) + " -ember-net-extraction-proof-owner");
            Check(a != null, mode + " owner client A started");
            if (a == null) return 1;
            Match admittedA = WaitMatch(serverLog, @"admitted client=(?<client>\d+) id=(?<id>\d+) party=\d+ slot=extract-a class=warrior netId=\d+", TimeoutSeconds, server);
            Check(admittedA != null, mode + " server admitted A");
            if (admittedA == null) return 1;

            Process b = Launch(exe, bLog, ClientArgs(port, "extract-b", "none", go, bQuit));
            Check(b != null, mode + " observer client B started");
            if (b == null) return 1;
            Match admittedB = WaitMatch(serverLog, @"admitted client=(?<client>\d+) id=(?<id>\d+) party=\d+ slot=extract-b class=warrior netId=\d+", TimeoutSeconds, server);
            Check(admittedB != null, mode + " server admitted B");
            if (admittedB == null) return 1;
            string clientA = admittedA.Groups["client"].Value, playerA = admittedA.Groups["id"].Value;
            string clientB = admittedB.Groups["client"].Value, playerB = admittedB.Groups["id"].Value;
            Check(clientA != clientB && playerA != playerB, mode + " client identities are distinct");

            Match fixture = WaitMatch(serverLog, @"extraction-proof fixture player=" + playerA + @" client=" + clientA + @" .*canInteract=True recovered=\d+ revision=(?<revision>\d+) hold=(?<hold>[\d.]+)", TimeoutSeconds, server);
            Check(fixture != null, mode + " fixture passed production extraction CanInteract");
            if (fixture == null) return 1;
            Match readyA = WaitMatch(aLog, @"\[client\] extraction-inventory-state player=" + playerA + @" inventory=(?<inventory>r=" + fixture.Groups["revision"].Value + @";[^\s]*)", TimeoutSeconds, a);
            Match readyB = WaitMatch(bLog, @"\[client\] owner id=" + playerB + @" views=2 owned=1", TimeoutSeconds, b);
            Check(readyA != null && readyB != null, mode + " both clients reached stable owner views and A has fixture inventory revision");
            if (readyA == null || readyB == null) return 1;

            int movementSegmentsBefore = Count(serverLog, @"seg id=" + playerA + @" kind=move ");
            File.WriteAllText(go, fixture.Groups["hold"].Value);
            Match begin = WaitMatch(serverLog, @"extract-hold begin sender=" + clientA + @" player=" + playerA + @" seq=1 duration=", TimeoutSeconds, server);
            Match beginClient = WaitMatch(aLog, @"\[client\] extraction-interrupt-begin player=" + playerA + @" queued=True", TimeoutSeconds, a);
            Check(begin != null && beginClient != null, mode + " A began the real extraction hold through its production client bridge");
            if (begin == null || beginClient == null) return 1;
            Match stimulus = null;
            if (mode == "movement")
            {
                Match scriptStart = WaitMatch(aLog, @"\[client\] script walk-forward:0\.7@0,walk-forward:0\.7@90,walk-forward:0\.7@180,walk-forward:0\.7@270 start pos=", TimeoutSeconds, a);
                bool segmentObserved = Wait(() => Count(serverLog, @"seg id=" + playerA + @" kind=move ") > movementSegmentsBefore, TimeoutSeconds, server);
                Check(scriptStart != null && segmentObserved, "A sent its scripted real movement and the server resolved a new move segment");
                if (!segmentObserved) return 1;
                MatchCollection segments = Regex.Matches(Read(serverLog), @"seg id=" + playerA + @" kind=move .*?dist=(?<dist>[\d.]+)");
                Match movedSegment = segments.Count > movementSegmentsBefore ? segments[segments.Count - 1] : null;
                Check(movedSegment != null && float.TryParse(movedSegment.Groups["dist"].Value, NumberStyles.Float,
                          CultureInfo.InvariantCulture, out float movedDistance) && movedDistance > 0.05f,
                    "server-observed client movement exceeded the extraction hold's 5 cm tolerance");
                if (movedSegment == null) return 1;
            }
            else
            {
                File.WriteAllText(action, mode);
                stimulus = WaitMatch(serverLog, @"extraction-proof stimulus mode=" + mode + @" player=" + playerA + @" .*", TimeoutSeconds, server);
                Check(stimulus != null, mode + " server fixture applied a real server-side damage API stimulus");
                if (stimulus == null) return 1;
            }
            string expectedReason = mode == "movement" ? "Movement interrupted extraction" : "Damage interrupted extraction";
            Match interruption = WaitMatch(serverLog, @"extract-hold interrupt sender=" + clientA + @" player=" + playerA + @" seq=1 reason=" + Regex.Escape(expectedReason), TimeoutSeconds, server);
            Match ownerHoldResult = WaitMatch(aLog, @"\[client\] extraction-hold-result player=" + playerA + @" seq=1 accepted=False reason=" + Regex.Escape(expectedReason.Replace(' ', '_')), TimeoutSeconds, a);
            Check(interruption != null && ownerHoldResult != null, mode + " authoritative interruption removed the lease and reached its owner");
            if (interruption == null || ownerHoldResult == null) return 1;

            if (mode == "death")
            {
                Check(stimulus != null && stimulus.Value.Contains("mode=death") && stimulus.Value.Contains("dead=True"), "death stimulus killed the rig through HealthComponent");
                Check(stimulus.Value.Contains("health=0"), "death stimulus left zero server health");
                Match deadServerResult = WaitMatch(serverLog, @"run-result sent owner=" + clientA + @" player=" + playerA + @" outcome=Dead class=(?<class>[^ ]+) loot=(?<loot>[^ ]+) items=(?<items>\d+) kills=(?<kills>\d+) duration=(?<duration>[^ ]+) gear=(?<gear>\d+)", TimeoutSeconds, server);
                Match deadOwnerResult = WaitMatch(aLog, @"\[client\] extraction-result owner=" + playerA + @" count=1 outcome=Dead class=(?<class>[^ ]+) loot=(?<loot>[^ ]+) items=(?<items>\d+) kills=(?<kills>\d+) duration=(?<duration>[^ ]+) gear=(?<gear>\d+) inventoryBefore=(?<before>[^ ]+) inventoryAfter=(?<after>[^ ]+) cache=True", TimeoutSeconds, a);
                Check(deadServerResult != null && deadOwnerResult != null, "death produced the genuine RunDirector Dead result and replicated it once to A");
                if (deadServerResult != null && deadOwnerResult != null)
                {
                    foreach (string field in new[] { "class", "loot", "items", "kills", "duration", "gear" })
                        Check(deadOwnerResult.Groups[field].Value == deadServerResult.Groups[field].Value, "dead owner result matches server field " + field);
                    Check(deadOwnerResult.Groups["before"].Value == readyA.Groups["inventory"].Value,
                        "death result began from A's confirmed pre-death inventory snapshot");
                    Check(Regex.IsMatch(deadOwnerResult.Groups["after"].Value, @"^r=\d+;stacks=$") &&
                          deadOwnerResult.Groups["before"].Value != deadOwnerResult.Groups["after"].Value,
                        "owner inventory reflects the server-authoritative source clear after death custody");
                    Check(deadServerResult.Index > interruption.Index,
                        "the extraction lease was interrupted before RunDirector emitted the actual death outcome");
                    Match deathCorpse = WaitMatch(serverLog, @"death corpse committed id=\d+ source=" + playerA + @" entries=\d+", TimeoutSeconds, server);
                    Check(deathCorpse != null && deathCorpse.Index < deadServerResult.Index,
                        "server committed A's loadout to corpse custody before publishing the Dead result");
                }
                Check(Count(serverLog, @"run-result sent owner=" + clientA + @" player=" + playerA + @" outcome=Dead") == 1 &&
                      Count(aLog, @"\[client\] extraction-result owner=" + playerA + @" ") == 1,
                    "death produced exactly one authoritative and one owner-visible result");
                Check(Count(serverLog, @"run-result sent owner=" + clientA + @" player=" + playerA + @" outcome=Extracted") == 0,
                    "death was not mislabeled as successful extraction");
            }
            else
            {
                Check(mode == "movement" || (stimulus != null && stimulus.Value.Contains("dead=False")),
                    mode + " stimulus preserved the intended nonlethal condition");
                Thread.Sleep(1000);
                Check(Count(serverLog, @"extract-hold complete sender=" + clientA + @" player=" + playerA + @" seq=1 accepted=True") == 0,
                    mode + " interruption never committed extraction");
                Check(Count(serverLog, @"run-result sent owner=" + clientA + @" player=" + playerA + @" ") == 0 &&
                      Count(aLog, @"\[client\] extraction-result owner=" + playerA + @" ") == 0,
                    mode + " interruption did not invent a terminal player result");
            }

            Check(Count(bLog, @"\[client\] extraction-result-leak receiver=" + playerB + @" resultPlayer=" + playerA + @" ") == 0,
                mode + " observer B received no owner-only result event");
            File.WriteAllText(aQuit, "quit");
            File.WriteAllText(bQuit, "quit");
            Check(WaitExit(a, 20), mode + " client A shut down cleanly");
            Check(WaitExit(b, 20), mode + " client B shut down cleanly");
            File.WriteAllText(serverQuit, "quit");
            Check(WaitExit(server, 20), mode + " server shut down cleanly");
            return _failures == 0 ? 0 : 1;
        }

        private static string ClientArgs(int port, string slot, string script, string startFile, string quitFile) =>
            "-ember-net client -ember-address 127.0.0.1 -ember-port " + port + " -ember-slot " + slot +
            " -ember-class warrior -ember-net-script " + script + " -ember-net-script-start-file " + Q(startFile) +
            " -ember-net-quit-file " + Q(quitFile);

        private static Process Launch(string exe, string log, string args)
        {
            try
            {
                var info = new ProcessStartInfo { FileName = exe, Arguments = "-batchmode -nographics -logFile " + Q(log) + " " + args,
                    WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false, CreateNoWindow = true };
                Process process = Process.Start(info);
                if (process != null) Started.Add(process);
                Debug.Log(Tag + " launched pid=" + (process != null ? process.Id.ToString() : "-") + " " + info.Arguments);
                return process;
            }
            catch (Exception e) { Debug.LogError(Tag + " launch failed: " + e.Message); return null; }
        }

        private static bool WaitFor(string path, string pattern, double seconds, Process process) =>
            Wait(() => Regex.IsMatch(Read(path), pattern), seconds, process);
        private static Match WaitMatch(string path, string pattern, double seconds, Process process)
        {
            Match found = null;
            Wait(() => { found = Regex.Match(Read(path), pattern); return found.Success; }, seconds, process);
            return found != null && found.Success ? found : null;
        }
        private static int Count(string path, string pattern) => Regex.Matches(Read(path), pattern).Count;
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
        private static bool WaitForExitCode(Process process, int seconds, int expectedExitCode, out int actualExitCode)
        {
            actualExitCode = -1;
            try
            {
                if (process == null) return false;
                if (!process.WaitForExit(seconds * 1000))
                {
                    if (!Exited(process)) process.Kill();
                    return false;
                }
                actualExitCode = process.ExitCode;
                return actualExitCode == expectedExitCode;
            }
            catch { return false; }
        }
        private static bool Exited(Process process) { try { return process == null || process.HasExited; } catch { return true; } }
        private static void Cleanup()
        {
            foreach (Process process in Started)
            {
                try
                {
                    if (process != null && !process.HasExited)
                    {
                        Debug.LogError(Tag + " cleanup killed process " + process.Id);
                        _checks++; _failures++; process.Kill(); process.WaitForExit(5000);
                    }
                }
                catch { }
            }
        }
        private static int FreeUdpPort()
        {
            using (var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) return ((IPEndPoint)udp.Client.LocalEndPoint).Port;
        }
        private static string Read(string path)
        {
            try
            {
                if (!File.Exists(path)) return "";
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
            catch { return ""; }
        }
        private static string ArgValue(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return "";
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
