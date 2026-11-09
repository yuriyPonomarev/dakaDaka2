using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Combat;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Interaction;

namespace Emberdelve.AI
{
    /// <summary>
    /// Scene object for one monster. Owns identity (combat id), health and movement; all decisions
    /// come from EnemyBrain and all damage goes through the combat authority, exactly like the player.
    /// D1c4: its melee is the same authority-owned swept swing the player uses. The agent is the pose provider (eye at its
    /// attack origin, yaw frozen to the direction the brain committed at windup start, pitch 0); the brain's windup stays the
    /// telegraph and the swing begins when the brain asks for the attack.
    /// Movement is grid-steered (transform based) rather than CharacterController based, so the body
    /// and head hit colliders can stay plain colliders on child objects.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(HealthComponent))]
    public sealed class EnemyAgent : MonoBehaviour, ICombatEntity, IStatusTarget, IDamageOutgoingProvider, IActiveWeaponProvider, IHitShapeProvider, IAimPoseProvider
    {
        [Header("Data")]
        [SerializeField] private EnemyData data;
        [SerializeField] private NavGridData navGrid;
        [SerializeField] private GameConfig config;

        [Header("Wiring")]
        [Tooltip("RunDirector, assigned by the level builder. Used to drop loot on death.")]
        [SerializeField] private MonoBehaviour lootHost;
        [SerializeField] private Collider headCollider;
        [SerializeField] private Transform bodyTransform;

        [Header("Movement")]
        [SerializeField] private float groundHeight = 0f;
        [SerializeField] private float repathInterval = 0.55f;
        [SerializeField] private float waypointTolerance = 0.85f;

        private HealthComponent _health;
        private NavGridPathfinder _nav;
        private EnemyBrain _brain;
        private Animator _restAnimator;
        private Collider[] _standingColliders;
        private bool[] _standingColliderEnabled;
        private readonly List<HitShape> _restShapes = new List<HitShape>(8);
        private CapsuleCollider[] _restColliders;
        private Collider _restHead;
        private bool _restHitPose;
        private readonly List<Vector3> _path = new List<Vector3>(64);
        private readonly RaycastHit[] _doorMoveHits = new RaycastHit[16];
        private readonly Collider[] _doorOverlapHits = new Collider[8];
        private int _pathIndex;
        private float _repathTimer;
        private float _timeSinceDamage;
        private int _combatId;
        private Vector3 _lastPathRequest = new Vector3(float.MaxValue, 0f, float.MaxValue);
        private Vector3 _bodyHomePosition;
        private Quaternion _lookRotation;
        private AttackTelegraphVisual _telegraph;
        private StatusComponent _status;
        private float _whiffTimer;
        private float _enemyDoorCooldown;
        private string _dropKey;

        private const float AggressorMemorySeconds = 4f;
        private const float TargetSwitchFactor = 0.75f;
        private int _lastAggressorId;
        private float _aggressorUntil;
        private int _currentTargetId;

        // D1c4: the swept swing in flight. _latchedDirection is the committed direction snapshot taken when the swing began, so
        // a new windup (a short attackCooldown) can never turn a swing that is still running.
        private int _swingId;
        private bool _swingLatched;
        private bool _swingResolved;
        private Vector3 _latchedDirection;
        private SwingPhase _swingPhase;

        /// <summary>Height above the root of the swing's eye: the origin the attack has always been aimed from.</summary>
        public const float AttackEyeHeight = 1.2f;

        public EntityKind Kind => EntityKind.Enemy;
        public int CombatId => _combatId;
        public int TeamId => CombatTeams.MonsterTeam;
        public Vector3 CombatCenter => _restHitPose && _restShapes.Count > 1
            ? (_restShapes[1].A + _restShapes[1].B) * .5f : transform.position + Vector3.up * 1.15f;
        public Collider HeadCollider => _restHitPose ? _restHead : headCollider;
        public bool IsDormant => _brain != null && _brain.IsDormant;
        public bool IsWaking => _brain != null && _brain.IsWaking;

        private HitVolume[] _hitVolumes;

