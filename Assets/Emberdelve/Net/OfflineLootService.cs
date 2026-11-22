using System;
using System.Collections.Generic;
using System.Linq;
using Emberdelve.Core;
using Emberdelve.Flow;
using Emberdelve.Interaction;
using Emberdelve.Items;
using Emberdelve.Level;
using Emberdelve.Player;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>
    /// Offline stand-in for the server's loot-container table. Chests and dead enemies hand their rolled loot to
    /// <see cref="IWorldLootContainerHost"/> and the player takes items out through the same loot window that a
    /// networked host uses, instead of the items landing in the bag the moment the chest opens or the mob dies.
    /// Every request is validated like the server does (alive, admitted, in reach, line of sight) and the item
    /// itself moves through <see cref="CorpseContainerModel.TryTransferToBag"/>, so UID and affix custody are the
    /// same code path as the network.
    /// Ordinary headless gates keep direct grants; the offline Royale adapter explicitly installs this authority.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OfflineLootService : MonoBehaviour, IWorldLootContainerHost, ICorpseDeathCustodian, IRetiredFloorLootSink
    {
        private readonly HashSet<int> _committedDeaths = new HashSet<int>();
        private readonly HashSet<int> _committingDeaths = new HashSet<int>();
        private const float PollSeconds = 0.25f;
        private static OfflineLootService _instance;

        private readonly Dictionary<ulong, CorpseContainerModel> _containers = new Dictionary<ulong, CorpseContainerModel>();
        private readonly List<Rect> _retiredFloorBounds = new List<Rect>();
        private ulong _nextId = 1;
        private uint _sequence = 1;
        private CorpseLootWorldPresenter _presenter;
        private PlayerRig _rig;
        private RunDirector _director;
        private float _nextPoll;
        private bool _registered;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (_instance != null || Application.isBatchMode) return;
            CreateInstance();
        }

        private static void CreateInstance()
        {
            var go = new GameObject("OfflineLootService");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<OfflineLootService>();
        }

        /// <summary>Royale and ordinary offline play share one container table and one presenter.</summary>
        public static OfflineLootService Ensure(RunDirector director)
        {
            if (!NetRole.IsOffline || director == null || director.Player == null) return null;
            if (_instance == null) CreateInstance();
            return _instance.Attach(director, director.Player) ? _instance : null;
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + PollSeconds;

            if (!NetRole.IsOffline) { Detach(); return; }
            if (_rig == null || !_rig.IsLocallyControlled)
            {
                _rig = null;
                foreach (PlayerRig candidate in FindObjectsByType<PlayerRig>(FindObjectsSortMode.None))
                    if (candidate.IsLocallyControlled) { _rig = candidate; break; }
            }
            if (_rig == null) { Detach(); return; }
            if (_director == null) _director = FindFirstObjectByType<RunDirector>();

            if (_director != null) Attach(_director, _rig);
        }

        private bool Attach(RunDirector director, PlayerRig rig)
        {
            // Check authority before configuring the presenter, otherwise even a rejected registration can steal its routing.
            if (GameServices.TryGet<IWorldLootContainerHost>(out var other) && !ReferenceEquals(other, this)) return false;
            if (_presenter == null || _presenter.gameObject != rig.gameObject || _director != director)
            {
                ClearContainers();
                _committedDeaths.Clear();
                _committingDeaths.Clear();
                _retiredFloorBounds.Clear();
                _director = director;
                _rig = rig;
                _presenter = _rig.GetComponent<CorpseLootWorldPresenter>();
                if (_presenter == null) _presenter = _rig.gameObject.AddComponent<CorpseLootWorldPresenter>();
                _presenter.ConfigureOffline(this, _rig, _director != null ? _director.InteractionReach : InteractionRules.DefaultReach);
            }

            if (!_registered)
            {
                GameServices.Register<IWorldLootContainerHost>(this);
                GameServices.Register<ICorpseDeathCustodian>(this);
                GameServices.Register<IRetiredFloorLootSink>(this);
                _registered = true;
            }
            return true;
        }

        private void Detach()
        {
            if (_registered)
            {
                GameServices.Unregister<IWorldLootContainerHost>(this);
                GameServices.Unregister<ICorpseDeathCustodian>(this);
                GameServices.Unregister<IRetiredFloorLootSink>(this);
            }
            _registered = false;
            ClearContainers();
            _presenter = null;
            _rig = null;
            _director = null;
            _committedDeaths.Clear();
            _committingDeaths.Clear();
            _retiredFloorBounds.Clear();
        }

        /// <summary>
        /// A bot that dies offline leaves its whole loadout (equipment and bag) in a corpse container, searched in
        /// the same player-style window as a networked corpse. The source is emptied only after the container has
        /// been validated and can no longer fail to publish.
        /// </summary>
        public bool TryCommitDeath(int playerId, Vector3 position, Inventory bag, Equipment equipment, out string reason)
        {
            reason = "Only an admitted dead offline actor can leave a lootable corpse";
            if (!NetRole.IsOffline || _presenter == null || _director == null ||
                !_director.TryGetSession(playerId, out var actor) || actor.IsLocal || actor.Rig == null ||
                !actor.Rig.IsDead || actor.Inventory != bag || actor.Rig.Equipment != equipment) return false;
            if (_committedDeaths.Contains(playerId)) { reason = null; return true; }
            reason = "Death loot is already being committed";
            if (!_committingDeaths.Add(playerId)) return false;
            try
            {
                if (!CorpseLootManifest.TryCapture(playerId, position, bag, equipment, out var manifest, out reason) ||
                    !CorpseContainerModel.TryCreate(manifest, out var model, out reason)) return false;
                if (model.EntryCount == 0) { _committedDeaths.Add(playerId); reason = null; return true; }
                if (!TryReserve(model, out ulong id, out reason)) return false;
                bool committed = false;
                try
                {
                    var empty = manifest.CopyLoadout();
                    empty.bag.entries = Array.Empty<ContainerEntry>();
                    empty.equipment.entries = Array.Empty<ContainerEntry>();
                    empty.weaponSet = 0;
                    empty.belt = new string[LoadoutSnapshot.BeltSlotCount];
                    Array.Fill(empty.belt, string.Empty);
                    empty.Seal();
                    if (!manifest.StillMatches(bag, equipment))
                    { reason = "Source loadout changed before death custody commit"; return false; }
                    if (!LoadoutSnapshot.TryRestore(empty, bag, equipment, null, out reason)) return false;
                    committed = true;
                }
                finally { if (!committed) _containers.Remove(id); }
                _committedDeaths.Add(playerId);
                Show(id, model);
                reason = null;
                return true;
            }
            finally { _committingDeaths.Remove(playerId); }
        }

        private void OnDestroy()
        {
            Detach();
            if (ReferenceEquals(_instance, this)) _instance = null;
        }

        public bool TryPublishLoot(CorpseContainerModel model, out ulong id, out string reason)
        {
            if (!TryReserve(model, out id, out reason)) return false;
            Show(id, model);
            return true;
        }

        private bool TryReserve(CorpseContainerModel model, out ulong id, out string reason)
        {
            id = 0;
            if (!NetRole.IsOffline || _presenter == null || model == null || model.EntryCount == 0 ||
                _nextId == 0 || _nextId == ulong.MaxValue || IsRetired(model.WorldPosition))
            {
                reason = "Loot window is unavailable";
                return false;
            }
            var incoming = model.CopyEntries().Where(row => row.IsGear).Select(row => row.Uid).ToHashSet(StringComparer.Ordinal);
            if (incoming.Count > 0 && _containers.Values.Any(container => container.CopyEntries().Any(row => incoming.Contains(row.Uid))))
            { reason = "An item is already held in another loot container"; return false; }
            if (!CorpseContainerWire.TryCreate(_nextId, model, out _, out reason)) return false;
            id = _nextId++;
            _containers.Add(id, model);
            reason = null;
            return true;
        }

        private void Show(ulong id, CorpseContainerModel model)
        {
            // A chest is its own world object; a dead enemy or bot is searched on its own body.
            if (model.Kind != "chest")
            {
                _presenter.OnBodyPresence(id, model.WorldPosition, model.Revision, false);
            }
        }

        public void OpenLoot(IInteractionActor actor, ulong id)
        {
            if (!CanLoot(actor, id, out _)) return;
            _presenter.OpenContainer(id);
        }

        internal bool CanLoot(IInteractionActor actor, ulong id, out string reason)
        {
            reason = "Loot container is unavailable";
            return NetRole.IsOffline && _presenter != null && ReferenceEquals(actor, _rig) &&
                _containers.TryGetValue(id, out var model) && Validate(_rig, model, out reason);
        }

        internal bool RequestState(CorpseLootWorldPresenter presenter, PlayerRig rig, ulong id,
            System.Func<uint, bool> beforeSend, out uint sequence)
        {
            sequence = 0;
            if (!_containers.TryGetValue(id, out CorpseContainerModel model)) return false;
            if (!BeginRequest(beforeSend, out sequence)) return false;
            if (!Validate(rig, model, out string reason))
            {
                presenter.OnCorpseResult(id, sequence, false, model.Revision, reason);
                return true;
            }
            if (!CorpseContainerWire.TryCreate(id, model, out CorpseContainerWire wire, out reason))
            {
                presenter.OnCorpseResult(id, sequence, false, model.Revision, reason);
                return true;
            }
            presenter.OnCorpseState(wire);
            presenter.OnCorpseResult(id, sequence, true, model.Revision, string.Empty);
            return true;
        }

        internal bool RequestTransfer(CorpseLootWorldPresenter presenter, PlayerRig rig,
            Rules.CorpseTransferUiRequest request, System.Func<uint, bool> beforeSend, out uint sequence)
        {
            sequence = 0;
            if (!_containers.TryGetValue(request.CorpseId, out CorpseContainerModel model)) return false;
            if (!BeginRequest(beforeSend, out sequence)) return false;

            ItemUidRegistry uids = null;
            string reason;
            bool accepted = false;
            if (Validate(rig, model, out reason) &&
                NetInventoryServer.TryBuildProfileUidRegistry(rig, out uids, out reason))
            {
                accepted = model.TryTransferToBag(request.EntryId, request.Amount, request.CorpseRevision,
                    rig.Inventory, request.BagRevision, uids, out reason, request.DestinationCell);
            }

            if (!accepted)
            {
                presenter.OnCorpseResult(request.CorpseId, sequence, false, model.Revision, reason);
                if (CorpseContainerWire.TryCreate(request.CorpseId, model, out CorpseContainerWire current, out _))
                    presenter.OnCorpseState(current);
                return true;
            }

            presenter.OnCorpseResult(request.CorpseId, sequence, true, model.Revision, string.Empty);
            if (model.Kind == "chest")
                foreach (ChestInteractable chest in FindObjectsByType<ChestInteractable>(FindObjectsSortMode.None))
                    if (ReferenceEquals(chest.LootContainer, model)) chest.NotifyLootChanged();

            // A searched-out body and an emptied chest stay in the world; they simply open an empty window.
            if (CorpseContainerWire.TryCreate(request.CorpseId, model, out CorpseContainerWire wire, out _))
            {
                presenter.OnCorpseState(wire);
            }
            return true;
        }

        /// <summary>Moves one bag stack or gear instance into an open chest/body. The bag is restored from a snapshot without that entry and the container is undone if that is refused, so nothing is created, lost or duplicated.</summary>
        internal bool PutFromBag(CorpseLootWorldPresenter presenter, PlayerRig rig, ulong id, Vector2Int origin,
            string itemId, out string reason)
        {
            if (!_containers.TryGetValue(id, out CorpseContainerModel model)) { reason = "Container is unavailable"; return false; }
            if (!Validate(rig, model, out reason)) return false;
            Inventory bag = rig.Inventory;
            if (bag == null || bag.Catalog == null) { reason = "Your bag is unavailable"; return false; }

            ContainerSnapshot snapshot = bag.ToContainerSnapshot().Clone();
            int index = -1;
            for (int i = 0; i < snapshot.entries.Length; i++)
            {
                ContainerEntry e = snapshot.entries[i];
                if (e != null && e.item != null && e.x == origin.x && e.y == origin.y && e.item.itemId == itemId) { index = i; break; }
            }
            if (index < 0) { reason = "That item is no longer in your bag"; return false; }

            ItemSnapshot moved = snapshot.entries[index].item.Clone();
            if (!model.TryAddFromBag(moved, bag.Catalog, out ulong entryId, out reason)) return false;

            var remaining = new List<ContainerEntry>(snapshot.entries);
            remaining.RemoveAt(index);
            snapshot.entries = remaining.ToArray();
            snapshot.Seal();
            if (!bag.TryRestore(snapshot, out reason))
            {
                model.TryRemoveEntry(entryId);
                return false;
            }

            if (model.Kind == "chest")
                foreach (ChestInteractable chest in FindObjectsByType<ChestInteractable>(FindObjectsSortMode.None))
                    if (ReferenceEquals(chest.LootContainer, model)) chest.NotifyLootChanged();
            if (CorpseContainerWire.TryCreate(id, model, out CorpseContainerWire wire, out _)) presenter.OnCorpseState(wire);
            reason = null;
            return true;
        }

        private bool BeginRequest(Func<uint, bool> beforeSend, out uint sequence)
        {
            sequence = _sequence;
            if (sequence == 0 || sequence > Rules.CorpseRequestSequenceGate.MaximumSequence ||
                (beforeSend != null && !beforeSend(sequence))) { sequence = 0; return false; }
            _sequence++;
            return true;
        }

        public void RetireFloorLoot(Rect bounds)
        {
            if (!NetRole.IsOffline) return;
            if (!_retiredFloorBounds.Contains(bounds)) _retiredFloorBounds.Add(bounds);
            foreach (var entry in _containers.Where(entry => bounds.Contains(new Vector2(entry.Value.WorldPosition.x, entry.Value.WorldPosition.z))).ToArray())
            {
                _containers.Remove(entry.Key);
                if (_presenter != null) _presenter.OnPresence(entry.Key, entry.Value.WorldPosition, ulong.MaxValue, true);
            }
        }

        private void ClearContainers()
        {
            foreach (var entry in _containers)
                if (_presenter != null) _presenter.OnPresence(entry.Key, entry.Value.WorldPosition, ulong.MaxValue, true);
            _containers.Clear();
        }

        private bool IsRetired(Vector3 position) =>
            _retiredFloorBounds.Any(bounds => bounds.Contains(new Vector2(position.x, position.z))) ||
            DungeonModuleSceneAssembler.IsPositionOnRetiredFloor(position);

        private bool Validate(PlayerRig rig, CorpseContainerModel model, out string reason)
        {
            reason = "Loot container is unavailable";
            return NetRole.IsOffline && ReferenceEquals(rig, _rig) && model != null &&
                !IsRetired(model.WorldPosition) &&
                NetInventoryServer.ValidateLootAccess(_director, rig, model, out reason);
        }
    }
}
