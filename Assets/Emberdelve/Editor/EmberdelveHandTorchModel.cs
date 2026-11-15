using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Emberdelve.EditorTools
{
    /// <summary>Original saved wood, leather, forged iron and charred cloth. No runtime primitive assembly.</summary>
    public static class EmberdelveHandTorchModel
    {
        public const string Art = "Assets/Emberdelve/Art/Models/HandTorch";
        public const string ModelName = "Authored torch body";

        /// <summary>Builds only the body, retaining the existing handle origin and flame attachment at y=.55m.</summary>
        public static void Build(Transform root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            EmberdelveVisualSetup.EnsureFolder(Art);
            var old = root.Find(ModelName);
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var materials = new[]
            {
                Surface("SplitAshWood", new Color(.30f,.165f,.075f), 0, .19f, 0),
                Surface("OiledGripLeather", new Color(.105f,.054f,.024f), 0, .25f, 1),
                Surface("HammeredIron", new Color(.24f,.225f,.20f), .83f, .35f, 2),
                Surface("CharredPitchCloth", new Color(.064f,.046f,.029f), 0, .12f, 3)
            };
            var mesh = new Shape();
            // A tapered, gently crooked ash shaft. The grip centre remains on the established hand socket.
            mesh.Lathe(0, 18, 15, -.085f, .47f,
                (y,a)=> WoodRadius(y) * (1f+.035f*Mathf.Sin(a*5f+y*24f)+.023f*Mathf.Sin(a*9f-y*13f)),
                y=>new Vector2(.0032f*Mathf.Sin(y*11f),.002f*Mathf.Sin(y*17f)), true, false);
            // Broad overlapping leather spiral, with raised edges instead of painted horizontal lines.
            mesh.Spiral(1, -.048f, .205f, 5.1f, .035f, 153,
                (y,a)=>WoodRadius(y)+.0024f, .0016f);
            mesh.Ring(1, -.055f, .011f, .0228f, .018f);
            mesh.Ring(1, .207f, .013f, .0219f, .017f);
            // Ferrule at the butt and a shallow forged cup at the base of the burning bundle.
            mesh.Ring(2, -.077f, .015f, .0205f, .0168f);
            mesh.Ring(2, .413f, .020f, .036f, .0235f);
            mesh.Ring(2, .431f, .009f, .0415f, .030f);

            // Oil-soaked fabric swells and folds unevenly; the top remains open and charred, not a flat cylinder cap.
            mesh.Lathe(3, 32, 28, .433f, .563f,
                (y,a)=> HeadRadius(y) + .0016f*Mathf.Sin(a*3f+y*73f)
                    + .0012f*Mathf.Sin(a*7f-y*41f) + .0015f*Mathf.Pow(.5f+.5f*Mathf.Cos(a-(y-.433f)*240f),5f),
                y=>new Vector2(.001f*Mathf.Sin(y*27f),0), true, true);
            // A distinct fabric edge catches firelight around the bundle, with a small loose end.
            mesh.Spiral(3, .439f, .553f, 3.7f, .019f, 111,
                (y,a)=>HeadRadius(y)+.0031f, .0010f);
            mesh.ClothTail(3, .8f, .506f, .040f, .046f, .014f);
            mesh.ClothTail(3, 3.9f, .468f, .039f, .031f, .010f);

            mesh.Ring(2, .445f, .007f, .0437f, .0395f);
            mesh.Ring(2, .521f, .007f, .0430f, .0388f);
            for (int i=0;i<4;i++)
            {
                float angle=i*Mathf.PI*.5f+.35f;
                mesh.Strap(2, angle, .429f, .562f, .008f, y=>HeadRadius(y)+.0044f);
                foreach(float y in new[]{.445f,.521f})
                {
                    Vector3 radial=new Vector3(Mathf.Sin(angle),0,Mathf.Cos(angle));
                    mesh.Dome(2, radial*(HeadRadius(y)+.006f)+Vector3.up*y, radial, .0044f, .0018f);
                }
            }
            // Broken dark coals have a restrained emissive crack texture, visible even between flame tongues.
            for(int i=0;i<7;i++)
            {
                float a=i*2.39996f;
                Vector3 centre=new Vector3(Mathf.Sin(a)*.022f,.557f+(i%3)*.002f,Mathf.Cos(a)*.022f);
                mesh.Coal(3,centre,new Vector3(.013f,.008f,.014f),i*.4f);
            }
            Mesh built=mesh.Mesh();
            if (built.triangles.Length/3>10000) throw new InvalidOperationException("Hand torch body exceeds 10k triangles");
            Mesh saved=SaveMesh(built);
            var model=new GameObject(ModelName,typeof(MeshFilter),typeof(MeshRenderer));
            model.transform.SetParent(root,false);
            model.GetComponent<MeshFilter>().sharedMesh=saved;
            var renderer=model.GetComponent<MeshRenderer>();
            renderer.sharedMaterials=materials;
            renderer.shadowCastingMode=ShadowCastingMode.Off;
            renderer.receiveShadows=true;
            Debug.Log("[HandTorchModel] saved body triangles="+saved.triangles.Length/3+" submeshes="+saved.subMeshCount+" bounds="+saved.bounds);
        }

        static float WoodRadius(float y) => Mathf.Lerp(.0195f,.0165f,Mathf.InverseLerp(-.085f,.30f,y))
            + .0065f*Mathf.SmoothStep(0,1,Mathf.InverseLerp(.32f,.47f,y));
        static float HeadRadius(float y) => .034f+.0065f*Mathf.Sin(Mathf.Clamp01((y-.427f)/.145f)*Mathf.PI);

        static Mesh SaveMesh(Mesh mesh)
        {
            mesh.name="PitchTorchBody";
            string path=Art+"/PitchTorchBody.asset";
            var previous=AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if(previous==null){AssetDatabase.CreateAsset(mesh,path);return mesh;}
            EditorUtility.CopySerialized(mesh,previous);EditorUtility.SetDirty(previous);Object.DestroyImmediate(mesh);return previous;
        }
        static Texture2D SaveTexture(Texture2D texture,string name)
        {
            texture.name=name;
            string path=Art+"/"+name+".asset";
            var previous=AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if(previous==null){AssetDatabase.CreateAsset(texture,path);return texture;}
            EditorUtility.CopySerialized(texture,previous);EditorUtility.SetDirty(previous);Object.DestroyImmediate(texture);return previous;
        }
        static Material Surface(string name,Color baseColour,float metallic,float smooth,int kind)
        {
            const int size=256;
            var albedo=new Texture2D(size,size,TextureFormat.RGBA32,true,false){wrapMode=TextureWrapMode.Repeat,filterMode=FilterMode.Trilinear,anisoLevel=4};
            var normals=new Texture2D(size,size,TextureFormat.RGBA32,true,true){wrapMode=TextureWrapMode.Repeat,filterMode=FilterMode.Trilinear,anisoLevel=4};
            var glow=kind==3?new Texture2D(size,size,TextureFormat.RGBA32,true,false){wrapMode=TextureWrapMode.Repeat}:null;
            var colours=new Color[size*size];var bumps=new Color[size*size];var emissions=kind==3?new Color[size*size]:null;
            float Grain(float u,float v)
            {
                float pore=Mathf.PerlinNoise(u*87f+13f,v*87f+7f);
                if(kind==0)
                {
                    float warp=Mathf.PerlinNoise(u*6f,v*3f)*.045f;
                    float streak=Mathf.Sin((u+warp)*150f+Mathf.Sin(v*9f));
                    return .52f+.22f*pore+.18f*streak+.13f*Mathf.PerlinNoise(u*30f,v*3f);
                }
                if(kind==1)return .55f+.30f*pore+.10f*Mathf.PerlinNoise(u*12f,v*19f);
                if(kind==2)return .54f+.25f*Mathf.PerlinNoise(u*24f,v*24f)+.14f*pore;
                float weave=Mathf.Sin(u*256f)*Mathf.Sin(v*256f);
                return .40f+.24f*pore+.18f*Mathf.PerlinNoise(u*18f,v*18f)+.12f*weave;
            }
            for(int y=0;y<size;y++)for(int x=0;x<size;x++)
            {
                float u=x/(float)size,v=y/(float)size,g=Grain(u,v);
                float factor=.55f+g*.82f;
                colours[y*size+x]=new Color(baseColour.r*factor,baseColour.g*factor,baseColour.b*factor,1);
                float dx=(Grain(u+1f/size,v)-g)*.65f,dy=(Grain(u,v+1f/size)-g)*.65f;
                Vector3 n=new Vector3(-dx,-dy,1f).normalized;
                bumps[y*size+x]=new Color(n.x*.5f+.5f,n.y*.5f+.5f,n.z*.5f+.5f,1);
                if(kind==3)
                {
                    float cells=Mathf.PerlinNoise(u*19f+3f,v*24f+6f);
                    float cracks=Mathf.Clamp01(1f-Mathf.Abs(cells-.48f)*65f);
                    float hot=Mathf.Pow(Mathf.PerlinNoise(u*6f+17f,v*6f+11f),3f);
                    emissions[y*size+x]=new Color(cracks*hot,cracks*hot*.095f,cracks*hot*.008f,1);
                }
            }
            albedo.SetPixels(colours);albedo.Apply(true);
            normals.SetPixels(bumps);normals.Apply(true);
            string path=Art+"/"+name+".mat";
            var material=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(material==null){material=new Material(Shader.Find("Universal Render Pipeline/Lit"));AssetDatabase.CreateAsset(material,path);}
            material.SetColor("_BaseColor",Color.white);
            material.SetTexture("_BaseMap",SaveTexture(albedo,name+"Albedo"));
            material.SetTexture("_BumpMap",SaveTexture(normals,name+"Normal"));material.EnableKeyword("_NORMALMAP");
            material.SetFloat("_BumpScale",.65f);material.SetFloat("_Metallic",metallic);material.SetFloat("_Smoothness",smooth);
            material.SetFloat("_Cull",0f);
            if(glow!=null)
            {
                glow.SetPixels(emissions);glow.Apply(true);
                material.EnableKeyword("_EMISSION");material.SetTexture("_EmissionMap",SaveTexture(glow,name+"Embers"));
                material.SetColor("_EmissionColor",new Color(2.1f,1.1f,.4f));
            }
            EditorUtility.SetDirty(material);return material;
        }

        sealed class Shape
        {
            readonly List<Vector3> vertices=new List<Vector3>();
            readonly List<Vector2> uv=new List<Vector2>();
            readonly List<int>[] faces={new List<int>(),new List<int>(),new List<int>(),new List<int>()};
            int Vertex(Vector3 point,Vector2 tex){int index=vertices.Count;vertices.Add(point);uv.Add(tex);return index;}
            void Tri(int mat,int a,int b,int c){faces[mat].Add(a);faces[mat].Add(b);faces[mat].Add(c);}
            void Quad(int mat,int a,int b,int c,int d){Tri(mat,a,b,c);Tri(mat,b,d,c);}
            public void Lathe(int mat,int sides,int rows,float low,float high,Func<float,float,float> radius,Func<float,Vector2> bend,bool capBottom,bool capTop)
            {
                int start=vertices.Count;
                for(int r=0;r<=rows;r++)
                {
                    float y=Mathf.Lerp(low,high,r/(float)rows);var shift=bend(y);
                    for(int c=0;c<=sides;c++)
                    {
                        float a=c*2*Mathf.PI/sides,rad=radius(y,a);
                        Vertex(new Vector3(Mathf.Sin(a)*rad+shift.x,y,Mathf.Cos(a)*rad+shift.y),new Vector2(c/(float)sides*2f,r/(float)rows*2f));
                    }
                }
                for(int r=0;r<rows;r++)for(int c=0;c<sides;c++)
                {int a=start+r*(sides+1)+c,b=a+sides+1;Quad(mat,a,a+1,b,b+1);}
                foreach(bool top in new[]{false,true})
                {
                    if(top?!capTop:!capBottom)continue;
                    float y=top?high:low;var shift=bend(y);
                    int middle=Vertex(new Vector3(shift.x,y,shift.y),Vector2.one*.5f);
                    for(int c=0;c<sides;c++)
                    {
                        int a=start+(top?rows:0)*(sides+1)+c;
                        if(top)Tri(mat,middle,a,a+1);else Tri(mat,middle,a+1,a);
                    }
                }
            }
            public void Ring(int mat,float y,float height,float outer,float inner)
            {
                const int sides=28;int start=vertices.Count;
                var radii=new[]{outer-.0012f,outer,outer,outer-.0012f,inner,inner};
                var levels=new[]{y-height*.5f,y-height*.5f+.001f,y+height*.5f-.001f,y+height*.5f,y+height*.5f,y-height*.5f};
                for(int r=0;r<6;r++)for(int c=0;c<=sides;c++)
                {
                    float a=c*2*Mathf.PI/sides;
                    Vertex(new Vector3(Mathf.Sin(a)*radii[r],levels[r],Mathf.Cos(a)*radii[r]),new Vector2(c/(float)sides*2f,r*.2f));
                }
                for(int r=0;r<6;r++)for(int c=0;c<sides;c++)
                {int a=start+r*(sides+1)+c,b=start+((r+1)%6)*(sides+1)+c;Quad(mat,a,a+1,b,b+1);}
            }
            public void Spiral(int mat,float low,float high,float turns,float width,int segments,Func<float,float,float> radius,float crown)
            {
                int start=vertices.Count;
                for(int i=0;i<=segments;i++)
                {
                    float t=i/(float)segments,a=t*turns*2*Mathf.PI;
                    for(int j=0;j<3;j++)
                    {
                        float y=Mathf.Lerp(low,high,t)+(j-1)*width*.5f;
                        float rad=radius(y,a)+(j==1?crown:0);
                        Vertex(new Vector3(Mathf.Sin(a)*rad,y,Mathf.Cos(a)*rad),new Vector2(t*turns*2f,j*.5f));
                    }
                }
                for(int i=0;i<segments;i++)for(int j=0;j<2;j++)
                {int a=start+i*3+j;Quad(mat,a,a+3,a+1,a+4);}
            }
            public void Strap(int mat,float angle,float low,float high,float width,Func<float,float> radius)
            {
                int start=vertices.Count;const int rows=12;
                for(int i=0;i<=rows;i++)for(int j=0;j<3;j++)
                {
                    float y=Mathf.Lerp(low,high,i/(float)rows),rad=radius(y)+(j==1?.0012f:0);
                    float a=angle+(j-1)*width*.5f/rad;
                    Vertex(new Vector3(Mathf.Sin(a)*rad,y,Mathf.Cos(a)*rad),new Vector2(j*.5f,i*.2f));
                }
                for(int i=0;i<rows;i++)for(int j=0;j<2;j++){int a=start+i*3+j;Quad(mat,a,a+1,a+3,a+4);}
            }
            public void ClothTail(int mat,float angle,float y,float radius,float length,float width)
            {
                int start=vertices.Count;
                for(int i=0;i<7;i++)for(int j=0;j<3;j++)
                {
                    float t=i/6f,rad=radius+.008f*Mathf.Sin(t*Mathf.PI*.6f),a=angle+(j-1)*width*.5f/rad;
                    float bottom=y-length*t+(i==6?.003f*Mathf.Sin(j*4f):0);
                    Vertex(new Vector3(Mathf.Sin(a)*rad,bottom,Mathf.Cos(a)*rad),new Vector2(j*.5f,t));
                }
                for(int i=0;i<6;i++)for(int j=0;j<2;j++){int a=start+i*3+j;Quad(mat,a,a+3,a+1,a+4);}
            }
            public void Dome(int mat,Vector3 centre,Vector3 normal,float radius,float depth)
            {
                Vector3 u=Vector3.Cross(normal,Vector3.up).normalized,v=Vector3.Cross(normal,u);
                int middle=Vertex(centre+normal*depth,Vector2.one*.5f),start=vertices.Count;
                for(int i=0;i<12;i++){float a=i*Mathf.PI/6;Vertex(centre+(u*Mathf.Cos(a)+v*Mathf.Sin(a))*radius,new Vector2(.5f+Mathf.Cos(a)*.5f,.5f+Mathf.Sin(a)*.5f));}
                for(int i=0;i<12;i++)Tri(mat,middle,start+i,start+(i+1)%12);
            }
            public void Coal(int mat,Vector3 centre,Vector3 radii,float twist)
            {
                int start=vertices.Count;const int rows=4,sides=8;
                for(int r=0;r<=rows;r++)for(int c=0;c<=sides;c++)
                {
                    float b=r*Mathf.PI/rows,a=c*2*Mathf.PI/sides+twist;
                    Vector3 p=new Vector3(Mathf.Sin(b)*Mathf.Sin(a),Mathf.Cos(b),Mathf.Sin(b)*Mathf.Cos(a));
                    Vertex(centre+Vector3.Scale(p,radii),new Vector2(c/(float)sides,r/(float)rows));
                }
                for(int r=0;r<rows;r++)for(int c=0;c<sides;c++){int a=start+r*(sides+1)+c,b=a+sides+1;Quad(mat,a,b,a+1,b+1);}
            }
            public Mesh Mesh()
            {
                var result=new Mesh();result.SetVertices(vertices);result.SetUVs(0,uv);result.subMeshCount=4;
                for(int i=0;i<4;i++)result.SetTriangles(faces[i],i);
                result.RecalculateNormals();result.RecalculateTangents();result.RecalculateBounds();return result;
            }
        }
    }
}
