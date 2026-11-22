using System;
using System.Collections.Generic;
using System.Text;

namespace Emberdelve.Level
{
    /// <summary>
    /// Pure deterministic 3x3 planner. It uses a local xorshift stream and never touches UnityEngine.Random,
    /// so unrelated effects and loot rolls cannot change a map generated from the same seed.
    /// </summary>
    public static class DungeonModulePlanner
    {
        private static readonly DungeonModuleSockets[] Directions =
        {
            DungeonModuleSockets.North, DungeonModuleSockets.East,
            DungeonModuleSockets.South, DungeonModuleSockets.West
        };

        private static readonly int[] Dx = { 0, 1, 0, -1 };
        private static readonly int[] Dy = { 1, 0, -1, 0 };

        public static bool TryCreate(int seed, IReadOnlyList<DungeonModuleDefinition> modules, out DungeonModulePlan plan, out string reason)
        {
            plan = null;
            reason = string.Empty;
            if (modules == null || modules.Count == 0) { reason = "module catalog is empty"; return false; }
            if (modules.Count > ushort.MaxValue + 1) { reason = "module catalog exceeds the 16-bit wire limit"; return false; }

            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            HashSet<DungeonModuleKind> combatKinds = new HashSet<DungeonModuleKind>();
            int bridgeModules = 0;
            for (int i = 0; i < modules.Count; i++)
            {
                if (modules[i] == null) { reason = "module catalog contains a missing prefab"; return false; }
                if (!modules[i].HasValidSockets(out string validation))
                { reason = "module '" + modules[i].ModuleId + "' is invalid: " + validation; return false; }
                if (!ids.Add(modules[i].ModuleId)) { reason = "duplicate module id '" + modules[i].ModuleId + "'"; return false; }
                if (modules[i].WidthCells != DungeonModulePlan.ModuleWidthCells || modules[i].HeightCells != DungeonModulePlan.ModuleHeightCells)
                { reason = "module '" + modules[i].ModuleId + "' must use a " + DungeonModulePlan.ModuleWidthCells + "x" +
                    DungeonModulePlan.ModuleHeightCells + " cell footprint"; return false; }
                if (modules[i].Kind != DungeonModuleKind.Entry && modules[i].Kind != DungeonModuleKind.Extraction)
                    combatKinds.Add(modules[i].Kind);
                if (modules[i].Kind == DungeonModuleKind.Bridge) bridgeModules++;
            }
            if (combatKinds.Count < 2) { reason = "catalog needs at least two different combat-room kinds"; return false; }
            if (bridgeModules != 1) { reason = "catalog must contain exactly one dedicated bridge module for the center cell"; return false; }

            PlanRandom random = new PlanRandom(unchecked((uint)seed) ^ 0x9E3779B9u);
            bool[,] links = new bool[9, 9];
            BuildSpanningTree(ref random, links);
            AddOptionalLoops(ref random, links);
            // A bridge must connect its two walkable ends, not terminate at a sealed socket.
            // Keep existing routes; add an opposite pair only when the center has none.
            if (!(links[4, 1] && links[4, 7]) && !(links[4, 3] && links[4, 5]))
            {
                int first = random.Next(2) == 0 ? 1 : 3;
                int opposite = first == 1 ? 7 : 5;
                links[4, first] = links[first, 4] = true;
                links[4, opposite] = links[opposite, 4] = true;
            }

            DungeonModulePlacement[] placements = new DungeonModulePlacement[9];
            int[] kindUse = new int[Enum.GetValues(typeof(DungeonModuleKind)).Length];
            for (int index = 0; index < placements.Length; index++)
            {
                int x = index % DungeonModulePlan.Width;
                int y = index / DungeonModulePlan.Width;
                DungeonModuleSockets required = RequiredSockets(index, links);
                DungeonModuleRole role = index == 0 ? DungeonModuleRole.Start : index == 8 ? DungeonModuleRole.Extraction : DungeonModuleRole.Combat;
                DungeonModuleKind? requiredKind = index == 4 ? DungeonModuleKind.Bridge : (DungeonModuleKind?)null;
                if (!TryPickModule(ref random, modules, required, role, requiredKind, kindUse, out int moduleIndex, out byte rotation))
                {
                    reason = "no catalog module can satisfy sockets " + required + " for cell " + x + "," + y;
                    return false;
                }
                kindUse[(int)modules[moduleIndex].Kind]++;

                placements[index] = new DungeonModulePlacement
                {
                    X = x,
                    Y = y,
                    ModuleIndex = moduleIndex,
                    QuarterTurnsClockwise = rotation,
                    OpenSockets = required,
                    Role = role,
                    PlayerSpawnSlot = (byte)index
                };
            }

            HashSet<DungeonModuleKind> plannedCombatKinds = new HashSet<DungeonModuleKind>();
            for (int i = 1; i < placements.Length - 1; i++) plannedCombatKinds.Add(modules[placements[i].ModuleIndex].Kind);
            if (plannedCombatKinds.Count < 2) { reason = "generated plan has fewer than two combat-room kinds"; return false; }

            plan = new DungeonModulePlan { Seed = seed, Placements = placements, CatalogHash = ComputeCatalogHash(modules) };
            plan.LayoutHash = ComputeHashForWire(plan);
            if (!plan.Validate(out reason)) { plan = null; return false; }
            return true;
        }

