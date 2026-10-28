using System;
using System.Reflection;
using Emberdelve.Net.Rules;
using Unity.Netcode.Transports.UTP;

namespace Emberdelve.Net
{
    /// <summary>
    /// Chooses the socket layer of the transport (Docs/Tasks/UTP_RESET.md), before the NetworkManager starts:
    ///   stock / scan : nothing here (scan finds the stock socket later, see NetUdpResetGuard);
    ///   ws           : UnityTransport.UseWebSockets = true (TCP; no ICMP involved);
    ///   managed      : the custom INetworkInterface of the optional assembly Emberdelve.Net.Managed (compiled only when the scripting
    ///                  define EMBER_UTP_MANAGED_SOCKET is set), reached by reflection so this assembly never depends on it.
    /// If the managed assembly is missing or refuses, the transport stays on the stock socket and the result says so (it is
    /// never silent: the probe fails that arm by name).
    /// </summary>
    public static class NetSocketPath
    {
        private const string ManagedType = "Emberdelve.Net.Managed.ManagedUdpDriver, Emberdelve.Net.Managed";

        /// <summary>Applies <paramref name="path"/>; returns the text for the "socket-path" log line and sets <paramref name="effective"/>.</summary>
        public static string Apply(UnityTransport transport, NetUdpPath path, out NetUdpPath effective)
        {
            effective = path;
            switch (path)
            {
                case NetUdpPath.WebSockets:
                    transport.UseWebSockets = true;
                    return "ws requested=ws effective=ws transport=websockets(tcp)";

                case NetUdpPath.Managed:
                    {
                        string error = InstallManaged(transport);
                        if (error == null) return "managed requested=managed effective=managed transport=custom-udp(System.Net.Sockets)";
                        effective = NetUdpPath.Stock;
                        return "managed requested=managed effective=stock-FALLBACK reason=" + error;
                    }

                case NetUdpPath.Scan:
                    return "scan requested=scan effective=scan transport=stock-udp+connreset-off(" + (IsWindows() ? "windows" : "not-windows: no-op") + ")";

                default:
                    return "stock requested=stock effective=stock transport=stock-udp";
            }
        }

        /// <summary>Extra text of the managed interface (bind result, ioctl result); empty when it is not in use.</summary>
        public static string ManagedDetail()
        {
            try
            {
                Type type = Type.GetType(ManagedType, false);
                MethodInfo describe = type == null ? null : type.GetMethod("Describe", BindingFlags.Public | BindingFlags.Static);
                return describe == null ? "" : (string)describe.Invoke(null, null);
            }
            catch (Exception e)
            {
                return "describe-failed " + e.GetType().Name;
            }
        }

        private static string InstallManaged(UnityTransport transport)
        {
            try
            {
                Type type = Type.GetType(ManagedType, false);
                if (type == null) return "assembly-missing(define EMBER_UTP_MANAGED_SOCKET not set when this player was built)";
                MethodInfo install = type.GetMethod("Install", BindingFlags.Public | BindingFlags.Static);
                if (install == null) return "Install-method-missing";
                return (string)install.Invoke(null, new object[] { transport });
            }
            catch (Exception e)
            {
                Exception inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                return "install-threw " + inner.GetType().Name + " " + inner.Message;
            }
        }

        private static bool IsWindows()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            return true;
#else
            return false;
#endif
        }
    }
}
