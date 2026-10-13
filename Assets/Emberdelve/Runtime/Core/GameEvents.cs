using System;
using UnityEngine;
using Emberdelve.Data;

namespace Emberdelve.Core
{
    /// <summary>
    /// Central gameplay event hub. Systems publish facts here; UI, audio and VFX only ever listen.
    /// A future authoritative network layer can raise the exact same events from replicated
    /// server state, which means presentation code does not change when networking is added.
    /// Subscribers must unsubscribe in OnDisable/OnDestroy.
    /// </summary>
    public static class GameEvents
    {
        public static event Action<RunState> RunStateChanged;
        public static event Action<float, float> PlayerHealthChanged;
        public static event Action<DamageInfo> PlayerDamaged;
        public static event Action<HitFeedback> DamageDealt;
        public static event Action<ItemData, int> ItemCollected;
        public static event Action InventoryChanged;
        public static event Action<int, int> AmmoChanged;
        public static event Action<string> InteractionPromptChanged;
        public static event Action<float> InteractionHoldChanged;
        public static event Action<string> ObjectiveChanged;
        public static event Action<string> StatusLineChanged;
        public static event Action<string> ToastRaised;
        public static event Action<RunResult> RunEnded;
        public static event Action<bool> PauseChanged;

        public static void RaiseRunState(RunState state) => RunStateChanged?.Invoke(state);
        public static void RaisePlayerHealth(float current, float max) => PlayerHealthChanged?.Invoke(current, max);
        public static void RaisePlayerDamaged(DamageInfo info) => PlayerDamaged?.Invoke(info);
        public static void RaiseDamageDealt(HitFeedback feedback) => DamageDealt?.Invoke(feedback);
        public static void RaiseItemCollected(ItemData item, int totalCollected) => ItemCollected?.Invoke(item, totalCollected);
        public static void RaiseInventoryChanged() => InventoryChanged?.Invoke();
        public static void RaiseAmmo(int current, int reserve) => AmmoChanged?.Invoke(current, reserve);
        public static void RaiseInteractionPrompt(string prompt) => InteractionPromptChanged?.Invoke(prompt);
        public static void RaiseInteractionHold(float progress) => InteractionHoldChanged?.Invoke(progress);
        public static void RaiseObjective(string objective) => ObjectiveChanged?.Invoke(objective);
        public static void RaiseStatusLine(string status) => StatusLineChanged?.Invoke(status);
        public static void RaiseToast(string message) => ToastRaised?.Invoke(message);
        public static void RaiseRunEnded(RunResult result) => RunEnded?.Invoke(result);
        public static void RaisePause(bool paused) => PauseChanged?.Invoke(paused);
    }
}
