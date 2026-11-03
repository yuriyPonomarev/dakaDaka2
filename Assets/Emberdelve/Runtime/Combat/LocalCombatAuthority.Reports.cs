using System;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;

namespace Emberdelve.Combat
{
    /// <summary>U3B3: which resolution path produced a reported hit.</summary>
    public enum CombatHitSource
    {
        Melee = 0,
        Projectile = 1,
        SkillProjectile = 2
    }

    /// <summary>
    /// U3B3: one damage application the combat authority resolved, with the factors it was built from. Observability for the network layer
    /// (the server logs it, tells the clients about it) and for the gates (they re-derive the amount from data). Raised AFTER the target took
    /// the damage; nothing a listener does changes the hit.
    /// </summary>
    public sealed class CombatHitReport
    {
        public CombatHitSource Source;
        public int AttackerId;
        public int TargetId;
        public string WeaponId = "";
        /// <summary>The number the target's health actually lost (after location, guard, armor and falloff).</summary>
        public float Amount;
        public HitLocation Location;
        public float LocationMultiplier = 1f;
        public bool Blocked;
        public float BlockReduction;
        public bool GuardBroken;
        public bool Killed;
        /// <summary>The attacker's outgoing multiplier (attributes, passives, status) and flat weapon-base bonus as the authority read them.</summary>
        public float OutgoingMultiplier = 1f;
        public float FlatBonus;
        /// <summary>The target's physical armor rating as the authority read it (0 when it has none).</summary>
        public float ArmorRating;
        /// <summary>Melee: the combo step's damage multiplier, the impact-zone multiplier and the contact parameter along the blade.</summary>
        public float StepMultiplier = 1f;
        public float ZoneMultiplier = 1f;
        public float ContactT;
        /// <summary>Projectile: the charge the shot carried, seconds in the air at contact and the air-time falloff scale (negative = the legacy range falloff).</summary>
        public float Charge = 1f;
        public float AirTime;
        public float AirTimeScale = -1f;
        public Vector3 Point;
    }

    /// <summary>U3B3: a plain-weapon projectile that was just launched (the server tells observers so they can draw the same flight).</summary>
    public sealed class CombatLaunchReport
    {
        public int Serial;
        public int InstigatorId;
        public string WeaponId = "";
        public Vector3 Origin;
        public Vector3 Direction;
        public float Speed;
        public float Gravity;
        public float MaxFlightTime;
        public float Range;
        public float Charge;
        public float Radius;
        public float Length;
    }

    /// <summary>U3B3: a projectile flight ended (stopped by a wall, a body or a blade, or by its range or time).</summary>
    public readonly struct CombatFlightEnd
    {
        public readonly int Serial;
        public readonly int InstigatorId;
        public readonly int TargetId;
        public readonly Vector3 Position;
        public readonly float AirTime;

        public CombatFlightEnd(int serial, int instigatorId, int targetId, Vector3 position, float airTime)
        {
            Serial = serial;
            InstigatorId = instigatorId;
            TargetId = targetId;
            Position = position;
            AirTime = airTime;
        }
    }

    /// <summary>
    /// U3B3: the report events of the combat authority. They only OBSERVE what the authority already resolved: no listener can change a hit,
    /// and the offline game (no listener) pays one null check per event.
    /// </summary>
    public sealed partial class LocalCombatAuthority
    {
        /// <summary>A weapon or projectile-skill hit was applied to a target.</summary>
        public event Action<CombatHitReport> HitResolved;

        /// <summary>A plain-weapon projectile was launched and accepted.</summary>
        public event Action<CombatLaunchReport> ProjectileLaunched;

        /// <summary>A projectile flight ended.</summary>
        public event Action<CombatFlightEnd> FlightEnded;

        private void ReportHit(CombatHitSource source, ICombatEntity instigator, ICombatEntity target, WeaponData weapon, in DamageInfo damage,
            HitLocation location, float locationMultiplier, float stepMultiplier, float zoneMultiplier, float contactT,
            float charge, float airTime, float airTimeScale,
            float outgoingMultiplier = -1f, float flatBonus = -1f, float armorRating = -1f)
        {
            Action<CombatHitReport> listeners = HitResolved;
            if (listeners == null) return;

            CombatHitReport report = new CombatHitReport
            {
                Source = source,
                AttackerId = instigator != null ? instigator.CombatId : 0,
                TargetId = target != null ? target.CombatId : 0,
                WeaponId = weapon != null && weapon.weaponId != null ? weapon.weaponId : "",
                Amount = damage.Amount,
                Location = location,
                LocationMultiplier = locationMultiplier,
                Blocked = damage.Blocked,
                BlockReduction = damage.BlockReduction,
                GuardBroken = damage.GuardBroken,
                Killed = target != null && target.IsDead,
                // FIX1: a caller that already resolved the factors passes the VALUES THE RESOLVER USED, so the report cannot drift from the
                // damage (a provider is never asked a second time after the hit's side effects). Negative means "read them now" (projectile paths).
                OutgoingMultiplier = outgoingMultiplier >= 0f ? outgoingMultiplier : OutgoingMultiplier(instigator, weapon),
                FlatBonus = flatBonus >= 0f ? flatBonus : FlatWeaponBonus(instigator, weapon),
                ArmorRating = armorRating >= 0f ? armorRating
                    : target is IArmorRatingProvider armor ? DamageResolver.SanitizeArmorRating(armor.GetArmorRating()) : 0f,
                StepMultiplier = stepMultiplier,
                ZoneMultiplier = zoneMultiplier,
                ContactT = contactT,
                Charge = charge,
                AirTime = airTime,
                AirTimeScale = airTimeScale,
                Point = damage.Point
            };
            listeners(report);
        }

        private void ReportLaunch(ProjectileFlight flight)
        {
            Action<CombatLaunchReport> listeners = ProjectileLaunched;
            if (listeners == null || flight == null || flight.Weapon == null) return;

            listeners(new CombatLaunchReport
            {
                Serial = flight.Serial,
                InstigatorId = flight.InstigatorId,
                WeaponId = flight.Weapon.weaponId != null ? flight.Weapon.weaponId : "",
                Origin = flight.Origin,
                Direction = flight.Direction,
                Speed = flight.Speed,
                Gravity = flight.Gravity,
                MaxFlightTime = flight.MaxFlightTime,
                Range = flight.Range,
                Charge = flight.Attack.Charge,
                Radius = Mathf.Max(0.02f, flight.Weapon.projectileRadius),
                Length = Mathf.Max(0.2f, flight.Weapon.projectileLength)
            });
        }

        private void ReportFlightEnd(ProjectileFlight flight, int targetId)
        {
            Action<CombatFlightEnd> listeners = FlightEnded;
            if (listeners == null || flight == null || flight.SkillGroup != null) return;
            listeners(new CombatFlightEnd(flight.Serial, flight.InstigatorId, targetId, flight.Position, flight.Steps * Emberdelve.Ballistics.ProjectileBallistics.StepSeconds));
        }
    }
}
