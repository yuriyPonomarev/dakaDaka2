using Emberdelve.Core;

namespace Emberdelve.Items
{
    /// <summary>Run authority takes custody of rolled loot before the source releases it.</summary>
    public interface IWorldLootContainerHost
    {
        bool TryPublishLoot(CorpseContainerModel model, out ulong id, out string reason);
        void OpenLoot(IInteractionActor actor, ulong id);
    }
}
