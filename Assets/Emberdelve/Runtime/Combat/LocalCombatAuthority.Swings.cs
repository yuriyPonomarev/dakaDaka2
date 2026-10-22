using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;

namespace Emberdelve.Combat
{
    /// <summary>
    /// D1a: the swept-weapon melee. The authority owns the whole swing.
    ///
    /// Begin (<see cref="TryBeginMeleeSwing"/>): validation only, on the authority's own state and clock.
    /// Step (<see cref="StepSwings"/>, every FixedUpdate, or by hand in a gate): for every running swing the
    /// ACTIVE phase is cut into K equal time samples (K comes from the pattern data alone, see
    /// <see cref="SwingGeometry.SampleCount"/>). Each sample whose time has been reached is processed in order:
    ///   1. the weapon segment (base, tip) is built from the pattern pose at that sample time in the attacker's
    ///      authoritative aim frame (position/yaw/pitch interpolated between this tick's pose and the last one);
    ///   2. level geometry is tested along that segment: a hit clips the segment at the wall and the swing STOPS
    ///      after this sample (recoil) - nothing behind the wall is touched, anything touched earlier keeps its
    ///      damage;
    ///   3. every hostile, live, not-yet-hit entity that exposes hit volumes is tested (segment-vs-capsule with
    ///      the pattern's sweep radius added); the first touch opens a short contact window, the deepest
    ///      volume touched inside the window decides the location, then the damage is applied through the
    ///      ordinary DamageResolver (guard, armour, multipliers) with the location table.
    /// Because the sample grid is fixed by the data, the result does not depend on the tick length: stepping in
    /// 2 ms or 500 ms slices yields the same hits, locations and wall stop.
    /// </summary>
    public sealed partial class LocalCombatAuthority
    {
        [Header("Swept melee (D1a)")]
        [Tooltip("Largest angle (degrees) between the aim a client claims when it begins a swing and the pose the authority holds.")]
        [SerializeField] private float swingAimTolerance = 12f;
        [Tooltip("Entities whose centre is further than effective reach + this margin from the eye are skipped before their volumes are tested.")]
        [SerializeField] private float swingBroadPhaseMargin = 3f;

        private const int FinishedSwingMemory = 96;

        private sealed class Contact
        {
            public ICombatEntity Target;
            public int FirstSample;
            public int LastWindowSample;
            public HitLocation Location;
            public float Depth;
            public Vector3 Point;
        }

        private sealed class SwingRecord
        {
            public int Id;
            public int InstigatorId;
            public ICombatEntity Instigator;
            public IAimPoseProvider Actor;
            public WeaponData Weapon;
            public SwingPatternData Pattern;
            public int Sequence;
            public float StartTime;
            public int SampleCount;
            public int NextSample;
            public float BladeLength;
            public float LastTs;
            public Vector3 LastEye;
            public float LastYaw;
            public float LastPitch;
            public SwingPhase Phase;
            public SwingEndReason EndReason;
            public float EndTs;
            public bool Stopped;
            public float StopTs;
            public int WallSample = -1;
            public readonly HashSet<int> Hit = new HashSet<int>();
            public readonly List<Contact> Pending = new List<Contact>(4);
            public readonly List<MeleeSwingHit> Hits = new List<MeleeSwingHit>(4);
            public IReadOnlyList<MeleeSwingHit> HitsView;
        }

        /// <summary>Per instigator: the stamp of its last accepted swing and that swing's full length (the cooldown floor).</summary>
        private readonly Dictionary<int, KeyValuePair<float, float>> _lastSwingFloor = new Dictionary<int, KeyValuePair<float, float>>();

        private readonly Dictionary<int, SwingRecord> _swingsById = new Dictionary<int, SwingRecord>();
        private readonly Dictionary<int, SwingRecord> _swingByInstigator = new Dictionary<int, SwingRecord>();
        private readonly List<SwingRecord> _activeSwings = new List<SwingRecord>();
        private readonly Queue<int> _finishedSwingIds = new Queue<int>();
        private readonly List<SwingRecord> _stepBuffer = new List<SwingRecord>();
        private readonly List<ICombatEntity> _registryBuffer = new List<ICombatEntity>(32);
        private readonly List<HitShape> _shapeBuffer = new List<HitShape>(16);
        private int _nextSwingId = 1;

        /// <summary>When true (default) FixedUpdate steps the swings. A gate turns it off to step by hand on an injected clock.</summary>
        internal bool AutoStepSwings = true;

