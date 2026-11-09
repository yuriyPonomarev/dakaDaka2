using System;
using Emberdelve.Items;
using Unity.Netcode;

namespace Emberdelve.Net
{
    /// <summary>Owner-only server snapshot; includes session stack IDs in addition to the persistent loadout.</summary>
    public struct NetworkLoadoutWire : INetworkSerializable
    {
        public ulong Revision;
        public string LoadoutJson;
        public NetworkLoadoutState.StackIdentity[] Stacks;

        public static NetworkLoadoutWire FromState(NetworkLoadoutState state) => new NetworkLoadoutWire
        {
            Revision = state.Revision, LoadoutJson = state.Loadout.ToJson(), Stacks = state.Stacks
        };

        public bool TryToState(out NetworkLoadoutState state, out string reason)
        {
            state = null;
            if (Stacks == null || Stacks.Length > ContainerSnapshot.MaxEntries)
            { reason = "invalid stack identity count"; return false; }
            if (!LoadoutSnapshot.TryFromJson(LoadoutJson, out var loadout, out reason)) return false;
            state = new NetworkLoadoutState { Loadout = loadout, Revision = Revision,
                Stacks = (NetworkLoadoutState.StackIdentity[])Stacks.Clone() };
            return true;
        }

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Revision);
            if (serializer.IsWriter && LoadoutJson == null) LoadoutJson = "";
            serializer.SerializeValue(ref LoadoutJson);
            int count = serializer.IsWriter ? (Stacks == null ? 0 : Stacks.Length) : 0;
            serializer.SerializeValue(ref count);
            if (count < 0 || count > ContainerSnapshot.MaxEntries) throw new InvalidOperationException("Invalid network inventory stack count");
            if (serializer.IsReader) Stacks = new NetworkLoadoutState.StackIdentity[count];
            for (int i = 0; i < count; i++)
            {
                var stack = Stacks[i];
                serializer.SerializeValue(ref stack.X);
                serializer.SerializeValue(ref stack.Y);
                serializer.SerializeValue(ref stack.Id);
                if (serializer.IsReader) Stacks[i] = stack;
            }
        }
    }
}
