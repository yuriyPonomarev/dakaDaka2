using System.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Emberdelve.Data;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// U4A process gate: two independent clients talk to one real NGO server. A moves one of its actual bag stacks and gets
    /// the owner-only result/snapshot. B then calls the same production RPC on A's spawned NetworkObject; the server must
    /// reject B by transport sender identity before looking at the structurally valid payload or mutating either bag.
    /// Run through the net gate set; the player/server processes use the project's proof build and loopback transport.
    /// </summary>
    public static class EmberdelveNetInventoryGate
    {
        private const string Tag = "[Emberdelve][NetInventory]";
        private const int TimeoutSeconds = 90;
        private static int _checks;
        private static int _failures;
        private static readonly List<Process> Started = new List<Process>();
        private static string _runRoot;
        private static string _proofExe;

        public static void Run()
        {
            int exit = 1;
            try
            {
                if (!Application.isBatchMode)
                {
                    Check(false, "must run in batch mode because this gate starts real network processes");
                }
                else
                {
                    exit = Execute();
                    if (exit == 0) exit = ExecuteChestContest();
                    if (exit == 0) exit = ExecuteCorpseProof();
                    if (exit == 0) exit = ExecuteCorpseHostProof();
                    if (exit == 0) exit = ExecuteCorpseDeathProof();
                    if (exit == 0) exit = ExecuteFloorLootProof();
                }
            }
            catch (Exception e)
            {
                Check(false, "gate threw " + e);
                exit = 1;
            }
            finally { KillSurvivors(); }

            string verdict = _failures == 0 && exit == 0 ? "PASS" : "FAIL";
            string line = Tag + " " + verdict + " checks=" + _checks + " failures=" + _failures;
            if (_failures == 0) Debug.Log(line); else Debug.LogError(line);
            if (Application.isBatchMode) EmberdelveGateExit.Exit(_failures == 0 && exit == 0 ? 0 : 1);
        }

        public static void RunFloorLootProofOnly()
        {
            _checks = 0;
            _failures = 0;
            Started.Clear();
            int exit = 1;
            try
            {
                if (!Application.isBatchMode)
                {
                    Check(false, "must run in batch mode because this gate starts real network processes");
                }
                else if (InitializeFloorLootProofRuntime())
                {
                    exit = ExecuteFloorLootProof();
                }
            }
            catch (Exception e)
            {
                Check(false, "floor-loot gate threw " + e);
                exit = 1;
            }
            finally { KillSurvivors(); }

            string verdict = _failures == 0 && exit == 0 ? "PASS" : "FAIL";
            string line = Tag + " " + verdict + " set=net-floor-loot checks=" + _checks + " failures=" + _failures;
            if (_failures == 0) Debug.Log(line); else Debug.LogError(line);
            if (Application.isBatchMode) EmberdelveGateExit.Exit(_failures == 0 && exit == 0 ? 0 : 1);
        }

        public static void RunInventoryDropProofOnly()
        {
            _checks = 0;
            _failures = 0;
            Started.Clear();
            int exit = 1;
            try
            {
                if (!Application.isBatchMode)
                    Check(false, "must run in batch mode because this gate starts real network processes");
                else if (InitializeFloorLootProofRuntime())
                    exit = ExecuteInventoryDropProof();
            }
            catch (Exception e)
            {
                Check(false, "inventory-drop gate threw " + e);
                exit = 1;
            }
            finally { KillSurvivors(); }

            string verdict = _failures == 0 && exit == 0 ? "PASS" : "FAIL";
            string line = Tag + " " + verdict + " set=net-inventory-drop checks=" + _checks + " failures=" + _failures;
            if (_failures == 0) Debug.Log(line); else Debug.LogError(line);
            if (Application.isBatchMode) EmberdelveGateExit.Exit(_failures == 0 && exit == 0 ? 0 : 1);
        }

        private static bool InitializeFloorLootProofRuntime()
        {
            string root = Directory.GetParent(Application.dataPath).FullName;
            string runDir = ArgValue("-ember-proof-dir");
            if (string.IsNullOrEmpty(runDir))
                runDir = Path.Combine(root, "Builds", "net-floor-loot", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            runDir = Path.GetFullPath(runDir);
            _runRoot = runDir;
            if (runDir.StartsWith(Path.GetFullPath(Application.dataPath), StringComparison.OrdinalIgnoreCase))
            {
                Check(false, "proof directory must be outside Assets");
                return false;
            }

            string exe = ArgValue("-ember-proof-reuse-exe");
            if (string.IsNullOrEmpty(exe))
            {
                exe = Path.Combine(runDir, "player", "EmberdelveNet.exe");
                exe = EmberdelvePlayerBuildCache.TestPlayerPath(exe, "net-proof");
                bool built = EmberdelveWindowsBuild.BuildNetProofPlayer(exe, out string buildError);
                Check(built, "development proof player built" + (built ? "" : ": " + buildError));
                if (!built) return false;
            }
            else Check(File.Exists(exe), "reused proof player exists");

            if (!File.Exists(exe)) return false;
            _proofExe = Path.GetFullPath(exe);
            return true;
        }

        private static int Execute()
        {
            string root = Directory.GetParent(Application.dataPath).FullName;
            string runDir = ArgValue("-ember-proof-dir");
            if (string.IsNullOrEmpty(runDir))
                runDir = Path.Combine(root, "Builds", "net-inventory", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            runDir = Path.GetFullPath(runDir);
            _runRoot = runDir;
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
            _proofExe = exe;

            int port = FreeUdpPort();
            Check(port > 0, "loopback UDP port allocated");
            if (port <= 0) return 1;

            string serverQuit = Path.Combine(runDir, "server.quit");
            string aQuit = Path.Combine(runDir, "a.quit");
            string bQuit = Path.Combine(runDir, "b.quit");
            string goA = Path.Combine(runDir, "a.go");
            string goB = Path.Combine(runDir, "b.go");
            foreach (string file in new[] { serverQuit, aQuit, bQuit, goA, goB })
                if (File.Exists(file)) File.Delete(file);

            string serverLog = Path.Combine(logs, "server.log");
            string aLog = Path.Combine(logs, "client_a.log");
            string bLog = Path.Combine(logs, "client_b.log");
            Process server = Launch(exe, serverLog, "-ember-net server -ember-port " + port +
                " -ember-net-max-players 3 -ember-net-no-enemies -ember-net-quit-file " + Q(serverQuit));
            Check(server != null, "dedicated server process started");
            if (server == null || !WaitForLog(serverLog, "[server] ready scene=", TimeoutSeconds, server))
            { Check(false, "server reached ready state"); return 1; }
            Check(true, "server reached ready state");

            Process a = Launch(exe, aLog, ClientArgs(port, "inv-a", "inventory-owner", goA, aQuit));
            Check(a != null, "owner client A started");
            if (a == null) return 1;
            Match admittedA = WaitForRegex(serverLog, @"admitted client=(?<client>\d+) id=(?<id>\d+) party=\d+ slot=inv-a class=warrior netId=(?<net>\d+)", TimeoutSeconds, server);
            Check(admittedA != null, "server admitted client A");
            if (admittedA == null) return 1;

            Process b = Launch(exe, bLog, ClientArgs(port, "inv-b", "inventory-forge", goB, bQuit));
            Check(b != null, "second client B started");
            if (b == null) return 1;
            Match admittedB = WaitForRegex(serverLog, @"admitted client=(?<client>\d+) id=(?<id>\d+) party=\d+ slot=inv-b class=warrior netId=(?<net>\d+)", TimeoutSeconds, server);
            Check(admittedB != null, "server admitted client B");
            if (admittedB == null) return 1;
            string clientA = admittedA.Groups["client"].Value;
            string playerA = admittedA.Groups["id"].Value;
            string clientB = admittedB.Groups["client"].Value;
            string playerB = admittedB.Groups["id"].Value;
            Check(clientA != clientB && playerA != playerB, "clients and server player identities are distinct");

            string viewA = @"\[client\] owner id=" + playerA + @" views=2 owned=1 .*";
            string viewB = @"\[client\] owner id=" + playerB + @" views=2 owned=1 .*";
            Check(WaitForRegex(aLog, viewA, TimeoutSeconds, a) != null, "A sees both spawned NetworkObjects");
            Check(WaitForRegex(bLog, viewB, TimeoutSeconds, b) != null, "B sees both spawned NetworkObjects");
            Match bBaseline = WaitForRegex(bLog, @"\[client\] inventory-state role=forge digest=(?<digest>[^\s]+) resultEvents=0 confirmed=False", TimeoutSeconds, b);
            Check(bBaseline != null, "B's own inventory snapshot and response observer are ready before A acts");
            if (bBaseline == null) return 1;

            WriteAtomic(goA, "go");
            Match acceptedOnServer = WaitForRegex(serverLog,
                @"inventory-command sender=" + clientA + @" id=" + playerA + @" seq=1 accepted=True changed=True revision=(?<revision>\d+)", TimeoutSeconds, server);
            Check(acceptedOnServer != null, "server received and committed A's real Move command");
            Match ownerResult = WaitForRegex(aLog, @"\[client\] inventory-result seq=1 accepted=True changed=True revision=(?<revision>\d+)", TimeoutSeconds, a);
            Check(ownerResult != null, "A received the owner-only successful command result");
            Match ownerSnapshot = WaitForRegex(aLog,
                @"\[client\] inventory-snapshot confirmed seq=1 revision=(?<revision>\d+) digest=(?<digest>[^\s]+)", TimeoutSeconds, a);
            Check(ownerSnapshot != null, "A applied the authoritative snapshot revision after the result");
            if (acceptedOnServer == null || ownerResult == null || ownerSnapshot == null) return 1;
            Check(ownerResult.Groups["revision"].Value == acceptedOnServer.Groups["revision"].Value &&
                  ownerSnapshot.Groups["revision"].Value == acceptedOnServer.Groups["revision"].Value,
                "server, owner result and owner snapshot agree on the committed revision");

            int aStateCountBeforeForge = CountMatches(aLog, @"\[client\] inventory-state role=owner ");
            int bStateCountBeforeForge = CountMatches(bLog, @"\[client\] inventory-state role=forge ");
            WriteAtomic(goB, "go");
            Match forged = WaitForRegex(bLog, @"\[client\] inventory-forge sent target=" + playerA + @" netId=\d+ seq=1 kind=Move", TimeoutSeconds, b);
            Check(forged != null, "B invoked the production inventory RPC on A's visible NetworkObject");
            Match rejected = WaitForRegex(serverLog,
                @"inventory reject=Identity client=" + clientB + @" .*intent identity", TimeoutSeconds, server);
            Check(rejected != null, "server rejected B's foreign-object command by sender identity");
            if (forged == null || rejected == null) return 1;

            Match bAfter = WaitForRegexCount(bLog,
                @"\[client\] inventory-state role=forge digest=(?<digest>[^\s]+) resultEvents=0 confirmed=False", bStateCountBeforeForge + 1, TimeoutSeconds, b);
            Check(bAfter != null, "B received no result while its own inventory remained isolated");
            if (bAfter != null)
                Check(bAfter.Groups["digest"].Value == bBaseline.Groups["digest"].Value,
                    "B's own inventory revision and stack identities were unchanged across A's command and B's forgery");

            Match aAfterForge = WaitForRegexCount(aLog,
                @"\[client\] inventory-state role=owner digest=(?<digest>[^\s]+) resultEvents=1 confirmed=True", aStateCountBeforeForge + 1, TimeoutSeconds, a);
            Check(aAfterForge != null, "A continued observing its owner state after the foreign attempt");
            if (aAfterForge != null)
                Check(aAfterForge.Groups["digest"].Value == ownerSnapshot.Groups["digest"].Value,
                    "B's foreign command caused no additional mutation to A after the accepted move");
            Check(CountMatches(aLog, @"\[client\] inventory-result ") == 1,
                "A received exactly one result, for its own accepted command");
            Check(CountMatches(bLog, @"\[client\] inventory-result ") == 0,
                "B received no owner-only result for either A's command or the rejected forgery");
            Check(CountMatches(serverLog, @"inventory-command sender=" + clientA + @" ") == 1 &&
                  CountMatches(serverLog, @"inventory-command sender=" + clientB + @" ") == 0,
                "only A's command reached the server-side inventory authority");

            WriteAtomic(aQuit, "quit");
            WriteAtomic(bQuit, "quit");
            Check(WaitForExit(a, 20), "client A shut down cleanly");
            Check(WaitForExit(b, 20), "client B shut down cleanly");

            // Keep the server listening until both clients have completed their requested
            // disconnect. Shutting it down concurrently can turn A's local quit into a
            // server-initiated disconnect and bypass the client's clean-exit path.
            WriteAtomic(serverQuit, "quit");
            Check(WaitForExit(server, 20), "server shut down cleanly");
            return _failures == 0 ? 0 : 1;
        }

        private static int ExecuteChestContest()
        {
            string runDir = Path.Combine(_runRoot, "chest-contest");
            string logs = Path.Combine(runDir, "logs");
            Directory.CreateDirectory(logs);
            int port = FreeUdpPort();
            Check(port > 0, "contest loopback UDP port allocated");
            if (port <= 0) return 1;

            string serverQuit = Path.Combine(runDir, "server.quit");
            string aQuit = Path.Combine(runDir, "a.quit");
            string bQuit = Path.Combine(runDir, "b.quit");
            string goBoth = Path.Combine(runDir, "contest.go");
            foreach (string file in new[] { serverQuit, aQuit, bQuit, goBoth })
                if (File.Exists(file)) File.Delete(file);

            string serverLog = Path.Combine(logs, "server.log");
            string aLog = Path.Combine(logs, "client_a.log");
            string bLog = Path.Combine(logs, "client_b.log");
            Process server = Launch(_proofExe, serverLog, "-ember-net server -ember-port " + port +
                " -ember-net-max-players 3 -ember-net-no-enemies -ember-net-inventory-contest-server -ember-net-quit-file " + Q(serverQuit));
            Check(server != null, "contest dedicated server started");
            if (server == null || !WaitForLog(serverLog, "[server] ready scene=", TimeoutSeconds, server))
            { Check(false, "contest server reached ready state"); return 1; }
            Check(true, "contest server reached ready state");

            Process a = Launch(_proofExe, aLog, ContestClientArgs(port, "contest-a", "a", goBoth, aQuit));
            Check(a != null, "contest client A started");
            if (a == null) return 1;
            Match admittedA = WaitForRegex(serverLog, @"admitted client=(?<client>\d+) id=(?<id>\d+) party=\d+ slot=contest-a class=warrior netId=(?<net>\d+)", TimeoutSeconds, server);
            Check(admittedA != null, "contest server admitted A");
            if (admittedA == null) return 1;

            Process b = Launch(_proofExe, bLog, ContestClientArgs(port, "contest-b", "b", goBoth, bQuit));
            Check(b != null, "contest client B started");
            if (b == null) return 1;
            Match admittedB = WaitForRegex(serverLog, @"admitted client=(?<client>\d+) id=(?<id>\d+) party=\d+ slot=contest-b class=warrior netId=(?<net>\d+)", TimeoutSeconds, server);
            Check(admittedB != null, "contest server admitted B");
            if (admittedB == null) return 1;
            string clientA = admittedA.Groups["client"].Value;
            string playerA = admittedA.Groups["id"].Value;
            string clientB = admittedB.Groups["client"].Value;
            string playerB = admittedB.Groups["id"].Value;
            Check(clientA != clientB && playerA != playerB, "contest clients and actors are distinct");

            Match fixture = WaitForRegex(serverLog,
                @"chest-contest fixture chest=(?<chest>[A-Za-z0-9_-]+) uid=(?<uid>[0-9a-f]{32}) playerA=" + playerA +
                @" playerB=" + playerB + @" clientA=" + clientA + @" clientB=" + clientB +
                @" reach=(?<reach>[0-9.]+) canInteractA=True canInteractB=True distanceA=(?<distanceA>[0-9.]+) distanceB=(?<distanceB>[0-9.]+)", TimeoutSeconds, server);
            Check(fixture != null, "server fixture selected one reachable Supply chest and minted one exact UID");
            if (fixture == null) return 1;
            string chestId = fixture.Groups["chest"].Value;
            string uid = fixture.Groups["uid"].Value;
            double distanceA = double.Parse(fixture.Groups["distanceA"].Value, System.Globalization.CultureInfo.InvariantCulture);
            double distanceB = double.Parse(fixture.Groups["distanceB"].Value, System.Globalization.CultureInfo.InvariantCulture);
            double reach = double.Parse(fixture.Groups["reach"].Value, System.Globalization.CultureInfo.InvariantCulture);
            Check(distanceA <= reach && distanceB <= reach, "both fixture actors are inside the configured authority reach");

            string chestPattern = Regex.Escape(chestId);
            Match readyA = WaitForRegex(aLog, @"\[client\] chest-contest-ready role=a chest=" + chestPattern + @" canInteract=True remaining=1", TimeoutSeconds, a);
            Match readyB = WaitForRegex(bLog, @"\[client\] chest-contest-ready role=b chest=" + chestPattern + @" canInteract=True remaining=1", TimeoutSeconds, b);
            Check(readyA != null && readyB != null, "both clients see the same replicated chest and pass local reach checks");
            if (readyA == null || readyB == null) return 1;

            WriteAtomic(goBoth, chestId + "|" + uid);
            Match sentA = WaitForRegex(aLog, @"\[client\] chest-contest-sent role=a chest=" + chestPattern + @" uid=" + uid + @" queued=True localReach=True", TimeoutSeconds, a);
            Match sentB = WaitForRegex(bLog, @"\[client\] chest-contest-sent role=b chest=" + chestPattern + @" uid=" + uid + @" queued=True localReach=True", TimeoutSeconds, b);
            Check(sentA != null && sentB != null, "both clients concurrently sent the production chest-open intent RPC");
            if (sentA == null || sentB == null) return 1;

            string intentPattern = @"chest-intent sender=(?<sender>\d+) player=(?<player>\d+) chest=" + chestPattern +
                                   @" accepted=(?<accepted>True|False)(?<tail>[^\r\n]*)";
            WaitForRegexCount(serverLog, intentPattern, 2, TimeoutSeconds, server);
            MatchCollection intents = Regex.Matches(ReadShared(serverLog), intentPattern);
            Check(intents.Count == 2, "server processed exactly two contested chest RPCs through production validation");
            if (intents.Count < 2) return 1;
            Check(intents.Cast<Match>().All(m => m.Groups["accepted"].Value == "True"), "both players may inspect the same chest without claiming its items");
            Match readyLootA = WaitForRegex(aLog, @"chest-loot-ready role=a container=(?<id>\d+) revision=0 entries=1", TimeoutSeconds, a);
            Match readyLootB = WaitForRegex(bLog, @"chest-loot-ready role=b container=(?<id>\d+) revision=0 entries=1", TimeoutSeconds, b);
            Check(readyLootA != null && readyLootB != null, "both owners received unopened contents with the exact original item still present");
            if (readyLootA == null || readyLootB == null) return 1;
            Check(readyLootA.Groups["id"].Value == readyLootB.Groups["id"].Value, "reopening shares one container rather than rolling a second copy");
            WriteAtomic(goBoth, "take");
            Match resultA = WaitForRegex(aLog, @"chest-loot-result role=a accepted=(?<accepted>True|False) reason=(?<reason>[^\s]+)", TimeoutSeconds, a);
            Match resultB = WaitForRegex(bLog, @"chest-loot-result role=b accepted=(?<accepted>True|False) reason=(?<reason>[^\s]+)", TimeoutSeconds, b);
            Check(resultA != null && resultB != null, "both competing transfers receive a result");
            if (resultA == null || resultB == null) return 1;
            bool wonA = resultA.Groups["accepted"].Value == "True";
            bool wonB = resultB.Groups["accepted"].Value == "True";
            Check(wonA != wonB, "exactly one item transfer wins the contested UID");
            Match refused = wonA ? resultB : resultA;
            Check(refused.Groups["reason"].Value == "Corpse_is_unavailable" || refused.Groups["reason"].Value.Contains("changed"), "loser is refused because the item was taken, not because of arbitrary distance or LOS");
            string winningPlayer = wonA ? playerA : playerB;

            string stateA = @"\[client\] chest-contest-state role=a chest=" + chestPattern + @" uid=" + uid +
                            @" held=(?<held>True|False) rolled=True opened=True remaining=0 result=True";
            string stateB = @"\[client\] chest-contest-state role=b chest=" + chestPattern + @" uid=" + uid +
                            @" held=(?<held>True|False) rolled=True opened=True remaining=0 result=True";
            Match ownerStateA = WaitForRegex(aLog, stateA, TimeoutSeconds, a);
            Match ownerStateB = WaitForRegex(bLog, stateB, TimeoutSeconds, b);
            Check(ownerStateA != null && ownerStateB != null, "both clients converged on rolled/opened/empty chest state");
            if (ownerStateA == null || ownerStateB == null) return 1;
            bool aOwns = ownerStateA.Groups["held"].Value == "True";
            bool bOwns = ownerStateB.Groups["held"].Value == "True";
            Check(aOwns != bOwns, "the concrete UID is present on exactly one owner's authoritative inventory/equipment snapshot");
            Check((winningPlayer == playerA && aOwns) || (winningPlayer == playerB && bOwns),
                "the winner owns the exact UID observed in the server fixture");

            WriteAtomic(aQuit, "quit");
            WriteAtomic(bQuit, "quit");
            Check(WaitForExit(a, 20), "contest client A shut down cleanly");
            Check(WaitForExit(b, 20), "contest client B shut down cleanly");
            WriteAtomic(serverQuit, "quit");
            Check(WaitForExit(server, 20), "contest server shut down cleanly");
            return _failures == 0 ? 0 : 1;
        }

        private static string ContestClientArgs(int port, string slot, string role, string startFile, string quitFile) =>
            "-ember-net client -ember-address 127.0.0.1 -ember-port " + port + " -ember-slot " + slot +
            " -ember-class warrior -ember-net-inventory-contest-role " + role + " -ember-net-script-start-file " + Q(startFile) +
            " -ember-net-quit-file " + Q(quitFile);

        private static int ExecuteCorpseProof()
        {
            string runDir = Path.Combine(_runRoot, "corpse-proof");
            string logs = Path.Combine(runDir, "logs");
            Directory.CreateDirectory(logs);
            int port = FreeUdpPort();
            Check(port > 0, "corpse-proof loopback UDP port allocated");
            if (port <= 0) return 1;

            string serverQuit = Path.Combine(runDir, "server.quit");
            string aQuit = Path.Combine(runDir, "a.quit");
            string bQuit = Path.Combine(runDir, "b.quit");
            string go = Path.Combine(runDir, "stage.txt");
            string action = Path.Combine(runDir, "server-action.txt");
            foreach (string file in new[] { serverQuit, aQuit, bQuit, go, action })
                if (File.Exists(file)) File.Delete(file);

            string serverLog = Path.Combine(logs, "server.log");
            string aLog = Path.Combine(logs, "client_a.log");
            string bLog = Path.Combine(logs, "client_b.log");
            Process server = Launch(_proofExe, serverLog, "-ember-net server -ember-port " + port +
                " -ember-net-max-players 3 -ember-net-party-size 2 -ember-net-no-enemies -ember-net-corpse-proof-server" +
                " -ember-net-corpse-proof-action-file " + Q(action) + " -ember-net-quit-file " + Q(serverQuit));
            Check(server != null, "corpse-proof dedicated server started");
            if (server == null || !WaitForLog(serverLog, "[server] ready scene=", TimeoutSeconds, server))
            { Check(false, "corpse-proof server reached ready state"); return 1; }
            Check(true, "corpse-proof server reached ready state");

            Process a = Launch(_proofExe, aLog, CorpseClientArgs(port, "corpse-a", "a", go, aQuit));
            Check(a != null, "corpse-proof client A started");
            if (a == null) return 1;
            Match admittedA = WaitForRegex(serverLog, @"admitted client=(?<client>\d+) id=(?<id>\d+) party=\d+ slot=corpse-a class=warrior netId=\d+", TimeoutSeconds, server);
            Check(admittedA != null, "corpse-proof server admitted A");
            if (admittedA == null) return 1;

            Process b = Launch(_proofExe, bLog, CorpseClientArgs(port, "corpse-b", "b", go, bQuit));
            Check(b != null, "corpse-proof client B started");
            if (b == null) return 1;
            Match admittedB = WaitForRegex(serverLog, @"admitted client=(?<client>\d+) id=(?<id>\d+) party=\d+ slot=corpse-b class=warrior netId=\d+", TimeoutSeconds, server);
            Check(admittedB != null, "corpse-proof server admitted B");
            if (admittedB == null) return 1;
            string clientA = admittedA.Groups["client"].Value, playerA = admittedA.Groups["id"].Value;
            string clientB = admittedB.Groups["client"].Value, playerB = admittedB.Groups["id"].Value;
            Check(clientA != clientB && playerA != playerB, "corpse-proof transport and player identities are distinct");

            Match fixture = WaitForRegex(serverLog,
                @"corpse-proof fixture id=(?<corpse>\d+) uid=(?<uid>[0-9a-f]{32}) entry=(?<entry>\d+) revision=(?<revision>\d+)" +
                @" playerA=" + playerA + @" clientA=" + clientA + @" playerB=" + playerB + @" clientB=" + clientB +
                @" reach=(?<reach>[0-9.]+) canA=True canB=False distanceA=(?<distanceA>[0-9.]+) distanceB=(?<distanceB>[0-9.]+) stage=far",
                TimeoutSeconds, server);
            Check(fixture != null, "dev-only server registered a unique UID corpse model with A in range and B initially distant");
            if (fixture == null) return 1;
            string corpseId = fixture.Groups["corpse"].Value, uid = fixture.Groups["uid"].Value, entryId = fixture.Groups["entry"].Value;
            string revision = fixture.Groups["revision"].Value;
            double reach = double.Parse(fixture.Groups["reach"].Value, System.Globalization.CultureInfo.InvariantCulture);
            double farDistance = double.Parse(fixture.Groups["distanceB"].Value, System.Globalization.CultureInfo.InvariantCulture);
            Check(farDistance > reach, "fixture places B outside server interaction reach before any content is sent");

            WriteAtomic(go, "foreign|" + corpseId + "|" + entryId + "|" + uid + "|" + revision);
            Match forged = WaitForRegex(bLog, @"\[client\] corpse-proof-forge role=b target=" + playerA + @" corpse=" + corpseId + @" .*seq=1", TimeoutSeconds, b);
            Check(forged != null, "B invoked the real corpse transfer RPC on A's player NetworkObject");
            Match identityReject = WaitForRegex(serverLog,
                @"inventory reject=Identity client=" + clientB + @" .*corpse transfer identity", TimeoutSeconds, server);
            Check(identityReject != null, "server rejected foreign corpse transfer by NGO sender/object ownership");
            if (forged == null || identityReject == null) return 1;

            WriteAtomic(go, "far|" + corpseId + "|" + entryId + "|" + uid + "|" + revision);
            Match farDenied = WaitForRegex(bLog,
                @"\[client\] corpse-proof-result role=b corpse=" + corpseId + @" seq=1 accepted=False .*reason=Too_far_away states=0", TimeoutSeconds, b);
            Check(farDenied != null, "real owner RPC refused B outside reach and leaked no corpse state");
            Check(CountMatches(bLog, @"\[client\] corpse-proof-state role=b corpse=" + corpseId + @" ") == 0,
                "B received no content snapshot while outside reach");
            if (farDenied == null) return 1;
            Thread.Sleep(350);

            WriteAtomic(action, "blocked");
            Match blockedStage = WaitForRegex(serverLog,
                @"corpse-proof stage=blocked playerB=" + playerB + @" canInteract=False distance=(?<distance>[0-9.]+) lineOfSight=False reason=No_line_of_sight", TimeoutSeconds, server);
            Check(blockedStage != null, "dev fixture puts B inside reach behind a level-layer LOS blocker");
            if (blockedStage == null) return 1;
            double blockedDistance = double.Parse(blockedStage.Groups["distance"].Value, System.Globalization.CultureInfo.InvariantCulture);
            Check(blockedDistance <= reach, "LOS refusal is isolated from the range check");

            WriteAtomic(go, "blocked|" + corpseId + "|" + entryId + "|" + uid + "|" + revision);
            Match losDenied = WaitForRegex(bLog,
                @"\[client\] corpse-proof-result role=b corpse=" + corpseId + @" seq=2 accepted=False .*reason=No_line_of_sight states=0", TimeoutSeconds, b);
            Check(losDenied != null, "real owner RPC refused occluded B and leaked no corpse state");
            Check(CountMatches(bLog, @"\[client\] corpse-proof-state role=b corpse=" + corpseId + @" ") == 0,
                "B received no content snapshot while line of sight was blocked");
            if (losDenied == null) return 1;
            Thread.Sleep(350);

            WriteAtomic(action, "near");
            Match nearStage = WaitForRegex(serverLog,
                @"corpse-proof stage=near playerB=" + playerB + @" canInteract=True distance=(?<distance>[0-9.]+) lineOfSight=True reason=none", TimeoutSeconds, server);
            Check(nearStage != null, "same production server validator admits B after blocker removal and near placement");
            if (nearStage == null) return 1;
            double nearDistance = double.Parse(nearStage.Groups["distance"].Value, System.Globalization.CultureInfo.InvariantCulture);
            Check(nearDistance <= reach, "authorized B is inside configured corpse reach");

            WriteAtomic(go, "near|" + corpseId + "|" + entryId + "|" + uid + "|" + revision);
            string rowPattern = @"\[client\] corpse-proof-state role=(?<role>[ab]) corpse=" + corpseId +
                                @" revision=" + revision + @" entries=1 entry=" + entryId + @" uid=" + uid + @" count=\d+";
            Match stateA = WaitForRegex(aLog, rowPattern.Replace("role=(?<role>[ab])", "role=a"), TimeoutSeconds, a);
            Match stateB = WaitForRegex(bLog, rowPattern.Replace("role=(?<role>[ab])", "role=b"), TimeoutSeconds, b);
            Check(stateA != null && stateB != null, "both authorized owners received the same server-authored corpse revision and exact UID row");
            if (stateA == null || stateB == null) return 1;
            Match sentA = WaitForRegex(aLog, @"\[client\] corpse-proof-state-sent role=a stage=near corpse=" + corpseId + @" seq=\d+ queued=True", TimeoutSeconds, a);
            Match sentB = WaitForRegex(bLog, @"\[client\] corpse-proof-state-sent role=b stage=near corpse=" + corpseId + @" seq=\d+ queued=True", TimeoutSeconds, b);
            Check(sentA != null && sentB != null, "both clients requested state through production owner RPCs after the fixture was reachable");
            if (sentA == null || sentB == null) return 1;

            WriteAtomic(go, "transfer|" + corpseId + "|" + entryId + "|" + uid + "|" + revision);
            Match transferA = WaitForRegex(aLog, @"\[client\] corpse-proof-transfer-sent role=a corpse=" + corpseId + @" entry=" + entryId + @" uid=" + uid + @" .*queued=True", TimeoutSeconds, a);
            Match transferB = WaitForRegex(bLog, @"\[client\] corpse-proof-transfer-sent role=b corpse=" + corpseId + @" entry=" + entryId + @" uid=" + uid + @" .*queued=True", TimeoutSeconds, b);
            Check(transferA != null && transferB != null, "both clients concurrently sent the real per-entry transfer intent");
            if (transferA == null || transferB == null) return 1;
            Match committed = WaitForRegex(serverLog,
                @"corpse transfer sender=(?<winner>\d+) corpse=" + corpseId + @" entry=" + entryId + @" amount=1 accepted=true corpseRevision=(?<revision>\d+) bagRevision=(?<bagRevision>\d+)", TimeoutSeconds, server);
            Check(committed != null, "server committed exactly one real corpse transfer");
            if (committed == null) return 1;
            string winner = committed.Groups["winner"].Value;
            Thread.Sleep(350);
            Check(CountMatches(serverLog, @"corpse transfer sender=\d+ corpse=" + corpseId + @" entry=" + entryId + @" amount=1 accepted=true") == 1,
                "contending RPCs cannot commit the same gear UID twice");

            Match finalA = WaitForRegex(aLog,
                @"\[client\] corpse-proof-final role=a corpse=" + corpseId + @" uid=" + uid + @" accepted=(?<accepted>True|False) held=(?<held>True|False) bagRevision=\d+ presenceRemoved=(?<removed>True|False) states=\d+", TimeoutSeconds, a);
            Match finalB = WaitForRegex(bLog,
                @"\[client\] corpse-proof-final role=b corpse=" + corpseId + @" uid=" + uid + @" accepted=(?<accepted>True|False) held=(?<held>True|False) bagRevision=\d+ presenceRemoved=(?<removed>True|False) states=\d+", TimeoutSeconds, b);
            Check(finalA != null && finalB != null, "both clients observed final authoritative inventory/presence state");
            if (finalA == null || finalB == null) return 1;
            bool acceptedA = finalA.Groups["accepted"].Value == "True", acceptedB = finalB.Groups["accepted"].Value == "True";
            bool holdsA = finalA.Groups["held"].Value == "True", holdsB = finalB.Groups["held"].Value == "True";
            Check(acceptedA != acceptedB && holdsA != holdsB, "exactly the server's successful owner holds the minted UID");
            Check((winner == clientA && acceptedA && holdsA && !holdsB) || (winner == clientB && acceptedB && holdsB && !holdsA),
                "server winner, owner result and final exact UID custody agree");
            Check(finalA.Groups["removed"].Value == "True" && finalB.Groups["removed"].Value == "True",
                "both clients received corpse presence removal after its final item transferred");

            WriteAtomic(aQuit, "quit");
            WriteAtomic(bQuit, "quit");
            Check(WaitForExit(a, 20), "corpse-proof client A shut down cleanly");
            Check(WaitForExit(b, 20), "corpse-proof client B shut down cleanly");
            WriteAtomic(serverQuit, "quit");
            Check(WaitForExit(server, 20), "corpse-proof server shut down cleanly");
            return _failures == 0 ? 0 : 1;
        }

        private static int ExecuteCorpseHostProof()
        {
            string runDir = Path.Combine(_runRoot, "corpse-host-proof");
            string logs = Path.Combine(runDir, "logs");
            Directory.CreateDirectory(logs);
            int port = FreeUdpPort();
            Check(port > 0, "corpse-host-proof loopback UDP port allocated");
            if (port <= 0) return 1;

            string hostQuit = Path.Combine(runDir, "host.quit");
            string clientQuit = Path.Combine(runDir, "client.quit");
            string go = Path.Combine(runDir, "client-action.txt");
            string hostAction = Path.Combine(runDir, "host-action.txt");
            foreach (string file in new[] { hostQuit, clientQuit, go, hostAction })
                if (File.Exists(file)) File.Delete(file);

            string hostLog = Path.Combine(logs, "host.log");
            string clientLog = Path.Combine(logs, "client_b.log");
            Process host = Launch(_proofExe, hostLog, "-ember-net host -ember-port " + port +
                " -ember-slot corpse-a -ember-class warrior -ember-net-max-players 3 -ember-net-party-size 2" +
                " -ember-net-no-enemies -ember-net-corpse-proof-server -ember-net-corpse-proof-action-file " +
                Q(hostAction) + " -ember-net-quit-file " + Q(hostQuit));
            Check(host != null, "listen-host corpse-proof process started");
            if (host == null || !WaitForLog(hostLog, "[host] ready scene=", TimeoutSeconds, host))
            { Check(false, "listen host reached ready state"); return 1; }
            Check(true, "listen host reached ready state");

            Process client = Launch(_proofExe, clientLog, CorpseClientArgs(port, "corpse-b", "b", go, clientQuit));
            Check(client != null, "corpse-host-proof remote client started");
            if (client == null) return 1;
            Match hostAdmitted = WaitForRegex(hostLog,
                @"admitted client=(?<client>\d+) id=(?<id>\d+) party=\d+ slot=corpse-a class=warrior netId=\d+", TimeoutSeconds, host);
            Match clientAdmitted = WaitForRegex(hostLog,
                @"admitted client=(?<client>\d+) id=(?<id>\d+) party=\d+ slot=corpse-b class=warrior netId=\d+", TimeoutSeconds, host);
            Check(hostAdmitted != null && clientAdmitted != null, "listen host and remote player were admitted into the proof run");
            if (hostAdmitted == null || clientAdmitted == null) return 1;
            string hostClientId = hostAdmitted.Groups["client"].Value;
            string hostPlayerId = hostAdmitted.Groups["id"].Value;
            string remoteClientId = clientAdmitted.Groups["client"].Value;
            string remotePlayerId = clientAdmitted.Groups["id"].Value;
            Check(hostClientId == "0" && remoteClientId != hostClientId && remotePlayerId != hostPlayerId,
                "corpse-proof actor A is the server-owned listen-host player and B is a distinct remote owner");

            Match fixture = WaitForRegex(hostLog,
                @"corpse-proof fixture id=(?<corpse>\d+) uid=(?<uid>[0-9a-f]{32}) entry=(?<entry>\d+) revision=(?<revision>\d+)" +
                @" playerA=" + hostPlayerId + @" clientA=" + hostClientId + @" playerB=" + remotePlayerId + @" clientB=" + remoteClientId +
                @" reach=(?<reach>[0-9.]+) canA=True canB=False distanceA=(?<distanceA>[0-9.]+) distanceB=(?<distanceB>[0-9.]+) stage=far",
                TimeoutSeconds, host);
            Check(fixture != null, "listen-host fixture registered one server-owned UID row with B initially out of range");
            if (fixture == null) return 1;
            string corpseId = fixture.Groups["corpse"].Value;
            string entryId = fixture.Groups["entry"].Value;
            string uid = fixture.Groups["uid"].Value;
            Check(WaitForLog(hostLog, "corpse-proof-host presenter=ready role=HostPlayer", TimeoutSeconds, host),
                "listen-host owner has the real CorpseLootWorldPresenter direct-server path");

            WriteAtomic(go, "foreign|" + corpseId + "|" + entryId + "|" + uid + "|" + fixture.Groups["revision"].Value);
            Match forged = WaitForRegex(clientLog,
                @"\[client\] corpse-proof-forge role=b target=" + hostPlayerId + @" corpse=" + corpseId + @" .*seq=1", TimeoutSeconds, client);
            Match identityReject = WaitForRegex(hostLog,
                @"inventory reject=Identity client=" + remoteClientId + @" .*corpse transfer identity", TimeoutSeconds, host);
            Check(forged != null && identityReject != null,
                "remote client cannot submit a corpse transfer on the listen-host-owned player object");

            WriteAtomic(go, "far|" + corpseId + "|" + entryId + "|" + uid);
            Match remoteDenied = WaitForRegex(clientLog,
                @"\[client\] corpse-proof-result role=b corpse=" + corpseId + @" seq=1 accepted=False .*reason=Too_far_away states=0", TimeoutSeconds, client);
            Check(remoteDenied != null, "remote owner outside reach receives refusal without corpse contents");
            Check(CountMatches(clientLog, @"\[client\] corpse-proof-state role=b corpse=" + corpseId + @" ") == 0,
                "remote range refusal disclosed no corpse state");

            WriteAtomic(hostAction, "host-far");
            Match hostFar = WaitForRegex(hostLog,
                @"corpse-proof-host stage=far canInteract=False .*reason=Too_far_away", TimeoutSeconds, host);
            Match hostFarDenied = WaitForRegex(hostLog,
                @"corpse-proof-host result corpse=" + corpseId + @" seq=1 accepted=False .*held=False reason=Too_far_away", TimeoutSeconds, host);
            Check(hostFar != null && hostFarDenied != null, "listen-host direct request is refused by server range validation");
            Check(CountMatches(hostLog, @"corpse-proof-host state corpse=" + corpseId + @" ") == 0,
                "listen-host range refusal delivered no corpse contents");

            Thread.Sleep(350);
            WriteAtomic(hostAction, "host-blocked");
            Match hostBlocked = WaitForRegex(hostLog,
                @"corpse-proof-host stage=blocked canInteract=False .*reason=No_line_of_sight", TimeoutSeconds, host);
            Match hostBlockedDenied = WaitForRegex(hostLog,
                @"corpse-proof-host result corpse=" + corpseId + @" seq=2 accepted=False .*held=False reason=No_line_of_sight", TimeoutSeconds, host);
            Check(hostBlocked != null && hostBlockedDenied != null, "listen-host direct request is refused by server LOS validation");
            Check(CountMatches(hostLog, @"corpse-proof-host state corpse=" + corpseId + @" ") == 0,
                "listen-host LOS refusal delivered no corpse contents");

            Thread.Sleep(350);
            WriteAtomic(hostAction, "host-open");
            Match hostOpen = WaitForRegex(hostLog,
                @"corpse-proof-host stage=open canInteract=True .*reason=none", TimeoutSeconds, host);
            Match hostState = WaitForRegex(hostLog,
                @"corpse-proof-host state corpse=" + corpseId + @" revision=(?<revision>\d+) entries=1 entry=" + entryId + @" uid=" + uid,
                TimeoutSeconds, host);
            Match hostOpenResult = WaitForRegex(hostLog,
                @"corpse-proof-host result corpse=" + corpseId + @" seq=3 accepted=True .*reason=none", TimeoutSeconds, host);
            Check(hostOpen != null && hostState != null && hostOpenResult != null,
                "host panel's state-request path delivered the exact server-owned row and accepted result");
            if (hostState == null) return 1;

            Thread.Sleep(350);
            WriteAtomic(hostAction, "host-stale");
            Check(WaitForRegex(hostLog,
                @"corpse-proof-host request=transfer stage=stale corpse=" + corpseId + @" entry=" + entryId + @" uid=" + uid + @" .*queued=True",
                TimeoutSeconds, host) != null, "listen host sent a stale-revision transfer through its presenter");
            Match stale = WaitForRegex(hostLog,
                @"corpse-proof-host result corpse=" + corpseId + @" seq=4 accepted=False .*held=False reason=recipient_bag_changed;_refresh_the_inventory", TimeoutSeconds, host);
            Check(stale != null, "server rejected stale host bag revision without moving the UID");
            if (stale == null) return 1;

            Thread.Sleep(350);
            WriteAtomic(hostAction, "host-transfer");
            Match hostTransferQueued = WaitForRegex(hostLog,
                @"corpse-proof-host request=transfer stage=commit corpse=" + corpseId + @" entry=" + entryId + @" uid=" + uid + @" .*queued=True",
                TimeoutSeconds, host);
            Match hostCommit = WaitForRegex(hostLog,
                @"corpse transfer sender=" + hostClientId + @" corpse=" + corpseId + @" entry=" + entryId + @" amount=1 accepted=true corpseRevision=\d+ bagRevision=\d+",
                TimeoutSeconds, host);
            Match hostCommittedResult = WaitForRegex(hostLog,
                @"corpse-proof-host result corpse=" + corpseId + @" seq=5 accepted=True .*held=True reason=none", TimeoutSeconds, host);
            Match hostRemoved = WaitForRegex(hostLog,
                @"corpse-proof-host presence corpse=" + corpseId + @" revision=\d+ removed=True", TimeoutSeconds, host);
            Check(hostTransferQueued != null && hostCommit != null && hostCommittedResult != null && hostRemoved != null,
                "listen-host server handler transferred the exact UID into its own bag and removed the empty corpse");

            WriteAtomic(clientQuit, "quit");
            Check(WaitForExit(client, 20), "corpse-host-proof remote client shut down cleanly");
            WriteAtomic(hostQuit, "quit");
            Check(WaitForExit(host, 20), "listen host shut down cleanly");
            return _failures == 0 ? 0 : 1;
        }

        private static int ExecuteFloorLootProof()
        {
            string runDir = Path.Combine(_runRoot, "floor-loot-proof");
            string logs = Path.Combine(runDir, "logs");
            Directory.CreateDirectory(logs);
            int port = FreeUdpPort();
            Check(port > 0, "floor-loot-proof loopback UDP port allocated");
            if (port <= 0) return 1;
            string quitServer = Path.Combine(runDir, "server.quit"), quitA = Path.Combine(runDir, "a.quit"),
                quitB = Path.Combine(runDir, "b.quit"), quitLate = Path.Combine(runDir, "late.quit"), action = Path.Combine(runDir, "client-action.txt"),
                stageAction = Path.Combine(runDir, "server-action.txt");
            foreach (string file in new[] { quitServer, quitA, quitB, quitLate, action, stageAction }) if (File.Exists(file)) File.Delete(file);
            string serverLog = Path.Combine(logs, "server.log"), aLog = Path.Combine(logs, "client_a.log"), bLog = Path.Combine(logs, "client_b.log");
            Process server = Launch(_proofExe, serverLog, "-ember-net server -ember-port " + port +
                " -ember-net-max-players 4 -ember-net-no-enemies -ember-net-floor-loot-proof-server -ember-net-floor-loot-proof-action-file " + Q(stageAction) +
                " -ember-net-quit-file " + Q(quitServer));
            Check(server != null && WaitForLog(serverLog, "[server] ready scene=", TimeoutSeconds, server), "floor-loot-proof server ready");
            if (server == null || HasExited(server)) return 1;
            Process a = Launch(_proofExe, aLog, "-ember-net client -ember-address 127.0.0.1 -ember-port " + port +
                " -ember-slot loot-a -ember-class warrior -ember-net-floor-loot-proof-role a -ember-net-floor-loot-proof-action-file " + Q(action) + " -ember-net-quit-file " + Q(quitA));
            Process b = Launch(_proofExe, bLog, "-ember-net client -ember-address 127.0.0.1 -ember-port " + port +
                " -ember-slot loot-b -ember-class warrior -ember-net-floor-loot-proof-role b -ember-net-floor-loot-proof-action-file " + Q(action) + " -ember-net-quit-file " + Q(quitB));
            Check(a != null && b != null, "two independent floor-loot clients started");
            if (a == null || b == null) return 1;
            Match fixture = WaitForRegex(serverLog, @"floor-loot-proof fixture ready item=(?<id>\d+) uid=(?<uid>[0-9a-f]{32}) deadItem=(?<dead>\d+) deadUid=(?<deaduid>[0-9a-f]{32}) playerA=(?<pa>\d+) clientA=(?<ca>\d+) playerB=(?<pb>\d+) clientB=(?<cb>\d+)", TimeoutSeconds, server);
            Check(fixture != null, "server seeded two unique authoritative floor-loot UIDs for admitted owners");
            if (fixture == null) return 1;
            string id = fixture.Groups["id"].Value, uid = fixture.Groups["uid"].Value, deadId = fixture.Groups["dead"].Value, deadUid = fixture.Groups["deaduid"].Value;
            string playerA = fixture.Groups["pa"].Value, playerB = fixture.Groups["pb"].Value;
            Check(fixture.Groups["ca"].Value != fixture.Groups["cb"].Value && uid != deadUid && id != deadId, "contest actors and test items have distinct identities");
            Check(WaitForRegex(aLog, @"\[client\] owner id=" + playerA + @" views=2 owned=1", TimeoutSeconds, a) != null, "client A sees both actors");
            Check(WaitForRegex(bLog, @"\[client\] owner id=" + playerB + @" views=2 owned=1", TimeoutSeconds, b) != null, "client B sees both actors");
            Check(WaitForLog(aLog, "floor-loot-state role=owner", TimeoutSeconds, a) && WaitForLog(bLog, "floor-loot-state role=owner", TimeoutSeconds, b), "both clients received full initial authoritative pickup snapshots");

            WriteAtomic(stageAction, "far|" + id + "|" + uid);
            Check(WaitForLog(serverLog, "floor-loot-proof stage=far", TimeoutSeconds, server), "server staged B outside reach");
            WriteAtomic(action, "far|" + id + "|" + uid);
            Check(WaitForRegex(serverLog, @"floor loot reject=Authority client=" + fixture.Groups["cb"].Value + @" .*reason=Too_far_away", TimeoutSeconds, server) != null, "server rejected far pickup request");
            WriteAtomic(stageAction, "blocked|" + id + "|" + uid);
            Check(WaitForRegex(serverLog, @"floor-loot-proof stage=blocked .*verified=True reason=No_line_of_sight", TimeoutSeconds, server) != null,
                "server verified the production LOS check rejects the staged pickup ray");
            WriteAtomic(action, "blocked|" + id + "|" + uid);
            Check(WaitForRegex(serverLog, @"floor loot reject=Authority client=" + fixture.Groups["cb"].Value + @" .*reason=No_line_of_sight", TimeoutSeconds, server) != null, "server rejected occluded pickup request");
            WriteAtomic(stageAction, "near|" + id + "|" + uid);
            Check(WaitForLog(serverLog, "floor-loot-proof stage=near", TimeoutSeconds, server), "server restored both actors within reach");
            Thread.Sleep(350);
            WriteAtomic(action, "contest|" + id + "|" + uid);
            Match winner = WaitForRegex(serverLog, @"floor loot pickup sender=\d+ id=(?<winner>\d+) item=" + id + @" uid=" + uid + @" accepted=True", TimeoutSeconds, server);
            Match loser = WaitForRegex(serverLog, @"floor loot (?:reject=Unavailable|pickup sender=\d+ id=\d+ item=" + id + @" uid=" + uid + @" accepted=False)", TimeoutSeconds, server);
            Check(winner != null && loser != null, "concurrent owners contend on one UID; exactly one source transaction succeeds");
            if (winner == null) return 1;
            string winnerRole = winner.Groups["winner"].Value == playerA ? "a" : "b";
            WriteAtomic(action, "observe|" + id + "|" + uid + "|" + deadId + "|" + deadUid + "|" + winnerRole);
            Check(WaitForRegex(aLog, @"floor-loot-proof observe role=a item=" + id + @" held=(?<held>True|False) visible=False remaining=True", TimeoutSeconds, a) != null, "client A converged to pickup removal state");
            Check(WaitForRegex(bLog, @"floor-loot-proof observe role=b item=" + id + @" held=(?<held>True|False) visible=False remaining=True", TimeoutSeconds, b) != null, "client B converged to pickup removal state");
            string winnerLog = winnerRole == "a" ? aLog : bLog;
            Check(WaitForRegex(winnerLog, @"floor-loot-proof observe role=" + winnerRole + @" item=" + id + @" held=True visible=False remaining=True", TimeoutSeconds, winnerRole == "a" ? a : b) != null,
                "successful owner's replicated inventory contains the exact contested UID");
            WriteAtomic(action, "replay|" + id + "|" + uid);
            Check(WaitForRegex(serverLog, @"floor loot reject=Replay client=" + fixture.Groups["cb"].Value + @" seq=3", TimeoutSeconds, server) != null, "server rejected replayed sequence");
            WriteAtomic(action, "forge|" + id + "|" + uid);
            Check(WaitForRegex(serverLog, @"floor loot reject=Identity client=" + fixture.Groups["cb"].Value, TimeoutSeconds, server) != null, "server rejected foreign-owner RPC");
            WriteAtomic(stageAction, "dead|" + deadId + "|" + deadUid);
            Check(WaitForLog(serverLog, "floor-loot-proof stage=dead", TimeoutSeconds, server), "server killed B through normal damage path");
            WriteAtomic(action, "dead|" + deadId + "|" + deadUid);
            Check(WaitForRegex(serverLog, @"floor loot reject=Authority client=" + fixture.Groups["cb"].Value + @" .*reason=The_dead_cannot_interact", TimeoutSeconds, server) != null, "server rejects dead player's pickup request");

            string lateLog = Path.Combine(logs, "client_late.log");
            Process late = Launch(_proofExe, lateLog, "-ember-net client -ember-address 127.0.0.1 -ember-port " + port +
                " -ember-slot loot-late -ember-class warrior -ember-net-floor-loot-proof-role late -ember-net-floor-loot-proof-action-file " + Q(action) + " -ember-net-quit-file " + Q(quitLate));
            Check(late != null, "late-joining client started after contested pickup removal");
            if (late == null) return 1;
            WriteAtomic(action, "late|" + id + "|" + uid + "|" + deadId + "|" + deadUid);
            Check(WaitForRegex(lateLog, @"floor-loot-proof late revision=\d+ removed=True remaining=True removedId=" + id + @" remainingId=" + deadId, TimeoutSeconds, late) != null,
                "late join snapshot excludes collected pickup and includes uncollected pickup");
            WriteAtomic(quitLate, "quit"); WriteAtomic(quitA, "quit"); WriteAtomic(quitB, "quit");
            Check(WaitForExit(late, 20) && WaitForExit(a, 20) && WaitForExit(b, 20), "floor-loot clients shut down cleanly");
            WriteAtomic(quitServer, "quit");
            Check(WaitForExit(server, 20), "floor-loot server shut down cleanly");
            return _failures == 0 ? 0 : 1;
        }

        private static int ExecuteInventoryDropProof()
        {
            string runDir = Path.Combine(_runRoot, "inventory-drop-proof");
            string logs = Path.Combine(runDir, "logs");
            Directory.CreateDirectory(logs);
            int port = FreeUdpPort();
            Check(port > 0, "inventory-drop-proof loopback UDP port allocated");
            if (port <= 0) return 1;
            string serverQuit = Path.Combine(runDir, "server.quit"), ownerQuit = Path.Combine(runDir, "owner.quit"),
                peerQuit = Path.Combine(runDir, "peer.quit"), lateQuit = Path.Combine(runDir, "late.quit"),
                action = Path.Combine(runDir, "client-action.txt"), expected = Path.Combine(runDir, "expected-item.json");
            foreach (string file in new[] { serverQuit, ownerQuit, peerQuit, lateQuit, action, expected })
                if (File.Exists(file)) File.Delete(file);
            string serverLog = Path.Combine(logs, "server.log"), ownerLog = Path.Combine(logs, "owner.log"),
                peerLog = Path.Combine(logs, "peer.log"), lateLog = Path.Combine(logs, "late.log");
            Process server = Launch(_proofExe, serverLog, "-ember-net server -ember-port " + port +
                " -ember-net-max-players 4 -ember-net-no-enemies -ember-net-inventory-drop-proof-server" +
                " -ember-net-inventory-drop-proof-action-file " + Q(action) +
                " -ember-net-inventory-drop-proof-expected-file " + Q(expected) + " -ember-net-quit-file " + Q(serverQuit));
            Check(server != null && WaitForLog(serverLog, "[server] ready scene=", TimeoutSeconds, server), "inventory-drop-proof server ready");
            if (server == null || HasExited(server)) return 1;
            string clientCommon = "-ember-net client -ember-address 127.0.0.1 -ember-port " + port +
                " -ember-class warrior -ember-net-inventory-drop-proof-action-file " + Q(action) +
                " -ember-net-inventory-drop-proof-expected-file " + Q(expected);
            Process owner = Launch(_proofExe, ownerLog, clientCommon + " -ember-slot drop-a -ember-net-inventory-drop-proof-role owner -ember-net-quit-file " + Q(ownerQuit));
            Process peer = Launch(_proofExe, peerLog, clientCommon + " -ember-slot drop-b -ember-net-inventory-drop-proof-role peer -ember-net-quit-file " + Q(peerQuit));
            Check(owner != null && peer != null, "independent equipment-drop owner and peer clients started");
            if (owner == null || peer == null) return 1;
            Match fixture = WaitForRegex(serverLog,
                @"inventory-drop-proof fixture ready uid=(?<uid>[0-9a-f]{32}) item=(?<item>\S+) rarity=(?<rarity>\d+) affixes=(?<affixes>\d+) slot=Chest owner=(?<owner>\d+) ownerClient=(?<ownerClient>\d+) peer=(?<peer>\d+) peerClient=(?<peerClient>\d+) revision=(?<revision>\d+)",
                TimeoutSeconds, server);
            Check(fixture != null, "server rolled and equipped one affixed exact instance on the owner rig");
            if (fixture == null) return 1;
            string uid = fixture.Groups["uid"].Value;
            Check(fixture.Groups["affixes"].Value != "0" && fixture.Groups["ownerClient"].Value != fixture.Groups["peerClient"].Value,
                "fixture item has affixes and owner/peer are distinct admitted clients");
            Check(WaitForLog(ownerLog, "[client] owner id=" + fixture.Groups["owner"].Value, TimeoutSeconds, owner) &&
                  WaitForLog(peerLog, "[client] owner id=" + fixture.Groups["peer"].Value, TimeoutSeconds, peer),
                "owner and peer have settled spawned rigs");
            WriteAtomic(action, "drop|" + (int)EquipmentSlot.Chest + "|" + uid + "|" + fixture.Groups["revision"].Value);
            Check(WaitForRegex(ownerLog, @"inventory-drop-proof sent uid=" + uid + @" slot=Chest queued=True", TimeoutSeconds, owner) != null,
                "actual owner sent a normal equipment Drop intent for the fixture UID");
            Check(WaitForRegex(serverLog, @"inventory-drop-command sender=" + fixture.Groups["ownerClient"].Value +
                @" id=" + fixture.Groups["owner"].Value + @" uid=" + uid + @" slot=Chest accepted=False changed=False revision=" + fixture.Groups["revision"].Value,
                TimeoutSeconds, server) != null, "injected publication failure refuses the normal owner Drop without a revision change");
            Check(WaitForLog(serverLog, "inventory-drop-proof rollback uid=" + uid + " exactInstance=True floorCount=0 revision=" + fixture.Groups["revision"].Value,
                TimeoutSeconds, server), "server rollback retains the exact equipped UID and affixes with no ground duplicate");
            WriteAtomic(action, "verify-retained|" + uid + "|" + fixture.Groups["revision"].Value);
            Check(WaitForLog(ownerLog, "inventory-drop-proof retained role=owner uid=" + uid + " floorCount=0 exactOwner=True", TimeoutSeconds, owner),
                "owner converges to the exact unchanged equipment snapshot after refusal");
            Check(WaitForLog(peerLog, "inventory-drop-proof retained role=peer uid=" + uid + " floorCount=0 exactOwner=False", TimeoutSeconds, peer),
                "peer sees no duplicate floor item after failed publication");
            WriteAtomic(action, "drop|" + (int)EquipmentSlot.Chest + "|" + uid + "|" + fixture.Groups["revision"].Value);
            Check(WaitForRegex(serverLog, @"inventory-command sender=" + fixture.Groups["ownerClient"].Value +
                @" id=" + fixture.Groups["owner"].Value + @" seq=\d+ accepted=True changed=True revision=\d+", TimeoutSeconds, server) != null,
                "server committed the owner's equipment Drop command");
            Check(WaitForRegex(serverLog, @"inventory-drop-command sender=" + fixture.Groups["ownerClient"].Value +
                @" id=" + fixture.Groups["owner"].Value + @" uid=" + uid + @" slot=Chest accepted=True changed=True revision=\d+",
                TimeoutSeconds, server) != null,
                "server Drop-specific result marker ties acceptance to the exact UID and equipment slot");
            Check(WaitForRegex(serverLog, @"inventory-drop-proof committed uid=" + uid + @" pickupId=\d+ uidCount=1 exactInstance=True", TimeoutSeconds, server) != null,
                "server ground registry contains the exact instance once after source removal");
            WriteAtomic(action, "verify|" + uid);
            string verifiedPattern = @"inventory-drop-proof verified role=(owner|peer) uid=" + uid + @" item=" +
                Regex.Escape(fixture.Groups["item"].Value) + @" rarity=" + fixture.Groups["rarity"].Value +
                @" affixes=" + fixture.Groups["affixes"].Value + @" uidCount=1";
            Check(WaitForRegex(ownerLog, verifiedPattern.Replace("(owner|peer)", "owner"), TimeoutSeconds, owner) != null,
                "owner ground snapshot matches exact UID and every rolled affix once");
            Check(WaitForRegex(peerLog, verifiedPattern.Replace("(owner|peer)", "peer"), TimeoutSeconds, peer) != null,
                "peer ground snapshot matches exact UID and every rolled affix once");

            Process late = Launch(_proofExe, lateLog, clientCommon + " -ember-slot drop-late -ember-net-inventory-drop-proof-role late -ember-net-quit-file " + Q(lateQuit));
            Check(late != null, "late-joining drop-proof client started");
            if (late == null) return 1;
            Check(WaitForRegex(lateLog, @"inventory-drop-proof verified role=late uid=" + uid + @" item=" +
                Regex.Escape(fixture.Groups["item"].Value) + @" rarity=" + fixture.Groups["rarity"].Value +
                @" affixes=" + fixture.Groups["affixes"].Value + @" uidCount=1", TimeoutSeconds, late) != null,
                "late join snapshot contains the exact dropped UID and all affixes exactly once");
            WriteAtomic(ownerQuit, "quit"); WriteAtomic(peerQuit, "quit"); WriteAtomic(lateQuit, "quit");
            Check(WaitForExit(owner, 20) && WaitForExit(peer, 20) && WaitForExit(late, 20), "drop-proof clients shut down cleanly");
            WriteAtomic(serverQuit, "quit");
            Check(WaitForExit(server, 20), "drop-proof server shut down cleanly");
            return _failures == 0 ? 0 : 1;
        }

        private static int ExecuteCorpseDeathProof()
        {
            string runDir = Path.Combine(_runRoot, "corpse-death-proof");
            string logs = Path.Combine(runDir, "logs");
            Directory.CreateDirectory(logs);
            int port = FreeUdpPort();
            Check(port > 0, "corpse-death-proof loopback UDP port allocated");
            if (port <= 0) return 1;
            string serverQuit = Path.Combine(runDir, "server.quit");
            string clientAQuit = Path.Combine(runDir, "a.quit");
            string clientBQuit = Path.Combine(runDir, "b.quit");
            string action = Path.Combine(runDir, "action.txt");
            foreach (string file in new[] { serverQuit, clientAQuit, clientBQuit, action })
                if (File.Exists(file)) File.Delete(file);
            string serverLog = Path.Combine(logs, "server.log");
            Process server = Launch(_proofExe, serverLog, "-ember-net server -ember-port " + port +
                " -ember-net-max-players 3 -ember-net-no-enemies -ember-net-corpse-death-proof-server" +
                " -ember-net-corpse-death-proof-action-file " + Q(action) + " -ember-net-quit-file " + Q(serverQuit));
            Check(server != null, "corpse-death-proof server started");
            if (server == null || !WaitForLog(serverLog, "[server] ready scene=", TimeoutSeconds, server))
            { Check(false, "corpse-death-proof server became ready"); return 1; }
            Check(true, "corpse-death-proof server became ready");
            Process a = Launch(_proofExe, Path.Combine(logs, "client_a.log"),
                "-ember-net client -ember-address 127.0.0.1 -ember-port " + port + " -ember-slot corpse-death-a -ember-class warrior -ember-net-quit-file " + Q(clientAQuit));
            Check(a != null, "corpse-death-proof owner started");
            if (a == null) return 1;
            Process b = Launch(_proofExe, Path.Combine(logs, "client_b.log"),
                "-ember-net client -ember-address 127.0.0.1 -ember-port " + port + " -ember-slot corpse-death-b -ember-class warrior -ember-net-quit-file " + Q(clientBQuit));
            Check(b != null, "corpse-death-proof second client started");
            if (b == null) return 1;
            Match fixture = WaitForRegex(serverLog, @"corpse-death-proof fixture ready player=(?<player>\d+) client=(?<client>\d+) uid=(?<uid>[0-9a-f]{32}) disconnectPlayer=(?<disconnectPlayer>\d+) disconnectClient=(?<disconnectClient>\d+) disconnectUid=(?<disconnectUid>[0-9a-f]{32})", TimeoutSeconds, server);
            Check(fixture != null, "real admitted actors have separate minted UIDs for death and disconnect custody proofs");
            if (fixture == null) return 1;
            string player = fixture.Groups["player"].Value, uid = fixture.Groups["uid"].Value;
            Check(fixture.Groups["client"].Value != fixture.Groups["disconnectClient"].Value && uid != fixture.Groups["disconnectUid"].Value,
                "death and disconnect fixtures use distinct owners and distinct item identities");
            Match admittedDisconnectOwner = WaitForRegex(serverLog,
                @"admitted client=" + fixture.Groups["disconnectClient"].Value + @" id=" + fixture.Groups["disconnectPlayer"].Value +
                @" party=\d+ slot=corpse-death-b class=warrior netId=\d+", TimeoutSeconds, server);
            Check(admittedDisconnectOwner != null, "the actor designated for disconnect is the actual second proof client");
            WriteAtomic(action, "refuse");
            Match refused = WaitForRegex(serverLog,
                @"corpse-death-proof stage=refused player=" + player + @" dead=True uidHeldOnSource=True resultPublished=False corpseFound=False", TimeoutSeconds, server);
            Check(refused != null, "normal lethal damage invoked death hook; failed custody preflight preserved exact UID and withheld result/corpse");
            if (refused == null) return 1;
            WriteAtomic(action, "retry");
            Match committed = WaitForRegex(serverLog,
                @"corpse-death-proof stage=committed player=" + player + @" corpse=\d+ uid=" + uid + @" sourceEmpty=True resultPublished=True", TimeoutSeconds, server);
            Check(committed != null, "retry transferred the same UID exactly once and only then published the death result");

            // Kill the second real client process. Netcode must run its transport disconnect lifecycle; the test never calls
            // NetServer.OnClientDisconnected or any other internal cleanup method directly.
            string disconnectUid = fixture.Groups["disconnectUid"].Value;
            WriteAtomic(action, "disconnect-refuse");
            Check(WaitForLog(serverLog, "corpse-death-proof disconnect stage=refusal-armed", TimeoutSeconds, server),
                "server armed a deterministic custody refusal before transport disconnect");
            bool disconnectClientWasRunning = !b.HasExited;
            Check(disconnectClientWasRunning, "disconnect proof client is still running before the forced transport loss");
            if (disconnectClientWasRunning) b.Kill();
            Check(WaitForTermination(b, 20), "second client process terminated after the forced transport loss");
            Match retained = WaitForRegex(serverLog,
                @"corpse-death-proof disconnect stage=retained player=" + fixture.Groups["disconnectPlayer"].Value +
                @" uid=" + disconnectUid + @" uidHeldOnSource=True spawned=True owner=0", TimeoutSeconds, server);
            Check(retained != null, "failed disconnect custody retained the spawned server rig with its exact UID after owner disconnect");
            if (retained != null)
            {
                WriteAtomic(action, "disconnect-retry");
                Match disconnectCommitted = WaitForRegex(serverLog,
                    @"corpse-death-proof disconnect stage=committed player=" + fixture.Groups["disconnectPlayer"].Value +
                    @" corpse=\d+ uid=" + disconnectUid + @" corpseHeld=True sourceEmpty=True spawned=False", TimeoutSeconds, server);
                Check(disconnectCommitted != null, "disconnect retry moved the retained UID into corpse custody before despawning the rig");
            }
            WriteAtomic(clientAQuit, "quit");
            WriteAtomic(clientBQuit, "quit");
            Check(WaitForExit(a, 20), "corpse-death-proof owner shut down cleanly");
            WriteAtomic(serverQuit, "quit");
            Check(WaitForExit(server, 20), "corpse-death-proof server shut down cleanly");
            return _failures == 0 ? 0 : 1;
        }

        private static string CorpseClientArgs(int port, string slot, string role, string startFile, string quitFile) =>
            "-ember-net client -ember-address 127.0.0.1 -ember-port " + port + " -ember-slot " + slot +
            " -ember-class warrior -ember-net-corpse-proof-role " + role + " -ember-net-script-start-file " + Q(startFile) +
            " -ember-net-quit-file " + Q(quitFile);

        private static string ClientArgs(int port, string slot, string script, string startFile, string quitFile) =>
            "-ember-net client -ember-address 127.0.0.1 -ember-port " + port + " -ember-slot " + slot +
            " -ember-class warrior -ember-net-script " + script + " -ember-net-script-start-file " + Q(startFile) +
            " -ember-net-quit-file " + Q(quitFile);

        private static Process Launch(string exe, string logPath, string args)
        {
            try
            {
                if (File.Exists(logPath)) File.Delete(logPath);
                var info = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "-batchmode -nographics -logFile " + Q(logPath) + " " + args,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process p = Process.Start(info);
                if (p != null) Started.Add(p);
                Debug.Log(Tag + " launched pid=" + (p != null ? p.Id.ToString() : "-") + " " + info.Arguments);
                return p;
            }
            catch (Exception e) { Debug.LogError(Tag + " launch failed: " + e.Message); return null; }
        }

        private static bool WaitForLog(string path, string text, double seconds, Process process) =>
            WaitFor(() => ReadShared(path).IndexOf(text, StringComparison.Ordinal) >= 0, seconds, process);

        private static Match WaitForRegex(string path, string pattern, double seconds, Process process) =>
            WaitForRegexCount(path, pattern, 1, seconds, process);

        private static Match WaitForRegexCount(string path, string pattern, int count, double seconds, Process process)
        {
            Match found = null;
            WaitFor(() =>
            {
                MatchCollection matches = Regex.Matches(ReadShared(path), pattern);
                if (matches.Count < count) return false;
                found = matches[count - 1];
                return true;
            }, seconds, process);
            return found;
        }

        private static int CountMatches(string path, string pattern) => Regex.Matches(ReadShared(path), pattern).Count;

        private static bool WaitFor(Func<bool> test, double seconds, Process process)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                if (test()) return true;
                if (process != null && HasExited(process)) return test();
                Thread.Sleep(200);
            }
            return test();
        }

        private static bool WaitForExit(Process process, int seconds)
        {
            try
            {
                if (process != null && process.WaitForExit(seconds * 1000)) return process.ExitCode == 0;
                Check(false, "process did not exit within " + seconds + " seconds");
                if (process != null && !HasExited(process)) process.Kill();
            }
            catch (Exception e) { Check(false, "process shutdown failed: " + e.Message); }
            return false;
        }

        /// <summary>Waits for an intentionally killed process; unlike clean shutdown, its nonzero exit is expected.</summary>
        private static bool WaitForTermination(Process process, int seconds)
        {
            if (process == null) return false;
            try { return process.WaitForExit(seconds * 1000); }
            catch { return HasExited(process); }
        }

        private static bool HasExited(Process p) { try { return p == null || p.HasExited; } catch { return true; } }

        private static void KillSurvivors()
        {
            foreach (Process p in Started)
            {
                try
                {
                    if (p != null && !p.HasExited)
                    {
                        Debug.LogError(Tag + " cleanup killed process " + p.Id);
                        _failures++;
                        _checks++;
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                }
                catch (Exception) { }
            }
        }

        private static int FreeUdpPort()
        {
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                return ((IPEndPoint)socket.Client.LocalEndPoint).Port;
        }

        private static string ReadShared(string path)
        {
            try
            {
                if (!File.Exists(path)) return "";
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        private static string ArgValue(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return "";
        }

        private static string Q(string value) => "\"" + value + "\"";

        private static void WriteAtomic(string path, string value)
        {
            string temp = path + ".tmp";
            // The live proof reader can briefly hold the destination on Windows.
            // Retry the atomic publication; never expose a partially-written command.
            for (int attempt = 0; ; attempt++)
            {
                File.WriteAllText(temp, value, Encoding.UTF8);
                try
                {
                    if (!File.Exists(path)) File.Move(temp, path);
                    else File.Replace(temp, path, null);
                    return;
                }
                catch (IOException) when (attempt < 19) { System.Threading.Thread.Sleep(25); }
            }
        }

        private static void Check(bool success, string message)
        {
            _checks++;
            if (success) Debug.Log(Tag + " ok: " + message);
            else { _failures++; Debug.LogError(Tag + " FAIL: " + message); }
        }
    }
}
