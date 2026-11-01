using System;
using ACunicombat.Sensing.Contracts;
using UnityEngine;
using Unity.Mathematics;
namespace ACunicombat.Mechs
{
    public sealed partial class MechFleetController
    {
        public static bool BetterOpportunisticPair(bool ready,float score,bool hasBest,bool bestReady,float bestScore)
            =>!hasBest||ready&&!bestReady||ready==bestReady&&score>bestScore;
        public bool TryGetOpportunisticTrack(out TrackSnapshot result)
        {
            result=default;if(!Configured||State.Mode!=FleetAttackMode.Free||radar==null||weapons==null)return false;
            int count=radar.CopyTracks(air);float best=float.NegativeInfinity;bool found=false,bestReady=false;
            weapons.CopyEquipmentWeaponSnapshots(equipment);
            for(int i=0;i<count;i++)
            {
                var t=air[i];if(t.ObjectClass==TrackObjectClass.Missile||t.ObjectClass==TrackObjectClass.Decoy||t.Lifecycle!=TrackLifecycle.Confirmed)continue;
                float range=Vector3.Distance(transform.position,radar.PredictTrackPosition(t));
                foreach(var e in equipment)
                {
                    if(!State.AllowsWeapon(e.InstanceId)||!AllowsMissionEquipment(e.SlotId,e.Role)||e.Role==MechWeaponRole.AirToAirMissile&&!State.AllowsOffensiveMissile(e.InstanceId))continue;
                    if(!weapons.PreviewOffensiveTarget(e,t,out bool ready))continue;
                    if(playerAutoShotUntil.TryGetValue(e.InstanceId,out var until)&&radar.SimTimeSeconds<until)ready=false;
                    weapons.TryGetWeaponSlotStatus(new MechEquipmentSlot(e.SlotId),out var status);
                    float score=MechAiRulePlanner.ScoreWeapon(in status,range)-range/1000f;
                    if(!BetterOpportunisticPair(ready,score,found,bestReady,best))continue;
                    found=true;bestReady=ready;best=score;result=t;
                }
            }
            return found;
        }
        public bool TryGetOrderedTrack(out TrackSnapshot track)
        {
            track=default;if(!HasTask||State.Task.Order.Order==FleetOrderKind.Move||State.Task.Phase==FleetTaskPhase.Returning)return false;
            if(HasSpatialMission)return TryMissionAirTarget(out track);
            double now=radar.SimTimeSeconds;var report=State.Task.Order.Target;
            if(resolvedOrder.IsValid&&radar.TryGetTrack(resolvedOrder,out var retained)&&now-retained.LastUpdateTime<=1.5&&retained.Affiliation!=TrackAffiliation.Friendly&&(!retained.Identity.Available||retained.Identity.CanAttackPlatform))
            {track=retained;lastSeenOrder=now;return true;}
            int n=radar.CopyTracks(air);int at=MechFleetContactMatcher.Match(report,air,n,now);
            if(at<0){resolvedOrder=default;return false;}
            track=air[at];resolvedOrder=track.TrackId;lastSeenOrder=now;
            if(track.Identity.Available&&!track.Identity.CanAttackPlatform){LastDecision="IdentityDenied";return false;}
            return track.Affiliation!=TrackAffiliation.Friendly;
        }
        private void UpdateOrder(double now)
        {
            if(!HasTask)return;
            if(State.Task.Phase==FleetTaskPhase.Returning)
            {
                if(State.Leader==MemberId){State.SetPhase(FleetTaskPhase.Completed,State.Task.Reason,now);return;}
                if(TryFormationPoint(now,out var point,out var velocity)&&Vector3.Distance(point,transform.position)<150&&(motion==null||(motion.Velocity-velocity).magnitude<20))State.SetPhase(FleetTaskPhase.Completed,State.Task.Reason,now);
                return;
            }
            if(HasSpatialMission)
            {State.SetPhase(State.Task.Order.TaskGroup==FleetTaskGroup.Strike&&!TryMissionAirTarget(out _)?FleetTaskPhase.Acquiring:FleetTaskPhase.Executing,FleetTaskReason.None,now);return;}
            if(State.Task.Order.Order==FleetOrderKind.Move)
            {
                State.SetPhase(FleetTaskPhase.Executing,FleetTaskReason.None,now);
                if(Vector3.Distance(MoveDestination(),transform.position)<150&&(motion==null||motion.Velocity.magnitude<20))
                {
                    if(State.Task.WaypointIndex+1<State.Task.Order.Waypoints.Length)State.Task.WaypointIndex++;
                    else State.SetPhase(FleetTaskPhase.Completed,FleetTaskReason.None,now);
                }
                return;
            }
            if(State.Task.Order.Order==FleetOrderKind.AttackWithWeapon)
            {
                weapons.CopyEquipmentWeaponSnapshots(equipment);var exists=equipment.Find(e=>e.InstanceId==State.Task.Order.Weapon);
                if(exists.InstanceId==Guid.Empty||!exists.Enabled||exists.Rounds<=0&&exists.Magazines<=1){State.SetPhase(FleetTaskPhase.Returning,FleetTaskReason.MissingWeapon,now);return;}
            }
            if(State.Task.Order.AutomaticEngagement&&TryGetOrderedTrack(out var automaticTrack)&&!WithinAutomaticAttackRange(transform.position,radar.PredictTrackPosition(automaticTrack),MaximumOffensiveRange())){State.SetPhase(FleetTaskPhase.Completed,FleetTaskReason.EngagementCompleted,now);return;}
            if(TryGetOrderedTrack(out _)){if(State.Task.Phase==FleetTaskPhase.Acquiring)State.SetPhase(FleetTaskPhase.Executing,FleetTaskReason.None,now);}
            else if(now-lastSeenOrder>8){State.SetPhase(FleetTaskPhase.Returning,FleetTaskReason.TargetLost,now);}
        }
        public bool TryNavigation(out Vector3 direction,out MechPlayerInputFrame frame)
        {
            direction=transform.forward;frame=default;
            if(!TryGetNavigationObjective(out var objective))return false;
            frame=MechAutonomousFlight.Navigate(motion,transform.position,in objective,out direction);return true;
        }
        public bool TryGetNavigationObjective(out MechNavigationObjective objective)
        {
            objective=default;if(!Configured||(IsPlayer&&!PlayerAutopilotActive)||motion==null)return false;
            double now=radar.SimTimeSeconds;Vector3 point,velocity;
            bool follow=State.Leader!=MemberId&&(!Detached||HasTask&&State.Task.Phase==FleetTaskPhase.Returning);
            if(HasTask&&State.Task.Order.Order==FleetOrderKind.Move&&State.Task.Phase!=FleetTaskPhase.Returning)
            {point=MoveDestination();velocity=Vector3.zero;}
            else if(HasSpatialMission&&TryMissionNavigation(out point,out velocity)){}
            else if(follow)
            {
                if(!TryFormationPoint(now,out point,out velocity)){LastDecision="LeaderReportExpired";point=transform.position;velocity=Vector3.zero;}
            }
            // Combat approach is handled by the AI's weapon-dependent standoff
            // controller. Do not replace it with navigation to the target itself.
            else if(State.CruiseCommandActive&&!HasTask){point=transform.position+transform.forward*State.CruiseSpeed*10;velocity=Vector3.zero;}
            else if(TaskGroup==FleetTaskGroup.Support&&!HasTask){point=transform.position;velocity=Vector3.zero;}
            else return false;
            var task=follow?(HasTask&&State.Task.Phase==FleetTaskPhase.Returning?MechFlightTask.Return:MechFlightTask.Formation):MechFlightTask.Navigation;
            objective=new MechNavigationObjective(task,point,velocity,State.CruiseSpeed);return true;
        }
        private readonly MechFormationCourse formationCourse=new();
        public bool TryGetNavigationGoal(out Vector3 point)
        {
            point=default;if(!Configured)return false;
            if(PlayerAutopilotActive&&playerNavigationValid){point=playerNavigationGoal;return true;}
            if(HasTask&&State.Task.Order.Order==FleetOrderKind.Move&&State.Task.Phase!=FleetTaskPhase.Returning){point=MoveDestination();return true;}
            if(attackPlanValid&&HasTask&&State.Task.Order.Operation==planOrder&&State.Task.Phase!=FleetTaskPhase.Returning){point=attackPlanPoint;return true;}
            if(HasSpatialMission&&TryMissionNavigation(out point,out _))return true;
            if(State.Leader!=MemberId&&(!Detached||HasTask&&State.Task.Phase==FleetTaskPhase.Returning))return TryFormationPoint(radar.SimTimeSeconds,out point,out _);
            var ai=GetComponent<MechAiTargetController>();return ai!=null&&ai.TryGetObservedNavigationGoal(out point);
        }
        private Vector3 MoveDestination()
        {
            var point=(Vector3)(float3)State.Task.Order.Waypoints[State.Task.WaypointIndex];
            if(State.Task.Order.WholeFleet)point+=OwnFormationOffset();
            return point;
        }
        private bool TryFormationPoint(double now,out Vector3 point,out Vector3 velocity)
        {
            point=velocity=Vector3.zero;
            if(!State.Members.TryGetValue(State.Leader,out var leader)||!State.Fresh(leader,now)||!leader.Available)return false;
            velocity=(Vector3)(float3)leader.Velocity;
            var offset=OwnFormationOffset();
            if(Roster.TryGetValue(State.Leader,out var leaderEntry)&&GroupRoot(leaderEntry)==State.FormationRoot)
                offset-=leaderEntry.FormationShape==FleetFormationShape.Custom?leaderEntry.FormationOffset:FormationSlotOffset(leaderEntry.FormationShape,leaderEntry.FormationIndex,leaderEntry.FormationSize,FormationSpacing);
            formationCourse.Resolve(State.Leader,now,velocity,offset,out var worldOffset,out velocity);
            point=(Vector3)(float3)(leader.Position+leader.Velocity*Math.Max(0,now-leader.ObservedAt))+worldOffset;return true;
        }
        private void RefreshIssuedTargets(double now)
        {
            foreach(var entry in pending.Values)
            {
                var p=entry.Packet;if(p.Kind!=FleetPacketKind.Order||p.TaskDeadline<now||p.Order!=FleetOrderKind.Attack&&p.Order!=FleetOrderKind.AttackWithWeapon&&!(p.Order==FleetOrderKind.AssignMission&&p.MissionGeometry==FleetMissionGeometry.Target))continue;
                TrackSnapshot target=default;
                if(contactTracks.TryGetValue(p.Contact,out var local)&&radar.TryGetTrack(local,out var current))target=current;
                else {int count=radar.CopyTracks(air);int at=MechFleetContactMatcher.Match(p.Target,air,count,now,8);if(at>=0)target=air[at];}
                if(!target.TrackId.IsValid)continue;
                var update=p;update.Kind=FleetPacketKind.TargetUpdate;update.Target=target;update.ObservedAt=now;update.ExpiresAt=now+1;Send(update);p.Target=target;entry.Packet=p;
            }
        }
        public static bool WithinAutomaticAttackRange(Vector3 shooter,Vector3 target,float maximum)
            =>float.IsFinite(maximum)&&maximum>0&&(target-shooter).sqrMagnitude<=maximum*maximum;
        public float MaximumOffensiveRange()
        {
            using var costScope = ACunicombat.Simulation.Core.SimulationCostProbe.Measure(ACunicombat.Simulation.Core.SimulationCostProbe.SystemId.FleetRange);
            float maximum=0;if(weapons==null)return 0;weapons.CopyEquipmentWeaponSnapshots(equipment);
            foreach(var e in equipment)if(e.Enabled&&weapons.TryGetWeaponSlotStatus(new MechEquipmentSlot(e.SlotId),out var s)&&s.HasReserveAmmunition&&MechAiRulePlanner.IsOffensiveRole(s.Role))maximum=Mathf.Max(maximum,s.MaximumRangeMeters);
            return maximum;
        }
        private void CoordinateAutonomousAttack(double now)
        {
            if(HasTask)return;int count=radar.CopyTracks(air);
            TrackSnapshot Pick(Vector3 origin,float maximum)
            {
                TrackSnapshot best=default;float distance=float.PositiveInfinity;
                for(int i=0;i<count;i++){var t=air[i];if(t.ObjectClass==TrackObjectClass.Missile||t.ObjectClass==TrackObjectClass.Decoy||t.Affiliation==TrackAffiliation.Friendly||t.Identity.Available&&!t.Identity.CanAttackPlatform||t.Lifecycle!=TrackLifecycle.Confirmed)continue;
                    var point=(Vector3)(float3)t.PredictPosition(now);float d=Vector3.Distance(origin,point);if(d>=distance||!WithinAutomaticAttackRange(origin,point,maximum))continue;best=t;distance=d;}
                return best;
            }
            if(!IsPlayer)
            {
                var target=Pick(transform.position,MaximumOffensiveRange());
                if(target.TrackId.IsValid)State.Receive(new FleetPacket{Kind=FleetPacketKind.Order,Sender=MemberId,Recipient=MemberId,Fleet=State.Fleet,Operation=Guid.NewGuid(),Revision=++revision,Order=FleetOrderKind.Attack,Target=target,AutomaticEngagement=true,ObservedAt=now,ExpiresAt=now+3,TaskDeadline=now+Math.Max(TaskSeconds,300),WholeFleet=true},now);
            }
            foreach(var r in Roster.Values)
            {
                if(r.Leader!=MemberId||r.Member==MemberId||r.Player||!State.Members.TryGetValue(r.Member,out var member)||!State.Fresh(member,now)||member.Mode!=FleetAttackMode.Free)continue;
                if(member.Phase!=FleetTaskPhase.None&&member.Phase!=FleetTaskPhase.Completed&&member.Phase!=FleetTaskPhase.Cancelled&&member.Phase!=FleetTaskPhase.Expired)continue;
                float maximum=0;foreach(var cap in State.Capabilities.Values)if(cap.Sender==r.Member&&cap.Available&&cap.Ammunition>0&&State.Fresh(cap,now)&&MechAiRulePlanner.IsOffensiveRole((MechWeaponRole)cap.Role))maximum=Mathf.Max(maximum,cap.MaximumRange);
                var target=Pick((Vector3)(float3)member.Position,maximum);if(!target.TrackId.IsValid)continue;
                IssueInternal(r.Member,FleetOrderKind.Attack,target,default,State.Mode,true,now+Math.Max(TaskSeconds,300),automaticEngagement:true);
            }
        }
    }
}
