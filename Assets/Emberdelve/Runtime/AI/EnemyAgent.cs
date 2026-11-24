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
        private Emberdelve.Player.SwingTrail _attackLine;
        private bool _attackLineShown;
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

        // Attack variant (index into the attack weapon's swing patterns). Chosen on the authority side when a windup starts, so the
        // telegraph shows the blow that will really come; frozen into _swingVariant when the swing begins.
        private int _pendingVariant;
        private int _swingVariant;
        private bool _wasWindingUp;
        private System.Random _variantRandom;
        private int _telegraphVariant;

        /// <summary>Height above the root of the swing's eye: the origin the attack has always been aimed from.</summary>
        public const float AttackEyeHeight = 1.2f;

        public EntityKind Kind => EntityKind.Enemy;
        public int CombatId => _combatId;
        public int TeamId => CombatTeams.MonsterTeam;
        public bool UsesCinderClub => data != null && data.IsCinderWarden;
        public Vector3 CombatCenter => _restHitPose && _restShapes.Count > 1
            ? (_restShapes[1].A + _restShapes[1].B) * .5f
            : transform.position + Vector3.up * (UsesCinderClub ? EnemyData.CinderWardenCenterHeight : 1.15f);
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
            if (_hitVolumes == null) _hitVolumes = HitVolumeLayout.EnsureHumanoid(gameObject, data != null ? data.BodyScale : 1f);
            if (_restHitPose) { shapes.AddRange(_restShapes); return; }
            HitVolumeLayout.Collect(_hitVolumes, shapes);
        }

        // The visual is lying on the floor, so both ranged colliders and swept hit shapes must follow it.
        // Normal upright collision stays untouched and is restored after the rise.
        public void RefreshDormantHitPose(Animator posedAnimator)
        {
            if (IsDead) return; // the body's colliders were switched off at death and must stay off
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

        /// <summary>A bowman: its attack weapon is ranged, so it draws and looses arrows through the projectile authority instead of swinging.</summary>
        public bool IsArcher => data != null && data.IsArcher;

        /// <summary>Arrows this archer has loosed that the authority accepted.</summary>
        public int ArrowsLoosed { get; private set; }

        /// <summary>Why the last shot was refused (empty when it was accepted).</summary>
        public string LastShotRejectReason { get; private set; } = string.Empty;

        /// <summary>Half-width (degrees) of the archer's ground strip telegraph along the aim.</summary>
        public const float ArcherStripHalfAngle = 2.5f;

        // Archer state: where the draw is aimed (the target's chest at the end of the draw) and when the last arrow left the bow.
        private Vector3 _aimPoint;
        private bool _hasAimPoint;
        private float _shotAt = -100f;
        private int _drawTargetId;

        /// <summary>The widest angle (degrees) between where the archer faces and where it looses; beyond it the shot is refused.</summary>
        public const float ArcherMaxShotOffFacing = 40f;

        /// <summary>Accepted authority timing when swinging; authored first-step timing during the telegraph.</summary>
        public float AttackSwingSeconds
        {
            get
            {
                if (IsArcher) return EnemyAttackPhase.ArcherReleaseSeconds + EnemyAttackPhase.ArcherRecoverySeconds;
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
                if (IsArcher) return ArcherPresentation();
                float telegraph = Mathf.Max(0.05f, WindupSeconds);
                if (_swingId != 0 && GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority) &&
                    authority.TryGetMeleeSwingStatus(_swingId, out MeleeSwingStatus status) && !status.Finished)
                {
                    SwingTiming timing = status.Timing;
                    if (status.Phase == SwingPhase.Windup)
                        return new EnemyAttackPhase(1, telegraph + status.Elapsed, telegraph + timing.Windup);
                    if (status.Phase == SwingPhase.Active)
                        return new EnemyAttackPhase(2, status.Elapsed - timing.Windup, timing.Active);
                    return EnemyAttackPhase.Recovery(status, UsesCinderClub, AttackVariantIndex);
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
        public SwingPatternData AttackPattern => AttackPatternAt(AttackVariantIndex);

        /// <summary>Number of attacks this monster can choose between (the attack weapon's patterns; the Cinder Warden's club has one).</summary>
        public int AttackVariantCount
        {
            get
            {
                WeaponData weapon = data != null ? data.attackWeapon : null;
                if (weapon == null || weapon.swingPatterns == null || UsesCinderClub) return 1;
                return Mathf.Max(1, weapon.swingPatterns.Length);
            }
        }

        /// <summary>The attack of the swing in flight, else the one the current/next windup warns (0 = the original attack).</summary>
        public int AttackVariantIndex => _swingId != 0 ? _swingVariant : _pendingVariant;

        /// <summary>Pattern index <paramref name="index"/> of the attack weapon, or null when the weapon does not allow it.</summary>
        public SwingPatternData AttackPatternAt(int index)
        {
            WeaponData weapon = data != null ? data.attackWeapon : null;
            return weapon != null && weapon.TryResolveSwingPattern(0, index, out SwingPatternData pattern) ? pattern : null;
        }

        private void PickAttackVariant()
        {
            if (_variantRandom == null) _variantRandom = new System.Random(unchecked(System.Environment.TickCount * 397 + _combatId * 7919));
            _pendingVariant = UsesCinderClub ? 0 : EnemyAttackChoice.PickFor(data != null ? data.attackWeapon : null, _variantRandom);
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
                GetTelegraphWedge(AttackVariantIndex, out float min, out float max);
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
            if (IsArcher && _hasAimPoint)
            {
                // The bow is aimed at the target's chest as seen at the end of the draw, pitched up for the arrow's drop.
                Vector3 toAim = _aimPoint - eye;
                yawDegrees = Mathf.Atan2(toAim.x, toAim.z) * Mathf.Rad2Deg;
                WeaponData bow = data.attackWeapon;
                TryBallisticPitch(eye, _aimPoint, bow != null ? bow.projectileSpeed : 30f, ArrowGravity(bow), out pitchDegrees);
                return true;
            }
            Vector3 direction = _swingLatched ? _latchedDirection : TelegraphDirection();
            yawDegrees = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            if (UsesCinderClub) yawDegrees = Mathf.DeltaAngle(0f, yawDegrees + EnemyAttackPhase.ClubAimYaw);
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

        /// <summary>Creates a fresh monster under its floor with all wiring assigned before Awake/Start.</summary>
        public static EnemyAgent CreateForFloor(EnemyData definition, NavGridData grid, GameConfig gameConfig,
            MonoBehaviour host, Transform parent, Vector3 position, GameObject visualPrefab, string objectName)
        {
            if (NetRole.IsClientOnly || definition == null || grid == null || !(host is IEnemyDropHost)
                || parent == null || visualPrefab == null) return null;
            var root = new GameObject(objectName);
            root.SetActive(false);
            root.transform.SetParent(parent, false);
            root.transform.position = position;
            int layer = LayerMask.NameToLayer(GameConfig.EnemyLayerName);
            if (layer >= 0) root.layer = layer;
            var agent = root.AddComponent<EnemyAgent>();
            agent.data = definition; agent.navGrid = grid; agent.config = gameConfig; agent.lootHost = host;
            agent.groundHeight = position.y;
            float scale = definition.BodyScale;
            var body = new GameObject("Body");
            body.transform.SetParent(root.transform, false); body.layer = root.layer;
            body.transform.localPosition = Vector3.up * (.95f * scale);
            body.AddComponent<BoxCollider>().size = new Vector3(.78f, 1.45f, .58f) * scale;
            agent.bodyTransform = body.transform;
            var head = new GameObject("Head");
            head.transform.SetParent(root.transform, false); head.layer = root.layer;
            head.transform.localPosition = Vector3.up * (1.85f * scale);
            var headBox = head.AddComponent<BoxCollider>(); headBox.size = Vector3.one * (.44f * scale);
            agent.headCollider = headBox;
            var visual = Instantiate(visualPrefab, root.transform, false);
            visual.name = "Visual";
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one * (definition.IsCinderWarden ? 1f : scale);
            root.SetActive(true);
            return agent;
        }

        private void Awake()
        {
            if (_health != null) return;
            _health = GetComponent<HealthComponent>();
            _status = StatusComponent.Ensure(gameObject);
            _combatId = CombatRegistry.AllocateId();
            CombatRegistry.Register(_combatId, this);
            _lookRotation = transform.rotation;
            _bodyHomePosition = bodyTransform != null ? bodyTransform.localPosition : Vector3.zero;
            if (data != null) _hitVolumes = HitVolumeLayout.EnsureHumanoid(gameObject, data.BodyScale);
        }

        private void Start()
        {
            if (!EnsureRuntimeInitialized())
            {
                Debug.LogError("[EnemyAgent] No EnemyData assigned; disabling.", this);
                enabled = false;
            }
        }

        public bool IsRuntimeInitialized { get; private set; }
        public bool EnsureRuntimeInitialized()
        {
            if (IsRuntimeInitialized) return true;
            if (data == null || !gameObject.activeInHierarchy) return false;
            if (_health == null) Awake();

            _dropKey = AllocateDropKey(data.enemyId, transform.position);
            _health.Configure(Mathf.Max(1, data.maxHealth), 0f, 0f);
            _health.Died += HandleDeath;
            _health.Damaged += HandleDamaged;

            _nav = new NavGridPathfinder(navGrid);
            _brain = new EnemyBrain(data, _nav, transform.position, config != null ? config.levelMask : Physics.DefaultRaycastLayers,
                startsDormant: !UsesCinderClub);
            _lookRotation = transform.rotation;

            // D1c4: the telegraph is the ground projection of the swing pattern (its yaw range at its effective reach). A weapon
            // without a pattern cannot attack any more (the arc-less claw path is closed for pose providers); warn once.
            if (!IsArcher && AttackPatternAt(0) == null)
                Debug.LogWarning("[EnemyAgent] " + data.displayName + ": the attack weapon has no swing pattern, so this monster cannot attack.", this);
            ApplyTelegraph(0);
            IsRuntimeInitialized = true;
            return true;
        }

        /// <summary>
        /// Shows the ground wedge of attack variant <paramref name="variant"/> on the one telegraph visual (its mesh is rebuilt in place only when
        /// the range changes). The Cinder Warden keeps its legacy cone and never changes.
        /// </summary>
        private void ApplyTelegraph(int variant)
        {
            _telegraphVariant = variant;
            float minYaw, maxYaw;
            if (IsArcher)
            {
                // An archer warns with a narrow strip along its aim (its length follows the target), lit through the draw.
                minYaw = -ArcherStripHalfAngle; maxYaw = ArcherStripHalfAngle;
            }
            else if (UsesCinderClub || AttackPatternAt(variant) == null)
            {
                float half = Mathf.Clamp(data.attackTelegraphCone, 40f, 200f) * 0.5f;
                minYaw = -half; maxYaw = half;
            }
            else GetTelegraphWedge(variant, out minYaw, out maxYaw);

            if (_telegraph == null) _telegraph = AttackTelegraphVisual.Create(transform, minYaw, maxYaw, data.telegraphColor);
            else _telegraph.SetWedge(minYaw, maxYaw);
        }

        /// <summary>Margin (degrees) added to the overhead strip beyond the farthest lateral tip of the baked sweep, and its clamp.</summary>
        public const float OverheadStripMargin = 6f, OverheadStripMin = 12f, OverheadStripMax = 40f;

        /// <summary>
        /// The warned wedge of attack <paramref name="variant"/>: the pattern's blade yaw range. The Servitor's vertical overhead (variant 1; never
        /// the Cinder Warden's club, which keeps its raw yaw range) is a narrow strip along the facing instead, because a near-vertical blade's yaw
        /// is meaningless (it reads 60 degrees off at the top); its half angle covers the farthest lateral tip of the baked sweep plus a margin.
        /// </summary>
        public void GetTelegraphWedge(int variant, out float minYaw, out float maxYaw)
        {
            SwingPatternData pattern = AttackPatternAt(variant);
            if (pattern == null)
            {
                float half = Mathf.Clamp(data != null ? data.attackTelegraphCone : 120f, 40f, 200f) * 0.5f;
                minYaw = -half; maxYaw = half;
                return;
            }
            if (!UsesCinderClub && variant == EnemyAttackPhase.OverheadVariant)
            {
                float strip = OverheadStripHalfAngle(pattern, data.attackWeapon);
                minYaw = -strip; maxYaw = strip;
                return;
            }
            pattern.YawRange(out minYaw, out maxYaw);
        }

        /// <summary>Half angle of the overhead strip: the largest lateral angle of the sweep's blade tips (those in front of the eye) plus a margin, clamped.</summary>
        public static float OverheadStripHalfAngle(SwingPatternData pattern, WeaponData weapon)
        {
            float widest = 0f;
            if (pattern != null && weapon != null && pattern.keys != null)
            {
                float length = pattern.BladeLength(weapon);
                for (int i = 0; i < pattern.keys.Length; i++)
                {
                    SwingKey key = pattern.keys[i];
                    Vector3 tip = key.basePos + Quaternion.Euler(key.bladePitch, key.bladeYaw, 0f) * Vector3.forward * length;
                    if (tip.z < 0.3f) continue;
                    widest = Mathf.Max(widest, Mathf.Atan2(Mathf.Abs(tip.x), tip.z) * Mathf.Rad2Deg);
                }
            }
            return Mathf.Clamp(widest + OverheadStripMargin, OverheadStripMin, OverheadStripMax);
        }

        private void OnDestroy()
        {
            if (_attackLine != null) Destroy(_attackLine.gameObject);
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
            // A corpse is scenery: nothing may block movement, shots or interaction rays while it lies there.
            foreach (var collider in GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            _swingId = 0;
            _swingLatched = false;
            _status?.ClearStatuses();
            _path.Clear();
            _telegraph?.SetState(false, 0f, Vector3.zero, 1f, false);
            _attackLine?.EndStroke();
            _attackLineShown = false;

            if (bodyTransform != null)
            {
                bodyTransform.localRotation = Quaternion.Euler(80f, 0f, 12f);
                bodyTransform.localPosition += Vector3.up * -0.25f;
            }

            // The kill is announced to the player who landed the last hit; with no known killer it is for everybody.
            int killer = GameEvents.NoPlayer;
            if (_lastAggressorId != 0 && CombatRegistry.TryGet(_lastAggressorId, out ICombatEntity last) && last is IPlayerOwned owner) killer = owner.PlayerId;
            GameEvents.RaiseToast(killer, $"{(data != null ? data.displayName : "Enemy")} slain");

            // The body stays on the floor for the rest of the raid; it is searchable when it dropped loot.
            DropLoot();
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

            // A new windup picks the attack it will deliver (authority side only; this component does not exist on pure clients).
            bool windingNow = _brain.IsWindingUp;
            if (windingNow && !_wasWindingUp) PickAttackVariant();
            _wasWindingUp = windingNow;
            if (_telegraph != null && _telegraphVariant != AttackVariantIndex && (windingNow || _swingId != 0)) ApplyTelegraph(AttackVariantIndex);

            // D1c4: the windup was the telegraph; its end begins the authority's swept swing along the committed direction.
            // Whether it lands is the sweep's business (arc, reach, walls), not a wedge check made here.
            if (IsArcher)
            {
                // The draw belongs to the target it started on: it tracks that target, and the arrow flies at where its chest was when the
                // string is released. If the brain switched targets mid-draw (an aggressor), the release looses nothing.
                int targetId = target != null ? target.CombatId : 0;
                if (windingNow && _drawTargetId == 0) _drawTargetId = targetId;
                bool drawnOnThis = target != null && targetId == _drawTargetId;
                if ((windingNow || order.RequestAttack) && drawnOnThis) { _aimPoint = order.FaceTowards; _hasAimPoint = true; }
                if (order.RequestAttack && target != null)
                {
                    if (drawnOnThis) TryShootArrow(out _);
                    else LastShotRejectReason = "the target changed during the draw";
                }
                if (!windingNow) _drawTargetId = 0;
            }
            else if (order.RequestAttack && target != null) TryBeginAttack(TelegraphDirection(), out _);

            RotateTowards(order.FaceTowards, dt);

            // A monster that has begun its swing stands its ground until the swing is over: the eye the sweep follows stays put.
            if (_swingId == 0) Move(order, dt);
            PlayTelegraph();
        }

        private void RotateTowards(Vector3 faceTowards, float dt)
        {
            if (UsesCinderClub && (_swingLatched || IsWindingUp))
            {
                TryGetAimPose(out _, out float yaw, out _);
                Quaternion committed = Quaternion.Euler(0f, yaw, 0f);
                _lookRotation = _swingLatched ? committed : Quaternion.RotateTowards(_lookRotation, committed, data.turnRate * dt);
                transform.rotation = _lookRotation;
                return;
            }
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
            if (door == null || !door.AllowsEnemyOpening || !NetRole.RunsSimulation || !CombatRegistry.TryGet(_currentTargetId, out ICombatEntity target) ||
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

            UpdateAttackLine(_brain.IsWindingUp || (_swingId != 0 && _swingPhase != SwingPhase.Recovery));

            if (_telegraph == null) return;

            if (_whiffTimer > 0f) _whiffTimer -= Time.deltaTime;
            bool whiffed = _whiffTimer > 0f;
            bool winding = _brain.IsWindingUp;

            // The wedge stays lit through the windup and the swing's own windup and active phases, then flashes when the swing found nothing.
            bool striking = _swingId != 0 && _swingPhase != SwingPhase.Recovery;
            if (IsArcher)
            {
                float length = _hasAimPoint ? new Vector2(_aimPoint.x - transform.position.x, _aimPoint.z - transform.position.z).magnitude : 0f;
                _telegraph.SetState(winding && _hasAimPoint, progress, TelegraphDirection(), Mathf.Clamp(length, 1f, 40f), false);
                return;
            }
            _telegraph.SetState(winding || striking || whiffed,
                winding ? progress : 1f,
                _swingLatched ? _latchedDirection : TelegraphDirection(),
                TelegraphReach,
                whiffed && !winding && !striking);
        }

        /// <summary>
        /// The enemy's own red attack line, the counterpart of the player's amber swing ribbon: the committed blade path of the swing
        /// (the very path the authority sweeps, from <see cref="BladeWarning"/>) is drawn in the world from the start of the windup until
        /// the active phase ends, so the direction of the blow (side, overhead, thrust) can be read before it lands.
        /// </summary>
        private void UpdateAttackLine(bool show)
        {
            if (!show)
            {
                if (_attackLineShown) { _attackLine?.EndStroke(); _attackLineShown = false; }
                return;
            }

            if (_attackLineShown) return;
            WeaponData weapon = data != null ? data.attackWeapon : null;
            SwingPatternData pattern = AttackPattern;
            if (weapon == null || pattern == null || !TryGetAimPose(out Vector3 eye, out float yaw, out float pitch)) return;

            BladeWarning warning = BladeWarning.Describe(pattern, weapon, eye, yaw, pitch, null, 16);
            if (!warning.IsValid) return;
            if (_attackLine == null)
            {
                _attackLine = Emberdelve.Player.SwingTrail.Create();
                _attackLine.Tint = new Color(1f, 0.12f, 0.08f);
                _attackLine.PeakAlpha = 0.6f;
            }

            _attackLine.Begin(pattern.trailMaterial);
            for (int s = 0; s <= warning.SampleCount; s++)
            {
                warning.SegmentAt(s, out Vector3 b, out Vector3 t);
                _attackLine.Add(Vector3.Lerp(b, t, 0.7f), t);
            }

            _attackLineShown = true;
        }

        private void OnDisable()
        {
            if (_attackLine != null) _attackLine.EndStroke();
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
        // ------------------------------------------------------------------ archer (ranged undead)

        /// <summary>The archer's presentation clock: phase 1 = the brain's draw (windup), 2 = the release, 3 = lowering the bow.</summary>
        private EnemyAttackPhase ArcherPresentation()
        {
            if (_brain != null && _brain.IsWindingUp)
            {
                float windup = Mathf.Max(0.05f, WindupSeconds);
                return new EnemyAttackPhase(1, windup - _brain.WindupRemaining, windup);
            }
            float since = Time.time - _shotAt;
            if (since >= 0f && since < EnemyAttackPhase.ArcherReleaseSeconds)
                return new EnemyAttackPhase(2, since, EnemyAttackPhase.ArcherReleaseSeconds);
            since -= EnemyAttackPhase.ArcherReleaseSeconds;
            if (since >= 0f && since < EnemyAttackPhase.ArcherRecoverySeconds)
                return new EnemyAttackPhase(3, since, EnemyAttackPhase.ArcherRecoverySeconds);
            return default;
        }

        /// <summary>The gravity the authority will fly this bow's arrow with (zero when it simulates straight flights).</summary>
        private static float ArrowGravity(WeaponData bow)
        {
            if (bow == null) return 0f;
            if (GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority) && authority is LocalCombatAuthority local && !local.SimulateBallistics)
                return 0f;
            return Mathf.Clamp(bow.projectileGravity, 0f, 100f);
        }

        /// <summary>
        /// Pitch (degrees, positive down, the aim-pose convention) of the flatter launch angle that carries a projectile of
        /// <paramref name="speed"/> under <paramref name="gravity"/> from <paramref name="from"/> through <paramref name="to"/>. False when the
        /// point is out of ballistic reach (the pitch is then the straight line, which falls short).
        /// </summary>
        public static bool TryBallisticPitch(Vector3 from, Vector3 to, float speed, float gravity, out float pitchDegrees)
        {
            Vector3 delta = to - from;
            float dx = new Vector2(delta.x, delta.z).magnitude;
            float dy = delta.y;
            float straight = -Mathf.Atan2(dy, Mathf.Max(dx, 1e-4f)) * Mathf.Rad2Deg;
            if (gravity <= 1e-4f || dx < 0.05f || speed <= 0.1f) { pitchDegrees = straight; return true; }
            float v2 = speed * speed;
            float discriminant = v2 * v2 - gravity * (gravity * dx * dx + 2f * dy * v2);
            if (discriminant < 0f) { pitchDegrees = straight; return false; }
            float tangent = (v2 - Mathf.Sqrt(discriminant)) / (gravity * dx);
            pitchDegrees = -Mathf.Atan(tangent) * Mathf.Rad2Deg;
            return true;
        }

        /// <summary>
        /// Looses one arrow at the aim point through the authority (the same validated <see cref="ICombatAuthority.LaunchProjectile"/> a player's
        /// bow uses: alive, ranged weapon in hand, cooldown on the authority clock, origin at this monster's eye, direction equal to its own aim
        /// pose, muzzle not inside a wall). The flight, hit location, shields and walls are the authority's; nothing is decided here.
        /// </summary>
        public bool TryShootArrow(out string reason)
        {
            reason = string.Empty;
            if (IsDead) { reason = "dead"; return false; }
            WeaponData bow = ActiveWeapon;
            if (bow == null || !bow.IsRanged) { reason = "no ranged weapon in hand"; return false; }
            if (!_hasAimPoint) { reason = "no aim point"; return false; }
            if (!GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority)) { reason = "no combat authority"; return false; }
            if (!TryGetAimPose(out Vector3 eye, out float yaw, out float pitch)) { reason = "no aim pose"; return false; }

            Vector3 facing = transform.forward;
            if (Mathf.Abs(Mathf.DeltaAngle(Mathf.Atan2(facing.x, facing.z) * Mathf.Rad2Deg, yaw)) > ArcherMaxShotOffFacing)
            { reason = "the aim is not in front of the archer"; LastShotRejectReason = reason; return false; }

            Vector3 direction = Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward;
            int sequence = CombatRegistry.NextSequence(_combatId);
            AttackIntent intent = new AttackIntent(_combatId, Kind, bow, 1f, eye, direction, Mathf.Max(1f, bow.rangedRange), sequence, Time.time);
            if (!authority.LaunchProjectile(intent))
            {
                reason = authority is LocalCombatAuthority local && !string.IsNullOrEmpty(local.LastAttackRejectReason)
                    ? local.LastAttackRejectReason : "the authority refused the arrow";
                LastShotRejectReason = reason;
                return false;
            }

            _shotAt = Time.time;
            ArrowsLoosed++;
            SwingsBegun++;
            LastShotRejectReason = string.Empty;
            return true;
        }

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
            if (!EnemyAttackChoice.IsValid(_pendingVariant, AttackVariantCount)) _pendingVariant = 0;
            _swingLatched = true;
            TryGetAimPose(out _, out float yaw, out float pitch);

            // Same per-combat-id allocator the player's components use, so an enemy that ever carries two attack sources
            // cannot collide with itself.
            int sequence = CombatRegistry.NextSequence(_combatId);
            // The pattern index is the server brain's choice; the authority validates it against the weapon's patterns.
            int variant = _pendingVariant;
            MeleeSwingIntent intent = new MeleeSwingIntent(_combatId, weapon.weaponId, variant, 0, yaw, pitch, sequence);

            if (!authority.TryBeginMeleeSwing(intent, out int swingId, out reason))
            {
                _swingLatched = false;
                LastAttackWhiffed = true;
                _whiffTimer = 0.22f;
                return false;
            }

            _swingId = swingId;
            _swingVariant = variant;
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
