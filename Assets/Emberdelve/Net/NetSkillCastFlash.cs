using Emberdelve.Net.Rules;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>
    /// U3B2, client (and host) process, observers only: the lightweight presentation of ANOTHER player's skill cast. NetPlayerRig
    /// .SkillVisualRpc (server to every client but the owner) hands over the skill id; this component logs it for the proof gates and
    /// keeps a short floating label above the caster for a windowed client. It changes no gameplay state: health, mana and cooldown
    /// never travel with the visual, and nothing here reads or writes the rig's kit. A real effect (particles, audio) binds to
    /// <see cref="NetPlayerRig"/> through this component later.
    ///   [client] skill-visual netId=N playerId=P slot=S skill=ID
    /// </summary>
    public sealed class NetSkillCastFlash : MonoBehaviour
    {
        private const float ShowSeconds = 1.2f;

        private string _text = "";
        private float _until;
        private int _count;

        public int Count => _count;

        public static void Show(NetPlayerRig caster, SkillVisualWire visual)
        {
            if (caster == null) return;

            NetSkillCastFlash flash = caster.GetComponent<NetSkillCastFlash>();
            if (flash == null) flash = caster.gameObject.AddComponent<NetSkillCastFlash>();
            flash._count++;
            flash._text = string.IsNullOrEmpty(visual.SkillId) ? "skill " + (visual.Slot + 1) : visual.SkillId;
            flash._until = Time.unscaledTime + ShowSeconds;

            string role = caster.IsServer ? "host" : "client";
            Debug.Log(NetTuning.LogPrefix + "[" + role + "] skill-visual netId=" + caster.NetworkObjectId + " playerId=" + caster.ServerPlayerId.Value +
                      " slot=" + visual.Slot + " skill=" + (string.IsNullOrEmpty(visual.SkillId) ? "-" : visual.SkillId));
        }

        private void OnGUI()
        {
            if (Application.isBatchMode || Time.unscaledTime >= _until) return;

            Camera camera = Camera.main;
            if (camera == null) return;
            Vector3 screen = camera.WorldToScreenPoint(transform.position + Vector3.up * 2.1f);
            if (screen.z <= 0f) return;
            GUI.Label(new Rect(screen.x - 80f, Screen.height - screen.y - 12f, 160f, 24f), _text);
        }
    }
}