        /// <summary>
        /// D1a: this monster's hit volumes (head, torso, arms, legs, feet), sized from its body scale and created
        /// once. They are analytic shapes the swept melee reads; the Body/Head colliders stay as they were (ranged
        /// rays, movement blockers).
        /// </summary>
        public void CollectHitShapes(List<HitShape> shapes)
        {
            if (_hitVolumes == null) _hitVolumes = HitVolumeLayout.EnsureHumanoid(gameObject, data != null && data.isElite ? 1.25f : 1f);
            if (_restHitPose) { shapes.AddRange(_restShapes); return; }
            HitVolumeLayout.Collect(_hitVolumes, shapes);
        }

        // The visual is lying on the floor, so both ranged colliders and swept hit shapes must follow it.
        // Normal upright collision stays untouched and is restored after the rise.
        public void RefreshDormantHitPose(Animator posedAnimator)
        {
            bool resting = (IsDormant || IsWaking) && !IsDead;
            if (!resting)
            {
                if (!_restHitPose) return;
                for (int i = 0; i < _standingColliders.Length; i++)
                    if (_standingColliders[i] != null) _standingColliders[i].enabled = _standingColliderEnabled[i];
                foreach (var collider in _restColliders) collider.enabled = false;
                _restHitPose = false;
                return;
            }
            if (posedAnimator == null || !posedAnimator.isHuman) return;
            _restAnimator = posedAnimator;
            _restShapes.Clear();
            float scale = data != null && data.isElite ? 1.25f : 1f;
            AddRestShape(HitLocation.Head, HumanBodyBones.Head, HumanBodyBones.Head, .20f * scale);
            AddRestShape(HitLocation.Body, HumanBodyBones.Hips, HumanBodyBones.Chest, .25f * scale);
            AddRestShape(HitLocation.Arms, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftHand, .11f * scale);
            AddRestShape(HitLocation.Arms, HumanBodyBones.RightUpperArm, HumanBodyBones.RightHand, .11f * scale);
            AddRestShape(HitLocation.Legs, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftFoot, .14f * scale);
            AddRestShape(HitLocation.Legs, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightFoot, .14f * scale);
            if (_restShapes.Count != 6) return;
            if (_restColliders == null)
            {
                _standingColliders = GetComponentsInChildren<Collider>();
                _standingColliderEnabled = new bool[_standingColliders.Length];
                for (int i=0;i<_standingColliders.Length;i++) _standingColliderEnabled[i]=_standingColliders[i].enabled;
                _restColliders = new CapsuleCollider[6];
                for (int i=0;i<6;i++)
                {
                    var go = new GameObject("RestingHit_"+i);
                    go.layer = headCollider != null ? headCollider.gameObject.layer : gameObject.layer;
                    go.transform.SetParent(transform, false);
                    _restColliders[i]=go.AddComponent<CapsuleCollider>();
                }
                _restHead=_restColliders[0];
            }
            foreach (var collider in _standingColliders) if(collider!=null) collider.enabled=false;
            for(int i=0;i<6;i++)
            {
                var shape=_restShapes[i]; var collider=_restColliders[i];
                collider.transform.position=(shape.A+shape.B)*.5f;
                var axis=shape.B-shape.A;
                collider.transform.rotation=axis.sqrMagnitude>.0001f ? Quaternion.FromToRotation(Vector3.up,axis.normalized) : Quaternion.identity;
                collider.radius=shape.Radius;
                collider.height=axis.magnitude+shape.Radius*2f;
                collider.enabled=true;
            }
            _restHitPose=true;
        }

        private void AddRestShape(HitLocation location, HumanBodyBones from, HumanBodyBones to, float radius)
        {
            Transform a=_restAnimator.GetBoneTransform(from), b=_restAnimator.GetBoneTransform(to);
            if(a!=null && b!=null) _restShapes.Add(new HitShape(location,a.position,b.position,radius));
        }

        /// <summary>
        /// An enemy attacks with its authored attack weapon and nothing else. While stunned it holds no weapon, which is how
        /// the authority learns that a stun cut a running swing short (it ends a swing whose weapon is no longer in hand).
        /// </summary>
        public WeaponData ActiveWeapon => data != null && !IsStunned && !IsDormant && !IsWaking ? data.attackWeapon : null;

