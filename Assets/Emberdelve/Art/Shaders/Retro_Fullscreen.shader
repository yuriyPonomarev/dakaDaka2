// V2: full-screen "late-90s" pass for the URP FullScreenPassRendererFeature (injected after URP post-processing, before
// the final blit). Three steps, all driven by material properties:
//   1. pixelation   - the frame is sampled on a coarse grid (_PixelSize screen pixels per retro pixel)
//   2. quantisation - each colour channel is rounded to _ColorLevels steps (32 = 5 bit), in gamma space so the steps
//                     are perceptually even even though the project works in linear colour
//   3. ordered dither - a 4x4 Bayer matrix shifts the rounding threshold, which trades banding for the classic
//                     cross-hatch pattern (_DitherStrength 0 = hard banding, 1 = full dither)
// _Strength blends the result with the untouched frame (0 = effect off, used by the comparison toggle).
Shader "Emberdelve/RetroFullscreen"
{
    Properties
    {
        _PixelSize ("Screen pixels per retro pixel", Range(1, 6)) = 2
        _ColorLevels ("Levels per channel (32 = 5 bit)", Range(2, 256)) = 32
        _DitherStrength ("Bayer dither strength", Range(0, 1)) = 1
        _Strength ("Effect strength (0 = off)", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        Pass
        {
            Name "EmberdelveRetro"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _PixelSize;
                float _ColorLevels;
                float _DitherStrength;
                float _Strength;
            CBUFFER_END

            static const float kBayer4[16] =
            {
                 0.0,  8.0,  2.0, 10.0,
                12.0,  4.0, 14.0,  6.0,
                 3.0, 11.0,  1.0,  9.0,
                15.0,  7.0, 13.0,  5.0
            };

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord;
                float4 source = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, uv, 0);

                // 1. pixelation: snap to the centre of a coarse cell
                float pixelSize = max(_PixelSize, 1.0);
                float2 gridSize = max(floor(_ScreenParams.xy / pixelSize), 1.0);
                float2 cell = floor(uv * gridSize);
                float2 snapped = (cell + 0.5) / gridSize;
                float3 color = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, snapped, 0).rgb;

                // 2 + 3. quantise with an ordered dither, in gamma space
                float3 encoded = LinearToSRGB(saturate(color));
                uint2 bayerCell = uint2(cell) & 3u;
                float threshold = (kBayer4[bayerCell.y * 4u + bayerCell.x] + 0.5) / 16.0 - 0.5; // -0.47 .. +0.47
                float steps = max(_ColorLevels, 2.0) - 1.0;
                encoded = floor(encoded * steps + 0.5 + threshold * _DitherStrength) / steps;
                float3 retro = SRGBToLinear(saturate(encoded));

                return float4(lerp(source.rgb, retro, _Strength), source.a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
