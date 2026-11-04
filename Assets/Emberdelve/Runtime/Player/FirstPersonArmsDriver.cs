using UnityEngine;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;

namespace Emberdelve.Player
{
    /// <summary>
    /// V3c: drives the first-person arms (a Humanoid mannequin with an arms-only skinned mesh, human skin and gambeson sleeves,
    /// built by the editor from the Quaternius rig) of the LOCAL player. Presentation only, like <c>EnemyVisualDriver</c>: it reads
    /// the state the client already has (ground speed from the rig's own movement, the guard state, the swing the loadout starts,
    /// the damage events of the player's health) and sets Animator parameters. It never changes damage, hit volumes, swing
    /// trajectories, timings or messages: the swing, the damage segment bar and the afterimage trail still come from the
    /// SwingPatternData clock in <see cref="WeaponVisualRig"/>; the arm clip is only played over the same total duration.
    ///
    /// The weapon model hangs under the right hand grip (<see cref="gripRight"/>), a shield under the left one
    /// (<see cref="gripLeft"/>); both grips are derived by the editor from the poses meant for them.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FirstPersonArmsDriver : MonoBehaviour
    {
        // Animator contract (the controller is built by EmberdelveFirstPersonArms with exactly these names).
        public const string SpeedParameter = "Speed";
        public const string WalkPlaybackParameter = "WalkPlayback";
        public const string SwingSpeedParameter = "SwingSpeed";
        public const string BlockParameter = "Block";
        public const string SwingHorizontalTrigger = "SwingHorizontal";
        public const string SwingOverheadTrigger = "SwingOverhead";
        public const string ThrustTrigger = "Thrust";
        public const string HitReactTrigger = "HitReact";

        /// <summary>Animator layer 1 (avatar mask: left arm and fingers) plays the shield guard of the off hand; its weight is 1 while a shield is held, 0 otherwise.</summary>
        public const int OffHandLayer = 1;

        public const string IdleState = "Idle";
        public const string WalkState = "Walk";
        public const string SwingHorizontalState = "SwingHorizontal";
        public const string SwingOverheadState = "SwingOverhead";
        public const string ThrustState = "Thrust";
        public const string BlockState = "Block";
        public const string HitReactState = "HitReact";

        public static readonly string[] StateNames = { IdleState, WalkState, SwingHorizontalState, SwingOverheadState, ThrustState, BlockState, HitReactState };

        /// <summary>Name of the clip (a project copy of the library take) played in each swing state: the driver reads its length to match the swing duration.</summary>
        public static string SwingClipName(string state) => "FP_" + state;

        public const float MinSwingPlayback = 0.4f;
        public const float MaxSwingPlayback = 3.5f;

        /// <summary>Walk clip plays at this multiple of its speed per metre per second of ground speed (the clip is 1.33 s per cycle).</summary>
        private const float WalkReferenceSpeed = 3.5f;

        [SerializeField] private Animator animator;
        [SerializeField] private Transform gripRight;
        [SerializeField] private Transform gripLeft;
        [Tooltip("Where the arms root sits relative to the aim frame (camera pivot) when nothing is in front of the player.")]
        [SerializeField] private Vector3 restPosition = Vector3.zero;
        [Tooltip("Distance in front of the camera the weapon may reach before the arms are pushed back (a stand-in for a separate viewmodel camera).")]
        [SerializeField] private float wallReach = 1.1f;
        [SerializeField] private float maxWallPush = 0.45f;

        private WeaponViewModelSet _set;
        private PlayerRig _rig;
        private Camera _camera;
        private HealthComponent _health;
        private GameObject _mainHand;
        private GameObject _offHand;
        private Vector3 _lastRigPosition;
        private float _speed;
        private float _swingEnd;
        private float _push;
        private int _levelMask;
        private bool _blockHeld;
        private bool _hasWeaponModel;

        public Animator Animator => animator;
        public Transform GripRight => gripRight;
        public Transform GripLeft => gripLeft;
        public GameObject MainHandModel => _mainHand;
        public GameObject OffHandModel => _offHand;
        public bool HasWeaponModel => _hasWeaponModel;
        public bool IsSwinging => Time.time < _swingEnd;
        public bool BlockHeld => _blockHeld;
        public float WallPush => _push;

        /// <summary>
        /// Creates the arms for the local player under <paramref name="anchor"/> (the weapon anchor of the look pivot), or returns null
        /// when this rig is not the local, camera-carrying player (clones for other players, servers and tools get none) or the
        /// data asset / prefab is missing. Nothing is created outside play mode.
        /// </summary>
        public static FirstPersonArmsDriver Create(Transform anchor, PlayerRig rig, Camera camera, WeaponViewModelSet set)
        {
            if (anchor == null || rig == null || set == null || set.armsPrefab == null) return null;
            if (!Application.isPlaying || !rig.IsLocallyControlled) return null;
            if (camera == null || !camera.isActiveAndEnabled) return null;

            GameObject instance = Instantiate(set.armsPrefab, anchor, false);
            instance.name = "FirstPersonArms";
            FirstPersonArmsDriver driver = instance.GetComponent<FirstPersonArmsDriver>();
            if (driver == null) { Destroy(instance); return null; }
            driver.Bind(rig, camera, set);
            return driver;
        }

        public void Bind(PlayerRig rig, Camera camera, WeaponViewModelSet set)
        {
            _rig = rig;
            _camera = camera;
            _set = set;
            _levelMask = LayerMask.GetMask(GameConfig.LevelLayerName);
            if (animator == null) animator = GetComponentInChildren<Animator>(true);
            transform.localPosition = restPosition;
            transform.localRotation = Quaternion.identity;
            if (rig != null) _lastRigPosition = rig.transform.position;

            if (_health != null) _health.Damaged -= OnDamaged;
            _health = rig != null ? rig.Health : null;
            if (_health != null) _health.Damaged += OnDamaged;
        }

        private void OnDestroy()
        {
            if (_health != null) _health.Damaged -= OnDamaged;
        }

        /// <summary>Puts the models of the held weapons into the hands. Returns true when the main hand weapon has a model.</summary>
        public bool SetWeapons(WeaponData main, WeaponData off)
        {
            DestroyHeld(ref _mainHand);
            DestroyHeld(ref _offHand);
            _hasWeaponModel = false;

            if (_set != null && main != null && !main.IsRanged)
            {
                WeaponViewModelSet.Entry entry = _set.Find(main.weaponId);
                if (entry != null && gripRight != null)
                {
                    _mainHand = Hold(entry, gripRight, "Held_" + main.weaponId);
                    _hasWeaponModel = _mainHand != null;
                }
            }

            WeaponViewModelSet.Entry shield = _set != null && off != null && off.IsShield ? _set.Find(off.weaponId) : null;
            if (shield != null && gripLeft != null) _offHand = Hold(shield, gripLeft, "Held_" + off.weaponId);

            ApplyBlockClip(_offHand != null);
            return _hasWeaponModel;
        }

        private GameObject Hold(WeaponViewModelSet.Entry entry, Transform grip, string name)
        {
            GameObject held = Instantiate(entry.prefab, grip, false);
            held.name = name;
            held.transform.localPosition = Vector3.zero;
            held.transform.localRotation = Quaternion.identity;
            Collider[] colliders = held.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++) Destroy(colliders[i]);
            SetLayerRecursive(held, LayerMask.NameToLayer("Ignore Raycast"));
            return held;
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            if (layer < 0) return;
            Transform[] all = go.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++) all[i].gameObject.layer = layer;
        }

        private void DestroyHeld(ref GameObject held)
        {
            if (held != null) Destroy(held);
            held = null;
        }

        private void ApplyBlockClip(bool shieldHeld)
        {
            if (animator != null && animator.layerCount > OffHandLayer) animator.SetLayerWeight(OffHandLayer, shieldHeld ? 1f : 0f);
        }

        // ---- the state the client already has ----------------------------------------------------------

        /// <summary>A swing of this kind starts and lasts <paramref name="totalSeconds"/> (windup + active + recovery, from the pattern data).</summary>
        public void PlaySwing(SwingKind kind, float totalSeconds)
        {
            if (animator == null) return;
            string trigger;
            string state;
            switch (kind)
            {
                case SwingKind.Overhead: trigger = SwingOverheadTrigger; state = SwingOverheadState; break;
                case SwingKind.Thrust: trigger = ThrustTrigger; state = ThrustState; break;
                default: trigger = SwingHorizontalTrigger; state = SwingHorizontalState; break;
            }

            float clipLength = ClipLength(SwingClipName(state));
            float playback = clipLength > 0f && totalSeconds > 0.01f ? clipLength / totalSeconds : 1f;
            animator.SetFloat(SwingSpeedParameter, Mathf.Clamp(playback, MinSwingPlayback, MaxSwingPlayback));
            animator.ResetTrigger(HitReactTrigger);
            animator.SetTrigger(trigger);
            _swingEnd = Time.time + Mathf.Max(0.05f, totalSeconds);
        }

        /// <summary>The authority stopped the swing at a wall: the arms return to the idle pose.</summary>
        public void PlayRecoil()
        {
            if (animator == null) return;
            _swingEnd = 0f;
            animator.ResetTrigger(SwingHorizontalTrigger);
            animator.ResetTrigger(SwingOverheadTrigger);
            animator.ResetTrigger(ThrustTrigger);
            animator.CrossFadeInFixedTime(IdleState, 0.12f);
        }

        public void SetBlock(bool held)
        {
            _blockHeld = held;
            if (animator != null) animator.SetBool(BlockParameter, held);
        }

        private void OnDamaged(DamageInfo damage)
        {
            if (animator == null || damage.Amount <= 0f) return;
            // A hit during a swing does not abort the arm clip (the swing itself goes on in the authority).
            if (IsSwinging) return;
            animator.SetTrigger(HitReactTrigger);
        }

        private float ClipLength(string clipName)
        {
            RuntimeAnimatorController controller = animator != null ? animator.runtimeAnimatorController : null;
            if (controller == null) return 0f;
            AnimationClip[] clips = controller.animationClips;
            for (int i = 0; i < clips.Length; i++)
            {
                if (clips[i] != null && clips[i].name == clipName) return clips[i].length;
            }

            return 0f;
        }

        private void Update()
        {
            if (animator == null || _rig == null) return;
            if (!_rig.IsLocallyControlled) { Destroy(gameObject); return; }

            float dt = Mathf.Max(1e-4f, Time.deltaTime);
            Vector3 delta = _rig.transform.position - _lastRigPosition;
            _lastRigPosition = _rig.transform.position;
            delta.y = 0f;
            float measured = Mathf.Min(12f, delta.magnitude / dt); // a teleport / respawn is not a sprint
            _speed = Mathf.Lerp(_speed, measured, 1f - Mathf.Exp(-12f * dt));
            animator.SetFloat(SpeedParameter, _speed);
            animator.SetFloat(WalkPlaybackParameter, Mathf.Clamp(_speed / WalkReferenceSpeed, 0.6f, 1.8f));
        }

        private void LateUpdate()
        {
            if (_camera == null) return;

            // Camera clipping: a ray along the view; when level geometry is closer than the weapon's reach the arms step back.
            Transform view = _camera.transform;
            float target = 0f;
            if (_levelMask != 0 && Physics.Raycast(view.position, view.forward, out RaycastHit hit, wallReach, _levelMask, QueryTriggerInteraction.Ignore))
                target = Mathf.Clamp(wallReach - hit.distance, 0f, maxWallPush);

            _push = Mathf.MoveTowards(_push, target, 3f * Time.deltaTime);
            transform.localPosition = restPosition + new Vector3(0f, _push * 0.25f, -_push);
        }
    }
}
