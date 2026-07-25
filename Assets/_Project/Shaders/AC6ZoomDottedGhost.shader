Shader "ACunicombat/Zoom Dotted Ghost"
{
    Properties
    {
        _GhostColor("Ghost Color", Color) = (0.20, 1.00, 0.72, 1.00)
        _GhostAlpha("Ghost Alpha", Range(0.05, 0.8)) = 0.24
        _GridSpacing("Grid Spacing", Range(4, 32)) = 10
        _DotRadius("Dot Radius", Range(0.5, 4)) = 1.25
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent+10"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "ZoomDottedGhost"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 screenPos : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _GhostColor;
                half _GhostAlpha;
                half _GridSpacing;
                half _DotRadius;
            CBUFFER_END

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.screenPos = ComputeScreenPos(output.positionCS);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 screenUv = input.screenPos.xy / input.screenPos.w;
                float2 pixel = screenUv * _ScaledScreenParams.xy;
                float2 cell = frac((pixel + _GridSpacing * 0.5) / _GridSpacing);
                float2 delta = (cell - 0.5) * _GridSpacing;
                float distanceToDot = length(delta);
                float dotMask = 1.0 - smoothstep(
                    _DotRadius - 0.75,
                    _DotRadius + 0.75,
                    distanceToDot);
                half alpha = saturate(_GhostAlpha + dotMask * 0.42);
                half3 color = lerp(
                    _GhostColor.rgb * 0.42,
                    _GhostColor.rgb,
                    dotMask);
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
