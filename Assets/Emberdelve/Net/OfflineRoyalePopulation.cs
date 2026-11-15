using Emberdelve.Core;
using Emberdelve.Flow;
using Emberdelve.Player;
using Emberdelve.Level;
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
        private float _nextTick;
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
                    if ((director.RoyaleMode.HasValue || RunContext.SelectedRoyaleMode.HasValue) && !director.IsServerMode && !NetRole.IsClientOnly
                        && director.GetComponent<OfflineRoyalePopulation>() == null)
                        director.gameObject.AddComponent<OfflineRoyalePopulation>();
        }

        private void Update()
        {
            if (NetRole.IsClientOnly || Time.unscaledTime < _nextTick) return;
            _nextTick = Time.unscaledTime + .25f;
            if (_director == null) _director = GetComponent<RunDirector>();
            if (_director == null || _director.IsServerMode || !_director.IsStarted || !_director.RoyaleMode.HasValue) return;
            if (_population != null && _population.Started)
            {
                if (_descent == null) _descent = new RoyaleDescentDriver(_director,
                    _director.Config != null ? _director.Config.raidDurationSeconds : 600, PrepareFloor, SpawnBot, _ => true);
                if (!_descent.Tick(Time.unscaledTime, out string wait) && wait != _lastReason)
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
            if (!_population.Tick(Time.unscaledTime, _prefab != null && _director.LocalSession != null, out string reason)
                && reason != _lastReason)
            { _lastReason = reason; Debug.Log("[Royale] " + reason, this); }
        }

        private bool PrepareFloor(int floor, out string reason)
        {
            reason = "Missing floor catalog or initial plan";
            if (!RoyaleFloorCatalogs.TryLoad(floor, out var catalog, out reason)) return false;
            var initial = _director.FloorCoordinator.CopyPreparedPlan(1);
            if (catalog == null || initial == null || _director.Layout == null) return false;
            int seed = unchecked(initial.Seed + floor * 7919);
            if (!DungeonModulePlanner.TryCreate(seed, catalog.Modules, out var plan, out reason)) return false;
            var layout = _director.Layout;
            float spacing = Mathf.Max(layout.gridWidth, layout.gridHeight) * layout.CellSize + 100f;
            var origin = layout.origin + Vector3.right * (spacing * (floor - 1));
            return _director.FloorCoordinator.TryPrepare(floor, layout, catalog, plan, origin, out reason);
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
