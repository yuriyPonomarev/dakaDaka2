using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;

namespace Emberdelve.Combat
{
    /// <summary>
    /// Player/AI intent only. It carries no damage value on purpose: damage is computed by the
    /// authority from weapon data plus validated geometry. A future server receives this same struct
    /// over the network and runs the identical validation.
    /// </summary>
    public readonly struct AttackIntent
    {
        public readonly int InstigatorCombatId;
        public readonly EntityKind InstigatorKind;
        public readonly string WeaponId;
        public readonly WeaponData Weapon;
        public readonly float Charge;
        public readonly Vector3 Origin;
        public readonly Vector3 Direction;
        public readonly float MaxRange;
        public readonly int Sequence;
        public readonly float Timestamp;

        public AttackIntent(int instigatorCombatId, EntityKind instigatorKind, WeaponData weapon, float charge,
            Vector3 origin, Vector3 direction, float maxRange, int sequence, float timestamp)
        {
            InstigatorCombatId = instigatorCombatId;
            InstigatorKind = instigatorKind;
            Weapon = weapon;
            WeaponId = weapon != null ? weapon.weaponId : string.Empty;
            Charge = Mathf.Clamp01(charge);
            Origin = origin;
            Direction = direction.normalized;
            MaxRange = maxRange;
            Sequence = sequence;
            Timestamp = timestamp;
        }
    }

    /// <summary>
    /// Intent for a skill cast. Same shape as <see cref="AttackIntent"/> on purpose: a skill is an
    /// attack with a different source of damage, so it travels the same validated pipeline, claims a
    /// sequence from the same per-entity allocator, and can be replayed to a server unchanged.
    ///
    /// It carries no damage number. Damage comes from the skill asset plus the weapon actually held,
    /// resolved by the authority. Every other field is a REQUEST: the authority resolves the skill from the
    /// activation it opened itself, rejects a claimed weapon or range it cannot confirm, and IGNORES
    /// <see cref="Timestamp"/>, <see cref="TargetStatus"/> and <see cref="EffectScale"/> (cooldown uses the
    /// authority clock; the status comes from the skill asset; the scale is read from the caster's own stats).
    /// The fields stay so the data shape is unchanged and so tests can forge them.
    /// </summary>
    public readonly struct SkillIntent
    {
        public readonly int InstigatorCombatId;
        public readonly EntityKind InstigatorKind;
        public readonly string SkillId;
        public readonly SkillData Skill;
        public readonly WeaponData Weapon;
        public readonly Vector3 Origin;
        public readonly Vector3 Direction;
        public readonly float MaxRange;
        public readonly int Sequence;
        public readonly float Timestamp;

        /// <summary>
        /// Identifies one button press. A three arrow volley or an arrow that pierces two bodies submits
        /// several intents for the same activation, and the authority must be able to tell that apart from
        /// the player pressing the skill key again.
        /// </summary>
        public readonly int ActivationId;

        /// <summary>Status the authority should apply to the target if - and only if - the hit is accepted.</summary>
        public readonly StatusSpec TargetStatus;

        /// <summary>
        /// The caster's ability effect scale for this activation (Docs/Design/ATTRIBUTES.md:
        /// clamp(1 + 0.04 * CHA modifier, 0.6, 1.4)), snapshotted by the caster when the cast was accepted
        /// and carried with every intent that activation produces. It is a magnitude for the authority to
        /// bound and apply, not a damage number: the damage itself is still derived by the authority from
        /// the skill asset and the weapon actually held.
        ///
        /// Callers that have no attribute state (an enemy, a synthetic intent, anything predating this
        /// amendment) leave it at 1 and resolve exactly as they did before.
        /// </summary>
        public readonly float EffectScale;

        public SkillIntent(int instigatorCombatId, EntityKind instigatorKind, SkillData skill, WeaponData weapon,
            Vector3 origin, Vector3 direction, float maxRange, int sequence, float timestamp, StatusSpec targetStatus,
            int activationId = 0, float effectScale = 1f)
        {
            InstigatorCombatId = instigatorCombatId;
            InstigatorKind = instigatorKind;
            Skill = skill;
            SkillId = skill != null ? skill.skillId : string.Empty;
            Weapon = weapon;
            Origin = origin;
            Direction = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.zero;
            MaxRange = Mathf.Max(0.1f, maxRange);
            Sequence = sequence;
            Timestamp = timestamp;
            TargetStatus = targetStatus;
            ActivationId = activationId;
            EffectScale = effectScale > 0f && !float.IsNaN(effectScale) && !float.IsInfinity(effectScale)
                ? effectScale
                : 1f;
        }
    }

