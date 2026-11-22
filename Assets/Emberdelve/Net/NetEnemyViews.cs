using System.Collections.Generic;
using System.Globalization;
using Emberdelve.AI;
using Emberdelve.Data;
using Emberdelve.Net.Rules;
using UnityEngine;
using Unity.Netcode;

namespace Emberdelve.Net
{
    /// <summary>
    /// U3B3, client process: the minimal presence of enemies. Client processes have no enemies (the scene's enemy spawner is a server system and
    /// NetSceneRoles switches it off for clients), so the server sends each client snapshots through its own player object (NetPlayerRig
    /// EnemyPoseRpc, unreliable, a few times a second; EnemyVitalsRpc and EnemyPresentationRpc, reliable) and this class keeps one PROXY per enemy: a CC0
    /// skeleton visual that eases to the last position and animates locomotion from received pose deltas. A proxy has no collider, brain or
    /// health component; its pose snapshots remain scoped to the owning client's channel.
    ///
    ///   [client] enemy-view id=.. health=.. max=.. dead=..       when an enemy's reported vitals change
    ///   [client] enemy-view summary total=.. alive=.. dead=.. poses=..   at most every 2 s while enemies are known
    /// </summary>
    public static class NetEnemyViews
    {
        private const string VisualResourcePath = "Emberdelve/NetEnemyVisuals/EnemyVisual_";

        private sealed class Entry
        {
            public NetEnemyProxy Proxy;
            public float Health;
            public float MaxHealth;
            public bool Dead;
            public bool HasVitals;
            public byte VisualKind;
            public bool HasPresentation;
            public bool HasPosition;
            public Vector3 LastPosition;
            public EnemyPresentationWire Presentation;
            public readonly EnemyHitPresentationQueue PendingHits = new EnemyHitPresentationQueue();
        }

        private static readonly Dictionary<int, Entry> Entries = new Dictionary<int, Entry>();
        private static readonly HashSet<int> RetiredEnemies = new HashSet<int>();
        private static readonly List<Rect> RetiredFloors = new List<Rect>();
        private static int _poses;
        private static float _nextSummary;

        public static int Total => Entries.Count;
        public static int PoseCount => _poses;

        internal static bool TryGetAudioEmitter(int enemyId, out Transform emitter)
        {
            emitter = Entries.TryGetValue(enemyId, out Entry entry) && entry.Proxy != null ? entry.Proxy.transform : null;
            return emitter != null;
        }

