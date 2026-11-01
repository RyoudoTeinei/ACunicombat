using System;
using ACunicombat.Sensing.Contracts;
using UnityEngine;
namespace ACunicombat.Mechs
{
 // Single geometry/palette source for the M map and the compact tactical radar.
 public static class MechTacticalSymbols
 {
  public static readonly Color Friendly=new Color32(0x37,0xA4,0xC7,0xFF);
  public static readonly Color Hostile=new Color32(0xA7,0x37,0x32,0xFF);
  public static readonly Color Unknown=new Color(.68f,.58f,.36f);
  public static readonly Color Own=new Color(.78f,.80f,.76f);
  private static void Segment(Action<Vector2,Vector2,Color,float> line,Vector2 center,float scale,Vector2 a,Vector2 b,Color color,float width=1.8f)=>line(center+a*scale,center+b*scale,color,width*scale);
  private static void Circle(Action<Vector2,Vector2,Color,float> line,Vector2 center,float scale,float radius,int segments,Color color)
  {
   for(int i=0;i<segments;i++){float a=i*Mathf.PI*2/segments,b=(i+1)*Mathf.PI*2/segments;Segment(line,center,scale,new Vector2(Mathf.Cos(a),Mathf.Sin(a))*radius,new Vector2(Mathf.Cos(b),Mathf.Sin(b))*radius,color);}
  }
  public static void Draw(Action<Vector2,Vector2,Color,float> line,Vector2 center,TrackAffiliation affiliation,bool missile,Color color,float scale=1)
  {
   if(missile)
   {
    Segment(line,center,scale,new Vector2(-5.4f,6.3f),new Vector2(-5.4f,-4.5f),color);
    Segment(line,center,scale,new Vector2(-5.4f,-4.5f),Vector2.zero,color);
    Segment(line,center,scale,Vector2.zero,new Vector2(5.4f,-4.5f),color);
    Segment(line,center,scale,new Vector2(5.4f,-4.5f),new Vector2(5.4f,6.3f),color);
    if(affiliation==TrackAffiliation.Friendly)
     for(int i=0;i<16;i++){float a=Mathf.PI+i*Mathf.PI/16,b=Mathf.PI+(i+1)*Mathf.PI/16;Segment(line,center,scale,new Vector2(Mathf.Cos(a)*10.8f,-7.2f+Mathf.Sin(a)*10.8f),new Vector2(Mathf.Cos(b)*10.8f,-7.2f+Mathf.Sin(b)*10.8f),color);}
    else{Segment(line,center,scale,new Vector2(-10.8f,-8.1f),new Vector2(0,-18.9f),color);Segment(line,center,scale,new Vector2(0,-18.9f),new Vector2(10.8f,-8.1f),color);}
   }
   else if(affiliation==TrackAffiliation.Friendly){Circle(line,center,scale,11.7f,40,color);Circle(line,center,scale,1.17f,8,color);}
   else if(affiliation==TrackAffiliation.Hostile)
   {
    float r=11.7f;Segment(line,center,scale,new Vector2(-r,-r),new Vector2(r,-r),color);Segment(line,center,scale,new Vector2(r,-r),new Vector2(r,r),color);
    Segment(line,center,scale,new Vector2(r,r),new Vector2(-r,r),color);Segment(line,center,scale,new Vector2(-r,r),new Vector2(-r,-r),color);Circle(line,center,scale,1.17f,8,color);
   }
   else{float r=12.6f;Segment(line,center,scale,Vector2.up*r,Vector2.right*r,color);Segment(line,center,scale,Vector2.right*r,Vector2.down*r,color);Segment(line,center,scale,Vector2.down*r,Vector2.left*r,color);Segment(line,center,scale,Vector2.left*r,Vector2.up*r,color);}
  }
  // Texture cache. A friendly symbol is 48 line draws and a friendly missile 20; drawn
  // line by line in IMGUI each costs a matrix change and a draw call, which dominated the
  // HUD in close combat. The same geometry is rasterised once per shape (white, soft
  // edges matching the HUD line profile) and tinted at draw time: one draw per symbol.
  private const int CachePixelsPerUnit=2;
  private const float CacheExtent=24f; // symbol geometry fits in +-21 units
  private static readonly System.Collections.Generic.Dictionary<int,Texture2D> cache=new();
  private static int CacheKey(TrackAffiliation affiliation,bool missile)=>(missile?8:0)+(int)affiliation;

