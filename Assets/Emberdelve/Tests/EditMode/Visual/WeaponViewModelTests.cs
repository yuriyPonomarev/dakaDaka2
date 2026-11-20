using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Emberdelve.Data;
using Emberdelve.EditorTools;
using Emberdelve.Player;

namespace Emberdelve.Tests.Visual
{
    /// <summary>
    /// V3c: the Poly Haven weapon models, the first-person arms and the enemy weapons. Expectations come from the data the editor tools
    /// publish (specs, mapping, state names, parameter names) and from the WeaponData assets, never from source text.
    /// </summary>
    public sealed class WeaponViewModelTests
    {
        private static WeaponViewModelSet Set()
        {
            WeaponViewModelSet set = WeaponViewModelSet.Load();
            Assert.IsNotNull(set, "WeaponViewModelSet exists (build with EmberdelveWeaponModels.EnsureAll)");
            return set;
        }

        private static IEnumerable<string> WeaponIds()
        {
            string[] guids = AssetDatabase.FindAssets("t:WeaponData");
            for (int i = 0; i < guids.Length; i++)
            {
                WeaponData weapon = AssetDatabase.LoadAssetAtPath<WeaponData>(AssetDatabase.GUIDToAssetPath(guids[i]));
                if (weapon != null) yield return weapon.weaponId;
            }
        }

        private static IEnumerable<string> ModelIds()
        {
            for (int i = 0; i < EmberdelveWeaponModels.Specs.Length; i++) yield return EmberdelveWeaponModels.Specs[i].Id;
        }

        private static IEnumerable<string> VariantNames()
        {
            for (int i = 0; i < EmberdelveEnemyVisualFactory.Variants.Length; i++) yield return EmberdelveEnemyVisualFactory.Variants[i].Name;
        }

        // ---------------------------------------------------------------- mapping

        [TestCaseSource(nameof(WeaponIds))]
        public void EveryWeaponData_MapsToAModel_OrIsExplicitlyUnmappedWithAReason(string weaponId)
        {
            WeaponViewModelSet set = Set();
            bool mapped = set.Find(weaponId) != null;
            bool unmapped = set.IsUnmapped(weaponId);
            Assert.AreNotEqual(mapped, unmapped, weaponId + " is mapped xor unmapped");

            if (unmapped)
            {
                for (int i = 0; i < set.unmapped.Length; i++)
                {
                    if (set.unmapped[i].weaponId == weaponId) Assert.IsFalse(string.IsNullOrWhiteSpace(set.unmapped[i].reason), weaponId + " has a reason");
                }
            }
        }

        [Test]
        public void Mapping_OnlyNamesRealWeaponsAndRealModels()
        {
            HashSet<string> weapons = new HashSet<string>(WeaponIds());
            for (int i = 0; i < EmberdelveWeaponModels.Mapping.Length; i++)
            {
                string[] pair = EmberdelveWeaponModels.Mapping[i];
                Assert.IsTrue(weapons.Contains(pair[0]), pair[0] + " is a WeaponData id");
                Assert.IsNotNull(EmberdelveWeaponModels.Find(pair[1]), pair[1] + " is a model spec");
            }
        }

        // ---------------------------------------------------------------- models

