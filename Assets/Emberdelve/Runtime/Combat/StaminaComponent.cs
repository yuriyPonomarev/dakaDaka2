using System;
using UnityEngine;

namespace Emberdelve.Combat
{
    /// <summary>
    /// Stamina pool, shaped exactly like <see cref="ManaComponent"/>: it only moves a number and never
    /// decides what an action costs. U2A3 gives it a ceiling, a regeneration rate and the same
    /// no-free-refill rule as health and mana; sprint/guard drain and its authoritative replication are
    /// U3, so nothing in this slice spends stamina.
    /// </summary>
    public sealed class StaminaComponent : MonoBehaviour
    {
        [SerializeField] private int maxStamina = 100;
        [SerializeField] private float regenPerSecond = 5f;
        [SerializeField] private float regenDelay = 0f;

        private float _current;
        private float _timeSinceSpend;
        private bool _regenAllowed = true;
        private bool _configured;

        /// <summary>Raised whenever the pool changes, so a future HUD never reads this component.</summary>
        public event Action<float, float> Changed;

        public int MaxStamina => maxStamina;
        public float CurrentStamina => _current;
        public float RegenPerSecond => regenPerSecond;
        public bool IsFull => _current >= maxStamina - 0.001f;
        public bool RegenerationAllowed => _regenAllowed;
        public bool IsRegenerating => _configured && _regenAllowed && regenPerSecond > 0f
                                       && _timeSinceSpend >= regenDelay && !IsFull;

        public static StaminaComponent Ensure(GameObject target)
        {
            if (target == null) return null;
            StaminaComponent existing = target.GetComponent<StaminaComponent>();
            return existing != null ? existing : target.AddComponent<StaminaComponent>();
        }

        private void Awake()
        {
            if (!_configured)
            {
                _current = maxStamina;
                _timeSinceSpend = regenDelay;
            }
        }

        /// <summary>Applies the class kit. Always fills once, which is also the reset a new run needs.</summary>
        public void Configure(int max, float regenRate, float delay)
        {
            maxStamina = Mathf.Max(1, max);
            regenPerSecond = Mathf.Max(0f, regenRate);
            regenDelay = Mathf.Max(0f, delay);
            _current = maxStamina;
            _timeSinceSpend = regenDelay;
            _regenAllowed = true;
            _configured = true;
            Changed?.Invoke(_current, maxStamina);
        }

        public bool HasAmount(int amount) => _current + 0.0001f >= Mathf.Max(0, amount);

        /// <summary>
        /// Changes the ceiling the way equipment does: a smaller ceiling clamps the current value down,
        /// a bigger ceiling raises the ceiling only. It never hands out the difference, and it never
        /// revives or refills a pool that was already spent.
        /// </summary>
        public void SetMax(int newMax)
        {
            int clamped = Mathf.Max(1, newMax);
            if (clamped == maxStamina) return;

            maxStamina = clamped;
            if (_current > maxStamina) _current = maxStamina;
            Changed?.Invoke(_current, maxStamina);
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
            Changed?.Invoke(_current, maxStamina);
            return true;
        }

        public void Restore(float amount)
        {
            if (amount <= 0f) return;
            float previous = _current;
            _current = Mathf.Min(maxStamina, _current + amount);
            if (!Mathf.Approximately(previous, _current)) Changed?.Invoke(_current, maxStamina);
        }

        /// <summary>Explicit refill to the ceiling. Only a reset path (new run, respawn) may call it.</summary>
        public void RefillToMax()
        {
            if (IsFull) return;
            _current = maxStamina;
            Changed?.Invoke(_current, maxStamina);
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

            _current = Mathf.Min(maxStamina, _current + regenPerSecond * Time.deltaTime);
            Changed?.Invoke(_current, maxStamina);
        }
    }
}
