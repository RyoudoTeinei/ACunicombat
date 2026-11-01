using System;
using ACunicombat.Sensing.Contracts;
using UnityEngine;
using Unity.Mathematics;
namespace ACunicombat.Mechs
{
 public sealed partial class MechBattlefieldMap
 {
  private bool speedMenuOpen,speedCustomEditing;private Rect speedMenuAnchor,speedMenuBounds;private Guid speedMenuMember;
  private string speedCustomText=MechFleetState.DefaultCruiseSpeed.ToString("0");
  private double nextPreview;private string previewOwner,previewTarget;private Guid previewWeapon;
  private Vector3 previewPoint;private float previewReach;private bool previewApproach;
  private void RequestCruiseSpeed(float speed)
  {
   TrackMapOperation(commandFleet.RequestCruiseSpeed(speedMenuMember,speed));speedMenuOpen=false;
  }
  private void DrawCruiseMenu()
  {
   if(!speedMenuOpen)return;
   speedMenuBounds=new Rect(speedMenuAnchor.x,speedMenuAnchor.y-214,190,208);MenuPanel(speedMenuBounds,Background);
   Line(new Vector2(speedMenuBounds.x+12,speedMenuBounds.y+20),new Vector2(speedMenuBounds.x+12,speedMenuBounds.yMax-20),PrimaryText,2);
   string[] captions={"设定航速","全速","巡航速度","1/2速度","停车"};
   float[] speeds={-1,MechFleetState.MaxCruiseSpeed,MechFleetState.DefaultCruiseSpeed,MechFleetState.MaxCruiseSpeed*.5f,0};
   for(int i=0;i<captions.Length;i++)
   {
    float speed=speeds[i];var bounds=new Rect(speedMenuBounds.x+22,speedMenuBounds.y+6+i*39,156,39);
    if(speed<0)MenuItem(bounds,captions[i],()=>{speedCustomEditing=true;speedMenuOpen=false;});
    else MenuItem(bounds,captions[i],()=>RequestCruiseSpeed(speed));
   }
  }
  private void DrawCruiseSpeedEditor()
  {
   if(!speedCustomEditing)return;
   var matrix=GUI.matrix;var tint=GUI.color;int depth=GUI.depth;
   GUI.matrix=Matrix4x4.Scale(Vector3.one*(Screen.height/(float)H));GUI.depth=-4002;
   var box=new Rect((layoutWidth-380)/2,(H-190)/2,380,190);
   GUI.color=new Color(.02f,.04f,.045f,.90f);GUI.DrawTexture(box,Texture2D.whiteTexture);
   GUI.color=Color.white;
   var style=new GUIStyle(GUI.skin.label){font=font,fontSize=16};style.normal.textColor=PrimaryText;
   GUI.Label(new Rect(box.x+20,box.y+16,340,28),"设定航速（0–800 m/s）",style);
   speedCustomText=GUI.TextField(new Rect(box.x+20,box.y+60,340,34),speedCustomText,8);
   if(GUI.Button(new Rect(box.x+190,box.yMax-52,80,32),"取消"))speedCustomEditing=false;
   if(GUI.Button(new Rect(box.x+280,box.yMax-52,80,32),"确认")&&float.TryParse(speedCustomText,out float speed))
   {
    speed=Mathf.Clamp(speed,0,MechFleetState.MaxCruiseSpeed);RequestCruiseSpeed(speed);speedCustomEditing=false;
   }
   GUI.color=tint;GUI.matrix=matrix;GUI.depth=depth;
  }
  public static bool CanPreviewAttackTarget(bool own,TrackAffiliation affiliation,ContactIdentitySnapshot identity)
  {
   if(own||affiliation==TrackAffiliation.Friendly)return false;
   return !identity.Available||identity.CanAttackPlatform;
  }
  private void DrawAttackPreview(Contact owner)
  {
   if(contextOpen||!InPlot(mapPointer)||!ContactAt(mapPointer,out var enemy)||!CanPreviewAttackTarget(enemy.Own,enemy.Affiliation,enemy.Identity))return;
   double now=Time.timeAsDouble;
   if(now>=nextPreview||previewOwner!=owner.Key||previewTarget!=enemy.Key||previewWeapon!=commandWeapon)
   {
    nextPreview=now+2;previewOwner=owner.Key;previewTarget=enemy.Key;previewWeapon=commandWeapon;previewReach=0;
    foreach(var weapon in commandWeapons)
    {
     if(commandWeapon!=Guid.Empty&&weapon.Instance!=commandWeapon||weapon.Ammo<=0)continue;
     if(!enemy.Missile&&!MechAiRulePlanner.IsOffensiveRole(weapon.Role))continue;
     float r=MechEngagementEstimate.Reach(owner.Position,owner.Velocity,enemy.Position,enemy.Velocity,weapon.EffectiveRange,weapon.MaximumRange,weapon.ProjectileSpeed);
     previewReach=Mathf.Max(previewReach,r);
    }
    previewPoint=MechEngagementEstimate.FiringPoint(owner.Position,enemy.Position,previewReach);
    previewApproach=previewReach>0&&Vector3.Distance(owner.Position,enemy.Position)>previewReach;
   }
   if(previewReach<=0)return;
   var yellow=new Color(.85f,.72f,.25f,.95f);
   DrawWorldRange(owner.Position,MechEngagementEstimate.HorizontalRadius(previewReach,enemy.Position.y-owner.Position.y),yellow);
   if(previewApproach){DrawRouteSegment(Project(owner.Position),Project(previewPoint));DrawRouteEndpoint(Project(previewPoint));DrawWorldRange(previewPoint,MechEngagementEstimate.HorizontalRadius(previewReach,enemy.Position.y-previewPoint.y),yellow);}
  }
  private void DrawAttackLinks()
  {
   if(commandFleet==null||!commandFleet.Configured)return;
   foreach(var c in contacts)
   {
    if(!ResolveSelectedMember(c,out _,out var report)||report.Phase==FleetTaskPhase.None||report.Phase==FleetTaskPhase.Completed||report.Phase==FleetTaskPhase.Returning||report.Phase==FleetTaskPhase.Rejected||report.Phase==FleetTaskPhase.Cancelled||report.Phase==FleetTaskPhase.Expired)continue;
    if(report.Order!=FleetOrderKind.Attack&&report.Order!=FleetOrderKind.AttackWithWeapon||!report.Target.TrackId.IsValid)continue;
    var end=(Vector3)(float3)report.Target.PredictPosition(radar.SimTimeSeconds);
    Line(Project(c.Position),Project(end),new Color(.85f,.15f,.12f,.95f),1.3f);
   }
  }
 }
}
