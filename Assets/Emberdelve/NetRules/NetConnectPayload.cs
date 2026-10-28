using System;
using System.Text;

namespace Emberdelve.Net.Rules
{
    /// <summary>
    /// Connection-approval payload sent by a client and the pure admission decision made by the server.
    /// U3A1 is a stub: protocol version, size and capacity only. The slot and class strings are carried
    /// but not trusted or validated yet (U3A3 validates them against the server's catalog; the slot is a
    /// dev-scoped identity, not authentication).
    /// </summary>
    public static class NetConnectPayload
    {
        public const int MaxBytes = 256;
        private const string Magic = "EMB";
        private const int MaxField = 32;

        public static byte[] Encode(int protocolVersion, string slot, string classId)
        {
            string text = Magic + "|" + protocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                          "|" + Clean(slot) + "|" + Clean(classId);
            return Encoding.UTF8.GetBytes(text);
        }

        public static bool TryDecode(byte[] payload, out int protocolVersion, out string slot, out string classId)
        {
            protocolVersion = -1;
            slot = "";
            classId = "";
            if (payload == null || payload.Length == 0 || payload.Length > MaxBytes) return false;

            string text;
            try { text = Encoding.UTF8.GetString(payload); }
            catch (Exception) { return false; }

            string[] parts = text.Split('|');
            if (parts.Length != 4 || parts[0] != Magic) return false;
            if (!int.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out protocolVersion)) return false;

            slot = parts[2];
            classId = parts[3];
            return true;
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            value = value.Replace('|', '_');
            return value.Length > MaxField ? value.Substring(0, MaxField) : value;
        }
    }

    public readonly struct NetApprovalResult
    {
        public readonly bool Approved;
        public readonly string Reason;
        public readonly string Slot;
        public readonly string ClassId;

        public NetApprovalResult(bool approved, string reason, string slot, string classId)
        {
            Approved = approved;
            Reason = reason;
            Slot = slot ?? "";
            ClassId = classId ?? "";
        }
    }

    public static class NetApproval
    {
        /// <param name="connectedCount">Players already admitted (not counting the requester).</param>
        public static NetApprovalResult Evaluate(byte[] payload, int serverProtocol, int connectedCount, int maxPlayers)
        {
            if (payload == null || payload.Length == 0)
                return new NetApprovalResult(false, "payload missing", "", "");
            if (payload.Length > NetConnectPayload.MaxBytes)
                return new NetApprovalResult(false, "payload too large", "", "");
            if (!NetConnectPayload.TryDecode(payload, out int protocol, out string slot, out string classId))
                return new NetApprovalResult(false, "payload malformed", "", "");
            if (protocol != serverProtocol)
                return new NetApprovalResult(false,
                    "protocol mismatch client=" + protocol + " server=" + serverProtocol, slot, classId);
            if (connectedCount >= maxPlayers)
                return new NetApprovalResult(false, "server full", slot, classId);

            return new NetApprovalResult(true, "", slot, classId);
        }
    }
}
