using UnityEngine;

namespace Emberdelve.UI
{
    // Keep the lobby's existing pixel-based controls together at small window sizes.
    public sealed class LobbyLayoutScale : MonoBehaviour
    {
        private void OnEnable() => Update();
        private void Update() => transform.localScale = Vector3.one * Mathf.Min(1f, Screen.width / 1440f, Screen.height / 800f);
    }
}
