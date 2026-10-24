using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using UnityEngine;
namespace ACunicombat.Mechs
{
 public sealed partial class MechRadarHudPresenter
 {
  private const float TacticalSize=400, TacticalForwardPixels=258;
  private static readonly Vector2 TacticalOwn=new Vector2(200,282);
  private static readonly Color TacticalFriend=MechTacticalSymbols.Friendly;
  private static readonly Color TacticalHostile=MechTacticalSymbols.Hostile;
  private readonly EmissionEvent[] tacticalEmissions=new EmissionEvent[64];
  private readonly Dictionary<TrackId,MechBattlefieldMap.ObservedTrail> tacticalTrails=new();
  private readonly HashSet<TrackId> tacticalPresent=new();
  private readonly List<TrackId> tacticalExpired=new();
  private float tacticalRangeMeters=100000;
  private Vector2 TacticalPoint(Vector3 world)
  {
   var local=ToHeadingLocal(world-transform.position);
   return TacticalOwn+new Vector2(local.x,-local.z)*(TacticalForwardPixels/tacticalRangeMeters);
  }
  private void UpdateCombinedTacticalState()
  {
   double now=radar.SimTimeSeconds;tacticalPresent.Clear();
   for(int i=0;i<trackCount;i++)
   {
    var t=tracks[i];if(!ShouldDisplayTrack(i)||t.Lifecycle==TrackLifecycle.Dropped||IsMissileTrack(in t))continue;
    var p=radar.PredictTrackPosition(in t);
    tacticalPresent.Add(t.TrackId);
    if(!tacticalTrails.TryGetValue(t.TrackId,out var trail)){trail=new MechBattlefieldMap.ObservedTrail();tacticalTrails.Add(t.TrackId,trail);}
    trail.Observe(p,t.LastUpdateTime,now);
   }
   tacticalExpired.Clear();foreach(var id in tacticalTrails.Keys)if(!tacticalPresent.Contains(id))tacticalExpired.Add(id);
   foreach(var id in tacticalExpired)tacticalTrails.Remove(id);
   // Only an actual STT lock may expand this local display. TWS contacts and candidates do not.
   bool locked=radar.HasStt&&radar.TryGetTrack(radar.SttTrackId,out _);
   float distance=0;
   if(locked&&radar.TryGetTrack(radar.SttTrackId,out var target))distance=Vector3.Distance(radar.PredictTrackPosition(in target),transform.position);
   tacticalRangeMeters=ResolveTacticalDisplayRange(locked,distance);
  }
  public static float ResolveTacticalDisplayRange(bool locked,float distanceMeters) =>
   locked&&float.IsFinite(distanceMeters)&&distanceMeters>100000?distanceMeters*1.1f:100000;

