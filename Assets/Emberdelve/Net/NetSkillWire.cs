using System;
using Emberdelve.Net.Rules;
using Unity.Netcode;

namespace Emberdelve.Net
{
    /// <summary>
    /// U3B2, server to owner: the server's mana and cooldown picture of the owner's own player (see <see cref="SkillStateData"/>).
    /// The payload of an owner-read / server-write NetworkVariable (published a few times a second while anything changes) and part
    /// of every cast result. No identity of any kind. The IEquatable implementation lets the variable's change detection compare
    /// values without touching the bytes (the same pattern as MoveParamsWire).
    /// </summary>
    public struct SkillStateWire : INetworkSerializable, IEquatable<SkillStateWire>
    {
        public float Mana;
        public float MaxMana;
        public float Cooldown0;
        public float Cooldown1;
        public float Duration0;
        public float Duration1;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Mana);
            serializer.SerializeValue(ref MaxMana);
            serializer.SerializeValue(ref Cooldown0);
            serializer.SerializeValue(ref Cooldown1);
            serializer.SerializeValue(ref Duration0);
            serializer.SerializeValue(ref Duration1);
        }

        public static SkillStateWire From(in SkillStateData data)
        {
            return new SkillStateWire
            {
                Mana = data.Mana,
                MaxMana = data.MaxMana,
                Cooldown0 = data.Cooldown0,
                Cooldown1 = data.Cooldown1,
                Duration0 = data.Duration0,
                Duration1 = data.Duration1
            };
        }

        /// <summary>The value as pure data (re-sanitized: a NaN or negative number on the wire becomes 0).</summary>
        public SkillStateData ToData()
        {
            return new SkillStateData(Mana, MaxMana, Cooldown0, Cooldown1, Duration0, Duration1);
        }

        public bool Equals(SkillStateWire other) { return ToData().Equals(other.ToData()); }

        public override bool Equals(object obj) { return obj is SkillStateWire other && Equals(other); }

        public override int GetHashCode() { return ToData().GetHashCode(); }
    }

    /// <summary>
    /// U3B2, server to owner, reliable: the answer to ONE skill request that passed the wire gate (requests the gate rejected get no
    /// answer: replying to a flood would only amplify it). Carries the request's sequence number so the client can match it, what
    /// the server decided (cast or refused, with the reason code) and the state right after that decision: mana and per-slot
    /// cooldowns, exactly the server's numbers. The skill id is the one the server resolved for the slot (a label for the HUD).
    /// </summary>
    public struct SkillResultWire : INetworkSerializable
    {
        public uint Sequence;
        public byte Slot;
        public byte Outcome;
        public byte Reason;
        public SkillStateWire State;
        public string SkillId;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            if (serializer.IsWriter && SkillId == null) SkillId = "";
            serializer.SerializeValue(ref Sequence);
            serializer.SerializeValue(ref Slot);
            serializer.SerializeValue(ref Outcome);
            serializer.SerializeValue(ref Reason);
            State.NetworkSerialize(serializer);
            serializer.SerializeValue(ref SkillId);
        }

        public SkillCastOutcome OutcomeKind { get { return Outcome == (byte)SkillCastOutcome.Cast ? SkillCastOutcome.Cast : SkillCastOutcome.Refused; } }

        public SkillRefusalCode ReasonCode { get { return Reason <= (byte)SkillRefusalCode.ClientRole ? (SkillRefusalCode)Reason : SkillRefusalCode.Failed; } }
    }

    /// <summary>
    /// U3B2, server to every client but the owner, reliable: "this player's object just cast this skill", so the observer can show the
    /// effect presentation. The caster is the object the call is made on. No outcome of any kind: nothing a client does with this
    /// changes gameplay (health, mana and cooldown never travel here).
    /// </summary>
    public struct SkillVisualWire : INetworkSerializable
    {
        public byte Slot;
        public string SkillId;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            if (serializer.IsWriter && SkillId == null) SkillId = "";
            serializer.SerializeValue(ref Slot);
            serializer.SerializeValue(ref SkillId);
        }
    }
}
