using System.Collections.Generic;
using UnityEngine;
using Emberdelve.BladeDefense;
using Emberdelve.Core;
using Emberdelve.Combat;
using Emberdelve.Data;
using Emberdelve.InputLayer;
using Emberdelve.Items;
using Emberdelve.Movement;
using Emberdelve.Skills;
using Emberdelve.Stats;

namespace Emberdelve.Player
{
    /// <summary>
    /// Composition root for the local player. It owns identity (combat id, kind), exposes aim and
    /// guard state, and forwards class setup. Behaviour lives in the components it references.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(EmberdelveInput))]
    [RequireComponent(typeof(PlayerMovement))]
    [RequireComponent(typeof(PlayerLook))]
    [RequireComponent(typeof(HealthComponent))]
    public sealed class PlayerRig : MonoBehaviour, ICombatEntity, IDefenseProvider, IStatusTarget, IDamageOutgoingProvider, IDamageFlatWeaponBonusProvider, IArmorRatingProvider, IActiveWeaponProvider, IWeaponChangeSource, ISkillCasterState, IAmmoSource, IAimPoseProvider, IHitShapeProvider, IGuardBreakDefender, IGuardStateSource, IMoveFlowSource, IPlayerOwned, IInteractionActor, IRunParticipant, IPlayerCombatFeedback, IKitSeedSource, IKitView, IMovementRollback, IActionSpeedSource, IInteractionSpeedSource, ISkillCooldownSource, IRiposteKit
    {
        /// <summary>Effective capture read by the offline authority; U3B must provide the server-owned equivalent.</summary>
        public float ActionSpeedFactor => EffectiveStats.IsReady ? EffectiveStats.ActionSpeedFactor : 1f;

        /// <summary>Effective CHA rate for local chest holds; not proof of server-timed interaction.</summary>
        public float InteractionSpeedFactor => EffectiveStats.IsReady ? EffectiveStats.InteractionSpeedFactor : 1f;

        public float SkillCooldownFactor => EffectiveStats.IsReady ? EffectiveStats.SkillCooldownFactor : 1f;

        [Header("Components")]
        [SerializeField] private EmberdelveInput input;
        [SerializeField] private PlayerMovement movement;
        [SerializeField] private PlayerLook look;
        [SerializeField] private HealthComponent health;
        [SerializeField] private ManaComponent mana;
        [SerializeField] private StatusComponent status;
        [SerializeField] private SkillCaster skills;
        [SerializeField] private Inventory inventory;
        [SerializeField] private Equipment equipment;
        [SerializeField] private ClassLoadout loadout;

        private int _combatId;
        private CharacterClassData _class;
        private WeaponData _mainHand;
        private WeaponData _offhand;
        private DefenseProfile _defense;
        private bool _guardHeld;
        private bool _wired;

        private StatsComponent _attributeStats;
        private EffectiveStatsSnapshot _effectiveStatsSnapshot;
        private bool _effectiveStatsSnapshotCached;
        private int _effectiveStatsRevision;
        private int _effectiveStatsSnapshotRevision;
        private int _effectiveStatsSnapshotAttributesVersion;
        private int _effectiveStatsSnapshotModifiersVersion;
        private int _effectiveStatsSnapshotStatusVersion;
        private CharacterClassData _effectiveStatsSnapshotClass;
        private Equipment _effectiveStatsSnapshotEquipment;
        private StatusComponent _effectiveStatsSnapshotStatus;
        private CharacterStatStack _effectiveStatsSnapshotModifiers;

        [Header("Identity (R7)")]
        [Tooltip("True when THIS process drives the rig (input, camera, HUD). It selects EntityKind.LocalPlayer vs RemotePlayer and nothing else: no gameplay rule reads it.")]
        [SerializeField] private bool locallyControlled = true;

        private int _playerId;
        private int _partyId;
        private bool _extracted;
        private bool _registered;

        /// <summary>Whether this process controls the rig (input, camera, HUD binding). Not a gameplay rule.</summary>
        public bool IsLocallyControlled => locallyControlled;

        /// <summary>
        /// R7: this player's identity in the run. Every player-scoped event carries it. It defaults to the rig's combat id
        /// (unique per process) until the session layer hands out account ids (U3A); the run director never changes it after the rig is wired,
        /// so no event is ever raised under a different id than the one a viewer is bound to.
        /// </summary>
        public int PlayerId
        {
            get
            {
                if (_playerId == 0) WireRig();
                return _playerId;
            }
        }

        /// <summary>
        /// U3A2: a client process gives a replicated view the id the SERVER issued for that player (the server's combat id).
        /// Only a client-only process may do this, only once, and only with a positive id; the view is never registered as a
        /// combat entity (see <see cref="WireRig"/>), so the id is a label for events and the HUD and nothing else. Returns
        /// false (changing nothing) anywhere else: the server and offline processes derive the id themselves.
        /// </summary>
        public bool SetNetworkPlayerId(int playerId)
        {
            if (!NetRole.IsClientOnly || playerId <= 0) return false;
            WireRig();
            if (_playerId != 0 && _playerId != playerId) return false;
            _playerId = playerId;
            return true;
        }

        /// <summary>The party this player belongs to (0 until the run director assigns one). Teammates are friendly, other parties hostile.</summary>
        public int PartyId => _partyId;

        /// <summary>True after this player extracted: they left the dungeon and are nobody's target.</summary>
        public bool IsExtracted => _extracted;
        public bool IsInFloorTransit { get; private set; }
        public bool IsPortalSpectating { get; private set; }
        public int RoyaleFloor { get; private set; }
        public uint FloorMovementGeneration { get; private set; }
        public event System.Action<PlayerRig> FloorMovementChanged;
        public bool IsInPlay => !_extracted && !IsInFloorTransit;

        /// <summary>Freezes the existing authority-owned actor without changing its carried state.</summary>
        internal void SetRoyaleFloor(int floor) => RoyaleFloor = floor;

        public bool TryBeginFloorTransit(out string reason, bool spectateParty = false)
        {
            reason = null;
            if (NetRole.IsClientOnly || IsDead || _extracted || IsInFloorTransit)
            { reason = "Actor cannot begin floor transit"; return false; }
            IsInFloorTransit = true;
            IsPortalSpectating = spectateParty;
            FloorMovementGeneration++;
            GetComponent<MeleeCombat>()?.SetRemoteAttack(false);
            SetGuardHeld(false);
            _impactGuard.Reset();
            _replicatedImpactGuardUntil = 0f;
            if (movement != null) movement.CancelDash();
            SetInputEnabled(false);
            RefreshFloorTransitPresentation();
            FloorMovementChanged?.Invoke(this);
            return true;
        }

        /// <summary>The caller validates map readiness and a safe destination before releasing this actor.</summary>
        public bool TryEndFloorTransit(Vector3 position, Quaternion rotation, out string reason, int destinationFloor = 0)
        {
            reason = null;
            if (NetRole.IsClientOnly || !IsInFloorTransit || IsDead || _extracted || movement == null)
            { reason = "Actor cannot end floor transit"; return false; }
            float rotationLength = rotation.x * rotation.x + rotation.y * rotation.y +
                                   rotation.z * rotation.z + rotation.w * rotation.w;
            if (!FiniteTransitValue(position.x) || !FiniteTransitValue(position.y) || !FiniteTransitValue(position.z) ||
                !FiniteTransitValue(rotationLength) || rotationLength < 0.000001f)
            { reason = "Floor destination must be finite with a valid rotation"; return false; }
            movement.TeleportTo(position);
            transform.rotation = rotation.normalized;
            IsInFloorTransit = false;
            IsPortalSpectating = false;
            if (destinationFloor != 0) SetRoyaleFloor(destinationFloor);
            FloorMovementGeneration++;
            RefreshFloorTransitPresentation();
            SetInputEnabled(locallyControlled);
            FloorMovementChanged?.Invoke(this);
            return true;
        }

