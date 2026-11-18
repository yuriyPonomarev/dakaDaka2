using System;
using System.Collections.Generic;
using Emberdelve.Core;
using Emberdelve.Interaction;
using Emberdelve.Player;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>Local owner world representation and request bridge for server-advertised corpse presence.</summary>
    [DisallowMultipleComponent]
    public sealed class CorpseLootWorldPresenter : MonoBehaviour
    {
        private readonly Dictionary<ulong, CorpseWorldInteractable> _views = new Dictionary<ulong, CorpseWorldInteractable>();
        private readonly Rules.CorpsePresenceStateTable _states = new Rules.CorpsePresenceStateTable();
        private NetInventoryClient _client;
        private NetPlayerRig _hostOwner;
        private OfflineLootService _offline;
        private CorpseContainerScreenController _containerUi;
        private PlayerRig _rig;
        private float _interactionReach = 3f;
        private ulong _awaitedCorpseId;
        private uint _nextHostSequence = 1;

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        internal event Action<CorpseContainerDocument> ProofStateObserved;
        internal event Action<ulong, uint, bool, ulong, string> ProofResultObserved;
        internal event Action<ulong, ulong, bool> ProofPresenceObserved;
#endif

        public void Configure(NetInventoryClient client, PlayerRig rig, float interactionReach)
        {
            if (_client != null) _client.CorpsePresenceReceived -= OnPresence;
            _client = client;
            _rig = rig;
            _interactionReach = !float.IsNaN(interactionReach) && !float.IsInfinity(interactionReach) && interactionReach > 0f
                ? interactionReach : 3f;
            if (_client != null) _client.CorpsePresenceReceived += OnPresence;
            EnsureContainerUi();
        }

        public void ConfigureHost(NetPlayerRig owner, PlayerRig rig, float interactionReach)
        {
            if (_client != null) _client.CorpsePresenceReceived -= OnPresence;
            _client = null;
            _hostOwner = owner;
            _rig = rig;
            _interactionReach = !float.IsNaN(interactionReach) && !float.IsInfinity(interactionReach) && interactionReach > 0f
                ? interactionReach : 3f;
            EnsureContainerUi();
        }

        /// <summary>Offline play has no server: a local loot service answers the same state/transfer requests.</summary>
        public void ConfigureOffline(OfflineLootService service, PlayerRig rig, float interactionReach)
        {
            if (_client != null) _client.CorpsePresenceReceived -= OnPresence;
            _client = null;
            _hostOwner = null;
            _offline = service;
            _rig = rig;
            _interactionReach = !float.IsNaN(interactionReach) && !float.IsInfinity(interactionReach) && interactionReach > 0f
                ? interactionReach : 3f;
            EnsureContainerUi();
        }

        public bool IsContainerUiOpen => _containerUi != null && _containerUi.IsOpen;
        public bool IsContainerUiOpenFor(ulong corpseId) => _containerUi != null && _containerUi.IsOpenFor(corpseId);

        private void EnsureContainerUi()
        {
            if (Application.isBatchMode) return;
            if (_containerUi == null) _containerUi = GetComponent<CorpseContainerScreenController>();
            if (_containerUi == null) _containerUi = gameObject.AddComponent<CorpseContainerScreenController>();
            _containerUi.Configure(this, _client, _hostOwner, _rig);
        }

        public bool CanInteract(IInteractionActor actor, out string reason)
        {
            if (_offline != null)
            {
                if (actor == null || !ReferenceEquals(actor, _rig)) { reason = "Not your loot"; return false; }
                return InteractionRules.Validate(actor, this, _interactionReach, out reason);
            }
            bool isClientOwner = _client != null && _client.IsOwnerReady;
            bool isHostOwner = _hostOwner != null && _hostOwner.IsServer && _hostOwner.IsOwner &&
                               _hostOwner.Role == NetPlayerRole.HostPlayer && _hostOwner.IsSpawned &&
                               _rig != null && _rig.Inventory != null;
            if (!Rules.CorpseInteractionGate.CanRequest(isClientOwner, isHostOwner,
                    actor != null && ReferenceEquals(actor, _rig), actor != null && actor.IsAlive, out reason)) return false;
            return InteractionRules.Validate(actor, this, _interactionReach, out reason);
        }

        public bool RequestState(ulong corpseId)
        {
            return RequestState(corpseId, null, out _);
        }

        public bool OpenContainer(ulong corpseId)
        {
            return _containerUi != null && _containerUi.OpenPending(corpseId);
        }

        public bool RequestState(ulong corpseId, System.Func<uint, bool> beforeSend, out uint sequence)
        {
            sequence = 0;
            if (corpseId == 0) return false;
            if (_offline != null) return _offline.RequestState(this, _rig, corpseId, beforeSend, out sequence);
            if (_client != null) return _client.RequestCorpseState(corpseId, beforeSend, out sequence);
            if (_hostOwner == null || !_hostOwner.IsServer || !_hostOwner.IsOwner || !_hostOwner.IsSpawned ||
                _nextHostSequence == 0 || _nextHostSequence > Rules.CorpseRequestSequenceGate.MaximumSequence) return false;
            NetInventoryServer server = NetInventoryServer.Current;
            if (server == null) return false;
            sequence = _nextHostSequence++;
            if (beforeSend != null && !beforeSend(sequence))
            {
                RestoreHostSequence(sequence);
                sequence = 0;
                return false;
            }
            server.RequestCorpseState(_hostOwner.OwnerClientId, _hostOwner.NetworkObject, corpseId, sequence);
            return true;
        }

        public bool RequestTransfer(Rules.CorpseTransferUiRequest request, System.Func<uint, bool> beforeSend,
            out uint sequence)
        {
            sequence = 0;
            if (request.CorpseId == 0 || request.EntryId == 0 || request.Amount <= 0) return false;
            if (_offline != null) return _offline.RequestTransfer(this, _rig, request, beforeSend, out sequence);
            if (_client != null)
                return _client.RequestCorpseTransfer(request.CorpseId, request.EntryId, request.Amount,
                    request.CorpseRevision, request.BagRevision, beforeSend, out sequence, request.DestinationCell);
            if (_hostOwner == null || !_hostOwner.IsServer || !_hostOwner.IsOwner || !_hostOwner.IsSpawned ||
                _nextHostSequence == 0 || _nextHostSequence > Rules.CorpseRequestSequenceGate.MaximumSequence) return false;
            NetInventoryServer server = NetInventoryServer.Current;
            if (server == null) return false;
            sequence = _nextHostSequence++;
            if (beforeSend != null && !beforeSend(sequence))
            {
                RestoreHostSequence(sequence);
                sequence = 0;
                return false;
            }
            server.RequestCorpseTransfer(_hostOwner.OwnerClientId, _hostOwner.NetworkObject, request.CorpseId,
                request.EntryId, request.Amount, request.CorpseRevision, request.BagRevision, sequence, request.DestinationCell?.x ?? -1, request.DestinationCell?.y ?? -1);
            return true;
        }

        private void RestoreHostSequence(uint sequence)
        {
            if (sequence == Rules.CorpseRequestSequenceGate.MaximumSequence) _nextHostSequence = sequence;
            else if (_nextHostSequence == sequence + 1) _nextHostSequence = sequence;
        }

        public void OnCorpseState(CorpseContainerWire wire)
        {
            if (!wire.TryRead(out CorpseContainerDocument document, out _))
            {
                _containerUi?.OnInvalidState("Server sent invalid corpse contents");
                return;
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            try { ProofStateObserved?.Invoke(document); }
            catch (Exception e) { Debug.LogError("[Emberdelve][CorpseProof][host] state observer failed: " + e); }
#endif
            _containerUi?.OnState(document);
        }

        public void OnCorpseResult(ulong corpseId, uint sequence, bool accepted, ulong corpseRevision, string reason)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            try { ProofResultObserved?.Invoke(corpseId, sequence, accepted, corpseRevision, reason ?? string.Empty); }
            catch (Exception e) { Debug.LogError("[Emberdelve][CorpseProof][host] result observer failed: " + e); }
#endif
            _containerUi?.OnResult(corpseId, sequence, accepted, corpseRevision, reason);
        }

        public void OnPresence(ulong corpseId, Vector3 position, ulong revision, bool removed)
        {
            if (!_states.TryApply(corpseId, position, revision, removed, out Rules.CorpsePresenceStateTable.State state)) return;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            try { ProofPresenceObserved?.Invoke(corpseId, revision, removed); }
            catch (Exception e) { Debug.LogError("[Emberdelve][CorpseProof][host] presence observer failed: " + e); }
#endif
            if (state.Removed)
            {
                RemoveView(corpseId);
                if (IsContainerUiOpenFor(corpseId))
                {
                    _containerUi.OnEmptied(state.Revision);
                }
                return;
            }

            if (!_views.TryGetValue(corpseId, out CorpseWorldInteractable view) || view == null)
            {
                view = CreateView(corpseId);
                if (view == null) return;
                _views[corpseId] = view;
            }
            view.SetPresence(state.Position, state.Revision);
        }

        public void CloseContainerUi(bool relockCursor = true) => _containerUi?.Close(relockCursor);

        private CorpseWorldInteractable CreateView(ulong corpseId)
        {
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "CorpseLoot_" + corpseId;
            int layer = LayerMask.NameToLayer(GameConfig.InteractLayerName);
            if (layer >= 0) body.layer = layer;
            body.transform.localScale = new Vector3(0.52f, 0.38f, 0.82f);
            body.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            var view = body.AddComponent<CorpseWorldInteractable>();
            view.Configure(this, corpseId);
            Renderer renderer = body.GetComponent<Renderer>();
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (renderer != null && shader != null)
            {
                var material = new Material(shader) { color = new Color(0.16f, 0.11f, 0.09f, 1f) };
                renderer.material = material;
                view.SetOwnedMaterial(material);
            }
            return view;
        }

        private void RemoveView(ulong corpseId)
        {
            if (!_views.TryGetValue(corpseId, out CorpseWorldInteractable view)) return;
            _views.Remove(corpseId);
            if (view != null) Destroy(view.gameObject);
        }

        private void OnDestroy()
        {
            if (_client != null) _client.CorpsePresenceReceived -= OnPresence;
            foreach (CorpseWorldInteractable view in _views.Values)
                if (view != null) Destroy(view.gameObject);
            _views.Clear();
            _states.Clear();
        }
    }
}
