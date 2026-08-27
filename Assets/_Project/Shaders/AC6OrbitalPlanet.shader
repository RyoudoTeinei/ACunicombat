Shader "ACunicombat/Environment/Orbital Planet"
{
    Properties
    {
        [MainTexture] _BaseMap("Surface Color", 2D) = "white" {}
        [MainColor] _BaseColor("Surface Tint", Color) = (1, 1, 1, 1)
        [HideInInspector][NoScaleOffset] _SurfacePosX("Surface +X", 2D) = "white" {}
        [HideInInspector][NoScaleOffset] _SurfaceNegX("Surface -X", 2D) = "white" {}
        [HideInInspector][NoScaleOffset] _SurfacePosY("Surface +Y", 2D) = "white" {}
        [HideInInspector][NoScaleOffset] _SurfaceNegY("Surface -Y", 2D) = "white" {}
        [HideInInspector][NoScaleOffset] _SurfacePosZ("Surface +Z", 2D) = "white" {}
        [HideInInspector][NoScaleOffset] _SurfaceNegZ("Surface -Z", 2D) = "white" {}
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
        [HideInInspector] _UseSurfaceCube("Use Surface Cube", Float) = 0
        [HideInInspector] _UseCloudCube("Use Cloud Cube", Float) = 0
        _DustCloudColor("Dust Cloud Color", Color) = (0.69, 0.43, 0.24, 1)
        _WaterIceCloudColor("Water Ice Cloud Color", Color) = (0.76, 0.82, 0.86, 1)
        _DustCloudStrength("Dust Cloud Strength", Range(0, 1)) = 0.16
        _WaterIceCloudStrength("Water Ice Cloud Strength", Range(0, 1)) = 0.2
        _CloudRotationSpeed("Cloud Rotation Speed", Range(-0.001, 0.001)) = 0.000035
        [HideInInspector] _CloudMapLongitudeRadians("Cloud Map Longitude", Float) = 3.316126
        [HideInInspector] _CloudMapTiltRadians("Cloud Map Seasonal Tilt", Float) = -0.959931
        _CloudShadowStrength("Cloud Shadow Strength", Range(0, 0.5)) = 0.16
        _SunlightContrast("Sunlight Contrast", Range(0.5, 3)) = 1.35
        [Normal] _BumpMap("Orbital Normal", 2D) = "bump" {}
        _BumpScale("Relief Strength", Range(0, 2)) = 1
        _TextureMipBias("Texture Mip Bias", Range(-3, 0)) = -1
        _AlbedoSharpness("Orbital Color Sharpness", Range(0, 1.5)) = 0.55
        _SunDirection("Sun Direction (world)", Vector) = (0.454, 0.515, -0.727, 0)
        _SunColor("Sun Color", Color) = (1, 0.972, 0.863, 1)
        _DaylightStrength("Daylight Strength", Range(0, 2)) = 0.7
        _DaylightFloor("Daylight Detail Floor", Range(0, 1)) = 0.025
        _TerminatorSoftness("Terminator Softness", Range(0.001, 0.2)) = 0.018
        _NightAmbient("Night Ambient", Range(0, 0.1)) = 0.006
        _AtmosphereColor("Atmosphere Color", Color) = (0.62, 0.34, 0.18, 1)
        _AtmosphereStrength("Atmosphere Strength", Range(0, 0.5)) = 0.14
        _AtmospherePower("Atmosphere Falloff", Range(1, 16)) = 6
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry-100"
        }

        Pass
        {
            Name "OrbitalPlanet"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_SurfacePosX); SAMPLER(sampler_SurfacePosX);
            TEXTURE2D(_SurfaceNegX);
            TEXTURE2D(_SurfacePosY);
            TEXTURE2D(_SurfaceNegY);
            TEXTURE2D(_SurfacePosZ);
            TEXTURE2D(_SurfaceNegZ);
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
            float4 _BaseMap_ST;
            float4 _BaseMap_TexelSize;
            half4 _BaseColor;
            half _UseSurfaceCube;
            half _UseCloudCube;
            half4 _DustCloudColor;
            half4 _WaterIceCloudColor;
            half _DustCloudStrength;
            half _WaterIceCloudStrength;
            half _CloudRotationSpeed;
            half _CloudMapLongitudeRadians;
            half _CloudMapTiltRadians;
            half _CloudShadowStrength;
            half _SunlightContrast;
            half _BumpScale;
            half _TextureMipBias;
            half _AlbedoSharpness;
            float4 _SunDirection;
            half4 _SunColor;
            half _DaylightStrength;
            half _DaylightFloor;
            half _TerminatorSoftness;
            half _NightAmbient;
            half4 _AtmosphereColor;
            half _AtmosphereStrength;
            half _AtmospherePower;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                half4 tangentWS : TEXCOORD2;
                float3 positionWS : TEXCOORD3;
                half3 directionOS : TEXCOORD4;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normal = GetVertexNormalInputs(input.normalOS, input.tangentOS);
                output.positionCS = position.positionCS;

                // Scaled-space bodies share the local combat camera but must stay behind every
                // local object. Writing just inside the far plane preserves the silhouette while
                // preventing the visually scaled sphere from occluding targets hundreds of km
                // above the real surface.
                #if UNITY_REVERSED_Z
                    output.positionCS.z = output.positionCS.w * 0.00001;
                #else
                    output.positionCS.z = output.positionCS.w * 0.99999;
                #endif

                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.normalWS = normal.normalWS;
                output.tangentWS = half4(
                    normal.tangentWS,
                    input.tangentOS.w * GetOddNegativeScale());
                output.positionWS = position.positionWS;
                output.directionOS = input.normalOS;
                return output;
            }

            half3 SampleOrbitalAlbedo(float2 uv)
            {
                half3 center = SAMPLE_TEXTURE2D_BIAS(
                    _BaseMap,
                    sampler_BaseMap,
                    uv,
                    _TextureMipBias).rgb;
                float2 texel = _BaseMap_TexelSize.xy;
                half3 neighbours = (
                    SAMPLE_TEXTURE2D_BIAS(_BaseMap, sampler_BaseMap, uv + float2(texel.x, 0), _TextureMipBias).rgb +
                    SAMPLE_TEXTURE2D_BIAS(_BaseMap, sampler_BaseMap, uv - float2(texel.x, 0), _TextureMipBias).rgb +
                    SAMPLE_TEXTURE2D_BIAS(_BaseMap, sampler_BaseMap, uv + float2(0, texel.y), _TextureMipBias).rgb +
                    SAMPLE_TEXTURE2D_BIAS(_BaseMap, sampler_BaseMap, uv - float2(0, texel.y), _TextureMipBias).rgb) * 0.25h;
                return saturate(center + (center - neighbours) * _AlbedoSharpness);
            }

            void ResolveSpaceEngineFace(
                half3 meshDirection,
                out int face,
                out float2 faceUv)
            {
                float3 direction = normalize(float3(
                    meshDirection.x,
                    meshDirection.y,
                    -meshDirection.z));
                float3 absoluteDirection = abs(direction);
                float u;
                float v;

                if (absoluteDirection.x >= absoluteDirection.y &&
                    absoluteDirection.x >= absoluteDirection.z)
                {
                    if (direction.x >= 0.0)
                    {
                        face = 0;
                        u = -direction.z / absoluteDirection.x;
                    }
                    else
                    {
                        face = 1;
                        u = direction.z / absoluteDirection.x;
                    }
                    v = -direction.y / absoluteDirection.x;
                }
                else if (absoluteDirection.y > absoluteDirection.x &&
                    absoluteDirection.y >= absoluteDirection.z)
                {
                    face = direction.y >= 0.0 ? 2 : 3;
                    u = direction.x / absoluteDirection.y;
                    v = direction.y >= 0.0
                        ? direction.z / absoluteDirection.y
                        : -direction.z / absoluteDirection.y;
                }
                else
                {
                    if (direction.z >= 0.0)
                    {
                        face = 4;
                        u = direction.x / absoluteDirection.z;
                    }
                    else
                    {
                        face = 5;
                        u = -direction.x / absoluteDirection.z;
                    }
                    v = -direction.y / absoluteDirection.z;
                }

                faceUv = saturate(float2(u, v) * 0.5 + 0.5);
            }

            half3 SampleSurfaceCube(half3 directionOS)
            {
                int face;
                float2 faceUv;
                ResolveSpaceEngineFace(directionOS, face, faceUv);
                if (face == 0) return SAMPLE_TEXTURE2D_BIAS(_SurfacePosX, sampler_SurfacePosX, faceUv, _TextureMipBias).rgb;
                if (face == 1) return SAMPLE_TEXTURE2D_BIAS(_SurfaceNegX, sampler_SurfacePosX, faceUv, _TextureMipBias).rgb;
                if (face == 2) return SAMPLE_TEXTURE2D_BIAS(_SurfacePosY, sampler_SurfacePosX, faceUv, _TextureMipBias).rgb;
                if (face == 3) return SAMPLE_TEXTURE2D_BIAS(_SurfaceNegY, sampler_SurfacePosX, faceUv, _TextureMipBias).rgb;
                if (face == 4) return SAMPLE_TEXTURE2D_BIAS(_SurfacePosZ, sampler_SurfacePosX, faceUv, _TextureMipBias).rgb;
                return SAMPLE_TEXTURE2D_BIAS(_SurfaceNegZ, sampler_SurfacePosX, faceUv, _TextureMipBias).rgb;
            }

            half SampleDustCloudCube(half3 directionOS)
            {
                int face;
                float2 faceUv;
                ResolveSpaceEngineFace(directionOS, face, faceUv);
                if (face == 0) return SAMPLE_TEXTURE2D(_CloudDustPosX, sampler_CloudDustPosX, faceUv).r;
                if (face == 1) return SAMPLE_TEXTURE2D(_CloudDustNegX, sampler_CloudDustPosX, faceUv).r;
                if (face == 2) return SAMPLE_TEXTURE2D(_CloudDustPosY, sampler_CloudDustPosX, faceUv).r;
                if (face == 3) return SAMPLE_TEXTURE2D(_CloudDustNegY, sampler_CloudDustPosX, faceUv).r;
                if (face == 4) return SAMPLE_TEXTURE2D(_CloudDustPosZ, sampler_CloudDustPosX, faceUv).r;
                return SAMPLE_TEXTURE2D(_CloudDustNegZ, sampler_CloudDustPosX, faceUv).r;
            }

            half SampleWaterIceCloudCube(half3 directionOS)
            {
                int face;
                float2 faceUv;
                ResolveSpaceEngineFace(directionOS, face, faceUv);
                if (face == 0) return SAMPLE_TEXTURE2D(_CloudWaterIcePosX, sampler_CloudWaterIcePosX, faceUv).r;
                if (face == 1) return SAMPLE_TEXTURE2D(_CloudWaterIceNegX, sampler_CloudWaterIcePosX, faceUv).r;
                if (face == 2) return SAMPLE_TEXTURE2D(_CloudWaterIcePosY, sampler_CloudWaterIcePosX, faceUv).r;
                if (face == 3) return SAMPLE_TEXTURE2D(_CloudWaterIceNegY, sampler_CloudWaterIcePosX, faceUv).r;
                if (face == 4) return SAMPLE_TEXTURE2D(_CloudWaterIcePosZ, sampler_CloudWaterIcePosX, faceUv).r;
                return SAMPLE_TEXTURE2D(_CloudWaterIceNegZ, sampler_CloudWaterIcePosX, faceUv).r;
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
                half3 geometricNormal = normalize(input.normalWS);
                half3 tangent = normalize(input.tangentWS.xyz);
                half3 bitangent = normalize(cross(geometricNormal, tangent) * input.tangentWS.w);
                half3 normalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D_BIAS(
                        _BumpMap,
                        sampler_BumpMap,
                        input.uv,
                        _TextureMipBias * 0.5h),
                    _BumpScale);
                half3 normalWS = normalize(
                    tangent * normalTS.x + bitangent * normalTS.y + geometricNormal * normalTS.z);

                half3 sunDirection = SafeNormalize(_SunDirection.xyz);
                half geometricDiffuse = dot(geometricNormal, sunDirection);
                half reliefDiffuse = saturate(dot(normalWS, sunDirection));
                half dayMask = smoothstep(
                    -_TerminatorSoftness,
                    _TerminatorSoftness,
                    geometricDiffuse);
                half shapedDiffuse = pow(reliefDiffuse, _SunlightContrast);
                half daylight = lerp(
                    _DaylightFloor,
                    1.0h,
                    shapedDiffuse) * dayMask * _DaylightStrength;
                half3 albedo = (_UseSurfaceCube > 0.5h
                    ? SampleSurfaceCube(input.directionOS)
                    : SampleOrbitalAlbedo(input.uv)) * _BaseColor.rgb;
                half cloudCoverage = 0.0h;
                if (_UseCloudCube > 0.5h)
                {
                    half3 cloudDirection = RotateCloudDirection(input.directionOS);
                    half dustMask = SampleDustCloudCube(cloudDirection);
                    half waterIceMask = SampleWaterIceCloudCube(cloudDirection);
                    half dust = smoothstep(0.015h, 0.52h, dustMask) *
                        _DustCloudStrength;
                    half waterIce = smoothstep(0.025h, 0.62h, waterIceMask) *
                        _WaterIceCloudStrength;
                    cloudCoverage = saturate(dust + waterIce - dust * waterIce);
                }
                half cloudShadow = 1.0h - cloudCoverage * _CloudShadowStrength;
                half3 illumination = _NightAmbient.xxx +
                    _SunColor.rgb * daylight * cloudShadow;
                half3 surface = albedo * illumination;

                half3 viewDirection = SafeNormalize(GetWorldSpaceViewDir(input.positionWS));
                half viewFacing = saturate(dot(geometricNormal, viewDirection));
                half atmosphere = pow(saturate(1.0h - viewFacing), _AtmospherePower)
                    * _AtmosphereStrength
                    * smoothstep(-0.12h, 0.18h, geometricDiffuse);
                half atmosphereLight = _NightAmbient +
                    saturate(geometricDiffuse) * _DaylightStrength;
                half3 atmosphereColor = _AtmosphereColor.rgb *
                    _SunColor.rgb * atmosphereLight;
                return half4(lerp(surface, atmosphereColor, atmosphere), 1.0h);
            }
            ENDHLSL
        }
    }
}
