using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Level;
using Emberdelve.Interaction;
using Emberdelve.Net.Rules;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>
    /// U3A3, client process only (created by NetBootstrap). Two jobs, both observation for the proof gate and the dev overlay:
    ///
    ///  1. Logs the settled view picture: "[client] owner id=&lt;playerId&gt; views=&lt;k&gt; owned=1 class=.. party=.. ids=a,b". A picture is
    ///     "settled" when it has not changed for <see cref="SettleSeconds"/>; a new line is written each time the settled picture
    ///     changes (a player joins or leaves), so the gate can read both "views=2" and the later drop.
    ///  2. Runs the optional test script (--ember-net-script) "forge-owner" (the skill scripts are NetSkillSender's). It exists in development builds / the Editor only: once another
    ///     player's view exists, call the dev-only test RPC on THAT player's object (2 times) and once on our own object. The server
    ///     must reject the first two (we are not that object's owner) and accept the last.
    ///  3. U3A4a: every half second logs where it sees each OTHER player: "[client] view id=&lt;n&gt; pos=(x,y,z)" (written when the
    ///     view moved, and as a heartbeat every two seconds), so a gate can compare an observer's picture with the server's. The
    ///     movement stream and the movement scripts are NetInputSender's; the probe still only runs forge-owner.
    /// The probe sends no gameplay input and never decides anything; the server's replicated values are only read.
    /// </summary>
    public sealed class NetClientProbe : MonoBehaviour
    {
        private const float SettleSeconds = 0.75f;

        private string _script = "";
        private string _lastKey = "";
        private string _loggedKey = "";
        private float _changedAt;
        private bool _scriptDone;
        private readonly List<int> _ids = new List<int>();
        private readonly Dictionary<int, Vector3> _viewLogged = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, float> _viewLoggedAt = new Dictionary<int, float>();
        private float _nextViewLog;
        private NetPlayerRig _inventoryObservedRig;
        private bool _inventoryIntentSent;
        private bool _inventoryBaselineLogged;
        private bool _inventorySnapshotConfirmed;
        private ulong _inventoryIntentSequence;
        private ulong _inventoryExpectedRevision;
        private bool _inventoryResultAccepted;
        private bool _inventoryResultChanged;
        private int _inventoryResultEvents;
        private string _inventoryContestRole = "";
        private string _inventoryContestChestId = "";
        private string _inventoryContestUid = "";
        private NetInventoryClient _contestClient;
        private bool _contestRequestSent;
        private bool _contestResultReceived;
        private CorpseContainerDocument _contestLoot;
        private bool _contestTakeSent;
        private uint _contestTakeSequence;
        private float _nextContestStateLog;
        private float _nextContestWaitLog;
        private float _nextContestCandidateLog;
        private float _nextInventoryStateLog;
        private float _nextInventoryRetryLog;
        private NetPlayerRig _extractionObservedRig;
        private NetExtractionClient _extractionObservedClient;
        private bool _extractionBaselineLogged;
        private bool _extractionHoldStarted;
        private bool _extractionCompleteSent;
        private bool _extractionDuplicateSent;
        private double _extractionStartedAt;
        private float _nextExtractionStateLog;
        private string _extractionInventoryBefore = "";
        private int _extractionResultEvents;
        private string _corpseProofRole = "";
        private string _corpseProofLastCommand = "";
        private NetInventoryClient _corpseProofClient;
        private CorpseContainerDocument _corpseProofDocument;
        private ulong _corpseProofId;
        private ulong _corpseProofEntryId;
        private string _corpseProofUid = "";
        private int _corpseProofStateCount;
        private int _corpseProofResultCount;
        private bool _corpseProofPresenceRemoved;
        private bool _corpseProofTransferSent;
        private bool _corpseProofFinalLogged;
        private float _corpseProofFinalAt;
        private ulong _corpseProofExpectedBagRevision;
        private bool _corpseProofAcceptedTransfer;
        private string _doorProofRole = "";
        private string _floorLootProofRole = "";
        private string _floorLootProofLastCommand = "";
        private string _inventoryDropProofRole = "";
        private string _inventoryDropProofLastAction = "";
        private string _inventoryDropProofActionFile = "";
        private string _inventoryDropProofExpectedFile = "";
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private string _floorLootObserveTelemetryKey = "";
        private int _floorLootObserveTelemetryCount;
        private const int MaxFloorLootObserveTelemetryLines = 8;
#endif
        private string _doorProofActionFile = "";
        private string _doorProofLastCommand = "";
        private string _doorProofDoorId = "";
        private NetInventoryClient _doorProofClient;

        private const float ViewLogSeconds = 0.5f;
        private const float ViewHeartbeatSeconds = 2f;
        private const float ViewMoveEpsilon = 0.002f;

        public void Configure(NetLaunchArgs args)
        {
            _script = args != null ? args.Script : "";
            _inventoryContestRole = args != null ? args.InventoryContestRole : "";
            _corpseProofRole = args != null ? args.CorpseProofRole : "";
            _doorProofRole = args != null ? args.DoorProofRole : "";
            _doorProofActionFile = args != null ? args.DoorProofActionFile : "";
            _floorLootProofRole = args != null ? args.FloorLootProofRole : "";
            _inventoryDropProofRole = args != null ? args.InventoryDropProofRole : "";
            _inventoryDropProofActionFile = args != null ? args.InventoryDropProofActionFile : "";
            _inventoryDropProofExpectedFile = args != null ? args.InventoryDropProofExpectedFile : "";
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private bool _movementProofLayoutLogged;
        private void LogMovementProofLayout()
        {
            if (_movementProofLayoutLogged || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EMBER_MOVEMENT_PROOF_PATH"))) return;
            DungeonModuleSceneAssembler assembler = FindFirstObjectByType<DungeonModuleSceneAssembler>();
            if (assembler == null || !assembler.RuntimeMapValidationComplete || assembler.RuntimeLayout.playerSpawns.Count < 4) return;
            _movementProofLayoutLogged = true;
            for (int i = 0; i < 4; i++)
            {
                Vector3 pos = assembler.RuntimeLayout.PlayerSpawnWorld(i);
                Debug.Log(NetTuning.LogPrefix + "[client] movement-proof layout hash=" + assembler.AppliedLayoutHash +
                          " slot=" + i + " pos=(" + pos.x.ToString("F3", CultureInfo.InvariantCulture) + "," +
                          pos.y.ToString("F3", CultureInfo.InvariantCulture) + "," + pos.z.ToString("F3", CultureInfo.InvariantCulture) + ")");
            }
        }
#endif

        private void Update()
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            LogMovementProofLayout();
#endif
            IReadOnlyList<NetPlayerRig> rigs = NetPlayerRig.Spawned;
            if (rigs.Count == 0 && _lastKey.Length == 0) return;

            LogViews(rigs);

            int total = 0;
            int owned = 0;
            NetPlayerRig ownedRig = null;
            bool idsKnown = true;
            _ids.Clear();

            for (int i = 0; i < rigs.Count; i++)
            {
                NetPlayerRig rig = rigs[i];
                if (rig == null || !rig.IsSpawned) continue;
                total++;
                int id = rig.ServerPlayerId.Value;
                if (id <= 0) idsKnown = false;
                _ids.Add(id);
                if (rig.IsOwner)
                {
                    owned++;
                    ownedRig = rig;
                }
            }

            _ids.Sort();
            StringBuilder csv = new StringBuilder();
            for (int i = 0; i < _ids.Count; i++)
            {
                if (i > 0) csv.Append(',');
                csv.Append(_ids[i]);
            }

            int ownedId = ownedRig != null ? ownedRig.ServerPlayerId.Value : 0;
            string key = total + "|" + owned + "|" + ownedId + "|" + csv;
            float now = Time.unscaledTime;
            if (key != _lastKey)
            {
                _lastKey = key;
                _changedAt = now;
            }

            if (now - _changedAt < SettleSeconds) return;
            if (owned == 1 && idsKnown && total >= 1) RunScript(ownedRig, rigs);
            if (key == _loggedKey) return;
            _loggedKey = key;

            if (owned == 1 && idsKnown && total >= 1)
            {
                Debug.Log(NetTuning.LogPrefix + "[client] owner id=" + ownedId + " views=" + total + " owned=" + owned +
                          " class=" + ownedRig.ClassIdText + " party=" + ownedRig.ServerPartyId.Value + " ids=" + csv);
            }
            else
            {
                Debug.Log(NetTuning.LogPrefix + "[client] views unsettled total=" + total + " owned=" + owned + " idsKnown=" + idsKnown);
            }
        }

        private void LogViews(IReadOnlyList<NetPlayerRig> rigs)
        {
            float now = Time.unscaledTime;
            if (now < _nextViewLog) return;
            _nextViewLog = now + ViewLogSeconds;

            for (int i = 0; i < rigs.Count; i++)
            {
                NetPlayerRig rig = rigs[i];
                if (rig == null || !rig.IsSpawned || rig.IsOwner) continue;

                int id = rig.ServerPlayerId.Value;
                if (id <= 0) continue;

                Vector3 pos = rig.transform.position;
                bool known = _viewLogged.TryGetValue(id, out Vector3 last);
                bool moved = !known || (pos - last).sqrMagnitude > ViewMoveEpsilon * ViewMoveEpsilon;
                bool heartbeat = known && _viewLoggedAt.TryGetValue(id, out float at) && now - at >= ViewHeartbeatSeconds;
                if (!moved && !heartbeat) continue;

                _viewLogged[id] = pos;
                _viewLoggedAt[id] = now;
                Debug.Log(NetTuning.LogPrefix + "[client] view id=" + id + " pos=(" +
                          pos.x.ToString("F3", CultureInfo.InvariantCulture) + "," +
                          pos.y.ToString("F3", CultureInfo.InvariantCulture) + "," +
                          pos.z.ToString("F3", CultureInfo.InvariantCulture) + ")");
            }
        }

        private void RunScript(NetPlayerRig ownedRig, IReadOnlyList<NetPlayerRig> rigs)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            ObserveExtractionResults(ownedRig);
#endif
            if (!string.IsNullOrEmpty(_inventoryContestRole)) { RunInventoryContest(ownedRig); return; }
            if (!string.IsNullOrEmpty(_inventoryDropProofRole)) { RunInventoryDropProof(ownedRig); return; }
            if (!string.IsNullOrEmpty(_floorLootProofRole)) { RunFloorLootProof(ownedRig); return; }
            if (!string.IsNullOrEmpty(_corpseProofRole)) { RunCorpseProof(ownedRig); return; }
            if (!string.IsNullOrEmpty(_doorProofRole)) { RunDoorProof(ownedRig); return; }
            if (_script == "extraction-owner") { RunExtractionOwner(ownedRig); return; }
            if (_script == "extraction-forge") { RunExtractionForge(ownedRig); return; }
            if (NetBootstrap.LaunchArgs != null && NetBootstrap.LaunchArgs.ExtractionProofOwner)
            { RunExtractionInterrupt(ownedRig); return; }
            if (_script == "extraction-interrupt") { RunExtractionInterrupt(ownedRig); return; }
            if (_script == "inventory-owner") { RunInventoryOwner(ownedRig); return; }
            if (_script == "inventory-forge") { RunInventoryForge(ownedRig); return; }
            // U3B2: the skill scripts (skill-cast, skill-proof, skill-abuse) run on NetSkillSender; this probe only runs forge-owner.
            if (_scriptDone || _script != NetLaunchArgs.ScriptForgeOwner) return;

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            NetPlayerRig victim = NetPlayerRig.FindProbeVictim();
            if (victim == null) return; // wait until somebody else's view exists

            _scriptDone = true;
            int victimId = victim.ServerPlayerId.Value;
            victim.ForgeOwnerTestRpc(777);
            victim.ForgeOwnerTestRpc(778);
            ownedRig.ForgeOwnerTestRpc(5);
            Debug.Log(NetTuning.LogPrefix + "[client] script forge-owner sent target=" + victimId + " forged=2 self=1");
#else
            _scriptDone = true;
            Debug.Log(NetTuning.LogPrefix + "[client] script " + _script + " unavailable (not a development build)");
#endif
        }

        private void RunFloorLootProof(NetPlayerRig ownedRig)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            NetInventoryClient client = ownedRig != null ? ownedRig.GetComponent<NetInventoryClient>() : null;
            if (client == null || !client.IsReady || client.FloorLootRevision == 0 || NetBootstrap.LaunchArgs == null ||
                string.IsNullOrEmpty(NetBootstrap.LaunchArgs.FloorLootProofActionFile)) return;
            string command;
            try { command = File.Exists(NetBootstrap.LaunchArgs.FloorLootProofActionFile) ?
                ReadSharedText(NetBootstrap.LaunchArgs.FloorLootProofActionFile).Trim() : ""; }
            catch (IOException) { return; }
            if (string.IsNullOrEmpty(command) || command == _floorLootProofLastCommand) return;
            string[] fields = command.Split('|');
            if (_floorLootProofRole == "late")
            {
                if (fields.Length != 5 || fields[0] != "late" || !ulong.TryParse(fields[1], out ulong removedId) ||
                    !Guid.TryParseExact(fields[2], "N", out _) || !ulong.TryParse(fields[3], out ulong remainingId) ||
                    !Guid.TryParseExact(fields[4], "N", out _)) return;
                bool removed = !client.HasFloorLootPickup(removedId);
                bool remaining = client.HasFloorLootPickup(remainingId);
                if (!removed || !remaining) return; // retry after the newly spawned owner's full snapshot arrives
                _floorLootProofLastCommand = command;
                Debug.Log(NetTuning.LogPrefix + "[client] floor-loot-proof late revision=" + client.FloorLootRevision +
                          " removed=" + removed + " remaining=" + remaining + " removedId=" + removedId + " remainingId=" + remainingId);
                _floorLootProofLastCommand = command;
                return;
            }
            if (fields.Length < 3 || !ulong.TryParse(fields[1], out ulong pickupId) || !Guid.TryParseExact(fields[2], "N", out _)) return;
            if (fields[0] == "observe" && fields.Length == 6 && (_floorLootProofRole == "a" || _floorLootProofRole == "b"))
            {
                if (!ulong.TryParse(fields[3], out ulong remainingId) || !Guid.TryParseExact(fields[2], "N", out Guid contestUid) ||
                    !Guid.TryParseExact(fields[4], "N", out _)) return;
                bool held = ownedRig.Rig.Inventory.HoldsInstanceUid(contestUid) || ownedRig.Rig.Equipment.HoldsUid(contestUid);
                bool itemVisible = client.HasFloorLootPickup(pickupId);
                bool remainingVisible = client.HasFloorLootPickup(remainingId);
                LogFloorLootObserveTelemetry(ownedRig, pickupId, remainingId, contestUid, held, itemVisible, remainingVisible);
                if (itemVisible || !remainingVisible || ownedRig.Rig.Inventory.Revision == 0 ||
                    (_floorLootProofRole == fields[5] && !held)) return;
                _floorLootProofLastCommand = command;
                Debug.Log(NetTuning.LogPrefix + "[client] floor-loot-proof observe role=" + _floorLootProofRole +
                          " item=" + pickupId + " held=" + held + " visible=" + itemVisible + " remaining=" + remainingVisible +
                          " bagRevision=" + ownedRig.Rig.Inventory.Revision);
                return;
            }
            if (fields[0] == "contest" && (_floorLootProofRole == "a" || _floorLootProofRole == "b"))
            {
                if (!client.HasFloorLootPickup(pickupId)) return;
                bool queued = ownedRig.RequestFloorLootPickup(pickupId);
                _floorLootProofLastCommand = command;
                Debug.Log(NetTuning.LogPrefix + "[client] floor-loot-proof sent role=" + _floorLootProofRole + " stage=contest item=" + pickupId +
                          " visible=True queued=" + queued);
            }
            else if ((fields[0] == "far" || fields[0] == "blocked") && _floorLootProofRole == "b")
            {
                if (!client.HasFloorLootPickup(pickupId)) return;
                bool queued = ownedRig.RequestFloorLootPickup(pickupId);
                _floorLootProofLastCommand = command;
                Debug.Log(NetTuning.LogPrefix + "[client] floor-loot-proof sent role=b stage=" + fields[0] + " item=" + pickupId +
                          " visible=True queued=" + queued);
            }
            else if (fields[0] == "replay" && _floorLootProofRole == "b")
            {
                ownedRig.FloorLootPickupIntentRpc(pickupId, 3);
                _floorLootProofLastCommand = command;
                Debug.Log(NetTuning.LogPrefix + "[client] floor-loot-proof replay role=b item=" + pickupId + " seq=3");
            }
            else if (fields[0] == "forge" && _floorLootProofRole == "b")
            {
                NetPlayerRig victim = NetPlayerRig.FindProbeVictim();
                if (victim == null) return;
                victim.FloorLootPickupIntentRpc(pickupId, 4);
                _floorLootProofLastCommand = command;
                Debug.Log(NetTuning.LogPrefix + "[client] floor-loot-proof forge role=b target=" + victim.ServerPlayerId.Value + " item=" + pickupId + " seq=4");
            }
            else if (fields[0] == "dead" && _floorLootProofRole == "b")
            {
                bool queued = ownedRig.RequestFloorLootPickup(pickupId);
                _floorLootProofLastCommand = command;
                Debug.Log(NetTuning.LogPrefix + "[client] floor-loot-proof dead role=b item=" + pickupId + " queued=" + queued);
            }
#else
            _scriptDone = true;
#endif
        }

        private void RunInventoryDropProof(NetPlayerRig ownedRig)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            NetInventoryClient client = ownedRig != null ? ownedRig.GetComponent<NetInventoryClient>() : null;
            if (client == null || !client.IsReady ||
                string.IsNullOrEmpty(_inventoryDropProofActionFile)) return;
            string action;
            try { action = File.Exists(_inventoryDropProofActionFile) ? ReadSharedText(_inventoryDropProofActionFile).Trim() : ""; }
            catch (IOException) { return; }
            if (string.IsNullOrEmpty(action) || action == _inventoryDropProofLastAction) return;
            string[] fields = action.Split('|');
            if (fields.Length == 4 && fields[0] == "drop" && _inventoryDropProofRole == "owner" &&
                int.TryParse(fields[1], out int rawSlot) && Enum.IsDefined(typeof(EquipmentSlot), rawSlot) &&
                Guid.TryParseExact(fields[2], "N", out Guid uid) && ulong.TryParse(fields[3], out ulong expectedRevision))
            {
                EquipmentSlot slot = (EquipmentSlot)rawSlot;
                if (ownedRig.Rig?.Equipment?.EquippedInstance(slot)?.Uid != uid ||
                    ownedRig.Rig.Inventory.Revision != expectedRevision) return;
                IInventoryAuthority authority = ownedRig.Rig != null ? ownedRig.Rig.Inventory?.CommandAuthority : null;
                if (authority == null || !authority.TryCreateLocalCommand(InventoryCommandKind.Drop, 0, 0,
                        Vector2Int.zero, slot, uid, Guid.Empty, out InventoryCommand command, out string reason)) return;
                InventoryCommandResult queued = authority.Submit(ownedRig.Rig, command);
                if (!queued.Accepted) return;
                _inventoryDropProofLastAction = action;
                Debug.Log(NetTuning.LogPrefix + "[client] inventory-drop-proof sent uid=" + uid.ToString("N") +
                          " slot=" + slot + " queued=True");
                return;
            }
            if (fields.Length == 3 && fields[0] == "verify-retained" &&
                Guid.TryParseExact(fields[1], "N", out Guid retainedUid) && ulong.TryParse(fields[2], out ulong retainedRevision))
            {
                if (client.CountFloorLootProofUid(fields[1]) != 0) return;
                if (_inventoryDropProofRole == "owner")
                {
                    ItemInstance held = ownedRig.Rig?.Equipment?.EquippedInstance(EquipmentSlot.Chest);
                    if (held == null || held.Uid != retainedUid || ownedRig.Rig.Inventory.Revision != retainedRevision ||
                        ownedRig.Rig.Inventory.HoldsInstanceUid(retainedUid)) return;
                    string expectedJson;
                    try { expectedJson = ReadSharedText(_inventoryDropProofExpectedFile); }
                    catch (IOException) { return; }
                    if (JsonUtility.ToJson(held.ToSnapshot()) != expectedJson) return;
                }
                _inventoryDropProofLastAction = action;
                Debug.Log(NetTuning.LogPrefix + "[client] inventory-drop-proof retained role=" + _inventoryDropProofRole +
                    " uid=" + fields[1] + " floorCount=0 exactOwner=" + (_inventoryDropProofRole == "owner"));
                return;
            }
            if (fields.Length == 2 && fields[0] == "verify" &&
                (_inventoryDropProofRole == "owner" || _inventoryDropProofRole == "peer" || _inventoryDropProofRole == "late") &&
                Guid.TryParseExact(fields[1], "N", out Guid expectedUid) && !string.IsNullOrEmpty(_inventoryDropProofExpectedFile))
            {
                if (ownedRig.Rig?.Inventory?.HoldsInstanceUid(expectedUid) == true ||
                    ownedRig.Rig?.Equipment?.HoldsUid(expectedUid) == true) return;
                string expectedJson;
                try { expectedJson = File.Exists(_inventoryDropProofExpectedFile) ? ReadSharedText(_inventoryDropProofExpectedFile) : ""; }
                catch (IOException) { return; }
                ItemSnapshot expected = string.IsNullOrEmpty(expectedJson) ? null : JsonUtility.FromJson<ItemSnapshot>(expectedJson);
                if (!TryMatchDropProofSnapshot(client, expectedUid.ToString("N"), expected, out ItemSnapshot actual, out int uidCount)) return;
                _inventoryDropProofLastAction = action;
                Debug.Log(NetTuning.LogPrefix + "[client] inventory-drop-proof verified role=" + _inventoryDropProofRole +
                          " uid=" + actual.uid + " item=" + actual.itemId + " rarity=" + actual.rarity +
                          " affixes=" + actual.AffixCount + " uidCount=" + uidCount);
            }
