using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Data;

namespace Emberdelve.Combat
{
    /// <summary>Why a rig is handing the authority a kit seed (R1c2). The reason decides what the seed may change.</summary>
    public enum KitSeedReason
    {
        /// <summary>A class was (re)applied: new kit epoch, pools rebuilt and refilled, ammo pools rebuilt. A run start, not a gear change.</summary>
        ClassApply,
        /// <summary>The rig respawned / the kit was restored: new epoch (cooldowns die), regen resumes. Mana and ammo are carried, not refilled.</summary>
        Respawn,
        /// <summary>The rig died: new epoch, regen and reload suspended.</summary>
        Death,
        /// <summary>Worn equipment changed (the shared inventory/equipment revision): mana ceiling and regen rules only, clamp-only, never a refill.</summary>
        Revision,
        /// <summary>The weapon in hand changed: ammo pools the authority already knows are KEPT (no free refill); only pools it has never seen are adopted.</summary>
        Weapon,
    }

    /// <summary>One weapon's ammunition as the loadout reports it.</summary>
    public struct KitAmmoSeed
    {
        public string WeaponId;
        public int MagazineSize;
        public float ReloadTime;
        public int Magazine;
        public int Reserve;
        public int MaxReserve;
    }

    /// <summary>
    /// R1c2: everything a rig may tell the authority about its kit, at the well-defined points only (class apply, death,
    /// respawn, equipment revision, weapon change). Plain data; how much of it the authority believes depends on the reason
    /// (see <see cref="KitSeedReason"/>), and a seed can never raise a value the authority already holds except through
    /// ClassApply, which is a run start.
    /// </summary>
    public struct KitSeed
    {
        public KitSeedReason Reason;
        public float Mana;
        public int MaxMana;
        public float ManaRegen;
        public float ManaRegenDelay;
        public string ActiveWeaponId;
        public KitAmmoSeed[] Pools;
        public SkillData[] ActiveSkills;
    }

    /// <summary>Read-only picture of one entity's authority-owned kit record, for HUD, rig views and tests.</summary>
    public struct KitView
    {
        public int Epoch;
        public float Mana;
        public int MaxMana;
        public float ManaRegen;
        public float ManaRegenDelay;
        public float ManaSinceSpend;
        public bool ManaRegenAllowed;
        public bool Frozen;
        public string ActiveWeaponId;
        public bool HasActiveAmmo;
        public int Magazine;
        public int Reserve;
        public int MaxReserve;
        public int MagazineSize;
        public float ReloadTime;
        public bool Reloading;
        public float ReloadRemaining;
        public int ActiveCastId;
    }

    /// <summary>Implemented by the entity that can seed its kit (the player rig). The authority may pull a first seed from it.</summary>
    public interface IKitSeedSource
    {
        KitSeed CaptureKitSeed(KitSeedReason reason);
    }

    /// <summary>Implemented by the entity that displays its authority record (the player rig). The authority pushes; the entity never writes back.</summary>
    public interface IKitView
    {
        void ApplyKitView(in KitView view);
    }

    /// <summary>
    /// R1c2: the authority's own copy of one entity's combat kit: kit epoch, mana pool, ammunition pools and reload, the
    /// active kit and the open cast. Plain C# (no components), so a server owns exactly this. Time never enters except
    /// through <see cref="Tick"/>, so tests drive it on an injected clock. Cooldown stamps live next to it in the authority.
    /// </summary>
    internal sealed class CombatKitState
    {
        public sealed class AmmoPool
        {
            public string WeaponId = string.Empty;
            public int Magazine;
            public int Reserve;
            public int MaxReserve;
            public int MagazineSize = 1;
            public float ReloadTime = 1f;
        }

        public int CombatId;
        public int Epoch;
        public SkillData[] Skills = new SkillData[0];
        public int ActiveCastId;

        public float Mana;
        public int MaxMana = 1;
        public float ManaRegen;
        public float ManaRegenDelay;
        public float ManaSinceSpend;
        public bool ManaRegenAllowed = true;
        public bool Frozen;

        public readonly Dictionary<string, AmmoPool> Pools = new Dictionary<string, AmmoPool>();
        public string ActiveWeaponId = string.Empty;
        public bool Reloading;
        public float ReloadRemaining;

        public AmmoPool Active =>
            ActiveWeaponId.Length > 0 && Pools.TryGetValue(ActiveWeaponId, out AmmoPool pool) ? pool : null;

