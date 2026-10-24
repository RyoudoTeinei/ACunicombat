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
  public static void Selection(Action<Vector2,Vector2,Color,float> line,Vector2 center,float scale=1)
  {
   for(int x=-1;x<=1;x+=2)for(int y=-1;y<=1;y+=2){var p=new Vector2(x*20,y*20);Segment(line,center,scale,p,p-new Vector2(x*7,0),Unknown,1.5f);Segment(line,center,scale,p,p-new Vector2(0,y*7),Unknown,1.5f);}
  }
 }
}
