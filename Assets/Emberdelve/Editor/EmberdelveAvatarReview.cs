using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Emberdelve.Combat;
using Emberdelve.Data;
using Emberdelve.Player;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Edit-mode review frames of a class body seen from outside, posed the way the game poses other players and rival bots
    /// (<see cref="RemoteWeaponPresentation"/>): every combo step of a weapon, the guard, the bow draw and the locomotion loops.
    /// Written to Logs/review/avatar. No Play mode, nothing is saved to assets.
    /// </summary>
    public static class EmberdelveAvatarReview
    {
        public const string Folder = "Logs/review/avatar";
        private const int Cell = 420;
        private const float EyeHeight = 1.62f;

        /// <summary>Batch entry for the test mirror (a batch Editor starts in an unsaved scene).</summary>
        public static void RenderBatch()
        {
            if (EditorBuildSettings.scenes.Length > 0)
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(EditorBuildSettings.scenes[0].path, UnityEditor.SceneManagement.OpenSceneMode.Single);
            Render();
        }

        [MenuItem("Emberdelve/Debug/Render Avatar Review (Edit Mode)")]
        public static void Render()
        {
            if (EditorApplication.isPlaying) { Debug.LogError("[AvatarReview] leave Play mode first"); return; }
            CharacterClassData warrior = AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_Warrior.asset");
            CharacterClassData archer = AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_Archer.asset");
            if (warrior == null || warrior.characterVisual == null) { Debug.LogError("[AvatarReview] Class_Warrior has no body"); return; }

            Arms = "Sword_Idle";
            Combo("sword", warrior.characterVisual, "ashen_short_sword", "emberbulwark_shield");
            Combo("arming", warrior.characterVisual, "arming_sword", "");
            Combo("two", warrior.characterVisual, "two_handed_sword", "");
            Combo("rapier", warrior.characterVisual, "ember_rapier", "");
            Ranged(archer != null && archer.characterVisual != null ? archer.characterVisual : warrior.characterVisual, warrior.characterVisual);
            Still("still", warrior.characterVisual, archer != null && archer.characterVisual != null ? archer.characterVisual : warrior.characterVisual);
            Debug.Log("[AvatarReview] rendered to " + Folder);
        }

        /// <summary>Library clips sampled on the warrior body (sword in hand), six moments each: what a clip does before it is used.</summary>
        [MenuItem("Emberdelve/Debug/Render Avatar Clip Sheet (Edit Mode)")]
        public static void RenderClips()
        {
            if (EditorApplication.isPlaying) { Debug.LogError("[AvatarReview] leave Play mode first"); return; }
            CharacterClassData warrior = AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_Warrior.asset");
            var files = new List<string>();
            foreach (string clipName in new[] { "Sword_Idle", "Sword_Attack", "Sword_Regular_A", "Sword_Regular_B", "Sword_Regular_C", "Sword_Block", "Idle_Shield_Loop", "Shield_OneShot" })
            {
                AnimationClip clip = EmberdelveEnemyVisualFactory.FindClip(clipName);
                if (clip == null) { Debug.LogWarning("[AvatarReview] no clip " + clipName); continue; }
                for (int i = 0; i < 6; i++)
                {
                    float at = i / 5f * 0.98f;
                    files.Add(Shot(Folder + "/clip-" + clipName + "-" + i + ".png", warrior.characterVisual, null, at, false, p =>
                        p.Present("ashen_short_sword", "emberbulwark_shield", "", false, false, 0f, default, Frame(), bot: true), clip));
                }
            }
            EmberdelveViewmodelPreview.Sheet(Folder + "/sheet-clips.png", files, 6, Cell, Cell);
            Debug.Log("[AvatarReview] clip sheet rendered");
        }

        private static void Combo(string name, GameObject body, string mainId, string offId)
        {
            WeaponData weapon = EmberdelveViewmodelPreview.FindWeapon(mainId);
            if (!string.IsNullOrEmpty(offId)) EmberdelveViewmodelPreview.FindWeapon(offId);
            if (weapon == null || weapon.comboSteps == null) return;
            var files = new List<string>();
            int n = 0;
            for (int step = 0; step < weapon.comboSteps.Length; step++)
            {
                ComboStep cs = weapon.comboSteps[step];
                SwingPatternData pattern = weapon.swingPatterns[cs.patternIndex];
                SwingTiming timing = pattern.TimingFor(cs.windupScale, cs.recoveryScale,
                    step == weapon.comboSteps.Length - 1 ? weapon.comboFinisherRecovery : 0f, cs.activeScale);
                // Six moments of the step: start, mid wind-up, strike start, mid strike, strike end, mid recovery.
                float[] times =
                {
                    0.02f, timing.Windup * 0.6f, timing.Windup, timing.Windup + timing.Active * 0.5f, timing.ActiveEnd,
                    timing.ActiveEnd + (timing.Total - timing.ActiveEnd) * 0.5f
                };
                foreach (float time in times)
                {
                    float t = time;
                    string path = Folder + "/" + name + "-" + (n++).ToString("00") + "-s" + step + ".png";
                    files.Add(Shot(path, body, "Idle", 0.3f, false, presentation =>
                        presentation.Present(mainId, offId, pattern.patternId, false, true, t, timing, Frame(), bot: true)));
                }
            }
            EmberdelveViewmodelPreview.Sheet(Folder + "/sheet-" + name + ".png", files, 6, Cell, Cell);
        }

        /// <summary>The bow from carried to full draw and the crossbow, on both bodies, front and side.</summary>
        private static void Ranged(GameObject archer, GameObject warrior)
        {
            var files = new List<string>();
            Arms = null;
            foreach (GameObject body in new[] { archer, warrior })
                foreach (bool side in new[] { false, true })
                {
                    string v = (body == archer ? "a" : "w") + (side ? "-side" : "");
                    files.Add(Shot(Folder + "/bow-carry-" + v + ".png", body, "Idle", 0.3f, side, p =>
                        p.Present("hollow_bow", "", "", false, false, 0f, default, Frame(), bot: true)));
                    foreach (float draw in new[] { 0.06f, 0.16f, 0.5f, 1f })
                    {
                        float d = draw;
                        files.Add(Shot(Folder + "/bow-" + d.ToString("0.00") + "-" + v + ".png", body, "Idle", 0.3f, side, p =>
                        {
                            p.Present("hollow_bow", "", "", false, false, 0f, default, Frame(), bot: true);
                            p.PresentDraw("hollow_bow", true, d);
                        }));
                    }
                    files.Add(Shot(Folder + "/xbow-" + v + ".png", body, "Idle", 0.3f, side, p =>
                        p.Present("ember_crossbow", "", "", false, false, 0f, default, Frame(), bot: true)));
                    files.Add(Shot(Folder + "/xbow-guard-" + v + ".png", body, "Idle", 0.3f, side, p =>
                        p.Present("ember_crossbow", "", "", true, false, 0f, default, Frame(), bot: true)));
                }
            EmberdelveViewmodelPreview.Sheet(Folder + "/sheet-ranged.png", files, 7, Cell, Cell);
        }

        private static void Still(string name, GameObject warrior, GameObject archer)
        {
            var files = new List<string>();
            foreach (bool side in new[] { false, true })
            {
                string v = side ? "-side" : "";
                Arms = "Sword_Idle";
                files.Add(Shot(Folder + "/" + name + "-idle" + v + ".png", warrior, "Idle", 0.3f, side, p =>
                    p.Present("ashen_short_sword", "emberbulwark_shield", "", false, false, 0f, default, Frame(), bot: true)));
                Arms = "Idle_Shield_Loop";
                files.Add(Shot(Folder + "/" + name + "-guard" + v + ".png", warrior, "Idle", 0.3f, side, p =>
                    p.Present("ashen_short_sword", "emberbulwark_shield", "", true, false, 0f, default, Frame(), bot: true)));
                Arms = "Sword_Block";
                files.Add(Shot(Folder + "/" + name + "-guard2h" + v + ".png", warrior, "Idle", 0.3f, side, p =>
                    p.Present("two_handed_sword", "", "", true, false, 0f, default, Frame(), bot: true)));
                Arms = "Sword_Idle";
                files.Add(Shot(Folder + "/" + name + "-walk" + v + ".png", warrior, "Walk", 0.25f, side, p =>
                    p.Present("ashen_short_sword", "emberbulwark_shield", "", false, false, 0f, default, Frame(), bot: true)));
                files.Add(Shot(Folder + "/" + name + "-run" + v + ".png", warrior, "Run", 0.2f, side, p =>
                    p.Present("arming_sword", "", "", false, false, 0f, default, Frame(), bot: true)));
                Arms = null;
                files.Add(Shot(Folder + "/" + name + "-bow" + v + ".png", archer, "Idle", 0.3f, side, p =>
                {
                    p.Present("hollow_bow", "", "", false, false, 0f, default, Frame(), bot: true);
                    p.PresentDraw("hollow_bow", true, 1f);
                }));
            }
            EmberdelveViewmodelPreview.Sheet(Folder + "/sheet-" + name + ".png", files, 6, Cell, Cell);

            // The weapon hand close up: carried, walking, mid strike of each short sword step, guard.
            var hands = new List<string>();
            WeaponData sword = EmberdelveViewmodelPreview.FindWeapon("ashen_short_sword");
            CloseUp = true;
            try
            {
                Arms = "Sword_Idle";
                hands.Add(Shot(Folder + "/hand-idle.png", warrior, "Idle", 0.3f, false, p =>
                    p.Present("ashen_short_sword", "emberbulwark_shield", "", false, false, 0f, default, Frame(), bot: true)));
                hands.Add(Shot(Folder + "/hand-walk.png", warrior, "Walk", 0.25f, false, p =>
                    p.Present("ashen_short_sword", "emberbulwark_shield", "", false, false, 0f, default, Frame(), bot: true)));
                for (int step = 0; step < sword.comboSteps.Length && step < 3; step++)
                {
                    ComboStep cs = sword.comboSteps[step];
                    SwingPatternData pattern = sword.swingPatterns[cs.patternIndex];
                    SwingTiming timing = pattern.TimingFor(cs.windupScale, cs.recoveryScale, 0f, cs.activeScale);
                    int moment = 0;
                    foreach (float at in new[] { timing.Windup * 0.6f, timing.Windup, timing.Windup + timing.Active * 0.5f, timing.ActiveEnd })
                    {
                        float t = at;
                        hands.Add(Shot(Folder + "/hand-strike" + step + "-" + (moment++) + ".png", warrior, "Idle", 0.3f, false, p =>
                            p.Present("ashen_short_sword", "emberbulwark_shield", pattern.patternId, false, true, t, timing, Frame(), bot: true)));
                    }
                }
                Arms = "Idle_Shield_Loop";
                hands.Add(Shot(Folder + "/hand-guard.png", warrior, "Idle", 0.3f, false, p =>
                    p.Present("ashen_short_sword", "emberbulwark_shield", "", true, false, 0f, default, Frame(), bot: true)));
            }
            finally { CloseUp = false; }
            EmberdelveViewmodelPreview.Sheet(Folder + "/sheet-hands.png", hands, 5, Cell, Cell);

            // Guards from four sides, wide enough for both arms: shield guard, sword-only guard, two-handed guard, bow.
            var guards = new List<string>();
            CloseUp = true;
            Vector3 usual = CloseFrom;
            try
            {
                foreach (Vector3 from in new[] { new Vector3(1.5f, 0.4f, 1.6f), new Vector3(-1.5f, 0.4f, 1.6f), new Vector3(1.9f, 0.3f, -0.9f), new Vector3(-1.9f, 0.3f, -0.9f) })
                {
                    CloseFrom = from;
                    string v = "-" + guards.Count;
                    CloseLeft = true; Arms = "Idle_Shield_Loop";
                    guards.Add(Shot(Folder + "/guard-shield" + v + ".png", warrior, "Idle", 0.3f, false, p =>
                        p.Present("ashen_short_sword", "emberbulwark_shield", "", true, false, 0f, default, Frame(), bot: true)));
                    CloseLeft = false; Arms = "Sword_Block";
                    guards.Add(Shot(Folder + "/guard-sword" + v + ".png", warrior, "Idle", 0.3f, false, p =>
                        p.Present("arming_sword", "", "", true, false, 0f, default, Frame(), bot: true)));
                    guards.Add(Shot(Folder + "/guard-two" + v + ".png", warrior, "Idle", 0.3f, false, p =>
                        p.Present("two_handed_sword", "", "", true, false, 0f, default, Frame(), bot: true)));
                    CloseLeft = true; Arms = null;
                    guards.Add(Shot(Folder + "/guard-bow" + v + ".png", archer, "Idle", 0.3f, false, p =>
                    {
                        p.Present("hollow_bow", "", "", false, false, 0f, default, Frame(), bot: true);
                        p.PresentDraw("hollow_bow", true, 1f);
                    }));
                }
            }
            finally { CloseUp = false; CloseLeft = false; CloseFrom = usual; }
            EmberdelveViewmodelPreview.Sheet(Folder + "/sheet-guards.png", guards, 4, Cell, Cell);
        }

        private static SwingFrame Frame() =>
            new SwingFrame(EmberdelveViewmodelPreview.Origin + new Vector3(0f, EyeHeight, 0f), Quaternion.identity);

        private static string Shot(string path, GameObject bodyPrefab, string state, float clipTime, bool side, System.Action<RemoteWeaponPresentation> present,
            AnimationClip direct = null)
        {
            EmberdelveViewmodelPreview.FindWeapon("ashen_short_sword");
            return EmberdelveViewmodelPreview.Capture(path, Cell, Cell, 38f, true, (root, camera) =>
            {
                Vector3 origin = EmberdelveViewmodelPreview.Origin;
                Vector3 from = side ? new Vector3(4.6f, 1.3f, 0.5f) : new Vector3(2.6f, 1.7f, 3.9f);
                camera.transform.position = origin + from;
                camera.transform.rotation = Quaternion.LookRotation(origin + new Vector3(0f, 1.05f, 0.45f) - camera.transform.position);
                bool close = CloseUp;

                var body = new GameObject("ReviewBody");
                body.transform.SetParent(root, false);
                body.transform.position = origin;
                GameObject model = (GameObject)PrefabUtility.InstantiatePrefab(bodyPrefab);
                model.transform.SetParent(body.transform, false);
                Animator animator = model.GetComponentInChildren<Animator>(true);
                AnimationClip clip = direct != null ? direct : FindClip(animator, state);
                if (clip != null) EmberdelveFirstPersonArms.Evaluate(animator, clip, clipTime * clip.length);
                if (direct == null && Arms != null) OverlayArms(animator, EmberdelveEnemyVisualFactory.FindClip(Arms), clip, clipTime);

                RemoteWeaponPresentation presentation = body.AddComponent<RemoteWeaponPresentation>();
                presentation.BindBody(body.transform, model);
                present(presentation);
                presentation.PoseNow();
                if (close && animator != null && animator.isHuman)
                {
                    // Close on the weapon hand, from the front-right and a little above.
                    Vector3 hand = animator.GetBoneTransform(CloseLeft ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand).position;
                    Vector3 elbow = animator.GetBoneTransform(CloseLeft ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm).position;
                    Vector3 look = Vector3.Lerp(hand, elbow, 0.35f);
                    camera.transform.position = look + CloseFrom;
                    camera.transform.rotation = Quaternion.LookRotation(look - camera.transform.position);
                }
            }, 0.1f);
        }

        /// <summary>Library clip the arm layer plays in the next shots (null = none), as <see cref="PlayerAvatar"/> selects it in the game.</summary>
        private static string Arms;
        private static bool CloseUp, CloseLeft;
        private static Vector3 CloseFrom = new Vector3(0.75f, 0.3f, 0.95f);

        private static void OverlayArms(Animator animator, AnimationClip arms, AnimationClip baseClip, float baseTime)
        {
            if (animator == null || arms == null || !animator.isHuman) return;
            EmberdelveFirstPersonArms.Evaluate(animator, arms, 0.3f * arms.length);
            var keep = new List<Transform>();
            foreach (HumanBodyBones start in new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm })
            {
                Transform bone = animator.GetBoneTransform(start);
                if (bone != null) keep.AddRange(bone.GetComponentsInChildren<Transform>(true));
            }
            var rotations = new Quaternion[keep.Count];
            for (int i = 0; i < keep.Count; i++) rotations[i] = keep[i].localRotation;
            if (baseClip != null) EmberdelveFirstPersonArms.Evaluate(animator, baseClip, baseTime * baseClip.length);
            for (int i = 0; i < keep.Count; i++) keep[i].localRotation = rotations[i];
        }

        private static AnimationClip FindClip(Animator animator, string state)
        {
            if (animator == null || animator.runtimeAnimatorController == null) return null;
            AnimationClip partial = null;
            foreach (AnimationClip clip in animator.runtimeAnimatorController.animationClips)
            {
                if (clip == null) continue;
                if (clip.name == state) return clip;
                if (partial == null && clip.name.IndexOf(state, System.StringComparison.OrdinalIgnoreCase) >= 0) partial = clip;
            }
            return partial;
        }
    }
}
