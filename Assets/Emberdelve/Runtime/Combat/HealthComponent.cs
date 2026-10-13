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

        public void Heal(float amount)
        {
            if (IsDead || amount <= 0f) return;
            float previous = _current;
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
