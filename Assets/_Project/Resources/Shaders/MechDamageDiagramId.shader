Shader "Hidden/ACunicombat/DamageDiagramId"
{
    Properties { _PartId ("Part ID", Float) = 1 _RightPartId ("Other limb ID", Float) = 0 _LeftSign ("Left side", Float) = -1 }
    SubShader { Pass {
        Cull Off ZWrite On ZTest LEqual Blend Off
        HLSLPROGRAM
        #pragma vertex vert
        #pragma fragment frag
        #include "UnityCG.cginc"
        float _PartId, _RightPartId, _LeftSign;
        float4x4 _RegionMatrix;
        struct v2f { float4 pos:SV_POSITION; float side:TEXCOORD0; };
        v2f vert(float4 vertex:POSITION) { v2f o;o.pos=UnityObjectToClipPos(vertex);o.side=mul(_RegionMatrix,vertex).x*_LeftSign;return o; }
        float4 frag(v2f i):SV_Target { float id=(_RightPartId>0&&i.side<0)?_RightPartId:_PartId;return float4(fmod(id,256)/255.0,floor(id/256)/255.0,0,1); }
        ENDHLSL
    } }
}
