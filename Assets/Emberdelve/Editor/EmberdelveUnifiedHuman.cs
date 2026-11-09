using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    // Both views use one weighted body. Quaternius supplies animation clips only.
    public sealed class EmberdelveUnifiedHuman : AssetPostprocessor
    {
        public const string ModelPath = "Assets/Emberdelve/Art/ThirdParty/Innerscene/Human/Human.fbx";
        void OnPreprocessModel()
        {
            if (assetPath != ModelPath) return;
            var importer = (ModelImporter)assetImporter;
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.importAnimation = false;
            importer.isReadable = true;
            importer.optimizeGameObjects = false;
            importer.importCameras = false;
            importer.importLights = false;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            var bones = new List<HumanBone>();
            void Map(string human, string bone) { bones.Add(new HumanBone { humanName=human, boneName=bone, limit=new HumanLimit { useDefaultValues=true } }); }
            Map("Hips", "root"); Map("Spine", "spine05"); Map("Chest", "spine03");
            Map("Neck", "neck01"); Map("Head", "head");
            foreach (string side in new[]{"Left", "Right"})
            {
                string suffix=side=="Left"?".L":".R";
                Map(side+"Shoulder", "clavicle"+suffix);
                Map(side+"UpperArm", "upperarm01"+suffix); Map(side+"LowerArm", "lowerarm01"+suffix); Map(side+"Hand", "wrist"+suffix);
                Map(side+"UpperLeg", "upperleg01"+suffix); Map(side+"LowerLeg", "lowerleg01"+suffix); Map(side+"Foot", "foot"+suffix); Map(side+"Toes", "toe1-1"+suffix);
                string[] fingers={"Thumb","Index","Middle","Ring","Little"};
                string[] joints={"Proximal","Intermediate","Distal"};
                for(int f=0;f<5;f++) for(int j=0;j<3;j++) Map(side+" "+fingers[f]+" "+joints[j], "finger"+(f+1)+"-"+(j+1)+suffix);
            }
            var description=importer.humanDescription;
            description.human=bones.ToArray();
            description.armStretch=0; description.legStretch=0;
            description.upperArmTwist=.5f; description.lowerArmTwist=.5f;
            description.upperLegTwist=.5f; description.lowerLegTwist=.5f;
            importer.humanDescription=description;
        }

        public static void Build()
        {
            AssetDatabase.ImportAsset(ModelPath, ImportAssetOptions.ForceUpdate);
            var avatar=AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<Avatar>().FirstOrDefault();
            if(avatar==null || !avatar.isValid || !avatar.isHuman) throw new Exception("Unified human avatar is invalid");
            Debug.Log("[UnifiedHuman] avatar valid; "+EmberdelveFirstPersonArms.EnsureAll(true));
            EmberdelveGrimClassVisuals.BuildVisuals();
            AssetDatabase.SaveAssets();
            EmberdelveGripReview.Capture();
            EmberdelveViewmodelPreview.Capture("Logs/grip-review/classes.png", 1200, 900, 35, true, (root,camera) =>
            {
                int i=0;
                foreach(string name in new[]{"Warrior","Archer"})
                {
                    var model=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Emberdelve/Resources/Emberdelve/PlayerVisuals/"+name+".prefab"),root);
                    model.transform.position=EmberdelveViewmodelPreview.Origin+new Vector3(i==0?-.55f:.55f,0,0);
                    var animator=model.GetComponentInChildren<Animator>();
                    animator.Rebind(); animator.Play("Idle",0,0); animator.Update(0);
                    i++;
                }
                camera.transform.position=EmberdelveViewmodelPreview.Origin+new Vector3(0,1.25f,4.2f);
                camera.transform.LookAt(EmberdelveViewmodelPreview.Origin+Vector3.up*.95f);
            });
            Debug.Log("[UnifiedHuman] built both classes and first-person arms from "+ModelPath);
        }
    }
}
