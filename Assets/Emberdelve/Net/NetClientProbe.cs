using System.Collections.Generic;
using System.Globalization;
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
    ///  2. Runs the optional test script (--ember-net-script) "forge-owner" (the skill scripts are NetSkillSender's). It exists in development builds / the Editor only: once another
    ///     player's view exists, call the dev-only test RPC on THAT player's object (2 times) and once on our own object. The server
    ///     must reject the first two (we are not that object's owner) and accept the last.
    ///  3. U3A4a: every half second logs where it sees each OTHER player: "[client] view id=&lt;n&gt; pos=(x,y,z)" (written when the
    ///     view moved, and as a heartbeat every two seconds), so a gate can compare an observer's picture with the server's. The
    ///     movement stream and the movement scripts are NetInputSender's; the probe still only runs forge-owner.
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
        private readonly Dictionary<int, Vector3> _viewLogged = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, float> _viewLoggedAt = new Dictionary<int, float>();
        private float _nextViewLog;

        private const float ViewLogSeconds = 0.5f;
        private const float ViewHeartbeatSeconds = 2f;
        private const float ViewMoveEpsilon = 0.002f;

        public void Configure(NetLaunchArgs args)
        {
            _script = args != null ? args.Script : "";
        }

        private void Update()
        {
            IReadOnlyList<NetPlayerRig> rigs = NetPlayerRig.Spawned;
            if (rigs.Count == 0 && _lastKey.Length == 0) return;

            LogViews(rigs);

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

        private void LogViews(IReadOnlyList<NetPlayerRig> rigs)
        {
            float now = Time.unscaledTime;
            if (now < _nextViewLog) return;
            _nextViewLog = now + ViewLogSeconds;

            for (int i = 0; i < rigs.Count; i++)
            {
                NetPlayerRig rig = rigs[i];
                if (rig == null || !rig.IsSpawned || rig.IsOwner) continue;

                int id = rig.ServerPlayerId.Value;
                if (id <= 0) continue;

                Vector3 pos = rig.transform.position;
                bool known = _viewLogged.TryGetValue(id, out Vector3 last);
                bool moved = !known || (pos - last).sqrMagnitude > ViewMoveEpsilon * ViewMoveEpsilon;
                bool heartbeat = known && _viewLoggedAt.TryGetValue(id, out float at) && now - at >= ViewHeartbeatSeconds;
                if (!moved && !heartbeat) continue;

                _viewLogged[id] = pos;
                _viewLoggedAt[id] = now;
                Debug.Log(NetTuning.LogPrefix + "[client] view id=" + id + " pos=(" +
                          pos.x.ToString("F3", CultureInfo.InvariantCulture) + "," +
                          pos.y.ToString("F3", CultureInfo.InvariantCulture) + "," +
                          pos.z.ToString("F3", CultureInfo.InvariantCulture) + ")");
            }
        }

        private void RunScript(NetPlayerRig ownedRig, IReadOnlyList<NetPlayerRig> rigs)
        {
            // U3B2: the skill scripts (skill-cast, skill-proof, skill-abuse) run on NetSkillSender; this probe only runs forge-owner.
            if (_scriptDone || _script != NetLaunchArgs.ScriptForgeOwner) return;

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            NetPlayerRig victim = NetPlayerRig.FindProbeVictim();
            if (victim == null) return; // wait until somebody else's view exists

            _scriptDone = true;
            int victimId = victim.ServerPlayerId.Value;
            victim.ForgeOwnerTestRpc(777);
            victim.ForgeOwnerTestRpc(778);
            ownedRig.ForgeOwnerTestRpc(5);
            Debug.Log(NetTuning.LogPrefix + "[client] script forge-owner sent target=" + victimId + " forged=2 self=1");
#else
            _scriptDone = true;
            Debug.Log(NetTuning.LogPrefix + "[client] script " + _script + " unavailable (not a development build)");
#endif
        }
    }
}
