using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emberdelve.Player;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.EditorTools
{
    public sealed class EmberdelveMeshyCharacter : AssetPostprocessor
    {
        public const string Folder = "Assets/Emberdelve/Art/Models/Meshy/CryptDelver";
        public const string ModelPath = Folder + "/CryptWarriorHero.fbx";
        public const string ArmsPrefab = Folder + "/CryptDelverArms.prefab";
        private const string Evidence = "Logs/acceptance/meshy-character-20261117";

        private void OnPreprocessModel()
        {
            if (!assetPath.StartsWith(Folder + "/", StringComparison.Ordinal) ||
                !assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) return;
            var importer = (ModelImporter)assetImporter;
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.importAnimation = assetPath != ModelPath;
            importer.isReadable = true;
            importer.optimizeGameObjects = false;
            importer.importCameras = false;
            importer.importLights = false;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            var mappings = new List<HumanBone>();
            for (int i = 0; i < HumanTrait.BoneName.Length; i++)
            {
                string human = HumanTrait.BoneName[i];
                string bone = human.Replace(" ", "");
                if (bone != "Jaw" && bone != "LeftEye" && bone != "RightEye")
                    mappings.Add(new HumanBone { humanName = human, boneName = bone,
                        limit = new HumanLimit { useDefaultValues = true } });
            }
            var description = importer.humanDescription;
            description.human = mappings.ToArray();
            description.armStretch = 0f;
            description.legStretch = 0f;
            importer.humanDescription = description;
        }

        [MenuItem("Emberdelve/Visual/Build Meshy Crypt Delver")]
        public static void Build()
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (model == null) throw new FileNotFoundException(ModelPath);
            Material material = BuildMaterial(Folder + "/Textures/Hero", "Meshy_CryptWarriorDetail");
            var body = (GameObject)PrefabUtility.InstantiatePrefab(model);
            try
            {
                Animator animator = body.GetComponent<Animator>();
                if (animator == null || animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman)
                    throw new InvalidOperationException("The imported Meshy avatar is invalid.");
                int fingerCount = 0;
                foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
                {
                    string name = bone.ToString();
                    if (name.Contains("Proximal") || name.Contains("Intermediate") || name.Contains("Distal"))
                        if (animator.GetBoneTransform(bone) != null) fingerCount++;
                }
                if (fingerCount != 30) throw new InvalidOperationException("Meshy fingers mapped " + fingerCount + "/30.");
                foreach (var renderer in body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    renderer.sharedMaterials = Enumerable.Repeat(material, renderer.sharedMesh.subMeshCount).ToArray();
                animator.applyRootMotion = false;
                animator.runtimeAnimatorController = BuildController();
                PrefabUtility.SaveAsPrefabAsset(body, Folder + "/CryptDelver.prefab");
            }
            finally { Object.DestroyImmediate(body); }
            var armsBody = (GameObject)PrefabUtility.InstantiatePrefab(
                AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath));
            try
            {
                var animator = armsBody.GetComponent<Animator>();
                Mesh arms = BuildArmsMesh(armsBody, animator);
                RuntimeAnimatorController fpController = BuildFirstPersonController();
                string result = EmberdelveFirstPersonArms.BuildPrefab(arms,
                    new[] { material, material }, fpController, ModelPath, ArmsPrefab);
                ConfigureGrip();
                Debug.Log("[MeshyCharacter] fingers=30/30; " + result);
            }
            finally { Object.DestroyImmediate(armsBody); }
            AssetDatabase.SaveAssets();
            InstallWarrior();
            Directory.CreateDirectory(Evidence);
            var sourceMesh = model.GetComponentInChildren<SkinnedMeshRenderer>(true).sharedMesh;
            var armsMesh = AssetDatabase.LoadAssetAtPath<Mesh>(Folder + "/CryptDelverArms.asset");
            File.WriteAllText(Evidence + "/import-result.json", JsonUtility.ToJson(new ImportResult
            {
                avatarValid = true, mappedFingerBones = 30, firstPersonPrefab = ArmsPrefab,
                sourceTriangles = sourceMesh.triangles.Length / 3,
                armsTriangles = armsMesh.triangles.Length / 3,
                armsVertices = armsMesh.vertexCount,
                uses32BitIndices = armsMesh.indexFormat == UnityEngine.Rendering.IndexFormat.UInt32
            }, true));
        }

        [Serializable]
        private sealed class ImportResult
        {
            public bool avatarValid;
            public int mappedFingerBones;
            public string firstPersonPrefab;
            public int sourceTriangles;
            public int armsTriangles;
            public int armsVertices;
            public bool uses32BitIndices;
        }

        private static Material BuildMaterial(string textures, string materialName)
        {
            foreach (string file in new[] { "base_color.png", "normal.png", "metallic.png", "roughness.png" })
            {
                var importer = (TextureImporter)AssetImporter.GetAtPath(textures + "/" + file);
                importer.maxTextureSize = 4096;
                importer.isReadable = file == "metallic.png" || file == "roughness.png";
                importer.sRGBTexture = file == "base_color.png";
                importer.textureType = file == "normal.png" ? TextureImporterType.NormalMap : TextureImporterType.Default;
                importer.SaveAndReimport();
            }
            var metallic = AssetDatabase.LoadAssetAtPath<Texture2D>(textures + "/metallic.png");
            var roughness = AssetDatabase.LoadAssetAtPath<Texture2D>(textures + "/roughness.png");
            Color32[] metals = metallic.GetPixels32();
            Color32[] roughs = roughness.GetPixels32();
            var pixels = new Color32[metals.Length];
            for (int y = 0; y < metallic.height; y++)
                for (int x = 0; x < metallic.width; x++)
                {
                    int index = y * metallic.width + x;
                    int rx = x * roughness.width / metallic.width;
                    int ry = y * roughness.height / metallic.height;
                    pixels[index] = new Color32(metals[index].r, 0, 0,
                        (byte)(255 - roughs[ry * roughness.width + rx].r));
                }
            var packed = new Texture2D(metallic.width, metallic.height, TextureFormat.RGBA32, false, true);
            packed.SetPixels32(pixels);
            packed.Apply();
            string packedPath = textures + "/MetallicSmoothness.png";
            File.WriteAllBytes(packedPath, packed.EncodeToPNG());
            Object.DestroyImmediate(packed);
            AssetDatabase.ImportAsset(packedPath, ImportAssetOptions.ForceSynchronousImport);
            var packedImporter = (TextureImporter)AssetImporter.GetAtPath(packedPath);
            packedImporter.sRGBTexture = false;
            packedImporter.maxTextureSize = 4096;
            packedImporter.alphaSource = TextureImporterAlphaSource.FromInput;
            packedImporter.SaveAndReimport();
            string path = EmberdelveFirstPersonArms.MaterialFolder + "/" + materialName + ".mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(material, path);
            }
            material.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(textures + "/base_color.png"));
            material.SetColor("_BaseColor", Color.white);
            material.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(textures + "/normal.png"));
            material.SetFloat("_BumpScale", 0.65f);
            material.EnableKeyword("_NORMALMAP");
            material.SetTexture("_MetallicGlossMap", AssetDatabase.LoadAssetAtPath<Texture2D>(packedPath));
            material.SetFloat("_Smoothness", 0.55f);
            material.EnableKeyword("_METALLICSPECGLOSSMAP");
            material.SetFloat("_Cull", 0f);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static RuntimeAnimatorController BuildController()
        {
            string path = Folder + "/CryptDelver.controller";
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller == null) controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            var machine = controller.layers[0].stateMachine;
            foreach (var state in machine.states) machine.RemoveState(state.state);
            foreach (var entry in new[] { (File: "Walk", State: "Walk"), (File: "Run", State: "Run"), (File: "Actions", State: "") })
            {
                string file = entry.File;
                string clipPath = Folder + "/Animations/" + file + ".fbx";
                var importer = AssetImporter.GetAtPath(clipPath) as ModelImporter;
                if (importer == null) continue;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                importer.sourceAvatar = null;
                var clipSettings = importer.defaultClipAnimations;
                foreach (var clip in clipSettings)
                {
                    clip.loopTime = file == "Walk" || file == "Run" ||
                        clip.name.IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        clip.name.IndexOf("torch", StringComparison.OrdinalIgnoreCase) >= 0;
                    clip.lockRootPositionXZ = true;
                    clip.lockRootHeightY = true;
                    clip.lockRootRotation = true;
                }
                importer.clipAnimations = clipSettings;
                importer.SaveAndReimport();
                foreach (var clip in AssetDatabase.LoadAllAssetsAtPath(clipPath).OfType<AnimationClip>())
                {
                    if (clip.name.StartsWith("__preview__")) continue;
                    string stateName = entry.State.Length > 0 ? entry.State : clip.name;
                    var state = machine.AddState(stateName);
                    state.motion = clip;
                    if (stateName.Equals("Idle", StringComparison.OrdinalIgnoreCase)) machine.defaultState = state;
                }
            }
            foreach (var pair in new[] { ("Block", "Idle_Shield_Loop"), ("Death", "Death01") })
            {
                var state = machine.AddState(pair.Item1);
                state.motion = EmberdelveEnemyVisualFactory.FindClip(pair.Item2);
            }
            EditorUtility.SetDirty(controller);
            return controller;
        }

        private static RuntimeAnimatorController BuildFirstPersonController()
        {
            var original = AssetDatabase.LoadAssetAtPath<AnimatorController>(EmberdelveFirstPersonArms.ControllerPath);
            string path = Folder + "/CryptDelverArms.overrideController";
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorOverrideController>(path);
            if (controller == null)
            {
                controller = new AnimatorOverrideController(original);
                AssetDatabase.CreateAsset(controller, path);
            }
            controller.runtimeAnimatorController = original;
            AnimationClip Find(string file, string name = null) =>
                AssetDatabase.LoadAllAssetsAtPath(Folder + "/Animations/" + file + ".fbx")
                    .OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview__") &&
                        (name == null || c.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0));
            var idle = Find("Actions", "idle");
            var walk = Find("Walk");
            var sword = Find("Actions", "slash");
            var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();
            controller.GetOverrides(overrides);
            for (int i = 0; i < overrides.Count; i++)
            {
                AnimationClip source = overrides[i].Key;
                AnimationClip replacement = null;
                if (source.name == "FP_Idle") replacement = idle;
                if (source.name == "FP_Walk") replacement = walk;
                if (source.name == "FP_SwingHorizontal") replacement = sword;
                overrides[i] = new KeyValuePair<AnimationClip, AnimationClip>(source, replacement);
            }
            controller.ApplyOverrides(overrides);
            EditorUtility.SetDirty(controller);
            return controller;
        }

        private static void ConfigureGrip()
        {
            string gripPath = Folder + "/CryptDelverGrip.asset";
            var grip = AssetDatabase.LoadAssetAtPath<FpGripPose>(gripPath);
            if (grip == null)
            {
                grip = Object.Instantiate(FpGripPose.Load());
                grip.name = "CryptDelverGrip";
                AssetDatabase.CreateAsset(grip, gripPath);
            }
            var source = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath));
            try
            {
                var animator = source.GetComponent<Animator>();
                var bones = new List<int>();
                var closed = new List<Quaternion>();
                var hooks = new List<Quaternion>();
                for (int b = (int)HumanBodyBones.LeftThumbProximal; b <= (int)HumanBodyBones.RightLittleDistal; b++)
                {
                    var bone = animator.GetBoneTransform((HumanBodyBones)b);
                    if (bone == null) continue;
                    int number = b - (int)HumanBodyBones.LeftThumbProximal;
                    bool left = number < 15;
                    int finger = number % 15 / 3;
                    int joint = number % 3;
                    Vector3 direction = bone.TransformDirection(Vector3.up);
                    Vector3 hinge = bone.InverseTransformDirection(Vector3.Cross(direction, Vector3.forward).normalized);
                    Vector3 oppose = bone.InverseTransformDirection(Vector3.forward);
                    float curl = finger == 0 ? new[] { 30f, 50f, 45f }[joint] : new[] { 55f, 85f, 50f }[joint];
                    Quaternion opposition = finger == 0 && joint == 0
                        ? Quaternion.AngleAxis(left ? -40f : 40f, oppose) : Quaternion.identity;
                    bones.Add(b);
                    closed.Add(bone.localRotation * opposition * Quaternion.AngleAxis(curl, hinge));
                    float hook = finger == 0 ? 10f : new[] { 5f, 80f, 45f }[joint];
                    hooks.Add(bone.localRotation * Quaternion.AngleAxis(hook, hinge));
                }
                grip.nativeBones = bones.ToArray();
                grip.nativeRotations = closed.ToArray();
                grip.nativeBowRotations = hooks.ToArray();
                EditorUtility.SetDirty(grip);
            }
            finally { Object.DestroyImmediate(source); }
            var root = PrefabUtility.LoadPrefabContents(ArmsPrefab);
            try
            {
                var driver = new SerializedObject(root.GetComponent<FirstPersonArmsDriver>());
                driver.FindProperty("supportGripRoll").floatValue = -90f;
                driver.FindProperty("alignWristToGrip").boolValue = true;
                driver.FindProperty("modelScale").floatValue = 1.4f;
                driver.FindProperty("gripPoseOverride").objectReferenceValue = grip;
                driver.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, ArmsPrefab);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static Mesh BuildArmsMesh(GameObject body, Animator animator)
        {
            var renderers = body.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers.Length != 1) throw new InvalidOperationException("Expected one Meshy skinned body.");
            var renderer = renderers[0];
            Mesh source = renderer.sharedMesh;
            var armBones = new HashSet<Transform>();
            foreach (var side in new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm })
                foreach (var bone in animator.GetBoneTransform(side).GetComponentsInChildren<Transform>(true))
                    armBones.Add(bone);
            var handBones = new HashSet<Transform>();
            foreach (var side in new[] { HumanBodyBones.LeftHand, HumanBodyBones.RightHand })
                foreach (var bone in animator.GetBoneTransform(side).GetComponentsInChildren<Transform>(true))
                    handBones.Add(bone);
            var eligible = renderer.bones.Select(armBones.Contains).ToArray();
            var hands = renderer.bones.Select(handBones.Contains).ToArray();
            var weights = source.boneWeights;
            var vertices = source.vertices;
            var normals = source.normals;
            var tangents = source.tangents;
            var uv = source.uv;
            int[] triangles = source.triangles;
            var sleeveTriangles = new List<int>();
            var handTriangles = new List<int>();
            var lookup = new Dictionary<int, int>();
            var indices = new List<int>();
            bool IsArm(int index) => eligible[EmberdelveFirstPersonArms.DominantBone(weights[index])];
            int Remap(int index)
            {
                if (!lookup.TryGetValue(index, out int mapped))
                {
                    mapped = indices.Count;
                    lookup.Add(index, mapped);
                    indices.Add(index);
                }
                return mapped;
            }
            for (int i = 0; i < triangles.Length; i += 3)
                if (IsArm(triangles[i]) && IsArm(triangles[i + 1]) && IsArm(triangles[i + 2]))
                {
                    bool hand = hands[EmberdelveFirstPersonArms.DominantBone(weights[triangles[i]])];
                    var target = hand ? handTriangles : sleeveTriangles;
                    for (int j = 0; j < 3; j++) target.Add(Remap(triangles[i + j]));
                }
            if (handTriangles.Count < 300 || sleeveTriangles.Count < 300)
                throw new InvalidOperationException("Meshy arms extraction is empty.");
            string path = Folder + "/CryptDelverArms.asset";
            Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (mesh == null)
            {
                mesh = new Mesh();
                AssetDatabase.CreateAsset(mesh, path);
            }
            mesh.Clear();
            mesh.name = "CryptDelverArms";
            mesh.indexFormat = indices.Count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices = indices.Select(i => vertices[i]).ToArray();
            mesh.normals = indices.Select(i => normals[i]).ToArray();
            if (tangents.Length == vertices.Length) mesh.tangents = indices.Select(i => tangents[i]).ToArray();
            mesh.uv = indices.Select(i => uv[i]).ToArray();
            mesh.boneWeights = indices.Select(i => weights[i]).ToArray();
            mesh.bindposes = source.bindposes;
            mesh.subMeshCount = 2;
            mesh.SetTriangles(sleeveTriangles, 0);
            mesh.SetTriangles(handTriangles, 1);
            mesh.RecalculateBounds();
            if (tangents.Length != vertices.Length) mesh.RecalculateTangents();
            EditorUtility.SetDirty(mesh);
            return mesh;
        }

        public static void CaptureSourceHands()
        {
            if (Application.isBatchMode)
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                    "Assets/Emberdelve/Scenes/Lobby.unity", UnityEditor.SceneManagement.OpenSceneMode.Single);
            var material = BuildMaterial(Folder + "/Textures/Hero", "Meshy_CryptWarriorDetail");
            foreach (bool left in new[] { false, true })
                EmberdelveViewmodelPreview.Capture(Evidence + "/hero-open-" + (left ? "left" : "right") + ".png",
                    960, 720, 30f, false, (root, camera) =>
                    {
                        var body = (GameObject)PrefabUtility.InstantiatePrefab(
                            AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath));
                        body.transform.SetParent(root, false);
                        foreach (var renderer in body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                            renderer.sharedMaterials = Enumerable.Repeat(material, renderer.sharedMesh.subMeshCount).ToArray();
                        var animator = body.GetComponent<Animator>();
                        var hand = animator.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                        var elbow = animator.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
                        Vector3 centre = hand.position + (hand.position - elbow.position).normalized * .08f;
                        camera.transform.position = centre + Vector3.forward * .65f;
                        camera.transform.LookAt(centre);
                    }, .01f);
            AssetDatabase.SaveAssets();
        }

        public static void CaptureReview()
        {
            if (Application.isBatchMode)
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                    "Assets/Emberdelve/Scenes/Lobby.unity", UnityEditor.SceneManagement.OpenSceneMode.Single);
            Directory.CreateDirectory(Evidence);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ArmsPrefab);
            var files = new List<string>();
            foreach (var pair in new[]
            {
                ("sword", "ashen_short_sword", (string)null, 0f),
                ("shield", "ashen_short_sword", "emberbulwark_shield", 1f),
                ("two-handed", "two_handed_sword", (string)null, 0f),
                ("bow", "hollow_bow", (string)null, 0f),
                ("crossbow", "ember_crossbow", (string)null, 0f),
                ("torch", "pitch_torch", (string)null, 0f)
            })
            {
                string path = Evidence + "/" + pair.Item1 + ".png";
                if (pair.Item1 == "bow" || pair.Item1 == "crossbow")
                {
                    CaptureRanged(path, prefab, pair.Item2);
                    files.Add(path);
                    continue;
                }
                EmberdelveViewmodelPreview.FirstPersonPattern(path, pair.Item2, pair.Item3,
                    null, -1f, pair.Item4, armsPrefab: prefab);
                files.Add(path);
            }
            EmberdelveViewmodelPreview.Sheet(Evidence + "/first-person-sheet.png", files, 3, 960, 540);
            CaptureMotionReview(prefab);
            EmberdelveHandGripRender.Render();
        }

        public static void CaptureAnimatedReview()
        {
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                "Assets/Emberdelve/Scenes/Lobby.unity", UnityEditor.SceneManagement.OpenSceneMode.Single);
            var arms = AssetDatabase.LoadAssetAtPath<GameObject>(ArmsPrefab);
            var body = AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "/CryptDelver.prefab");
            var weapon = EmberdelveViewmodelPreview.FindWeapon("ashen_short_sword");
            var pattern = weapon.swingPatterns.First(p => p != null);
            var clip = AssetDatabase.LoadAllAssetsAtPath(Folder + "/Animations/Actions.fbx")
                .OfType<AnimationClip>().First(c => c.name == "Right_Hand_Sword_Slash");
            const int frames = 24;
            for (int i = 0; i < frames; i++)
            {
                float phase = i / (float)(frames - 1);
                EmberdelveViewmodelPreview.FirstPersonPattern(Evidence + "/motion/fp-" + i.ToString("D2") + ".png",
                    weapon.weaponId, "emberbulwark_shield", pattern, phase * pattern.Timing.Total, 0f,
                    width: 640, height: 360, armsPrefab: arms);
                EmberdelveViewmodelPreview.Capture(Evidence + "/motion/body-" + i.ToString("D2") + ".png",
                    360, 480, 35f, true, (root, camera) =>
                    {
                        var model = (GameObject)PrefabUtility.InstantiatePrefab(body);
                        model.transform.SetParent(root, false);
                        EmberdelveFirstPersonArms.Evaluate(model.GetComponent<Animator>(), clip, phase * clip.length);
                        camera.transform.position = root.position + new Vector3(0f, 1.15f, 3.9f);
                        camera.transform.LookAt(root.position + Vector3.up);
                    });
            }
            File.WriteAllText(Evidence + "/motion/durations.json", "{\"frames\":24,\"body\":" +
                clip.length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"fp\":" +
                pattern.Timing.Total.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
        }

        private static void CaptureMotionReview(GameObject armsPrefab)
        {
            var weapon = EmberdelveViewmodelPreview.FindWeapon("ashen_short_sword");
            var swingFiles = new List<string>();
            foreach (var pattern in weapon.swingPatterns)
            {
                if (pattern == null) continue;
                var timing = pattern.Timing;
                float[] samples = { timing.Windup, timing.Windup + timing.Active * .5f, timing.ActiveEnd };
                for (int i = 0; i < samples.Length; i++)
                {
                    string path = Evidence + "/swing-" + pattern.name + "-" + i + ".png";
                    EmberdelveViewmodelPreview.FirstPersonPattern(path, weapon.weaponId,
                        "emberbulwark_shield", pattern, samples[i], 0f,
                        width: 640, height: 360, armsPrefab: armsPrefab);
                    swingFiles.Add(path);
                }
            }
            if (swingFiles.Count > 0)
                EmberdelveViewmodelPreview.Sheet(Evidence + "/swing-motion.png", swingFiles, 3, 640, 360);
            var bodyFiles = new List<string>();
            var body = AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "/CryptDelver.prefab");
            foreach (var clip in AssetDatabase.LoadAllAssetsAtPath(Folder + "/Animations/Actions.fbx").OfType<AnimationClip>())
            {
                if (clip.name.StartsWith("__preview__")) continue;
                for (int i = 0; i < 3; i++)
                {
                    string path = Evidence + "/body-" + clip.name + "-" + i + ".png";
                    float time = clip.length * (i + .5f) / 3f;
                    EmberdelveViewmodelPreview.Capture(path, 480, 640, 35f, true, (root, camera) =>
                    {
                        var model = (GameObject)PrefabUtility.InstantiatePrefab(body);
                        model.transform.SetParent(root, false);
                        EmberdelveFirstPersonArms.Evaluate(model.GetComponent<Animator>(), clip, time);
                        camera.transform.position = root.position + new Vector3(0f, 1.15f, 3.9f);
                        camera.transform.LookAt(root.position + Vector3.up * 1f);
                    });
                    bodyFiles.Add(path);
                }
            }
            EmberdelveViewmodelPreview.Sheet(Evidence + "/body-motion.png", bodyFiles, 3, 480, 640);
        }

        private static void CaptureRanged(string path, GameObject prefab, string weaponId)
        {
            EmberdelveViewmodelPreview.Capture(path, 960, 540, 70f, true, (root, camera) =>
            {
                camera.transform.SetPositionAndRotation(root.position + Vector3.up * 1.66f, Quaternion.identity);
                var set = WeaponViewModelSet.Load();
                var arms = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                arms.transform.SetParent(camera.transform, false);
                var driver = arms.GetComponent<FirstPersonArmsDriver>();
                driver.Bind(null, camera, set);
                var weapon = EmberdelveViewmodelPreview.FindWeapon(weaponId);
                driver.SetWeapons(weapon, null);
                var visual = WeaponVisualBuilder.Build(weapon, camera.transform);
                driver.SetVisual(visual);
                EmberdelveViewmodelPreview.PoseArms(arms, driver, camera, null, -1f, 0f);
                var bow = visual.GetComponentInChildren<BowPresentation>();
                if (bow != null) driver.PoseBow(camera.transform, bow, 1f);
                else driver.PoseCrossbow(camera.transform, visual.transform, false, 0f);
            });
        }

        [MenuItem("Emberdelve/Visual/Use Meshy First Person Arms")]
        public static void UseArms()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ArmsPrefab);
            if (prefab == null) throw new FileNotFoundException(ArmsPrefab);
            var set = WeaponViewModelSet.Load();
            set.armsPrefab = prefab;
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
        }

        [MenuItem("Emberdelve/Visual/Install Meshy Warrior")]
        public static void InstallWarrior()
        {
            var warrior = AssetDatabase.LoadAssetAtPath<Emberdelve.Data.CharacterClassData>(
                "Assets/Emberdelve/Data/Class_Warrior.asset");
            var body = AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "/CryptDelver.prefab");
            var arms = AssetDatabase.LoadAssetAtPath<GameObject>(ArmsPrefab);
            var set = WeaponViewModelSet.Load();
            if (warrior == null || body == null || arms == null || set == null)
                throw new InvalidOperationException("Meshy warrior assets are missing.");
            warrior.characterVisual = body;
            set.armsPrefab = arms;
            EditorUtility.SetDirty(warrior);
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            Debug.Log("[MeshyCharacter] installed warrior body and first-person arms.");
            if (Application.isBatchMode) CaptureReview();
        }
    }
}

