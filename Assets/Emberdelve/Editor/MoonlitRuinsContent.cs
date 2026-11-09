using UnityEditor;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Level;
using Emberdelve.Interaction;

namespace Emberdelve.EditorTools
{
    internal static class MoonlitRuinsContent
    {
        public static void BuildAndReview()
        {
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Lobby.unity");
            DungeonModuleContentBuilder.BuildCatalog();
            var catalog=Resources.Load<DungeonModuleCatalog>("Emberdelve/DungeonModuleCatalog");
            foreach(var definition in catalog.Modules)
            {
                var module=Object.Instantiate(definition.gameObject);
                var grid=ScriptableObject.CreateInstance<NavGridData>();
                try
                {
                    grid.width=12;grid.height=12;grid.cellSize=2;grid.origin=new Vector3(-12,0,-12);grid.mask=new string('.',144);
                    Physics.SyncTransforms();
                    Emberdelve.AI.NavGridSurface.Bake(grid,module.transform,LayerMask.GetMask(GameConfig.LevelLayerName));
                    var finder=new Emberdelve.AI.NavGridPathfinder(grid);
                    var path=new System.Collections.Generic.List<Vector3>();
                    foreach(var t in module.GetComponentsInChildren<Transform>())
                    {
                        if(t.name!="Upper_Deck")continue;
                        var target=t.position+Vector3.up*t.localScale.y*.5f;
                        if(!finder.TryFindPath(Vector3.zero,target,path) || path.Count==0 || Mathf.Abs(path[path.Count-1].y-target.y)>.2f)
                            throw new System.InvalidOperationException("Terrace unreachable: "+definition.name+" "+t.parent.name);
                        Debug.Log("[MoonlitRuins] reachable "+definition.name+" "+t.parent.name+" height="+target.y+" nodes="+path.Count);
                    }
                }
                finally {Object.DestroyImmediate(module);Object.DestroyImmediate(grid);}
            }
            EmberdelveViewmodelPreview.Capture("Logs/moonlit-ruins/courtyard.png",1280,720,75,false,(root,camera)=>
            {
                var module=Object.Instantiate(catalog.Modules[6].gameObject,root);
                module.transform.localPosition=Vector3.zero;
                AddEnvironment(module.transform);module.GetComponent<MoonlitRuinsEnvironment>().Apply();
                camera.clearFlags=CameraClearFlags.Skybox;
                camera.transform.position=EmberdelveViewmodelPreview.Origin+new Vector3(-2,1.68f,-4);
                camera.transform.LookAt(EmberdelveViewmodelPreview.Origin+new Vector3(7,5,7));
            });
            AssetDatabase.SaveAssets();
            Debug.Log("[MoonlitRuins] catalog, terrace paths and preview complete");
        }

        internal static void AddEnvironment(Transform root)
        {
            const string path="Assets/Emberdelve/Art/Generated/MoonlitRuinsSky.mat";
            var sky=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(sky==null){sky=new Material(Shader.Find("Emberdelve/MoonlitSky"));AssetDatabase.CreateAsset(sky,path);}
            var environment=root.gameObject.AddComponent<MoonlitRuinsEnvironment>();environment.sky=sky;
            var go=new GameObject("Moonlight");go.transform.SetParent(root,false);
            var light=go.AddComponent<Light>();light.type=LightType.Directional;
            light.color=new Color(.6f,.72f,1);light.intensity=1.15f;
            light.shadows=LightShadows.Soft;light.shadowStrength=.8f;light.shadowBias=.035f;
            environment.moon=light;
        }

