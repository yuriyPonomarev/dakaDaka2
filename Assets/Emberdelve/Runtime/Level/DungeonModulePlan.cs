using System;
using System.Collections.Generic;
using Emberdelve.Rules;

namespace Emberdelve.Level
{
    public enum DungeonModuleRole : byte
    {
        Combat,
        Start,
        Extraction
    }

    [Serializable]
    public struct DungeonModulePlacement
    {
        public int X;
        public int Y;
        public int ModuleIndex;
        public byte QuarterTurnsClockwise;
        public DungeonModuleSockets OpenSockets;
        public DungeonModuleRole Role;
        /// <summary>Stable legacy anchor number; Royale party clusters are derived separately from the plan mode.</summary>
        public byte PlayerSpawnSlot;
    }

    /// <summary>A deterministic 3x3 logical map. It contains references only on the authority/editor side;
    /// network payloads should carry Seed + ModuleIndex + rotation + sockets + role + spawn slot, never Unity objects.</summary>
    [Serializable]
    public sealed class DungeonModulePlan
    {
        public const int Width = 3;
        public const int Height = 3;
        public const int ModuleWidthCells = 12;
        public const int ModuleHeightCells = 12;

        public RoyalePartyMode? RoyaleMode { get; internal set; }
        public int Seed { get; internal set; }

        public DungeonModulePlan WithRoyaleMode(RoyalePartyMode? mode)
        {
            if (mode.HasValue && (mode.Value < RoyalePartyMode.Solo || mode.Value > RoyalePartyMode.Trio))
                throw new ArgumentOutOfRangeException(nameof(mode));
            if (!Validate(out string reason)) throw new InvalidOperationException(reason);
            var copy = new DungeonModulePlan { Seed = Seed, CatalogHash = CatalogHash,
                Placements = (DungeonModulePlacement[])Placements.Clone(), RoyaleMode = mode };
            copy.LayoutHash = DungeonModulePlanner.ComputeHashForWire(copy);
            return copy;
        }
        public string CatalogHash { get; internal set; }
        public DungeonModulePlacement[] Placements { get; internal set; }
        public string LayoutHash { get; internal set; }

        public DungeonModulePlacement At(int x, int y)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height) throw new ArgumentOutOfRangeException();
            return Placements[y * Width + x];
        }

        public bool Validate(out string reason)
        {
            reason = string.Empty;
            if (RoyaleMode.HasValue && (RoyaleMode.Value < RoyalePartyMode.Solo || RoyaleMode.Value > RoyalePartyMode.Trio))
            { reason = "invalid Royale party mode"; return false; }
            if (Placements == null || Placements.Length != Width * Height) { reason = "plan must contain exactly nine modules"; return false; }
            if (!uint.TryParse(CatalogHash, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out _)) { reason = "plan has no valid catalog fingerprint"; return false; }

            int starts = 0;
            int extractions = 0;
            bool[] spawnSlots = new bool[Width * Height];
            for (int i = 0; i < Placements.Length; i++)
            {
                DungeonModulePlacement p = Placements[i];
                if (p.X != i % Width || p.Y != i / Width) { reason = "placements must be in canonical row-major order"; return false; }
                DungeonModuleRole expectedRole = i == 0 ? DungeonModuleRole.Start : i == Placements.Length - 1
                    ? DungeonModuleRole.Extraction : DungeonModuleRole.Combat;
                if (p.Role != expectedRole) { reason = "start and extraction must occupy opposite corners; all other cells are combat rooms"; return false; }
                if (p.ModuleIndex < 0 || p.QuarterTurnsClockwise > 3 || p.Role > DungeonModuleRole.Extraction ||
                    (((byte)p.OpenSockets) & ~((byte)DungeonModuleSockets.All)) != 0)
                { reason = "placement contains invalid module, rotation, role, or socket data"; return false; }
                starts += p.Role == DungeonModuleRole.Start ? 1 : 0;
                extractions += p.Role == DungeonModuleRole.Extraction ? 1 : 0;
                if (p.PlayerSpawnSlot >= spawnSlots.Length || spawnSlots[p.PlayerSpawnSlot])
                { reason = "player spawn slots must uniquely cover 0 through 8"; return false; }
                spawnSlots[p.PlayerSpawnSlot] = true;

                if (p.X == 0 && Has(p.OpenSockets, DungeonModuleSockets.West) ||
                    p.X == Width - 1 && Has(p.OpenSockets, DungeonModuleSockets.East) ||
                    p.Y == 0 && Has(p.OpenSockets, DungeonModuleSockets.South) ||
                    p.Y == Height - 1 && Has(p.OpenSockets, DungeonModuleSockets.North))
                { reason = "module opens onto the outside of the 3x3 map"; return false; }

                if (p.X + 1 < Width && Has(p.OpenSockets, DungeonModuleSockets.East) != Has(Placements[i + 1].OpenSockets, DungeonModuleSockets.West))
                { reason = "east/west sockets do not match"; return false; }
                if (p.Y + 1 < Height && Has(p.OpenSockets, DungeonModuleSockets.North) != Has(Placements[i + Width].OpenSockets, DungeonModuleSockets.South))
                { reason = "north/south sockets do not match"; return false; }
            }

            if (starts != 1 || extractions != 1) { reason = "plan must contain one start and one extraction module"; return false; }
            if (Placements[0].Role != DungeonModuleRole.Start || Placements[Placements.Length - 1].Role != DungeonModuleRole.Extraction)
            { reason = "start and extraction modules must occupy opposite corners"; return false; }
            for (int i = 0; i < spawnSlots.Length; i++) if (!spawnSlots[i]) { reason = "plan is missing player spawn slot " + i; return false; }
            if (!AllReachable()) { reason = "module graph is disconnected"; return false; }
            return true;
        }

        private bool AllReachable()
        {
            bool[] seen = new bool[Placements.Length];
            Queue<int> queue = new Queue<int>();
            queue.Enqueue(Array.FindIndex(Placements, p => p.Role == DungeonModuleRole.Start));
            seen[queue.Peek()] = true;
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                int x = i % Width;
                int y = i / Width;
                Visit(i, x, y + 1, DungeonModuleSockets.North, DungeonModuleSockets.South, seen, queue);
                Visit(i, x + 1, y, DungeonModuleSockets.East, DungeonModuleSockets.West, seen, queue);
                Visit(i, x, y - 1, DungeonModuleSockets.South, DungeonModuleSockets.North, seen, queue);
                Visit(i, x - 1, y, DungeonModuleSockets.West, DungeonModuleSockets.East, seen, queue);
            }

            for (int i = 0; i < seen.Length; i++) if (!seen[i]) return false;
            return true;
        }

        private void Visit(int from, int x, int y, DungeonModuleSockets outSocket, DungeonModuleSockets inSocket, bool[] seen, Queue<int> queue)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height) return;
            int next = y * Width + x;
            if (seen[next] || !Has(Placements[from].OpenSockets, outSocket) || !Has(Placements[next].OpenSockets, inSocket)) return;
            seen[next] = true;
            queue.Enqueue(next);
        }

        internal static bool Has(DungeonModuleSockets value, DungeonModuleSockets flag) => (value & flag) != 0;
    }
}
