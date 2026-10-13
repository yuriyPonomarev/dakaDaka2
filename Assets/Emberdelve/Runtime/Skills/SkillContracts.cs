using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Player;

namespace Emberdelve.Skills
{
    /// <summary>
    /// Everything a skill behavior is allowed to touch, assembled by SkillCaster. A behavior never
    /// finds its own references and never mutates health: it reads the rig, asks the combat authority,
    /// and returns whether the cast physically happened.
    /// </summary>
    public sealed class SkillContext
    {
        public PlayerRig Rig;
        public ClassLoadout Loadout;
        public MeleeCombat Melee;
        public RangedCombat Ranged;
        public ManaComponent Mana;
        public GameConfig Config;
        public ICombatAuthority Authority;
        public SkillData Skill;
        public CharacterStatStack Stats;

        /// <summary>
        /// Id of this activation, shared by every intent one button press produces. Set by SkillCaster.
        /// </summary>
        public int ActivationId;

        /// <summary>Reusable scratch buffer so a cast allocates nothing per shot.</summary>
        public readonly List<HitCandidate> Hits = new List<HitCandidate>(8);
        public readonly List<int> AlreadyHit = new List<int>(8);

        public WeaponData ActiveWeapon => Loadout != null ? Loadout.ActiveWeapon : null;
        public bool HasRangedInHand => ActiveWeapon != null && ActiveWeapon.IsRanged;
        public bool HasMeleeInHand => ActiveWeapon != null && !ActiveWeapon.IsRanged;

        public LayerMask HitMask => Config != null ? Config.hitMask : LayerMask.GetMask("Default");
        public LayerMask LevelMask => Config != null ? Config.levelMask : Physics.DefaultRaycastLayers;

        public void ResetScratch()
        {
            Hits.Clear();
            AlreadyHit.Clear();
        }
    }

    /// <summary>
    /// One skill effect. <see cref="CanCast"/> answers the rules question (and why a cast is refused);
    /// <see cref="Cast"/> performs the effect and returns false only when the effect itself could not
    /// happen. Mana and cooldown bookkeeping is SkillCaster's, never a behavior's.
    /// </summary>
    public interface ISkillBehavior
    {
        bool CanCast(SkillContext context, out string reason);
        bool Cast(SkillContext context);
    }

    /// <summary>
    /// The only place that maps a skill asset to code. Adding a skill that reuses an existing effect is
    /// a new asset; adding a genuinely new effect is one small class plus one line here. Nothing in the
    /// runtime branches on a class id, a skill id or a display name.
    /// </summary>
    public static class SkillBehaviorCatalog
    {
        private static readonly Dictionary<SkillEffectKind, ISkillBehavior> _behaviors =
            new Dictionary<SkillEffectKind, ISkillBehavior>
            {
                { SkillEffectKind.Dash, new DashBehavior() },
                { SkillEffectKind.AreaCry, new BattleCryBehavior() },
                { SkillEffectKind.SpreadShot, new MultiShotBehavior() },
                { SkillEffectKind.PiercingShot, new PiercingShotBehavior() },
            };

        public static ISkillBehavior Resolve(SkillEffectKind kind)
        {
            _behaviors.TryGetValue(kind, out ISkillBehavior behavior);
            return behavior;
        }

        public static bool Supports(SkillData skill)
        {
            if (skill == null) return false;
            if (skill.IsPassive) return true;
            return Resolve(skill.effect) != null;
        }
    }
}
