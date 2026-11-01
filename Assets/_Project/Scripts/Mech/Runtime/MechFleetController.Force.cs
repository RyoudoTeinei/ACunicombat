using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;
using UnityEngine;
namespace ACunicombat.Mechs
{
 // Force allocation (leader) and Winchester (every member). Strength comes only from
 // received heartbeats/capabilities and own tracks; orders are ordinary Attack packets.
 public sealed partial class MechFleetController
 {
  /// <summary>No usable offensive ammunition left (Sea Power's Winchester).</summary>
  public bool IsWinchester {get;private set;}
  private Vector3 spawnPosition;private bool spawnRecorded,everArmed;private double firstWinchesterCheck;
  public IReadOnlyList<MechForceAssignment> LastAllocation=>lastAllocation;
  private List<MechForceAssignment> lastAllocation=new();
  public int ReserveCount {get;private set;}

  private void UpdateWinchester(double now)
  {
   if(!spawnRecorded){spawnPosition=transform.position;spawnRecorded=true;firstWinchesterCheck=now;}
   if(weapons==null){IsWinchester=false;return;}
   weapons.CopyEquipmentWeaponSnapshots(equipment);
   bool armed=false;
   foreach(var e in equipment)
   {
    if(!MechAiRulePlanner.IsOffensiveRole(e.Role))continue;
    if(e.Enabled&&(e.Rounds>0||e.Magazines>1||e.Reloading||e.Pending)){armed=true;break;}
   }
   // Only a unit that was observed armed can run dry: loadout snapshots are empty
   // for the first frames, and a unit that never carried offensive weapons is not Winchester.
   if(armed)everArmed=true;
   bool was=IsWinchester;IsWinchester=everArmed&&!armed&&now-firstWinchesterCheck>=3;
   if(IsWinchester&&!was)Log("winchester",State.Task?.Order??default,IsActingLeader?"Withdraw":"RejoinDefensive");
  }

  /// <summary>Winchester wingmen hold formation; a Winchester leader withdraws away from the threat toward its spawn.</summary>
  private bool TryWinchesterNavigation(out Vector3 point,out Vector3 velocity)
  {
   point=transform.position;velocity=Vector3.zero;if(!IsWinchester)return false;
   double now=radar.SimTimeSeconds;
   if(!IsActingLeader&&TryFormationPoint(now,out point,out velocity))return true;
   var home=spawnPosition;var away=home-transform.position;
   if(TryNearestHostile(now,out var hostile))
   {
    var flee=transform.position-hostile;
    // Head home, but never toward the threat.
    if(Vector3.Dot(away,flee)<0||away.sqrMagnitude<1e4f)away=flee;
   }
   point=transform.position+(away.sqrMagnitude>1?away.normalized:transform.forward)*5000f;return true;
  }

  private bool TryNearestHostile(double now,out Vector3 position)
  {
   position=default;if(radar==null)return false;int n=radar.CopyTracks(air);float best=float.PositiveInfinity;
   for(int i=0;i<n;i++)
   {
    var t=air[i];if(t.ObjectClass==TrackObjectClass.Missile||t.ObjectClass==TrackObjectClass.Decoy||t.Affiliation!=TrackAffiliation.Hostile||now-t.LastUpdateTime>3)continue;
    var p=radar.PredictTrackPosition(t);float d=(p-transform.position).sqrMagnitude;if(d<best){best=d;position=p;}
   }
   return float.IsFinite(best);
  }

  private float OwnStrength()
  {
   if(weapons==null)return 0;weapons.CopyEquipmentWeaponSnapshots(equipment);int offensive=0;bool missiles=false;
   foreach(var e in equipment)if(e.Enabled&&MechAiRulePlanner.IsOffensiveRole(e.Role)&&(e.Rounds>0||e.Magazines>1)){offensive++;missiles|=e.Role==MechWeaponRole.AirToAirMissile;}
   return MechForceAllocation.MemberStrength(body!=null?body.HealthFraction:1,offensive,missiles,IsWinchester);
  }