    /// <summary>
    /// Where the attacker claims the swing/shot connected. The authority re-resolves the target from its own registry.</summary>
    public readonly struct HitCandidate
    {
        public readonly int TargetCombatId;
        public readonly Vector3 HitPoint;
        public readonly bool Headshot;

        public HitCandidate(int targetCombatId, Vector3 hitPoint, bool headshot)
        {
            TargetCombatId = targetCombatId;
            HitPoint = hitPoint;
            Headshot = headshot;
        }
    }

    /// <summary>
    /// D1a request to begin a swept melee swing. Plain data, the whole of what a client sends: WHO swings, WITH
    /// WHICH weapon, WHICH of the weapon's patterns (at which combo step), the aim the client believes it has
    /// and the command sequence. There is deliberately no hit point, range, timestamp, damage, head flag or
    /// target: the authority validates the request, then places the weapon segment from its own copy of the
    /// attacker's pose and the pattern asset and decides every hit itself.
    /// </summary>
    public readonly struct MeleeSwingIntent
    {
        public readonly int InstigatorCombatId;
        public readonly string WeaponId;
        public readonly int PatternIndex;
        public readonly int ComboStep;
        public readonly float AimYaw;
        public readonly float AimPitch;
        public readonly int Sequence;

        public MeleeSwingIntent(int instigatorCombatId, string weaponId, int patternIndex, int comboStep,
            float aimYawDegrees, float aimPitchDegrees, int sequence)
        {
            InstigatorCombatId = instigatorCombatId;
            WeaponId = weaponId ?? string.Empty;
            PatternIndex = patternIndex;
            ComboStep = comboStep;
            AimYaw = aimYawDegrees;
            AimPitch = aimPitchDegrees;
            Sequence = sequence;
        }
    }

    public enum SwingPhase { Windup, Active, Recovery, Finished }

    public enum SwingEndReason { None, Completed, WallStop, InstigatorDied, InstigatorGone, WeaponChanged }

    /// <summary>One damage event of a swing, as the authority recorded it.</summary>
    public readonly struct MeleeSwingHit
    {
        public readonly int TargetCombatId;
        public readonly HitLocation Location;
        public readonly float Amount;
        public readonly bool Blocked;
        /// <summary>Sample at which the damage was resolved (the end of the target's contact window, or the wall/end sample).</summary>
        public readonly int Sample;

        /// <summary>Sample at which the weapon segment first touched the target.</summary>
        public readonly int FirstSample;
        public readonly Vector3 Point;

        /// <summary>D1b: where on the damage segment the contact was (0 = hilt end, 1 = tip), computed by the authority from the sweep.</summary>
        public readonly float ContactT;

        /// <summary>D1b: the impact-zone multiplier the weapon's bands gave that contact.</summary>
        public readonly float ZoneMultiplier;

        /// <summary>D1b: the target's guard was engaged and broken by this hit.</summary>
        public readonly bool GuardBroken;

        public MeleeSwingHit(int targetCombatId, HitLocation location, float amount, bool blocked, int sample, int firstSample, Vector3 point,
            float contactT = 0f, float zoneMultiplier = 1f, bool guardBroken = false)
        {
            ContactT = contactT;
            ZoneMultiplier = zoneMultiplier;
            GuardBroken = guardBroken;
            TargetCombatId = targetCombatId;
            Location = location;
            Amount = amount;
            Blocked = blocked;
            Sample = sample;
            FirstSample = firstSample;
            Point = point;
        }
    }

