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
            Blend One One
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

            float3 Noise(float3 p){ return SAMPLE_TEXTURE3D_LOD(_NoiseTex,sampler_NoiseTex,p,0).rgb; }

            // A teardrop of burning gas over the pitch head: a wide rounded base, a body that sways more the higher it
            // goes and a top that tears into separate tongues. Heat is highest in the lower core.
            float FireDensity(float3 p,float time,out float heat)
            {
                float h=p.y+.5;
                float hn=saturate((h-.10)/.74);
                float3 q=float3(p.x*1.7,h*1.25-time*1.25,p.z*1.7);
                float3 n1=Noise(q*.85+float3(.11,.23,.37));
                float3 n2=Noise(q*2.2+(n1-.5)*.7+float3(.41,-time*.4,.73));
                float2 sway=(n1.gb-.5)*(.04+.30*hn*hn)
                    +float2(sin(time*3.3+h*4.1),cos(time*2.6+h*3.4))*(.03*hn);
                float r=length(p.xz-sway);
                float radius=.34*pow(saturate(1-hn),.7)*(.72+.28*smoothstep(0,.14,hn));
                radius*=.80+.70*(n2.r-.40)*(.25+hn);
                float shape=saturate(1-r/max(radius,1e-3));
                float tongues=smoothstep(.30,.62,n2.g+(1-hn)*.55);
                float density=pow(shape,1.6)*lerp(1,tongues,smoothstep(.25,.85,hn));
                density*=smoothstep(0,.05,hn)*(1-smoothstep(.95,1,h));
                // the cloth head stays readable through the base of the fire
                density*=lerp(.60,1,smoothstep(.04,.22,hn));
                heat=saturate(shape*1.25*(1-hn*.9)+(n2.b-.5)*.25);
                return density;
            }

            float3 FireColour(float heat)
            {
                float3 c=lerp(float3(.45,.035,0),float3(2.0,.48,.03),smoothstep(0,.32,heat));
                c=lerp(c,float3(2.5,1.25,.22),smoothstep(.32,.68,heat));
                return lerp(c,float3(2.7,2.05,.95),smoothstep(.80,1,heat));
            }

            half4 Frag(Varyings input):SV_Target
            {
                float3 cameraOS=TransformWorldToObject(GetCameraPositionWS());
                float3 ray=normalize(input.positionOS-cameraOS);
                float2 interval=BoxInterval(cameraOS,ray);
                float start=max(interval.x,0);
                float lengthInVolume=max(0,interval.y-start);
                if(lengthInVolume<=0)return half4(0,0,0,0);
                const int steps=56;
                float stepLength=lengthInVolume/steps;
                float time=_PreviewTime>=0?_PreviewTime:_Time.y;
                float3 accumulated=0;
                float transmittance=1;
                // per-pixel jitter hides the slicing of the march
                float jitter=frac(sin(dot(input.positionCS.xy,float2(12.9898,78.233)))*43758.5453);
                [loop] for(int i=0;i<steps;i++)
                {
                    float3 p=cameraOS+ray*(start+(i+jitter)*stepLength);
                    float heat;
                    float density=FireDensity(p,time,heat);
                    if(density<=1e-4)continue;
                    float opacity=1-exp(-density*stepLength*_Density*3.0);
                    accumulated+=transmittance*opacity*FireColour(heat)*(_Radiance*.75);
                    // gas glows and only weakly hides its own far side
                    transmittance*=1-opacity*.45;
                    if(transmittance<.02)break;
                }
                // additive: a flame lights what is behind it, it never darkens it
                return half4(MixFogColor(accumulated,half3(0,0,0),input.fog),0);
            }
            ENDHLSL
        }
    }
}
