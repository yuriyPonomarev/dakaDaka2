using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Emberdelve.AI;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// D1c5 data / health gate (D1a review lows):
    ///   1. at Level01 start every hostile (monster team) entity in the CombatRegistry exposes at least one finite
    ///      HitShape. An entity without shapes is silently unhittable by the swept melee and by projectiles.
    ///   2. every melee weapon a player can hold in the main hand (class primary / secondary weapon, every Weapon-slot
    ///      item's grantedWeapon) has swing patterns (UsesSweptMelee) and valid patterns. A melee weapon without
    ///      patterns cannot attack at all since D1a.
    ///
    /// Run: Unity -batchmode -nographics -projectPath . -logFile Logs/d1c5_combatdata.log
    ///      -executeMethod Emberdelve.EditorTools.EmberdelveCombatDataGate.Run
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveCombatDataGate
    {
        private const string Pending = "Emberdelve.CombatDataGate.Pending";

        static EmberdelveCombatDataGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("CombatData_Gate").AddComponent<CombatDataHarness>();
                }
            };
        }

        public static void Run()
        {
            SessionState.SetBool(Pending, true);
            EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_Level01.unity");
            EditorApplication.EnterPlaymode();
        }
    }

    public sealed class CombatDataHarness : MonoBehaviour
    {
        private const string Tag = "[CombatDataGate]";

        private int _checks;
        private int _failures;

        private void Check(bool ok, string label)
        {
            _checks++;
            if (!ok)
            {
                _failures++;
                Debug.LogError($"{Tag} FAIL {label}");
            }
        }

        private IEnumerator Start()
        {
            yield return null;
            yield return null;
            yield return null;
            try
            {
                HostileShapes();
                MeleeWeaponsHavePatterns();
            }
            catch (Exception e)
            {
                _failures++;
                Debug.LogError($"{Tag} FAIL gate threw");
                Debug.LogException(e);
            }

            Debug.Log($"{Tag} {(_failures == 0 ? "PASS" : "FAIL")} checks={_checks} failures={_failures}");
            EditorApplication.Exit(_failures == 0 ? 0 : 1);
        }

        private void HostileShapes()
        {
            EnemyAgent[] agents = FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None);
            Check(agents.Length > 0, $"Level01 starts with enemies in the scene ({agents.Length})");

            int hostile = 0;
            int withShapes = 0;
            int badShapes = 0;
            List<HitShape> shapes = new List<HitShape>(16);
            IReadOnlyList<ICombatEntity> all = CombatRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                ICombatEntity entity = all[i];
                if (entity == null || !CombatTeams.IsMonsterTeam(entity.TeamId)) continue;
                hostile++;
                shapes.Clear();
                if (entity is IHitShapeProvider provider) provider.CollectHitShapes(shapes);
                if (shapes.Count >= 1) withShapes++;
                else Debug.LogError($"{Tag} hostile entity #{entity.CombatId} ({entity.GetType().Name}) exposes no hit shape");
                for (int s = 0; s < shapes.Count; s++)
                {
                    HitShape shape = shapes[s];
                    if (!(shape.Radius > 0f) || float.IsInfinity(shape.Radius) || float.IsNaN(shape.A.x) || float.IsNaN(shape.A.y) || float.IsNaN(shape.A.z)
                        || float.IsNaN(shape.B.x) || float.IsNaN(shape.B.y) || float.IsNaN(shape.B.z)) badShapes++;
                }
            }

            Check(hostile > 0, $"the CombatRegistry holds hostile entities at Level01 start ({hostile})");
            Check(hostile >= agents.Length, $"every enemy agent in the scene is a registered hostile entity ({hostile} registered, {agents.Length} agents)");
            Check(withShapes == hostile, $"every hostile registry entity exposes at least one hit shape ({withShapes} of {hostile})");
            Check(badShapes == 0, $"every hostile hit shape has a finite position and a positive radius ({badShapes} bad)");
        }

        private static bool IsMeleeAttackWeapon(WeaponData weapon) => weapon != null && !weapon.IsRanged && !weapon.IsShield;

        private void MeleeWeaponsHavePatterns()
        {
            string[] classGuids = AssetDatabase.FindAssets("t:CharacterClassData");
            Check(classGuids.Length >= 2, $"the project has the player classes ({classGuids.Length})");
            int meleeSeen = 0;
            foreach (string guid in classGuids)
            {
                CharacterClassData data = AssetDatabase.LoadAssetAtPath<CharacterClassData>(AssetDatabase.GUIDToAssetPath(guid));
                if (data == null) continue;
                foreach ((WeaponData weapon, string slot) w in new[] { (data.primaryWeapon, "primary"), (data.secondaryWeapon, "secondary") })
                {
                    if (!IsMeleeAttackWeapon(w.weapon)) continue;
                    meleeSeen++;
                    CheckWeapon(w.weapon, $"{data.name} {w.slot} weapon '{w.weapon.name}'");
                }
            }

            string[] itemGuids = AssetDatabase.FindAssets("t:ItemData");
            Check(itemGuids.Length > 0, $"the project has item definitions ({itemGuids.Length})");
            foreach (string guid in itemGuids)
            {
                ItemData item = AssetDatabase.LoadAssetAtPath<ItemData>(AssetDatabase.GUIDToAssetPath(guid));
                if (item == null || item.equipSlot != EquipmentSlot.Weapon || !item.GrantsWeapon || !IsMeleeAttackWeapon(item.grantedWeapon)) continue;
                meleeSeen++;
                CheckWeapon(item.grantedWeapon, $"item '{item.name}' grants melee weapon '{item.grantedWeapon.name}'");
            }

            Check(meleeSeen > 0, $"at least one player-usable melee weapon was inspected ({meleeSeen})");
        }

        private void CheckWeapon(WeaponData weapon, string label)
        {
            Check(weapon.UsesSweptMelee, $"{label}: has swing patterns (UsesSweptMelee)");
            bool valid = weapon.swingPatterns != null && weapon.swingPatterns.Length > 0;
            if (valid)
            {
                foreach (SwingPatternData pattern in weapon.swingPatterns)
                {
                    if (pattern == null || !pattern.Validate(out _)) valid = false;
                }
            }

            Check(valid, $"{label}: every swing pattern is present and valid");
        }
    }
}
