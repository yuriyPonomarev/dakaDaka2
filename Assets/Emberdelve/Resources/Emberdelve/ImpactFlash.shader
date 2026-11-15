Shader "Emberdelve/ImpactFlash"
{
    Properties { _BaseColor("Impact color", Color) = (1,.8,.35,1) }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Tags { "LightMode"="SRPDefaultUnlit" }
            ZWrite Off ZTest LEqual Cull Back Blend SrcAlpha One
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
            CBUFFER_END
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
            Varyings vert(float3 positionOS : POSITION, float2 uv : TEXCOORD0)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(positionOS);
                o.uv = uv;
                return o;
            }
            half4 frag(Varyings i) : SV_Target
            {
                float2 p = i.uv * 2 - 1;
                half radial = saturate(1 - length(p));
                half core = radial * radial * radial * radial;
                half rays = pow(saturate(1 - abs(p.x) * 18), 3)
                          + pow(saturate(1 - abs(p.y) * 18), 3);
                half opacity = saturate(core + rays * radial * radial * .55);
                return half4(_BaseColor.rgb, _BaseColor.a * opacity);
            }
            ENDHLSL
        }
    }
}
