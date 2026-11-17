using System;
using System.Linq;
using Emberdelve.AI;
using Emberdelve.Data;
using Emberdelve.Level;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Emberdelve.EditorTools
{
    internal static class EmberdelveCinderWarden
    {
        private const string Art = "Assets/Emberdelve/Art/Models/AI/Meshy/CinderWarden/";
        private const string Data = "Assets/Emberdelve/Data/";
        private const string Visual = "Assets/Emberdelve/Resources/Emberdelve/NetEnemyVisuals/EnemyVisual_CinderWarden.prefab";
        private const string Crypt = "Assets/Emberdelve/Scenes/Emberdelve_ModularCrypts.unity";
        private const float ArtScale = .88f;

        [MenuItem("Emberdelve/Visual/Build Cinder Warden")]
        public static void Build()
        {
            BuildController();
            EnemyData legacy = Load<EnemyData>(Data + "Enemy_AshenWarden.asset");
            SwingPatternData pattern = LoadOrCreate<SwingPatternData>(Data + "Pattern_CinderClub.asset");
            WeaponData weapon = LoadOrCreate<WeaponData>(Data + "Weapon_CinderClub.asset");
            EditorUtility.CopySerialized(legacy.attackWeapon, weapon);
            weapon.name = "Weapon_CinderClub";
            weapon.weaponId = "cinder_club";
            weapon.displayName = "Ironbound Club";
            weapon.baseDamage = 39;
            weapon.impactPower = 42;
            weapon.swingPatterns = new[] { pattern };

            BakeVisualAndSwing(pattern, weapon);

            EnemyData boss = LoadOrCreate<EnemyData>(Data + "Enemy_CinderWarden.asset");
            EditorUtility.CopySerialized(legacy, boss);
            boss.name = "Enemy_CinderWarden";
            boss.enemyId = EnemyData.CinderWardenId;
            boss.displayName = "Cinder Warden";
            boss.maxHealth = 320;
            boss.attackWeapon = weapon;
            boss.attackRange = 2.38f;
            boss.attackWindup = .98f;
            boss.attackCooldown = 2.65f;
            boss.attackTelegraphHeight = .05f;
            boss.attackTelegraphCone = 64f;
            boss.patrolRadius = 3.5f;
            boss.patrolSpeed = .85f;
            boss.chaseSpeed = 2.85f;
            boss.turnRate = 240f;
            boss.sightCone = 170f;
            EditorUtility.SetDirty(pattern);
            EditorUtility.SetDirty(weapon);
            EditorUtility.SetDirty(boss);

            var layout = Load<DungeonLayoutData>(Data + "DungeonLayout_AshenHalls.asset");
            var slot = layout.enemySpawns.Single(spec => spec.enemy == legacy || spec.enemy == boss);
            slot.enemy = boss;
            slot.cell = new Vector2Int(7, 17);
            EditorUtility.SetDirty(layout);
            AssetDatabase.SaveAssets();
            InstallInCrypt(legacy, boss, layout.CellCenterWorld(slot.cell));
        }

        private static void BuildController()
        {
            var controller = Load<AnimatorController>(Art + "CinderWarden.controller");
            string parameter = EnemyVisualDriver.DeadParameter;
            if (controller.parameters.Any(p => p.name == parameter)) return;
            AnimationClip clip = EmberdelveEnemyVisualFactory.FindClip("Death01");
            if (clip == null || !clip.humanMotion) throw new InvalidOperationException("Missing humanoid Quaternius Death01.");
            controller.AddParameter(parameter, AnimatorControllerParameterType.Bool);
            var machine = controller.layers[0].stateMachine;
            foreach (AnimatorStateTransition existing in machine.anyStateTransitions)
                existing.AddCondition(AnimatorConditionMode.IfNot, 0, parameter);
            var state = machine.AddState("Death", new Vector3(600, 260, 0));
            state.motion = clip;
            var transition = machine.AddAnyStateTransition(state);
            transition.canTransitionToSelf = false;
            transition.hasExitTime = false;
            transition.duration = .10f;
            transition.AddCondition(AnimatorConditionMode.If, 0f, parameter);
            EditorUtility.SetDirty(controller);
        }

        private static void BakeVisualAndSwing(SwingPatternData pattern, WeaponData weapon)
        {
            Scene preview = EditorSceneManager.NewPreviewScene();
            try
            {
                GameObject root = new GameObject("EnemyVisual_CinderWarden");
                SceneManager.MoveGameObjectToScene(root, preview);
                var art = (GameObject)PrefabUtility.InstantiatePrefab(Load<GameObject>(Art + "CinderWarden.prefab"), preview);
                art.transform.SetParent(root.transform, false);
                art.transform.localScale = Vector3.one * ArtScale;
                Animator animator = art.GetComponentInChildren<Animator>();
                var driver = new SerializedObject(root.AddComponent<EnemyVisualDriver>());
                driver.FindProperty("animator").objectReferenceValue = animator;
                ParticleSystem blood = EmberdelveBloodEffect.Create(root.transform);
                blood.transform.localPosition = Vector3.up * EnemyData.CinderWardenCenterHeight;
                driver.FindProperty("bloodEffect").objectReferenceValue = blood;
                driver.ApplyModifiedPropertiesWithoutUndo();
                if (PrefabUtility.SaveAsPrefabAsset(root, Visual) == null)
                    throw new InvalidOperationException("Could not save Cinder Warden enemy visual.");

                Transform club = animator.GetBoneTransform(HumanBodyBones.RightHand).Find("ClubGrip/Ironbound Club");
                MeshFilter mesh = club.GetComponentInChildren<MeshFilter>();
                Vector3[] vertices = mesh.sharedMesh.vertices.Select(p => club.InverseTransformPoint(mesh.transform.TransformPoint(p))).ToArray();
                float min = vertices.Min(p => p.y);
                float height = vertices.Max(p => p.y) - min;
                Vector3 lower = ClubSection(vertices, min, height, .54f);
                Vector3 upper = ClubSection(vertices, min, height, .91f);
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.Rebind();
                Vector3 eye = Vector3.up * EnemyAgent.AttackEyeHeight;

                SwingKey Sample(string state, float time, float t, out Vector3 end, out float length)
                {
                    animator.Play(state, 0, time);
                    animator.Update(0);
                    Vector3 start = root.transform.InverseTransformPoint(club.TransformPoint(lower));
                    end = root.transform.InverseTransformPoint(club.TransformPoint(upper));
                    Vector3 delta = end - start;
                    length = delta.magnitude;
                    float yaw = Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg;
                    float pitch = -Mathf.Asin(Mathf.Clamp(delta.y / length, -1f, 1f)) * Mathf.Rad2Deg;
                    return new SwingKey(t, start - eye, yaw, pitch);
                }

                SwingKey rest = Sample("Idle", .14f, 0f, out _, out _);
                pattern.patternId = "cinder_club_sweep";
                pattern.kind = SwingKind.Overhead;
                pattern.windupTime = .18f;
                pattern.activeTime = .22f;
                pattern.recoveryTime = .62f;
                pattern.wallRecoilTime = .42f;
                pattern.sweepRadius = .27f;
                pattern.maxTargets = 3;
                pattern.restBase = rest.basePos;
                pattern.restYaw = rest.bladeYaw;
                pattern.restPitch = rest.bladePitch;
                pattern.restRoll = 0f;
                pattern.keys = new SwingKey[13];
                float reach = 0f, lengthSum = 0f;
                for (int i = 0; i < pattern.keys.Length; i++)
                {
                    float t = i / (float)(pattern.keys.Length - 1);
                    var key = Sample("Attack", Mathf.Lerp(EnemyAttackPhase.ClubSweepStart, EnemyAttackPhase.ClubSweepEnd, t),
                        t, out Vector3 tip, out float length);
                    if (i > 0)
                    {
                        float previous = pattern.keys[i - 1].bladeYaw;
                        key.bladeYaw = previous + Mathf.DeltaAngle(previous, key.bladeYaw);
                    }
                    pattern.keys[i] = key;
                    reach = Mathf.Max(reach, new Vector2(tip.x, tip.z).magnitude);
                    lengthSum += length;
                }
                float contactLength = lengthSum / pattern.keys.Length;
                weapon.reach = reach;
                weapon.hitSphereRadius = pattern.sweepRadius;
                pattern.reachInset = reach - contactLength;
                Debug.Log($"[CinderWarden] Baked {pattern.keys.Length} club samples, head {contactLength:F2} m, reach {pattern.EffectiveReach(weapon):F2} m.");
            }
            finally { EditorSceneManager.ClosePreviewScene(preview); }
        }

        private static Vector3 ClubSection(Vector3[] vertices, float min, float height, float fraction)
        {
            float y = min + height * fraction;
            var section = vertices.Where(p => Mathf.Abs(p.y - y) < height * .04f).ToArray();
            return new Vector3(section.Average(p => p.x), y, section.Average(p => p.z));
        }

        private static void InstallInCrypt(EnemyData legacy, EnemyData boss, Vector3 spawn)
        {
            Scene scene = SceneManager.GetSceneByPath(Crypt);
            bool opened = !scene.IsValid() || !scene.isLoaded;
            if (opened) scene = EditorSceneManager.OpenScene(Crypt, OpenSceneMode.Additive);
            try
            {
                if (scene.isDirty) throw new InvalidOperationException("Save the open Modular Crypts scene before rebuilding its boss.");
                EnemyAgent agent = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<EnemyAgent>(true))
                    .Single(enemy => enemy.Data == legacy || enemy.Data == boss);
                agent.name = "Enemy_Cinder Warden_4";
                agent.transform.position = spawn;
                float scale = boss.BodyScale;
                SetBox(agent.transform.Find("Body"), new Vector3(0f, .95f * scale, 0f), new Vector3(.97f, 1.45f, .72f) * scale);
                SetBox(agent.transform.Find("Head"), Vector3.up * (1.85f * scale), Vector3.one * (.44f * scale));
                Transform oldVisual = agent.transform.Find("Visual");
                if (oldVisual != null) Object.DestroyImmediate(oldVisual.gameObject);
                var serialized = new SerializedObject(agent);
                serialized.FindProperty("data").objectReferenceValue = boss;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                if (EmberdelveEnemyVisualFactory.Attach(agent.gameObject, boss, 4) == null)
                    throw new InvalidOperationException("Could not attach the Cinder Warden visual.");
                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save Modular Crypts.");
            }
            finally { if (opened) EditorSceneManager.CloseScene(scene, true); }
        }

        private static void SetBox(Transform box, Vector3 position, Vector3 size)
        {
            box.localPosition = position;
            box.localScale = size;
        }

        private static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path) ??
            throw new InvalidOperationException("Missing asset " + path);

        private static T LoadOrCreate<T>(string path) where T : ScriptableObject
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset != null) return asset;
            asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }
    }
}
