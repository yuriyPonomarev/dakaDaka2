using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Emberdelve.AI;
using Emberdelve.Data;
using Emberdelve.EditorTools;

namespace Emberdelve.Tests.Visual
{
    /// <summary>
    /// V3b: the enemy visuals are the Quaternius humanoid mannequin turned grim undead. Everything is derived from the data the
    /// factory / importer publish (variant table, file table, state names, loop pattern), from the manifest and from the built assets.
    /// </summary>
    public sealed class EnemyVisualQuaterniusTests
    {
        private static IEnumerable<string> VariantNames()
        {
            for (int i = 0; i < EmberdelveEnemyVisualFactory.Variants.Length; i++) yield return EmberdelveEnemyVisualFactory.Variants[i].Name;
        }

        private static IEnumerable<string> LibraryFiles()
        {
            for (int i = 0; i < EmberdelveQuaterniusImport.Files.Length; i++) yield return EmberdelveQuaterniusImport.Files[i][0];
        }

        private static GameObject Instantiate(string variant)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(EmberdelveEnemyVisualFactory.PrefabPath(variant));
            Assert.IsNotNull(prefab, "prefab for " + variant + " exists (build with EmberdelveEnemyVisualFactory.EnsureAll)");
            return (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        }

        private static AnimatorController Controller(string variant)
        {
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(EmberdelveEnemyVisualFactory.ControllerPath(variant));
            Assert.IsNotNull(controller, "controller for " + variant);
            return controller;
        }

        // ---------------------------------------------------------------- import / manifest

        [Test]
        public void Manifest_ListsEveryFile_WithMatchingHashSizeAndCc0License()
        {
            EmberdelveQuaterniusImport.Manifest manifest = EmberdelveQuaterniusImport.ReadManifest();
            Assert.IsNotNull(manifest, "Quaternius/manifest.json parses");
            Assert.AreEqual("CC0-1.0", manifest.license);
            Assert.AreEqual(EmberdelveQuaterniusImport.Files.Length, manifest.files.Count);

            for (int i = 0; i < EmberdelveQuaterniusImport.Files.Length; i++)
            {
                string[] expected = EmberdelveQuaterniusImport.Files[i];
                EmberdelveQuaterniusImport.ManifestFile file = manifest.files.Find(f => f.path == expected[0]);
                Assert.IsNotNull(file, expected[0] + " is listed");

                string absolute = EmberdelveQuaterniusImport.Absolute(expected[0]);
                Assert.IsTrue(File.Exists(absolute), expected[0] + " exists");
                string actual = EmberdelveQuaterniusImport.Sha256(absolute);
                Assert.AreEqual(expected[2], actual, expected[0] + " matches the sha256 of the download");
                Assert.AreEqual(actual, file.sha256, expected[0] + " manifest hash");
                Assert.AreEqual(new FileInfo(absolute).Length, file.bytes, expected[0] + " manifest size");
                StringAssert.StartsWith("https://opengameart.org/", file.source);
            }

            string license = File.ReadAllText(EmberdelveQuaterniusImport.Absolute(EmberdelveQuaterniusImport.LicensePath));
            StringAssert.Contains("CC0", license);
            StringAssert.Contains("Quaternius", license);
        }

        [TestCaseSource(nameof(LibraryFiles))]
        public void Library_IsHumanoid_WithAvatarFromItsOwnModel(string path)
        {
            ModelImporter importer = AssetImporter.GetAtPath(path) as ModelImporter;
            Assert.IsNotNull(importer, path);
            Assert.AreEqual(ModelImporterAnimationType.Human, importer.animationType);
            Assert.AreEqual(ModelImporterAvatarSetup.CreateFromThisModel, importer.avatarSetup);
            Assert.IsFalse(importer.optimizeGameObjects, "bones stay (hand / head lookup)");

            Avatar avatar = null;
            Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
            for (int i = 0; i < assets.Length && avatar == null; i++) avatar = assets[i] as Avatar;
            Assert.IsNotNull(avatar, path + " has an avatar");
            Assert.IsTrue(avatar.isValid && avatar.isHuman, path + " avatar is a valid human avatar");
        }