    /// <summary>Read-only snapshot of one swing for the client (visual, recoil) and for tools and gates.</summary>
    public readonly struct MeleeSwingStatus
    {
        public readonly int SwingId;
        public readonly int InstigatorCombatId;
        public readonly SwingPhase Phase;
        public readonly SwingEndReason EndReason;
        public readonly float Elapsed;
        public readonly int SamplesProcessed;
        public readonly int SampleCount;
        public readonly int WallContactSample;
        public readonly IReadOnlyList<MeleeSwingHit> Hits;

        /// <summary>D1b: which step of the weapon's combo chain this swing is, and the damage multiplier the chain gave it.</summary>
        public readonly int ComboStep;
        public readonly float StepMultiplier;

        /// <summary>D1b: the resolved timing of this swing (chain step scales and the finisher's extra recovery applied).</summary>
        public readonly Emberdelve.Data.SwingTiming Timing;

        /// <summary>
        /// D1c2: total degrees (yaw plus pitch, summed over every step of the swing) by which the authority's turn ceiling
        /// held its swept aim pose back from the reported pose and DISCARDED. 0 for any honest turn.
        /// </summary>
        public readonly float TurnClampedDegrees;

        /// <summary>
        /// B2: where the blade met level geometry when the swing was wall-stopped (world space, the authority's own
        /// geometry), the surface normal there (zero when the contact has none, e.g. a buried hand), and whether the
        /// point is valid. Presentation only: spark and camera kick read it, nothing in damage does.
        /// </summary>
        public readonly Vector3 WallPoint;
        public readonly Vector3 WallNormal;
        public readonly bool HasWallPoint;

        public MeleeSwingStatus(int swingId, int instigatorCombatId, SwingPhase phase, SwingEndReason endReason,
            float elapsed, int samplesProcessed, int sampleCount, int wallContactSample, IReadOnlyList<MeleeSwingHit> hits,
            int comboStep = 0, float stepMultiplier = 1f, Emberdelve.Data.SwingTiming timing = default,
            float turnClampedDegrees = 0f, Vector3 wallPoint = default, Vector3 wallNormal = default, bool hasWallPoint = false)
        {
            WallPoint = wallPoint;
            WallNormal = wallNormal;
            HasWallPoint = hasWallPoint;
            TurnClampedDegrees = turnClampedDegrees;
            ComboStep = comboStep;
            StepMultiplier = stepMultiplier;
            Timing = timing;
            SwingId = swingId;
            InstigatorCombatId = instigatorCombatId;
            Phase = phase;
            EndReason = endReason;
            Elapsed = elapsed;
            SamplesProcessed = samplesProcessed;
            SampleCount = sampleCount;
            WallContactSample = wallContactSample;
            Hits = hits;
        }

        public bool Finished => Phase == SwingPhase.Finished;
        public bool WallStopped => EndReason == SwingEndReason.WallStop;
    }

    /// <summary>
    /// B2: read-only view of an instigator's melee combo chain for the HUD pips. Implemented by the authority next to
    /// <see cref="ICombatAuthority.TryGetExpectedComboStep"/>; it changes nothing.
    /// </summary>
    public interface IComboStateSource
    {
        /// <summary>
        /// False when the instigator is unknown or its held weapon has no combo chain (ranged, shield, single pattern).
        /// Otherwise <paramref name="total"/> is the chain length, <paramref name="step"/> the number of chain steps
        /// already started in the live chain (0 = no live chain; the finisher in progress counts as <paramref name="total"/>),
        /// and <paramref name="windowRemaining"/> the seconds left before the live chain would reset on the authority
        /// clock (0 when none; while a swing is still running it also includes the time until its recovery begins).
        /// </summary>
        bool TryGetComboState(int instigatorCombatId, out int step, out int total, out float windowRemaining);
    }

    public interface ICombatAuthority
    {
        /// <summary>
        /// D1a: asks the authority to begin a swept melee swing. The authority validates the request (alive,
        /// weapon equal to the one the instigator actually holds, the pattern belongs to that weapon at that
        /// combo step, command sequence, cooldown on its own clock, no swing already running, aim consistent with
        /// the pose it holds) and, when accepted, steps the swing on its own tick, sweeping the pattern's
        /// weapon segment through the targets' hit volumes. Returns the swing id on success.
        /// </summary>
        bool TryBeginMeleeSwing(in MeleeSwingIntent intent, out int swingId, out string reason);