        public bool IsManaFull => Mana >= MaxMana - 0.001f;

        // ---- seeding ------------------------------------------------------------------------------------

        public void ApplySeed(in KitSeed seed, bool isNew)
        {
            if (isNew || seed.Reason == KitSeedReason.ClassApply)
            {
                ApplyClass(seed);
                return;
            }

            switch (seed.Reason)
            {
                case KitSeedReason.Respawn:
                    Epoch++;
                    Frozen = false;
                    if (!ManaRegenAllowed) ManaSinceSpend = ManaRegenDelay;
                    ManaRegenAllowed = true;
                    ActiveCastId = 0;
                    break;
                case KitSeedReason.Death:
                    Epoch++;
                    Frozen = true;
                    ManaRegenAllowed = false;
                    ActiveCastId = 0;
                    break;
                case KitSeedReason.Revision:
                    ApplyRevision(seed);
                    break;
                case KitSeedReason.Weapon:
                    ApplyWeapon(seed);
                    break;
            }
        }

        private void ApplyClass(in KitSeed seed)
        {
            Epoch++;
            Frozen = false;
            ActiveCastId = 0;
            Skills = seed.ActiveSkills != null ? (SkillData[])seed.ActiveSkills.Clone() : new SkillData[0];

            MaxMana = Mathf.Max(1, seed.MaxMana);
            ManaRegen = Mathf.Max(0f, seed.ManaRegen);
            ManaRegenDelay = Mathf.Max(0f, seed.ManaRegenDelay);
            // A class apply is a run start and refills; the first contact with a rig that was set up before this authority
            // existed adopts the pool it actually has.
            Mana = seed.Reason == KitSeedReason.ClassApply ? MaxMana : Mathf.Clamp(seed.Mana, 0f, MaxMana);
            ManaSinceSpend = ManaRegenDelay;
            ManaRegenAllowed = true;

            Pools.Clear();
            Reloading = false;
            ReloadRemaining = 0f;
            AddPools(seed, true);
            ActiveWeaponId = seed.ActiveWeaponId ?? string.Empty;
        }

        private void ApplyRevision(in KitSeed seed)
        {
            // Clamp-only, exactly ManaComponent.SetMax/SetRegen: a smaller ceiling clamps the pool down, a bigger one raises
            // the ceiling only. The regen clock is not touched. Nothing here can raise Mana.
            int clamped = Mathf.Max(1, seed.MaxMana);
            if (clamped != MaxMana)
            {
                MaxMana = clamped;
                if (Mana > MaxMana) Mana = MaxMana;
            }

            ManaRegen = Mathf.Max(0f, seed.ManaRegen);
            ManaRegenDelay = Mathf.Max(0f, seed.ManaRegenDelay);
        }

        private void ApplyWeapon(in KitSeed seed)
        {
            AddPools(seed, false);
            ActiveWeaponId = seed.ActiveWeaponId ?? string.Empty;
            // RangedCombat.Initialize always ended a draw/reload when the hand changed.
            Reloading = false;
            ReloadRemaining = 0f;
        }

        private void AddPools(in KitSeed seed, bool adoptAll)
        {
            if (seed.Pools == null) return;

            for (int i = 0; i < seed.Pools.Length; i++)
            {
                KitAmmoSeed s = seed.Pools[i];
                if (string.IsNullOrEmpty(s.WeaponId)) continue;

                int size = Mathf.Clamp(s.MagazineSize, 1, 99);
                int max = Mathf.Max(0, s.MaxReserve);

                if (!adoptAll && Pools.TryGetValue(s.WeaponId, out AmmoPool known))
                {
                    // A pool the authority already holds wins over whatever the rig now says: a weapon swap is not a refill.
                    known.MagazineSize = size;
                    known.ReloadTime = s.ReloadTime;
                    known.MaxReserve = max;
                    known.Magazine = Mathf.Min(known.Magazine, size);
                    known.Reserve = Mathf.Min(known.Reserve, max);
                    continue;
                }

                Pools[s.WeaponId] = new AmmoPool
                {
                    WeaponId = s.WeaponId,
                    MagazineSize = size,
                    ReloadTime = s.ReloadTime,
                    MaxReserve = max,
                    Magazine = Mathf.Clamp(s.Magazine, 0, size),
                    Reserve = Mathf.Clamp(s.Reserve, 0, max),
                };
            }
        }

        // ---- time ---------------------------------------------------------------------------------------

