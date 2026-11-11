Shader "Emberdelve/LocalLightHaze"
{
    Properties { _Tint("Scattering tint", Color) = (.045,.025,.012,1) }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent+10" "RenderType"="Transparent" }
        Pass
        {
            Cull Front ZWrite Off ZTest Always Blend One One
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
            CBUFFER_END
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };
            Varyings vert(float3 positionOS : POSITION)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(positionOS);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }
            half4 frag(Varyings i) : SV_Target
            {
                float3 origin = GetCameraPositionWS();
                float3 ray = normalize(i.positionWS - origin);
                float3 centre = TransformObjectToWorld(float3(0,0,0));
                float radius = length(TransformObjectToWorldDir(float3(1,0,0), false)) * .5;
                float3 offset = origin - centre;
                float b = dot(offset, ray);
                float discriminant = b*b - dot(offset, offset) + radius*radius;
                if (discriminant <= 0) return 0;
                float root = sqrt(discriminant);
                float nearDistance = max(0, -b-root);
                float farDistance = max(0, -b+root);
                float2 uv = i.positionCS.xy / _ScaledScreenParams.xy;
                float depth = SampleSceneDepth(uv);
                #if !UNITY_REVERSED_Z
                    depth = lerp(UNITY_NEAR_CLIP_VALUE, 1, depth);
                #endif
                float3 scene = ComputeWorldSpacePosition(uv, depth, UNITY_MATRIX_I_VP);
                farDistance = min(farDistance, length(scene-origin));
                float stepLength = max(0, farDistance-nearDistance) / 8;
                float density = 0;
                [unroll] for (int step = 0; step < 8; step++)
                {
                    float3 p = (origin + ray * (nearDistance+(step+.5)*stepLength) - centre) / max(radius,.001);
                    float falloff = saturate(1-dot(p,p));
                    density += falloff*falloff*stepLength;
                }
                return half4(_Tint.rgb*density, 0);
            }
            ENDHLSL
        }
    }
}