#endif
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private static bool TryMatchDropProofSnapshot(NetInventoryClient client, string uid, ItemSnapshot expected,
            out ItemSnapshot actual, out int uidCount)
        {
            actual = null;
            uidCount = client != null ? client.CountFloorLootProofUid(uid) : 0;
            if (client == null || expected == null || uidCount != 1 ||
                !client.TryGetFloorLootProofItemByUid(uid, out _, out actual) || actual == null ||
                actual.itemId != expected.itemId || actual.quantity != expected.quantity || actual.rarity != expected.rarity ||
                actual.AffixCount != expected.AffixCount) return false;
            for (int i = 0; i < actual.AffixCount; i++)
            {
                AffixSnapshot left = actual.affixes[i], right = expected.affixes[i];
                if (left == null || right == null || left.id != right.id || left.key != right.key || left.value != right.value) return false;
            }
            return true;
        }
#endif

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private void LogFloorLootObserveTelemetry(NetPlayerRig ownedRig, ulong pickupId, ulong remainingId, Guid expectedUid,
            bool held, bool itemVisible, bool remainingVisible)
        {
            if (_floorLootObserveTelemetryCount >= MaxFloorLootObserveTelemetryLines || ownedRig?.Rig?.Inventory == null) return;

            Inventory bag = ownedRig.Rig.Inventory;
            ContainerSnapshot snapshot = bag.ToContainerSnapshot();
            var contents = new StringBuilder();
            if (snapshot.entries != null)
            {
                for (int i = 0; i < snapshot.entries.Length; i++)
                {
                    ContainerEntry entry = snapshot.entries[i];
                    if (entry?.item == null) continue;
                    if (contents.Length > 0) contents.Append(',');
                    contents.Append(string.IsNullOrEmpty(entry.item.uid) ? "-" : entry.item.uid)
                        .Append('/').Append(entry.item.itemId).Append('x').Append(entry.item.quantity);
                }
            }
            string contentText = contents.Length == 0 ? "-" : contents.ToString();
            string key = _floorLootProofRole + "|" + held + "|" + itemVisible + "|" + remainingVisible + "|" + bag.Revision + "|" + contentText;
            if (string.Equals(key, _floorLootObserveTelemetryKey, StringComparison.Ordinal)) return;
            _floorLootObserveTelemetryKey = key;
            _floorLootObserveTelemetryCount++;
            Debug.Log(NetTuning.LogPrefix + "[client] floor-loot-proof telemetry role=" + _floorLootProofRole +
                      " item=" + pickupId + " expectedUid=" + expectedUid.ToString("N") + " held=" + held +
                      " visible=" + itemVisible + " remaining=" + remainingVisible + " bagRevision=" + bag.Revision +
                      " bag=" + contentText + " sample=" + _floorLootObserveTelemetryCount + "/" + MaxFloorLootObserveTelemetryLines);
        }