        [TestCaseSource(nameof(ModelIds))]
        public void Model_IsNormalised_UrpLit_AndHasNoCollider(string id)
        {
            EmberdelveWeaponModels.Spec spec = EmberdelveWeaponModels.Find(id);
            GameObject prefab = EmberdelveWeaponModels.LoadPrefab(id);
            Assert.IsNotNull(prefab, id + " prefab");
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                Assert.AreEqual(0, instance.GetComponentsInChildren<Collider>(true).Length, "no colliders");
                Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
                Assert.Greater(renderers.Length, 0);
                Bounds bounds = renderers[0].bounds;
                for (int r = 0; r < renderers.Length; r++)
                {
                    bounds.Encapsulate(renderers[r].bounds);
                    Material[] materials = renderers[r].sharedMaterials;
                    for (int m = 0; m < materials.Length; m++)
                    {
                        Assert.IsNotNull(materials[m], renderers[r].name);
                        Assert.AreEqual("Universal Render Pipeline/Lit", materials[m].shader.name, renderers[r].name);
                        Assert.IsNotNull(materials[m].GetTexture("_BaseMap"), renderers[r].name + " has an albedo");
                    }
                }

                // Convention: grip at the origin, the far end (tip / head) at +Y, spec.Length metres away. Shield: centred, spec.Length tall.
                float tolerance = spec.Length * 0.15f;
                if (spec.Shape == EmberdelveWeaponModels.Shape.Shield)
                {
                    Assert.AreEqual(spec.Length, bounds.size.y, tolerance, id + " height");
                    Assert.AreEqual(0f, bounds.center.y, spec.Length * 0.25f, id + " is centred on the boss");
                }
                else
                {
                    Assert.AreEqual(spec.Length, bounds.max.y, tolerance, id + " far end along +Y");
                    Assert.Less(bounds.min.y, spec.Length * 0.4f, id + " grip is near the origin, not at the far end");
                    Assert.Greater(bounds.size.y, Mathf.Max(bounds.size.x, bounds.size.z), id + " is longest along Y");
                }
            }
            finally { Object.DestroyImmediate(instance); }
        }

        [Test]
        public void SetEntries_MatchTheSpecs()
        {
            WeaponViewModelSet set = Set();
            Assert.AreEqual(EmberdelveWeaponModels.Mapping.Length + 1, set.entries.Length);
            for (int i = 0; i < set.entries.Length; i++)
            {
                if (set.entries[i].weaponId == HandheldTorchVisual.WeaponId)
                {
                    Assert.AreEqual(EmberdelveTorchContent.Length, set.entries[i].length, .001f);
                    Assert.AreSame(AssetDatabase.LoadAssetAtPath<GameObject>(EmberdelveTorchContent.PrefabPath), set.entries[i].prefab);
                    Assert.IsNotNull(set.entries[i].prefab.GetComponent<HandheldTorchVisual>());
                    continue;
                }
                EmberdelveWeaponModels.Spec spec = EmberdelveWeaponModels.Find(set.entries[i].sourceId);
                Assert.IsNotNull(spec, set.entries[i].weaponId);
                Assert.AreEqual(spec.Length, set.entries[i].length, 0.001f, set.entries[i].weaponId + " length");
                Assert.AreSame(EmberdelveWeaponModels.LoadPrefab(spec.Id), set.entries[i].prefab);
            }

            for (int i = 0; i < EmberdelveWeaponModels.Spare.Length; i++) CollectionAssert.Contains(set.spareModels, EmberdelveWeaponModels.Spare[i]);
        }

        // ---------------------------------------------------------------- arms

        private static GameObject InstantiateArms()
        {
            WeaponViewModelSet set = Set();
            Assert.IsNotNull(set.armsPrefab, "arms prefab");
            return (GameObject)PrefabUtility.InstantiatePrefab(set.armsPrefab);
        }

        [Test]
        public void Arms_HaveAHumanoidAvatar_NoCollider_AndTheDriver()
        {
            GameObject arms = InstantiateArms();
            try
            {
                Animator animator = arms.GetComponentInChildren<Animator>(true);
                Assert.IsNotNull(animator);
                Assert.IsNotNull(animator.avatar);
                Assert.IsTrue(animator.avatar.isHuman && animator.avatar.isValid, "Humanoid avatar");
                Assert.IsNotNull(animator.runtimeAnimatorController);
                Assert.AreEqual(AnimatorCullingMode.AlwaysAnimate, animator.cullingMode, "the arms are always in view of the local camera");
                Assert.IsFalse(animator.applyRootMotion);
                Assert.IsNotNull(arms.GetComponent<FirstPersonArmsDriver>());
                Assert.AreEqual(0, arms.GetComponentsInChildren<Collider>(true).Length, "no colliders");
            }
            finally { Object.DestroyImmediate(arms); }
        }

