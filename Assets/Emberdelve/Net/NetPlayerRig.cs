using System.Collections.Generic;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Interaction;
using Emberdelve.Items;
using Emberdelve.Movement;
using Emberdelve.Net.Rules;
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
    ///
    /// U3A4a: carries the movement stream. The owning client (NetInputSender) calls <see cref="SubmitMoveRpc"/> once per network
    /// tick with its last three input frames; the server hands the batch to NetMovementServer together with the connection that
    /// delivered it (RpcParams.Receive.SenderClientId, never a payload field). A development-only twin,
    /// <c>ForgeMoveTestRpc</c>, has InvokePermission Everyone so a client can try to feed ANOTHER player's object; the same server path
    /// rejects and counts it. No movement RPC parameter names a player, combat or party id.
    ///
    /// U3A4b: the way back. The server tells the owner what it reached (<see cref="AckRpc"/>, unreliable, owner only, every second
    /// tick) and publishes the movement numbers it resolved for this player (<see cref="MoveParams"/>, owner-read), and the owner
    /// predicts with them (NetOwnerPrediction). Observers are unchanged: they interpolate the server's NetworkTransform.
    ///
    /// U3B2: the first production combat channel, active skills (slots 0 and 1). The owner's NetSkillSender calls
    /// <see cref="SkillIntentRpc"/> (reliable, ordered) with a slot and a per-connection sequence; NetSkillServer decides everything
    /// (identity first, the connection's gate, the server's own SkillCaster) and answers the owner with <see cref="SkillResultRpc"/>
    /// (cast or refused + reason, the server's mana and cooldowns), publishes the owner's mana / cooldowns in the owner-read
    /// <see cref="SkillState"/> variable and tells every other client about a cast with <see cref="SkillVisualRpc"/>. No skill RPC
    /// parameter names a player, combat or party id, and no client applies a gameplay outcome. All three exist in release builds; only
    /// the hostile probes below stay development-only. The two hostile-probe RPCs use InvokePermission (RequireOwnership is obsolete in
    /// NGO 2.x): Everyone, because the server's handler is the check, exactly as before.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayerRig))]
    public sealed class NetPlayerRig : NetworkBehaviour, IDoorInteractionAuthority, IFloorLootInteractionAuthority, IDescentPortalAuthority
    {
        public readonly NetworkVariable<int> ServerPlayerId = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<int> ServerPartyId = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>The skill kit this player runs ("active,active;passive,passive" ids), set by the server after validating the player's request. Clients use it to show the same skills.</summary>
        public readonly NetworkVariable<FixedString128Bytes> ServerKit = new NetworkVariable<FixedString128Bytes>(
            default(FixedString128Bytes), NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>The class the server admitted this player as (the id string only; the numbers stay the server's).</summary>
        public readonly NetworkVariable<FixedString32Bytes> ServerClassId = new NetworkVariable<FixedString32Bytes>(
            default(FixedString32Bytes), NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>
        /// U3A4b: the movement numbers the server resolved for this player (DEX, gear, status and flow folded in), readable by the owner
        /// only, written by the server only. The owner's prediction runs on exactly these. NEW NGO usage (UNCERTAIN): a
        /// NetworkVariable of an INetworkSerializable + IEquatable struct with Owner read permission.
        /// </summary>
        public readonly NetworkVariable<MoveParamsWire> MoveParams = new NetworkVariable<MoveParamsWire>(
            default(MoveParamsWire), NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);

        /// <summary>
        /// U3B2: the server's mana and cooldown picture of this player, readable by the owner only, written by the server only (a few
        /// times a second while anything changes, see NetSkillServer.Tick). The owner's NetOwnerSkillView shows these numbers; it never
        /// regenerates or counts down mana itself.
        /// </summary>
        public readonly NetworkVariable<SkillStateWire> SkillState = new NetworkVariable<SkillStateWire>(
            default(SkillStateWire), NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);

        /// <summary>The server's run-wide countdown sample for this owner; clients cannot author or change the raid clock.</summary>
        public readonly NetworkVariable<RaidClockWire> RaidClock = new NetworkVariable<RaidClockWire>(
            default(RaidClockWire), NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);

        /// <summary>
        /// U3B3: the server's health, maximum and death flag of this player, readable by everyone, written by the server only (see
        /// NetCombatServer.Tick, a few times a second while it changes). Clients show it through HealthComponent.ApplyReplicatedState and never
        /// compute it. Same NGO usage as <see cref="SkillState"/> with Everyone read permission.
        /// </summary>
        public readonly NetworkVariable<CombatVitalsWire> Vitals = new NetworkVariable<CombatVitalsWire>(
            default(CombatVitalsWire), NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>Effective server guard, including stagger/death; presentation only on the owner.</summary>
        public readonly NetworkVariable<bool> GuardRaised = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);

        /// <summary>Approximate owner buff display; exact guard expiry remains server-owned.</summary>
        public readonly NetworkVariable<float> ImpactGuardRemaining = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
        public readonly NetworkVariable<RangedAmmoWire> RangedAmmo = new NetworkVariable<RangedAmmoWire>(
            default, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<AvatarCombatWire> AvatarCombat = new NetworkVariable<AvatarCombatWire>(
            default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        public readonly NetworkVariable<AvatarArmorWire> AvatarArmor = new NetworkVariable<AvatarArmorWire>(
            default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private PlayerAvatar _armorTarget;
        private AvatarArmorWire _presentedArmor;
        private bool _armorPresented;
        public readonly NetworkVariable<FloorMovementWire> FloorMovement = new NetworkVariable<FloorMovementWire>(
            default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        public readonly NetworkVariable<DescentPortalWire> DescentPortals = new NetworkVariable<DescentPortalWire>(
            default, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
        public readonly NetworkVariable<RoyaleZoneWire> RoyaleZone = new NetworkVariable<RoyaleZoneWire>(
            default, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
        public void SetRoyaleZoneFromServer(RoyaleZoneWire state)
        { if (IsServer && IsSpawned && state.IsValid && !RoyaleZone.Value.Equals(state)) RoyaleZone.Value = state; }
        private uint _appliedFloorGeneration;
        private bool _floorMovementApplied;
        private int _ownerPresentedSwingId;
        private int _ownerPresentedRecoilId;
        private bool _ownerCombatAnimating;
        private void LateUpdate()
        {
            if (!IsSpawned || Rig == null) return;
            var view = Rig.GetComponent<Emberdelve.Player.PlayerAvatar>();
            if (IsServer)
            {
                var armor = AvatarArmorWire.Capture(Rig.Equipment);
                if (!AvatarArmor.Value.Equals(armor)) AvatarArmor.Value = armor;
            }
            else if (!IsOwner && view != null)
            {
                var armor = AvatarArmor.Value;
                if (!_armorPresented || _armorTarget != view || !_presentedArmor.Equals(armor))
                {
                    var shown = armor.IsValid ? armor : default;
                    view.PresentArmor(shown.Head.ToString(), shown.Chest.ToString(), shown.Legs.ToString(),
                        shown.Hands.ToString(), shown.Feet.ToString(), shown.Back.ToString());
                    _armorTarget = view; _presentedArmor = armor; _armorPresented = true;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-ember-item-model-review") >= 0)
                        Debug.Log(NetTuning.LogPrefix + "[client] armor-appearance player=" + ServerPlayerId.Value +
                            " head=" + shown.Head + " chest=" + shown.Chest + " legs=" + shown.Legs +
                            " hands=" + shown.Hands + " feet=" + shown.Feet + " back=" + shown.Back);
#endif
                }
            }
            var pose = AvatarCombat.Value;
            // Remote movement is disabled. Only observers consume replicated stance for footsteps;
            // the owner keeps its predicted controller state, including reconciliation.
            if (NetRole.IsClientOnly && !IsOwner)
            {
                var footsteps = Rig.GetComponent<Emberdelve.Feedback.WorldFootsteps>();
                if (footsteps != null) footsteps.ReplicatedCrouched = pose.IsValid && pose.Crouched;
            }
            if (NetRole.IsClientOnly)
            {
                var ranged = Rig.GetComponent<RangedCombat>();
                if (ranged != null)
                {
                    ranged.ApplyReplicatedReload(pose.IsValid ? pose.Main.ToString() : "",
                        pose.IsValid && pose.Reloading, pose.ReloadProgress, pose.ReloadDuration);
                    if (IsOwner)
                    {
                        var ammo = RangedAmmo.Value;
                        ranged.ApplyReplicatedAmmo(ammo.IsValid ? ammo.Weapon.ToString() : "", ammo.Magazine, ammo.Reserve);
                    }
                }
            }
            if (NetRole.IsClientOnly && IsOwner)
            {
                // The owner's ranged simulation is disabled: feed the same authoritative
                // charge to the first-person bow/string/hands as to observers.
                var weapon = Rig.ActiveWeapon;
                bool drawing = pose.IsValid && pose.Drawing && !pose.Guard && !pose.Reloading
                    && !pose.Swing && !pose.Recoil && !Rig.IsDead && !Rig.IsExtracted
                    && weapon != null && pose.Main.ToString() == weapon.weaponId;
                Rig.SetDrawVisual(drawing ? pose.DrawProgress : 0f);
                if (pose.IsValid) PresentOwnerCombat(pose);
            }
            if (NetRole.IsClientOnly && pose.IsValid)
                PotionDrinkPresentation.Ensure(Rig).ApplyNetwork(pose.Drinking, pose.DrinkProgress, pose.DrinkDuration);
            if (view != null && pose.IsValid) view.PresentUtility(pose.Utility.ToString());
            if (view != null && pose.IsValid && (NetRole.IsClientOnly || !pose.Bot))
                view.PresentCombat(pose.Main.ToString(),pose.Off.ToString(),pose.Pattern.ToString(),pose.Guard,pose.Swing,
                    pose.PresentationElapsed(NetworkManager.ServerTime.Time),new Emberdelve.Data.SwingTiming(pose.Windup,pose.Active,pose.Recovery,0),
                    new Emberdelve.Combat.SwingFrame(Rig.transform.position+pose.EyeOffset,pose.Aim),
                    pose.Recoil,pose.StopFraction,pose.RecoilProgress(NetworkManager.ServerTime.Time),pose.Bot);
            // Draw is server presentation state, never a client charge or ammunition claim.
            var remoteWeapons = Rig.GetComponent<RemoteWeaponPresentation>();
            if (remoteWeapons != null)
            {
                remoteWeapons.PresentCrossbowLoaded(pose.IsValid ? pose.Main.ToString() : "", pose.IsValid && pose.CrossbowLoaded);
                remoteWeapons.PresentDraw(pose.IsValid ? pose.Main.ToString() : "",
                    pose.IsValid && pose.Drawing && !Rig.IsDead && !Rig.IsExtracted, pose.DrawProgress);
            }
        }

        private void PresentOwnerCombat(in AvatarCombatWire pose)
        {
            var visual = Rig.Loadout != null ? Rig.Loadout.Visual : null;
            if (visual == null) return;
            if ((!pose.Swing && !pose.Recoil) || Rig.IsDead)
            {
                if (_ownerCombatAnimating) visual.CancelNetworkSwing();
                _ownerCombatAnimating = false;
                return;
            }
            var weapon = Rig.ActiveWeapon;
            if (weapon == null || weapon.weaponId != pose.Main.ToString() || weapon.swingPatterns == null) return;
            Emberdelve.Data.SwingPatternData pattern = null;
            foreach (var candidate in weapon.swingPatterns)
                if (candidate != null && candidate.patternId == pose.Pattern.ToString()) { pattern = candidate; break; }
            if (pattern == null) return;
            if (!_ownerCombatAnimating || _ownerPresentedSwingId != pose.SwingId)
            {
                Rig.PlaySwingVisual(weapon, pattern, new Emberdelve.Data.SwingTiming(pose.Windup,pose.Active,pose.Recovery,pose.RecoilDuration));
                _ownerPresentedSwingId = pose.SwingId;
                _ownerCombatAnimating = true;
                Debug.Log(NetTuning.LogPrefix + "[client] owner-swing-visual id=" + ServerPlayerId.Value + " swing=" + pose.SwingId + " pattern=" + pose.Pattern);
            }
            double now = NetworkManager.ServerTime.Time;
            if (pose.Recoil)
            {
                visual.ApplyNetworkRecoil(pose.StopFraction,pose.RecoilElapsed(now));
                if (_ownerPresentedRecoilId != pose.SwingId)
                {
                    _ownerPresentedRecoilId = pose.SwingId;
                    Debug.Log(NetTuning.LogPrefix + "[client] owner-recoil-visual id=" + ServerPlayerId.Value + " swing=" + pose.SwingId);
                }
            }
            else visual.SyncNetworkSwingTime(pose.PresentationElapsed(now));
        }

        private static readonly List<NetPlayerRig> SpawnedRigs = new List<NetPlayerRig>();

        /// <summary>Every spawned player object this process knows (the server: all admitted; a client: all views, owned or not).</summary>
        public static IReadOnlyList<NetPlayerRig> Spawned => SpawnedRigs;

        private PlayerRig _rig;
        private uint _floorLootSequence;

        /// <summary>The replicated class id as text ("" before the first value).</summary>
        public string ClassIdText => ServerClassId.Value.ToString();

        public NetPlayerRole Role { get; private set; }
        public PlayerRig Rig => _rig != null ? _rig : (_rig = GetComponent<PlayerRig>());
        public bool DoorStateReady => Role == NetPlayerRole.HostPlayer ||
            (Role == NetPlayerRole.Owner && GetComponent<NetInventoryClient>() != null && GetComponent<NetInventoryClient>().DoorStateReady);

        public bool RequestDoorToggle(string doorId)
        {
            if (!DungeonDoorInteractable.IsValidDoorId(doorId) || !IsSpawned) return false;
            if (ShouldUseHostDoorBridge(Role))
            {
                NetInventoryServer server = NetInventoryServer.Current;
                if (server == null) return false;
                server.RequestDoorToggle(NetworkManager.ServerClientId, NetworkObject, new FixedString128Bytes(doorId));
                return true;
            }
            if (Role == NetPlayerRole.Owner)
                return GetComponent<NetInventoryClient>() != null && GetComponent<NetInventoryClient>().RequestDoorToggle(doorId);
            return false;
        }

        public static bool ShouldUseHostDoorBridge(NetPlayerRole role) => role == NetPlayerRole.HostPlayer;

        public static NetPlayerRole ResolveRole(bool server, bool host, bool owner, bool playerObject) => server
            ? (host && owner && playerObject ? NetPlayerRole.HostPlayer : NetPlayerRole.Server)
            : (owner && playerObject ? NetPlayerRole.Owner : NetPlayerRole.Observer);

        public override void OnNetworkSpawn()
        {
            _rig = GetComponent<PlayerRig>();
            if (IsOwner && NetworkObject.IsPlayerObject && NetRole.IsClientOnly && _rig.Inventory != null) _rig.Inventory.BeginNetworkSession();

            if (IsServer)
            {
                // Server and host both ran the admission already, so the rig has its real id and party.
                _rig.FloorMovementChanged += PublishFloorMovement;
                FloorMovement.Value = CaptureFloorMovement(_rig);
                ServerPlayerId.Value = _rig.PlayerId;
                ServerPartyId.Value = _rig.PartyId;
                ServerClassId.Value = new FixedString32Bytes(ClassIdForWire(_rig));
                ServerKit.Value = new FixedString128Bytes(Emberdelve.Skills.SkillLoadoutStore.KitString(_rig.Class));
                AvatarCombat.Value = new AvatarCombatWire { InFloorTransit = _rig.IsInFloorTransit,
                    Aim = Quaternion.LookRotation(_rig.AimDirection, Vector3.up),
                    EyeOffset = _rig.AimOrigin - _rig.transform.position };
                Role = ResolveRole(true, IsHost, IsOwner, NetworkObject.IsPlayerObject);
                ApplyServerRole();
            }
            else
            {
                Role = ResolveRole(false, false, IsOwner, NetworkObject.IsPlayerObject);
                ServerPlayerId.OnValueChanged += OnServerPlayerIdChanged;
                ServerPartyId.OnValueChanged += OnServerPartyIdChanged;
                ApplyClientView();
                FloorMovement.OnValueChanged += OnFloorMovementChanged;

                // U3B3: the server's health / death (observers and owner alike), and the line the gate reads.
                Vitals.OnValueChanged += OnVitalsChanged;
                if (Vitals.Value.MaxHealth > 0f) OnVitalsChanged(default(CombatVitalsWire), Vitals.Value);
            }

            if (!SpawnedRigs.Contains(this)) SpawnedRigs.Add(this);

            // Listen-host owns a simulated server rig rather than a client inventory replica. Give that local
            // player the same corpse locator, but keep its inventory authority and interactions server-local.
            if (ShouldEnableHostCorpsePresenter(Role) && GetComponent<CorpseLootWorldPresenter>() == null)
            {
                Emberdelve.Flow.RunDirector director = FindFirstObjectByType<Emberdelve.Flow.RunDirector>(FindObjectsInactive.Include);
                gameObject.AddComponent<CorpseLootWorldPresenter>().ConfigureHost(this, _rig,
                    director != null ? director.InteractionReach : Emberdelve.Interaction.InteractionRules.DefaultReach);
            }

            // U3A4a: the owning client of a remote-simulated player streams its input (host's own player moves with its own Update).
            // U3A4b: and predicts it locally (the prediction exists first so the sender can hand it every frame).
            if (Role == NetPlayerRole.Owner)
            {
                if (GetComponent<NetInventoryClient>() == null)
                    gameObject.AddComponent<NetInventoryClient>().Configure(this);
                if (GetComponent<NetExtractionClient>() == null)
                    gameObject.AddComponent<NetExtractionClient>().Configure(this);
                // U3B2: the server's skill picture for this player, and the sender of its skill key presses.
                if (GetComponent<NetOwnerSkillView>() == null)
                {
                    gameObject.AddComponent<NetOwnerSkillView>().Configure(this);
                    SkillState.OnValueChanged += OnSkillStateChanged;
                    if (SkillState.Value.MaxMana > 0f) OnSkillStateChanged(default(SkillStateWire), SkillState.Value);
                }
                if (GetComponent<NetFinalExitView>() == null) gameObject.AddComponent<NetFinalExitView>().Configure(this);
                if (GetComponent<NetRoyaleZoneBoundary>() == null) gameObject.AddComponent<NetRoyaleZoneBoundary>().Configure(this);
                if (GetComponent<NetDescentPortalViews>() == null) gameObject.AddComponent<NetDescentPortalViews>().Configure(this);
                DescentPortals.OnValueChanged += OnDescentPortalsChanged;
                OnDescentPortalsChanged(default, DescentPortals.Value);
                RaidClock.OnValueChanged += OnRaidClockChanged;
                OnRaidClockChanged(default, RaidClock.Value);
                ImpactGuardRemaining.OnValueChanged += OnImpactGuardChanged;
                OnImpactGuardChanged(0f, ImpactGuardRemaining.Value);
                RangedAmmo.OnValueChanged += OnRangedAmmoChanged;
                OnRangedAmmoChanged(default, RangedAmmo.Value);

                if (GetComponent<NetSkillSender>() == null)
                    gameObject.AddComponent<NetSkillSender>().Configure(this, NetBootstrap.LaunchArgs);

                // U3B3: the owner's combat intents (real input, or a script in proof runs) and the server's answers.
                if (GetComponent<NetOwnerCombatView>() == null)
                    gameObject.AddComponent<NetOwnerCombatView>().Configure(this);
                if (GetComponent<NetCombatSender>() == null)
                    gameObject.AddComponent<NetCombatSender>().Configure(this, NetBootstrap.LaunchArgs);

                if (GetComponent<NetOwnerPrediction>() == null)
                {
                    gameObject.AddComponent<NetOwnerPrediction>().Configure(this);
                    MoveParams.OnValueChanged += OnMoveParamsChanged;
                }

                if (GetComponent<NetInputSender>() == null)
                    gameObject.AddComponent<NetInputSender>().Configure(this, NetBootstrap.LaunchArgs);
            }
            else if (Role == NetPlayerRole.HostPlayer && GetComponent<NetExtractionClient>() == null)
            {
                // Host extraction uses the same server lease directly in-process.
                gameObject.AddComponent<NetExtractionClient>().Configure(this);
            }

            if (NetOwnerRunResultView.IsOwnerView(Role) && GetComponent<NetOwnerRunResultView>() == null)
                gameObject.AddComponent<NetOwnerRunResultView>().Configure(this);
            if (Role == NetPlayerRole.Owner)
            {
                PlayerView.BindNetworkLocalPlayer(_rig.PlayerId);
                // The scene's Game_Master (including its offline result screen) is disabled on clients.
                // Bind presentation to the local network actor; observers must not create result UI.
                if (GetComponent<Emberdelve.UI.ResultScreenController>() == null)
                    gameObject.AddComponent<Emberdelve.UI.ResultScreenController>();
            }

            Debug.Log(Emberdelve.Net.Rules.NetTuning.LogPrefix + "[" + (IsServer ? (IsOwner ? "host" : "server") : "client") +
                      "] player object spawned role=" + Role + " netId=" + NetworkObjectId + " owner=" + OwnerClientId +
                      " playerId=" + _rig.PlayerId + " party=" + _rig.PartyId + " class=" + ClassIdText);
            if (!IsServer) OnFloorMovementChanged(default, FloorMovement.Value);
        }

        private static FloorMovementWire CaptureFloorMovement(PlayerRig rig) => new FloorMovementWire
        {
            Generation = rig.FloorMovementGeneration, InTransit = rig.IsInFloorTransit,
            SpectatingParty = rig.IsPortalSpectating, RoyaleFloor = (byte)rig.RoyaleFloor,
            Position = rig.transform.position, Rotation = rig.transform.rotation
        };

        private void PublishFloorMovement(PlayerRig rig)
        {
            if (!IsServer || !IsSpawned) return;
            NetMovementServer.Current?.OnFloorMovementChanged(rig);
            if (!rig.IsInFloorTransit)
            {
                rig.Look?.ApplyAimPose(rig.transform.eulerAngles.y, 0f);
                GetComponent<Unity.Netcode.Components.NetworkTransform>()?.Teleport(
                    rig.transform.position, rig.transform.rotation, rig.transform.localScale);
            }
            FloorMovement.Value = CaptureFloorMovement(rig);
        }

        private void OnFloorMovementChanged(FloorMovementWire previous, FloorMovementWire current)
        {
            if (!NetRole.IsClientOnly || _rig == null || !current.IsValid
                || (_floorMovementApplied && current.Generation <= _appliedFloorGeneration)) return;
            if (current.Generation == 0) return; // Ordinary initial spawn has no transition history to discard.
            if (!_rig.ApplyReplicatedFloorMovement(current.Generation, current.InTransit, current.Position, current.Rotation,
                current.RoyaleFloor, current.SpectatingParty)) return;
            _appliedFloorGeneration = current.Generation; _floorMovementApplied = true;
            GetComponent<NetOwnerPrediction>()?.ResetForFloorMovement();
            GetComponent<NetInputSender>()?.ResetForFloorMovement(current.Rotation);
            OnRaidClockChanged(default, RaidClock.Value);
            _rig.Look?.ApplyAimPose(current.Rotation.eulerAngles.y, 0f);
            Debug.Log(NetTuning.LogPrefix + "[client] floor movement applied id=" + ServerPlayerId.Value
                + " generation=" + current.Generation + " transit=" + current.InTransit + " pos=" + current.Position);
        }

        public override void OnNetworkDespawn()
        {
            _armorTarget = null; _armorPresented = false; _presentedArmor = default;
            if (_rig != null) _rig.FloorMovementChanged -= PublishFloorMovement;
            FloorMovement.OnValueChanged -= OnFloorMovementChanged;
            int despawningPlayerId = ServerPlayerId.Value;
            if (Role == NetPlayerRole.Owner) PlayerView.ClearNetworkLocalPlayer(despawningPlayerId);
            ServerPlayerId.OnValueChanged -= OnServerPlayerIdChanged;
            ServerPartyId.OnValueChanged -= OnServerPartyIdChanged;
            SpawnedRigs.Remove(this);

            MoveParams.OnValueChanged -= OnMoveParamsChanged;
            SkillState.OnValueChanged -= OnSkillStateChanged;
            RaidClock.OnValueChanged -= OnRaidClockChanged;
            DescentPortals.OnValueChanged -= OnDescentPortalsChanged;
            GetComponent<NetDescentPortalViews>()?.Apply(default);
            GetComponent<NetFinalExitView>()?.Hide();
            ImpactGuardRemaining.OnValueChanged -= OnImpactGuardChanged;
            if (!IsServer && _rig != null) _rig.ApplyReplicatedImpactGuard(0f);
            RangedAmmo.OnValueChanged -= OnRangedAmmoChanged;
            if (!IsServer && _rig != null)
            {
                var ranged = _rig.GetComponent<RangedCombat>();
                if (ranged != null)
                { ranged.ApplyReplicatedAmmo("", 0, 0); ranged.ApplyReplicatedReload("", false, 0f, 0f); }
            }
            Vitals.OnValueChanged -= OnVitalsChanged;

            NetCombatSender combatSender = GetComponent<NetCombatSender>();
            if (combatSender != null)
            {
                combatSender.LogFinal();
                Destroy(combatSender);
            }

            NetOwnerCombatView combatView = GetComponent<NetOwnerCombatView>();
            if (combatView != null) Destroy(combatView);
            if (Role == NetPlayerRole.Owner)
            {
                var inventoryClient = GetComponent<NetInventoryClient>();
                if (inventoryClient != null) Destroy(inventoryClient);
                NetEnemyViews.Clear();
                NetCombatFx.ClearBloodHistory();
            }

            NetSkillSender skillSender = GetComponent<NetSkillSender>();
            if (skillSender != null)
            {
                skillSender.LogFinal();
                Destroy(skillSender);
            }

            NetOwnerSkillView skillView = GetComponent<NetOwnerSkillView>();
            if (skillView != null) Destroy(skillView);

            NetOwnerRunResultView resultView = GetComponent<NetOwnerRunResultView>();
            if (resultView != null) Destroy(resultView);

            NetInputSender sender = GetComponent<NetInputSender>();
            if (sender != null) Destroy(sender);

            NetOwnerPrediction prediction = GetComponent<NetOwnerPrediction>();
            if (prediction != null)
            {
                prediction.LogFinal();
                Destroy(prediction);
            }

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

        /// <summary>
        /// The movement stream (client to server, once per network tick, unreliable: the batch carries the last three frames so a
        /// lost packet is covered by the next). the default InvokePermission (Owner) stays: only the owning connection may call it on this object,
        /// and the server handler re-derives the sender from the connection anyway. No identity parameter exists.
        /// </summary>
        [Rpc(SendTo.Server, Delivery = RpcDelivery.Unreliable)]
        public void SubmitMoveRpc(MoveBatchWire batch, RpcParams rpcParams = default)
        {
            NetMovementServer movement = NetMovementServer.Current;
            if (movement == null) return;
            movement.Receive(this, rpcParams.Receive.SenderClientId, in batch);
        }

        /// <summary>
        /// U3A4b, server to owner: the state the server reached after the newest input frame it processed (see MoveAckWire). Unreliable
        /// (a lost Ack is replaced by the next), owner only, no identity parameter. NEW NGO usage (UNCERTAIN): SendTo.Owner together
        /// with Delivery = Unreliable. Runs on the owning client; anywhere else it does nothing.
        /// </summary>
        [Rpc(SendTo.Owner, Delivery = RpcDelivery.Unreliable)]
        public void AckRpc(MoveAckWire ack)
        {
            NetOwnerPrediction prediction = GetComponent<NetOwnerPrediction>();
            if (prediction != null) prediction.OnAck(in ack);
        }

        private void OnMoveParamsChanged(MoveParamsWire previous, MoveParamsWire current)
        {
            NetOwnerPrediction prediction = GetComponent<NetOwnerPrediction>();
            if (prediction == null) return;
            MoveParamsData data = current.ToData();
            if (data.IsValid) prediction.OnParamsChanged(in data);
        }

        /// <summary>
        /// U3B2, client to server, RELIABLE and ordered (NGO's default delivery): "I pressed the key of active skill <paramref name="slot"/>".
        /// <paramref name="sequence"/> is the connection's one shared counter (1, 2, 3 ... across both slots). The call carries no
        /// identity, aim, outcome or time: the sender is the connection that delivered it, the object must be that connection's own
        /// player, the clock is the server's and the aim is the server rig's. InvokePermission is Everyone ON PURPOSE: a modified client
        /// can call this on anybody's object, and NetSkillServer.Receive (identity first, then the gate) is the one place that rejects,
        /// counts and, past a threshold, disconnects it. Compiled in every build.
        /// </summary>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void SkillIntentRpc(int slot, uint sequence, RpcParams rpcParams = default)
        {
            NetSkillServer skills = NetSkillServer.Current;
            if (skills == null) return;
            skills.Receive(this, rpcParams.Receive.SenderClientId, slot, sequence);
        }

        /// <summary>
        /// U3B2, server to owner, reliable: the server's answer to one request that passed the wire gate (see SkillResultWire). Runs on
        /// the owning client; anywhere else it does nothing. The owner records it; nothing here changes gameplay.
        /// </summary>
        [Rpc(SendTo.Owner)]
        public void SkillResultRpc(SkillResultWire result)
        {
            NetOwnerSkillView view = GetComponent<NetOwnerSkillView>();
            if (view != null) view.OnResult(result);
        }

        /// <summary>
        /// U3B2, server to every client but the owner, reliable: this player's object just cast a skill (see SkillVisualWire). Presentation
        /// only; a dedicated server ignores it.
        /// </summary>
        [Rpc(SendTo.NotOwner)]
        public void SkillVisualRpc(SkillVisualWire visual)
        {
            if (NetRole.Current == NetRoleKind.DedicatedServer) return;
            NetSkillCastFlash.Show(this, visual);
        }

        private void OnSkillStateChanged(SkillStateWire previous, SkillStateWire current)
        {
            NetOwnerSkillView view = GetComponent<NetOwnerSkillView>();
            if (view != null) view.OnState(current);
        }

        private void OnDescentPortalsChanged(DescentPortalWire before, DescentPortalWire current)
            => GetComponent<NetDescentPortalViews>()?.Apply(current);
        public void SetDescentPortalsFromServer(DescentPortalWire state)
        { if (IsServer && IsSpawned && state.IsValid && !DescentPortals.Value.Equals(state)) DescentPortals.Value = state; }
        public bool DescentReady(int floor) => IsSpawned && Rig != null && Rig.IsInPlay && !Rig.IsDead
            && (Role == NetPlayerRole.HostPlayer || (Role == NetPlayerRole.Owner && DescentPortals.Value.IsValid
                && DescentPortals.Value.Floor == floor && DescentPortals.Value.Generation == Rig.FloorMovementGeneration));
        public bool RequestDescent(int floor, int group, int slot, DescentPortalIntent intent = DescentPortalIntent.Complete)
        {
            if (!IsSpawned || floor < 1 || floor > 2 || group < 0 || group > 2 || slot < 0 || slot > 2) return false;
            if (intent != DescentPortalIntent.Cancel && !DescentReady(floor)) return false;
            if (Role == NetPlayerRole.HostPlayer) return NetInventoryServer.Current != null
                && NetInventoryServer.Current.RequestDescent(NetworkManager.ServerClientId, NetworkObject, floor, group, slot, intent);
            if (Role != NetPlayerRole.Owner) return false;
            int size = DescentPortals.Value.Count / 3;
            if (intent != DescentPortalIntent.Cancel && (slot >= size || !DescentPortals.Value.IsAvailable(group * size + slot))) return false;
            DescentIntentRpc((byte)floor, (byte)group, (byte)slot, intent); return true;
        }
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void DescentIntentRpc(byte floor, byte group, byte slot, DescentPortalIntent intent = DescentPortalIntent.Complete, RpcParams rpcParams = default)
        {
            bool accepted = NetInventoryServer.Current != null && NetInventoryServer.Current.RequestDescent(
                rpcParams.Receive.SenderClientId, NetworkObject, floor, group, slot, intent);
            if (intent == DescentPortalIntent.Complete && rpcParams.Receive.SenderClientId == OwnerClientId) DescentResultRpc(accepted);
        }
        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Server)]
        public void DescentResultRpc(bool accepted)
        { if (!accepted && Role == NetPlayerRole.Owner) GameEvents.RaiseToast(Rig.PlayerId, "Portal is unavailable or out of reach"); }

        private void OnRaidClockChanged(RaidClockWire previous, RaidClockWire current)
        {
            NetOwnerSkillView view = GetComponent<NetOwnerSkillView>();
            if (view == null) return;
            if (current.Floor > 0 && (_rig == null || current.MovementGeneration != _rig.FloorMovementGeneration
                || (current.Enabled && _rig.IsInFloorTransit))) { view.OnRaidClock(default); return; }
            view.OnRaidClock(current);
        }

        private void OnImpactGuardChanged(float previous, float current)
        {
            if (!IsServer && IsOwner && _rig != null) _rig.ApplyReplicatedImpactGuard(current);
        }

        private void OnRangedAmmoChanged(RangedAmmoWire previous, RangedAmmoWire current)
        {
            if (!IsOwner || IsServer || !current.IsValid) return;
            Debug.Log(NetTuning.LogPrefix + "[client] ranged-ammo weapon=" + current.Weapon
                + " magazine=" + current.Magazine + " reserve=" + current.Reserve);
        }

        /// <summary>Writes the newest server clock sample to this player's owner-only replicated value.</summary>
        public void SetRaidClockFromServer(RaidClockWire sample)
        {
            if (!IsServer || !IsSpawned || !sample.IsValid) return;
            if (!RaidClock.Value.Equals(sample)) RaidClock.Value = sample;
        }

        // ---- U3B3: weapon combat --------------------------------------------------------------------------------

        /// <summary>
        /// U3B3, client to server, RELIABLE and ordered: "I did combat action <paramref name="kind"/>" (CombatIntentKind: attack press, guard
        /// down / up, draw start / release, weapon-set swap). <paramref name="sequence"/> is the connection's one shared counter. The call
        /// carries no identity, aim, charge, target, damage or time: the sender is the connection that delivered it, the object must be that
        /// connection's own player, the clock, the charge and the aim are the server's. InvokePermission is Everyone ON PURPOSE (a modified
        /// client can call this on anybody's object; NetCombatServer.Receive is the one place that rejects, counts and, through the
        /// shared skill / combat ledger, disconnects). Compiled in every build.
        /// </summary>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void CombatIntentRpc(int kind, uint sequence, RpcParams rpcParams = default)
        {
            NetCombatServer combat = NetCombatServer.Current;
            if (combat == null) return;
            combat.Receive(this, rpcParams.Receive.SenderClientId, kind, sequence);
        }

        /// <summary>Reliable inventory intent; the server checks sender ownership before executing it.</summary>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void InventoryIntentRpc(InventoryIntentWire intent, RpcParams rpcParams = default)
        {
            var server = NetInventoryServer.Current;
            if (server == null || !server.Receive(rpcParams.Receive.SenderClientId, NetworkObject, intent, out var result)) return;
            InventoryResultRpc(new InventoryResultWire { Sequence = intent.Sequence, Accepted = result.Accepted,
                Changed = result.Changed, Revision = result.Revision });
        }

        /// <summary>Owner-only acknowledgement; no local inventory state is applied by this message.</summary>
        public event System.Action<InventoryResultWire> InventoryResultReceived;
        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Server)]
        public void InventoryResultRpc(InventoryResultWire result) => InventoryResultReceived?.Invoke(result);

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void InventorySnapshotRequestRpc(RpcParams rpcParams = default)
        {
            NetInventoryServer.Current?.RequestSnapshot(rpcParams.Receive.SenderClientId, NetworkObject);
        }

        public bool RequestFloorLootPickup(ulong pickupId)
        {
            if (pickupId == 0 || (Role != NetPlayerRole.Owner && Role != NetPlayerRole.HostPlayer)
                || _floorLootSequence >= FloorLootIntentGate.MaxSequence) return false;
            uint sequence = ++_floorLootSequence;
            if (Role == NetPlayerRole.HostPlayer)
                return NetInventoryServer.Current != null && NetInventoryServer.Current.RequestFloorLootPickup(
                    NetworkManager.ServerClientId, NetworkObject, pickupId, sequence);
            if (Role != NetPlayerRole.Owner) return false;
            FloorLootPickupIntentRpc(pickupId, sequence);
            return true;
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void FloorLootPickupIntentRpc(ulong pickupId, uint sequence, RpcParams rpcParams = default)
        {
            bool accepted = NetInventoryServer.Current != null && NetInventoryServer.Current.RequestFloorLootPickup(
                rpcParams.Receive.SenderClientId, NetworkObject, pickupId, sequence);
            FloorLootPickupResultRpc(sequence, accepted, new FixedString128Bytes(accepted ? string.Empty : "Pickup request refused"));
        }

        private uint _lastAudibleFloorPickup;

        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Server)]
        public void FloorLootPickupResultRpc(uint sequence, bool accepted, FixedString128Bytes reason)
        {
            if (Role != NetPlayerRole.Owner) return;
            if (accepted && NetRole.IsClientOnly && sequence != 0 && sequence > _lastAudibleFloorPickup)
            {
                _lastAudibleFloorPickup = sequence;
                Emberdelve.Feedback.WorldAudio.PlayLocal(Emberdelve.Feedback.WorldSound.Pickup, .6f);
            }
            if (!accepted) GameEvents.RaiseToast(_rig != null ? _rig.PlayerId : GameEvents.NoPlayer, reason.ToString());
        }

        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Server)]
        public void FloorLootStateRpc(FloorLootWire state)
        {
            if (Role != NetPlayerRole.Owner) return;
            GetComponent<NetInventoryClient>()?.OnFloorLootState(state);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void ChestOpenIntentRpc(FixedString128Bytes chestId, RpcParams rpcParams = default)
        {
            NetInventoryServer.Current?.RequestChestOpen(rpcParams.Receive.SenderClientId, NetworkObject, chestId);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void DoorToggleIntentRpc(FixedString128Bytes doorId, RpcParams rpcParams = default)
        {
            NetInventoryServer.Current?.RequestDoorToggle(rpcParams.Receive.SenderClientId, NetworkObject, doorId);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void DoorSnapshotRequestRpc(RpcParams rpcParams = default)
        {
            NetInventoryServer.Current?.RequestDoorSnapshot(rpcParams.Receive.SenderClientId, NetworkObject);
        }

        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Server)]
        public void DoorStateRpc(FixedString128Bytes doorId, bool isOpen, uint revision, bool snapshotStart, bool snapshotEnd)
        {
            if (!NetRole.IsClientOnly || !IsOwner) return;
            GetComponent<NetInventoryClient>()?.OnDoorState(doorId.ToString(), isOpen, revision, snapshotStart, snapshotEnd);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void ChestHoldBeginRpc(FixedString128Bytes chestId, uint sequence, RpcParams rpcParams = default)
        {
            NetInventoryServer.Current?.BeginChestHold(rpcParams.Receive.SenderClientId, NetworkObject, chestId, sequence);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void ChestHoldHeartbeatRpc(FixedString128Bytes chestId, uint sequence, RpcParams rpcParams = default)
        {
            NetInventoryServer.Current?.HeartbeatChestHold(rpcParams.Receive.SenderClientId, NetworkObject, chestId, sequence);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void ChestHoldCancelRpc(FixedString128Bytes chestId, uint sequence, RpcParams rpcParams = default)
        {
            NetInventoryServer.Current?.CancelChestHold(rpcParams.Receive.SenderClientId, NetworkObject, chestId, sequence);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void ChestHoldCompleteRpc(FixedString128Bytes chestId, uint sequence, RpcParams rpcParams = default)
        {
            NetInventoryServer.Current?.CompleteChestHold(rpcParams.Receive.SenderClientId, NetworkObject, chestId, sequence);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void CorpseStateRequestRpc(ulong corpseId, uint sequence, RpcParams rpcParams = default)
        {
            NetInventoryServer.Current?.RequestCorpseState(rpcParams.Receive.SenderClientId, NetworkObject, corpseId, sequence);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void CorpseTransferIntentRpc(ulong corpseId, ulong entryId, int amount,
            ulong corpseRevision, ulong bagRevision, uint sequence, int destinationX = -1, int destinationY = -1, RpcParams rpcParams = default)
        {
            NetInventoryServer.Current?.RequestCorpseTransfer(rpcParams.Receive.SenderClientId, NetworkObject,
                corpseId, entryId, amount, corpseRevision, bagRevision, sequence, destinationX, destinationY);
        }

        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Server)]
        public void OpenLootContainerRpc(ulong containerId)
        {
            if (!NetRole.IsClientOnly || !IsOwner) return;
            GetComponent<NetInventoryClient>()?.OnLootOpened(containerId);
        }

        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Server)]
        public void CorpseContainerStateRpc(CorpseContainerWire state)
        {
            if (!NetRole.IsClientOnly || !IsOwner) return;
            GetComponent<NetInventoryClient>()?.OnCorpseState(state);
        }

        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Server)]
        public void CorpsePresenceRpc(ulong corpseId, Vector3 position, ulong revision, bool removed)
        {
            if (!NetRole.IsClientOnly || !IsOwner) return;
            GetComponent<NetInventoryClient>()?.OnCorpsePresence(corpseId, position, revision, removed);
        }

        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Server)]
        public void CorpseTransferResultRpc(ulong corpseId, uint sequence, bool accepted,
            ulong corpseRevision, ulong bagRevision, FixedString128Bytes reason)
        {
            if (!NetRole.IsClientOnly || !IsOwner) return;
            GetComponent<NetInventoryClient>()?.OnCorpseResult(corpseId, sequence, accepted,
                corpseRevision, bagRevision, reason.ToString());
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void ExtractionHoldBeginRpc(uint sequence, RpcParams rpcParams = default)
        {
            NetExtractionServer.Current?.BeginHold(rpcParams.Receive.SenderClientId, NetworkObject, sequence);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void ExtractionHoldHeartbeatRpc(uint sequence, RpcParams rpcParams = default)
        {
            NetExtractionServer.Current?.Heartbeat(rpcParams.Receive.SenderClientId, NetworkObject, sequence);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void ExtractionHoldCancelRpc(uint sequence, RpcParams rpcParams = default)
        {
            NetExtractionServer.Current?.CancelHold(rpcParams.Receive.SenderClientId, NetworkObject, sequence);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void ExtractionHoldCompleteRpc(uint sequence, RpcParams rpcParams = default)
        {
            NetExtractionServer.Current?.CompleteHold(rpcParams.Receive.SenderClientId, NetworkObject, sequence);
        }

        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Server)]
        public void ExtractionHoldResultRpc(uint sequence, bool accepted, FixedString128Bytes reason)
        {
            if (!NetRole.IsClientOnly || !IsOwner) return;
            GetComponent<NetExtractionClient>()?.OnResult(sequence, accepted, reason.ToString());
        }

        /// <summary>U4B: the exact server-authored terminal result, delivered to this player's owner only.</summary>
        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Server)]
        public void RunResultRpc(RunResultWire result)
        {
            if (!NetRole.IsClientOnly || !IsOwner) return;
            GetComponent<NetOwnerRunResultView>()?.ReceiveServerResult(result);
        }

        /// <summary>Server-authored chest presentation state, delivered only to this player's owner client.</summary>
        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Server)]
        public void ChestStateRpc(FixedString128Bytes chestId, bool rolled, bool opened, int remainingUnits, bool snapshotStart, bool snapshotEnd)
        {
            if (!NetRole.IsClientOnly || !IsOwner) return;
            GetComponent<NetInventoryClient>()?.OnChestState(chestId.ToString(), rolled, opened, remainingUnits, snapshotStart, snapshotEnd);
        }

        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Server)]
        public void ChestResultRpc(bool accepted, FixedString128Bytes chestId, uint sequence, FixedString128Bytes reason)
        {
            if (!NetRole.IsClientOnly || !IsOwner) return;
            GetComponent<NetInventoryClient>()?.OnChestResult(accepted, chestId.ToString(), sequence, reason.ToString());
        }

        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Server)]
        public void InventoryStateRpc(NetworkLoadoutWire state)
        {
            if (!NetRole.IsClientOnly || !IsOwner) return;
            var client = GetComponent<NetInventoryClient>();
            if (client != null) client.OnSnapshot(state);
        }

        /// <summary>U3B3, server to owner, reliable: the server's answer to one combat request that passed the wire gate (see CombatResultWire).</summary>
        [Rpc(SendTo.Owner)]
        public void CombatResultRpc(CombatResultWire result)
        {
            NetOwnerCombatView view = GetComponent<NetOwnerCombatView>();
            if (view != null) view.OnResult(result);
        }

        /// <summary>U3B3, server to clients, reliable: a melee swing began on this player's object (presentation only; hits arrive as CombatHitRpc).</summary>
        [Rpc(SendTo.ClientsAndHost)]
        public void CombatSwingRpc(CombatSwingWire swing)
        {
            if (!NetRole.IsClientOnly) return;
            NetCombatFx.OnSwing(this, in swing);
        }

        /// <summary>U3B3, server to clients, reliable: a projectile was launched by this player's object (the parameters the D2 integrator needs).</summary>
        [Rpc(SendTo.ClientsAndHost)]
        public void CombatShotRpc(CombatShotWire shot)
        {
            if (!NetRole.IsClientOnly) return;
            NetCombatFx.OnShot(this, in shot);
        }

        /// <summary>U3B3, server to clients, reliable: that projectile's flight ended where the server says.</summary>
        [Rpc(SendTo.ClientsAndHost)]
        public void CombatFlightEndRpc(CombatFlightEndWire end)
        {
            if (!NetRole.IsClientOnly) return;
            NetCombatFx.OnFlightEnd(this, in end);
        }

        /// <summary>U3B3, server to clients, reliable: a hit the server resolved (feedback only: health arrives through <see cref="Vitals"/> / enemy vitals).</summary>
        [Rpc(SendTo.ClientsAndHost)]
        public void CombatHitRpc(CombatHitWire hit)
        {
            if (!NetRole.IsClientOnly) return;
            NetCombatFx.OnHit(this, in hit);
        }

        /// <summary>U3B3, server to the owner, unreliable: where one enemy is (a snapshot; the client draws a proxy, it simulates nothing).</summary>
        [Rpc(SendTo.Owner, Delivery = RpcDelivery.Unreliable)]
        public void EnemyPoseRpc(EnemyPoseWire pose)
        {
            if (!NetRole.IsClientOnly) return;
            NetEnemyViews.OnPose(in pose);
        }

        /// <summary>U3B3, server to the owner, reliable: one enemy's health and death flag.</summary>
        [Rpc(SendTo.Owner)]
        public void EnemyVitalsRpc(EnemyVitalsWire vitals)
        {
            if (!NetRole.IsClientOnly) return;
            NetEnemyViews.OnVitals(in vitals);
        }

        /// <summary>Server-authored, versioned visual identity and action edges for one enemy.</summary>
        [Rpc(SendTo.Owner)]
        public void EnemyPresentationRpc(EnemyPresentationWire presentation)
        {
            if (!NetRole.IsClientOnly) return;
            NetEnemyViews.OnPresentation(in presentation);
        }

        private void OnVitalsChanged(CombatVitalsWire previous, CombatVitalsWire current)
        {
            if (_rig == null || _rig.Health == null) return;
            bool applied = _rig.Health.ApplyReplicatedState(current.Health, current.MaxHealth, current.Dead);
            if (applied && current.Extracted && !_rig.IsExtracted) _rig.MarkExtracted();
            Debug.Log(Emberdelve.Net.Rules.NetTuning.LogPrefix + "[client] vitals id=" + ServerPlayerId.Value +
                      " health=" + current.Health.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                      " max=" + current.MaxHealth.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                      " dead=" + current.Dead + " owned=" + IsOwner + " applied=" + applied);
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        /// <summary>
        /// DEVELOPMENT-ONLY (U3B2, P11): the one rule both probes use to pick the object they attack: another player's object, the one
        /// with the highest server player id that this client does not own. Deterministic with any number of players.
        /// </summary>
        internal static NetPlayerRig FindProbeVictim()
        {
            NetPlayerRig best = null;
            for (int i = 0; i < SpawnedRigs.Count; i++)
            {
                NetPlayerRig rig = SpawnedRigs[i];
                if (rig == null || !rig.IsSpawned || rig.IsOwner) continue;
                if (best == null || rig.ServerPlayerId.Value > best.ServerPlayerId.Value) best = rig;
            }

            return best;
        }

        /// <summary>
        /// DEVELOPMENT-ONLY forged-owner movement probe: the same payload and the same server path as <see cref="SubmitMoveRpc"/>,
        /// but InvokePermission is Everyone, so a client that does NOT own this object can still deliver it. The server compares the
        /// connection that delivered it with the object's owner and rejects, counts and logs the forgery.
        /// </summary>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone, Delivery = RpcDelivery.Unreliable)]
        public void ForgeMoveTestRpc(MoveBatchWire batch, RpcParams rpcParams = default)
        {
            NetMovementServer movement = NetMovementServer.Current;
            if (movement == null) return;
            movement.Receive(this, rpcParams.Receive.SenderClientId, in batch);
        }

        /// <summary>Test state a legitimate owner call writes (and a forged call must never touch). Server side only.</summary>
        public int TestProbe { get; private set; }

        /// <summary>Server side: forged owner calls rejected so far in this process.</summary>
        public static int ForgeRejectCount { get; private set; }

        /// <summary>
        /// DEVELOPMENT-ONLY forged-owner probe. InvokePermission is deliberately Everyone so a client that is NOT this object's owner can
        /// still deliver the call; the handler therefore has to do the ownership check itself, from the connection that carried the
        /// message (RpcParams.Receive.SenderClientId) and never from anything in the parameters. A call from another connection is
        /// counted and ignored; the owner's own call writes <see cref="TestProbe"/> so the gate can tell the check discriminates.
        /// The parameter is a free-form number, not an identity: no RPC in this project carries a player or combat id.
        /// </summary>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
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
            StripPresentation();

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
            var director = FindFirstObjectByType<Emberdelve.Flow.RunDirector>(FindObjectsInactive.Include);
            if (director != null && director.TryResolveClass(ClassIdText, out var data))
            {
                // The server's kit for this player (their chosen skills), so icons, passives and the sidebar match what the server runs.
                data = Emberdelve.Skills.SkillLoadoutStore.ApplyKit(data, ServerKit.Value.ToString());
                _rig.InitializeReplicatedClass(data, director.Config);
            }

            if (_rig.Movement != null) _rig.Movement.ExternalDriven = true;
            _rig.SetInputEnabled(false);

            CharacterController controller = GetComponent<CharacterController>();
            if (controller != null) controller.enabled = false;

            DisableBehaviour<PlayerLook>();
            DisableBehaviour<MeleeCombat>();
            DisableBehaviour<RangedCombat>();
            if (ShouldEnableOwnerInteractor(Role)) EnableBehaviour<Interactor>();
            else DisableBehaviour<Interactor>();
            if (Role == NetPlayerRole.Owner) EnableBehaviour<HotbarController>();
            else DisableBehaviour<HotbarController>();

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

        private void EnableBehaviour<T>() where T : Behaviour
        {
            T component = GetComponent<T>();
            if (component != null) component.enabled = true;
        }

        public static bool ShouldEnableOwnerInteractor(NetPlayerRole role) => role == NetPlayerRole.Owner;
        public static bool ShouldEnableHostCorpsePresenter(NetPlayerRole role) => role == NetPlayerRole.HostPlayer;

        private void ApplyReplicatedIdentity()
        {
            _rig.SetNetworkPlayerId(ServerPlayerId.Value);
            _rig.AssignParty(ServerPartyId.Value, Role == NetPlayerRole.Owner);
            if (Role == NetPlayerRole.Owner) PlayerView.BindNetworkLocalPlayer(_rig.PlayerId);
        }

        private void OnServerPlayerIdChanged(int previous, int current) => ApplyReplicatedIdentity();
        private void OnServerPartyIdChanged(int previous, int current) => ApplyReplicatedIdentity();
    }
}
