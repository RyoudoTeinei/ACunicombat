Shader "ACunicombat/AC6/Parts Approx"
{
    Properties
    {
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        [MainColor] _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        [Normal] _BumpMap("Normal Map", 2D) = "bump" {}
        _BumpScale("Normal Scale", Range(0, 2)) = 0.75
        _Mask3Map("AC6 3M Mask", 2D) = "white" {}
        _EmissionMap("Emission Map", 2D) = "black" {}
        _EmissionColor("Emission Color", Color) = (0, 0, 0, 1)
        _EmissionStrength("Emission Strength", Range(0, 8)) = 0
        _PaintAlbedoMap("AC Paint Albedo", 2D) = "gray" {}
        _PaintColor("Paint Tint", Color) = (0.62, 0.66, 0.68, 1)
        _PaintInfluence("Paint Influence", Range(0, 1)) = 0.18
        _ZoneMode("Use AC6 Zone Material", Range(0, 1)) = 0
        _ZoneMaskStrength("AC6 Zone Mask Strength", Range(0, 1)) = 1
        _Zone1Color("AC6 Zone 1", Color) = (0.42, 0.375, 0.32, 1)
        _Zone2Color("AC6 Zone 2", Color) = (0.389, 0.389, 0.389, 1)
        _Zone3Color("AC6 Zone 3", Color) = (0.42, 0.375, 0.32, 1)
        _Zone4Color("AC6 Zone 4", Color) = (0.091, 0.091, 0.091, 1)
        _Zone5Color("AC6 Zone 5", Color) = (0.263, 0.263, 0.263, 1)
        _Zone6Color("AC6 Zone 6", Color) = (0.214, 0.214, 0.214, 1)
        _Zone7Color("AC6 Zone 7 / Dust", Color) = (0, 0, 0, 1)
        _AlbedoDetailStrength("Albedo Detail Strength", Range(0, 1)) = 0.22
        _Metallic("Base Metallic", Range(0, 1)) = 0.38
        _Smoothness("Base Smoothness", Range(0, 1)) = 0.38
        _OcclusionStrength("Mask Occlusion", Range(0, 1)) = 0.45
        _MaskMetallicInfluence("Mask Metallic Influence", Range(0, 1)) = 0.35
        _MaskSmoothnessInfluence("Mask Smoothness Influence", Range(0, 1)) = 0.45
        _Cutoff("Alpha Cutoff", Range(0, 1)) = 0.5
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend One Zero
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _EMISSION
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_Mask3Map);
            SAMPLER(sampler_Mask3Map);
            TEXTURE2D(_PaintAlbedoMap);
            SAMPLER(sampler_PaintAlbedoMap);

            CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half4 _EmissionColor;
            half4 _PaintColor;
            half4 _Zone1Color;
            half4 _Zone2Color;
            half4 _Zone3Color;
            half4 _Zone4Color;
            half4 _Zone5Color;
            half4 _Zone6Color;
            half4 _Zone7Color;
            half _BumpScale;
            half _EmissionStrength;
            half _PaintInfluence;
            half _ZoneMode;
            half _ZoneMaskStrength;
            half _AlbedoDetailStrength;
            half _Metallic;
            half _Smoothness;
            half _OcclusionStrength;
            half _MaskMetallicInfluence;
            half _MaskSmoothnessInfluence;
            half _Cutoff;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 texcoord : TEXCOORD0;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                half4 tangentWS : TEXCOORD3;
                half4 color : COLOR;
                half fogFactor : TEXCOORD4;
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
                output.positionWS = vertexInput.positionWS;
                output.normalWS = normalInput.normalWS;
                output.tangentWS = half4(normalInput.tangentWS.xyz, input.tangentOS.w * GetOddNegativeScale());
                output.color = input.color;
                output.fogFactor = ComputeFogFactor(vertexInput.positionCS.z);
                output.positionCS = vertexInput.positionCS;
                return output;
            }

            half3 BuildStableArmorAlbedo(half3 sourceAlbedo, half3 mask, float2 uv)
            {
                half sourceLuma = dot(sourceAlbedo, half3(0.299h, 0.587h, 0.114h));
                half3 paintSample = SAMPLE_TEXTURE2D(_PaintAlbedoMap, sampler_PaintAlbedoMap, uv * 2.0).rgb;

                // AC6 part albedo is not a final Unity-ready base color. Keep it as restrained panel detail.
                half panelDetail = lerp(0.92h, 1.08h, saturate(sourceLuma));
                half paintDetail = lerp(0.96h, 1.04h, dot(paintSample, half3(0.299h, 0.587h, 0.114h)));
                half maskDetail = lerp(0.96h, 1.04h, saturate(mask.b));
                half detail = lerp(1.0h, panelDetail * paintDetail * maskDetail, _AlbedoDetailStrength);

                half3 stablePaint = lerp(_BaseColor.rgb, _PaintColor.rgb, _PaintInfluence);
                return stablePaint * detail;
            }

            half3 BuildAc6Mask0(half maskRed)
            {
                // Blender CM graph: 3m.R -> CONSTANT ColorRamp -> Mask_0 RGB.
                half3 mask0 = half3(0, 0, 0);
                if (maskRed >= 0.332h)
                {
                    mask0.b = 1.0h;
                }
                else if (maskRed >= 0.182h)
                {
                    mask0.g = 0.5h;
                }
                else if (maskRed >= 0.068h)
                {
                    mask0.r = 1.0h;
                }

                return saturate(mask0 * _ZoneMaskStrength);
            }

            half3 BuildAc6Mask1(half maskRed, half maskGreen)
            {
                // Blender CM graph: a narrow 3m.R band drives Mask_1.R;
                // inverted 3m.G through a high-end ramp drives Mask_1.G.
                half redBand = (maskRed >= 0.055h && maskRed < 0.091h) ? 1.0h : 0.0h;
                half greenBand = smoothstep(0.945h, 1.0h, 1.0h - maskGreen);
                return saturate(half3(redBand, greenBand, 0.0h) * _ZoneMaskStrength);
            }

            half3 BuildAc6ZoneAlbedo(half3 sourceAlbedo, half3 mask, float2 uv)
            {
                half3 mask0 = BuildAc6Mask0(mask.r);
                half3 mask1 = BuildAc6Mask1(mask.r, mask.g);

                half3 albedo = _Zone1Color.rgb;
                albedo = lerp(albedo, _Zone2Color.rgb, mask0.r);
                albedo = lerp(albedo, _Zone3Color.rgb, mask0.g);
                albedo = lerp(albedo, _Zone4Color.rgb, mask0.b);
                albedo = lerp(albedo, _Zone5Color.rgb, mask1.r);
                albedo = lerp(albedo, _Zone6Color.rgb, mask1.g);
                albedo = lerp(albedo, _Zone7Color.rgb, mask1.b);

                half sourceLuma = dot(sourceAlbedo, half3(0.299h, 0.587h, 0.114h));
                half panelDetail = lerp(0.94h, 1.06h, saturate(sourceLuma));
                return albedo * lerp(1.0h, panelDetail, _AlbedoDetailStrength);
            }

            InputData BuildInputData(Varyings input, half3 normalTS)
            {
                InputData inputData = (InputData)0;

                half3 normalWS = NormalizeNormalPerPixel(input.normalWS);
                half3 tangentWS = normalize(input.tangentWS.xyz);
                half tangentSign = input.tangentWS.w;
                half3 bitangentWS = tangentSign * cross(normalWS, tangentWS);
                half3x3 tangentToWorld = half3x3(tangentWS, bitangentWS, normalWS);

                inputData.positionWS = input.positionWS;
                inputData.positionCS = input.positionCS;
                inputData.tangentToWorld = tangentToWorld;
                inputData.normalWS = NormalizeNormalPerPixel(TransformTangentToWorld(normalTS, tangentToWorld));
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.fogCoord = input.fogFactor;
                inputData.vertexLighting = half3(0, 0, 0);
                inputData.bakedGI = SampleSH(inputData.normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);
                return inputData;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 baseSample = SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap));
                half3 maskSample = SAMPLE_TEXTURE2D(_Mask3Map, sampler_Mask3Map, input.uv).rgb;

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = _ZoneMode > 0.5h
                    ? BuildAc6ZoneAlbedo(baseSample.rgb, maskSample, input.uv)
                    : BuildStableArmorAlbedo(baseSample.rgb, maskSample, input.uv);
                surfaceData.alpha = 1.0h;
                surfaceData.metallic = saturate(_Metallic + (maskSample.r - 0.5h) * _MaskMetallicInfluence);
                surfaceData.smoothness = saturate(_Smoothness + ((1.0h - maskSample.g) - 0.5h) * _MaskSmoothnessInfluence);
                surfaceData.occlusion = lerp(1.0h, maskSample.b, _OcclusionStrength);
                surfaceData.normalTS = SampleNormal(input.uv, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap), _BumpScale);
                #if defined(_EMISSION)
                    half3 emissionSample = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, input.uv).rgb;
                    surfaceData.emission = (emissionSample + 0.04h) * _EmissionColor.rgb * _EmissionStrength;
                #else
                    surfaceData.emission = half3(0, 0, 0);
                #endif
                surfaceData.specular = half3(0, 0, 0);
                surfaceData.clearCoatMask = 0;
                surfaceData.clearCoatSmoothness = 0;

                InputData inputData = BuildInputData(input, surfaceData.normalTS);
                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                color.a = 1.0h;
                return color;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
