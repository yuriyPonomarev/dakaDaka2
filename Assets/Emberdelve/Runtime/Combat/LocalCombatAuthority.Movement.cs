using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;

namespace Emberdelve.Combat
{
    /// <summary>One dash the authority found dishonest and undid. Observability for tests and, later, anti-cheat.</summary>
    public readonly struct DashRollback
    {
        public readonly int CasterId;
        public readonly int ActivationId;
        public readonly string Reason;
        public readonly Vector3 Claimed;
        public readonly Vector3 Accepted;
        public readonly bool FromExpiry;

        public DashRollback(int casterId, int activationId, string reason, Vector3 claimed, Vector3 accepted, bool fromExpiry)
        {
            CasterId = casterId;
            ActivationId = activationId;
            Reason = reason;
            Claimed = claimed;
            Accepted = accepted;
            FromExpiry = fromExpiry;
        }
    }

    /// <summary>
    /// R1c3: movement abilities and cast lifetime on the authority's clock.
    ///
    /// Dash: the client still moves (its CharacterController is the only collision-respecting mover, and the user decided the
    /// authority validates instead of executing). When the dash ends the client reports where it stopped
    /// (<see cref="ReportDashResult"/>). The authority compares that with what the skill data allows - the distance (scaled
    /// by the caster's effect scale, exactly as the client scaled it), how far the dash could have got by now on the authority
    /// clock, and a sphere cast along the path against the level - and teleports the entity back to the furthest honest point
    /// when the report is not true. An honest report is never touched. A dash nobody reports is validated by the authority
    /// itself from the entity's position once its duration plus a grace has passed.
    ///
    /// Cast lifetime: <see cref="ExpireActivations"/> completes an activation nobody completed once its window is over, so
    /// a caller that skips <c>CompleteSkillCast</c> neither keeps the activation open nor loses the skill's self buff.
    /// </summary>
    public sealed partial class LocalCombatAuthority
    {
        /// <summary>Seconds after a dash should have ended that the authority waits for the client's report before it checks by itself.</summary>
        public const float DashReportGrace = 0.75f;

        /// <summary>Flat slack on the claimed dash distance (physics step overshoot, a ledge slide).</summary>
        public const float DashDistanceTolerance = 0.5f;

        /// <summary>Extra slack per m/s of dash speed: one frame of overshoot at the dash's own speed.</summary>
        public const float DashSpeedSlack = 0.1f;

        /// <summary>The most the claimed end may differ in height from the start.</summary>
        public const float DashMaxVertical = 2.5f;

        /// <summary>Slack added to the sphere cast hit distance: the real capsule is wider than the cast sphere.</summary>
        public const float DashWallSlack = 0.3f;

        /// <summary>Walking speed the authority allows for the time between the end of a dash and its own expiry check.</summary>
        public const float DashPostWalkSpeed = 8f;

        private const int MaxDashRollbackHistory = 32;
        private readonly List<DashRollback> _dashRollbacks = new List<DashRollback>();

        public int DashRollbackCount { get; private set; }
        public int DashAcceptedCount { get; private set; }
        public int ActivationsAutoCompleted { get; private set; }
        public string LastDashRollbackReason { get; private set; } = string.Empty;
        public IReadOnlyList<DashRollback> DashRollbacks => _dashRollbacks;
        public event System.Action<DashRollback> DashRolledBack;

        // ---- legacy (sender-less) path ----------------------------------------------------------------------------

        private bool _senderBound;

        /// <summary>
        /// Sender-less commands for player-owned ids are refused (R1c3). The sender-less overloads remain the trusted local path for
        /// entities nobody owns (enemies, fixtures without an owner). Tests that deliberately exercise the old trusted behavior can set this.
        /// </summary>
        public bool AllowSenderlessPlayerCommands { get; internal set; }

        private bool LegacyAllowed(int entityId, string command, bool skill, out string reason)
        {
            reason = string.Empty;
            if (_senderBound || AllowSenderlessPlayerCommands || !CombatSenders.TryGetOwner(entityId, out _)) return true;

            reason = $"{command} for player-owned entity #{entityId} carries no sender";
            if (skill) LastSkillRejectReason = reason;
            else LastAttackRejectReason = reason;
            RejectedCount++;
            SenderRefusalCount++;
            if (logRejections)
            {
                CombatDiagnostics.Log($"reject-senderless:{entityId}:{command}", $"[CombatAuthority] Rejected {command}: {reason}", this);
            }
            return false;
        }

