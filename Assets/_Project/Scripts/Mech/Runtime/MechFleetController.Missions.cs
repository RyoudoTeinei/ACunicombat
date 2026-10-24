using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;
using UnityEngine;
namespace ACunicombat.Mechs
{
 public sealed partial class MechFleetController
 {
  private int missionWaypoint;
  public bool HasSpatialMission=>HasTask&&State.Task.Order.Order==FleetOrderKind.AssignMission&&State.Task.Phase!=FleetTaskPhase.Returning;
  public static bool MissionAvailable(FleetTaskGroup group)=>group!=FleetTaskGroup.ElectronicWarfare&&group!=FleetTaskGroup.EarlyWarningCommand&&group!=FleetTaskGroup.Logistics;
  public List<Guid> RequestFormationMission(Guid root,FleetPacket specification)
  {
   var operations=new List<Guid>();if(!CanEditFormation(root)||!MissionAvailable(specification.TaskGroup))return operations;
   foreach(var member in FormationMembers(root))
   {
    var p=specification;p.Kind=FleetPacketKind.Order;p.Order=FleetOrderKind.AssignMission;p.Sender=MemberId;p.Recipient=member.Member;p.Fleet=State.Fleet;
    p.Operation=Guid.NewGuid();p.Revision=++revision;p.ObservedAt=radar.SimTimeSeconds;p.ExpiresAt=p.ObservedAt+30;p.TaskDeadline=p.ObservedAt+3600;p.WholeFleet=true;p.AuthorizedWeaponSelection=IsPlayer;p.Contact=ContactFor(p.Target.TrackId);
    pending[p.Operation]=new Pending{Packet=p,NextRetry=p.ObservedAt+.5};operations.Add(p.Operation);
    if(member.Member==MemberId&&IsPlayer){pending[p.Operation].Acknowledged=true;if(State.ReceiveLocalPlayerOrder(p,p.ObservedAt)){missionWaypoint=0;resolvedOrder=default;UpdateFormationRoster();}}
    else Send(p);
   }
   if(operations.Count>0)for(int i=1;i<operations.Count;i++)delegation[operations[i]]=operations[0];
   return operations;
  }
  public static bool MissionContains(in FleetPacket p,Vector3 world)
  {
   if(p.MissionGeometry!=FleetMissionGeometry.Area)return Vector3.Distance(world,(Vector3)(float3)p.MissionPoint)<=Mathf.Max(5000,p.MissionCellSize*.5f);
   var local=math.mul(math.inverse(p.MissionGridRotation),(float3)((double3)(float3)world-p.MissionGridOrigin));
   int x=(int)math.floor(local.x/p.MissionCellSize),z=(int)math.floor(local.z/p.MissionCellSize);
   foreach(var rect in p.MissionSectors)if(x>=rect.x&&x<=rect.z&&z>=rect.y&&z<=rect.w)return true;
   return false;
  }
  private bool TryMissionProtectedState(out Vector3 position,out Vector3 velocity)
  {
   position=velocity=default;if(!HasSpatialMission)return false;var p=State.Task.Order;double now=radar.SimTimeSeconds;
   int count=0;
   foreach(var pair in State.Members)
   {
    if(pair.Key!=p.MissionProtectedMember&&!(p.MissionProtectFormation&&Roster.TryGetValue(pair.Key,out var entry)&&GroupRoot(entry)==p.MissionProtectedMember))continue;
    var report=pair.Value;if(!State.Fresh(report,now)||!report.Available)continue;
    position+=(Vector3)(float3)(report.Position+report.Velocity*Math.Max(0,now-report.ObservedAt));velocity+=(Vector3)(float3)report.Velocity;count++;
   }
   if(count>0){position/=count;velocity/=count;return true;}
   if(p.Target.TrackId.IsValid&&now-p.Target.LastUpdateTime<3){position=(Vector3)(float3)p.Target.PredictPosition(now);velocity=(Vector3)(float3)p.Target.Velocity;return true;}
   return false;
  }
  private bool TryMissionAirTarget(out TrackSnapshot track)
  {
   track=default;if(!HasSpatialMission)return false;var p=State.Task.Order;
   if(p.TaskGroup!=FleetTaskGroup.Strike&&p.TaskGroup!=FleetTaskGroup.AirSuperiority&&p.TaskGroup!=FleetTaskGroup.AirDefenseSuppression)return false;
   int n=radar.CopyTracks(air);double now=radar.SimTimeSeconds;
   if(p.TaskGroup==FleetTaskGroup.Strike||p.TaskGroup==FleetTaskGroup.AirDefenseSuppression)
   {
    int match=MechFleetContactMatcher.Match(p.Target,air,n,now);if(match<0)return false;track=air[match];
    if(track.Affiliation==TrackAffiliation.Friendly||track.Identity.Available&&!track.Identity.CanAttackPlatform)return false;
   }
   else
   {
    float best=float.PositiveInfinity;bool any=false;
    for(int i=0;i<n;i++)
    {
     var candidate=air[i];if(candidate.ObjectClass==TrackObjectClass.Missile||candidate.ObjectClass==TrackObjectClass.Decoy||candidate.Lifecycle!=TrackLifecycle.Confirmed||now-candidate.LastUpdateTime>3||candidate.Affiliation!=TrackAffiliation.Hostile||candidate.Identity.Available&&!candidate.Identity.CanAttackPlatform)continue;
     var point=radar.PredictTrackPosition(candidate);if(!MissionContains(p,point))continue;
     float score=Vector3.Distance(transform.position,point);if(candidate.TrackId.Equals(resolvedOrder))score*=.8f;
     if(score<best){best=score;track=candidate;any=true;}
    }
    if(!any)return false;
   }
   if(p.TaskGroup==FleetTaskGroup.AirDefenseSuppression)
   {
    if(!HasSuppressionWeapon()){LastDecision="SuppressionWeaponUnavailable";return false;}
    if(!radar.TryAssociatePassiveTarget(track,out _)){LastDecision="SuppressionEmitterUnconfirmedOrSilent";return false;}
   }
   resolvedOrder=track.TrackId;lastSeenOrder=now;return true;
  }
  private bool MissionAllowsOffensiveTarget(TrackId id)
  {
   if(!HasSpatialMission)return true;var p=State.Task.Order;
   return (p.TaskGroup==FleetTaskGroup.Strike||p.TaskGroup==FleetTaskGroup.AirSuperiority||p.TaskGroup==FleetTaskGroup.AirDefenseSuppression)&&id.Equals(resolvedOrder)&&radar.TryGetTrack(id,out var t)&&(p.TaskGroup!=FleetTaskGroup.AirSuperiority||MissionContains(p,radar.PredictTrackPosition(t)));
  }
  public bool AllowsMissionEquipment(string slotId,MechWeaponRole role)
   =>!HasSpatialMission||State.Task.Order.TaskGroup!=FleetTaskGroup.AirDefenseSuppression||IsDefensiveRole(role)||weapons!=null&&weapons.SupportsPassiveRfHoming(slotId);
  private bool HasSuppressionWeapon()
  {
   if(weapons==null)return false;weapons.CopyEquipmentWeaponSnapshots(equipment);
   return equipment.Exists(e=>e.Enabled&&(e.Rounds>0||e.Magazines>1)&&weapons.SupportsPassiveRfHoming(e.SlotId));
  }
  private float SuppressionStandOff()
  {
   float reach=0;if(weapons==null)return 0;weapons.CopyEquipmentWeaponSnapshots(equipment);
   foreach(var e in equipment)if(e.Enabled&&weapons.SupportsPassiveRfHoming(e.SlotId)&&weapons.TryGetWeaponSlotStatus(new MechEquipmentSlot(e.SlotId),out var s))reach=Mathf.Max(reach,s.EffectiveRangeMeters);
   return reach*.8f;
  }
  private float EscortStandOff()
  {
   weapons.CopyEquipmentWeaponSnapshots(equipment);float reach=0;
   foreach(var e in equipment)if(e.Enabled&&IsDefensiveRole(e.Role)&&weapons.TryGetWeaponSlotStatus(new MechEquipmentSlot(e.SlotId),out var status))reach=Mathf.Max(reach,status.EffectiveRangeMeters);
   return Mathf.Clamp(reach*.2f,1000,12000);
  }
  private bool TryMissionNavigation(out Vector3 point,out Vector3 velocity)
  {
   point=transform.position;velocity=Vector3.zero;if(!HasSpatialMission)return false;
   var p=State.Task.Order;float phase=(float)(radar.SimTimeSeconds*.035)+State.FormationIndex*.35f;
   if(p.TaskGroup==FleetTaskGroup.Strike)return false;
   if(p.TaskGroup==FleetTaskGroup.Escort)
   {
    if(!TryMissionProtectedState(out var protectedPosition,out velocity))return true;
    var forward=velocity.sqrMagnitude>1?velocity.normalized:Vector3.forward;
    var side=Vector3.Cross(Vector3.up,forward).normalized;if(side.sqrMagnitude<.1f)side=Vector3.right;
    float spacing=EscortStandOff();float sign=State.FormationIndex%2==0?1:-1;
    var watch=TryDefenseLook(out var danger)?danger.normalized:forward;
    point=protectedPosition+watch*spacing+side*spacing*.5f*sign+Vector3.up*spacing*.15f+OwnFormationOffset();return true;
   }
   if(p.TaskGroup==FleetTaskGroup.AirDefenseSuppression)
   {
    if(TryMissionAirTarget(out _))return false;
    if(!TryMissionProtectedState(out var target,out velocity))return true;
    var relative=target-transform.position;float standOff=SuppressionStandOff();
    if(standOff<=0)return true;
    standOff=Mathf.Max(standOff,relative.magnitude);
    point=relative.sqrMagnitude>1?target-relative.normalized*standOff:transform.position;return true;
   }
   if(p.TaskGroup==FleetTaskGroup.AirSuperiority&&MissionContains(p,transform.position)&&TryMissionAirTarget(out _))return false;
   if(p.MissionGeometry==FleetMissionGeometry.Area&&p.MissionSectors.Length>0)
   {
    int index=missionWaypoint%p.MissionSectors.Length;var rect=p.MissionSectors[index];
    float x=(rect.x+rect.z+1)*.5f*p.MissionCellSize,z=(rect.y+rect.w+1)*.5f*p.MissionCellSize;
    float radius=Mathf.Min(rect.z-rect.x+1,rect.w-rect.y+1)*p.MissionCellSize*.3f;
    var local=new float3(x+Mathf.Cos(phase)*radius,0,z+Mathf.Sin(phase)*radius);
    var height=math.mul(math.inverse(p.MissionGridRotation),(float3)(p.MissionPoint-p.MissionGridOrigin)).y;local.y=height;point=(Vector3)(float3)(p.MissionGridOrigin+(double3)math.mul(p.MissionGridRotation,local));
    if(p.MissionSectors.Length>1&&Vector3.Distance(transform.position,point)<500)missionWaypoint++;
   }
   else if(p.MissionGeometry==FleetMissionGeometry.PointDirection)point=(Vector3)(float3)p.MissionPoint;
   else point=(Vector3)(float3)p.MissionPoint+(Vector3)math.mul(p.MissionGridRotation,new float3(Mathf.Cos(phase),0,Mathf.Sin(phase)))*Mathf.Max(1000,FormationSpacing*6);
   return true;
  }
  public bool TryMissionLook(out Vector3 direction)
  {
   direction=default;if(!HasSpatialMission)return false;var p=State.Task.Order;
   if(p.TaskGroup==FleetTaskGroup.Reconnaissance&&p.MissionGeometry==FleetMissionGeometry.PointDirection&&Vector3.Distance(transform.position,(Vector3)(float3)p.MissionPoint)<1000)
   {direction=(Vector3)p.MissionDirection;return direction.sqrMagnitude>.001f;}
   if(p.TaskGroup==FleetTaskGroup.AirDefenseSuppression&&TryMissionProtectedState(out var target,out _)){direction=target-transform.position;return direction.sqrMagnitude>1;}
   return false;
  }
  private bool IsAssignedMissionThreat(in MissileTrackSnapshot m)
  {
   if(!HasSpatialMission||m.IsFriendly||!m.IsWarning)return false;var p=State.Task.Order;
   if(p.TaskGroup==FleetTaskGroup.AirSuperiority)return MissionContains(p,(Vector3)(float3)m.PredictPosition(radar.SimTimeSeconds));
   return p.TaskGroup==FleetTaskGroup.Escort&&TryMissionProtectedState(out var point,out var velocity)&&Threatens(m,(double3)(float3)point,(double3)(float3)velocity,ProtectedRadiusMetres,ProtectionHorizonSeconds,out _);
  }
 }
}
