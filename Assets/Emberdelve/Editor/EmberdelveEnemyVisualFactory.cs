using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Emberdelve.AI;
using Emberdelve.Data;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// V3b (was V2 KayKit skeletons): the enemy models are the CC0 Quaternius humanoid mannequin turned into grim undead
    /// (<see cref="EmberdelveUndeadMaterials"/>: rotting flesh, rags, rusty metal, glowing eyes on the Mage only) and animated with the
    /// Humanoid clips of the Universal Animation Library 1 + 2 (<see cref="EmberdelveQuaterniusImport"/>).
    ///
    /// Per variant this builds an AnimatorController (Idle / Walk / Run / Attack / Hit / Death, parameters Speed, AttackSpeed, Attack,
    /// Hit, Dead: the contract <see cref="EnemyVisualDriver"/> drives, unchanged) and the prefab
    /// Assets/Emberdelve/Prefabs/Visual/EnemyVisual_&lt;Variant&gt;.prefab: Model (mannequin, Animator with the Humanoid avatar, derived
    /// mesh + variant material), the weapons on the humanoid hand bones, an EnemyVisualDriver. The level builder attaches the prefab as
    /// the child "Visual" of each enemy and switches the cube renderers off (the cubes stay: they carry the colliders the combat code
    /// uses). Nothing of the visual collides; the swing / windup timing is the brain's, the attack clip speed is matched by the driver.
    /// V3c: the weapons are the Poly Haven CC0 models of the view-model set (estoc, dagger, hatchet, kite shield: <see cref="EmberdelveWeaponModels"/>,
    /// normalised: grip at the origin, tip along +Y), tinted with rusted copies of their own materials. The model's root yaw is identity: the
    /// live Animator (root motion off) faces the model's +Z, and the grips are derived in that live frame (V3b had the model turned 180 degrees,
    /// which showed the animated enemies from behind in Play).
    /// </summary>
    public static class EmberdelveEnemyVisualFactory
    {
        public const string Folder = "Assets/Emberdelve/Prefabs/Visual";
        public const string VisualChildName = "Visual";

        /// <summary>Height of the model after fit (metres). The old blockout was 2.07 m (head top); hit volumes are unchanged.</summary>
        public const float NormalHeight = 1.9f;

        /// <summary>The elite is scaled up like its hit volumes (1.25).</summary>
        public const float EliteScale = 1.25f;

        public const string MaleBody = EmberdelveQuaterniusImport.Ual1Path;
        public const string FemaleBody = EmberdelveQuaterniusImport.MannequinFPath;

        public const string EyeNameLeft = "Eye_L";
        public const string EyeNameRight = "Eye_R";

        public sealed class Variant
        {
            public string Name;
            public string Body;            // mannequin FBX (mesh, bones, avatar)
            public string MeshName;        // derived mesh asset
            public string MainHand;        // name of the weapon object under the right hand bone (null = none)
            public string MainHandSource;  // weapon model id of the view-model set (Poly Haven asset id)
            public float MainHandLength;   // metres, tip to pommel
            public string OffHand;         // name of the shield object under the left hand bone (null = none)
            public string OffHandSource;
            public float OffHandSize;      // metres, shield diameter
            public bool GlowingEyes;
            public bool Pauldrons;
            // Humanoid clip names (without the "Rig|" / "Armature|" take prefix).
            public string Idle, Walk, Run, Attack, Hit, Death;
            public float WalkPlayback = 1f, RunPlayback = 1f;
        }

        public static readonly Variant[] Variants =
        {
            new Variant { Name = "Minion", Body = MaleBody, MeshName = "Undead_Mannequin_M", MainHand = "Weapon_RustyBlade", MainHandSource = "antique_estoc", MainHandLength = 0.85f,
                Idle = "Sword_Idle", Walk = "Zombie_Walk_Fwd_Loop", Run = "Zombie_Walk_Fwd_Loop", RunPlayback = 1.7f, Attack = "Sword_Regular_A", Hit = "Hit_Chest", Death = "Death01" },
            new Variant { Name = "Rogue", Body = FemaleBody, MeshName = "Undead_Mannequin_F", MainHand = "Weapon_RustyDagger", MainHandSource = "ornate_medieval_dagger", MainHandLength = 0.38f,
                Idle = "Sword_Idle", Walk = "Walk_Loop", Run = "Jog_Fwd_Loop", Attack = "Sword_Regular_A", Hit = "Hit_Head", Death = "Death01" },
            new Variant { Name = "Warrior", Body = MaleBody, MeshName = "Undead_Mannequin_M", MainHand = "Weapon_RustyAxe", MainHandSource = "hatchet", MainHandLength = 0.4f,
                OffHand = "Weapon_RustyShield", OffHandSource = "kite_shield", OffHandSize = 0.7f, Pauldrons = true,
                Idle = "Idle_Shield_Loop", Walk = "Walk_Formal_Loop", Run = "Jog_Fwd_Loop", Attack = "Sword_Regular_A", Hit = "Hit_Chest", Death = "Death01" },
            new Variant { Name = "Mage", Body = MaleBody, MeshName = "Undead_Mannequin_M", GlowingEyes = true,
                Idle = "Spell_Simple_Idle_Loop", Walk = "Walk_Formal_Loop", Run = "Jog_Fwd_Loop", Attack = "Spell_Simple_Shoot", Hit = "Hit_Chest", Death = "Death01" },
        };

        public static readonly string[] StateNames = { "Idle", "Walk", "Run", "Attack", "Hit", "Death" };

        /// <summary>Clip the weapon grip is derived from (blade held up-forward) and the clip the shield grip is derived from (guard).</summary>
        private const string SwordGripPose = "Sword_Idle";
        private const string ShieldGripPose = "Idle_Shield_Loop";

        // ---------------------------------------------------------------- mapping

        /// <summary>
        /// Enemy data -> variant. Elites (the Ashen Warden) are Warriors with axe and shield. Normal enemies (the Hollow
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
        public static string AttackClipPath(string variant) { return Folder + "/EnemyVisual_" + variant + "_Attack.anim"; }

        public static Variant Find(string name)
        {
            for (int i = 0; i < Variants.Length; i++) if (Variants[i].Name == name) return Variants[i];
            return null;
        }

        // ---------------------------------------------------------------- build

        private static bool _done;

        /// <summary>Builds every controller and prefab (once per editor session unless forced). False when the Quaternius files are missing.</summary>
        public static bool EnsureAll(bool force)
        {
            if (_done && !force) return AllPrefabsExist();

            if (!EmberdelveQuaterniusImport.EnsureConfigured())
            {
                Debug.LogWarning("[Emberdelve][EnemyVisual] the Quaternius files are missing; enemy visuals are not built (the blockout cubes stay).");
                return false;
            }

            EmberdelveWeaponModels.EnsureAll(false);
            EmberdelveUndeadMaterials.EnsureAll();
            if (!EmberdelveSkeletonAssets.EnsureConfigured()) return false;
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
            GameObject fbx = AssetDatabase.LoadAssetAtPath<GameObject>(EmberdelveSkeletonAssets.ModelPath);
            SkinnedMeshRenderer source = fbx != null ? fbx.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
            Mesh mesh = source != null ? source.sharedMesh : null;
            Material bodyMaterial = EmberdelveSkeletonAssets.MaterialFor(variant.Name);
            if (fbx == null || mesh == null || bodyMaterial == null)
            {
                Debug.LogWarning("[Emberdelve][EnemyVisual] variant " + variant.Name + " skipped: model, derived mesh or material missing.");
                return false;
            }

            AnimatorController controller = BuildController(variant);
            if (controller == null) return false;

            Avatar avatar = FindAvatar(EmberdelveSkeletonAssets.ModelPath);
            GameObject root = new GameObject("EnemyVisual_" + variant.Name);
            try
            {
                GameObject model = CreateModel(fbx, root.transform, mesh, bodyMaterial, avatar, controller);
                FitHeight(model.transform, NormalHeight);
                Animator animator = model.GetComponent<Animator>();

                Material rust = EmberdelveUndeadMaterials.Load(EmberdelveUndeadMaterials.RustyMetalPath);
                if (!string.IsNullOrEmpty(variant.MainHand)) AttachMainHand(variant, fbx, mesh, bodyMaterial, avatar, animator);
                if (!string.IsNullOrEmpty(variant.OffHand)) AttachOffHand(variant, fbx, mesh, bodyMaterial, avatar, animator);
                if (variant.Pauldrons) AddPauldrons(animator, rust);
                if (variant.GlowingEyes) AddEyesFromLivePose(fbx, mesh, bodyMaterial, avatar, animator, variant.Idle);

                // Built facing +Z (bind pose turned), shipped with identity yaw: the live pose faces +Z by itself.
                model.transform.localRotation = Quaternion.identity;

                // Pure visuals: nothing of the model may collide.
                Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < colliders.Length; i++) Object.DestroyImmediate(colliders[i]);

                EnemyVisualDriver driver = root.AddComponent<EnemyVisualDriver>();
                ParticleSystem blood = EmberdelveBloodEffect.Create(root.transform);
                SerializedObject serialized = new SerializedObject(driver);
                SerializedProperty animatorProperty = serialized.FindProperty("animator");
                if (animatorProperty != null) animatorProperty.objectReferenceValue = animator;
                serialized.FindProperty("bloodEffect").objectReferenceValue = blood;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath(variant.Name));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }

            return true;
        }

        private static GameObject CreateModel(GameObject fbx, Transform parent, Mesh mesh, Material material, Avatar avatar, RuntimeAnimatorController controller, float yaw = 180f)
        {
            GameObject model = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            model.name = "Model";
            model.transform.SetParent(parent, false);
            // The bind pose faces -Z (toes point to -Z); the build turns it to +Z to place eyes / pauldrons, the shipped prefab has yaw 0.
            model.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            PrefabUtility.UnpackPrefabInstance(model, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

            Animator animator = model.GetComponent<Animator>();
            if (animator == null) animator = model.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            if (avatar != null) animator.avatar = avatar;

            SkinnedMeshRenderer[] skinned = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinned.Length; i++)
            {
                skinned[i].sharedMesh = mesh;
                skinned[i].sharedMaterials = new[] { material };
                skinned[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }

            return model;
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

        // ---------------------------------------------------------------- bones

        /// <summary>
        /// Hand bone: the Humanoid avatar's hand when the model has an Animator with a human avatar, else a non-IK bone whose name
        /// contains "hand" on the right / left side ("DEF-hand.R", "hand_r"), else null.
        /// </summary>
        public static Transform FindHandBone(Transform model, bool right)
        {
            Animator animator = model != null ? model.GetComponentInChildren<Animator>(true) : null;
            if (animator != null && animator.avatar != null && animator.avatar.isHuman)
            {
                Transform bone = animator.GetBoneTransform(right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
                if (bone != null) return bone;
            }

            Transform[] all = model.GetComponentsInChildren<Transform>(true);
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

        // ---------------------------------------------------------------- weapons, armour, eyes

        /// <summary>
        /// A weapon: wrapper object (the name the gate / tests look for) around the Poly Haven model of the view-model set (grip at the origin, tip
        /// along +Y, face along +Z), sized in metres (uniformly: <paramref name="longest"/> is the length from the grip to the far end, the height
        /// of a shield) and re-skinned with the rusted copies of its own materials.
        /// </summary>
        private static GameObject CreateAccessory(string name, string weaponId, float longest)
        {
            GameObject source = EmberdelveWeaponModels.LoadPrefab(weaponId);
            EmberdelveWeaponModels.Spec spec = EmberdelveWeaponModels.Find(weaponId);
            if (source == null || spec == null)
            {
                Debug.LogWarning("[Emberdelve][EnemyVisual] weapon model missing: " + weaponId);
                return null;
            }

            GameObject wrapper = new GameObject(name);
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
            instance.transform.SetParent(wrapper.transform, false);
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

            Dictionary<string, Material> rusty = EmberdelveWeaponModels.RustyMaterials(weaponId);
            Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Material[] materials = renderers[i].sharedMaterials;
                for (int m = 0; m < materials.Length; m++)
                {
                    Material replacement;
                    if (materials[m] != null && rusty.TryGetValue(materials[m].name, out replacement)) materials[m] = replacement;
                }

                renderers[i].sharedMaterials = materials;
                renderers[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }

            Collider[] colliders = wrapper.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++) Object.DestroyImmediate(colliders[i]);

            wrapper.transform.localScale = Vector3.one * (longest / Mathf.Max(0.01f, spec.Length));
            return wrapper;
        }

        /// <summary>A temporary copy of the model, posed by a humanoid clip, to read where the hand is in the pose the weapon is meant for.</summary>
        private static GameObject PosedCopy(GameObject fbx, Mesh mesh, Material material, Avatar avatar, float scale, string clipName)
        {
            GameObject copy = CreateModel(fbx, null, mesh, material, avatar, null, 0f);
            copy.transform.localScale = Vector3.one * scale;
            AnimationClip clip = FindClip(clipName);
            // The live frame: the pose the runtime Animator shows (faces +Z), see EmberdelveFirstPersonArms.Evaluate.
            if (clip != null) EmberdelveFirstPersonArms.Evaluate(copy.GetComponent<Animator>(), clip, Mathf.Min(0.2f, clip.length * 0.5f));
            return copy;
        }

        private static void AttachMainHand(Variant variant, GameObject fbx, Mesh mesh, Material material, Avatar avatar, Animator animator)
        {
            GameObject weapon = CreateAccessory(variant.MainHand, variant.MainHandSource, variant.MainHandLength);
            if (weapon == null) return;

            float scale = animator.transform.localScale.x;
            GameObject posed = PosedCopy(fbx, mesh, material, avatar, scale, SwordGripPose);
            try
            {
                Animator posedAnimator = posed.GetComponent<Animator>();
                Transform hand = posedAnimator.GetBoneTransform(HumanBodyBones.RightHand);
                Transform knuckle = posedAnimator.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
                Transform finalHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
                if (hand == null || finalHand == null) { Object.DestroyImmediate(weapon); return; }

                // Blade up and forward (in the model's frame), flat side facing the model's right, grip between wrist and knuckles.
                Vector3 up = Vector3.up, forward = Vector3.forward, right = Vector3.right; // the posed copy stands at the origin; the character faces +Z
                Vector3 tip = (up + forward * 0.45f).normalized;
                Vector3 flat = Vector3.ProjectOnPlane(right, tip).normalized;
                Quaternion world = Quaternion.LookRotation(flat, tip);
                Vector3 grip = knuckle != null ? Vector3.Lerp(hand.position, knuckle.position, 0.75f) : HandSurfaceCentre(posedAnimator, hand);

                weapon.transform.SetParent(finalHand, false);
                weapon.transform.localPosition = hand.InverseTransformPoint(grip);
                weapon.transform.localRotation = Quaternion.Inverse(hand.rotation) * world;
                weapon.transform.localScale = DivideScale(weapon.transform.localScale, finalHand.lossyScale, 1f);
            }
            finally
            {
                Object.DestroyImmediate(posed);
            }
        }

        private static void AttachOffHand(Variant variant, GameObject fbx, Mesh mesh, Material material, Avatar avatar, Animator animator)
        {
            GameObject shield = CreateAccessory(variant.OffHand, variant.OffHandSource, variant.OffHandSize);
            if (shield == null) return;

            float scale = animator.transform.localScale.x;
            GameObject posed = PosedCopy(fbx, mesh, material, avatar, scale, ShieldGripPose);
            try
            {
                Animator posedAnimator = posed.GetComponent<Animator>();
                Transform hand = posedAnimator.GetBoneTransform(HumanBodyBones.LeftHand);
                Transform finalHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
                if (hand == null || finalHand == null) { Object.DestroyImmediate(shield); return; }

                // Face forward (slightly outward), upright; centred a little in front of the hand.
                Vector3 facing = (Vector3.forward - Vector3.right * 0.3f).normalized;
                Quaternion world = Quaternion.LookRotation(facing, Vector3.up);
                Vector3 centre = HandSurfaceCentre(posedAnimator, hand) + facing * 0.09f * scale / 0.98f;

                shield.transform.SetParent(finalHand, false);
                shield.transform.localPosition = hand.InverseTransformPoint(centre);
                shield.transform.localRotation = Quaternion.Inverse(hand.rotation) * world;
                shield.transform.localScale = DivideScale(shield.transform.localScale, finalHand.lossyScale, 1f);
            }
            finally
            {
                Object.DestroyImmediate(posed);
            }
        }

        // Some humanoid rigs have no finger bones. Estimate the palm from vertices weighted to the hand
        // in the actual grip pose instead of attaching a weapon at the wrist.
        private static Vector3 HandSurfaceCentre(Animator animator, Transform hand)
        {
            var renderer = animator.GetComponentInChildren<SkinnedMeshRenderer>();
            if (renderer == null || renderer.sharedMesh == null || !renderer.sharedMesh.isReadable) return hand.position;
            int boneIndex = System.Array.IndexOf(renderer.bones, hand);
            if (boneIndex < 0) return hand.position;
            var weights = renderer.sharedMesh.boneWeights;
            var baked = new Mesh();
            try
            {
                renderer.BakeMesh(baked);
                var vertices = baked.vertices;
                Vector3 sum = Vector3.zero;
                int count = 0;
                for (int i = 0; i < weights.Length && i < vertices.Length; i++)
                {
                    BoneWeight w = weights[i];
                    float weight = (w.boneIndex0 == boneIndex ? w.weight0 : 0f) +
                        (w.boneIndex1 == boneIndex ? w.weight1 : 0f) +
                        (w.boneIndex2 == boneIndex ? w.weight2 : 0f) +
                        (w.boneIndex3 == boneIndex ? w.weight3 : 0f);
                    if (weight < 0.5f) continue;
                    sum += renderer.transform.TransformPoint(vertices[i]);
                    count++;
                }
                return count > 0 ? sum / count : hand.position;
            }
            finally { Object.DestroyImmediate(baked); }
        }

        /// <summary>Wanted world scale / the bone's lossy scale, per axis (so the accessory has the intended size under a scaled skeleton).</summary>
        private static Vector3 DivideScale(Vector3 wanted, Vector3 lossy, float multiplier)
        {
            return new Vector3(wanted.x / Mathf.Max(0.0001f, lossy.x), wanted.y / Mathf.Max(0.0001f, lossy.y), wanted.z / Mathf.Max(0.0001f, lossy.z)) * multiplier;
        }

        private static void AddPauldrons(Animator animator, Material rust)
        {
            Mesh sphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
            float scale = animator.transform.localScale.x;
            HumanBodyBones[] bones = { HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm };
            string[] names = { "Pauldron_L", "Pauldron_R" };
            for (int i = 0; i < bones.Length; i++)
            {
                Transform bone = animator.GetBoneTransform(bones[i]);
                if (bone == null) continue;

                // Holder with a uniform scale under the bone (no shear), the squashed sphere inside it.
                GameObject pauldron = new GameObject(names[i]);
                pauldron.transform.position = bone.position + Vector3.up * 0.035f * scale + (i == 0 ? Vector3.left : Vector3.right) * 0.02f * scale;
                pauldron.transform.rotation = Quaternion.identity;
                pauldron.transform.SetParent(bone, true);

                GameObject dome = new GameObject("Dome");
                dome.AddComponent<MeshFilter>().sharedMesh = sphere;
                dome.AddComponent<MeshRenderer>().sharedMaterial = rust;
                dome.transform.SetParent(pauldron.transform, false);
                dome.transform.localScale = DivideScale(new Vector3(0.12f, 0.06f, 0.13f) * scale, pauldron.transform.lossyScale, 1f);
            }
        }

        private static void AddEyesFromLivePose(GameObject fbx, Mesh mesh, Material material, Avatar avatar, Animator animator, string pose)
        {
            GameObject posed = PosedCopy(fbx, mesh, material, avatar, animator.transform.localScale.x, pose);
            try
            {
                Animator sampled = posed.GetComponent<Animator>();
                AddEyes(sampled);
                Transform sourceHead = sampled.GetBoneTransform(HumanBodyBones.Head);
                Transform targetHead = animator.GetBoneTransform(HumanBodyBones.Head);
                if (sourceHead == null || targetHead == null) return;
                foreach (string eyeName in new[] { EyeNameLeft, EyeNameRight })
                {
                    Transform eye = sourceHead.Find(eyeName);
                    if (eye == null) continue;
                    eye.SetParent(targetHead, false);
                }
            }
            finally { Object.DestroyImmediate(posed); }
        }

        private static void AddEyes(Animator animator)
        {
            Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
            if (head == null) return;

            Mesh sphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
            Material glow = EmberdelveUndeadMaterials.Load(EmberdelveUndeadMaterials.EyeGlowPath);
            SkinnedMeshRenderer body = animator.GetComponentInChildren<SkinnedMeshRenderer>(true);
            float scale = animator.transform.localScale.x;

            // Head geometry from the baked bind pose: top of the skull, and the face front at eye height.
            Mesh baked = new Mesh();
            body.BakeMesh(baked, false);
            Vector3[] vertices = baked.vertices;
            Matrix4x4 toWorld = body.transform.localToWorldMatrix;
            float headY = head.position.y;
            float top = headY;
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 w = toWorld.MultiplyPoint3x4(vertices[i]);
                if (w.y > headY - 0.01f && w.y > top) top = w.y;
            }

            float eyeY = headY + (top - headY) * 0.36f; // the mannequin's eye sockets sit about a third up the head
            float front = head.position.z;
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 w = toWorld.MultiplyPoint3x4(vertices[i]);
                if (Mathf.Abs(w.x) < 0.04f * scale && Mathf.Abs(w.y - eyeY) < 0.03f * scale && w.z > front) front = w.z;
            }

            Object.DestroyImmediate(baked);

            string[] names = { EyeNameLeft, EyeNameRight };
            for (int i = 0; i < 2; i++)
            {
                GameObject eye = new GameObject(names[i]);
                eye.AddComponent<MeshFilter>().sharedMesh = sphere;
                MeshRenderer renderer = eye.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = glow;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                eye.transform.position = new Vector3((i == 0 ? -1f : 1f) * 0.032f * scale, eyeY, front - 0.007f * scale);
                eye.transform.localScale = Vector3.one * 0.02f * scale;
                eye.transform.SetParent(head, true);
            }
        }

        // ---------------------------------------------------------------- animator controller

        private static Dictionary<string, AnimationClip> _clipCache;

        /// <summary>Every humanoid clip of both libraries by bare name ("Sword_Attack"; the "Rig|" / "Armature|" take prefix is dropped).</summary>
        private static Dictionary<string, AnimationClip> Clips()
        {
            if (_clipCache != null && _clipCache.Count > 0)
            {
                foreach (KeyValuePair<string, AnimationClip> pair in _clipCache) { if (pair.Value == null) { _clipCache = null; } break; }
            }

            if (_clipCache != null && _clipCache.Count > 0) return _clipCache;

            _clipCache = new Dictionary<string, AnimationClip>();
            string[] files = { EmberdelveQuaterniusImport.Ual1Path, EmberdelveQuaterniusImport.Ual2Path };
            for (int f = 0; f < files.Length; f++)
            {
                Object[] assets = AssetDatabase.LoadAllAssetsAtPath(files[f]);
                for (int i = 0; i < assets.Length; i++)
                {
                    AnimationClip clip = assets[i] as AnimationClip;
                    if (clip == null || clip.name.StartsWith("__preview__")) continue;
                    int bar = clip.name.IndexOf('|');
                    string bare = bar >= 0 ? clip.name.Substring(bar + 1) : clip.name;
                    if (!_clipCache.ContainsKey(bare)) _clipCache[bare] = clip;
                }
            }

            return _clipCache;
        }

        public static AnimationClip FindClip(string bareName)
        {
            AnimationClip clip;
            return Clips().TryGetValue(bareName, out clip) ? clip : null;
        }

        private static AnimatorController BuildController(Variant variant)
        {
            string path = ControllerPath(variant.Name);
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(path) != null) AssetDatabase.DeleteAsset(path);
            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(path);

            controller.AddParameter(EnemyVisualDriver.SpeedParameter, AnimatorControllerParameterType.Float);
            controller.AddParameter(new AnimatorControllerParameter { name = EnemyVisualDriver.AttackSpeedParameter, type = AnimatorControllerParameterType.Float, defaultFloat = 1f });
            controller.AddParameter(EnemyVisualDriver.AttackTrigger, AnimatorControllerParameterType.Trigger);
            controller.AddParameter(EnemyVisualDriver.HitTrigger, AnimatorControllerParameterType.Trigger);
            controller.AddParameter(EnemyVisualDriver.DeadParameter, AnimatorControllerParameterType.Bool);

            AnimatorStateMachine machine = controller.layers[0].stateMachine;

            AnimatorState idle = AddState(machine, "Idle", Require(variant.Idle), new Vector3(300f, 0f, 0f));
            AnimatorState walk = AddState(machine, "Walk", Require(variant.Walk), new Vector3(300f, 70f, 0f));
            AnimatorState run = AddState(machine, "Run", Require(variant.Run), new Vector3(300f, 140f, 0f));
            AnimatorState attack = AddState(machine, "Attack", AttackCopy(variant), new Vector3(560f, 0f, 0f));
            AnimatorState hit = AddState(machine, "Hit", Require(variant.Hit), new Vector3(560f, 70f, 0f));
            AnimatorState death = AddState(machine, "Death", Require(variant.Death), new Vector3(560f, 140f, 0f));
            machine.defaultState = idle;
            walk.speed = variant.WalkPlayback;
            run.speed = variant.RunPlayback;

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

        private static AnimationClip Require(string bareName)
        {
            AnimationClip clip = FindClip(bareName);
            if (clip == null) Debug.LogError("[Emberdelve][EnemyVisual] humanoid clip '" + bareName + "' not found in the Quaternius libraries.");
            return clip;
        }

        /// <summary>
        /// The attack clip as a project asset named "Attack": EnemyVisualDriver finds the clip length by the name containing "Attack"
        /// (the library takes are named after the move, e.g. "Spell_Simple_Shoot"). Same curves and settings as the library take.
        /// </summary>
        private static AnimationClip AttackCopy(Variant variant)
        {
            AnimationClip source = Require(variant.Attack);
            if (source == null) return null;

            string path = AttackClipPath(variant.Name);
            if (AssetDatabase.LoadAssetAtPath<AnimationClip>(path) != null) AssetDatabase.DeleteAsset(path);

            AnimationClip copy = Object.Instantiate(source);
            copy.name = "Attack";
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(source);
            settings.loopTime = false;
            settings.loopBlendOrientation = true;
            settings.loopBlendPositionY = true;
            // Extract horizontal travel instead of baking a lunge into the skeleton.
            // Root motion is disabled: authority owns movement and collider placement.
            settings.loopBlendPositionXZ = false;
            AnimationUtility.SetAnimationClipSettings(copy, settings);
            AssetDatabase.CreateAsset(copy, path);
            return copy;
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