#endif

        private void RunDoorProof(NetPlayerRig ownedRig)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            NetInventoryClient client = ownedRig != null ? ownedRig.GetComponent<NetInventoryClient>() : null;
            if (client == null) return;
            if (_doorProofClient != client)
            {
                if (_doorProofClient != null) _doorProofClient.DoorStateReceived -= OnDoorProofState;
                _doorProofClient = client;
                _doorProofClient.DoorStateReceived += OnDoorProofState;
            }
            if (string.IsNullOrWhiteSpace(_doorProofActionFile) || !File.Exists(_doorProofActionFile)) return;

            string command;
            try
            {
                using (var stream = new FileStream(_doorProofActionFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8)) command = reader.ReadToEnd().Trim();
            }
            catch (IOException) { return; }
            if (string.IsNullOrEmpty(command) || command == _doorProofLastCommand) return;
            string[] parts = command.Split('|');
            if (parts.Length != 3 || !DungeonDoorInteractable.IsValidDoorId(parts[1])) return;
            _doorProofLastCommand = command;
            _doorProofDoorId = parts[1];
            if (parts[0] == "toggle")
            {
                bool queued = client.RequestDoorToggle(_doorProofDoorId);
                Debug.Log(NetTuning.LogPrefix + "[client] door-proof command role=" + _doorProofRole + " action=toggle id=" +
                          _doorProofDoorId + " seq=" + parts[2] + " queued=" + queued);
            }
            else if (parts[0] == "observe")
            {
                if (client.DoorStateReady)
                    Debug.Log(NetTuning.LogPrefix + "[client] door-proof snapshot role=" + _doorProofRole +
                              " phase=end revision=" + client.DoorRevision);
                LogDoorProofState("observe", _doorProofDoorId, client);
            }