        [TestCaseSource(nameof(LibraryFiles))]
        public void Library_Clips_LoopOnlyIdleAndLocomotion_AndNeverMoveTheRoot(string path)
        {
            ModelImporter importer = AssetImporter.GetAtPath(path) as ModelImporter;
            int expectedClips = path == EmberdelveQuaterniusImport.MannequinFPath ? 0 : 1;
            ModelImporterClipAnimation[] clips = importer.clipAnimations;
            if (expectedClips == 0) { Assert.IsFalse(importer.importAnimation, path + " carries no clips"); return; }

            Assert.IsTrue(importer.importAnimation);
            Assert.Greater(clips.Length, 20, path + " clip list");
            for (int i = 0; i < clips.Length; i++)
            {
                bool shouldLoop = EmberdelveQuaterniusImport.LoopPattern.IsMatch(clips[i].name);
                Assert.AreEqual(shouldLoop, clips[i].loopTime, clips[i].name + " loopTime");
                Assert.IsTrue(clips[i].lockRootRotation && clips[i].lockRootPositionXZ, clips[i].name + " root rotation / XZ baked into the pose");
            }
        }

        // ---------------------------------------------------------------- variants

        [Test]
        public void SkeletonModel_IsHumanoidSkinnedAndWithinPolygonBudget()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(EmberdelveSkeletonAssets.ModelPath);
            Assert.IsNotNull(model);
            var skin = model.GetComponentInChildren<SkinnedMeshRenderer>(true);
            Assert.IsNotNull(skin);
            Assert.Greater(skin.bones.Length, 10);
            Assert.AreEqual(skin.sharedMesh.vertexCount, skin.sharedMesh.boneWeights.Length);
            Assert.LessOrEqual(skin.sharedMesh.triangles.Length / 3, 25000);
            var avatar = System.Array.Find(AssetDatabase.LoadAllAssetsAtPath(EmberdelveSkeletonAssets.ModelPath), x => x is Avatar) as Avatar;
            Assert.IsNotNull(avatar);
            Assert.IsTrue(avatar.isValid && avatar.isHuman);
        }

        [TestCaseSource(nameof(VariantNames))]
        public void Variant_Clips_AllResolve_AndLocomotionClipsLoop(string name)
        {
            EmberdelveEnemyVisualFactory.Variant v = EmberdelveEnemyVisualFactory.Find(name);
            string[] clipNames = { v.Idle, v.Walk, v.Run, v.Attack, v.Hit, v.Death };
            for (int i = 0; i < clipNames.Length; i++) Assert.IsNotNull(EmberdelveEnemyVisualFactory.FindClip(clipNames[i]), name + ": clip '" + clipNames[i] + "' exists in the Quaternius libraries");

            Assert.IsTrue(EmberdelveEnemyVisualFactory.FindClip(v.Idle).isLooping, "idle loops");
            Assert.IsTrue(EmberdelveEnemyVisualFactory.FindClip(v.Walk).isLooping, "walk loops");
            Assert.IsTrue(EmberdelveEnemyVisualFactory.FindClip(v.Run).isLooping, "run loops");
            Assert.IsFalse(EmberdelveEnemyVisualFactory.FindClip(v.Death).isLooping, "death plays once");
        }

        [TestCaseSource(nameof(VariantNames))]
        public void Variant_Prefab_HasDriver_HumanoidAnimator_AndNoColliders(string name)
        {
            GameObject instance = Instantiate(name);
            try
            {
                Assert.IsNotNull(instance.GetComponent<EnemyVisualDriver>(), "driver on the root");
                Animator animator = instance.GetComponentInChildren<Animator>(true);
                Assert.IsNotNull(animator);
                Assert.IsNotNull(animator.avatar);
                Assert.IsTrue(animator.avatar.isHuman, "Humanoid avatar");
                Assert.IsFalse(animator.applyRootMotion, "the agent moves the root");
                Assert.IsNotNull(animator.runtimeAnimatorController);
                Assert.AreSame(animator, instance.GetComponent<EnemyVisualDriver>().Animator, "driver drives that animator");
                Assert.AreEqual(0, instance.GetComponentsInChildren<Collider>(true).Length, "pure visual: no colliders");
            }
            finally { Object.DestroyImmediate(instance); }
        }

