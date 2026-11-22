using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;

namespace Emberdelve.Player
{
    /// <summary>
    /// Temporary first-person weapon visuals made from primitives, plus swing/draw motion.
    /// Replace WeaponVisualBuilder with a model-based builder (or a real prefab) later: nothing in the
    /// combat code changes because it only asks for a muzzle position and animation hooks.
    ///
    /// D1a: a melee swing is no longer a hand-tuned lerp. While a swing plays, the rig is posed every frame by
    /// <see cref="SwingPatternData.Evaluate"/> in the world frame of the player's aim transform - the very pattern
    /// and frame the combat authority sweeps its damage segment with. The thin "Sweep_Edge" bar and the
    /// afterimage ribbon (<see cref="SwingTrail"/>) are drawn along that segment, so the visible path is the
    /// real one (the blockout sword model itself is only ~0.8 m long; the bar shows the full reach).
    /// </summary>
    public sealed class WeaponVisualRig : MonoBehaviour
    {
        // Upright relaxed grip: blade leans 35 degrees from vertical toward the view centre.
        public static readonly Quaternion ReadyRotation = Quaternion.Euler(-55f, -60f, 0f);

        private Transform _muzzle;
        private Transform _arrow;
        private Transform _string;
        private Transform _stringLeft, _stringRight;
        private bool _isRanged;
        private bool _crossbow;
        private bool _bowGuard;

        // ---- swept melee presentation (D1a) -------------------------------------------------------------
        private Transform _aimFrame;
        private Transform _edge;
        private SwingPatternData _pattern;
        private SwingTiming _timing;
        private float _bladeLength;
        private float _visibleLength;
        private int _clipMask;
        private float _swingStart;
        private bool _swinging;
        private bool _swingSoundPlayed;
        private string _soundWeaponId;
        private bool _recoiling;
        private float _recoilStart;
        // Combo chain: while the attack button is held the weapon stays where a strike ended until the next step starts from there;
        // with the button released it goes straight back to rest (no waiting for the combo window).
        private bool _chainHold, _holding, _releasing;
        private float _holdUntil, _releaseStart, _holdBlend;
        private Vector3 _holdBase;
        private Quaternion _holdRotation;
        public const float ReleaseSeconds = 0.16f;
        /// <summary>A held button never keeps the end pose longer than this past the strike (a refused or blocked next step).</summary>
        public const float MaxChainHoldSeconds = 2f;
        private const float HoldBlendRate = 10f;

        /// <summary>The local attack button is held (set every frame by the first-person arms): the chain keeps the end pose.</summary>
        public bool ChainInputHeld { get; set; }
        private Vector3 _transitionBase;
        private Quaternion _transitionRotation;
        private Vector3 _restLocalPosition;
        private Quaternion _restLocalRotation;
        private Vector3 _restLocalScale;
        private SwingTrail _trail;
        private Transform _guardBlade;
        private bool _twoHanded; // picks the guard stance: the hanging parry for two-handed swords, the high ward otherwise

        /// <summary>True for a two-handed weapon: its guard is the hanging parry (GuardBladePose), a one-handed one keeps the high ward.</summary>
        public bool TwoHandedGuard { get => _twoHanded; set => _twoHanded = value; }
        private bool _guardHeld;
        private float _blockImpactUntil;
        private const float BlockImpactDuration = 0.22f;
        private static readonly Vector3 GuardBladeUnitSize = Vector3.one;

        /// <summary>The afterimage ribbon spans from this fraction of the blade (from the hand) to the tip: the outer 8 percent, a thin edge trail.</summary>
        public const float TrailInnerFraction = 0.92f;

        public Transform Muzzle => _muzzle;
        public bool IsRanged => _isRanged;
        public float DrawProgress { get; private set; }
        public bool IsSwinging => _swinging || _recoiling;
        /// <summary>True between combo steps while the weapon stays where the last strike ended: the arms must keep the swing pose, not drop to idle.</summary>
        public bool IsChainHolding => _holding && !_guardHeld;

        /// <summary>True while the current swing is in its damaging (active) phase.</summary>
        public bool InActivePhase { get; private set; }

        /// <summary>The trail material of the pattern being played (null when none).</summary>
        public Material ActiveTrailMaterial => _pattern != null ? _pattern.trailMaterial : null;

        /// <summary>Set by the first-person arms: they draw the ribbon along the visible blade instead of the far damage segment.</summary>
        public bool ExternalTrail { get; set; }
        public float BladeLength => _bladeLength;

        /// <summary>B2: the length of the bar / ribbon actually drawn: the blade clipped where it meets level geometry (visual only; the authority clips its own segment).</summary>
        public float VisibleBladeLength => _visibleLength;
        public SwingPatternData Pattern => _pattern;