#else
            Debug.Log(NetTuning.LogPrefix + "[client] door-proof unavailable outside development builds");
#endif
        }

        private void OnDoorProofState(string doorId, bool isOpen, uint revision, bool snapshotStart, bool snapshotEnd)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            if (snapshotStart)
                Debug.Log(NetTuning.LogPrefix + "[client] door-proof snapshot role=" + _doorProofRole + " phase=start revision=" + revision);
            if (!string.IsNullOrEmpty(doorId) && string.Equals(doorId, _doorProofDoorId, StringComparison.Ordinal))
                LogDoorProofState("replica", doorId, _doorProofClient);
            if (snapshotEnd)
            {
                Debug.Log(NetTuning.LogPrefix + "[client] door-proof snapshot role=" + _doorProofRole + " phase=end revision=" + revision);
                if (!string.IsNullOrEmpty(_doorProofDoorId)) LogDoorProofState("snapshot", _doorProofDoorId, _doorProofClient);
            }
#endif
        }

        private void LogDoorProofState(string source, string doorId, NetInventoryClient client)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            if (client == null || !client.TryGetDoorState(doorId, out bool open, out uint revision)) return;
            DungeonDoorInteractable[] doors = FindObjectsByType<DungeonDoorInteractable>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < doors.Length; i++)
            {
                DungeonDoorInteractable door = doors[i];
                if (door == null || !string.Equals(door.DoorId, doorId, StringComparison.Ordinal)) continue;
                Debug.Log(NetTuning.LogPrefix + "[client] door-proof state role=" + _doorProofRole + " source=" + source +
                          " id=" + doorId + " open=" + open + " revision=" + revision + " ready=" + client.DoorStateReady +
                          " replicaOpen=" + door.IsOpen + " blocker=" + door.BlockerEnabled);
                return;
            }
            Debug.Log(NetTuning.LogPrefix + "[client] door-proof state role=" + _doorProofRole + " source=" + source +
                      " id=" + doorId + " open=" + open + " revision=" + revision + " ready=" + client.DoorStateReady +
                      " replicaOpen=missing blocker=missing");