        /// <summary>Presentation of the server-owned flag only. Clients cannot use it to admit or settle a transfer.</summary>
        public void ApplyReplicatedFloorTransit(bool inTransit, bool spectateParty = false)
        {
            if (!NetRole.IsClientOnly || (IsInFloorTransit == inTransit && IsPortalSpectating == spectateParty)) return;
            IsInFloorTransit = inTransit;
            IsPortalSpectating = inTransit && spectateParty;
            SetInputEnabled(!inTransit && locallyControlled);
            RefreshFloorTransitPresentation();
        }

        public bool ApplyReplicatedFloorMovement(uint generation, bool inTransit, Vector3 position, Quaternion rotation,
            int royaleFloor = 0, bool spectateParty = false)
        {
            if (!NetRole.IsClientOnly || generation < FloorMovementGeneration || movement == null
                || !FiniteTransitValue(position.x) || !FiniteTransitValue(position.y) || !FiniteTransitValue(position.z)
                || !FiniteTransitValue(rotation.x) || !FiniteTransitValue(rotation.y) || !FiniteTransitValue(rotation.z)
                || !FiniteTransitValue(rotation.w) || Quaternion.Dot(rotation, rotation) < .000001f) return false;
            FloorMovementGeneration = generation;
            SetRoyaleFloor(royaleFloor);
            ApplyReplicatedFloorTransit(inTransit, spectateParty);
            movement.TeleportTo(position);
            transform.rotation = rotation;
            return true;
        }

        private void RefreshFloorTransitPresentation()
        {
            var view = GetComponent<FloorTransitPresentation>();
            if (view == null && IsInFloorTransit) view = gameObject.AddComponent<FloorTransitPresentation>();
            if (view != null) view.Refresh(this);
        }

        private static bool FiniteTransitValue(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        /// <summary>Called by the run director when it admits the rig to a run (and by tools that build rigs): party and control. Identity never changes the player id.</summary>
        public void AssignParty(int partyId, bool isLocallyControlled)
        {
            _partyId = Mathf.Max(0, partyId);
            locallyControlled = isLocallyControlled;
        }

        /// <summary>The run director marks a player that extracted: no input, no longer targetable or hostile.</summary>
        public void MarkExtracted()
        {
            _extracted = true;
            SetGuardHeld(false);
            SetInputEnabled(false);
        }

        public EntityKind Kind => locallyControlled ? EntityKind.LocalPlayer : EntityKind.RemotePlayer;
        public int CombatId => _combatId;
        /// <summary>
        /// R1c1: the sender identity this rig's own components put on the commands they send for it. The player sender the run
        /// director bound at admission; a rig that no run admitted (fixtures, tools) is an unowned entity and acts as the trusted
        /// host. Local process only: a server takes the sender from the connection and never from the rig.
        /// </summary>
        public CombatSenderId CombatSender => CombatSenders.TryGetOwner(_combatId, out CombatSenderId owner) ? owner : CombatSenderId.TrustedHost;
        public int TeamId => CombatTeams.TeamForParty(_partyId);

        // ---- IInteractionActor (R2) ----
        public Vector3 InteractionOrigin => transform.position + Vector3.up * 1.0f;

        // ---- IPlayerCombatFeedback (R7): the authority hands results to the entity, the entity raises its own events ----
        public void OnDamageTaken(in DamageInfo damage) { GameEvents.RaisePlayerDamaged(PlayerId, damage); }
        public void OnHitDealt(in HitFeedback feedback) { GameEvents.RaiseDamageDealt(PlayerId, feedback); }
        public Vector3 CombatCenter => transform.position + Vector3.up * 1.1f;
        public Collider HeadCollider => null;

        private HitVolume[] _hitVolumes;

        /// <summary>
        /// D1a: the player's hit volumes (head, torso, arms, legs, feet), created once under this rig. They are
        /// analytic shapes and not colliders, so the CharacterController, loot raycasts and interaction checks are
        /// unaffected. The sweep of another attacker reads them through <see cref="IHitShapeProvider"/>.
        /// </summary>
        public void CollectHitShapes(List<HitShape> shapes)
        {
            if (_hitVolumes == null) _hitVolumes = HitVolumeLayout.EnsureHumanoid(gameObject, 0.92f, 1.68f);
            int first = shapes.Count;
            HitVolumeLayout.Collect(_hitVolumes, shapes);

            // D1c3: the volumes are built for standing. While crouched they follow the lowered eye: every volume is
            // compressed vertically toward the feet by the crouch ratio (radii keep their size). Standing is an
            // exact no-op (ratio 1), so every standing melee/ranged number is untouched.
            float ratio = look != null ? look.HitVolumeHeightRatio : 1f;
            if (ratio < 0.9999f)
            {
                float baseY = transform.position.y;
                for (int i = first; i < shapes.Count; i++)
                {
                    HitShape s = shapes[i];
                    Vector3 a = s.A;
                    Vector3 b = s.B;
                    a.y = baseY + (a.y - baseY) * ratio;
                    b.y = baseY + (b.y - baseY) * ratio;
                    shapes[i] = new HitShape(s.Location, a, b, s.Radius);
                }
            }
            // Append after crouch compression: the shared guard root already follows the lowered eye.
            // The grip is an Arms hit volume, so normal melee/projectile damage and interruption apply.
            if (TryGetGuardGripHitShape(out HitShape grip)) shapes.Add(grip);
        }

        /// <summary>The authoritative aim pose the swept melee is positioned from: the camera the player looks through.</summary>
        public bool TryGetAimPose(out Vector3 eye, out float yawDegrees, out float pitchDegrees)
        {
            eye = AimOrigin;
            return SwingGeometry.TryAimAngles(AimDirection, out yawDegrees, out pitchDegrees);
        }

        /// <summary>The weapon the loadout actually has in hand; the combat authority resolves attacks against this.</summary>
        public bool IsHoldingUtility => inventory != null && inventory.HeldItem != null;
        private int _utilityInputFrame = -1;
        public WeaponData HeldMeleeWeapon
        {
            get
            {
                ItemData held = inventory != null ? inventory.HeldItem : null;
                WeaponData weapon = held != null ? held.grantedWeapon : null;
                return held != null && held.itemId == "pitch_torch" && weapon != null &&
                    weapon.weaponId == HandheldTorchVisual.WeaponId && !weapon.IsRanged ? weapon : null;
            }
        }
        public bool WeaponInputSuppressed => IsInFloorTransit || (IsHoldingUtility && HeldMeleeWeapon == null) || _utilityInputFrame == Time.frameCount;
        public void SuppressWeaponInputForFrame() { _utilityInputFrame = Time.frameCount; }
        public WeaponData ActiveWeapon => IsHoldingUtility ? HeldMeleeWeapon : loadout != null ? loadout.ActiveWeapon : null;
        public int WeaponChangeCount => loadout != null ? loadout.WeaponChangeCount : 0;

        // ---- what the combat authority reads about this caster (ISkillCasterState / IAmmoSource) ----

        IReadOnlyList<SkillData> ISkillCasterState.ActiveSkills => skills != null ? skills.ActiveSkills : null;
        ManaComponent ISkillCasterState.Mana => mana;
        int ISkillCasterState.KitEpoch => skills != null ? skills.KitEpoch : 0;
        float ISkillCasterState.AbilityEffectScale => AbilityEffectScale;

        /// <summary>
        /// The caster's own ability effect scale (Docs/Design/ATTRIBUTES.md): clamp(1 + 0.04 * CHA modifier,
        /// 0.6, 1.4) from the current effective snapshot, 1 when the rig has no ready snapshot. The only definition: the
        /// authority reads it for statuses and damage, and the skill kit reads it for local movement effects.
        /// </summary>
        public float AbilityEffectScale
        {
            get
            {
                EffectiveStatsSnapshot snapshot = EffectiveStats;
                return snapshot.IsReady ? snapshot.AbilityEffectScale : 1f;
            }
        }

        // ---- R1c2: the authority owns the kit record; this rig seeds it at defined points and only displays it ----

        private int _kitSeedSuppress;
        private KitView _authorityKit;
        private bool _hasAuthorityKit;

        /// <summary>The last authority kit record pushed into this rig (mana, ammunition, reload, epoch). False until the authority has one.</summary>
        public bool TryGetAuthorityKit(out KitView view)
        {
            view = _authorityKit;
            return _hasAuthorityKit;
        }

        /// <summary>
        /// Seeds the combat authority's kit record from this rig. Called at class apply, death, respawn, equipment revision and
        /// weapon change; a no-op while a class application is still assembling the rig, or when no authority exists (the rig then
        /// keeps its own R1b behavior).
        /// </summary>
        public void SeedKit(KitSeedReason reason)
        {
            if (_kitSeedSuppress > 0) return;
            if (!GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority)) return;
            WireRig();

            KitSeed seed = CaptureKitSeed(reason);
            authority.SeedKit(CombatSender, _combatId, in seed, out _);
        }