        /// <summary>
        /// B2: the blade length that may be drawn from <paramref name="bladeBase"/> along <paramref name="direction"/>: the
        /// distance to the first collider in <paramref name="mask"/> (triggers ignored), 0 when the hand itself is buried in
        /// one, else the full length. Mirrors the authority's own wall clip (same mask) so the bar and ribbon end where the
        /// damage segment ends. Allocation free; mask 0 disables it.
        /// </summary>
        public static float ClipBladeLength(Vector3 bladeBase, Vector3 direction, float length, int mask)
        {
            if (mask == 0 || length <= 0f) return length;
            if (Physics.CheckSphere(bladeBase, 0.03f, mask, QueryTriggerInteraction.Ignore)) return 0f;
            return Physics.Raycast(bladeBase, direction, out RaycastHit hit, length, mask, QueryTriggerInteraction.Ignore)
                ? Mathf.Min(length, hit.distance)
                : length;
        }

        /// <summary>World position of the weapon segment's base (the hand) while the pattern drives this rig.</summary>
        public Vector3 BladeBase => transform.position;

        /// <summary>World position of the weapon segment's tip: the same point the authority sweeps.</summary>
        public Vector3 BladeTip => transform.position + transform.forward * _bladeLength;

        private Vector3 VisibleTip => transform.position + transform.forward * _visibleLength;

        public void Configure(Transform muzzle, Transform arrow, Transform bowString, bool isRanged, bool crossbow = false, bool bowGuard = false)
        {
            _muzzle = muzzle;
            _arrow = arrow;
            _string = bowString;
            _isRanged = isRanged;
            _crossbow = crossbow;
            if (crossbow && bowString != null)
            {
                _stringLeft = bowString.Find("Left");
                _stringRight = bowString.Find("Right");
            }
            _bowGuard = bowGuard;
            if (_arrow != null) _arrow.gameObject.SetActive(false);
        }

        /// <summary>
        /// Melee presentation wiring: the aim frame the pattern is expressed in (the camera the player looks
        /// through, the same frame the authority builds the sweep in) and the thin bar that shows the exact
        /// damage segment during a swing.
        /// </summary>
        public void ConfigureMelee(Transform aimFrame, Transform edge, Transform guardBlade)
        {
            _aimFrame = aimFrame;
            _edge = edge;
            _guardBlade = guardBlade;
            _restLocalPosition = transform.localPosition;
            _restLocalRotation = transform.localRotation;
            _restLocalScale = transform.localScale;
            if (_edge != null) _edge.gameObject.SetActive(false);
        }