#endif
        }

        private void RunInventoryContest(NetPlayerRig ownedRig)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            NetInventoryClient client = ownedRig != null ? ownedRig.GetComponent<NetInventoryClient>() : null;
            if (client == null || !client.IsReady || !client.ChestStateReady || ownedRig.Rig == null)
            {
                if (Time.unscaledTime >= _nextContestWaitLog)
                {
                    _nextContestWaitLog = Time.unscaledTime + 2f;
                    Debug.Log(NetTuning.LogPrefix + "[client] chest-contest-wait role=" + _inventoryContestRole +
                              " client=" + (client != null) + " bagReady=" + (client != null && client.IsReady) +
                              " chestReady=" + (client != null && client.ChestStateReady) + " rig=" + (ownedRig != null && ownedRig.Rig != null) +
                              " ownerSpawned=" + (ownedRig != null && ownedRig.IsSpawned) +
                              " floorRevision=" + (client != null ? client.FloorLootRevision : 0));
                }
                return;
            }
            if (_contestClient != client)
            {
                if (_contestClient != null) {
                    _contestClient.ChestResultReceived -= OnContestChestResult;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    _contestClient.LootOpenedForProof -= OnContestLootOpen;
#endif
                    _contestClient.CorpseContainerReceived -= OnContestLootState;
                    _contestClient.CorpseTransferResultReceived -= OnContestLootResult;
                }
                _contestClient = client;
                _contestClient.ChestResultReceived += OnContestChestResult;
                _contestClient.LootOpenedForProof += OnContestLootOpen;
                _contestClient.CorpseContainerReceived += OnContestLootState;
                _contestClient.CorpseTransferResultReceived += OnContestLootResult;
            }

            if (_inventoryContestChestId.Length == 0)
            {
                ChestInteractable[] chests = FindObjectsByType<ChestInteractable>(FindObjectsSortMode.None);
                StringBuilder candidates = new StringBuilder();
                for (int i = 0; i < chests.Length; i++)
                {
                    ChestInteractable chest = chests[i];
                    if (chest == null || chest.Variant != ChestVariant.Supply) continue;
                    bool canInteract = chest.CanInteract(ownedRig.Rig, out string blockedReason);
                    if (candidates.Length > 0) candidates.Append(',');
                    candidates.Append(chest.ChestId).Append(":rolled=").Append(chest.IsRolled).Append("/remaining=")
                        .Append(chest.RemainingUnits).Append("/can=").Append(canInteract).Append("/").Append(blockedReason ?? "none");
                    if (!canInteract && string.Equals(blockedReason, "No line of sight", StringComparison.Ordinal) &&
                        InteractionRules.TryGetLineOfSightBlocker(ownedRig.Rig, chest, out RaycastHit obstruction, out Vector3 targetPoint))
                    {
                        Collider hitCollider = obstruction.collider;
                        candidates.Append("/ray=").Append(hitCollider != null ? HierarchyPath(hitCollider.transform) : "missing")
                            .Append("@layer:").Append(hitCollider != null ? LayerMask.LayerToName(hitCollider.gameObject.layer) : "-")
                            .Append("@scene:").Append(hitCollider != null ? hitCollider.gameObject.scene.name : "-")
                            .Append("@hit:").Append(obstruction.point.ToString("F3", CultureInfo.InvariantCulture))
                            .Append("@dist:").Append(obstruction.distance.ToString("F3", CultureInfo.InvariantCulture))
                            .Append("@actor:").Append(ownedRig.Rig.transform.position.ToString("F3", CultureInfo.InvariantCulture))
                            .Append("@origin:").Append(ownedRig.Rig.InteractionOrigin.ToString("F3", CultureInfo.InvariantCulture))
                            .Append("@target:").Append(targetPoint.ToString("F3", CultureInfo.InvariantCulture))
                            .Append("@chest:").Append(chest.transform.position.ToString("F3", CultureInfo.InvariantCulture));
                    }
                    if (!chest.IsRolled || chest.RemainingUnits != 1 || !canInteract) continue;
                    _inventoryContestChestId = chest.ChestId;
                    Debug.Log(NetTuning.LogPrefix + "[client] chest-contest-ready role=" + _inventoryContestRole +
                              " chest=" + _inventoryContestChestId + " canInteract=True remaining=1");
                    break;
                }
                if (_inventoryContestChestId.Length == 0 && Time.unscaledTime >= _nextContestCandidateLog)
                {
                    _nextContestCandidateLog = Time.unscaledTime + 2f;
                    Debug.Log(NetTuning.LogPrefix + "[client] chest-contest-candidates role=" + _inventoryContestRole +
                              " chestReady=" + client.ChestStateReady + " count=" + chests.Length + " supply=" + candidates);
                }
            }

            if (!_contestRequestSent && !string.IsNullOrEmpty(_inventoryContestChestId) && StartFileReady())
            {
                string[] fixture = ReadStartCommand().Split('|');
                if (fixture.Length == 2 && fixture[0] == _inventoryContestChestId && Guid.TryParseExact(fixture[1], "N", out _))
                {
                    _inventoryContestUid = fixture[1];
                    ChestInteractable[] chests = FindObjectsByType<ChestInteractable>(FindObjectsSortMode.None);
                    for (int i = 0; i < chests.Length; i++)
                    {
                        if (chests[i] == null || !string.Equals(chests[i].ChestId, _inventoryContestChestId, System.StringComparison.Ordinal)) continue;
                        // The barrier is released only after this client has passed the real local CanInteract check.
                        // Send through the normal inventory client authority so a result arriving first cannot suppress
                        // B's competing request locally; the server re-runs authoritative admission, range and LOS.
                        bool queued = client.RequestChestOpen(_inventoryContestChestId);
                        _contestRequestSent = true;
                        Debug.Log(NetTuning.LogPrefix + "[client] chest-contest-sent role=" + _inventoryContestRole +
                                  " chest=" + _inventoryContestChestId + " uid=" + _inventoryContestUid +
                                  " queued=" + queued + " localReach=True via=inventory-client-authority");
                        break;
                    }
                }
            }

            if (!_contestTakeSent && _contestLoot != null && ReadStartCommand() == "take")
            {
                foreach (var entry in _contestLoot.entries)
                {
                    if (entry.item.uid != _inventoryContestUid) continue;
                    _contestTakeSent = true;
                    client.RequestCorpseTransfer(_contestLoot.corpseId, entry.entryId, 1, _contestLoot.revision,
                        ownedRig.Rig.Inventory.Revision, sequence => { _contestTakeSequence = sequence; return true; }, out _);
                    Debug.Log(NetTuning.LogPrefix + "[client] chest-loot-take role=" + _inventoryContestRole);
                    break;
                }
            }

            if (_contestRequestSent && Time.unscaledTime >= _nextContestStateLog)
            {
                _nextContestStateLog = Time.unscaledTime + 0.5f;
                ChestInteractable chest = null;
                ChestInteractable[] chests = FindObjectsByType<ChestInteractable>(FindObjectsSortMode.None);
                for (int i = 0; i < chests.Length; i++)
                    if (chests[i] != null && string.Equals(chests[i].ChestId, _inventoryContestChestId, System.StringComparison.Ordinal)) { chest = chests[i]; break; }
                bool holds = false;
                if (Guid.TryParseExact(_inventoryContestUid, "N", out Guid uid))
                    holds = ownedRig.Rig.Inventory.HoldsInstanceUid(uid) || ownedRig.Rig.Equipment.HoldsUid(uid);
                if (chest != null)
                    Debug.Log(NetTuning.LogPrefix + "[client] chest-contest-state role=" + _inventoryContestRole +
                              " chest=" + _inventoryContestChestId + " uid=" + _inventoryContestUid + " held=" + holds +
                              " rolled=" + chest.IsRolled + " opened=" + chest.Opened + " remaining=" + chest.RemainingUnits +
                              " result=" + _contestResultReceived);
            }
#else
            _scriptDone = true;
            Debug.Log(NetTuning.LogPrefix + "[client] inventory contest unavailable outside development build");
