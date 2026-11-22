using Emberdelve.Net.Rules;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Skills;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>
    /// U3B2, client (and host) process, observers only: the lightweight presentation of ANOTHER player's skill cast. NetPlayerRig
    /// .SkillVisualRpc (server to every client but the owner) hands over the skill id; this component logs it for the proof gates and
    /// keeps a short floating label above the caster for a windowed client. It changes no gameplay state: health, mana and cooldown
    /// never travel with the visual, and nothing here writes the rig's kit. Audio resolves the replicated skill id against
    /// the class's shared skill definitions.
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
            // Host already heard the authority's cast; this RPC supplies only remote client audio.
            if (NetRole.IsClientOnly) PlayCastAudio(caster, visual.SkillId);

            string role = caster.IsServer ? "host" : "client";
            Debug.Log(NetTuning.LogPrefix + "[" + role + "] skill-visual netId=" + caster.NetworkObjectId + " playerId=" + caster.ServerPlayerId.Value +
                      " slot=" + visual.Slot + " skill=" + (string.IsNullOrEmpty(visual.SkillId) ? "-" : visual.SkillId));
        }

        internal static void PlayCastAudio(NetPlayerRig caster, string skillId)
        {
            if (caster == null || caster.Rig == null || string.IsNullOrEmpty(skillId)) return;
            foreach (SkillData skill in SkillLoadoutStore.Pool(caster.Rig.Class, SkillSlot.Active))
            {
                if (skill.skillId != skillId) continue;
                SkillCaster.PlayCastAudio(skill, caster.transform.position);
                return;
            }
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
