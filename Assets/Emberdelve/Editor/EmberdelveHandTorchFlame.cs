using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Emberdelve.EditorTools
{
    // Item-only flame assets: room sconces and braziers retain their existing appearance.
    public static class EmberdelveHandTorchFlame
    {
        const string Folder = "Assets/Emberdelve/Art/Models/HandTorch/Fire";
        public static Transform Build(Transform parent, Vector3 position)
        {
            Directory.CreateDirectory(Folder); AssetDatabase.Refresh();
            var fire = Material("FlameTongue", true);
            var smokeMaterial = Material("SmokeWisp", false);
            var root = new GameObject("Flame").transform;
            root.SetParent(parent, false); root.localPosition = position;
            BuildVolume(root);
            ParticleSystem.MainModule main;
            ParticleSystem.NoiseModule noise;

            // Sparse separate sparks, rather than another large luminous billboard.
            var sparks=Layer(root,"Embers",fire,9,.65f,1.2f,.3f,.65f,20);
            Shape(sparks,.028f,18f);
            main=sparks.main; main.simulationSpace=ParticleSystemSimulationSpace.World;
            main.startSize3D=true; main.startSizeX=new ParticleSystem.MinMaxCurve(.003f,.007f);
            main.startSizeY=new ParticleSystem.MinMaxCurve(.014f,.028f); main.startSizeZ=1;
            Colours(sparks,new Color(1f,.65f,.18f),new Color(1f,.15f,.015f),1f);
            Size(sparks,1f,.65f,0f);
            noise=sparks.noise; noise.enabled=true; noise.strength=.09f; noise.frequency=1.5f;

            var smoke=Layer(root,"SmokeWisps",smokeMaterial,4,1.1f,1.7f,.12f,.24f,10);
            smoke.transform.localPosition=Vector3.up*.19f;
            Shape(smoke,.026f,12f);
            main=smoke.main; main.simulationSpace=ParticleSystemSimulationSpace.World;
            main.startSize=new ParticleSystem.MinMaxCurve(.08f,.14f);
            main.startRotation=new ParticleSystem.MinMaxCurve(0,Mathf.PI*2);
            Colours(smoke,new Color(.22f,.20f,.18f),new Color(.16f,.16f,.17f),.16f);
            Size(smoke,.4f,1f,2.1f);
            noise=smoke.noise; noise.enabled=true; noise.strength=.055f; noise.frequency=1f; noise.scrollSpeed=.4f;
            smoke.GetComponent<ParticleSystemRenderer>().renderMode=ParticleSystemRenderMode.Billboard;
            return root;
        }
        static void BuildVolume(Transform parent)
        {
            string noisePath=Folder+"/FlameVolumeNoise.asset";
            var texture=AssetDatabase.LoadAssetAtPath<Texture3D>(noisePath);
            if(texture==null)
            {
                const int size=64;
                texture=new Texture3D(size,size,size,TextureFormat.RGBA32,false){name="FlameVolumeNoise",wrapMode=TextureWrapMode.Repeat,filterMode=FilterMode.Bilinear};
                var pixels=new Color[size*size*size];
                float Hash(int x,int y,int z,uint seed)
                {
                    uint h=(uint)(x&7)*374761393u+(uint)(y&7)*668265263u+(uint)(z&7)*2246822519u+seed;
                    h=(h^(h>>13))*1274126177u;h^=h>>16;return (h&65535)/65535f;
                }
                float Field(float x,float y,float z,uint seed)
                {
                    int ix=Mathf.FloorToInt(x),iy=Mathf.FloorToInt(y),iz=Mathf.FloorToInt(z);
                    float u=x-ix,v=y-iy,w=z-iz;u=u*u*(3-2*u);v=v*v*(3-2*v);w=w*w*(3-2*w);
                    float Slice(int k)=>Mathf.Lerp(Mathf.Lerp(Hash(ix,iy,k,seed),Hash(ix+1,iy,k,seed),u),Mathf.Lerp(Hash(ix,iy+1,k,seed),Hash(ix+1,iy+1,k,seed),u),v);
                    return Mathf.Lerp(Slice(iz),Slice(iz+1),w);
                }
                for(int z=0;z<size;z++)for(int y=0;y<size;y++)for(int x=0;x<size;x++)
                {
                    float a=x/8f,b=y/8f,c=z/8f;
                    pixels[x+size*(y+size*z)]=new Color(Field(a,b,c,17),Field(a,b,c,373),Field(a,b,c,1987),1);
                }
                texture.SetPixels(pixels);texture.Apply(false,false);AssetDatabase.CreateAsset(texture,noisePath);
            }
            string materialPath=Folder+"/FlameVolume.mat";
            var material=AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if(material==null){material=new Material(Shader.Find("Emberdelve/HandTorchVolume"));AssetDatabase.CreateAsset(material,materialPath);}
            material.shader=Shader.Find("Emberdelve/HandTorchVolume");
            material.SetTexture("_NoiseTex",texture);material.SetFloat("_PreviewTime",-1);
            material.SetFloat("_Density",5.5f);material.SetFloat("_Radiance",1.4f);
            EditorUtility.SetDirty(material);
            var volume=GameObject.CreatePrimitive(PrimitiveType.Cube); volume.name="FlameVolume";
            Object.DestroyImmediate(volume.GetComponent<Collider>());
            volume.transform.SetParent(parent,false);volume.transform.localPosition=Vector3.up*.082f;
            volume.transform.localScale=new Vector3(.28f,.46f,.28f);
            var renderer=volume.GetComponent<MeshRenderer>();renderer.sharedMaterial=material;
            renderer.shadowCastingMode=ShadowCastingMode.Off;renderer.receiveShadows=false;
            renderer.lightProbeUsage=LightProbeUsage.Off;renderer.reflectionProbeUsage=ReflectionProbeUsage.Off;
        }
        static ParticleSystem Layer(Transform parent,string name,Material material,float rate,float lifeMin,float lifeMax,float speedMin,float speedMax,int max)
        {
            var go=new GameObject(name); go.transform.SetParent(parent,false);
            go.transform.localRotation=Quaternion.Euler(-90,0,0);
            var ps=go.AddComponent<ParticleSystem>(); ps.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);
            var main=ps.main; main.duration=1; main.loop=true; main.playOnAwake=true; main.prewarm=true;
            main.startLifetime=new ParticleSystem.MinMaxCurve(lifeMin,lifeMax);
            main.startSpeed=new ParticleSystem.MinMaxCurve(speedMin,speedMax);
            main.startColor=Color.white; main.maxParticles=max;
            main.simulationSpace=ParticleSystemSimulationSpace.Local; main.scalingMode=ParticleSystemScalingMode.Hierarchy;
            main.cullingMode=ParticleSystemCullingMode.PauseAndCatchup;
            var emission=ps.emission; emission.rateOverTime=rate;
            var renderer=ps.GetComponent<ParticleSystemRenderer>(); renderer.sharedMaterial=material;
            renderer.renderMode=ParticleSystemRenderMode.VerticalBillboard;
            renderer.shadowCastingMode=ShadowCastingMode.Off; renderer.receiveShadows=false;
            renderer.lightProbeUsage=LightProbeUsage.Off; renderer.reflectionProbeUsage=ReflectionProbeUsage.Off;
            renderer.sortMode=ParticleSystemSortMode.Distance;
            return ps;
        }
        static void Shape(ParticleSystem ps,float radius,float angle)
        { var s=ps.shape;s.enabled=true;s.shapeType=ParticleSystemShapeType.Cone;s.radius=radius;s.angle=angle; }
        static void Colours(ParticleSystem ps,Color bottom,Color top,float alpha)
        {
            var g=new Gradient();g.SetKeys(new[]{new GradientColorKey(bottom,0),new GradientColorKey(top,1)},
                new[]{new GradientAlphaKey(0,0),new GradientAlphaKey(alpha,.15f),new GradientAlphaKey(alpha*.7f,.55f),new GradientAlphaKey(0,1)});
            var colour=ps.colorOverLifetime;colour.enabled=true;colour.color=g;
        }
        static void Size(ParticleSystem ps,float begin,float middle,float end)
        { var size=ps.sizeOverLifetime;size.enabled=true;size.size=new ParticleSystem.MinMaxCurve(1,new AnimationCurve(new Keyframe(0,begin),new Keyframe(.3f,middle),new Keyframe(1,end))); }
        static Material Material(string name,bool flame)
        {
            string texturePath=Folder+"/"+name+".png";
            var texture=new Texture2D(128,128,TextureFormat.RGBA32,false);
            for(int y=0;y<128;y++)for(int x=0;x<128;x++)
            {
                float u=(x+.5f)/128f,v=(y+.5f)/128f;
                float alpha;
                if(flame)
                {
                    float center=.5f+.11f*Mathf.Sin(v*8f)*v;
                    float width=.34f*Mathf.Pow(Mathf.Sin(Mathf.PI*v),.8f)*(1f-.62f*v);
                    float edge=Mathf.Clamp01((width-Mathf.Abs(u-center))/.065f);
                    float streak=.65f+.35f*Mathf.PerlinNoise(u*12f+v*4f,v*6f);
                    alpha=edge*streak*Mathf.SmoothStep(0,1,Mathf.Clamp01(v/.12f))*Mathf.Clamp01((1-v)*12);
                }
                else
                {
                    float radius=Vector2.Distance(new Vector2(u,v),new Vector2(.5f,.5f))*2;
                    alpha=Mathf.Pow(Mathf.Clamp01(1-radius),1.6f)*(.5f+.5f*Mathf.PerlinNoise(u*6,v*6));
                }
                texture.SetPixel(x,y,new Color(1,1,1,alpha));
            }
            texture.Apply();File.WriteAllBytes(texturePath,texture.EncodeToPNG());Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(texturePath,ImportAssetOptions.ForceSynchronousImport);
            var importer=(TextureImporter)AssetImporter.GetAtPath(texturePath);importer.alphaIsTransparency=true;
            importer.mipmapEnabled=false;importer.wrapMode=TextureWrapMode.Clamp;importer.filterMode=FilterMode.Bilinear;
            importer.textureCompression=TextureImporterCompression.Uncompressed;importer.SaveAndReimport();
            string path=Folder+"/"+name+".mat";
            var material=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(material==null)
            {
                material=new Material(AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Materials/Torch_Flame.mat"));
                AssetDatabase.CreateAsset(material,path);
            }
            if(flame)material.shader=Shader.Find("Emberdelve/HandTorchFire");
            material.SetTexture("_BaseMap",AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath));
            Color tint=flame?new Color(3.2f,2.2f,1.4f,1):Color.white;
            material.SetColor("_BaseColor",tint);
            if(material.HasProperty("_Color"))material.SetColor("_Color",tint);
            material.SetFloat("_Blend",flame?2:0);material.SetFloat("_SrcBlend",(float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend",(float)(flame?BlendMode.One:BlendMode.OneMinusSrcAlpha));
            material.SetFloat("_SrcBlendAlpha",(float)BlendMode.One);material.SetFloat("_DstBlendAlpha",(float)(flame?BlendMode.One:BlendMode.OneMinusSrcAlpha));
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON"); material.DisableKeyword("_ALPHAMODULATE_ON");
            EditorUtility.SetDirty(material);return material;
        }
    }
}
