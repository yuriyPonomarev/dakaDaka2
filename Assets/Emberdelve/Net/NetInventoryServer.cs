using System;
using System.Collections.Generic;
using System.Text;
using Emberdelve.Core;
using Emberdelve.Flow;
using Emberdelve.Interaction;
using Emberdelve.Items;
using Emberdelve.Combat;
using Emberdelve.Net.Rules;
using Unity.Netcode;
using Unity.Collections;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.Net
{
    /// <summary>
    /// Server-only entry point for a future reliable inventory RPC. It validates the admitted sender and the
    /// network object before deriving the command owner. The bound local authority decides revision, item UID,
    /// life state and all mutations. Only requests passing identity and admission receive a response.
    /// One instance belongs to one server session; OnClosed must be called on every disconnect.
    /// </summary>
    public sealed class NetInventoryServer : ICorpseDeathCustodian, IDoorAiAuthority, IWorldLootContainerHost
    {
        public static NetInventoryServer Current { get; private set; }
        public int RegisteredCorpseCount => _corpseContainers.Count;
        public uint DoorRevision => _doorRevision;
        private readonly NetworkManager _network;
        private readonly NetIdentityTable _identities;
        private readonly NetSkillServer _skills;
        private readonly System.Action<string> _log;
        private readonly bool _doorProofEnabled;
        private readonly Dictionary<ulong, InventoryIntentGate> _gates = new Dictionary<ulong, InventoryIntentGate>();
        private readonly Dictionary<ulong, double> _lastSnapshotRequest = new Dictionary<ulong, double>();
        private readonly Dictionary<ulong, double> _lastChestIntent = new Dictionary<ulong, double>();
        private readonly Dictionary<ulong, double> _lastDoorIntent = new Dictionary<ulong, double>();
        private readonly Dictionary<ulong, double> _lastDoorSnapshotRequest = new Dictionary<ulong, double>();
        private uint _doorRevision;
        private readonly Dictionary<ulong, uint> _lastChestHoldSequence = new Dictionary<ulong, uint>();
        private readonly Dictionary<ulong, uint> _lastChestHeartbeatRefusalSequence = new Dictionary<ulong, uint>();
        private readonly Dictionary<ulong, double> _lastChestHoldStart = new Dictionary<ulong, double>();
        private readonly Dictionary<ulong, PendingChestHold> _chestHolds = new Dictionary<ulong, PendingChestHold>();
        private readonly List<ulong> _chestHoldScratch = new List<ulong>();
        private readonly Dictionary<ulong, CorpseContainerModel> _corpseContainers = new Dictionary<ulong, CorpseContainerModel>();
        private readonly HashSet<int> _committedDeathPlayers = new HashSet<int>();
        private readonly HashSet<int> _committingDeathPlayers = new HashSet<int>();
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private bool _proofRejectDeathCustody;
#endif
        private readonly Dictionary<ulong, CorpseRequestSequenceGate> _corpseSequences = new Dictionary<ulong, CorpseRequestSequenceGate>();
        private readonly Dictionary<ulong, CorpseRequestRateGate> _corpseStateRates = new Dictionary<ulong, CorpseRequestRateGate>();
        private readonly Dictionary<ulong, CorpseRequestRateGate> _corpseTransferRates = new Dictionary<ulong, CorpseRequestRateGate>();
        private readonly Dictionary<ulong, FloorLootIntentGate> _floorLootGates = new Dictionary<ulong, FloorLootIntentGate>();
        private readonly Dictionary<ulong, LootPickup> _floorLoot = new Dictionary<ulong, LootPickup>();
        private readonly Dictionary<int, ulong> _floorLootObjectIds = new Dictionary<int, ulong>();
        private ulong _nextCorpseId = 1;
        private ulong _nextFloorLootId = 1;
        private ulong _floorLootRevision;
        private string _lastFloorLootContent = string.Empty;
        private bool _floorLootUnavailable;
        private float _nextFloorLootSyncAt;
        private const int MaxNetworkChests = 128;
        private const int MaxNetworkCorpses = 128;
        private const float ChestIntentInterval = 0.15f;
        private const double CorpseStateInterval = 0.25;
        private const double CorpseTransferInterval = 0.1;
        private const int MaxFloorLootItems = 256;

        private sealed class PendingChestHold
        {
            public ChestInteractable Chest;
            public ChestHoldWindow Window;
            public uint Sequence;
            public bool CompletionRequested;
            public double LastHeartbeatAt;
            public HealthComponent Health;
            public System.Action<DamageInfo> DamageHandler;
        }

        public NetInventoryServer(NetworkManager network, NetIdentityTable identities)
            : this(network, identities, null, null) { }

        internal NetInventoryServer(NetworkManager network, NetIdentityTable identities, NetSkillServer skills, System.Action<string> log = null, bool doorProofEnabled = false)
        {
            _network = network;
            _identities = identities;
            _skills = skills;
            _log = log;
            _doorProofEnabled = doorProofEnabled;
            Current = this;
            GameEvents.InventoryChanged += OnInventoryChanged;
            GameServices.Register<ICorpseDeathCustodian>(this);
            GameServices.Register<IWorldLootContainerHost>(this);
            GameServices.Register<IDoorAiAuthority>(this);
        }

        public void OnClosed(ulong clientId) { _gates.Remove(clientId); _lastSnapshotRequest.Remove(clientId); _lastChestIntent.Remove(clientId); _lastDoorIntent.Remove(clientId); _lastDoorSnapshotRequest.Remove(clientId); _lastChestHoldSequence.Remove(clientId); _lastChestHeartbeatRefusalSequence.Remove(clientId); _lastChestHoldStart.Remove(clientId); _corpseSequences.Remove(clientId); _corpseStateRates.Remove(clientId); _corpseTransferRates.Remove(clientId); _floorLootGates.Remove(clientId); RemoveChestHold(clientId); }
        public void Dispose() { GameServices.Unregister<IWorldLootContainerHost>(this); GameServices.Unregister<ICorpseDeathCustodian>(this); GameServices.Unregister<IDoorAiAuthority>(this); GameEvents.InventoryChanged -= OnInventoryChanged; _gates.Clear(); _lastSnapshotRequest.Clear(); _lastChestIntent.Clear(); _lastDoorIntent.Clear(); _lastDoorSnapshotRequest.Clear(); _lastChestHoldSequence.Clear(); _lastChestHeartbeatRefusalSequence.Clear(); _lastChestHoldStart.Clear(); _corpseContainers.Clear(); _committedDeathPlayers.Clear(); _committingDeathPlayers.Clear(); _corpseSequences.Clear(); _corpseStateRates.Clear(); _corpseTransferRates.Clear(); _floorLootGates.Clear(); _floorLoot.Clear(); _floorLootObjectIds.Clear(); _chestHoldScratch.Clear(); foreach (ulong sender in new List<ulong>(_chestHolds.Keys)) RemoveChestHold(sender); if (ReferenceEquals(Current, this)) Current = null; }

        /// <summary>
        /// Atomically moves a real server actor's loadout into the network corpse table. All fallible capture,
        /// model/wire validation and registry capacity checks happen before source mutation; after the atomic
        /// empty-loadout restore, publishing the already-reserved model cannot fail.
        /// </summary>
        public bool TryCommitDeath(int playerId, Vector3 position, Inventory bag, Equipment equipment, out string reason)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            if (_proofRejectDeathCustody)
            {
                reason = "development proof preflight refusal";
                return false;
            }
#endif
            if (_network == null || !_network.IsServer || playerId < 0 || bag == null || equipment == null ||
                !equipment.IsPairedWith(bag))
            {
                reason = "corpse death custody requires a paired loadout on the server";
                return false;
            }
            if (_committedDeathPlayers.Contains(playerId))
            {
                LoadoutSnapshot prior = LoadoutSnapshot.Capture(bag, equipment);
                if (prior != null && prior.bag.entries.Length == 0 && prior.equipment.entries.Length == 0)
                { reason = null; return true; }
                reason = "death was already committed but source loadout is no longer empty";
                return false;
            }
            if (_committingDeathPlayers.Contains(playerId))
            { reason = "this player's death custody transaction is already in progress"; return false; }
            if (!CorpseLootManifest.TryCapture(playerId, position, bag, equipment, out CorpseLootManifest manifest, out reason)) return false;
            if (!CorpseContainerModel.TryCreate(manifest, out CorpseContainerModel model, out reason)) return false;
            LoadoutSnapshot captured = manifest.CopyLoadout();
            bool hasItems = captured.bag.entries.Length > 0 || captured.equipment.entries.Length > 0;
            if (hasItems && (_corpseContainers.Count >= MaxNetworkCorpses || _nextCorpseId == 0 || _nextCorpseId == ulong.MaxValue))
            {
                reason = "server corpse registry is full";
                return false;
            }

            ulong reservedId = hasItems ? _nextCorpseId : 0;
            if (hasItems && !CorpseContainerWire.TryCreate(reservedId, model, out _, out reason)) return false;
            LoadoutSnapshot empty = captured.Clone();
            empty.bag.entries = Array.Empty<ContainerEntry>();
            empty.equipment.entries = Array.Empty<ContainerEntry>();
            empty.weaponSet = 0;
            empty.belt = new[] { "", "", "", "" };
            empty.Seal();
            if (HasCorpseUidOverlap(model))
            { reason = "a gear UID from this loadout is already held by another network corpse"; return false; }
            _committingDeathPlayers.Add(playerId);
            bool restored;
            try
            {
                restored = manifest.StillMatches(bag, equipment) && LoadoutSnapshot.TryRestore(empty, bag, equipment, null, out reason);
                if (!restored && string.IsNullOrEmpty(reason)) reason = "source loadout changed before death custody commit";
            }
            finally { _committingDeathPlayers.Remove(playerId); }
            if (!restored)
            {
                return false;
            }

            _committedDeathPlayers.Add(playerId);
            if (hasItems)
            {
                _nextCorpseId++;
                _corpseContainers.Add(reservedId, model);
                try
                {
                    BroadcastCorpsePresence(reservedId, model, false);
                    BroadcastNearbyCorpseState(reservedId, model, null);
                }
                catch (Exception e) { _log?.Invoke("death corpse committed but immediate notification failed id=" + reservedId + " error=" + e.Message); }
                _log?.Invoke("death corpse committed id=" + reservedId + " source=" + playerId + " entries=" + model.EntryCount);
            }
            reason = null;
            return true;
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        public void SetDeathCustodyRefusalForProof(bool refused) => _proofRejectDeathCustody = refused;

        internal bool OpenNewestLootForReview(IInteractionActor actor, string kind)
        {
            ulong newest = 0;
            foreach (var pair in _corpseContainers) if (pair.Value.Kind == kind && pair.Key > newest) newest = pair.Key;
            if (newest == 0) return false;
            OpenLoot(actor, newest); return true;
        }

        public bool TryFindCorpseUidForProof(string uidText, out ulong corpseId)
        {
            return TryFindCorpseUidForProof(-1, uidText, out corpseId);
        }

        public bool TryFindCorpseUidForProof(int sourcePlayerId, string uidText, out ulong corpseId)
        {
            corpseId = 0;
            if (!Guid.TryParseExact(uidText, "N", out Guid uid)) return false;
            foreach (KeyValuePair<ulong, CorpseContainerModel> pair in _corpseContainers)
            {
                if (sourcePlayerId >= 0 && pair.Value.SourcePlayerId != sourcePlayerId) continue;
                CorpseItemView[] rows = pair.Value.CopyEntries();
                for (int i = 0; i < rows.Length; i++)
                    if (Guid.TryParseExact(rows[i].Uid, "N", out Guid held) && held == uid)
                    { corpseId = pair.Key; return true; }
            }
            return false;
        }
#endif

        /// <summary>Registers a server-owned model, primarily for development fixtures; real death uses TryCommitDeath.</summary>
        public bool RegisterCorpse(CorpseContainerModel model, out ulong corpseId, out string reason)
        {
            corpseId = 0;
            if (_network == null || !_network.IsServer || model == null || model.EntryCount <= 0)
            {
                reason = "corpse registration requires a non-empty model on the server";
                return false;
            }
            if (_corpseContainers.Count >= MaxNetworkCorpses || _nextCorpseId == 0 || _nextCorpseId == ulong.MaxValue)
            {
                reason = "server corpse registry is full";
                return false;
            }
            if (HasCorpseUidOverlap(model))
            {
                reason = "corpse contains a gear UID already held by another network corpse";
                return false;
            }
            if (!CorpseContainerWire.TryCreate(_nextCorpseId, model, out _, out reason)) return false;
            corpseId = _nextCorpseId++;
            _corpseContainers.Add(corpseId, model);
            BroadcastCorpsePresence(corpseId, model, false);
            BroadcastNearbyCorpseState(corpseId, model, null);
            reason = null;
            _log?.Invoke("corpse registered id=" + corpseId + " source=" + model.SourcePlayerId + " entries=" + model.EntryCount);
            return true;
        }

        public bool TryPublishLoot(CorpseContainerModel model, out ulong id, out string reason) => RegisterCorpse(model, out id, out reason);

        public void OpenLoot(IInteractionActor actor, ulong id)
        {
            if (actor == null || !_corpseContainers.TryGetValue(id, out CorpseContainerModel model)) return;
            foreach (NetIdentity row in _identities.All)
            {
                if (row?.Rig == null || !ReferenceEquals(row.Rig, actor) || !ValidateCorpseActor(row, model, out _)) continue;
                if (IsHostOwner(row)) row.Object.GetComponent<CorpseLootWorldPresenter>()?.OpenContainer(id);
                else row.Object.GetComponent<NetPlayerRig>()?.OpenLootContainerRpc(id);
                return;
            }
        }

        private bool HasCorpseUidOverlap(CorpseContainerModel candidate)
        {
            if (candidate == null) return false;
            CorpseItemView[] incoming = candidate.CopyEntries();
            HashSet<Guid> seen = new HashSet<Guid>();
            for (int i = 0; i < incoming.Length; i++)
                if (!string.IsNullOrEmpty(incoming[i].Uid) &&
                    (!Guid.TryParseExact(incoming[i].Uid, "N", out Guid uid) || !seen.Add(uid))) return true;
            foreach (CorpseContainerModel existing in _corpseContainers.Values)
            {
                CorpseItemView[] held = existing.CopyEntries();
                for (int i = 0; i < held.Length; i++)
                    if (!string.IsNullOrEmpty(held[i].Uid) && Guid.TryParseExact(held[i].Uid, "N", out Guid uid) && seen.Contains(uid)) return true;
            }
            return false;
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        /// <summary>Uses the exact server admission, life, reach and LOS checks for a dev-only network proof fixture.</summary>
        public bool ValidateCorpseActorForProof(NetIdentity row, CorpseContainerModel model, out string reason) =>
            ValidateCorpseActor(row, model, out reason);
#endif

        public void SendCorpsePresenceTo(ulong clientId)
        {
            if (_network == null || !_network.IsServer || _identities == null ||
                !_identities.TryGetByClient(clientId, out NetIdentity row)) return;
            SendCorpsePresenceSnapshot(row);
            SendFloorLootSnapshot(row);
        }

        private bool TryResolve(ulong sender, NetworkObject target, out NetIdentity row)
        {
            row = null;
            return _network != null && _network.IsServer && _identities != null &&
                _identities.TryGetByClient(sender, out row) && row != null && row.ClientId == sender &&
                target != null && target.IsSpawned && target.OwnerClientId == sender && ReferenceEquals(target, row.Object) &&
                row.Rig != null && row.PlayerId == row.Rig.PlayerId && ReferenceEquals(target.GetComponent<Emberdelve.Player.PlayerRig>(), row.Rig);
        }

        public void RequestSnapshot(ulong sender, NetworkObject target)
        {
            if (_skills != null && _skills.DropIfDisconnecting(sender)) return;
            if (!TryResolve(sender, target, out var row)) { Charge(sender, SkillRejectKind.Identity, "snapshot identity", false); return; }
            double now = Time.realtimeSinceStartupAsDouble;
            if (_lastSnapshotRequest.TryGetValue(sender, out double last) && now - last < 1.0)
            { Charge(sender, SkillRejectKind.Flood, "snapshot rate", false); return; }
            _lastSnapshotRequest[sender] = now;
            SendSnapshot(row);
            SendChestSnapshot(row);
            SendCorpsePresenceSnapshot(row);
            SyncFloorLoot();
            SendFloorLootSnapshot(row);
        }

        public bool RequestFloorLootPickup(ulong sender, NetworkObject target, ulong pickupId, uint sequence)
        {
            if (_skills != null && _skills.DropIfDisconnecting(sender)) return false;
            if (!TryResolve(sender, target, out NetIdentity row))
            { Charge(sender, SkillRejectKind.Identity, "floor loot identity", false); _log?.Invoke("floor loot reject=Identity client=" + sender + " item=" + pickupId); return false; }
            if (!_floorLootGates.TryGetValue(sender, out FloorLootIntentGate gate))
            { gate = new FloorLootIntentGate(); _floorLootGates.Add(sender, gate); }
            if (!gate.TryAccept(sequence, Time.realtimeSinceStartupAsDouble, out string reason))
            { Charge(sender, SkillRejectKind.Replay, "floor loot sequence=" + sequence, false); _log?.Invoke("floor loot reject=Replay client=" + sender + " seq=" + sequence); return false; }
            SyncFloorLoot();
            RunDirector director = Object.FindFirstObjectByType<RunDirector>();
            if (director == null || !row.Rig.IsAlive || !director.IsInteractionActorAdmitted(row.Rig, out reason))
            {
                if (string.IsNullOrEmpty(reason)) reason = director == null ? "Run is unavailable" : "The dead cannot interact";
                _log?.Invoke("floor loot reject=Authority client=" + sender + " seq=" + sequence + " item=" + pickupId + " reason=" + reason.Replace(' ', '_'));
                SendFloorLootSnapshot(row);
                return false;
            }
            if (pickupId == 0 || !_floorLoot.TryGetValue(pickupId, out LootPickup pickup) || pickup == null || !pickup.isActiveAndEnabled)
            { _log?.Invoke("floor loot reject=Unavailable client=" + sender + " seq=" + sequence + " item=" + pickupId); SendFloorLootSnapshot(row); return false; }
            if (!ValidateFloorLootReach(row.Rig, pickup, director.InteractionReach, out reason) ||
                !pickup.CanInteract(row.Rig, out reason))
            {
                if (string.IsNullOrEmpty(reason)) reason = "Player cannot interact";
                _log?.Invoke("floor loot reject=Authority client=" + sender + " seq=" + sequence + " item=" + pickupId + " reason=" + reason.Replace(' ', '_'));
                SendFloorLootSnapshot(row);
                return false;
            }
            bool accepted = pickup.TryCollectFromNetwork(row.Rig);
            SyncFloorLoot();
            SendFloorLootSnapshot(row);
            _log?.Invoke("floor loot pickup sender=" + sender + " id=" + row.PlayerId + " item=" + pickupId + " uid=" +
                (pickup.Instance != null ? pickup.Instance.UidText : "-") + " accepted=" + accepted);
            return accepted;
        }

        public void RequestCorpseState(ulong sender, NetworkObject target, ulong corpseId, uint sequence)
        {
            if (_skills != null && _skills.DropIfDisconnecting(sender)) return;
            if (!TryResolve(sender, target, out NetIdentity row))
            {
                Charge(sender, SkillRejectKind.Identity, "corpse state identity", false);
                return;
            }
            if (!TryConsumeCorpseSequence(sender, sequence, out string reason))
            {
                Charge(sender, SkillRejectKind.Replay, "corpse state sequence=" + sequence, false);
                SendCorpseResult(row, corpseId, sequence, false, 0, CurrentBagRevision(row), reason);
                return;
            }
            if (!TryAcceptCorpseRate(_corpseStateRates, sender, CorpseStateInterval, out reason))
            {
                Charge(sender, SkillRejectKind.Flood, "corpse state rate", false);
                SendCorpseResult(row, corpseId, sequence, false, 0, CurrentBagRevision(row), reason);
                return;
            }
            if (!_corpseContainers.TryGetValue(corpseId, out CorpseContainerModel model) || model == null)
            {
                SendCorpseResult(row, corpseId, sequence, false, 0, CurrentBagRevision(row), "Corpse is unavailable");
                return;
            }
            if (!ValidateCorpseActor(row, model, out reason))
            {
                SendCorpseResult(row, corpseId, sequence, false, model.Revision, CurrentBagRevision(row), reason);
                return;
            }
            if (!SendCorpseState(row, corpseId, model, out reason))
            {
                SendCorpseResult(row, corpseId, sequence, false, model.Revision, CurrentBagRevision(row), reason);
                return;
            }
            SendCorpseResult(row, corpseId, sequence, true, model.Revision, CurrentBagRevision(row), string.Empty);
                if (model.Kind == "chest")
                    foreach (ChestInteractable chest in Object.FindObjectsByType<ChestInteractable>(FindObjectsSortMode.None))
                        if (ReferenceEquals(chest.LootContainer, model)) { chest.NotifyLootChanged(); BroadcastChestState(chest); }
        }

        public void RequestCorpseTransfer(ulong sender, NetworkObject target, ulong corpseId, ulong entryId,
            int amount, ulong expectedCorpseRevision, ulong expectedBagRevision, uint sequence)
        {
            if (_skills != null && _skills.DropIfDisconnecting(sender)) return;
            if (!TryResolve(sender, target, out NetIdentity row))
            {
                Charge(sender, SkillRejectKind.Identity, "corpse transfer identity", false);
                return;
            }
            if (!TryConsumeCorpseSequence(sender, sequence, out string reason))
            {
                Charge(sender, SkillRejectKind.Replay, "corpse transfer sequence=" + sequence, false);
                SendCorpseResult(row, corpseId, sequence, false, 0, CurrentBagRevision(row), reason);
                return;
            }
            if (!TryAcceptCorpseRate(_corpseTransferRates, sender, CorpseTransferInterval, out reason))
            {
                Charge(sender, SkillRejectKind.Flood, "corpse transfer rate", false);
                SendCorpseResult(row, corpseId, sequence, false, 0, CurrentBagRevision(row), reason);
                return;
            }
            if (!_corpseContainers.TryGetValue(corpseId, out CorpseContainerModel model) || model == null)
            {
                SendCorpseResult(row, corpseId, sequence, false, 0, CurrentBagRevision(row), "Corpse is unavailable");
                return;
            }
            if (!ValidateCorpseActor(row, model, out reason))
            {
                SendCorpseResult(row, corpseId, sequence, false, model.Revision, CurrentBagRevision(row), reason);
                return;
            }
            if (!TryBuildProfileUidRegistry(row.Rig, out ItemUidRegistry profileUids, out reason))
            {
                SendCorpseResult(row, corpseId, sequence, false, model.Revision, CurrentBagRevision(row), reason);
                return;
            }

            bool accepted = model.TryTransferToBag(entryId, amount, expectedCorpseRevision,
                row.Rig.Inventory, expectedBagRevision, profileUids, out reason);
            if (accepted)
            {
                SendCorpseResult(row, corpseId, sequence, true, model.Revision, CurrentBagRevision(row), string.Empty);
                if (model.Kind == "chest")
                    foreach (ChestInteractable chest in Object.FindObjectsByType<ChestInteractable>(FindObjectsSortMode.None))
                        if (ReferenceEquals(chest.LootContainer, model)) { chest.NotifyLootChanged(); BroadcastChestState(chest); }
                BroadcastCorpsePresence(corpseId, model, model.EntryCount == 0);
                if (model.EntryCount == 0)
                {
                    _corpseContainers.Remove(corpseId);
                }
                else
                {
                    BroadcastNearbyCorpseState(corpseId, model, row.ClientId);
                    SendCorpseState(row, corpseId, model, out _);
                }
                _log?.Invoke("corpse transfer sender=" + sender + " corpse=" + corpseId + " entry=" + entryId +
                             " amount=" + amount + " accepted=true corpseRevision=" + model.Revision +
                             " bagRevision=" + CurrentBagRevision(row));
                return;
            }

            SendCorpseResult(row, corpseId, sequence, false, model.Revision, CurrentBagRevision(row), reason);
            // A failed stale/capacity/UID request receives the current state so its owner can reconcile and retry.
            SendCorpseState(row, corpseId, model, out _);
            _log?.Invoke("corpse transfer sender=" + sender + " corpse=" + corpseId + " entry=" + entryId +
                         " amount=" + amount + " accepted=false reason=" + reason);
        }

        public void RequestChestOpen(ulong sender, NetworkObject target, FixedString128Bytes requestedId)
        {
            string chestId = requestedId.ToString();
            if (_skills != null && _skills.DropIfDisconnecting(sender)) return;
            if (!TryResolve(sender, target, out NetIdentity row))
            {
                Charge(sender, SkillRejectKind.Identity, "chest identity", false);
                if (_identities != null && _identities.TryGetByClient(sender, out NetIdentity owner))
                    SendChestResult(owner, false, "Chest request is not yours", chestId);
                return;
            }

            if (!ChestInteractable.IsValidNetworkChestId(chestId))
            {
                Charge(sender, SkillRejectKind.Invalid, "chest id", false);
                SendChestResult(row, false, "Invalid chest request", chestId);
                return;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            if (_lastChestIntent.TryGetValue(sender, out double last) && now - last < ChestIntentInterval)
            {
                Charge(sender, SkillRejectKind.Flood, "chest rate", false);
                SendChestResult(row, false, "Please wait before interacting again", chestId);
                return;
            }
            _lastChestIntent[sender] = now;

            if (!TryFindChest(chestId, out ChestInteractable chest, out string reason))
            {
                _log?.Invoke("chest reject sender=" + sender + " id=" + chestId + " reason=" + reason);
                SendChestResult(row, false, "Chest is unavailable", chestId);
                return;
            }

            if (chest.RequiresHold)
            {
                SendChestResult(row, false, "Hold to open this chest", chestId);
                return;
            }

            // Open() runs on the server's admitted PlayerRig. Its existing interaction rules re-check life,
            // membership, authoritative transform distance, LOS and sealed-chest requirements before rolling.
            if (!chest.CanInteract(row.Rig, out reason))
            {
                SendChestResult(row, false, reason, chestId);
                BroadcastChestState(chest);
                _log?.Invoke("chest-intent sender=" + sender + " player=" + row.PlayerId + " chest=" + chestId +
                             " accepted=False reason=" + reason);
                return;
            }
            bool opened = chest.Open(row.Rig);
            SendChestResult(row, opened, opened ? "" : "Chest could not be opened", chestId);
            _log?.Invoke("chest-intent sender=" + sender + " player=" + row.PlayerId + " chest=" + chestId +
                         " accepted=" + opened + " opened=" + chest.Opened + " remaining=" + chest.RemainingUnits);
            BroadcastChestState(chest);
        }

        public void RequestDoorSnapshot(ulong sender, NetworkObject target)
        {
            if (!TryResolve(sender, target, out NetIdentity row)) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (_lastDoorSnapshotRequest.TryGetValue(sender, out double last) && now - last < 1.0) return;
            _lastDoorSnapshotRequest[sender] = now;
            SendDoorSnapshot(row);
        }

        public void RequestDoorToggle(ulong sender, NetworkObject target, FixedString128Bytes requestedId)
        {
            string doorId = requestedId.ToString();
            if (_skills != null && _skills.DropIfDisconnecting(sender)) return;
            if (!TryResolve(sender, target, out NetIdentity row) || !DungeonDoorInteractable.IsValidDoorId(doorId))
            { Charge(sender, SkillRejectKind.Identity, "door identity", false); LogDoorProof(sender, doorId, false, "identity", null); return; }
            double now = Time.realtimeSinceStartupAsDouble;
            if (_lastDoorIntent.TryGetValue(sender, out double last) && now - last < 0.3)
            { Charge(sender, SkillRejectKind.Flood, "door rate", false); LogDoorProof(sender, doorId, false, "rate", null); return; }
            _lastDoorIntent[sender] = now;
            if (!TryFindDoor(doorId, out DungeonDoorInteractable door)) { LogDoorProof(sender, doorId, false, "unavailable", null); return; }
            if (!door.CanInteract(row.Rig, out string reason)) { LogDoorProof(sender, doorId, false, reason, door); return; }
            door.ToggleAuthoritatively();
            unchecked { _doorRevision++; }
            BroadcastDoorState(door);
            LogDoorProof(sender, doorId, true, string.Empty, door);
        }

        /// <summary>Publishes an enemy opening through the same revision stream used by player door intents.</summary>
        public bool RequestEnemyDoorOpen(string doorId)
        {
            if (_network == null || !_network.IsServer || !DungeonDoorInteractable.IsValidDoorId(doorId) ||
                !TryFindDoor(doorId, out DungeonDoorInteractable door) || !door.OpenAuthoritatively()) return false;
            unchecked { _doorRevision++; }
            BroadcastDoorState(door);
            return true;
        }

        private void LogDoorProof(ulong sender, string doorId, bool accepted, string reason, DungeonDoorInteractable door)
        {
            if (!_doorProofEnabled) return;
            _log?.Invoke("door-proof request sender=" + sender + " id=" + (doorId ?? string.Empty) +
                         " accepted=" + accepted + " reason=" + (string.IsNullOrEmpty(reason) ? "-" : reason.Replace(' ', '_')) +
                         " open=" + (door != null && door.IsOpen) + " revision=" + _doorRevision +
                         " blocker=" + (door != null && door.BlockerEnabled));
        }

        private void SendDoorSnapshot(NetIdentity row)
        {
            if (row?.Object == null || !row.Object.IsSpawned) return;
            NetPlayerRig view = row.Object.GetComponent<NetPlayerRig>();
            if (view == null) return;
            view.DoorStateRpc(default, false, _doorRevision, true, false);
            DungeonDoorInteractable[] doors = UnityEngine.Object.FindObjectsByType<DungeonDoorInteractable>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < doors.Length; i++)
                if (doors[i] != null && DungeonDoorInteractable.IsValidDoorId(doors[i].DoorId))
                    view.DoorStateRpc(new FixedString128Bytes(doors[i].DoorId), doors[i].IsOpen, _doorRevision, false, false);
            view.DoorStateRpc(default, false, _doorRevision, false, true);
            if (_doorProofEnabled)
                _log?.Invoke("door-proof snapshot client=" + row.ClientId + " doors=" + UnityEngine.Object.FindObjectsByType<DungeonDoorInteractable>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length + " revision=" + _doorRevision);
        }

        private void BroadcastDoorState(DungeonDoorInteractable door)
        {
            if (door == null) return;
            foreach (NetIdentity row in _identities.All)
            {
                if (row?.Object == null || !row.Object.IsSpawned) continue;
                NetPlayerRig view = row.Object.GetComponent<NetPlayerRig>();
                view?.DoorStateRpc(new FixedString128Bytes(door.DoorId), door.IsOpen, _doorRevision, false, false);
            }
        }

        private static bool TryFindDoor(string doorId, out DungeonDoorInteractable door)
        {
            DungeonDoorInteractable[] all = UnityEngine.Object.FindObjectsByType<DungeonDoorInteractable>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && string.Equals(all[i].DoorId, doorId, StringComparison.Ordinal)) { door = all[i]; return true; }
            door = null;
            return false;
        }

        public void BeginChestHold(ulong sender, NetworkObject target, FixedString128Bytes requestedId, uint sequence)
        {
            if (_skills != null && _skills.DropIfDisconnecting(sender)) return;
            if (!TryResolve(sender, target, out NetIdentity row))
            {
                Charge(sender, SkillRejectKind.Identity, "chest hold identity", false);
                if (_identities != null && _identities.TryGetByClient(sender, out NetIdentity owner))
                    SendChestResult(owner, false, "Chest request is not yours");
                return;
            }

            string chestId = requestedId.ToString();
            if (!TryConsumeChestHoldSequence(sender, sequence))
            {
                Charge(sender, SkillRejectKind.Replay, "chest hold sequence=" + sequence, false);
                SendChestResult(row, false, "Stale chest hold request", chestId, sequence);
                return;
            }
            if (!ChestInteractable.IsValidNetworkChestId(chestId) ||
                !TryFindChest(chestId, out ChestInteractable chest, out string reason))
            {
                SendChestResult(row, false, "Chest is unavailable", chestId, sequence);
                return;
            }
            double now = Time.realtimeSinceStartupAsDouble;
            if (_lastChestHoldStart.TryGetValue(sender, out double lastStart) && now - lastStart < ChestIntentInterval)
            {
                Charge(sender, SkillRejectKind.Flood, "chest hold rate", false);
                SendChestResult(row, false, "Please wait before holding again", chestId, sequence);
                return;
            }
            _lastChestHoldStart[sender] = now;
            if (!chest.RequiresHold)
            {
                SendChestResult(row, false, "This chest opens instantly", chestId, sequence);
                return;
            }
            if (!chest.CanInteract(row.Rig, out reason))
            {
                SendChestResult(row, false, reason, chestId, sequence);
                return;
            }
            if (_chestHolds.ContainsKey(sender))
            {
                SendChestResult(row, false, "Another chest hold is already active", chestId, sequence);
                return;
            }

            if (!ChestHoldWindow.TryStart(chestId, now, chest.ResolveHoldDuration(row.Rig), chest.StateRevision,
                    out ChestHoldWindow window))
            {
                SendChestResult(row, false, "Chest hold duration is invalid", chestId, sequence);
                return;
            }
            var pending = new PendingChestHold { Chest = chest, Window = window, Sequence = sequence,
                LastHeartbeatAt = now, Health = row.Rig.Health };
            if (pending.Health != null)
            {
                pending.DamageHandler = _ => InterruptChestHold(sender, sequence, "Damage interrupted the chest hold");
                pending.Health.Damaged += pending.DamageHandler;
            }
            _chestHolds.Add(sender, pending);
            _log?.Invoke("chest-hold begin sender=" + sender + " chest=" + chestId + " seq=" + sequence + " duration=" + window.Duration);
        }

        public void CancelChestHold(ulong sender, NetworkObject target, FixedString128Bytes requestedId, uint sequence)
        {
            if (!TryResolve(sender, target, out _))
            {
                Charge(sender, SkillRejectKind.Identity, "chest hold cancel identity", false);
                return;
            }
            if (!_chestHolds.TryGetValue(sender, out PendingChestHold pending)) return;
            if (pending.Sequence == sequence && string.Equals(pending.Window.ChestId, requestedId.ToString(), System.StringComparison.Ordinal))
            {
                RemoveChestHold(sender);
                _log?.Invoke("chest-hold cancel sender=" + sender + " chest=" + pending.Window.ChestId);
            }
        }

        public void HeartbeatChestHold(ulong sender, NetworkObject target, FixedString128Bytes requestedId, uint sequence)
        {
            if (!TryResolve(sender, target, out NetIdentity row))
            {
                Charge(sender, SkillRejectKind.Identity, "chest hold heartbeat identity", false);
                return;
            }
            if (!_chestHolds.TryGetValue(sender, out PendingChestHold pending) || pending == null)
            {
                if (_lastChestHoldSequence.TryGetValue(sender, out uint last) && last == sequence &&
                    (!_lastChestHeartbeatRefusalSequence.TryGetValue(sender, out uint reported) || reported != sequence))
                {
                    _lastChestHeartbeatRefusalSequence[sender] = sequence;
                    SendChestResult(row, false, "Chest hold expired; refreshing state", requestedId.ToString(), sequence);
                }
                return;
            }
            if (pending.Sequence != sequence || !string.Equals(pending.Window.ChestId, requestedId.ToString(), System.StringComparison.Ordinal)) return;
            pending.LastHeartbeatAt = Time.realtimeSinceStartupAsDouble;
        }

        public void CompleteChestHold(ulong sender, NetworkObject target, FixedString128Bytes requestedId, uint sequence)
        {
            if (_skills != null && _skills.DropIfDisconnecting(sender)) return;
            if (!TryResolve(sender, target, out NetIdentity row))
            {
                Charge(sender, SkillRejectKind.Identity, "chest hold complete identity", false);
                if (_identities != null && _identities.TryGetByClient(sender, out NetIdentity owner))
                    SendChestResult(owner, false, "Chest request is not yours");
                return;
            }
            if (!_chestHolds.TryGetValue(sender, out PendingChestHold pending) || pending == null || pending.Chest == null)
            {
                // A completion from an already-cancelled lease is stale. Reporting it could cancel a newer
                // local hold after its Begin has already reached the server.
                return;
            }

            string chestId = requestedId.ToString();
            if (pending.Sequence != sequence || !string.Equals(pending.Window.ChestId, chestId, System.StringComparison.Ordinal))
            {
                _log?.Invoke("chest-hold reject stale complete sender=" + sender + " seq=" + sequence);
                return;
            }
            ChestHoldVerdict verdict = pending.Window.Evaluate(chestId, pending.Chest.StateRevision, Time.realtimeSinceStartupAsDouble);
            if (verdict == ChestHoldVerdict.TooEarly)
            {
                // Completion may arrive before the authoritative duration because client timing and RPC
                // delivery are independent. Queue it; Tick opens only after the server clock reaches the lease.
                pending.CompletionRequested = true;
                return;
            }
            if (verdict != ChestHoldVerdict.Valid)
            {
                RemoveChestHold(sender);
                SendChestResult(row, false, "Chest hold was interrupted", pending.Window.ChestId, pending.Sequence);
                return;
            }
            if (!pending.Chest.CanInteract(row.Rig, out string reason))
            {
                RemoveChestHold(sender);
                SendChestResult(row, false, reason, pending.Window.ChestId, pending.Sequence);
                BroadcastChestState(pending.Chest);
                return;
            }

            RemoveChestHold(sender);
            bool opened = pending.Chest.Open(row.Rig);
            SendChestResult(row, opened, opened ? "" : "Chest could not be opened", chestId, sequence);
            _log?.Invoke("chest-hold complete sender=" + sender + " player=" + row.PlayerId + " chest=" + chestId +
                         " accepted=" + opened + " remaining=" + pending.Chest.RemainingUnits);
            BroadcastChestState(pending.Chest);
        }

        public void Tick(float now)
        {
            if (_chestHolds.Count == 0) { TickFloorLoot(now); return; }
            double serverNow = Time.realtimeSinceStartupAsDouble;
            _chestHoldScratch.Clear();
            foreach (ulong sender in _chestHolds.Keys) _chestHoldScratch.Add(sender);
            for (int i = 0; i < _chestHoldScratch.Count; i++)
            {
                ulong sender = _chestHoldScratch[i];
                if (!_chestHolds.TryGetValue(sender, out PendingChestHold pending)) continue;
                if (pending == null || pending.Chest == null || _identities == null ||
                    !_identities.TryGetByClient(sender, out NetIdentity row) || row == null)
                {
                    RemoveChestHold(sender);
                    continue;
                }
                ChestHoldVerdict verdict = pending.Window.Evaluate(pending.Window.ChestId,
                    pending.Chest.StateRevision, serverNow);
                string reason = null;
                if (verdict != ChestHoldVerdict.TooEarly && verdict != ChestHoldVerdict.Valid)
                    reason = "Chest hold was interrupted";
                else if (!pending.Chest.CanInteract(row.Rig, out reason)) { }

                if (reason == null && !ChestHoldWindow.IsHeartbeatFresh(pending.LastHeartbeatAt, serverNow, 1.25))
                    reason = "Chest hold heartbeat expired";

                if (reason == null && pending.CompletionRequested && verdict == ChestHoldVerdict.Valid)
                {
                    RemoveChestHold(sender);
                    bool opened = pending.Chest.Open(row.Rig);
                    SendChestResult(row, opened, opened ? "" : "Chest could not be opened", pending.Window.ChestId, pending.Sequence);
                    BroadcastChestState(pending.Chest);
                    continue;
                }

                if (reason == null) continue;
                RemoveChestHold(sender);
                SendChestResult(row, false, reason, pending.Window.ChestId, pending.Sequence);
                BroadcastChestState(pending.Chest);
                _log?.Invoke("chest-hold interrupt sender=" + sender + " chest=" + pending.Window.ChestId + " reason=" + reason);
            }
            _chestHoldScratch.Clear();
            TickFloorLoot(now);
        }

        private void TickFloorLoot(float now)
        {
            if (now < _nextFloorLootSyncAt) return;
            SyncFloorLoot();
        }

        private void SendChestSnapshot(NetIdentity row)
        {
            if (row?.Object == null || !row.Object.IsSpawned) return;
            if (!TryGetChests(out ChestInteractable[] chests, out string reason))
            {
                _log?.Invoke("chest snapshot refused client=" + row.ClientId + " reason=" + reason);
                return;
            }
            NetPlayerRig view = row.Object.GetComponent<NetPlayerRig>();
            if (view == null) return;
            view.ChestStateRpc(default, false, false, 0, true, chests.Length == 0);
            for (int i = 0; i < chests.Length; i++)
            {
                ChestInteractable chest = chests[i];
                bool last = i == chests.Length - 1;
                view.ChestStateRpc(new FixedString128Bytes(chest.ChestId), chest.IsRolled, chest.Opened, chest.RemainingUnits, false, last);
            }
        }

        private void SendCorpsePresenceSnapshot(NetIdentity row)
        {
            if (row?.Object == null || !row.Object.IsSpawned) return;
            foreach (KeyValuePair<ulong, CorpseContainerModel> pair in _corpseContainers)
                SendCorpsePresence(row, pair.Key, pair.Value, false);
        }

        private void SyncFloorLoot()
        {
            if (_network == null || !_network.IsServer) return;
            _nextFloorLootSyncAt = Time.unscaledTime + 0.1f;
            RunDirector director = Object.FindFirstObjectByType<RunDirector>();
            // Modular remapping finishes before RunDirector starts. Waiting for Running avoids publishing the
            // authored coordinates during scene assembly, and this first scan seeds all remapped floor pickups once.
            if (director == null || director.State != RunState.Running) return;
            LootPickup[] pickups = Object.FindObjectsByType<LootPickup>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (pickups.Length > MaxFloorLootItems)
            {
                PublishFloorLootUnavailable("scene exceeds " + MaxFloorLootItems + " live pickup limit");
                return;
            }
            var seenObjects = new HashSet<int>();
            var entries = new List<FloorLootEntryDocument>(Mathf.Min(pickups.Length, MaxFloorLootItems));
            for (int i = 0; i < pickups.Length; i++)
            {
                LootPickup pickup = pickups[i];
                if (pickup == null || pickup.IsSpent || pickup.Item == null) continue;
                int objectId = pickup.GetInstanceID();
                seenObjects.Add(objectId);
                if (!_floorLootObjectIds.TryGetValue(objectId, out ulong id))
                {
                    if (_nextFloorLootId == 0 || _nextFloorLootId == ulong.MaxValue) continue;
                    id = _nextFloorLootId++;
                    _floorLootObjectIds.Add(objectId, id);
                    pickup.SetNetworkLootId(id);
                }
                if (entries.Count >= MaxFloorLootItems) continue;
                ItemSnapshot item = pickup.Instance != null ? pickup.Instance.ToSnapshot() :
                    new ItemSnapshot { itemId = pickup.Item.itemId, quantity = pickup.Amount, rarity = 0, affixes = Array.Empty<AffixSnapshot>() };
                entries.Add(new FloorLootEntryDocument { id = id, position = pickup.HomePosition, item = item });
                _floorLoot[id] = pickup;
            }
            var stale = new List<ulong>();
            foreach (var pair in _floorLoot) if (pair.Value == null || !pair.Value.isActiveAndEnabled || pair.Value.IsSpent) stale.Add(pair.Key);
            for (int i = 0; i < stale.Count; i++) _floorLoot.Remove(stale[i]);
            var staleObjects = new List<int>();
            foreach (var pair in _floorLootObjectIds) if (!seenObjects.Contains(pair.Key)) staleObjects.Add(pair.Key);
            for (int i = 0; i < staleObjects.Count; i++) _floorLootObjectIds.Remove(staleObjects[i]);

            entries.Sort((a, b) => a.id.CompareTo(b.id));
            var document = new FloorLootDocument { revision = 0, entries = entries.ToArray() };
            string content = JsonUtility.ToJson(document);
            if (string.Equals(content, _lastFloorLootContent, StringComparison.Ordinal)) return;
            if (Encoding.UTF8.GetByteCount(content) > FloorLootWire.MaxUtf8Bytes)
            {
                PublishFloorLootUnavailable("payload exceeds " + FloorLootWire.MaxUtf8Bytes + " byte limit");
                return;
            }
            _lastFloorLootContent = content;
            if (_floorLootRevision == ulong.MaxValue)
            { _log?.Invoke("floor loot revision exhausted; refusing to publish state"); return; }
            _floorLootRevision++;
            document.revision = _floorLootRevision;
            var wire = new FloorLootWire { Json = JsonUtility.ToJson(document) };
            foreach (NetIdentity row in _identities.All) SendFloorLoot(row, wire);
            _floorLootUnavailable = false;
            _log?.Invoke("floor loot state revision=" + _floorLootRevision + " entries=" + entries.Count);
        }

        private void PublishFloorLootUnavailable(string reason)
        {
            if (_floorLootUnavailable) { _floorLoot.Clear(); return; }
            _log?.Invoke("floor loot state unavailable: " + reason + "; clients receive an empty snapshot until capacity recovers");
            _floorLoot.Clear();
            if (_floorLootRevision == ulong.MaxValue) return;
            _floorLootRevision++;
            var empty = new FloorLootDocument { revision = _floorLootRevision, entries = Array.Empty<FloorLootEntryDocument>() };
            _lastFloorLootContent = JsonUtility.ToJson(new FloorLootDocument { revision = 0, entries = Array.Empty<FloorLootEntryDocument>() });
            var wire = new FloorLootWire { Json = JsonUtility.ToJson(empty) };
            foreach (NetIdentity row in _identities.All) SendFloorLoot(row, wire);
            _floorLootUnavailable = true;
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        public bool TryGetFloorLootProofId(string uid, out ulong pickupId)
        {
            SyncFloorLoot();
            pickupId = 0;
            foreach (var pair in _floorLoot)
                if (pair.Value != null && pair.Value.Instance != null && pair.Value.Instance.UidText == uid)
                { pickupId = pair.Key; return true; }
            return false;
        }

        public int CountFloorLootProofUid(string uid)
        {
            SyncFloorLoot();
            int count = 0;
            foreach (LootPickup pickup in _floorLoot.Values)
                if (pickup != null && pickup.Instance != null && string.Equals(pickup.Instance.UidText, uid, StringComparison.Ordinal)) count++;
            return count;
        }

        public bool TryGetFloorLootProofPosition(ulong pickupId, out Vector3 position)
        {
            SyncFloorLoot();
            if (_floorLoot.TryGetValue(pickupId, out LootPickup pickup) && pickup != null)
            { position = pickup.HomePosition; return true; }
            position = Vector3.zero;
            return false;
        }

        public bool TryGetFloorLootProofPickup(ulong pickupId, out LootPickup pickup)
        {
            SyncFloorLoot();
            return _floorLoot.TryGetValue(pickupId, out pickup) && pickup != null && pickup.isActiveAndEnabled && !pickup.IsSpent;
        }

        public bool SendFloorLootProofIntent(ulong sender, NetworkObject target, ulong pickupId, uint sequence) =>
            RequestFloorLootPickup(sender, target, pickupId, sequence);
#endif

        private void SendFloorLootSnapshot(NetIdentity row)
        {
            if (row?.Object == null || !row.Object.IsSpawned) return;
            var document = new FloorLootDocument { revision = _floorLootRevision, entries = new List<FloorLootEntryDocument>().ToArray() };
            foreach (var pair in _floorLoot)
            {
                LootPickup pickup = pair.Value;
                if (pickup == null || pickup.IsSpent || pickup.Item == null) continue;
                ItemSnapshot item = pickup.Instance != null ? pickup.Instance.ToSnapshot() :
                    new ItemSnapshot { itemId = pickup.Item.itemId, quantity = pickup.Amount, rarity = 0, affixes = Array.Empty<AffixSnapshot>() };
                Array.Resize(ref document.entries, document.entries.Length + 1);
                document.entries[document.entries.Length - 1] = new FloorLootEntryDocument { id = pair.Key, position = pickup.HomePosition, item = item };
            }
            string json = JsonUtility.ToJson(document);
            if (Encoding.UTF8.GetByteCount(json) > FloorLootWire.MaxUtf8Bytes) return;
            SendFloorLoot(row, new FloorLootWire { Json = json });
        }

        private static void SendFloorLoot(NetIdentity row, FloorLootWire wire)
        {
            if (row?.Object == null || !row.Object.IsSpawned || IsHostOwner(row)) return;
            row.Object.GetComponent<NetPlayerRig>()?.FloorLootStateRpc(wire);
        }

        private static bool ValidateFloorLootReach(Emberdelve.Player.PlayerRig rig, LootPickup pickup, float reach, out string reason)
        {
            if (rig == null || pickup == null || !rig.IsAlive)
            { reason = "Player or pickup is unavailable"; return false; }
            return pickup.ValidateNetworkInteraction(rig, reach, out reason);
        }

        private bool TryConsumeCorpseSequence(ulong sender, uint sequence, out string reason)
        {
            if (!_corpseSequences.TryGetValue(sender, out CorpseRequestSequenceGate gate))
            {
                gate = new CorpseRequestSequenceGate();
                _corpseSequences.Add(sender, gate);
            }
            return gate.TryAccept(sequence, out reason);
        }

        private static bool TryAcceptCorpseRate(Dictionary<ulong, CorpseRequestRateGate> gates,
            ulong sender, double interval, out string reason)
        {
            if (!gates.TryGetValue(sender, out CorpseRequestRateGate gate))
            {
                gate = new CorpseRequestRateGate(interval);
                gates.Add(sender, gate);
            }
            return gate.TryAccept(Time.realtimeSinceStartupAsDouble, out reason);
        }

        private static bool ValidateCorpseActor(NetIdentity row, CorpseContainerModel model, out string reason)
        {
            if (row?.Rig == null || model == null)
            {
                reason = "Player or corpse is unavailable";
                return false;
            }
            if (!row.Rig.IsAlive)
            {
                reason = "The dead cannot loot a corpse";
                return false;
            }

            RunDirector director = Object.FindFirstObjectByType<RunDirector>();
            if (director == null)
            {
                reason = "The server run is unavailable";
                return false;
            }
            if (!director.IsInteractionActorAdmitted(row.Rig, out reason))
            {
                return false;
            }

            if (model.Kind == "chest")
            {
                ChestInteractable source = null;
                foreach (ChestInteractable chest in Object.FindObjectsByType<ChestInteractable>(FindObjectsSortMode.None))
                    if (ReferenceEquals(chest.LootContainer, model)) { source = chest; break; }
                if (source == null) { reason = "Chest is unavailable"; return false; }
                if (!source.CanInteract(row.Rig, out reason)) return false;
            }
            Vector3 origin = row.Rig.InteractionOrigin;
            Vector3 point = model.WorldPosition;
            if (!IsFinite(origin) || !IsFinite(point))
            {
                reason = "Invalid corpse interaction position";
                return false;
            }
            Vector3 offset = point - origin;
            float distance = offset.magnitude;
            float reach = Mathf.Max(0.5f, director.InteractionReach);
            if (!IsFinite(distance) || distance > reach)
            {
                reason = "Too far away";
                return false;
            }
            if (distance > 0.24f)
            {
                int levelMask = LayerMask.GetMask(GameConfig.LevelLayerName);
                if (levelMask != 0 && Physics.Raycast(origin, offset / distance, out _, distance - 0.12f,
                        levelMask, QueryTriggerInteraction.Ignore))
                {
                    reason = "No line of sight";
                    return false;
                }
            }

            reason = null;
            return true;
        }

        private static bool TryBuildProfileUidRegistry(Emberdelve.Player.PlayerRig rig,
            out ItemUidRegistry registry, out string reason)
        {
            registry = null;
            if (rig?.Inventory == null || rig.Equipment == null)
            {
                reason = "Player inventory or equipment is unavailable";
                return false;
            }
            LoadoutSnapshot snapshot = LoadoutSnapshot.Capture(rig.Inventory, rig.Equipment);
            if (snapshot == null || !snapshot.ChecksumMatches())
            {
                reason = "Player loadout cannot be validated";
                return false;
            }

            var bagUids = new List<Guid>();
            var equipmentUids = new List<Guid>();
            var all = new HashSet<Guid>();
            if (!CollectUids(snapshot.bag, bagUids, all, out reason) ||
                !CollectUids(snapshot.equipment, equipmentUids, all, out reason)) return false;

            var holdings = new Dictionary<string, IReadOnlyCollection<Guid>>
            {
                { Inventory.SnapshotContainerId, bagUids },
                { Equipment.SnapshotContainerId, equipmentUids }
            };
            registry = new ItemUidRegistry();
            if (registry.TryReplaceOwners(holdings, out reason)) return true;
            registry = null;
            return false;
        }

        private static bool CollectUids(ContainerSnapshot container, List<Guid> collected, HashSet<Guid> all, out string reason)
        {
            if (container?.entries == null)
            {
                reason = "Player loadout has a missing container entry list";
                return false;
            }
            for (int i = 0; i < container.entries.Length; i++)
            {
                ContainerEntry entry = container.entries[i];
                if (entry?.item == null)
                {
                    reason = "Player loadout contains an invalid item row";
                    return false;
                }
                string uidText = entry.item.uid;
                if (string.IsNullOrEmpty(uidText)) continue;
                if (!Guid.TryParseExact(uidText, "N", out Guid uid) || uid == Guid.Empty || !all.Add(uid))
                {
                    reason = "Player loadout contains an invalid or repeated gear uid";
                    return false;
                }
                collected.Add(uid);
            }
            reason = null;
            return true;
        }

        private bool SendCorpseState(NetIdentity row, ulong corpseId, CorpseContainerModel model, out string reason)
        {
            if (row?.Object == null || !row.Object.IsSpawned)
            {
                reason = "Owner player object is unavailable";
                return false;
            }
            if (!CorpseContainerWire.TryCreate(corpseId, model, out CorpseContainerWire wire, out reason)) return false;
            NetPlayerRig view = row.Object.GetComponent<NetPlayerRig>();
            if (view == null)
            {
                reason = "Owner network view is unavailable";
                return false;
            }
            if (IsHostOwner(row))
            {
                CorpseLootWorldPresenter presenter = row.Object.GetComponent<CorpseLootWorldPresenter>();
                if (presenter == null)
                {
                    reason = "Host corpse presenter is unavailable";
                    return false;
                }
                presenter.OnCorpseState(wire);
                return true;
            }
            view.CorpseContainerStateRpc(wire);
            return true;
        }

        private void BroadcastNearbyCorpseState(ulong corpseId, CorpseContainerModel model, ulong? excludedClient)
        {
            if (_identities == null || !CorpseContainerWire.TryCreate(corpseId, model, out CorpseContainerWire wire, out _)) return;
            foreach (NetIdentity row in _identities.All)
            {
                if (row == null || (excludedClient.HasValue && row.ClientId == excludedClient.Value) || row.Object == null || !row.Object.IsSpawned ||
                    !ValidateCorpseActor(row, model, out _)) continue;
                if (IsHostOwner(row)) row.Object.GetComponent<CorpseLootWorldPresenter>()?.OnCorpseState(wire);
                else row.Object.GetComponent<NetPlayerRig>()?.CorpseContainerStateRpc(wire);
            }
        }

        private void BroadcastCorpsePresence(ulong corpseId, CorpseContainerModel model, bool removed)
        {
            if (_identities == null) return;
            foreach (NetIdentity row in _identities.All) SendCorpsePresence(row, corpseId, model, removed);
        }

        private static void SendCorpsePresence(NetIdentity row, ulong corpseId, CorpseContainerModel model, bool removed)
        {
            if (model.Kind == "chest" && !removed) return;
            if (row?.Object == null || !row.Object.IsSpawned || model == null) return;
            if (IsHostOwner(row))
            {
                row.Object.GetComponent<CorpseLootWorldPresenter>()?.OnPresence(corpseId, model.WorldPosition, model.Revision, removed);
                return;
            }
            row.Object.GetComponent<NetPlayerRig>()?.CorpsePresenceRpc(corpseId, model.WorldPosition, model.Revision, removed);
        }

        private static bool IsHostOwner(NetIdentity row) => row != null && NetRole.Current == NetRoleKind.Host &&
            row.ClientId == NetworkManager.ServerClientId;

        private static ulong CurrentBagRevision(NetIdentity row) => row?.Rig?.Inventory != null ? row.Rig.Inventory.Revision : 0;

        private static void SendCorpseResult(NetIdentity row, ulong corpseId, uint sequence, bool accepted,
            ulong corpseRevision, ulong bagRevision, string reason)
        {
            if (row?.Object == null || !row.Object.IsSpawned) return;
            NetPlayerRig view = row.Object.GetComponent<NetPlayerRig>();
            if (view == null) return;
            string bounded = reason ?? string.Empty;
            if (bounded.Length > 100) bounded = bounded.Substring(0, 100);
            if (IsHostOwner(row))
            {
                row.Object.GetComponent<CorpseLootWorldPresenter>()?.OnCorpseResult(corpseId, sequence,
                    accepted, corpseRevision, bounded);
                return;
            }
            view.CorpseTransferResultRpc(corpseId, sequence, accepted, corpseRevision, bagRevision,
                new FixedString128Bytes(bounded));
        }

        private static bool IsFinite(Vector3 value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private void BroadcastChestState(ChestInteractable chest)
        {
            if (chest == null || _identities == null) return;
            FixedString128Bytes id = new FixedString128Bytes(chest.ChestId);
            foreach (NetIdentity row in _identities.All)
            {
                if (row?.Object == null || !row.Object.IsSpawned) continue;
                NetPlayerRig view = row.Object.GetComponent<NetPlayerRig>();
                if (view != null) view.ChestStateRpc(id, chest.IsRolled, chest.Opened, chest.RemainingUnits, false, false);
            }
        }

        internal void PublishChestState(ChestInteractable chest) => BroadcastChestState(chest);

        private static void SendChestResult(NetIdentity row, bool accepted, string reason, string chestId = "", uint sequence = 0)
        {
            if (row?.Object == null || !row.Object.IsSpawned) return;
            NetPlayerRig view = row.Object.GetComponent<NetPlayerRig>();
            if (view == null) return;
            string boundedReason = reason ?? "";
            if (boundedReason.Length > 100) boundedReason = boundedReason.Substring(0, 100);
            view.ChestResultRpc(accepted, new FixedString128Bytes(chestId ?? ""), sequence, new FixedString128Bytes(boundedReason));
        }

        private static bool TryFindChest(string chestId, out ChestInteractable found, out string reason)
        {
            found = null;
            if (!TryGetChests(out ChestInteractable[] chests, out reason)) return false;
            for (int i = 0; i < chests.Length; i++)
            {
                if (!string.Equals(chests[i].ChestId, chestId, System.StringComparison.Ordinal)) continue;
                if (found != null)
                {
                    reason = "duplicate chest id";
                    found = null;
                    return false;
                }
                found = chests[i];
            }
            if (found != null) { reason = null; return true; }
            reason = "unknown chest id";
            return false;
        }

        private static bool TryGetChests(out ChestInteractable[] chests, out string reason)
        {
            chests = Object.FindObjectsByType<ChestInteractable>(FindObjectsSortMode.None);
            if (chests.Length > MaxNetworkChests)
            {
                reason = "chest count exceeds limit";
                return false;
            }
            var ids = new HashSet<string>(System.StringComparer.Ordinal);
            for (int i = 0; i < chests.Length; i++)
            {
                if (chests[i] == null || !ChestInteractable.IsValidNetworkChestId(chests[i].ChestId))
                {
                    reason = "invalid chest id in server scene";
                    return false;
                }
                if (!ids.Add(chests[i].ChestId))
                {
                    reason = "duplicate chest id in server scene";
                    return false;
                }
            }
            reason = null;
            return true;
        }

        private void OnInventoryChanged(int playerId)
        {
            if (_identities != null && _identities.TryGetByPlayer(playerId, out var row)) SendSnapshot(row);
        }

        private static void SendSnapshot(NetIdentity row)
        {
            if (row?.Rig == null || row.Object == null || !row.Object.IsSpawned) return;
            var view = row.Object.GetComponent<NetPlayerRig>();
            var state = NetworkLoadoutState.Capture(row.Rig.Inventory, row.Rig.Equipment);
            if (view == null || state == null) return;
            var wire = NetworkLoadoutWire.FromState(state);
            if (wire.LoadoutJson.Length > 16000)
            {
                Debug.LogError("[Emberdelve][Inventory] owner snapshot exceeds transport limit");
                return;
            }
            view.InventoryStateRpc(wire);
        }

        public bool Receive(ulong sender, NetworkObject target, in InventoryIntentWire wire, out InventoryCommandResult result)
        {
            result = default;
            if (_skills != null && _skills.DropIfDisconnecting(sender)) return false;
            if (!TryResolve(sender, target, out NetIdentity row)) { Charge(sender, SkillRejectKind.Identity, "intent identity", false); return false; }

            if (!wire.TryCreateCommand(row.PlayerId, out InventoryCommand command, out string reason))
            { Charge(sender, SkillRejectKind.Invalid, "intent structure", false); return false; }
            if (!_gates.TryGetValue(sender, out InventoryIntentGate gate))
            {
                gate = new InventoryIntentGate();
                _gates.Add(sender, gate);
            }
            SkillIntentVerdict verdict = gate.Submit(command.Sequence, Time.realtimeSinceStartupAsDouble);
            if (verdict != SkillIntentVerdict.Accepted)
            {
                var kind = verdict == SkillIntentVerdict.Invalid ? SkillRejectKind.Invalid :
                    verdict == SkillIntentVerdict.Replay ? SkillRejectKind.Replay : SkillRejectKind.Flood;
                Charge(sender, kind, "intent sequence=" + command.Sequence, gate.Exhausted);
                return false;
            }
            var inventory = row.Rig.Inventory;
            if (inventory == null || inventory.CommandAuthority == null)
            {
                result = Refused("player has no inventory authority");
                return true;
            }
            // The wire sequence belongs to this connection. Local commands (e.g. a server-side weapon swap)
            // share the bag authority but not that wire channel, so allocate its own sequence on the server.
            if (!inventory.CommandAuthority.TryCreateLocalCommand(command.Kind, command.SourceStackId, command.TargetStackId,
                    command.DestinationCell, command.DestinationSlot, command.ExpectedSourceGearUid, command.ExpectedTargetGearUid,
                    out InventoryCommand stamped, out reason))
            {
                result = Refused(reason);
                return true;
            }
            var serverCommand = new InventoryCommand(command.Kind, row.PlayerId, stamped.Sequence, command.ExpectedRevision,
                command.SourceStackId, command.TargetStackId, command.DestinationCell, command.DestinationSlot,
                command.ExpectedSourceGearUid, command.ExpectedTargetGearUid);
            result = inventory.CommandAuthority.Submit(row.Rig, serverCommand);
            if (command.Kind == InventoryCommandKind.Drop)
                _log?.Invoke("inventory-drop-command sender=" + sender + " id=" + row.PlayerId +
                    " uid=" + command.ExpectedSourceGearUid.ToString("N") + " slot=" + command.DestinationSlot +
                    " accepted=" + result.Accepted + " changed=" + result.Changed + " revision=" + result.Revision);
            _log?.Invoke("inventory-command sender=" + sender + " id=" + row.PlayerId + " seq=" + command.Sequence +
                         " accepted=" + result.Accepted + " changed=" + result.Changed + " revision=" + result.Revision);
            return true;
        }

        private static InventoryCommandResult Refused(string reason) => new InventoryCommandResult(false, false, 0, reason);
        private void Charge(ulong sender, SkillRejectKind kind, string detail, bool exhausted) =>
            _skills?.ChargeReject(sender, kind, "inventory", detail, exhausted);

        private bool TryConsumeChestHoldSequence(ulong sender, uint sequence)
        {
            if (sequence == 0) return false;
            uint expected = 1;
            if (_lastChestHoldSequence.TryGetValue(sender, out uint previous))
            {
                if (previous == uint.MaxValue) return false;
                expected = previous + 1;
            }
            if (sequence != expected) return false;
            _lastChestHoldSequence[sender] = sequence;
            return true;
        }

        private void RemoveChestHold(ulong sender)
        {
            if (!_chestHolds.TryGetValue(sender, out PendingChestHold pending)) return;
            if (pending?.Health != null && pending.DamageHandler != null) pending.Health.Damaged -= pending.DamageHandler;
            _chestHolds.Remove(sender);
        }

        private void InterruptChestHold(ulong sender, uint sequence, string reason)
        {
            if (!_chestHolds.TryGetValue(sender, out PendingChestHold pending) || pending.Sequence != sequence) return;
            RemoveChestHold(sender);
            if (_identities != null && _identities.TryGetByClient(sender, out NetIdentity row))
                SendChestResult(row, false, reason, pending.Window.ChestId, pending.Sequence);
        }
    }
}