        /// <summary>Advances reload and mana regeneration by <paramref name="dt"/> seconds of authority time (same arithmetic the rig components used).</summary>
        public void Tick(float dt)
        {
            if (dt <= 0f) return;

            if (Reloading && !Frozen)
            {
                ReloadRemaining -= dt;
                if (ReloadRemaining <= 0f) FinishReload();
            }

            if (ManaRegenAllowed && ManaRegen > 0f && !IsManaFull)
            {
                ManaSinceSpend += dt;
                if (ManaSinceSpend >= ManaRegenDelay) Mana = Mathf.Min(MaxMana, Mana + ManaRegen * dt);
            }
        }

        private void FinishReload()
        {
            Reloading = false;
            ReloadRemaining = 0f;
            AmmoPool pool = Active;
            if (pool == null) return;

            int need = pool.MagazineSize - pool.Magazine;
            int taken = Mathf.Min(need, pool.Reserve);
            pool.Magazine += taken;
            pool.Reserve -= taken;
        }

        // ---- mana ---------------------------------------------------------------------------------------

        public bool HasMana(int amount) => Mana + 0.0001f >= Mathf.Max(0, amount);

        public bool TrySpendMana(int amount)
        {
            int cost = Mathf.Max(0, amount);
            if (cost == 0) return true;
            if (!HasMana(cost)) return false;
            Mana = Mathf.Max(0f, Mana - cost);
            ManaSinceSpend = 0f;
            return true;
        }

        public void RestoreMana(float amount)
        {
            if (amount <= 0f) return;
            Mana = Mathf.Min(MaxMana, Mana + amount);
        }

        // ---- ammunition ---------------------------------------------------------------------------------

        public bool HasAmmo(int count)
        {
            AmmoPool pool = Active;
            return pool != null && pool.Magazine >= Mathf.Max(0, count);
        }

        public bool TryConsumeAmmo(int count)
        {
            int needed = Mathf.Max(0, count);
            if (needed == 0) return true;
            if (!HasAmmo(needed)) return false;
            Active.Magazine -= needed;
            return true;
        }

        public bool TryStartReload()
        {
            AmmoPool pool = Active;
            if (pool == null || Reloading || Frozen) return false;
            if (pool.Magazine >= pool.MagazineSize || pool.Reserve <= 0) return false;
            Reloading = true;
            ReloadRemaining = Mathf.Max(0.1f, pool.ReloadTime);
            return true;
        }

        public bool CanAcceptReserve(int amount)
        {
            AmmoPool pool = Active;
            return pool != null && amount > 0 && (pool.MaxReserve <= 0 || pool.Reserve < pool.MaxReserve);
        }

        public void GrantReserve(int amount)
        {
            AmmoPool pool = Active;
            if (pool == null || amount <= 0) return;
            pool.Reserve = pool.MaxReserve > 0 ? Mathf.Min(pool.Reserve + amount, pool.MaxReserve) : pool.Reserve + amount;
        }

        public void ForfeitRound()
        {
            AmmoPool pool = Active;
            if (pool != null) pool.Magazine = Mathf.Max(0, pool.Magazine - 1);
        }

        public void RefundMagazine(int count)
        {
            AmmoPool pool = Active;
            if (pool == null || count <= 0) return;
            pool.Magazine = Mathf.Min(pool.MagazineSize, pool.Magazine + count);
        }

        public KitView ToView()
        {
            AmmoPool pool = Active;
            return new KitView
            {
                Epoch = Epoch,
                Mana = Mana,
                MaxMana = MaxMana,
                ManaRegen = ManaRegen,
                ManaRegenDelay = ManaRegenDelay,
                ManaSinceSpend = ManaSinceSpend,
                ManaRegenAllowed = ManaRegenAllowed,
                Frozen = Frozen,
                ActiveWeaponId = ActiveWeaponId,
                HasActiveAmmo = pool != null,
                Magazine = pool != null ? pool.Magazine : 0,
                Reserve = pool != null ? pool.Reserve : 0,
                MaxReserve = pool != null ? pool.MaxReserve : 0,
                MagazineSize = pool != null ? pool.MagazineSize : 0,
                ReloadTime = pool != null ? pool.ReloadTime : 0f,
                Reloading = Reloading,
                ReloadRemaining = Reloading ? ReloadRemaining : 0f,
                ActiveCastId = ActiveCastId,
            };
        }
    }
}
