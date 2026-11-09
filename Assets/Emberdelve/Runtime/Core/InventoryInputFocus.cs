using System.Collections.Generic;
using UnityEngine;

namespace Emberdelve.Core
{
    /// <summary>Lets simulation defer Escape to inventory UI without depending on the UI assembly.</summary>
    public static class InventoryInputFocus
    {
        private static readonly HashSet<object> Owners = new HashSet<object>();
        public static int LastCloseFrame { get; private set; } = -1;
        public static bool OwnsEscape => Owners.Count > 0 || LastCloseFrame == Time.frameCount;
        public static void Open(object owner) => Owners.Add(owner);
        public static void Close(object owner)
        {
            if (Owners.Remove(owner)) LastCloseFrame = Time.frameCount;
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() { Owners.Clear(); LastCloseFrame = -1; }
    }
}
