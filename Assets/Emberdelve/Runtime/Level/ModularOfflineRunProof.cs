#if UNITY_EDITOR || DEVELOPMENT_BUILD
using UnityEngine;

namespace Emberdelve.Level
{
    /// <summary>Development-only evidence that consecutive offline modular raids build and start different validated maps.</summary>
    internal static class ModularOfflineRunProof
    {
        private static bool _hasPreviousRun;
        private static ulong _previousRaidSeed;
        private static int _previousMapSeed;
        private static string _previousLayoutHash;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() => Clear();

        internal static void Clear()
        {
            _hasPreviousRun = false;
            _previousRaidSeed = 0;
            _previousMapSeed = 0;
            _previousLayoutHash = string.Empty;
        }

        internal static void RecordStartedRun(ulong raidSeed, int mapSeed, string layoutHash, bool runStarted,
            bool layoutInstalled, bool sceneValidated)
        {
            if (!runStarted || !layoutInstalled || !sceneValidated || string.IsNullOrWhiteSpace(layoutHash))
            {
                Debug.LogError("[modular-offline-proof] FAIL: run start evidence is incomplete; no seed variation PASS recorded");
                return;
            }

            Debug.Log("[modular-offline-proof] run-started raidSeed=" + raidSeed + " mapSeed=" + mapSeed +
                      " hash=" + layoutHash + " layoutInstalled=true geometryNavExtractionValidated=true");
            if (_hasPreviousRun)
            {
                bool changed = raidSeed != _previousRaidSeed && mapSeed != _previousMapSeed && layoutHash != _previousLayoutHash;
                if (changed)
                    Debug.Log("[modular-offline-proof] TWO-RAID PASS previousRaidSeed=" + _previousRaidSeed +
                              " previousMapSeed=" + _previousMapSeed + " previousHash=" + _previousLayoutHash +
                              " currentRaidSeed=" + raidSeed + " currentMapSeed=" + mapSeed + " currentHash=" + layoutHash +
                              " bothRunsStartedAndValidated=true");
                else
                    Debug.LogError("[modular-offline-proof] TWO-RAID FAIL: consecutive validated modular raids reused a seed or layout");
            }

            _hasPreviousRun = true;
            _previousRaidSeed = raidSeed;
            _previousMapSeed = mapSeed;
            _previousLayoutHash = layoutHash;
        }
    }
}
#endif
