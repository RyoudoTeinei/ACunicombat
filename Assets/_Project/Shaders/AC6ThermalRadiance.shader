// 热成像物体重绘 shader：由 "AC6ThermalObjects"（RenderObjects feature）作为
// override material 使用，把不透明场景按温度重画成灰度辐射亮度。
// 温度来源：
//   - 挂了 MechThermalSignature / MechThermalPointEmitter 的物体经
//     MaterialPropertyBlock 注入逐 renderer 的 _ThermalTemp（开尔文）；
//   - 未标记的物体落回材质默认值（环境冷物体）。
// 亮度映射：L = ε·(T/1000)^4，窗口 _AC6ThermalWindow 由
// MechThermalVisionController 在 CPU 端按当前场景温度域计算（AGC），
// 因此不需要 GPU 亮度回读。白热/黑热极性统一在屏幕 pass 反转。
Shader "ACunicombat/AC6/Thermal Radiance"
{
    Properties
    {
        _ThermalTemp("Temperature (Kelvin)", Float) = 240
        _Emissivity("Emissivity", Range(0, 1)) = 0.92
        _GrazingFalloff("Grazing Emissivity Falloff", Range(0, 1)) = 0.35
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
        }

        // 与首轮不透明绘制同深度重画：LEqual 让相同几何体通过，颜色被热辐射值覆盖。
        Pass
        {
            Name "AC6ThermalRadiance"
            ZTest LEqual
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _ThermalTemp;
                float _Emissivity;
                float _GrazingFalloff;
            CBUFFER_END

            // x = 窗口下限辐射亮度, y = 1 / (上限 − 下限)。CPU 端 AGC 每帧更新。
            float4 _AC6ThermalWindow;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 viewDirWS : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.viewDirWS = GetWorldSpaceViewDir(positionWS);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // 真实红外中掠射角发射率下降，轮廓边缘偏暗——廉价但显著提升可读性。
                float ndv = saturate(dot(normalize(input.normalWS), normalize(input.viewDirWS)));
                float emissivity = _Emissivity * lerp(1.0 - _GrazingFalloff, 1.0, ndv);

                float t = _ThermalTemp * 0.001;
                float radiance = emissivity * t * t * t * t;
                float g = saturate((radiance - _AC6ThermalWindow.x) * _AC6ThermalWindow.y);
                return half4(g, g, g, 1);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
