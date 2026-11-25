using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Emberdelve.Player;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Adds the arm layer (<see cref="AvatarArms"/>) to the class bodies' animator controllers, in place: an override layer masked to
    /// the arms and fingers with a weapon stance and two guards from the animation library already in the project. Idempotent.
    /// </summary>
    public static class EmberdelveAvatarArmLayer
    {
        private const string Folder = "Assets/Emberdelve/Art/Models/GrimPlayers";
        private const string MaskPath = Folder + "/ArmsOnly.mask";
        private static readonly string[] Controllers = { "Warrior", "Archer" };
        private static readonly string[] States = { AvatarArms.Armed, AvatarArms.Guard, AvatarArms.ShieldGuard };
        private static readonly string[] Clips = { "Sword_Idle", "Sword_Block", "Idle_Shield_Loop" };

        [MenuItem("Emberdelve/Visual/Apply Avatar Arm Layer")]
        public static void MenuApply() => Debug.Log("[Emberdelve][AvatarArms] " + ApplyToExisting());

        public static string ApplyToExisting()
        {
            int done = 0;
            foreach (string name in Controllers)
            {
                AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(Folder + "/" + name + ".controller");
                if (controller == null) continue;
                Ensure(controller);
                done++;
            }
            AssetDatabase.SaveAssets();
            return "arm layer on " + done + " controller(s)";
        }

        public static AvatarMask Mask()
        {
            AvatarMask mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(MaskPath);
            if (mask == null)
            {
                mask = new AvatarMask();
                AssetDatabase.CreateAsset(mask, MaskPath);
            }
            for (int i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++)
            {
                AvatarMaskBodyPart part = (AvatarMaskBodyPart)i;
                bool arms = part == AvatarMaskBodyPart.LeftArm || part == AvatarMaskBodyPart.RightArm
                    || part == AvatarMaskBodyPart.LeftFingers || part == AvatarMaskBodyPart.RightFingers;
                mask.SetHumanoidBodyPartActive(part, arms);
            }
            EditorUtility.SetDirty(mask);
            return mask;
        }

        /// <summary>Rebuilds the layer on one controller (its other layers are not touched).</summary>
        public static void Ensure(AnimatorController controller)
        {
            if (controller == null) return;
            AnimatorControllerLayer[] layers = controller.layers;
            for (int i = layers.Length - 1; i >= 1; i--)
                if (layers[i].name == AvatarArms.Layer) controller.RemoveLayer(i);

            var machine = new AnimatorStateMachine { name = AvatarArms.Layer, hideFlags = HideFlags.HideInHierarchy };
            AssetDatabase.AddObjectToAsset(machine, controller);
            machine.defaultState = machine.AddState(AvatarArms.Free);
            for (int i = 0; i < States.Length; i++)
            {
                AnimationClip clip = EmberdelveEnemyVisualFactory.FindClip(Clips[i]);
                if (clip == null) throw new System.Exception("Missing clip " + Clips[i]);
                machine.AddState(States[i]).motion = clip;
            }
            controller.AddLayer(new AnimatorControllerLayer
            {
                name = AvatarArms.Layer, stateMachine = machine, avatarMask = Mask(),
                blendingMode = AnimatorLayerBlendingMode.Override, defaultWeight = 0f
            });
            EditorUtility.SetDirty(controller);
        }
    }
}
