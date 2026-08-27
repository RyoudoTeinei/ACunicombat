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
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _CoreColor;
                half4 _EdgeColor;
                half _Intensity;
                half _Opacity;
                half _Softness;
                half _FlowSpeed;
                half _Seed;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.uv = input.uv;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half radialDistance = abs(input.uv.x * 2.0h - 1.0h);
                half radial = pow(saturate(1.0h - radialDistance), max(0.5h, _Softness));
                half longitudinal = 1.0h - smoothstep(0.58h, 1.0h, input.uv.y);
                half rootTightening = smoothstep(0.0h, 0.07h, input.uv.y) * 0.18h + 0.82h;

                half phase = input.uv.y * 43.0h - _Time.y * _FlowSpeed + _Seed;
                half turbulence = 0.92h + 0.08h * sin(phase) * sin(phase * 0.37h + 1.7h);
                half fineFlow = 0.96h + 0.04h * sin(phase * 2.31h + input.positionWS.x * 0.23h);
                half alpha = radial * longitudinal * rootTightening * turbulence * fineFlow;
                alpha *= _Opacity * saturate(_Intensity);

                half coreMask = pow(radial, 2.2h);
                half3 color = lerp(_EdgeColor.rgb, _CoreColor.rgb, coreMask);
                color *= 0.72h + 0.42h * saturate(_Intensity);
                return half4(color * alpha, alpha);
            }
            ENDHLSL
        }
    }
}
