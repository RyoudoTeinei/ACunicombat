// 热成像全屏 pass：由 "AC6ThermalScreen"（URP 内置 FullScreenPassRendererFeature）
// 在 BeforeRenderingPostProcessing 注入。职责：
//   1. 深度 == far 的像素判为太空背景，压到接近零的冷底（解决黑机体对黑太空）；
//   2. 传感器质感：按块量化 UV（低分辨率探测器阵列观感）+ 时域噪声；
//   3. 白热 / 黑热极性反转（唯一反转点，物体 pass 恒为白热映射）。
// 参数 _AC6ThermalScreenParams 由 MechThermalVisionController 每帧推送。
Shader "Hidden/ACunicombat/AC6/Thermal Screen"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        Cull Off
        ZTest Always

        Pass
        {
            Name "AC6ThermalScreen"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            // x = 探测器像素块边长(屏幕像素), y = 噪声幅度, z = 背景冷底灰度,
            // w = 极性（>0 白热，<0 黑热）。
            float4 _AC6ThermalScreenParams;
            // x = 锐化强度, y = 对比度, z = 太空背景噪声比例。
            float4 _AC6ThermalClarityParams;

            float Hash21(float2 p)
            {
                p = frac(p * float2(0.1031, 0.1030));
                p += dot(p, p.yx + 33.33);
                return frac((p.x + p.y) * p.x);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float blockPx = max(1.0, _AC6ThermalScreenParams.x);
                float2 grid = _ScreenParams.xy / blockPx;
                float2 cell = floor(uv * grid);
                float2 quv = (cell + 0.5) / grid;

                half3 color = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, quv).rgb;
                float g = dot(color, float3(0.299, 0.587, 0.114));

                float depthRaw = SampleSceneDepth(quv);
                #if UNITY_REVERSED_Z
                bool isBackground = depthRaw <= 1.0e-7;
                #else
                bool isBackground = depthRaw >= 1.0 - 1.0e-7;
                #endif
                if (isBackground)
                {
                    g = _AC6ThermalScreenParams.z;
                }
                else
                {
                    float2 texel = blockPx / _ScreenParams.xy;
                    float gLeft = dot(
                        SAMPLE_TEXTURE2D_X(
                            _BlitTexture,
                            sampler_LinearClamp,
                            quv - float2(texel.x, 0)).rgb,
                        float3(0.299, 0.587, 0.114));
                    float gRight = dot(
                        SAMPLE_TEXTURE2D_X(
                            _BlitTexture,
                            sampler_LinearClamp,
                            quv + float2(texel.x, 0)).rgb,
                        float3(0.299, 0.587, 0.114));
                    float gUp = dot(
                        SAMPLE_TEXTURE2D_X(
                            _BlitTexture,
                            sampler_LinearClamp,
                            quv + float2(0, texel.y)).rgb,
                        float3(0.299, 0.587, 0.114));
                    float gDown = dot(
                        SAMPLE_TEXTURE2D_X(
                            _BlitTexture,
                            sampler_LinearClamp,
                            quv - float2(0, texel.y)).rgb,
                        float3(0.299, 0.587, 0.114));
                    float neighbourAverage =
                        (gLeft + gRight + gUp + gDown) * 0.25;
                    g = saturate(
                        g + (g - neighbourAverage) *
                        _AC6ThermalClarityParams.x);
                    g = saturate(
                        (g - 0.5) * _AC6ThermalClarityParams.y + 0.5);
                }

                // 逐探测器单元的时域噪声：seed 用块坐标，帧间由 _Time 扰动。
                float noise = Hash21(cell + frac(_Time.y * 7.31) * 251.0) - 0.5;
                float noiseScale = isBackground
                    ? _AC6ThermalClarityParams.z
                    : 1.0;
                g = saturate(
                    g + noise * _AC6ThermalScreenParams.y * noiseScale);

                if (_AC6ThermalScreenParams.w < 0.0)
                {
                    g = 1.0 - g;
                }

                return half4(g, g, g, 1);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
