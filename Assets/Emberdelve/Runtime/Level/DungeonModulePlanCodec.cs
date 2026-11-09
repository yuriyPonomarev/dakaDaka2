using System;
using System.IO;
using System.Collections.Generic;

namespace Emberdelve.Level
{
    /// <summary>
    /// Bounded, versioned snapshot codec for server-to-client plan handoff. The payload identifies catalog entries by stable
    /// catalog index; each process resolves those entries locally and rejects missing or incompatible authored prefabs.
    /// </summary>
    public static class DungeonModulePlanCodec
    {
        private const byte Version = 1;
        private const int HeaderBytes = 1 + 4 + 4 + 1;
        private const int PlacementBytes = 2 + 1 + 1 + 1 + 1;
        private const int HashBytes = 4;
        public const int PayloadBytes = HeaderBytes + DungeonModulePlan.Width * DungeonModulePlan.Height * PlacementBytes + HashBytes;

        public static byte[] Encode(DungeonModulePlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (!plan.Validate(out string reason)) throw new ArgumentException("Cannot encode invalid plan: " + reason, nameof(plan));
            string expectedHash = DungeonModulePlanner.ComputeHashForWire(plan);
            if (!string.Equals(plan.LayoutHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Cannot encode plan with a stale layout hash.", nameof(plan));

            using (MemoryStream stream = new MemoryStream(PayloadBytes))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(Version);
                writer.Write(plan.Seed);
                writer.Write(Convert.ToUInt32(plan.CatalogHash, 16));
                writer.Write((byte)plan.Placements.Length);
                for (int i = 0; i < plan.Placements.Length; i++)
                {
                    DungeonModulePlacement p = plan.Placements[i];
                    if (p.ModuleIndex < 0 || p.ModuleIndex > ushort.MaxValue)
                        throw new ArgumentOutOfRangeException(nameof(plan), "Catalog index exceeds the wire format.");
                    writer.Write((ushort)p.ModuleIndex);
                    writer.Write(p.QuarterTurnsClockwise);
                    writer.Write((byte)p.OpenSockets);
                    writer.Write((byte)p.Role);
                    writer.Write(p.PlayerSpawnSlot);
                }
                writer.Write(Convert.ToUInt32(plan.LayoutHash, 16));
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static bool TryDecode(byte[] payload, System.Collections.Generic.IReadOnlyList<DungeonModuleDefinition> modules,
            out DungeonModulePlan plan, out string reason)
        {
            plan = null;
            reason = string.Empty;
            if (modules == null) { reason = "module catalog is missing"; return false; }
            if (modules.Count == 0 || modules.Count > ushort.MaxValue + 1) { reason = "module catalog size is invalid"; return false; }
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < modules.Count; i++)
            {
                if (modules[i] == null || !modules[i].HasValidSockets(out _)) { reason = "module catalog contains an invalid entry"; return false; }
                if (!ids.Add(modules[i].ModuleId)) { reason = "module catalog contains duplicate module ids"; return false; }
                if (modules[i].WidthCells != DungeonModulePlan.ModuleWidthCells || modules[i].HeightCells != DungeonModulePlan.ModuleHeightCells)
                { reason = "module footprint differs from the 12x12 contract"; return false; }
            }
            if (payload == null || payload.Length != PayloadBytes) { reason = "payload has the wrong length"; return false; }
            try
            {
                using (MemoryStream stream = new MemoryStream(payload, false))
                using (BinaryReader reader = new BinaryReader(stream))
                {
                    if (reader.ReadByte() != Version) { reason = "unsupported module-plan version"; return false; }
                    int seed = reader.ReadInt32();
                    uint transmittedCatalogHash = reader.ReadUInt32();
                    string localCatalogHash = DungeonModulePlanner.ComputeCatalogHash(modules);
                    if (!uint.TryParse(localCatalogHash, System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out uint parsedCatalogHash) || parsedCatalogHash != transmittedCatalogHash)
                    { reason = "module catalog differs from the server catalog"; return false; }
                    if (reader.ReadByte() != DungeonModulePlan.Width * DungeonModulePlan.Height)
                    { reason = "module count is not nine"; return false; }

                    DungeonModulePlacement[] placements = new DungeonModulePlacement[9];
                    HashSet<DungeonModuleKind> plannedKinds = new HashSet<DungeonModuleKind>();
                    for (int i = 0; i < placements.Length; i++)
                    {
                        ushort moduleIndex = reader.ReadUInt16();
                        byte rotation = reader.ReadByte();
                        byte sockets = reader.ReadByte();
                        byte role = reader.ReadByte();
                        byte spawnSlot = reader.ReadByte();
                        if (moduleIndex >= modules.Count || modules[moduleIndex] == null) { reason = "module catalog index is unavailable"; return false; }
                        if (!modules[moduleIndex].HasValidSockets(out string moduleReason))
                        { reason = "module catalog entry is invalid: " + moduleReason; return false; }
                        if ((i == 0 && modules[moduleIndex].Kind != DungeonModuleKind.Entry) ||
                            (i == 8 && modules[moduleIndex].Kind != DungeonModuleKind.Extraction) ||
                            (i > 0 && i < 8 && (modules[moduleIndex].Kind == DungeonModuleKind.Entry || modules[moduleIndex].Kind == DungeonModuleKind.Extraction)))
                        { reason = "module kind does not match the required map role"; return false; }
                        if (i > 0 && i < 8) plannedKinds.Add(modules[moduleIndex].Kind);
                        if (rotation > 3 || (sockets & ~((byte)DungeonModuleSockets.All)) != 0 || role > (byte)DungeonModuleRole.Extraction)
                        { reason = "placement contains invalid enum data"; return false; }
                        placements[i] = new DungeonModulePlacement
                        {
                            X = i % DungeonModulePlan.Width,
                            Y = i / DungeonModulePlan.Width,
                            ModuleIndex = moduleIndex,
                            QuarterTurnsClockwise = rotation,
                            OpenSockets = (DungeonModuleSockets)sockets,
                            Role = (DungeonModuleRole)role,
                            PlayerSpawnSlot = spawnSlot
                        };
                        DungeonModuleSockets available = DungeonModulePlanner.Rotate(modules[moduleIndex].AvailableSockets, rotation);
                        if ((available & placements[i].OpenSockets) != placements[i].OpenSockets)
                        { reason = "module does not provide its planned sockets"; return false; }
                    }

                    uint transmittedHash = reader.ReadUInt32();
                    if (plannedKinds.Count < 2) { reason = "plan lacks room variety"; return false; }
                    plan = new DungeonModulePlan { Seed = seed, Placements = placements, CatalogHash = localCatalogHash };
                    plan.LayoutHash = DungeonModulePlanner.ComputeHashForWire(plan);
                    if (!plan.Validate(out reason)) { plan = null; return false; }
                    if (!uint.TryParse(plan.LayoutHash, System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out uint calculatedHash) || calculatedHash != transmittedHash)
                    { plan = null; reason = "plan hash mismatch"; return false; }
                    return true;
                }
            }
            catch (Exception e) when (e is EndOfStreamException || e is IOException || e is ArgumentException || e is FormatException)
            {
                plan = null;
                reason = "malformed module-plan payload";
                return false;
            }
        }
    }
}