        public static bool TryGetNearestAliveProxy(Vector3 from, out int enemyId, out Vector3 position)
        {
            enemyId = 0;
            position = default;
            float best = float.PositiveInfinity;
            foreach (KeyValuePair<int, Entry> pair in Entries)
            {
                Entry entry = pair.Value;
                if (entry.Dead || entry.Proxy == null) continue;
                float distance = (entry.Proxy.transform.position - from).sqrMagnitude;
                if (distance >= best) continue;
                best = distance;
                enemyId = pair.Key;
                position = entry.Proxy.transform.position;
            }
            return enemyId > 0;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() { Clear(); }

        public static int AliveCount
        {
            get
            {
                int alive = 0;
                foreach (KeyValuePair<int, Entry> pair in Entries) if (!pair.Value.Dead) alive++;
                return alive;
            }
        }

        public static void Clear()
        {
            foreach (KeyValuePair<int, Entry> pair in Entries)
                DestroyProxy(pair.Value.Proxy);
            Entries.Clear();
            RetiredEnemies.Clear(); RetiredFloors.Clear();
            _poses = 0;
        }

        private static void DestroyProxy(NetEnemyProxy proxy)
        {
            if (proxy == null) return;
            proxy.gameObject.SetActive(false);
            if (Application.isPlaying) Object.Destroy(proxy.gameObject); else Object.DestroyImmediate(proxy.gameObject);
        }

        private static bool IsRetiredPosition(Vector3 position)
        {
            foreach (var bounds in RetiredFloors)
                if (bounds.Contains(new Vector2(position.x, position.z))) return true;
            return false;
        }

        private static void RetireEnemy(int id)
        {
            RetiredEnemies.Add(id);
            if (!Entries.TryGetValue(id, out var entry)) return;
            DestroyProxy(entry.Proxy); Entries.Remove(id);
        }

        public static void RetireFloor(Rect bounds)
        {
            if (RetiredFloors.Contains(bounds)) return;
            RetiredFloors.Add(bounds);
            var retired = new List<int>();
            foreach (var pair in Entries)
                if (pair.Value.HasPosition && IsRetiredPosition(pair.Value.LastPosition)) retired.Add(pair.Key);
            foreach (int id in retired) RetireEnemy(id);
        }

        private static Entry Get(int id)
        {
            if (!Entries.TryGetValue(id, out Entry entry))
            {
                entry = new Entry();
                Entries[id] = entry;
            }

            return entry;
        }

        public static void OnPose(in EnemyPoseWire pose)
        {
            if (!pose.IsValid || RetiredEnemies.Contains(pose.Enemy)) return;
            Vector3 where = new Vector3(pose.X, pose.Y, pose.Z);
            if (IsRetiredPosition(where)) { RetireEnemy(pose.Enemy); return; }
            Entry entry = Get(pose.Enemy);
            entry.HasPosition = true; entry.LastPosition = where;
            if (entry.Dead) return;
            _poses++;

            if (entry.Proxy == null)
            {
                entry.Proxy = CreateProxy(pose.Enemy, entry.VisualKind, entry.PendingHits);
                entry.Proxy.Snap(where, pose.Yaw);
                if (entry.HasPresentation)
                    entry.Proxy.ApplyPresentation(in entry.Presentation, true);
            }
            else
            {
                entry.Proxy.SetTarget(where, pose.Yaw);
            }
        }

        public static void OnVitals(in EnemyVitalsWire vitals)
        {
            if (RetiredEnemies.Contains(vitals.Enemy)) return;
            Entry entry = Get(vitals.Enemy);
            bool changed = !entry.HasVitals || entry.Dead != vitals.Dead || !Mathf.Approximately(entry.Health, vitals.Health) ||
                           !Mathf.Approximately(entry.MaxHealth, vitals.MaxHealth);
            entry.HasVitals = true;
            entry.Health = vitals.Health;
            entry.MaxHealth = vitals.MaxHealth;
            if (vitals.Dead && !entry.Dead && entry.Proxy != null) entry.Proxy.SetDead();
            entry.Dead = vitals.Dead;

            if (changed)
            {
                Debug.Log(NetTuning.LogPrefix + "[client] enemy-view id=" + vitals.Enemy + " health=" + F(vitals.Health) +
                          " max=" + F(vitals.MaxHealth) + " dead=" + vitals.Dead);
            }
        }

        public static void OnPresentation(in EnemyPresentationWire state)
        {
            if (!state.IsValid || RetiredEnemies.Contains(state.Enemy)) return;
            Entry entry = Get(state.Enemy);
            byte oldKind = entry.VisualKind;
            bool hadPresentation = entry.HasPresentation;
            entry.PendingHits.Observe(state.HitSequence, state.IsDead);
            entry.VisualKind = state.VisualKind;
            entry.HasPresentation = true;
            entry.Presentation = state;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log(NetTuning.LogPrefix + "[client] enemy-presentation id=" + state.Enemy + " kind=" + state.VisualKindName +
                      " windingUp=" + state.IsWindingUp + " swings=" + state.SwingsBegun + " hits=" + state.HitSequence + " dead=" + state.IsDead);
#endif
            if (entry.Proxy != null)
            {
                entry.Proxy.SetIdentity(state.Enemy, state.VisualKind);
                if (!hadPresentation || oldKind != state.VisualKind) entry.Proxy.SetVisual(LoadVisual(state.VisualKind));
                entry.Proxy.ApplyPresentation(in state, !hadPresentation);
            }
            entry.Dead |= state.IsDead;
        }

        /// <summary>Called from the owner's NetOwnerCombatView.Update: at most one summary line every 2 s while enemies are known.</summary>
        public static void TickSummary(float unscaledNow)
        {
            if (Entries.Count == 0 || unscaledNow < _nextSummary) return;
            _nextSummary = unscaledNow + 2f;
            int alive = AliveCount;
            Debug.Log(NetTuning.LogPrefix + "[client] enemy-view summary total=" + Entries.Count + " alive=" + alive + " dead=" + (Entries.Count - alive) + " poses=" + _poses);
        }

        private static string F(float value) { return value.ToString("F1", CultureInfo.InvariantCulture); }

        private static GameObject LoadVisual(byte visualKind)
        {
            string name = EnemyPresentationWire.NameOfVisual(visualKind <= EnemyPresentationWire.CinderWardenKind ? visualKind : (byte)0);
            return Resources.Load<GameObject>(VisualResourcePath + name);
        }

        private static NetEnemyProxy CreateProxy(int enemyId, byte visualKind, EnemyHitPresentationQueue pendingHits)
        {
            GameObject root = new GameObject("NetEnemyProxy_" + enemyId);
            NetEnemyProxy proxy = root.AddComponent<NetEnemyProxy>();
            proxy.SetIdentity(enemyId, visualKind);
            proxy.SetPendingHitQueue(pendingHits);
            proxy.SetVisual(LoadVisual(visualKind));
            return proxy;
        }
    }

