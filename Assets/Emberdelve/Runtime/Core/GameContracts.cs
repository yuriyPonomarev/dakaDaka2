using UnityEngine;

namespace Emberdelve.Core
{
    public enum RunState { Lobby, Briefing, Running, Paused, Dead, Extracted }

    /// <summary>
    /// What an entity is. R7: <see cref="LocalPlayer"/> now means "a player that THIS process controls" (input, camera
    /// and HUD binding) and <see cref="RemotePlayer"/> "a player somebody else controls". Gameplay rules (hostility,
    /// enemy targeting, kills, feedback routing) never branch on that difference: they use <see cref="EntityKinds.IsPlayer"/>
    /// and the player/party ids.
    /// </summary>
    public enum EntityKind { LocalPlayer, RemotePlayer, Enemy, Neutral }

    public static class EntityKinds
    {
        /// <summary>True for any player, whoever controls it.</summary>
        public static bool IsPlayer(EntityKind kind) => kind == EntityKind.LocalPlayer || kind == EntityKind.RemotePlayer;
    }

    public enum DamageType { Physical, Pierce, Fire, Holy }

    /// <summary>
    /// Where on a target's body a hit landed (D1a). Decided by the AUTHORITY from the target's own hit volumes,
    /// never claimed by the attacker. <see cref="Unspecified"/> is a hit that has no location (the legacy
    /// arc-less melee/ranged paths and anything area based): its multiplier is 1. The numeric order is the
    /// tie-break priority (lower wins) when two volumes are touched equally deep.
    /// </summary>
    public enum HitLocation { Unspecified = 0, Head = 1, Body = 2, Arms = 3, Hands = 4, Legs = 5, Feet = 6 }

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

        /// <summary>D1a/D1c3: the hit volume the swept melee or an authority-stepped projectile touched (Unspecified for every other damage source and for targets without hit volumes).</summary>
        public readonly HitLocation Location;

        /// <summary>D1b: the attacker weapon's impact power for this hit (weapon x pattern), set by the authority from data. 0 = the source has none (skills with a flat number) and can never break a guard.</summary>
        public readonly float ImpactPower;

        /// <summary>D1b: the defender's guard was engaged but the hit's impact power reached its impact resist: reduced mitigation and a guard stagger. Always accompanied by <see cref="Blocked"/>.</summary>
        public readonly bool GuardBroken;
        /// <summary>True only for a hit produced by an authority-stepped ranged projectile.</summary>
        public readonly bool RangedProjectile;
        /// <summary>True when that projectile physically intersected the defender's guarding blade.</summary>
        public readonly bool GuardBladeContact;

        public DamageInfo(float amount, Vector3 point, Vector3 direction, Vector3 sourcePosition,
            int sourceCombatId, EntityKind sourceKind, DamageType type, bool blocked, bool headshot, float blockReduction,
            HitLocation location = HitLocation.Unspecified, float impactPower = 0f, bool guardBroken = false,
            bool rangedProjectile = false, bool guardBladeContact = false)
        {
            ImpactPower = impactPower;
            GuardBroken = guardBroken;
            RangedProjectile = rangedProjectile;
            GuardBladeContact = guardBladeContact;
            Location = location;
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

        public DamageInfo WithReduction(float reduction, out bool blocked, bool guardBroken = false)
        {
            blocked = reduction > 0f;
            return new DamageInfo(Mathf.Max(1f, Amount * (1f - Mathf.Clamp01(reduction))), Point, Direction,
                SourcePosition, SourceCombatId, SourceKind, Type, blocked, Headshot, Mathf.Clamp01(reduction), Location,
                ImpactPower, guardBroken && blocked, RangedProjectile, GuardBladeContact);
        }

        /// <summary>The same hit carrying the attacker's impact power (the resolver sets it before the defender's guard is asked).</summary>
        public DamageInfo WithImpactPower(float impactPower) =>
            new DamageInfo(Amount, Point, Direction, SourcePosition, SourceCombatId, SourceKind, Type, Blocked, Headshot,
                BlockReduction, Location, impactPower, GuardBroken, RangedProjectile, GuardBladeContact);

        public DamageInfo WithProjectileGuardContact(bool bladeContact) =>
            new DamageInfo(Amount, Point, Direction, SourcePosition, SourceCombatId, SourceKind, Type, Blocked,
                Headshot, BlockReduction, Location, ImpactPower, GuardBroken, true, bladeContact);
    }

