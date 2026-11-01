using System;
using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;
using UnityEngine;
namespace ACunicombat.Mechs
{
 // L2 formation layer: slot role, keeping style and banded station keeping
 // (after Sea Power's MovingInFormation). Built only from received leader
 // reports and the member's own loadout; nothing here reads another body.
 public sealed partial class MechFleetController
 {
  public MechStationRole StationRole {get;private set;}
  public MechStationRole ForcedStationRole {get;private set;}
  public MechStationBand StationBand {get;private set;}=MechStationBand.Far;
  public MechFormationStyle FormationStyle=>styleOverride??MechFormationDoctrine.DefaultStyle(TaskGroup);
  private MechFormationStyle? styleOverride;
  private double nextRoleRefresh;
  /// <summary>Force a slot role (commander override). Default restores automatic assignment.</summary>
  public void ForceStationRole(MechStationRole role){ForcedStationRole=role;nextRoleRefresh=0;}
  public void OverrideFormationStyle(MechFormationStyle? style)=>styleOverride=style;
  private float StyledSpacing=>FormationSpacing*MechFormationDoctrine.Parameters(FormationStyle).SpacingScale;

  private void RefreshStationRole(double now)
  {
   if(now<nextRoleRefresh)return;nextRoleRefresh=now+2;
   bool heavy=false,passiveRf=false;
   if(weapons!=null)
   {
    weapons.CopyEquipmentWeaponSnapshots(equipment);
    foreach(var e in equipment)
    {
     if(!e.Enabled||e.Rounds<=0&&e.Magazines<=1)continue;
     if(e.Role==MechWeaponRole.AirToAirMissile||e.Role==MechWeaponRole.HandCannon)heavy=true;
     if(weapons.SupportsPassiveRfHoming(e.SlotId))passiveRf=true;
    }
   }
   bool jammer=GetComponent<MechElectronicWarfareController>()!=null;
   StationRole=MechFormationDoctrine.ResolveRole(TaskGroup,State.FormationIndex,State.Leader==MemberId,heavy,passiveRf,jammer,ForcedStationRole);
  }

  // The AI's own engagement target is reported in heartbeats so members can
  // deconflict shooters without any new packet type.
  private TrackSnapshot reportedEngagement;private double reportedEngagementTime=double.NegativeInfinity;
  public void ReportEngagementTarget(in TrackSnapshot track){if(radar==null)return;reportedEngagement=track;reportedEngagementTime=radar.SimTimeSeconds;}
  public void ClearEngagementTarget()=>reportedEngagementTime=double.NegativeInfinity;
  private bool HasReportedEngagement(double now)=>now-reportedEngagementTime<1.5&&reportedEngagement.TrackId.IsValid;
  private readonly TrackSnapshot[] yieldScratch=new TrackSnapshot[1];

  /// <summary>
  /// True when a fresh formation member reports engaging this same contact from
  /// meaningfully closer (Sea Power's CloserAlliedShooterHolds). Ordered tasks
  /// never yield: the commander's assignment wins over local deconfliction.
  /// </summary>
  public bool FriendlyHoldsTarget(in TrackSnapshot track,Vector3 predictedPosition)
  {
   if(!Configured||radar==null||HasTask)return false;
   double now=radar.SimTimeSeconds;float own=Vector3.Distance(transform.position,predictedPosition);
   yieldScratch[0]=track;
   foreach(var pair in State.Members)
   {
    var m=pair.Value;if(pair.Key==MemberId||!State.Fresh(m,now)||!m.Available||m.IsPlayer||!m.Target.TrackId.IsValid)continue;
    if(MechFleetContactMatcher.Match(m.Target,yieldScratch,1,now)!=0)continue;
    float friend=Vector3.Distance((Vector3)(float3)(m.Position+m.Velocity*Math.Max(0,now-m.ObservedAt)),predictedPosition);
    if(MechFormationDoctrine.ShouldYieldTarget(own,friend,State.FormationIndex,m.FormationIndex))return true;
   }
   return false;
  }

  /// <summary>Banded station-keeping frame toward the slot (moving, leader-relative).</summary>
  private MechPlayerInputFrame KeepStation(Vector3 slot,Vector3 slotVelocity,double now,out Vector3 direction)
  {
   var p=MechFormationDoctrine.Parameters(FormationStyle);
   var forward=slotVelocity.sqrMagnitude>100?slotVelocity.normalized:(motion.LogicalRotation*Vector3.forward);
   var right=Vector3.Cross(Vector3.up,forward);if(right.sqrMagnitude<.001f)right=Vector3.right;right.Normalize();
   slot+=right*MechFormationDoctrine.WeaveOffset(in p,(float)now,State.FormationIndex);
   var error=slot-transform.position;
   StationBand=MechFormationDoctrine.ResolveBand(error.magnitude,(motion.Velocity-slotVelocity).magnitude,StationBand,in p);
   direction=StationBand==MechStationBand.OnStation?forward:(error.sqrMagnitude>1?error.normalized:forward);
   float toward=motion.EstimateTranslationAcceleration(error.sqrMagnitude>1?error.normalized:forward);
   float away=motion.EstimateTranslationAcceleration(error.sqrMagnitude>1?-error.normalized:-forward);
   var acceleration=MechFormationDoctrine.StationAcceleration(error,slotVelocity,motion.Velocity,StationBand,in p,away,Mathf.Max(toward,away,.1f));
   bool boost=p.BoostToRejoin&&StationBand==MechStationBand.Far&&error.magnitude>p.JoinRadius*1.5f&&motion.CanQuickBoost&&motion.ManeuverEnergyNormalized>.6f;
   return MechAutonomousFlight.Acceleration(motion,acceleration,boost);
  }
 }
}
