// Procedural draw of one GPU plume layer (see MechGpuPlume.compute). Matches the URP
// Particles/Unlit additive material used by the Shuriken plume: texture x tint x
// colour-over-lifetime, blended SrcAlpha/One, no depth write.
Shader "Hidden/ACunicombat/GpuPlume"
{
    Properties
    {
        _BaseMap("Particle", 2D) = "white" {}
        _Tint("Tint (HDR)", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "GpuPlume"
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha One
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Emitter { float3 position; float scale; float3 forward; float burning; float3 up; float pad0; float3 velocity; float pad1; };
            struct Particle { float3 position; float age; float3 velocity; float lifetime; float size; uint emitter; float seed; float pad; };

            StructuredBuffer<Particle> _Particles;
            StructuredBuffer<Emitter> _Emitters;
            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            TEXTURE2D(_Curves); SAMPLER(sampler_Curves);
            float4 _Tint;
            float _CurveRow;      // layer index: colour row = layer, size row = layer + 3
            float _WorldSpace;    // 1: smoke (world), 0: nozzle-local
            float _Stretch;       // 1: stretched along velocity (core)
            float _LengthScale;
            float _VelocityScale;

            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };

            float4 Curve(float row, float t)
            {
                // 64 samples per row, 6 rows: rows 0..2 colour x alpha, rows 3..5 size in r.
                float2 uv = float2((saturate(t) * 63 + .5) / 64, (row + .5) / 6);
                return SAMPLE_TEXTURE2D_LOD(_Curves, sampler_Curves, uv, 0);
            }

            Varyings vert(uint vertexId : SV_VertexID)
            {
                Varyings o;
                uint index = vertexId / 6, corner = vertexId % 6;
                Particle p = _Particles[index];
                // Two triangles: (0,0)(1,0)(1,1) (0,0)(1,1)(0,1)
                static const float2 corners[6] = { float2(-1,-1), float2(1,-1), float2(1,1), float2(-1,-1), float2(1,1), float2(-1,1) };
                float2 c = corners[corner];
                o.uv = c * .5 + .5;
                if (p.lifetime <= 0)
                {
                    o.positionCS = float4(0, 0, -2, 1); // degenerate, clipped
                    o.color = 0;
                    return o;
                }
                float t = p.age / p.lifetime;
                float4 colour = Curve(_CurveRow, t);
                float size = p.size * Curve(_CurveRow + 3, t).r;

                float3 world, worldVelocity;
                if (_WorldSpace > .5) { world = p.position; worldVelocity = p.velocity; }
                else
                {
                    Emitter e = _Emitters[p.emitter];
                    float3 right = normalize(cross(e.up, e.forward));
                    float3 up = cross(e.forward, right);
                    float3x3 toWorld = float3x3(right, up, e.forward);
                    world = e.position + mul(p.position, toWorld);
                    worldVelocity = mul(p.velocity, toWorld);
                }

                float3 toCamera = normalize(_WorldSpaceCameraPos - world);
                float3 offset;
                if (_Stretch > .5 && dot(worldVelocity, worldVelocity) > 1e-6)
                {
                    float3 axis = normalize(worldVelocity);
                    float3 side = normalize(cross(axis, toCamera));
                    float length = size * _LengthScale + sqrt(dot(worldVelocity, worldVelocity)) * _VelocityScale;
                    offset = side * (c.x * size * .5) + axis * (c.y * length * .5);
                }
                else
                {
                    float3 camRight = UNITY_MATRIX_V[0].xyz, camUp = UNITY_MATRIX_V[1].xyz;
                    offset = (camRight * c.x + camUp * c.y) * (size * .5);
                }
                o.positionCS = TransformWorldToHClip(world + offset);
                o.color = colour;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv);
                half4 col = tex * _Tint * i.color;
                return half4(col.rgb, col.a);
            }
            ENDHLSL
        }
    }
}