        // ---- dash ------------------------------------------------------------------------------------------------

        private static float DashSecondsFor(SkillData skill, float scale) => Mathf.Clamp(skill.dashDuration * scale, 0.05f, 2f);

        private void BeginMovementTracking(SkillActivation activation, SkillData skill, ICombatEntity caster, float scale)
        {
            if (skill.effect != SkillEffectKind.Dash || !(caster is IMovementRollback mover)) return;

            // The same clamps PlayerMovement.TryStartDash applies, so an honest dash ends inside what is computed here.
            float seconds = DashSecondsFor(skill, scale);
            float meters = Mathf.Clamp(skill.dashDistance * scale, 0.5f, 20f);
            float speed = Mathf.Min(meters / seconds, 24f);

            activation.DashTracked = true;
            activation.DashStart = mover.MovementPosition;
            activation.DashSeconds = seconds;
            activation.DashSpeed = speed;
            activation.DashMeters = speed * seconds;
            activation.DashEpoch = _kits.TryGetValue(activation.CasterId, out CombatKitState kit) ? kit.Epoch : 0;
        }

        public bool ReportDashResult(CombatSenderId sender, int casterCombatId, int activationId, Vector3 endPosition, out string reason)
        {
            if (!AuthorizeSender(sender, casterCombatId, "dash report", true, out reason)) return false;

            if (!_activations.TryGetValue(activationId, out SkillActivation activation) || activation.CasterId != casterCombatId)
            {
                reason = "no such dash activation for this caster";
                return RejectDash(reason);
            }
            if (!activation.DashTracked)
            {
                reason = "activation is not a tracked dash";
                return RejectDash(reason);
            }
            if (activation.DashReported)
            {
                reason = "dash result already reported";
                return RejectDash(reason);
            }
            if (!CombatRegistry.TryGet(casterCombatId, out ICombatEntity caster) || caster.IsDead || !(caster is IMovementRollback mover))
            {
                activation.DashReported = true;
                reason = "caster cannot report a dash";
                return RejectDash(reason);
            }

            activation.DashReported = true;
            return JudgeDash(activation, mover, endPosition, 0f, false, out reason);
        }

        private bool RejectDash(string reason)
        {
            LastSkillRejectReason = reason;
            RejectedCount++;
            return false;
        }

        /// <summary>The authority's own check of a dash nobody reported: reads the entity's position itself.</summary>
        private void ExpireDash(SkillActivation activation)
        {
            activation.DashReported = true;
            if (!CombatRegistry.TryGet(activation.CasterId, out ICombatEntity caster) || caster.IsDead || !(caster is IMovementRollback mover)) return;

            // A death or respawn since the cast moved the entity legitimately; there is nothing honest to compare.
            if (_kits.TryGetValue(activation.CasterId, out CombatKitState kit) && kit.Epoch != activation.DashEpoch) return;

            float sinceEnd = Mathf.Max(0f, Now - activation.OpenedAt - activation.DashSeconds);
            JudgeDash(activation, mover, mover.MovementPosition, DashPostWalkSpeed * sinceEnd, true, out _);
        }

