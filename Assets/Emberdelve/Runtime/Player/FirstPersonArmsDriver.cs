using UnityEngine;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using UnityEngine.Rendering;

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
        public static readonly Vector3 HandScale = new Vector3(0.62f, 0.52f, 0.5f);
        /// <summary>The pattern's guard base (every melee pattern authors the same rest base).</summary>
        public static readonly Vector3 PatternRest = new Vector3(0.26f, -0.20f, 0.30f);

        /// <summary>Cosmetic sword ready pose of the first-person hand (aim frame) and its blade direction (rig frame, blade along forward).</summary>
        public static readonly Vector3 SwordReadyHand = new Vector3(0.23f, -0.13f, 0.50f);
        public static readonly Quaternion SwordReadyRotation = Quaternion.Euler(-64f, -24f, 0f);
        public static readonly Quaternion ShieldGuardSwordRotation = Quaternion.Euler(-20f, -20f, 0f);

        public static readonly Vector3 ShieldRest = new Vector3(-0.40f, -0.30f, 0.50f);
        public static readonly Vector3 ShieldRaised = new Vector3(-0.17f, -0.17f, 0.52f);
        public static readonly Vector3 ShieldRestEuler = new Vector3(0f, 14f, 0f);
        public static readonly Vector3 ShieldRaisedEuler = new Vector3(-6f, 4f, 0f);

        /// <summary>Elbow hints (aim frame): below and a little outwards.</summary>
        private static readonly Vector3 RightPole = new Vector3(0.7f, -1f, -0.4f);
        private static readonly Vector3 LeftPole = new Vector3(-0.7f, -1f, -0.4f);
        private static Material _guardDirectionMaterial;
        private static Mesh _guardDirectionMesh;

        /// <summary>The weapon rig's blade runs along its forward; a model grip has the tip on +Y and the flat face on +Z.</summary>
        public static readonly Quaternion GripFromRig = Quaternion.Euler(90f, 0f, 0f);

        /// <summary>The shield model is shown a little smaller than its world size so that it frames the view instead of filling it.</summary>
        public const float ShieldScale = 0.72f;
        // The kite shield's rear straps sit above its geometric centre.
        public static readonly Vector3 ShieldMountOffset = new Vector3(0f, -0.115f, 0f);

        public const float MinModelScale = 0.9f;
        public const float MaxModelScale = 1.1f;
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
        private EquippedArmorView _gloves;
        private Camera _camera;
        private Transform _frame;
        private WeaponVisualRig _visual;
        private HealthComponent _health;
        private GameObject _mainHand;
        private GameObject _offHand;
        private GameObject _guardDirectionMarker;
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
        private bool _hasPresentedPose;
        private Vector3 _presentedBase, _guardFromBase;
        private Quaternion _presentedRotation = Quaternion.identity, _guardFromRotation = Quaternion.identity;
        private float _guardChangedAt = float.NegativeInfinity;
        private const float GuardTransitionSeconds = 0.14f;
        private float _bobPhase;
        private Vector3 _joltRight;
        private Vector3 _joltLeft;
        private Transform _upperR, _lowerR, _handR, _upperL, _lowerL, _handL;

        public Animator Animator => animator;
        public Transform GripRight => gripRight;
        public Transform GripLeft => gripLeft;
        public Transform SupportGrip => _leftWeaponGrip;
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
            _drink = PotionDrinkPresentation.Ensure(rig);
            _camera = camera;
            _set = set;
            _levelMask = LayerMask.GetMask(GameConfig.LevelLayerName);
            if (animator == null) animator = GetComponentInChildren<Animator>(true);
            _gloves = GetComponent<EquippedArmorView>() ?? gameObject.AddComponent<EquippedArmorView>();
            _gloves.Bind(rig != null ? rig.Equipment : null, handsOnly: true);
            _gloves.Refresh();
            transform.localPosition = restPosition;
            transform.localRotation = Quaternion.identity;
            if (rig != null) _lastRigPosition = rig.transform.position;
            _frame = rig != null && rig.Look != null ? rig.Look.AimTransform : (camera != null ? camera.transform : transform.parent);

            if (_health != null) _health.Damaged -= OnDamaged;
            _health = rig != null ? rig.Health : null;
            if (_health != null) _health.Damaged += OnDamaged;
        }

        /// <summary>The weapon rig whose pose the right hand follows (rebuilt by the loadout whenever the weapon changes).</summary>
        public void SetVisual(WeaponVisualRig visual)
        {
            _visual = visual;
            if (_visual != null) _visual.ExternalTrail = true;
        }

        private SwingTrail _trail;
        private bool _trailOn;
        private float _swingWeight;

        /// <summary>The swing ribbon follows the sword the player actually sees, not the far damage segment.</summary>
        private void UpdateViewTrail()
        {
            bool active = _visual != null && _visual.InActivePhase && _mainHand != null && gripRight != null && _mainEntry != null;
            if (active)
            {
                if (!_trailOn)
                {
                    if (_trail == null) _trail = SwingTrail.Create();
                    _trail.Begin(_visual.ActiveTrailMaterial);
                    _trailOn = true;
                }
                float length = Mathf.Max(0.2f, _mainEntry.length * WeaponScale);
                Vector3 grip = gripRight.position, along = gripRight.up;
                _trail.Add(grip + along * (length * 0.82f), grip + along * length);
            }
            else if (_trailOn)
            {
                _trail.EndStroke();
                _trailOn = false;
            }
        }

        private void OnDestroy()
        {
            _fingerPoseHandler?.Dispose();
            if (_health != null) _health.Damaged -= OnDamaged;
        }

        /// <summary>Puts the models of the held weapons into the hands. Returns true when the main hand weapon has a model.</summary>
        private ItemData _utilityItem;
        private PotionDrinkPresentation _drink;
        private float _drinkRaise;
        private PotionBottleVisual _bottle;
        private BandageVisual _bandage;
        public bool SetUtility(ItemData item)
        {
            SetWeapons(null, null);
            _utilityItem=item;
            if (item == null || gripRight == null) return false;
            _mainHand=HandheldUtilityVisual.Create(item,gripRight);
            _bottle = _mainHand != null ? _mainHand.GetComponent<PotionBottleVisual>() : null;
            _bandage = _mainHand != null ? _mainHand.GetComponent<BandageVisual>() : null;
            if (_bottle != null) _mainHand.transform.localPosition = new Vector3(0f, -.055f, 0f);
            _drinkRaise = 0f;
            if (_mainHand != null) SetLayerRecursive(_mainHand,LayerMask.NameToLayer("Ignore Raycast"));
            _hasWeaponModel=_mainHand != null;
            return _hasWeaponModel;
        }

        public bool SetWeapons(WeaponData main, WeaponData off)
        {
            _utilityItem=null;
            _bottle = null;
            _bandage = null;
            _drinkRaise = 0f;
            DestroyHeld(ref _mainHand);
            DestroyHeld(ref _offHand);
            DestroyHeld(ref _guardDirectionMarker);
            _hasWeaponModel = false;
            _weaponGripsCalibrated = false;
            _hasPresentedPose = false;
            _guardChangedAt = float.NegativeInfinity;
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
                    if (_mainHand != null && main.CanBlock) _guardDirectionMarker = CreateGuardDirectionMarker(_mainHand.transform);
                }
            }

            // A two-handed weapon is held with both hands: no shield, whatever the off hand slot says.
            bool twoHanded = main != null && main.twoHanded;
            WeaponViewModelSet.Entry shield = _set != null && off != null && off.IsShield && !twoHanded ? _set.Find(off.weaponId) : null;
            if (shield != null && gripLeft != null) _offHand = Hold(shield, gripLeft, "Held_" + off.weaponId);

            UpdateGuardDirectionMarker();
            ApplyBlockClip(_offHand != null);
            return _hasWeaponModel || (main != null && (main.weaponId == "hollow_bow" || main.CanCrossbowGuard));
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

        /// <summary>Small camera-visible arrow on block-capable blades; points along the same local blade axis as the guard pose.</summary>
        private static GameObject CreateGuardDirectionMarker(Transform weapon)
        {
            if (weapon == null) return null;
            if (_guardDirectionMaterial == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Unlit/Color");
                if (shader == null) shader = Shader.Find("Standard");
                if (shader == null) return null;
                _guardDirectionMaterial = new Material(shader)
                {
                    name = "Emberdelve_GuardDirection",
                    hideFlags = HideFlags.HideAndDontSave,
                    color = new Color(1f, 0.68f, 0.18f, 1f)
                };
                if (_guardDirectionMaterial.HasProperty("_BaseColor")) _guardDirectionMaterial.SetColor("_BaseColor", _guardDirectionMaterial.color);
                if (_guardDirectionMaterial.HasProperty("_EmissionColor"))
                {
                    _guardDirectionMaterial.SetColor("_EmissionColor", _guardDirectionMaterial.color);
                    _guardDirectionMaterial.EnableKeyword("_EMISSION");
                }
            }

            GameObject marker = new GameObject("Guard_Direction_Marker");
            marker.transform.SetParent(weapon, false);
            MeshFilter filter = marker.AddComponent<MeshFilter>();
            MeshRenderer renderer = marker.AddComponent<MeshRenderer>();
            if (_guardDirectionMesh == null)
            {
                _guardDirectionMesh = new Mesh { name = "Emberdelve_GuardDirectionArrow", hideFlags = HideFlags.HideAndDontSave };
                _guardDirectionMesh.vertices = new[]
                {
                    new Vector3(0.032f, 0.16f, 0.045f), new Vector3(0.052f, 0.16f, 0.045f),
                    new Vector3(0.032f, 0.34f, 0.045f), new Vector3(0.052f, 0.34f, 0.045f),
                    new Vector3(0.012f, 0.32f, 0.045f), new Vector3(0.042f, 0.43f, 0.045f), new Vector3(0.072f, 0.32f, 0.045f)
                };
                _guardDirectionMesh.triangles = new[] { 0, 1, 2, 1, 3, 2, 4, 6, 5 };
                _guardDirectionMesh.RecalculateNormals();
                _guardDirectionMesh.RecalculateBounds();
            }
            filter.sharedMesh = _guardDirectionMesh;
            renderer.sharedMaterial = _guardDirectionMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            marker.SetActive(false);
            return marker;
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
            if (held != _blockHeld && _hasPresentedPose)
            {
                _guardFromBase = _presentedBase;
                _guardFromRotation = _presentedRotation;
                _guardChangedAt = Time.time;
            }
            _blockHeld = held;
            UpdateGuardDirectionMarker();
        }

        private void UpdateGuardDirectionMarker()
        {
            if (_guardDirectionMarker != null)
                _guardDirectionMarker.SetActive(_blockHeld && _offHand == null && (_rig == null || !_rig.Defense.HasShield));
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

            if (damage.Blocked)
            {
                PlayConfirmedBlockImpact(_rig != null && _rig.Defense.HasShield);
                return;
            }

            if (damage.Amount <= 0f) return;
            AddJolt(new Vector3(away.x * 0.05f, -0.035f, -0.045f), false);
            AddJolt(new Vector3(away.x * 0.05f, -0.035f, -0.045f), true);
        }

        /// <summary>Presentation only: called from authoritative damage feedback, including the network hit report.</summary>
        public void PlayConfirmedBlockImpact(bool shieldBlock)
        {
            if (!shieldBlock && _visual != null) _visual.PlayBlockImpact();
            AddJolt(new Vector3(0f, 0.025f, -0.07f), shieldBlock);
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

        private float _bowDraw;
        private Transform _leftWeaponGrip;
        private bool _weaponGripsCalibrated;


        private void LateUpdate()
        {
            if (_camera == null) return;
            _gloves?.Refresh();
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
            Quaternion localRotation = WeaponVisualRig.ReadyRotation;
            if (_visual != null)
            {
                localBase = frame.InverseTransformPoint(_visual.transform.position);
                localRotation = Quaternion.Inverse(frame.rotation) * _visual.transform.rotation;
            }

            bool swinging = _visual != null && _visual.IsSwinging;
            _swingWeight = Mathf.MoveTowards(_swingWeight, swinging ? 1f : 0f, (swinging ? 14f : 4f) * dt);
            // A shield does the blocking: the sword stays lowered at the side instead of crossing in front of the shield.
            if (_offHand != null && _blockHeld && !swinging)
            {
                localBase = PatternRest;
                localRotation = ShieldGuardSwordRotation;
            }

            float guardTarget = _blockHeld ? 1f : 0f;
            _guardBlend = Mathf.MoveTowards(_guardBlend, guardTarget, 7f * dt);
            // Ease only the rendered hands when entering/leaving guard. The weapon rig also
            // supplies the authority's guard volume, so its transform must remain immediate.
            if (_visual == null || !_visual.IsSwinging)
            {
                float blend = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((Time.time - _guardChangedAt) / GuardTransitionSeconds));
                localBase = Vector3.Lerp(_guardFromBase, localBase, blend);
                localRotation = Quaternion.Slerp(_guardFromRotation, localRotation, blend);
            }
            else _guardChangedAt = float.NegativeInfinity;
            _presentedBase = localBase;
            _presentedRotation = localRotation;
            _hasPresentedPose = true;
            Pose(frame, _camera, localBase, localRotation, _guardBlend, bob + _joltRight, bob + _joltLeft);
            UpdateViewTrail();
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

            if (_utilityItem != null || (_mainData != null && !_mainData.IsRanged)) PoseFingers(false, 0f);

            // Scale the mannequin and hold the middle of the shoulders at its viewmodel anchor.
            Transform model = animator.transform;
            model.localScale = Vector3.one * ModelScale;
            Vector3 mid = (_upperR.position + _upperL.position) * 0.5f;
            model.position += frame.TransformPoint(ShoulderCenter) - mid;

            if (_mainData != null && _mainData.CanCrossbowGuard && _visual != null)
            {
                RangedCombat ranged = _rig != null ? _rig.GetComponent<RangedCombat>() : null;
                PoseCrossbow(frame, _visual.transform, !_blockHeld && ranged != null && ranged.IsReloading,
                    ranged != null ? ranged.ReloadProgress : 0f, _blockHeld || guardBlend > .5f);
                return;
            }

            if (_mainData != null && _mainData.weaponId == "hollow_bow" && _visual != null)
            {
                var bow = _visual.GetComponentInChildren<BowPresentation>();
                if (_blockHeld && _mainData.CanBowGuard && bow != null)
                {
                    PoseBowGuard(frame, bow);
                    return;
                }
                var skills = _rig != null ? _rig.GetComponent<Emberdelve.Skills.SkillCaster>() : null;
                bool prepared = skills != null && skills.PreparedSlot >= 0;
                bool held = _rig != null && _rig.Input != null && _rig.Input.PrimaryHeld && !(skills != null && skills.SuppressPrimary);
                bool draw = !InventoryInputFocus.OwnsEscape && _rig != null && !_rig.IsDead && _rig.Input != null && _rig.Input.GameplayInputEnabled && (prepared || held);
                // Ordinary draw comes from RangedCombat's captured, stat-scaled clock. Never run a second authored-time clock.
                // Prepared skills have their own held pose and do not use the ordinary ranged draw state.
                _bowDraw = prepared
                    ? Mathf.MoveTowards(_bowDraw, draw ? 1f : 0f, Time.deltaTime * (draw ? 1f / Mathf.Max(.1f, _mainData.drawTime) : 16f))
                    : _visual.DrawProgress;
                if (bow != null)
                {
                    PoseBow(frame, bow, _bowDraw);
                }
                return;
            }

            EnsureWeaponGrips();

            // Right hand: the rig's pose, the base brought to arm's reach and into the frustum.
            Vector3 handLocal = ClampToView(camera, ViewHand(localBase) + offsetRight);
            // Keep the sword hilt mostly below frame; guard still raises it to its defensive pose.
            if (_mainData != null && (_mainData.weaponId.Contains("sword") || _mainData.weaponId.Contains("rapier")))
            {
                // Ready framing only: low and to the right with the forearm in view, blade leaning forward
                // instead of standing up through the whole screen. Swings and the blade guard keep the rig's pose.
                float ready = (1f - Mathf.SmoothStep(0f, 1f, guardBlend)) * (1f - Mathf.SmoothStep(0f, 1f, _swingWeight));
                if (_offHand != null && _blockHeld) ready = Mathf.Max(ready, 1f - Mathf.SmoothStep(0f, 1f, _swingWeight));
                handLocal = Vector3.Lerp(handLocal, ClampToView(camera, SwordReadyHand + offsetRight), ready);
                localRotation = Quaternion.Slerp(localRotation, SwordReadyRotation, ready);
            }
            // A long burning head needs its own ready framing: the sword's leftward yaw
            // sends it through the top edge. Keep this cosmetic offset out of swing/guard poses.
            if (_mainData != null && _mainData.weaponId == HandheldTorchVisual.WeaponId &&
                (_visual == null || !_visual.IsSwinging))
            {
                float ready = 1f - Mathf.SmoothStep(0f, 1f, guardBlend);
                handLocal = Vector3.Lerp(handLocal,
                    ClampToView(camera, new Vector3(.34f, -.36f, .52f) + offsetRight), ready);
                localRotation = Quaternion.Slerp(localRotation, Quaternion.Euler(-74f, 12f, 0f), ready);
            }
            if (_utilityItem != null)
            {
                bool torch = _utilityItem.itemId == "pitch_torch";
                handLocal = ClampToView(camera, (torch ? new Vector3(.34f,-.36f,.52f) : new Vector3(.13f,-.22f,.45f)) + offsetRight);
                localRotation = Quaternion.Euler(torch ? -74f : -82f, torch ? 12f : 15f, 0f);
                if (_bottle != null)
                {
                    bool drinking = _drink != null && _drink.IsDrinking;
                    float progress = drinking ? _drink.Progress : 0f;
                    _drinkRaise = Mathf.MoveTowards(_drinkRaise, drinking ? PotionDrinkPresentation.Raise(progress) : 0f, Time.deltaTime * 7f);
                    handLocal = Vector3.Lerp(handLocal, new Vector3(.08f, -.12f, .34f) + offsetRight * .25f, _drinkRaise);
                    localRotation = Quaternion.Slerp(localRotation, Quaternion.Euler(-172f, 0f, -12f), _drinkRaise);
                    _bottle.SetDrinkProgress(progress, drinking);
                }
                if (_bandage != null)
                {
                    bool applying = _drink != null && _drink.IsDrinking;
                    float p = applying ? _drink.Progress : 0f;
                    _drinkRaise = Mathf.MoveTowards(_drinkRaise, applying ? PotionDrinkPresentation.Raise(p) : 0f, Time.deltaTime * 7f);
                    float turn = p * Mathf.PI * 12f;
                    Vector3 wrap = new Vector3(-.015f + .055f * Mathf.Sin(turn), -.19f + .035f * Mathf.Cos(turn), .43f);
                    handLocal = Vector3.Lerp(handLocal, wrap, _drinkRaise);
                    localRotation = Quaternion.Slerp(localRotation, Quaternion.Euler(-60f, 15f, Mathf.Sin(turn) * 30f), _drinkRaise);
                    _bandage.SetApplyProgress(p, applying);
                }
            }
            Quaternion gripRotation = frame.rotation * localRotation * GripFromRig;
            Vector3 gripPosition = frame.TransformPoint(handLocal);
            if (gripRight != null)
            {
                SolveArm(_upperR, _lowerR, _handR, gripRight, gripPosition, gripRotation, frame.rotation * RightPole);
                if (_bottle == null && _bandage == null)
                {
                    // A handle is free to turn in the fist. Roll the grip about the handle so the knuckles continue the
                    // forearm (a straight wrist) instead of keeping a fixed roll that bends the wrist sideways.
                    for (int pass = 0; pass < 3; pass++)
                    {
                        // A fist holds its handle across the palm: keep the handle roughly perpendicular to the forearm
                        // (25 degrees of wrist deviation allowed) instead of letting it run along the arm.
                        Vector3 forearm = (_handR.position - _lowerR.position).normalized;
                        Vector3 handle = gripRotation * Vector3.up;
                        Vector3 across = Vector3.ProjectOnPlane(handle, forearm);
                        if (across.sqrMagnitude < .04f) across = Vector3.ProjectOnPlane(frame.up, forearm);
                        if (across.sqrMagnitude > 1e-6f)
                        {
                            // Swings keep the blade exactly where the pattern sends it; only the ready pose is straightened.
                            Vector3 natural = Vector3.RotateTowards(across.normalized, handle, Mathf.Lerp(25f, 180f, Mathf.SmoothStep(0f, 1f, _swingWeight)) * Mathf.Deg2Rad, 0f);
                            gripRotation = Quaternion.FromToRotation(handle, natural) * gripRotation;
                        }
                        gripRotation = RollToForearm(gripRotation, forearm, _handR, gripRight);
                        SolveArm(_upperR, _lowerR, _handR, gripRight, gripPosition, gripRotation, frame.rotation * RightPole);
                    }
                }
                if (_mainHand != null) _mainHand.transform.localScale = Vector3.one * WeaponScale;
            }

            if (_bottle != null && _drink != null && _drink.IsDrinking && gripLeft != null)
            {
                float p = _drink.Progress;
                float uncork = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.12f, .27f, p));
                Vector3 cork = gripRight.TransformPoint(new Vector3(0f, .145f, 0f));
                Vector3 leftRest = frame.TransformPoint(new Vector3(-.20f, -.28f, .35f));
                SolveArm(_upperL, _lowerL, _handL, gripLeft, Vector3.Lerp(leftRest, cork, uncork),
                    frame.rotation * Quaternion.Euler(15f, 30f, -65f), frame.rotation * LeftPole);
            }

            if (_bandage != null && gripLeft != null && _drinkRaise > 0f)
            {
                Vector3 leftRest = new Vector3(-.20f, -.28f, .35f);
                Vector3 wrappedWrist = new Vector3(-.09f, -.18f, .43f);
                SolveArm(_upperL, _lowerL, _handL, gripLeft,
                    frame.TransformPoint(Vector3.Lerp(leftRest, wrappedWrist, _drinkRaise)),
                    frame.rotation * Quaternion.Euler(10f, 65f, -75f), frame.rotation * LeftPole);
            }

            // Left hand: on the shield (rest / raised), or on the second grip of a two-handed weapon, otherwise left to the idle pose.
            if (_offHand != null && gripLeft != null)
            {
                _offHand.transform.localScale = Vector3.one * ShieldScale;
                _offHand.transform.localPosition = ShieldMountOffset;
                Vector3 shieldPosition = Vector3.Lerp(ShieldRest, ShieldRaised, guardBlend) + offsetLeft;
                Quaternion shieldRotation = Quaternion.Euler(Vector3.Lerp(ShieldRestEuler, ShieldRaisedEuler, guardBlend));
                SolveArm(_upperL, _lowerL, _handL, gripLeft, frame.TransformPoint(ClampToView(camera, shieldPosition)), frame.rotation * shieldRotation, frame.rotation * LeftPole);
                if (_blockHeld)
                {
                    GuardShieldShape shield = GuardShieldPose.Resolve(frame.position, frame.rotation);
                    // Raised defense follows the physical surface immediately; cosmetic bob/clamping
                    // must not make the shield appear somewhere the server cannot block.
                    SolveArm(_upperL, _lowerL, _handL, gripLeft,
                        frame.TransformPoint(GuardShieldPose.Grip), shield.Rotation, frame.rotation * LeftPole);
                    GuardShieldPose.ApplyModel(_offHand.transform, shield);
                }
            }
            else if (_mainData != null && _mainData.twoHanded && gripRight != null && gripLeft != null)
            {
                Vector3 axis = _mainHand != null ? _mainHand.transform.up : gripRight.up;
                Vector3 second = gripRight.position - axis * (SecondGripOffset * WeaponScale);
                // Solve the palm's authored grip, not the wrist. Keep its grasp aligned with the
                // handle throughout the swing instead of preserving the idle wrist rotation.
                SolveArm(_upperL, _lowerL, _handL, _leftWeaponGrip, second, gripRight.rotation, frame.rotation * LeftPole);
            }
        }

        // Handle axis is the knuckle row (little -> index), not an arbitrary world axis
        // measured from a lantern/shield animation. +Z follows the fingers from the wrist.
        public static bool CalibrateWeaponGrip(Animator animator, Transform grip, bool left)
        {
            if (animator == null || grip == null) return false;
            var hand = animator.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            var index = animator.GetBoneTransform(left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal);
            var little = animator.GetBoneTransform(left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal);
            var middle = animator.GetBoneTransform(left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
            var curl = animator.GetBoneTransform(left ? HumanBodyBones.LeftMiddleDistal : HumanBodyBones.RightMiddleDistal);
            var bend = animator.GetBoneTransform(left ? HumanBodyBones.LeftMiddleIntermediate : HumanBodyBones.RightMiddleIntermediate);
            if (hand == null || index == null || little == null || middle == null || curl == null) return false;
            Vector3 axis = (index.position - little.position).normalized;
            Vector3 forward = Vector3.ProjectOnPlane(middle.position - hand.position, axis).normalized;
            if (axis.sqrMagnitude < .5f || forward.sqrMagnitude < .5f) return false;
            // The handle runs through the ring the closed middle finger makes: the centre of its joints.
            grip.position = bend != null ? (middle.position + bend.position + curl.position) / 3f
                : Vector3.Lerp(middle.position, curl.position, .5f);
            grip.rotation = Quaternion.LookRotation(forward, axis);
            return true;
        }

        private void EnsureWeaponGrips()
        {
            if (_weaponGripsCalibrated || animator == null || _handL == null) return;
            if (_leftWeaponGrip == null)
            {
                _leftWeaponGrip = new GameObject("WeaponGrip_L").transform;
                _leftWeaponGrip.SetParent(_handL, false);
                _leftWeaponGrip.localPosition = gripLeft.localPosition;
                _leftWeaponGrip.localRotation = gripLeft.localRotation;
            }
            CalibrateWeaponGrip(animator, gripRight, false);
            // Roll the palm around a forward sword handle so the forearm enters from the side,
            // instead of forcing the wrist over the top of the hilt. Bow grips stay upright.
            if (_mainData != null && !_mainData.IsRanged)
                gripRight.localRotation *= Quaternion.AngleAxis(90f, Vector3.up);
            CalibrateWeaponGrip(animator, _leftWeaponGrip, true);
            _weaponGripsCalibrated = true;
        }

        private HumanPoseHandler _fingerPoseHandler;
        private HumanPose _fingerPose;
        private Transform[] _poseBones;
        private Vector3[] _posePositions;
        private Quaternion[] _poseRotations;
        private static int[,,] FingerMuscles;
        private Transform _stringGrip;

        private static int[,,] FindFingerMuscles()
        {
            var result = new int[2, 5, 3];
            string[] fingers = { "Thumb", "Index", "Middle", "Ring", "Little" };
            for (int side = 0; side < 2; side++)
                for (int finger = 0; finger < 5; finger++)
                    for (int joint = 0; joint < 3; joint++)
                    {
                        result[side, finger, joint] = -1;
                        for (int muscle = 0; muscle < HumanTrait.MuscleCount; muscle++)
                        {
                            string name = HumanTrait.MuscleName[muscle].Replace('.', ' ');
                            if (name.StartsWith(side == 0 ? "Left" : "Right") && name.Contains(fingers[finger]) &&
                                name.Contains(" " + (joint + 1) + " Stretched"))
                                result[side, finger, joint] = muscle;
                        }
                    }
            return result;
        }

        // Apply normalized humanoid finger curls before arm IK; preserve the current body pose.
        private void PoseFingers(bool bow, float draw)
        {
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman) return;
            if (FingerMuscles == null) FingerMuscles = FindFingerMuscles();
            if (_fingerPoseHandler == null) _fingerPoseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
            if (_poseBones == null)
            {
                int count = (int)HumanBodyBones.LastBone;
                _poseBones = new Transform[count]; _posePositions = new Vector3[count]; _poseRotations = new Quaternion[count];
                for (int i = 0; i < count; i++) _poseBones[i] = animator.GetBoneTransform((HumanBodyBones)i);
            }
            Vector3 rootPosition = animator.transform.localPosition;
            Quaternion rootRotation = animator.transform.localRotation;
            for (int i = 0; i < _poseBones.Length; i++) if (_poseBones[i] != null)
            {
                _posePositions[i] = _poseBones[i].localPosition;
                _poseRotations[i] = _poseBones[i].localRotation;
            }
            _fingerPoseHandler.GetHumanPose(ref _fingerPose);
            for (int side = 0; side < 2; side++)
            {
                if (!bow && side == 0 && _offHand == null && !(_mainData != null && _mainData.twoHanded)) continue;
                for (int finger = 0; finger < 5; finger++)
                    for (int joint = 0; joint < 3; joint++)
                    {
                        int index = FingerMuscles[side, finger, joint];
                        if (index < 0) continue;
                        // Closed around a handle: every finger joint flexed, the thumb opposed over the index.
                        float curl = joint == 0 ? -.85f : joint == 1 ? -1f : -.9f;
                        if (finger == 0) curl = joint == 0 ? .35f : joint == 1 ? -.55f : -.6f;
                        if (bow && side == 1)
                        {
                            // Index above the arrow, middle/ring below; thumb stays clear of the string.
                            float hook = finger == 0 ? -.3f : joint == 0 ? .05f : joint == 1 ? -.9f : -.6f;
                            curl = Mathf.Lerp(finger == 0 ? .4f : .65f, hook, Mathf.Lerp(.65f, 1f, Mathf.Clamp01(draw)));
                        }
                        _fingerPose.muscles[index] = curl;
                    }
            }
            _fingerPoseHandler.SetHumanPose(ref _fingerPose);
            // HumanPose also retargets the body root. Keep the already-scaled viewmodel and
            // animated arms exactly where they were; retain only the requested finger rotations.
            animator.transform.SetLocalPositionAndRotation(rootPosition, rootRotation);
            for (int i = 0; i < _poseBones.Length; i++) if (_poseBones[i] != null)
            {
                _poseBones[i].localPosition = _posePositions[i];
                bool finger = i >= (int)HumanBodyBones.LeftThumbProximal && i <= (int)HumanBodyBones.RightLittleDistal;
                if (!finger) _poseBones[i].localRotation = _poseRotations[i];
            }
        }

        public static readonly Vector3 CrossbowRightGrip = new Vector3(0f, -.15f, -.01f);
        public static readonly Vector3 CrossbowSupportGrip = new Vector3(0f, -.07f, .24f);

        /// <summary>Original-authored support/string pull/bolt seating motion; no gameplay clock or ammunition changes.</summary>
        public static Vector3 CrossbowLeftGrip(bool reloading, float progress)
        {
            if (!reloading || float.IsNaN(progress) || float.IsInfinity(progress)) return CrossbowSupportGrip;
            float p = Mathf.Clamp01(progress);
            Vector3 stringGrip = new Vector3(0f, 0f, -.025f);
            Vector3 pulledString = new Vector3(0f, 0f, -.22f);
            Vector3 boltSeat = new Vector3(0f, .045f, .18f);
            if (p < .2f) return Vector3.Lerp(CrossbowSupportGrip, stringGrip, Mathf.SmoothStep(0f, 1f, p / .2f));
            if (p < .5f) return Vector3.Lerp(stringGrip, pulledString, Mathf.SmoothStep(0f, 1f, (p - .2f) / .3f));
            if (p < .75f) return Vector3.Lerp(pulledString, boltSeat, Mathf.SmoothStep(0f, 1f, (p - .5f) / .25f));
            return Vector3.Lerp(boltSeat, CrossbowSupportGrip, Mathf.SmoothStep(0f, 1f, (p - .75f) / .25f));
        }

        public void PoseCrossbow(Transform frame, Transform crossbow, bool reloading, float reloadProgress, bool guarding = false)
        {
            if (!ResolveBones() || frame == null || crossbow == null) return;
            PoseFingers(false, 0f);
            EnsureWeaponGrips();
            // The procedural weapon stays in its physical pose; only the mannequin follows it.
            SolveArm(_upperR, _lowerR, _handR, gripRight, crossbow.TransformPoint(CrossbowRightGrip),
                crossbow.rotation * Quaternion.Euler(-12f, 0f, 0f), frame.rotation * RightPole);
            // Calibrated grip +Z runs wrist-to-fingers. At rest fingers point up around the stock,
            // keeping the wrist below the sightline. Raised guard retains the reachable overhead grip.
            float turn = 0f;
            if (reloading && !float.IsNaN(reloadProgress) && !float.IsInfinity(reloadProgress))
            {
                float p = Mathf.Clamp01(reloadProgress);
                // Turn over the stock to pull the string and seat the bolt from above.
                // Match the resting orientation at both ends, without moving the physical weapon.
                turn = Mathf.SmoothStep(0f, 1f, p / .2f)
                    * (1f - Mathf.SmoothStep(0f, 1f, (p - .75f) / .25f));
            }
            SolveArm(_upperL, _lowerL, _handL, _leftWeaponGrip, crossbow.TransformPoint(CrossbowLeftGrip(reloading, reloadProgress)),
                crossbow.rotation * Quaternion.Euler(guarding ? 90f : Mathf.Lerp(-90f, 25f, turn), 0f, 0f), frame.rotation * LeftPole);
        }

        public void PoseBowGuard(Transform frame, BowPresentation bow)
        {
            if (!ResolveBones() || frame == null || bow == null) return;
            PoseFingers(false, 0f);
            EnsureWeaponGrips();
            bow.SetDraw(0f);
            SolveArm(_upperL, _lowerL, _handL, _leftWeaponGrip, bow.transform.TransformPoint(BowGuardPose.LeftGrip),
                bow.transform.rotation, frame.rotation * LeftPole);
            SolveArm(_upperR, _lowerR, _handR, gripRight, bow.transform.TransformPoint(BowGuardPose.RightGrip),
                bow.transform.rotation, frame.rotation * RightPole);
        }

        public void PoseBow(Transform frame, BowPresentation bow, float draw)
        {
            if (!ResolveBones() || frame == null || bow == null) return;
            PoseFingers(true, draw);
            EnsureWeaponGrips();
            bow.SetDraw(draw);
            // Bow handle and string are vertical; the sword's +90-degree mapping is wrong here.
            SolveArm(_upperL, _lowerL, _handL, _leftWeaponGrip, bow.transform.position,
                bow.transform.rotation, frame.rotation * LeftPole);
            if (_stringGrip == null)
            {
                _stringGrip = new GameObject("StringHook_R").transform;
                _stringGrip.SetParent(_handR, false);
            }
            var index = animator.GetBoneTransform(HumanBodyBones.RightIndexDistal);
            var middle = animator.GetBoneTransform(HumanBodyBones.RightMiddleDistal);
            _stringGrip.position = index != null && middle != null ? Vector3.Lerp(index.position, middle.position, .5f) : gripRight.position;
            _stringGrip.rotation = gripRight.rotation;
            SolveArm(_upperR, _lowerR, _handR, _stringGrip, bow.transform.TransformPoint(bow.Nock),
                bow.transform.rotation, frame.rotation * RightPole);
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

        private Vector3 _fingerLocalR;

        /// <summary>Turns <paramref name="target"/> about its handle axis (+Y) so the hand's finger direction follows the forearm.</summary>
        private Quaternion RollToForearm(Quaternion target, Vector3 forearm, Transform hand, Transform effector)
        {
            if (_fingerLocalR == Vector3.zero)
            {
                Transform middle = animator != null ? animator.GetBoneTransform(HumanBodyBones.RightMiddleProximal) : null;
                if (middle == null) return target;
                _fingerLocalR = Quaternion.Inverse(hand.rotation) * (middle.position - hand.position);
            }
            Quaternion effectorLocal = Quaternion.Inverse(hand.rotation) * effector.rotation;
            Quaternion handRotation = target * Quaternion.Inverse(effectorLocal);
            Vector3 axis = target * Vector3.up;
            Vector3 fingers = Vector3.ProjectOnPlane(handRotation * _fingerLocalR, axis);
            Vector3 wanted = Vector3.ProjectOnPlane(forearm, axis);
            if (fingers.sqrMagnitude < 1e-8f || wanted.sqrMagnitude < 1e-8f) return target;
            return Quaternion.AngleAxis(Vector3.SignedAngle(fingers, wanted, axis), axis) * target;
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
            // Rotation-only local offset retains the mannequin's scale. InverseTransformPoint
            // removes that scale and leaves the grip short of its target on scaled viewmodels.
            Vector3 effectorLocal = Quaternion.Inverse(hand.rotation) * (effector.position - hand.position);
            Quaternion effectorRotationLocal = Quaternion.Inverse(hand.rotation) * effector.rotation;
            Quaternion handRotation = targetRotation * Quaternion.Inverse(effectorRotationLocal);
            Vector3 wrist = targetPosition - handRotation * effectorLocal;

            Vector3 a = upper.position;
            float l1 = (lower.position - a).magnitude;
            float l2 = (hand.position - lower.position).magnitude;
            if (l1 < 1e-4f || l2 < 1e-4f) return;
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
            if (pole.sqrMagnitude < 1e-6f) pole = Vector3.ProjectOnPlane(Vector3.right, direction);
            pole.Normalize();
            Vector3 elbow = a + direction * (l1 * cos) + pole * (l1 * sin);

            upper.rotation = Quaternion.FromToRotation(lower.position - a, elbow - a) * upper.rotation;
            lower.rotation = Quaternion.FromToRotation(hand.position - lower.position, wrist - lower.position) * lower.rotation;
            hand.rotation = handRotation;
        }
    }
}
