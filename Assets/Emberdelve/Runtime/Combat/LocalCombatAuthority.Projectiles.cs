using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Player;

namespace Emberdelve.Combat
{
    public sealed partial class LocalCombatAuthority
    {
        private readonly List<ProjectileFlight> _projectiles = new List<ProjectileFlight>(32);
        private readonly RaycastHit[] _projectileHits = new RaycastHit[64];
        private readonly List<HitShape> _projectileShapeBuffer = new List<HitShape>(16);
        private readonly Dictionary<int, ProjectileSkillGroup> _projectileSkillGroups = new Dictionary<int, ProjectileSkillGroup>();
        private readonly List<int> _projectileGroupCleanup = new List<int>();

        private sealed class ProjectileSkillGroup
        {
            public SkillActivation Activation;
            public readonly HashSet<int> Targets = new HashSet<int>();
            public readonly HashSet<int> LaunchedSlots = new HashSet<int>();
            public int Hits;
            public int Budget;
            public int LaunchBudget;
            public int PendingFlights;
            public Vector3 AimEye;
            public Vector3 Origin;
            public Vector3 BaseDirection;
            public bool HasAimSnapshot;
        }

        private sealed class ProjectileFlight
        {
            public int InstigatorId;
            public WeaponData Weapon;
            public AttackIntent Attack;
            public SkillIntent SkillIntent;
            public ProjectileSkillGroup SkillGroup;
            public Vector3 Origin;
            public Vector3 Direction;
            public Vector3 Position;
            public float Range;
            public float Traveled;
            public float Speed;
            public float Radius;
            public float DamageMultiplier;
            public float FlatDamageBonus;
            public int MaxTargets = 1;
            public int HitCount;
            public bool Piercing;
            public readonly HashSet<int> HitTargets = new HashSet<int>();
            public ProjectileVisual Visual;
        }
        /// <summary>Validates and owns one ordinary ranged arrow/bolt flight.</summary>
        public bool LaunchProjectile(AttackIntent intent)
        {
            if (!LegacyAllowed(intent.InstigatorCombatId, "projectile", false, out _)) return false;
            if (!TryValidateProjectileLaunch(intent, out ICombatEntity instigator, out WeaponData weapon,
                    out Vector3 origin, out Vector3 direction, out float range, out string reason))
            {
                LastAttackRejectReason = reason;
                RejectedCount++;
                return false;
            }

            AttackIntent resolved = new AttackIntent(instigator.CombatId, instigator.Kind, weapon, intent.Charge,
                origin, direction, range, intent.Sequence, Now);
            FlightDefaults(resolved.Origin, resolved.Direction, range, weapon, out float speed, out float radius);
            ProjectileFlight flight = new ProjectileFlight
            {
                InstigatorId = instigator.CombatId,
                Weapon = weapon,
                Attack = resolved,
                Origin = origin,
                Position = origin,
                Direction = direction,
                Range = range,
                Speed = speed,
                Radius = radius,
                DamageMultiplier = OutgoingMultiplier(instigator, weapon),
                FlatDamageBonus = FlatWeaponBonus(instigator, weapon),
            };

            KitAccess ammo = KitFor(instigator.CombatId, instigator, null, instigator as IAmmoSource);
            if (ammo.HasAmmoModel && !ammo.TryConsumeAmmo(1))
            {
                LastAttackRejectReason = "no ammunition in the magazine";
                RejectedCount++;
                return false;
            }

            _lastAttackTime[instigator.CombatId] = Now;
            _projectiles.Add(flight);
            flight.Visual = ProjectileVisual.SpawnArrow(origin, origin + direction * range, weapon, true);
            AcceptedCount++;
            LastAttackRejectReason = string.Empty;
            return true;
        }

