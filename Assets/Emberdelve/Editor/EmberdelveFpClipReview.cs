using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Emberdelve.Player;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Renders free humanoid melee clips (Kevin Iglesias, Human Melee Animations FREE) on the first-person arms rig, seen from
    /// the rig's own head: shows whether third-person clips read as first-person arm motion before they are wired in.
    /// </summary>
    public static class EmberdelveFpClipReview
    {
        public const string Folder = "Logs/review/fp-clips";
        const string Root = "Assets/Kevin Iglesias/Human Animations/Animations/Male/Combat/";

        [MenuItem("Emberdelve/Debug/Render FP Clip Review (Edit Mode)")]
        public static void Render()
        {
            if (EditorApplication.isPlaying) { Debug.LogError("[FpClips] leave Play mode first"); return; }
            var clips = new (string name, string path)[]
            {
                ("idle1h", Root + "1H/HumanM@CombatIdle1H01.fbx"),
                ("attack1h", Root + "1H/HumanM@Attack1H01_R.fbx"),
                ("shield", Root + "Shield/HumanM@AttackShield01.fbx"),
            };
            var files = new List<string>();
            foreach (var entry in clips)
            {
                AnimationClip clip = LoadClip(entry.path);
                if (clip == null) { Debug.LogError("[FpClips] no clip in " + entry.path); continue; }
                int frames = entry.name == "idle1h" ? 2 : 6;
                for (int i = 0; i < frames; i++)
                {
                    float t = frames == 1 ? 0f : clip.length * i / (frames - 1);
                    string path = Folder + "/" + entry.name + "-" + i + ".png";
                    EmberdelveViewmodelPreview.Capture(path, 640, 400, 70f, true, (root, camera) =>
                    {
                        var set = WeaponViewModelSet.Load();
                        var arms = (GameObject)PrefabUtility.InstantiatePrefab(set.armsPrefab);
                        arms.transform.SetParent(root, false);
                        arms.transform.localPosition = Vector3.zero;
                        var driver = arms.GetComponent<FirstPersonArmsDriver>();
                        Animator animator = driver != null ? driver.Animator : arms.GetComponentInChildren<Animator>();
                        animator.transform.localScale = Vector3.one;
                        clip.SampleAnimation(animator.gameObject, t);
                        Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
                        Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                        Vector3 forward = hips != null ? Vector3.ProjectOnPlane(hips.forward, Vector3.up).normalized : Vector3.forward;
                        if (forward.sqrMagnitude < .5f) forward = animator.transform.forward;
                        camera.nearClipPlane = .03f;
                        camera.transform.position = head.position + forward * .08f + Vector3.up * .06f;
                        camera.transform.rotation = Quaternion.LookRotation(Quaternion.AngleAxis(12f, Vector3.Cross(Vector3.up, forward)) * forward, Vector3.up);
                    });
                    files.Add(path);
                }
            }
            EmberdelveViewmodelPreview.Sheet(Folder + "/sheet.png", files, 4, 640, 400);
            Debug.Log("[FpClips] rendered " + files.Count + " frames to " + Folder);
        }

        static AnimationClip LoadClip(string path)
        {
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
                if (asset is AnimationClip clip && !clip.name.StartsWith("__preview__")) return clip;
            return null;
        }
    }
}
