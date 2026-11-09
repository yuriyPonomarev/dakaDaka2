using System.Collections.Generic;
using System.Globalization;
using Emberdelve.Level;
using Emberdelve.Core;
using Emberdelve.Interaction;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>Server-only world decisions. Repeated bounded full snapshots handle late module assembly/join.</summary>
    public sealed class NetBreakableWorld : MonoBehaviour
    {
        private const string Message = "ember.world.breakables.v1";
        private const int MaxBroken = 2048;
        private NetworkManager _network;
        private bool _registered;
        private float _nextSend;
        private readonly HashSet<ulong> _broken = new HashSet<ulong>();
        public void Configure(NetworkManager network)
        {
            _network=network;
            DungeonModulePlanSession.Changed+=OnPlanChanged;
        }
        private void OnPlanChanged(DungeonModulePlan plan) { _broken.Clear(); _nextSend=0f; }

        private void Update()
        {
            if (_network==null || !_network.IsListening || _network.CustomMessagingManager==null) return;
            var plan=DungeonModulePlanSession.Current;
            if(plan==null || !uint.TryParse(plan.LayoutHash, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint layoutHash)) return;
            if (!_network.IsServer)
            {
                if (!_registered)
                {
                    _network.CustomMessagingManager.RegisterNamedMessageHandler(Message, Receive);
                    _registered=true;
                }
                // A snapshot can arrive before local module prefabs finish instantiating.
                foreach(var barrel in BreakableBarrel.Active)
                    if (barrel!=null && !barrel.IsBroken && _broken.Contains(barrel.StableId)) barrel.ApplyReplicatedBroken();
                return;
            }
            if (Time.unscaledTime<_nextSend) return;
            _nextSend=Time.unscaledTime+.25f;
            _broken.Clear();
            foreach(var barrel in BreakableBarrel.Active)
                if (barrel!=null && barrel.IsBroken) _broken.Add(barrel.StableId);
            if (_broken.Count>MaxBroken) { Debug.LogError("Breakable world exceeds snapshot capacity"); return; }
            using(var writer=new FastBufferWriter(12+_broken.Count*8, Allocator.Temp))
            {
                writer.WriteValueSafe(plan.Seed);
                writer.WriteValueSafe(layoutHash);
                writer.WriteValueSafe(_broken.Count);
                foreach(ulong id in _broken) writer.WriteValueSafe(id);
                foreach(ulong client in _network.ConnectedClientsIds)
                    if(client!=NetworkManager.ServerClientId)
                        _network.CustomMessagingManager.SendNamedMessage(Message, client, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private void Receive(ulong sender, FastBufferReader reader)
        {
            if (_network==null || _network.IsServer || sender!=NetworkManager.ServerClientId || !NetRole.IsClientOnly) return;
            if (!reader.TryBeginRead(12)) return;
            reader.ReadValueSafe(out int seed);
            reader.ReadValueSafe(out uint layoutHash);
            reader.ReadValueSafe(out int count);
            var plan=DungeonModulePlanSession.Current;
            if(plan==null || seed!=plan.Seed || !uint.TryParse(plan.LayoutHash, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint expected) || layoutHash!=expected) return;
            if(count<0 || count>MaxBroken || reader.Length-reader.Position!=count*8 || !reader.TryBeginRead(count*8)) return;
            _broken.Clear();
            for(int i=0;i<count;i++) { reader.ReadValueSafe(out ulong id); _broken.Add(id); }
        }

        private void OnDestroy()
        {
            DungeonModulePlanSession.Changed-=OnPlanChanged;
            if(_registered && _network!=null && _network.CustomMessagingManager!=null)
                _network.CustomMessagingManager.UnregisterNamedMessageHandler(Message);
        }
    }
}
