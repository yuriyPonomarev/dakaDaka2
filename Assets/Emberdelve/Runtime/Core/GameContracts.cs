using UnityEngine;

namespace Emberdelve.Core
{
    public enum RunState { Lobby, Briefing, Running, Paused, Dead, Extracted }

    public enum EntityKind { LocalPlayer, RemotePlayer, Enemy, Neutral }

    public enum DamageType { Physical, Pierce, Fire, Holy }

    /// <summary>
    /// A resolved damage application. Produced only by an <see cref="Emberdelve.Combat.ICombatAuthority"/>,
    /// never by the party that requested the attack. This is the shape a future authoritative
    /// server would broadcast to clients.
    /// </summary>
    public readonly struct DamageInfo
    {
        public readonly float Amount;
        public readonly Vector3 Point;
        public readonly Vector3 Direction;
        public readonly Vector3 SourcePosition;
        public readonly int SourceCombatId;
        public readonly EntityKind SourceKind;
        public readonly DamageType Type;
        public readonly bool Blocked;
        public readonly bool Headshot;
        public readonly float BlockReduction;

        public DamageInfo(float amount, Vector3 point, Vector3 direction, Vector3 sourcePosition,
            int sourceCombatId, EntityKind sourceKind, DamageType type, bool blocked, bool headshot, float blockReduction)
        {
            Amount = amount;
            Point = point;
            Direction = direction;
            SourcePosition = sourcePosition;
            SourceCombatId = sourceCombatId;
            SourceKind = sourceKind;
            Type = type;
            Blocked = blocked;
            Headshot = headshot;
            BlockReduction = blockReduction;
        }

        public DamageInfo WithReduction(float reduction, out bool blocked)
        {
            blocked = reduction > 0f;
            return new DamageInfo(Mathf.Max(1f, Amount * (1f - Mathf.Clamp01(reduction))), Point, Direction,
                SourcePosition, SourceCombatId, SourceKind, Type, blocked, Headshot, Mathf.Clamp01(reduction));
        }
    }

    public readonly struct HitFeedback
    {
        public readonly Vector3 Point;
        public readonly float Damage;
        public readonly bool Blocked;
        public readonly bool Headshot;
        public readonly bool TargetKilled;
        public readonly EntityKind TargetKind;

        public HitFeedback(Vector3 point, float damage, bool blocked, bool headshot, bool targetKilled, EntityKind targetKind)
        {
            Point = point; Damage = damage; Blocked = blocked; Headshot = headshot;
            TargetKilled = targetKilled; TargetKind = targetKind;
        }
    }

    public interface IDamageable
    {
        bool IsDead { get; }
        float CurrentHealth { get; }
        float MaxHealth { get; }
        void ApplyDamage(DamageInfo damage);
    }

    public interface ICombatant
    {
        EntityKind Kind { get; }
        int CombatId { get; }
        Vector3 CombatCenter { get; }
        Collider HeadCollider { get; }
    }

    /// <summary>One object that is both damageable and a combat participant (player rig, enemy agent).</summary>
    public interface ICombatEntity : IDamageable, ICombatant
    {
    }

    /// <summary>Defenders expose their active guard through this so damage is reduced by the authority, not by the attacker.</summary>
    public interface IDefenseProvider
    {
        bool TryGetGuardReduction(in DamageInfo incoming, out float reduction);
    }

    /// <summary>Anything the player can act on with the interact key.</summary>
    public interface IInteractable
    {
        string PromptText { get; }
        bool RequiresHold { get; }
        float HoldDuration { get; }
        bool CanInteract(out string blockedReason);
        void OnHoldComplete();
        void OnInteractInstant();
    }
}
