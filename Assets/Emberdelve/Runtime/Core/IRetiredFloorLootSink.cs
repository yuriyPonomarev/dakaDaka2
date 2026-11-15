using UnityEngine;

namespace Emberdelve.Core
{
    /// <summary>Removes inaccessible world custody after a source floor has safely retired.</summary>
    public interface IRetiredFloorLootSink
    {
        void RetireFloorLoot(Rect bounds);
    }
}
