using System.Text;
using Emberdelve.Core;
using Unity.Collections;
using Unity.Netcode;

namespace Emberdelve.Net
{
    /// <summary>Exact server-authored per-player result payload. It carries no commands or payout authority.</summary>
    public struct RunResultWire : INetworkSerializable
    {
        public byte Outcome;
        public FixedString128Bytes ClassName;
        public float LootValue;
        public int ItemsCollected;
        public int EnemiesSlain;
        public float DurationSeconds;
        public int PlayerId;
        public int GearScore;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Outcome);
            serializer.SerializeValue(ref ClassName);
            serializer.SerializeValue(ref LootValue);
            serializer.SerializeValue(ref ItemsCollected);
            serializer.SerializeValue(ref EnemiesSlain);
            serializer.SerializeValue(ref DurationSeconds);
            serializer.SerializeValue(ref PlayerId);
            serializer.SerializeValue(ref GearScore);
        }

        public static bool TryCreate(in RunResult result, out RunResultWire wire)
        {
            wire = default;
            if ((result.Outcome != RunState.Dead && result.Outcome != RunState.Extracted) || result.PlayerId <= 0 ||
                !IsFinite(result.LootValue) || result.LootValue < 0f || result.ItemsCollected < 0 ||
                result.EnemiesSlain < 0 || !IsFinite(result.DurationSeconds) || result.DurationSeconds < 0f || result.GearScore < 0)
                return false;

            string className = result.ClassName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(className) || Encoding.UTF8.GetByteCount(className) > 120) return false;

            wire = new RunResultWire
            {
                Outcome = (byte)result.Outcome,
                ClassName = new FixedString128Bytes(className),
                LootValue = result.LootValue,
                ItemsCollected = result.ItemsCollected,
                EnemiesSlain = result.EnemiesSlain,
                DurationSeconds = result.DurationSeconds,
                PlayerId = result.PlayerId,
                GearScore = result.GearScore
            };
            return true;
        }

        public bool TryToRunResult(int expectedPlayerId, out RunResult result)
        {
            result = default;
            if (expectedPlayerId <= 0 || PlayerId != expectedPlayerId ||
                (Outcome != (byte)RunState.Dead && Outcome != (byte)RunState.Extracted) ||
                !IsFinite(LootValue) || LootValue < 0f || ItemsCollected < 0 || EnemiesSlain < 0 ||
                !IsFinite(DurationSeconds) || DurationSeconds < 0f || GearScore < 0)
                return false;

            string className = ClassName.ToString();
            if (string.IsNullOrWhiteSpace(className)) return false;
            result = new RunResult((RunState)Outcome, className, LootValue, ItemsCollected, EnemiesSlain,
                DurationSeconds, PlayerId, GearScore);
            return true;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
