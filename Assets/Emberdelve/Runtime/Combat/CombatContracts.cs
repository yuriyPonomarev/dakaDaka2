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
    /// resolved by the authority.
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

        public SkillIntent(int instigatorCombatId, EntityKind instigatorKind, SkillData skill, WeaponData weapon,
            Vector3 origin, Vector3 direction, float maxRange, int sequence, float timestamp, StatusSpec targetStatus,
            int activationId = 0)
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

    public interface ICombatAuthority
    {
        /// <summary>Returns true when the authority accepted the intent and applied damage.</summary>
        bool SubmitAttack(AttackIntent intent, HitCandidate hit);

        /// <summary>
        /// Returns true when the authority accepted the skill hit and applied its damage (and, when the
        /// intent carries one, its status). Skills are validated on their own cooldown clock so a skill
        /// cast cannot be rejected for breaking a weapon's cooldown, but they share the anti-replay
        /// sequence with everything else this entity does.
        /// </summary>
        bool SubmitSkill(SkillIntent intent, HitCandidate hit);

        /// <summary>
        /// Applies a status to an entity the authority can verify (self, or a hostile entity). Returns
        /// false when the target is unknown, dead, or not a valid recipient. This is the only route a
        /// status has into an entity, which keeps self-buffs server-checkable too.
        /// </summary>
        bool ApplyStatus(int targetCombatId, int sourceCombatId, StatusSpec spec);
    }

    /// <summary>
    /// The single hostility rule. Teams exist now so that friendly fire, party play and a future
    /// server-side check all read one rule instead of inventing their own.
    /// </summary>
    public static class CombatTeams
    {
        public const int PlayerTeam = 0;
        public const int MonsterTeam = 1;

        public static bool AreHostile(ICombatEntity instigator, ICombatEntity target)
        {
            if (instigator == null || target == null) return false;
            if (instigator.CombatId == target.CombatId) return false;
            return instigator.TeamId != target.TeamId;
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
        private static int _nextActivationId = 1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatic()
        {
            _entities.Clear();
            _ordered.Clear();
            _lastSequence.Clear();
            _nextActivationId = 1;
            _nextId = 1;
        }

        public static int AllocateId() => _nextId++;

        /// <summary>
        /// Next skill activation id. One key press is one activation even when its effect resolves as
        /// several intents - a three arrow volley, or one arrow that pierces two bodies. The authority uses
        /// it to tell "the rest of this cast" apart from "the player pressed the button again", which is the
        /// same distinction a server would have to make.
        /// </summary>
        public static int NextActivationId() => _nextActivationId++;

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
            if (entity != null) _ordered.Remove(entity);
        }

        public static bool TryGet(int id, out ICombatEntity entity) => _entities.TryGetValue(id, out entity);

        public static IReadOnlyList<ICombatEntity> All => _ordered;

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
