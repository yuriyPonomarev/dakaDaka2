using System;

namespace Emberdelve.Net.Rules
{
    /// <summary>Exact fixed-width wire format for the modular-map readiness ACK.</summary>
    public static class ModulePlanAckCodec
    {
        public const int PayloadBytes = sizeof(uint) + sizeof(byte);

        public static byte[] Encode(uint layoutHash, bool accepted)
        {
            return new[]
            {
                (byte)layoutHash,
                (byte)(layoutHash >> 8),
                (byte)(layoutHash >> 16),
                (byte)(layoutHash >> 24),
                (byte)(accepted ? 1 : 0)
            };
        }

        public static bool TryDecode(byte[] payload, out uint layoutHash, out bool accepted, out string reason)
        {
            layoutHash = 0;
            accepted = false;
            reason = string.Empty;
            if (payload == null || payload.Length != PayloadBytes)
            { reason = "ACK payload must be exactly " + PayloadBytes + " bytes"; return false; }
            if (payload[4] > 1)
            { reason = "ACK accepted flag must be 0 or 1"; return false; }

            layoutHash = (uint)payload[0] |
                         ((uint)payload[1] << 8) |
                         ((uint)payload[2] << 16) |
                         ((uint)payload[3] << 24);
            accepted = payload[4] == 1;
            return true;
        }
    }
}
