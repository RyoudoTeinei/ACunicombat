using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using UnityEngine;
using Unity.Mathematics;
namespace ACunicombat.Mechs
{
    public sealed partial class MechIdentityController : ICombatRadiometricReceiver
    {
        public bool CombatObservationsEnabled=true;
        public bool CommandAuthority;
        public float ProtectedVolumeRadiusMetres=25;
        private readonly Dictionary<TrackId,TrackSourceEvidenceSet> combatSources=new Dictionary<TrackId,TrackSourceEvidenceSet>();
        private readonly MechCombatBehaviorTracker combat=new MechCombatBehaviorTracker();
        private readonly List<ProtectedCombatContact> protectedContacts=new List<ProtectedCombatContact>();
        private readonly HashSet<TrackId> missionProtected=new HashSet<TrackId>();
        private readonly Dictionary<Guid,(TrackId track,double expires)> receivedBehaviorBindings=new Dictionary<Guid,(TrackId,double)>();
        private readonly List<CombatBehaviorReport> behaviorOutbox=new List<CombatBehaviorReport>();
        private readonly Dictionary<string,Guid> continuousBehaviorIds=new Dictionary<string,Guid>();
        private readonly Dictionary<string,(double first,double last)> radiationHistory=new Dictionary<string,(double,double)>();
        private readonly Dictionary<TrackId,Dictionary<Guid,double>> invalidReplies=new Dictionary<TrackId,Dictionary<Guid,double>>();
        private readonly WarningObservation[] warnings=new WarningObservation[64];
        private readonly MissileTrackSnapshot[] protectedMissiles=new MissileTrackSnapshot[64];
        private readonly RaycastHit[] visibilityHits=new RaycastHit[32];
        public int ConfirmedAttackReports {get;private set;}
        public bool SetMissionProtected(ObserverTrackRef reference,bool value)
        {if(Radar==null||reference.ObserverScope!=Radar.ObserverScope||!reference.IsValid)return false;if(value)missionProtected.Add(reference.TrackId);else missionProtected.Remove(reference.TrackId);return true;}
        public bool DeclareHostile(ObserverTrackRef reference)
        {
            if(!CommandAuthority||Radar==null||reference.ObserverScope!=Radar.ObserverScope||!reference.IsValid)return false;
            for(int i=0;i<count;i++)if(tracks[i].TrackId.Equals(reference.TrackId)){PublishContinuousBehavior(tracks[i],CombatBehaviorKind.CommandHostile,"command/"+reference.TrackId,Time.timeAsDouble,1,1);return true;}
            return false;
        }
        private void BuildProtectedContacts()
        {
            protectedContacts.Clear();var motion=GetComponent<MechFlightMotionController>();
            protectedContacts.Add(new ProtectedCombatContact{Position=transform.position,Velocity=motion!=null?motion.Velocity:Vector3.zero,Radius=ProtectedVolumeRadiusMetres,Kind=ProtectedContactKind.Ownship});
            for(int i=0;i<count;i++)
            {
                var t=tracks[i];if(t.ObjectClass==TrackObjectClass.Missile||t.ObjectClass==TrackObjectClass.Decoy)continue;bool friendly=t.Affiliation==TrackAffiliation.Friendly;
                if(snapshots.TryGetValue(t.TrackId,out var identity))friendly=identity.Relation==IdentityRelation.Friendly;
                bool protectedByMission=missionProtected.Contains(t.TrackId);
                if(Radar.SimTimeSeconds-t.LastUpdateTime>3)continue;
                protectedContacts.Add(new ProtectedCombatContact{Position=(Vector3)(float3)t.PredictPosition(Radar.SimTimeSeconds),Velocity=(Vector3)(float3)t.Velocity,Radius=ProtectedVolumeRadiusMetres,SensorTrack=t.TrackId,Kind=protectedByMission?ProtectedContactKind.MissionProtected:friendly?ProtectedContactKind.FriendlyPlatform:ProtectedContactKind.UnprotectedPlatform});
            }
            int missileCount=Radar.CopyMissileTracks(protectedMissiles);
            for(int i=0;i<missileCount;i++){var t=protectedMissiles[i];if(Radar.SimTimeSeconds-t.LastUpdateTime>1||t.PositionUncertaintyMeters>10)continue;
                protectedContacts.Add(new ProtectedCombatContact{Position=(Vector3)(float3)t.PredictPosition(Radar.SimTimeSeconds),Velocity=(Vector3)(float3)t.PredictVelocity(Radar.SimTimeSeconds),Radius=8,SensorTrack=t.TrackId,AssociatedSensorTrack=t.AssociatedSensorTrackId,LaunchIdentity=t.OwnLaunchIdentity,Kind=t.IsFriendly?ProtectedContactKind.FriendlyEquipment:ProtectedContactKind.UnprotectedEquipment});}
        }
        private void PollCombatObservations(double now)
        {
            BuildProtectedContacts();combat.Prune(now);MechCombatObservationSensor.Poll(this,now);combat.FollowSensorTracks(protectedMissiles,Radar.CopyMissileTracks(protectedMissiles),Radar.SimTimeSeconds,protectedContacts);ObserveRadiation(now);
            behaviorOutbox.RemoveAll(r=>r.ExpiresAt<=now);
            var retired=new List<Guid>();foreach(var pair in receivedBehaviorBindings)if(pair.Value.expires<now)retired.Add(pair.Key);foreach(var id in retired)receivedBehaviorBindings.Remove(id);
            var oldRadiation=new List<string>();foreach(var pair in radiationHistory)if(now-pair.Value.last>3)oldRadiation.Add(pair.Key);foreach(var key in oldRadiation)radiationHistory.Remove(key);
            var obsoleteSources=new List<TrackId>();foreach(var id in combatSources.Keys)if(!active.Contains(id))obsoleteSources.Add(id);foreach(var id in obsoleteSources)combatSources.Remove(id);
            var oldTracks=new List<TrackId>();foreach(var pair in invalidReplies)if(!active.Contains(pair.Key))oldTracks.Add(pair.Key);foreach(var id in oldTracks)invalidReplies.Remove(id);
        }
        public bool TryObserveCombatPoint(Vector3 point,bool missile,bool launch,out Vector3 measured,out float error)
        {
            return TryObserveCombatRadiance(point,missile?58f:28f,launch,out measured,out error);
        }
        public bool TryObserveCombatRadiance(Vector3 point,float signatureDb,bool launch,out Vector3 measured,out float error)
        {
            measured=point;error=0;
            if(!isActiveAndEnabled||!CombatObservationsEnabled||Radar==null||(body!=null&&!body.IsAlive))return false;
            if(!Radar.TryObserveCombatGeometry(point,signatureDb,out error)){if(launch)Log("launch_not_observed",default,IffOutcome.None,Radar.CombatObservationDiagnostic(point));return false;}
            var delta=point-transform.position;float distance=delta.magnitude;
            if(launch&&distance<ProtectedVolumeRadiusMetres)return false;
            int hits=Physics.RaycastNonAlloc(transform.position,delta.normalized,visibilityHits,Mathf.Max(0,distance-20),~0,QueryTriggerInteraction.Ignore);
            if(hits==visibilityHits.Length)return false;
            for(int i=0;i<hits;i++){var hit=visibilityHits[i];if(hit.collider.transform.IsChildOf(transform)||hit.collider.GetComponentInParent<MechTestProjectile>()!=null)continue;if(launch)Log("launch_occluded",default,IffOutcome.None,hit.collider.name+" distance="+hit.distance);return false;}
            // Deterministic sub-resolution displacement. This is AnalyticEO, not a rendered-image classifier.
            measured+=new Vector3(.23f,-.17f,.11f)*error;return true;
        }
        public void ObserveWeaponLaunch(Guid observation,bool missile,Vector3 position,double time,float error)
        {
            if(Vector3.Distance(position,transform.position)<ProtectedVolumeRadiusMetres)return;
            bool accepted=combat.Launch(observation,missile,position,time,error,tracks,count,Radar.SimTimeSeconds);
            Log("weapon_launch_observation",default,IffOutcome.None,accepted?"unique_observed_launch_source":"unattributed_launch",observation.ToString("N"));
        }
        public void ObserveWeaponAcquisition(Guid observation,bool missile,Vector3 position,double time,float error)
        {
            bool associated=combat.Acquire(observation,missile,position,time,error,tracks,count,Radar.SimTimeSeconds);
            Log("weapon_acquired",default,IffOutcome.None,associated?"candidate_separation":"no_unique_nearby_source",observation.ToString("N"));
        }
        public void ObserveWeaponSample(Guid observation,Vector3 position,double time,float error)=>combat.Sample(observation,position,time,error,protectedContacts);
        public void ObserveWeaponImpact(Guid observation,Vector3 position,double time){BuildProtectedContacts();combat.Impact(observation,position,time,protectedContacts);}
        public static IdentityEvidenceKind BehaviorEvidenceKind(CombatBehaviorKind kind)
        {
            switch(kind){case CombatBehaviorKind.WeaponLaunch:return IdentityEvidenceKind.WeaponLaunch;case CombatBehaviorKind.FireControlLock:return IdentityEvidenceKind.FireControlIllumination;
            case CombatBehaviorKind.LaserIllumination:return IdentityEvidenceKind.LaserIllumination;case CombatBehaviorKind.DirectedJamming:return IdentityEvidenceKind.DirectedJamming;
            case CombatBehaviorKind.InvalidFriendlyAuthentication:return IdentityEvidenceKind.AuthenticationConflict;case CombatBehaviorKind.CommandHostile:return IdentityEvidenceKind.MissionHostile;default:return IdentityEvidenceKind.ObservedAttack;}
        }
        private void AcceptLocalBehavior(CombatBehaviorReport report)
        {
            if(!active.Contains(report.SourceTrack.TrackId)){Log("combat_source_track_retired",report.SourceTrack.TrackId,IffOutcome.None,report.Kind.ToString(),report.OriginalId.ToString("N"));return;}
            for(int i=0;i<count;i++)if(tracks[i].TrackId.Equals(report.SourceTrack.TrackId))
            {var source=tracks[i];double age=Math.Max(0,Radar.SimTimeSeconds-source.LastUpdateTime);source.Position=source.PredictPosition(Radar.SimTimeSeconds);source.LastPredictTime=Time.timeAsDouble;source.LastUpdateTime=Time.timeAsDouble-age;report.SourceTrack=source;break;}
            if(SubmitBehavior(report.SourceTrack.TrackId,report,"local-observation"))
            {
                int existing=behaviorOutbox.FindIndex(x=>x.OriginalId==report.OriginalId&&x.Kind==report.Kind);
                if(existing>=0)behaviorOutbox[existing]=report;else {if(behaviorOutbox.Count>=32)behaviorOutbox.RemoveAt(0);behaviorOutbox.Add(report);}
            }
        }
        private bool SubmitBehavior(TrackId track,CombatBehaviorReport report,string source)
        {
            if(!Enum.IsDefined(typeof(CombatBehaviorKind),report.Kind)||report.OriginalId==Guid.Empty)return false;
            var kind=BehaviorEvidenceKind(report.Kind);
            bool accepted=identities.Submit(track,new IdentityEvidence{OriginalId="combat/"+report.OriginalId.ToString("N")+"/"+report.Kind,Source=source,Kind=kind,Quality=report.Quality,Association=report.Association,ObservedAt=report.ObservedAt,ExpiresAt=report.ExpiresAt},Time.timeAsDouble);
            if(accepted){
                var origin=source=="authenticated-data-link"?TrackSourceMask.DataLink:report.Kind==CombatBehaviorKind.FireControlLock||report.Kind==CombatBehaviorKind.DirectedJamming?TrackSourceMask.Esm:report.Kind==CombatBehaviorKind.CommandHostile||report.Kind==CombatBehaviorKind.InvalidFriendlyAuthentication?TrackSourceMask.None:TrackSourceMask.ElectroOptical;
                if(origin!=TrackSourceMask.None){combatSources.TryGetValue(track,out var evidence);evidence.Record(origin,report.Quality*report.Association,Radar.SimTimeSeconds-(Time.timeAsDouble-report.ObservedAt));combatSources[track]=evidence;}
                snapshots[track]=identities.Evaluate(track,Time.timeAsDouble);if(kind==IdentityEvidenceKind.ObservedAttack)ConfirmedAttackReports++;Log("combat_evidence",track,IffOutcome.None,$"{report.Kind}; source={source}; quality={report.Quality:F3}; association={report.Association:F3}",report.OriginalId.ToString("N"));}
            return accepted;
        }
        public int CopyBehaviorReports(List<CombatBehaviorReport> destination)
        {destination.Clear();foreach(var report in behaviorOutbox)if(report.ExpiresAt>Time.timeAsDouble)destination.Add(report);return destination.Count;}
        public static bool IsCurrentTrustedBehaviorReport(CombatBehaviorReport report,double now,bool commander)=>
            report.OriginalId!=Guid.Empty&&Enum.IsDefined(typeof(CombatBehaviorKind),report.Kind)&&double.IsFinite(report.ObservedAt)&&double.IsFinite(report.ExpiresAt)&&report.ExpiresAt>now&&report.ObservedAt<=now+.001&&now-report.ObservedAt<=15&&(report.Kind!=CombatBehaviorKind.CommandHostile||commander);
        internal bool ReceiveBehaviorReport(CombatBehaviorReport report,bool commander)
        {
            if(Radar==null||!IsCurrentTrustedBehaviorReport(report,Time.timeAsDouble,commander)||Time.timeAsDouble-report.SourceTrack.LastUpdateTime>3)return false;
            var point=(Vector3)(float3)report.SourceTrack.PredictPosition(Time.timeAsDouble);
            float uncertainty=Mathf.Sqrt((float)Math.Max(0,report.SourceTrack.GetCovariance(0,0)+report.SourceTrack.GetCovariance(1,1)+report.SourceTrack.GetCovariance(2,2)));
            if(!float.IsFinite(uncertainty)||uncertainty>500)return false;
            int match=MechCombatBehaviorTracker.Associate(tracks,count,point,(Vector3)(float3)report.SourceTrack.Velocity,Radar.SimTimeSeconds,uncertainty,true,out float association);
            if(match<0){Log("combat_report_unassociated",default,IffOutcome.None,"no_unique_local_track",report.OriginalId.ToString("N"));return false;}
            var matched=tracks[match].TrackId;
            if(receivedBehaviorBindings.TryGetValue(report.OriginalId,out var binding)&&!binding.track.Equals(matched))return false;
            receivedBehaviorBindings[report.OriginalId]=(matched,report.ExpiresAt);
            report.Association=Mathf.Min(report.Association,association);return SubmitBehavior(matched,report,"authenticated-data-link");
        }
        private void PublishContinuousBehavior(TrackSnapshot track,CombatBehaviorKind kind,string source,double now,float quality,float association)
        {
            string key=kind+"/"+source;if(!continuousBehaviorIds.TryGetValue(key,out var id)){if(continuousBehaviorIds.Count>=128)continuousBehaviorIds.Clear();id=Guid.NewGuid();continuousBehaviorIds.Add(key,id);}
            track.Position=track.PredictPosition(Radar.SimTimeSeconds);track.LastPredictTime=track.LastUpdateTime=now;
            AcceptLocalBehavior(new CombatBehaviorReport{OriginalId=id,Kind=kind,SourceTrack=track,ObservedAt=now,ExpiresAt=now+3,Quality=quality,Association=association});
        }
        private void ObserveRadiation(double now)
        {
            int length=Radar.CopyWarningObservations(warnings);
            for(int i=0;i<length;i++)
            {
                var w=warnings[i];if(!w.IsMainlobe||Radar.SimTimeSeconds-w.Timestamp>.5||w.WaveformConfidence<.7f)continue;
                bool laser=w.EmissionType==EmissionType.Laser,jam=w.EmissionType==EmissionType.Jamming;
                if(!laser&&!jam)continue;
                if(jam&&(Radar.GetStatusSnapshot().Flags&RadarStatusFlags.Jammed)==0)continue;
                int match=UniqueBearing((Vector3)w.Direction,5);if(match<0)continue;
                string key=tracks[match].TrackId+"/"+w.EmissionType;
                if(!radiationHistory.TryGetValue(key,out var interval)||now-interval.last>.5)interval=(now,now);
                interval.last=now;radiationHistory[key]=interval;
                if(now-interval.first<1)continue;
                PublishContinuousBehavior(tracks[match],laser?CombatBehaviorKind.LaserIllumination:CombatBehaviorKind.DirectedJamming,key,now,.8f,.9f);
            }
        }
        private void ApplyCombatSources(ref TrackSnapshot track)
        {
            if(!combatSources.TryGetValue(track.TrackId,out var sources)||Radar==null)return;
            foreach(var kind in new[]{TrackSourceMask.ElectroOptical,TrackSourceMask.Esm,TrackSourceMask.DataLink})
            {var e=sources.Get(kind);if(e.Score(Radar.SimTimeSeconds)<0)continue;track.SourceMask|=kind;track.SourceEvidence.Record(kind,e.Confidence,e.Timestamp);}
        }
        private void ObserveInvalidAuthentication(IffReply reply,double now)
        {
            if(reply.Domain!=Credential.Domain&&Array.IndexOf(AlliedDomains,reply.Domain)<0)return;
            int match=UniqueBearing(pendingBearing,5);if(match<0)return;var track=tracks[match];
            if(!invalidReplies.TryGetValue(track.TrackId,out var events)){events=new Dictionary<Guid,double>();invalidReplies.Add(track.TrackId,events);}
            var stale=new List<Guid>();foreach(var e in events)if(now-e.Value>30)stale.Add(e.Key);foreach(var id in stale)events.Remove(id);
            events[reply.Transaction]=now;if(events.Count<3)return;
            PublishContinuousBehavior(track,CombatBehaviorKind.InvalidFriendlyAuthentication,"invalid-iff/"+track.TrackId,now,.8f,.9f);
        }
    }
}
