using System;
using System.Collections.Generic;
using System.Linq;
using Emberdelve.Feedback;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace Emberdelve.EditorTools
{
    // Local, physically motivated light pools. No global exposure/ambient change.
    public static class DungeonLayoutLighting
    {
        public const string GroupName = "Layout_Lighting";
        const string CandleName = "Authored_Candlelight";
        const string SconcePath = MeshyMapDressing.Folder + "/WallTorch/WallSconce.prefab";

        public static void Apply(GameObject room)
        {
            var old = room.transform.Find(GroupName);
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var group = new GameObject(GroupName).transform;
            group.SetParent(room.transform, false);
            var furniture = room.transform.Find(DungeonLayoutFurnishing.GroupName);
            if (furniture == null) throw new InvalidOperationException("Furnish before lighting " + room.name);
            foreach (var candle in furniture.GetComponentsInChildren<Transform>().Where(t => t.name == "Top_wooden_candlestick").ToArray())
                LightCandle(candle);

            foreach(Transform shelf in furniture)
                if(shelf.name=="Floor_wooden_bookshelf_worn") LightShelf(shelf);

            // One source on the closed back wall of each side chamber, aimed into it.
            var mounts = new List<Vector3>();
            var chambers = room.GetComponentsInChildren<Transform>().Where(t => t.name == "Side_Chamber").ToArray();
            foreach (var chamber in chambers)
            {
                Vector3 origin = chamber.position + Vector3.up * 2.05f - chamber.right * 1.4f;
                if (!Mount(room, group, origin, new[] { chamber.forward }, 7, mounts))
                    throw new InvalidOperationException("No supported chamber torch: " + room.name + " " + chamber.position);
            }
            // Shelving already occupies the quiet wall bays; anchor secondary sconces to
            // those actual bays, rejecting freestanding shelves without nearby masonry.
            foreach (Transform item in furniture)
            {
                if (item.name != "Floor_wooden_bookshelf_worn") continue;
                var b = BoundsOf(item.Find("Model"));
                Vector3 origin = new Vector3(b.center.x, b.max.y + .45f, b.center.z);
                if (mounts.Any(v => Vector3.Distance(v, origin) < 5.5f)) continue;
                Mount(room, group, origin, new[] { -item.forward, item.right, -item.right, item.forward }, 3.5f, mounts);
                if (mounts.Count >= 6) break;
            }
            if(room.GetComponent<Emberdelve.Level.DungeonModuleDefinition>().Kind==Emberdelve.Level.DungeonModuleKind.Shrine)
                foreach(float side in new[]{-1f,1f})
                    if(!Mount(room,group,room.transform.TransformPoint(new Vector3(side*8,2.25f,8)),new[]{room.transform.right*-side},2,mounts))
                        throw new InvalidOperationException("No chapel aisle torch support "+room.name);
            Validate(room);
            Debug.Log("[LayoutLighting] " + room.name + " candles=" + furniture.GetComponentsInChildren<Transform>().Count(t=>t.name==CandleName) + " sconces=" + mounts.Count);
        }

        static void LightShelf(Transform shelf)
        {
            var old=shelf.Find("Shelf_Candlestick");
            if(old!=null)Object.DestroyImmediate(old.gameObject);
            var b=BoundsOf(shelf.Find("Model"));
            var ray=new Ray(new Vector3(b.center.x,b.max.y+.2f,b.center.z),Vector3.down);
            Vector3 seat=Vector3.zero;bool found=false;float nearest=10;
            // Hit the actual upper shelf board; outer bounds include taller side posts.
            foreach(var filter in shelf.Find("Model").GetComponentsInChildren<MeshFilter>())
            {
                var probe=new GameObject("TemporaryShelfSurfaceProbe");
                try
                {
                    probe.transform.SetPositionAndRotation(filter.transform.position,filter.transform.rotation);
                    probe.transform.localScale=filter.transform.lossyScale;
                    var collider=probe.AddComponent<MeshCollider>();collider.sharedMesh=filter.sharedMesh;
                    if(collider.Raycast(ray,out var hit,b.size.y+.3f) && hit.distance<nearest)
                    {nearest=hit.distance;seat=hit.point;found=true;}
                }
                finally{Object.DestroyImmediate(probe);}
            }
            if(!found)throw new InvalidOperationException("No actual shelf board for candle "+shelf.position);
            var anchor=new GameObject("Shelf_Candlestick").transform;anchor.SetParent(shelf,false);
            var model=Object.Instantiate(EmberdelvePolyHavenImport.LoadModel("wooden_candlestick"),anchor).transform;
            model.name="Model";
            foreach(var c in model.GetComponentsInChildren<Collider>(true))Object.DestroyImmediate(c);
            var bounds=BoundsOf(model);model.localScale*=.24f/bounds.size.y;
            bounds=BoundsOf(model);model.position+=seat-new Vector3(bounds.center.x,bounds.min.y,bounds.center.z);
            // Keep the anchor at the same physical location as the model, with unit scale.
            var pose=model.position;anchor.position=seat;model.position=pose;
            LightCandle(anchor);
            var light=anchor.Find(CandleName).GetComponent<Light>();light.intensity=.65f;light.range=2.8f;
            ConfigureFlicker(light,.065f,3.1f);
        }

        static void LightCandle(Transform candle)
        {
            var old = candle.Find(CandleName);
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var b = BoundsOf(candle.Find("Model"));
            var lightRoot = new GameObject(CandleName).transform;
            lightRoot.SetParent(candle, false);
            lightRoot.position = new Vector3(b.center.x, b.max.y + .035f, b.center.z);
            var light = lightRoot.gameObject.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1, .72f, .42f);
            light.intensity = 1.15f;
            light.range = 3.6f;
            light.shadows = LightShadows.None;
            ConfigureFlicker(light, .07f, 3.2f);
            // Existing appearance setup respects this authored child; a candle does not
            // receive the generic 1.4m torch haze sphere at runtime.
            new GameObject("LocalLightHaze").transform.SetParent(lightRoot, false);
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(SconcePath).transform.Find("Flame");
            var flame = Object.Instantiate(source.gameObject, lightRoot).transform;
            flame.name = "Candle_Flame";
            flame.localPosition = new Vector3(0, -.025f, 0);
            flame.localScale = Vector3.one;
            foreach (var inherited in flame.GetComponents<Light>()) Object.DestroyImmediate(inherited);
            foreach (var inherited in flame.GetComponents<TorchFlicker>()) Object.DestroyImmediate(inherited);
            for (int i=flame.childCount-1;i>=0;i--) Object.DestroyImmediate(flame.GetChild(i).gameObject);
            var ps = flame.GetComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main=ps.main;
            main.startLifetime=new ParticleSystem.MinMaxCurve(.18f,.3f);
            main.startSpeed=new ParticleSystem.MinMaxCurve(.035f,.065f);
            main.startSize=new ParticleSystem.MinMaxCurve(.025f,.04f);
            main.gravityModifier=0; main.maxParticles=12;
            main.scalingMode=ParticleSystemScalingMode.Hierarchy;
            var emission=ps.emission; emission.rateOverTime=28;
            var shape=ps.shape;shape.radius=.003f;shape.angle=3;
            var noise=ps.noise;noise.strength=.008f;noise.frequency=1.2f;
        }

        static bool Mount(GameObject room, Transform group, Vector3 origin, Vector3[] directions, float reach, List<Vector3> mounts)
        {
            var walls = room.GetComponentsInChildren<BoxCollider>(true).Where(c => !c.isTrigger && c.enabled
                && c.bounds.size.y >= 2.8f && !c.transform.IsChildOf(room.transform.Find(DungeonLayoutFurnishing.GroupName))).ToArray();
            foreach (var direction in directions)
            {
                RaycastHit nearest=default; BoxCollider support=null; float distance=reach;
                foreach (var wall in walls)
                    if (wall.Raycast(new Ray(origin,direction),out var hit,reach) && hit.distance>.15f && hit.distance<distance)
                    { nearest=hit;support=wall;distance=hit.distance; }
                if (support==null || Mathf.Abs(nearest.normal.y)>.1f || mounts.Any(v=>Vector3.Distance(v,nearest.point)<4.5f))continue;
                var sconce = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(SconcePath),group).transform;
                sconce.name = "Bay_Torch_"+mounts.Count;
                sconce.SetPositionAndRotation(nearest.point,Quaternion.LookRotation(nearest.normal,Vector3.up));
                // Imported model back sits just outside the real wall face. Fit actual
                // rendered geometry, not the original model's arbitrary pivot.
                var model = sconce.Find(EmberdelveTorchFactory.ModelName);
                var local = LocalBounds(sconce,model);
                sconce.position += sconce.forward * (.012f-local.min.z) - Vector3.up * local.min.y;
                var b=BoundsOf(model);
                if (b.max.y>support.bounds.max.y-.15f || b.min.y<1.95f)
                { Object.DestroyImmediate(sconce.gameObject);continue; }
                var light=sconce.GetComponentInChildren<Light>();
                light.type=LightType.Point;
                light.color=new Color(1,.67f,.36f);
                light.intensity=1.8f;light.range=6f;
                light.spotAngle=145;light.innerSpotAngle=100;
                light.transform.rotation=Quaternion.LookRotation((sconce.forward-Vector3.up*.55f).normalized,Vector3.up);
                // Rotation belongs to the light, not the flame particle cone.
                var particles=light.GetComponent<ParticleSystem>();
                var flameVisual=Object.Instantiate(particles.gameObject,light.transform).transform;
                flameVisual.name="Torch_Flame_Visual";
                flameVisual.localPosition=Vector3.zero;
                flameVisual.rotation=Quaternion.Euler(-90,0,0);
                foreach(var c in flameVisual.GetComponents<Light>())Object.DestroyImmediate(c);
                foreach(var c in flameVisual.GetComponents<TorchFlicker>())Object.DestroyImmediate(c);
                Object.DestroyImmediate(light.GetComponent<ParticleSystemRenderer>());
                Object.DestroyImmediate(particles);
                foreach(Transform child in light.transform.Cast<Transform>().ToArray())
                    if(child!=flameVisual)Object.DestroyImmediate(child.gameObject);
                light.shadows=LightShadows.Soft;
                var urp=light.gameObject.AddComponent<UniversalAdditionalLightData>();
                var urpSettings=new SerializedObject(urp);
                urpSettings.FindProperty("m_AdditionalLightsShadowResolutionTier").intValue=UniversalAdditionalLightData.AdditionalLightsShadowResolutionTierCustom;
                urpSettings.ApplyModifiedPropertiesWithoutUndo();
                // URP's custom tier reads shadowResolution as a pixel count (its own
                // Light inspector uses the same representation), not shadowCustomResolution.
                var lightSettings=new SerializedObject(light);
                lightSettings.FindProperty("m_Shadows.m_Resolution").intValue=128;
                lightSettings.ApplyModifiedPropertiesWithoutUndo();
                urp.usePipelineSettings=false;
                light.shadowCustomResolution=128;light.shadowBias=.025f;light.shadowNormalBias=.12f;light.shadowNearPlane=.08f;
                ConfigureFlicker(light,.10f,4.1f);
                mounts.Add(nearest.point);
                return true;
            }
            return false;
        }

        static void ConfigureFlicker(Light light,float amount,float speed)
        {
            var flicker=light.GetComponent<TorchFlicker>() ?? light.gameObject.AddComponent<TorchFlicker>();
            var serialized=new SerializedObject(flicker);
            serialized.FindProperty("lightSource").objectReferenceValue=light;
            serialized.FindProperty("baseIntensity").floatValue=light.intensity;
            serialized.FindProperty("flickerAmount").floatValue=amount;
            serialized.FindProperty("speed").floatValue=speed;
            serialized.FindProperty("affectRange").boolValue=false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
        static Bounds BoundsOf(Transform t)
        {var rs=t.GetComponentsInChildren<MeshRenderer>();var b=rs[0].bounds;foreach(var r in rs)b.Encapsulate(r.bounds);return b;}
        static Bounds LocalBounds(Transform frame,Transform model)
        {
            var result=new Bounds();bool first=true;
            foreach(var r in model.GetComponentsInChildren<MeshRenderer>())
                for(int i=0;i<8;i++)
                {
                    var v=r.localBounds.center+Vector3.Scale(r.localBounds.extents,new Vector3((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1));
                    v=frame.InverseTransformPoint(r.transform.TransformPoint(v));
                    if(first){result=new Bounds(v,Vector3.zero);first=false;}else result.Encapsulate(v);
                }
            return result;
        }
        public static void Validate(GameObject room)
        {
            var group=room.transform.Find(GroupName);
            if(group==null || group.GetComponentsInChildren<Collider>().Length!=0)throw new InvalidOperationException("Lighting must not add physical obstacles");
            foreach(var light in group.GetComponentsInChildren<Light>())
                if(light.type!=LightType.Point || light.shadows==LightShadows.None || light.range>6.1f
                    || light.GetComponent<UniversalAdditionalLightData>()?.additionalLightsShadowResolutionTier!=UniversalAdditionalLightData.AdditionalLightsShadowResolutionTierCustom || (int)light.shadowResolution!=128)
                    throw new InvalidOperationException("Sconces need bounded, wall-occluded light");
            foreach(var candle in room.GetComponentsInChildren<Transform>().Where(t=>t.name=="Top_wooden_candlestick" || t.name=="Shelf_Candlestick"))
            {
                var light=candle.Find(CandleName)?.GetComponent<Light>();
                var b=BoundsOf(candle.Find("Model"));
                if(light==null || Mathf.Abs(light.transform.position.y-b.max.y-.035f)>.004f)
                    throw new InvalidOperationException("Candle light must sit at its wick");
            }
        }
        public static void ApplySaved()
        {
            if(!Application.dataPath.Replace('\\','/').EndsWith("/DakaDaka_clean_handoff/Assets",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Author only in clean_handoff");
            foreach(var guid in AssetDatabase.FindAssets("t:Prefab",new[]{"Assets/Emberdelve/Prefabs/DungeonModules"}).OrderBy(AssetDatabase.GUIDToAssetPath))
            {
                string path=AssetDatabase.GUIDToAssetPath(guid);var root=PrefabUtility.LoadPrefabContents(path);
                try{Apply(root);PrefabUtility.SaveAsPrefabAsset(root,path);}
                finally{PrefabUtility.UnloadPrefabContents(root);}
            }
        }
    }
}
