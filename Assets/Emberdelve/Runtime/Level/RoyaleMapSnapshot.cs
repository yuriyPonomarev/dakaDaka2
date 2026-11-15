using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Emberdelve.Level
{
    /// <summary>Immutable destination geometry identity. Origin is part of the acknowledged hash.</summary>
    public sealed class RoyaleMapSnapshot
    {
        public const int PayloadBytes = 1 + 1 + 4 + 12 + DungeonModulePlanCodec.PayloadBytes;
        public const int AckBytes = 6;
        public readonly int Floor;
        public readonly uint InitialHash, Hash;
        public readonly Vector3 Origin;
        private readonly DungeonModulePlan _plan;
        private readonly byte[] _payload;
        public DungeonModulePlan CopyPlan() => _plan.WithRoyaleMode(_plan.RoyaleMode);
        public byte[] Encode() => (byte[])_payload.Clone();
        private RoyaleMapSnapshot(int floor, uint initialHash, Vector3 origin, DungeonModulePlan plan)
        {
            Floor = floor; InitialHash = initialHash; Origin = origin; _plan = plan.WithRoyaleMode(plan.RoyaleMode);
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1); writer.Write((byte)floor); writer.Write(initialHash);
                writer.Write(origin.x); writer.Write(origin.y); writer.Write(origin.z);
                writer.Write(DungeonModulePlanCodec.Encode(_plan)); _payload = stream.ToArray();
            }
            uint hash = 2166136261;
            foreach (byte value in _payload) hash = unchecked((hash ^ value) * 16777619);
            Hash = hash;
        }
        private static bool Valid(int floor, Vector3 origin) => floor >= 2 && floor <= 3
            && float.IsFinite(origin.x) && float.IsFinite(origin.y) && float.IsFinite(origin.z)
            && Mathf.Abs(origin.x) <= 100000 && Mathf.Abs(origin.y) <= 10000 && Mathf.Abs(origin.z) <= 100000;
        public static bool TryCreate(int floor, string initialHash, Vector3 origin, DungeonModulePlan plan,
            out RoyaleMapSnapshot snapshot)
        {
            snapshot = null;
            if (!Valid(floor, origin) || plan == null || !plan.RoyaleMode.HasValue || !plan.Validate(out _)
                || !uint.TryParse(initialHash, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out uint hash)) return false;
            snapshot = new RoyaleMapSnapshot(floor, hash, origin, plan); return true;
        }
        public static bool TryDecode(byte[] bytes, out RoyaleMapSnapshot snapshot, out string reason)
        {
            snapshot = null; reason = "Invalid destination map payload";
            if (bytes == null || bytes.Length != PayloadBytes || bytes[0] != 1 || bytes[1] < 2 || bytes[1] > 3) return false;
            if (!RoyaleFloorCatalogs.TryLoad(bytes[1], out var catalog, out reason)) return false;
            return TryDecode(bytes, catalog.Modules, out snapshot, out reason);
        }

        public static bool TryDecode(byte[] bytes, IReadOnlyList<DungeonModuleDefinition> modules,
            out RoyaleMapSnapshot snapshot, out string reason)
        {
            snapshot = null; reason = "Invalid destination map payload";
            if (bytes == null || bytes.Length != PayloadBytes) return false;
            using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadByte() != 1) return false;
                int floor = reader.ReadByte(); uint initial = reader.ReadUInt32();
                var origin = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                if (!Valid(floor, origin)) return false;
                if (!DungeonModulePlanCodec.TryDecode(reader.ReadBytes(DungeonModulePlanCodec.PayloadBytes), modules, out var plan, out reason)
                    || !plan.RoyaleMode.HasValue) return false;
                snapshot = new RoyaleMapSnapshot(floor, initial, origin, plan); reason = null; return true;
            }
        }
        public byte[] Acknowledge(bool accepted)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            { writer.Write((byte)Floor); writer.Write(Hash); writer.Write((byte)(accepted ? 1 : 0)); return stream.ToArray(); }
        }
        public bool Accepts(byte[] ack) => ack != null && ack.Length == AckBytes && ack[0] == Floor
            && ack[1] == (byte)Hash && ack[2] == (byte)(Hash >> 8) && ack[3] == (byte)(Hash >> 16)
            && ack[4] == (byte)(Hash >> 24) && ack[5] == 1;
    }
}
