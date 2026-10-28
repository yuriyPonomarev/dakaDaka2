using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Switches the scripting define EMBER_UTP_MANAGED_SOCKET (Standalone) that compiles the optional assembly
    /// Emberdelve.Net.Managed (socket path "managed" of Docs/Tasks/UTP_RESET.md). Off by default so a mismatch with the installed
    /// transport package API can never break the rest of the project. Unity recompiles after the change.
    /// Batch: -executeMethod Emberdelve.EditorTools.EmberdelveManagedSocketDefine.Enable  (or .Disable), then run the probe in a second launch.
    /// </summary>
    public static class EmberdelveManagedSocketDefine
    {
        public const string Define = "EMBER_UTP_MANAGED_SOCKET";

        [MenuItem("Emberdelve/Net/Toggle managed UDP socket define")]
        public static void Toggle()
        {
            if (IsOn()) Disable(); else Enable();
        }

        public static void Enable() { Set(true); }
        public static void Disable() { Set(false); }

        private static bool IsOn()
        {
            return (";" + PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Standalone) + ";").Contains(";" + Define + ";");
        }

        private static void Set(bool on)
        {
            string current = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Standalone);
            string list = ";" + current + ";";
            list = list.Replace(";" + Define + ";", ";");
            string next = list.Trim(';');
            if (on) next = next.Length == 0 ? Define : next + ";" + Define;
            PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.Standalone, next);
            Debug.Log("[Emberdelve][Net] scripting define " + Define + (on ? " ENABLED" : " disabled") + " for Standalone (was '" + current + "', now '" + next + "')");
        }
    }
}