        /// <summary>
        /// V3c: a textured weapon model in the first-person hand replaces the blockout cubes. Only their renderers go: the transforms stay
        /// (the guard blade box and the muzzle are read from them) and the damage-segment bar (Sweep_Edge) keeps showing the real swing.
        /// </summary>
        public void HideBlockoutRenderers()
        {
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i].GetComponent<SwingTrail>() != null) continue;
                renderers[i].enabled = false;
            }
        }

        /// <summary>The guard pose shown to the player and sampled by the authority during projectile flight.</summary>
        public void SetGuardHeld(bool held)
        {
            _guardHeld = held;
            if (!_swinging && !_recoiling) ApplyGuardPose();
        }

        /// <summary>Short, local presentation response to a server-confirmed successful block.</summary>
        public void PlayBlockImpact()
        {
            if (_isRanged) return;
            // The server's block result wins over a still-playing local pose (for example, one waiting for its end RPC).
            if (_swinging || _recoiling) EndSwing();
            _blockImpactUntil = Time.time + BlockImpactDuration;
            ApplyGuardPose();
        }

        public bool TryGetGuardBladeBox(out GuardBladeBox box)
        {
            if (!_isRanged && _guardBlade != null)
            {
                box = new GuardBladeBox(_guardBlade, GuardBladeUnitSize);
                return true;
            }

            box = default;
            return false;
        }

        private void ApplyGuardPose()
        {
            if (_isRanged)
            {
                if (!_crossbow && !_bowGuard) return;
                if (_guardHeld && _aimFrame != null)
                    {
                    Quaternion aim = Quaternion.LookRotation(_aimFrame.forward, Vector3.up);
                    if (_bowGuard) BowGuardPose.ApplyModel(transform, _aimFrame.position, aim);
                    else CrossbowGuardPose.ApplyModel(transform, _aimFrame.position, aim);
                }
                else
                {
                    transform.localPosition = _restLocalPosition;
                    transform.localRotation = _restLocalRotation;
                    transform.localScale = _restLocalScale;
                }
                return;
            }
            // Between combo steps (held end pose, or easing back) the rest/guard pose must not be written: the per-frame guard
            // update runs before the next swing captures its start pose, and would make it start from rest (a one-frame idle flash).
            if (_swinging || _recoiling || _holding || _releasing || _guardBlade == null) return;
            if (_guardHeld && _aimFrame != null)
            {
                Quaternion aimRotation = Quaternion.LookRotation(_aimFrame.forward, Vector3.up);
                GuardBladePose.ResolveRoot(_aimFrame.position, aimRotation, true,
                    out Vector3 worldPosition, out Quaternion worldRotation, _twoHanded);
                transform.SetPositionAndRotation(worldPosition, worldRotation);
                ApplyBlockImpactOffset();
                return;
            }

            transform.localPosition = GuardBladePose.RootRestPosition;
            transform.localRotation = ReadyRotation;
            ApplyBlockImpactOffset();
        }

        private void ApplyBlockImpactOffset()
        {
            float remaining = _blockImpactUntil - Time.time;
            if (remaining <= 0f || _aimFrame == null) return;
            float t = 1f - Mathf.Clamp01(remaining / BlockImpactDuration);
            float pulse = Mathf.Sin(t * Mathf.PI) * (1f - t * 0.25f);
            transform.position += _aimFrame.rotation * new Vector3(0f, 0.03f, -0.17f) * pulse;
            transform.rotation = Quaternion.AngleAxis(-15f * pulse, _aimFrame.right) * transform.rotation;
        }

        /// <summary>Starts presenting a swing: from now the rig is posed by the pattern curve, clock = Time.time.</summary>
        public void PlaySwing(WeaponData weapon, SwingPatternData pattern) =>
            PlaySwing(weapon, pattern, pattern != null ? pattern.Timing : default);

        /// <summary>Same, with the resolved timing of this swing (a combo step may stretch its windup or recovery).</summary>
        public void PlaySwing(WeaponData weapon, SwingPatternData pattern, in SwingTiming timing)
        {
            _soundWeaponId = weapon != null ? weapon.weaponId : null;
            if (_isRanged || pattern == null || weapon == null) return;

            CaptureTransitionStart();
            _pattern = pattern;
            _timing = timing;
            _bladeLength = pattern.BladeLength(weapon);
            _visibleLength = _bladeLength;
            _clipMask = GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority) && authority is LocalCombatAuthority local
                ? local.SwingBlockerMask.value
                : 0;
            _swingStart = Time.time;
            _swingSoundPlayed = false;
            _swinging = true;
            _recoiling = false;
            _holding = false;
            _releasing = false;
            _chainHold = ContinuesChain(weapon, pattern);
            _holdBlend = ChainInputHeld ? 1f : 0f;

            if (_edge != null)
            {
                _edge.localPosition = new Vector3(0f, 0f, _bladeLength * 0.5f);
                Renderer edgeRenderer = _edge.GetComponent<Renderer>();
                if (edgeRenderer != null && pattern.trailMaterial != null) edgeRenderer.sharedMaterial = pattern.trailMaterial;
            }

            if (_trail == null) _trail = SwingTrail.Create();
            _trail.Begin(pattern.trailMaterial);
            ApplySwingTime(0f);
        }

        /// <summary>The authority stopped the swing at a wall: the weapon rebounds from where it is back to rest.</summary>
        public void PlayRecoil()
        {
            if (!_swinging || _pattern == null) return;

            CaptureTransitionStart();
            _recoilStart = Time.time;
            _recoiling = true;
            _swinging = false;
            InActivePhase = false;
            if (_trail != null) _trail.EndStroke();
        }

        /// <summary>
        /// Poses the active blade exactly where the pattern puts the weapon <paramref name="swingTime"/> seconds into the
        /// swing, in the world frame of the aim transform. The authority's sweep places the damage segment with
        /// the same pattern and the same frame, so this is the real path.
        /// </summary>
        public void ApplySwingTime(float swingTime)
        {
            if (_pattern == null) return;
            bool active = swingTime >= _timing.Windup && swingTime <= _timing.ActiveEnd;
            SamplePresentation(_pattern, _timing, swingTime, _transitionBase, _transitionRotation, _guardHeld,
                out Vector3 position, out Quaternion rotation, _twoHanded);
            if (_chainHold && swingTime > _timing.ActiveEnd && _holdBlend > 0f)
            {
                // Button held: stay at the strike's end pose; released: the ordinary recovery back to rest (blended, no pop).
                SwingPose end = _pattern.Evaluate(_timing.ActiveEnd, _timing);
                position = Vector3.Lerp(position, end.Base, _holdBlend);
                rotation = Quaternion.Slerp(rotation, end.LocalRotation, _holdBlend);
            }
            ApplyPresentation(position, rotation, active);
        }

        public void SyncNetworkSwingTime(float elapsed)
        {
            _swingStart = Time.time - Mathf.Max(0f, elapsed);
            ApplySwingTime(elapsed);
        }

        public void ApplyNetworkRecoil(float stopFraction, float elapsed)
        {
            if (_pattern == null) return;
            SwingPose contact = _pattern.ActivePose(Mathf.Clamp01(stopFraction));
            _transitionBase = contact.Base;
            _transitionRotation = contact.LocalRotation;
            _swinging = false;
            _recoiling = true;
            _recoilStart = Time.time - Mathf.Max(0f, elapsed);
            if (_trail != null) _trail.EndStroke();
            SampleRecoil(_pattern, stopFraction, _timing.WallRecoil > 0f ? elapsed / _timing.WallRecoil : 1f,
                _guardHeld, out Vector3 position, out Quaternion rotation, _twoHanded);
            ApplyPresentation(position, rotation, false);
        }

        /// <summary>
        /// The server reports the swing over. It is NOT a snap to rest: a held chain keeps the end pose for the next step, otherwise
        /// the weapon eases back from wherever it is (the server's end usually arrives right at the recovery's end anyway).
        /// </summary>
        public void CancelNetworkSwing()
        {
            if (_swinging && _chainHold && !_guardHeld && ChainInputHeld && BeginChainHold()) return;
            if (_swinging || _holding) { ReleaseFromCurrentPose(); return; }
            if (_releasing) return;
            EndSwing();
        }

        private void ReleaseFromCurrentPose()
        {
            Transform frame = _aimFrame != null ? _aimFrame : transform.parent;
            _holdBase = frame != null ? frame.InverseTransformPoint(transform.position) : transform.localPosition;
            _holdRotation = frame != null ? Quaternion.Inverse(frame.rotation) * transform.rotation : transform.localRotation;
            _swinging = false;
            InActivePhase = false;
            if (_edge != null) _edge.gameObject.SetActive(false);
            if (_trail != null) _trail.EndStroke();
            BeginRelease();
        }

        public static void SampleRecoil(SwingPatternData pattern, float stopFraction, float progress,
            bool guarding, out Vector3 position, out Quaternion rotation, bool twoHanded = false)
        {
            SwingPose contact = pattern.ActivePose(Mathf.Clamp01(stopFraction));
            ReadyPose(guarding, twoHanded, out Vector3 rest, out Quaternion ready);
            float eased = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(progress));
            position = Vector3.Lerp(contact.Base, rest, eased);
            rotation = Quaternion.Slerp(contact.LocalRotation, ready, eased);
        }

        private void CaptureTransitionStart()
        {
            Transform frame = _aimFrame != null ? _aimFrame : transform.parent;
            _transitionBase = frame != null ? frame.InverseTransformPoint(transform.position) : transform.localPosition;
            _transitionRotation = frame != null ? Quaternion.Inverse(frame.rotation) * transform.rotation : transform.localRotation;
        }

        // Only non-damaging phases are eased. Active blade samples stay identical to the authority.
        public static void SamplePresentation(SwingPatternData pattern, in SwingTiming timing, float time,
            Vector3 startBase, Quaternion startRotation, bool guarding, out Vector3 position, out Quaternion rotation, bool twoHanded = false)
        {
            SwingPose pose = pattern.Evaluate(time, timing);
            position = pose.Base;
            rotation = pose.LocalRotation;
            if (time < timing.Windup && timing.Windup > 0f)
            {
                SwingPose contact = pattern.Evaluate(timing.Windup, timing);
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(time / timing.Windup));
                position = Vector3.Lerp(startBase, contact.Base, k);
                rotation = Quaternion.Slerp(startRotation, contact.LocalRotation, k);
            }
            else if (time > timing.ActiveEnd)
            {
                SwingPose contact = pattern.Evaluate(timing.ActiveEnd, timing);
                float k = Mathf.SmoothStep(0f, 1f, timing.Recovery > 0f ? Mathf.Clamp01((time - timing.ActiveEnd) / timing.Recovery) : 1f);
                ReadyPose(guarding, twoHanded, out Vector3 endBase, out Quaternion endRotation);
                position = Vector3.Lerp(contact.Base, endBase, k);
                rotation = Quaternion.Slerp(contact.LocalRotation, endRotation, k);
            }
        }

        private static void ReadyPose(bool guarding, bool twoHanded, out Vector3 position, out Quaternion rotation)
        {
            position = GuardBladePose.RootRestPosition + (guarding ? GuardBladePose.RootGuardOffset(twoHanded) : Vector3.zero);
            rotation = guarding ? GuardBladePose.RootRestRotation * GuardBladePose.RootGuardRotation(twoHanded) : ReadyRotation;
        }

        private void ApplyPresentation(Vector3 position, Quaternion rotation, bool active)
        {
            Transform frame = _aimFrame != null ? _aimFrame : transform.parent;
            if (frame == null) return;
            transform.SetPositionAndRotation(frame.position + frame.rotation * position, frame.rotation * rotation);

            if (_edge != null)
            {
                // The damage-segment bar only exists while the segment can hurt (the active phase).
                if (_edge.gameObject.activeSelf != active) _edge.gameObject.SetActive(active);

                // B2: the bar (and the ribbon below) stops where the blade meets level geometry, like the damage segment.
                _visibleLength = active
                    ? ClipBladeLength(transform.position, transform.forward, _bladeLength, _clipMask)
                    : _bladeLength;
                _edge.localPosition = new Vector3(0f, 0f, _visibleLength * 0.5f);
                _edge.localScale = new Vector3(0.02f, 0.02f, Mathf.Max(0.001f, _visibleLength));
            }
        }

        private void LateUpdate()
        {
            if (_isRanged)
            {
                if (_crossbow)
                {
                    var owner = GetComponentInParent<PlayerRig>();
                    var ranged = owner != null ? owner.GetComponent<RangedCombat>() : null;
                    SetCrossbowState(ranged != null && ranged.HasAuthoritativeAmmo && ranged.Magazine > 0,
                        ranged != null && ranged.IsReloading, ranged != null ? ranged.ReloadProgress : 0f);
                }
                if (_crossbow || _bowGuard) ApplyGuardPose();
                return;
            }
            if (_pattern == null)
            {
                ApplyGuardPose();
                return;
            }

            if (_swinging)
            {
                float t = Time.time - _swingStart;
                _holdBlend = Mathf.MoveTowards(_holdBlend, ChainInputHeld && !_guardHeld ? 1f : 0f, HoldBlendRate * Time.deltaTime);
                if (t >= _timing.Total)
                {
                    if (_chainHold && !_guardHeld && ChainInputHeld && BeginChainHold()) return;
                    // Released at the very end while still near the end pose: ease back instead of snapping.
                    if (_chainHold && _holdBlend > 0.05f) { ReleaseFromCurrentPose(); return; }
                    EndSwing();
                    return;
                }

                ApplySwingTime(t);
                if (!_swingSoundPlayed && t >= _timing.Windup)
                {
                    _swingSoundPlayed = true;
                    if (t <= _timing.ActiveEnd)
                        Emberdelve.Feedback.WorldAudio.Play(Emberdelve.Feedback.WorldAudio.SwingFor(_soundWeaponId), transform.position, .7f);
                }
                float activeEnd = _timing.ActiveEnd;
                InActivePhase = t >= _timing.Windup && t <= activeEnd;
                if (ExternalTrail) { }
                else if (t >= _timing.Windup && t <= activeEnd)
                {
                    // The ribbon is drawn along the outer third of the segment (the striking edge): the whole blade
                    // fan seen edge-on from the eye would fill the lower screen.
                    if (_trail != null) _trail.Add(Vector3.Lerp(BladeBase, VisibleTip, TrailInnerFraction), VisibleTip);
                }
                else if (t > activeEnd && _trail != null)
                {
                    _trail.EndStroke();
                }
            }
            else if (_recoiling)
            {
                float duration = Mathf.Max(1e-5f, _timing.WallRecoil); // Accepted action-scaled duration; epsilon only guards division.
                float k = (Time.time - _recoilStart) / duration;
                if (k >= 1f)
                {
                    EndSwing();
                    return;
                }

                float eased = k * k * (3f - 2f * k);
                ReadyPose(_guardHeld, _twoHanded, out Vector3 endBase, out Quaternion endRotation);
                ApplyPresentation(Vector3.Lerp(_transitionBase, endBase, eased),
                    Quaternion.Slerp(_transitionRotation, endRotation, eased), false);
            }
            else if (_holding)
            {
                if (_guardHeld || !ChainInputHeld || Time.time >= _holdUntil) BeginRelease();
                else ApplyPresentation(_holdBase, _holdRotation, false);
            }
            else if (_releasing)
            {
                float k = (Time.time - _releaseStart) / ReleaseSeconds;
                if (k >= 1f) { EndSwing(); return; }
                float eased = k * k * (3f - 2f * k);
                ReadyPose(_guardHeld, _twoHanded, out Vector3 endBase, out Quaternion endRotation);
                ApplyPresentation(Vector3.Lerp(_holdBase, endBase, eased), Quaternion.Slerp(_holdRotation, endRotation, eased), false);
            }
            else ApplyGuardPose();
        }

        /// <summary>True when another step follows this pattern in the weapon's chain (the last step, and any weapon without a chain, returns to rest as before).</summary>
        private static bool ContinuesChain(WeaponData weapon, SwingPatternData pattern)
        {
            if (weapon == null || !weapon.HasComboChain || weapon.comboSteps.Length < 2 || weapon.swingPatterns == null) return false;
            int lastIndex = weapon.comboSteps[weapon.comboSteps.Length - 1].patternIndex;
            return lastIndex < 0 || lastIndex >= weapon.swingPatterns.Length || weapon.swingPatterns[lastIndex] != pattern;
        }

        private bool BeginChainHold()
        {
            SwingPose end = _pattern.Evaluate(_timing.ActiveEnd, _timing);
            _holdBase = end.Base;
            _holdRotation = end.LocalRotation;
            _holdUntil = _swingStart + _timing.Total + MaxChainHoldSeconds;
            _swinging = false;
            _holding = true;
            InActivePhase = false;
            if (_edge != null) _edge.gameObject.SetActive(false);
            if (_trail != null) _trail.EndStroke();
            ApplyPresentation(_holdBase, _holdRotation, false);
            return true;
        }

        private void BeginRelease()
        {
            _holding = false;
            _releasing = true;
            _releaseStart = Time.time;
        }

        private void EndSwing()
        {
            InActivePhase = false;
            _swinging = false;
            _recoiling = false;
            _holding = false;
            _releasing = false;
            transform.localPosition = _restLocalPosition;
            transform.localRotation = _restLocalRotation;
            if (_edge != null) _edge.gameObject.SetActive(false);
            if (_trail != null) _trail.EndStroke();
            ApplyGuardPose();
        }

        private void OnDestroy()
        {
            if (_trail != null) { if (Application.isPlaying) Destroy(_trail.gameObject); else DestroyImmediate(_trail.gameObject); }
        }

        /// <summary>Cosmetic state from the authoritative magazine and reload clock; never changes the muzzle or ammunition.</summary>
        public void SetCrossbowState(bool loaded, bool reloading, float progress)
        {
            if (!_crossbow) return;
            float p = float.IsNaN(progress) || float.IsInfinity(progress) ? 0f : Mathf.Clamp01(progress);
            float pull = loaded ? 1f : reloading ? Mathf.SmoothStep(0f, 1f, (p - .2f) / .3f) : 0f;
            Vector3 nock = new Vector3(0f, .05f * pull, Mathf.Lerp(-.025f, -.22f, pull));
            PoseString(_stringLeft, new Vector3(-.215f, 0f, -.025f), nock);
            PoseString(_stringRight, new Vector3(.215f, 0f, -.025f), nock);
            if (_arrow != null)
            {
                bool seating = reloading && p >= .5f;
                _arrow.gameObject.SetActive(loaded || seating);
                float seat = loaded ? 1f : Mathf.SmoothStep(0f, 1f, (p - .5f) / .25f);
                _arrow.localPosition = Vector3.Lerp(new Vector3(0f, .12f, -.06f), new Vector3(0f, .05f, -.22f + _arrow.localScale.z * .5f), seat);
            }
        }

        private static void PoseString(Transform segment, Vector3 tip, Vector3 nock)
        {
            if (segment == null) return;
            Vector3 delta = nock - tip;
            segment.localPosition = (tip + nock) * .5f;
            segment.localRotation = Quaternion.FromToRotation(Vector3.right, delta);
            segment.localScale = new Vector3(delta.magnitude, .008f, .008f);
        }

        public void SetDrawProgress(float progress)
        {
            if (!_isRanged || _crossbow) return;
            float p = Mathf.Clamp01(progress);
            DrawProgress = p;
            var bow = GetComponentInChildren<BowPresentation>();
            if (bow != null) bow.SetDraw(p);

            if (_arrow != null)
            {
                _arrow.gameObject.SetActive(p > 0.02f);
                Vector3 local = _arrow.localPosition;
                local.z = 0.12f - 0.22f * p;
                _arrow.localPosition = local;
            }

            if (_string != null)
            {
                Vector3 local = _string.localPosition;
                local.z = -0.02f - 0.2f * p;
                _string.localPosition = local;
                _string.localScale = _crossbow
                    ? new Vector3(0.43f + 0.06f * p, 0.012f, 0.012f)
                    : new Vector3(0.012f, 0.52f + 0.06f * p, 0.012f);
            }
        }
    }

    public static class WeaponVisualBuilder
    {
        public static WeaponVisualRig Build(WeaponData weapon, Transform parent, Transform aimFrame = null)
        {
            if (weapon == null || parent == null) return null;

            int ignoreLayer = LayerMask.NameToLayer("Ignore Raycast");
            bool ranged = weapon.IsRanged;

            GameObject root = new GameObject(ranged ? "Weapon_Bow_Blockout" : "Weapon_Sword_Blockout");
            root.transform.SetParent(parent, false);
            // Bring the crossbow's prod and bolt channel into the ready view, with enough
            // depth to avoid an oversized stock end. Its muzzle remains attached to this
            // same physical model on owner and authority; raised guard has its own pose.
            root.transform.localPosition = !ranged ? GuardBladePose.RootRestPosition
                : weapon.weaponId == "hollow_bow" ? new Vector3(-.18f, -.13f, .75f)
                : weapon.weaponId == "ember_crossbow" ? CrossbowGuardPose.ReadyPosition
                : new Vector3(.20f, -.17f, .30f);
            root.transform.localRotation = ranged ? (weapon.weaponId == "ember_crossbow" ? CrossbowGuardPose.ReadyRotation : Quaternion.Euler(0f, -8f, 0f)) : WeaponVisualRig.ReadyRotation;
            if (ignoreLayer >= 0) root.layer = ignoreLayer;

            Transform muzzle;
            Transform arrow = null;
            Transform bowString = null;

            if (ranged)
            {
                bool crossbow = weapon.weaponId == "ember_crossbow";
                if (crossbow)
                {
                    // Distinct, readable crossbow silhouette from the same authored wood/metal materials.
                    for (int i = 0; i < CrossbowGuardPose.PartCount; i++)
                    {
                        CrossbowGuardPart part = CrossbowGuardPose.Part(i);
                        Material material = part.Material == CrossbowPartMaterial.Edge ? weapon.edgeMaterial
                            : part.Material == CrossbowPartMaterial.Grip ? weapon.gripMaterial : weapon.shaftMaterial;
                        BuildPart(root.transform, part.Name, part.Center, part.Size, part.Rotation, material);
                    }
                    bowString = new GameObject("Crossbow_String").transform;
                    bowString.SetParent(root.transform, false);
                    BuildPart(bowString, "Left", new Vector3(-.1075f, 0f, -.025f), new Vector3(.215f, .008f, .008f), Quaternion.identity, weapon.edgeMaterial);
                    BuildPart(bowString, "Right", new Vector3(.1075f, 0f, -.025f), new Vector3(.215f, .008f, .008f), Quaternion.identity, weapon.edgeMaterial);
                    arrow = ProjectileVisual.CreateModel(weapon, 1f).transform;
                    arrow.name = "Crossbow_Bolt";
                    arrow.SetParent(root.transform, false);
                    arrow.localScale = new Vector3(1f, 1f, Mathf.Max(.2f, weapon.projectileLength));
                    muzzle = BuildPart(root.transform, "Crossbow_Muzzle", new Vector3(0f, 0f, 0.40f), new Vector3(0.001f, 0.001f, 0.001f), Quaternion.identity, null);
                    CrossbowAppearance.Dress(root.transform, weapon);
                }
                else
                {
                    BowPresentation.Build(root.transform, weapon);
                    muzzle = BuildPart(root.transform, "Bow_Muzzle", new Vector3(0f, 0f, .30f), Vector3.one * .001f, Quaternion.identity, null);
                }
            }
            else
            {
                BuildPart(root.transform, "Sword_Blade", GuardBladePose.BladeLocalCenterFor(weapon.twoHanded), GuardBladePose.BladeSizeFor(weapon.twoHanded), Quaternion.identity, weapon.edgeMaterial);
                BuildPart(root.transform, "Sword_Tip", new Vector3(0f, 0f, 0.74f), new Vector3(0.03f, 0.014f, 0.12f), Quaternion.identity, weapon.edgeMaterial);
                BuildPart(root.transform, "Sword_Guard", new Vector3(0f, 0f, 0.02f), new Vector3(0.17f, 0.035f, 0.05f), Quaternion.identity, weapon.gripMaterial);
                BuildPart(root.transform, "Sword_Grip", GuardBladePose.GripLocalCenterFor(weapon.twoHanded), GuardBladePose.GripSize, Quaternion.identity, weapon.gripMaterial);
                muzzle = BuildPart(root.transform, "Sword_Muzzle", new Vector3(0f, 0f, 0.8f), new Vector3(0.001f, 0.001f, 0.001f), Quaternion.identity, null);
            }

            WeaponVisualRig rig = root.AddComponent<WeaponVisualRig>();
            rig.TwoHandedGuard = weapon.twoHanded;
            rig.Configure(muzzle, arrow, bowString, ranged, ranged && weapon.weaponId == "ember_crossbow", weapon.CanBowGuard);

            if (!ranged)
            {
                // The real damage segment, drawn as a thin bar from the hand along the blade direction; it is
                // only shown while a swing plays and its length is set from the pattern.
                Transform edge = BuildPart(root.transform, "Sweep_Edge", Vector3.zero, new Vector3(0.01f, 0.01f, 1f), Quaternion.identity, weapon.edgeMaterial);
                rig.ConfigureMelee(aimFrame != null ? aimFrame : parent, edge, root.transform.Find("Sword_Blade"));
            }
            else rig.ConfigureMelee(aimFrame != null ? aimFrame : parent, null, null);

            return rig;
        }

        private static Transform BuildPart(Transform parent, string name, Vector3 localPosition, Vector3 scale, Quaternion rotation, Material material)
        {
            GameObject part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = localPosition;
            part.transform.localScale = scale;
            part.transform.localRotation = rotation;

            Collider collider = part.GetComponent<Collider>();
            if (collider != null)
            {
                if (Application.isPlaying) Object.Destroy(collider);
                else Object.DestroyImmediate(collider);
            }

            int ignoreLayer = LayerMask.NameToLayer("Ignore Raycast");
            if (ignoreLayer >= 0) part.layer = ignoreLayer;

            Renderer renderer = part.GetComponent<Renderer>();
            if (renderer != null && material != null) renderer.material = material;

            return part.transform;
        }
    }

    /// <summary>
    /// The afterimage of a swing: a thin additive ribbon along the outer part of the weapon segment, built from the
    /// positions the weapon actually had during the active phase (the same pattern curve the authority sweeps) and
    /// left in the world. It fades out in a fifth of a second after the stroke ends, never hides what is behind it
    /// for long, and has no collider or gameplay query. Buffers are allocated once.
    /// </summary>
    public sealed class SwingTrail : MonoBehaviour
    {
        private const float FadeSeconds = 0.2f;
        private const int MaxPoints = 256;
        private const float DefaultPeakAlpha = 0.4f;

        /// <summary>Ribbon colour (the player's stroke is amber; an enemy's committed attack line is red).</summary>
        public Color Tint = new Color(1f, 0.74f, 0.32f);
        public float PeakAlpha = DefaultPeakAlpha;

        private readonly Vector3[] _vertices = new Vector3[MaxPoints * 2];
        private readonly int[] _indices = new int[(MaxPoints - 1) * 12];
        private int _count;
        private Mesh _mesh;
        private Material _material;
        private MeshRenderer _renderer;
        private bool _ownsMaterial;
        private float _endedAt = -1f;
        private bool _open;

        public int PointCount => _count;

        /// <summary>Current opacity 0..1 (1 while the stroke is open, fading to 0 after it ends).</summary>
        public float Opacity => _open ? 1f : (_endedAt < 0f ? 0f : Mathf.Clamp01(1f - (Time.time - _endedAt) / FadeSeconds));

        public static SwingTrail Create()
        {
            GameObject go = new GameObject("Swing_Trail");
            int ignoreLayer = LayerMask.NameToLayer("Ignore Raycast");
            if (ignoreLayer >= 0) go.layer = ignoreLayer;

            SwingTrail trail = go.AddComponent<SwingTrail>();
            trail._mesh = new Mesh { name = "Emberdelve_SwingTrail" };
            trail._mesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = trail._mesh;
            trail._renderer = go.AddComponent<MeshRenderer>();
            trail._renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            trail._renderer.receiveShadows = false;
            go.SetActive(false);
            return trail;
        }

        public void Begin(Material material = null)
        {
            if (_material != null && _ownsMaterial) { if (Application.isPlaying) Destroy(_material); else DestroyImmediate(_material); }
            _material = null;

            if (material != null)
            {
                _material = new Material(material) { hideFlags = HideFlags.HideAndDontSave };
                _ownsMaterial = true;
            }
            else
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Standard");
                if (shader != null)
                {
                    _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    _ownsMaterial = true;
                }
            }

            if (_material != null) _renderer.sharedMaterial = _material;
            _count = 0;
            _open = true;
            _endedAt = -1f;
            _mesh.Clear();
            gameObject.SetActive(false);
        }

        public void Add(Vector3 basePoint, Vector3 tipPoint)
        {
            if (!_open || _count >= MaxPoints) return;

            _vertices[_count * 2] = basePoint;
            _vertices[_count * 2 + 1] = tipPoint;
            _count++;
            if (_count < 2) return;

            int quads = _count - 1;
            int t = 0;
            for (int i = 0; i < quads; i++)
            {
                int a = i * 2, b = a + 1, c = a + 2, d = a + 3;
                _indices[t++] = a; _indices[t++] = b; _indices[t++] = c;
                _indices[t++] = c; _indices[t++] = b; _indices[t++] = d;
                _indices[t++] = a; _indices[t++] = c; _indices[t++] = b;
                _indices[t++] = c; _indices[t++] = d; _indices[t++] = b;
            }

            _mesh.Clear();
            _mesh.SetVertices(_vertices, 0, _count * 2);
            _mesh.SetIndices(_indices, 0, quads * 12, MeshTopology.Triangles, 0);
            _mesh.RecalculateBounds();
            gameObject.SetActive(true);
            ApplyAlpha(PeakAlpha);
        }

        public void EndStroke()
        {
            if (!_open) return;
            _open = false;
            _endedAt = Time.time;
        }

        private void ApplyAlpha(float alpha)
        {
            if (_material == null) return;
            Color c = new Color(Tint.r, Tint.g, Tint.b, alpha);
            if (_material.HasProperty("_BaseColor")) _material.SetColor("_BaseColor", c);
            if (_material.HasProperty("_Color")) _material.SetColor("_Color", c);
        }

        private void Update()
        {
            if (_open) return;
            float opacity = Opacity;
            if (opacity <= 0f) { gameObject.SetActive(false); return; }
            ApplyAlpha(PeakAlpha * opacity);
        }

        private void OnDestroy()
        {
            if (_mesh != null) { if (Application.isPlaying) Destroy(_mesh); else DestroyImmediate(_mesh); }
            if (_material != null && _ownsMaterial) { if (Application.isPlaying) Destroy(_material); else DestroyImmediate(_material); }
        }
    }
}