        private bool TryValidateProjectileLaunch(AttackIntent intent, out ICombatEntity instigator,
            out WeaponData weapon, out Vector3 origin, out Vector3 direction, out float range, out string reason)
        {
            instigator = null;
            weapon = null;
            origin = default;
            direction = default;
            range = 0f;
            reason = string.Empty;
            if (!CombatRegistry.TryGet(intent.InstigatorCombatId, out instigator)) { reason = "unknown instigator id"; return false; }
            if (instigator.IsDead) { reason = "instigator is dead"; return false; }
            if (!(instigator is IActiveWeaponProvider holder) || holder.ActiveWeapon == null || !holder.ActiveWeapon.IsRanged)
            { reason = "instigator has no authoritative ranged weapon"; return false; }

            weapon = holder.ActiveWeapon;
            if (intent.WeaponId != weapon.weaponId || (intent.Weapon != null && intent.Weapon != weapon))
            { reason = $"claimed weapon '{intent.WeaponId}' is not the equipped weapon '{weapon.weaponId}'"; return false; }
            if (!IsFinite(intent.Origin) || !IsFinite(intent.Direction) || !IsFiniteScalar(intent.MaxRange)
                || !IsFiniteScalar(intent.Charge))
            { reason = "projectile launch contains a non-finite vector, range, or charge"; return false; }
            if (intent.MaxRange <= 0f) { reason = "projectile launch range must be positive"; return false; }
            if (_lastSequence.TryGetValue(instigator.CombatId, out int lastSequence) && intent.Sequence <= lastSequence)
            { reason = "stale or repeated command sequence"; return false; }

            float now = Now;
            float cooldown = Mathf.Max(0.1f, weapon.cooldown);
            if (_lastAttackTime.TryGetValue(instigator.CombatId, out float lastAttack) && now - lastAttack < cooldown * cooldownGrace)
            { reason = "attack faster than weapon cooldown"; return false; }

            range = Mathf.Max(1f, weapon.rangedRange);
            if (intent.MaxRange > range + rangeTolerance) { reason = "claimed range exceeds the equipped weapon's reach"; return false; }
            origin = intent.Origin;
            Vector3 aimEye;
            Vector3 authoritativeAim;
            if (instigator is PlayerRig player)
            {
                Vector3 authoritativeMuzzle = player.GetMuzzlePosition();
                if (Vector3.Distance(authoritativeMuzzle, intent.Origin) > 0.75f)
                { reason = "projectile origin is not attached to the equipped muzzle"; return false; }
                origin = authoritativeMuzzle;
                if (!TryReadAimPose(player, out aimEye, out authoritativeAim))
                { reason = "instigator has no usable authoritative aim pose"; return false; }
            }
            else if (instigator is IAimPoseProvider aim)
            {
                if (!TryReadAimPose(aim, out aimEye, out authoritativeAim))
                { reason = "instigator has no usable authoritative aim pose"; return false; }
                if (Vector3.Distance(aimEye, intent.Origin) > maxOriginOffsetFromInstigator)
                { reason = "projectile origin is not attached to the instigator"; return false; }
            }
            else
            { reason = "instigator has no authoritative aim pose"; return false; }

            if (authoritativeAim.sqrMagnitude < 0.0001f)
            { reason = "projectile has no valid authoritative direction"; return false; }
            authoritativeAim.Normalize();
            if (Vector3.Angle(authoritativeAim, intent.Direction.normalized) > 1f)
            { reason = "claimed projectile direction does not match authoritative aim"; return false; }
            direction = authoritativeAim;
            if (!IsProjectileMuzzleClear(aimEye, origin, weapon))
            { reason = "projectile muzzle is obstructed by level geometry"; return false; }
            _lastSequence[instigator.CombatId] = intent.Sequence;
            return true;
        }