        /// <summary>State of a running or recently finished swing. False when the id is unknown or has aged out.</summary>
        bool TryGetMeleeSwingStatus(int swingId, out MeleeSwingStatus status);

        /// <summary>
        /// D1b: the combo step the authority would accept next for this instigator. 0 when it has no running chain, the
        /// combo window ran out, the weapon changed, it died, or the chain is complete. This is the authority's own state,
        /// so a client asks it instead of counting steps itself.
        /// </summary>
        bool TryGetExpectedComboStep(int instigatorCombatId, out int step);

        /// <summary>Returns true when the authority accepted the intent and applied damage.</summary>
        bool SubmitAttack(AttackIntent intent, HitCandidate hit);

        /// <summary>Asks the authority to launch one ranged projectile; no target or block result is client supplied.</summary>
        bool LaunchProjectile(AttackIntent intent);

        /// <summary>
        /// Opens a skill activation. This is the ONLY way a skill cast starts, and the authority decides
        /// everything about it from its own state: the caster must own the skill in its kit, be alive, hold the
        /// weapon the skill needs, be off cooldown on the authority clock, and be able to pay the mana and the
        /// ammo, which the authority itself spends from the caster's own components. A cast that is refused
        /// spends nothing. On success the authority allocates the activation id the cast's hits must carry
        /// (<see cref="SkillIntent.ActivationId"/>); an id the authority did not hand to that caster for that
        /// skill, or one past its hit budget or window, is refused by <see cref="SubmitSkill"/>. The caller
        /// passes a skill id and nothing else: weapon, scale, status specs and costs come from the authority.
        /// </summary>
        bool TryBeginSkillCast(int casterCombatId, string skillId, out int activationId, out string reason);

        /// <summary>
        /// The caster reports that the effect finished. The authority closes the activation (no later hit can
        /// ride on it) and applies the skill's own self status, taken from the skill asset and scaled by the
        /// caster's own effect scale, after the hits so a cry does not empower itself. Skipping this call only
        /// costs the caster its buff; calling it early only closes the activation early.
        /// </summary>
        bool CompleteSkillCast(int casterCombatId, int activationId);

        /// <summary>
        /// Returns true when the authority accepted the skill hit and applied its damage (and the skill's own
        /// target status). The hit must belong to an open activation of this caster (see
        /// <see cref="TryBeginSkillCast"/>); the claimed weapon, range, hit point, headshot flag, timestamp,
        /// effect scale and status are requests that are bounded or ignored. Skills share the anti-replay
        /// sequence with everything else this entity does.
        /// </summary>
        bool SubmitSkill(SkillIntent intent, HitCandidate hit);

        /// <summary>Launches one projectile for an open ranged-skill activation; targets are discovered during flight.</summary>
        bool LaunchSkillProjectile(SkillIntent intent, int projectileIndex);

        // ---- R1c1: sender-bound commands -------------------------------------------------------------------------
        // The same commands, carrying the identity of whoever sent them. The authority refuses, before reading or
        // changing any state, a command whose instigator/caster combat id the sender does not own (see
        // CombatSenders.Authorize). Player code uses these with PlayerRig.CombatSender; the overloads above stay the
        // local trusted path for enemies, fixtures and tools.

        bool SubmitAttack(CombatSenderId sender, AttackIntent intent, HitCandidate hit, out string reason);
        bool TryBeginMeleeSwing(CombatSenderId sender, in MeleeSwingIntent intent, out int swingId, out string reason);
        bool LaunchProjectile(CombatSenderId sender, AttackIntent intent, out string reason);
        bool TryBeginSkillCast(CombatSenderId sender, int casterCombatId, string skillId, out int activationId, out string reason);
        bool CompleteSkillCast(CombatSenderId sender, int casterCombatId, int activationId);
        bool SubmitSkill(CombatSenderId sender, SkillIntent intent, HitCandidate hit, out string reason);
        bool LaunchSkillProjectile(CombatSenderId sender, SkillIntent intent, int projectileIndex, out string reason);
    }

