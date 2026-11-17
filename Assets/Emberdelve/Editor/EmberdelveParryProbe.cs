using UnityEditor;
using UnityEngine;
using Emberdelve.AI;
using Emberdelve.BladeDefense;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Player;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Play-mode probe for the DaD sword parry: takes the shield off the local player, holds the guard toward the nearest
    /// enemy and logs every enemy swing that lands (blade contact, block, live parry window). Presentation-free; nothing is saved.
    /// </summary>
    public static class EmberdelveParryProbe
    {
        private const string Tag = "[ParryProbe]";
        private static PlayerRig _rig;
        private static MeleeCombat _melee;
        private static EnemyAgent _enemy;
        private static double _until;
        private static float _lastHealth;
        private static int _hits, _parries, _blocks;
        private static bool _hadWindow;

        [MenuItem("Emberdelve/Debug/Parry Probe (Play Mode)")]
        public static void Run()
        {
            if (!EditorApplication.isPlaying) { Debug.LogError(Tag + " enter Play mode first"); return; }
            _rig = Object.FindFirstObjectByType<PlayerRig>();
            if (_rig == null) { Debug.LogError(Tag + " no PlayerRig"); return; }
            _rig.SetOffhandWeapon(null);
            _melee = _rig.GetComponentInChildren<MeleeCombat>();
            if (_melee == null) _melee = _rig.GetComponentInParent<MeleeCombat>();
            if (_melee != null) _melee.enabled = false;

            _enemy = null;
            float best = float.PositiveInfinity;
            foreach (EnemyAgent agent in Object.FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None))
            {
                if (agent.IsDead) continue;
                float d = (agent.transform.position - _rig.transform.position).sqrMagnitude;
                if (d < best) { best = d; _enemy = agent; }
            }
            if (_enemy != null)
            {
                Vector3 flat = _rig.AimDirection; flat.y = 0f;
                if (flat.sqrMagnitude < 1e-4f) flat = _rig.transform.forward;
                Vector3 p = _rig.transform.position + flat.normalized * 1.7f;
                p.y = _enemy.transform.position.y;
                _enemy.transform.position = p;
                _enemy.transform.rotation = Quaternion.LookRotation(-flat.normalized, Vector3.up);
            }

            _lastHealth = _rig.Health != null ? _rig.Health.CurrentHealth : 0f;
            _hits = _parries = _blocks = 0;
            _hadWindow = false;
            _until = EditorApplication.timeSinceStartup + 14.0;
            Debug.Log($"{Tag} start: weapon={(_rig.ActiveWeapon != null ? _rig.ActiveWeapon.weaponId : "none")} shield={_rig.GuardHasShield} canParry={_rig.CanParry} enemy={(_enemy != null ? _enemy.name : "none")}");
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            if (!EditorApplication.isPlaying || _rig == null) { Stop("play mode ended"); return; }
            _rig.SetGuardHeld(true);

            LocalCombatAuthority authority = GameServices.TryGet<ICombatAuthority>(out ICombatAuthority a) ? a as LocalCombatAuthority : null;
            float window = authority != null ? authority.RiposteWindowRemaining(_rig.CombatId) : 0f;
            float health = _rig.Health != null ? _rig.Health.CurrentHealth : 0f;
            if (health < _lastHealth - 0.01f)
            {
                _hits++;
                Debug.Log($"{Tag} took {(_lastHealth - health):F1} damage (guard outcome {_rig.LastGuardOutcome}, guarding={_rig.IsGuarding})");
            }
            else if (window > 0f && !_hadWindow)
            {
                _parries++;
                Debug.Log($"{Tag} PARRY: blade block opened a riposte window {window:F2}s (outcome {_rig.LastGuardOutcome})");
            }
            _hadWindow = window > 0f;
            _lastHealth = health;
            if (EditorApplication.timeSinceStartup > _until) Stop("done");
        }

        private static void Stop(string why)
        {
            EditorApplication.update -= Tick;
            if (_melee != null) _melee.enabled = true;
            if (_rig != null) _rig.SetGuardHeld(false);
            Debug.Log($"{Tag} {why}: damaged {_hits}x, parries {_parries}x, guard={( _rig != null ? _rig.LastGuardOutcome.ToString() : "-")}");
        }
    }
}
