using System;
using System.Collections.Generic;
using System.Text;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.Interaction;
using Emberdelve.Level;
using Emberdelve.Items;
using Emberdelve.Net.Rules;
using Emberdelve.Player;
using Emberdelve.UI;
using UnityEngine;
using Unity.Collections;

namespace Emberdelve.Net
{
    /// <summary>Owner-side inventory request queue. Mutations are applied only from server snapshots.</summary>
    public sealed class NetInventoryClient : MonoBehaviour, IInventoryAuthority, IChestInteractionAuthority, IDoorInteractionAuthority
    {
        private NetPlayerRig _owner;
        private Inventory _bag;
        private PlayerRig _rig;
        private ulong _nextSequence = 1;
        private ulong _pendingSequence;
        private ulong _awaitRevision;
        private bool _awaitSnapshot;
        private bool _ready;
        private bool _hasSnapshot;
        private float _deadline;
        private float _nextSync;
        private InventoryScreenController _screen;
        private CorpseLootWorldPresenter _corpsePresenter;
        private readonly Dictionary<ulong, GameObject> _floorLootViews = new Dictionary<ulong, GameObject>();
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private readonly Dictionary<ulong, ItemSnapshot> _floorLootProofItems = new Dictionary<ulong, ItemSnapshot>();
#endif
        private bool _floorLootSnapshotReceived;
        private ulong _floorLootRevision;
        private readonly Dictionary<string, ChestReplicaState> _chestStates = new Dictionary<string, ChestReplicaState>(StringComparer.Ordinal);
        private bool _chestStateReady;
        private bool _buildingChestSnapshot;
        private bool _hasChestSnapshot;
        private readonly Dictionary<string, DoorReplicaState> _doorStates = new Dictionary<string, DoorReplicaState>(StringComparer.Ordinal);
        private bool _doorStateReady;
        private int _doorSnapshotAttempts;
        private float _nextDoorSnapshotRequestAt;
        private const int MaxDoorSnapshotAttempts = 6;
        private const float DoorSnapshotRetrySeconds = 1.25f;
        private uint _doorRevision;
        private string _activeChestHoldId = "";
        private uint _nextChestHoldSequence;
        private uint _nextCorpseSequence = 1;
        private uint _activeChestHoldSequence;
        private string _pendingChestCompletionId = "";
        private uint _pendingChestCompletionSequence;
        private double _pendingChestCompletionStartedAt;
        private float _nextChestHoldHeartbeat;
        private const float ChestHoldHeartbeatSeconds = 0.25f;
        public const double ChestCompletionTimeoutSeconds = 8.0;

        private readonly struct ChestReplicaState
        {
            public readonly bool Rolled;
            public readonly bool Opened;
            public readonly int RemainingUnits;
            public ChestReplicaState(bool rolled, bool opened, int remainingUnits) { Rolled = rolled; Opened = opened; RemainingUnits = remainingUnits; }
        }

        private readonly struct DoorReplicaState
        {
            public readonly bool Open;
            public readonly uint Revision;
            public DoorReplicaState(bool open, uint revision) { Open = open; Revision = revision; }
        }

        public bool IsOpen => (_screen != null && _screen.IsOpen) ||
                              (_corpsePresenter != null && _corpsePresenter.IsContainerUiOpen);
        public bool IsReady => _ready;
        public bool IsOwnerReady => _owner != null && _owner.IsOwner && _owner.IsSpawned && _ready;
        public bool ChestStateReady => _chestStateReady;
        public bool HasChestState(string id) => _chestStateReady && id != null && _chestStates.ContainsKey(id);
        public bool DoorStateReady => _doorStateReady;
        public uint DoorRevision => _doorRevision;
        public ulong FloorLootRevision => _floorLootRevision;
        public bool HasFloorLootPickup(ulong pickupId) => pickupId != 0 && _floorLootViews.TryGetValue(pickupId, out GameObject view) && view != null;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        public bool TryGetFloorLootProofItem(ulong pickupId, out ItemSnapshot item) =>
            _floorLootProofItems.TryGetValue(pickupId, out item);