  private static readonly System.Action<Vector2,Vector2,Color,float> TacticalSymbolLine=(a,b,c,w)=>TacticalLine(a,b,c,w);
  private static void TacticalLabel(Rect rect,string text,GUIStyle style)
  {rect.position+=new Vector2(18,18);GUI.Label(rect,text,style);}
  private static bool ClipTacticalAxis(float p,float q,ref float from,ref float to)
  {
   if(Mathf.Abs(p)<.00001f)return q>=0;
   float t=q/p;if(p<0){if(t>to)return false;from=Mathf.Max(from,t);}else{if(t<from)return false;to=Mathf.Min(to,t);}return true;
  }
  private static void TacticalLine(Vector2 a,Vector2 b,Color color,float width,bool dashed=false)
  {
   var delta=b-a;float from=0,to=1;
   if(!ClipTacticalAxis(-delta.x,a.x-1,ref from,ref to)||!ClipTacticalAxis(delta.x,399-a.x,ref from,ref to)||!ClipTacticalAxis(-delta.y,a.y-1,ref from,ref to)||!ClipTacticalAxis(delta.y,399-a.y,ref from,ref to))return;
   DrawLine(a+delta*from+new Vector2(18,18),a+delta*to+new Vector2(18,18),color,width,dashed);
  }
  private static void TacticalBeamLine(Vector2 start,Vector2 end,Color color)
  {
   var offset=start-TacticalOwn;var delta=end-start;float length=delta.magnitude;
   const float clearance=12;
   if(length<.001f)return;
   if(offset.sqrMagnitude<clearance*clearance)
   {
    var direction=delta/length;float b=Vector2.Dot(offset,direction);
    float exit=-b+Mathf.Sqrt(Mathf.Max(0,b*b+clearance*clearance-offset.sqrMagnitude));
    if(exit>=length)return;start+=direction*exit;
   }
   TacticalLine(start,end,color,.8f);
  }
  private Texture2D tacticalSectorTexture;
  private float tacticalSectorAngle=-1,tacticalSectorReach=-1;
  private void DrawTacticalSector(Rect bounds,Color tint)
  {
   float halfAngle=Mathf.Clamp(radarStatus.ScanAzimuthDegrees*.5f,1,89);
   float reach=Mathf.Min(TacticalForwardPixels,Mathf.Max(0,radarStatus.RangeMeters)/tacticalRangeMeters*TacticalForwardPixels);
   if(tacticalSectorTexture==null||Mathf.Abs(halfAngle-tacticalSectorAngle)>.1f||Mathf.Abs(reach-tacticalSectorReach)>.5f)
   {
    if(tacticalSectorTexture!=null)UnityEngine.Object.Destroy(tacticalSectorTexture);
    const int size=400;var pixels=new Color32[size*size];
    for(int y=0;y<size;y++)for(int x=0;x<size;x++)
    {
     int inside=0;
     for(int sy=0;sy<2;sy++)for(int sx=0;sx<2;sx++)
     {
      float dx=x+(sx+.5f)/2-TacticalOwn.x,dy=size-y-(sy+.5f)/2-TacticalOwn.y;
      if(dx*dx+dy*dy<=reach*reach&&Mathf.Abs(Mathf.Atan2(dx,-dy)*Mathf.Rad2Deg)<=halfAngle)inside++;
     }
     pixels[y*size+x]=new Color(.12f,.48f,.22f,.065f*inside/4);
    }
    tacticalSectorTexture=new Texture2D(size,size,TextureFormat.RGBA32,false,true){name="Uniform Tactical Sector",filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp,hideFlags=HideFlags.HideAndDontSave};
    tacticalSectorTexture.SetPixels32(pixels);tacticalSectorTexture.Apply(false,true);tacticalSectorAngle=halfAngle;tacticalSectorReach=reach;
   }
   GUI.color=tint;GUI.DrawTexture(bounds,tacticalSectorTexture,ScaleMode.StretchToFill,true);
   var outline=new Color(.18f,.65f,.3f,.6f);
   TacticalLine(TacticalOwn+RadarDirection(-halfAngle)*12,TacticalOwn+RadarDirection(-halfAngle)*reach,outline,1);
   TacticalLine(TacticalOwn+RadarDirection(halfAngle)*12,TacticalOwn+RadarDirection(halfAngle)*reach,outline,1);
   var previous=TacticalOwn+RadarDirection(-halfAngle)*reach;
   for(float angle=-halfAngle+1;angle<=halfAngle+1;angle+=1)
   {var next=TacticalOwn+RadarDirection(Mathf.Min(angle,halfAngle))*reach;TacticalLine(previous,next,outline,1);previous=next;}
  }
  private void DrawCombinedTacticalRadar(bool showTracks,bool showScan)
  {
   var tint=GUI.color;var bounds=new Rect(18,18,TacticalSize,TacticalSize);

   GUI.color=MultiplyGuiTint(new Color(.016f,.042f,.024f,.94f),tint);GUI.DrawTexture(bounds,Texture2D.whiteTexture);GUI.color=tint;
   if(showScan)DrawTacticalSector(bounds,tint);
   var frame=new Color(.18f,.65f,.3f,.75f);var grid=new Color(.12f,.46f,.21f,.45f);
   // Real world XZ lines, projected by the same heading transform as contacts.
   float step=25000,extent=tacticalRangeMeters*2;
   int count=Mathf.CeilToInt(extent/step);count=Mathf.Min(count,64);
   float x0=Mathf.Floor(transform.position.x/step)*step,z0=Mathf.Floor(transform.position.z/step)*step,y=transform.position.y;
   for(int i=-count;i<=count;i++)
   {
    float x=x0+i*step,z=z0+i*step;
    TacticalLine(TacticalPoint(new Vector3(x,y,z0-extent)),TacticalPoint(new Vector3(x,y,z0+extent)),grid,1);
    TacticalLine(TacticalPoint(new Vector3(x0-extent,y,z)),TacticalPoint(new Vector3(x0+extent,y,z)),grid,1);
   }
   if(showScan)
   {
    int n=radar.CopyOwnRadarEmissions(tacticalEmissions);
    for(int i=0;i<n;i++)
    {
     var e=tacticalEmissions[i];if(!MechBattlefieldMap.IsVisibleRadarEmission(e,radar.SimTimeSeconds)||!double.IsFinite(e.MaxRangeMeters)||e.MaxRangeMeters<=0)continue;
     var direction=((Vector3)e.Direction).normalized;
     var across=Vector3.Cross(direction,Mathf.Abs(direction.y)<.9f?Vector3.up:Vector3.right).normalized;
     var vertical=Vector3.Cross(across,direction).normalized;
     var start=TacticalPoint(ToVector3(e.Origin));
     float half=Mathf.Min(e.BeamWidth*.5f,Mathf.PI*.49f);
     float fade=1-Mathf.Clamp01((float)(radar.SimTimeSeconds-e.EndTime)/.25f);
     var beam=new Color(.24f,.68f,.34f,.36f*fade);
     // Identical event geometry to the M map: real origin, direction, beam width and world range.
     // Only the viewport clips the ray; the sector backdrop contains no decorative rays or inner range rings.
     for(int side=0;side<12;side++)
     {
      float angle=side*Mathf.PI*2/12;
      var edge=direction*Mathf.Cos(half)+(across*Mathf.Cos(angle)+vertical*Mathf.Sin(angle))*Mathf.Sin(half);
      TacticalBeamLine(start,TacticalPoint(MechBattlefieldMap.RadarBeamEndpoint(e,edge)),new Color(beam.r,beam.g,beam.b,beam.a*.16f));
     }
     TacticalBeamLine(start,TacticalPoint(MechBattlefieldMap.RadarBeamEndpoint(e,direction)),beam);
    }
   }
   if(showTracks)
   {
    for(int i=0;i<trackCount;i++)
    {
     var t=tracks[i];if(!ShouldDisplayTrack(i)||t.Lifecycle==TrackLifecycle.Dropped||IsMissileTrack(in t))continue;
     var world=radar.PredictTrackPosition(in t);var point=TacticalPoint(world);
     if(!new Rect(12,20,376,358).Contains(point))continue;
     var color=t.Affiliation==TrackAffiliation.Friendly?TacticalFriend:t.Affiliation==TrackAffiliation.Hostile?TacticalHostile:MechTacticalSymbols.Unknown;
     color.a=GetLifecycleAlpha(in t);
     if(tacticalTrails.TryGetValue(t.TrackId,out var trail))
     {
      var last=point;float remaining=28;
      for(int j=trail.Samples.Count-1;j>=0&&remaining>0;j--)
      {
       var next=TacticalPoint(trail.Samples[j].Position);var delta=next-last;float length=Mathf.Min(delta.magnitude,remaining);
       if(length>.1f){TacticalLine(last,last+delta.normalized*length,new Color(color.r,color.g,color.b,color.a*.4f),1,true);remaining-=length;}last=next;
      }
     }
     MechTacticalSymbols.Draw(TacticalSymbolLine,point,t.Affiliation,false,color,.7f);
     var v=ToHeadingLocal(ToVector3(t.Velocity));var heading=new Vector2(v.x,-v.z);
     if(heading.magnitude>1)TacticalLine(point+heading.normalized*8,point+heading.normalized*20,color,1);
     bool selected=t.TrackId.Equals(radar.SttTrackId)||t.TrackId.Equals(radar.CandidateTrackId);
     if(selected)MechTacticalSymbols.Selection(TacticalSymbolLine,point,.7f);
     var rel=world-transform.position;string name=MechTrackDisplayNames.HighlightHostile(MechTrackDisplayNames.For(radar).Label(t));
     float altitude=Vector3.Dot(rel,GetReferenceUp());if(Mathf.Abs(altitude)>100)DrawAltitudeCue(point+new Vector2(8,18),altitude>0,color);
     microStyle.alignment=TextAnchor.UpperLeft;microStyle.normal.textColor=color;
     TacticalLabel(ClampToRect(new Rect(point.x+10,point.y-8,95,selected?32:18),new Rect(8,20,384,358),0),selected?name+"\n"+(rel.magnitude/1000).ToString("0.0")+" km":name,microStyle);
    }
    int n=radar.CopyMissileTracks(missileTracks);int closest=-1;float closestDistance=float.PositiveInfinity;
    for(int i=0;i<n;i++)if(ShouldDisplayIncomingMissile(in missileTracks[i])&&missileTracks[i].IsLikelyTargetingOwnship)
    {float distance=Vector3.Distance(ToVector3(missileTracks[i].PredictPosition(radar.SimTimeSeconds)),transform.position);if(distance<closestDistance){closest=i;closestDistance=distance;}}
    for(int i=0;i<n;i++)
    {
     var t=missileTracks[i];if(!ShouldDisplayFriendlyMissile(in t)&&!ShouldDisplayIncomingMissile(in t))continue;
     var point=TacticalPoint(ToVector3(t.PredictPosition(radar.SimTimeSeconds)));if(!new Rect(12,20,376,358).Contains(point))continue;
     var color=t.IsFriendly?TacticalFriend:t.IsLikelyTargetingOwnship||t.Affiliation==TrackAffiliation.Hostile?TacticalHostile:MechTacticalSymbols.Unknown;
     MechTacticalSymbols.Draw(TacticalSymbolLine,point,t.IsFriendly?TrackAffiliation.Friendly:t.Affiliation,true,color,.56f);
     if(i==closest){microStyle.normal.textColor=color;microStyle.alignment=TextAnchor.UpperLeft;TacticalLabel(ClampToRect(new Rect(point.x+9,point.y+1,70,16),new Rect(8,20,384,358),0),(closestDistance/1000).ToString("0.0")+" km",microStyle);}
    }
   }
   MechTacticalSymbols.Draw(TacticalSymbolLine,TacticalOwn,TrackAffiliation.Friendly,false,MechTacticalSymbols.Own,.7f);TacticalLine(TacticalOwn+Vector2.up*8,TacticalOwn+Vector2.up*15,HudBrightGreen,1.5f);
   TacticalLine(new Vector2(1,1),new Vector2(399,1),frame,1);TacticalLine(new Vector2(1,1),new Vector2(1,399),frame,1);
   TacticalLine(new Vector2(399,1),new Vector2(399,399),frame,1);TacticalLine(new Vector2(1,399),new Vector2(399,399),frame,1);
   microStyle.alignment=TextAnchor.UpperRight;microStyle.normal.textColor=HudGreen;TacticalLabel(new Rect(290,378,98,18),(tacticalRangeMeters/1000).ToString("0.#")+" km",microStyle);
   GUI.color=tint;
  }
 }
}