        private static void BuildSpanningTree(ref PlanRandom random, bool[,] links)
        {
            bool[] visited = new bool[9];
            int[] stack = new int[9];
            int depth = 1;
            stack[0] = 0;
            visited[0] = true;
            while (depth > 0)
            {
                int current = stack[depth - 1];
                int[] candidates = new int[4];
                int count = Neighbors(current, visited, candidates);
                if (count == 0) { depth--; continue; }
                int next = candidates[random.Next(count)];
                links[current, next] = links[next, current] = true;
                visited[next] = true;
                stack[depth++] = next;
            }
        }

        private static void AddOptionalLoops(ref PlanRandom random, bool[,] links)
        {
            for (int i = 0; i < 9; i++)
            {
                for (int d = 0; d < 2; d++)
                {
                    int next = Neighbor(i, d);
                    if (next >= 0 && !links[i, next] && random.Next(100) < 28)
                        links[i, next] = links[next, i] = true;
                }
            }
        }

        private static int Neighbors(int index, bool[] visited, int[] result)
        {
            int count = 0;
            for (int d = 0; d < Directions.Length; d++)
            {
                int next = Neighbor(index, d);
                if (next >= 0 && !visited[next]) result[count++] = next;
            }
            return count;
        }

        private static int Neighbor(int index, int direction)
        {
            int x = index % DungeonModulePlan.Width + Dx[direction];
            int y = index / DungeonModulePlan.Width + Dy[direction];
            return x < 0 || x >= DungeonModulePlan.Width || y < 0 || y >= DungeonModulePlan.Height
                ? -1 : y * DungeonModulePlan.Width + x;
        }

        private static DungeonModuleSockets RequiredSockets(int index, bool[,] links)
        {
            DungeonModuleSockets sockets = DungeonModuleSockets.None;
            for (int d = 0; d < Directions.Length; d++)
            {
                int next = Neighbor(index, d);
                if (next >= 0 && links[index, next]) sockets |= Directions[d];
            }
            return sockets;
        }

        private static bool TryPickModule(ref PlanRandom random, IReadOnlyList<DungeonModuleDefinition> modules,
            DungeonModuleSockets required, DungeonModuleRole role, DungeonModuleKind? requiredKind,
            int[] kindUse, out int chosenIndex, out byte chosenRotation)
        {
            List<int> candidates = new List<int>();
            List<byte> rotations = new List<byte>();
            int leastUsedKind = int.MaxValue;
            for (int i = 0; i < modules.Count; i++)
            {
                for (byte rotation = 0; rotation < 4; rotation++)
                {
                    if (role == DungeonModuleRole.Start && modules[i].Kind != DungeonModuleKind.Entry) continue;
                    if (role == DungeonModuleRole.Extraction && modules[i].Kind != DungeonModuleKind.Extraction) continue;
                    if (role == DungeonModuleRole.Combat && (modules[i].Kind == DungeonModuleKind.Entry || modules[i].Kind == DungeonModuleKind.Extraction)) continue;
                    if (requiredKind.HasValue && modules[i].Kind != requiredKind.Value) continue;
                    if (!requiredKind.HasValue && modules[i].Kind == DungeonModuleKind.Bridge) continue;
                    if (modules[i].Kind == DungeonModuleKind.Bridge)
                    {
                        DungeonModuleSockets ends = Rotate(DungeonModuleSockets.North | DungeonModuleSockets.South, rotation);
                        if ((required & ends) != ends) continue;
                    }
                    if ((Rotate(modules[i].AvailableSockets, rotation) & required) == required)
                    {
                        int used = role == DungeonModuleRole.Combat ? kindUse[(int)modules[i].Kind] : 0;
                        if (used < leastUsedKind)
                        {
                            leastUsedKind = used;
                            candidates.Clear();
                            rotations.Clear();
                        }
                        if (used == leastUsedKind) { candidates.Add(i); rotations.Add(rotation); }
                    }
                }
            }

            if (candidates.Count == 0) { chosenIndex = -1; chosenRotation = 0; return false; }
            int pick = random.Next(candidates.Count);
            chosenIndex = candidates[pick];
            chosenRotation = rotations[pick];
            return true;
        }

