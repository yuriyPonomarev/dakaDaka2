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
    /// <c>PlayerRig.CombatSender</c>. Closing the sender-less path for player-owned ids is R1c3.
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
            if (CombatSenders.Authorize(sender, entityId, out reason)) return true;

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
            return SubmitAttack(intent, hit, out reason);
        }

        public bool TryBeginMeleeSwing(CombatSenderId sender, in MeleeSwingIntent intent, out int swingId, out string reason)
        {
            swingId = 0;
            if (!AuthorizeSender(sender, intent.InstigatorCombatId, "swing", false, out reason)) return false;
            return TryBeginMeleeSwing(in intent, out swingId, out reason);
        }

        public bool LaunchProjectile(CombatSenderId sender, AttackIntent intent, out string reason)
        {
            if (!AuthorizeSender(sender, intent.InstigatorCombatId, "projectile", false, out reason)) return false;
            bool launched = LaunchProjectile(intent);
            reason = launched ? string.Empty : LastAttackRejectReason;
            return launched;
        }

        public bool TryBeginSkillCast(CombatSenderId sender, int casterCombatId, string skillId, out int activationId, out string reason)
        {
            activationId = 0;
            if (!AuthorizeSender(sender, casterCombatId, "skill cast", true, out reason)) return false;
            return TryBeginSkillCast(casterCombatId, skillId, out activationId, out reason);
        }

        public bool CompleteSkillCast(CombatSenderId sender, int casterCombatId, int activationId)
        {
            return AuthorizeSender(sender, casterCombatId, "skill complete", true, out _)
                   && CompleteSkillCast(casterCombatId, activationId);
        }

        public bool SubmitSkill(CombatSenderId sender, SkillIntent intent, HitCandidate hit, out string reason)
        {
            if (!AuthorizeSender(sender, intent.InstigatorCombatId, "skill hit", true, out reason)) return false;
            return SubmitSkill(intent, hit, out reason);
        }

        public bool LaunchSkillProjectile(CombatSenderId sender, SkillIntent intent, int projectileIndex, out string reason)
        {
            if (!AuthorizeSender(sender, intent.InstigatorCombatId, "skill projectile", true, out reason)) return false;
            bool launched = LaunchSkillProjectile(intent, projectileIndex);
            reason = launched ? string.Empty : LastSkillRejectReason;
            return launched;
        }
    }
}
