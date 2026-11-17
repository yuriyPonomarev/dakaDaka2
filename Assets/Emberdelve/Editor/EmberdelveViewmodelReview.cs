using UnityEditor;
using UnityEngine;
using Emberdelve.Player;

namespace Emberdelve.EditorTools
{
    /// <summary>Play-mode review helpers for the first-person arms: slow motion and a held guard without a mouse.</summary>
    public static class EmberdelveViewmodelReview
    {
        [MenuItem("Emberdelve/Debug/Slow Motion Toggle (Play Mode)")]
        public static void SlowMotion()
        {
            if (!EditorApplication.isPlaying) return;
            Time.timeScale = Time.timeScale < 0.99f ? 1f : 0.12f;
            Debug.Log("[ViewmodelReview] timeScale " + Time.timeScale);
        }

        [MenuItem("Emberdelve/Debug/Hold Guard Toggle (Play Mode)")]
        public static void HoldGuard()
        {
            if (!EditorApplication.isPlaying) return;
            PlayerRig rig = Object.FindFirstObjectByType<PlayerRig>();
            if (rig == null || rig.Input == null) return;
            var property = rig.Input.GetType().GetProperty("SecondaryHeldOverride",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (property == null) { Debug.LogError("[ViewmodelReview] no SecondaryHeldOverride"); return; }
            bool? current = (bool?)property.GetValue(rig.Input);
            bool next = !(current.HasValue && current.Value);
            property.SetValue(rig.Input, next ? (bool?)true : null);
            Debug.Log("[ViewmodelReview] guard " + (next ? "held" : "released"));
        }

        [MenuItem("Emberdelve/Debug/Hold Pitch Torch (Play Mode)")]
        public static void HoldTorch()
        {
            if (!EditorApplication.isPlaying) return;
            var hotbar = Emberdelve.Items.HotbarController.Local;
            if (hotbar == null || hotbar.Bag == null) { Debug.LogError("[ViewmodelReview] no local hotbar"); return; }
            for (int slot = 0; slot < 6; slot++)
            {
                if (!hotbar.Bag.Hotbar.TryGetStack(slot, out var stack) || stack.Item == null || stack.Item.itemId != "pitch_torch") continue;
                bool ok = hotbar.Bag.RequestQuickStack(stack.StackId, out string reason);
                Debug.Log("[ViewmodelReview] hold torch slot " + slot + ": " + (ok ? "ok" : reason));
                return;
            }
            Debug.LogError("[ViewmodelReview] no pitch torch on the belt");
        }
    }
}
