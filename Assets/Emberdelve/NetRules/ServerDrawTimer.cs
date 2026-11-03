using System;

namespace Emberdelve.Net.Rules
{
    /// <summary>What a draw release meant to the server.</summary>
    public enum DrawReleaseOutcome
    {
        /// <summary>No draw was running (a release without a start, or after a cancel).</summary>
        NotDrawing = 0,
        /// <summary>The charge reached less than the weapon's minimum: the shot is not fired.</summary>
        TooEarly = 1,
        /// <summary>The charge reached the minimum: fire with <see cref="DrawReleaseResult.Charge"/>.</summary>
        Fire = 2
    }

    public readonly struct DrawReleaseResult
    {
        public readonly DrawReleaseOutcome Outcome;
        /// <summary>0..1, measured by the server clock (never sent by the client).</summary>
        public readonly float Charge;
        public readonly float HeldSeconds;

        public DrawReleaseResult(DrawReleaseOutcome outcome, float charge, float heldSeconds)
        {
            Outcome = outcome;
            Charge = charge;
            HeldSeconds = heldSeconds;
        }
    }

    /// <summary>
    /// U3B3: the SERVER-side record of one ranged draw. The server stamps the begin on its own clock (<c>Time.time</c> of the server
    /// process); the release request carries no charge, so the charge is exactly (release time - begin time) / draw duration, clamped to 0..1,
    /// where the duration is what the server resolved for this wielder (weapon draw time and action speed). A client can therefore
    /// neither claim a full charge nor shorten the draw; the network delay between press and release only shifts the measured hold.
    /// Pure and deterministic (the caller supplies the clock); no Unity dependency.
    /// </summary>
    public sealed class ServerDrawTimer
    {
        private double _startedAt;
        private float _duration;
        private float _minCharge;

        public bool IsDrawing { get; private set; }
        public float Duration => _duration;
        public float MinCharge => _minCharge;
        public int Begun { get; private set; }
        public int Fired { get; private set; }
        public int TooEarly { get; private set; }

        /// <summary>Starts a draw. False (nothing changed) when one is running or an argument is not a usable number.</summary>
        public bool Begin(double now, float duration, float minChargeToFire)
        {
            if (IsDrawing) return false;
            if (double.IsNaN(now) || double.IsInfinity(now) || !(duration > 0f) || float.IsInfinity(duration)) return false;

            IsDrawing = true;
            _startedAt = now;
            _duration = duration;
            _minCharge = float.IsNaN(minChargeToFire) ? 0f : Math.Max(0f, Math.Min(1f, minChargeToFire));
            Begun++;
            return true;
        }

        /// <summary>Charge (0..1) the draw has at <paramref name="now"/>; 0 when none is running or the clock went backwards.</summary>
        public float ChargeAt(double now)
        {
            if (!IsDrawing || double.IsNaN(now)) return 0f;
            double held = now - _startedAt;
            if (held <= 0.0) return 0f;
            double charge = held / _duration;
            return charge >= 1.0 ? 1f : (float)charge;
        }

        /// <summary>Ends the draw and says what the release means. Always leaves the timer idle.</summary>
        public DrawReleaseResult Release(double now)
        {
            if (!IsDrawing) return new DrawReleaseResult(DrawReleaseOutcome.NotDrawing, 0f, 0f);

            float charge = ChargeAt(now);
            float held = (float)Math.Max(0.0, now - _startedAt);
            IsDrawing = false;
            if (charge < _minCharge)
            {
                TooEarly++;
                return new DrawReleaseResult(DrawReleaseOutcome.TooEarly, charge, held);
            }

            Fired++;
            return new DrawReleaseResult(DrawReleaseOutcome.Fire, charge, held);
        }

        public void Cancel()
        {
            IsDrawing = false;
        }

        /// <summary>True when a draw has been held longer than its duration plus the overhold limit (the owner is gone or never releases).</summary>
        public bool IsStale(double now)
        {
            return IsDrawing && !double.IsNaN(now) && now - _startedAt > _duration + CombatNetLimits.MaxDrawOverholdSeconds;
        }
    }
}
