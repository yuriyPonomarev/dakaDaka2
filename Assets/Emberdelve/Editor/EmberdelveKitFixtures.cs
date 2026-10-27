using System;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Player;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// R1c2: fixtures used to edit a rig's mana and ammunition. Once the combat authority holds a kit record for a rig, the
    /// rig's ManaComponent/RangedCombat are only a display of it and a direct edit is (correctly) overwritten on the next
    /// push, so a test that needs "this rig has no mana / a full magazine" edits the authority's record through the trusted
    /// host seam instead. A rig the authority has no record for (no authority in the scene, or a bare fixture) is edited
    /// directly, exactly as before.
    /// </summary>
    internal static class EmberdelveKitFixtures
    {
        // ---- R1c3: sender-less commands are refused for player-owned ids ------------------------------------------------
        // A fixture that drives the authority "as" an entity sends the command with the sender that owns it (the player's
        // session sender when RunDirector.TryJoin bound one, the trusted host for an unowned id: enemies, bare fixtures).
        // That is exactly what the production callers do with rig.CombatSender, so the fixture exercises the real path.

        public static CombatSenderId SenderFor(int combatId) =>
            CombatSenders.TryGetOwner(combatId, out CombatSenderId owner) ? owner : CombatSenderId.TrustedHost;

        public static CombatSenderId SenderFor(PlayerRig rig) => rig != null ? rig.CombatSender : CombatSenderId.TrustedHost;

        public static bool SubmitAttackAsOwner(this ICombatAuthority a, AttackIntent intent, HitCandidate hit) =>
            a.SubmitAttack(SenderFor(intent.InstigatorCombatId), intent, hit, out _);

        public static bool SubmitAttackAsOwner(this ICombatAuthority a, AttackIntent intent, HitCandidate hit, out string reason) =>
            a.SubmitAttack(SenderFor(intent.InstigatorCombatId), intent, hit, out reason);

        public static bool TryBeginMeleeSwingAsOwner(this ICombatAuthority a, in MeleeSwingIntent intent, out int swingId, out string reason) =>
            a.TryBeginMeleeSwing(SenderFor(intent.InstigatorCombatId), in intent, out swingId, out reason);

        public static bool LaunchProjectileAsOwner(this ICombatAuthority a, AttackIntent intent) =>
            a.LaunchProjectile(SenderFor(intent.InstigatorCombatId), intent, out _);

        public static bool TryBeginSkillCastAsOwner(this ICombatAuthority a, int casterCombatId, string skillId, out int activationId, out string reason) =>
            a.TryBeginSkillCast(SenderFor(casterCombatId), casterCombatId, skillId, out activationId, out reason);

        public static bool CompleteSkillCastAsOwner(this ICombatAuthority a, int casterCombatId, int activationId) =>
            a.CompleteSkillCast(SenderFor(casterCombatId), casterCombatId, activationId);

        public static bool SubmitSkillAsOwner(this ICombatAuthority a, SkillIntent intent, HitCandidate hit) =>
            a.SubmitSkill(SenderFor(intent.InstigatorCombatId), intent, hit, out _);

        public static bool SubmitSkillAsOwner(this ICombatAuthority a, SkillIntent intent, HitCandidate hit, out string reason) =>
            a.SubmitSkill(SenderFor(intent.InstigatorCombatId), intent, hit, out reason);

        public static bool LaunchSkillProjectileAsOwner(this ICombatAuthority a, SkillIntent intent, int projectileIndex) =>
            a.LaunchSkillProjectile(SenderFor(intent.InstigatorCombatId), intent, projectileIndex, out _);

        private static LocalCombatAuthority Authority =>
            GameServices.TryGet<ICombatAuthority>(out ICombatAuthority a) ? a as LocalCombatAuthority : null;

        private static bool Edit(PlayerRig rig, Action<LocalCombatAuthority.KitEdit> edit)
        {
            LocalCombatAuthority authority = Authority;
            return authority != null && rig != null && authority.HostEditKit(rig.CombatId, edit);
        }

        public static void RefillMana(PlayerRig rig)
        {
            if (!Edit(rig, e => e.RefillMana()) && rig != null && rig.Mana != null) rig.Mana.RefillToMax();
        }

        public static void RefillMana(ManaComponent mana) => RefillMana(mana != null ? mana.GetComponent<PlayerRig>() : null);

        public static bool SpendMana(ManaComponent mana, int amount)
        {
            bool ok = false;
            PlayerRig rig = mana != null ? mana.GetComponent<PlayerRig>() : null;
            if (!Edit(rig, e => ok = e.SpendMana(amount))) ok = mana != null && mana.TrySpend(amount);
            return ok;
        }

        public static void RestoreMana(ManaComponent mana, float amount)
        {
            PlayerRig rig = mana != null ? mana.GetComponent<PlayerRig>() : null;
            if (!Edit(rig, e => e.RestoreMana(amount)) && mana != null) mana.Restore(amount);
        }

        public static void RefundAmmo(RangedCombat ranged, int count)
        {
            PlayerRig rig = ranged != null ? ranged.GetComponent<PlayerRig>() : null;
            if (!Edit(rig, e => e.RefundMagazine(count)) && ranged != null) ranged.RefundAmmo(count);
        }

        public static bool ConsumeAmmo(RangedCombat ranged, int count)
        {
            bool ok = false;
            PlayerRig rig = ranged != null ? ranged.GetComponent<PlayerRig>() : null;
            if (!Edit(rig, e => ok = e.ConsumeAmmo(count))) ok = ranged != null && ranged.TryConsumeAmmo(count);
            return ok;
        }

        /// <summary>The old "re-initialize the weapon with these numbers" fixture: now an explicit authority edit of the weapon in hand (magazine -1 = full).</summary>
        public static void ResetAmmo(RangedCombat ranged, WeaponData weapon, int reserve, int maxReserve, int magazine)
        {
            ranged.Initialize(weapon, reserve, maxReserve, magazine);
            PlayerRig rig = ranged.GetComponent<PlayerRig>();
            Edit(rig, e => e.SetActivePool(magazine, reserve, maxReserve));
        }
    }
}