    /// <summary>Client-only enemy view. Eases to server snapshots and drives locomotion from their positional deltas.</summary>
    public sealed class NetEnemyProxy : MonoBehaviour
    {
        private const float Ease = 10f;
        private const float SpeedParameterCutoff = 0.1f;
        private const float StaleSnapshotSeconds = 0.75f;
        private static readonly int SpeedHash = Animator.StringToHash("Speed");
        private static readonly int AttackSpeedHash = Animator.StringToHash("AttackSpeed");
        private static readonly int AttackHash = Animator.StringToHash("Attack");
        private static readonly int HitHash = Animator.StringToHash("Hit");
        private static readonly int DeadHash = Animator.StringToHash("Dead");
        private static readonly int AttackStateHash = Animator.StringToHash("Attack");
        private static readonly int HitStateHash = Animator.StringToHash("Hit");
        private static readonly int DeathStateHash = Animator.StringToHash("Death");
        private Vector3 _target;
        private float _yaw;
        private Animator _animator;
        private Vector3 _previousSnapshotPosition;
        private float _previousSnapshotTime;
        private float _snapshotSpeed;
        private float _visualSpeed;
        // Rebound once during CreateProxy so Entry can retain presentation edges received before the first pose.
        private EnemyHitPresentationQueue _pendingHits = new EnemyHitPresentationQueue();
        private bool _wasWindingUp;
        private bool _dead;
        private float _deathAt;
        private float _deathDuration = 2.5f;
        private EnemyAttackPhase _attackPhase;
        private double _phaseSampleTime;
        private float _phaseReceivedAt;
        private bool _phaseDriven;
        private bool _restDriven, _dormant, _waking;
        private float _wakeElapsed;
        private int _enemyId;
        private byte _visualKind;
        private int _lastAnimatorStateHash;

        public void SetIdentity(int enemyId, byte visualKind)
        {
            _enemyId = enemyId;
            if (GetComponent<Emberdelve.Feedback.WorldFootsteps>() == null)
                gameObject.AddComponent<Emberdelve.Feedback.WorldFootsteps>().Audible = false;
            _visualKind = visualKind;
        }

        public void SetPendingHitQueue(EnemyHitPresentationQueue pendingHits)
        {
            _pendingHits = pendingHits ?? new EnemyHitPresentationQueue();
        }

        public void SetVisualAnimator(Animator animator)
        {
            _animator = animator;
            if (_animator != null)
            {
                _animator.applyRootMotion = false;
                // Keep the state probe deterministic in development builds even outside the camera frustum.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
#endif
            }
        }

        public void SetVisual(GameObject prefab)
        {
            bool wasDead = _dead;
            for (int i = transform.childCount - 1; i >= 0; i--) Object.Destroy(transform.GetChild(i).gameObject);
            _animator = null;
            _dead = false;
            if (prefab == null)
            {
                GameObject capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                capsule.name = "CapsuleFallback";
                capsule.transform.SetParent(transform, false);
                Collider collider = capsule.GetComponent<Collider>();
                if (collider != null) Object.Destroy(collider);
                if (wasDead) SetDead();
                return;
            }
            GameObject visual = Object.Instantiate(prefab, transform, false);
            visual.name = "SkeletonVisual";
            visual.transform.localPosition = Vector3.down * (_visualKind == EnemyPresentationWire.CinderWardenKind
                ? EnemyData.CinderWardenCenterHeight : 1.15f);
            EnemyVisualDriver driver = visual.GetComponent<EnemyVisualDriver>();
            if (driver != null) Object.Destroy(driver);
            Transform blood = visual.transform.Find("BloodHit");
            if (blood != null) Object.Destroy(blood.gameObject);
            SetVisualAnimator(visual.GetComponentInChildren<Animator>());
            if (wasDead) SetDead();
        }

