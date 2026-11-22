using UnityEngine;
using Emberdelve.BladeDefense;
using Emberdelve.Core;
using Emberdelve.Data;

namespace Emberdelve.Combat
{
    /// <summary>
    /// U3C3: parry then riposte, server side. The authority records every successful sword block itself (the defender's guard HELD,
    /// the kit is a sword and not a shield, the hit was melee), cancels the record when the defender takes damage, and answers the
    /// riposte requests the block press turns into. The two strikes are ordinary swept swings through
    /// <see cref="TryBeginMeleeSwing(in MeleeSwingIntent, out int, out string)"/> with the weapon's riposte pattern and multiplier
    /// (step multiplier only: impact power is untouched, so a riposte never breaks a guard its normal swing would not).
    /// </summary>
    public sealed partial class LocalCombatAuthority : IRiposteAuthority
    {
        private struct RiposteOverride
        {
            public bool Active;
            public int PatternIndex;
            public float Multiplier;
            public int Strike;
            public bool SkipCooldown;
        }

        private RiposteOverride _riposteOverride;
        private readonly RiposteTracker _riposte = new RiposteTracker();

        /// <summary>Read-only view of the parry / riposte memory for tools, gates and tests.</summary>
        public RiposteTracker Riposte => _riposte;

        /// <summary>Verdict of the most recent riposte request (Allowed after an accepted one).</summary>
        public RiposteVerdict LastRiposteVerdict { get; private set; }

        /// <summary>Reason text of the most recent refused riposte request (empty after an accepted one).</summary>
        public string LastRiposteReason { get; private set; } = string.Empty;

        private static bool RegisteredId(int id) => CombatRegistry.TryGet(id, out _);

        /// <summary>A hit that damaged <paramref name="target"/>: any damage cancels its live parry and stops its riposte.</summary>
        private void NoteDamageTaken(ICombatEntity target, in DamageInfo damage)
        {
            if (target != null && damage.Amount > 0f) _riposte.NoteDamage(target.CombatId);
        }

        /// <summary>Records the sword response and returns whether this hit earned a physical parry.</summary>
        private bool NoteMeleeResult(int attackerId, ICombatEntity target, in DamageInfo damage)
        {
            if (target == null) return false;
            bool kitCan = target is IRiposteKit kit && RiposteKitOf(target, kit, out _);
            // Player guards have authoritative geometry. Abstract rule-only combat entities
            // retain their own guard contract; they do not expose a physical player blade.
            bool physicalPlayerBlock = !(target is Emberdelve.Player.PlayerRig)
                || (damage.MeleeGuardChecked && damage.GuardBladeContact);
            if (!damage.GuardShieldContact && physicalPlayerBlock
                && RiposteRules.IsRiposteBlock(damage.Blocked, damage.GuardBroken, damage.RangedProjectile, kitCan))
            {
                bool parryBonus = damage.MeleeGuardChecked && damage.GuardBladeContact
                    && target is Emberdelve.Player.PlayerRig rig && !rig.Defense.HasShield
                    && rig.TryGetGuardBlade(out Emberdelve.Player.GuardBladeBox blade)
                    && blade.IsInParryRegion(damage.Point, rig.ActiveWeapon.parryBladeRegion);
                // Sword/dagger response requires the confirmed sweet spot. A metal block
                // elsewhere still stops damage, but must not open or retain a parry grant.
                if (target is Emberdelve.Player.PlayerRig && !parryBonus)
                {
                    _riposte.Forget(target.CombatId);
                    return false;
                }
                _riposte.NoteBlock(target.CombatId, attackerId, Now, WeaponEpochOf(target), parryBonus);
                return parryBonus;
            }

            NoteDamageTaken(target, in damage);
            return false;
        }

        private static bool RiposteKitOf(ICombatEntity entity, IRiposteKit kit, out WeaponData weapon)
        {
            weapon = entity is IActiveWeaponProvider holder ? holder.ActiveWeapon : null;
            return weapon != null && RiposteRules.KitCanRiposte(weapon.canRiposte, weapon.IsShield, kit.GuardHasShield, weapon.IsRanged, weapon.RiposteStrikeCount, weapon.riposteWithOffhandShield);
        }

        private bool BuildRiposteRequest(int id, out ICombatEntity entity, out WeaponData weapon, out RiposteRequest request)
        {
            weapon = null;
            request = default;
            if (!CombatRegistry.TryGet(id, out entity)) return false;
            bool kitCan = entity is IRiposteKit kit && RiposteKitOf(entity, kit, out weapon);
            request = new RiposteRequest
            {
                KitCanRiposte = kitCan,
                Alive = !entity.IsDead,
                Staggered = IsGuardStaggered(id),
                Busy = IsSwingInProgress(id),
                Window = weapon != null ? weapon.riposteWindow : 0f,
                WeaponEpoch = WeaponEpochOf(entity)
            };
            return true;
        }

        /// <summary>Seconds left in the riposte window of this entity's live parry (0 when there is none). Presentation / HUD read.</summary>
        public float RiposteWindowRemaining(int combatId)
        {
            if (!BuildRiposteRequest(combatId, out ICombatEntity entity, out WeaponData weapon, out RiposteRequest req) || weapon == null) return 0f;
            return _riposte.WindowRemaining(combatId, Now, req.Window);
        }