        [TestCaseSource(nameof(VariantNames))]
        public void Variant_Controller_KeepsTheDriverContract(string name)
        {
            AnimatorController controller = Controller(name);
            Dictionary<string, AnimatorControllerParameterType> parameters = new Dictionary<string, AnimatorControllerParameterType>();
            for (int i = 0; i < controller.parameters.Length; i++) parameters[controller.parameters[i].name] = controller.parameters[i].type;

            Assert.AreEqual(5, parameters.Count);
            Assert.AreEqual(AnimatorControllerParameterType.Float, parameters[EnemyVisualDriver.SpeedParameter]);
            Assert.AreEqual(AnimatorControllerParameterType.Float, parameters[EnemyVisualDriver.AttackSpeedParameter]);
            Assert.AreEqual(AnimatorControllerParameterType.Trigger, parameters[EnemyVisualDriver.AttackTrigger]);
            Assert.AreEqual(AnimatorControllerParameterType.Trigger, parameters[EnemyVisualDriver.HitTrigger]);
            Assert.AreEqual(AnimatorControllerParameterType.Bool, parameters[EnemyVisualDriver.DeadParameter]);

            AnimatorStateMachine machine = controller.layers[0].stateMachine;
            Dictionary<string, AnimatorState> states = new Dictionary<string, AnimatorState>();
            for (int i = 0; i < machine.states.Length; i++) states[machine.states[i].state.name] = machine.states[i].state;

            Assert.AreEqual(EmberdelveEnemyVisualFactory.StateNames.Length, states.Count);
            for (int i = 0; i < EmberdelveEnemyVisualFactory.StateNames.Length; i++)
            {
                string state = EmberdelveEnemyVisualFactory.StateNames[i];
                Assert.IsTrue(states.ContainsKey(state), "state " + state);
                Assert.IsNotNull(states[state].motion, "state " + state + " has a clip");
            }

            Assert.AreEqual("Idle", machine.defaultState.name);
            Assert.IsTrue(states["Attack"].speedParameterActive);
            Assert.AreEqual(EnemyVisualDriver.AttackSpeedParameter, states["Attack"].speedParameter, "the driver matches the attack clip to the brain's windup + swing");
            Assert.GreaterOrEqual(machine.anyStateTransitions.Length, 3, "Attack / Hit / Death from any state");

            string[] loops = { "Idle", "Walk", "Run" };
            for (int i = 0; i < loops.Length; i++) Assert.IsTrue(((AnimationClip)states[loops[i]].motion).isLooping, loops[i] + " loops");

            // the driver reads the attack length from the controller's clip whose name contains "Attack"
            bool named = false;
            AnimationClip[] clips = controller.animationClips;
            for (int i = 0; i < clips.Length; i++) if (clips[i] != null && clips[i].name.IndexOf("Attack", System.StringComparison.OrdinalIgnoreCase) >= 0) named = true;
            Assert.IsTrue(named, "an 'Attack' clip is findable by the driver");
        }

        [TestCaseSource(nameof(VariantNames))]
        public void Variant_Height_MatchesNormalHeight(string name)
        {
            GameObject instance = Instantiate(name);
            try
            {
                SkinnedMeshRenderer[] renderers = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                Assert.Greater(renderers.Length, 0);
                Bounds bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
                Assert.AreEqual(EmberdelveEnemyVisualFactory.NormalHeight, bounds.size.y, EmberdelveEnemyVisualFactory.NormalHeight * 0.08f, name + " height");
            }
            finally { Object.DestroyImmediate(instance); }
        }

