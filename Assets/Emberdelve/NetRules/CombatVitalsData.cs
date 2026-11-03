using System;

namespace Emberdelve.Net.Rules
{
    /// <summary>
    /// U3B3: the server's health picture of one combat body as every client may see it (player health and death are public in a
    /// PvPvE match). Plain data, filled by the server from the HealthComponent and replicated read-only. The tenth-of-a-point rounding keeps
    /// the replicated value from changing every frame while health regenerates by a hair.
    /// </summary>
    public readonly struct CombatVitalsData : IEquatable<CombatVitalsData>
    {
        public readonly float Health;
        public readonly float MaxHealth;
        public readonly bool Dead;

        public CombatVitalsData(float health, float maxHealth, bool dead)
        {
            MaxHealth = Sane(maxHealth);
            Health = Math.Min(Sane(health), MaxHealth > 0f ? MaxHealth : Sane(health));
            Dead = dead;
        }

        public CombatVitalsData Quantized()
        {
            return new CombatVitalsData(Q(Health), Q(MaxHealth), Dead);
        }

        public bool Equals(CombatVitalsData other)
        {
            return Health == other.Health && MaxHealth == other.MaxHealth && Dead == other.Dead;
        }

        public override bool Equals(object obj) { return obj is CombatVitalsData other && Equals(other); }

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Health.GetHashCode();
                h = h * 31 + MaxHealth.GetHashCode();
                return h * 31 + (Dead ? 1 : 0);
            }
        }

        private static float Q(float value) { return (float)Math.Round(value * 10f) / 10f; }

        private static float Sane(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) || value < 0f ? 0f : value;
        }
    }
}
