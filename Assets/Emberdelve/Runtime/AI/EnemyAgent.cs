using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Combat;
using Emberdelve.Data;
using Emberdelve.Items;

namespace Emberdelve.AI
{
    /// <summary>
    /// Scene object for one monster. Owns identity (combat id), health and movement; all decisions
    /// come from EnemyBrain and all damage goes through the combat authority, exactly like the player.
    /// Movement is grid-steered (transform based) rather than CharacterController based, so the body
    /// and head hit colliders can stay plain colliders on child objects.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(HealthComponent))]
    public sealed class EnemyAgent : MonoBehaviour, ICombatEntity, IStatusTarget, IDamageOutgoingProvider, IActiveWeaponProvider
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
        private readonly List<Vector3> _path = new List<Vector3>(64);
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
        private string _dropKey;

        public EntityKind Kind => EntityKind.Enemy;
        public int CombatId => _combatId;
        public int TeamId => CombatTeams.MonsterTeam;
        public Vector3 CombatCenter => transform.position + Vector3.up * 1.15f;
        public Collider HeadCollider => headCollider;

        /// <summary>An enemy attacks with its authored attack weapon and nothing else.</summary>
        public WeaponData ActiveWeapon => data != null ? data.attackWeapon : null;
        public bool IsDead => _health != null && _health.IsDead;
        public float CurrentHealth => _health != null ? _health.CurrentHealth : 0f;
        public float MaxHealth => _health != null ? _health.MaxHealth : 0f;
        public EnemyData Data => data;
        public EnemyBrain Brain => _brain;
        public HealthComponent Health => _health;

        /// <summary>True when the last committed swing warned a wedge the player was not inside, so nothing was submitted.</summary>
        public bool LastAttackWhiffed { get; private set; }
        public bool IsWindingUp => _brain != null && _brain.IsWindingUp;

        public StatusComponent Status => _status;

        /// <summary>Chase speed after whatever the authority has currently done to this monster, e.g. a Battle Cry slow.</summary>
        public float EffectiveChaseSpeed => data != null
            ? data.chaseSpeed * (_status != null ? _status.SpeedMultiplier : 1f)
            : 0f;

        private void Awake()
        {
            _health = GetComponent<HealthComponent>();
            _status = StatusComponent.Ensure(gameObject);
            _combatId = CombatRegistry.AllocateId();
            CombatRegistry.Register(_combatId, this);
            _lookRotation = transform.rotation;
            _bodyHomePosition = bodyTransform != null ? bodyTransform.localPosition : Vector3.zero;
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
            _brain = new EnemyBrain(data, _nav, transform.position, config != null ? config.levelMask : Physics.DefaultRaycastLayers);
            _lookRotation = transform.rotation;

            _telegraph = AttackTelegraphVisual.Create(transform, data.attackTelegraphCone, data.telegraphColor);
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
            _brain?.NotifyDamaged(damage);
            _timeSinceDamage = 0f;
        }