  /// <summary>Rasterise every shape up front (load time) so no combat frame pays for it.</summary>
  public static void WarmCache()
  {
   foreach(TrackAffiliation affiliation in Enum.GetValues(typeof(TrackAffiliation)))
    foreach(bool missile in new[]{false,true})
    {int key=CacheKey(affiliation,missile);if(!cache.TryGetValue(key,out var t)||t==null)cache[key]=Rasterize(affiliation,missile);}
  }

  public static void DrawCached(Vector2 center,TrackAffiliation affiliation,bool missile,Color color,float scale=1)
  {
   int key=CacheKey(affiliation,missile);
   if(!cache.TryGetValue(key,out var texture)||texture==null)cache[key]=texture=Rasterize(affiliation,missile);
   float size=CacheExtent*2*scale;
   var previous=GUI.color;GUI.color=new Color(color.r*previous.r,color.g*previous.g,color.b*previous.b,color.a*previous.a);
   GUI.DrawTexture(new Rect(center.x-size*.5f,center.y-size*.5f,size,size),texture,ScaleMode.StretchToFill,true);
   GUI.color=previous;
  }

  private static Texture2D Rasterize(TrackAffiliation affiliation,bool missile)
  {
   var segments=new System.Collections.Generic.List<(Vector2 a,Vector2 b,float width)>();
   Draw((a,b,_,w)=>segments.Add((a,b,w)),Vector2.zero,affiliation,missile,Color.white,1);
   int size=Mathf.CeilToInt(CacheExtent*2*CachePixelsPerUnit);var pixels=new Color32[size*size];
   for(int y=0;y<size;y++)for(int x=0;x<size;x++)
   {
    // GUI space: +y is down, texture row 0 is the bottom.
    var p=new Vector2((x+.5f)/CachePixelsPerUnit-CacheExtent,CacheExtent-(y+.5f)/CachePixelsPerUnit);
    float alpha=0;
    foreach(var s in segments)
    {
     // Cheap reject: outside the segment's padded bounding box.
     float pad=s.width*.5f+1.5f;
     if(p.x<Mathf.Min(s.a.x,s.b.x)-pad||p.x>Mathf.Max(s.a.x,s.b.x)+pad||p.y<Mathf.Min(s.a.y,s.b.y)-pad||p.y>Mathf.Max(s.a.y,s.b.y)+pad)continue;
     var ab=s.b-s.a;float t=ab.sqrMagnitude>1e-6f?Mathf.Clamp01(Vector2.Dot(p-s.a,ab)/ab.sqrMagnitude):0;
     float d=Vector2.Distance(p,s.a+ab*t),half=s.width*.5f;
     // Same cross-section as the HUD soft line: solid core, ~1 unit feathered edge.
     alpha=Mathf.Max(alpha,d<=half?1:Mathf.Clamp01(1-(d-half)/1.2f)*.62f);
    }
    pixels[y*size+x]=new Color32(255,255,255,(byte)(alpha*255));
   }
   var texture=new Texture2D(size,size,TextureFormat.RGBA32,false,true){name="Tactical Symbol "+affiliation+(missile?" Missile":""),filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp,hideFlags=HideFlags.HideAndDontSave};
   texture.SetPixels32(pixels);texture.Apply(false,true);return texture;
  }

  public static void Selection(Action<Vector2,Vector2,Color,float> line,Vector2 center,float scale=1)
  {
   for(int x=-1;x<=1;x+=2)for(int y=-1;y<=1;y+=2){var p=new Vector2(x*20,y*20);Segment(line,center,scale,p,p-new Vector2(x*7,0),Unknown,1.5f);Segment(line,center,scale,p,p-new Vector2(0,y*7),Unknown,1.5f);}
  }
 }
}