        public bool TryGetFloorLootProofItemByUid(string uid, out ulong pickupId, out ItemSnapshot item)
        {
            foreach (KeyValuePair<ulong, ItemSnapshot> pair in _floorLootProofItems)
                if (pair.Value != null && string.Equals(pair.Value.uid, uid, StringComparison.Ordinal))
                { pickupId = pair.Key; item = pair.Value; return true; }
            pickupId = 0;
            item = null;
            return false;
        }

        public int CountFloorLootProofUid(string uid)
        {
            int count = 0;
            foreach (ItemSnapshot item in _floorLootProofItems.Values)
                if (item != null && string.Equals(item.uid, uid, StringComparison.Ordinal)) count++;
            return count;
        }
#endif
        public event Action<bool, string, uint, string> ChestResultReceived;
        public event Action<string, bool, uint, bool, bool> DoorStateReceived;
        public event Action<CorpseContainerDocument> CorpseContainerReceived;
        public event Action<ulong, Vector3, ulong, bool> CorpsePresenceReceived;
        public event Action<ulong, uint, bool, ulong, ulong, string> CorpseTransferResultReceived;

        public void Configure(NetPlayerRig owner)
        {
            _owner = owner;
            _rig = owner != null ? owner.Rig : null;
            _bag = _rig != null ? _rig.Inventory : null;
            if (_bag != null) _bag.SetNetworkAuthority(this);
            if (_owner != null) _owner.InventoryResultReceived += OnResult;
            if (!Application.isBatchMode && _rig != null)
            {
                _screen = gameObject.AddComponent<InventoryScreenController>();
                _screen.Configure(null, _rig);
                RunDirector director = FindFirstObjectByType<RunDirector>(FindObjectsInactive.Include);
                _corpsePresenter = gameObject.AddComponent<CorpseLootWorldPresenter>();
                _corpsePresenter.Configure(this, _rig, director != null ? director.InteractionReach : InteractionRules.DefaultReach);
            }
        }

        private void OnDestroy()
        {
            if (_owner != null) _owner.InventoryResultReceived -= OnResult;
            if (_bag != null) _bag.SetNetworkAuthority(null);
            foreach (GameObject view in _floorLootViews.Values) if (view != null) Destroy(view);
            _floorLootViews.Clear();
            if (_screen != null) { if (_screen.IsOpen) _screen.Close(); Destroy(_screen); }
        }

        private bool EnsureCatalog()
        {
            if (_bag == null) return false;
            if (_bag.Catalog != null) return true;
            // On client-only scenes the run director is disabled but retains the authored catalog and grid settings.
            var director = FindFirstObjectByType<RunDirector>(FindObjectsInactive.Include);
            var config = director != null ? director.Config : null;
            if (config == null || config.itemCatalog == null) return false;
            _bag.SetCatalog(config.itemCatalog);
            _bag.ConfigureGrid(config.inventoryGridWidth, config.inventoryGridHeight);
            return true;
        }

