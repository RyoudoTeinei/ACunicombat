using System;
using ACunicombat.Sensing.Contracts;
using UnityEngine;
using Unity.Mathematics;
namespace ACunicombat.Mechs
{
 public sealed partial class MechBattlefieldMap
 {
  private bool speedMenuOpen;private Rect speedMenuAnchor,speedMenuBounds;private Guid speedMenuMember;
  private double nextPreview;private string previewOwner,previewTarget;private Guid previewWeapon;
  private Vector3 previewPoint;private float previewReach;private bool previewApproach;
  private void DrawCruiseMenu()
  {
   if(!speedMenuOpen)return;
   speedMenuBounds=new Rect(speedMenuAnchor.x,speedMenuAnchor.y-252,180,246);MenuPanel(speedMenuBounds);
   Line(new Vector2(speedMenuBounds.x+12,speedMenuBounds.y+20),new Vector2(speedMenuBounds.x+12,speedMenuBounds.yMax-20),PrimaryText,2);
   float[] gears={800,500,300,200,100,0};
   for(int i=0;i<gears.Length;i++){float speed=gears[i];MenuItem(new Rect(speedMenuBounds.x+22,speedMenuBounds.y+6+i*39,156,39),speed==0?"停车":"前进 "+speed+" m/s",()=>{TrackMapOperation(commandFleet.RequestCruiseSpeed(speedMenuMember,speed));speedMenuOpen=false;});}
  }
  private void DrawAttackPreview(Contact owner)
  {
   if(contextOpen||!InPlot(mapPointer)||!ContactAt(mapPointer,out var enemy)||enemy.Own||enemy.Affiliation!=TrackAffiliation.Hostile)return;
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