        /// <summary>Validates one projectile skill at launch; flight keeps its activation snapshot after cast completion.</summary>
        public bool LaunchSkillProjectile(SkillIntent intent, int projectileIndex)
        {
            LastSkillRejectReason = string.Empty;
            if (!LegacyAllowed(intent.InstigatorCombatId, "skill projectile", true, out _)) return false;
            if (!CombatRegistry.TryGet(intent.InstigatorCombatId, out ICombatEntity instigator))
                return RejectSkillLaunch(intent, "unknown instigator id");
            if (instigator.IsDead) return RejectSkillLaunch(intent, "instigator is dead");
            if (!(instigator is IActiveWeaponProvider holder))
                return RejectSkillLaunch(intent, "instigator has no authoritative weapon");
            if (intent.ActivationId <= 0 || !_activations.TryGetValue(intent.ActivationId, out SkillActivation activation))
                return RejectSkillLaunch(intent, $"no open activation {intent.ActivationId}");
            if (activation.CasterId != instigator.CombatId || activation.Completed)
                return RejectSkillLaunch(intent, "skill activation is closed or belongs to another entity");
            if (Now - activation.OpenedAt > skillActivationWindow)
                return RejectSkillLaunch(intent, "activation window expired before projectile launch");
            if (activation.Hits >= activation.Budget)
                return RejectSkillLaunch(intent, "activation is past its hit budget");
            if (intent.SkillId != activation.Skill.skillId || (intent.Skill != null && intent.Skill != activation.Skill))
                return RejectSkillLaunch(intent, "claimed skill differs from the open activation");

            WeaponData weapon = activation.Weapon;
            if (weapon == null || !weapon.IsRanged || holder.ActiveWeapon != weapon || intent.Weapon != weapon)
                return RejectSkillLaunch(intent, "the equipped ranged weapon changed since the cast opened");
            if (!activation.Skill.IsProjectileSkill)
                return RejectSkillLaunch(intent, "activation is not a projectile skill");
            if (_lastSequence.TryGetValue(instigator.CombatId, out int lastSequence) && intent.Sequence <= lastSequence)
                return RejectSkillLaunch(intent, "stale or repeated command sequence");
            if (!IsFinite(intent.Origin) || !IsFinite(intent.Direction) || !IsFiniteScalar(intent.MaxRange))
                return RejectSkillLaunch(intent, "projectile launch contains a non-finite vector or range");
            if (intent.MaxRange <= 0f)
                return RejectSkillLaunch(intent, "projectile launch range must be positive");

            float range = Mathf.Max(1f, weapon.rangedRange);
            if (intent.MaxRange > range + rangeTolerance)
                return RejectSkillLaunch(intent, "claimed range exceeds the equipped weapon's reach");
            Vector3 origin = intent.Origin;
            Vector3 aimEye;
            Vector3 authoritativeAim;
            int shotCount = Mathf.Max(1, activation.Skill.ProjectilesPerCast);
            if (projectileIndex < 0 || projectileIndex >= shotCount)
                return RejectSkillLaunch(intent, "projectile index is outside the activation volley");
            if (instigator is PlayerRig player)
            {
                Vector3 authoritativeMuzzle = player.GetMuzzlePosition();
                if (Vector3.Distance(authoritativeMuzzle, intent.Origin) > 0.75f)
                    return RejectSkillLaunch(intent, "projectile origin is not attached to the equipped muzzle");
                origin = authoritativeMuzzle;
                if (!TryReadAimPose(player, out aimEye, out authoritativeAim))
                    return RejectSkillLaunch(intent, "instigator has no usable authoritative aim pose");
            }
            else if (instigator is IAimPoseProvider aim)
            {
                if (!TryReadAimPose(aim, out aimEye, out authoritativeAim))
                    return RejectSkillLaunch(intent, "instigator has no usable authoritative aim pose");
                if (Vector3.Distance(aimEye, intent.Origin) > maxOriginOffsetFromInstigator)
                    return RejectSkillLaunch(intent, "projectile origin is not attached to the instigator");
            }
            else return RejectSkillLaunch(intent, "instigator has no authoritative aim pose");
            if (authoritativeAim.sqrMagnitude < 0.0001f)
                return RejectSkillLaunch(intent, "projectile has no valid direction");
            authoritativeAim.Normalize();

            // The first valid launch snapshots the volley pose. Later slots fan from that same authority
            // aim/origin, so per-arrow client aim changes cannot steer individual arrows around the spread.
            bool hasGroup = _projectileSkillGroups.TryGetValue(activation.Id, out ProjectileSkillGroup group);
            Vector3 baseDirection = authoritativeAim;
            if (hasGroup && group.HasAimSnapshot)
            {
                if (Vector3.Distance(group.AimEye, aimEye) > maxOriginOffsetFromInstigator
                    || Vector3.Distance(group.Origin, origin) > maxOriginOffsetFromInstigator)
                    return RejectSkillLaunch(intent, "projectile volley pose changed after its first launch");
                baseDirection = group.BaseDirection;
                origin = group.Origin;
                aimEye = group.AimEye;
            }
            float spreadOffset = shotCount <= 1 ? 0f : (projectileIndex / (shotCount - 1f) - 0.5f)
                * Mathf.Clamp(activation.Skill.spreadDegrees, 0f, 170f);
            Vector3 expectedDirection = Quaternion.AngleAxis(spreadOffset, Vector3.up) * baseDirection;
            if (Vector3.Angle(expectedDirection, intent.Direction) > 1f)
                return RejectSkillLaunch(intent, "projectile direction does not match its authority-defined spread slot");
            Vector3 direction = expectedDirection.normalized;
            if (!IsProjectileMuzzleClear(aimEye, origin, weapon))
                return RejectSkillLaunch(intent, "projectile muzzle is obstructed by level geometry");

            if (!hasGroup)
            {
                group = new ProjectileSkillGroup
                {
                    Activation = activation,
                    Budget = activation.Budget,
                    LaunchBudget = shotCount,
                    AimEye = aimEye,
                    Origin = origin,
                    BaseDirection = authoritativeAim,
                    HasAimSnapshot = true,
                };
                _projectileSkillGroups.Add(activation.Id, group);
            }
            if (group.LaunchedSlots.Contains(projectileIndex))
                return RejectSkillLaunch(intent, "projectile spread slot was already launched");

            _lastSequence[instigator.CombatId] = intent.Sequence;
            AttackIntent appearance = new AttackIntent(instigator.CombatId, instigator.Kind, weapon, 1f,
                origin, direction, range, intent.Sequence, Now);
            FlightDefaults(origin, direction, range, weapon, out float speed, out float radius);
            ProjectileFlight flight = new ProjectileFlight
            {
                InstigatorId = instigator.CombatId,
                Weapon = weapon,
                Attack = appearance,
                SkillIntent = intent,
                SkillGroup = group,
                Origin = origin,
                Position = origin,
                Direction = direction,
                Range = range,
                Speed = speed,
                Radius = radius,
                DamageMultiplier = OutgoingMultiplier(instigator, weapon),
                FlatDamageBonus = FlatWeaponBonus(instigator, weapon),
                MaxTargets = Mathf.Max(1, activation.Skill.projectileMaxTargets),
                Piercing = activation.Skill.projectileMaxTargets > 1,
            };
            _projectiles.Add(flight);
            group.LaunchedSlots.Add(projectileIndex);
            group.PendingFlights++;
            flight.Visual = ProjectileVisual.SpawnArrow(origin, origin + direction * range, weapon, true);
            AcceptedCount++;
            return true;
        }
        private bool RejectSkillLaunch(SkillIntent intent, string reason)
        {
            LastSkillRejectReason = reason;
            RejectedCount++;
            return false;
        }