  private float ReportedStrength(Guid member,in FleetPacket heartbeat,double now)
  {
   int offensive=0;bool missiles=false;
   foreach(var cap in State.Capabilities.Values)
   {
    if(cap.Sender!=member||!State.Fresh(cap,now)||!cap.Available||cap.Ammunition<=0||!MechAiRulePlanner.IsOffensiveRole((MechWeaponRole)cap.Role))continue;
    offensive++;missiles|=(MechWeaponRole)cap.Role==MechWeaponRole.AirToAirMissile;
   }
   return MechForceAllocation.MemberStrength(heartbeat.HasPlatformStatus?heartbeat.HealthFraction:1,offensive,missiles,false);
  }

  private float ReportedReach(Guid member,double now)
  {
   float maximum=0;
   foreach(var cap in State.Capabilities.Values)if(cap.Sender==member&&cap.Available&&cap.Ammunition>0&&State.Fresh(cap,now)&&MechAiRulePlanner.IsOffensiveRole((MechWeaponRole)cap.Role))maximum=Mathf.Max(maximum,cap.MaximumRange);
   return maximum;
  }

  private readonly List<MechForceMember> forceMembers=new();
  private readonly List<MechForceThreat> forceThreats=new();
  private readonly List<Guid> forceIds=new();

  /// <summary>
  /// Leader-side allocation replacing the old "every idle wingman takes its nearest
  /// target": commit enough strength per threat, keep the rest in reserve.
  /// </summary>
  private void CoordinateAutonomousAttack(double now)
  {
   if(HasTask)return;int count=radar.CopyTracks(air);
   forceThreats.Clear();
   for(int i=0;i<count;i++)
   {
    var t=air[i];if(t.ObjectClass==TrackObjectClass.Missile||t.ObjectClass==TrackObjectClass.Decoy||t.Affiliation==TrackAffiliation.Friendly||t.Identity.Available&&!t.Identity.CanAttackPlatform||t.Lifecycle!=TrackLifecycle.Confirmed)continue;
    forceThreats.Add(new MechForceThreat{Track=t,Position=(Vector3)(float3)t.PredictPosition(now),Strength=MechForceAllocation.ThreatStrength(in t)});
   }
   forceMembers.Clear();forceIds.Clear();
   if(!IsPlayer)forceMembers.Add(new MechForceMember{Id=MemberId,Position=transform.position,Strength=OwnStrength(),MaximumRange=MaximumOffensiveRange(),Busy=false});
   foreach(var r in Roster.Values)
   {
    if(r.Leader!=MemberId||r.Member==MemberId||r.Player||!State.Members.TryGetValue(r.Member,out var member)||!State.Fresh(member,now)||member.Mode!=FleetAttackMode.Free||!member.Available)continue;
    bool busy=member.Phase!=FleetTaskPhase.None&&member.Phase!=FleetTaskPhase.Completed&&member.Phase!=FleetTaskPhase.Cancelled&&member.Phase!=FleetTaskPhase.Expired;
    forceMembers.Add(new MechForceMember{Id=r.Member,Position=(Vector3)(float3)(member.Position+member.Velocity*Math.Max(0,now-member.ObservedAt)),Strength=ReportedStrength(r.Member,member,now),MaximumRange=ReportedReach(r.Member,now),Busy=busy});
   }
   lastAllocation=MechForceAllocation.Allocate(forceMembers,forceThreats,transform.position);
   ReserveCount=0;foreach(var m in forceMembers)if(!m.Busy&&m.Strength>0&&!lastAllocation.Exists(a=>a.Member==m.Id))ReserveCount++;
   if(forceThreats.Count>0)Log("force_allocation",default,$"threats={forceThreats.Count};committed={lastAllocation.Count};reserve={ReserveCount}");
   foreach(var a in lastAllocation)
   {
    var target=forceThreats[a.ThreatIndex].Track;
    if(a.Member==MemberId){State.Receive(new FleetPacket{Kind=FleetPacketKind.Order,Sender=MemberId,Recipient=MemberId,Fleet=State.Fleet,Operation=Guid.NewGuid(),Revision=++revision,Order=FleetOrderKind.Attack,Target=target,AutomaticEngagement=true,ObservedAt=now,ExpiresAt=now+3,TaskDeadline=now+Math.Max(TaskSeconds,300),WholeFleet=true},now);continue;}
    IssueInternal(a.Member,FleetOrderKind.Attack,target,default,State.Mode,true,now+Math.Max(TaskSeconds,300),automaticEngagement:true);
   }
  }
 }
}