        [TestCaseSource(nameof(VariantNames))]
        public void Variant_Materials_AreUrpLit_NotTheKayKitAtlas(string name)
        {
            GameObject instance = Instantiate(name);
            try
            {
                Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
                Assert.Greater(renderers.Length, 0);
                for (int r = 0; r < renderers.Length; r++)
                {
                    // Particle renderers use their own unlit effect material, not a body/weapon material.
                    if (renderers[r] is ParticleSystemRenderer) continue;
                    Material[] materials = renderers[r].sharedMaterials;
                    Assert.Greater(materials.Length, 0, renderers[r].name);
                    for (int m = 0; m < materials.Length; m++)
                    {
                        Assert.IsNotNull(materials[m], renderers[r].name + " material " + m);
                        Assert.AreEqual("Universal Render Pipeline/Lit", materials[m].shader.name, renderers[r].name);
                        string path = AssetDatabase.GetAssetPath(materials[m]);
                        StringAssert.StartsWith(EmberdelveUndeadMaterials.Folder + "/", path, renderers[r].name + " uses an undead material");
                        Texture map = materials[m].GetTexture("_BaseMap");
                        if (map != null) Assert.IsFalse(AssetDatabase.GetAssetPath(map).Contains("/KayKit/"), renderers[r].name + " does not sample a KayKit atlas");
                    }
                }

                SkinnedMeshRenderer body = instance.GetComponentInChildren<SkinnedMeshRenderer>(true);
                Assert.AreEqual(EmberdelveSkeletonAssets.MaterialPath(name), AssetDatabase.GetAssetPath(body.sharedMaterial), "body material of the variant");
                Assert.AreEqual(EmberdelveSkeletonAssets.ModelPath, AssetDatabase.GetAssetPath(body.sharedMesh), "uses the skinned skeleton mesh");
                Assert.IsNotNull(body.sharedMaterial.GetTexture("_BaseMap"));
            }
            finally { Object.DestroyImmediate(instance); }
        }

        [TestCaseSource(nameof(VariantNames))]
        public void Variant_DoesNotDependOnAKayKitSkeletonCharacter(string name)
        {
            string[] dependencies = AssetDatabase.GetDependencies(EmberdelveEnemyVisualFactory.PrefabPath(name), true);
            Regex character = new Regex("/KayKit/.*/Skeleton_(Minion|Warrior|Rogue|Mage)\\.fbx$");
            for (int i = 0; i < dependencies.Length; i++) Assert.IsFalse(character.IsMatch(dependencies[i]), name + " depends on " + dependencies[i]);
        }

        [TestCaseSource(nameof(VariantNames))]
        public void Variant_Weapons_SitOnTheHandBones_WithRustyMetal(string name)
        {
            EmberdelveEnemyVisualFactory.Variant v = EmberdelveEnemyVisualFactory.Find(name);
            GameObject instance = Instantiate(name);
            try
            {
                Transform right = EmberdelveEnemyVisualFactory.FindHandBone(instance.transform, true);
                Transform left = EmberdelveEnemyVisualFactory.FindHandBone(instance.transform, false);
                Assert.IsNotNull(right);
                Assert.IsNotNull(left);

                CheckHand(right, v.MainHand, v.MainHandSource, name + " main hand");
                CheckHand(left, v.OffHand, v.OffHandSource, name + " off hand");
            }
            finally { Object.DestroyImmediate(instance); }
        }

        private static void CheckHand(Transform hand, string expectedChild, string weaponId, string label)
        {
            if (string.IsNullOrEmpty(expectedChild))
            {
                for (int i = 0; i < hand.childCount; i++) StringAssert.DoesNotStartWith("Weapon_", hand.GetChild(i).name, label + " carries nothing");
                return;
            }

            Transform weapon = hand.Find(expectedChild);
            Assert.IsNotNull(weapon, label + ": '" + expectedChild + "' is a child of the hand bone");
            Renderer[] renderers = weapon.GetComponentsInChildren<Renderer>(true);
            Assert.Greater(renderers.Length, 0);
            for (int i = 0; i < renderers.Length; i++)
            {
                Material[] materials = renderers[i].sharedMaterials;
                for (int m = 0; m < materials.Length; m++)
                {
                    // V3c: the Poly Haven weapon materials, rusted copies (Rusty_<source material>.mat in the undead folder)
                    StringAssert.StartsWith(EmberdelveWeaponModels.RustyFolder + "/Rusty_", AssetDatabase.GetAssetPath(materials[m]), label + " is rusted");
                }
            }

            Assert.AreEqual(0, weapon.GetComponentsInChildren<Collider>(true).Length, label + " has no collider");
            Assert.IsNotNull(EmberdelveWeaponModels.LoadPrefab(weaponId), label + ": the Poly Haven model prefab exists");
        }

