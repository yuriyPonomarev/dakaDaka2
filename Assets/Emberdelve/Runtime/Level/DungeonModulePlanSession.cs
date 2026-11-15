using System;
using UnityEngine;

namespace Emberdelve.Level
{
    /// <summary>Process-local copy of the authority's immutable map plan, installed before clients admit player actors.</summary>
    public static class DungeonModulePlanSession
    {
        public static DungeonModulePlan Current { get; private set; }
        public static event Action<DungeonModulePlan> Changed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlay() => Reset();

        public static void Install(DungeonModulePlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (!plan.Validate(out string reason)) throw new ArgumentException("Invalid module plan: " + reason, nameof(plan));
            Current = plan;
            Changed?.Invoke(plan);
        }

        public static void Reset()
        {
            ClearCurrent();
            Changed = null;
        }

        public static void ClearCurrent() => Current = null;

        public static bool TryApplyToLoadedScene(out string reason)
        {
            reason = "no modular scene assembler is present";
            if (Current == null) { reason = "the server plan has not arrived"; return false; }
            MonoBehaviour[] behaviours = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            bool found = false;
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (!(behaviours[i] is IDungeonModulePlanConsumer consumer)) continue;
                if (!consumer.AcceptsSessionPlan) continue;
                found = true;
                if (!consumer.ApplyDungeonModulePlan(Current, out reason)) return false;
            }
            return found;
        }
    }

    public interface IDungeonModulePlanConsumer
    {
        bool AcceptsSessionPlan { get; }
        bool ApplyDungeonModulePlan(DungeonModulePlan plan, out string reason);
    }
}
