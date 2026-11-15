#if DEVELOPMENT_BUILD || UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Emberdelve.Flow;
using Emberdelve.Interaction;
using Emberdelve.Items;
using Emberdelve.Level;
using Emberdelve.Player;
using Emberdelve.Rules;
using UnityEngine;

namespace Emberdelve.Net
{
    internal sealed partial class NetServer
    {
        private readonly Dictionary<int, (string kit, string ammo, ulong net)> _descentProofItems
            = new Dictionary<int, (string, string, ulong)>();
        private string _royaleProofLootKey;
        private int _royaleProofLootValue;
        private PlayerRig _botLootProofActor;
        private LootPickup _botLootProofPickup;
        private string _botLootProofUid;
        private bool _botLootProofReleased, _botLootProofReported;
        private (string kit, string ammo) _botDescentProofItems;
        private ulong _botLootProofNetworkId;
        private bool _botExitProofPlaced, _botExitProofReported;
        private float _botExitProofStarted, _botExitProofDiagnosticAt;
        private (string kit, string ammo) _botExitProofItems;

        private void TickBotLootProof()
        {
            if (!File.Exists(_descentProofPath)) return;
            string command = ReadSharedText(_descentProofPath).Trim();
            if (_botLootProofActor == null && command == "botloot-stage")
            {
                foreach (var session in _director.Players)
                {
                    var rig = session.Rig;
                    var brain = rig != null ? rig.GetComponent<Emberdelve.AI.RoyaleBotController>() : null;
                    if (!session.IsBot || !session.IsRunning || session.Floor != 1 || rig == null || rig.IsDead || !rig.IsInPlay
                        || brain == null || !brain.enabled || brain.TargetPlayerId >= 0 || brain.SeekingSafeZone) continue;
                    var definition = rig.Equipment.MainHandInstance(0)?.Definition;
                    if (definition == null || rig.Inventory.FindFreeOrigin(definition.EffectiveFootprint).x < 0
                        || !rig.Inventory.Catalog.TryMintCommon(ItemIdentityIssuer.CreateLocalTrusted(),
                        definition, out var item, out _)) continue;
                    // Pause only the chosen brain while publishing a known source. Resume before
                    // collection; no direct pickup call, damage immunity or inventory injection.
                    brain.enabled = false;
                    _botLootProofActor = rig; _botLootProofUid = item.UidText;
                    _botLootProofNetworkId = rig.GetComponent<Unity.Netcode.NetworkObject>().NetworkObjectId;
                    _botLootProofPickup = _director.SpawnLootInstance(item, rig.transform.position + Vector3.up * .45f);
                    if (_botLootProofPickup == null) { brain.enabled = true; _descentProofFailed = true; return; }
                    _log("Royale bot loot staged id=" + rig.PlayerId + " uid=" + _botLootProofUid);
                    break;
                }
            }
            if (_botLootProofActor == null || _botLootProofReported) return;
            if (!_botLootProofReleased && command == "botloot-go " + _botLootProofUid)
            {
                _botLootProofReleased = true;
                _botLootProofActor.GetComponent<Emberdelve.AI.RoyaleBotController>().enabled = true;
            }
            if (!_botLootProofReleased) return;
            int owned = 0, copies = 0;
            foreach (var session in _director.Players)
                if (session.Rig?.Inventory != null)
                    foreach (var slot in session.Rig.Inventory.Slots)
                        if (slot.UidText == _botLootProofUid)
                        { copies++; if (session.Rig == _botLootProofActor) owned++; }
            if (owned != 1 || copies != 1 || (_botLootProofPickup != null && !_botLootProofPickup.IsSpent)) return;
            _botLootProofReported = true;
            _log("Royale bot loot collected id=" + _botLootProofActor.PlayerId + " uid=" + _botLootProofUid + " copies=" + copies);
        }
        private static string LootProofKey(ContainerEntry entry) => entry?.item == null ? "" : !string.IsNullOrEmpty(entry.item.uid)
            ? "uid:" + entry.item.uid : "stack:" + entry.item.itemId + ":" + entry.item.quantity;
        private bool CollectRoyaleProofLoot(NetIdentity row)
        {
            var layout = _director.Layout;
            foreach (var pickup in UnityEngine.Object.FindObjectsByType<LootPickup>(FindObjectsSortMode.None))
            {
                var position = pickup.HomePosition;
                if (pickup.IsSpent || pickup.Item == null || pickup.Item.value <= 0 || pickup.NetworkLootId == 0
                    || position.x < layout.origin.x || position.z < layout.origin.z
                    || position.x >= layout.origin.x + layout.gridWidth * layout.CellSize
                    || position.z >= layout.origin.z + layout.gridHeight * layout.CellSize) continue;
                var before = new HashSet<string>();
                foreach (var entry in row.Rig.Inventory.ToContainerSnapshot().entries) if (entry?.item != null) before.Add(LootProofKey(entry));
                row.Rig.Movement.TeleportTo(position - Vector3.up * .45f); Physics.SyncTransforms();
                if (!_inventory.RequestFloorLootPickup(row.ClientId, row.Object, pickup.NetworkLootId, 1)) return false;
                foreach (var entry in row.Rig.Inventory.ToContainerSnapshot().entries)
                    if (entry?.item != null && !before.Contains(LootProofKey(entry))) { _royaleProofLootKey = LootProofKey(entry); break; }
                _royaleProofLootValue = row.Rig.Inventory.RecoveredValue;
                _log("Royale loot proof id=" + row.PlayerId + " key=" + _royaleProofLootKey + " value=" + _royaleProofLootValue);
                return !string.IsNullOrEmpty(_royaleProofLootKey) && _royaleProofLootValue > 0;
            }
            return false;
        }
        private bool ArrangeUncontestedExitProof(ExtractionPoint exit, bool includeHumans = false)
        {
            var map = exit.GetComponentInParent<DungeonModuleSceneAssembler>(); if (map == null) return false;
            var layout = map.RuntimeLayout; var nav = map.RuntimeNavGrid; var positions = new List<Vector3>();
            var actors = new List<(Transform target, PlayerRig rig)>();
            foreach (var session in _director.Players)
                if (session.Floor == 3 && (session.IsBot || includeHumans) && session.IsRunning && !session.Rig.IsDead
                    && session.Rig != _botLootProofActor) actors.Add((session.Rig.transform, session.Rig));
            bool IsHuman(PlayerRig rig) => rig != null && _director.SessionOf(rig) is PlayerSession session && !session.IsBot;
            actors.Sort((a, b) => (IsHuman(a.rig) ? 0 : 1).CompareTo(IsHuman(b.rig) ? 0 : 1));
            var protectedPositions = new List<Vector3>();
            if (!includeHumans) foreach (var row in _table.All)
                if (row.Rig != null && _director.SessionOf(row.Rig)?.IsRunning == true)
                    protectedPositions.Add(row.Rig.transform.position);
            int bots = actors.Count;
            foreach (var enemy in map.GetComponentsInChildren<Emberdelve.AI.EnemyAgent>())
                if (!enemy.IsDead) actors.Add((enemy.transform, null));
            foreach (var actor in actors)
            {
                bool placed = false;
                for (int z = 1; z < nav.height - 1 && !placed; z++) for (int x = 1; x < nav.width - 1 && !placed; x++)
                {
                    var cell = new Vector2Int(x,z); var at = layout.CellCenterWorld(cell) + Vector3.up * .15f;
                    if (!nav.IsWalkable(x,z) || Vector3.Distance(at, exit.transform.position) < 35) continue;
                    bool near = false;
                    foreach (var old in positions) if (Vector3.Distance(old,at) < 2) near = true;
                    // Both proof humans are passive while the bot extracts. Cluster them away from
                    // the exit regardless of party; every AI still stays30m from EACH waiting human.
                    // Sampling every cell avoids missing clear2m aisles in the authored room geometry.
                    foreach (var human in protectedPositions)
                    {
                        float distance = Vector3.Distance(human, at);
                        bool waitingHuman = IsHuman(actor.rig);
                        if (distance < (waitingHuman ? 2 : 30) || (waitingHuman && distance > 6)) near = true;
                    }
                    if (near || !DungeonModuleSceneAssembler.TryFindClearSpawnCell(layout,map.transform,cell,cell,cell,out _)
                        || Physics.CheckCapsule(at + Vector3.up * .38f,at + Vector3.up * 1.44f,.36f,Physics.AllLayers,QueryTriggerInteraction.Ignore)) continue;
                    if (actor.rig != null) actor.rig.Movement.TeleportTo(at); else actor.target.position = at;
                    positions.Add(at);
                    if (IsHuman(actor.rig)) protectedPositions.Add(at);
                    placed = true; Physics.SyncTransforms();
                }
                if (!placed)
                {
                    _logError("Royale exit proof setup has no clear staging cell actor=" + actor.target.name
                        + " human=" + IsHuman(actor.rig) + " placed=" + positions.Count
                        + " protectedHumans=" + protectedPositions.Count + " exit=" + exit.transform.position);
                    return false;
                }
            }
            _log("Royale exit proof uncontested setup bots=" + bots + " monsters=" + (actors.Count - bots) + " minDistance=35 aiEnabled=True damageEnabled=True");
            return true;
        }
        private int _descentProofPending, _descentProofApproaching, _descentProofCompleted = 1;
        private float _descentProofSourceDuration;
        private bool _descentProofFailed, _finalExitProofPlaced;
        private readonly string _descentProofPath = DescentProofPath();
        private static string DescentProofPath()
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-ember-royale-descent-proof-file");
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        private static string ProofAmmo(PlayerRig rig) => JsonUtility.ToJson(typeof(ClassLoadout)
            .GetMethod("CapturePreparedAmmo", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(rig.Loadout, new object[] { LoadoutSnapshot.Capture(rig.Inventory, rig.Equipment) }));
        private void TickRoyaleDescentProof()
        {
            if (_descentProofFailed || string.IsNullOrEmpty(_descentProofPath) || !_royaleMapProofReported
                || _director == null || _table.Count != 2) return;
            TickBotLootProof();
            if (_descentProofApproaching != 0)
            {
                foreach (var row in _table.All) if (!row.Rig.IsInFloorTransit) return;
                if (_botLootProofActor == null || !_botLootProofActor.IsInFloorTransit) return;
                if (!_director.AdvanceRoyaleFloorForProof(_descentProofApproaching - 1, _descentProofSourceDuration))
                { _descentProofFailed = true; return; }
                _descentProofPending = _descentProofApproaching; _descentProofApproaching = 0;
                _log("Royale descent proof queued floor=" + _descentProofPending + " humans=" + _descentProofItems.Count);
                return;
            }
            if (_descentProofPending != 0)
            {
                foreach (var row in _table.All)
                    if (_director.SessionOf(row.Rig)?.Floor != _descentProofPending || row.Rig.IsInFloorTransit) return;
                if (_botLootProofActor == null || _director.SessionOf(_botLootProofActor)?.Floor != _descentProofPending
                    || _botLootProofActor.IsInFloorTransit) return;
                int count = 0; var parties = new Dictionary<int, int>();
                foreach (var session in _director.Players)
                    if (session.Floor == _descentProofPending && session.IsRunning && !session.Rig.IsDead)
                    { count++; parties[session.PartyId] = parties.TryGetValue(session.PartyId, out int n) ? n + 1 : 1; }
                bool valid = count == new RoyaleRoster(_director.RoyaleMode.Value).Capacity;
                foreach (int size in parties.Values) valid &= size == (int)_director.RoyaleMode.Value;
                int botLootCopies = 0;
                foreach (var slot in _botLootProofActor.Inventory.Slots)
                    if (slot.UidText == _botLootProofUid) botLootCopies++;
                ulong botNetId = _botLootProofActor.GetComponent<Unity.Netcode.NetworkObject>().NetworkObjectId;
                bool botRetained = botLootCopies == 1 && botNetId == _botLootProofNetworkId
                    && LoadoutSnapshot.Capture(_botLootProofActor.Inventory, _botLootProofActor.Equipment).checksum == _botDescentProofItems.kit
                    && ProofAmmo(_botLootProofActor) == _botDescentProofItems.ammo;
                valid &= botRetained;
                _log("Royale bot descent floor=" + _descentProofPending + " id=" + _botLootProofActor.PlayerId
                    + " netId=" + botNetId + " uid=" + _botLootProofUid + " retained=" + botRetained
                    + " generation=" + _botLootProofActor.FloorMovementGeneration);
                foreach (var row in _table.All)
                {
                    var old = _descentProofItems[row.PlayerId]; var rig = row.Rig;
                    string kit = LoadoutSnapshot.Capture(rig.Inventory, rig.Equipment).checksum;
                    bool retained = kit == old.kit && ProofAmmo(rig) == old.ammo && row.Object.NetworkObjectId == old.net;
                    valid &= retained;
                    if (row.ClientId != Unity.Netcode.NetworkManager.ServerClientId)
                    {
                        bool found = false;
                        foreach (var entry in rig.Inventory.ToContainerSnapshot().entries) if (LootProofKey(entry) == _royaleProofLootKey) found = true;
                        valid &= found && rig.Inventory.RecoveredValue == _royaleProofLootValue;
                        _log("Royale loot retained floor=" + _descentProofPending + " key=" + _royaleProofLootKey + " found=" + found + " value=" + rig.Inventory.RecoveredValue);
                    }
                    var view = row.Object.GetComponent<NetPlayerRig>(); var ammo = view.RangedAmmo.Value;
                    _log("Royale descent actor floor=" + _descentProofPending + " id=" + row.PlayerId + " netId=" + row.Object.NetworkObjectId
                        + " kit=" + kit + " retained=" + retained + " generation=" + rig.FloorMovementGeneration
                        + " magazine=" + ammo.Magazine + " reserve=" + ammo.Reserve + " pos=" + rig.transform.position.ToString("F3"));
                }
                _log("Royale descent proof floor=" + _descentProofPending + " population=" + count + " fullParties=" + valid);
                _descentProofFailed = !valid; _descentProofCompleted = _descentProofPending; _descentProofPending = 0;
                return;
            }
            if (_descentProofCompleted == 3 && !_botExitProofReported && File.Exists(_descentProofPath)
                && ReadSharedText(_descentProofPath).Trim() == "5")
            {
                var exit = _director.FloorCoordinator.EncounterFor(3)?.FinalExit;
                if (exit == null || _botLootProofActor == null) return;
                if (!_botExitProofPlaced)
                {
                    if (!ArrangeUncontestedExitProof(exit, true)) { _descentProofFailed = true; return; }
                    _botExitProofItems = (LoadoutSnapshot.Capture(_botLootProofActor.Inventory,
                        _botLootProofActor.Equipment).checksum, ProofAmmo(_botLootProofActor));
                    _botLootProofActor.Movement.TeleportTo(exit.transform.position - Vector3.up * 1.1f);
                    Physics.SyncTransforms();
                    _botExitProofStarted = Time.time; _botExitProofPlaced = true;
                    _log("Royale bot final exit approaching id=" + _botLootProofActor.PlayerId);
                    return;
                }
                if (!_botLootProofActor.IsExtracted)
                {
                    if (Time.time >= _botExitProofDiagnosticAt)
                    {
                        _botExitProofDiagnosticAt = Time.time + 2f;
                        var brain = _botLootProofActor.GetComponent<Emberdelve.AI.RoyaleBotController>();
                        bool allowed = exit.CanInteract(_botLootProofActor, out string why);
                        _log("Royale bot exit waiting alive=" + !_botLootProofActor.IsDead + " inPlay=" + _botLootProofActor.IsInPlay
                            + " enabled=" + brain.enabled + " seek=" + brain.SeekingExtraction + " hold=" + brain.HoldingExtraction
                            + " target=" + brain.TargetPlayerId + " allowed=" + allowed + " reason=" + why
                            + " recovered=" + _botLootProofActor.Inventory.RecoveredValue + " pos=" + _botLootProofActor.transform.position.ToString("F3"));
                    }
                    return;
                }
                int copies = 0;
                foreach (var slot in _botLootProofActor.Inventory.Slots) if (slot.UidText == _botLootProofUid) copies++;
                bool retained = copies == 1
                    && LoadoutSnapshot.Capture(_botLootProofActor.Inventory, _botLootProofActor.Equipment).checksum == _botExitProofItems.kit
                    && ProofAmmo(_botLootProofActor) == _botExitProofItems.ammo
                    && _botLootProofActor.GetComponent<Unity.Netcode.NetworkObject>().NetworkObjectId == _botLootProofNetworkId
                    && _director.SessionOf(_botLootProofActor).HasResult;
                bool held = Time.time - _botExitProofStarted >= exit.HoldDuration;
                _botExitProofReported = true; _descentProofFailed = !retained || !held;
                _log("Royale bot final exit id=" + _botLootProofActor.PlayerId + " uid=" + _botLootProofUid
                    + " retained=" + retained + " held=" + held);
                return;
            }
            if (_descentProofCompleted == 3 && !_finalExitProofPlaced && File.Exists(_descentProofPath)
                && ReadSharedText(_descentProofPath).Trim() == "4")
            {
                var exit = _director.FloorCoordinator.EncounterFor(3)?.FinalExit;
                if (exit == null || !exit.gameObject.activeInHierarchy) return;
                _finalExitProofPlaced = true;
                if (!ArrangeUncontestedExitProof(exit)) { _descentProofFailed = true; _logError("Could not arrange uncontested exit proof"); return; }
                foreach (var row in _table.All) if (row.ClientId != Unity.Netcode.NetworkManager.ServerClientId)
                {
                    row.Rig.Movement.TeleportTo(exit.transform.position - Vector3.up * 1.1f); Physics.SyncTransforms();
                    _log("Royale final exit proof approaching id=" + row.PlayerId);
                }
                return;
            }
            if (!File.Exists(_descentProofPath) || !int.TryParse(ReadSharedText(_descentProofPath).Trim(), out int destination)
                || destination != _descentProofCompleted + 1 || destination > 3) return;
            int source = destination - 1;
            var encounter = _director.FloorCoordinator.EncounterFor(source);
            if (encounter == null || !_director.TryGetRoyaleFloorClock(source, out float elapsed, out float remaining)) return;
            _descentProofFailed = true; // A failed setup must not silently repeat a partly consumed transaction.
            if (!_botLootProofReported || _botLootProofActor == null || _botLootProofActor.IsDead
                || _director.SessionOf(_botLootProofActor)?.Floor != source)
            { _logError("Royale descent proof requires the living bot that collected the source UID"); return; }
            if (!_director.AdvanceRoyaleFloorForProof(source, elapsed + remaining - 59)) return;
            encounter.Tick(); _descentProofItems.Clear();
            foreach (var row in _table.All)
            {
                var rig = row.Rig;
                if (source == 1 && row.ClientId != Unity.Netcode.NetworkManager.ServerClientId && !CollectRoyaleProofLoot(row))
                { _logError("Royale proof must collect actual initial-floor loot before descending"); return; }
                _descentProofItems.Add(row.PlayerId, (LoadoutSnapshot.Capture(rig.Inventory, rig.Equipment).checksum,
                    ProofAmmo(rig), row.Object.NetworkObjectId));
                bool entered = false;
                foreach (var group in encounter.Groups)
                {
                    foreach (var slot in group.GetComponentsInChildren<DescentPortalSlot>(true))
                    {
                        if (group.SlotUsed(slot.Slot)) continue;
                        rig.Movement.TeleportTo(slot.transform.position - Vector3.up * 1.1f); Physics.SyncTransforms();
                        if (row.ClientId == Unity.Netcode.NetworkManager.ServerClientId)
                        {
                            if (!group.TryEnter(rig, slot.Slot, out string reason))
                            { _logError("Royale descent proof portal refused: " + reason); return; }
                        }
                        else _log("Royale portal proof remote approaching floor=" + source + " group=" + group.Group + " slot=" + slot.Slot);
                        entered = true; break;
                    }
                    if (entered) break;
                }
                if (!entered) { _logError("Royale descent proof missing portal slot"); return; }
            }
            bool botApproaching = false;
            // Use the last free slot so it does not overlap the remote human awaiting its RPC
            // in the first free slot. Only position the bot; its normal FixedUpdate enters it.
            for (int groupIndex = encounter.Groups.Count - 1; groupIndex >= 0 && !botApproaching; groupIndex--)
            {
                var group = encounter.Groups[groupIndex];
                var slots = group.GetComponentsInChildren<DescentPortalSlot>(true);
                for (int slotIndex = slots.Length - 1; slotIndex >= 0; slotIndex--)
                {
                    var slot = slots[slotIndex];
                    if (group.SlotUsed(slot.Slot) || !slot.isActiveAndEnabled) continue;
                    _botLootProofActor.Movement.TeleportTo(slot.transform.position - Vector3.up * 1.1f);
                    Physics.SyncTransforms();
                    _botDescentProofItems = (LoadoutSnapshot.Capture(_botLootProofActor.Inventory,
                        _botLootProofActor.Equipment).checksum, ProofAmmo(_botLootProofActor));
                    _log("Royale bot portal approaching floor=" + source + " id=" + _botLootProofActor.PlayerId
                        + " group=" + group.Group + " slot=" + slot.Slot);
                    botApproaching = true; break;
                }
            }
            if (!botApproaching) { _logError("Royale descent proof missing bot portal slot"); return; }
            _descentProofSourceDuration = (float)encounter.Portals.Duration;
            _descentProofApproaching = destination; _descentProofFailed = false;
        }
    }
}
#endif
