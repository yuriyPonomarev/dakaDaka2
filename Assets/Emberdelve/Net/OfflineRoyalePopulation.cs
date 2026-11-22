using Emberdelve.Core;
using Emberdelve.Flow;
using Emberdelve.Player;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Emberdelve.Net
{
    /// <summary>Offline adapter uses the same pristine player prefab without spawning network objects.</summary>
    public sealed class OfflineRoyalePopulation : MonoBehaviour
    {
        private RunDirector _director;
        private RoyalePopulationDriver _population;
        private RoyaleDescentDriver _descent;
        private GameObject _prefab;
        private OfflineLootService _loot;
        private float _nextTick;
        private uint _lastLocalDescentGeneration;
        private string _lastReason;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= Attach;
            SceneManager.sceneLoaded += Attach;
            Attach(SceneManager.GetActiveScene(), LoadSceneMode.Single);
        }

        private static void Attach(Scene scene, LoadSceneMode mode)
        {
            if (!Application.isPlaying || !scene.isLoaded) return;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var director in root.GetComponentsInChildren<RunDirector>(true))
                    if ((director.RoyaleMode.HasValue || RunContext.SelectedRoyaleMode.HasValue)
                        && !director.IsServerMode && NetRole.IsOffline
                        && director.GetComponent<OfflineRoyalePopulation>() == null)
                        director.gameObject.AddComponent<OfflineRoyalePopulation>();
        }

        private void LateUpdate()
        {
            if (!NetRole.IsOffline) return;
            if (_director == null) _director = GetComponent<RunDirector>();
            if (_director == null || _director.IsServerMode || !_director.IsStarted || !_director.RoyaleMode.HasValue) return;
            float now = Time.unscaledTime;
            var player = _director.Player;
            // The interaction enters transit during Update. Process that new entry before rendering.
            bool enteredPortal = _population?.Started == true && player != null && player.IsInFloorTransit
                && player.FloorMovementGeneration != _lastLocalDescentGeneration;
            if (!enteredPortal && now < _nextTick) return;
            _nextTick = now + .25f;
            if (enteredPortal) _lastLocalDescentGeneration = player.FloorMovementGeneration;
            if (_loot == null && _director.Player != null)
            {
                _loot = OfflineLootService.Ensure(_director);
            }
            if (_population != null && _population.Started)
            {
                if (_descent == null) _descent = new RoyaleDescentDriver(_director,
                    _director.Config != null ? _director.Config.raidDurationSeconds : 600,
                    _director.FloorCoordinator.TryPrepareDestination, SpawnBot, _ => true);
                if (!_descent.Tick(now, out string wait) && wait != _lastReason)
                { _lastReason = wait; Debug.Log("[Royale] " + wait, this); }
                return;
            }
            if (_prefab == null)
            {
                var catalog = Resources.Load<NetPrefabCatalog>(NetPrefabCatalog.ResourcePath);
                if (catalog != null) _prefab = catalog.PlayerPrefab;
            }
            if (_population == null) _population = new RoyalePopulationDriver(_director, 1,
                _director.Config != null ? _director.Config.raidDurationSeconds : 600, 0, SpawnBot);
            if (!_population.Tick(now, _prefab != null && _director.LocalSession != null, out string reason)
                && reason != _lastReason)
            { _lastReason = reason; Debug.Log("[Royale] " + reason, this); }
        }

        private bool SpawnBot(int floor, int party, out PlayerRig rig, out string reason)
        {
            rig = null; reason = "No class for offline bot";
            string preferred = _director.FloorCoordinator.ReservedCount(floor) % 2 == 0 ? "warrior" : "archer";
            if (!_director.TryResolveClass(preferred, out var character)) character = _director.LocalSession?.Class;
            return character != null && _director.FloorCoordinator.TrySpawnBot(floor, party, _prefab, character, out rig, out reason);
        }
    }
}