    public readonly struct HitFeedback
    {
        public readonly Vector3 Point;
        public readonly float Damage;
        public readonly bool Blocked;
        public readonly bool Headshot;
        public readonly bool TargetKilled;
        public readonly EntityKind TargetKind;

        /// <summary>B2: the authority's hit location of this hit (Unspecified when the source has none); drives the hit-marker variant.</summary>
        public readonly HitLocation Location;

        public HitFeedback(Vector3 point, float damage, bool blocked, bool headshot, bool targetKilled, EntityKind targetKind,
            HitLocation location = HitLocation.Unspecified)
        {
            Point = point; Damage = damage; Blocked = blocked; Headshot = headshot;
            TargetKilled = targetKilled; TargetKind = targetKind; Location = location;
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
        /// Who this entity fights for (R7). A player's team id is derived from its party (<see cref="Emberdelve.Combat.CombatTeams.TeamForParty"/>),
        /// so two parties are hostile to each other (PvP) and party mates are friendly; monsters use
        /// <see cref="Emberdelve.Combat.CombatTeams.MonsterTeam"/> and are hostile to every player.
        /// </summary>
        int TeamId { get; }
    }

    /// <summary>
    /// R7: an entity that belongs to one player. The id is the player's identity in the run (not the combat id, which
    /// identifies a body): every player-scoped event carries it and every per-player rule is keyed on it.
    /// </summary>
    public interface IPlayerOwned
    {
        int PlayerId { get; }
        int PartyId { get; }
    }

    /// <summary>
    /// R7: whether an entity is still part of the fight. A player who extracted left the dungeon: no monster targets them
    /// and no weapon or skill can hit them. Dead players are covered by <see cref="IDamageable.IsDead"/>.
    /// </summary>
    public interface IRunParticipant
    {
        bool IsInPlay { get; }
    }

    /// <summary>
    /// R7: the entity that owns the presentation of a combat result. The authority resolves a hit and hands the result to
    /// the entities involved; a player rig turns it into events carrying ITS player id, so the authority never raises a
    /// HUD event or looks at who is "the" local player.
    /// </summary>
    public interface IPlayerCombatFeedback
    {
        /// <summary>This player was hit (damage already applied).</summary>
        void OnDamageTaken(in DamageInfo damage);

        /// <summary>This player's attack hit something.</summary>
        void OnHitDealt(in HitFeedback feedback);
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

    /// <summary>
    /// The weapon the AUTHORITY believes an entity is holding right now. The combat authority resolves the
    /// weapon from here and rejects an <see cref="Emberdelve.Combat.AttackIntent"/> that names a different one,
    /// so damage, reach and cooldown never come from a client-chosen asset. A player reports its loadout's
    /// active hand; an enemy reports its authored attack weapon. An instigator that does not implement this
    /// cannot submit weapon attacks at all (reject by default, not trust by default). Local seam only: a server
    /// would read the same answer from replicated equipment.
    /// </summary>
    public interface IActiveWeaponProvider
    {
        Emberdelve.Data.WeaponData ActiveWeapon { get; }
    }

    /// <summary>Optional action-rate read on a registered entity. The offline authority reads it locally; U3B must own its server equivalent. Never supplied by an intent.</summary>
    public interface IActionSpeedSource
    {
        float ActionSpeedFactor { get; }
    }

    /// <summary>
    /// Optional companion of <see cref="IActiveWeaponProvider"/>: a counter that goes up on EVERY change of the held
    /// weapon (also a change that is undone before anyone looked: an A-B-A swap inside one frame). The combat authority
    /// remembers it with a combo chain and a running swing and drops both when it differs, because comparing the
    /// current weapon with the chain's weapon cannot see A-B-A. An entity that does not implement it reports 0 (weapon
    /// identity comparison only, as before).
    /// </summary>
    public interface IWeaponChangeSource
    {
        int WeaponChangeCount { get; }
    }

    /// <summary>
    /// The authoritative aim pose of an entity that can swing a swept melee weapon (D1a): the eye position and
    /// the yaw/pitch (degrees, pitch positive DOWN, i.e. Quaternion.Euler(pitch, yaw, 0)) of its view. The
    /// combat authority reads this on its own tick to place the weapon segment, so a swing is positioned from
    /// the attacker's pose and the pattern data, never from anything the client sends with the intent. Local
    /// seam only: a server would read the same answer from the replicated pose.
    /// </summary>
    public interface IAimPoseProvider
    {
        bool TryGetAimPose(out Vector3 eye, out float yawDegrees, out float pitchDegrees);
    }

    /// <summary>Defenders expose their active guard through this so damage is reduced by the authority, not by the attacker.</summary>
    public interface IDefenseProvider
    {
        bool TryGetGuardReduction(in DamageInfo incoming, out float reduction);
    }

    /// <summary>What a defender's guard did to one hit (D1b).</summary>
    public readonly struct GuardResult
    {
        public readonly float Reduction;
        public readonly bool Broken;

        public GuardResult(float reduction, bool broken)
        {
            Reduction = reduction;
            Broken = broken;
        }
    }

    /// <summary>
    /// A defender whose guard can be BROKEN (D1b). The resolver hands it the hit (which carries the attacker's impact
    /// power) and the authority's tuning, and the defender answers with the reduction it applies and whether its impact
    /// resist was reached. The directional rule (frontal arc, never from behind) stays the defender's own. A defender
    /// that only implements IDefenseProvider keeps the old behaviour (no break).
    /// </summary>
    public interface IGuardBreakDefender
    {
        bool TryResolveGuard(in DamageInfo incoming, Emberdelve.Data.CombatFlowTuning tuning, out GuardResult result);
    }

    /// <summary>
    /// D1c5: an entity that reports whether its guard is up right now (the existing <c>PlayerRig.IsGuarding</c> read).
    /// The combat authority refuses a melee swing begin while the instigator reports true, so a forged client cannot
    /// block and attack at once. The guard flag itself is still client-held until U3B; this only removes the offline
    /// window. Optional: an entity that does not implement it is never refused.
    /// </summary>
    public interface IGuardStateSource
    {
        bool IsGuarding { get; }
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

    /// <summary>
    /// R2: the entity that acts on an <see cref="IInteractable"/>. Every interaction names its actor, so distance, line
    /// of sight, alive state, the bag that receives loot and the extraction rule are all evaluated for THAT player and
    /// never for a global "the player".
    /// </summary>
    public interface IInteractionActor : IPlayerOwned
    {
        bool IsAlive { get; }

        /// <summary>World position reach and line of sight are measured from (chest height on a player).</summary>
        Vector3 InteractionOrigin { get; }

        /// <summary>The actor's own bag (the item catalog comes from it). A host never trusts it for the carried value: it keeps its own session record.</summary>
        Emberdelve.Items.Inventory Inventory { get; }
    }

    /// <summary>Anything a player can act on with the interact key. R2: every call carries the acting entity.</summary>
    public interface IInteractable
    {
        string PromptText { get; }
        bool RequiresHold { get; }
        float HoldDuration { get; }
        bool CanInteract(IInteractionActor actor, out string blockedReason);
        void OnHoldComplete(IInteractionActor actor);
        void OnInteractInstant(IInteractionActor actor);
    }
}
