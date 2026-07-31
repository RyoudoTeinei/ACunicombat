// 程序化深空天空盒。取代原先的 4096x2048 等距柱状全景图，原因有二：
//
//   1. 清晰度是算术上限，不是调参问题。4096 像素铺满 360 度只有约 11.4 像素/度，
//      而 1080p 配 60 度 FOV 需要约 32 像素/度——贴图必然被放大近三倍。
//      片元里逐像素生成的星点没有这个上限，任何分辨率与 FOV 下都锐利。
//   2. 等距柱状投影在天顶与天底把整行像素收敛到一点，星点被压扁抹平。
//      这里用立方体面参数化，没有极点退化。
//
// 走样处理：小于一个像素的星点在镜头转动时会闪烁。这里把星点足迹的最小尺寸锁到
// 一个像素，同时按面积比缩减强度以守恒通量——星点因此既不闪烁，暗星也不会被
// 人为提亮成一片噪点。
Shader "AC6/Deep Space Skybox"
{
    Properties
    {
        [Header(Background)]
        _BackgroundRadiance("Background Radiance", Color) = (0.0008, 0.0008, 0.0011, 1)

        [Header(Stars)]
        _StarDensity("Star Density", Range(0, 1)) = 0.35
        _StarBrightness("Star Brightness", Range(0, 4)) = 1.0
        // 真实星等分布是幂律：暗星远多于亮星。指数越高，亮星越稀有。
        _StarMagnitudeBias("Star Magnitude Bias", Range(1, 10)) = 3.6
        _StarColorSpread("Star Colour Spread", Range(0, 1)) = 0.55
        _StarCoarseCells("Bright Star Cells", Range(4, 128)) = 22
        _StarFineCells("Faint Star Cells", Range(8, 512)) = 92
        _StarRadius("Star Core Radius", Range(0.002, 0.2)) = 0.035

        [Header(Sun)]
        _SunDirection("Sun Direction (world)", Vector) = (0, 0.515, -0.857, 0)
        _SunColor("Sun Colour", Color) = (1, 0.972, 0.863, 1)
        _SunDiskRadiance("Sun Disk Radiance", Float) = 220
        _SunAngularRadius("Sun Angular Radius (deg)", Range(0.05, 5)) = 0.72
        _SunCoronaRadiance("Sun Corona Radiance", Float) = 6.5
        _SunCoronaRadius("Sun Corona Radius (deg)", Range(0.5, 40)) = 7.0
        _SunCoronaFalloff("Sun Corona Falloff", Range(1, 8)) = 2.6

        [Header(Nebula)]
        _NebulaStrength("Nebula Strength", Range(0, 2)) = 0.4
        _NebulaScale("Nebula Scale", Range(0.2, 8)) = 1.6
        _NebulaColorA("Nebula Colour A", Color) = (0.055, 0.075, 0.135, 1)
        _NebulaColorB("Nebula Colour B", Color) = (0.115, 0.050, 0.070, 1)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Background"
            "PreviewType" = "Skybox"
            "RenderPipeline" = "UniversalPipeline"
        }

        Cull Off
        ZWrite Off

        Pass
        {
            Name "DeepSpaceSkybox"

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
            half4 _BackgroundRadiance;
            half _StarDensity;
            half _StarBrightness;
            half _StarMagnitudeBias;
            half _StarColorSpread;
            half _StarCoarseCells;
            half _StarFineCells;
            half _StarRadius;
            float4 _SunDirection;
            half4 _SunColor;
            float _SunDiskRadiance;
            float _SunAngularRadius;
            float _SunCoronaRadiance;
            float _SunCoronaRadius;
            float _SunCoronaFalloff;
            half _NebulaStrength;
            half _NebulaScale;
            half4 _NebulaColorA;
            half4 _NebulaColorB;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 directionOS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                // Unity 的天空盒网格以相机为心，物体空间顶点方向即视线方向。
                output.directionOS = input.positionOS.xyz;
                return output;
            }

            float3 Hash33(float3 p)
            {
                p = float3(
                    dot(p, float3(127.1, 311.7, 74.7)),
                    dot(p, float3(269.5, 183.3, 246.1)),
                    dot(p, float3(113.5, 271.9, 124.6)));
                return frac(sin(p) * 43758.5453123);
            }

            float Hash13(float3 p)
            {
                return frac(sin(dot(p, float3(12.9898, 78.233, 37.719))) * 43758.5453123);
            }

            // 立方体面参数化：相比等距柱状，没有极点退化，各面畸变也温和得多。
            void DirectionToFaceUv(float3 dir, out float2 uv, out float faceId)
            {
                float3 a = abs(dir);
                if (a.x >= a.y && a.x >= a.z)
                {
                    uv = dir.zy / a.x;
                    faceId = dir.x > 0.0 ? 0.0 : 1.0;
                }
                else if (a.y >= a.z)
                {
                    uv = dir.xz / a.y;
                    faceId = dir.y > 0.0 ? 2.0 : 3.0;
                }
                else
                {
                    uv = dir.xy / a.z;
                    faceId = dir.z > 0.0 ? 4.0 : 5.0;
                }
            }

            half3 StarColour(float t)
            {
                // 松散对应光谱型：蓝白 → 白 → 黄白 → 橙。真实肉眼星野以近白为主，
                // 所以 _StarColorSpread 控制向纯白回退多少。
                half3 hot = half3(0.72, 0.81, 1.00);
                half3 mid = half3(1.00, 0.97, 0.94);
                half3 cool = half3(1.00, 0.80, 0.60);
                half3 spectral = t < 0.5
                    ? lerp(hot, mid, saturate(t * 2.0))
                    : lerp(mid, cool, saturate((t - 0.5) * 2.0));
                return lerp(half3(1.0, 1.0, 1.0), spectral, _StarColorSpread);
            }

            // 一层星点。每个单元格最多一颗星，星心被限制在格内 [0.2, 0.8]，因此星点永远
            // 不会跨越格边界，单次查表即可，不需要查邻域。
            half3 StarLayer(
                float2 uv,
                float faceId,
                float cells,
                float pixelFootprintUv,
                float brightnessScale,
                float salt)
            {
                float2 grid = uv * cells;
                float2 cellId = floor(grid);
                float2 local = grid - cellId;

                float3 h = Hash33(float3(cellId, faceId * 31.0 + salt));
                float exists = step(1.0 - _StarDensity, h.z);
                if (exists <= 0.0)
                {
                    return half3(0.0, 0.0, 0.0);
                }

                float2 centre = 0.2 + 0.6 * h.xy;
                float distanceToStar = length(local - centre);

                // 走样防护。足迹不得小于一个像素，否则镜头一转星点就在采样格之间跳进跳出；
                // 放大之后按面积比削减强度以守恒通量，暗星才不会被顺带提亮。
                float pixel = pixelFootprintUv * cells;
                float radius = max(_StarRadius, pixel);
                float flux = (_StarRadius * _StarRadius) / max(radius * radius, 1e-6);

                float magnitude = pow(Hash13(float3(cellId, salt + 7.3)), _StarMagnitudeBias);
                float shape = saturate(1.0 - distanceToStar / radius);
                shape *= shape;

                return StarColour(h.x) * (magnitude * shape * flux * brightnessScale);
            }

            float ValueNoise(float3 p)
            {
                float3 i = floor(p);
                float3 f = p - i;
                f = f * f * (3.0 - 2.0 * f);

                float n000 = Hash13(i + float3(0, 0, 0));
                float n100 = Hash13(i + float3(1, 0, 0));
                float n010 = Hash13(i + float3(0, 1, 0));
                float n110 = Hash13(i + float3(1, 1, 0));
                float n001 = Hash13(i + float3(0, 0, 1));
                float n101 = Hash13(i + float3(1, 0, 1));
                float n011 = Hash13(i + float3(0, 1, 1));
                float n111 = Hash13(i + float3(1, 1, 1));

                float x00 = lerp(n000, n100, f.x);
                float x10 = lerp(n010, n110, f.x);
                float x01 = lerp(n001, n101, f.x);
                float x11 = lerp(n011, n111, f.x);
                return lerp(lerp(x00, x10, f.y), lerp(x01, x11, f.y), f.z);
            }

            half3 Nebula(float3 dir)
            {
                if (_NebulaStrength <= 0.0)
                {
                    return half3(0.0, 0.0, 0.0);
                }

                float3 p = dir * _NebulaScale;
                float n = ValueNoise(p) * 0.6 + ValueNoise(p * 2.7 + 11.3) * 0.4;
                // 压到高端：星云应是稀疏的几片，不是铺满全天的雾。
                n = saturate(n * n * n * 2.2);
                float tint = saturate(ValueNoise(p * 1.7 - 5.1) * 1.4);
                half3 colour = lerp(_NebulaColorA.rgb, _NebulaColorB.rgb, tint);
                return colour * (n * _NebulaStrength);
            }

            half3 Sun(float3 dir, float pixelAngle)
            {
                // 方向为零向量时 normalize 会产生 NaN，而 NaN 会顺着加法污染整个像素、
                // 通常表现为纯白。材质属性丢失时这是很容易踩到的一种失效方式。
                float3 sunDir = _SunDirection.xyz;
                float sunLengthSq = dot(sunDir, sunDir);
                if (sunLengthSq < 1e-8)
                {
                    return half3(0.0, 0.0, 0.0);
                }
                sunDir *= rsqrt(sunLengthSq);

                float cosAngle = clamp(dot(dir, sunDir), -1.0, 1.0);
                float angle = acos(cosAngle);

                float diskRadius = radians(_SunAngularRadius);
                // 边缘至少铺开一个像素，否则日面轮廓会有硬锯齿。
                float edge = max(pixelAngle, diskRadius * 0.02);
                float disk = 1.0 - smoothstep(diskRadius - edge, diskRadius + edge, angle);

                // 临边昏暗：日面中心比边缘亮，是恒星大气的真实特征，也让日面不像一张贴纸。
                float normalised = saturate(angle / max(diskRadius, 1e-6));
                float limb = lerp(1.0, 0.72, normalised * normalised);

                float coronaRadius = radians(max(_SunCoronaRadius, 1e-4));
                float coronaFade = saturate(1.0 - angle / coronaRadius);
                // 指数必须夹到 1 以上。pow(x, 0) 在 HLSL 里对 x = 0 也返回 1，所以一个为零的
                // 指数会把日冕变成覆盖全天的常数项，整片天空被顶成纯白——材质属性没拿到
                // shader 默认值时就是这个后果。
                float corona = pow(coronaFade, max(_SunCoronaFalloff, 1.0));

                return _SunColor.rgb *
                    (disk * limb * _SunDiskRadiance + corona * _SunCoronaRadiance);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 dir = normalize(input.directionOS);

                // 像素张角。用方向的导数而非 uv 的导数：uv 在立方体面接缝处会跳变，
                // 拿它当足迹会让接缝上的星点被放大成一条线。
                float pixelAngle = length(fwidth(dir));

                float2 uv;
                float faceId;
                DirectionToFaceUv(dir, uv, faceId);
                // 面上 uv 跨度 [-1, 1] 对应 90 度，据此把张角换算成 uv 单位。
                float pixelFootprintUv = pixelAngle * (2.0 / radians(90.0));

                half3 colour = _BackgroundRadiance.rgb;
                colour += Nebula(dir);
                colour += StarLayer(
                    uv, faceId, _StarCoarseCells, pixelFootprintUv, _StarBrightness, 3.1);
                colour += StarLayer(
                    uv, faceId, _StarFineCells, pixelFootprintUv, _StarBrightness * 0.55, 19.7);
                colour += Sun(dir, pixelAngle);

                return half4(colour, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
