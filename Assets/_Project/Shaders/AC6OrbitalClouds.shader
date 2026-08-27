Shader "ACunicombat/Environment/Orbital Clouds"
{
    Properties
    {
        [HideInInspector][NoScaleOffset] _CloudDustPosX("Dust +X", 2D) = "black" {}
        [HideInInspector][NoScaleOffset] _CloudDustNegX("Dust -X", 2D) = "black" {}
        [HideInInspector][NoScaleOffset] _CloudDustPosY("Dust +Y", 2D) = "black" {}
        [HideInInspector][NoScaleOffset] _CloudDustNegY("Dust -Y", 2D) = "black" {}
        [HideInInspector][NoScaleOffset] _CloudDustPosZ("Dust +Z", 2D) = "black" {}
        [HideInInspector][NoScaleOffset] _CloudDustNegZ("Dust -Z", 2D) = "black" {}
        [HideInInspector][NoScaleOffset] _CloudWaterIcePosX("Water Ice +X", 2D) = "black" {}
        [HideInInspector][NoScaleOffset] _CloudWaterIceNegX("Water Ice -X", 2D) = "black" {}
        [HideInInspector][NoScaleOffset] _CloudWaterIcePosY("Water Ice +Y", 2D) = "black" {}
        [HideInInspector][NoScaleOffset] _CloudWaterIceNegY("Water Ice -Y", 2D) = "black" {}
        [HideInInspector][NoScaleOffset] _CloudWaterIcePosZ("Water Ice +Z", 2D) = "black" {}
        [HideInInspector][NoScaleOffset] _CloudWaterIceNegZ("Water Ice -Z", 2D) = "black" {}
        _DustCloudColor("Dust Cloud Color", Color) = (0.74, 0.53, 0.34, 1)
        _WaterIceCloudColor("Water Ice Cloud Color", Color) = (0.86, 0.90, 0.92, 1)
        _DustCloudStrength("Dust Cloud Strength", Range(0, 1)) = 0.52
        _WaterIceCloudStrength("Water Ice Cloud Strength", Range(0, 1)) = 0.62
        _CloudRotationSpeed("Cloud Rotation Speed", Range(-0.001, 0.001)) = 0.000035
        [HideInInspector] _CloudMapLongitudeRadians("Cloud Map Longitude", Float) = 3.316126
        [HideInInspector] _CloudMapTiltRadians("Cloud Map Seasonal Tilt", Float) = -1.570796
        _SunDirection("Sun Direction (world)", Vector) = (0.454, 0.515, -0.727, 0)
        _SunColor("Sun Color", Color) = (1, 0.972, 0.863, 1)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent-20"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "OrbitalClouds"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            Cull Back
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_CloudDustPosX); SAMPLER(sampler_CloudDustPosX);
            TEXTURE2D(_CloudDustNegX);
            TEXTURE2D(_CloudDustPosY);
            TEXTURE2D(_CloudDustNegY);
            TEXTURE2D(_CloudDustPosZ);
            TEXTURE2D(_CloudDustNegZ);
            TEXTURE2D(_CloudWaterIcePosX); SAMPLER(sampler_CloudWaterIcePosX);
            TEXTURE2D(_CloudWaterIceNegX);
            TEXTURE2D(_CloudWaterIcePosY);
            TEXTURE2D(_CloudWaterIceNegY);
            TEXTURE2D(_CloudWaterIcePosZ);
            TEXTURE2D(_CloudWaterIceNegZ);

            CBUFFER_START(UnityPerMaterial)
            half4 _DustCloudColor;
            half4 _WaterIceCloudColor;
            half _DustCloudStrength;
            half _WaterIceCloudStrength;
            half _CloudRotationSpeed;
            half _CloudMapLongitudeRadians;
            half _CloudMapTiltRadians;
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
                half3 directionOS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                #if UNITY_REVERSED_Z
                    output.positionCS.z = output.positionCS.w * 0.00002;
                #else
                    output.positionCS.z = output.positionCS.w * 0.99998;
                #endif
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.directionOS = input.normalOS;
                output.positionWS = position.positionWS;
                return output;
            }

            void ResolveSpaceEngineFace(half3 meshDirection, out int face, out float2 faceUv)
            {
                float3 direction = normalize(float3(meshDirection.x, meshDirection.y, -meshDirection.z));
                float3 absoluteDirection = abs(direction);
                float u;
                float v;

                if (absoluteDirection.x >= absoluteDirection.y && absoluteDirection.x >= absoluteDirection.z)
                {
                    face = direction.x >= 0.0 ? 0 : 1;
                    u = direction.x >= 0.0
                        ? -direction.z / absoluteDirection.x
                        : direction.z / absoluteDirection.x;
                    v = -direction.y / absoluteDirection.x;
                }
                else if (absoluteDirection.y > absoluteDirection.x && absoluteDirection.y >= absoluteDirection.z)
                {
                    face = direction.y >= 0.0 ? 2 : 3;
                    u = direction.x / absoluteDirection.y;
                    v = direction.y >= 0.0
                        ? direction.z / absoluteDirection.y
                        : -direction.z / absoluteDirection.y;
                }
                else
                {
                    face = direction.z >= 0.0 ? 4 : 5;
                    u = direction.z >= 0.0
                        ? direction.x / absoluteDirection.z
                        : -direction.x / absoluteDirection.z;
                    v = -direction.y / absoluteDirection.z;
                }

                faceUv = saturate(float2(u, v) * 0.5 + 0.5);
            }

            half SampleDust(half3 directionOS)
            {
                int face;
                float2 uv;
                ResolveSpaceEngineFace(directionOS, face, uv);
                if (face == 0) return SAMPLE_TEXTURE2D(_CloudDustPosX, sampler_CloudDustPosX, uv).r;
                if (face == 1) return SAMPLE_TEXTURE2D(_CloudDustNegX, sampler_CloudDustPosX, uv).r;
                if (face == 2) return SAMPLE_TEXTURE2D(_CloudDustPosY, sampler_CloudDustPosX, uv).r;
                if (face == 3) return SAMPLE_TEXTURE2D(_CloudDustNegY, sampler_CloudDustPosX, uv).r;
                if (face == 4) return SAMPLE_TEXTURE2D(_CloudDustPosZ, sampler_CloudDustPosX, uv).r;
                return SAMPLE_TEXTURE2D(_CloudDustNegZ, sampler_CloudDustPosX, uv).r;
            }

            half SampleWaterIce(half3 directionOS)
            {
                int face;
                float2 uv;
                ResolveSpaceEngineFace(directionOS, face, uv);
                if (face == 0) return SAMPLE_TEXTURE2D(_CloudWaterIcePosX, sampler_CloudWaterIcePosX, uv).r;
                if (face == 1) return SAMPLE_TEXTURE2D(_CloudWaterIceNegX, sampler_CloudWaterIcePosX, uv).r;
                if (face == 2) return SAMPLE_TEXTURE2D(_CloudWaterIcePosY, sampler_CloudWaterIcePosX, uv).r;
                if (face == 3) return SAMPLE_TEXTURE2D(_CloudWaterIceNegY, sampler_CloudWaterIcePosX, uv).r;
                if (face == 4) return SAMPLE_TEXTURE2D(_CloudWaterIcePosZ, sampler_CloudWaterIcePosX, uv).r;
                return SAMPLE_TEXTURE2D(_CloudWaterIceNegZ, sampler_CloudWaterIcePosX, uv).r;
            }

            half3 RotateCloudDirection(half3 directionOS)
            {
                float longitudeSine;
                float longitudeCosine;
                sincos(
                    _Time.y * _CloudRotationSpeed + _CloudMapLongitudeRadians,
                    longitudeSine,
                    longitudeCosine);
                half3 rotated = half3(
                    directionOS.x * longitudeCosine - directionOS.z * longitudeSine,
                    directionOS.y,
                    directionOS.x * longitudeSine + directionOS.z * longitudeCosine);
                float tiltSine;
                float tiltCosine;
                sincos(_CloudMapTiltRadians, tiltSine, tiltCosine);
                return half3(
                    rotated.x,
                    rotated.y * tiltCosine - rotated.z * tiltSine,
                    rotated.y * tiltSine + rotated.z * tiltCosine);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half3 cloudDirection = RotateCloudDirection(input.directionOS);
                half dust = smoothstep(0.04h, 0.62h, SampleDust(cloudDirection)) *
                    _DustCloudStrength;
                half waterIce = smoothstep(0.04h, 0.65h, SampleWaterIce(cloudDirection)) *
                    _WaterIceCloudStrength;
                half alpha = saturate(dust + waterIce - dust * waterIce);
                clip(alpha - 0.008h);

                half3 color = lerp(
                    _DustCloudColor.rgb,
                    _WaterIceCloudColor.rgb,
                    waterIce / max(dust + waterIce, 0.001h));
                half3 normalWS = normalize(input.normalWS);
                half3 sunDirection = SafeNormalize(_SunDirection.xyz);
                half diffuse = pow(saturate(dot(normalWS, sunDirection)), 1.2h);
                half dayMask = smoothstep(-0.02h, 0.02h, dot(normalWS, sunDirection));
                half lighting = lerp(0.035h, 1.0h, diffuse) * dayMask + 0.008h;
                half3 viewDirection = SafeNormalize(GetWorldSpaceViewDir(input.positionWS));
                half rim = pow(saturate(1.0h - dot(normalWS, viewDirection)), 4.0h) *
                    dayMask * 0.22h;
                return half4(color * _SunColor.rgb * (lighting + rim), alpha);
            }
            ENDHLSL
        }
    }
}
