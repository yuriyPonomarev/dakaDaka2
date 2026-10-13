using UnityEngine;
using Emberdelve.InputLayer;
using Emberdelve.Player;

namespace Emberdelve.Items
{
    /// <summary>
    /// Turns the 1-4 keys into "use inventory slot N". Kept separate from Inventory so the bag has no
    /// dependency on devices, and separate from the world Interactor so picking up and using never mix.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HotbarController : MonoBehaviour
    {
        [SerializeField] private PlayerRig rig;
        [SerializeField] private Inventory inventory;

        private void Awake()
        {
            if (rig == null) rig = GetComponent<PlayerRig>();
            if (inventory == null) inventory = GetComponent<Inventory>();
        }

        private void Update()
        {
            if (rig == null || inventory == null) return;

            EmberdelveInput input = rig.Input;
            if (input == null || rig.IsDead) return;

            for (int i = 0; i < 4; i++)
            {
                if (input.SlotPressed(i)) inventory.TryUseSlot(i);
            }
        }
    }
}
