Shader "ACunicombat/Environment/Orbital Atmosphere"
{
    Properties
    {
        _AtmosphereColor("Atmosphere Color", Color) = (0.62, 0.38, 0.25, 1)
        _AtmosphereStrength("Atmosphere Strength", Range(0, 1)) = 0.12
        _InnerRadiusRatio("Inner Radius Ratio", Range(0.9, 0.9999)) = 0.99404
        _PathScale("Optical Path Scale", Range(0.1, 20)) = 7.5
        _SunDirection("Sun Direction (world)", Vector) = (0.454, 0.515, -0.727, 0)
        _SunColor("Sun Color", Color) = (1, 0.972, 0.863, 1)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent-10"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "OrbitalAtmosphere"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha One
            Cull Back
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
            half4 _AtmosphereColor;
            half _AtmosphereStrength;
            half _InnerRadiusRatio;
            half _PathScale;
            float4 _SunDirection;
            half4 _SunColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                #if UNITY_REVERSED_Z
                    output.positionCS.z = output.positionCS.w * 0.000015;
                #else
                    output.positionCS.z = output.positionCS.w * 0.999985;
                #endif
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.positionWS = position.positionWS;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half3 normalWS = normalize(input.normalWS);
                half3 viewDirection = SafeNormalize(GetWorldSpaceViewDir(input.positionWS));
                half viewCosine = saturate(dot(normalWS, viewDirection));
                half impactRadiusSquared = saturate(1.0h - viewCosine * viewCosine);
                half innerIntersection = sqrt(max(
                    _InnerRadiusRatio * _InnerRadiusRatio - impactRadiusSquared,
                    0.0h));
                half opticalPath = max(viewCosine - innerIntersection, 0.0h);
                half pathDensity = smoothstep(
                    0.015h,
                    0.92h,
                    opticalPath * _PathScale);

                half3 sunDirection = SafeNormalize(_SunDirection.xyz);
                half sunFacing = dot(normalWS, sunDirection);
                half daylight = smoothstep(-0.18h, 0.14h, sunFacing);
                half forwardScatter = pow(
                    saturate(dot(viewDirection, sunDirection) * 0.5h + 0.5h),
                    3.0h);
                half alpha = pathDensity * _AtmosphereStrength * daylight;
                half radiance = 0.72h + forwardScatter * 0.38h;
                return half4(
                    _AtmosphereColor.rgb * _SunColor.rgb * radiance,
                    alpha);
            }
            ENDHLSL
        }
    }
}
