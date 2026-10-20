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

        /// <summary>What a resolver returns when the request could not produce any damage at all.</summary>
        public static DamageInfo Invalid => new DamageInfo(0f, Vector3.zero, Vector3.zero, Vector3.zero,
            -1, EntityKind.Neutral, DamageType.Physical, false, false, 0f);

        public bool IsValid => Amount > 0f && SourceCombatId >= 0;

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

        /// <summary>
        /// Who this entity fights for. Local and remote players share a team so friendly fire rules
        /// already exist before any networking does; monsters are a different team. 0 is the player team.
        /// </summary>
        int TeamId { get; }
    }

    /// <summary>
    /// Attackers expose their outgoing damage multipliers here instead of multiplying damage themselves.
    /// The combat authority reads it when it resolves a hit, which is the same information a server
    /// would read from its own replicated stats.
    /// </summary>
    public interface IDamageOutgoingProvider
    {
        float GetOutgoingDamageMultiplier(Emberdelve.Data.WeaponData weapon);
    }

    /// <summary>
    /// Attackers whose gear adds a FLAT amount to the weapon's own authored base damage expose it here, on a
    /// channel deliberately separate from <see cref="IDamageOutgoingProvider">IDamageOutgoingProvider</see>:
    /// a flat bonus is not a multiplier, so it is not smuggled into one and a multiplier is not reinterpreted
    /// as a flat. The combat authority reads it from the instigator it already validated, at the same
    /// accepted-hit boundary as the multiplier, so a client, an <see cref="Emberdelve.Combat.AttackIntent"/>
    /// or a <see cref="Emberdelve.Combat.SkillIntent"/> can never supply a bonus. An entity that does not
    /// implement this (an enemy, anything with no rolled gear) contributes 0 and resolves exactly as before.
    ///
    /// Same local seam as the multiplier provider: it is how the local authority reads its own actor's state,
    /// which is what a real server would read from replicated data. It is not proof of server authority (U3).
    /// </summary>
    public interface IDamageFlatWeaponBonusProvider
    {
        /// <summary>
        /// Flat damage added to the weapon's authored base BEFORE range falloff, charge, the outgoing
        /// multiplier, the ability scale and headshot. Keyed on the weapon actually used, so a melee affix
        /// contributes nothing to a ranged shot and a projectile affix nothing to a swing. The reader
        /// sanitizes the claim: NaN, ±Infinity and a negative total all count as 0.
        /// </summary>
        float GetFlatWeaponDamageBonus(Emberdelve.Data.WeaponData weapon);
    }

    /// <summary>
    /// One skill slot as the HUD needs to see it. A value type built by the skill system, so the HUD
    /// never has to reach into a rig, a cooldown timer or a mana pool.
    /// </summary>
    public readonly struct SkillSnapshot
    {
        public readonly int Slot;
        public readonly bool HasSkill;
        public readonly string SkillId;
        public readonly string DisplayName;
        public readonly string Description;
        public readonly string Summary;
        public readonly int ManaCost;
        public readonly float CooldownSeconds;
        public readonly float CooldownRemaining;
        public readonly bool Ready;
        public readonly bool Castable;
        public readonly string KeyLabel;
        public readonly UnityEngine.Color AccentColor;

        public SkillSnapshot(int slot, bool hasSkill, string skillId, string displayName, string description,
            string summary, int manaCost, float cooldownSeconds, float cooldownRemaining, bool ready,
            bool castable, string keyLabel, UnityEngine.Color accentColor)
        {
            Slot = slot;
            HasSkill = hasSkill;
            SkillId = skillId ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            Description = description ?? string.Empty;
            Summary = summary ?? string.Empty;
            ManaCost = manaCost;
            CooldownSeconds = cooldownSeconds;
            CooldownRemaining = cooldownRemaining;
            Ready = ready;
            Castable = castable;
            KeyLabel = keyLabel ?? string.Empty;
            AccentColor = accentColor;
        }

        public static SkillSnapshot Empty(int slot) =>
            new SkillSnapshot(slot, false, string.Empty, string.Empty, string.Empty, string.Empty, 0, 0f, 0f, false, false, string.Empty, UnityEngine.Color.gray);

        /// <summary>0 when off cooldown, 1 just after a cast.</summary>
        public float CooldownFraction => CooldownSeconds > 0f ? Mathf.Clamp01(CooldownRemaining / CooldownSeconds) : 0f;
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

    /// <summary>
    /// Defenders that wear physical armor expose their CURRENT effective armor rating here, so the mitigation
    /// is the authority's own read of the defender on the hit it already accepted - the same shape as
    /// <see cref="IDefenseProvider"/>. An <see cref="Emberdelve.Combat.AttackIntent"/>/
    /// <see cref="Emberdelve.Combat.SkillIntent"/> carries no armor claim and cannot be made to carry one, and
    /// an attacker's own rating is never consulted for damage it receives.
    ///
    /// The contract is OPTIONAL: an entity that does not implement it (an enemy, a rig with no armor on it)
    /// has a rating of 0 and resolves bit-for-bit as it did before this slice. The rating is a flat number of
    /// armor points - direction-independent, independent of guard/parry/shield behaviour, and never a
    /// Blocked flag. See Docs/Design/ARMOR_RULES.md for the prototype mitigation rule the resolver applies.
    /// Same local seam as the guard and outgoing-damage providers: how the local authority reads its own
    /// actor's state, which is what a real server would read from replicated gear. Not proof of server
    /// authority (U3).
    /// </summary>
    public interface IArmorRatingProvider
    {
        /// <summary>
        /// The defender's effective armor rating right now: the fixed authored <see cref="Emberdelve.Data.SkillStat.Armor"/>
        /// flat modifiers plus the rolled armor affix points of the very instances being worn, summed once.
        /// The resolver sanitizes the claim: NaN, ±Infinity and a negative rating all count as 0.
        /// </summary>
        float GetArmorRating();
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