        [Test]
        public void Arms_Controller_HasTheSevenStates_WithClips_AndTheParameters()
        {
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(EmberdelveFirstPersonArms.ControllerPath);
            Assert.IsNotNull(controller);

            Dictionary<string, AnimatorControllerParameterType> parameters = new Dictionary<string, AnimatorControllerParameterType>();
            for (int i = 0; i < controller.parameters.Length; i++) parameters[controller.parameters[i].name] = controller.parameters[i].type;
            Assert.AreEqual(AnimatorControllerParameterType.Float, parameters[FirstPersonArmsDriver.SpeedParameter]);
            Assert.AreEqual(AnimatorControllerParameterType.Float, parameters[FirstPersonArmsDriver.WalkPlaybackParameter]);
            Assert.AreEqual(AnimatorControllerParameterType.Float, parameters[FirstPersonArmsDriver.SwingSpeedParameter]);
            Assert.AreEqual(AnimatorControllerParameterType.Bool, parameters[FirstPersonArmsDriver.BlockParameter]);
            Assert.AreEqual(AnimatorControllerParameterType.Trigger, parameters[FirstPersonArmsDriver.SwingHorizontalTrigger]);
            Assert.AreEqual(AnimatorControllerParameterType.Trigger, parameters[FirstPersonArmsDriver.SwingOverheadTrigger]);
            Assert.AreEqual(AnimatorControllerParameterType.Trigger, parameters[FirstPersonArmsDriver.ThrustTrigger]);
            Assert.AreEqual(AnimatorControllerParameterType.Trigger, parameters[FirstPersonArmsDriver.HitReactTrigger]);

            AnimatorStateMachine machine = controller.layers[0].stateMachine;
            Dictionary<string, AnimatorState> states = new Dictionary<string, AnimatorState>();
            for (int i = 0; i < machine.states.Length; i++) states[machine.states[i].state.name] = machine.states[i].state;
            Assert.AreEqual(FirstPersonArmsDriver.StateNames.Length, states.Count);
            Assert.AreEqual(7, states.Count);
            for (int i = 0; i < FirstPersonArmsDriver.StateNames.Length; i++)
            {
                string name = FirstPersonArmsDriver.StateNames[i];
                Assert.IsTrue(states.ContainsKey(name), "state " + name);
                Assert.IsNotNull(states[name].motion, name + " has a clip");
            }

            string[] swings = { FirstPersonArmsDriver.SwingHorizontalState, FirstPersonArmsDriver.SwingOverheadState, FirstPersonArmsDriver.ThrustState };
            for (int i = 0; i < swings.Length; i++)
            {
                Assert.IsTrue(states[swings[i]].speedParameterActive, swings[i] + " plays at the swing speed");
                Assert.AreEqual(FirstPersonArmsDriver.SwingSpeedParameter, states[swings[i]].speedParameter);
                Assert.IsFalse(((AnimationClip)states[swings[i]].motion).isLooping, swings[i] + " plays once");
                Assert.AreEqual(FirstPersonArmsDriver.SwingClipName(swings[i]), states[swings[i]].motion.name, "the driver reads the length by this name");
            }

            Assert.IsTrue(((AnimationClip)states[FirstPersonArmsDriver.IdleState].motion).isLooping);
            Assert.AreEqual(FirstPersonArmsDriver.IdleState, machine.defaultState.name);
            Assert.GreaterOrEqual(controller.layers.Length, 2, "off hand layer for the shield");
            Assert.IsNotNull(controller.layers[FirstPersonArmsDriver.OffHandLayer].avatarMask, "masked to the left arm");
        }

