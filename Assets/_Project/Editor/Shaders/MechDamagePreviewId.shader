Shader "Hidden/ACunicombat/Editor/DamagePreviewId"
{
    Properties { _PartId ("Part ID", Float) = 1 }
    SubShader
    {
        Pass
        {
            Cull Off ZWrite On ZTest LEqual Blend Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float _PartId;
            float4 vert(float4 vertex : POSITION) : SV_POSITION { return UnityObjectToClipPos(vertex); }
            float4 frag() : SV_Target { return float4(_PartId / 255.0, 0, 0, 1); }
            ENDHLSL
        }
    }
}