        private static void FlightDefaults(Vector3 origin, Vector3 direction, float range, WeaponData weapon,
            out float speed, out float radius)
        {
            speed = Mathf.Max(2f, weapon.projectileSpeed);
            // projectileRadius is the authored shaft width; collision uses its visible half-width.
            radius = Mathf.Max(0.01f, weapon.projectileRadius * 0.5f);
        }

        private bool IsProjectileMuzzleClear(Vector3 eye, Vector3 origin, WeaponData weapon)
        {
            LayerMask levelMask = config != null ? config.levelMask : Physics.DefaultRaycastLayers;
            if (levelMask.value == 0) return true;
            FlightDefaults(origin, Vector3.forward, 0f, weapon, out _, out float radius);
            if (Physics.CheckSphere(eye, radius, levelMask, QueryTriggerInteraction.Ignore)
                || Physics.CheckSphere(origin, radius, levelMask, QueryTriggerInteraction.Ignore))
                return false;
            Vector3 eyeToMuzzle = origin - eye;
            float distance = eyeToMuzzle.magnitude;
            return distance <= 0.001f
                || !Physics.SphereCast(eye, radius, eyeToMuzzle / distance, out _, distance,
                    levelMask, QueryTriggerInteraction.Ignore);
        }

        internal void AdvanceProjectiles(float deltaTime)
        {
            if (deltaTime <= 0f || _projectiles.Count == 0)
            {
                PruneProjectileSkillGroups();
                return;
            }
            for (int i = _projectiles.Count - 1; i >= 0; i--)
            {
                ProjectileFlight flight = _projectiles[i];
                bool finished = StepProjectile(flight, Mathf.Max(0f, flight.Speed * deltaTime));
                if (finished)
                {
                    if (flight.Visual != null) flight.Visual.FinishAuthoritative(flight.Position);
                    _projectiles.RemoveAt(i);
                    if (flight.SkillGroup != null)
                    {
                        flight.SkillGroup.PendingFlights = Mathf.Max(0, flight.SkillGroup.PendingFlights - 1);
                    }
                }
                else if (flight.Visual != null) flight.Visual.MoveAuthoritative(flight.Position, flight.Direction);
            }
            PruneProjectileSkillGroups();
        }

