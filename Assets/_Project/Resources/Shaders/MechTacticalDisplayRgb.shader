Shader "Hidden/ACunicombat/TacticalDisplayRgb"
{
 Properties { _RgbStrength("RGB emitter strength",Range(0,1))=1  _MainTex("Input / logical pixels",2D)="black" {} _LogicalSize("Logical resolution",Vector)=(1000,625,0,0) _ViewRect("Viewport UV",Vector)=(0,0,1,1) _OutputSize("Output resolution",Vector)=(1600,1000,0,0) _GlassScatter("Glass scatter",Range(0,.15))=.045 _GlassDispersion("Glass dispersion in cells",Range(0,.3))=.08 }
 SubShader
 {
  Cull Off ZWrite Off ZTest Always Blend Off
  // Stage 1: exact overlap integration in linear light. Each output texel is a real display cell.
  Pass
  {
   HLSLPROGRAM
   #pragma vertex vert_img
   #pragma fragment integrate
   #pragma target 3.5
   #include "UnityCG.cginc"
   sampler2D _MainTex;float4 _MainTex_TexelSize,_LogicalSize;
   float4 integrate(v2f_img i):SV_Target
   {
    float2 cell=floor(i.uv*_LogicalSize.xy);
    float2 lo=cell*_MainTex_TexelSize.zw/_LogicalSize.xy;
    float2 hi=(cell+1)*_MainTex_TexelSize.zw/_LogicalSize.xy;
    float3 sum=0;
    // Integrate every overlapped source pixel, including non-integer reduction ratios.
    for(int y=(int)floor(lo.y);y<(int)ceil(hi.y);y++)
    for(int x=(int)floor(lo.x);x<(int)ceil(hi.x);x++)
    {
     float area=max(0,min(hi.x,x+1)-max(lo.x,x))*max(0,min(hi.y,y+1)-max(lo.y,y));
     sum+=tex2Dlod(_MainTex,float4((float2(x,y)+.5)*_MainTex_TexelSize.xy,0,0)).rgb*area;
    }
    return float4(sum/((hi.x-lo.x)*(hi.y-lo.y)),1);
   }
   ENDHLSL
  }
  // Stage 2: reconstruct ONLY logical cells through finite apertures and an optical point-spread function.
  Pass
  {
   HLSLPROGRAM
   #pragma vertex vert_img
   #pragma fragment reconstruct
   #pragma target 3.5
   #include "UnityCG.cginc"
   sampler2D _MainTex;float4 _MainTex_TexelSize,_ViewRect,_OutputSize;float _RgbStrength;
   float phi(float x){return exp(-.5*x*x)*.3989422804;}
   float cdf(float x)
   {
    float a=abs(x),t=1/(1+.2316419*a);
    float tail=phi(a)*t*(.31938153+t*(-.356563782+t*(1.781477937+t*(-1.821255978+t*1.330274429))));
    return x>=0 ? 1-tail : tail;
   }
   float primitive(float x,float sigma){return x*cdf(x/sigma)+sigma*phi(x/sigma);}
   float emitter(float delta,float footprint,float halfWidth,float sigma)
   {
    float b=max(.0001,footprint*.5);
    // Analytic integral of a Gaussian-blurred rectangular emitter over the output pixel area.
    return max(0,(primitive(delta+halfWidth+b,sigma)-primitive(delta+halfWidth-b,sigma)-primitive(delta-halfWidth+b,sigma)+primitive(delta-halfWidth-b,sigma))/(2*b*2*halfWidth));
   }
   float aperture(float delta,float footprint){return emitter(delta,footprint,.38,.14);}
   float4 reconstruct(v2f_img i):SV_Target
   {
    float2 uv=_ViewRect.xy+i.uv*_ViewRect.zw;
    float2 p=uv*_MainTex_TexelSize.zw-.5;
    float2 footprint=_ViewRect.zw*_MainTex_TexelSize.zw/_OutputSize.xy;
    float2 origin=floor(p);
    float3 light=0;
    for(int y=-1;y<=2;y++)for(int x=-1;x<=2;x++)
    {
     float2 index=origin+float2(x,y);
     float2 delta=p-index;
     float weight=aperture(delta.x,footprint.x)*aperture(delta.y,footprint.y);
     float3 value=tex2Dlod(_MainTex,float4((index+.5)*_MainTex_TexelSize.xy,0,0)).rgb;
     // Low black level is emitted by the same cells, never a separate dot overlay.
     // Separate finite RGB emitters inside each cell, each normalized to conserve its channel energy.
     // Slightly distinct optical spread models the cover glass rather than adding random colour.
     float3 rgbWeight=float3(
       emitter(delta.x+.18,footprint.x,.20,.12)*emitter(delta.y+.035,footprint.y,.36,.145),
       emitter(delta.x,footprint.x,.20,.115)*emitter(delta.y,footprint.y,.36,.14),
       emitter(delta.x-.18,footprint.x,.20,.125)*emitter(delta.y-.035,footprint.y,.36,.15));
     // Suppress unresolved RGB stripes at normal/minified scale to avoid false colour and moire.
     float resolved=1-smoothstep(.28,.8,max(footprint.x,footprint.y));
     float brightness=max(value.r,max(value.g,value.b));
     float visibleStructure=_RgbStrength*resolved*lerp(.12,1,smoothstep(.008,.08,brightness));
     light+=value*lerp(float3(weight,weight,weight),rgbWeight,visibleStructure)
         +float3(.00025,.00032,.00034)*weight;
    }
    // When cells become subpixel, use the logical texture's area-filtered mip chain.
    float lod=max(0,log2(max(footprint.x,footprint.y)));
    float3 averaged=tex2Dlod(_MainTex,float4(uv,0,lod)).rgb+float3(.00025,.00032,.00034);
    light=lerp(light,averaged,smoothstep(.9,1.5,max(footprint.x,footprint.y)));
    float2 edge=uv*2-1;
    return float4(max(0,light)*(1-.08*smoothstep(.4,1.8,dot(edge,edge))),1);
   }
   ENDHLSL
  }
  // Stage 3: weak glass scattering of reconstructed emissions. The sharp cell image remains intact.
  Pass
  {
   HLSLPROGRAM
   #pragma vertex vert_img
   #pragma fragment glass
   #include "UnityCG.cginc"
   sampler2D _MainTex;float4 _MainTex_TexelSize,_LogicalSize,_ViewRect;
   float _GlassScatter,_GlassDispersion;
   float3 bright(float2 uv)
   {
    float3 c=tex2D(_MainTex,uv).rgb;
    return c*smoothstep(.015,.12,max(c.r,max(c.g,c.b)));
   }
   float4 glass(v2f_img i):SV_Target
   {
    float3 core=tex2D(_MainTex,i.uv).rgb;
    float2 cellUV=1/(_LogicalSize.xy*_ViewRect.zw);
    float2 displacement=float2(.85,.35)*cellUV*_GlassDispersion;
    float3 halo=0;
    for(int j=0;j<8;j++)
    {
     float a=j*.785398163;float2 offset=float2(cos(a),sin(a))*cellUV*1.1;
     halo+=float3(bright(i.uv+offset+displacement).r,bright(i.uv+offset).g,bright(i.uv+offset-displacement).b)*.125;
    }
    // Very faint broad surface reflection, independent of frame noise or image zoom.
    float2 p=(_ViewRect.xy+i.uv*_ViewRect.zw)-float2(.3,.75);
    float reflection=exp(-dot(p*float2(.85,1.6),p*float2(.85,1.6))*3);
    return float4(core+halo*_GlassScatter+float3(.000035,.000040,.000045)*reflection,1);
   }
   ENDHLSL
  }

  // Native-resolution area resolve, deliberately free of the physical pixel structure.
  Pass
  {
   HLSLPROGRAM
   #pragma vertex vert_img
   #pragma fragment clean
   #include "UnityCG.cginc"
   sampler2D _MainTex;float4 _OutputSize;
   float4 clean(v2f_img i):SV_Target
   {
    float2 d=.25/_OutputSize.xy;
    return (tex2D(_MainTex,i.uv+d)+tex2D(_MainTex,i.uv-d)
      +tex2D(_MainTex,i.uv+float2(d.x,-d.y))+tex2D(_MainTex,i.uv+float2(-d.x,d.y)))*.25;
   }
   ENDHLSL
  }
  // Smooth route transition; floating readouts always use the clean path.
  Pass
  {
   HLSLPROGRAM
   #pragma vertex vert_img
   #pragma fragment compose
   #include "UnityCG.cginc"
   sampler2D _MainTex,_CleanTex;float _PixelBlend;float4 _DetailsRect,_FooterRect,_FormationRect;
   float inside(float2 p,float4 r){return step(r.x,p.x)*step(r.y,p.y)*step(p.x,r.z)*step(p.y,r.w);}
   float4 compose(v2f_img i):SV_Target
   {
    float panel=max(max(inside(i.uv,_DetailsRect),inside(i.uv,_FooterRect)),inside(i.uv,_FormationRect));
    return lerp(tex2D(_CleanTex,i.uv),tex2D(_MainTex,i.uv),_PixelBlend*(1-panel));
   }
   ENDHLSL
  }

 }
}
