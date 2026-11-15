namespace Emberdelve.Net.Rules
{
    public readonly struct CorpseTransferUiRequest
    {
        public readonly ulong CorpseId;
        public readonly ulong EntryId;
        public readonly int Amount;
        public readonly ulong CorpseRevision;
        public readonly ulong BagRevision;
        public readonly UnityEngine.Vector2Int? DestinationCell;

        public CorpseTransferUiRequest(ulong corpseId, ulong entryId, int amount, ulong corpseRevision, ulong bagRevision, UnityEngine.Vector2Int? destinationCell = null)
        {
            CorpseId = corpseId;
            EntryId = entryId;
            Amount = amount;
            CorpseRevision = corpseRevision;
            BagRevision = bagRevision;
            DestinationCell = destinationCell;
        }
    }

    /// <summary>UI-only request and response state. It never applies inventory or corpse mutations.</summary>
    public sealed class CorpseContainerUiSession
    {
        private bool _preparingTransfer;
        private ulong _pendingEntryId;
        private int _pendingAmount;

        public ulong ActiveCorpseId { get; private set; }
        public bool IsOpen => ActiveCorpseId != 0;
        public CorpseContainerDocument Document { get; private set; }
        public uint StateRequestSequence { get; private set; }
        public uint TransferSequence { get; private set; }
        public bool IsTransferPending => _preparingTransfer || TransferSequence != 0;
        public bool NeedsRefresh { get; private set; }
        public string Status { get; private set; }

        public bool Open(ulong corpseId)
        {
            if (corpseId == 0) return false;
            Close();
            ActiveCorpseId = corpseId;
            Status = "Requesting contents…";
            return true;
        }

        public void Close()
        {
            ActiveCorpseId = 0;
            Document = null;
            StateRequestSequence = 0;
            TransferSequence = 0;
            _preparingTransfer = false;
            _pendingEntryId = 0;
            _pendingAmount = 0;
            NeedsRefresh = false;
            Status = string.Empty;
        }

        public bool BeginStateRequest(uint sequence)
        {
            if (!IsOpen || sequence == 0 || StateRequestSequence != 0 || IsTransferPending) return false;
            StateRequestSequence = sequence;
            Status = "Requesting current contents…";
            return true;
        }

        public void CancelStateRequest(uint sequence, string reason)
        {
            if (sequence == 0 || StateRequestSequence != sequence) return;
            StateRequestSequence = 0;
            Status = reason ?? "Could not refresh container contents";
        }

        public bool ApplyState(CorpseContainerDocument document)
        {
            if (!IsOpen || document == null || document.corpseId != ActiveCorpseId || document.entries == null ||
                (Document != null && document.revision < Document.revision)) return false;
            Document = document;
            StateRequestSequence = 0;
            NeedsRefresh = false;
            Status = document.entries.Length == 0 ? "This container is empty" : string.Empty;
            return true;
        }

        public bool TryBeginTransfer(ulong entryId, int amount, ulong bagRevision,
            out CorpseTransferUiRequest request, out string reason, UnityEngine.Vector2Int? destinationCell = null)
        {
            request = default;
            if (!IsOpen || Document == null)
            {
                reason = "Wait for current container contents";
                return false;
            }
            if (StateRequestSequence != 0 || IsTransferPending)
            {
                reason = "Wait for the previous container request";
                return false;
            }
            if (entryId == 0 || amount <= 0)
            {
                reason = "Invalid item selection";
                return false;
            }

            CorpseContainerEntryDocument selected = null;
            for (int i = 0; i < Document.entries.Length; i++)
            {
                CorpseContainerEntryDocument candidate = Document.entries[i];
                if (candidate != null && candidate.entryId == entryId)
                {
                    selected = candidate;
                    break;
                }
            }
            if (selected?.item == null || amount > selected.item.quantity)
            {
                reason = "That item is no longer available";
                return false;
            }

            _preparingTransfer = true;
            _pendingEntryId = entryId;
            _pendingAmount = amount;
            request = new CorpseTransferUiRequest(ActiveCorpseId, entryId, amount, Document.revision, bagRevision, destinationCell);
            Status = "Sending transfer…";
            reason = null;
            return true;
        }

        public bool AttachTransferSequence(uint sequence)
        {
            if (!_preparingTransfer || sequence == 0) return false;
            _preparingTransfer = false;
            TransferSequence = sequence;
            return true;
        }

        public void CancelTransfer(string reason)
        {
            if (!_preparingTransfer && TransferSequence == 0) return;
            _preparingTransfer = false;
            TransferSequence = 0;
            _pendingEntryId = 0;
            _pendingAmount = 0;
            Status = reason ?? "Transfer could not be sent";
        }

        /// <returns>True only for the currently awaited state or transfer result.</returns>
        public bool ApplyResult(ulong corpseId, uint sequence, bool accepted, ulong corpseRevision, string reason)
        {
            if (!IsOpen || corpseId != ActiveCorpseId || sequence == 0) return false;
            if (sequence == StateRequestSequence)
            {
                StateRequestSequence = 0;
                Status = accepted ? "Waiting for contents…" : Refusal(reason);
                NeedsRefresh = accepted && Document == null;
                return true;
            }
            if (sequence != TransferSequence) return false;

            TransferSequence = 0;
            _pendingEntryId = 0;
            _pendingAmount = 0;
            NeedsRefresh = true;
            Status = accepted ? "Transfer accepted; syncing contents…" : Refusal(reason);
            return true;
        }

        public void MarkRefreshRequested()
        {
            NeedsRefresh = false;
        }

        private static string Refusal(string reason) => string.IsNullOrEmpty(reason) ? "Transfer refused" : reason;
    }
}
