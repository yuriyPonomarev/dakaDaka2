using UnityEditor;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    internal static class EmberdelveGateExit
    {
        public static void Exit(int code)
        {
            if (EmberdelveGateSuite.Active) { EmberdelveGateSuite.NotifyExit(code); return; }
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(code);
            }
            else
            {
                if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
                Debug.Log("[Emberdelve][GateExit] interactive editor: not exiting (code=" + code + ")");
            }
        }
    }
}
