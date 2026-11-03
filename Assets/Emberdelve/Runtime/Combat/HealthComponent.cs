using System;
using UnityEngine;
using Emberdelve.Core;

namespace Emberdelve.Combat
{
    /// <summary>
    /// Health pool for any combat entity. Damage only arrives through an authority
    /// (see LocalCombatAuthority); nothing in this class computes damage.
    ///
    /// U3B3: in a client-only network process this pool is a DISPLAY of the server's numbers. <see cref="ApplyDamage"/> does nothing there
    /// (it only counts the attempt in <see cref="ClientBlockedDamage"/>), the local regeneration is off, and the only writer is
    /// <see cref="ApplyReplicatedState"/>, which is itself refused in every other process role. Offline, host and server behaviour is unchanged.
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

        /// <summary>U3B3: damage applications refused because this process is a network client (a client never resolves combat). Process-wide counter for logs and gates.</summary>
        public static int ClientBlockedDamage { get; private set; }

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
            GameEventTransaction.Publish(Changed, _current, (float)maxHealth);
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

            GameEventTransaction.Publish(Changed, _current, (float)maxHealth);
        }

        public void ApplyDamage(DamageInfo damage)
        {
            if (NetRole.IsClientOnly)
            {
                ClientBlockedDamage++;
                return;
            }

            if (IsDead) return;

            _current = Mathf.Max(0f, _current - Mathf.Max(0f, damage.Amount));
            _timeSinceDamage = 0f;
            GameEventTransaction.Publish(Changed, _current, (float)maxHealth);
            Damaged?.Invoke(damage);

            if (_current <= 0f)
            {
                IsDead = true;
                Died?.Invoke();
            }
        }

        /// <summary>
        /// U3B3, client-only processes: shows the server's replicated health (current, maximum, dead). Returns false and changes nothing in
        /// any other role, so no server, host or offline code path can be handed a number this way. Raises Changed, and Died the first time
        /// the server reports the body dead.
        /// </summary>
        public bool ApplyReplicatedState(float current, float max, bool dead)
        {
            if (!NetRole.IsClientOnly) return false;
            if (float.IsNaN(current) || float.IsNaN(max) || float.IsInfinity(current) || float.IsInfinity(max)) return false;

            maxHealth = Mathf.Max(1, Mathf.RoundToInt(max));
            _current = Mathf.Clamp(current, 0f, maxHealth);
            bool wasDead = IsDead;
            IsDead = dead || _current <= 0f;
            if (IsDead) _current = 0f;
            GameEventTransaction.Publish(Changed, _current, (float)maxHealth);
            if (IsDead && !wasDead) Died?.Invoke();
            return true;
        }

        /// <summary>Sets the pool directly. Used by verification flows that need a known starting number.</summary>
        public void SetHealth(float amount)
        {
            if (IsDead) return;

            float previous = _current;
            _current = Mathf.Clamp(amount, 1f, maxHealth);
            if (!Mathf.Approximately(previous, _current)) GameEventTransaction.Publish(Changed, _current, (float)maxHealth);
        }

        public void Heal(float amount)
        {
            if (IsDead || amount <= 0f) return;            float previous = _current;
            _current = Mathf.Min(maxHealth, _current + amount);
            if (!Mathf.Approximately(previous, _current))
            {
                GameEventTransaction.Publish(Changed, _current, (float)maxHealth);
            }
        }

        private void Update()
        {
            if (IsDead || regenPerSecond <= 0f) return;
            if (NetRole.IsClientOnly) return; // U3B3: a client shows the server's number, it does not regenerate its own
            if (_current >= maxHealth) return;

            _timeSinceDamage += Time.deltaTime;
            if (_timeSinceDamage < regenDelay) return;

            _current = Mathf.Min(maxHealth, _current + regenPerSecond * Time.deltaTime);
            GameEventTransaction.Publish(Changed, _current, (float)maxHealth);
        }
    }
}