        private void PruneProjectileSkillGroups()
        {
            _projectileGroupCleanup.Clear();
            foreach (KeyValuePair<int, ProjectileSkillGroup> pair in _projectileSkillGroups)
            {
                ProjectileSkillGroup group = pair.Value;
                bool activationClosed = group.Activation.Completed
                    || Now - group.Activation.OpenedAt > skillActivationWindow;
                if (group.PendingFlights == 0 && activationClosed) _projectileGroupCleanup.Add(pair.Key);
            }
            for (int i = 0; i < _projectileGroupCleanup.Count; i++)
                _projectileSkillGroups.Remove(_projectileGroupCleanup[i]);
            _projectileGroupCleanup.Clear();
        }

        private bool StepProjectile(ProjectileFlight flight, float stepDistance)
        {
            if (flight.Traveled >= flight.Range) return true;
            float remainingStep = Mathf.Min(stepDistance, flight.Range - flight.Traveled);
            int safety = 8;
            while (remainingStep > 0.0001f && safety-- > 0)
            {
                Vector3 start = flight.Position;
                Vector3 end = start + flight.Direction * remainingStep;
                float segmentLength = remainingStep;
                if (!TryFindProjectileContact(flight, start, end, out ICombatEntity target,
                        out Vector3 point, out float distance, out bool bladeContact, out bool wall))
                {
                    flight.Position = end;
                    flight.Traveled += remainingStep;
                    return flight.Traveled >= flight.Range;
                }

                flight.Position = start + flight.Direction * distance;
                flight.Traveled += distance;
                remainingStep -= distance;
                if (wall || target == null) return true;

                bool hasSource = CombatRegistry.TryGet(flight.InstigatorId, out ICombatEntity source);
                bool canDamage = hasSource && !target.IsDead && CombatTeams.AreHostile(source, target);
                bool alreadyHit = flight.SkillGroup != null && flight.SkillGroup.Targets.Contains(target.CombatId);
                if (canDamage && !alreadyHit)
                {
                    ApplyProjectileImpact(flight, target, point, bladeContact, start, segmentLength);
                    if (flight.SkillGroup != null && !flight.SkillGroup.Targets.Contains(target.CombatId))
                        flight.SkillGroup.Targets.Add(target.CombatId);
                }
                if (flight.SkillGroup != null) flight.HitTargets.Add(target.CombatId);

                if (bladeContact || !flight.Piercing || !canDamage || alreadyHit || flight.HitCount >= flight.MaxTargets
                    || flight.SkillGroup != null && flight.SkillGroup.Hits >= flight.SkillGroup.Budget)
                    return true;

                // Continue past a pierced body in this same tick. The next search skips all targets this cast
                // already hit, so a body with several colliders cannot absorb the same arrow twice.
                float advance = Mathf.Min(0.002f, remainingStep);
                flight.Position += flight.Direction * advance;
                flight.Traveled += advance;
                remainingStep -= advance;
            }
            return flight.Traveled >= flight.Range;
        }

