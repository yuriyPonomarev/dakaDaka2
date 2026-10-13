using System.Collections.Generic;
using UnityEngine;

namespace Emberdelve.Core
{
    /// <summary>
    /// Deliberately small service locator used only for services that runtime-spawned objects
    /// (projectiles, spawned loot, hit effects) need to reach. Everything scene-authored is wired
    /// through explicit serialized references instead.
    /// </summary>
    public static class GameServices
    {
        private static readonly Dictionary<System.Type, object> _services = new Dictionary<System.Type, object>();

        public static void Register<T>(T service) where T : class
        {
            if (service == null) return;
            _services[typeof(T)] = service;
        }

        public static void Unregister<T>(T service) where T : class
        {
            if (service == null) return;
            if (_services.TryGetValue(typeof(T), out object current) && ReferenceEquals(current, service))
            {
                _services.Remove(typeof(T));
            }
        }

        public static bool TryGet<T>(out T service) where T : class
        {
            service = null;
            if (_services.TryGetValue(typeof(T), out object found) && found is T typed)
            {
                service = typed;
                return true;
            }
            return false;
        }

        public static void Clear() => _services.Clear();
    }
}
