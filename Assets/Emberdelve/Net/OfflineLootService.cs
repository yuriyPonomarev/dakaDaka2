using System.Collections.Generic;
using Emberdelve.Core;
using Emberdelve.Flow;
using Emberdelve.Interaction;
using Emberdelve.Items;
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
    /// The service only runs for an interactive offline player. Batch-mode gates keep the direct-grant behavior.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OfflineLootService : MonoBehaviour, IWorldLootContainerHost, ICorpseDeathCustodian
    {
        private readonly HashSet<int> _committedDeaths = new HashSet<int>();
        private const float PollSeconds = 0.25f;
        private static OfflineLootService _instance;

        private readonly Dictionary<ulong, CorpseContainerModel> _containers = new Dictionary<ulong, CorpseContainerModel>();
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
            var go = new GameObject("OfflineLootService");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<OfflineLootService>();
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

            if (_presenter == null || _presenter.gameObject != _rig.gameObject)
            {
                _containers.Clear();
                _presenter = _rig.GetComponent<CorpseLootWorldPresenter>();
                if (_presenter == null) _presenter = _rig.gameObject.AddComponent<CorpseLootWorldPresenter>();
                _presenter.ConfigureOffline(this, _rig, _director != null ? _director.InteractionReach : InteractionRules.DefaultReach);
            }

            if (!_registered)
            {
                if (GameServices.TryGet<IWorldLootContainerHost>(out var other) && !ReferenceEquals(other, this)) return;
                GameServices.Register<IWorldLootContainerHost>(this);
                GameServices.Register<ICorpseDeathCustodian>(this);
                _registered = true;
            }
        }

        private void Detach()
        {
            if (_registered)
            {
                GameServices.Unregister<IWorldLootContainerHost>(this);
                GameServices.Unregister<ICorpseDeathCustodian>(this);
            }
            _registered = false;
            _presenter = null;
            _rig = null;
            _containers.Clear();
            _committedDeaths.Clear();
        }

        /// <summary>
        /// A bot that dies offline leaves its whole loadout (equipment and bag) in a corpse container, searched in
        /// the same player-style window as a networked corpse. The source is emptied only after the container has
        /// been validated and can no longer fail to publish.
        /// </summary>
        public bool TryCommitDeath(int playerId, Vector3 position, Inventory bag, Equipment equipment, out string reason)
        {
            if (_presenter == null || playerId < 0 || bag == null || equipment == null || !equipment.IsPairedWith(bag))
            {
                reason = "offline corpse custody needs a ready loot window and a paired loadout";
                return false;
            }
            if (!_committedDeaths.Add(playerId)) { reason = null; return true; }
            if (!CorpseLootManifest.TryCapture(playerId, position, bag, equipment, out CorpseLootManifest manifest, out reason) ||
                !CorpseContainerModel.TryCreate(manifest, out CorpseContainerModel model, out reason))
            {
                _committedDeaths.Remove(playerId);
                return false;
            }
            LoadoutSnapshot captured = manifest.CopyLoadout();
            bool hasItems = captured.bag.entries.Length > 0 || captured.equipment.entries.Length > 0;
            if (hasItems && !CorpseContainerWire.TryCreate(_nextId, model, out _, out reason))
            {
                _committedDeaths.Remove(playerId);
                return false;
            }
            LoadoutSnapshot empty = captured.Clone();
            empty.bag.entries = System.Array.Empty<ContainerEntry>();
            empty.equipment.entries = System.Array.Empty<ContainerEntry>();
            empty.weaponSet = 0;
            empty.belt = new string[HotbarBindings.SlotCount];
            for (int i = 0; i < empty.belt.Length; i++) empty.belt[i] = "";
            empty.Seal();
            if (!manifest.StillMatches(bag, equipment) || !LoadoutSnapshot.TryRestore(empty, bag, equipment, null, out reason))
            {
                _committedDeaths.Remove(playerId);
                if (string.IsNullOrEmpty(reason)) reason = "source loadout changed before death custody commit";
                return false;
            }
            if (hasItems && !TryPublishLoot(model, out _, out reason))
                Debug.LogError("[Emberdelve][OfflineLoot] corpse for player " + playerId + " could not be published after the loadout was emptied: " + reason);
            reason = null;
            return true;
        }

        private void OnDestroy()
        {
            Detach();
            if (ReferenceEquals(_instance, this)) _instance = null;
        }

        public bool TryPublishLoot(CorpseContainerModel model, out ulong id, out string reason)
        {
            id = 0;
            if (_presenter == null || model == null)
            {
                reason = "Loot window is unavailable";
                return false;
            }
            if (!CorpseContainerWire.TryCreate(_nextId, model, out _, out reason)) return false;
            id = _nextId++;
            _containers.Add(id, model);
            // A chest is its own world object; a dead enemy or bot is searched on its own body.
            if (model.Kind != "chest")
            {
                _presenter.OnBodyPresence(id, model.WorldPosition, model.Revision, false);
            }
            reason = null;
            return true;
        }

        public void OpenLoot(IInteractionActor actor, ulong id)
        {
            if (_presenter == null || _rig == null || !ReferenceEquals(actor, _rig) || !_containers.ContainsKey(id)) return;
            _presenter.OpenContainer(id);
        }

        internal bool RequestState(CorpseLootWorldPresenter presenter, PlayerRig rig, ulong id,
            System.Func<uint, bool> beforeSend, out uint sequence)
        {
            sequence = 0;
            if (!_containers.TryGetValue(id, out CorpseContainerModel model)) return false;
            sequence = _sequence++;
            if (beforeSend != null && !beforeSend(sequence)) { sequence = 0; return false; }
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
            sequence = _sequence++;
            if (beforeSend != null && !beforeSend(sequence)) { sequence = 0; return false; }

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

        private bool Validate(PlayerRig rig, CorpseContainerModel model, out string reason)
        {
            if (rig == null || model == null) { reason = "Player or loot is unavailable"; return false; }
            if (!rig.IsAlive) { reason = "The dead cannot loot"; return false; }
            if (_director == null) _director = FindFirstObjectByType<RunDirector>();
            if (_director == null) { reason = "The run is unavailable"; return false; }
            if (!_director.IsInteractionActorAdmitted(rig, out reason)) return false;

            if (model.Kind == "chest")
            {
                ChestInteractable source = null;
                foreach (ChestInteractable chest in FindObjectsByType<ChestInteractable>(FindObjectsSortMode.None))
                    if (ReferenceEquals(chest.LootContainer, model)) { source = chest; break; }
                if (source == null) { reason = "Chest is unavailable"; return false; }
                if (!source.CanInteract(rig, out reason)) return false;
            }

            Vector3 origin = rig.InteractionOrigin;
            Vector3 offset = model.WorldPosition - origin;
            float distance = offset.magnitude;
            if (float.IsNaN(distance) || float.IsInfinity(distance) || distance > Mathf.Max(0.5f, _director.InteractionReach))
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
    }
}
