using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Stats;

namespace Emberdelve.Combat
{
    /// <summary>
    /// R1c2: the authority owns each entity's combat kit state (kit epoch, mana, ammunition, reload, active kit, open cast)
    /// in a <see cref="CombatKitState"/> record keyed by combat id. The rig seeds it at well-defined points
    /// (<see cref="SeedKit"/>), the authority ticks regeneration and reload on its own clock, every cast/shot is validated and
    /// spent against the record and never against rig components, and the rig only DISPLAYS what the authority pushes back
    /// (<see cref="IKitView"/>). An entity that never seeds (a fixture that only implements the R1b interfaces) has no record
    /// and keeps the R1b behavior of being read through <see cref="ISkillCasterState"/> / <see cref="IAmmoSource"/>.
    /// </summary>
    public sealed partial class LocalCombatAuthority
    {
        private readonly Dictionary<int, CombatKitState> _kits = new Dictionary<int, CombatKitState>();
        private readonly List<int> _kitScratch = new List<int>();
        private float _kitClockLast = float.NaN;

        /// <summary>Longest slice of authority time one frame may advance regen/reload by (a hitch or a restored clock cannot refill a pool).</summary>
        public const float MaxKitTickDelta = 0.5f;

        /// <summary>Entities that currently have an authority kit record.</summary>
        public int KitCount => _kits.Count;

        // ---- seeding / reading -----------------------------------------------------------------------------

        /// <summary>
        /// The ONE seeding API. The rig calls it at class apply, death, respawn, equipment revision and weapon change; the sender
        /// must own the entity. What a seed may change depends on its reason (see <see cref="KitSeedReason"/>): only ClassApply
        /// (a run start) refills; Revision is clamp-only; Weapon keeps every pool the authority already holds.
        /// </summary>
        public bool SeedKit(CombatSenderId sender, int combatId, in KitSeed seed, out string reason)
        {
            if (!AuthorizeSender(sender, combatId, "kit seed", true, out reason)) return false;
            if (!CombatRegistry.TryGet(combatId, out ICombatEntity entity))
            {
                reason = "unknown instigator id";
                LastSkillRejectReason = reason;
                return false;
            }

            bool isNew = !_kits.TryGetValue(combatId, out CombatKitState kit);
            if (isNew)
            {
                kit = new CombatKitState { CombatId = combatId };
                _kits[combatId] = kit;
            }

            kit.ApplySeed(in seed, isNew);
            if (isNew || seed.Reason == KitSeedReason.ClassApply || seed.Reason == KitSeedReason.Respawn || seed.Reason == KitSeedReason.Death)
            {
                _skillStamps.Remove(combatId);
            }

            PushKit(kit, entity);
            reason = string.Empty;
            return true;
        }

        public bool TryGetKit(int combatId, out KitView view)
        {
            if (_kits.TryGetValue(combatId, out CombatKitState kit))
            {
                view = kit.ToView();
                return true;
            }

            view = default;
            return false;
        }

        /// <summary>Seconds until the authority would accept this skill again from this entity (0 when ready, stale-epoch or unknown). False without a kit record.</summary>
        public bool TryGetSkillCooldown(int combatId, string skillId, out float remaining)
        {
            remaining = 0f;
            if (!_kits.TryGetValue(combatId, out CombatKitState kit) || string.IsNullOrEmpty(skillId)) return false;

            SkillData skill = null;
            for (int i = 0; i < kit.Skills.Length; i++)
            {
                if (kit.Skills[i] != null && kit.Skills[i].skillId == skillId) { skill = kit.Skills[i]; break; }
            }
            if (skill == null) return false;

            if (_skillStamps.TryGetValue(combatId, out Dictionary<string, SkillStamp> stamps)
                && stamps.TryGetValue(skillId, out SkillStamp stamp) && stamp.Epoch == kit.Epoch)
            {
                remaining = Mathf.Max(0f, stamp.Time + skill.EffectiveCooldown - Now);
            }
            return true;
        }

        // ---- rig commands against the record -----------------------------------------------------------------

