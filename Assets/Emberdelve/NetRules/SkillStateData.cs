using System;

namespace Emberdelve.Net.Rules
{
    /// <summary>
    /// U3B2: the server's skill picture of one player as the owner needs to see it: the mana pool and, per active slot, the
    /// cooldown left and its full length. Plain data, filled by the server from its own kit and replicated to the owner only.
    /// </summary>
    public readonly struct SkillStateData : IEquatable<SkillStateData>
    {
        public readonly float Mana;
        public readonly float MaxMana;
        public readonly float Cooldown0;
        public readonly float Cooldown1;
        public readonly float Duration0;
        public readonly float Duration1;

        public SkillStateData(float mana, float maxMana, float cooldown0, float cooldown1, float duration0, float duration1)
        {
            Mana = Sane(mana);
            MaxMana = Sane(maxMana);
            Cooldown0 = Sane(cooldown0);
            Cooldown1 = Sane(cooldown1);
            Duration0 = Sane(duration0);
            Duration1 = Sane(duration1);
        }

        public float Cooldown(int slot) { return slot == 0 ? Cooldown0 : slot == 1 ? Cooldown1 : 0f; }
        public float Duration(int slot) { return slot == 0 ? Duration0 : slot == 1 ? Duration1 : 0f; }

        /// <summary>Tenth-of-a-unit rounding used before publishing, so the replicated value only changes when something visible changed.</summary>
        public SkillStateData Quantized()
        {
            return new SkillStateData(Q(Mana), Q(MaxMana), Q(Cooldown0), Q(Cooldown1), Q(Duration0), Q(Duration1));
        }

        public bool Equals(SkillStateData other)
        {
            return Mana == other.Mana && MaxMana == other.MaxMana && Cooldown0 == other.Cooldown0 &&
                   Cooldown1 == other.Cooldown1 && Duration0 == other.Duration0 && Duration1 == other.Duration1;
        }

        public override bool Equals(object obj) { return obj is SkillStateData other && Equals(other); }

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Mana.GetHashCode();
                h = h * 31 + MaxMana.GetHashCode();
                h = h * 31 + Cooldown0.GetHashCode();
                h = h * 31 + Cooldown1.GetHashCode();
                h = h * 31 + Duration0.GetHashCode();
                h = h * 31 + Duration1.GetHashCode();
                return h;
            }
        }

        private static float Q(float value) { return (float)Math.Round(value * 10f) / 10f; }

        private static float Sane(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) || value < 0f ? 0f : value;
        }
    }

    /// <summary>
    /// U3B2: what a client shows for its own skills. It stores ONLY what the server replicated (state variable and cast results);
    /// the mana number is never simulated or regenerated here. The one derived value is the cooldown countdown between two
    /// replicated updates, which is a display extrapolation from the server's last remaining time, re-anchored by every update.
    /// </summary>
    public sealed class SkillViewModel
    {
        private SkillStateData _state;
        private double _receivedAt;

        public bool HasState { get; private set; }
        public int Updates { get; private set; }
        public SkillStateData State => _state;

        public void Apply(in SkillStateData state, double now)
        {
            _state = state;
            _receivedAt = now;
            HasState = true;
            Updates++;
        }

        /// <summary>The server's mana, exactly as last replicated.</summary>
        public float Mana => _state.Mana;
        public float MaxMana => _state.MaxMana;

        public float CooldownRemaining(int slot, double now)
        {
            if (!HasState) return 0f;
            double elapsed = Math.Max(0.0, now - _receivedAt);
            return (float)Math.Max(0.0, _state.Cooldown(slot) - elapsed);
        }

        public float CooldownFraction(int slot, double now)
        {
            float duration = _state.Duration(slot);
            if (duration <= 0f) return 0f;
            float fraction = CooldownRemaining(slot, now) / duration;
            return fraction < 0f ? 0f : fraction > 1f ? 1f : fraction;
        }
    }
}