        public bool TryBeginRiposte(CombatSenderId sender, in MeleeSwingIntent intent, out int swingId, out int strikesTotal, out string reason)
        {
            swingId = 0;
            strikesTotal = 0;
            if (!AuthorizeSender(sender, intent.InstigatorCombatId, "riposte", false, out reason))
            {
                LastRiposteReason = reason;
                return false;
            }

            int id = intent.InstigatorCombatId;
            if (!BuildRiposteRequest(id, out _, out WeaponData weapon, out RiposteRequest request))
            {
                reason = "unknown instigator id";
                LastRiposteReason = reason;
                return false;
            }

            float now = Now;
            RiposteVerdict verdict = _riposte.Check(id, now, in request);
            LastRiposteVerdict = verdict;
            if (verdict != RiposteVerdict.Allowed) return RefuseRiposte(id, verdict, out reason);

            if (!weapon.TryResolveRiposteStrike(0, out int patternIndex, out SwingPatternData pattern))
            {
                return RefuseRiposte(id, RiposteVerdict.KitCannotRiposte, out reason);
            }

            if (!RunRiposteSwing(in intent, patternIndex, weapon, 1, false, out swingId, out reason))
            {
                LastRiposteReason = reason;
                return false;
            }

            strikesTotal = weapon.RiposteStrikeCount;
            _riposte.Commit(id, strikesTotal, SwingEndTime(swingId), weapon.riposteStrikeGap);
            LastRiposteVerdict = RiposteVerdict.Allowed;
            LastRiposteReason = string.Empty;
            return true;
        }

        public RiposteResult TryContinueRiposte(CombatSenderId sender, in MeleeSwingIntent intent, out int swingId, out string reason)
        {
            swingId = 0;
            if (!AuthorizeSender(sender, intent.InstigatorCombatId, "riposte strike", false, out reason))
            {
                LastRiposteReason = reason;
                return RiposteResult.Refused;
            }

            int id = intent.InstigatorCombatId;
            if (!BuildRiposteRequest(id, out _, out WeaponData weapon, out RiposteRequest request))
            {
                reason = "unknown instigator id";
                LastRiposteReason = reason;
                return RiposteResult.Refused;
            }

            RiposteVerdict verdict = _riposte.CheckContinue(id, Now, in request);
            if (verdict == RiposteVerdict.TooEarly)
            {
                reason = "the strike gap has not passed";
                return RiposteResult.NotYet;
            }

            LastRiposteVerdict = verdict;
            if (verdict != RiposteVerdict.Allowed)
            {
                _riposte.CancelGrant(id);
                RefuseRiposte(id, verdict, out reason, false);
                return RiposteResult.Refused;
            }

            int strike = _riposte.StrikesDone(id);
            if (!weapon.TryResolveRiposteStrike(strike, out int patternIndex, out _))
            {
                _riposte.CancelGrant(id);
                RefuseRiposte(id, RiposteVerdict.KitCannotRiposte, out reason, false);
                return RiposteResult.Refused;
            }

            if (!RunRiposteSwing(in intent, patternIndex, weapon, strike + 1, true, out swingId, out reason))
            {
                _riposte.CancelGrant(id);
                LastRiposteReason = reason;
                return RiposteResult.Refused;
            }

            _riposte.CommitContinue(id, SwingEndTime(swingId), weapon.riposteStrikeGap);
            LastRiposteVerdict = RiposteVerdict.Allowed;
            LastRiposteReason = string.Empty;
            return RiposteResult.Started;
        }

        private bool RunRiposteSwing(in MeleeSwingIntent intent, int patternIndex, WeaponData weapon, int strikeNumber, bool skipCooldown, out int swingId, out string reason)
        {
            bool previousBound = _senderBound;
            _riposteOverride = new RiposteOverride
            {
                Active = true,
                PatternIndex = patternIndex,
                Multiplier = _riposte.HasParryBonus(intent.InstigatorCombatId)
                    ? RiposteRules.StrikeMultiplier(weapon.riposteDamageMultiplier) : 1f,
                Strike = strikeNumber,
                SkipCooldown = skipCooldown
            };
            _senderBound = true;
            try
            {
                return TryBeginMeleeSwing(in intent, out swingId, out reason);
            }
            finally
            {
                _senderBound = previousBound;
                _riposteOverride = default;
            }
        }

        private float SwingEndTime(int swingId)
        {
            return TryGetMeleeSwingStatus(swingId, out MeleeSwingStatus status) ? Now + Mathf.Max(0f, status.Timing.Total - status.Elapsed) : Now;
        }

        private bool RefuseRiposte(int id, RiposteVerdict verdict, out string reason, bool countReject = true)
        {
            reason = "riposte refused: " + verdict;
            LastRiposteVerdict = verdict;
            LastRiposteReason = reason;
            // A block press with no parry on record is simply a guard being raised (every guard raise is that press): not a rejection worth counting or logging.
            if (verdict == RiposteVerdict.NoParry) return false;
            if (countReject) RejectedCount++;
            if (logRejections)
            {
                CombatDiagnostics.Log($"reject-riposte:{id}:{verdict}", $"[CombatAuthority] Rejected riposte for #{id}: {verdict}", this);
            }
            return false;
        }
    }
}