        private bool TryFindProjectileContact(ProjectileFlight flight, Vector3 start, Vector3 end,
            out ICombatEntity target, out Vector3 point, out float distance, out bool bladeContact, out bool wall)
        {
            target = null;
            point = end;
            distance = Vector3.Distance(start, end);
            bladeContact = false;
            wall = false;
            Vector3 delta = end - start;
            float length = delta.magnitude;
            if (length <= 0.00001f) return false;
            Vector3 direction = delta / length;
            LayerMask hitMask = config != null ? config.hitMask : Physics.AllLayers;
            LayerMask levelMask = config != null ? config.levelMask : Physics.DefaultRaycastLayers;
            int mask = hitMask.value | levelMask.value;

            int hitCount = Physics.SphereCastNonAlloc(start, flight.Radius, direction, _projectileHits,
                length, mask, QueryTriggerInteraction.Ignore);
            RaycastHit[] queryHits = _projectileHits;
            if (hitCount >= _projectileHits.Length)
            {
                // The fixed buffer may omit the true nearest wall/body when saturated. Pay for a
                // complete query only in that rare case to keep contact ordering authoritative.
                queryHits = Physics.SphereCastAll(start, flight.Radius, direction, length, mask,
                    QueryTriggerInteraction.Ignore);
                System.Array.Sort(queryHits,
                    System.Collections.Generic.Comparer<RaycastHit>.Create((a, b) => a.distance.CompareTo(b.distance)));
                hitCount = queryHits.Length;
            }
            else
            {
                System.Array.Sort(_projectileHits, 0, hitCount,
                    System.Collections.Generic.Comparer<RaycastHit>.Create((a, b) => a.distance.CompareTo(b.distance)));
            }
            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit hit = queryHits[i];
                if (hit.collider == null) continue;
                ICombatEntity entity = hit.collider.GetComponentInParent<ICombatEntity>();
                if (entity == null)
                {
                    // Head colliders are sometimes separate scene objects rather than children of
                    // their combat entity. Resolve registered heads before treating the collider as a wall.
                    for (int e = 0; e < CombatRegistry.All.Count; e++)
                    {
                        ICombatEntity candidate = CombatRegistry.All[e];
                        if (candidate != null && candidate.HeadCollider == hit.collider)
                        { entity = candidate; break; }
                    }
                }
                if (entity != null && IsInactiveCombatEntity(entity)) continue;
                if (entity != null && entity.CombatId == flight.InstigatorId) continue;
                if (entity != null && flight.HitTargets.Contains(entity.CombatId)) continue;
                target = entity;
                point = hit.point;
                distance = hit.distance;
                wall = entity == null;
                break;
            }

            // Some authoritative entities use analytic hit volumes and have no colliders. Their body is the
            // same half-metre sphere used by existing hit-point validation.
            IReadOnlyList<ICombatEntity> entities = CombatRegistry.All;
            for (int i = 0; i < entities.Count; i++)
            {
                ICombatEntity entity = entities[i];
                if (entity == null || IsInactiveCombatEntity(entity) || entity.IsDead || entity.CombatId == flight.InstigatorId) continue;
                if (flight.HitTargets.Contains(entity.CombatId)) continue;
                if (entity is IHitShapeProvider shapeProvider)
                {
                    _projectileShapeBuffer.Clear();
                    shapeProvider.CollectHitShapes(_projectileShapeBuffer);
                    for (int s = 0; s < _projectileShapeBuffer.Count; s++)
                    {
                        HitShape shape = _projectileShapeBuffer[s];
                        if (TrySweepCapsule(start, direction, length, shape.A, shape.B,
                                shape.Radius + flight.Radius, out float shapeDistance) && shapeDistance < distance)
                        {
                            target = entity;
                            distance = shapeDistance;
                            point = start + direction * shapeDistance;
                            wall = false;
                        }
                    }
                }
                else if (TrySegmentSphere(start, direction, length, entity.CombatCenter,
                             LocalCombatAuthority.TargetBodyRadius + flight.Radius, out float bodyDistance)
                         && bodyDistance < distance)
                {
                    target = entity;
                    distance = bodyDistance;
                    point = start + direction * bodyDistance;
                    wall = false;
                }
            }

            // Blade contact is evaluated against the current visible guard pose on every flight step.
            for (int i = 0; i < entities.Count; i++)
            {
                if (!(entities[i] is PlayerRig defender) || IsInactiveCombatEntity(defender) || defender.CombatId == flight.InstigatorId || defender.IsDead) continue;
                if (!defender.TryGetProjectileGuardBlade(out GuardBladeBox blade)) continue;
                if (blade.TrySweep(start, end, flight.Radius, out float bladeDistance) && bladeDistance <= distance)
                {
                    target = defender;
                    distance = bladeDistance;
                    point = start + direction * bladeDistance;
                    bladeContact = true;
                    wall = false;
                }
            }

            return target != null || wall;
        }

        private static bool IsInactiveCombatEntity(ICombatEntity entity)
        {
            return entity is Component component && component != null && !component.gameObject.activeInHierarchy;
        }

        private static bool TrySegmentSphere(Vector3 start, Vector3 direction, float length,
            Vector3 center, float radius, out float distance)
        {
            Vector3 offset = start - center;
            float b = Vector3.Dot(offset, direction);
            float c = offset.sqrMagnitude - radius * radius;
            float discriminant = b * b - c;
            if (discriminant < 0f) { distance = 0f; return false; }
            float entry = -b - Mathf.Sqrt(discriminant);
            float exit = -b + Mathf.Sqrt(discriminant);
            if (exit < 0f) { distance = 0f; return false; }
            if (entry < 0f) entry = 0f;
            distance = entry;
            return entry <= length;
        }

