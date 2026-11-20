using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Emberdelve.AI;
using Emberdelve.Combat;
using Emberdelve.Data;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Review only: poses the shipped enemy visual prefabs through the attack exactly as <see cref="EnemyVisualDriver"/> scrubs them
    /// (phase -> <see cref="EnemyAttackPhase.NormalizedTime"/> -> Play("Attack")), and draws the authority's swept weapon segment of the
    /// same instant on top (red: the segment now, orange dots: the tip path of the whole active phase). It also logs, per active sample,
    /// how far the visible weapon's grip and tip are from the swept segment's base and tip. Nothing is saved.
    /// </summary>
    public static class EmberdelveEnemySwingReview
    {
        private const string Folder = "Logs/review/enemy-swing";
        private const int W = 640, H = 480;

        private struct Frame { public byte Phase; public float Fraction; public string Label; }

        private static readonly Frame[] Timeline =
        {
            new Frame { Phase = 0, Fraction = 0.3f, Label = "idle" },
            new Frame { Phase = 5, Fraction = 0.25f, Label = "walk" },
            new Frame { Phase = 1, Fraction = 0f, Label = "windup 0" },
            new Frame { Phase = 1, Fraction = 0.5f, Label = "windup .5" },
            new Frame { Phase = 1, Fraction = 0.8f, Label = "windup .8" },
            new Frame { Phase = 1, Fraction = 0.9f, Label = "windup .9" },
            new Frame { Phase = 2, Fraction = 0f, Label = "active 0" },
            new Frame { Phase = 2, Fraction = 0.25f, Label = "active .25" },
            new Frame { Phase = 2, Fraction = 0.5f, Label = "active .5" },
            new Frame { Phase = 2, Fraction = 0.75f, Label = "active .75" },
            new Frame { Phase = 2, Fraction = 1f, Label = "active 1" },
            new Frame { Phase = 3, Fraction = 0.35f, Label = "recovery .35" },
        };

        [MenuItem("Emberdelve/Visual/Review Enemy Swing vs Sweep")]
        public static void Render()
        {
            Directory.CreateDirectory(Folder);
            foreach (string f in Directory.GetFiles(Folder, "*.png")) File.Delete(f);
            WeaponData claw = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_HollowClaw.asset");
            WeaponData maul = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_WardenMaul.asset");
            var report = new StringBuilder();
            
            RenderVariant("Minion", claw, 1f, report);
            RenderVariant("Rogue", claw, 1f, report);
            RenderVariant("Warrior", maul, EmberdelveEnemyVisualFactory.EliteScale, report);
            File.WriteAllText(Folder + "/report.txt", report.ToString());
            Debug.Log("[EnemySwingReview]\n" + report);
        }

        /// <summary>Batch entry for the CI mirror only: rebuilds the enemy visuals and the data library (the baked enemy sweeps), then renders.</summary>
        public static void RebuildBatch()
        {
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_NetBoot.unity");
            try
            {
                EmberdelveEnemyVisualFactory.EnsureAll(true);
                EmberdelveAssetLibrary.BuildAll();
                AssetDatabase.SaveAssets();
                Render();
            }
            catch (System.Exception e) { Debug.LogException(e); EditorApplication.Exit(1); return; }
            EditorApplication.Exit(0);
        }

        /// <summary>Batch entry for the CI mirror only.</summary>
        public static void RenderBatch()
        {
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_NetBoot.unity");
            try { Render(); }
            catch (System.Exception e) { Debug.LogException(e); EditorApplication.Exit(1); return; }
            EditorApplication.Exit(0);
        }

        private static void RenderVariant(string name, WeaponData weapon, float scale, StringBuilder report)
        {
            EmberdelveEnemyVisualFactory.Variant variant = EmberdelveEnemyVisualFactory.Find(name);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(EmberdelveEnemyVisualFactory.PrefabPath(name));
            if (variant == null || prefab == null || weapon == null || !weapon.TryResolveSwingPattern(0, 0, out SwingPatternData pattern))
            {
                report.AppendLine(name + ": skipped (prefab, weapon or pattern missing)");
                return;
            }

            float bladeLength = pattern.BladeLength(weapon);
            if (name == "Minion")
                foreach (string c in new[] { "Sword_Attack" })
                    ArmTrace(c, prefab, scale, report, EmberdelveEnemyVisualFactory.FindClip(c));
            report.AppendLine($"{name}: pattern {pattern.patternId} reach {pattern.EffectiveReach(weapon):F2} bladeLength {bladeLength:F2} visual weapon {variant.MainHandLength:F2} x{scale:F2}");

            foreach (bool top in new[] { false, true })
            {
                var files = new List<string>();
                for (int i = 0; i < Timeline.Length; i++)
                {
                    Frame frame = Timeline[i];
                    string path = $"{Folder}/{name}-{(top ? "top" : "side")}-{i:00}.png";
                    EmberdelveViewmodelPreview.Capture(path, W, H, top ? 50f : 40f, true, (root, camera) =>
                    {
                        GameObject visual = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                        visual.transform.SetParent(root, false);
                        visual.transform.localScale = Vector3.one * scale;
                        Animator animator = visual.GetComponentInChildren<Animator>();
                        // The Servitor's real phase lengths: anticipation = brain telegraph + swing windup, then the pattern's own timing.
                        float duration = frame.Phase == 1 ? 0.7f : frame.Phase == 2 ? pattern.activeTime : pattern.recoveryTime;
                        float normalized = new EnemyAttackPhase(frame.Phase, frame.Fraction * duration, duration).NormalizedTime();
                        AnimationClip clip = frame.Phase == 0 ? EmberdelveEnemyVisualFactory.FindClip(variant.Idle)
                            : frame.Phase == 5 ? EmberdelveEnemyVisualFactory.FindClip(variant.Walk)
                            : AssetDatabase.LoadAssetAtPath<AnimationClip>(EmberdelveEnemyVisualFactory.AttackClipPath(name));
                        if (frame.Phase == 0 || frame.Phase == 5) normalized = frame.Fraction;
                        AnimationClip guard = string.IsNullOrEmpty(variant.OffHand) ? null : EmberdelveEnemyVisualFactory.FindClip("Idle_Shield_Loop");
                        EmberdelveFirstPersonArms.PoseInstance(visual, clip, normalized * (clip != null ? clip.length : 1f), guard, 0.3f);

                        Vector3 eye = root.position + Vector3.up * EnemyAgent.AttackEyeHeight;
                        SwingFrame swing = new SwingFrame(eye, 0f, 0f);
                        for (int s = 0; s <= 16; s++)
                        {
                            SwingGeometry.PathSegment(swing, pattern, bladeLength, s / 16f, out _, out Vector3 tipDot);
                            Marker(root, tipDot, 0.035f, new Color(1f, 0.55f, 0.1f));
                        }

                        if (frame.Phase == 2)
                        {
                            SwingGeometry.PathSegment(swing, pattern, bladeLength, frame.Fraction, out Vector3 b, out Vector3 t);
                            Bar(root, b, t, 0.025f, new Color(1f, 0.1f, 0.1f));
                            Transform held = FindDeep(visual.transform, variant.MainHand);
                            if (!top && held != null)
                            {
                                Vector3 grip = held.position;
                                Vector3 tip = held.position + held.up * variant.MainHandLength * scale;
                                report.AppendLine($"  {frame.Label}: grip->base {(grip - b).magnitude:F2} m, tip->tip {(tip - t).magnitude:F2} m, visual tip {Local(tip, eye)} swept tip {Local(t, eye)}");
                            }
                        }

                        if (top)
                        {
                            camera.transform.SetPositionAndRotation(root.position + new Vector3(0f, 5.2f, 0.9f), Quaternion.Euler(90f, 0f, 0f));
                        }
                        else
                        {
                            Quaternion look = Quaternion.Euler(10f, 210f, 0f);
                            camera.transform.SetPositionAndRotation(root.position + new Vector3(0f, 1.2f, 0.7f) - look * Vector3.forward * 3.4f, look);
                        }
                    });
                    files.Add(path);
                }

                EmberdelveViewmodelPreview.Sheet($"{Folder}/{name}-{(top ? "top" : "side")}-sheet.png", files, 5, W, H);
            }
        }

        private static void ArmTrace(string name, GameObject prefab, float scale, StringBuilder report, AnimationClip clip)
        {
            GameObject visual = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                visual.transform.localScale = Vector3.one * scale;
                Animator animator = visual.GetComponentInChildren<Animator>();
                if (clip == null) { report.AppendLine("  trace " + name + ": clip missing"); return; }
                AnimationClip copy = Object.Instantiate(clip);
                AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
                settings.loopTime = false; settings.loopBlendOrientation = true; settings.loopBlendPositionY = true; settings.loopBlendPositionXZ = false;
                AnimationUtility.SetAnimationClipSettings(copy, settings);
                clip = copy;
                report.AppendLine($"  trace {name}: {clip.length:F2} s");
                Vector3 eye = visual.transform.position + Vector3.up * EnemyAgent.AttackEyeHeight;
                Vector3 last = Vector3.zero;
                for (int i = 0; i <= 80; i++)
                {
                    float n = i / 80f;
                    EmberdelveFirstPersonArms.Evaluate(animator, clip, n * clip.length);
                    Vector3 sh = animator.GetBoneTransform(HumanBodyBones.RightUpperArm).position;
                    Vector3 el = animator.GetBoneTransform(HumanBodyBones.RightLowerArm).position;
                    Vector3 ha = animator.GetBoneTransform(HumanBodyBones.RightHand).position;
                    float speed = i == 0 ? 0f : (ha - last).magnitude * 80f / clip.length;
                    last = ha;
                    report.AppendLine($"  trace n={n:F3} shoulder {Local(sh, eye)} elbow {Local(el, eye)} hand {Local(ha, eye)} handSpeed {speed:F2} m/s");
                }
            }
            finally { Object.DestroyImmediate(visual); }
        }

        /// <summary>A library clip with the runtime attack-copy settings (root XZ to the root, which is reset), on the Minion prefab.</summary>
        private static void RenderClipStrip(string clipName)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(EmberdelveEnemyVisualFactory.PrefabPath("Minion"));
            AnimationClip source = EmberdelveEnemyVisualFactory.FindClip(clipName);
            if (prefab == null || source == null) return;
            AnimationClip clip = Object.Instantiate(source);
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(source);
            settings.loopTime = false; settings.loopBlendOrientation = true; settings.loopBlendPositionY = true; settings.loopBlendPositionXZ = false;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            var files = new List<string>();
            for (int i = 0; i < 20; i++)
            {
                float n = i / 38f;
                string path = $"{Folder}/clip-{clipName}-{i:00}.png";
                EmberdelveViewmodelPreview.Capture(path, 480, 480, 40f, true, (root, camera) =>
                {
                    GameObject visual = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    visual.transform.SetParent(root, false);
                    EmberdelveFirstPersonArms.Evaluate(visual.GetComponentInChildren<Animator>(), clip, n * clip.length);
                    Quaternion look = Quaternion.Euler(6f, 235f, 0f);
                    camera.transform.SetPositionAndRotation(root.position + new Vector3(0f, 1.1f, 0.4f) - look * Vector3.forward * 3.0f, look);
                });
                files.Add(path);
            }
            EmberdelveViewmodelPreview.Sheet($"{Folder}/clip-{clipName}-sheet.png", files, 10, 480, 480);
            Object.DestroyImmediate(clip);
        }

        private static string Local(Vector3 world, Vector3 eye)
        {
            Vector3 d = world - eye;
            return $"({d.x:F2},{d.y:F2},{d.z:F2})";
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
            return null;
        }

        private static Material Unlit(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            var material = new Material(shader != null ? shader : Shader.Find("Unlit/Color"));
            material.color = color;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            return material;
        }

        private static void Marker(Transform root, Vector3 position, float size, Color color)
        {
            GameObject dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.DestroyImmediate(dot.GetComponent<Collider>());
            dot.transform.SetParent(root, true);
            dot.transform.position = position;
            dot.transform.localScale = Vector3.one * size;
            dot.GetComponent<Renderer>().sharedMaterial = Unlit(color);
        }

        private static void Bar(Transform root, Vector3 a, Vector3 b, float thickness, Color color)
        {
            GameObject bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(bar.GetComponent<Collider>());
            bar.transform.SetParent(root, true);
            bar.transform.position = (a + b) * 0.5f;
            bar.transform.rotation = Quaternion.LookRotation(b - a);
            bar.transform.localScale = new Vector3(thickness, thickness, (b - a).magnitude);
            bar.GetComponent<Renderer>().sharedMaterial = Unlit(color);
        }
    }
}
