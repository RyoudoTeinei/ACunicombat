using System;
using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;
using UnityEngine;
namespace ACunicombat.Mechs
{
 // L3 mission layer for Strike: assemble -> ingress -> attack -> egress -> rejoin.
 // Each member evaluates the same received reports, so phases agree without
 // new packet types. Attackers prosecute the mission target; escorts screen.
 public sealed partial class MechFleetController
 {
  private readonly MechStrikeMission strike=new();
  private Guid strikeOperation;
  public MechStrikePhase StrikePhase=>strike.Phase;
  public bool HasStrikeMission=>HasSpatialMission&&State.Task.Order.TaskGroup==FleetTaskGroup.Strike;
  private bool IsStrikeAttacker=>StationRole!=MechStationRole.Escort;

  private void UpdateStrikeMission(double now)
  {
   if(!HasStrikeMission){strikeOperation=Guid.Empty;return;}
   if(strikeOperation!=State.Task.Order.Operation){strikeOperation=State.Task.Order.Operation;strike.Reset((float)now);}
   var inputs=new MechStrikeInputs{Now=(float)now,AttackRangeMeters=MaximumOffensiveRange(),PackagePosition=PackagePosition(now)};
   if(TryMissionAirTarget(out var track)){inputs.TargetObserved=true;inputs.TargetPosition=radar.PredictTrackPosition(track);}
   else if(State.Task.Order.Target.TrackId.IsValid&&now-State.Task.Order.Target.LastUpdateTime<3){inputs.TargetObserved=true;inputs.TargetPosition=(Vector3)(float3)State.Task.Order.Target.PredictPosition(now);}
   CountPackage(now,out inputs.MembersFresh,out inputs.MembersOnStation);
   var before=strike.Phase;
   strike.Update(in inputs);
   if(strike.Phase!=before)Log("strike_phase",State.Task.Order,before+"->"+strike.Phase);
   if(strike.Phase==MechStrikePhase.Complete)State.SetPhase(FleetTaskPhase.Completed,FleetTaskReason.EngagementCompleted,now);
  }

  private Vector3 PackagePosition(double now)
  {
   if(IsActingLeader||!State.Members.TryGetValue(ActiveLeader,out var leader)||!State.Fresh(leader,now))return transform.position;
   return (Vector3)(float3)(leader.Position+leader.Velocity*Math.Max(0,now-leader.ObservedAt));
  }

  private void CountPackage(double now,out int fresh,out int onStation)
  {
   fresh=1;onStation=StationBand==MechStationBand.OnStation||IsActingLeader?1:0;
   var leaderPosition=PackagePosition(now);float join=MechFormationDoctrine.Parameters(FormationStyle).JoinRadius;
   foreach(var pair in State.Members)
   {
    if(pair.Key==MemberId)continue;var m=pair.Value;
    if(!State.Fresh(m,now)||!m.Available||m.FormationRoot!=State.FormationRoot)continue;
    fresh++;
    var position=(Vector3)(float3)(m.Position+m.Velocity*Math.Max(0,now-m.ObservedAt));
    if(pair.Key==ActiveLeader||Vector3.Distance(position,leaderPosition)<=join)onStation++;
   }
  }

  /// <summary>Phase navigation point. False lets the unit brain fly combat geometry.</summary>
  private bool TryStrikeNavigation(out Vector3 point,out Vector3 velocity)
  {
   point=transform.position;velocity=Vector3.zero;double now=radar.SimTimeSeconds;
   var target=State.Task.Order.Target.TrackId.IsValid?(Vector3)(float3)State.Task.Order.Target.PredictPosition(now):transform.position;
   if(TryMissionAirTarget(out var observed))target=radar.PredictTrackPosition(observed);
   float reach=Mathf.Max(500,MaximumOffensiveRange());
   switch(strike.Phase)
   {
    case MechStrikePhase.Assemble:
    case MechStrikePhase.Rejoin:
     if(IsActingLeader){point=transform.position;return true;}
     return TryFormationPoint(now,out point,out velocity);
    case MechStrikePhase.Ingress:
     if(IsActingLeader){point=MechStrikeMission.IngressPoint(transform.position,target,reach);return true;}
     return TryFormationPoint(now,out point,out velocity);
    case MechStrikePhase.Attack:
     if(IsStrikeAttacker)return false;
     var package=PackagePosition(now);
     var threat=TryDefenseLook(out var danger)?danger:target-package;
     point=MechStrikeMission.EscortCoverPoint(package,threat,StyledSpacing,State.FormationIndex);return true;
    case MechStrikePhase.Egress:
     point=MechStrikeMission.EgressPoint(transform.position,target,reach);return true;
    default:
     return false;
   }
  }

  /// <summary>Strike phases that must override the unit brain's own combat geometry.</summary>
  public bool StrikeRequiresNavigation=>HasStrikeMission&&strike.Phase!=MechStrikePhase.Attack&&strike.Phase!=MechStrikePhase.Complete;

  /// <summary>Escorts never prosecute the strike target; attackers only once the package is in.</summary>
  private bool StrikeAllowsMissionTarget=>!HasStrikeMission||strike.Phase==MechStrikePhase.Attack&&IsStrikeAttacker;

  /// <summary>
  /// A strike escort may pick its own air targets (screening) once the package
  /// is attacking: any hostile platform except the strike target itself.
  /// </summary>
  public bool AllowsScreeningEngagement=>HasStrikeMission&&!IsStrikeAttacker&&strike.Phase==MechStrikePhase.Attack;
  public bool IsStrikeTarget(TrackId id)=>HasStrikeMission&&TryMissionAirTarget(out var t)&&t.TrackId.Equals(id);
 }
}
