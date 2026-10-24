using System;
using ACunicombat.Sensing.Contracts;
using UnityEngine;
using Unity.Mathematics;
namespace ACunicombat.Mechs
{
 public sealed partial class MechFleetController
 {
  private double nextAttackPlan;private Guid planOrder;private TrackId planTrack;
  private Vector3 attackPlanPoint;private float attackPlanReach;private bool attackPlanValid;
  public bool TryAttackNavigation(in TrackSnapshot track,out MechPlayerInputFrame frame,out Vector3 point)
  {
   frame=default;point=default;
   if(!HasTask||State.Task.Phase==FleetTaskPhase.Returning||State.Task.Order.Order!=FleetOrderKind.Attack&&State.Task.Order.Order!=FleetOrderKind.AttackWithWeapon||motion==null||weapons==null)return false;
   if(State.Task.Order.AutomaticEngagement)
   {
    point=transform.position;frame.HasAutonomousFlightCommand=true;frame.AutonomousAccelerationWorld=Vector3.ClampMagnitude(-motion.Velocity/2,30);
    if(TryNavigation(out _,out var formationFrame))frame=formationFrame;
    attackPlanValid=false;return true;
   }
   double now=radar.SimTimeSeconds;
   if(now>=nextAttackPlan||planOrder!=State.Task.Order.Operation||!planTrack.Equals(track.TrackId))
   {
    nextAttackPlan=now+2;planOrder=State.Task.Order.Operation;planTrack=track.TrackId;attackPlanValid=false;
    var target=radar.PredictTrackPosition(track);float score=float.NegativeInfinity;
    weapons.CopyEquipmentWeaponSnapshots(equipment);
    foreach(var e in equipment)
    {
     if(!e.Enabled||!State.AllowsWeapon(e.InstanceId)||!weapons.TryGetWeaponSlotStatus(new MechEquipmentSlot(e.SlotId),out var status)||!status.HasReserveAmmunition||!MechAiRulePlanner.IsOffensiveRole(status.Role))continue;
     if(!AllowsMissionEquipment(e.SlotId,status.Role))continue;
     float reach=MechEngagementEstimate.Reach(transform.position,motion.Velocity,target,(Vector3)(float3)track.Velocity,status.EffectiveRangeMeters,status.MaximumRangeMeters,weapons.EstimatedProjectileSpeed(e.SlotId));
     if(reach<=status.MinimumRangeMeters)continue;
     float candidate=MechAiRulePlanner.ScoreWeapon(in status,Vector3.Distance(transform.position,target));
     // Favor a weapon that can engage without a long approach.
     candidate-=Mathf.Max(0,Vector3.Distance(transform.position,target)-reach)/1000f;
     if(candidate<=score)continue;score=candidate;attackPlanReach=reach;attackPlanPoint=MechEngagementEstimate.FiringPoint(transform.position,target,reach,status.MinimumRangeMeters);attackPlanValid=true;
    }
   }
   if(!attackPlanValid)return false;
   point=attackPlanPoint;var desired=Vector3.ClampMagnitude((point-transform.position)*.15f,State.CruiseSpeed);
   frame.HasAutonomousFlightCommand=true;frame.AutonomousAccelerationWorld=Vector3.ClampMagnitude((desired-motion.Velocity)/2,30);return true;
  }
 }
}
