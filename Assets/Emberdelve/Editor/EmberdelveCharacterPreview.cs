using System.Linq;
using UnityEditor;
using UnityEngine;
namespace Emberdelve.EditorTools
{
    public static class EmberdelveCharacterPreview
    {
        public static void BuildAndPackage()
        {
            const string folder="Assets/Emberdelve/Resources/Emberdelve/UI";
            if(!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets/Emberdelve/Resources/Emberdelve","UI");
            var source=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Emberdelve/Art/ThirdParty/Quaternius/UAL2/Mannequin_F.fbx");
            var model=Object.Instantiate(source); model.name="CharacterPreview";
            try
            {
                var idle=AssetDatabase.LoadAllAssetsAtPath("Assets/Emberdelve/Art/ThirdParty/Quaternius/UAL2/UAL2_Standard.fbx").OfType<AnimationClip>().FirstOrDefault(c=>c.name == "Armature|Idle_FoldArms_Loop");
                if(idle!=null) idle.SampleAnimation(model,.2f);
                foreach(var animator in model.GetComponentsInChildren<Animator>()) Object.DestroyImmediate(animator);
                PrefabUtility.SaveAsPrefabAsset(model,folder+"/CharacterPreview.prefab");
                AssetDatabase.SaveAssets();
                Debug.Log("[CharacterPreview] generated; pose="+(idle!=null ? idle.name : "bind"));
            }
            finally { Object.DestroyImmediate(model); }
            EmberdelveWindowsBuild.BuildNetPlayable();
        }
    }
}
