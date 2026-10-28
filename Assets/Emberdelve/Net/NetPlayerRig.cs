using System.Collections.Generic;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Interaction;
using Emberdelve.Items;
using Emberdelve.Movement;
using Emberdelve.Player;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>What this process is to one spawned player object.</summary>
    public enum NetPlayerRole
    {
        /// <summary>Not spawned yet.</summary>
        None = 0,
        /// <summary>Server process, someone else's player: the full headless rig the simulation runs.</summary>
        Server = 1,
        /// <summary>Host process, its own player: presents (camera, input) and simulates.</summary>
        HostPlayer = 2,
        /// <summary>Client process, its own player: camera, no simulation (prediction is U3A4).</summary>
        Owner = 3,
        /// <summary>Client process, someone else's player: a view that follows the server's transform.</summary>
        Observer = 4
    }

    /// <summary>
    /// U3A2: the NetworkBehaviour on the single network player prefab. One prefab serves every role; what each process keeps
    /// alive of the rig is decided here in OnNetworkSpawn from IsServer / IsOwner (the plan's section 2.4).
    ///
    /// The server admitted the player (RunDirector.TrySpawnNetworkPlayer: id, party, class, sender) BEFORE the NetworkObject
    /// was spawned, and replicates the result: the server-issued player id and party are server-write / everyone-read
    /// NetworkVariables, and a client only ever uses them as labels (PlayerRig.SetNetworkPlayerId). Nothing here sends
    /// input yet and nothing here carries a player or combat id in a parameter; the input channel is U3A4.
    ///
    /// U3A3: also replicates the class id (server-write / everyone-read), keeps a registry of the spawned player objects for the
    /// client probe, logs the owner / view lifecycle lines the admission gate reads, and (development builds and the Editor only)
    /// carries ONE test RPC that a hostile client can call on somebody else's object: the server rejects it because the sender
    /// connection is not the object's owner. The RPC takes an arbitrary probe number and no identity of any kind.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayerRig))]
    public sealed class NetPlayerRig : NetworkBehaviour
    {
        public readonly NetworkVariable<int> ServerPlayerId = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<int> ServerPartyId = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>The class the server admitted this player as (the id string only; the numbers stay the server's).</summary>
        public readonly NetworkVariable<FixedString32Bytes> ServerClassId = new NetworkVariable<FixedString32Bytes>(
            default(FixedString32Bytes), NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private static readonly List<NetPlayerRig> SpawnedRigs = new List<NetPlayerRig>();

        /// <summary>Every spawned player object this process knows (the server: all admitted; a client: all views, owned or not).</summary>
        public static IReadOnlyList<NetPlayerRig> Spawned => SpawnedRigs;

        private PlayerRig _rig;

        /// <summary>The replicated class id as text ("" before the first value).</summary>
        public string ClassIdText => ServerClassId.Value.ToString();

        public NetPlayerRole Role { get; private set; }
        public PlayerRig Rig => _rig != null ? _rig : (_rig = GetComponent<PlayerRig>());

        public override void OnNetworkSpawn()
        {
            _rig = GetComponent<PlayerRig>();

            if (IsServer)
            {
                // Server and host both ran the admission already, so the rig has its real id and party.
                ServerPlayerId.Value = _rig.PlayerId;
                ServerPartyId.Value = _rig.PartyId;
                ServerClassId.Value = new FixedString32Bytes(ClassIdForWire(_rig));
                Role = IsOwner ? NetPlayerRole.HostPlayer : NetPlayerRole.Server;
                ApplyServerRole();
            }
            else
            {
                Role = IsOwner ? NetPlayerRole.Owner : NetPlayerRole.Observer;
                ServerPlayerId.OnValueChanged += OnServerPlayerIdChanged;
                ServerPartyId.OnValueChanged += OnServerPartyIdChanged;
                ApplyClientView();
            }

            if (!SpawnedRigs.Contains(this)) SpawnedRigs.Add(this);

            Debug.Log(Emberdelve.Net.Rules.NetTuning.LogPrefix + "[" + (IsServer ? (IsOwner ? "host" : "server") : "client") +
                      "] player object spawned role=" + Role + " netId=" + NetworkObjectId + " owner=" + OwnerClientId +
                      " playerId=" + _rig.PlayerId + " party=" + _rig.PartyId + " class=" + ClassIdText);
        }

        public override void OnNetworkDespawn()
        {
            ServerPlayerId.OnValueChanged -= OnServerPlayerIdChanged;
            ServerPartyId.OnValueChanged -= OnServerPartyIdChanged;
            SpawnedRigs.Remove(this);

            if (!IsServer)
            {
                // Read the replicated id before anything resets; the admission gate waits for this line on the other clients.
                Debug.Log(Emberdelve.Net.Rules.NetTuning.LogPrefix + "[client] view despawned id=" + ServerPlayerId.Value +
                          " owned=" + IsOwner + " views=" + Mathf.Max(0, SpawnedRigs.Count));
            }

            Role = NetPlayerRole.None;
        }

        private static string ClassIdForWire(PlayerRig rig)
        {
            string id = rig != null && rig.Class != null && rig.Class.classId != null ? rig.Class.classId : "";
            return id.Length > 24 ? id.Substring(0, 24) : id;
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        /// <summary>Test state a legitimate owner call writes (and a forged call must never touch). Server side only.</summary>
        public int TestProbe { get; private set; }

        /// <summary>Server side: forged owner calls rejected so far in this process.</summary>
        public static int ForgeRejectCount { get; private set; }

        /// <summary>
        /// DEVELOPMENT-ONLY forged-owner probe. RequireOwnership is deliberately OFF so a client that is NOT this object's owner can
        /// still deliver the call; the handler therefore has to do the ownership check itself, from the connection that carried the
        /// message (RpcParams.Receive.SenderClientId) and never from anything in the parameters. A call from another connection is
        /// counted and ignored; the owner's own call writes <see cref="TestProbe"/> so the gate can tell the check discriminates.
        /// The parameter is a free-form number, not an identity: no RPC in this project carries a player or combat id.
        /// </summary>
        [Rpc(SendTo.Server, RequireOwnership = false)]
        public void ForgeOwnerTestRpc(int probe, RpcParams rpcParams = default)
        {
            ulong sender = rpcParams.Receive.SenderClientId;
            string senderPlayer = NetIdentityTable.Current != null ? NetIdentityTable.Current.ResolveSender(sender).ToString() : "?";

            if (sender != OwnerClientId)
            {
                ForgeRejectCount++;
                Debug.Log(Emberdelve.Net.Rules.NetTuning.LogPrefix + "[server] REJECT forge-owner count=" + ForgeRejectCount +
                          " target=" + ServerPlayerId.Value + " owner=" + OwnerClientId + " sender=" + sender +
                          " senderPlayer=" + senderPlayer + " probe=" + TestProbe);
                return;
            }

            TestProbe = probe;
            Debug.Log(Emberdelve.Net.Rules.NetTuning.LogPrefix + "[server] forge-owner self-call accepted target=" +
                      ServerPlayerId.Value + " sender=" + sender + " probe=" + TestProbe);
        }
#endif

        private void ApplyServerRole()
        {
            if (Role != NetPlayerRole.Server) return; // the host's own player keeps the camera and input the prefab authored

            // RunDirector.TrySpawnNetworkPlayer already stripped the presentation; make it unconditional here too so a
            // rig spawned by some other path is still a headless simulation body.
            _rig.SetInputEnabled(false);
            if (_rig.Movement != null) _rig.Movement.ExternalDriven = true;
        }

        /// <summary>
        /// Client process: the rig is a view of the server's player. PlayerRig.Awake already skipped combat registration
        /// (NetRole.IsClientOnly), so what is left is making the object follow the NetworkTransform instead of fighting it:
        /// no CharacterController (it would override transform writes), no self-driven movement, no look, no combat.
        /// </summary>
        private void ApplyClientView()
        {
            ApplyReplicatedIdentity();

            if (_rig.Movement != null) _rig.Movement.ExternalDriven = true;
            _rig.SetInputEnabled(false);

            CharacterController controller = GetComponent<CharacterController>();
            if (controller != null) controller.enabled = false;

            DisableBehaviour<PlayerLook>();
            DisableBehaviour<MeleeCombat>();
            DisableBehaviour<RangedCombat>();
            DisableBehaviour<Interactor>();
            DisableBehaviour<HotbarController>();

            if (Role == NetPlayerRole.Observer) StripPresentation();
            // Owner: camera and listener stay. Input and prediction arrive with the movement stream (U3A4).
        }

        private void StripPresentation()
        {
            Camera[] cameras = GetComponentsInChildren<Camera>(true);
            for (int i = 0; i < cameras.Length; i++)
            {
                cameras[i].enabled = false;
                cameras[i].gameObject.tag = "Untagged";
                AudioListener listener = cameras[i].GetComponent<AudioListener>();
                if (listener != null) listener.enabled = false;
            }

            Light[] lights = GetComponentsInChildren<Light>(true);
            for (int i = 0; i < lights.Length; i++) lights[i].enabled = false;
        }

        private void DisableBehaviour<T>() where T : Behaviour
        {
            T component = GetComponent<T>();
            if (component != null) component.enabled = false;
        }

        private void ApplyReplicatedIdentity()
        {
            _rig.SetNetworkPlayerId(ServerPlayerId.Value);
            _rig.AssignParty(ServerPartyId.Value, IsOwner);
        }

        private void OnServerPlayerIdChanged(int previous, int current) => ApplyReplicatedIdentity();
        private void OnServerPartyIdChanged(int previous, int current) => ApplyReplicatedIdentity();
    }
}
