Shader "Emberdelve/RoyaleZoneBoundary"
{
    // Local visual of the closing zone: a drifting storm wall with rising wisps, thin bright filaments and a glow
    // where the wall meets floors and walls. Purely cosmetic; RoyaleZoneBoundary owns placement.
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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
            CBUFFER_END
            struct Attributes { float3 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float2 uv : TEXCOORD1; float eyeDepth : TEXCOORD2; };
            Varyings vert(Attributes v)
            {
                Varyings o; o.positionWS = TransformObjectToWorld(v.positionOS);
                o.positionCS = TransformWorldToHClip(o.positionWS); o.uv = v.uv;
                o.eyeDepth = -TransformWorldToView(o.positionWS).z; return o;
            }
            float Hash(float3 p)
            {
                p = frac(p * .3183099 + .1); p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }
            float Noise(float3 x)
            {
                float3 i = floor(x); float3 f = frac(x); f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(lerp(Hash(i), Hash(i + float3(1,0,0)), f.x), lerp(Hash(i + float3(0,1,0)), Hash(i + float3(1,1,0)), f.x), f.y),
                            lerp(lerp(Hash(i + float3(0,0,1)), Hash(i + float3(1,0,1)), f.x), lerp(Hash(i + float3(0,1,1)), Hash(i + float3(1,1,1)), f.x), f.y), f.z);
            }
            float Fbm(float3 p)
            {
                float value = 0.0; float amplitude = .5;
                for (int octave = 0; octave < 4; octave++) { value += amplitude * Noise(p); p = p * 2.03 + 7.1; amplitude *= .5; }
                return value;
            }
            half4 frag(Varyings i) : SV_Target
            {
                float3 p = i.positionWS; float time = _Time.y;
                float slow = Fbm(float3(p.x * .22, p.y * .30 - time * .18, p.z * .22));
                float fast = Fbm(float3(p.x * .75 + time * .05, p.y * 1.25 - time * .65, p.z * .75));
                float wisps = smoothstep(.34, .78, slow * .7 + fast * .5);
                float filament = pow(saturate(1.0 - abs(fast * 2.0 - 1.0) * 2.2), 6.0);
                float vertical = smoothstep(0.0, .10, i.uv.y) * smoothstep(0.0, .40, 1.0 - i.uv.y);

                float2 screenUV = i.positionCS.xy / _ScaledScreenParams.xy;
                float sceneDepth = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                float gap = sceneDepth - i.eyeDepth;
                float contact = saturate(1.0 - gap / 1.4); contact *= contact * step(0.0, gap);
                float soft = saturate(gap / .12);
                float nearFade = smoothstep(.35, 2.0, i.eyeDepth);

                float3 deep = _Tint.rgb * .55;
                float3 storm = float3(.34, .26, .86);
                float3 hot = float3(.55, .85, 1.7);
                float3 color = lerp(deep, storm, saturate(slow * 1.4));
                color += hot * (wisps * wisps * .9 + filament * 1.1 + contact * 1.6);

                float alpha = _Tint.a * vertical * (.38 + .62 * wisps) + filament * .35 * vertical + contact * .55;
                alpha = saturate(alpha) * soft * nearFade;
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
