using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using UnityEngine;
using Unity.Mathematics;
namespace ACunicombat.Mechs
{
    public sealed partial class MechFleetController
    {
        [Min(1)] public float ProtectionHorizonSeconds=30;
        [Min(1)] public float ProtectedRadiusMetres=150;
        private readonly Dictionary<Guid,FleetPacket> threatReports=new Dictionary<Guid,FleetPacket>();
        private readonly Dictionary<Guid,TrackId> threatBindings=new Dictionary<Guid,TrackId>();
        private readonly Dictionary<TrackId,double> claimStarted=new Dictionary<TrackId,double>();
        private readonly Dictionary<TrackId,double> retryDefenseAfter=new Dictionary<TrackId,double>();
        private readonly Dictionary<TrackId,double> lastDefenseCommit=new Dictionary<TrackId,double>();
        private readonly Dictionary<TrackId,Guid> claimContacts=new Dictionary<TrackId,Guid>();
        private readonly List<MechTestProjectile> activeInterceptors=new List<MechTestProjectile>();
        private readonly TrackSnapshot[] missileAssociation=new TrackSnapshot[96];
        private readonly Dictionary<TrackId,MechActionProposal> pendingDefense=new();
        public void NotifyDefenseProposed(MechActionProposal proposal){if(proposal.Target.IsValid)pendingDefense[proposal.Target.TrackId]=proposal;}
        private bool HasPendingDefense(TrackId id)
        {
            if(!pendingDefense.TryGetValue(id,out var p))return false;
            if(Time.timeAsDouble>=p.Deadline || !weapons.Actions.TryGetFeedback(p.Id,out var f) || f.Phase>MechActionPhase.Running){pendingDefense.Remove(id);return false;}
            return true;
        }
        private readonly Dictionary<TrackId,double> lastActionable=new();
        private readonly Dictionary<TrackId,double> nextClaimMessage=new(),nextClaimBundle=new(),nextThreatMessage=new();
        private double nextDefensePoll;
        public TrackId DefenseTrack {get;private set;}
        private TrackId defenseLookTrack;
        private void ReceiveThreat(FleetPacket p,double now)
        {if(p.Fleet!=State.Fleet||p.Contact==Guid.Empty||p.Threat.IsFriendly||!p.Threat.IsWarning||now-p.Threat.LastEvidenceTime>1)return;
         if(p.Kind==FleetPacketKind.DefenseClaim||p.Kind==FleetPacketKind.DefenseCommitted)p.EstimatedSeconds=p.ThreatTimeSeconds;
         if(!threatReports.TryGetValue(p.Contact,out var old)||p.Threat.LastEvidenceTime>old.Threat.LastEvidenceTime)threatReports[p.Contact]=p;}
        public static bool Threatens(in MissileTrackSnapshot m,double3 position,double3 velocity,double radius,double horizon,out float time)
        {
            var r=m.Position-position;var v=m.Velocity-velocity;double speed=math.lengthsq(v);time=float.PositiveInfinity;
            if(speed<1||math.dot(r,v)>=0)return false;double t=-math.dot(r,v)/speed;
            if(t>horizon)return false;
            double miss=math.length(r+v*t);time=(float)t;
            return miss<=radius+Math.Min(200,m.PositionUncertaintyMeters);
        }
        public bool IsFleetThreat(in MissileTrackSnapshot track)=>track.IsWeaponsQuality&&radar.SimTimeSeconds-track.LastEvidenceTime<=.6&&IsProtectionCandidate(track);
        private bool IsProtectionCandidate(in MissileTrackSnapshot track)
        {
            if(!Configured||track.IsFriendly||!track.IsWarning||radar.SimTimeSeconds-track.LastEvidenceTime>1.2)return false;
            var m=track;m.Position=track.PredictPosition(radar.SimTimeSeconds);
            if(m.Incoming||IsAssignedMissionThreat(m))return true;
            foreach(var pair in State.Members)
            {var member=pair.Value;if(!State.Fresh(member,radar.SimTimeSeconds)||!member.Available)continue;
             if(Threatens(m,member.Position+member.Velocity*Math.Max(0,radar.SimTimeSeconds-member.ObservedAt),member.Velocity,ProtectedRadiusMetres,ProtectionHorizonSeconds,out _))return true;}
            foreach(var pair in threatBindings)if(pair.Value.Equals(track.TrackId)&&threatReports.TryGetValue(pair.Key,out var report)&&State.Fresh(report,radar.SimTimeSeconds))return true;
            return false;
        }
        // Zero CPA can mean an unresolved/diverging estimate, not impact now. It must not
        // erase another observer's positive, still-future protection deadline.
        public static float EarlierFutureDeadline(float current,float reported,double age)
        {float remaining=reported-(float)Math.Max(0,age);return float.IsFinite(remaining)&&remaining>.05f?Math.Min(current,remaining):current;}
        public float ProtectedThreatTime(in MissileTrackSnapshot track)
        {
            if(!IsFleetThreat(track))return -1;
            double now=radar.SimTimeSeconds;float best=track.Incoming&&track.TimeToClosestApproachSeconds>.05f?track.TimeToClosestApproachSeconds:float.PositiveInfinity;
            var m=track;m.Position=track.PredictPosition(now);
            foreach(var member in State.Members.Values)
                if(State.Fresh(member,now)&&member.Available&&Threatens(m,member.Position+member.Velocity*Math.Max(0,now-member.ObservedAt),member.Velocity,ProtectedRadiusMetres,ProtectionHorizonSeconds,out float t))best=EarlierFutureDeadline(best,t,0);
            foreach(var pair in threatReports)
                if(State.Fresh(pair.Value,now)&&threatBindings.TryGetValue(pair.Key,out var local)&&local.Equals(track.TrackId))best=EarlierFutureDeadline(best,pair.Value.EstimatedSeconds,now-pair.Value.ObservedAt);
            if(!float.IsFinite(best)&&IsAssignedMissionThreat(m))best=ProtectionHorizonSeconds;
            return float.IsFinite(best)?best:-1;
        }
        public bool TryDefenseLook(out Vector3 direction)
        {
            direction=default;if(!radar.TryGetMissileTrack(defenseLookTrack,out var m)||!IsProtectionCandidate(m))return false;
            direction=(Vector3)(float3)m.PredictPosition(radar.SimTimeSeconds)-transform.position;return direction.sqrMagnitude>1;
        }
        private bool SameDefenseContact(TrackId id,Guid contact)=>threatBindings.TryGetValue(contact,out var local)&&local.Equals(id)||claimContacts.TryGetValue(id,out var own)&&own==contact;
        private bool ReservedByOther(TrackId id,double now)=>MechFleetState.TrySelectDefenseClaim(State.Claims.Values,c=>SameDefenseContact(id,c),now,out var winner)&&winner.Sender!=MemberId;
        private void UpdateFleetDefense(double now)
        {
            if(now<nextDefensePoll)return;nextDefensePoll=now+.1;
            int count=radar.CopyMissileTracks(missiles);
            for(int i=0;i<count;i++)missileAssociation[i]=MechFleetContactMatcher.Missile(missiles[i],now);
            var expired=new List<Guid>();foreach(var p in threatReports)if(p.Value.ExpiresAt<now)expired.Add(p.Key);foreach(var id in expired){threatReports.Remove(id);threatBindings.Remove(id);}
            foreach(var pair in threatReports)
            {var remote=MechFleetContactMatcher.Missile(pair.Value.Threat,now);int at=MechFleetContactMatcher.Match(remote,missileAssociation,count,now,1);if(at>=0)threatBindings[pair.Key]=missiles[at].TrackId;}
            DefenseTrack=default;defenseLookTrack=default;float best=float.PositiveInfinity,lookBest=float.PositiveInfinity;
            for(int i=0;i<count;i++)
            {
                var m=missiles[i];if(!m.IsWarning||m.IsFriendly||now-m.LastEvidenceTime>1.2)continue;
                if(m.Incoming&&(!nextThreatMessage.TryGetValue(m.TrackId,out var nextReport)||now>=nextReport))
                {
                    nextThreatMessage[m.TrackId]=now+.5;
                    var p=new FleetPacket{Kind=FleetPacketKind.Threat,Contact=ContactFor(m.TrackId),ProtectedMember=MemberId,Threat=m,Position=(double3)(float3)transform.position,Velocity=(double3)(float3)(motion!=null?motion.Velocity:Vector3.zero),ObservedAt=now,ExpiresAt=now+1.5,EstimatedSeconds=m.TimeToClosestApproachSeconds};
                    p.Sender=MemberId;p.Fleet=State.Fleet;threatReports[p.Contact]=p;threatBindings[p.Contact]=m.TrackId;Send(p);
                }
                if(!IsProtectionCandidate(m))continue;
                if(IsFleetThreat(m))lastActionable[m.TrackId]=now;
                float urgency=m.Incoming&&m.TimeToClosestApproachSeconds>.05f?m.TimeToClosestApproachSeconds:ProtectionHorizonSeconds;
                foreach(var report in threatReports)if(threatBindings.TryGetValue(report.Key,out var local)&&local.Equals(m.TrackId))if(report.Value.EstimatedSeconds>.05f)urgency=Mathf.Min(urgency,report.Value.EstimatedSeconds);
                if(urgency<lookBest){lookBest=urgency;defenseLookTrack=m.TrackId;}
                // A reserved threat remains visible, but available defenders consider the next threat.
                if(ReservedByOther(m.TrackId,now))urgency+=1000;
                if(urgency<best&&IsFleetThreat(m)&&CanBid(m,out _)){best=urgency;DefenseTrack=m.TrackId;}
                if(HasPendingDefense(m.TrackId)||HasInterceptorInFlight(m.TrackId))MaintainDefenseClaim(m,now);
            }
            if(defenseLookTrack.IsValid)radar.CuePointDefenseTrack(defenseLookTrack);
            if(DefenseTrack.IsValid){radar.CuePointDefenseTrack(DefenseTrack);if(radar.TryGetMissileTrack(DefenseTrack,out var selected)&&IsFleetThreat(selected)&&!ReservedByOther(DefenseTrack,now))MaintainDefenseClaim(selected,now);}
            var stale=new List<TrackId>();foreach(var id in claimStarted.Keys)if((!radar.TryGetMissileTrack(id,out var t)||!IsFleetThreat(t))&&(!lastActionable.TryGetValue(id,out var seen)||now-seen>1.2))stale.Add(id);
            foreach(var id in stale)ReleaseDefense(id,now,"ThreatNoLongerActionable");
        }
        private Guid CanonicalContact(TrackId track)
        {
            Guid best=Guid.Empty;foreach(var pair in threatBindings)if(pair.Value.Equals(track)&&(best==Guid.Empty||pair.Key.CompareTo(best)<0))best=pair.Key;
            return best!=Guid.Empty?best:ContactFor(track);
        }
        private bool HasInterceptorInFlight(TrackId track)
        {
            foreach(var p in MechTestProjectile.Registry)if(p!=null&&p.IsOwnedBy(transform)&&p.IsInterceptorMissile&&p.IsEngagementActive&&p.GuidanceTrackId.Equals(track))return true;
            return false;
        }
        private bool CanBid(in MissileTrackSnapshot threat,out float estimate)
        {
            estimate=float.PositiveInfinity;weapons.CopyEquipmentWeaponSnapshots(equipment);
            float range=Vector3.Distance(transform.position,(Vector3)(float3)threat.PredictPosition(radar.SimTimeSeconds));
            foreach(var e in equipment)
            {
                if(!e.Enabled||e.Reloading||e.Rounds<=0||!IsDefensiveRole(e.Role))continue;
                if(weapons.TryEstimateFleetDefense(e.SlotId,threat,out float seconds))estimate=Math.Min(estimate,seconds+Math.Max(0,e.NextFireTime-Time.time));
            }
            return float.IsFinite(estimate);
        }
        private void MaintainDefenseClaim(in MissileTrackSnapshot track,double now)
        {
            bool flying=HasInterceptorInFlight(track.TrackId);
            if(!flying&&retryDefenseAfter.TryGetValue(track.TrackId,out var retry)&&now<retry)return;
            if(!flying&&!HasPendingDefense(track.TrackId)&&!CanBid(track,out _))return;
            Guid key=CanonicalContact(track.TrackId);
            if(!claimStarted.ContainsKey(track.TrackId)){claimStarted[track.TrackId]=now;claimContacts[track.TrackId]=key;}
            if(flying)
            {PublishClaim(track.TrackId,FleetPacketKind.DefenseCommitted,0,now,now+1.5);return;}
            if(lastDefenseCommit.TryGetValue(track.TrackId,out var shot)&&now-shot<.4)
            {PublishClaim(track.TrackId,FleetPacketKind.DefenseCommitted,0,now,now+.4);return;}
            if(!HasPendingDefense(track.TrackId)&&now-claimStarted[track.TrackId]>4)
            {ReleaseDefense(track.TrackId,now,"NoCommitOrInterceptorEnded");retryDefenseAfter[track.TrackId]=now+1;return;}
            CanBid(track,out float estimated);if(!float.IsFinite(estimated))estimated=ProtectionHorizonSeconds;PublishClaim(track.TrackId,FleetPacketKind.DefenseClaim,estimated,now,now+1.5);
        }
        private void PublishClaim(TrackId local,FleetPacketKind kind,float estimate,double now,double until)
        {
            Guid key=claimContacts.TryGetValue(local,out var existing)?existing:CanonicalContact(local);
            var p=new FleetPacket{Kind=kind,Sender=MemberId,Fleet=State.Fleet,Contact=key,Revision=++revision,ObservedAt=now,ExpiresAt=until,LeaseUntil=until,EstimatedSeconds=estimate};
            State.Receive(p,now);
            if(nextClaimMessage.TryGetValue(local,out var next)&&now<next)return;
            nextClaimMessage[local]=now+.35;
            if(!nextClaimBundle.TryGetValue(local,out var bundle)||now>=bundle)
            {
                if(radar.TryGetMissileTrack(local,out var observation)){p.Threat=observation;p.ThreatTimeSeconds=ProtectedThreatTime(observation);}
                nextClaimBundle[local]=now+.7;
            }
            Send(p);
        }
        private void ReleaseDefense(TrackId local,double now,string reason)
        {if(claimContacts.TryGetValue(local,out var key)){var p=new FleetPacket{Kind=FleetPacketKind.DefenseRelease,Sender=MemberId,Fleet=State.Fleet,Contact=key,Revision=++revision,ObservedAt=now,ExpiresAt=now+1};State.Receive(p,now);Send(p);Log("defense_release",p,reason);}claimStarted.Remove(local);claimContacts.Remove(local);lastDefenseCommit.Remove(local);lastActionable.Remove(local);pendingDefense.Remove(local);nextClaimMessage.Remove(local);nextClaimBundle.Remove(local);nextThreatMessage.Remove(local);}
        public bool MayDefend(in MissileTrackSnapshot track,out string reason,bool preparationOnly=false)
        {
            reason=null;if(!Configured)return true;
            if(!IsFleetThreat(track)&&!(preparationOnly&&track.IsWarning&&!track.IsFriendly&&lastActionable.TryGetValue(track.TrackId,out var seen)&&radar.SimTimeSeconds-seen<=1.2)){reason="NotAProtectedFleetThreat";return false;}
            double now=radar.SimTimeSeconds;
            if(track.Incoming&&track.TimeToClosestApproachSeconds>0&&track.TimeToClosestApproachSeconds<1&&track.ClosestApproachDistanceMeters<ProtectedRadiusMetres+track.CpaUncertaintyMeters){LastDecision="EmergencySelfDefense";return true;}
            if(!claimStarted.TryGetValue(track.TrackId,out var started)||now-started<Math.Max(.25,radar.TelemetryLink.endToEndLatencyMs*.002+.1)){reason="CoordinationPending";return false;}
            var localId=track.TrackId;
            bool any=MechFleetState.TrySelectDefenseClaim(State.Claims.Values,c=>SameDefenseContact(localId,c),now,out var winner);
            if(!any||winner.Sender!=MemberId){reason="AssignedToFleetMember";return false;}
            if(HasInterceptorInFlight(track.TrackId)){reason="OwnInterceptorAlreadyInFlight";return false;}
            return true;
        }
        private void DefenseCommitted(TrackId target,MechWeaponRole role)
        {if(!target.IsValid)return;double now=radar.SimTimeSeconds;lastDefenseCommit[target]=now;claimStarted[target]=now;nextClaimMessage.Remove(target);nextClaimBundle.Remove(target);PublishClaim(target,FleetPacketKind.DefenseCommitted,0,now,now+(role==MechWeaponRole.InterceptorMissile?1.5:.4));}
        public void NotifyApsDefense(TrackId target)=>DefenseCommitted(target,MechWeaponRole.PointDefenseCannon);
    }
}
