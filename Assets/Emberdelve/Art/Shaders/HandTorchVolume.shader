Shader "Emberdelve/HandTorchVolume"
{
    Properties
    {
        _NoiseTex("Flow noise (3D)", 3D) = "white" {}
        _Density("Flame optical density", Range(1,20)) = 10
        _Radiance("Fire radiance", Range(.1,8)) = 2
        _PreviewTime("Preview time (-1 = animated)", Float) = -1
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Tags { "LightMode"="SRPDefaultUnlit" }
            Blend One OneMinusSrcAlpha
            ZWrite Off
            Cull Back
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE3D(_NoiseTex); SAMPLER(sampler_NoiseTex);
            CBUFFER_START(UnityPerMaterial)
                float _Density;
                float _Radiance;
                float _PreviewTime;
            CBUFFER_END

            struct Attributes { float4 positionOS:POSITION; };
            struct Varyings
            {
                float4 positionCS:SV_POSITION;
                float3 positionOS:TEXCOORD0;
                half fog:TEXCOORD1;
            };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS=TransformObjectToHClip(input.positionOS.xyz);
                output.positionOS=input.positionOS.xyz;
                output.fog=ComputeFogFactor(output.positionCS.z);
                return output;
            }

            // The volume is the ordinary unit cube. Sampling in its local coordinates keeps
            // all fire attached to the viewmodel's actual render transform, including its scale.
            float2 BoxInterval(float3 origin,float3 direction)
            {
                float3 safeDirection=direction;
                safeDirection.x=abs(safeDirection.x)<1e-5?1e-5:safeDirection.x;
                safeDirection.y=abs(safeDirection.y)<1e-5?1e-5:safeDirection.y;
                safeDirection.z=abs(safeDirection.z)<1e-5?1e-5:safeDirection.z;
                float3 a=(-.5-origin)/safeDirection;
                float3 b=( .5-origin)/safeDirection;
                float3 nearPlane=min(a,b),farPlane=max(a,b);
                return float2(max(max(nearPlane.x,nearPlane.y),nearPlane.z),
                              min(min(farPlane.x,farPlane.y),farPlane.z));
            }

            float FireDensity(float3 p,float time,out float heat)
            {
                float height=p.y+.5;
                float3 flow=float3(p.x*1.25,height*1.10-time*.63,p.z*1.25);
                // Two advected frequencies and one folded detail sample. The same field changes
                // both the envelope and its interior, avoiding a smooth cone with noise painted on.
                float3 broad=SAMPLE_TEXTURE3D_LOD(_NoiseTex,sampler_NoiseTex,flow*.63+float3(.13,.27,.51),0).rgb;
                float3 fine=SAMPLE_TEXTURE3D_LOD(_NoiseTex,sampler_NoiseTex,flow*1.45+float3(.37,-time*.08,.11),0).rgb;
                float ribbon=SAMPLE_TEXTURE3D_LOD(_NoiseTex,sampler_NoiseTex,
                    flow*2.15+(broad-.5)*.22+float3(.17,0,.39),0).r;

                float2 centre=float2(sin(height*7.3-time*2.1),cos(height*5.8-time*1.7))
                    *(.012+.065*height*height);
                float2 curl=(broad.gb-.5)*(.09+.23*height);
                float radius=lerp(.22,.035,height)+.085*sin(height*3.14159265);
                float radial=length(p.xz-centre-curl);
                float envelope=1-radial/max(radius,.018);
                float folded=(broad.r-.5)*.90+(fine.r-.5)*.65+(ribbon-.5)*.30;
                // Fire is a folded reaction surface, not a filled density cone. A narrow
                // isosurface leaves the inside transparent, and smaller broken folds cross it.
                // In particular, an on-axis sample is NOT automatically dense or white-hot.
                float field=envelope+folded;
                float sheet=1-smoothstep(.06,.22,abs(field-.36));
                float inside=smoothstep(-.08,.16,envelope);
                float vein=1-smoothstep(.018,.080,abs(fine.g+(broad.b-.5)*.35-.52));
                vein*=smoothstep(.35,.65,broad.r)*smoothstep(.1,.35,envelope);
                float gaps=smoothstep(.18,.38,fine.b+.16*(broad.g-.5));
                float reaction=sheet*.80+vein*.28;
                float body=saturate(field-.10)*.09;
                float density=(reaction+body)*inside*gaps;
                density*=smoothstep(.065,.16,height)*(1-smoothstep(.87,.99,height));

                // The head occupies height .07Р Р†Р вЂљРІР‚Сљ.35 in the shorter review volume. Leave its
                // central fabric visible, with a reactive sheath around its outside.
                float sheath=smoothstep(.13,.17,length(p.xz));
                density*=lerp(sheath,1,smoothstep(.27,.40,height));
                density*=lerp(.22,1,smoothstep(.30,.43,height));
                heat=saturate(((.10+.72*sheet+.24*vein+.14*(ribbon-.5))*reaction+.22*body)/(reaction+body+1e-4));
                return density;
            }

            half4 Frag(Varyings input):SV_Target
            {
                float3 cameraOS=TransformWorldToObject(GetCameraPositionWS());
                float3 ray=normalize(input.positionOS-cameraOS);
                float2 interval=BoxInterval(cameraOS,ray);
                float start=max(interval.x,0);
                float lengthInVolume=max(0,interval.y-start);
                if(lengthInVolume<=0)return half4(0,0,0,0);
                const int steps=96;
                float stepLength=lengthInVolume/steps;
                float time=_PreviewTime>=0?_PreviewTime:_Time.y;
                float3 accumulated=0;
                float transmittance=1;
                float jitter=.5;
                [loop] for(int i=0;i<steps;i++)
                {
                    float3 p=cameraOS+ray*(start+(i+jitter)*stepLength);
                    float heat;
                    float density=FireDensity(p,time,heat);
                    float opacity=1-exp(-density*stepLength*_Density);
                    // A pale hot interior, gold mid-temperature folds and thin orange/red edges.
                    // HDR is intentional: scene bloom can soften the brightest small filaments.
                    float3 cool=float3(1.8,.105,.008);
                    float3 gold=float3(2.8,1.05,.055);
                    float3 hot=float3(3.0,2.5,1.25);
                    float3 emission=lerp(cool,gold,smoothstep(.02,.40,heat));
                    emission=lerp(emission,hot,smoothstep(.62,.94,heat));
                    accumulated+=transmittance*opacity*emission*_Radiance;
                    transmittance*=1-opacity;
                    if(transmittance<.012)break;
                }
                return half4(MixFogColor(accumulated,half3(0,0,0),input.fog),1-transmittance);
            }
            ENDHLSL
        }
    }
}