        public void ApplyPresentation(in EnemyPresentationWire state, bool initial = false)
        {
            if (!state.IsValid || _animator == null) return;
            var footsteps = GetComponent<Emberdelve.Feedback.WorldFootsteps>();
            if (footsteps != null) footsteps.Audible = !state.IsDead && !state.IsDormant && !state.IsWaking;
            if (!initial && !state.IsDead && state.AttackPhase == 2 && _attackPhase.Phase != 2)
                Emberdelve.Feedback.WorldAudio.PlayAttached(Emberdelve.Feedback.WorldSound.Swing, transform, Vector3.up, .8f);
            bool starting = state.AttackPhase != 0 && (_attackPhase.Phase == 0 || initial);
            _attackPhase = new EnemyAttackPhase(state.AttackPhase, state.PhaseElapsed, state.PhaseDuration, state.RecoilStart);
            _phaseSampleTime = state.SampleTime;
            _phaseReceivedAt = Time.unscaledTime;
            _wasWindingUp = state.IsWindingUp;
            _dormant = state.IsDormant;
            _waking = state.IsWaking;
            _wakeElapsed = state.WakeElapsed;
            if (state.IsDead) { SetDead(); return; }
            if (_dormant || _waking) { LogAction(_dormant ? "Dormant" : "Wake"); ApplyRestClock(); return; }
            ApplyRestClock();
            if (starting) LogAction("Attack");
            ApplyAttackClock();
            TryPlayPendingHit(_wasWindingUp, starting);
        }

        private void TryPlayPendingHit(bool winding, bool swingJustStarted)
        {
            if (_dormant || _waking) return;
            if (!HasParameter(HitHash, AnimatorControllerParameterType.Trigger)) return;
            if (!_pendingHits.TryConsume(_dead, winding, swingJustStarted, _attackPhase.Phase != 0)) return;
            _animator.SetTrigger(HitHash);
            LogAction("Hit");
        }

        private bool ApplyRestClock()
        {
            float age = NetworkManager.Singleton != null
                ? (float)System.Math.Max(0d, NetworkManager.Singleton.ServerTime.Time - _phaseSampleTime)
                : Time.unscaledTime - _phaseReceivedAt;
            return EnemyDormantPose.Apply(_animator, _dormant, _waking, _wakeElapsed + age, ref _restDriven);
        }

        private void ApplyAttackClock()
        {
            if (_animator == null || _dead || _dormant || _waking) return;
            if (_attackPhase.Phase != 0)
            {
                float age = NetworkManager.Singleton != null
                    ? (float)System.Math.Max(0d, NetworkManager.Singleton.ServerTime.Time - _phaseSampleTime)
                    : Time.unscaledTime - _phaseReceivedAt;
                _phaseDriven = true;
                if (HasParameter(AttackHash, AnimatorControllerParameterType.Trigger)) _animator.ResetTrigger(AttackHash);
                if (HasParameter(HitHash, AnimatorControllerParameterType.Trigger)) _animator.ResetTrigger(HitHash);
                _animator.SetFloat(AttackSpeedHash, 0f);
                // Extrapolate within this phase only. Never predict damage/recovery before the authority announces it.
                _animator.Play("Attack", 0, _attackPhase.NormalizedTime(age, _visualKind == EnemyPresentationWire.CinderWardenKind));
            }
            else if (_phaseDriven)
            {
                _phaseDriven = false;
                _animator.SetFloat(AttackSpeedHash, 1f);
                _animator.CrossFadeInFixedTime("Idle", 0.08f);
            }
        }

        public void SetDead()
        {
            if (_dead) return;
            _dead = true;
            var footsteps = GetComponent<Emberdelve.Feedback.WorldFootsteps>();
            if (footsteps != null) footsteps.Audible = false;
            EnemyDormantPose.FinishDead(_animator, ref _restDriven);
            _dormant = _waking = false;
            _pendingHits.Discard();
            _deathAt = Time.unscaledTime;
            if (_animator != null)
            {
                if (HasParameter(AttackHash, AnimatorControllerParameterType.Trigger)) _animator.ResetTrigger(AttackHash);
                if (HasParameter(HitHash, AnimatorControllerParameterType.Trigger)) _animator.ResetTrigger(HitHash);
                _animator.SetFloat(SpeedHash, 0f);
                if (HasParameter(DeadHash, AnimatorControllerParameterType.Bool))
                {
                    _animator.SetBool(DeadHash, true);
                    LogAction("Death");
                }
                RuntimeAnimatorController controller = _animator.runtimeAnimatorController;
                if (controller != null)
                    foreach (AnimationClip clip in controller.animationClips)
                        if (clip != null && (clip.name.IndexOf("Death", System.StringComparison.OrdinalIgnoreCase) >= 0 || clip.name.IndexOf("Dead", System.StringComparison.OrdinalIgnoreCase) >= 0))
                        { _deathDuration = Mathf.Clamp(clip.length, 0.5f, 10f); break; }
            }
        }