        /// <summary>True while the brain is stunned (see <see cref="EnemyBrain.SetStunned"/>).</summary>
        public bool IsStunned => _brain != null && _brain.IsStunned;

        /// <summary>Stuns or releases this monster (the seam a stun source will use; nothing stuns an enemy yet).</summary>
        public void SetStunned(bool stunned) => _brain?.SetStunned(stunned);
        public bool IsDead => _health != null && _health.IsDead;
        public float CurrentHealth => _health != null ? _health.CurrentHealth : 0f;
        public float MaxHealth => _health != null ? _health.MaxHealth : 0f;
        public EnemyData Data => data;

        /// <summary>R7: combat id of the player this monster is currently targeting (0 = none). Read-only, for tools and tests.</summary>
        public int CurrentTargetCombatId => _currentTargetId;
        public EnemyBrain Brain => _brain;
        public HealthComponent Health => _health;

        /// <summary>True when the last committed swing warned a wedge the player was not inside, so nothing was submitted.</summary>
        public bool LastAttackWhiffed { get; private set; }
        public bool IsWindingUp => _brain != null && _brain.IsWindingUp;
        /// <summary>Authoritative, monotonically increasing count of accepted damage events, for remote flinch presentation.</summary>
        public uint DamageSequence { get; private set; }
        public float WindupSeconds => data != null ? Mathf.Clamp(data.attackWindup, 0f, 10f) : 0f;

        /// <summary>Accepted authority timing when swinging; authored first-step timing during the telegraph.</summary>
        public float AttackSwingSeconds
        {
            get
            {
                if (_swingId != 0 && GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority) &&
                    authority.TryGetMeleeSwingStatus(_swingId, out MeleeSwingStatus status))
                    return status.Timing.Total;
                SwingPatternData pattern = AttackPattern;
                return pattern != null && data != null && data.attackWeapon != null
                    ? data.attackWeapon.ComboTiming(0, pattern).Total : 0f;
            }
        }

        public StatusComponent Status => _status;