#endif
        }

        private static string HierarchyPath(Transform node)
        {
            if (node == null) return "-";
            var path = new List<string>();
            while (node != null) { path.Add(node.name); node = node.parent; }
            path.Reverse();
            return string.Join("/", path);
        }

        private void RunCorpseProof(NetPlayerRig ownedRig)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            NetInventoryClient client = ownedRig != null ? ownedRig.GetComponent<NetInventoryClient>() : null;
            if (client == null || !client.IsReady || ownedRig.Rig == null) return;
            if (!_corpseProofFinalLogged && _corpseProofFinalAt > 0f && Time.unscaledTime >= _corpseProofFinalAt &&
                client.IsReady && ownedRig.Rig.Inventory.Revision >= _corpseProofExpectedBagRevision)
            {
                _corpseProofFinalLogged = true;
                bool held = Guid.TryParseExact(_corpseProofUid, "N", out Guid uid) &&
                            (ownedRig.Rig.Inventory.HoldsInstanceUid(uid) || ownedRig.Rig.Equipment.HoldsUid(uid));
                Debug.Log(NetTuning.LogPrefix + "[client] corpse-proof-final role=" + _corpseProofRole +
                          " corpse=" + _corpseProofId + " uid=" + _corpseProofUid + " accepted=" + _corpseProofAcceptedTransfer +
                          " held=" + held + " bagRevision=" + ownedRig.Rig.Inventory.Revision +
                          " presenceRemoved=" + _corpseProofPresenceRemoved + " states=" + _corpseProofStateCount);
            }
            if (_corpseProofClient != client)
            {
                if (_corpseProofClient != null)
                {
                    _corpseProofClient.CorpseContainerReceived -= OnCorpseProofState;
                    _corpseProofClient.CorpseTransferResultReceived -= OnCorpseProofResult;
                    _corpseProofClient.CorpsePresenceReceived -= OnCorpseProofPresence;
                }
                _corpseProofClient = client;
                _corpseProofClient.CorpseContainerReceived += OnCorpseProofState;
                _corpseProofClient.CorpseTransferResultReceived += OnCorpseProofResult;
                _corpseProofClient.CorpsePresenceReceived += OnCorpseProofPresence;
            }

            string command = ReadStartCommand();
            if (string.IsNullOrEmpty(command) || command == _corpseProofLastCommand) return;
            string[] parts = command.Split('|');
            if (parts.Length < 4 || !ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _corpseProofId) ||
                !ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _corpseProofEntryId)) return;
            _corpseProofUid = parts[3];
            string stage = parts[0];
            _corpseProofLastCommand = command;
            if ((stage == "far" || stage == "blocked") && _corpseProofRole != "b") return;
            if (stage == "near" && _corpseProofRole != "a" && _corpseProofRole != "b") return;
            if (stage == "transfer" && _corpseProofRole != "a" && _corpseProofRole != "b") return;

            if (stage == "far" || stage == "blocked" || stage == "near")
            {
                bool queued = client.RequestCorpseState(_corpseProofId,
                    sequence => { _corpseProofLastStateSequence = sequence; return true; }, out uint sequence);
                Debug.Log(NetTuning.LogPrefix + "[client] corpse-proof-state-sent role=" + _corpseProofRole +
                          " stage=" + stage + " corpse=" + _corpseProofId + " seq=" + sequence + " queued=" + queued);
            }
            else if (stage == "foreign" && _corpseProofRole == "b")
            {
                NetPlayerRig victim = NetPlayerRig.FindProbeVictim();
                if (victim == null || !victim.IsSpawned)
                {
                    _corpseProofLastCommand = "";
                    return;
                }
                ulong corpseRevision = parts.Length > 4 && ulong.TryParse(parts[4], NumberStyles.None,
                    CultureInfo.InvariantCulture, out ulong parsedRevision) ? parsedRevision : 1;
                victim.CorpseTransferIntentRpc(_corpseProofId, _corpseProofEntryId, 1,
                    corpseRevision, ownedRig.Rig.Inventory.Revision, 1);
                Debug.Log(NetTuning.LogPrefix + "[client] corpse-proof-forge role=b target=" +
                          victim.ServerPlayerId.Value + " corpse=" + _corpseProofId + " entry=" + _corpseProofEntryId +
                          " uid=" + _corpseProofUid + " seq=1");
            }
            else if (stage == "transfer")
            {
                if (_corpseProofDocument == null || _corpseProofDocument.corpseId != _corpseProofId ||
                    _corpseProofDocument.entries == null || _corpseProofDocument.entries.Length != 1 ||
                    _corpseProofDocument.entries[0].entryId != _corpseProofEntryId ||
                    !string.Equals(_corpseProofDocument.entries[0].item?.uid, _corpseProofUid, StringComparison.Ordinal))
                {
                    Debug.LogError(NetTuning.LogPrefix + "[client] corpse-proof transfer refused locally: fixture state mismatch role=" + _corpseProofRole);
                    return;
                }
                bool queued = client.RequestCorpseTransfer(_corpseProofId, _corpseProofEntryId, 1,
                    _corpseProofDocument.revision, ownedRig.Rig.Inventory.Revision, null, out uint sequence);
                _corpseProofTransferSent = queued;
                Debug.Log(NetTuning.LogPrefix + "[client] corpse-proof-transfer-sent role=" + _corpseProofRole +
                          " corpse=" + _corpseProofId + " entry=" + _corpseProofEntryId + " uid=" + _corpseProofUid +
                          " corpseRevision=" + _corpseProofDocument.revision + " bagRevision=" + ownedRig.Rig.Inventory.Revision +
                          " seq=" + sequence + " queued=" + queued);
            }
#else
            _scriptDone = true;
            Debug.Log(NetTuning.LogPrefix + "[client] corpse proof unavailable outside development build");