        // ---------------------------------------------------------------- eyes

        [TestCaseSource(nameof(VariantNames))]
        public void Variant_GlowingEyes_ExistExactlyWhereTheVariantAsksForThem(string name)
        {
            EmberdelveEnemyVisualFactory.Variant v = EmberdelveEnemyVisualFactory.Find(name);
            Material glow = AssetDatabase.LoadAssetAtPath<Material>(EmberdelveUndeadMaterials.EyeGlowPath);
            Assert.IsNotNull(glow);
            Assert.IsTrue(glow.IsKeywordEnabled("_EMISSION"), "eye material emits");
            Color emission = glow.GetColor("_EmissionColor");
            Assert.Greater(Mathf.Max(emission.r, emission.g, emission.b), 1f, "HDR ember emission");
            Assert.Greater(emission.r, emission.b, "ember orange, not blue");

            GameObject instance = Instantiate(name);
            try
            {
                Animator animator = instance.GetComponentInChildren<Animator>(true);
                Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
                Assert.IsNotNull(head);
                Transform left = head.Find(EmberdelveEnemyVisualFactory.EyeNameLeft);
                Transform right = head.Find(EmberdelveEnemyVisualFactory.EyeNameRight);

                if (!v.GlowingEyes)
                {
                    Assert.IsNull(left, name + " has no eyes");
                    Assert.IsNull(right, name + " has no eyes");
                    return;
                }

                Assert.IsNotNull(left);
                Assert.IsNotNull(right);
                Assert.AreSame(glow, left.GetComponent<Renderer>().sharedMaterial);
                Assert.AreSame(glow, right.GetComponent<Renderer>().sharedMaterial);
                Assert.IsNull(left.GetComponent<Collider>());
                Assert.IsNull(right.GetComponent<Collider>());
                // The live pose (what the runtime Animator shows) faces the model's +Z; the bind pose faces -Z.
                EmberdelveFirstPersonArms.Evaluate(animator, EmberdelveEnemyVisualFactory.FindClip(v.Idle), 0f);
                Assert.Less(left.position.x, right.position.x, "left eye is left of the right eye (the live pose faces +Z)");
            }
            finally { Object.DestroyImmediate(instance); }
        }

        [Test]
        public void OnlyTheMage_HasGlowingEyes()
        {
            List<string> glowing = new List<string>();
            for (int i = 0; i < EmberdelveEnemyVisualFactory.Variants.Length; i++) if (EmberdelveEnemyVisualFactory.Variants[i].GlowingEyes) glowing.Add(EmberdelveEnemyVisualFactory.Variants[i].Name);
            CollectionAssert.AreEqual(new[] { "Mage" }, glowing);
        }

        // ---------------------------------------------------------------- look

        [Test]
        public void Variants_LookDifferent_AndFleshIsDarkAndDesaturated()
        {
            List<Color> averages = new List<Color>();
            for (int i = 0; i < EmberdelveUndeadMaterials.Looks.Length; i++)
            {
                string path = EmberdelveUndeadMaterials.BodyTexturePath(EmberdelveUndeadMaterials.Looks[i].Name);
                Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                try
                {
                    Assert.IsTrue(texture.LoadImage(File.ReadAllBytes(EmberdelveQuaterniusImport.Absolute(path))), path);
                    Color32[] pixels = texture.GetPixels32();
                    double r = 0, g = 0, b = 0;
                    for (int p = 0; p < pixels.Length; p++) { r += pixels[p].r; g += pixels[p].g; b += pixels[p].b; }
                    Color average = new Color((float)(r / pixels.Length / 255.0), (float)(g / pixels.Length / 255.0), (float)(b / pixels.Length / 255.0));
                    averages.Add(average);

                    float max = Mathf.Max(average.r, average.g, average.b), min = Mathf.Min(average.r, average.g, average.b);
                    Assert.Less(max, 0.45f, path + " is dark (no bright cartoon colours)");
                    Assert.Less((max - min) / Mathf.Max(0.001f, max), 0.5f, path + " is desaturated");
                }
                finally { Object.DestroyImmediate(texture); }
            }

            for (int a = 0; a < averages.Count; a++)
            {
                for (int b = a + 1; b < averages.Count; b++)
                {
                    Color d = averages[a] - averages[b];
                    Assert.Greater(Mathf.Abs(d.r) + Mathf.Abs(d.g) + Mathf.Abs(d.b), 0.01f, EmberdelveUndeadMaterials.Looks[a].Name + " and " + EmberdelveUndeadMaterials.Looks[b].Name + " have different textures");
                }
            }
        }