        public KitSeed CaptureKitSeed(KitSeedReason reason)
        {
            KitSeed seed = new KitSeed { Reason = reason, MaxMana = 1 };
            if (mana != null)
            {
                seed.Mana = mana.CurrentMana;
                seed.MaxMana = mana.MaxMana;
                seed.ManaRegen = mana.RegenPerSecond;
                seed.ManaRegenDelay = mana.RegenDelay;
            }

            // Held utilities suppress attacks, but preserve the selected weapon ammo pool.
            WeaponData weapon = loadout != null ? loadout.ActiveWeapon : null;
            seed.ActiveWeaponId = weapon != null && weapon.IsRanged ? weapon.weaponId : string.Empty;
            seed.ActiveAmmoPoolId = loadout != null ? loadout.ActiveAmmoPoolId : string.Empty;
            seed.Pools = loadout != null ? loadout.CaptureAmmoSeeds(Ranged) : new KitAmmoSeed[0];

            IReadOnlyList<SkillData> active = skills != null ? skills.ActiveSkills : null;
            if (active == null) seed.ActiveSkills = new SkillData[0];
            else
            {
                seed.ActiveSkills = new SkillData[active.Count];
                for (int i = 0; i < active.Count; i++) seed.ActiveSkills[i] = active[i];
            }
            return seed;
        }

        /// <summary>The authority pushes its record here; mana and ammunition components become displays of it.</summary>
        public void ApplyKitView(in KitView view)
        {
            _authorityKit = view;
            _hasAuthorityKit = true;
            if (mana != null) mana.ApplyView(view.Mana, view.MaxMana, view.ManaRegen, view.ManaRegenDelay, view.ManaSinceSpend, view.ManaRegenAllowed);
            if (loadout != null) loadout.ApplyAmmoView(in view);
            RangedCombat ranged = Ranged;
            if (ranged != null) ranged.ApplyKitView(in view);
        }

        private RangedCombat _ranged;
        private RangedCombat Ranged => _ranged != null ? _ranged : (_ranged = GetComponent<RangedCombat>());

        bool IAmmoSource.HasAmmo(int count) => !IsInFloorTransit && Ranged != null && Ranged.HasAmmo(count);
        bool IAmmoSource.TryConsumeAmmo(int count) => !IsInFloorTransit && Ranged != null && Ranged.TryConsumeAmmo(count);

        public bool IsDead => health != null && health.IsDead;
        public float CurrentHealth => health != null ? health.CurrentHealth : 0f;
        public float MaxHealth => health != null ? health.MaxHealth : 0f;
        public bool IsGuarding => !IsHoldingUtility && _guardHeld && Defense.CanBlock && !IsDead && !IsGuardStaggered;

        /// <summary>
        /// D1b: true while a broken guard keeps this player from raising the guard. The stagger is the combat
        /// authority's state (its clock), so a client cannot shorten it by holding the button.
        /// </summary>
        public bool IsGuardStaggered =>
            GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority) && authority is ICombatFlowState flow && flow.IsGuardStaggered(_combatId);
        public bool CanGuard => !IsHoldingUtility && Defense.CanBlock;

        /// <summary>U3C3: a shield contributes to the guard (a shield never parries, so the authority never records a riposte for it).</summary>
        public bool GuardHasShield => Defense.HasShield;
        public bool CanParry => Defense.CanParry;
        public DefenseProfile Defense
        {
            get
            {
                EffectiveStatsSnapshot snapshot = EffectiveStats;
                return snapshot.IsReady ? snapshot.Defense : _defense;
            }
        }
        public bool IsAlive => !IsDead;

        public EmberdelveInput Input => input;
        public PlayerMovement Movement => movement;
        public PlayerLook Look => look;
        public HealthComponent Health => health;
        public ManaComponent Mana => mana;
        public StatusComponent Status => status;
        public SkillCaster Skills => skills;
        public Inventory Inventory => inventory;
        public Equipment Equipment => equipment;
        public ClassLoadout Loadout => loadout;
        public CharacterClassData Class => _class;
        public WeaponData MainHandWeapon => _mainHand;
        public WeaponData OffhandWeapon => _offhand != null ? _offhand : (_class != null && !KitIsItems ? _class.offhandShield : null);

        /// <summary>U5A: the class kit lives in the hand slots as items, so an empty hand stays empty (no class shield or weapon comes back).</summary>
        private bool KitIsItems => equipment != null && equipment.KitIsItems;

        /// <summary>Passive modifiers resolved from the class kit. Null until the class has been applied.</summary>
        public CharacterStatStack Stats => skills != null ? skills.Stats : null;

        /// <summary>
        /// The live six-attribute / derived-vital owner (Docs/Design/ATTRIBUTES.md). Distinct from
        /// <see cref="Stats"/>, which stays the passive and gear StatModifier stack. Null only before the
        /// rig has been wired.
        /// </summary>
        public StatsComponent AttributeStats => _attributeStats;

        /// <summary>
        /// One cached capture over the current authoritative sources. Source version checks cover direct
        /// stack, attribute, and status mutations; the rig revision covers hand/defense and class commits.
        /// </summary>
        public EffectiveStatsSnapshot EffectiveStats
        {
            get
            {
                CharacterStatStack modifiers = Stats;
                int attributesVersion = _attributeStats != null ? _attributeStats.Version : 0;
                int modifiersVersion = modifiers != null ? modifiers.Version : 0;
                int statusVersion = status != null ? status.Version : 0;
                if (!_effectiveStatsSnapshotCached
                    || _effectiveStatsSnapshotRevision != _effectiveStatsRevision
                    || _effectiveStatsSnapshotClass != _class
                    || _effectiveStatsSnapshotEquipment != equipment
                    || _effectiveStatsSnapshotStatus != status
                    || _effectiveStatsSnapshotModifiers != modifiers
                    || _effectiveStatsSnapshotAttributesVersion != attributesVersion
                    || _effectiveStatsSnapshotModifiersVersion != modifiersVersion
                    || _effectiveStatsSnapshotStatusVersion != statusVersion)
                {
                    _effectiveStatsSnapshot = EffectiveStatsResolver.Capture(_class, _attributeStats,
                        modifiers, equipment != null ? equipment.WornInstances : null, status, _defense);
                    _effectiveStatsSnapshotCached = true;
                    _effectiveStatsSnapshotRevision = _effectiveStatsRevision;
                    _effectiveStatsSnapshotClass = _class;
                    _effectiveStatsSnapshotEquipment = equipment;
                    _effectiveStatsSnapshotStatus = status;
                    _effectiveStatsSnapshotModifiers = modifiers;
                    _effectiveStatsSnapshotAttributesVersion = attributesVersion;
                    _effectiveStatsSnapshotModifiersVersion = modifiersVersion;
                    _effectiveStatsSnapshotStatusVersion = statusVersion;
                }

                return _effectiveStatsSnapshot;
            }
        }

