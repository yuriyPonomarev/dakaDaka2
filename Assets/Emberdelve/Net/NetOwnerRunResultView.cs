using Emberdelve.Core;
using Emberdelve.Net.Rules;
using Emberdelve.Player;
using Unity.Netcode;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>Owner-side presentation of the result received from the server.</summary>
    public sealed class NetOwnerRunResultView : MonoBehaviour
    {
        private NetPlayerRig _owner;
        private PlayerRig _rig;
        private readonly RunResultReceiptGate _receipt = new RunResultReceiptGate();

        public void Configure(NetPlayerRig owner)
        {
            _owner = owner;
            _rig = owner != null ? owner.Rig : null;
        }

        /// <summary>Called only by the owner RPC receiver or the host's server-side event bridge.</summary>
        public bool ReceiveServerResult(RunResultWire wire, bool publishLocalEvents = true)
        {
            if (_owner == null || !_owner.IsOwner || _rig == null ||
                !wire.TryToRunResult(_rig.PlayerId, out RunResult result) ||
                !_receipt.TryAccept(_rig.PlayerId, result.PlayerId)) return false;

            if (result.Outcome == RunState.Extracted) _rig.MarkExtracted();
            else _rig.SetInputEnabled(false);

            RunContext.CompleteRun(result); // local display cache only; the server remains the only authority.
            if (!publishLocalEvents) return true; // host already raised these events in RunDirector.

            GameEvents.RaisePlayerRunState(result.PlayerId, result.Outcome);
            GameEvents.RaiseRunEnded(result.PlayerId, result);
            return true;
        }

        public static bool IsOwnerView(NetPlayerRole role) => role == NetPlayerRole.Owner || role == NetPlayerRole.HostPlayer;
    }
}