        private bool JudgeDash(SkillActivation activation, IMovementRollback mover, Vector3 claimedEnd, float extraAllowance, bool fromExpiry, out string reason)
        {
            reason = string.Empty;
            Vector3 start = activation.DashStart;
            Vector3 flat = new Vector3(claimedEnd.x - start.x, 0f, claimedEnd.z - start.z);
            float claimed = flat.magnitude;
            Vector3 dir = claimed > 0.0001f ? flat / claimed : Vector3.zero;

            float tolerance = DashDistanceTolerance + DashSpeedSlack * activation.DashSpeed;
            float elapsed = Mathf.Max(0f, Now - activation.OpenedAt);
            float allowed = claimed;
            string problem = null;

            if (float.IsNaN(claimedEnd.x) || float.IsNaN(claimedEnd.y) || float.IsNaN(claimedEnd.z) || float.IsInfinity(claimed))
            {
                allowed = 0f;
                problem = "end position is not a number";
            }
            else
            {
                if (Mathf.Abs(claimedEnd.y - start.y) > DashMaxVertical + extraAllowance)
                {
                    problem = $"vertical displacement {Mathf.Abs(claimedEnd.y - start.y):F1}m";
                    allowed = Mathf.Min(allowed, activation.DashMeters);
                }

                float reach = activation.DashMeters + tolerance + extraAllowance;
                if (claimed > reach)
                {
                    problem = problem ?? $"dash {claimed:F1}m exceeds its {activation.DashMeters:F1}m";
                    allowed = Mathf.Min(allowed, activation.DashMeters);
                }

                // How far a dash that started when the cast opened can have got by now on the authority clock.
                float soFar = activation.DashSpeed * Mathf.Min(elapsed, activation.DashSeconds);
                if (claimed > soFar + tolerance + extraAllowance)
                {
                    problem = problem ?? $"dash {claimed:F1}m reported {elapsed:F2}s after the cast, too early for {activation.DashSpeed:F1}m/s";
                    allowed = Mathf.Min(allowed, soFar);
                }

                if (allowed > 0.01f && dir != Vector3.zero)
                {
                    Vector3 center = start + mover.MovementCenterOffset;
                    float radius = Mathf.Max(0.1f, mover.MovementRadius * 0.6f);
                    if (Physics.SphereCast(center, radius, dir, out RaycastHit hit, allowed + DashWallSlack, LevelCastMask(), QueryTriggerInteraction.Ignore))
                    {
                        float clear = hit.distance;
                        if (allowed > clear + DashWallSlack)
                        {
                            problem = problem ?? $"dash passes through level geometry at {clear:F1}m";
                            allowed = Mathf.Max(0f, clear);
                        }
                    }
                }
            }

            if (problem == null)
            {
                DashAcceptedCount++;
                return true;
            }

            Vector3 accepted = new Vector3(start.x + dir.x * allowed, start.y, start.z + dir.z * allowed);
            reason = problem;
            DashRollbackCount++;
            LastDashRollbackReason = problem;
            LastSkillRejectReason = problem;
            RejectedCount++;

            DashRollback record = new DashRollback(activation.CasterId, activation.Id, problem, claimedEnd, accepted, fromExpiry);
            _dashRollbacks.Add(record);
            if (_dashRollbacks.Count > MaxDashRollbackHistory) _dashRollbacks.RemoveAt(0);

            mover.RollbackTo(accepted);
            if (logRejections)
            {
                CombatDiagnostics.Log($"dash-rollback:{activation.CasterId}",
                    $"[CombatAuthority] Dash of #{activation.CasterId} rolled back ({problem}); claimed {claimedEnd}, accepted {accepted}.", this);
            }
            DashRolledBack?.Invoke(record);
            return false;
        }

        private int LevelCastMask()
        {
            if (config != null && config.levelMask.value != 0) return config.levelMask.value;
            int mask = LayerMask.GetMask(GameConfig.LevelLayerName);
            return mask != 0 ? mask : Physics.DefaultRaycastLayers;
        }

        // ---- cast lifetime ----------------------------------------------------------------------------------------

        private List<SkillActivation> _expireScratch;

        /// <summary>
        /// Completes activations nobody completed once their window is over, and validates dashes nobody reported. Runs on every
        /// authority tick and before any cast or completion, so it needs no per-frame cooperation from the caller.
        /// </summary>
        private void ExpireActivations()
        {
            if (_activations.Count == 0) return;

            float now = Now;
            List<SkillActivation> due = null;
            foreach (KeyValuePair<int, SkillActivation> pair in _activations)
            {
                SkillActivation a = pair.Value;
                bool needsComplete = !a.Completed && now > a.CompleteBy;
                bool needsDash = a.DashTracked && !a.DashReported && now >= a.OpenedAt + a.DashSeconds + DashReportGrace;
                if (!needsComplete && !needsDash) continue;
                if (due == null) due = _expireScratch ?? (_expireScratch = new List<SkillActivation>());
                due.Add(a);
            }
            if (due == null) return;

            for (int i = 0; i < due.Count; i++)
            {
                SkillActivation a = due[i];
                if (a.DashTracked && !a.DashReported && now >= a.OpenedAt + a.DashSeconds + DashReportGrace) ExpireDash(a);
                if (a.Completed || now <= a.CompleteBy) continue;
                if (CombatRegistry.TryGet(a.CasterId, out ICombatEntity caster) && !caster.IsDead)
                {
                    a.AutoCompleted = true;
                    ActivationsAutoCompleted++;
                    CompleteActivation(a, caster);
                }
                else
                {
                    a.Completed = true;
                }
            }
            due.Clear();
        }
    }
}