        private void Update()
        {
            if (_owner == null || !_owner.IsSpawned)
            {
                _corpsePresenter?.CloseContainerUi(false);
                return;
            }
            if (NetRole.IsClientOnly && _owner.IsOwner && ShouldRequestDoorSnapshot(_doorStateReady, _doorSnapshotAttempts) &&
                Time.unscaledTime >= _nextDoorSnapshotRequestAt)
            {
                _owner.DoorSnapshotRequestRpc();
                _doorSnapshotAttempts++;
                _nextDoorSnapshotRequestAt = Time.unscaledTime + DoorSnapshotRetrySeconds;
            }
            if (NetRole.IsClientOnly && _doorStateReady) ApplyDoorSnapshotToScene();
            if (_rig == null)
            {
                _corpsePresenter?.CloseContainerUi(false);
                return;
            }
            if (_rig.IsDead || _rig.IsExtracted)
            {
                _corpsePresenter?.CloseContainerUi(false);
                return;
            }
            if (NetRole.IsClientOnly && _hasChestSnapshot && !_chestStateReady)
                _chestStateReady = ApplyChestSnapshotToScene();
            if (_rig.Input != null)
            {
                if (_corpsePresenter != null && _corpsePresenter.IsContainerUiOpen)
                {
                    // The paired loot screen owns I/Escape and both panels while open.
                }
                else if (_screen != null)
                {
                    var combat = GetComponent<NetCombatSender>();
                    if (_rig.Input.InventoryPressed && InventoryScreenController.LastCloseFrame != Time.frameCount && (IsOpen || combat == null || combat.CanOpenInventory))
                    {
                        _screen.Toggle();
                        if (IsOpen) GetComponent<NetInputSender>()?.ClearPendingMovement();
                    }
                    _screen.HandlePointer();
                }
            }
            double realtime = Time.realtimeSinceStartupAsDouble;
            if (_pendingChestCompletionSequence != 0 && IsChestCompletionTimedOut(_pendingChestCompletionStartedAt,
                    realtime, ChestCompletionTimeoutSeconds))
            {
                string expiredChestId = _pendingChestCompletionId;
                uint expiredSequence = _pendingChestCompletionSequence;
                _pendingChestCompletionId = "";
                _pendingChestCompletionSequence = 0;
                _pendingChestCompletionStartedAt = 0.0;
                _owner.ChestHoldCancelRpc(new FixedString128Bytes(expiredChestId), expiredSequence);
                _owner.InventorySnapshotRequestRpc();
                _ready = false;
                _chestStateReady = false;
                _nextSync = 0f;
                if (_rig != null) GameEvents.RaiseToast(_rig.PlayerId, "Chest response timed out; syncing state");
            }
            if (_pendingChestCompletionSequence != 0 && Time.unscaledTime >= _nextChestHoldHeartbeat)
            {
                _nextChestHoldHeartbeat = Time.unscaledTime + ChestHoldHeartbeatSeconds;
                _owner.ChestHoldHeartbeatRpc(new FixedString128Bytes(_pendingChestCompletionId), _pendingChestCompletionSequence);
            }
            float now = Time.unscaledTime;
            if (_pendingSequence != 0 && now >= _deadline)
            {
                _pendingSequence = 0;
                _awaitSnapshot = false;
                _ready = false;
                GameEvents.RaiseToast(_rig.PlayerId, "Inventory response timed out; refreshing");
            }
            bool needsSnapshot = (!_ready && EnsureCatalog()) || !_chestStateReady;
            if (needsSnapshot && now >= _nextSync)
            {
                _nextSync = now + 2f;
                _owner.InventorySnapshotRequestRpc();
            }
        }

        public bool RequestDoorToggle(string doorId)
        {
            if (_owner == null || !_owner.IsOwner || !_owner.IsSpawned ||
                (NetRole.IsClientOnly && !_doorStateReady) || !DungeonDoorInteractable.IsValidDoorId(doorId)) return false;
            _owner.DoorToggleIntentRpc(new FixedString128Bytes(doorId));
            return true;
        }

        public static bool ShouldRequestDoorSnapshot(bool ready, int attempts) => !ready && attempts < MaxDoorSnapshotAttempts;

        public void OnDoorState(string doorId, bool isOpen, uint revision, bool snapshotStart, bool snapshotEnd)
        {
            if (snapshotStart)
            {
                _doorStates.Clear();
                _doorStateReady = false;
                _doorRevision = revision;
                DoorStateReceived?.Invoke(string.Empty, false, revision, true, false);
            }
            if (!string.IsNullOrEmpty(doorId))
            {
                if (!DungeonDoorInteractable.IsValidDoorId(doorId) || revision < _doorRevision) return;
                _doorStates[doorId] = new DoorReplicaState(isOpen, revision);
                _doorRevision = revision;
            }
            if (snapshotEnd) _doorStateReady = true;
            if (_doorStateReady) ApplyDoorSnapshotToScene(immediate: snapshotEnd);
            if (!string.IsNullOrEmpty(doorId)) DoorStateReceived?.Invoke(doorId, isOpen, revision, false, false);
            if (snapshotEnd) DoorStateReceived?.Invoke(string.Empty, false, revision, false, true);
        }

