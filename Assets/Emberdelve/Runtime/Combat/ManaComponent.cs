using System;
using UnityEngine;

namespace Emberdelve.Combat
{
    /// <summary>
    /// Mana pool for any combat entity, shaped exactly like HealthComponent: it only moves a number,
    /// it never decides whether an action is affordable. SkillCaster asks <see cref="HasAmount"/> and
    /// then spends through <see cref="TrySpend"/>, and a future authoritative server can run the same
    /// pool from replicated state.
    ///
    /// Regen stops while <see cref="SetRegenerationActive"/> is false, which is how death and any
    /// future "no magic while dead" rule is expressed without special cases elsewhere.
    /// </summary>
    public sealed class ManaComponent : MonoBehaviour
    {
        [SerializeField] private int maxMana = 100;
        [SerializeField] private float regenPerSecond = 6f;
        [SerializeField] private float regenDelay = 3f;

        private float _current;
        private float _timeSinceSpend;
        private bool _regenAllowed = true;
        private bool _configured;

        /// <summary>Raised whenever the pool changes, so the HUD never reads this component.</summary>
        public event Action<float, float> Changed;

        public int MaxMana => maxMana;
        public float CurrentMana => _current;
        public float RegenPerSecond => regenPerSecond;
        public bool IsFull => _current >= maxMana - 0.001f;

        /// <summary>False when something outside the pool - death, a future menu state - has suspended regeneration.</summary>
        public bool RegenerationAllowed => _regenAllowed;
        public bool IsRegenerating => _configured && _regenAllowed && regenPerSecond > 0f
                                       && _timeSinceSpend >= regenDelay && !IsFull;

        /// <summary>
        /// Seconds left before regeneration resumes after a spend. Reports the real countdown while the pool
        /// is configured, not full and regeneration is allowed; 0 only when it is running now or cannot run.
        /// </summary>
        public float RegenDelayRemaining
        {
            get
            {
                if (!_configured || !_regenAllowed || regenPerSecond <= 0f || IsFull) return 0f;
                return Mathf.Max(0f, regenDelay - _timeSinceSpend);
            }
        }

        public static ManaComponent Ensure(GameObject target)
        {
            if (target == null) return null;
            ManaComponent existing = target.GetComponent<ManaComponent>();
            return existing != null ? existing : target.AddComponent<ManaComponent>();
        }

        private void Awake()
        {
            if (!_configured)
            {
                _current = maxMana;
                _timeSinceSpend = regenDelay;
            }
        }

        /// <summary>Applies a class kit. Always refills, which is also the reset used by a new run.</summary>
        public void Configure(int max, float regenRate, float delay)
        {
            maxMana = Mathf.Max(1, max);
            regenPerSecond = Mathf.Max(0f, regenRate);
            regenDelay = Mathf.Max(0f, delay);
            _current = maxMana;
            _timeSinceSpend = regenDelay;
            _regenAllowed = true;
            _configured = true;
            Changed?.Invoke(_current, maxMana);
        }

        public bool HasAmount(int amount) => _current + 0.0001f >= Mathf.Max(0, amount);

        /// <summary>
        /// Changes the ceiling mid-run the way equipment does: a smaller ceiling clamps the current value
        /// down, a bigger ceiling raises the ceiling only and never pays out the difference. Spending mana
        /// is the only thing that lowers the pool, and a gear change never refills it.
        /// </summary>
        public void SetMax(int newMax)
        {
            int clamped = Mathf.Max(1, newMax);
            if (clamped == maxMana) return;

            maxMana = clamped;
            if (_current > maxMana) _current = maxMana;
            Changed?.Invoke(_current, maxMana);
        }

        /// <summary>Regeneration rate changed by something worn rather than by the class asset.</summary>
        public void SetRegen(float rate, float delay)
        {
            regenPerSecond = Mathf.Max(0f, rate);
            regenDelay = Mathf.Max(0f, delay);
        }

        public bool TrySpend(int amount)
        {
            int cost = Mathf.Max(0, amount);
            if (cost == 0) return true;
            if (!HasAmount(cost)) return false;

            _current = Mathf.Max(0f, _current - cost);
            _timeSinceSpend = 0f;
            Changed?.Invoke(_current, maxMana);
            return true;
        }

        public void Restore(float amount)
        {
            if (amount <= 0f) return;
            float previous = _current;
            _current = Mathf.Min(maxMana, _current + amount);
            if (!Mathf.Approximately(previous, _current)) Changed?.Invoke(_current, maxMana);
        }

        public void RefillToMax()
        {
            if (IsFull) return;
            _current = maxMana;
            Changed?.Invoke(_current, maxMana);
        }

        /// <summary>Turns regeneration on or off, e.g. while the owner is dead.</summary>
        public void SetRegenerationActive(bool active)
        {
            if (_regenAllowed == active) return;
            _regenAllowed = active;
            if (active) _timeSinceSpend = regenDelay;
        }

        private void Update()
        {
            if (!_configured || !_regenAllowed || regenPerSecond <= 0f) return;
            if (IsFull) return;

            _timeSinceSpend += Time.deltaTime;
            if (_timeSinceSpend < regenDelay) return;

            _current = Mathf.Min(maxMana, _current + regenPerSecond * Time.deltaTime);
            Changed?.Invoke(_current, maxMana);
        }
    }
}
