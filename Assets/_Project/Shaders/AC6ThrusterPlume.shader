Shader "ACunicombat/Mech Thruster Plume"
{
    Properties
    {
        [HDR] _CoreColor("Core Color", Color) = (1.5, 2.8, 5.8, 1)
        [HDR] _EdgeColor("Edge Color", Color) = (0.08, 0.75, 3.8, 1)
        _Intensity("Intensity", Range(0, 2)) = 1
        _Opacity("Opacity", Range(0, 1)) = 0.6
        _Softness("Radial Softness", Range(0.5, 6)) = 2
        _FlowSpeed("Flow Speed", Float) = 20
        _Seed("Seed", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent+20"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "ThrusterPlume"
            Tags { "LightMode" = "UniversalForward" }
            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma multi_compile_instancing
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            UNITY_INSTANCING_BUFFER_START(PlumeProperties)
                UNITY_DEFINE_INSTANCED_PROP(float4, _CoreColor)
                UNITY_DEFINE_INSTANCED_PROP(float4, _EdgeColor)
                UNITY_DEFINE_INSTANCED_PROP(float, _Intensity)
                UNITY_DEFINE_INSTANCED_PROP(float, _Opacity)
                UNITY_DEFINE_INSTANCED_PROP(float, _Softness)
                UNITY_DEFINE_INSTANCED_PROP(float, _FlowSpeed)
                UNITY_DEFINE_INSTANCED_PROP(float, _Seed)
            UNITY_INSTANCING_BUFFER_END(PlumeProperties)

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.uv = input.uv;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                half4 _CoreColorValue = UNITY_ACCESS_INSTANCED_PROP(PlumeProperties, _CoreColor);
                half4 _EdgeColorValue = UNITY_ACCESS_INSTANCED_PROP(PlumeProperties, _EdgeColor);
                half _IntensityValue = UNITY_ACCESS_INSTANCED_PROP(PlumeProperties, _Intensity);
                half _OpacityValue = UNITY_ACCESS_INSTANCED_PROP(PlumeProperties, _Opacity);
                half _SoftnessValue = UNITY_ACCESS_INSTANCED_PROP(PlumeProperties, _Softness);
                half _FlowSpeedValue = UNITY_ACCESS_INSTANCED_PROP(PlumeProperties, _FlowSpeed);
                half _SeedValue = UNITY_ACCESS_INSTANCED_PROP(PlumeProperties, _Seed);

                half radialDistance = abs(input.uv.x * 2.0h - 1.0h);
                half radial = pow(saturate(1.0h - radialDistance), max(0.5h, _SoftnessValue));
                half longitudinal = 1.0h - smoothstep(0.58h, 1.0h, input.uv.y);
                half rootTightening = smoothstep(0.0h, 0.07h, input.uv.y) * 0.18h + 0.82h;

                half phase = input.uv.y * 43.0h - _Time.y * _FlowSpeedValue + _SeedValue;
                half turbulence = 0.92h + 0.08h * sin(phase) * sin(phase * 0.37h + 1.7h);
                half fineFlow = 0.96h + 0.04h * sin(phase * 2.31h + input.positionWS.x * 0.23h);
                half alpha = radial * longitudinal * rootTightening * turbulence * fineFlow;
                alpha *= _OpacityValue * saturate(_IntensityValue);

                half coreMask = pow(radial, 2.2h);
                half3 color = lerp(_EdgeColorValue.rgb, _CoreColorValue.rgb, coreMask);
                color *= 0.72h + 0.42h * saturate(_IntensityValue);
                return half4(color * alpha, alpha);
            }
            ENDHLSL
        }
    }
}
