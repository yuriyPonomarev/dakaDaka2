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
            #include "UnityCG.cginc"
            float4 _MoonDirection;
            struct v2f { float4 position:SV_POSITION; float3 direction:TEXCOORD0; };
            v2f vert(float4 vertex:POSITION) { v2f o; o.position=UnityObjectToClipPos(vertex);o.direction=vertex.xyz;return o; }
            float hash(float3 p) { return frac(sin(dot(p,float3(127.1,311.7,74.7)))*43758.5453); }
            float4 frag(v2f i):SV_Target
            {
                float3 d=normalize(i.direction);
                float up=saturate(d.y);
                float3 col=lerp(float3(.065,.095,.16),float3(.006,.015,.042),pow(up,.55));
                float moon=dot(d,normalize(_MoonDirection.xyz));
                float disc=smoothstep(.99935,.99955,moon);
                float mottling=.72+.28*sin(d.x*230)*sin(d.z*170);
                col+=float3(.58,.68,.9)*pow(saturate(moon),100)*.12;
                col+=disc*float3(1.5,1.65,1.9)*mottling;
                float3 cells=floor(d*800);
                float star=step(.9993,hash(cells))*smoothstep(.08,.3,d.y);
                col+=star*float3(.5,.62,.85)*(1-disc);
                float cloud=sin(d.x*11+d.z*7)*sin(d.z*17-d.y*8);
                col+=float3(.012,.018,.028)*smoothstep(.15,.8,cloud)*up;
                return float4(col,1);
            }
            ENDHLSL
        }
    }
}
