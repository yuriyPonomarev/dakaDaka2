using System;
using System.Text;
using Emberdelve.Items;
using Unity.Netcode;
using UnityEngine;

namespace Emberdelve.Net
{
    [Serializable]
    public sealed class CorpseContainerDocument
    {
        public int schemaVersion = 1;
        public ulong corpseId;
        public ulong revision;
        public int sourcePlayerId;
        public Vector3 worldPosition;
        public CorpseContainerEntryDocument[] entries;
    }

    [Serializable]
    public sealed class CorpseContainerEntryDocument
    {
        public ulong entryId;
        public ItemSnapshot item;
    }

    /// <summary>Server-authored display snapshot. Item data is informational on clients; transfer intents carry only IDs and revisions.</summary>
    public struct CorpseContainerWire : INetworkSerializable
    {
        public const int MaxUtf8Bytes = 16000;
        public string Json;

        public static bool TryCreate(ulong corpseId, CorpseContainerModel model,
            out CorpseContainerWire wire, out string reason)
        {
            wire = default;
            if (corpseId == 0 || model == null)
            {
                reason = "corpse state needs a valid id and container";
                return false;
            }

            CorpseItemView[] items = model.CopyEntries();
            var document = new CorpseContainerDocument
            {
                corpseId = corpseId,
                revision = model.Revision,
                sourcePlayerId = model.SourcePlayerId,
                worldPosition = model.WorldPosition,
                entries = new CorpseContainerEntryDocument[items.Length]
            };
            for (int i = 0; i < items.Length; i++)
            {
                document.entries[i] = new CorpseContainerEntryDocument
                {
                    entryId = items[i].EntryId,
                    item = items[i].CopySnapshot()
                };
            }

            string json = JsonUtility.ToJson(document);
            if (string.IsNullOrEmpty(json) || Encoding.UTF8.GetByteCount(json) > MaxUtf8Bytes)
            {
                reason = "corpse state exceeds the network snapshot limit";
                return false;
            }
            wire = new CorpseContainerWire { Json = json };
            reason = null;
            return true;
        }

        public bool TryRead(out CorpseContainerDocument document, out string reason)
        {
            document = null;
            if (string.IsNullOrEmpty(Json) || Encoding.UTF8.GetByteCount(Json) > MaxUtf8Bytes)
            {
                reason = "corpse wire snapshot is empty or oversized";
                return false;
            }
            try
            {
                document = JsonUtility.FromJson<CorpseContainerDocument>(Json);
            }
            catch (Exception e)
            {
                reason = "corpse wire snapshot could not be parsed: " + e.Message;
                return false;
            }
            if (document == null || document.schemaVersion != 1 || document.corpseId == 0 ||
                document.sourcePlayerId < 0 || document.entries == null ||
                document.entries.Length > ContainerSnapshot.MaxEntries * 2)
            {
                document = null;
                reason = "corpse wire snapshot has invalid fields";
                return false;
            }
            for (int i = 0; i < document.entries.Length; i++)
            {
                CorpseContainerEntryDocument entry = document.entries[i];
                if (entry == null || entry.entryId == 0 || entry.item == null || entry.item.quantity <= 0)
                {
                    document = null;
                    reason = "corpse wire snapshot contains an invalid entry";
                    return false;
                }
                for (int j = 0; j < i; j++)
                {
                    if (document.entries[j].entryId == entry.entryId)
                    {
                        document = null;
                        reason = "corpse wire snapshot repeats an entry id";
                        return false;
                    }
                }
            }
            reason = null;
            return true;
        }

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            if (serializer.IsWriter && Json == null) Json = string.Empty;
            serializer.SerializeValue(ref Json);
        }
    }
}