        /// <summary>Asks the authority to start reloading the weapon in hand. The authority runs the timer and moves the rounds.</summary>
        public bool TryStartReload(CombatSenderId sender, int combatId, out string reason)
        {
            if (!AuthorizeSender(sender, combatId, "reload", false, out reason)) return false;
            if (!CombatRegistry.TryGet(combatId, out ICombatEntity entity) || entity.IsDead) { reason = "instigator is dead or unknown"; return false; }
            if (!_kits.TryGetValue(combatId, out CombatKitState kit)) { reason = "no kit record"; return false; }
            float rate = entity is IActionSpeedSource source ? source.ActionSpeedFactor : 1f;
            float duration = ActionSpeedRules.ReloadDuration(kit.Active != null ? kit.Active.ReloadTime : 0f, rate);
            if (!kit.TryStartReload(duration)) { reason = "nothing to reload"; return false; }

            PushKit(kit, entity);
            reason = string.Empty;
            return true;
        }

        /// <summary>The rig reports a shot the authority refused: the fired round is still lost (the pre-R1c2 client behavior), now on the record.</summary>
        public bool TryForfeitRound(CombatSenderId sender, int combatId)
        {
            if (!AuthorizeSender(sender, combatId, "forfeit round", false, out _)) return false;
            if (!_kits.TryGetValue(combatId, out CombatKitState kit)) return false;
            kit.ForfeitRound();
            if (CombatRegistry.TryGet(combatId, out ICombatEntity entity)) PushKit(kit, entity);
            return true;
        }

        // ---- time ----------------------------------------------------------------------------------------------

        private void TickKits()
        {
            float now = Now;
            float dt = float.IsNaN(_kitClockLast) ? 0f : Mathf.Clamp(now - _kitClockLast, 0f, MaxKitTickDelta);
            _kitClockLast = now;
            AdvanceKits(dt);
        }

        /// <summary>
        /// Test/replay seam: advances every kit record's regeneration and reload to authority time <paramref name="time"/> exactly
        /// (no per-frame clamp), then pushes the views. Pair it with <see cref="SetClock"/>.
        /// </summary>
        internal void AdvanceKitsTo(float time)
        {
            float dt = float.IsNaN(_kitClockLast) ? 0f : Mathf.Max(0f, time - _kitClockLast);
            _kitClockLast = time;
            AdvanceKits(dt);
        }

        private void AdvanceKits(float dt)
        {
            ExpireActivations();
            if (_kits.Count == 0) return;

            _kitScratch.Clear();
            foreach (KeyValuePair<int, CombatKitState> pair in _kits) _kitScratch.Add(pair.Key);

            for (int i = 0; i < _kitScratch.Count; i++)
            {
                int id = _kitScratch[i];
                CombatKitState kit = _kits[id];
                if (!CombatRegistry.TryGet(id, out ICombatEntity entity))
                {
                    _kits.Remove(id);
                    _skillStamps.Remove(id);
                    continue;
                }

                kit.Tick(dt);
                // Pushed every tick, changed or not: a rig whose displayed numbers were edited is put back.
                PushKit(kit, entity);
            }
        }

        private static void PushKit(CombatKitState kit, ICombatEntity entity)
        {
            if (entity is IKitView display)
            {
                KitView view = kit.ToView();
                display.ApplyKitView(in view);
            }
        }

        // ---- trusted-host operations (internal: Runtime code and the Editor assembly only) ---------------------------

        /// <summary>Inventory-backed ammunition grant (pickup, consumable). Not sender-bound: only trusted Runtime code can reach it.</summary>
        internal bool HostGrantReserve(int combatId, int amount)
        {
            if (!_kits.TryGetValue(combatId, out CombatKitState kit)) return false;
            kit.GrantReserve(amount);
            if (CombatRegistry.TryGet(combatId, out ICombatEntity entity)) PushKit(kit, entity);
            return true;
        }

        internal bool HostCanAcceptReserve(int combatId, int amount) =>
            _kits.TryGetValue(combatId, out CombatKitState kit) && kit.CanAcceptReserve(amount);

        /// <summary>Fixture operations: set a record value directly. False when the entity has no record.</summary>
        internal bool HostEditKit(int combatId, System.Action<KitEdit> edit)
        {
            if (!_kits.TryGetValue(combatId, out CombatKitState kit)) return false;
            edit(new KitEdit(kit));
            if (CombatRegistry.TryGet(combatId, out ICombatEntity entity)) PushKit(kit, entity);
            return true;
        }