        /// <summary>
        /// When false (default) a melee weapon attack from an entity that has a swing pose (the player) must go
        /// through <see cref="TryBeginMeleeSwing"/>: the old arc-less SubmitAttack melee path accepted a claimed
        /// hit point out to about 4 m with no arc and no walls. Fixtures that deliberately use that path as a
        /// damage-formula probe turn it on explicitly. Enemies (no swing pose) are not affected.
        /// </summary>
        internal bool AllowLegacyMeleeAttacks;

        /// <summary>Swings accepted by TryBeginMeleeSwing.</summary>
        public int SwingsStarted { get; private set; }

        /// <summary>Swings currently running (windup, active or recovery).</summary>
        public int ActiveSwingCount => _activeSwings.Count;

        private HitLocationTable LocationTable =>
            config != null && config.hitLocations != null ? config.hitLocations : HitLocationTable.Default;

        private LayerMask SwingLevelMask =>
            config != null ? config.levelMask : (LayerMask)LayerMask.GetMask(GameConfig.LevelLayerName);

        private void FixedUpdate()
        {
            if (AutoStepSwings) StepSwings();
        }

        // ------------------------------------------------------------------------------------------------
        // Begin
        // ------------------------------------------------------------------------------------------------

        public bool TryBeginMeleeSwing(in MeleeSwingIntent intent, out int swingId, out string reason)
        {
            swingId = 0;
            reason = string.Empty;
            LastAttackRejectReason = string.Empty;

            if (!CombatRegistry.TryGet(intent.InstigatorCombatId, out ICombatEntity instigator)) return RejectSwing(intent, "unknown instigator id", out reason);
            if (instigator.IsDead) return RejectSwing(intent, "instigator is dead", out reason);

            if (!(instigator is IActiveWeaponProvider holder)) return RejectSwing(intent, "instigator has no authoritative weapon", out reason);
            WeaponData weapon = holder.ActiveWeapon;
            if (weapon == null) return RejectSwing(intent, "instigator holds no weapon", out reason);
            if (intent.WeaponId != weapon.weaponId)
            {
                return RejectSwing(intent, $"claimed weapon '{intent.WeaponId}' is not the equipped weapon '{weapon.weaponId}'", out reason);
            }

            if (!weapon.UsesSweptMelee) return RejectSwing(intent, "the equipped weapon has no swing patterns", out reason);
            if (!(instigator is IAimPoseProvider actor)) return RejectSwing(intent, "instigator has no authoritative aim pose", out reason);

            if (!weapon.TryResolveSwingPattern(intent.ComboStep, intent.PatternIndex, out SwingPatternData pattern)
                || !pattern.Validate(out _))
            {
                return RejectSwing(intent, $"pattern {intent.PatternIndex} at combo step {intent.ComboStep} is not allowed for the equipped weapon", out reason);
            }

            if (_lastSequence.TryGetValue(intent.InstigatorCombatId, out int lastSequence) && intent.Sequence <= lastSequence)
            {
                return RejectSwing(intent, "stale or repeated command sequence", out reason);
            }
            _lastSequence[intent.InstigatorCombatId] = intent.Sequence;

            if (_swingByInstigator.ContainsKey(intent.InstigatorCombatId)) return RejectSwing(intent, "a swing is already in progress", out reason);

            float now = Now;
            float cooldown = Mathf.Max(0.1f, weapon.cooldown);
            // The interval between two attacks is the weapon cooldown on the authority clock, and never less than the
            // full length (windup + active + recovery) of the swing that was last accepted: a forged client cannot
            // swing faster than the pattern, even when a wall stop ended the previous swing early.
            float minimumInterval = cooldown * cooldownGrace;
            if (_lastAttackTime.TryGetValue(intent.InstigatorCombatId, out float lastAttack)
                && _lastSwingFloor.TryGetValue(intent.InstigatorCombatId, out KeyValuePair<float, float> floor)
                && floor.Key == lastAttack)
            {
                minimumInterval = Mathf.Max(minimumInterval, floor.Value);
            }

            if (_lastAttackTime.TryGetValue(intent.InstigatorCombatId, out lastAttack) && now - lastAttack < minimumInterval)
            {
                return RejectSwing(intent, "attack faster than weapon cooldown", out reason);
            }

            if (!actor.TryGetAimPose(out Vector3 eye, out float yaw, out float pitch) || !IsFinite(eye))
            {
                return RejectSwing(intent, "instigator has no usable aim pose", out reason);
            }

            Vector3 claimed = Quaternion.Euler(intent.AimPitch, intent.AimYaw, 0f) * Vector3.forward;
            Vector3 actual = Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward;
            if (float.IsNaN(intent.AimYaw) || float.IsNaN(intent.AimPitch) || float.IsInfinity(intent.AimYaw) || float.IsInfinity(intent.AimPitch)
                || !(Vector3.Angle(claimed, actual) <= swingAimTolerance))
            {
                return RejectSwing(intent, "aim snapshot does not match the authoritative pose", out reason);
            }

            // Accepted: only an accepted begin spends the cooldown (same clock and stamp as weapon attacks).
            _lastAttackTime[intent.InstigatorCombatId] = now;
            _lastSwingFloor[intent.InstigatorCombatId] = new KeyValuePair<float, float>(now, pattern.TotalTime);

            SwingRecord record = new SwingRecord
            {
                Id = _nextSwingId++,
                InstigatorId = intent.InstigatorCombatId,
                Instigator = instigator,
                Actor = actor,
                Weapon = weapon,
                Pattern = pattern,
                Sequence = intent.Sequence,
                StartTime = now,
                SampleCount = SwingGeometry.SampleCount(pattern, weapon),
                BladeLength = pattern.BladeLength(weapon),
                LastTs = 0f,
                LastEye = eye,
                LastYaw = yaw,
                LastPitch = pitch,
                Phase = pattern.windupTime > 0f ? SwingPhase.Windup : SwingPhase.Active
            };

            _swingsById[record.Id] = record;
            _swingByInstigator[record.InstigatorId] = record;
            _activeSwings.Add(record);
            SwingsStarted++;

            swingId = record.Id;
            return true;
        }

