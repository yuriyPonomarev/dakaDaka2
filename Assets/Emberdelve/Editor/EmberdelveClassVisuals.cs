using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Emberdelve.Data;
namespace Emberdelve.EditorTools
{
    public static class EmberdelveClassVisuals
    {
        const string Source="Assets/Emberdelve/Art/ThirdParty/KayKit/Adventurers/";
        const string Output="Assets/Emberdelve/Resources/Emberdelve/PlayerVisuals";
        public static void BuildAndPackage()
        {
            if(!AssetDatabase.IsValidFolder(Output)) AssetDatabase.CreateFolder("Assets/Emberdelve/Resources/Emberdelve","PlayerVisuals");
            Build("Knight","knight_texture","Warrior"); Build("Rogue_Hooded","rogue_texture","Archer");
            var importer=(TextureImporter)AssetImporter.GetAtPath("Assets/Emberdelve/Resources/Emberdelve/SkillIcons/Abilities.png");
            importer.mipmapEnabled=false; importer.maxTextureSize=2048; importer.textureCompression=TextureImporterCompression.Uncompressed; importer.SaveAndReimport();
            AssetDatabase.SaveAssets();
            EmberdelveWindowsBuild.BuildNetPlayable();
        }
        private static void Build(string name,string texture,string className)
        {
            string path=Source+name+".fbx";
            var importer=(ModelImporter)AssetImporter.GetAtPath(path);
            importer.animationType=ModelImporterAnimationType.Legacy; importer.importAnimation=true; importer.materialImportMode=ModelImporterMaterialImportMode.None;
            importer.SaveAndReimport();
            var clips=importer.defaultClipAnimations;
            string[] keys={"Idle_Rig","Walking_A_Rig","Running_A_Rig","Blocking_Rig","Death_A_Rig"};
            string[] names={"Idle","Walk","Run","Block","Death"};
            var selected=clips.Where(c=>keys.Any(k=>c.name.EndsWith(k,StringComparison.Ordinal))).ToArray();
            foreach(var clip in selected) { int i=Array.FindIndex(keys,k=>clip.name.EndsWith(k,StringComparison.Ordinal)); clip.name=names[i]; clip.loopTime=i<4; clip.wrapMode=i<4 ? WrapMode.Loop : WrapMode.ClampForever; }
            if(selected.Length!=5) throw new InvalidOperationException("Missing class animation clips: "+name+" / "+string.Join(",",clips.Select(c=>c.name)));
            importer.clipAnimations=selected; importer.SaveAndReimport();
            string materialPath=Output+"/"+className+".mat";
            var material=AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if(material==null) { material=new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(material,materialPath); }
            material.mainTexture=AssetDatabase.LoadAssetAtPath<Texture2D>(Source+texture+".png"); material.color=Color.white;
            material.SetFloat("_Smoothness",.25f); EditorUtility.SetDirty(material);
            var root=new GameObject(className+"Avatar");
            try
            {
                var model=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path),root.transform);
                foreach(var renderer in model.GetComponentsInChildren<Renderer>()) renderer.sharedMaterial=material;
                var animation=model.GetComponent<Animation>();
                if(animation==null) animation=model.AddComponent<Animation>();
                foreach(var clip in AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().Where(c=>!c.name.StartsWith("__"))) animation.AddClip(clip,clip.name);
                animation.clip=animation.GetClip("Idle"); animation.playAutomatically=true;
                animation.Play("Idle"); animation.Sample();
                var renderers=model.GetComponentsInChildren<Renderer>(); var bounds=renderers[0].bounds;
                foreach(var renderer in renderers) bounds.Encapsulate(renderer.bounds);
                float scale=1.8f/bounds.size.y; root.transform.localScale=Vector3.one*scale;
                model.transform.localPosition=new Vector3(0,-bounds.min.y,0);
                var prefab=PrefabUtility.SaveAsPrefabAsset(root,Output+"/"+className+".prefab");
                var data=AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_"+className+".asset");
                data.characterVisual=prefab; EditorUtility.SetDirty(data);
                Debug.Log("[ClassVisual] "+className+" clips="+selected.Length+" sourceHeight="+bounds.size.y);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