        private void InvalidateEffectiveStats() => _effectiveStatsRevision++;
        public Vector3 AimOrigin => look != null ? look.AimOrigin : transform.position;
        public Vector3 AimDirection => look != null ? look.AimDirection : transform.forward;
        public Vector3 Facing => transform.forward;

        private void Awake()
        {
            WireRig();
            if (GetComponent<GuardZoneVisual>() == null) gameObject.AddComponent<GuardZoneVisual>();
            if (GetComponent<Emberdelve.Feedback.WorldFootsteps>() == null) gameObject.AddComponent<Emberdelve.Feedback.WorldFootsteps>();
        }

        /// <summary>
        /// Component resolution and event wiring, split out of Awake and idempotent so the same rig can
        /// also be driven directly by an editor tool or a test harness that adds the components itself.
        /// Calling it twice resolves nothing twice: the combat registration and the health/mana event
        /// subscriptions happen exactly once.
        /// </summary>
        public void WireRig()
        {
            if (input == null) input = GetComponent<EmberdelveInput>();
            if (movement == null) movement = GetComponent<PlayerMovement>();
            if (look == null) look = GetComponent<PlayerLook>();
            // Old saved rigs predate item-owned torch lighting. Migrate only in a running game,
            // never while editor tooling wires the user's open scene.
            if (Application.isPlaying) DisableLegacyCameraFill(look != null ? look.AimTransform : null);
            if (health == null) health = GetComponent<HealthComponent>();
            if (inventory == null) inventory = GetComponent<Inventory>();
            if (loadout == null) loadout = GetComponent<ClassLoadout>();
            if (movement != null) movement.SetFlowSource(this);

            // Mana, status state and the skill kit are created here rather than required in the
            // scene, so a rig built before this milestone still gets them, and a rebuilt scene simply has
            // them already.
            if (mana == null) mana = ManaComponent.Ensure(gameObject);
            if (status == null) status = StatusComponent.Ensure(gameObject);
            if (equipment == null) equipment = Equipment.Ensure(gameObject);
            if (skills == null)
            {
                skills = GetComponent<SkillCaster>();
                if (skills == null) skills = gameObject.AddComponent<SkillCaster>();
            }

            if (_attributeStats == null) _attributeStats = new StatsComponent();

            if (_wired) return;

            // U3A2: a client-only process holds this rig as a replicated VIEW. It takes no combat id and is never
            // registered as a combat entity (the server owns the world); its player id is set by SetNetworkPlayerId, and
            // none of the combat event wiring below applies to it. Offline / server / host take the path they always did.
            if (NetRole.IsClientOnly)
            {
                _wired = true;
                return;
            }

            _combatId = CombatRegistry.AllocateId();
            _playerId = _combatId;
            CombatRegistry.Register(_combatId, this);
            _registered = true;

            _hitVolumes = HitVolumeLayout.EnsureHumanoid(gameObject, 0.92f, 1.68f);

            // HealthComponent only knows its own pool; GameEvents is what the HUD listens to. This rig is
            // the local player's composition root, so it is the place that turns local health into the
            // player-health event. Enemies keep their own Changed subscriptions and raise nothing here.
            if (health != null)
            {
                health.Changed += OnHealthChanged;
                health.Died += OnPlayerDied;
            }

            if (mana != null) mana.Changed += OnManaChanged;
            if (movement != null) movement.DashEnded += OnDashEnded;

            _wired = true;
        }

        internal static void DisableLegacyCameraFill(Transform camera)
        {
            if (camera == null || camera.GetComponent<Camera>() == null) return;
            Transform legacy = camera.Find("Player_Torch"); // Exact direct child, never an equipped item.
            Light source = legacy != null ? legacy.GetComponent<Light>() : null;
            if (source != null) source.enabled = false;
        }

        private void OnHealthChanged(float current, float max)
        {
            GameEvents.RaisePlayerHealth(PlayerId, current, max);
        }

        private void OnManaChanged(float current, float max)
        {
            GameEvents.RaisePlayerMana(PlayerId, current, max);
        }

        /// <summary>
        /// Re-applies the numbers that depend on the resolved stat picture after equipment changes: pool
        /// ceilings, regeneration, the movement speed rule and the defense profile.
        ///
        /// Two rules hold this together. First, the effective scores and derived pools are recomputed from the
        /// pristine class numbers plus every ITEM INSTANCE currently in an equipment slot - each worn piece's
        /// fixed authored attribute bonuses and that instance's own rolled attribute affixes, summed once per
        /// slot (U2B5) - so wearing the same piece twice is the same as wearing it once, and swapping two
        /// different rolls of the same base replaces the bonus set instead of adding to it. Second, only the
        /// CEILINGS are re-applied here: SetMax clamps a current pool down when the ceiling shrinks and pays
        /// out nothing when it grows, so equipping cannot heal, unequipping cannot refill, and neither can
        /// revive a dead pool. The regen clocks are not restarted, so a gear change keeps the delay the run
        /// already had.
        /// </summary>
        public void RefreshEquipmentStats()
        {
            if (_class == null) return;

            WireRig();

            RebuildAttributeStatsFromGear();

            // Client equipment snapshots dress the rig; vitals arrive on their own server-owned stream.
            // A later loadout refresh must not clamp a previously received health sample to local kit stats.
            if (!NetRole.IsClientOnly)
            {
                VitalLimits limits = ResolveVitalLimits();
                if (health != null) health.SetMax(limits.MaxHealth);
                if (mana != null)
                {
                    mana.SetMax(limits.MaxMana);
                    mana.SetRegen(limits.ManaRegen, limits.ManaRegenDelay);
                }

                // An equipment revision updates the authoritative ceiling/regen without refilling.
                SeedKit(KitSeedReason.Revision);
            }

            if (movement != null) PushMovementStatSources();

            _defense = ResolveDefense(_mainHand);
            InvalidateEffectiveStats();
        }

        /// <summary>
        /// The one place that tells the attribute component what is currently worn, and it always says it in
        /// the instance form: the fixed authored bonuses come from each worn definition and the rolled
        /// ability-score affixes come from each worn instance, so the scores, the derived pools, the movement
        /// term, the weapon scale and the ability scale are all produced from one derivation pass over one
        /// snapshot. No equipment component means nothing is worn, which is the pristine class numbers again.
        /// </summary>
        private void RebuildAttributeStatsFromGear()
        {
            if (_attributeStats == null) return;

            _attributeStats.RebuildFromInstances(equipment != null ? equipment.WornInstances : null);
        }

        /// <summary>
        /// The pool ceilings and rates this rig should be running for the class and gear it has right now.
        /// Attribute-derived pristine bases first, then the legacy passive/gear StatModifier stack applied
        /// on top of those bases exactly once - never both a class number and a derived number.
        /// </summary>
        public VitalLimits ResolveVitalLimits()
        {
            VitalLimits limits = new VitalLimits
            {
                MaxHealth = Mathf.Max(1, _class != null ? _class.maxHealth : 1),
                MaxMana = Mathf.Max(1, _class != null ? _class.EffectiveMaxMana : 1),
                ManaRegen = _class != null ? Mathf.Max(0f, _class.manaRegenPerSecond) : 0f,
                HealthRegenPerSecond = _class != null ? Mathf.Max(0f, _class.healthRegenPerSecond) : 0f,
                HealthRegenDelay = _class != null ? Mathf.Max(0f, _class.regenDelay) : 0f,
                ManaRegenDelay = _class != null ? Mathf.Max(0f, _class.manaRegenDelay) : 0f
            };

            if (_class == null) return limits;

            EffectiveStatsSnapshot snapshot = EffectiveStats;
            limits.MaxHealth = snapshot.MaxHealth;
            limits.MaxMana = snapshot.MaxMana;
            limits.ManaRegen = snapshot.ManaRegenPerSecond;
            limits.HealthRegenPerSecond = snapshot.HealthRegenPerSecond;
            limits.HealthRegenDelay = snapshot.HealthRegenDelay;
            limits.ManaRegenDelay = snapshot.ManaRegenDelay;
            return limits;
        }