        internal static void Dress(Transform root,DungeonModuleKind kind,int variant,Material wood,Material stone)
        {
            // Props occupy the two non-stair quadrants, never doorway axes or spawn lanes.
            for(int i=0;i<4;i++)
            {
                float side=i<2?-1:1;
                Vector3 p=new Vector3(side*(4.6f+(i%2)*1.35f),0,-side*8.8f);
                Barrel(root,"Barrel_"+i,p,wood);
            }
            Prop(root,"SupplyCrates","wooden_crate_01",new Vector3(-9,0,4),1.1f,20);
            Prop(root,"BrokenTable","WoodenTable_01",new Vector3(9,0,-4),1f,-20);
            Prop(root,"OldRoots","root_cluster_01",new Vector3(-10,0,9),1.2f,0);
            Prop(root,"Rubble","rock_moss_set_01#rock02",new Vector3(9,0,-10),.8f,variant*40);
            // Colliding fallen masonry provides cover; small fragments are visual only.
            for(int i=0;i<7;i++)
            {
                var rubble=GameObject.CreatePrimitive(PrimitiveType.Cube);
                rubble.name="Masonry_Fragment_"+i;rubble.transform.SetParent(root,false);
                rubble.transform.localPosition=new Vector3(-9.5f+(i%3)*.7f,.12f,5+(i/3)*.9f);
                rubble.transform.localScale=new Vector3(.55f,.24f,.4f);
                rubble.transform.localRotation=Quaternion.Euler(0,i*37,7);
                rubble.GetComponent<Renderer>().sharedMaterial=stone;
                Object.DestroyImmediate(rubble.GetComponent<Collider>());
            }
        }

        private static GameObject Prop(Transform parent,string name,string asset,Vector3 position,float height,float yaw)
        {
            var source=EmberdelvePolyHavenImport.LoadModel(asset);
            if(source==null)return null;
            var go=Object.Instantiate(source);go.name=name;
            foreach(var c in go.GetComponentsInChildren<Collider>())Object.DestroyImmediate(c);
            var renderers=go.GetComponentsInChildren<Renderer>();
            if(renderers.Length==0){Object.DestroyImmediate(go);return null;}
            Bounds bounds=renderers[0].bounds;foreach(var r in renderers)bounds.Encapsulate(r.bounds);
            go.transform.localScale*=height/Mathf.Max(.01f,bounds.size.y);
            bounds=renderers[0].bounds;foreach(var r in renderers)bounds.Encapsulate(r.bounds);
            go.transform.position-=new Vector3(bounds.center.x,bounds.min.y,bounds.center.z);
            var anchor=new GameObject(name+"Anchor");anchor.transform.SetParent(parent,false);
            anchor.transform.localPosition=position;anchor.transform.localRotation=Quaternion.Euler(0,yaw,0);
            go.transform.SetParent(anchor.transform,false);
            return anchor;
        }

        private static void Barrel(Transform parent,string name,Vector3 position,Material wood)
        {
            var root=new GameObject(name);root.transform.SetParent(parent,false);root.transform.localPosition=position;
            var intact=Prop(root.transform,"Intact","wine_barrel_01",Vector3.zero,1.15f,0);
            if(intact==null)throw new System.InvalidOperationException("Existing wine barrel source missing");
            intact.layer=LayerMask.NameToLayer(GameConfig.LevelLayerName);
            var collider=intact.AddComponent<BoxCollider>();collider.center=new Vector3(0,.575f,0);collider.size=new Vector3(.82f,1.15f,.82f);
            var broken=new GameObject("Broken_Staves");broken.transform.SetParent(root.transform,false);
            for(int i=0;i<9;i++)
            {
                var stave=GameObject.CreatePrimitive(PrimitiveType.Cube);stave.name="Stave_"+i;stave.transform.SetParent(broken.transform,false);
                float angle=i*2.39996f;stave.transform.localPosition=new Vector3(Mathf.Cos(angle)*.48f,.04f+(i%3)*.025f,Mathf.Sin(angle)*.48f);
                stave.transform.localRotation=Quaternion.Euler(0,i*47,0);stave.transform.localScale=new Vector3(.12f,.05f,.65f);
                stave.GetComponent<Renderer>().sharedMaterial=wood;Object.DestroyImmediate(stave.GetComponent<Collider>());
            }
            root.AddComponent<BreakableBarrel>().Configure(intact,broken,24);
        }
    }
}