#endif
        }

        private uint _corpseProofLastStateSequence;

        private string ReadStartCommand()
        {
            string path = NetBootstrap.LaunchArgs?.ScriptStartFile;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "";
            try { return ReadSharedText(path).Trim(); }
            catch (IOException) { return ""; }
        }

        private static string ReadSharedText(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
            catch (IOException) { return string.Empty; }
            catch (UnauthorizedAccessException) { return string.Empty; }
        }

        private void OnCorpseProofState(CorpseContainerDocument document)
        {
            if (document == null || document.corpseId != _corpseProofId) return;
            _corpseProofDocument = document;
            _corpseProofStateCount++;
            ulong entryId = document.entries != null && document.entries.Length > 0 ? document.entries[0].entryId : 0;
            string uid = document.entries != null && document.entries.Length > 0 ? document.entries[0].item?.uid ?? "" : "";
            Debug.Log(NetTuning.LogPrefix + "[client] corpse-proof-state role=" + _corpseProofRole + " corpse=" + document.corpseId +
                      " revision=" + document.revision + " entries=" + (document.entries != null ? document.entries.Length : -1) +
                      " entry=" + entryId + " uid=" + uid + " count=" + _corpseProofStateCount);
        }

        private void OnCorpseProofResult(ulong corpseId, uint sequence, bool accepted,
            ulong corpseRevision, ulong bagRevision, string reason)
        {
            if (corpseId != _corpseProofId) return;
            _corpseProofResultCount++;
            if (sequence != _corpseProofLastStateSequence && !_corpseProofTransferSent) return;
            string normalized = string.IsNullOrEmpty(reason) ? "none" : reason.Replace(' ', '_');
            Debug.Log(NetTuning.LogPrefix + "[client] corpse-proof-result role=" + _corpseProofRole + " corpse=" + corpseId +
                      " seq=" + sequence + " accepted=" + accepted + " corpseRevision=" + corpseRevision +
                      " bagRevision=" + bagRevision + " reason=" + normalized + " states=" + _corpseProofStateCount);
            if (_corpseProofTransferSent)
            {
                _corpseProofExpectedBagRevision = bagRevision;
                _corpseProofAcceptedTransfer = accepted;
                _corpseProofTransferSent = false;
                _corpseProofFinalAt = Time.unscaledTime + 1.0f;
            }
        }

        private void OnCorpseProofPresence(ulong corpseId, Vector3 position, ulong revision, bool removed)
        {
            if (corpseId == _corpseProofId && removed) _corpseProofPresenceRemoved = true;
        }

        private void OnContestLootOpen(ulong id) => _contestClient?.RequestCorpseState(id);
        private void OnContestLootState(CorpseContainerDocument doc)
        {
            if (doc.kind != "chest") return;
            _contestLoot = doc;
            Debug.Log(NetTuning.LogPrefix + "[client] chest-loot-ready role=" + _inventoryContestRole + " container=" + doc.corpseId + " revision=" + doc.revision + " entries=" + doc.entries.Length);
        }
        private void OnContestLootResult(ulong id, uint sequence, bool accepted, ulong revision, ulong bagRevision, string reason)
        {
            if (sequence != _contestTakeSequence || sequence == 0) return;
            Debug.Log(NetTuning.LogPrefix + "[client] chest-loot-result role=" + _inventoryContestRole + " accepted=" + accepted + " reason=" + (string.IsNullOrEmpty(reason) ? "none" : reason.Replace(' ', '_')));
        }

        private void OnContestChestResult(bool accepted, string chestId, uint sequence, string reason)
        {
            if (!string.Equals(chestId, _inventoryContestChestId, System.StringComparison.Ordinal)) return;
            _contestResultReceived = true;
            Debug.Log(NetTuning.LogPrefix + "[client] chest-contest-result role=" + _inventoryContestRole +
                      " chest=" + chestId + " seq=" + sequence + " accepted=" + accepted +
                      " reason=" + (string.IsNullOrEmpty(reason) ? "none" : reason.Replace(' ', '_')));
        }

        private void ObserveInventoryResults(NetPlayerRig ownedRig)
        {
            if (_inventoryObservedRig == ownedRig) return;
            if (_inventoryObservedRig != null) _inventoryObservedRig.InventoryResultReceived -= OnInventoryResult;
            _inventoryObservedRig = ownedRig;
            if (_inventoryObservedRig != null) _inventoryObservedRig.InventoryResultReceived += OnInventoryResult;
        }

        private void ObserveExtractionResults(NetPlayerRig ownedRig)
        {
            if (_extractionObservedRig == ownedRig) return;
            if (_extractionObservedRig != null) GameEvents.RunEnded -= OnExtractionRunEnded;
            if (_extractionObservedClient != null) _extractionObservedClient.HoldResultReceived -= OnExtractionHoldResult;
            _extractionObservedRig = ownedRig;
            if (_extractionObservedRig != null) GameEvents.RunEnded += OnExtractionRunEnded;
            _extractionObservedClient = _extractionObservedRig != null ? _extractionObservedRig.GetComponent<NetExtractionClient>() : null;
            if (_extractionObservedClient != null) _extractionObservedClient.HoldResultReceived += OnExtractionHoldResult;
        }

        private void OnExtractionHoldResult(uint sequence, bool accepted, string reason)
        {
            int playerId = _extractionObservedRig != null && _extractionObservedRig.Rig != null ? _extractionObservedRig.Rig.PlayerId : 0;
            Debug.Log(NetTuning.LogPrefix + "[client] extraction-hold-result player=" + playerId + " seq=" + sequence +
                      " accepted=" + accepted + " reason=" + (string.IsNullOrEmpty(reason) ? "none" : reason.Replace(' ', '_')));
        }

        private void OnExtractionRunEnded(int playerId, RunResult result)
        {
            if (_extractionObservedRig == null || _extractionObservedRig.Rig == null) return;
            if (playerId != _extractionObservedRig.Rig.PlayerId || result.PlayerId != playerId)
            {
                Debug.Log(NetTuning.LogPrefix + "[client] extraction-result-leak receiver=" + _extractionObservedRig.Rig.PlayerId +
                          " resultPlayer=" + playerId + " payloadPlayer=" + result.PlayerId);
                return;
            }
            _extractionResultEvents++;
            Inventory bag = _extractionObservedRig.Rig.Inventory;
            string after = bag != null ? InventoryDigest(bag) : "none";
            Debug.Log(NetTuning.LogPrefix + "[client] extraction-result owner=" + playerId + " count=" + _extractionResultEvents +
                      " outcome=" + result.Outcome + " class=" + (result.ClassName ?? "").Replace(' ', '_') +
                      " loot=" + result.LootValue.ToString("R", CultureInfo.InvariantCulture) +
                      " items=" + result.ItemsCollected + " kills=" + result.EnemiesSlain +
                      " duration=" + result.DurationSeconds.ToString("R", CultureInfo.InvariantCulture) +
                      " gear=" + result.GearScore + " inventoryBefore=" + _extractionInventoryBefore + " inventoryAfter=" + after +
                      " cache=" + (RunContext.HasResult && RunContext.LastResult.PlayerId == playerId));
        }

        private void RunExtractionOwner(NetPlayerRig ownedRig)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            NetExtractionClient client = ownedRig != null ? ownedRig.GetComponent<NetExtractionClient>() : null;
            Inventory bag = ownedRig != null && ownedRig.Rig != null ? ownedRig.Rig.Inventory : null;
            NetInventoryClient inventory = ownedRig != null ? ownedRig.GetComponent<NetInventoryClient>() : null;
            if (client == null || inventory == null || !inventory.IsReady || bag == null || ownedRig.Rig == null) return;
            ObserveExtractionResults(ownedRig);
            if (!_extractionBaselineLogged)
            {
                _extractionInventoryBefore = InventoryDigest(bag);
                _extractionBaselineLogged = true;
                Debug.Log(NetTuning.LogPrefix + "[client] extraction-baseline player=" + ownedRig.Rig.PlayerId +
                          " inventory=" + _extractionInventoryBefore);
            }
            if (!StartFileReady())
            {
                _extractionInventoryBefore = InventoryDigest(bag);
                if (Time.unscaledTime >= _nextExtractionStateLog)
                {
                    _nextExtractionStateLog = Time.unscaledTime + 0.5f;
                    Debug.Log(NetTuning.LogPrefix + "[client] extraction-inventory-state player=" + ownedRig.Rig.PlayerId +
                              " inventory=" + _extractionInventoryBefore);
                }
                return;
            }
            if (!_extractionHoldStarted)
            {
                _extractionHoldStarted = client.BeginExtractionHold();
                if (_extractionHoldStarted)
                {
                    _extractionStartedAt = Time.realtimeSinceStartupAsDouble;
                    Debug.Log(NetTuning.LogPrefix + "[client] extraction-begin-sent player=" + ownedRig.Rig.PlayerId + " seq=1");
                }
                return;
            }
            float holdDuration = 3f;
            string timingFile = NetBootstrap.LaunchArgs?.ScriptStartFile;
            if (!string.IsNullOrEmpty(timingFile) && File.Exists(timingFile) &&
                float.TryParse(File.ReadAllText(timingFile).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float configured) &&
                !float.IsNaN(configured) && !float.IsInfinity(configured) && configured > 0f) holdDuration = configured;
            if (!_extractionCompleteSent && Time.realtimeSinceStartupAsDouble - _extractionStartedAt >= holdDuration + 0.2f)
            {
                _extractionCompleteSent = client.CompleteExtractionHold();
                Debug.Log(NetTuning.LogPrefix + "[client] extraction-complete-sent player=" + ownedRig.Rig.PlayerId +
                          " seq=1 queued=" + _extractionCompleteSent);
                return;
            }
            if (_extractionResultEvents > 0 && !_extractionDuplicateSent)
            {
                ownedRig.ExtractionHoldCompleteRpc(1); // duplicate production RPC after the server has sealed the lease
                _extractionDuplicateSent = true;
                Debug.Log(NetTuning.LogPrefix + "[client] extraction-duplicate-complete-sent player=" + ownedRig.Rig.PlayerId + " seq=1");
            }
#else
            _scriptDone = true;
            Debug.Log(NetTuning.LogPrefix + "[client] script extraction-owner unavailable outside development build");
#endif
        }

        private void RunExtractionForge(NetPlayerRig ownedRig)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            ObserveExtractionResults(ownedRig);
            if (!StartFileReady() || _extractionHoldStarted) return;
            NetPlayerRig victim = NetPlayerRig.FindProbeVictim();
            if (victim == null || !victim.IsSpawned) return;
            victim.ExtractionHoldBeginRpc(1);
            victim.ExtractionHoldHeartbeatRpc(1);
            victim.ExtractionHoldCompleteRpc(1);
            victim.ExtractionHoldCancelRpc(1);
            _extractionHoldStarted = true;
            Debug.Log(NetTuning.LogPrefix + "[client] extraction-forge sent sender=" + ownedRig.Rig.PlayerId +
                      " target=" + victim.ServerPlayerId.Value + " begin=1 heartbeat=1 complete=1 cancel=1");
#else
            _scriptDone = true;
            Debug.Log(NetTuning.LogPrefix + "[client] script extraction-forge unavailable outside development build");
#endif
        }

        private void RunExtractionInterrupt(NetPlayerRig ownedRig)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            NetExtractionClient client = ownedRig != null ? ownedRig.GetComponent<NetExtractionClient>() : null;
            NetInventoryClient inventory = ownedRig != null ? ownedRig.GetComponent<NetInventoryClient>() : null;
            Inventory bag = ownedRig != null && ownedRig.Rig != null ? ownedRig.Rig.Inventory : null;
            if (client == null || inventory == null || !inventory.IsReady || bag == null || ownedRig.Rig == null) return;
            if (!StartFileReady())
            {
                _extractionInventoryBefore = InventoryDigest(bag);
                if (Time.unscaledTime >= _nextExtractionStateLog)
                {
                    _nextExtractionStateLog = Time.unscaledTime + 0.5f;
                    Debug.Log(NetTuning.LogPrefix + "[client] extraction-inventory-state player=" + ownedRig.Rig.PlayerId +
                              " inventory=" + _extractionInventoryBefore);
                }
                return;
            }
            if (_extractionHoldStarted) return;
            _extractionHoldStarted = client.BeginExtractionHold();
            Debug.Log(NetTuning.LogPrefix + "[client] extraction-interrupt-begin player=" + ownedRig.Rig.PlayerId +
                      " queued=" + _extractionHoldStarted);