        /// <summary>
        /// Fills the pools once from the current class/gear numbers. This is the run-start reset
        /// ClassLoadout calls when a class is applied; equipment changes never take this path because a
        /// ceiling change is not a refill (see <see cref="RefreshEquipmentStats"/>).
        /// </summary>
        public void InitializeVitals()
        {
            if (_class == null) return;

            VitalLimits limits = ResolveVitalLimits();

            if (movement != null) movement.EndCorpse();
            if (health != null) health.Configure(limits.MaxHealth, limits.HealthRegenPerSecond, limits.HealthRegenDelay);
            if (mana != null) mana.Configure(limits.MaxMana, limits.ManaRegen, limits.ManaRegenDelay);
        }

        /// <summary>One resolved set of pool ceilings/rates. Plain values, so any consumer can log or assert them.</summary>
        public struct VitalLimits
        {
            public int MaxHealth;
            public int MaxMana;
            public float ManaRegen;
            public float HealthRegenPerSecond;
            public float HealthRegenDelay;
            public float ManaRegenDelay;
        }

        /// <summary>
        /// Hands movement the two sources its speed rule needs, in one place: the legacy passive/gear stack
        /// (multiplier and flat) and the live attribute component (the DEX term). Called on class application
        /// and after every equipment change, so an equip or unequip changes the resolved walking speed on the
        /// very next query without either source being applied twice.
        /// </summary>
        private void PushMovementStatSources()
        {
            if (movement == null) return;

            movement.ApplyStatStack(Stats);
            movement.ApplyAttributeStats(_attributeStats);
            movement.ApplyEffectiveStatsSource(() => EffectiveStats);
        }

        /// <summary>
        /// Death is handled as state, not as damage: the kit is cleared, the dash is cancelled, temporary
        /// statuses come off, and mana stops regenerating. RunDirector still owns the run outcome.
        /// </summary>
        private void OnPlayerDied()
        {
            _impactGuard.Reset();
            _replicatedImpactGuardUntil = 0f;
            GetComponent<RangedCombat>()?.CancelDraw();
            if (skills != null) skills.HandleDeath();
            if (movement != null) movement.CancelDash();
            // On a client the bodies of others have no live capsule and the own body cannot block its owner.
            if (movement != null && !NetRole.IsClientOnly) movement.BeginCorpse();
            if (status != null) status.ClearStatuses();
            SetGuardHeld(false);
        }

        private void OnDestroy()
        {
            if (!_wired || !_registered) return;

            if (health != null)
            {
                health.Changed -= OnHealthChanged;
                health.Died -= OnPlayerDied;
            }

            if (mana != null) mana.Changed -= OnManaChanged;
            if (movement != null) movement.DashEnded -= OnDashEnded;

            CombatRegistry.Unregister(_combatId, this);
        }

        // ---- R1c3: dash result reporting / rollback ----

