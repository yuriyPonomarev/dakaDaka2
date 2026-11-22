using Emberdelve.Flow;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>Compatibility adapter for older offline Royale scenes; all loot custody belongs to the shared service.</summary>
    [DisallowMultipleComponent]
    internal sealed class OfflineRoyaleLoot : MonoBehaviour
    {
        public void Configure(RunDirector director) => OfflineLootService.Ensure(director);
    }
}
