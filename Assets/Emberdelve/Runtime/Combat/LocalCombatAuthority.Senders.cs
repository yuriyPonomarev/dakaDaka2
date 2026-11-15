namespace Emberdelve.Combat
{
    /// <summary>
    /// R1c1: sender-bound entry points. Every command here is checked against <see cref="CombatSenders"/> FIRST,
    /// before any registry lookup, sequence, cooldown, mana, ammo or swing state is read or written, then handed to
    /// the same validation the sender-less overloads run. A refusal therefore changes nothing: a forged instigator
    /// cannot burn the victim's sequence, cooldown stamp, mana, ammo or swing slot.
    ///
    /// The sender-less overloads (<c>SubmitAttack(intent, hit)</c>, <c>TryBeginSkillCast(casterId, ...)</c>, ...) are
    /// the local trusted path kept for fixtures, tools and enemies; player code goes through these overloads with
    /// <c>PlayerRig.CombatSender</c>. R1c3 closed the sender-less path for player-owned ids (see LegacyAllowed); it stays trusted for unowned ids only.
    /// </summary>
    public sealed partial class LocalCombatAuthority
    {
        /// <summary>The sender of the most recent sender-bound command (accepted or refused). Observability only.</summary>
        public CombatSenderId LastCommandSender { get; private set; }

        /// <summary>Commands refused because the sender does not own the claimed instigator/caster.</summary>
        public int SenderRefusalCount { get; private set; }

        /// <summary>Clears <see cref="LastCommandSender"/> so a test can prove which overload a call site used.</summary>
        internal void ClearLastCommandSender() => LastCommandSender = default;

        private bool AuthorizeSender(CombatSenderId sender, int entityId, string command, bool skill, out string reason)
        {
            LastCommandSender = sender;
            if (CombatSenders.Authorize(sender, entityId, out reason))
            {
                if (CombatRegistry.TryGet(entityId, out var entity) && entity is Emberdelve.Player.PlayerRig rig && rig.IsInFloorTransit)
                {
                    reason = "floor transfer pending";
                    if (skill) LastSkillRejectReason = reason; else LastAttackRejectReason = reason;
                    RejectedCount++;
                    return false;
                }
                return true;
            }

            if (skill) LastSkillRejectReason = reason;
            else LastAttackRejectReason = reason;
            RejectedCount++;
            SenderRefusalCount++;
            if (logRejections)
            {
                CombatDiagnostics.Log($"reject-sender:{sender}:{entityId}:{command}",
                    $"[CombatAuthority] Rejected {command} for #{entityId} from {sender}: {reason}", this);
            }
            return false;
        }

        public bool SubmitAttack(CombatSenderId sender, AttackIntent intent, HitCandidate hit, out string reason)
        {
            if (!AuthorizeSender(sender, intent.InstigatorCombatId, "attack", false, out reason)) return false;
            _senderBound = true;
            try { return SubmitAttack(intent, hit, out reason); }
            finally { _senderBound = false; }
        }

        public bool TryBeginMeleeSwing(CombatSenderId sender, in MeleeSwingIntent intent, out int swingId, out string reason)
        {
            swingId = 0;
            if (!AuthorizeSender(sender, intent.InstigatorCombatId, "swing", false, out reason)) return false;
            _senderBound = true;
            try { return TryBeginMeleeSwing(in intent, out swingId, out reason); }
            finally { _senderBound = false; }
        }

        public bool LaunchProjectile(CombatSenderId sender, AttackIntent intent, out string reason)
        {
            if (!AuthorizeSender(sender, intent.InstigatorCombatId, "projectile", false, out reason)) return false;
            _senderBound = true;
            bool launched;
            try { launched = LaunchProjectile(intent); }
            finally { _senderBound = false; }
            reason = launched ? string.Empty : LastAttackRejectReason;
            return launched;
        }

        public bool TryBeginSkillCast(CombatSenderId sender, int casterCombatId, string skillId, out int activationId, out string reason)
        {
            activationId = 0;
            if (!AuthorizeSender(sender, casterCombatId, "skill cast", true, out reason)) return false;
            _senderBound = true;
            try { return TryBeginSkillCast(casterCombatId, skillId, out activationId, out reason); }
            finally { _senderBound = false; }
        }

        public bool CompleteSkillCast(CombatSenderId sender, int casterCombatId, int activationId)
        {
            if (!AuthorizeSender(sender, casterCombatId, "skill complete", true, out _)) return false;
            _senderBound = true;
            try { return CompleteSkillCast(casterCombatId, activationId); }
            finally { _senderBound = false; }
        }

        public bool SubmitSkill(CombatSenderId sender, SkillIntent intent, HitCandidate hit, out string reason)
        {
            if (!AuthorizeSender(sender, intent.InstigatorCombatId, "skill hit", true, out reason)) return false;
            _senderBound = true;
            try { return SubmitSkill(intent, hit, out reason); }
            finally { _senderBound = false; }
        }

        public bool LaunchSkillProjectile(CombatSenderId sender, SkillIntent intent, int projectileIndex, out string reason)
        {
            if (!AuthorizeSender(sender, intent.InstigatorCombatId, "skill projectile", true, out reason)) return false;
            _senderBound = true;
            bool launched;
            try { launched = LaunchSkillProjectile(intent, projectileIndex); }
            finally { _senderBound = false; }
            reason = launched ? string.Empty : LastSkillRejectReason;
            return launched;
        }
    }
}