        public bool TryGetMeleeSwingStatus(int swingId, out MeleeSwingStatus status)
        {
            if (!_swingsById.TryGetValue(swingId, out SwingRecord r))
            {
                status = default;
                return false;
            }

            float elapsed = r.Phase == SwingPhase.Finished ? r.EndTs : Now - r.StartTime;
            if (r.HitsView == null) r.HitsView = r.Hits.AsReadOnly();
            status = new MeleeSwingStatus(r.Id, r.InstigatorId, r.Phase, r.EndReason, elapsed,
                Mathf.Min(r.NextSample, r.SampleCount + 1), r.SampleCount, r.WallSample, r.HitsView);
            return true;
        }

        /// <summary>The swing currently running for an instigator, or 0.</summary>
        public int ActiveSwingIdFor(int instigatorCombatId) =>
            _swingByInstigator.TryGetValue(instigatorCombatId, out SwingRecord r) ? r.Id : 0;

        private bool RejectSwing(in MeleeSwingIntent intent, string reason, out string reasonOut)
        {
            reasonOut = reason;
            LastAttackRejectReason = reason;
            RejectedCount++;
            if (logRejections)
            {
                CombatDiagnostics.Log($"reject-swing:{intent.InstigatorCombatId}:{reason}",
                    $"[CombatAuthority] Rejected melee swing from #{intent.InstigatorCombatId} ({intent.WeaponId}): {reason}", this);
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------------
        // Step
        // ------------------------------------------------------------------------------------------------

        /// <summary>Advances every running swing to the authority clock. Idempotent for an unchanged clock.</summary>
        internal void StepSwings()
        {
            if (_activeSwings.Count == 0) return;

            float now = Now;
            _stepBuffer.Clear();
            _stepBuffer.AddRange(_activeSwings);
            for (int i = 0; i < _stepBuffer.Count; i++) StepSwing(_stepBuffer[i], now);
        }

        private void StepSwing(SwingRecord rec, float now)
        {
            if (rec.Phase == SwingPhase.Finished) return;

            float ts = now - rec.StartTime;

            if (!CombatRegistry.TryGet(rec.InstigatorId, out ICombatEntity current) || current != rec.Instigator)
            {
                FinishSwing(rec, SwingEndReason.InstigatorGone, Mathf.Max(0f, ts));
                return;
            }

            if (rec.Instigator.IsDead)
            {
                rec.Pending.Clear();
                FinishSwing(rec, SwingEndReason.InstigatorDied, Mathf.Max(0f, ts));
                return;
            }

            // A weapon swap ends the swing: what is in the hand now is not the weapon this segment belongs to.
            if (!(rec.Instigator is IActiveWeaponProvider holder) || holder.ActiveWeapon != rec.Weapon)
            {
                rec.Pending.Clear();
                FinishSwing(rec, SwingEndReason.WeaponChanged, Mathf.Max(0f, ts));
                return;
            }

            if (!rec.Actor.TryGetAimPose(out Vector3 eye, out float yaw, out float pitch) || !IsFinite(eye))
            {
                eye = rec.LastEye;
                yaw = rec.LastYaw;
                pitch = rec.LastPitch;
            }

            SwingPatternData pattern = rec.Pattern;
            float windup = pattern.windupTime;
            float active = pattern.activeTime;

            if (ts > rec.LastTs)
            {
                while (!rec.Stopped && rec.NextSample <= rec.SampleCount)
                {
                    float sampleTime = windup + active * rec.NextSample / rec.SampleCount;
                    if (sampleTime > ts) break;

                    float span = ts - rec.LastTs;
                    float k = span <= 1e-6f ? 1f : Mathf.Clamp01((sampleTime - rec.LastTs) / span);
                    SwingFrame frame = new SwingFrame(Vector3.Lerp(rec.LastEye, eye, k),
                        Mathf.LerpAngle(rec.LastYaw, yaw, k), Mathf.Lerp(rec.LastPitch, pitch, k));

                    ProcessSample(rec, rec.NextSample, sampleTime, frame);
                    rec.NextSample++;
                }

                rec.LastTs = ts;
                rec.LastEye = eye;
                rec.LastYaw = yaw;
                rec.LastPitch = pitch;
            }

            if (rec.Stopped)
            {
                rec.Phase = SwingPhase.Recovery;
                if (ts >= rec.StopTs + pattern.wallRecoilTime) FinishSwing(rec, SwingEndReason.WallStop, rec.StopTs + pattern.wallRecoilTime);
                return;
            }

            if (rec.NextSample > rec.SampleCount)
            {
                ResolveAllPending(rec, windup + active);
                rec.Phase = SwingPhase.Recovery;
                if (ts >= pattern.TotalTime) FinishSwing(rec, SwingEndReason.Completed, pattern.TotalTime);
                return;
            }

            rec.Phase = ts < windup ? SwingPhase.Windup : SwingPhase.Active;
        }

        private void ProcessSample(SwingRecord rec, int sample, float sampleTime, in SwingFrame frame)
        {
            SwingPatternData pattern = rec.Pattern;
            SwingPose pose = pattern.ActivePose(sample / (float)rec.SampleCount);
            SwingGeometry.BladeSegment(frame, pose, rec.BladeLength, out Vector3 bladeBase, out Vector3 bladeTip);

            Vector3 axis = bladeTip - bladeBase;
            float length = axis.magnitude;
            Vector3 dir = length > 1e-5f ? axis / length : Vector3.forward;

            // Level geometry first. A wall ALWAYS clips the segment where it meets it (nothing is struck through
            // a wall); the swing STOPS (recoil) when the blade is moving into the surface it touched. A blade that
            // merely touches a wall while moving away from it or along it (the start of a slash that begins
            // pointing at a side wall, in a corridor) is clipped but keeps going. A base buried in a wall clips
            // the segment to nothing and always stops the swing.
            float clip = length;
            bool wall = false;
            LayerMask mask = SwingLevelMask;
            if (mask.value != 0)
            {
                // The line from the eye to the hand is tested too: a swing cannot start around an outside corner
                // (hand on the far side of a wall edge the eye cannot see past). Treated like a buried base.
                if (Physics.Linecast(frame.Eye, bladeBase, mask, QueryTriggerInteraction.Ignore)
                    || Physics.CheckSphere(bladeBase, pattern.sweepRadius * 0.5f, mask, QueryTriggerInteraction.Ignore))
                {
                    clip = 0f;
                    wall = true;
                }
                else if (Physics.Linecast(bladeBase, bladeTip, out RaycastHit wallHit, mask, QueryTriggerInteraction.Ignore))
                {
                    clip = Mathf.Min(length, wallHit.distance);
                    wall = MovesIntoSurface(rec, sample, frame, clip, wallHit.normal);
                }
            }

            Vector3 clippedTip = bladeBase + dir * clip;
            float reach = pattern.EffectiveReach(rec.Weapon) + swingBroadPhaseMargin;
            float reachSqr = reach * reach;
            float sweepRadius = pattern.sweepRadius;
            float sampleDt = pattern.activeTime / rec.SampleCount;
            int windowSamples = Mathf.CeilToInt(pattern.contactWindow / Mathf.Max(1e-5f, sampleDt));

            _registryBuffer.Clear();
            IReadOnlyList<ICombatEntity> all = CombatRegistry.All;
            for (int i = 0; i < all.Count; i++) _registryBuffer.Add(all[i]);

            for (int i = 0; i < _registryBuffer.Count; i++)
            {
                ICombatEntity target = _registryBuffer[i];
                if (target == null || target == rec.Instigator || target.IsDead) continue;
                if (rec.Hit.Contains(target.CombatId)) continue;

                // A deactivated object is not in the world (the old overlap query ignored its colliders too). A
                // merely DISABLED agent (a frozen test enemy, a stunned monster) is still there and can be hit.
                if (target is Component component && !component.gameObject.activeInHierarchy) continue;
                if (!CombatTeams.AreHostile(rec.Instigator, target)) continue;
                if (!(target is IHitShapeProvider provider)) continue;
                if ((target.CombatCenter - frame.Eye).sqrMagnitude > reachSqr) continue;

                _shapeBuffer.Clear();
                provider.CollectHitShapes(_shapeBuffer);

                float bestDepth = -1f;
                HitLocation bestLocation = HitLocation.Unspecified;
                Vector3 bestPoint = default;
                for (int s = 0; s < _shapeBuffer.Count; s++)
                {
                    HitShape shape = _shapeBuffer[s];
                    float distance = SwingGeometry.SegmentDistance(bladeBase, clippedTip, shape.A, shape.B, out Vector3 onBlade);
                    float depth = shape.Radius + sweepRadius - distance;
                    if (depth < 0f) continue;

                    if (IsBetterContact(depth, shape.Location, bestDepth, bestLocation))
                    {
                        bestDepth = depth;
                        bestLocation = shape.Location;
                        bestPoint = onBlade;
                    }
                }

                if (bestDepth < 0f) continue;

                Contact contact = FindPending(rec, target.CombatId);
                if (contact == null)
                {
                    if (rec.Hits.Count + rec.Pending.Count >= pattern.maxTargets) continue;

                    rec.Pending.Add(new Contact
                    {
                        Target = target,
                        FirstSample = sample,
                        LastWindowSample = sample + windowSamples,
                        Location = bestLocation,
                        Depth = bestDepth,
                        Point = bestPoint
                    });
                }
                else if (sample <= contact.LastWindowSample && IsBetterContact(bestDepth, bestLocation, contact.Depth, contact.Location))
                {
                    contact.Depth = bestDepth;
                    contact.Location = bestLocation;
                    contact.Point = bestPoint;
                }
            }

            // Contacts whose window has closed become hits now, in the order they were first touched.
            for (int i = 0; i < rec.Pending.Count;)
            {
                Contact c = rec.Pending[i];
                if (c.LastWindowSample <= sample)
                {
                    rec.Pending.RemoveAt(i);
                    ApplySwingHit(rec, c, sample, frame.Eye);
                }
                else
                {
                    i++;
                }
            }

            if (wall)
            {
                ResolveAllPending(rec, sampleTime, sample, frame.Eye);
                rec.Stopped = true;
                rec.StopTs = sampleTime;
                rec.WallSample = sample;
                rec.EndReason = SwingEndReason.WallStop;
            }
        }

        /// <summary>
        /// True when the blade point <paramref name="distance"/> along the segment of <paramref name="sample"/> is
        /// moving against the surface normal (into the surface). The velocity is the pattern's own motion between
        /// this sample and its neighbour in the same aim frame, so it does not depend on how the authority is stepped.
        /// </summary>
        private static bool MovesIntoSurface(SwingRecord rec, int sample, in SwingFrame frame, float distance, Vector3 surfaceNormal)
        {
            if (surfaceNormal.sqrMagnitude < 1e-6f) return true;

            int other = sample < rec.SampleCount ? sample + 1 : sample - 1;
            SwingGeometry.BladeSegment(frame, rec.Pattern.ActivePose(sample / (float)rec.SampleCount), rec.BladeLength, out Vector3 b0, out Vector3 t0);
            SwingGeometry.BladeSegment(frame, rec.Pattern.ActivePose(other / (float)rec.SampleCount), rec.BladeLength, out Vector3 b1, out Vector3 t1);

            Vector3 p0 = b0 + (t0 - b0).normalized * distance;
            Vector3 p1 = b1 + (t1 - b1).normalized * distance;
            Vector3 velocity = other > sample ? p1 - p0 : p0 - p1;
            return Vector3.Dot(velocity, surfaceNormal) < -1e-3f;
        }

        private static bool IsBetterContact(float depth, HitLocation location, float bestDepth, HitLocation bestLocation)
        {
            if (bestDepth < 0f) return true;
            if (depth > bestDepth + 1e-4f) return true;
            return Mathf.Abs(depth - bestDepth) <= 1e-4f && (int)location < (int)bestLocation;
        }

        private static Contact FindPending(SwingRecord rec, int targetId)
        {
            for (int i = 0; i < rec.Pending.Count; i++)
            {
                if (rec.Pending[i].Target.CombatId == targetId) return rec.Pending[i];
            }
            return null;
        }

        private void ResolveAllPending(SwingRecord rec, float time) => ResolveAllPending(rec, time, Mathf.Min(rec.NextSample, rec.SampleCount), rec.LastEye);

        private void ResolveAllPending(SwingRecord rec, float time, int sample, Vector3 eye)
        {
            while (rec.Pending.Count > 0)
            {
                Contact c = rec.Pending[0];
                rec.Pending.RemoveAt(0);
                ApplySwingHit(rec, c, sample, eye);
            }
        }

        private void ApplySwingHit(SwingRecord rec, Contact contact, int sample, Vector3 eye)
        {
            ICombatEntity instigator = rec.Instigator;
            ICombatEntity target = contact.Target;
            if (instigator == null || instigator.IsDead || target == null || target.IsDead) return;
            if (!CombatRegistry.TryGet(target.CombatId, out ICombatEntity registered) || registered != target) return;
            if (rec.Hit.Contains(target.CombatId)) return;

            float multiplier = LocationTable.Multiplier(contact.Location);
            Vector3 direction = contact.Point - eye;
            if (direction.sqrMagnitude < 0.0001f) direction = instigator.CombatCenter - target.CombatCenter;

            AttackIntent resolved = new AttackIntent(instigator.CombatId, instigator.Kind, rec.Weapon, 1f,
                instigator.CombatCenter, direction, rec.Pattern.EffectiveReach(rec.Weapon), rec.Sequence, Now);
            HitCandidate resolvedHit = new HitCandidate(target.CombatId, contact.Point, contact.Location == HitLocation.Head);

            DamageInfo damage = DamageResolver.ResolveSwing(rec.Weapon, rec.Pattern, resolved, resolvedHit,
                contact.Location, multiplier, target, OutgoingMultiplier(instigator, rec.Weapon),
                FlatWeaponBonus(instigator, rec.Weapon));
            if (!damage.IsValid) return;

            rec.Hit.Add(target.CombatId);
            target.ApplyDamage(damage);
            AcceptedCount++;
            rec.Hits.Add(new MeleeSwingHit(target.CombatId, contact.Location, damage.Amount, damage.Blocked, sample, contact.FirstSample, contact.Point));

            HitFeedback feedback = new HitFeedback(contact.Point, damage.Amount, damage.Blocked, damage.Headshot,
                target.IsDead, target.Kind);

            if (instigator.Kind == EntityKind.LocalPlayer) GameEvents.RaiseDamageDealt(feedback);
            if (target.Kind == EntityKind.LocalPlayer) GameEvents.RaisePlayerDamaged(damage);
        }

        private void FinishSwing(SwingRecord rec, SwingEndReason reason, float endTs)
        {
            if (rec.Phase == SwingPhase.Finished) return;

            rec.Phase = SwingPhase.Finished;
            rec.EndReason = reason;
            rec.EndTs = endTs;
            rec.Pending.Clear();

            _activeSwings.Remove(rec);
            if (_swingByInstigator.TryGetValue(rec.InstigatorId, out SwingRecord current) && current == rec)
            {
                _swingByInstigator.Remove(rec.InstigatorId);
            }

            _finishedSwingIds.Enqueue(rec.Id);
            while (_finishedSwingIds.Count > FinishedSwingMemory) _swingsById.Remove(_finishedSwingIds.Dequeue());
        }

        private static bool IsFinite(Vector3 v) =>
            !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
              || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
    }
}
