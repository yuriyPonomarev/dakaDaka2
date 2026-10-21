using System.Collections.Generic;
using UnityEngine;

namespace Emberdelve.Combat
{
    /// <summary>
    /// Low-noise combat logging. A message is emitted at most once per key per second (real time), so a held
    /// button or an enemy retrying every frame cannot flood the log, while the first occurrence of every
    /// distinct reason is always visible. Diagnostic only: nothing here influences gameplay.
    /// </summary>
    public static class CombatDiagnostics
    {
        public const float MinIntervalSeconds = 1f;

        private static readonly Dictionary<string, float> _lastLogged = new Dictionary<string, float>();

        /// <summary>True (and records the time) when this key has not logged within the last second.</summary>
        public static bool ShouldLog(string key)
        {
            float now = Time.realtimeSinceStartup;
            if (_lastLogged.TryGetValue(key, out float last) && now - last < MinIntervalSeconds && now >= last) return false;
            _lastLogged[key] = now;
            return true;
        }

        public static void Log(string key, string message, Object context = null)
        {
            if (ShouldLog(key)) Debug.Log(message, context);
        }

        /// <summary>Forgets throttle state (tests and domain-reload-free play mode restarts).</summary>
        public static void Reset() => _lastLogged.Clear();
    }
}
