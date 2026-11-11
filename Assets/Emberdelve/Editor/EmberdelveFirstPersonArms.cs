using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Emberdelve.Player;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// First-person arms of the local player. The same CC0 MakeHuman-derived body as the world character
    /// is reused, but only its ARMS are kept: a derived skinned mesh (<see cref="MeshPath"/>) made of the triangles whose strongest bone
    /// weight is an upper arm, forearm, hand or finger bone, so head, neck, torso, hips and legs do not exist in the mesh at all (nothing
    /// to hide, nothing that can show through when the rig bends). Two submeshes: sleeves (dark quilted gambeson, upper arm and forearm)
    /// and hands (weathered human skin, hand and fingers); both with a code-generated 128 px point-filtered URP Lit material, not undead.
    ///
    /// Prefab <see cref="PrefabPath"/>: FirstPersonArms (<see cref="FirstPersonArmsDriver"/>) / Model (the rig, Humanoid Animator with the controller
    /// below, turned and moved so the right hand sits at the guard position in front of the camera) with the weapon grips
    /// Grip_R / Grip_L under the hand bones, derived from the pose the weapons are meant for (the sword guard, the shield guard).
    ///
    /// Controller <see cref="ControllerPath"/>: states Idle, Walk, SwingHorizontal, SwingOverhead, Thrust, Block, HitReact; parameters
    /// Speed, WalkPlayback, SwingSpeed (float), Block (bool), SwingHorizontal / SwingOverhead / Thrust / HitReact (trigger). The clips are
    /// project copies of Quaternius library takes named FP_&lt;State&gt; (the driver reads the swing clip lengths by that name).
    /// </summary>
    public static class EmberdelveFirstPersonArms
    {
        public const string Folder = "Assets/Emberdelve/Prefabs/Visual/FirstPerson";
        public const string ClipFolder = Folder + "/Clips";
        public const string PrefabPath = Folder + "/FirstPersonArms.prefab";
        public const string ControllerPath = Folder + "/FirstPersonArms.controller";
        public const string MeshFolder = "Assets/Emberdelve/Art/Models/FirstPerson";
        public const string MeshPath = MeshFolder + "/FP_Arms.asset";
        public const string MaterialFolder = "Assets/Emberdelve/Art/Materials/FirstPerson";
        public const string SleeveMaterialPath = MaterialFolder + "/FP_Sleeve.mat";
        public const string SleeveTexturePath = MaterialFolder + "/FP_Sleeve.png";
        public const string SkinMaterialPath = MaterialFolder + "/FP_Skin.mat";
        public const string SkinTexturePath = MaterialFolder + "/FP_Skin.png";
        public const string BodySource = EmberdelveUnifiedHuman.ModelPath;
        public const string ModelName = "Model";
        public const string GripRightName = "Grip_R";
        public const string GripLeftName = "Grip_L";

        /// <summary>Pose the weapon grip is derived from (the sword guard) and the one the shield grip is derived from.</summary>
        public const string SwordGripClip = "Sword_Idle";
        public const string ShieldGripClip = "Idle_Shield_Loop";

        public sealed class StateClip
        {
            public string State;
            public string Library;   // bare take name in the Quaternius libraries
            public bool Loop;
        }

        /// <summary>
        /// Library takes per state, picked by looking at each from the eye (the clips are third person: most keep the hands at the hips, out of
        /// view). Idle: the lantern hold (right hand out in front, low right). Walk: the same hold with a procedural sway (the driver). Block:
        /// the pistol guard (both hands up in front, centre). Swings: the fast slash (right to left), the chop (high right to low left) and the
        /// straight punch (thrust). HitReact: the flinch. The off hand shield guard is a masked layer.
        /// </summary>
        public static readonly StateClip[] Clips =
        {
            new StateClip { State = FirstPersonArmsDriver.IdleState, Library = "Idle_Lantern_Loop", Loop = true },
            new StateClip { State = FirstPersonArmsDriver.WalkState, Library = "Idle_Lantern_Loop", Loop = true },
            new StateClip { State = FirstPersonArmsDriver.SwingHorizontalState, Library = "Sword_Regular_A", Loop = false },
            new StateClip { State = FirstPersonArmsDriver.SwingOverheadState, Library = "TreeChopping_Loop", Loop = false },
            new StateClip { State = FirstPersonArmsDriver.ThrustState, Library = "Punch_Cross", Loop = false },
            new StateClip { State = FirstPersonArmsDriver.BlockState, Library = "Pistol_Idle_Loop", Loop = false },
            new StateClip { State = FirstPersonArmsDriver.HitReactState, Library = "Hit_Chest", Loop = false },
        };

        public const string ShieldIdleLibrary = "Idle_Shield_Loop";
        public const string OffHandMaskPath = Folder + "/FP_OffHandMask.mask";

        /// <summary>Where the right hand (the guard grip) sits in front of the camera in the Idle pose: right, down, forward (metres). The shoulder then lies at or behind the eye plane, so no open sleeve end shows.</summary>
        public static readonly Vector3 HandTarget = new Vector3(0.2f, -0.1f, 0.5f);

        public static string ClipPath(string name) { return ClipFolder + "/FP_" + name + ".anim"; }

        // ---------------------------------------------------------------- orchestration

        [MenuItem("Emberdelve/Visual/Build First Person Arms")]
        public static void MenuBuild() { Debug.Log("[Emberdelve][FirstPersonArms] " + EnsureAll(true)); }

        public static string EnsureAll(bool force)
        {
            if (!EmberdelveQuaterniusImport.EnsureConfigured()) return "Quaternius files missing";
            EmberdelveUndeadMaterials.EnsureAll();
            EmberdelveWeaponModels.EnsureAll(false);
            EmberdelveVisualSetup.EnsureFolder(Folder);
            EmberdelveVisualSetup.EnsureFolder(ClipFolder);
            EmberdelveVisualSetup.EnsureFolder(MeshFolder);
            EmberdelveVisualSetup.EnsureFolder(MaterialFolder);

            if (!force && AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null) return "up to date";

            Material sleeve = EnsureMaterial(SleeveTexturePath, SleeveMaterialPath, 128, true, 0.12f);
            Material skin = EnsureMaterial(SkinTexturePath, SkinMaterialPath, 64, false, 0.22f);

            Mesh mesh = BuildArmsMesh();
            if (mesh == null) return "arms mesh failed";

            Dictionary<string, AnimationClip> clips = BuildClips();
            AnimatorController controller = BuildController(clips);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(ControllerPath, ImportAssetOptions.ForceUpdate);
            string result = BuildPrefab(mesh, sleeve, skin, controller);

            WeaponViewModelSet set = EmberdelveWeaponModels.WriteSet();
            set.armsPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            return result;
        }

        // ---------------------------------------------------------------- mesh

        private static int GroupOf(string boneName)
        {
            string n = boneName.ToLowerInvariant();
            if (n.Contains("upper_arm") || n.Contains("forearm") || n.Contains("upperarm") || n.Contains("lowerarm")) return 1;
            if (n.Contains("hand") || n.Contains("wrist") || n.Contains("metacarpal") || n.Contains("finger") || n.Contains("f_index") || n.Contains("f_middle") || n.Contains("f_ring") || n.Contains("f_pinky") || n.Contains("thumb")) return 2;
            return 0;
        }

        /// <summary>The arms-only mesh: triangles whose three vertices are all dominated by arm / hand bones. Submesh 0 sleeves, submesh 1 hands.</summary>
        public static Mesh BuildArmsMesh()
        {
            GameObject fbx = AssetDatabase.LoadAssetAtPath<GameObject>(BodySource);
            Mesh source = fbx != null ? fbx.GetComponentInChildren<SkinnedMeshRenderer>(true).sharedMesh : null;
            if (fbx == null || source == null) return null;

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            try
            {
                SkinnedMeshRenderer smr = instance.GetComponentInChildren<SkinnedMeshRenderer>(true);
                Transform[] bones = smr.bones;
                int[] group = new int[bones.Length];
                bool[] left = new bool[bones.Length];
                for (int i = 0; i < bones.Length; i++)
                {
                    group[i] = bones[i] != null ? GroupOf(bones[i].name) : 0;
                    left[i] = bones[i] != null && bones[i].name.EndsWith(".L");
                }

                Vector3[] vertices = source.vertices;
                BoneWeight[] weights = source.boneWeights;
                Vector3[] normals = source.normals;
                int[] triangles = source.GetTriangles(0);
                for (int s = 1; s < source.subMeshCount; s++)
                {
                    int[] more = source.GetTriangles(s);
                    int[] merged = new int[triangles.Length + more.Length];
                    triangles.CopyTo(merged, 0);
                    more.CopyTo(merged, triangles.Length);
                    triangles = merged;
                }

                int[] vGroup = new int[vertices.Length];
                for (int v = 0; v < vertices.Length; v++) vGroup[v] = group[DominantBone(weights[v])];

                // Cylinder axes (shoulder -> hand, bind pose, in the mesh's local space) for the UVs.
                Vector3[] axisOrigin = new Vector3[2], axisDirection = new Vector3[2];
                for (int side = 0; side < 2; side++)
                {
                    string suffix = side == 0 ? ".L" : ".R";
                    Vector3 from = Vector3.zero, to = Vector3.right;
                    for (int i = 0; i < bones.Length; i++)
                    {
                        if (bones[i] == null || !bones[i].name.EndsWith(suffix)) continue;
                        if ((bones[i].name.Contains("upper_arm") || bones[i].name.Contains("upperarm01"))) from = smr.transform.InverseTransformPoint(bones[i].position);
                        if ((bones[i].name.Contains("DEF-hand") || bones[i].name.Contains("wrist"))) to = smr.transform.InverseTransformPoint(bones[i].position);
                    }

                    axisOrigin[side] = from;
                    axisDirection[side] = (to - from).normalized;
                }

                List<int> sleeveTris = new List<int>(), handTris = new List<int>();
                for (int t = 0; t < triangles.Length; t += 3)
                {
                    int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                    if (vGroup[a] == 0 || vGroup[b] == 0 || vGroup[c] == 0) continue;
                    bool hand = vGroup[a] == 2 || vGroup[b] == 2 || vGroup[c] == 2;
                    List<int> list = hand ? handTris : sleeveTris;
                    list.Add(a); list.Add(b); list.Add(c);
                }

                // Compact the vertices actually used.
                Dictionary<int, int> remap = new Dictionary<int, int>();
                List<Vector3> nv = new List<Vector3>(), nn = new List<Vector3>();
                List<Vector2> nuv = new List<Vector2>();
                List<BoneWeight> nw = new List<BoneWeight>();
                int Map(int index)
                {
                    int mapped;
                    if (remap.TryGetValue(index, out mapped)) return mapped;
                    mapped = nv.Count;
                    remap[index] = mapped;
                    nv.Add(vertices[index]);
                    nn.Add(normals.Length > index ? normals[index] : Vector3.up);
                    nw.Add(weights[index]);
                    int side = left[DominantBone(weights[index])] ? 0 : 1;
                    nuv.Add(CylinderUv(vertices[index], axisOrigin[side], axisDirection[side]));
                    return mapped;
                }

                List<int> sleeveFinal = new List<int>(), handFinal = new List<int>();
                for (int i = 0; i < sleeveTris.Count; i++) sleeveFinal.Add(Map(sleeveTris[i]));
                for (int i = 0; i < handTris.Count; i++) handFinal.Add(Map(handTris[i]));

                Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
                bool create = mesh == null;
                if (create) mesh = new Mesh();
                mesh.Clear();
                mesh.name = "FP_Arms";
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt16;
                mesh.SetVertices(nv);
                mesh.SetNormals(nn);
                mesh.SetUVs(0, nuv);
                mesh.boneWeights = nw.ToArray();
                mesh.bindposes = source.bindposes;
                mesh.subMeshCount = 2;
                mesh.SetTriangles(sleeveFinal, 0);
                mesh.SetTriangles(handFinal, 1);
                mesh.RecalculateBounds();
                if (create) AssetDatabase.CreateAsset(mesh, MeshPath); else EditorUtility.SetDirty(mesh);
                AssetDatabase.SaveAssets();
                return mesh;
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        public static int DominantBone(BoneWeight w)
        {
            int best = w.boneIndex0;
            float weight = w.weight0;
            if (w.weight1 > weight) { best = w.boneIndex1; weight = w.weight1; }
            if (w.weight2 > weight) { best = w.boneIndex2; weight = w.weight2; }
            if (w.weight3 > weight) { best = w.boneIndex3; }
            return best;
        }

        /// <summary>u: distance along the arm (about 9 tiles over 0.7 m); v: the angle round it (4 tiles). Texture wraps.</summary>
        private static Vector2 CylinderUv(Vector3 point, Vector3 origin, Vector3 axis)
        {
            Vector3 rel = point - origin;
            float along = Vector3.Dot(rel, axis);
            Vector3 radial = rel - axis * along;
            Vector3 e1 = Vector3.ProjectOnPlane(Vector3.up, axis).normalized;
            Vector3 e2 = Vector3.Cross(axis, e1);
            float angle = Mathf.Atan2(Vector3.Dot(radial, e2), Vector3.Dot(radial, e1));
            return new Vector2(along * 12f, angle / (2f * Mathf.PI) * 4f);
        }

        // ---------------------------------------------------------------- textures and materials

        private static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 362437);
                h = (h ^ (h >> 13)) * 1274126177u;
                return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
            }
        }

        private static float Noise(float x, float y, int seed)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float fx = x - xi, fy = y - yi;
            fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy);
            float a = Hash(xi, yi, seed), b = Hash(xi + 1, yi, seed), c = Hash(xi, yi + 1, seed), d = Hash(xi + 1, yi + 1, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        private static Material EnsureMaterial(string texturePath, string materialPath, int size, bool quilt, float smoothness)
        {
            string absolute = EmberdelveQuaterniusImport.Absolute(texturePath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute));
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    pixels[y * size + x] = quilt ? Sleeve(x, y, size) : Skin(x, y, size);
                }
            }

            texture.SetPixels32(pixels);
            File.WriteAllBytes(absolute, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(texturePath, ImportAssetOptions.ForceSynchronousImport);

            TextureImporter importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
            if (importer != null)
            {
                importer.filterMode = FilterMode.Point;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.mipmapEnabled = true;
                importer.maxTextureSize = 256;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.sRGBTexture = true;
                importer.SaveAndReimport();
            }

            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            bool create = material == null;
            if (create) material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath));
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Smoothness", smoothness);
            material.SetFloat("_Metallic", 0f);
            if (create) AssetDatabase.CreateAsset(material, materialPath); else EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>Quilted gambeson: dark brown-grey, diamond seams, puffy cells, frayed pale thread, dirt.</summary>
        private static Color32 Sleeve(int x, int y, int size)
        {
            int cell = size / 8;
            int a = (x + y) % cell, b = (x - y + size * 4) % cell;
            float m = Mathf.Min(a, cell - a), n = Mathf.Min(b, cell - b);
            float puff = Mathf.Min(m, n) / (cell * 0.5f);
            bool seam = Mathf.Min(m, n) < 1f;
            float dirt = Noise(x * 0.11f, y * 0.11f, 3);
            float grain = Hash(x, y, 7);
            float shade = 0.72f + 0.42f * puff;
            float baseValue = 0.15f + 0.05f * (dirt - 0.5f);
            Color c = new Color(baseValue * 1.18f, baseValue * 0.95f, baseValue * 0.78f);
            c *= shade;
            c += (grain - 0.5f) * 0.035f * Color.white;
            if (seam) c = c * 0.55f + (Hash(x / 2, y / 2, 11) > 0.72f ? new Color(0.14f, 0.12f, 0.09f) : Color.black);
            return (Color32)new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), 1f);
        }

        /// <summary>Weathered human skin: desaturated pale-brown, mottled, a few darker veins and dirt, not undead.</summary>
        private static Color32 Skin(int x, int y, int size)
        {
            float tone = Noise(x * 0.18f, y * 0.18f, 21);
            float fine = Noise(x * 0.9f, y * 0.9f, 22);
            float vein = Mathf.Abs(Noise(x * 0.12f, y * 0.35f, 23) - 0.5f) < 0.025f ? 0.12f : 0f;
            float dirt = Noise(x * 0.07f, y * 0.07f, 24);
            Color c = new Color(0.47f, 0.34f, 0.27f);
            c *= 0.88f + 0.22f * tone;
            c += new Color(0.02f, 0f, -0.01f) * (fine - 0.5f) * 2f;
            c -= new Color(0.05f, 0.07f, 0.06f) * vein;
            c = Color.Lerp(c, new Color(0.17f, 0.13f, 0.1f), Mathf.Clamp01((dirt - 0.62f) * 2.2f) * 0.55f);
            return (Color32)new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), 1f);
        }

        // ---------------------------------------------------------------- clips and controller

        private static AnimationClip Copy(string library, string name, bool loop)
        {
            AnimationClip source = EmberdelveEnemyVisualFactory.FindClip(library);
            if (source == null) { Debug.LogError("[Emberdelve][FirstPersonArms] humanoid clip '" + library + "' not found."); return null; }

            string path = ClipPath(name);
            if (AssetDatabase.LoadAssetAtPath<AnimationClip>(path) != null) AssetDatabase.DeleteAsset(path);
            AnimationClip copy = Object.Instantiate(source);
            copy.name = "FP_" + name;
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(source);
            settings.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(copy, settings);
            AssetDatabase.CreateAsset(copy, path);
            return copy;
        }

        private static Dictionary<string, AnimationClip> BuildClips()
        {
            Dictionary<string, AnimationClip> result = new Dictionary<string, AnimationClip>();
            for (int i = 0; i < Clips.Length; i++) result[Clips[i].State] = Copy(Clips[i].Library, Clips[i].State, Clips[i].Loop);
            result["ShieldIdle"] = Copy(ShieldIdleLibrary, "ShieldIdle", true);
            return result;
        }

        private static AnimatorController BuildController(Dictionary<string, AnimationClip> clips)
        {
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath) != null) AssetDatabase.DeleteAsset(ControllerPath);
            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);

            controller.AddParameter(FirstPersonArmsDriver.SpeedParameter, AnimatorControllerParameterType.Float);
            controller.AddParameter(new AnimatorControllerParameter { name = FirstPersonArmsDriver.WalkPlaybackParameter, type = AnimatorControllerParameterType.Float, defaultFloat = 1f });
            controller.AddParameter(new AnimatorControllerParameter { name = FirstPersonArmsDriver.SwingSpeedParameter, type = AnimatorControllerParameterType.Float, defaultFloat = 1f });
            controller.AddParameter(FirstPersonArmsDriver.BlockParameter, AnimatorControllerParameterType.Bool);
            controller.AddParameter(FirstPersonArmsDriver.SwingHorizontalTrigger, AnimatorControllerParameterType.Trigger);
            controller.AddParameter(FirstPersonArmsDriver.SwingOverheadTrigger, AnimatorControllerParameterType.Trigger);
            controller.AddParameter(FirstPersonArmsDriver.ThrustTrigger, AnimatorControllerParameterType.Trigger);
            controller.AddParameter(FirstPersonArmsDriver.HitReactTrigger, AnimatorControllerParameterType.Trigger);

            AnimatorStateMachine machine = controller.layers[0].stateMachine;
            Dictionary<string, AnimatorState> states = new Dictionary<string, AnimatorState>();
            for (int i = 0; i < FirstPersonArmsDriver.StateNames.Length; i++)
            {
                string name = FirstPersonArmsDriver.StateNames[i];
                AnimatorState state = machine.AddState(name, new Vector3(300f, i * 70f, 0f));
                state.motion = clips[name];
                states[name] = state;
            }

            machine.defaultState = states[FirstPersonArmsDriver.IdleState];

            AnimatorState idle = states[FirstPersonArmsDriver.IdleState];
            AnimatorState walk = states[FirstPersonArmsDriver.WalkState];
            AnimatorState block = states[FirstPersonArmsDriver.BlockState];
            AnimatorState hit = states[FirstPersonArmsDriver.HitReactState];

            walk.speedParameterActive = true;
            walk.speedParameter = FirstPersonArmsDriver.WalkPlaybackParameter;

            AddFloat(idle, walk, AnimatorConditionMode.Greater, 0.35f, 0.15f);
            AddFloat(walk, idle, AnimatorConditionMode.Less, 0.2f, 0.15f);

            // Block is held: from Idle / Walk while the bool is true, back to Idle when it is released.
            AddBool(idle, block, true, 0.1f);
            AddBool(walk, block, true, 0.1f);
            AddBool(block, idle, false, 0.12f);

            // Swings interrupt anything (also a swing of the same kind: a combo step restarts the clip); they play at SwingSpeed.
            string[][] swings =
            {
                new[] { FirstPersonArmsDriver.SwingHorizontalState, FirstPersonArmsDriver.SwingHorizontalTrigger },
                new[] { FirstPersonArmsDriver.SwingOverheadState, FirstPersonArmsDriver.SwingOverheadTrigger },
                new[] { FirstPersonArmsDriver.ThrustState, FirstPersonArmsDriver.ThrustTrigger },
            };
            for (int i = 0; i < swings.Length; i++)
            {
                AnimatorState swing = states[swings[i][0]];
                swing.speedParameterActive = true;
                swing.speedParameter = FirstPersonArmsDriver.SwingSpeedParameter;

                AnimatorStateTransition any = machine.AddAnyStateTransition(swing);
                any.hasExitTime = false;
                any.hasFixedDuration = true;
                any.duration = 0.06f;
                any.canTransitionToSelf = true;
                any.AddCondition(AnimatorConditionMode.If, 0f, swings[i][1]);

                AnimatorStateTransition exit = swing.AddTransition(idle);
                exit.hasExitTime = true;
                exit.exitTime = 0.95f;
                exit.hasFixedDuration = true;
                exit.duration = 0.12f;
            }

            // HitReact only from the calm states (the driver does not even send it during a swing).
            AnimatorState[] calm = { idle, walk, block };
            for (int i = 0; i < calm.Length; i++)
            {
                AnimatorStateTransition toHit = calm[i].AddTransition(hit);
                toHit.hasExitTime = false;
                toHit.hasFixedDuration = true;
                toHit.duration = 0.04f;
                toHit.AddCondition(AnimatorConditionMode.If, 0f, FirstPersonArmsDriver.HitReactTrigger);
            }

            AnimatorStateTransition hitExit = hit.AddTransition(idle);
            hitExit.hasExitTime = true;
            hitExit.exitTime = 0.9f;
            hitExit.hasFixedDuration = true;
            hitExit.duration = 0.1f;

            // Layer 1: the off hand (left arm + fingers only). Weight 1 while a shield is held, 0 otherwise (the driver).
            AvatarMask mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(OffHandMaskPath);
            if (mask == null) { mask = new AvatarMask(); AssetDatabase.CreateAsset(mask, OffHandMaskPath); }
            for (int i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++) mask.SetHumanoidBodyPartActive((AvatarMaskBodyPart)i, false);
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftArm, true);
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftFingers, true);
            EditorUtility.SetDirty(mask);

            controller.AddLayer("OffHand");
            AnimatorControllerLayer[] layers = controller.layers;
            layers[1].avatarMask = mask;
            layers[1].defaultWeight = 0f;
            AnimatorState shieldIdle = layers[1].stateMachine.AddState("ShieldIdle", new Vector3(300f, 0f, 0f));
            shieldIdle.motion = clips["ShieldIdle"];
            layers[1].stateMachine.defaultState = shieldIdle;
            controller.layers = layers;

            EditorUtility.SetDirty(controller);
            return controller;
        }

        private static void AddFloat(AnimatorState from, AnimatorState to, AnimatorConditionMode mode, float threshold, float duration)
        {
            AnimatorStateTransition t = from.AddTransition(to);
            t.hasExitTime = false;
            t.hasFixedDuration = true;
            t.duration = duration;
            t.AddCondition(mode, threshold, FirstPersonArmsDriver.SpeedParameter);
        }

        private static void AddBool(AnimatorState from, AnimatorState to, bool value, float duration)
        {
            AnimatorStateTransition t = from.AddTransition(to);
            t.hasExitTime = false;
            t.hasFixedDuration = true;
            t.duration = duration;
            t.AddCondition(value ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0f, FirstPersonArmsDriver.BlockParameter);
        }

        // ---------------------------------------------------------------- pose evaluation (edit mode, no Play)

        /// <summary>
        /// Poses the rig with a clip at a time (seconds), the way the live Animator shows it. AnimationClip.SampleAnimation is the only evaluation
        /// that works in edit mode (neither a PlayableGraph nor Animator.Update poses a rig outside Play). It writes the clip's root curves (heading,
        /// lunge) on the sampled object; the live Animator discards them (root motion off, checked in Play mode: body faces the model's +Z, hips
        /// stay over the root), so the root transform is put back and the bones keep their root-relative pose. NOTE: in Play the mannequin
        /// faces model +Z, while its bind pose (edit mode) faces -Z.
        /// </summary>
        public static void Evaluate(Animator animator, AnimationClip clip, float time)
        {
            if (animator == null || clip == null) return;
            Transform root = animator.transform;
            Vector3 position = root.localPosition;
            Quaternion rotation = root.localRotation;
            bool rootMotion = animator.applyRootMotion;
            animator.applyRootMotion = true; // edit-mode SampleAnimation only gives the live (+Z facing) pose with it on; the root is reset below
            clip.SampleAnimation(animator.gameObject, time);
            animator.applyRootMotion = rootMotion;
            root.localPosition = position;
            root.localRotation = rotation;
        }

        /// <summary>
        /// Poses an instance of the arms prefab with a clip; <paramref name="offHandClip"/> (the shield guard) is applied on top to the left arm and
        /// fingers only, like the masked animator layer does.
        /// </summary>
        public static void PoseInstance(GameObject armsInstance, AnimationClip clip, float time, AnimationClip offHandClip = null, float offHandTime = 0.3f)
        {
            Animator animator = armsInstance.GetComponentInChildren<Animator>(true);
            if (animator == null || clip == null) return;
            if (offHandClip != null)
            {
                Evaluate(animator, offHandClip, offHandTime);
                List<Transform> chain = new List<Transform>();
                Transform leftShoulder = animator.GetBoneTransform(HumanBodyBones.LeftShoulder);
                Transform leftUpper = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                Transform start = leftShoulder != null ? leftShoulder : leftUpper;
                if (start != null) chain.AddRange(start.GetComponentsInChildren<Transform>(true));
                Quaternion[] rotations = new Quaternion[chain.Count];
                for (int i = 0; i < chain.Count; i++) rotations[i] = chain[i].localRotation;
                Evaluate(animator, clip, time);
                for (int i = 0; i < chain.Count; i++) chain[i].localRotation = rotations[i];
                return;
            }

            Evaluate(animator, clip, time);
        }

        // ---------------------------------------------------------------- prefab

        private static string BuildPrefab(Mesh mesh, Material sleeve, Material skin, AnimatorController controller)
        {
            GameObject fbx = AssetDatabase.LoadAssetAtPath<GameObject>(BodySource);
            Avatar avatar = null;
            Object[] assets = AssetDatabase.LoadAllAssetsAtPath(BodySource);
            for (int i = 0; i < assets.Length; i++) if (assets[i] is Avatar a) { avatar = a; break; }

            // 1. Measure on a probe in the guard pose (hands, feet, facing) - the live Animator pose with root motion off.
            Vector3 facing, handRight, handLeftShield;
            Quaternion gripRotationRight, gripRotationLeft;
            Vector3 gripPositionRight, gripPositionLeft;
            Measure(fbx, avatar, out facing, out handRight, out gripPositionRight, out gripRotationRight, out gripPositionLeft, out gripRotationLeft, out handLeftShield);

            // 2. The prefab.
            GameObject root = new GameObject("FirstPersonArms");
            try
            {
                GameObject model = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
                PrefabUtility.UnpackPrefabInstance(model, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                model.name = ModelName;
                model.transform.SetParent(root.transform, false);

                Animator animator = model.GetComponent<Animator>();
                if (animator == null) animator = model.AddComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                if (avatar != null) animator.avatar = avatar;

                SkinnedMeshRenderer[] skinned = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                for (int i = 0; i < skinned.Length; i++)
                {
                    skinned[i].sharedMesh = mesh;
                    skinned[i].sharedMaterials = new[] { sleeve, skin };
                    skinned[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    skinned[i].receiveShadows = true;
                    skinned[i].updateWhenOffscreen = true;
                    skinned[i].gameObject.layer = LayerMask.NameToLayer("Ignore Raycast") >= 0 ? LayerMask.NameToLayer("Ignore Raycast") : 0;
                }

                // In Play the rig faces model +Z (see Evaluate): no turn. Put the guard hand in front of the camera.
                Quaternion turn = model.transform.localRotation; // the FBX root's own rotation; the probe was measured with it
                model.transform.localPosition = HandTarget - turn * handRight;

                Transform rightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
                Transform leftHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
                if (rightHand == null || leftHand == null) return "no hand bones";

                Transform gripRight = new GameObject(GripRightName).transform;
                gripRight.SetParent(rightHand, false);
                gripRight.localPosition = gripPositionRight;
                gripRight.localRotation = gripRotationRight;
                Transform gripLeft = new GameObject(GripLeftName).transform;
                gripLeft.SetParent(leftHand, false);
                gripLeft.localPosition = gripPositionLeft;
                gripLeft.localRotation = gripRotationLeft;

                Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < colliders.Length; i++) Object.DestroyImmediate(colliders[i]);

                FirstPersonArmsDriver driver = root.AddComponent<FirstPersonArmsDriver>();
                SerializedObject serialized = new SerializedObject(driver);
                serialized.FindProperty("animator").objectReferenceValue = animator;
                serialized.FindProperty("gripRight").objectReferenceValue = gripRight;
                serialized.FindProperty("gripLeft").objectReferenceValue = gripLeft;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                AssetDatabase.ImportAsset(PrefabPath, ImportAssetOptions.ForceUpdate);
                return "built; facing=" + facing.ToString("0.00") + " handR=" + handRight.ToString("0.00") + " turn=" + turn.eulerAngles.ToString("0");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>
        /// Reads the guard poses on a probe copy of the rig. facing = where the feet point (flattened) in the model's own frame; handRight = the right hand
        /// in the sword guard; the grips are expressed in the hand bones' local frames. Sword: blade up and a little forward, flat side facing the model's
        /// right, grip three quarters of the way from the wrist to the middle knuckle (the V3b enemy rule). Shield: face forward and slightly out, upright,
        /// centred a hand's depth in front of the left hand, measured in the shield guard pose.
        /// </summary>
        private static void Measure(GameObject fbx, Avatar avatar, out Vector3 facing, out Vector3 handRight, out Vector3 gripPositionRight, out Quaternion gripRotationRight, out Vector3 gripPositionLeft, out Quaternion gripRotationLeft, out Vector3 handLeftShield)
        {
            GameObject probe = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            PrefabUtility.UnpackPrefabInstance(probe, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            try
            {
                Animator animator = probe.GetComponent<Animator>();
                if (animator == null) animator = probe.AddComponent<Animator>();
                if (avatar != null) animator.avatar = avatar;

                // Right grip in the Idle hold (live frame: the rig faces +Z, its right is +X).
                Evaluate(animator, EmberdelveEnemyVisualFactory.FindClip(Clips[0].Library), 0.3f);
                facing = Vector3.forward;
                Vector3 right = Vector3.right;
                Transform hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
                Transform knuckle = animator.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
                Vector3 tip = (Vector3.up + facing * 0.45f).normalized;
                Vector3 flat = Vector3.ProjectOnPlane(right, tip).normalized;
                Quaternion world = Quaternion.LookRotation(flat, tip);
                Vector3 grip = knuckle != null ? Vector3.Lerp(hand.position, knuckle.position, 0.75f) : hand.position;
                gripPositionRight = hand.InverseTransformPoint(grip);
                gripRotationRight = Quaternion.Inverse(hand.rotation) * world;
                handRight = hand.position;

                // Left grip in the shield guard.
                Evaluate(animator, EmberdelveEnemyVisualFactory.FindClip(ShieldGripClip), 0.3f);
                Transform leftHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
                Vector3 shieldFacing = (facing - right * 0.3f).normalized;
                Quaternion shieldWorld = Quaternion.LookRotation(shieldFacing, Vector3.up);
                // Anchor the shield to the palm, as for a weapon grip. A world-forward
                // offset becomes a sideways/downward gap when the animated wrist turns.
                Transform leftKnuckle = animator.GetBoneTransform(HumanBodyBones.LeftMiddleProximal);
                Vector3 centre = leftKnuckle != null
                    ? Vector3.Lerp(leftHand.position, leftKnuckle.position, 0.75f)
                    : leftHand.position;
                gripPositionLeft = leftHand.InverseTransformPoint(centre);
                gripRotationLeft = Quaternion.Inverse(leftHand.rotation) * shieldWorld;
                handLeftShield = leftHand.position;
            }
            finally
            {
                Object.DestroyImmediate(probe);
            }
        }
    }
}