        [Test]
        public void FleshTints_AreTheGrimGreyGreen_OfTheBrief()
        {
            EmberdelveUndeadMaterials.Look minion = EmberdelveUndeadMaterials.LookFor("Minion");
            Assert.IsNotNull(minion);
            Assert.Greater(minion.Flesh.g, minion.Flesh.r, "greenish");
            Assert.Greater(minion.Flesh.g, minion.Flesh.b, "greenish");
            Assert.Less(minion.Flesh.g, 0.4f, "dark");
        }

        [Test]
        public void RustyMetal_IsUrpLit_Metallic_AndTextured()
        {
            Material rust = AssetDatabase.LoadAssetAtPath<Material>(EmberdelveUndeadMaterials.RustyMetalPath);
            Assert.IsNotNull(rust);
            Assert.AreEqual("Universal Render Pipeline/Lit", rust.shader.name);
            Assert.Greater(rust.GetFloat("_Metallic"), 0.2f);
            Assert.IsNotNull(rust.GetTexture("_BaseMap"));
        }

        [Test]
        public void DerivedMeshes_KeepTheSkinning_AndHaveUvsInTheTexture()
        {
            string[] meshNames = { "Undead_Mannequin_M", "Undead_Mannequin_F" };
            string[] sources = { EmberdelveEnemyVisualFactory.MaleBody, EmberdelveEnemyVisualFactory.FemaleBody };
            for (int i = 0; i < meshNames.Length; i++)
            {
                Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(EmberdelveUndeadMaterials.MeshPath(meshNames[i]));
                Assert.IsNotNull(mesh, meshNames[i]);
                SkinnedMeshRenderer source = AssetDatabase.LoadAssetAtPath<GameObject>(sources[i]).GetComponentInChildren<SkinnedMeshRenderer>(true);

                Assert.AreEqual(source.sharedMesh.vertexCount, mesh.vertexCount);
                Assert.AreEqual(source.sharedMesh.bindposes.Length, mesh.bindposes.Length, "bind poses");
                Assert.AreEqual(mesh.vertexCount, mesh.boneWeights.Length, "every vertex is skinned");
                Assert.AreEqual(1, mesh.subMeshCount);

                Vector2[] uv = mesh.uv;
                Assert.AreEqual(mesh.vertexCount, uv.Length);
                for (int u = 0; u < uv.Length; u++)
                {
                    Assert.That(uv[u].x, Is.InRange(-0.02f, 1.02f));
                    Assert.That(uv[u].y, Is.InRange(-0.02f, 1.02f));
                }
            }
        }

        // ---------------------------------------------------------------- mapping

        [Test]
        public void VariantFor_MapsEliteToWarrior_AndAlternatesTheRest()
        {
            string[] guids = AssetDatabase.FindAssets("t:EnemyData");
            Assert.Greater(guids.Length, 0);
            for (int i = 0; i < guids.Length; i++)
            {
                EnemyData data = AssetDatabase.LoadAssetAtPath<EnemyData>(AssetDatabase.GUIDToAssetPath(guids[i]));
                for (int spawn = 0; spawn < 4; spawn++)
                {
                    string variant = EmberdelveEnemyVisualFactory.VariantFor(data, spawn);
                    Assert.IsNotNull(EmberdelveEnemyVisualFactory.Find(variant), variant + " is a built variant");
                    if (data.isElite) Assert.AreEqual("Warrior", variant);
                    else Assert.AreEqual((spawn & 1) == 0 ? "Minion" : "Rogue", variant);
                }
            }
        }
    }
}