    /// <summary>
    /// What the authority reads about a skill caster. Implemented by the entity (the player rig), never
    /// supplied by a caller: the active kit decides which skills can be cast, the mana pool is the one the
    /// authority spends from, the effect scale is the caster's own CHA-derived value and the epoch changes
    /// whenever the kit is re-applied or cleared (class apply, death, respawn) so stale cooldown stamps die
    /// with it. Local seam only: a server would read the same answers from replicated state.
    /// </summary>
    public interface ISkillCasterState
    {
        IReadOnlyList<SkillData> ActiveSkills { get; }
        ManaComponent Mana { get; }
        float AbilityEffectScale { get; }
        int KitEpoch { get; }
    }

    /// <summary>
    /// The ammunition the AUTHORITY can check and spend for an entity (the player's ranged magazine). An
    /// instigator that is not an ammo source (an enemy) has no ammunition model and is never ammo gated.
    /// </summary>
    public interface IAmmoSource
    {
        bool HasAmmo(int count);
        bool TryConsumeAmmo(int count);
    }

    /// <summary>
    /// The single hostility rule (R7). A player's team is its PARTY (<see cref="TeamForParty"/>); monsters share
    /// <see cref="MonsterTeam"/>.
    ///   different team            -> hostile (monster vs player, and party vs party: PvP is on)
    ///   same team, monsters       -> not hostile
    ///   same team, same party     -> friendly, unless <see cref="FriendlyFire"/> is on
    /// An entity that left the dungeon (<see cref="IRunParticipant.IsInPlay"/> false) is nobody's enemy.
    /// </summary>
    public static class CombatTeams
    {
        /// <summary>Team of party 0, and the default of a player rig nobody has assigned to a party (a solo run).</summary>
        public const int PlayerTeam = 0;
        public const int MonsterTeam = 1;

        /// <summary>The largest party the dungeon admits (DND_PARITY: parties of up to 3). The run director enforces it; GameConfig can lower it.</summary>
        public const int DefaultMaxPartySize = 3;

        /// <summary>
        /// Friendly fire between members of one party. Default OFF (DND_PARITY open question). Per-run state: the run
        /// director sets it from <c>GameConfig.friendlyFire</c> when a run starts and <see cref="CombatRegistry.ResetForRun"/> clears it.
        /// </summary>
        public static bool FriendlyFire { get; set; }

        /// <summary>Party 0 is team 0 (compatible with the old constant); party p >= 1 is team p + 1, which skips the monster team.</summary>
        public static int TeamForParty(int partyId) => partyId <= 0 ? PlayerTeam : partyId + 1;

        public static bool IsMonsterTeam(int teamId) => teamId == MonsterTeam;

        public static bool AreHostile(ICombatEntity instigator, ICombatEntity target) => AreHostile(instigator, target, FriendlyFire);

        public static bool AreHostile(ICombatEntity instigator, ICombatEntity target, bool friendlyFire)
        {
            if (instigator == null || target == null) return false;
            if (instigator.CombatId == target.CombatId) return false;
            if (target is IRunParticipant targetParticipant && !targetParticipant.IsInPlay) return false;
            if (instigator is IRunParticipant instigatorParticipant && !instigatorParticipant.IsInPlay) return false;
            if (instigator.TeamId != target.TeamId) return true;
            if (IsMonsterTeam(instigator.TeamId)) return false;
            return friendlyFire;
        }
    }

    /// <summary>
    /// Local stand-in for the entity table an authoritative server would own: combat id -> entity.
    /// Ids are allocated here so nothing trusts a scene reference handed over by a client.
    /// </summary>
    public static class CombatRegistry
    {
        private static readonly Dictionary<int, ICombatEntity> _entities = new Dictionary<int, ICombatEntity>();
        private static readonly List<ICombatEntity> _ordered = new List<ICombatEntity>();
        private static readonly Dictionary<int, int> _lastSequence = new Dictionary<int, int>();
        private static int _nextId = 1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatic() => ResetForRun();