        private void LogAction(string action)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            string[] names = { "Minion", "Rogue", "Warrior", "Mage" };
            string kind = _visualKind < names.Length ? names[_visualKind] : "Unknown";
            Debug.Log(NetTuning.LogPrefix + "[client] enemy-animation id=" + _enemyId + " action=" + action + " kind=" + kind);
#endif
        }

        private bool HasParameter(int hash, AnimatorControllerParameterType type)
        {
            if (_animator == null || _animator.runtimeAnimatorController == null) return false;
            AnimatorControllerParameter[] parameters = _animator.parameters;
            for (int i = 0; i < parameters.Length; i++)
                if (parameters[i].nameHash == hash && parameters[i].type == type) return true;
            return false;
        }

        public void Snap(Vector3 where, float yaw)
        {
            _target = where;
            _yaw = yaw;
            _previousSnapshotPosition = where;
            _previousSnapshotTime = Time.unscaledTime;
            _snapshotSpeed = 0f;
            _visualSpeed = 0f;
            transform.position = where;
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        public void SetTarget(Vector3 where, float yaw)
        {
            float now = Time.unscaledTime;
            float elapsed = now - _previousSnapshotTime;
            Vector3 displacement = where - _previousSnapshotPosition;
            displacement.y = 0f;
            // Estimate locomotion from the authoritative position samples, not from eased render-frame motion.
            _snapshotSpeed = elapsed > 0.01f && elapsed <= 2f ? displacement.magnitude / elapsed : 0f;
            _previousSnapshotPosition = where;
            _previousSnapshotTime = now;
            _target = where;
            _yaw = yaw;
        }

        private void Update()
        {
            float t = 1f - Mathf.Exp(-Ease * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, _target, t);
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.Euler(0f, _yaw, 0f), t);
            EnemyDormantPose.BlendShieldArm(_animator, !_dead && !_dormant && !_waking, Time.deltaTime);
            if (_dead)
            {
                if (Time.unscaledTime - _deathAt >= _deathDuration + 0.25f) Object.Destroy(gameObject);
                return;
            }
            if (ApplyRestClock()) return;
            if (_animator != null)
            {
                float requestedSpeed = Time.unscaledTime - _previousSnapshotTime <= StaleSnapshotSeconds ? _snapshotSpeed : 0f;
                float blend = 1f - Mathf.Exp(-8f * Time.unscaledDeltaTime);
                _visualSpeed = Mathf.Lerp(_visualSpeed, requestedSpeed, blend);
                _animator.SetFloat(SpeedHash, _visualSpeed < SpeedParameterCutoff ? 0f : _visualSpeed);
            }
            ApplyAttackClock();
            TryPlayPendingHit(_wasWindingUp, false);
        }

        private void LateUpdate()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Animator evaluation happens between Update and LateUpdate. This proves the controller entered the actual state,
            // beyond merely accepting a SetTrigger/SetBool command; it does not claim that pixels were rendered or reviewed.
            if (_animator == null || _animator.runtimeAnimatorController == null || _animator.layerCount == 0 || _animator.IsInTransition(0)) return;
            AnimatorStateInfo state = _animator.GetCurrentAnimatorStateInfo(0);
            int hash = state.shortNameHash;
            if (hash == _lastAnimatorStateHash) return;
            _lastAnimatorStateHash = hash;

            string name = hash == AttackStateHash ? "Attack" : hash == HitStateHash ? "Hit" : hash == DeathStateHash ? "Death" : null;
            if (name == null) return;
            Debug.Log(NetTuning.LogPrefix + "[client] enemy-animation-state id=" + _enemyId + " state=" + name + " kind=" + VisualKindName() +
                      " normalized=" + state.normalizedTime.ToString("F2", CultureInfo.InvariantCulture));
#endif
        }

        private string VisualKindName() => EnemyPresentationWire.NameOfVisual(_visualKind);
    }
}