#else
            _scriptDone = true;
            Debug.Log(NetTuning.LogPrefix + "[client] script extraction-interrupt unavailable outside development build");
#endif
        }

        private void OnInventoryResult(InventoryResultWire result)
        {
            _inventoryResultEvents++;
            Debug.Log(NetTuning.LogPrefix + "[client] inventory-result seq=" + result.Sequence + " accepted=" + result.Accepted +
                      " changed=" + result.Changed + " revision=" + result.Revision);
            if (result.Sequence != _inventoryIntentSequence) return;
            _inventoryResultAccepted = result.Accepted;
            _inventoryResultChanged = result.Changed;
            _inventoryExpectedRevision = result.Revision;
        }

        private void RunInventoryOwner(NetPlayerRig ownedRig)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            ObserveInventoryResults(ownedRig);
            NetInventoryClient client = ownedRig.GetComponent<NetInventoryClient>();
            Inventory bag = ownedRig.Rig != null ? ownedRig.Rig.Inventory : null;
            if (client == null || bag == null || !client.IsReady) return;
            if (!_inventoryBaselineLogged)
            {
                _inventoryBaselineLogged = true;
                LogInventoryState("owner", bag);
            }
            if (!StartFileReady() || _inventoryIntentSent) { LogInventoryProgress(ownedRig, "owner"); return; }

            IInventoryAuthority authority = bag.CommandAuthority;
            for (int i = 0; i < bag.Slots.Count; i++)
            {
                InventorySlot slot = bag.Slots[i];
                if (slot.StackId == 0) continue;
                for (int y = 0; y < bag.GridHeight; y++)
                for (int x = 0; x < bag.GridWidth; x++)
                {
                    Vector2Int destination = new Vector2Int(x, y);
                    if (destination == slot.Origin || !bag.CanPlace(destination, slot.Size, slot.Index)) continue;
                    if (!authority.TryCreateLocalCommand(InventoryCommandKind.Move, slot.StackId, 0, destination,
                            EquipmentSlot.None, System.Guid.Empty, System.Guid.Empty, out InventoryCommand command, out _)) continue;
                    InventoryCommandResult queued = authority.Submit(ownedRig.Rig, command);
                    if (!queued.Accepted) continue;
                    _inventoryIntentSequence = command.Sequence;
                    _inventoryIntentSent = true;
                    Debug.Log(NetTuning.LogPrefix + "[client] inventory-intent sent seq=" + command.Sequence + " revision=" +
                              command.ExpectedRevision + " stack=" + slot.StackId + " from=" + slot.Origin + " to=" + destination);
                    break;
                }
                if (_inventoryIntentSent) break;
            }
            if (!_inventoryIntentSent && Time.unscaledTime >= _nextInventoryRetryLog)
            {
                _nextInventoryRetryLog = Time.unscaledTime + 2f;
                Debug.Log(NetTuning.LogPrefix + "[client] inventory-intent waiting: no movable stack destination");
            }
            LogInventoryProgress(ownedRig, "owner");
#else
            _scriptDone = true;
            Debug.Log(NetTuning.LogPrefix + "[client] script inventory-owner unavailable (not a development build)");
#endif
        }

        private void RunInventoryForge(NetPlayerRig ownedRig)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            ObserveInventoryResults(ownedRig);
            NetInventoryClient client = ownedRig.GetComponent<NetInventoryClient>();
            Inventory bag = ownedRig.Rig != null ? ownedRig.Rig.Inventory : null;
            if (client == null || bag == null || !client.IsReady) return;
            if (!_inventoryBaselineLogged)
            {
                _inventoryBaselineLogged = true;
                LogInventoryState("forge", bag);
            }
            if (!_inventoryIntentSent && StartFileReady())
            {
                NetPlayerRig victim = NetPlayerRig.FindProbeVictim();
                if (victim == null || !victim.IsSpawned || victim.NetworkObject == null) return;
                var intent = new InventoryIntentWire
                {
                    Kind = (int)InventoryCommandKind.Move,
                    Sequence = 1,
                    ExpectedRevision = 0,
                    SourceStackId = 1,
                    CellX = 0,
                    CellY = 0,
                    Slot = (int)EquipmentSlot.None
                };
                victim.InventoryIntentRpc(intent);
                _inventoryIntentSent = true;
                Debug.Log(NetTuning.LogPrefix + "[client] inventory-forge sent target=" + victim.ServerPlayerId.Value +
                          " netId=" + victim.NetworkObjectId + " seq=1 kind=Move");
            }
            LogInventoryProgress(ownedRig, "forge");
#else
            _scriptDone = true;
            Debug.Log(NetTuning.LogPrefix + "[client] script inventory-forge unavailable (not a development build)");
#endif
        }

        private bool StartFileReady()
        {
            return string.IsNullOrEmpty(NetBootstrap.LaunchArgs?.ScriptStartFile) || File.Exists(NetBootstrap.LaunchArgs.ScriptStartFile);
        }

        private void LogInventoryProgress(NetPlayerRig ownedRig, string role)
        {
            Inventory bag = ownedRig.Rig != null ? ownedRig.Rig.Inventory : null;
            if (bag == null) return;
            if (role == "owner" && _inventoryResultAccepted && _inventoryResultChanged && !_inventorySnapshotConfirmed &&
                bag.Revision >= _inventoryExpectedRevision)
            {
                _inventorySnapshotConfirmed = true;
                Debug.Log(NetTuning.LogPrefix + "[client] inventory-snapshot confirmed seq=" + _inventoryIntentSequence +
                          " revision=" + bag.Revision + " digest=" + InventoryDigest(bag));
            }
            if (Time.unscaledTime < _nextInventoryStateLog) return;
            _nextInventoryStateLog = Time.unscaledTime + 1f;
            LogInventoryState(role, bag);
        }

        private void LogInventoryState(string role, Inventory bag)
        {
            Debug.Log(NetTuning.LogPrefix + "[client] inventory-state role=" + role + " digest=" + InventoryDigest(bag) +
                      " resultEvents=" + _inventoryResultEvents + " confirmed=" + _inventorySnapshotConfirmed);
        }

        private static string InventoryDigest(Inventory bag)
        {
            StringBuilder value = new StringBuilder("r=").Append(bag.Revision).Append(";stacks=");
            for (int i = 0; i < bag.Slots.Count; i++)
            {
                if (i > 0) value.Append(',');
                InventorySlot slot = bag.Slots[i];
                value.Append(slot.StackId).Append('@').Append(slot.Origin.x).Append(':').Append(slot.Origin.y).Append('x')
                    .Append(slot.Count).Append('x').Append(slot.Size.x).Append(':').Append(slot.Size.y);
            }
            return value.ToString();
        }

        private void OnDestroy()
        {
            if (_inventoryObservedRig != null) _inventoryObservedRig.InventoryResultReceived -= OnInventoryResult;
            if (_contestClient != null) {
                    _contestClient.ChestResultReceived -= OnContestChestResult;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    _contestClient.LootOpenedForProof -= OnContestLootOpen;
#endif
                    _contestClient.CorpseContainerReceived -= OnContestLootState;
                    _contestClient.CorpseTransferResultReceived -= OnContestLootResult;
                }
            if (_corpseProofClient != null)
            {
                _corpseProofClient.CorpseContainerReceived -= OnCorpseProofState;
                _corpseProofClient.CorpseTransferResultReceived -= OnCorpseProofResult;
                _corpseProofClient.CorpsePresenceReceived -= OnCorpseProofPresence;
            }
            if (_extractionObservedRig != null) GameEvents.RunEnded -= OnExtractionRunEnded;
            if (_extractionObservedClient != null) _extractionObservedClient.HoldResultReceived -= OnExtractionHoldResult;
        }
    }
}