        public EnemyAttackPhase AttackPresentation
        {
            get
            {
                if (IsDead) return default;
                float telegraph = Mathf.Max(0.05f, WindupSeconds);
                if (_swingId != 0 && GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority) &&
                    authority.TryGetMeleeSwingStatus(_swingId, out MeleeSwingStatus status) && !status.Finished)
                {
                    SwingTiming timing = status.Timing;
                    if (status.Phase == SwingPhase.Windup)
                        return new EnemyAttackPhase(1, telegraph + status.Elapsed, telegraph + timing.Windup);
                    if (status.Phase == SwingPhase.Active)
                        return new EnemyAttackPhase(2, status.Elapsed - timing.Windup, timing.Active);
                    return EnemyAttackPhase.Recovery(status);
                }
                if (_brain != null && _brain.IsWindingUp)
                {
                    SwingPatternData pattern = AttackPattern;
                    float swingWindup = pattern != null && data.attackWeapon != null
                        ? data.attackWeapon.ComboTiming(0, pattern).Windup : 0f;
                    return new EnemyAttackPhase(1, telegraph - _brain.WindupRemaining, telegraph + swingWindup);
                }
                return default;
            }
        }

        /// <summary>The swing pattern this monster's attack weapon swings (its first pattern), or null for a weapon without one.</summary>
        public SwingPatternData AttackPattern
        {
            get
            {
                WeaponData weapon = data != null ? data.attackWeapon : null;
                return weapon != null && weapon.TryResolveSwingPattern(0, 0, out SwingPatternData pattern) ? pattern : null;
            }
        }

        /// <summary>Radius of the telegraph: the pattern's effective reach from the eye (the legacy reach number for a weapon without a pattern).</summary>
        public float TelegraphReach
        {
            get
            {
                SwingPatternData pattern = AttackPattern;
                if (pattern != null) return pattern.EffectiveReach(data.attackWeapon);
                float reach = data != null && data.attackWeapon != null ? data.attackWeapon.reach + data.attackWeapon.hitSphereRadius : 1f;
                return Mathf.Max(0.6f, Mathf.Max(reach, data != null ? data.attackRange : 1f));
            }
        }

        /// <summary>Half of the telegraph's angle: half the pattern's yaw range (half the authored cone for a weapon without a pattern).</summary>
        public float TelegraphHalfAngle
        {
            get
            {
                SwingPatternData pattern = AttackPattern;
                if (pattern == null) return Mathf.Clamp(data != null ? data.attackTelegraphCone : 120f, 40f, 200f) * 0.5f;
                pattern.YawRange(out float min, out float max);
                return (max - min) * 0.5f;
            }
        }

        /// <summary>Id of the most recent swing the authority accepted from this monster (0 = none yet).</summary>
        public int LastSwingId { get; private set; }

        /// <summary>Swings the authority has accepted from this monster.</summary>
        public int SwingsBegun { get; private set; }

        /// <summary>The swing's eye: the monster's attack origin (root + <see cref="AttackEyeHeight"/>).</summary>
        public Vector3 AttackOrigin => transform.position + Vector3.up * AttackEyeHeight;

        /// <summary>True while a swing of this monster is running on the authority (windup, active or recovery).</summary>
        public bool IsSwinging
        {
            get
            {
                PollSwing();
                return _swingId != 0;
            }
        }

        /// <summary>
        /// The authoritative aim pose of the swing (IAimPoseProvider): the eye at the attack origin, the yaw frozen to the direction
        /// committed at windup start (latched for the whole swing), pitch 0. The body's own turning never enters it.
        /// </summary>
        public bool TryGetAimPose(out Vector3 eye, out float yawDegrees, out float pitchDegrees)
        {
            eye = AttackOrigin;
            Vector3 direction = _swingLatched ? _latchedDirection : TelegraphDirection();
            yawDegrees = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            pitchDegrees = 0f;
            return true;
        }

        /// <summary>Chase speed after whatever the authority has currently done to this monster, e.g. a Battle Cry slow.</summary>
        public float EffectiveChaseSpeed => data != null
            ? data.chaseSpeed * (_status != null ? _status.SpeedMultiplier : 1f)
            : 0f;

        /// <summary>Rebinds the grid before Start when the modular scene assembler installs this raid's nav layout.</summary>
        public void ConfigureNavigationGrid(NavGridData grid)
        {
            navGrid = grid;
            if (_nav != null) _nav = new NavGridPathfinder(navGrid);
        }

        private void Awake()
        {
            _health = GetComponent<HealthComponent>();
            _status = StatusComponent.Ensure(gameObject);
            _combatId = CombatRegistry.AllocateId();
            CombatRegistry.Register(_combatId, this);
            _lookRotation = transform.rotation;
            _bodyHomePosition = bodyTransform != null ? bodyTransform.localPosition : Vector3.zero;
            _hitVolumes = HitVolumeLayout.EnsureHumanoid(gameObject, data != null && data.isElite ? 1.25f : 1f);
        }

        private void Start()
        {
            if (data == null)
            {
                Debug.LogError("[EnemyAgent] No EnemyData assigned; disabling.", this);
                enabled = false;
                return;
            }

            _dropKey = AllocateDropKey(data.enemyId, transform.position);
            _health.Configure(Mathf.Max(1, data.maxHealth), 0f, 0f);
            _health.Died += HandleDeath;
            _health.Damaged += HandleDamaged;

            _nav = new NavGridPathfinder(navGrid);
            _brain = new EnemyBrain(data, _nav, transform.position, config != null ? config.levelMask : Physics.DefaultRaycastLayers, startsDormant: true);
            _lookRotation = transform.rotation;

            // D1c4: the telegraph is the ground projection of the swing pattern (its yaw range at its effective reach). A weapon
            // without a pattern cannot attack any more (the arc-less claw path is closed for pose providers); warn once.
            SwingPatternData attackPattern = AttackPattern;
            if (attackPattern != null)
            {
                attackPattern.YawRange(out float minYaw, out float maxYaw);
                _telegraph = AttackTelegraphVisual.Create(transform, minYaw, maxYaw, data.telegraphColor);
            }
            else
            {
                Debug.LogWarning("[EnemyAgent] " + data.displayName + ": the attack weapon has no swing pattern, so this monster cannot attack.", this);
                _telegraph = AttackTelegraphVisual.Create(transform, data.attackTelegraphCone, data.telegraphColor);
            }
        }

        private void OnDestroy()
        {
            CombatRegistry.Unregister(_combatId, this);
            if (_health != null)
            {
                _health.Died -= HandleDeath;
                _health.Damaged -= HandleDamaged;
            }
        }

        public void ApplyDamage(DamageInfo damage)
        {
            if (_health == null) return;
            _health.ApplyDamage(damage);
        }

        /// <summary>Statuses only ever arrive here, from the combat authority.</summary>
        public void ApplyStatus(StatusSpec spec, int sourceCombatId)
        {
            if (_status == null || IsDead) return;
            _status.ApplyStatus(spec, sourceCombatId);
        }

        public void ClearStatuses()
        {
            if (_status == null) return;
            _status.ClearStatuses();
        }

        public bool HasStatus(StatusKind kind) => _status != null && _status.HasStatus(kind);

        public float GetOutgoingDamageMultiplier(WeaponData weapon) => _status != null ? _status.OutgoingDamageMultiplier : 1f;

        private void HandleDamaged(DamageInfo damage)
        {
            if (DamageSequence != uint.MaxValue) DamageSequence++;
            _brain?.NotifyDamaged(damage);
            _timeSinceDamage = 0f;

            // R7: remember who hurt us last (a player), so aggro can switch to the attacker.
            if (CombatRegistry.TryGet(damage.SourceCombatId, out ICombatEntity source) && EntityKinds.IsPlayer(source.Kind))
            {
                _lastAggressorId = damage.SourceCombatId;
                _aggressorUntil = Time.time + AggressorMemorySeconds;
            }
        }

        private void HandleDeath()
        {
            _brain?.MarkDead();
            if (_restColliders != null) foreach (var collider in _restColliders) collider.enabled=false;
            _swingId = 0;
            _swingLatched = false;
            _status?.ClearStatuses();
            _path.Clear();
            _telegraph?.SetState(false, 0f, Vector3.zero, 1f, false);

            if (bodyTransform != null)
            {
                bodyTransform.localRotation = Quaternion.Euler(80f, 0f, 12f);
                bodyTransform.localPosition += Vector3.up * -0.25f;
            }

            // The kill is announced to the player who landed the last hit; with no known killer it is for everybody.
            int killer = GameEvents.NoPlayer;
            if (_lastAggressorId != 0 && CombatRegistry.TryGet(_lastAggressorId, out ICombatEntity last) && last is IPlayerOwned owner) killer = owner.PlayerId;
            GameEvents.RaiseToast(killer, $"{(data != null ? data.displayName : "Enemy")} slain");

            DropLoot();
            Destroy(gameObject, 6f);
        }

        /// <summary>
        /// R4: the enemy no longer rolls anything. It tells the loot host which enemy died and where, with a stable
        /// key, and the host rolls the enemy's drop table with the raid seed and the raid's issuer.
        /// </summary>
        private void DropLoot()
        {
            if (data == null || data.dropTable == null) return;

            IEnemyDropHost host = lootHost as IEnemyDropHost;
            if (host == null) return;

            host.DropEnemyLoot(data, DropKey, transform.position);
        }

        /// <summary>
        /// Stable identity of this spawn for drop seeding: enemy id plus the spawn point in quarter-metre units. The
        /// same layout gives the same keys in every raid, so (raid seed, key) fixes the drop.
        /// </summary>
        public string DropKey
        {
            get
            {
                if (_dropKey == null) _dropKey = AllocateDropKey(data != null ? data.enemyId : "enemy", transform.position);
                return _dropKey;
            }
        }

        private static readonly Dictionary<string, int> DropKeyCounts = new Dictionary<string, int>();

        /// <summary>Forgets the drop keys handed out so far. Called when a run starts (RunDirector.Awake).</summary>
        public static void ResetDropKeys() => DropKeyCounts.Clear();

        /// <summary>
        /// Allocates a drop key unique within the run: enemy id plus the spawn point in quarter-metre units (invariant
        /// culture), and for the second and later enemy at the same id and cell a "#n" suffix in allocation order. The
        /// same raid seed and spawn order give the same keys; two enemies can never share a key.
        /// </summary>
        public static string AllocateDropKey(string enemyId, Vector3 position)
        {
            string baseKey = string.Format(CultureInfo.InvariantCulture, "{0}@{1},{2}", enemyId,
                Mathf.RoundToInt(position.x * 4f), Mathf.RoundToInt(position.z * 4f));
            DropKeyCounts.TryGetValue(baseKey, out int used);
            DropKeyCounts[baseKey] = used + 1;
            return used == 0 ? baseKey : baseKey + "#" + used.ToString(CultureInfo.InvariantCulture);
        }

        private void Update()
        {
            if (IsDead || data == null) return;

            // Start() may not have run yet (script execution order, activation before initialisation).
            if (_brain == null || _health == null) return;

            float dt = Time.deltaTime;
            _timeSinceDamage += dt;
            _enemyDoorCooldown = Mathf.Max(0f, _enemyDoorCooldown - dt);

            float perceptionRadius = Mathf.Max(data.sightRange, data.hearingRange) + 6f;
            // R7: the nearest living, in-play player this monster is hostile to (any party, any controller). The current
            // target keeps the choice unless a rival is clearly closer, and whoever hurt us last within the memory window
            // is preferred, so aggro switches sensibly instead of flickering between equidistant players.
            int preferred = Time.time < _aggressorUntil ? _lastAggressorId : _currentTargetId;
            CombatRegistry.TryGetNearestHostilePlayer(this, transform.position, perceptionRadius, preferred, TargetSwitchFactor, out ICombatEntity target);
            _currentTargetId = target != null ? target.CombatId : 0;

            PollSwing();

            BrainOrder order = _brain.Tick(dt, Time.time, transform.position, transform.forward, target);

            // D1c4: the windup was the telegraph; its end begins the authority's swept swing along the committed direction.
            // Whether it lands is the sweep's business (arc, reach, walls), not a wedge check made here.
            if (order.RequestAttack && target != null) TryBeginAttack(TelegraphDirection(), out _);

            RotateTowards(order.FaceTowards, dt);

            // A monster that has begun its swing stands its ground until the swing is over: the eye the sweep follows stays put.
            if (_swingId == 0) Move(order, dt);
            PlayTelegraph();
        }

        private void RotateTowards(Vector3 faceTowards, float dt)
        {
            Vector3 delta = faceTowards - transform.position;
            delta.y = 0f;

            if (delta.sqrMagnitude > 0.04f)
            {
                Quaternion target = Quaternion.LookRotation(delta.normalized);
                _lookRotation = Quaternion.RotateTowards(_lookRotation, target, Mathf.Max(30f, data.turnRate) * dt);
                transform.rotation = _lookRotation;
            }
        }

        private void Move(BrainOrder order, float dt)
        {
            if (order.Speed <= 0.01f) return;

            Vector3 destination = order.MoveTo;

            if (ShouldRepath(destination)) RequestPath(destination);
            // A blocked terrace path is not permission to beeline up its vertical edge.
            if (_nav != null && _nav.HasSurfaceHeights && _path.Count == 0) return;

            Vector3 steer = ChooseSteerPoint(destination);
            Vector3 flat = new Vector3(steer.x - transform.position.x, 0f, steer.z - transform.position.z);

            if (flat.sqrMagnitude < 0.01f) return;

            // The brain asked for its own speed; whatever the authority has currently done to this monster
            // (a Battle Cry slow, for example) is applied here rather than inside the decision making.
            float statusMultiplier = _status != null ? _status.SpeedMultiplier : 1f;
            float step = Mathf.Min(order.Speed, 12f) * statusMultiplier * dt;
            if (step <= 0.0001f) return;

            Vector3 movement = flat.normalized * step;
            Vector3 requestedMovement = movement;
            int levelMask = config != null && config.levelMask.value != 0
                ? config.levelMask.value : Physics.DefaultRaycastLayers;
            movement = DoorwayMovementBlocker.ClampDelta(transform.position, movement, 1.0f, 0.22f,
                levelMask, _doorMoveHits, _doorOverlapHits, out DungeonDoorInteractable blockingDoor);
            if (blockingDoor != null && movement.sqrMagnitude < requestedMovement.sqrMagnitude * 0.25f)
                TryOpenDoorForCurrentTarget(blockingDoor);
            if (movement.sqrMagnitude <= 0.000001f) return;

            Vector3 next = transform.position + movement;
            if (_nav != null && _nav.HasSurfaceHeights)
            {
                if (!NavGridSurface.TryStep(transform.position, next, levelMask, out next)) return;
            }
            else next.y = groundHeight;
            transform.position = next;

            // Colliders are only moved with transforms on the next physics step. Combat queries run in
            // Update, so sync now or a raycast/overlap this frame would use last frame's position.
            Physics.SyncTransforms();
        }

        private void TryOpenDoorForCurrentTarget(DungeonDoorInteractable door)
        {
            if (door == null || !NetRole.RunsSimulation || !CombatRegistry.TryGet(_currentTargetId, out ICombatEntity target) ||
                target == null || target.IsDead || !EntityKinds.IsPlayer(target.Kind)) return;

            Vector3 delta = door.transform.position - transform.position;
            delta.y = 0f;
            if (!DoorwayMovementBlocker.ShouldEnemyOpenDoor(true, delta.magnitude, _enemyDoorCooldown)) return;

            // Throttle attempts even if the network service is not ready yet; no client is allowed to mutate door state.
            _enemyDoorCooldown = 1.25f;
            if (NetRole.IsOffline)
            {
                door.ApplyReplicatedState(door.DoorId, true);
                return;
            }

            if (GameServices.TryGet<IDoorAiAuthority>(out IDoorAiAuthority authority))
                authority.RequestEnemyDoorOpen(door.DoorId);
        }

        private bool ShouldRepath(Vector3 destination)
        {
            if (_path.Count == 0) return true;
            _repathTimer -= Time.deltaTime;
            if (_repathTimer <= 0f) return true;
            return (destination - _lastPathRequest).sqrMagnitude > 2.25f;
        }

        private void RequestPath(Vector3 destination)
        {
            _lastPathRequest = destination;
            _repathTimer = Mathf.Max(0.15f, repathInterval);

            if (_nav == null || !_nav.HasGrid)
            {
                _path.Clear();
                _path.Add(destination);
                _pathIndex = 0;
                return;
            }

            if (_nav.TryFindPath(transform.position, destination, _path))
            {
                _pathIndex = _path.Count > 1 ? 1 : 0;
            }
            else
            {
                _path.Clear();
            }
        }

        private Vector3 ChooseSteerPoint(Vector3 destination)
        {
            while (_pathIndex < _path.Count &&
                   HorizontalDistance(transform.position, _path[_pathIndex]) < waypointTolerance)
            {
                _pathIndex++;
            }

            if (_pathIndex < _path.Count) return _path[_pathIndex];
            return destination;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            Vector3 delta = a - b;
            delta.y = 0f;
            return delta.magnitude;
        }

        private void PlayTelegraph()
        {
            if (_brain == null) return;

            float progress = _brain.WindupProgress;
            float height = Mathf.Max(0.05f, data.attackTelegraphHeight);
            if (bodyTransform != null) bodyTransform.localPosition = _bodyHomePosition + Vector3.up * (progress * height);

            if (_telegraph == null) return;

            if (_whiffTimer > 0f) _whiffTimer -= Time.deltaTime;
            bool whiffed = _whiffTimer > 0f;
            bool winding = _brain.IsWindingUp;

            // The wedge stays lit through the windup and the swing's own windup and active phases, then flashes when the swing found nothing.
            bool striking = _swingId != 0 && _swingPhase != SwingPhase.Recovery;
            _telegraph.SetState(winding || striking || whiffed,
                winding ? progress : 1f,
                _swingLatched ? _latchedDirection : TelegraphDirection(),
                TelegraphReach,
                whiffed && !winding && !striking);
        }

        /// <summary>Ground-plane direction the brain committed at the start of the windup.</summary>
        private Vector3 TelegraphDirection()
        {
            Vector3 direction = _brain != null ? _brain.TelegraphDirection : Vector3.zero;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.0001f) direction = transform.forward;
            return direction.sqrMagnitude < 0.0001f ? Vector3.forward : direction.normalized;
        }

        /// <summary>
        /// D1c4: asks the authority to begin this monster's swept swing along <paramref name="flatDirection"/> (zero = the committed
        /// telegraph direction). The brain calls it when the windup ends; gates and tools use it to swing a frozen agent. The
        /// pose, the weapon and the pattern are the agent's own and the authority validates everything (alive, weapon in hand,
        /// cooldown on its clock, no swing running, aim consistent); nothing about hits or damage is decided here.
        /// </summary>
        public bool TryBeginAttack(Vector3 flatDirection, out string reason)
        {
            reason = string.Empty;
            if (IsDead) { reason = "dead"; return false; }

            WeaponData weapon = ActiveWeapon;
            if (weapon == null) { reason = "no attack weapon in hand"; return false; }
            if (!weapon.UsesSweptMelee) { reason = "the attack weapon has no swing pattern"; return false; }
            if (!GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority)) { reason = "no combat authority"; return false; }
            if (IsSwinging) { reason = "a swing is already running"; return false; }

            flatDirection.y = 0f;
            _latchedDirection = flatDirection.sqrMagnitude < 0.0001f ? TelegraphDirection() : flatDirection.normalized;
            _swingLatched = true;
            TryGetAimPose(out _, out float yaw, out float pitch);

            // Same per-combat-id allocator the player's components use, so an enemy that ever carries two attack sources
            // cannot collide with itself.
            int sequence = CombatRegistry.NextSequence(_combatId);
            MeleeSwingIntent intent = new MeleeSwingIntent(_combatId, weapon.weaponId, 0, 0, yaw, pitch, sequence);

            if (!authority.TryBeginMeleeSwing(intent, out int swingId, out reason))
            {
                _swingLatched = false;
                LastAttackWhiffed = true;
                _whiffTimer = 0.22f;
                return false;
            }

            _swingId = swingId;
            _swingResolved = false;
            _swingPhase = SwingPhase.Windup;
            LastSwingId = swingId;
            SwingsBegun++;
            LastAttackWhiffed = false;
            return true;
        }

        /// <summary>
        /// Reads the authority's state of the running swing. When its active phase is over (or the swing was stopped by a wall)
        /// with no hit, the attack counts as a whiff (flash + <see cref="LastAttackWhiffed"/>); a swing that ended abnormally (the
        /// monster died or lost its weapon) is not a whiff. The swing is forgotten once the authority calls it finished.
        /// </summary>
        private void PollSwing()
        {
            if (_swingId == 0) return;

            if (!GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority)
                || !authority.TryGetMeleeSwingStatus(_swingId, out MeleeSwingStatus status))
            {
                _swingId = 0;
                _swingLatched = false;
                return;
            }

            _swingPhase = status.Phase;

            if (!_swingResolved && (status.Phase == SwingPhase.Recovery || status.Finished))
            {
                _swingResolved = true;
                bool aborted = status.Finished && status.EndReason != SwingEndReason.Completed && status.EndReason != SwingEndReason.WallStop;
                if (status.Hits.Count == 0 && !aborted)
                {
                    LastAttackWhiffed = true;
                    _whiffTimer = 0.22f;
                }
            }

            if (status.Finished)
            {
                _swingId = 0;
                _swingLatched = false;
            }
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(transform.position + Vector3.up * 1.15f, 0.4f);

            if (data == null) return;
            Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.35f);
            Gizmos.DrawWireSphere(transform.position, data.sightRange);
        }
    }
}
