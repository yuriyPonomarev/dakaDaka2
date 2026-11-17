using System.Collections;
using System.Linq;
using Emberdelve.AI;
using Emberdelve.Core;
using Emberdelve.Player;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    [InitializeOnLoad]
    public static class EmberdelveCinderCryptGate
    {
        private const string Pending = "Emberdelve.CinderCryptGate.Pending";

        static EmberdelveCinderCryptGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending, false)) return;
                SessionState.SetBool(Pending, false);
                new GameObject("CinderCrypt_Gate").AddComponent<CinderCryptHarness>();
            };
        }

        public static void Run()
        {
            SessionState.SetBool(Pending, true);
            EditorSceneManager.OpenScene(EmberdelvePaths.ModularDungeonScenePath);
            EditorApplication.EnterPlaymode();
        }
    }

    internal sealed class CinderCryptHarness : MonoBehaviour
    {
        private const string Tag = "[Emberdelve][CinderCrypt]";
        private int _checks;
        private int _failures;

        private IEnumerator Start()
        {
            yield return null;
            yield return null;
            EnemyAgent[] enemies = FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None);
            EnemyAgent[] bosses = enemies.Where(enemy => enemy.UsesCinderClub).ToArray();
            var player = FindObjectsByType<PlayerRig>(FindObjectsSortMode.None).FirstOrDefault(rig => rig.Kind == EntityKind.LocalPlayer);
            Check(bosses.Length == 1, "the standard Crypt scene contains one active Cinder Warden");
            Check(player != null && !player.IsDead, "the Crypt scene has a live player");
            if (bosses.Length == 1 && player != null) yield return Fight(bosses[0], player, enemies);
            Debug.Log($"{Tag} {(_failures == 0 ? "PASS" : "FAIL")} checks={_checks} failures={_failures}");
            EmberdelveGateExit.Exit(_failures == 0 ? 0 : 1);
        }

        private IEnumerator Fight(EnemyAgent boss, PlayerRig player, EnemyAgent[] enemies)
        {
            foreach (EnemyAgent enemy in enemies)
                if (enemy != boss) enemy.gameObject.SetActive(false);
            Check(boss.IsRuntimeInitialized && boss.CurrentHealth == 320f && !boss.IsDormant,
                "the placed boss is awake with 320 HP");
            Check(boss.transform.Find("Visual") != null && boss.GetComponentInChildren<EnemyVisualDriver>() != null,
                "the placed boss uses the Meshy visual");

            int levelMask = LayerMask.GetMask(GameConfig.LevelLayerName);
            Vector3 origin = boss.transform.position;
            Vector3 destination = origin + boss.transform.forward * 2.05f;
            bool clear = false;
            for (int i = 0; i < 8; i++)
            {
                Vector3 direction = Quaternion.Euler(0f, i * 45f, 0f) * boss.transform.forward;
                Vector3 point = origin + direction * 2.05f;
                if (Physics.Linecast(origin + Vector3.up * 1.2f, point + Vector3.up * 1.1f, levelMask)) continue;
                if (!Physics.Raycast(point + Vector3.up, Vector3.down, out RaycastHit floor, 2.5f, levelMask)) continue;
                destination = floor.point + Vector3.up * .04f;
                clear = true;
                break;
            }
            Check(clear, "there is a supported path from the boss to melee range");
            if (!clear) yield break;
            var controller = player.GetComponent<CharacterController>();
            if (controller != null) controller.enabled = false;
            player.transform.position = destination;
            if (controller != null) controller.enabled = true;
            Physics.SyncTransforms();

            float health = player.CurrentHealth;
            float deadline = Time.time + 8f;
            while (!boss.IsWindingUp && Time.time < deadline) yield return null;
            Check(boss.IsWindingUp && boss.CurrentTargetCombatId == player.CombatId,
                "the placed boss targets the real player and winds up");
            Check(player.CurrentHealth == health, "windup does not damage the real player");
            while (player.CurrentHealth >= health && Time.time < deadline) yield return null;
            Check(boss.LastSwingId != 0 && player.CurrentHealth < health,
                $"the live club hit reduces player HP from {health:F1} to {player.CurrentHealth:F1}");
        }

        private void Check(bool condition, string label)
        {
            _checks++;
            if (condition) Debug.Log($"{Tag} ok {label}");
            else { _failures++; Debug.LogError($"{Tag} FAIL {label}"); }
        }
    }
}
