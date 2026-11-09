using System;
using System.Text;
using Emberdelve.Items;
using Unity.Netcode;
using UnityEngine;

namespace Emberdelve.Net
{
    [Serializable] public sealed class FloorLootDocument { public int schemaVersion = 1; public ulong revision; public FloorLootEntryDocument[] entries; }
    [Serializable] public sealed class FloorLootEntryDocument { public ulong id; public Vector3 position; public ItemSnapshot item; }

    /// <summary>Complete public ground-loot state, sent reliably to every admitted owner and on late join.</summary>
    public struct FloorLootWire : INetworkSerializable
    {
        public const int MaxUtf8Bytes = 48000;
        public string Json;
        public bool TryRead(out FloorLootDocument document, out string reason)
        {
            document = null;
            if (string.IsNullOrEmpty(Json) || Encoding.UTF8.GetByteCount(Json) > MaxUtf8Bytes)
            { reason = "floor loot snapshot is empty or oversized"; return false; }
            try { document = JsonUtility.FromJson<FloorLootDocument>(Json); }
            catch (Exception e) { reason = "floor loot snapshot parse failed: " + e.Message; return false; }
            if (document == null || document.schemaVersion != 1 || document.entries == null || document.entries.Length > 256)
            { document = null; reason = "floor loot snapshot has invalid fields"; return false; }
            for (int i = 0; i < document.entries.Length; i++)
            {
                var entry = document.entries[i];
                if (entry == null || entry.id == 0 || entry.item == null || string.IsNullOrEmpty(entry.item.itemId) || entry.item.quantity <= 0 ||
                    !Finite(entry.position))
                { document = null; reason = "floor loot snapshot contains an invalid entry"; return false; }
                for (int j = 0; j < i; j++) if (document.entries[j].id == entry.id)
                { document = null; reason = "floor loot snapshot repeats an item id"; return false; }
            }
            reason = null; return true;
        }
        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        { if (serializer.IsWriter && Json == null) Json = string.Empty; serializer.SerializeValue(ref Json); }
        private static bool Finite(Vector3 p) => Finite(p.x) && Finite(p.y) && Finite(p.z);
        private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
    }
}
