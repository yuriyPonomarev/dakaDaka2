Shader "Emberdelve/MoonlitSky"
{
    Properties { _MoonDirection("Moon direction", Vector) = (-.35,.74,-.55,0) }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            float4 _MoonDirection;
            struct v2f { float4 position:SV_POSITION; float3 direction:TEXCOORD0; };
            v2f vert(float4 vertex:POSITION) { v2f o; o.position=UnityObjectToClipPos(vertex);o.direction=vertex.xyz;return o; }

            float hash3(float3 p)
            {
                p=frac(p*.1031); p+=dot(p,p.yzx+33.33);
                return frac((p.x+p.y)*p.z);
            }
            float noise2(float2 p)
            {
                float2 c=floor(p), f=frac(p); f=f*f*(3-2*f);
                return lerp(lerp(hash3(float3(c,1)),hash3(float3(c+float2(1,0),1)),f.x),
                    lerp(hash3(float3(c+float2(0,1),1)),hash3(float3(c+1,1)),f.x),f.y);
            }
            float clouds(float2 p)
            {
                float n=0, a=.56;
                [unroll] for(int octave=0;octave<4;octave++)
                {
                    n+=noise2(p)*a;
                    p=mul(float2x2(1.72,1.09,-1.09,1.72),p)+19.7;
                    a*=.48;
                }
                return n;
            }
            float stars(float3 d,float scale,float seed)
            {
                float3 p=d*scale, cell=floor(p);
                float choice=hash3(cell+seed);
                float3 starPoint=.2+.6*float3(hash3(cell+seed+11),hash3(cell+seed+37),hash3(cell+seed+71));
                float distance=length(frac(p)-starPoint);
                float aa=max(.015,length(fwidth(p))*.55);
                float dotShape=1-smoothstep(.105-aa,.105+aa,distance);
                return dotShape*step(.983,choice)*lerp(.45,1.4,hash3(cell+seed+97));
            }
            float crater(float2 uv,float2 center,float radius)
            {
                float r=length((uv-center)/radius);
                return -.20*(1-smoothstep(.66,1,r))+.09*exp(-pow((r-.94)*10,2));
            }
            float4 frag(v2f i):SV_Target
            {
                float3 d=normalize(i.direction), moonDirection=normalize(_MoonDirection.xyz);
                float up=saturate(d.y);
                float3 col=lerp(float3(.055,.078,.128),float3(.007,.016,.040),pow(up,.55));
                float facing=dot(d,moonDirection);
                float3 right=normalize(cross(float3(0,1,0),moonDirection));
                float3 moonUp=cross(moonDirection,right);
                float2 moonUV=float2(dot(d,right),dot(d,moonUp))/.034;
                float radial=length(moonUV);
                float edge=max(.008,fwidth(radial));
                float disc=(1-smoothstep(1-edge,1+edge,radial))*step(0,facing);
                float halo=pow(saturate(facing),120);
                col+=float3(.13,.18,.27)*halo;
                col+=float3(.85,.94,1.2)*(stars(d,150,3)+stars(d,285,53)*.5)*smoothstep(.03,.22,d.y)*(1-disc);
                if(disc>.001)
                {
                    float mares=clouds(moonUV*3.1+23);
                    float surface=.80+.22*mares;
                    surface+=crater(moonUV,float2(-.32,.21),.25);
                    surface+=crater(moonUV,float2(.43,-.29),.18);
                    surface+=crater(moonUV,float2(-.11,-.59),.15);
                    surface+=crater(moonUV,float2(.30,.45),.12);
                    surface+=crater(moonUV,float2(-.61,-.20),.11);
                    float sphere=sqrt(saturate(1-radial*radial));
                    surface*=.62+.38*sphere;
                    col=lerp(col,float3(1.25,1.38,1.58)*surface,disc);
                }
                // Two separate wind layers: soft low cloud banks and higher thin streaks.
                float2 plane=d.xz/(max(d.y,0)+.24);
                float t=_Time.y;
                float low=clouds(plane*1.7+float2(t*.004,t*.0015));
                float high=clouds(plane*float2(3.8,2.1)+float2(43-t*.002,17+t*.001));
                float cover=smoothstep(.28,.64,low)*.87+smoothstep(.44,.74,high)*.20;
                cover=saturate(cover)*smoothstep(-.04,.12,d.y);
                float moonRim=halo*(.25+.75*smoothstep(.40,.56,low));
                float3 cloudColor=float3(.075,.09,.13)+moonRim*float3(.065,.081,.11);
                col=lerp(col,cloudColor,cover);
                return float4(col,1);
            }
            ENDHLSL
        }
    }
}

