Shader "Emberdelve/Dungeon World Stone"
{
    Properties
    {
        [MainTexture][NoScaleOffset] _BaseMap("Stone Albedo", 2D) = "white" {}
        [MainColor] _BaseColor("Stone Tint", Color) = (1,1,1,1)
        [Normal][NoScaleOffset] _BumpMap("Stone Normal", 2D) = "bump" {}
        _BumpScale("Normal Strength", Range(0,2)) = 1
        [NoScaleOffset] _MetallicGlossMap("Metallic (R), Smoothness (A)", 2D) = "black" {}
        _Smoothness("Smoothness Multiplier", Range(0,1)) = 0.5
        [NoScaleOffset] _OcclusionMap("Occlusion (G)", 2D) = "white" {}
        _OcclusionStrength("Occlusion Strength", Range(0,1)) = 1
        _WorldTiling("Texture Cycles Per Metre", Float) = 0.333333
        _MacroVariation("Broad Albedo Variation", Range(0,0.25)) = 0.08
        _SculptureDetail("Sculpture Vertex Masks", Range(0,1)) = 0
        [NoScaleOffset] _SculptureMossMap("Sculpture Moss and Coverage", 2D) = "black" {}
        [NoScaleOffset] _SculptureFissureMap("Sculpture Fissures (R) and Edges (G)", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" "UniversalMaterialType"="Lit" }
        LOD 300
        Cull Back
        ZWrite On

        // A single material layout is shared by every pass (SRP Batcher compatible).
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseColor;
            float _BumpScale;
            float _Smoothness;
            float _OcclusionStrength;
            float _WorldTiling;
            float _MacroVariation;
            float _SculptureDetail;
        CBUFFER_END
        TEXTURE2D(_MetallicGlossMap); SAMPLER(sampler_MetallicGlossMap);
        TEXTURE2D(_OcclusionMap); SAMPLER(sampler_OcclusionMap);
        TEXTURE2D(_SculptureMossMap); SAMPLER(sampler_SculptureMossMap);
        TEXTURE2D(_SculptureFissureMap); SAMPLER(sampler_SculptureFissureMap);

        struct StoneAttributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 lightmapUV : TEXCOORD1;
            float2 dynamicLightmapUV : TEXCOORD2;
            half4 sculpture : COLOR;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };
        struct StoneVaryings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float3 normalWS : TEXCOORD1;
            half4 fogAndVertexLight : TEXCOORD2;
            float4 shadowCoord : TEXCOORD3;
            DECLARE_LIGHTMAP_OR_SH(lightmapUV, vertexSH, 4);
            #ifdef DYNAMICLIGHTMAP_ON
                float2 dynamicLightmapUV : TEXCOORD5;
            #endif
            half3 sculpture : TEXCOORD6;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };
        struct StoneProjection
        {
            float2 uvX, uvY, uvZ;
            float3 weight, axisSign;
        };
        StoneProjection StoneProject(float3 positionWS, float3 normalWS)
        {
            StoneProjection p;
            float3 world = GetAbsolutePositionWS(positionWS) * max(_WorldTiling, 0.0001);
            p.axisSign = step(0.0, normalWS) * 2.0 - 1.0;
            // Right-handed bases, including negative faces. U cross V is the signed projection axis.
            p.uvX = float2(-p.axisSign.x * world.z, world.y);
            p.uvY = float2(world.x, -p.axisSign.y * world.z);
            p.uvZ = float2(p.axisSign.z * world.x, world.y);
            p.weight = normalWS * normalWS;
            p.weight *= p.weight;
            p.weight /= max(dot(p.weight, float3(1,1,1)), 0.0001);
            return p;
        }
        half3 StoneNormal(StoneProjection p, float3 geometricNormal)
        {
            float3 nx = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, p.uvX), _BumpScale);
            float3 ny = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, p.uvY), _BumpScale);
            float3 nz = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, p.uvZ), _BumpScale);
            // Blend surface slopes rather than projection-axis normals. A flat normal texture returns
            // the exact geometric normal even on bevels; projecting slopes onto its tangent plane
            // also prevents a blended perturbation from flipping into the back-facing hemisphere.
            float3 slopeX = float3(0, nx.y, -p.axisSign.x * nx.x) / max(nx.z, 0.2);
            float3 slopeY = float3(ny.x, 0, -p.axisSign.y * ny.y) / max(ny.z, 0.2);
            float3 slopeZ = float3(p.axisSign.z * nz.x, nz.y, 0) / max(nz.z, 0.2);
            float3 slope = slopeX * p.weight.x + slopeY * p.weight.y + slopeZ * p.weight.z;
            slope -= geometricNormal * dot(geometricNormal, slope);
            return normalize(geometricNormal + slope);
        }
        void StoneSurface(float3 positionWS, float3 geometricNormal, half3 sculpture, out SurfaceData surface, out half3 normalWS)
        {
            StoneProjection p = StoneProject(positionWS, geometricNormal);
            half3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, p.uvX).rgb * p.weight.x
                         + SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, p.uvY).rgb * p.weight.y
                         + SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, p.uvZ).rgb * p.weight.z;
            half4 metal = SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, p.uvX) * p.weight.x
                        + SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, p.uvY) * p.weight.y
                        + SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, p.uvZ) * p.weight.z;
            half occlusion = SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, p.uvX).g * p.weight.x
                           + SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, p.uvY).g * p.weight.y
                           + SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, p.uvZ).g * p.weight.z;
            float3 world = GetAbsolutePositionWS(positionWS);
            float broad = sin(dot(world, float3(0.17, 0.11, 0.13)))
                        * sin(dot(world, float3(-0.07, 0.19, 0.09)) + 1.37);
            surface = (SurfaceData)0;
            surface.albedo = albedo * _BaseColor.rgb * (1.0 + broad * saturate(_MacroVariation));
            surface.metallic = saturate(metal.r);
            surface.smoothness = saturate(metal.a * _Smoothness);
            surface.occlusion = lerp(1.0h, occlusion, saturate(_OcclusionStrength));
            if (_SculptureDetail > 0.5)
            {
                // Red: baked cavity visibility. Green: localized moss seeds. Blue: exposed fractures.
                half4 vegetation =
                      SAMPLE_TEXTURE2D(_SculptureMossMap, sampler_SculptureMossMap, p.uvX * 3.6).rgba * p.weight.x
                    + SAMPLE_TEXTURE2D(_SculptureMossMap, sampler_SculptureMossMap, p.uvY * 3.6).rgba * p.weight.y
                    + SAMPLE_TEXTURE2D(_SculptureMossMap, sampler_SculptureMossMap, p.uvZ * 3.6).rgba * p.weight.z;
                half2 fissure =
                      SAMPLE_TEXTURE2D(_SculptureFissureMap, sampler_SculptureFissureMap, p.uvX * 1.72).rg * p.weight.x
                    + SAMPLE_TEXTURE2D(_SculptureFissureMap, sampler_SculptureFissureMap, p.uvY * 1.72).rg * p.weight.y
                    + SAMPLE_TEXTURE2D(_SculptureFissureMap, sampler_SculptureFissureMap, p.uvZ * 1.72).rg * p.weight.z;
                half moss = smoothstep(0.61h, 0.72h, sculpture.g * 0.49h + vegetation.a * 0.78h)
                          * smoothstep(0.16h, 0.43h, sculpture.g);
                surface.albedo = lerp(surface.albedo, vegetation.rgb, moss * 0.96h);
                surface.albedo = lerp(surface.albedo, albedo * half3(1.12, 1.09, 1.01), sculpture.b * 0.93h);
                surface.albedo *= lerp(1.0h, 0.13h, fissure.r * (1.0h - moss * 0.47h));
                surface.albedo += fissure.g * (1.0h - fissure.r) * (1.0h - moss) * 0.021h;
                surface.occlusion *= lerp(1.0h, 0.49h, fissure.r);
                surface.albedo *= lerp(0.34h, 1.0h, sculpture.r);
                surface.occlusion *= lerp(0.42h, 1.0h, sculpture.r);
                surface.smoothness *= lerp(1.0h, 0.55h, max(moss, sculpture.b));
            }
            surface.alpha = 1;
            surface.normalTS = half3(0,0,1);
            normalWS = StoneNormal(p, geometricNormal);
        }
        StoneVaryings StoneVertex(StoneAttributes input)
        {
            StoneVaryings output = (StoneVaryings)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
            output.positionCS = pos.positionCS;
            output.positionWS = pos.positionWS;
            output.normalWS = TransformObjectToWorldNormal(input.normalOS);
            output.sculpture = input.sculpture.rgb;
            output.shadowCoord = GetShadowCoord(pos);
            output.fogAndVertexLight = half4(ComputeFogFactor(pos.positionCS.z), VertexLighting(pos.positionWS, output.normalWS));
            OUTPUT_LIGHTMAP_UV(input.lightmapUV, unity_LightmapST, output.lightmapUV);
            OUTPUT_SH(output.normalWS, output.vertexSH);
            #ifdef DYNAMICLIGHTMAP_ON
                output.dynamicLightmapUV = input.dynamicLightmapUV * unity_DynamicLightmapST.xy + unity_DynamicLightmapST.zw;
            #endif
            return output;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex StoneVertex
            #pragma fragment StoneFragment
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ USE_LEGACY_LIGHTMAPS
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            half4 StoneFragment(StoneVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                SurfaceData surface;
                half3 normalWS;
                StoneSurface(input.positionWS, normalize(input.normalWS), input.sculpture, surface, normalWS);
                InputData data = (InputData)0;
                data.positionWS = input.positionWS;
                data.positionCS = input.positionCS;
                data.normalWS = normalWS;
                data.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                    data.shadowCoord = input.shadowCoord;
                #else
                    data.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                #endif
                data.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1), input.fogAndVertexLight.x);
                data.vertexLighting = input.fogAndVertexLight.yzw;
                data.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                #ifdef DYNAMICLIGHTMAP_ON
                    data.bakedGI = SAMPLE_GI(input.lightmapUV, input.dynamicLightmapUV, input.vertexSH, normalWS);
                #else
                    data.bakedGI = SAMPLE_GI(input.lightmapUV, input.vertexSH, normalWS);
                #endif
                data.shadowMask = SAMPLE_SHADOWMASK(input.lightmapUV);
                half4 color = UniversalFragmentPBR(data, surface);
                color.rgb = MixFog(color.rgb, data.fogCoord);
                return half4(color.rgb, 1);
            }
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ColorMask 0
            ZTest LEqual
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            // Opaque, with no vertex displacement: stock URP bias/clamping is exactly applicable.
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ColorMask R
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormalsOnly" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex StoneVertex
            #pragma fragment StoneDepthNormals
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            half4 StoneDepthNormals(StoneVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float3 geometricNormal = normalize(input.normalWS);
                half3 normalWS = StoneNormal(StoneProject(input.positionWS, geometricNormal), geometricNormal);
                #if defined(_GBUFFER_NORMALS_OCT)
                    float2 oct = PackNormalOctQuadEncode(normalWS);
                    return half4(PackFloat2To888(saturate(oct * 0.5 + 0.5)), 0);
                #else
                    return half4(normalWS, 0);
                #endif
            }
            ENDHLSL
        }
        Pass
        {
            Name "Meta"
            Tags { "LightMode"="Meta" }
            Cull Off
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex StoneMetaVertex
            #pragma fragment StoneMetaFragment
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/MetaInput.hlsl"
            StoneVaryings StoneMetaVertex(StoneAttributes input)
            {
                StoneVaryings output = StoneVertex(input);
                output.positionCS = UnityMetaVertexPosition(input.positionOS.xyz, input.lightmapUV, input.dynamicLightmapUV);
                return output;
            }
            half4 StoneMetaFragment(StoneVaryings input) : SV_Target
            {
                SurfaceData surface; half3 unusedNormal;
                StoneSurface(input.positionWS, normalize(input.normalWS), input.sculpture, surface, unusedNormal);
                BRDFData brdf;
                InitializeBRDFData(surface.albedo, surface.metallic, surface.specular, surface.smoothness, surface.alpha, brdf);
                MetaInput meta = (MetaInput)0;
                meta.Albedo = brdf.diffuse + brdf.specular * brdf.roughness * 0.5;
                return MetaFragment(meta);
            }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
