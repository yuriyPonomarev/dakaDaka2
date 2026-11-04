using UnityEngine;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;

namespace Emberdelve.Player
{
    /// <summary>
    /// V3c/V3d: drives the first-person arms (a Humanoid mannequin with an arms-only skinned mesh, human skin and gambeson sleeves,
    /// built by the editor from the Quaternius rig) of the LOCAL player. Presentation only, like <c>EnemyVisualDriver</c>: it reads the
    /// state the client already has (ground speed, the guard state, the weapon rig's pose, the damage events of the player's health)
    /// and never changes damage, hit volumes, swing trajectories, timings or messages.
    ///
    /// V3d: the arms no longer play third-person clips for a swing. The Animator only supplies a calm idle base pose; every frame (after
    /// the <see cref="WeaponVisualRig"/> has placed the weapon from the SwingPatternData clock - windup, active, recovery, riposte strikes
    /// and the guard pose alike) a two-bone solver (<see cref="SolveArm"/>) puts the right hand on that pose and the left hand on the shield
    /// (rest / raised) or on the second grip of a two-handed weapon. The blade orientation is the rig's own (the yaw, pitch and roll of the
    /// pattern), the hand position is the rig's base position brought to arm's reach (<see cref="ViewHand"/>) and kept inside the camera
    /// frustum (<see cref="ClampToView"/>), because the pattern base travels up to a metre from the eye. The solver is a plain method so
    /// edit-mode tests and the evidence renderer pose the very same arms the game shows.
    ///
    /// The weapon model hangs under the right hand grip (<see cref="gripRight"/>), a shield under the left one (<see cref="gripLeft"/>).
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]
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

        /// <summary>Name of the clip (a project copy of the library take) of each controller state.</summary>
        public static string SwingClipName(string state) => "FP_" + state;

        // ---- the viewmodel layout (metres, aim frame: right, up, forward) --------------------------------

        /// <summary>The arms mannequin is scaled up so that its arms (0.55 m) reach the compressed swing arc.</summary>
        public const float ModelScale = 1.3f;

        /// <summary>Where the middle of the two shoulders sits in the aim frame (behind the eye plane's visible cone, below the eye).</summary>
        public static readonly Vector3 ShoulderCenter = new Vector3(0f, -0.34f, 0.06f);

        /// <summary>The right hand at the pattern's rest base is shown here; offsets from the rest base are scaled by <see cref="HandScale"/>.</summary>
        public static readonly Vector3 HandCenter = new Vector3(0.22f, -0.15f, 0.40f);
        public static readonly Vector3 HandScale = new Vector3(0.42f, 0.35f, 0.4f);
        /// <summary>The pattern's guard base (every melee pattern authors the same rest base).</summary>
        public static readonly Vector3 PatternRest = new Vector3(0.26f, -0.20f, 0.30f);

        public static readonly Vector3 ShieldRest = new Vector3(-0.40f, -0.30f, 0.50f);
        public static readonly Vector3 ShieldRaised = new Vector3(-0.17f, -0.17f, 0.52f);
        public static readonly Vector3 ShieldRestEuler = new Vector3(0f, 14f, 0f);
        public static readonly Vector3 ShieldRaisedEuler = new Vector3(-6f, 4f, 0f);

        /// <summary>Elbow hints (aim frame): below and a little outwards.</summary>
        private static readonly Vector3 RightPole = new Vector3(0.7f, -1f, -0.4f);
        private static readonly Vector3 LeftPole = new Vector3(-0.7f, -1f, -0.4f);

        /// <summary>The weapon rig's blade runs along its forward; a model grip has the tip on +Y and the flat face on +Z.</summary>
        public static readonly Quaternion GripFromRig = Quaternion.Euler(90f, 0f, 0f);

        /// <summary>The shield model is shown a little smaller than its world size so that it frames the view instead of filling it.</summary>
        public const float ShieldScale = 0.72f;

        public const float MinModelScale = 0.9f;
        public const float MaxModelScale = 1.5f;
        /// <summary>Fraction of the half field of view the hand may use before it is pulled back inside.</summary>
        public const float FrustumFill = 0.9f;

        /// <summary>Walk clip speed reference (kept for the controller).</summary>
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
        private Transform _frame;
        private WeaponVisualRig _visual;
        private HealthComponent _health;
        private GameObject _mainHand;
        private GameObject _offHand;
        private WeaponData _mainData;
        private WeaponViewModelSet.Entry _mainEntry;
        private float _bladeLength;
        private Vector3 _lastRigPosition;
        private float _speed;
        private float _swingEnd;
        private float _push;
        private int _levelMask;
        private bool _blockHeld;
        private bool _hasWeaponModel;
        private float _guardBlend;
        private float _bobPhase;
        private Vector3 _joltRight;
        private Vector3 _joltLeft;
        private Transform _upperR, _lowerR, _handR, _upperL, _lowerL, _handL;

        public Animator Animator => animator;
        public Transform GripRight => gripRight;
        public Transform GripLeft => gripLeft;
        public GameObject MainHandModel => _mainHand;
        public GameObject OffHandModel => _offHand;
        public bool HasWeaponModel => _hasWeaponModel;
        public bool IsSwinging => Time.time < _swingEnd || (_visual != null && _visual.IsSwinging);
        public bool BlockHeld => _blockHeld;
        public float WallPush => _push;
        /// <summary>True while a shield model is in the left hand and the left arm is posed on it.</summary>
        public bool ShieldShown => _offHand != null && _offHand.activeInHierarchy;
        /// <summary>Scale applied to the main hand model so that its blade matches the damage segment (clamped).</summary>
        public float WeaponScale { get; private set; } = 1f;

        /// <summary>U5B: the second hand of a two-handed weapon sits this far (metres at model scale 1) behind the right grip, along the weapon's own long axis.</summary>
        public const float SecondGripOffset = 0.1f;

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
            _frame = rig != null && rig.Look != null ? rig.Look.AimTransform : (camera != null ? camera.transform : transform.parent);

            if (_health != null) _health.Damaged -= OnDamaged;
            _health = rig != null ? rig.Health : null;
            if (_health != null) _health.Damaged += OnDamaged;
        }

        /// <summary>The weapon rig whose pose the right hand follows (rebuilt by the loadout whenever the weapon changes).</summary>
        public void SetVisual(WeaponVisualRig visual) { _visual = visual; }

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
            _mainData = main;
            _mainEntry = null;
            _bladeLength = 0f;
            WeaponScale = 1f;

            if (_set != null && main != null && !main.IsRanged)
            {
                WeaponViewModelSet.Entry entry = _set.Find(main.weaponId);
                if (entry != null && gripRight != null)
                {
                    _mainHand = Hold(entry, gripRight, "Held_" + main.weaponId);
                    _hasWeaponModel = _mainHand != null;
                    _mainEntry = entry;
                    _bladeLength = DamageBladeLength(main);
                    WeaponScale = ModelScaleFor(_bladeLength, entry.length);
                    if (_mainHand != null) _mainHand.transform.localScale = Vector3.one * WeaponScale;
                }
            }

            // A two-handed weapon is held with both hands: no shield, whatever the off hand slot says.
            bool twoHanded = main != null && main.twoHanded;
            WeaponViewModelSet.Entry shield = _set != null && off != null && off.IsShield && !twoHanded ? _set.Find(off.weaponId) : null;
            if (shield != null && gripLeft != null) _offHand = Hold(shield, gripLeft, "Held_" + off.weaponId);

            ApplyBlockClip(_offHand != null);
            return _hasWeaponModel;
        }

        /// <summary>The length of the damage segment of the weapon's first melee pattern (what the Sweep_Edge bar used to show).</summary>
        public static float DamageBladeLength(WeaponData weapon)
        {
            if (weapon == null) return 0f;
            SwingPatternData[] patterns = weapon.swingPatterns;
            if (patterns != null)
            {
                for (int i = 0; i < patterns.Length; i++) if (patterns[i] != null) return patterns[i].BladeLength(weapon);
            }

            return 0f;
        }

        public static float ModelScaleFor(float damageLength, float modelLength)
        {
            if (modelLength <= 0.01f || damageLength <= 0.01f) return 1f;
            return Mathf.Clamp(damageLength / modelLength, MinModelScale, MaxModelScale);
        }

        private GameObject Hold(WeaponViewModelSet.Entry entry, Transform grip, string name)
        {
            GameObject held = Instantiate(entry.prefab, grip, false);
            held.name = name;
            held.transform.localPosition = Vector3.zero;
            held.transform.localRotation = Quaternion.identity;
            Collider[] colliders = held.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (Application.isPlaying) Destroy(colliders[i]); else DestroyImmediate(colliders[i]);
            }
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
            if (held != null)
            {
                held.SetActive(false); // a Destroy is deferred to the end of the frame: the old model must not be seen or counted until then
                if (Application.isPlaying) Destroy(held); else DestroyImmediate(held);
            }
            held = null;
        }

        private void ApplyBlockClip(bool shieldHeld)
        {
            if (animator != null && animator.layerCount > OffHandLayer) animator.SetLayerWeight(OffHandLayer, shieldHeld ? 1f : 0f);
        }

        // ---- the state the client already has ----------------------------------------------------------

        /// <summary>A swing of this kind starts and lasts <paramref name="totalSeconds"/>: the hand follows the weapon rig, which has the same clock; this only marks the swing for IsSwinging.</summary>
        public void PlaySwing(SwingKind kind, float totalSeconds)
        {
            _swingEnd = Time.time + Mathf.Max(0.05f, totalSeconds);
        }

        /// <summary>The authority stopped the swing at a wall: the rig eases back to the guard pose and the hand follows.</summary>
        public void PlayRecoil()
        {
            _swingEnd = 0f;
        }

        public void SetBlock(bool held)
        {
            _blockHeld = held;
        }

        private void OnDamaged(DamageInfo damage)
        {
            Vector3 away = Vector3.zero;
            if (_frame != null && damage.SourcePosition != Vector3.zero)
            {
                Vector3 toSource = _frame.InverseTransformDirection(damage.SourcePosition - _frame.position);
                toSource.y = 0f;
                if (toSource.sqrMagnitude > 1e-4f) away = -toSource.normalized;
            }

            if (damage.Blocked && damage.Amount <= 0.001f)
            {
                // A block held at 0 HP: the guarding hand takes the hit (a short push back and up).
                AddJolt(new Vector3(0f, 0.025f, -0.07f), _offHand != null);
                return;
            }

            if (damage.Amount <= 0f) return;
            AddJolt(new Vector3(away.x * 0.05f, -0.035f, -0.045f), false);
            AddJolt(new Vector3(away.x * 0.05f, -0.035f, -0.045f), true);
        }

        /// <summary>A short camera-relative push of a hand (decays in a third of a second).</summary>
        public void AddJolt(Vector3 aimFrameImpulse, bool leftHand)
        {
            if (leftHand) _joltLeft += aimFrameImpulse; else _joltRight += aimFrameImpulse;
            _joltLeft = Vector3.ClampMagnitude(_joltLeft, 0.12f);
            _joltRight = Vector3.ClampMagnitude(_joltRight, 0.12f);
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
            animator.SetFloat(SpeedParameter, 0f); // the base pose stays the calm idle: walking is the procedural bob below
            animator.SetFloat(WalkPlaybackParameter, Mathf.Clamp(_speed / WalkReferenceSpeed, 0.6f, 1.8f));
        }

        private void LateUpdate()
        {
            if (_camera == null) return;
            float dt = Time.deltaTime;

            // Camera clipping: a ray along the view; when level geometry is closer than the weapon's reach the arms step back.
            Transform view = _camera.transform;
            float target = 0f;
            if (_levelMask != 0 && Physics.Raycast(view.position, view.forward, out RaycastHit hit, wallReach, _levelMask, QueryTriggerInteraction.Ignore))
                target = Mathf.Clamp(wallReach - hit.distance, 0f, maxWallPush);

            _push = Mathf.MoveTowards(_push, target, 3f * dt);
            transform.localPosition = restPosition;

            // Walk bob and idle breathing (procedural, aim frame).
            float walk = Mathf.Clamp01(_speed / WalkReferenceSpeed);
            _bobPhase += dt * (4.5f + 5f * walk);
            Vector3 bob = new Vector3(Mathf.Sin(_bobPhase * 0.5f) * 0.012f, -Mathf.Abs(Mathf.Sin(_bobPhase * 0.5f)) * 0.014f, 0f) * walk;
            bob += new Vector3(0f, Mathf.Sin(Time.time * 1.4f) * 0.003f, 0f);
            bob += new Vector3(0f, _push * 0.25f, -_push);

            float decay = Mathf.Exp(-9f * dt);
            _joltRight *= decay;
            _joltLeft *= decay;

            // The right hand follows the weapon rig (its pose is the true swing / guard pose); the guard raises the shield.
            Transform frame = _frame != null ? _frame : view;
            Vector3 localBase = PatternRest;
            Quaternion localRotation = Quaternion.Euler(-8f, 12f, 14f);
            if (_visual != null)
            {
                localBase = frame.InverseTransformPoint(_visual.transform.position);
                localRotation = Quaternion.Inverse(frame.rotation) * _visual.transform.rotation;
            }

            float guardTarget = _blockHeld ? 1f : 0f;
            _guardBlend = Mathf.MoveTowards(_guardBlend, guardTarget, 7f * dt);
            Pose(frame, _camera, localBase, localRotation, _guardBlend, bob + _joltRight, bob + _joltLeft);
        }

        // ---- the solver -----------------------------------------------------------------------------------

        /// <summary>
        /// Poses both arms for one frame. The Animator must already have posed the bones (the idle base pose): this only moves the shoulders'
        /// anchor, then solves the right arm onto the weapon grip target and the left arm onto the shield / second grip.
        /// <paramref name="localBase"/> / <paramref name="localRotation"/> are the weapon rig's pose in the aim frame.
        /// </summary>
        public void Pose(Transform frame, Camera camera, Vector3 localBase, Quaternion localRotation, float guardBlend, Vector3 offsetRight, Vector3 offsetLeft)
        {
            if (!ResolveBones() || frame == null) return;

            // Scale the mannequin and hold the middle of the shoulders at its viewmodel anchor.
            Transform model = animator.transform;
            model.localScale = Vector3.one * ModelScale;
            Vector3 mid = (_upperR.position + _upperL.position) * 0.5f;
            model.position += frame.TransformPoint(ShoulderCenter) - mid;

            // Right hand: the rig's pose, the base brought to arm's reach and into the frustum.
            Vector3 handLocal = ClampToView(camera, ViewHand(localBase) + offsetRight);
            Quaternion gripRotation = frame.rotation * localRotation * GripFromRig;
            Vector3 gripPosition = frame.TransformPoint(handLocal);
            if (gripRight != null)
            {
                SolveArm(_upperR, _lowerR, _handR, gripRight, gripPosition, gripRotation, frame.rotation * RightPole);
                if (_mainHand != null) _mainHand.transform.localScale = Vector3.one * WeaponScale;
            }

            // Left hand: on the shield (rest / raised), or on the second grip of a two-handed weapon, otherwise left to the idle pose.
            if (_offHand != null && gripLeft != null)
            {
                _offHand.transform.localScale = Vector3.one * ShieldScale;
                Vector3 shieldPosition = Vector3.Lerp(ShieldRest, ShieldRaised, guardBlend) + offsetLeft;
                Quaternion shieldRotation = Quaternion.Euler(Vector3.Lerp(ShieldRestEuler, ShieldRaisedEuler, guardBlend));
                SolveArm(_upperL, _lowerL, _handL, gripLeft, frame.TransformPoint(ClampToView(camera, shieldPosition)), frame.rotation * shieldRotation, frame.rotation * LeftPole);
            }
            else if (_mainData != null && _mainData.twoHanded && gripRight != null)
            {
                Vector3 axis = _mainHand != null ? _mainHand.transform.up : gripRight.up;
                Vector3 second = gripRight.position - axis * (SecondGripOffset * WeaponScale);
                SolveArm(_upperL, _lowerL, _handL, _handL, second, _handL.rotation, frame.rotation * LeftPole);
            }
        }

        /// <summary>
        /// The pattern base (aim frame) brought to what an arm can reach: offsets from the guard base are compressed per axis around the
        /// viewmodel hand centre. The blade ROTATION is never changed, so the sweep direction stays the pattern's.
        /// </summary>
        public static Vector3 ViewHand(Vector3 localBase)
        {
            Vector3 d = localBase - PatternRest;
            return HandCenter + new Vector3(d.x * HandScale.x, d.y * HandScale.y, d.z * HandScale.z);
        }

        /// <summary>Pulls a point (aim frame, the camera's own frame) back inside the view cone, <see cref="FrustumFill"/> of the half field of view, and beyond the near plane.</summary>
        public static Vector3 ClampToView(Camera camera, Vector3 local)
        {
            float fov = camera != null ? camera.fieldOfView : 70f;
            float aspect = camera != null ? Mathf.Max(0.5f, camera.aspect) : 16f / 9f;
            float near = camera != null ? camera.nearClipPlane : 0.05f;
            float halfV = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * FrustumFill;
            float halfH = halfV * aspect;
            local.z = Mathf.Max(local.z, near + 0.15f);
            local.x = Mathf.Clamp(local.x, -local.z * halfH, local.z * halfH);
            local.y = Mathf.Clamp(local.y, -local.z * halfV, local.z * halfV);
            return local;
        }

        private bool ResolveBones()
        {
            if (_upperR != null && _upperL != null) return true;
            if (animator == null) animator = GetComponentInChildren<Animator>(true);
            if (animator == null) return false;
            _upperR = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            _lowerR = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
            _handR = animator.GetBoneTransform(HumanBodyBones.RightHand);
            _upperL = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            _lowerL = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            _handL = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            return _upperR != null && _lowerR != null && _handR != null && _upperL != null && _lowerL != null && _handL != null;
        }

        /// <summary>
        /// Analytic two-bone solver: puts <paramref name="effector"/> (a child of <paramref name="hand"/>, or the hand itself) at
        /// <paramref name="targetPosition"/> with <paramref name="targetRotation"/>; the elbow bends towards <paramref name="poleDirection"/>.
        /// A target beyond the arm's reach is approached as far as the arm stretches.
        /// </summary>
        public static void SolveArm(Transform upper, Transform lower, Transform hand, Transform effector, Vector3 targetPosition, Quaternion targetRotation, Vector3 poleDirection)
        {
            Vector3 effectorLocal = hand.InverseTransformPoint(effector.position);
            Quaternion effectorRotationLocal = Quaternion.Inverse(hand.rotation) * effector.rotation;
            Quaternion handRotation = targetRotation * Quaternion.Inverse(effectorRotationLocal);
            Vector3 wrist = targetPosition - handRotation * effectorLocal;

            Vector3 a = upper.position;
            float l1 = (lower.position - a).magnitude;
            float l2 = (hand.position - lower.position).magnitude;
            Vector3 toTarget = wrist - a;
            float distance = toTarget.magnitude;
            if (distance < 1e-4f) return;
            Vector3 direction = toTarget / distance;
            float reach = Mathf.Clamp(distance, Mathf.Abs(l1 - l2) + 1e-3f, l1 + l2 - 1e-3f);
            wrist = a + direction * reach;

            float cos = Mathf.Clamp((l1 * l1 + reach * reach - l2 * l2) / (2f * l1 * reach), -1f, 1f);
            float sin = Mathf.Sqrt(1f - cos * cos);
            Vector3 pole = Vector3.ProjectOnPlane(poleDirection, direction);
            if (pole.sqrMagnitude < 1e-6f) pole = Vector3.ProjectOnPlane(Vector3.down, direction);
            pole.Normalize();
            Vector3 elbow = a + direction * (l1 * cos) + pole * (l1 * sin);

            upper.rotation = Quaternion.FromToRotation(lower.position - a, elbow - a) * upper.rotation;
            lower.rotation = Quaternion.FromToRotation(hand.position - lower.position, wrist - lower.position) * lower.rotation;
            hand.rotation = handRotation;
        }
    }
}