        private static bool TrySweepCapsule(Vector3 start, Vector3 direction, float length,
            Vector3 capsuleA, Vector3 capsuleB, float radius, out float distance)
        {
            float lo = 0f, hi = length;
            float r2 = radius * radius;
            for (int i = 0; i < 32; i++)
            {
                float a = lo + (hi - lo) / 3f;
                float b = hi - (hi - lo) / 3f;
                if (DistancePointSegmentSquared(start + direction * a, capsuleA, capsuleB)
                    <= DistancePointSegmentSquared(start + direction * b, capsuleA, capsuleB)) hi = b;
                else lo = a;
            }
            float closest = (lo + hi) * 0.5f;
            if (DistancePointSegmentSquared(start, capsuleA, capsuleB) <= r2)
            {
                distance = 0f;
                return true;
            }
            if (DistancePointSegmentSquared(start + direction * closest, capsuleA, capsuleB) > r2)
            {
                distance = 0f;
                return false;
            }

            lo = 0f;
            hi = closest;
            for (int i = 0; i < 32; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (DistancePointSegmentSquared(start + direction * mid, capsuleA, capsuleB) <= r2) hi = mid;
                else lo = mid;
            }
            distance = hi;
            return true;
        }

        private static float DistancePointSegmentSquared(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector3 segment = b - a;
            float lengthSq = segment.sqrMagnitude;
            float t = lengthSq > 0.000001f ? Mathf.Clamp01(Vector3.Dot(point - a, segment) / lengthSq) : 0f;
            return (point - (a + segment * t)).sqrMagnitude;
        }