        /// <summary>
        /// R7: drops everything this process-wide table learned during a run: entities, per-entity command sequences, the
        /// id counter and the per-run hostility switch (friendly fire). The run director calls it when its scene is
        /// destroyed (and on domain/play-mode start), so a second run or scene can never inherit a stale entity, a stale
        /// replay sequence or a friendly-fire setting. This stays a static table until the U3 server needs one per
        /// match; the reset points are what keep it from leaking across runs today.
        /// </summary>
        public static void ResetForRun()
        {
            _entities.Clear();
            _ordered.Clear();
            _lastSequence.Clear();
            CombatSenders.Reset();
            _nextId = 1;
            CombatTeams.FriendlyFire = false;
        }

        public static int AllocateId() => _nextId++;

        /// <summary>
        /// Next command sequence for a combat ENTITY. The sequence belongs to the entity, never to the
        /// component that happened to fire: one entity can attack with a sword and with a crossbow, and two
        /// private counters would make the second weapon's first attack look like a replay of the first.
        /// A future server validates the same per-entity sequence, so components must not invent their own.
        /// </summary>
        public static int NextSequence(int combatId)
        {
            _lastSequence.TryGetValue(combatId, out int last);
            int next = last + 1;
            _lastSequence[combatId] = next;
            return next;
        }

        public static void Register(int id, ICombatEntity entity)
        {
            if (entity == null) return;
            _entities[id] = entity;
            if (!_ordered.Contains(entity)) _ordered.Add(entity);
        }

        public static void Unregister(int id, ICombatEntity entity)
        {
            _entities.Remove(id);
            CombatSenders.Unbind(id);
            if (entity != null) _ordered.Remove(entity);
        }

        public static bool TryGet(int id, out ICombatEntity entity) => _entities.TryGetValue(id, out entity);

        public static IReadOnlyList<ICombatEntity> All => _ordered;

        /// <summary>
        /// R7: the nearest living PLAYER (any controller) that <paramref name="seeker"/> is hostile to, within
        /// <paramref name="maxDistance"/> of <paramref name="position"/>. Players that extracted, died or are friendly to the seeker
        /// are skipped, and <paramref name="preferred"/> (the seeker's current aggro target) keeps the choice unless a rival is
        /// closer by more than <paramref name="switchFactor"/> (0.8 = the rival must be 20 percent closer). Pass null for no preference.
        /// </summary>
        public static bool TryGetNearestHostilePlayer(ICombatEntity seeker, Vector3 position, float maxDistance,
            int preferredCombatId, float switchFactor, out ICombatEntity nearest)
        {
            nearest = null;
            float best = maxDistance * maxDistance;
            float preferredSqr = float.MaxValue;
            ICombatEntity preferred = null;

            for (int i = 0; i < _ordered.Count; i++)
            {
                ICombatEntity candidate = _ordered[i];
                if (candidate == null || candidate.IsDead) continue;
                if (!EntityKinds.IsPlayer(candidate.Kind)) continue;
                if (!CombatTeams.AreHostile(seeker, candidate)) continue;
                float sqr = (candidate.CombatCenter - position).sqrMagnitude;
                if (sqr > maxDistance * maxDistance) continue;
                if (candidate.CombatId == preferredCombatId)
                {
                    preferred = candidate;
                    preferredSqr = sqr;
                }
                if (sqr <= best)
                {
                    best = sqr;
                    nearest = candidate;
                }
            }

            if (preferred != null && nearest != null && !ReferenceEquals(preferred, nearest)
                && best >= preferredSqr * switchFactor * switchFactor)
            {
                nearest = preferred;
            }

            return nearest != null;
        }

        public static bool TryGetNearest(Vector3 position, EntityKind kind, float maxDistance, out ICombatEntity nearest)
        {
            nearest = null;
            float best = maxDistance * maxDistance;
            for (int i = 0; i < _ordered.Count; i++)
            {
                ICombatEntity candidate = _ordered[i];
                if (candidate == null || candidate.IsDead) continue;
                if (candidate.Kind != kind) continue;
                float sqr = (candidate.CombatCenter - position).sqrMagnitude;
                if (sqr <= best)
                {
                    best = sqr;
                    nearest = candidate;
                }
            }
            return nearest != null;
        }
    }
}
