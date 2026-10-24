Shader "Hidden/ACunicombat/TacticalScreen"
{
    Properties { _Intensity("Intensity",Range(0,1))=0.85 _MainTex("Screen",2D)="white" {} _Glow("Local glow",Range(0,2))=0.9 _Pitch("Pixel pitch",Float)=4 _DisplaySize("Display size",Vector)=(1920,1080,0,0) _Clock("Unscaled time",Float)=0 }
    SubShader
    {
        Tags { "Queue"="Overlay" }
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            Blend DstColor Zero
            ColorMask RGB
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            float _Intensity,_Pitch,_Clock;
            float4 _DisplaySize;
            float hash(float2 p) { return frac(sin(dot(p,float2(127.1,311.7)))*43758.5453); }
            float4 frag(v2f_img i):SV_Target
            {
                float2 px=i.uv*_DisplaySize.xy;
                float pitch=max(2,_Pitch);
                float2 cell=frac(px/pitch);
                // Fixed screen-space lattice: map zoom never changes this physical surface.
                float2 edge=min(cell,1-cell)*pitch;
                float2 gaps=1-smoothstep(.12,.72,edge);
                float grid=max(gaps.x,gaps.y);
                float scan=.5+.5*cos(px.y*3.14159265);
                float grain=hash(floor(px)+floor(_Clock*12)*float2(13,7))-.5;
                float2 centered=i.uv*2-1;
                float vignette=smoothstep(.28,1.65,dot(centered,centered));
                float stripe=frac(px.x/pitch);
                float3 phosphor=1-.055*float3(abs(stripe-.17),abs(stripe-.5),abs(stripe-.83));
                float luminance=1-.40*grid-.11*scan-.12*vignette+.032*grain;
                return float4(lerp(float3(1,1,1),phosphor*luminance,saturate(_Intensity)),1);
            }
            ENDHLSL
        }
        Pass
        {
            Cull Off ZWrite Off ZTest Always Blend Off
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment glowFrag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float _Glow;
            float3 bright(float2 uv)
            {
                float3 c=tex2D(_MainTex,uv).rgb;
                return c*smoothstep(.045,.32,max(c.r,max(c.g,c.b)));
            }
            float3 ring(float2 uv,float radius)
            {
                float2 d=_MainTex_TexelSize.xy*radius;
                return (bright(uv+float2(d.x,0))+bright(uv-float2(d.x,0))+
                    bright(uv+float2(0,d.y))+bright(uv-float2(0,d.y))+
                    bright(uv+d*.707)+bright(uv-d*.707)+
                    bright(uv+float2(d.x,-d.y)*.707)+bright(uv+float2(-d.x,d.y)*.707))*.125;
            }
            float4 glowFrag(v2f_img i):SV_Target
            {
                float3 core=tex2D(_MainTex,i.uv).rgb;
                float3 halo=ring(i.uv,1.25)*.45+ring(i.uv,2.75)*.35+ring(i.uv,5)*.2;
                return float4(core+halo*_Glow,1);
            }
            ENDHLSL
        }
    }
}
