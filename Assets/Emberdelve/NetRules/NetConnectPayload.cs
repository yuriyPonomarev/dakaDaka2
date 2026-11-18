using System;
using System.Text;

namespace Emberdelve.Net.Rules
{
    /// <summary>
    /// Connection-approval payload sent by a client and the pure admission decision made by the server.
    /// The slot is a dev-scoped identity (not authentication); the class is only an id string that the server
    /// resolves against its own approved catalog. Nothing in the payload is a number the server trusts.
    /// </summary>
    public static class NetConnectPayload
    {
        public const int MaxBytes = 256;
        private const string Magic = "EMB";
        /// <summary>Longest slot / class id the server accepts (the encoder truncates to this).</summary>
        public const int MaxField = 32;

        public static byte[] Encode(int protocolVersion, string slot, string classId, string kit = "")
        {
            string text = Magic + "|" + protocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                          "|" + Clean(slot) + "|" + Clean(classId);
            // The chosen skill kit rides along as an optional fifth field; an older client simply sends none.
            string cleanKit = CleanKit(kit);
            if (cleanKit.Length > 0) text += "|" + cleanKit;
            return Encoding.UTF8.GetBytes(text);
        }

        /// <summary>Longest kit string accepted: two active ids, two passive ids, separators.</summary>
        public const int MaxKit = 120;

        /// <summary>Lowercase ids, digits, underscore, comma and semicolon only; anything else (or too long) is dropped, never trusted.</summary>
        public static string CleanKit(string kit)
        {
            if (string.IsNullOrEmpty(kit) || kit.Length > MaxKit) return "";
            for (int i = 0; i < kit.Length; i++)
            {
                char c = kit[i];
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == ',' || c == ';')) return "";
            }
            return kit;
        }

        public static bool TryDecode(byte[] payload, out int protocolVersion, out string slot, out string classId) =>
            TryDecode(payload, out protocolVersion, out slot, out classId, out _);

        public static bool TryDecode(byte[] payload, out int protocolVersion, out string slot, out string classId, out string kit)
        {
            kit = "";
            protocolVersion = -1;
            slot = "";
            classId = "";
            if (payload == null || payload.Length == 0 || payload.Length > MaxBytes) return false;

            string text;
            try { text = Encoding.UTF8.GetString(payload); }
            catch (Exception) { return false; }

            string[] parts = text.Split('|');
            if ((parts.Length != 4 && parts.Length != 5) || parts[0] != Magic) return false;
            if (!int.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out protocolVersion)) return false;

            slot = parts[2];
            classId = parts[3];
            if (parts.Length == 5) kit = CleanKit(parts[4]);
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
        /// <summary>The requested skill kit ("a1,a2;p1,p2"), sanitised; empty when none was sent. The server still validates it against the class pool.</summary>
        public readonly string Kit;

        public NetApprovalResult(bool approved, string reason, string slot, string classId, string kit = "")
        {
            Kit = kit ?? "";
            Approved = approved;
            Reason = reason;
            Slot = slot ?? "";
            ClassId = classId ?? "";
        }
    }

    /// <summary>
    /// The pure admission rule. Reasons are short stable strings (the client logs them from its disconnect reason):
    /// "bad payload", "protocol mismatch client=X server=Y", "unknown class", "duplicate slot", "server full".
    /// Check order: payload, protocol, class, duplicate slot, capacity. A refusal that is about the REQUEST
    /// (class, slot) therefore wins over a full server, so a full server still tells a bad client what is wrong with it.
    /// </summary>
    public static class NetApproval
    {
        public const string ReasonBadPayload = "bad payload";
        public const string ReasonProtocolMismatch = "protocol mismatch";
        public const string ReasonUnknownClass = "unknown class";
        public const string ReasonDuplicateSlot = "duplicate slot";
        public const string ReasonServerFull = "server full";

        /// <summary>Capacity and protocol only (slot / class are not checked). Kept for the U3A1 callers and tests.</summary>
        /// <param name="connectedCount">Players already admitted (not counting the requester).</param>
        public static NetApprovalResult Evaluate(byte[] payload, int serverProtocol, int connectedCount, int maxPlayers)
        {
            return Evaluate(payload, serverProtocol, connectedCount, maxPlayers, null, null);
        }

        /// <param name="connectedCount">Players already admitted or reserved (not counting the requester).</param>
        /// <param name="isSlotInUse">True when another connection already holds this slot (null = not checked).</param>
        /// <param name="isClassApproved">True when the server's catalog knows this class id (null = not checked).</param>
        public static NetApprovalResult Evaluate(byte[] payload, int serverProtocol, int connectedCount, int maxPlayers,
            Func<string, bool> isSlotInUse, Func<string, bool> isClassApproved)
        {
            if (payload == null || payload.Length == 0 || payload.Length > NetConnectPayload.MaxBytes)
                return new NetApprovalResult(false, ReasonBadPayload, "", "");
            if (!NetConnectPayload.TryDecode(payload, out int protocol, out string slot, out string classId, out string kit))
                return new NetApprovalResult(false, ReasonBadPayload, "", "");

            // A hostile payload must not reach the logs or the tables as anything but a short printable token.
            if (!IsCleanField(slot, true) || !IsCleanField(classId, false))
                return new NetApprovalResult(false, ReasonBadPayload, "", "");

            if (protocol != serverProtocol)
                return new NetApprovalResult(false,
                    ReasonProtocolMismatch + " client=" + protocol + " server=" + serverProtocol, slot, classId);
            if (isClassApproved != null && !isClassApproved(classId))
                return new NetApprovalResult(false, ReasonUnknownClass, slot, classId);
            if (isSlotInUse != null && isSlotInUse(slot))
                return new NetApprovalResult(false, ReasonDuplicateSlot, slot, classId);
            if (connectedCount >= maxPlayers)
                return new NetApprovalResult(false, ReasonServerFull, slot, classId);

            return new NetApprovalResult(true, "", slot, classId, kit);
        }

        /// <summary>Printable ASCII, at most MaxField chars; the slot may not be empty (the class may: the class rule rejects it).</summary>
        public static bool IsCleanField(string value, bool required)
        {
            if (string.IsNullOrEmpty(value)) return !required;
            if (value.Length > NetConnectPayload.MaxField) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c < 0x20 || c > 0x7E) return false;
            }

            return true;
        }
    }
}
