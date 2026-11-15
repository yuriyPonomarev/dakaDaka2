using System;
using System.IO;
using System.Linq;
using Emberdelve.Items;
using Emberdelve.Player;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object=UnityEngine.Object;

namespace Emberdelve.EditorTools
{
    public static class EmberdelveHandTorchReview
    {
        const string Evidence="Logs/acceptance/torch-polish-20261113";
        public static void Build()
        {
            if(!Application.isBatchMode || !Application.dataPath.Replace('\\','/').Contains("DakaDaka_ci/"))
                throw new InvalidOperationException("Use the graphics batch mirror");
            var root=PrefabUtility.LoadPrefabContents(EmberdelveTorchContent.PrefabPath);
            try
            {
                var visual=root.GetComponent<HandheldTorchVisual>();
                visual.source.intensity=HandheldTorchVisual.LightIntensity;
                visual.source.range=HandheldTorchVisual.LightRange;
                visual.source.color=new Color(1f,.66f,.34f);
                for(int i=root.transform.childCount-1;i>=0;i--)
                {
                    var child=root.transform.GetChild(i);
                    if(child==visual.flame || child==visual.source.transform)continue;
                    Object.DestroyImmediate(child.gameObject);
                }
                EmberdelveHandTorchModel.Build(root.transform);
                for(int i=visual.flame.childCount-1;i>=0;i--)Object.DestroyImmediate(visual.flame.GetChild(i).gameObject);
                visual.flame.localPosition=Vector3.up*.55f;
                EmberdelveTorchFactory.BuildHandheldFlame(visual.flame,Vector3.zero);
                PrefabUtility.SaveAsPrefabAsset(root,EmberdelveTorchContent.PrefabPath);
                Debug.Log("[HandTorch] model triangles="+root.GetComponentsInChildren<MeshFilter>().Sum(x=>x.sharedMesh.triangles.Length/3)+
                    " particleSystems="+root.GetComponentsInChildren<ParticleSystem>().Length);
                // Ground items are unlit, using exactly the same wooden/iron/cloth meshes.
                Object.DestroyImmediate(visual.flame.gameObject);Object.DestroyImmediate(visual.source.gameObject);Object.DestroyImmediate(visual);
                var renderers=root.GetComponentsInChildren<Renderer>();var bounds=renderers[0].bounds;
                foreach(var renderer in renderers)bounds.Encapsulate(renderer.bounds);
                foreach(Transform child in root.transform)child.localPosition-=bounds.center-root.transform.position;
                const string worldPath="Assets/Emberdelve/Prefabs/Visual/Equipment/IV_pitch_torch.prefab";
                var world=PrefabUtility.SaveAsPrefabAsset(root,worldPath);
                var catalog=AssetDatabase.LoadAssetAtPath<ItemVisualCatalog>("Assets/Emberdelve/Resources/Emberdelve/ItemVisualCatalog.asset");
                var entry=catalog.entries.FirstOrDefault(x=>x.itemId=="pitch_torch");
                if(entry==null){entry=new ItemVisualCatalog.Entry{itemId="pitch_torch"};catalog.entries=catalog.entries.Concat(new[]{entry}).ToArray();}
                entry.worldPrefab=world;EmberdelveItemVisualBake.BakeIcon(entry);
                EditorUtility.SetDirty(catalog);AssetDatabase.SaveAssets();
            }
            finally{PrefabUtility.UnloadPrefabContents(root);}
            EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Lobby.unity");
            Directory.CreateDirectory(Evidence);
            foreach(float time in new[]{.25f,.65f,1.1f})
            {
                string tag=Mathf.RoundToInt(time*100).ToString();
                EmberdelveViewmodelPreview.Capture(Evidence+"/torch-close-"+tag+".png",1000,1000,42,false,(parent,camera)=>
                {
                    var torch=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(EmberdelveTorchContent.PrefabPath));
                    torch.transform.SetParent(parent,false);
                    camera.transform.position=parent.position+(time<.5f?new Vector3(.8f,.48f,1.4f):time<1?new Vector3(-.8f,.48f,-1.4f):new Vector3(1.5f,.48f,-.2f));
                    camera.transform.LookAt(parent.position+Vector3.up*.36f);
                    Simulate(torch,time);
                });
                EmberdelveViewmodelPreview.Capture(Evidence+"/torch-first-person-"+tag+".png",1280,800,70,false,(parent,camera)=>
                {
                    camera.transform.SetPositionAndRotation(parent.position+Vector3.up*1.66f,Quaternion.identity);
                    var set=WeaponViewModelSet.Load();
                    var arms=(GameObject)PrefabUtility.InstantiatePrefab(set.armsPrefab);arms.transform.SetParent(camera.transform,false);
                    var driver=arms.GetComponent<FirstPersonArmsDriver>();driver.Bind(null,camera,set);
                    driver.SetWeapons(EmberdelveViewmodelPreview.FindWeapon("pitch_torch"),null);
                    EmberdelveViewmodelPreview.PoseArms(arms,driver,camera,null,-1,0);
                    Simulate(arms,time);
                });
            }
            Debug.Log("[HandTorch] PASS authored model, flame, ground model, icon and six preview frames");
        }
        static void Simulate(GameObject root,float time)
        {
            foreach(var visual in root.GetComponentsInChildren<HandheldTorchVisual>())visual.AlignFlame();
            foreach(var renderer in root.GetComponentsInChildren<MeshRenderer>())
                if(renderer.sharedMaterial != null && renderer.sharedMaterial.shader.name=="Emberdelve/HandTorchVolume")
                {var block=new MaterialPropertyBlock();block.SetFloat("_PreviewTime",time);renderer.SetPropertyBlock(block);}
            foreach(var ps in root.GetComponentsInChildren<ParticleSystem>())
            {
                ps.useAutoRandomSeed=false;ps.randomSeed=1823;ps.Simulate(time,false,true,true);
                var samples=new ParticleSystem.Particle[ps.main.maxParticles];int count=ps.GetParticles(samples);
                if(count>0)Debug.Log("[HandTorch] "+ps.name+" color="+samples[count/2].GetCurrentColor(ps)+" material="+ps.GetComponent<ParticleSystemRenderer>().sharedMaterial.GetColor("_BaseColor"));
            }
        }
    }
}