        public bool TryGetDoorState(string doorId, out bool isOpen, out uint revision)
        {
            if (_doorStates.TryGetValue(doorId ?? string.Empty, out DoorReplicaState state))
            { isOpen = state.Open; revision = state.Revision; return true; }
            isOpen = false;
            revision = 0;
            return false;
        }

        private void ApplyDoorSnapshotToScene(bool immediate = false)
        {
            DungeonDoorInteractable[] doors = FindObjectsByType<DungeonDoorInteractable>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < doors.Length; i++)
                if (doors[i] != null && _doorStates.TryGetValue(doors[i].DoorId, out DoorReplicaState state))
                    doors[i].ApplyReplicatedState(doors[i].DoorId, state.Open, immediate);
        }

        public bool RequestChestOpen(string chestId)
        {
            if (!_chestStateReady || _owner == null || !_owner.IsSpawned ||
                !ChestInteractable.IsValidNetworkChestId(chestId)) return false;
            _owner.ChestOpenIntentRpc(new FixedString128Bytes(chestId));
            return true;
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        internal event Action<ulong> LootOpenedForProof;
#endif
        public void OnLootOpened(ulong id)
        {
            _corpsePresenter?.OpenContainer(id);
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            LootOpenedForProof?.Invoke(id);
#endif
        }

        public void CloseSatchelForCorpse()
        {
            if (_screen != null && _screen.IsOpen) _screen.Close();
        }

        public bool RequestCorpseState(ulong corpseId)
        {
            return RequestCorpseState(corpseId, null, out _);
        }

        public bool RequestCorpseState(ulong corpseId, System.Func<uint, bool> beforeSend, out uint sequence)
        {
            sequence = 0;
            if (corpseId == 0 || _owner == null || !_owner.IsSpawned || !TryTakeCorpseSequence(out sequence)) return false;
            if (beforeSend != null && !beforeSend(sequence))
            {
                RestoreCorpseSequence(sequence);
                sequence = 0;
                return false;
            }
            _owner.CorpseStateRequestRpc(corpseId, sequence);
            return true;
        }

        public bool RequestCorpseTransfer(ulong corpseId, ulong entryId, int amount,
            ulong corpseRevision, ulong bagRevision)
        {
            return RequestCorpseTransfer(corpseId, entryId, amount, corpseRevision, bagRevision, null, out _);
        }

        public bool RequestCorpseTransfer(ulong corpseId, ulong entryId, int amount,
            ulong corpseRevision, ulong bagRevision, System.Func<uint, bool> beforeSend, out uint sequence, Vector2Int? destinationCell = null)
        {
            sequence = 0;
            if (corpseId == 0 || entryId == 0 || amount <= 0 || _owner == null || !_owner.IsSpawned ||
                !TryTakeCorpseSequence(out sequence)) return false;
            if (beforeSend != null && !beforeSend(sequence))
            {
                RestoreCorpseSequence(sequence);
                sequence = 0;
                return false;
            }
            _owner.CorpseTransferIntentRpc(corpseId, entryId, amount, corpseRevision, bagRevision, sequence, destinationCell?.x ?? -1, destinationCell?.y ?? -1);
            return true;
        }

        public void OnCorpseState(CorpseContainerWire wire)
        {
            if (!wire.TryRead(out CorpseContainerDocument document, out string reason))
            {
                Debug.LogWarning("[Emberdelve][CorpseLoot] server state refused: " + reason);
                return;
            }
            CorpseContainerReceived?.Invoke(document);
        }

        public void OnCorpsePresence(ulong corpseId, Vector3 position, ulong revision, bool removed)
        {
            if (corpseId == 0 || !IsFinite(position)) return;
            CorpsePresenceReceived?.Invoke(corpseId, position, revision, removed);
            if (removed && _corpsePresenter != null && _corpsePresenter.IsContainerUiOpenFor(corpseId))
            {
                _corpsePresenter.CloseContainerUi();
                if (_rig != null) GameEvents.RaiseToast(_rig.PlayerId, "Loot container closed");
            }
        }

        public void OnCorpseResult(ulong corpseId, uint sequence, bool accepted,
            ulong corpseRevision, ulong bagRevision, string reason)
        {
            if (corpseId == 0 || sequence == 0) return;
            if (_bag != null && bagRevision > _bag.Revision && _owner != null && _owner.IsSpawned)
            {
                _ready = false;
                _nextSync = 0f;
                _owner.InventorySnapshotRequestRpc();
            }
            CorpseTransferResultReceived?.Invoke(corpseId, sequence, accepted, corpseRevision, bagRevision, reason ?? string.Empty);
        }

        private bool TryTakeCorpseSequence(out uint sequence)
        {
            sequence = _nextCorpseSequence;
            if (sequence == 0 || sequence > CorpseRequestSequenceGate.MaximumSequence) return false;
            _nextCorpseSequence = sequence == CorpseRequestSequenceGate.MaximumSequence ? 0 : sequence + 1;
            return true;
        }

        private void RestoreCorpseSequence(uint sequence)
        {
            if (sequence == CorpseRequestSequenceGate.MaximumSequence) _nextCorpseSequence = sequence;
            else if (_nextCorpseSequence == sequence + 1) _nextCorpseSequence = sequence;
        }

        private static bool IsFinite(Vector3 value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        public bool BeginChestHold(string chestId)
        {
            if (!_chestStateReady || _owner == null || !_owner.IsSpawned || _activeChestHoldId.Length != 0 ||
                _pendingChestCompletionSequence != 0 ||
                !ChestInteractable.IsValidNetworkChestId(chestId)) return false;
            _activeChestHoldId = chestId;
            _activeChestHoldSequence = NextChestHoldSequence();
            _nextChestHoldHeartbeat = Time.unscaledTime + ChestHoldHeartbeatSeconds;
            _owner.ChestHoldBeginRpc(new FixedString128Bytes(chestId), _activeChestHoldSequence);
            return true;
        }

        public void ContinueChestHold(string chestId)
        {
            if (_owner == null || !_owner.IsSpawned || !string.Equals(_activeChestHoldId, chestId, StringComparison.Ordinal) ||
                _activeChestHoldSequence == 0 || Time.unscaledTime < _nextChestHoldHeartbeat) return;
            _nextChestHoldHeartbeat = Time.unscaledTime + ChestHoldHeartbeatSeconds;
            _owner.ChestHoldHeartbeatRpc(new FixedString128Bytes(chestId), _activeChestHoldSequence);
        }

        public void CancelChestHold(string chestId)
        {
            if (_owner == null || !_owner.IsSpawned || !string.Equals(_activeChestHoldId, chestId, StringComparison.Ordinal)) return;
            uint sequence = _activeChestHoldSequence;
            _activeChestHoldId = "";
            _activeChestHoldSequence = 0;
            _owner.ChestHoldCancelRpc(new FixedString128Bytes(chestId), sequence);
        }

        public bool CompleteChestHold(string chestId)
        {
            if (_owner == null || !_owner.IsSpawned || !string.Equals(_activeChestHoldId, chestId, StringComparison.Ordinal)) return false;
            uint sequence = _activeChestHoldSequence;
            _pendingChestCompletionId = chestId;
            _pendingChestCompletionSequence = sequence;
            _pendingChestCompletionStartedAt = Time.realtimeSinceStartupAsDouble;
            _activeChestHoldId = "";
            _activeChestHoldSequence = 0;
            _owner.ChestHoldCompleteRpc(new FixedString128Bytes(chestId), sequence);
            _nextChestHoldHeartbeat = Time.unscaledTime;
            return true;
        }

        private uint NextChestHoldSequence()
        {
            unchecked { _nextChestHoldSequence++; }
            if (_nextChestHoldSequence == 0) _nextChestHoldSequence = 1;
            return _nextChestHoldSequence;
        }

        public void OnChestResult(bool accepted, string chestId, uint sequence, string reason)
        {
            if (_rig == null) return;
            bool pendingCompletion = MatchesPendingChestCompletion(_pendingChestCompletionId,
                _pendingChestCompletionSequence, chestId, sequence);
            if (!accepted && sequence != 0)
            {
                bool currentHold = ShouldCancelForChestResult(_activeChestHoldId, _activeChestHoldSequence, chestId, sequence);
                if (!currentHold && !pendingCompletion) return;
                if (currentHold) GetComponent<Interactor>()?.CancelActiveHold();
            }
            else if (sequence != 0 && !pendingCompletion &&
                !ShouldCancelForChestResult(_activeChestHoldId, _activeChestHoldSequence, chestId, sequence)) return;

            if (pendingCompletion)
            {
                _pendingChestCompletionId = "";
                _pendingChestCompletionSequence = 0;
                _pendingChestCompletionStartedAt = 0.0;
                if (!accepted)
                {
                    _owner.ChestHoldCancelRpc(new FixedString128Bytes(chestId), sequence);
                    _ready = false;
                    _chestStateReady = false;
                    _nextSync = 0f;
                    _owner.InventorySnapshotRequestRpc();
                }
            }
            ChestResultReceived?.Invoke(accepted, chestId, sequence, reason ?? "");
            GameEvents.RaiseToast(_rig.PlayerId, accepted ? "Chest opened" :
                (string.IsNullOrEmpty(reason) ? "Chest request refused" : reason));
        }

        public static bool ShouldCancelForChestResult(string activeChestId, uint activeSequence, string resultChestId, uint resultSequence) =>
            activeSequence != 0 && resultSequence == activeSequence &&
            string.Equals(activeChestId, resultChestId, StringComparison.Ordinal);

        public static bool MatchesPendingChestCompletion(string pendingChestId, uint pendingSequence, string resultChestId, uint resultSequence) =>
            pendingSequence != 0 && resultSequence == pendingSequence &&
            string.Equals(pendingChestId, resultChestId, StringComparison.Ordinal);

        public static bool IsChestCompletionTimedOut(double startedAt, double now, double timeoutSeconds) =>
            !double.IsNaN(startedAt) && !double.IsInfinity(startedAt) &&
            !double.IsNaN(now) && !double.IsInfinity(now) && now >= startedAt &&
            !double.IsNaN(timeoutSeconds) && !double.IsInfinity(timeoutSeconds) && timeoutSeconds > 0.0 &&
            now - startedAt >= timeoutSeconds;

        public void OnChestState(string chestId, bool rolled, bool opened, int remainingUnits, bool snapshotStart, bool snapshotEnd)
        {
            if (snapshotStart)
            {
                _chestStates.Clear();
                _buildingChestSnapshot = true;
                _chestStateReady = false;
            }

            if (!string.IsNullOrEmpty(chestId))
            {
                if (!ChestInteractable.IsValidNetworkChestId(chestId) || remainingUnits < 0)
                {
                    _buildingChestSnapshot = false;
                    _chestStateReady = false;
                    return;
                }
                _chestStates[chestId] = new ChestReplicaState(rolled, opened, remainingUnits);
                if (!_buildingChestSnapshot && _chestStateReady) ApplyChestStateToScene(chestId);
            }

            if (snapshotEnd && _buildingChestSnapshot)
            {
                _buildingChestSnapshot = false;
                _hasChestSnapshot = true;
                _chestStateReady = ApplyChestSnapshotToScene();
                if (!_chestStateReady) Debug.LogWarning("[Emberdelve][Inventory] chest snapshot did not match the local scene");
            }
        }

        /// <summary>Applies one complete server-authored public floor-loot snapshot; local authored pickups are hidden once.</summary>
        public void OnFloorLootState(FloorLootWire wire)
        {
            if (!wire.TryRead(out FloorLootDocument document, out string reason))
            { Debug.LogWarning("[Emberdelve][NetLoot] rejected snapshot: " + reason); return; }
            if (_floorLootSnapshotReceived && document.revision <= _floorLootRevision) return;
            if (!EnsureCatalog()) return;
            RunDirector interactionHost = FindFirstObjectByType<RunDirector>(FindObjectsInactive.Include);
            for (int i = 0; i < document.entries.Length; i++)
                if (!_bag.Catalog.TryResolveItem(document.entries[i].item.itemId, out _, out reason))
                { Debug.LogWarning("[Emberdelve][NetLoot] snapshot references an unknown item: " + document.entries[i].item.itemId); return; }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            _floorLootProofItems.Clear();
            for (int i = 0; i < document.entries.Length; i++)
                _floorLootProofItems[document.entries[i].id] = document.entries[i].item.Clone();
#endif
            if (NetRole.IsClientOnly)
            {
                var ids = new StringBuilder();
                for (int i = 0; i < document.entries.Length; i++)
                {
                    if (i > 0) ids.Append(',');
                    ids.Append(document.entries[i].id).Append(':').Append(document.entries[i].item.uid ?? "-");
                }
                Debug.Log(NetTuning.LogPrefix + "[client] floor-loot-state role=owner revision=" + document.revision +
                          " count=" + document.entries.Length + " entries=" + (ids.Length == 0 ? "-" : ids.ToString()));
            }
            if (!_floorLootSnapshotReceived)
            {
                GameObject authored = GameObject.Find("Loot");
                if (authored != null) authored.SetActive(false);
                _floorLootSnapshotReceived = true;
            }
            var present = new HashSet<ulong>();
            for (int i = 0; i < document.entries.Length; i++)
            {
                FloorLootEntryDocument entry = document.entries[i];
                if (!_bag.Catalog.TryResolveItem(entry.item.itemId, out ItemData definition, out reason))
                { return; }
                present.Add(entry.id);
                if (!_floorLootViews.TryGetValue(entry.id, out GameObject root) || root == null)
                {
                    root = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    root.name = "NetLoot_" + definition.itemId;
                    _floorLootViews[entry.id] = root;
                    int layer = LayerMask.NameToLayer(GameConfig.LootLayerName);
                    if (layer >= 0) root.layer = layer;
                    BoxCollider box = root.GetComponent<BoxCollider>();
                    if (box != null) box.size = new Vector3(1.9f, 1.9f, 1.9f);
                    Renderer renderer = root.GetComponent<Renderer>();
                    if (renderer != null && definition.worldMaterial != null) renderer.material = definition.worldMaterial;
                    root.AddComponent<LootPickup>().ConfigureNetworkReplica(definition, entry.item.quantity, entry.id, interactionHost);
                }
                else
                {
                    LootPickup pickup = root.GetComponent<LootPickup>();
                    if (pickup != null) pickup.ConfigureNetworkReplica(definition, entry.item.quantity, entry.id, interactionHost);
                }
                LootPickup viewPickup = root.GetComponent<LootPickup>();
                if (viewPickup != null) viewPickup.PlaceAt(entry.position);
                else root.transform.position = entry.position;
                root.transform.localScale = new Vector3(Mathf.Max(.12f, definition.worldSize.x),
                    Mathf.Max(.12f, definition.worldSize.y), Mathf.Max(.12f, definition.worldSize.z));
            }
            var removed = new List<ulong>();
            foreach (var pair in _floorLootViews) if (!present.Contains(pair.Key)) removed.Add(pair.Key);
            for (int i = 0; i < removed.Count; i++)
            {
                ulong id = removed[i];
                if (_floorLootViews.TryGetValue(id, out GameObject root) && root != null) Destroy(root);
                _floorLootViews.Remove(id);
            }
            _floorLootRevision = document.revision;
        }

        private bool ApplyChestSnapshotToScene()
        {
            EnsureClientChestPresentation();
            ChestInteractable[] chests = FindObjectsByType<ChestInteractable>(FindObjectsSortMode.None);
            if (chests.Length != _chestStates.Count) return false;
            for (int i = 0; i < chests.Length; i++)
            {
                ChestInteractable chest = chests[i];
                if (chest == null || !_chestStates.TryGetValue(chest.ChestId, out ChestReplicaState state) ||
                    !chest.ApplyReplicatedState(chest.ChestId, state.Rolled, state.Opened, state.RemainingUnits)) return false;
            }
            return true;
        }

        private static void EnsureClientChestPresentation()
        {
            if (!NetRole.IsClientOnly) return;
            RunDirector director = FindFirstObjectByType<RunDirector>(FindObjectsInactive.Include);
            if (director != null && director.Layout != null)
                ChestSpawner.EnsureClientPresentation(director.Layout);
        }

        private void ApplyChestStateToScene(string chestId)
        {
            if (!_chestStates.TryGetValue(chestId, out ChestReplicaState state)) return;
            ChestInteractable[] chests = FindObjectsByType<ChestInteractable>(FindObjectsSortMode.None);
            for (int i = 0; i < chests.Length; i++)
                if (chests[i] != null && string.Equals(chests[i].ChestId, chestId, StringComparison.Ordinal))
                    chests[i].ApplyReplicatedState(chestId, state.Rolled, state.Opened, state.RemainingUnits);
        }

        public void OnSnapshot(NetworkLoadoutWire wire)
        {
            if (_bag == null || _rig == null || !EnsureCatalog()) return;
            if ((_hasSnapshot && wire.Revision < _bag.Revision) ||
                (_awaitSnapshot && wire.Revision < _awaitRevision))
            {
                _ready = false;
                _nextSync = 0f;
                return;
            }
            if (_hasSnapshot && wire.Revision == _bag.Revision)
            {
                _ready = true;
            }
            else if (wire.TryToState(out var state, out string reason) && state.TryApply(_bag, _rig.Equipment, out reason))
            {
                _hasSnapshot = true;
                _ready = true;
            }
            else
            {
                _ready = false;
                Debug.LogWarning("[Emberdelve][Inventory] snapshot refused: " + reason);
                return;
            }
            if (_awaitSnapshot && wire.Revision >= _awaitRevision) { _pendingSequence = 0; _awaitSnapshot = false; }
        }

        private void OnResult(InventoryResultWire result)
        {
            if (_pendingSequence == 0 || result.Sequence != _pendingSequence) return;
            if (!result.Accepted)
            {
                _pendingSequence = 0;
                _ready = false;
                _nextSync = 0;
                GameEvents.RaiseToast(_rig.PlayerId, "Inventory change refused by the server; refreshing");
                return;
            }
            if (result.Changed && (!_hasSnapshot || _bag.Revision < result.Revision))
            {
                _awaitSnapshot = true;
                _awaitRevision = result.Revision;
                return;
            }
            _pendingSequence = 0;
        }

        public bool TryCreateLocalCommand(InventoryCommandKind kind, ulong sourceStackId, ulong targetStackId,
            Vector2Int destinationCell, EquipmentSlot destinationSlot, Guid expectedSourceGearUid,
            Guid expectedTargetGearUid, out InventoryCommand command, out string reason, int amount = 0)
        {
            command = default;
            if (!_ready || _owner == null || !_owner.IsSpawned || _rig == null || _bag == null)
            { reason = "Waiting for the server inventory"; return false; }
            if (_pendingSequence != 0) { reason = "Waiting for the previous inventory change"; return false; }
            if (_nextSequence == 0 || _nextSequence > Rules.InventoryIntentGate.SequenceCeiling)
            { reason = "Inventory session sequence exhausted"; return false; }
            var candidate = new InventoryCommand(kind, _rig.PlayerId, _nextSequence, _bag.Revision,
                sourceStackId, targetStackId, destinationCell, destinationSlot, expectedSourceGearUid, expectedTargetGearUid, amount);
            if (!candidate.IsStructurallyValid(out reason)) return false;
            command = candidate;
            return true;
        }

        public InventoryCommandResult Submit(PlayerRig requester, InventoryCommand command)
        {
            if (!_ready || _pendingSequence != 0 || !ReferenceEquals(requester, _rig) || command.Sequence != _nextSequence ||
                command.OwnerPlayerId != _rig.PlayerId || command.ExpectedRevision != _bag.Revision || !command.IsStructurallyValid(out _))
                return new InventoryCommandResult(false, false, _bag != null ? _bag.Revision : 0, "Inventory request is no longer current");
            _pendingSequence = _nextSequence++;
            _deadline = Time.unscaledTime + 5f;
            _owner.InventoryIntentRpc(InventoryIntentWire.FromCommand(command));
            // Accepted here means queued locally; the server result and snapshot arrive asynchronously.
            return new InventoryCommandResult(true, false, _bag.Revision, null);
        }
    }
}