        /// <summary>Handle given to <see cref="HostEditKit"/>: the few record edits fixtures need.</summary>
        internal readonly struct KitEdit
        {
            private readonly CombatKitState _k;
            internal KitEdit(CombatKitState kit) { _k = kit; }

            public void RefillMana() => _k.Mana = _k.MaxMana;
            public void RestoreMana(float amount) => _k.RestoreMana(amount);
            public void SetMana(float value) => _k.Mana = Mathf.Clamp(value, 0f, _k.MaxMana);
            public bool SpendMana(int amount) => _k.TrySpendMana(amount);
            public void RefundMagazine(int count) => _k.RefundMagazine(count);
            public bool ConsumeAmmo(int count) => _k.TryConsumeAmmo(count);

            public void SetActivePool(int magazine, int reserve, int maxReserve)
            {
                CombatKitState.AmmoPool pool = _k.Active;
                if (pool == null) return;
                pool.MaxReserve = Mathf.Max(0, maxReserve);
                pool.Magazine = magazine < 0 ? pool.MagazineSize : Mathf.Clamp(magazine, 0, pool.MagazineSize);
                pool.Reserve = Mathf.Clamp(reserve, 0, pool.MaxReserve);
                _k.Reloading = false;
            }
        }

        // ---- kit access used by the validation paths ------------------------------------------------------------

        private void EnsureKit(int combatId, ICombatEntity entity)
        {
            if (_kits.ContainsKey(combatId) || !(entity is IKitSeedSource source)) return;

            // First contact with an entity that was set up before any authority existed: adopt what it has, once.
            KitSeed seed = source.CaptureKitSeed(KitSeedReason.Weapon);
            CombatKitState kit = new CombatKitState { CombatId = combatId };
            _kits[combatId] = kit;
            kit.ApplySeed(in seed, true);
            PushKit(kit, entity);
        }

        private KitAccess KitFor(int combatId, ICombatEntity entity, ISkillCasterState state, IAmmoSource ammo)
        {
            EnsureKit(combatId, entity);
            _kits.TryGetValue(combatId, out CombatKitState kit);
            return new KitAccess(kit, entity, state, ammo);
        }

        /// <summary>
        /// Mana, ammunition, kit and epoch for one validation, from the authority's record when the entity has one, and from the
        /// R1b interfaces otherwise. The command paths ask this and never read the rig components themselves.
        /// </summary>
        private readonly struct KitAccess
        {
            private readonly CombatKitState _k;
            private readonly ICombatEntity _e;
            private readonly ISkillCasterState _s;
            private readonly IAmmoSource _am;

            public KitAccess(CombatKitState kit, ICombatEntity entity, ISkillCasterState state, IAmmoSource ammo)
            {
                _k = kit; _e = entity; _s = state; _am = ammo;
            }

            public IReadOnlyList<SkillData> Skills => _k != null ? (IReadOnlyList<SkillData>)_k.Skills : (_s != null ? _s.ActiveSkills : null);
            public int Epoch => _k != null ? _k.Epoch : (_s != null ? _s.KitEpoch : 0);
            public bool HasManaPool => _k != null || (_s != null && _s.Mana != null);
            public bool HasMana(int amount) => _k != null ? _k.HasMana(amount) : _s.Mana.HasAmount(amount);

            public bool TrySpendMana(int amount)
            {
                if (_k == null) return _s.Mana.TrySpend(amount);
                bool ok = _k.TrySpendMana(amount);
                if (ok) PushKit(_k, _e);
                return ok;
            }

            public void RestoreMana(float amount)
            {
                if (_k == null) { _s.Mana.Restore(amount); return; }
                _k.RestoreMana(amount);
                PushKit(_k, _e);
            }

            public bool HasAmmoModel => _k != null || _am != null;
            public bool HasAmmo(int count) => _k != null ? _k.HasAmmo(count) : _am != null && _am.HasAmmo(count);

            public bool TryConsumeAmmo(int count)
            {
                if (_k == null) return _am != null && _am.TryConsumeAmmo(count);
                bool ok = _k.TryConsumeAmmo(count);
                if (ok) PushKit(_k, _e);
                return ok;
            }

            public void NoteCast(int activationId)
            {
                if (_k != null) _k.ActiveCastId = activationId;
            }
        }

        private void NoteCastCompleted(int casterCombatId, int activationId)
        {
            if (_kits.TryGetValue(casterCombatId, out CombatKitState kit) && kit.ActiveCastId == activationId) kit.ActiveCastId = 0;
        }
    }
}
