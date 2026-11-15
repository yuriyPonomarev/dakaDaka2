using System;

namespace Emberdelve.BladeDefense
{
    /// <summary>
    /// Authority-clock window for a timely guard raise. Rearming is project-authored anti-spam
    /// behavior, not a measured reference timing. No clock or tuning is owned by this rule.
    /// </summary>
    public sealed class ImpactGuardWindow
    {
        private bool _hasEpoch;
        private int _epoch;
        private bool _held;
        private bool _active;
        private bool _hasTime;
        private float _lastTime;
        private double _expires;
        private double _readyAt = double.NegativeInfinity;

        public void Update(bool eligible, bool held, int weaponEpoch, float now, float duration, float rearmDelay)
        {
            bool changedWeapon = _hasEpoch && _epoch != weaponEpoch;
            bool rising = held && !_held && !changedWeapon;
            _hasEpoch = true;
            _epoch = weaponEpoch;
            _held = held;
            if (changedWeapon) _active = false;

            // Consume the input edge even when the clock or configuration is invalid, so
            // repairing it cannot turn a held button into another accepted raise.
            if (!Finite(now) || !Finite(duration) || !Finite(rearmDelay) || duration <= 0f
                || (_hasTime && now < _lastTime))
            {
                _active = false;
                return;
            }
            _hasTime = true;
            _lastTime = now;

            if (!eligible || !held)
            {
                _active = false;
                return;
            }
            if (!rising || now < _readyAt) return;

            _active = true;
            // Double addition keeps finite float inputs from overflowing expiry/cooldown.
            _expires = (double)now + duration;
            _readyAt = (double)now + Math.Max(duration, rearmDelay);
        }

        public float Remaining(float now)
        {
            if (!_active || !Finite(now) || (_hasTime && now < _lastTime)) return 0f;
            return (float)Math.Max(0d, _expires - now);
        }

        public void Reset()
        {
            _hasEpoch = false;
            _epoch = 0;
            _held = false;
            _active = false;
            _hasTime = false;
            _lastTime = 0f;
            _expires = 0d;
            _readyAt = double.NegativeInfinity;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
