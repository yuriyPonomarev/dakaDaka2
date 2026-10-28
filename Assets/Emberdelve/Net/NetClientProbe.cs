using System.Collections.Generic;
using System.Text;
using Emberdelve.Net.Rules;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>
    /// U3A3, client process only (created by NetBootstrap). Two jobs, both observation for the proof gate and the dev overlay:
    ///
    ///  1. Logs the settled view picture: "[client] owner id=&lt;playerId&gt; views=&lt;k&gt; owned=1 class=.. party=.. ids=a,b". A picture is
    ///     "settled" when it has not changed for <see cref="SettleSeconds"/>; a new line is written each time the settled picture
    ///     changes (a player joins or leaves), so the gate can read both "views=2" and the later drop.
    ///  2. Runs the optional test script (--ember-net-script). "forge-owner" (development builds / Editor only): once another
    ///     player's view exists, call the dev-only test RPC on THAT player's object (2 times) and once on our own object. The server
    ///     must reject the first two (we are not that object's owner) and accept the last.
    /// The probe sends no gameplay input and never decides anything; the server's replicated values are only read.
    /// </summary>
    public sealed class NetClientProbe : MonoBehaviour
    {
        private const float SettleSeconds = 0.75f;

        private string _script = "";
        private string _lastKey = "";
        private string _loggedKey = "";
        private float _changedAt;
        private bool _scriptDone;
        private readonly List<int> _ids = new List<int>();

        public void Configure(NetLaunchArgs args)
        {
            _script = args != null ? args.Script : "";
        }

        private void Update()
        {
            IReadOnlyList<NetPlayerRig> rigs = NetPlayerRig.Spawned;
            if (rigs.Count == 0 && _lastKey.Length == 0) return;

            int total = 0;
            int owned = 0;
            NetPlayerRig ownedRig = null;
            bool idsKnown = true;
            _ids.Clear();

            for (int i = 0; i < rigs.Count; i++)
            {
                NetPlayerRig rig = rigs[i];
                if (rig == null || !rig.IsSpawned) continue;
                total++;
                int id = rig.ServerPlayerId.Value;
                if (id <= 0) idsKnown = false;
                _ids.Add(id);
                if (rig.IsOwner)
                {
                    owned++;
                    ownedRig = rig;
                }
            }

            _ids.Sort();
            StringBuilder csv = new StringBuilder();
            for (int i = 0; i < _ids.Count; i++)
            {
                if (i > 0) csv.Append(',');
                csv.Append(_ids[i]);
            }

            int ownedId = ownedRig != null ? ownedRig.ServerPlayerId.Value : 0;
            string key = total + "|" + owned + "|" + ownedId + "|" + csv;
            float now = Time.unscaledTime;
            if (key != _lastKey)
            {
                _lastKey = key;
                _changedAt = now;
            }

            if (now - _changedAt < SettleSeconds || key == _loggedKey) return;
            _loggedKey = key;

            if (owned == 1 && idsKnown && total >= 1)
            {
                Debug.Log(NetTuning.LogPrefix + "[client] owner id=" + ownedId + " views=" + total + " owned=" + owned +
                          " class=" + ownedRig.ClassIdText + " party=" + ownedRig.ServerPartyId.Value + " ids=" + csv);
                RunScript(ownedRig, rigs);
            }
            else
            {
                Debug.Log(NetTuning.LogPrefix + "[client] views unsettled total=" + total + " owned=" + owned + " idsKnown=" + idsKnown);
            }
        }

        private void RunScript(NetPlayerRig ownedRig, IReadOnlyList<NetPlayerRig> rigs)
        {
            if (_scriptDone || _script != NetLaunchArgs.ScriptForgeOwner) return;

            NetPlayerRig victim = null;
            for (int i = 0; i < rigs.Count; i++)
            {
                NetPlayerRig rig = rigs[i];
                if (rig != null && rig.IsSpawned && !rig.IsOwner) { victim = rig; break; }
            }

            if (victim == null) return; // wait until somebody else's view exists

            _scriptDone = true;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            int victimId = victim.ServerPlayerId.Value;
            victim.ForgeOwnerTestRpc(777);
            victim.ForgeOwnerTestRpc(778);
            ownedRig.ForgeOwnerTestRpc(5);
            Debug.Log(NetTuning.LogPrefix + "[client] script forge-owner sent target=" + victimId + " forged=2 self=1");
#else
            Debug.Log(NetTuning.LogPrefix + "[client] script forge-owner unavailable (not a development build)");
#endif
        }
    }
}
