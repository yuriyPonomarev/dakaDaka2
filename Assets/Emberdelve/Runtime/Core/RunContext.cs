using UnityEngine;
using Emberdelve.Data;

namespace Emberdelve.Core
{
    public readonly struct RunResult
    {
        public readonly RunState Outcome;
        public readonly string ClassName;
        public readonly float LootValue;
        public readonly int ItemsCollected;
        public readonly int EnemiesSlain;
        public readonly float DurationSeconds;

        public RunResult(RunState outcome, string className, float lootValue, int itemsCollected, int enemiesSlain, float duration)
        {
            Outcome = outcome; ClassName = className; LootValue = lootValue;
            ItemsCollected = itemsCollected; EnemiesSlain = enemiesSlain; DurationSeconds = duration;
        }
    }

    /// <summary>
    /// Carries the player's chosen class and run bookkeeping between scenes.
    /// Today it is filled locally in the lobby. In a networked build this same struct would be
    /// produced by the session/lobby service and handed to the client, so nothing downstream changes.
    /// </summary>
    public static class RunContext
    {
        public static CharacterClassData SelectedClass { get; private set; }
        public static RunResult LastResult { get; private set; }
        public static bool HasResult { get; private set; }

        public static void SelectClass(CharacterClassData data)
        {
            SelectedClass = data;
            HasResult = false;
        }

        public static void CompleteRun(RunResult result)
        {
            LastResult = result;
            HasResult = true;
        }

        public static CharacterClassData GetSelectedClassOrDefault(CharacterClassData fallback)
        {
            if (SelectedClass != null) return SelectedClass;
            SelectedClass = fallback;
            return SelectedClass;
        }
    }
}
