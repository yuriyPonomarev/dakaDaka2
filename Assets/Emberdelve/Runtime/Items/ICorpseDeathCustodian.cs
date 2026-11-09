using UnityEngine;

namespace Emberdelve.Items
{
    /// <summary>Server-owned transaction boundary for moving a dead actor's exact loadout into world corpse custody.</summary>
    public interface ICorpseDeathCustodian
    {
        bool TryCommitDeath(int playerId, Vector3 position, Inventory bag, Equipment equipment, out string reason);
    }
}