        private void HandleDeath()
        {
            _brain?.MarkDead();
            _status?.ClearStatuses();
            _path.Clear();
            _telegraph?.SetState(false, 0f, Vector3.zero, 1f, false);

            if (bodyTransform != null)
            {
                bodyTransform.localRotation = Quaternion.Euler(80f, 0f, 12f);
                bodyTransform.localPosition += Vector3.up * -0.25f;
            }

            GameEvents.RaiseToast($"{(data != null ? data.displayName : "Enemy")} slain");

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

            float perceptionRadius = Mathf.Max(data.sightRange, data.hearingRange) + 6f;
            CombatRegistry.TryGetNearest(transform.position, EntityKind.LocalPlayer, perceptionRadius, out ICombatEntity target);

            BrainOrder order = _brain.Tick(dt, Time.time, transform.position, transform.forward, target);

            if (order.RequestAttack && target != null) SubmitAttack(target);

            RotateTowards(order.FaceTowards, dt);
            Move(order, dt);
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

            Vector3 steer = ChooseSteerPoint(destination);
            Vector3 flat = new Vector3(steer.x - transform.position.x, 0f, steer.z - transform.position.z);

            if (flat.sqrMagnitude < 0.01f) return;

            // The brain asked for its own speed; whatever the authority has currently done to this monster
            // (a Battle Cry slow, for example) is applied here rather than inside the decision making.
            float statusMultiplier = _status != null ? _status.SpeedMultiplier : 1f;
            float step = Mathf.Min(order.Speed, 12f) * statusMultiplier * dt;
            if (step <= 0.0001f) return;

            Vector3 next = transform.position + flat.normalized * step;
            next.y = groundHeight;
            transform.position = next;

            // Colliders are only moved with transforms on the next physics step. Combat queries run in
            // Update, so sync now or a raycast/overlap this frame would use last frame's position.
            Physics.SyncTransforms();
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
            if (bodyTransform == null || _brain == null) return;

            float progress = _brain.WindupProgress;
            float height = Mathf.Max(0.05f, data.attackTelegraphHeight);
            bodyTransform.localPosition = _bodyHomePosition + Vector3.up * (progress * height);

            if (_telegraph == null) return;

            if (_whiffTimer > 0f) _whiffTimer -= Time.deltaTime;
            bool whiffed = _whiffTimer > 0f;
            bool winding = _brain.IsWindingUp;

            _telegraph.SetState(winding || whiffed,
                winding ? progress : 1f,
                TelegraphDirection(),
                TelegraphLength(),
                whiffed && !winding);
        }

        /// <summary>Ground-plane direction the brain committed at the start of the windup.</summary>
        private Vector3 TelegraphDirection()
        {
            Vector3 direction = _brain != null ? _brain.TelegraphDirection : Vector3.zero;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.0001f) direction = transform.forward;
            return direction.sqrMagnitude < 0.0001f ? Vector3.forward : direction.normalized;
        }

        private float TelegraphLength()
        {
            float reach = data.attackWeapon != null
                ? data.attackWeapon.reach + data.attackWeapon.hitSphereRadius
                : 1f;
            return Mathf.Max(0.6f, Mathf.Max(reach, data.attackRange));
        }

        private void SubmitAttack(ICombatEntity target)
        {
            if (data.attackWeapon == null) return;
            if (!GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority)) return;

            Vector3 origin = transform.position + Vector3.up * 1.2f;
            Vector3 warned = TelegraphDirection();

            Vector3 toTarget = target.CombatCenter - origin;
            Vector3 flatTo = new Vector3(toTarget.x, 0f, toTarget.z);
            if (flatTo.sqrMagnitude < 0.0001f) flatTo = warned;

            float halfCone = Mathf.Clamp(data.attackTelegraphCone, 40f, 200f) * 0.5f;
            float distance = flatTo.magnitude;
            float maxRange = Mathf.Max(1f, data.attackRange) + 0.4f;

            // The wedge the player was warned about is the only wedge this attack can land in. Outside
            // it, or out of reach, no intent is submitted at all: dodging cannot be overridden locally.
            if (Vector3.Angle(warned, flatTo) > halfCone || distance > maxRange)
            {
                LastAttackWhiffed = true;
                _whiffTimer = 0.22f;
                return;
            }

            Vector3 aimPoint = origin + warned * distance + Vector3.up * (target.CombatCenter.y - origin.y);
            Vector3 direction = (aimPoint - origin).sqrMagnitude > 0.0001f
                ? (aimPoint - origin).normalized
                : warned;

            // Same per-combat-id allocator the player's components use, so an enemy that ever carries two
            // attack sources cannot collide with itself.
            int sequence = CombatRegistry.NextSequence(_combatId);

            AttackIntent intent = new AttackIntent(_combatId, EntityKind.Enemy, data.attackWeapon, 1f,
                origin, direction, maxRange, sequence, Time.time);

            LastAttackWhiffed = false;

            // The wedge/range gate above decides WHETHER an attack is submitted. The hit point is the target's own
            // centre: the committed telegraph direction can pass beside a strafing player, and the authority
            // only accepts a hit point that lies on the target.
            HitCandidate hit = new HitCandidate(target.CombatId, target.CombatCenter, false);

            authority.SubmitAttack(intent, hit);
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
