using System.Collections.Generic;
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
    public sealed class EnemyAgent : MonoBehaviour, ICombatEntity
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
        private int _attackSequence;
        private Vector3 _lastPathRequest = new Vector3(float.MaxValue, 0f, float.MaxValue);
        private Vector3 _bodyHomePosition;
        private Quaternion _lookRotation;

        public EntityKind Kind => EntityKind.Enemy;
        public int CombatId => _combatId;
        public Vector3 CombatCenter => transform.position + Vector3.up * 1.15f;
        public Collider HeadCollider => headCollider;
        public bool IsDead => _health != null && _health.IsDead;
        public float CurrentHealth => _health != null ? _health.CurrentHealth : 0f;
        public float MaxHealth => _health != null ? _health.MaxHealth : 0f;
        public EnemyData Data => data;
        public EnemyBrain Brain => _brain;

        private void Awake()
        {
            _health = GetComponent<HealthComponent>();
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

            _health.Configure(Mathf.Max(1, data.maxHealth), 0f, 0f);
            _health.Died += HandleDeath;
            _health.Damaged += HandleDamaged;

            _nav = new NavGridPathfinder(navGrid);
            _brain = new EnemyBrain(data, _nav, transform.position, config != null ? config.levelMask : Physics.DefaultRaycastLayers);
            _lookRotation = transform.rotation;
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

        private void HandleDamaged(DamageInfo damage)
        {
            _brain?.NotifyDamaged(damage);
            _timeSinceDamage = 0f;
        }

        private void HandleDeath()
        {
            _brain?.MarkDead();
            _path.Clear();

            if (bodyTransform != null)
            {
                bodyTransform.localRotation = Quaternion.Euler(80f, 0f, 12f);
                bodyTransform.localPosition += Vector3.up * -0.25f;
            }

            GameEvents.RaiseToast($"{(data != null ? data.displayName : "Enemy")} slain");

            DropLoot();
            Destroy(gameObject, 6f);
        }

        private void DropLoot()
        {
            ILootHost host = lootHost as ILootHost;
            if (host == null || data == null || data.lootTable == null) return;

            int rolls = Mathf.Max(0, data.lootRolls);
            for (int i = 0; i < rolls; i++)
            {
                if (data.lootTable.Length == 0) break;
                if (UnityEngine.Random.value > data.lootDropChance) continue;

                ItemData item = data.lootTable[UnityEngine.Random.Range(0, data.lootTable.Length)];
                if (item == null) continue;

                Vector3 offset = UnityEngine.Random.insideUnitCircle * 0.7f;
                host.SpawnLoot(item, 1, transform.position + new Vector3(offset.x, 0.35f, offset.y));
            }
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

            float step = Mathf.Min(order.Speed, 12f) * dt;
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
        }

        private void SubmitAttack(ICombatEntity target)
        {
            if (data.attackWeapon == null) return;
            if (!GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority)) return;

            Vector3 origin = transform.position + Vector3.up * 1.2f;
            Vector3 direction = (target.CombatCenter - origin).normalized;
            float maxRange = Mathf.Max(1f, data.attackRange) + 0.4f;

            _attackSequence++;

            AttackIntent intent = new AttackIntent(_combatId, EntityKind.Enemy, data.attackWeapon, 1f,
                origin, direction, maxRange, _attackSequence, Time.time);

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
