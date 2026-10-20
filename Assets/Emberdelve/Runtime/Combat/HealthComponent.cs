using System;
using UnityEngine;
using Emberdelve.Core;

namespace Emberdelve.Combat
{
    /// <summary>
    /// Health pool for any combat entity. Damage only arrives through an authority
    /// (see LocalCombatAuthority); nothing in this class computes damage.
    /// </summary>
    public sealed class HealthComponent : MonoBehaviour, IDamageable
    {
        [SerializeField] private int maxHealth = 100;
        [SerializeField] private float regenPerSecond = 0f;
        [SerializeField] private float regenDelay = 6f;

        private float _current;
        private float _timeSinceDamage;

        public event Action<DamageInfo> Damaged;
        public event Action<float, float> Changed;
        public event Action Died;

        public bool IsDead { get; private set; }
        public float CurrentHealth => _current;
        public float MaxHealth => maxHealth;
        public bool IsRegenerating => !IsDead && regenPerSecond > 0f && _timeSinceDamage >= regenDelay;

        /// <summary>The rate currently configured, so a tool or a panel can assert it without ticking frames.</summary>
        public float RegenPerSecond => regenPerSecond;

        private void Awake()
        {
            if (_current <= 0f)
            {
                _current = maxHealth;
            }
        }

        public void Configure(int health, float regenRate, float delay)
        {
            maxHealth = Mathf.Max(1, health);
            regenPerSecond = Mathf.Max(0f, regenRate);
            regenDelay = Mathf.Max(0f, delay);
            _current = maxHealth;
            IsDead = false;
            _timeSinceDamage = delay;
            Changed?.Invoke(_current, maxHealth);
        }

        /// <summary>
        /// Changes the ceiling without resetting the run, which is what equipping or unequipping armor does.
        /// A smaller ceiling clamps the current value down; a bigger ceiling raises the ceiling ONLY. The
        /// difference is never added to the pool, so wearing a breastplate cannot heal anybody and a raised
        /// constitution score cannot refill a wounded run. A pool that is already at 0 stays at 0: changing
        /// gear is not a revive. The regen clock is left alone, so a gear change neither restarts nor skips
        /// the delay the class authored.
        /// </summary>
        public void SetMax(int newMax)
        {
            int clamped = Mathf.Max(1, newMax);
            if (clamped == maxHealth) return;

            maxHealth = clamped;
            if (_current > maxHealth) _current = maxHealth;

            Changed?.Invoke(_current, maxHealth);
        }

        public void ApplyDamage(DamageInfo damage)
        {
            if (IsDead) return;

            _current = Mathf.Max(0f, _current - Mathf.Max(0f, damage.Amount));
            _timeSinceDamage = 0f;
            Changed?.Invoke(_current, maxHealth);
            Damaged?.Invoke(damage);

            if (_current <= 0f)
            {
                IsDead = true;
                Died?.Invoke();
            }
        }

        /// <summary>Sets the pool directly. Used by verification flows that need a known starting number.</summary>
        public void SetHealth(float amount)
        {
            if (IsDead) return;

            float previous = _current;
            _current = Mathf.Clamp(amount, 1f, maxHealth);
            if (!Mathf.Approximately(previous, _current)) Changed?.Invoke(_current, maxHealth);
        }

        public void Heal(float amount)
        {
            if (IsDead || amount <= 0f) return;            float previous = _current;
            _current = Mathf.Min(maxHealth, _current + amount);
            if (!Mathf.Approximately(previous, _current))
            {
                Changed?.Invoke(_current, maxHealth);
            }
        }

        private void Update()
        {
            if (IsDead || regenPerSecond <= 0f) return;
            if (_current >= maxHealth) return;

            _timeSinceDamage += Time.deltaTime;
            if (_timeSinceDamage < regenDelay) return;

            _current = Mathf.Min(maxHealth, _current + regenPerSecond * Time.deltaTime);
            Changed?.Invoke(_current, maxHealth);
        }
    }
}