        [Test]
        public void Arms_Mesh_HasNoHeadTorsoOrLegs_AndPlainMaterials()
        {
            GameObject arms = InstantiateArms();
            try
            {
                SkinnedMeshRenderer skinned = arms.GetComponentInChildren<SkinnedMeshRenderer>(true);
                Assert.IsNotNull(skinned);
                Mesh mesh = skinned.sharedMesh;
                Assert.IsNotNull(mesh);
                Assert.AreEqual(2, mesh.subMeshCount, "sleeves + hands");
                Assert.AreEqual(2, skinned.sharedMaterials.Length);

                Transform[] bones = skinned.bones;
                BoneWeight[] weights = mesh.boneWeights;
                Animator animator = arms.GetComponentInChildren<Animator>(true);
                Transform leftArm = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                Transform rightArm = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
                Assert.AreEqual(mesh.vertexCount, weights.Length);
                for (int v = 0; v < weights.Length; v++)
                {
                    Transform bone = bones[EmberdelveFirstPersonArms.DominantBone(weights[v])];
                    bool arm = bone.IsChildOf(leftArm) || bone.IsChildOf(rightArm);
                    Assert.IsTrue(arm, "vertex " + v + " is skinned to '" + bone.name + "': head / torso / legs are not in the mesh");
                }

                Vector3 head = animator.GetBoneTransform(HumanBodyBones.Head).position;
                Vector3 hips = animator.GetBoneTransform(HumanBodyBones.Hips).position;
                Bounds bounds = mesh.bounds;
                Assert.Less(bounds.size.y, (head.y - hips.y) * 2f, "arms only: much smaller than a body");

                Material[] materials = skinned.sharedMaterials;
                for (int m = 0; m < materials.Length; m++)
                {
                    Assert.AreEqual("Universal Render Pipeline/Lit", materials[m].shader.name);
                    Assert.IsNotNull(materials[m].GetTexture("_BaseMap"));
                    StringAssert.StartsWith(EmberdelveFirstPersonArms.MaterialFolder, AssetDatabase.GetAssetPath(materials[m]));
                }
            }
            finally { Object.DestroyImmediate(arms); }
        }

        [Test]
        public void Arms_WeaponGrips_SitUnderTheHandBones_AndTheLivePoseFacesForward()
        {
            GameObject arms = InstantiateArms();
            try
            {
                Animator animator = arms.GetComponentInChildren<Animator>(true);
                FirstPersonArmsDriver driver = arms.GetComponent<FirstPersonArmsDriver>();
                SerializedObject serialized = new SerializedObject(driver);
                Transform gripRight = (Transform)serialized.FindProperty("gripRight").objectReferenceValue;
                Transform gripLeft = (Transform)serialized.FindProperty("gripLeft").objectReferenceValue;
                Assert.IsNotNull(gripRight);
                Assert.IsNotNull(gripLeft);
                Assert.AreSame(animator.GetBoneTransform(HumanBodyBones.RightHand), gripRight.parent, "the weapon grip is under the right hand bone");
                Assert.AreSame(animator.GetBoneTransform(HumanBodyBones.LeftHand), gripLeft.parent, "the shield grip is under the left hand bone");

                // The live pose (what the runtime Animator shows) faces the arms' +Z, with the right hand to the right of the left one.
                AnimationClip idle = AssetDatabase.LoadAssetAtPath<AnimationClip>(EmberdelveFirstPersonArms.ClipPath(FirstPersonArmsDriver.IdleState));
                Assert.IsNotNull(idle);
                EmberdelveFirstPersonArms.Evaluate(animator, idle, 0.3f);
                Vector3 right = arms.transform.InverseTransformPoint(animator.GetBoneTransform(HumanBodyBones.RightHand).position);
                Vector3 left = arms.transform.InverseTransformPoint(animator.GetBoneTransform(HumanBodyBones.LeftHand).position);
                Assert.Greater(right.x, left.x, "right hand is on the right");
                Assert.Greater(right.z, 0.2f, "the guard hand is in front of the camera");
                Assert.Greater(right.x, 0f);
                Assert.Less(right.y, 0.1f, "below the eye line");
            }
            finally { Object.DestroyImmediate(arms); }
        }