        private static bool IsFiniteScalar(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool TryReadAimPose(IAimPoseProvider provider, out Vector3 eye, out Vector3 direction)
        {
            eye = default;
            direction = default;
            if (provider == null || !provider.TryGetAimPose(out eye, out float yaw, out float pitch)
                || !IsFinite(eye) || !IsFiniteScalar(yaw) || !IsFiniteScalar(pitch)) return false;
            direction = Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward;
            return IsFinite(direction) && direction.sqrMagnitude > 0.0001f;
        }

        private const float ProjectileLocationTie = 1e-4f;

        /// <summary>
        /// D1c3: the hit location of a projectile, decided by the authority from the target's own analytic hit
        /// volumes and nothing the client sends. The FIRST volume the projectile (shaft radius included) touches
        /// along the swept segment of this step wins (smallest entry distance); equal entries (within 0.1 mm) go to
        /// the lower <see cref="HitLocation"/> value, the same deterministic tie rule the melee sweep uses. This
        /// differs from melee on purpose: a swing takes the DEEPEST contact inside a time window, an arrow has no
        /// window, it simply stops at the first thing it touches. When the contact came from a physics collider and
        /// no volume is touched inside this step, the volume with the nearest surface to the impact point wins
        /// (same tie rule). A target without volumes (not an <see cref="IHitShapeProvider"/>, or none collected)
        /// resolves <see cref="HitLocation.Unspecified"/> (multiplier 1).
        /// </summary>
        private HitLocation ResolveProjectileLocation(ProjectileFlight flight, ICombatEntity target,
            Vector3 segmentStart, float segmentLength, Vector3 impactPoint)
        {
            if (!(target is IHitShapeProvider provider)) return HitLocation.Unspecified;
            _projectileShapeBuffer.Clear();
            provider.CollectHitShapes(_projectileShapeBuffer);
            if (_projectileShapeBuffer.Count == 0) return HitLocation.Unspecified;

            bool found = false;
            HitLocation best = HitLocation.Unspecified;
            float bestEntry = 0f;
            for (int s = 0; s < _projectileShapeBuffer.Count; s++)
            {
                HitShape shape = _projectileShapeBuffer[s];
                if (!TrySweepCapsule(segmentStart, flight.Direction, segmentLength, shape.A, shape.B,
                        shape.Radius + flight.Radius, out float entry)) continue;
                if (!found || entry < bestEntry - ProjectileLocationTie
                    || Mathf.Abs(entry - bestEntry) <= ProjectileLocationTie && (int)shape.Location < (int)best)
                {
                    found = true;
                    best = shape.Location;
                    bestEntry = entry;
                }
            }
            if (found) return best;

            float bestGap = float.MaxValue;
            for (int s = 0; s < _projectileShapeBuffer.Count; s++)
            {
                HitShape shape = _projectileShapeBuffer[s];
                float gap = Mathf.Sqrt(DistancePointSegmentSquared(impactPoint, shape.A, shape.B)) - shape.Radius;
                if (gap < bestGap - ProjectileLocationTie
                    || Mathf.Abs(gap - bestGap) <= ProjectileLocationTie && (int)shape.Location < (int)best)
                {
                    bestGap = gap;
                    best = shape.Location;
                }
            }
            return best;
        }

        private void ApplyProjectileImpact(ProjectileFlight flight, ICombatEntity target,
            Vector3 point, bool bladeContact, Vector3 segmentStart, float segmentLength)
        {
            if (!CombatRegistry.TryGet(flight.InstigatorId, out ICombatEntity instigator) || target.IsDead) return;
            // A blade contact is stopped by the guard before the body, so it has no body location (x1, the guard
            // reduction decides the rest). Otherwise the location comes from the target's hit volumes.
            HitLocation location = bladeContact
                ? HitLocation.Unspecified
                : ResolveProjectileLocation(flight, target, segmentStart, segmentLength, point);
            float locationMultiplier = LocationTable.Multiplier(location);
            HitCandidate hit = new HitCandidate(target.CombatId, point, location == HitLocation.Head);
            if (flight.SkillGroup == null)
            {
                AttackIntent resolved = new AttackIntent(instigator.CombatId, instigator.Kind, flight.Weapon,
                    flight.Attack.Charge, flight.Origin, flight.Direction, flight.Range, flight.Attack.Sequence, Now);
                DamageInfo damage = DamageResolver.Resolve(flight.Weapon, resolved, hit, target,
                    flight.DamageMultiplier, flight.FlatDamageBonus,
                    FlowTuning, true, bladeContact, location, locationMultiplier);
                if (!damage.IsValid) return;
                target.ApplyDamage(damage);
                ApplyHitEffects(target, damage, flight.Weapon);
                PublishProjectileFeedback(instigator, target, point, damage);
                return;
            }

            ProjectileSkillGroup group = flight.SkillGroup;
            SkillActivation activation = group.Activation;
            if (group.Targets.Contains(target.CombatId) || group.Hits >= group.Budget) return;
            SkillIntent resolvedSkill = new SkillIntent(instigator.CombatId, instigator.Kind,
                activation.Skill, flight.Weapon, flight.Origin, flight.Direction, flight.Range,
                flight.SkillIntent.Sequence, Now, default, activation.Id, activation.Scale);
            float scale = activation.Skill.UsesFlatDamage ? 1f : activation.Scale;
            DamageInfo skillDamage = DamageResolver.ResolveSkill(resolvedSkill, hit, target,
                flight.DamageMultiplier, scale, flight.FlatDamageBonus,
                true, bladeContact, location, locationMultiplier);
            if (!skillDamage.IsValid) return;
            target.ApplyDamage(skillDamage);
            StatusSpec status = ScaleStatus(activation.Skill.TargetStatusSpec, activation.Scale);
            if (!status.IsNone && target is IStatusTarget statusTarget) statusTarget.ApplyStatus(status, instigator.CombatId);
            group.Targets.Add(target.CombatId);
            activation.Targets.Add(target.CombatId);
            group.Hits++;
            flight.HitCount++;
            activation.Hits++;
            PublishProjectileFeedback(instigator, target, point, skillDamage);
        }

        private void PublishProjectileFeedback(ICombatEntity instigator, ICombatEntity target,
            Vector3 point, DamageInfo damage)
        {
            HitFeedback feedback = new HitFeedback(point, damage.Amount, damage.Blocked, damage.Headshot,
                target.IsDead, target.Kind, damage.Location);
            PublishCombatFeedback(instigator, target, feedback, damage);
        }

        private void OnDestroy()
        {
            for (int i = 0; i < _projectiles.Count; i++)
            {
                ProjectileVisual visual = _projectiles[i].Visual;
                if (visual != null) visual.FinishAuthoritative(visual.transform.position);
            }
            _projectiles.Clear();
            _projectileSkillGroups.Clear();
            _projectileGroupCleanup.Clear();
            GameServices.Unregister<ICombatAuthority>(this);
        }

    }
}
