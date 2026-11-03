using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Emberdelve.AI;
using Emberdelve.Data;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// V2: replaces the blockout cubes of the enemies with the KayKit skeletons. Builds, per variant, an AnimatorController
    /// (Idle / Walk / Run / Attack / Hit / Death) from the clips embedded in the character FBX and a prefab
    /// Assets/Emberdelve/Prefabs/Visual/EnemyVisual_&lt;Variant&gt;.prefab (the model, its weapon on the right hand bone, a shield on
    /// the left for the Warrior, an EnemyVisualDriver). The level builder attaches the prefab as the child "Visual" of each
    /// enemy and switches the cube renderers off (the cubes stay: they carry the colliders the combat code uses).
    /// </summary>
    internal static class EmberdelveEnemyVisualFactory
    {
        public const string Folder = "Assets/Emberdelve/Prefabs/Visual";
        public const string VisualChildName = "Visual";

        /// <summary>Height of the model after fit (metres). The old blockout was 2.07 m (head top); hit volumes are unchanged.</summary>
        public const float NormalHeight = 1.9f;

        /// <summary>The elite is scaled up like its hit volumes (1.25).</summary>
        public const float EliteScale = 1.25f;

        internal sealed class Variant
        {
            public string Name;
            public string Character;
            public string MainHand;
            public string OffHand;
            public string Idle, Walk, Run, Attack, Hit, Death;
        }

        public static readonly Variant[] Variants =
        {
            new Variant { Name = "Minion",  Character = "Skeleton_Minion",  MainHand = "Skeleton_Blade", OffHand = null,
                Idle = "Idle", Walk = "Walking_A", Run = "Running_A", Attack = "1H_Melee_Attack_Chop", Hit = "Hit_A", Death = "Death_A" },
            new Variant { Name = "Rogue",   Character = "Skeleton_Rogue",   MainHand = "Skeleton_Blade", OffHand = null,
                Idle = "Idle", Walk = "Walking_B", Run = "Running_B", Attack = "1H_Melee_Attack_Stab", Hit = "Hit_A", Death = "Death_B" },
            new Variant { Name = "Warrior", Character = "Skeleton_Warrior", MainHand = "Skeleton_Axe",   OffHand = "Skeleton_Shield_Small_A",
                Idle = "Idle", Walk = "Walking_B", Run = "Running_A", Attack = "1H_Melee_Attack_Slice_Diagonal", Hit = "Hit_A", Death = "Death_A" },
            new Variant { Name = "Mage",    Character = "Skeleton_Mage",    MainHand = null,             OffHand = null,
                Idle = "Idle", Walk = "Walking_A", Run = "Running_A", Attack = "Spellcast_Shoot", Hit = "Hit_A", Death = "Death_B" },
        };

        public static readonly string[] StateNames = { "Idle", "Walk", "Run", "Attack", "Hit", "Death" };

        // ---------------------------------------------------------------- mapping

        /// <summary>
        /// Enemy data -> skeleton variant. Elites (the Ashen Warden) are Warriors with axe and shield. Normal enemies (the Hollow
        /// Servitor) alternate Minion / Rogue by spawn index so a room does not look cloned. The Mage variant is built and
        /// available, but no enemy data maps to it yet (there is no caster enemy).
        /// </summary>
        public static string VariantFor(EnemyData enemy, int spawnIndex)
        {
            if (enemy != null && enemy.isElite) return "Warrior";
            return (spawnIndex & 1) == 0 ? "Minion" : "Rogue";
        }

        public static string PrefabPath(string variant) { return Folder + "/EnemyVisual_" + variant + ".prefab"; }
        public static string ControllerPath(string variant) { return Folder + "/EnemyVisual_" + variant + ".controller"; }

        public static Variant Find(string name)
        {
            for (int i = 0; i < Variants.Length; i++) if (Variants[i].Name == name) return Variants[i];
            return null;
        }

        // ---------------------------------------------------------------- build

        private static bool _done;

        /// <summary>Builds every controller and prefab (once per editor session unless forced). False when the KayKit models are missing.</summary>
        public static bool EnsureAll(bool force)
        {
            if (_done && !force) return AllPrefabsExist();

            EmberdelveVendorImport.EnsureImported(false);
            EmberdelveVisualSetup.EnsureFolder(Folder);

            bool ok = true;
            for (int i = 0; i < Variants.Length; i++) ok &= Build(Variants[i]);

            AssetDatabase.SaveAssets();
            _done = true;
            return ok;
        }

        private static bool AllPrefabsExist()
        {
            for (int i = 0; i < Variants.Length; i++)
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath(Variants[i].Name)) == null) return false;
            }

            return true;
        }

        private static bool Build(Variant variant)
        {
            string fbxPath = EmberdelveVendorImport.CharacterPath(variant.Character);
            GameObject fbx = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (fbx == null)
            {
                Debug.LogWarning("[Emberdelve][EnemyVisual] " + fbxPath + " is not imported; variant " + variant.Name + " skipped.");
                return false;
            }

            AnimatorController controller = BuildController(variant, fbxPath);
            if (controller == null) return false;

            GameObject root = new GameObject("EnemyVisual_" + variant.Name);
            try
            {
                GameObject model = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
                model.name = "Model";
                model.transform.SetParent(root.transform, false);
                PrefabUtility.UnpackPrefabInstance(model, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

                Animator animator = model.GetComponent<Animator>();
                if (animator == null) animator = model.AddComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
                if (animator.avatar == null) animator.avatar = FindAvatar(fbxPath);

                FitHeight(model.transform, NormalHeight);

                if (!string.IsNullOrEmpty(variant.MainHand)) AttachWeapon(model.transform, EmberdelveVendorImport.AccessoryPath(variant.MainHand), true);
                if (!string.IsNullOrEmpty(variant.OffHand)) AttachWeapon(model.transform, EmberdelveVendorImport.AccessoryPath(variant.OffHand), false);

                // Pure visuals: nothing of the model may collide.
                Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < colliders.Length; i++) Object.DestroyImmediate(colliders[i]);

                EnemyVisualDriver driver = root.AddComponent<EnemyVisualDriver>();
                SerializedObject serialized = new SerializedObject(driver);
                SerializedProperty animatorProperty = serialized.FindProperty("animator");
                if (animatorProperty != null) animatorProperty.objectReferenceValue = animator;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath(variant.Name));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }

            return true;
        }

        private static Avatar FindAvatar(string fbxPath)
        {
            Object[] assets = AssetDatabase.LoadAllAssetsAtPath(fbxPath);
            for (int i = 0; i < assets.Length; i++)
            {
                Avatar avatar = assets[i] as Avatar;
                if (avatar != null) return avatar;
            }

            return null;
        }

        /// <summary>Scales the model uniformly so its skinned meshes are <paramref name="height"/> metres tall (units of the FBX are not assumed).</summary>
        private static void FitHeight(Transform model, float height)
        {
            SkinnedMeshRenderer[] renderers = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers.Length == 0) return;

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

            float current = bounds.size.y;
            if (current < 0.05f || current > 50f)
            {
                Debug.LogWarning("[Emberdelve][EnemyVisual] implausible model height " + current + " m; the model keeps its imported scale.");
                return;
            }

            float scale = height / current;
            model.localScale = Vector3.one * Mathf.Round(scale * 1000f) / 1000f;
        }

        // ---------------------------------------------------------------- weapons

        /// <summary>
        /// Hand bone lookup: "handslot.r" / "handslot.l" (the KayKit weapon slot), else a bone whose name contains "handslot", else
        /// a non-IK bone containing "hand" on the right/left, else null (the caller falls back to the model root and warns).
        /// </summary>
        public static Transform FindHandBone(Transform model, bool right)
        {
            Transform[] all = model.GetComponentsInChildren<Transform>(true);

            string exact = right ? "handslot.r" : "handslot.l";
            for (int i = 0; i < all.Length; i++) if (all[i].name.ToLowerInvariant() == exact) return all[i];

            for (int i = 0; i < all.Length; i++)
            {
                string lower = all[i].name.ToLowerInvariant();
                if (lower.Contains("handslot") && IsSide(lower, right)) return all[i];
            }

            for (int i = 0; i < all.Length; i++)
            {
                string lower = all[i].name.ToLowerInvariant();
                if (lower.Contains("hand") && !lower.Contains("ik") && IsSide(lower, right)) return all[i];
            }

            return null;
        }

        private static bool IsSide(string lowerName, bool right)
        {
            if (right) return lowerName.EndsWith(".r") || lowerName.EndsWith("_r") || lowerName.Contains("right");
            return lowerName.EndsWith(".l") || lowerName.EndsWith("_l") || lowerName.Contains("left");
        }

        private static void AttachWeapon(Transform model, string weaponPath, bool right)
        {
            GameObject weaponAsset = AssetDatabase.LoadAssetAtPath<GameObject>(weaponPath);
            if (weaponAsset == null)
            {
                Debug.LogWarning("[Emberdelve][EnemyVisual] weapon model missing: " + weaponPath);
                return;
            }

            Transform hand = FindHandBone(model, right);
            if (hand == null)
            {
                Debug.LogWarning("[Emberdelve][EnemyVisual] no " + (right ? "right" : "left") + " hand bone in " + model.name + "; '" + weaponAsset.name + "' is attached to the model root.");
                hand = model;
            }

            GameObject weapon = (GameObject)PrefabUtility.InstantiatePrefab(weaponAsset);
            weapon.transform.SetParent(hand, false);
            weapon.transform.localPosition = Vector3.zero;
            weapon.transform.localRotation = Quaternion.identity;
        }

        // ---------------------------------------------------------------- animator controller

        private static AnimatorController BuildController(Variant variant, string fbxPath)
        {
            Dictionary<string, AnimationClip> clips = LoadClips(fbxPath);

            string path = ControllerPath(variant.Name);
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(path) != null) AssetDatabase.DeleteAsset(path);
            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(path);

            controller.AddParameter(EnemyVisualDriver.SpeedParameter, AnimatorControllerParameterType.Float);
            controller.AddParameter(new AnimatorControllerParameter { name = EnemyVisualDriver.AttackSpeedParameter, type = AnimatorControllerParameterType.Float, defaultFloat = 1f });
            controller.AddParameter(EnemyVisualDriver.AttackTrigger, AnimatorControllerParameterType.Trigger);
            controller.AddParameter(EnemyVisualDriver.HitTrigger, AnimatorControllerParameterType.Trigger);
            controller.AddParameter(EnemyVisualDriver.DeadParameter, AnimatorControllerParameterType.Bool);

            AnimatorStateMachine machine = controller.layers[0].stateMachine;

            AnimatorState idle = AddState(machine, "Idle", Pick(clips, variant.Idle, "idle"), new Vector3(300f, 0f, 0f));
            AnimatorState walk = AddState(machine, "Walk", Pick(clips, variant.Walk, "walking"), new Vector3(300f, 70f, 0f));
            AnimatorState run = AddState(machine, "Run", Pick(clips, variant.Run, "running"), new Vector3(300f, 140f, 0f));
            AnimatorState attack = AddState(machine, "Attack", Pick(clips, variant.Attack, "attack"), new Vector3(560f, 0f, 0f));
            AnimatorState hit = AddState(machine, "Hit", Pick(clips, variant.Hit, "hit"), new Vector3(560f, 70f, 0f));
            AnimatorState death = AddState(machine, "Death", Pick(clips, variant.Death, "death"), new Vector3(560f, 140f, 0f));
            machine.defaultState = idle;

            // The attack plays at the speed the driver asks for (clip length matched to the brain's windup + swing).
            attack.speedParameterActive = true;
            attack.speedParameter = EnemyVisualDriver.AttackSpeedParameter;

            // Locomotion by measured ground speed (m/s). Hysteresis keeps a noisy speed from flickering.
            AddSpeed(idle, walk, AnimatorConditionMode.Greater, 0.2f);
            AddSpeed(walk, idle, AnimatorConditionMode.Less, 0.1f);
            AddSpeed(walk, run, AnimatorConditionMode.Greater, 1.9f);
            AddSpeed(run, walk, AnimatorConditionMode.Less, 1.6f);
            AddSpeed(idle, run, AnimatorConditionMode.Greater, 1.9f);
            AddSpeed(run, idle, AnimatorConditionMode.Less, 0.1f);

            // Attack / Hit interrupt anything alive, and return to Idle when they have played.
            AnimatorStateTransition anyAttack = machine.AddAnyStateTransition(attack);
            ConfigureAny(anyAttack, 0.08f);
            anyAttack.AddCondition(AnimatorConditionMode.If, 0f, EnemyVisualDriver.AttackTrigger);
            anyAttack.AddCondition(AnimatorConditionMode.IfNot, 0f, EnemyVisualDriver.DeadParameter);

            AnimatorStateTransition anyHit = machine.AddAnyStateTransition(hit);
            ConfigureAny(anyHit, 0.05f);
            anyHit.AddCondition(AnimatorConditionMode.If, 0f, EnemyVisualDriver.HitTrigger);
            anyHit.AddCondition(AnimatorConditionMode.IfNot, 0f, EnemyVisualDriver.DeadParameter);

            AnimatorStateTransition anyDeath = machine.AddAnyStateTransition(death);
            ConfigureAny(anyDeath, 0.1f);
            anyDeath.AddCondition(AnimatorConditionMode.If, 0f, EnemyVisualDriver.DeadParameter);

            AddExit(attack, idle, 0.9f, 0.15f);
            AddExit(hit, idle, 0.85f, 0.1f);

            EditorUtility.SetDirty(controller);
            return controller;
        }

        private static AnimatorState AddState(AnimatorStateMachine machine, string name, AnimationClip clip, Vector3 position)
        {
            AnimatorState state = machine.AddState(name, position);
            state.motion = clip;
            return state;
        }

        private static void AddSpeed(AnimatorState from, AnimatorState to, AnimatorConditionMode mode, float threshold)
        {
            AnimatorStateTransition transition = from.AddTransition(to);
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = 0.12f;
            transition.AddCondition(mode, threshold, EnemyVisualDriver.SpeedParameter);
        }

        private static void ConfigureAny(AnimatorStateTransition transition, float duration)
        {
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = duration;
            transition.canTransitionToSelf = false;
        }

        private static void AddExit(AnimatorState from, AnimatorState to, float exitTime, float duration)
        {
            AnimatorStateTransition transition = from.AddTransition(to);
            transition.hasExitTime = true;
            transition.exitTime = exitTime;
            transition.hasFixedDuration = true;
            transition.duration = duration;
        }

        private static Dictionary<string, AnimationClip> LoadClips(string fbxPath)
        {
            Dictionary<string, AnimationClip> clips = new Dictionary<string, AnimationClip>();
            Object[] assets = AssetDatabase.LoadAllAssetsAtPath(fbxPath);
            for (int i = 0; i < assets.Length; i++)
            {
                AnimationClip clip = assets[i] as AnimationClip;
                if (clip == null || clip.name.StartsWith("__preview__")) continue;
                clips[clip.name] = clip;
            }

            return clips;
        }

        /// <summary>The clip with the exact take name; otherwise the first clip whose name contains the fallback text; otherwise null (logged).</summary>
        private static AnimationClip Pick(Dictionary<string, AnimationClip> clips, string exact, string fallbackContains)
        {
            if (clips.TryGetValue(exact, out AnimationClip clip)) return clip;

            foreach (KeyValuePair<string, AnimationClip> pair in clips)
            {
                if (pair.Key.IndexOf(fallbackContains, System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Debug.LogWarning("[Emberdelve][EnemyVisual] clip '" + exact + "' not found; using '" + pair.Key + "'.");
                    return pair.Value;
                }
            }

            Debug.LogError("[Emberdelve][EnemyVisual] clip '" + exact + "' not found and nothing contains '" + fallbackContains + "'.");
            return null;
        }

        // ---------------------------------------------------------------- scene hookup

        /// <summary>
        /// Adds the visual prefab as the child "Visual" of <paramref name="enemy"/> and switches the cube renderers (Body, Head)
        /// off. The cubes and their colliders stay exactly as the builder made them: the combat code reads those colliders, and
        /// the runtime smoke test still reads the Body renderer's material (a disabled renderer keeps its material).
        /// Returns the visual, or null (cubes stay visible) when the prefab does not exist.
        /// </summary>
        public static GameObject Attach(GameObject enemy, EnemyData data, int spawnIndex)
        {
            if (enemy == null) return null;

            string name = VariantFor(data, spawnIndex);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath(name));
            if (prefab == null) return null;

            GameObject visual = (GameObject)PrefabUtility.InstantiatePrefab(prefab, enemy.transform);
            visual.name = VisualChildName;
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one * (data != null && data.isElite ? EliteScale : 1f);

            string[] cubes = { "Body", "Head" };
            for (int i = 0; i < cubes.Length; i++)
            {
                Transform cube = enemy.transform.Find(cubes[i]);
                Renderer renderer = cube != null ? cube.GetComponent<Renderer>() : null;
                if (renderer != null) renderer.enabled = false;
            }

            return visual;
        }
    }
}
