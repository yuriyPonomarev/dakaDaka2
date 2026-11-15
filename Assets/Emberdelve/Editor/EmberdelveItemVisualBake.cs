using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Emberdelve.Items;
using Object = UnityEngine.Object;
namespace Emberdelve.EditorTools
{
    // Asset authoring/review only. Run on the test mirror with a graphics device.
    public static class EmberdelveItemVisualBake
    {
        const string CatalogPath = "Assets/Emberdelve/Resources/Emberdelve/ItemVisualCatalog.asset";
        const string Icons = "Assets/Emberdelve/Art/Models/Equipment/Icons";
        const string Evidence = "Logs/acceptance/item-models-20261113";
        public static void Build()
        {
            if (!Application.isBatchMode || !Application.dataPath.Replace('\\','/').Contains("DakaDaka_ci/"))
                throw new InvalidOperationException("Use graphics batch on DakaDaka_ci");
            EmberdelveItemArmorModels.CreateAssets();
            Bake();
            ReviewWorn();
            ReviewHands();
        }
        public static void Bake()
        {
            Directory.CreateDirectory(Icons); Directory.CreateDirectory(Evidence);
            AssetDatabase.Refresh();
            var catalog = AssetDatabase.LoadAssetAtPath<ItemVisualCatalog>(CatalogPath);
            if (catalog == null || catalog.entries.Length < 5) throw new InvalidOperationException("Missing armor model catalog");
            int triangles = 0;
            foreach (var entry in catalog.entries)
            {
                if (entry == null || entry.worldPrefab == null) continue;
                if (entry.worldPrefab.GetComponentsInChildren<Collider>(true).Length > 0)
                    throw new InvalidOperationException("Cosmetic prefab has a collider: " + entry.itemId);
                foreach(var filter in entry.worldPrefab.GetComponentsInChildren<MeshFilter>(true))
                    if(filter.sharedMesh != null) triangles += filter.sharedMesh.triangles.Length / 3;
                BakeIcon(entry);
                Render(entry.worldPrefab, Evidence + "/" + entry.itemId + ".png", 1000, false);
            }
            EditorUtility.SetDirty(catalog);AssetDatabase.SaveAssets();
            File.WriteAllText(Evidence+"/assets.txt", "entries="+catalog.entries.Length+" totalWorldTriangles="+triangles);
            Debug.Log("[ItemModels] PASS assets="+catalog.entries.Length+" triangles="+triangles);
        }
        public static void BakeIcon(ItemVisualCatalog.Entry entry)
        {
                Directory.CreateDirectory(Icons);
                string path = Icons + "/" + entry.itemId + ".png";
                Render(entry.worldPrefab, path, 512, true);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var importer=(TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType=TextureImporterType.Sprite; importer.spriteImportMode=SpriteImportMode.Single;
                importer.alphaIsTransparency=true; importer.mipmapEnabled=false;
                importer.textureCompression=TextureImporterCompression.Compressed; importer.maxTextureSize=512;
                importer.SaveAndReimport();
                entry.icon=AssetDatabase.LoadAssetAtPath<Sprite>(path);
                if(entry.icon==null)throw new InvalidOperationException("Missing icon " + entry.itemId);
        }
        public static void ReviewHands()
        {
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Lobby.unity");
            foreach(bool guard in new[]{false,true})
            EmberdelveViewmodelPreview.Capture(Evidence+"/gloves-first-person-"+(guard?"guard":"rest")+".png",1280,800,70,false,(root,camera)=>
            {
                camera.transform.SetPositionAndRotation(EmberdelveViewmodelPreview.Origin+Vector3.up*1.66f,Quaternion.identity);
                var set=Emberdelve.Player.WeaponViewModelSet.Load();
                var arms=(GameObject)PrefabUtility.InstantiatePrefab(set.armsPrefab);
                arms.transform.SetParent(camera.transform,false);
                var driver=arms.GetComponent<Emberdelve.Player.FirstPersonArmsDriver>();driver.Bind(null,camera,set);
                driver.SetWeapons(EmberdelveViewmodelPreview.FindWeapon("arming_sword"),null);
                arms.GetComponent<Emberdelve.Player.EquippedArmorView>().BindVisualIds(null,null,null,"graspwrap_gloves",null,null);
                EmberdelveViewmodelPreview.PoseArms(arms,driver,camera,null,-1,guard?1:0);
            });
        }
        public static void ReviewWorn()
        {
            Directory.CreateDirectory(Evidence);
            foreach(string character in new[]{"Warrior","Archer"})
            foreach(string pose in new[]{"Idle","Walk","Block"})
            {
                var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Emberdelve/Resources/Emberdelve/PlayerVisuals/"+character+".prefab");
                Render(prefab,Evidence+"/worn-"+character+"-"+pose+".png",1000,false,model=>
                {
                    var view=model.AddComponent<Emberdelve.Player.EquippedArmorView>();
                    view.BindVisualIds(null,"boiled_cuirass",null,"graspwrap_gloves","footpad_boots",character=="Warrior"?"ashen_cloak":"emberthread_mantle");
                    var animator=model.GetComponentInChildren<Animator>();
                    animator.Rebind();animator.Play(pose,0,.25f);animator.Update(0);
                });
            }
        }
        public static void Render(GameObject prefab,string path,int size,bool transparent,Action<GameObject> prepare=null,Vector3? viewOffset=null)
        {
            var preview=new PreviewRenderUtility();
            GameObject model=null;
            Texture2D texture=null;
            try
            {
                model=Object.Instantiate(prefab); preview.AddSingleGO(model);
                prepare?.Invoke(model);
                var renderers=model.GetComponentsInChildren<Renderer>();
                if(renderers.Length==0)throw new InvalidOperationException("No model renderers");
                Bounds bounds=renderers[0].bounds;
                foreach(var renderer in renderers) bounds.Encapsulate(renderer.bounds);
                var camera=preview.camera;
                camera.clearFlags=CameraClearFlags.SolidColor;
                camera.backgroundColor=transparent?Color.clear:new Color(.035f,.029f,.023f,1);
                camera.orthographic=true;
                camera.orthographicSize=Mathf.Max(bounds.size.y,bounds.size.x)*.70f;
                camera.nearClipPlane=.01f;camera.farClipPlane=30;
                camera.transform.position=bounds.center+(viewOffset ?? new Vector3(.65f,.25f,3f));
                camera.transform.LookAt(bounds.center);
                preview.lights[0].intensity=2.8f;
                preview.lights[0].color=new Color(1,.86f,.68f);
                preview.lights[0].transform.rotation=Quaternion.Euler(30,210,0);
                preview.lights[1].intensity=1.5f;
                preview.lights[1].color=new Color(.68f,.78f,1);
                preview.lights[1].transform.rotation=Quaternion.Euler(340,40,0);
                preview.ambientColor=new Color(.22f,.22f,.22f);
                preview.BeginStaticPreview(new Rect(0,0,size,size));
                preview.Render(true);
                if(transparent)
                {
                    var previous=RenderTexture.active;
                    var target=camera.targetTexture;
                    RenderTexture.active=target;
                    // Preview targets may exceed the requested size (DPI scaling). Read the full frame.
                    texture=new Texture2D(target.width,target.height,TextureFormat.RGBA32,false,true);
                    texture.ReadPixels(new Rect(0,0,target.width,target.height),0,0);texture.Apply();
                    // PNG stores display RGB; a linear preview target needs an explicit transfer.
                    if(QualitySettings.activeColorSpace==ColorSpace.Linear && !target.sRGB)
                    {
                        var pixels=texture.GetPixels();
                        for(int i=0;i<pixels.Length;i++) pixels[i]=pixels[i].gamma;
                        texture.SetPixels(pixels);texture.Apply();
                    }
                    Debug.Log("[ItemModels] icon target="+target.width+"x"+target.height+" sRGB="+target.sRGB+" colorSpace="+QualitySettings.activeColorSpace);
                    RenderTexture.active=previous;
                    var opaque=preview.EndStaticPreview();Object.DestroyImmediate(opaque);
                }
                else texture=preview.EndStaticPreview();
                File.WriteAllBytes(path,texture.EncodeToPNG());
            }
            finally
            {
                if(texture!=null)Object.DestroyImmediate(texture);
                preview.Cleanup();
            }
        }
    }
}
