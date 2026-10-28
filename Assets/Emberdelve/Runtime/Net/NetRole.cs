using System;

namespace Emberdelve.Core
{
    /// <summary>
    /// Which network role this process plays. Pure data: this file (and the whole Emberdelve.Runtime
    /// assembly) has no networking package reference. The default is Offline, and nothing network related
    /// is created while the role is Offline; the Emberdelve.Net bootstrap sets it exactly once, before the
    /// first scene loads, from the command line.
    /// </summary>
    public enum NetRoleKind
    {
        Offline = 0,
        DedicatedServer = 1,
        Host = 2,
        Client = 3
    }

    public static class NetRole
    {
        private static NetRoleKind _current = NetRoleKind.Offline;

        public static NetRoleKind Current => _current;

        public static bool IsOffline => _current == NetRoleKind.Offline;

        /// <summary>Offline, dedicated server and host run the gameplay simulation.</summary>
        public static bool RunsSimulation =>
            _current == NetRoleKind.Offline || _current == NetRoleKind.DedicatedServer || _current == NetRoleKind.Host;

        /// <summary>Offline, host and client present a local player (camera, input, HUD).</summary>
        public static bool PresentsLocalPlayer =>
            _current == NetRoleKind.Offline || _current == NetRoleKind.Host || _current == NetRoleKind.Client;

        public static bool IsClientOnly => _current == NetRoleKind.Client;

        public static bool IsNetworked => _current != NetRoleKind.Offline;

        /// <summary>
        /// U3A3 proof switch (--ember-net-no-enemies): on a process that runs the simulation, the dungeon's enemies are not
        /// started, so a walking/admission proof is not disturbed by combat. Always false offline; only the launcher sets it.
        /// </summary>
        public static bool EnemiesDisabled { get; private set; }

        /// <summary>Called by the launcher only.</summary>
        public static void SetEnemiesDisabled(bool disabled)
        {
            EnemiesDisabled = disabled && _current != NetRoleKind.Offline;
        }

        /// <summary>Called by the launcher only. Not meant for gameplay code.</summary>
        public static void Set(NetRoleKind role)
        {
            if (!Enum.IsDefined(typeof(NetRoleKind), role)) role = NetRoleKind.Offline;
            _current = role;
        }

        /// <summary>Restores the offline default (tests and gates).</summary>
        public static void ResetToOffline()
        {
            _current = NetRoleKind.Offline;
            EnemiesDisabled = false;
        }
    }
}