        [Test]
        public void Arms_SwingClips_AreProjectCopies_WithPositiveLength()
        {
            string[] states = FirstPersonArmsDriver.StateNames;
            for (int i = 0; i < states.Length; i++)
            {
                AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(EmberdelveFirstPersonArms.ClipPath(states[i]));
                Assert.IsNotNull(clip, states[i]);
                Assert.Greater(clip.length, 0.2f, states[i] + " length");
                Assert.AreEqual(FirstPersonArmsDriver.SwingClipName(states[i]), clip.name);
            }
        }

        // ---------------------------------------------------------------- enemies

        [TestCaseSource(nameof(VariantNames))]
        public void EnemyPrefab_DoesNotReferenceAKayKitMesh_AndFacesForward(string name)
        {
            string path = EmberdelveEnemyVisualFactory.PrefabPath(name);
            string[] dependencies = AssetDatabase.GetDependencies(path, true);
            for (int i = 0; i < dependencies.Length; i++) StringAssert.DoesNotContain("/KayKit/", dependencies[i], name + " depends on " + dependencies[i]);

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            try
            {
                Animator animator = instance.GetComponentInChildren<Animator>(true);
                Assert.AreEqual(Quaternion.identity, animator.transform.localRotation, "no extra yaw: the live Animator pose faces +Z itself");

                EmberdelveFirstPersonArms.Evaluate(animator, EmberdelveEnemyVisualFactory.FindClip(EmberdelveEnemyVisualFactory.Find(name).Idle), 0f);
                Transform rightShoulder = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
                Transform leftShoulder = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                Transform toes = animator.GetBoneTransform(HumanBodyBones.RightToes);
                Transform foot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
                Assert.Greater(instance.transform.InverseTransformPoint(rightShoulder.position).x, instance.transform.InverseTransformPoint(leftShoulder.position).x, "the enemy's right is +X: it faces +Z");
                if (toes != null && foot != null) Assert.Greater(toes.position.z, foot.position.z, "the toes point to +Z");
            }
            finally { Object.DestroyImmediate(instance); }
        }

        [TestCaseSource(nameof(VariantNames))]
        public void EnemyWeapons_ArePolyHavenModels_AtTheSizeOfTheVariant(string name)
        {
            EmberdelveEnemyVisualFactory.Variant v = EmberdelveEnemyVisualFactory.Find(name);
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(EmberdelveEnemyVisualFactory.PrefabPath(name)));
            try
            {
                CheckWeapon(EmberdelveEnemyVisualFactory.FindHandBone(instance.transform, true), v.MainHand, v.MainHandSource, v.MainHandLength);
                CheckWeapon(EmberdelveEnemyVisualFactory.FindHandBone(instance.transform, false), v.OffHand, v.OffHandSource, v.OffHandSize);
            }
            finally { Object.DestroyImmediate(instance); }
        }

        private static void CheckWeapon(Transform hand, string childName, string modelId, float size)
        {
            if (string.IsNullOrEmpty(childName)) return;
            Transform weapon = hand.Find(childName);
            Assert.IsNotNull(weapon, childName);
            MeshFilter[] filters = weapon.GetComponentsInChildren<MeshFilter>(true);
            Assert.Greater(filters.Length, 0);
            EmberdelveWeaponModels.Spec spec = EmberdelveWeaponModels.Find(modelId);
            Assert.IsNotNull(spec, modelId);
            // A Poly Haven model lives in its own folder; an OpenGameArt one (U5B) is the spec's own model file.
            string expected = string.IsNullOrEmpty(spec.ModelPath) ? "/PolyHaven/" + modelId + "/" : spec.ModelPath;
            for (int i = 0; i < filters.Length; i++) StringAssert.Contains(expected, AssetDatabase.GetAssetPath(filters[i].sharedMesh), childName + " mesh is the model " + modelId);

            float scale = size / spec.Length;
            Assert.AreEqual(scale, weapon.lossyScale.y / hand.root.lossyScale.y, scale * 0.12f, childName + " is scaled to " + size + " m");
        }
    }
}