        private void OnDashEnded(int activationId, Vector3 endPosition)
        {
            if (GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority))
            {
                authority.ReportDashResult(CombatSender, _combatId, activationId, endPosition, out _);
            }
        }

        Vector3 IMovementRollback.MovementPosition => transform.position;
        Vector3 IMovementRollback.MovementCenterOffset => movement != null ? movement.CapsuleCenterOffset : Vector3.up;
        float IMovementRollback.MovementRadius => movement != null ? movement.CapsuleRadius : 0.34f;

        void IMovementRollback.RollbackTo(Vector3 position)
        {
            if (movement != null) movement.TeleportTo(position);
            else transform.position = position;
        }

        /// <summary>Initializes a client view without minting items, filling vitals or starting simulation.</summary>
        public void InitializeReplicatedClass(CharacterClassData data, GameConfig config)
        {
            if (!NetRole.IsClientOnly || data == null || config == null) return;
            WireRig();
            _class = data;
            _mainHand = null;
            _offhand = null;
            equipment.Initialize(this);
            if (loadout != null) loadout.InitializeReplicatedClass(data, config, this);
            PlayerAvatar.Bind(this);
        }

        public void ApplyClass(CharacterClassData data, GameConfig config)
        {
            WireRig();

            _class = data;
            _mainHand = data != null ? data.primaryWeapon : null;
            _offhand = data != null ? data.offhandShield : null;

            // The pristine class scores and base vitals are captured before anything is configured, so the
            // pools ClassLoadout is about to build are derived from them and equipment can only ever be
            // added on top inside a later derivation. The rebuild that follows is what makes a class applied
            // on top of gear that is still worn (a re-entry, a class swap) fill its pools from pristine class
            // numbers plus that gear once, instead of from the bare class - InitializeVitals runs inside
            // loadout.Apply below, so the snapshot has to be complete before it.
            if (_attributeStats != null) _attributeStats.Initialize(data);
            RebuildAttributeStatsFromGear();

            // ClassLoadout is the single class-application point: it configures vitals, mana, the skill kit
            // and the weapon slots, and equipping the primary weapon calls back into ApplyMainHandWeapon.
            if (loadout != null)
            {
                // R1c2: the rig is still being assembled; the authority is seeded once, below, from the finished rig.
                _kitSeedSuppress++;
                try { loadout.Apply(data, config, this); }
                finally { _kitSeedSuppress--; }
            }

            // ClassLoadout handed movement the passive/gear stack; the attribute term comes from this rig,
            // which is the only owner of the live scores. Both are pushed together so movement resolves one
            // speed picture rather than two half-updated ones.
            PushMovementStatSources();

            _defense = ResolveDefense(_mainHand);
            InvalidateEffectiveStats();

            // R1c2: a class apply is a run start; the authority rebuilds and refills its record from the finished rig.
            // Restoring gear may publish weapon changes. Suppress intermediate metadata seeds;
            // the final ClassApply below replaces the authority record with the validated kit/ammo.
            _kitSeedSuppress++;
            try { Emberdelve.Items.OfflineProfileSession.ApplyPrepared(this); }
            finally { _kitSeedSuppress--; }
            SeedKit(KitSeedReason.ClassApply);
            PlayerAvatar.Bind(this);
        }

        /// <summary>
        /// What is in the off hand. The class asset decides the default; an equipped off-hand item replaces it
        /// for as long as it is worn, and taking the item off puts the class default back.
        /// </summary>
        public void SetOffhandWeapon(WeaponData weapon)
        {
            _offhand = weapon != null ? weapon : (_class != null && !KitIsItems ? _class.offhandShield : null);
            _defense = ResolveDefense(_mainHand);
            InvalidateEffectiveStats();
            SyncGuardVisual();
            if (loadout != null) loadout.RefreshHands(); // U5A: the arms and the third-person hands follow a change of the off hand too
        }

        private DefenseProfile ResolveDefense(WeaponData weapon)
        {
            if (_class == null) return DefenseProfile.Empty;

            CharacterStatStack stats = Stats;
            float passiveBlockBonus = stats != null
                ? stats.Bonus(SkillStat.BlockReduction, _class.blockReduction)
                : 0f;

            return DefenseProfile.Resolve(_class, weapon, _offhand != null ? _offhand : (KitIsItems ? null : _class.offhandShield), passiveBlockBonus);
        }

        /// <summary>
        /// Called by ClassLoadout whenever a different weapon ends up in the main hand. Defensive
        /// capability belongs to the weapon actually held, not to the class as a whole, so switching a
        /// sword for a crossbow legitimately removes the block and switching a bow for a rapier can
        /// legitimately introduce a Parry flag.
        /// </summary>
        public void ApplyMainHandWeapon(WeaponData weapon)
        {
            _mainHand = weapon;
            _defense = ResolveDefense(weapon);
            InvalidateEffectiveStats();
            SyncGuardVisual();
        }

        /// <summary>
        /// Outgoing damage multipliers the authority is allowed to apply: the attribute rule for the weapon
        /// actually being used, then the permanent passive/gear StatModifier factors, then any temporary
        /// buff. The attacker never multiplies its own damage, and the attribute term is applied here and
        /// nowhere else, so LocalCombatAuthority's single read of this provider is what makes it count once.
        /// </summary>
        public float GetOutgoingDamageMultiplier(WeaponData weapon)
        {
            EffectiveStatsSnapshot snapshot = EffectiveStats;
            if (snapshot.IsReady) return snapshot.GetOutgoingDamageMultiplier(weapon);

            float multiplier = 1f;
            CharacterStatStack stats = Stats;

            // Docs/Design/ATTRIBUTES.md: STR for sword/axe, DEX for bow/crossbow, the better of the two for
            // a definition marked finesse. Read as a multiplier (base damage 1) so the weapon's own number
            // still comes from the weapon asset inside the resolver.
            if (weapon != null && _attributeStats != null && _attributeStats.IsReady)
            {
                multiplier *= _attributeStats.GetWeaponDamage(1f, weapon.DamageScale);
            }

            if (stats != null && weapon != null)
            {
                multiplier *= weapon.IsRanged
                    ? stats.Multiplier(SkillStat.RangedDamage)
                    : stats.Multiplier(SkillStat.MeleeDamage);
            }

            if (status != null) multiplier *= status.OutgoingDamageMultiplier;

            return Mathf.Max(0f, multiplier);
        }

        /// <summary>
        /// The FLAT weapon-base damage the currently worn item INSTANCES add to the weapon actually being used
        /// (Docs/Design/ITEM_AFFIXES.md <c>melee_damage</c> / <c>projectile_damage</c>). The typed key is chosen
        /// by the weapon's own channel - <see cref="Emberdelve.Data.WeaponData.IsRanged"/> picks projectile_damage,
        /// anything else picks melee_damage - and every worn slot is summed once through that instance's
        /// immutable accepted <see cref="Emberdelve.Items.AffixRecord"/>s via
        /// <see cref="Emberdelve.Items.ItemInstance.AffixTotal"/>. No strings, no
        /// stat mutation, no cache: the combat authority re-reads this at each accepted hit, so equipping,
        /// unequipping, swapping to a different roll of the same base and a repeated refresh all resolve from
        /// the live slot contents and nothing accumulates.
        ///
        /// It is a separate channel from <see cref="GetOutgoingDamageMultiplier"/> on purpose. A rolled damage
        /// affix is NOT mapped onto the legacy SkillStat.MeleeDamage/RangedDamage multipliers, so the authored
        /// fixed multipliers, the STR/DEX attribute factor and any temporary buff keep their exact existing
        /// numbers, while the rolled flat scales the weapon base before falloff/charge instead of scaling the
        /// finished product. A non-finite or negative total sanitizes to 0 rather than multiplying damage by
        /// nonsense, and an entity with no gear - or a weapon whose channel nothing matches - gets 0, which is
        /// the previous behaviour unchanged.
        /// </summary>
        public float GetFlatWeaponDamageBonus(WeaponData weapon)
        {
            if (weapon == null) return 0f;

            EffectiveStatsSnapshot snapshot = EffectiveStats;
            if (snapshot.IsReady) return snapshot.GetFlatWeaponDamageBonus(weapon);

            IReadOnlyList<ItemInstance> worn = equipment != null ? equipment.WornInstances : null;
            if (worn == null || worn.Count == 0) return 0f;

            WeaponBaseDamageChannel channel = weapon.IsRanged
                ? WeaponBaseDamageChannel.Projectile : WeaponBaseDamageChannel.Melee;

            float total = 0f;
            for (int i = 0; i < worn.Count; i++)
            {
                ItemInstance item = worn[i];
                if (item == null) continue;
                IReadOnlyList<AffixRecord> affixes = item.Affixes;
                if (affixes == null) continue;
                for (int a = 0; a < affixes.Count; a++)
                {
                    AffixRecord affix = affixes[a];
                    if (!AffixStatKeys.TryGetRoute(affix.StatKey, out AffixStatRoute route)
                        || route.Kind != AffixStatRouteKind.WeaponBaseDamageFlat
                        || route.WeaponDamageChannel != channel) continue;
                    total += affix.Value;
                }
            }

            return WeaponData.SanitizeBaseDamageBonus(total);
        }

        /// <summary>
        /// The physical ARMOR RATING (Docs/Design/ARMOR_RULES.md, U2B8) this rig is currently wearing, read
        /// live from the one equipment stat source <see cref="Emberdelve.Items.Equipment.RefreshStats"/>
        /// replaces on every equip, unequip, uid swap and repeated refresh: that source already holds each worn
        /// definition's authored <see cref="SkillStat.Armor"/> modifiers AND each worn instance's rolled
        /// armor affix records mapped onto the same key (U2B6/U2B8), so this single flat total is the fixed and
        /// rolled rating summed exactly once - no second pass over the instances, no cache, no accumulation,
        /// and nothing left behind when a slot empties. A class passive that authors Armor lands in the same
        /// stack and is counted once by the same rule.
        ///
        /// It is a rating, not a damage claim and not a guard reduction: the authority reads it from the
        /// defender at the accepted-hit boundary, the intent carries nothing, and the resolver applies it to
        /// Physical damage only, from every direction, after the directional guard. The claim is sanitized to
        /// a nonnegative finite number here as well as in the resolver, so a tampered stack cannot poison
        /// damage; nothing worn and no stat stack at all both mean 0, which is the previous behaviour
        /// unchanged.
        /// </summary>
        public float GetArmorRating()
        {
            EffectiveStatsSnapshot snapshot = EffectiveStats;
            if (snapshot.IsReady) return snapshot.ArmorRating;

            CharacterStatStack stack = Stats;
            if (stack == null) return 0f;

            return DamageResolver.SanitizeArmorRating(stack.Flat(SkillStat.Armor));
        }

        public void ApplyStatus(StatusSpec spec, int sourceCombatId)
        {
            if (status == null || IsDead) return;
            status.ApplyStatus(spec, sourceCombatId);
        }

        public void ClearStatuses()
        {
            if (status == null) return;
            status.ClearStatuses();
        }

        public bool HasStatus(StatusKind kind) => status != null && status.HasStatus(kind);

        /// <summary>
        /// Only the combat authority calls this. A player object never applies damage to itself,
        /// which is exactly the property an authoritative server needs.
        /// </summary>
        public void ApplyDamage(DamageInfo damage)
        {
            if (health == null || IsInFloorTransit) return;
            health.ApplyDamage(damage);
        }

        public void SetGuardHeld(bool held)
        {
            RefreshImpactGuard(); // Observe a swapped kit before consuming this input edge.
            bool wasHeld = _guardHeld;
            _guardHeld = held && !IsInFloorTransit;
            if (_guardHeld && !wasHeld) _guardHeldSince = AuthorityClock();
            RefreshImpactGuard();
            SyncGuardVisual();
        }

        private readonly ImpactGuardWindow _impactGuard = new ImpactGuardWindow();
        private float _replicatedImpactGuardUntil;
        public void ApplyReplicatedImpactGuard(float remaining)
        {
            if (!NetRole.IsClientOnly) return;
            _replicatedImpactGuardUntil = Time.time + (float.IsNaN(remaining) || float.IsInfinity(remaining) ? 0f : Mathf.Max(0f, remaining));
        }
        public float ImpactGuardRemaining
        {
            get
            {
                if (IsDead || IsExtracted) return 0f;
                if (NetRole.IsClientOnly) return Mathf.Max(0f, _replicatedImpactGuardUntil - Time.time);
                RefreshImpactGuard();
                return _impactGuard.Remaining(AuthorityClock());
            }
        }
        private void RefreshImpactGuard()
        {
            if (NetRole.IsClientOnly) return;
            WeaponData weapon = ActiveWeapon;
            int epoch = weapon != null ? weapon.GetInstanceID() : 0;
            if (GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority)
                && authority is LocalCombatAuthority local && local.TryGetKit(_combatId, out KitView kit)) epoch = kit.Epoch;
            _impactGuard.Update(weapon != null && weapon.CanImpactGuard && IsGuardingWithDefense(Defense),
                _guardHeld, epoch, AuthorityClock(), weapon != null ? weapon.impactGuardDuration : 0f,
                weapon != null ? weapon.impactGuardRearmDelay : 0f);
        }

        private void SyncGuardVisual()
        {
            bool guarding = IsGuarding;
            if (movement != null) movement.SetGuardActive(guarding);
            if (loadout != null) loadout.SetGuardHeldVisual(guarding);
        }

        public bool TryGetProjectileGuardBlade(out GuardBladeBox blade) => TryGetGuardBlade(out blade);

        public bool TryGetGuardShield(out GuardShieldShape shield)
        {
            shield = default;
            DefenseProfile defense = Defense;
            if (!defense.HasShield || !IsGuardingWithDefense(defense) || AimDirection.sqrMagnitude < .0001f) return false;
            shield = GuardShieldPose.Resolve(AimOrigin, Quaternion.LookRotation(AimDirection.normalized, Vector3.up));
            return true;
        }

        public bool TrySweepGuard(Vector3 start, Vector3 end, float radius, out float distance, out bool shieldContact, out bool weaponContact,
            bool projectile = false)
        {
            distance = 0f;
            shieldContact = false;
            weaponContact = false;
            if (TryGetGuardShield(out GuardShieldShape shield))
            {
                shieldContact = shield.TrySweep(start, end, radius, out distance);
                return shieldContact;
            }
            if (TryGetCrossbowGuard(out CrossbowGuardShape crossbow))
            {
                weaponContact = crossbow.TrySweep(start, end, radius, out distance);
                return weaponContact;
            }
            if (TryGetBowGuard(out BowGuardShape bow))
            {
                weaponContact = bow.TrySweep(start, end, radius, out distance);
                return weaponContact;
            }
            if (!TryGetGuardBlade(out GuardBladeBox blade)) return false;
            if (blade.TrySweep(start, end, radius, out distance)) return true;   // the metal itself
            if (!projectile || !blade.TrySweep(start, end, radius + GuardBladePose.ProjectileCatchRadius, out distance)) return false;
            // Deflected in passing, unless it is going straight into the hand on the grip: that is flesh, not metal. The flight
            // is stepped, so the line is looked along beyond this step's end.
            Vector3 along = end - start;
            Vector3 ahead = start + along.normalized * Mathf.Max(along.magnitude, 1.5f);
            return !(TryGetGuardGripHitShape(out HitShape grip)
                && SwingGeometry.SegmentDistance(start, ahead, grip.A, grip.B, out _) <= grip.Radius + Mathf.Max(0f, radius));
        }

        public bool TryGetBowGuard(out BowGuardShape shape)
        {
            shape = default;
            if (ActiveWeapon == null || !ActiveWeapon.CanBowGuard || Defense.HasShield
                || !IsGuardingWithDefense(Defense) || AimDirection.sqrMagnitude < .0001f) return false;
            shape = BowGuardPose.Resolve(AimOrigin, Quaternion.LookRotation(AimDirection.normalized, Vector3.up));
            return true;
        }

        public bool TryGetCrossbowGuard(out CrossbowGuardShape shape)
        {
            shape = default;
            if (ActiveWeapon == null || !ActiveWeapon.CanCrossbowGuard || Defense.HasShield
                || !IsGuardingWithDefense(Defense) || AimDirection.sqrMagnitude < .0001f) return false;
            shape = CrossbowGuardPose.Resolve(AimOrigin, Quaternion.LookRotation(AimDirection.normalized, Vector3.up));
            return true;
        }

        public bool TryGetGuardGripHitShape(out HitShape grip)
        {
            grip = default;
            if (!TryGetGuardBlade(out _)) return false;
            grip = GuardBladePose.ResolveGripHitShape(AimOrigin, Quaternion.LookRotation(AimDirection.normalized, Vector3.up),
                ActiveWeapon != null && ActiveWeapon.twoHanded);
            return true;
        }

        /// <summary>The current visible guard blade shape, shared by projectile and melee authority sweeps.</summary>
        public bool TryGetGuardBlade(out GuardBladeBox blade)
        {
            blade = default;
            DefenseProfile defense = Defense;
            WeaponData weapon = ActiveWeapon;
            if (!IsGuardingWithDefense(defense) || weapon == null || weapon.IsRanged || !weapon.CanBlock || defense.HasShield) return false;
            if (GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority)
                && authority is ICombatFlowState flow && flow.IsSwingInProgress(_combatId)) return false;
            Vector3 aim = AimDirection;
            if (aim.sqrMagnitude < 0.0001f) return false;
            blade = GuardBladePose.Resolve(AimOrigin, Quaternion.LookRotation(aim.normalized, Vector3.up), true, weapon.twoHanded);
            return true;
        }

        public bool TryGetGuardReduction(in DamageInfo incoming, out float reduction)
        {
            if (TryResolveGuard(incoming, CombatFlowTuning.Default, out GuardResult result))
            {
                reduction = result.Reduction;
                return true;
            }

            reduction = 0f;
            return false;
        }

        /// <summary>
        /// Guard (D1b + U3C): the decision is the pure rule <see cref="BladeGuardRules.Resolve"/>, fed with what this rig knows:
        /// the guard flag and kit (as before), the bearing to the attacker (a hit from behind is never blocked), the guard height from
        /// the view pitch against the height band of the incoming swing, the impact check (a hit whose impact power reaches the kit's
        /// impact resist BREAKS the guard: only <c>guardBreakReductionScale</c> of the mitigation applies and the authority staggers the
        /// guard) and, when the tuning enables it, the guard endurance pool (an empty pool cannot block; a held hit pays exactly once).
        /// Shields and blades share the rule and neither parries. With the shipped tuning (no pool, bands only differ for a lowered
        /// guard against an overhead) the outcome is the D1b outcome.
        /// </summary>
        /// <summary>Radius of the line an instant (non-swept, non-projectile) hit is tested with against the raised defence.</summary>
        public const float InstantHitGuardRadius = 0.05f;

        public bool TryResolveGuard(in DamageInfo incoming, CombatFlowTuning tuning, out GuardResult result)
        {
            result = default;
            DefenseProfile defense = Defense;
            // Swept melee and projectiles must touch the actual raised defensive surface.
            // Facing alone cannot turn a body hit into either a blade or a shield block.
            if (incoming.RangedProjectile || incoming.MeleeGuardChecked || (ActiveWeapon != null && ActiveWeapon.CanRangedGuard))
            {
                bool contact = defense.HasShield ? incoming.GuardShieldContact
                    : ActiveWeapon != null && ActiveWeapon.CanRangedGuard ? incoming.GuardWeaponContact : incoming.GuardBladeContact;
                if (!contact) return false;
            }

            else
            {
                // Every other hit (a skill's direct damage, a legacy instant hit) is blocked by the same physics: only when its line
                // from the source to the struck point passes through the raised shield or blade. Facing alone never blocks.
                if (!IsGuardingWithDefense(defense)
                    || !TrySweepGuard(incoming.SourcePosition, incoming.Point, InstantHitGuardRadius, out _, out _, out _)) return false;
            }

            bool physicalGuard = incoming.GuardShieldContact || incoming.GuardWeaponContact || (incoming.GuardBladeContact && !defense.HasShield)
                || !(incoming.RangedProjectile || incoming.MeleeGuardChecked);

            CombatFlowTuning flow = tuning ?? CombatFlowTuning.Default;
            bool enduranceOn = flow.GuardEnduranceEnabled;
            float now = AuthorityClock();
            if (enduranceOn) _guardEndurance.Refresh(now, flow.guardEnduranceMax, flow.guardEnduranceRegenPerSecond, flow.guardEnduranceRegenDelay);

            GuardInput input = new GuardInput
            {
                Raised = IsGuardingWithDefense(defense),
                BlockReduction = defense.BlockReduction,
                ArcDegrees = defense.BlockArcDegrees,
                BearingDegrees = physicalGuard ? 0f : BearingToSource(incoming),
                HasShield = defense.HasShield,
                GuardHeight = CurrentGuardHeight(flow),
                AttackHeight = physicalGuard ? BladeHeight.Unspecified : incoming.AttackHeight,
                HeightTolerance = defense.HasShield ? flow.shieldHeightTolerance : flow.bladeHeightTolerance,
                ImpactPower = incoming.ImpactPower,
                ImpactResist = defense.ImpactResist + (ImpactGuardRemaining > 0f && ActiveWeapon != null ? Mathf.Max(0f, ActiveWeapon.impactGuardResistance) : 0f),
                BreakReductionScale = tuning != null ? tuning.guardBreakReductionScale : CombatFlowTuning.DefaultGuardBreakReductionScale,
                EnduranceEnabled = enduranceOn,
                EnduranceCurrent = _guardEndurance.Current,
                EnduranceCost = enduranceOn
                    ? BladeGuardRules.BlockCost(incoming.ImpactPower, flow.guardBlockCostBase, flow.guardBlockCostPerImpact)
                    : 0f
            };

            GuardVerdict verdict = BladeGuardRules.Resolve(in input);
            LastGuardOutcome = verdict.Outcome;
            if (enduranceOn && verdict.EnduranceSpent > 0f) _guardEndurance.Spend(verdict.EnduranceSpent, now);
            if (!verdict.Mitigated) return false;

            result = new GuardResult(verdict.Reduction, verdict.Broken);
            return true;
        }

        /// <summary>U3C: what the guard did to the last hit it was asked about (NotGuarding until the first hit). Read by gates and the HUD.</summary>
        public GuardOutcome LastGuardOutcome { get; private set; }

        /// <summary>U3C: guard endurance left (0 while the tuning has no pool). The pool is the authority clock's: it regenerates between hits.</summary>
        public float GuardEnduranceCurrent => _guardEndurance.Current;

        /// <summary>U3C: the guard band the view pitch puts this rig's guard in (Mid when the aim pose is unavailable).</summary>
        public BladeHeight CurrentGuardHeight(CombatFlowTuning flow)
        {
            CombatFlowTuning tuning = flow ?? CombatFlowTuning.Default;
            if (!TryGetAimPose(out _, out _, out float pitch)) return BladeHeight.Mid;
            return BladeGuardRules.GuardHeightFromPitch(pitch, tuning.guardHighPitchDegrees, tuning.guardLowPitchDegrees);
        }

        private readonly GuardEnduranceState _guardEndurance = new GuardEnduranceState();

        private static float AuthorityClock()
        {
            if (GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority) && authority is LocalCombatAuthority local) return local.Now;
            return Time.time;
        }

        /// <summary>Angle in degrees between this rig's facing and the horizontal direction to the attacker (0 ahead, 180 behind).</summary>
        private float BearingToSource(in DamageInfo incoming)
        {
            Vector3 toSource = incoming.SourcePosition - transform.position;
            toSource.y = 0f;
            if (toSource.sqrMagnitude < 0.0001f) toSource = -incoming.Direction;
            toSource.Normalize();
            return Vector3.Angle(transform.forward, toSource);
        }

        // D1c5: the guard flag does not reduce incoming damage while this rig's own swing runs on the authority (the
        // authority also refuses a swing begin while the flag is up, so the two states never legitimately overlap).
        // Reference port: the guard intercepts only once it has been held for the tuning's ready time (0.15 s). The raise itself
        // (visual, move slowdown, "is guarding" for refusing own attacks) starts with the press; see IsGuarding.
        private bool IsGuardingWithDefense(DefenseProfile defense) =>
            !IsHoldingUtility && _guardHeld && defense.CanBlock && !IsDead && !IsGuardStaggered && !OwnSwingRunning && IsGuardReady;

        private float _guardHeldSince = float.NegativeInfinity;

        /// <summary>True when the held guard has been up long enough to intercept (authority clock).</summary>
        public bool IsGuardReady
        {
            get
            {
                if (!_guardHeld) return false;
                float ready = GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority) && authority is LocalCombatAuthority local
                    ? local.GuardReadySeconds : CombatFlowTuning.DefaultGuardReadySeconds;
                return CombatFlowRules.IsGuardReady(_guardHeldSince, AuthorityClock(), ready);
            }
        }

        private bool OwnSwingRunning =>
            GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority)
            && authority is ICombatFlowState flow && flow.IsSwingInProgress(_combatId);

        public void PlaySwingVisual(WeaponData weapon, SwingPatternData pattern)
        {
            if (loadout != null) loadout.PlaySwingVisual(weapon, pattern, pattern != null ? pattern.Timing : default);
        }

        public void PlaySwingVisual(WeaponData weapon, SwingPatternData pattern, in SwingTiming timing)
        {
            if (loadout != null) loadout.PlaySwingVisual(weapon, pattern, timing);
        }

        /// <summary>
        /// D1b combat-flow move factor: the held weapon (and a contributing shield) penalty times what the combat
        /// authority says about this player's swing phase and guard stagger. PlayerMovement.ResolveGroundSpeed applies
        /// it, so the speed a server would validate already contains the fighting penalties.
        /// </summary>
        public float GetMoveFlowFactor()
        {
            WeaponData main = ActiveWeapon != null ? ActiveWeapon : _mainHand;
            WeaponData off = OffhandWeapon;
            bool offContributes = off != null && off.IsShield && !(main != null && main.twoHanded);
            // D3c: worn gear's authored penalty joins the held weapons' in the same factor (counted here and nowhere else).
            float factor = CombatFlowRules.HeldFactor(main, off, offContributes, equipment != null ? equipment.MoveSpeedPenaltyPoints : 0f);

            if (GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority) && authority is ICombatFlowState flow)
            {
                factor *= flow.GetMoveFactor(_combatId);
            }

            return factor;
        }

        /// <summary>The authority stopped the swing at level geometry: the visual recoils from where it is.</summary>
        public void PlaySwingRecoil()
        {
            if (loadout != null) loadout.PlaySwingRecoil();
        }

        public void SetDrawVisual(float progress)
        {
            if (loadout != null) loadout.SetDrawVisual(progress);
        }

        public Vector3 GetMuzzlePosition()
        {
            if (loadout != null && loadout.HasMuzzle) return loadout.MuzzlePosition;
            return AimOrigin + AimDirection * 0.35f;
        }

        public void SetAimingPenalty(float multiplier)
        {
            if (movement != null) movement.SetSpeedPenalty(multiplier);
        }

        public void SetInputEnabled(bool enabled)
        {
            enabled = enabled && !IsInFloorTransit;
            if (!enabled) GetComponent<RangedCombat>()?.CancelDraw();
            if (input != null) input.SetGameplayInputEnabled(enabled);
        }
    }
}