        internal static DungeonModuleSockets Rotate(DungeonModuleSockets sockets, byte clockwiseTurns)
        {
            for (int turn = 0; turn < clockwiseTurns; turn++)
            {
                DungeonModuleSockets rotated = DungeonModuleSockets.None;
                if ((sockets & DungeonModuleSockets.North) != 0) rotated |= DungeonModuleSockets.East;
                if ((sockets & DungeonModuleSockets.East) != 0) rotated |= DungeonModuleSockets.South;
                if ((sockets & DungeonModuleSockets.South) != 0) rotated |= DungeonModuleSockets.West;
                if ((sockets & DungeonModuleSockets.West) != 0) rotated |= DungeonModuleSockets.North;
                sockets = rotated;
            }
            return sockets;
        }

        /// <summary>Maps a world-facing socket direction back into a module's local frame.</summary>
        public static DungeonModuleSockets ToLocalSocketDirection(DungeonModuleSockets worldDirection, byte clockwiseTurns)
        {
            if (clockwiseTurns > 3) throw new ArgumentOutOfRangeException(nameof(clockwiseTurns));
            return Rotate(worldDirection, (byte)((4 - clockwiseTurns) & 3));
        }

        internal static string ComputeHashForWire(DungeonModulePlan plan)
        {
            // Stable FNV-1a over protocol-relevant placement data; excludes Unity instance ids and prefab object references.
            uint hash = 2166136261u;
            void Add(byte value) { hash = (hash ^ value) * 16777619u; }
            Add((byte)plan.Seed); Add((byte)(plan.Seed >> 8)); Add((byte)(plan.Seed >> 16)); Add((byte)(plan.Seed >> 24));
            uint catalogHash = uint.Parse(plan.CatalogHash, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
            Add((byte)catalogHash); Add((byte)(catalogHash >> 8)); Add((byte)(catalogHash >> 16)); Add((byte)(catalogHash >> 24));
            for (int i = 0; i < plan.Placements.Length; i++)
            {
                DungeonModulePlacement p = plan.Placements[i];
                Add((byte)p.ModuleIndex); Add((byte)(p.ModuleIndex >> 8));
                Add(p.QuarterTurnsClockwise); Add((byte)p.OpenSockets); Add((byte)p.Role); Add(p.PlayerSpawnSlot);
            }
            // Legacy map fingerprints stay stable; Royale placement is a distinct acknowledged map.
            if (plan.RoyaleMode.HasValue) { Add(0x52); Add((byte)plan.RoyaleMode.Value); }
            return hash.ToString("X8");
        }

        internal static string ComputeCatalogHash(IReadOnlyList<DungeonModuleDefinition> modules)
        {
            uint hash = 2166136261u;
            void Add(byte value) { hash = (hash ^ value) * 16777619u; }
            for (int i = 0; i < modules.Count; i++)
            {
                byte[] id = Encoding.UTF8.GetBytes(modules[i].ModuleId);
                Add((byte)id.Length); Add((byte)(id.Length >> 8));
                for (int k = 0; k < id.Length; k++) Add(id[k]);
                Add((byte)modules[i].Kind);
                Add((byte)modules[i].AvailableSockets);
                Add((byte)modules[i].WidthCells); Add((byte)(modules[i].WidthCells >> 8));
                Add((byte)modules[i].HeightCells); Add((byte)(modules[i].HeightCells >> 8));
            }
            return hash.ToString("X8");
        }

        private struct PlanRandom
        {
            private uint _state;
            public PlanRandom(uint seed) { _state = seed == 0 ? 0xA341316Cu : seed; }
            private uint NextUInt()
            {
                uint x = _state;
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                _state = x;
                return x;
            }
            public int Next(int exclusiveMax) => exclusiveMax <= 1 ? 0 : (int)(NextUInt() % (uint)exclusiveMax);
        }
    }
}
