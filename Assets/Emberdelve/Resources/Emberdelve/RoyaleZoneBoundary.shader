Shader "Emberdelve/RoyaleZoneBoundary"
{
    Properties { _Tint("Zone tint", Color) = (.24,.35,.64,.5) }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Cull Off ZWrite Off ZTest LEqual Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
            CBUFFER_END
            struct Attributes { float3 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float2 uv : TEXCOORD1; };
            Varyings vert(Attributes v)
            {
                Varyings o; o.positionWS = TransformObjectToWorld(v.positionOS);
                o.positionCS = TransformWorldToHClip(o.positionWS); o.uv = v.uv; return o;
            }
            half4 frag(Varyings i) : SV_Target
            {
                float fade = smoothstep(0,.2,i.uv.y) * smoothstep(0,.2,1-i.uv.y);
                float drift = .65 + .35 * sin(i.positionWS.x*.7 + i.positionWS.z*.9 + sin(i.positionWS.y*1.3-_Time.y)*2);
                return half4(_Tint.rgb, _Tint.a * fade * drift);
            }
            ENDHLSL
        }
    }
}
