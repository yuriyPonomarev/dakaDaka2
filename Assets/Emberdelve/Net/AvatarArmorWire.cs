using System;
using Emberdelve.Data;
using Emberdelve.Items;
using Unity.Collections;
using Unity.Netcode;

namespace Emberdelve.Net
{
    /// <summary>Retained, server-written cosmetic item IDs. No inventory quantities, affixes or authority.</summary>
    public struct AvatarArmorWire : INetworkSerializable, IEquatable<AvatarArmorWire>
    {
        public FixedString64Bytes Head, Chest, Legs, Hands, Feet, Back;
        public const int MaxIdLength = 61;
        public bool IsValid => ValidId(Head.ToString()) && ValidId(Chest.ToString()) && ValidId(Legs.ToString())
            && ValidId(Hands.ToString()) && ValidId(Feet.ToString()) && ValidId(Back.ToString());

        private static bool ValidId(string id)
        {
            if (string.IsNullOrEmpty(id)) return true;
            if (id.Length > MaxIdLength) return false;
            foreach (char c in id)
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.')) return false;
            return true;
        }
        // Invalid authored IDs are absent cosmetics, never an exception on the server update loop.
        public static FixedString64Bytes SafeId(string id) => ValidId(id) && !string.IsNullOrEmpty(id) ? new FixedString64Bytes(id) : default;
        public static AvatarArmorWire Capture(Equipment equipment)
        {
            if (equipment == null) return default;
            return new AvatarArmorWire
            {
                Head = SafeId(equipment.Equipped(EquipmentSlot.Head)?.itemId),
                Chest = SafeId(equipment.Equipped(EquipmentSlot.Chest)?.itemId),
                Legs = SafeId(equipment.Equipped(EquipmentSlot.Legs)?.itemId),
                Hands = SafeId(equipment.Equipped(EquipmentSlot.Hands)?.itemId),
                Feet = SafeId(equipment.Equipped(EquipmentSlot.Feet)?.itemId),
                Back = SafeId(equipment.Equipped(EquipmentSlot.Back)?.itemId)
            };
        }
        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Head); serializer.SerializeValue(ref Chest); serializer.SerializeValue(ref Legs);
            serializer.SerializeValue(ref Hands); serializer.SerializeValue(ref Feet); serializer.SerializeValue(ref Back);
        }
        public bool Equals(AvatarArmorWire other) => Head.Equals(other.Head) && Chest.Equals(other.Chest) && Legs.Equals(other.Legs)
            && Hands.Equals(other.Hands) && Feet.Equals(other.Feet) && Back.Equals(other.Back);
    }
}
