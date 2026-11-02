using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using Unity.Mathematics;
namespace ACunicombat.FireControl
{
        [Serializable] public struct FriendlyMissileLinkReport
        {
            public Unity.Collections.FixedString128Bytes KnownModel;
            public bool Active;
            public bool HasLaunchTimestamp;
            public double LaunchTimestamp;
            public LinkTerminalId SourceTerminal;
            public double3 Position;
            public double3 Velocity;
            public MissileMissionRole MissionRole;
            public float MissionRoleConfidence;
            public double StateTimestamp;
            public ulong Sequence;
        }
        public sealed class OwnLaunchRecord
        {
            public Unity.Collections.FixedString128Bytes KnownModel;public Guid Id;public double3 Position,Velocity;public double Time,LaunchTime;public TrackId Track,ContactId,AssignedTarget;public MissileMissionRole Role;public LinkTerminalId Terminal;
            private string associationKey;private Guid associationKeyId;
            // Batch ordering key ("L" + Id:N), built once per launch instead of every evaluation.
            internal string AssociationKey{get{if(associationKey==null||associationKeyId!=Id){associationKeyId=Id;associationKey="L"+Id.ToString("N");}return associationKey;}}
        }
    // Transport adapters submit authenticated reports; they do not decide contact identity.
    // All association is evaluated against one immutable pre-publication batch.
    public sealed class MissileContactManager
    {
        public const int MaximumContacts=128;
        private int nextOwnIdentity=4096;
        public int NextOwnIdentity {get=>nextOwnIdentity;set=>nextOwnIdentity=value;}
        public event Action<MissileContactReplayFrame> FrameEvaluated;
        public TrackId AllocateOwnIdentity()=>new TrackId(new Handle(nextOwnIdentity++,1));
        private const int MissileLinkIndexStart=0;
        private const double FriendlyMissileReportRetentionSeconds=1.0;
        private const double FriendlyMissileIdentityCoastSeconds=2.5;
        private const float FriendlyMissileAssociationMaximumMeters=2000;
        private const float FriendlyMissileAssociationVelocityGateMps=180;
        private const double FriendlyMissileAssociationScoreSeparation=.35;
        public readonly FriendlyMissileLinkReport[] Reports=new FriendlyMissileLinkReport[64];
        public readonly FriendlyMissileIdentityAssociation[] Associations=new FriendlyMissileIdentityAssociation[64];
        public readonly Dictionary<Guid,OwnLaunchRecord> OwnLaunches=new();
        public readonly Dictionary<TrackId,FriendlyMissileLinkReport> Retired=new();
        public readonly MissileTrackFusionState FusionState=new();
        public readonly MissileIntentEstimatorState IntentState=new();
        public Action<LinkTerminalId,FriendlyIdentityResolution,TrackId,TrackId,double,double> IdentityResolved;
        public Action<TrackId,double3,double3,double> TelemetryRefinement;
        public Action<TrackId> CloseAssociation;

        public void RegisterOwnLaunch(Guid id,double3 position,double3 velocity,MissileMissionRole role,double time,TrackId assignedTarget=default,Unity.Collections.FixedString128Bytes model=default)
        {
            OwnLaunches[id]=new OwnLaunchRecord{Id=id,KnownModel=model,ContactId=AllocateOwnIdentity(),Position=position,Velocity=velocity,Role=role,Time=time,LaunchTime=time,AssignedTarget=assignedTarget};
        }
        public void BindOwnTerminal(Guid id,LinkTerminalId terminal)
        {if(terminal.IsValid&&OwnLaunches.TryGetValue(id,out var own))own.Terminal=terminal;}
        public void EndOwnLaunch(Guid id,double time)
        {
            if(!OwnLaunches.TryGetValue(id,out var own))return;
            var ended=new FriendlyMissileLinkReport{StateTimestamp=time,MissionRole=own.Role,MissionRoleConfidence=1};
            if(own.ContactId.IsValid)Retired[own.ContactId]=ended;
            if(own.Track.IsValid){Retired[own.Track]=ended;CloseAssociation?.Invoke(own.Track);}
            OwnLaunches.Remove(id);
        }
        private FriendlyMissileLinkReport[] friendlyMissileReports=>Reports;
        private FriendlyMissileIdentityAssociation[] friendlyMissileIdentityAssociations=>Associations;
        private Dictionary<Guid,OwnLaunchRecord> ownLaunchRecords=>OwnLaunches;
        private Dictionary<TrackId,FriendlyMissileLinkReport> retiredFriendlyMissileTracks=>Retired;
        private readonly bool[] friendlyMissileTrackMatched=new bool[MaximumContacts];
        private MissileTrackSnapshot[] missileTrackCache;
        private int missileTrackCacheCount;
        public int CopyFriendlyIntentCandidates(double now,double maximumEvidenceAge,FriendlyMissileIntentCandidate[] destination)
        {
            int count=0;
            for(int i=0;i<missileTrackCacheCount&&count<destination.Length;i++)
            {
                var contact=missileTrackCache[i];
                if(!contact.IsFriendly||now<contact.LastEvidenceTime||now-contact.LastEvidenceTime>maximumEvidenceAge||Retired.ContainsKey(contact.TrackId))continue;
                destination[count++]=new FriendlyMissileIntentCandidate{TrackId=contact.TrackId,Position=contact.Position,Velocity=contact.Velocity,StateTime=contact.LastUpdateTime,LastEvidenceTime=contact.LastEvidenceTime};
            }
            return count;
        }
        private struct Decision {public int ReportIndex,TrackIndex;public OwnLaunchRecord Own;public double Score;public FriendlyIdentityResolution Resolution;}
        private struct IdentityRow {public int ReportIndex;public OwnLaunchRecord Own;public FriendlyMissileLinkReport Evidence;public FriendlyMissileIdentityAssociation Previous;public string Key;}
        // Reused per evaluation; the decision list is consumed before the next evaluation.
        private readonly List<IdentityRow> identityRows=new List<IdentityRow>();private readonly HashSet<Guid> coveredOwnLaunches=new HashSet<Guid>();
        private readonly List<Decision> identityDecisions=new List<Decision>();
        private readonly Dictionary<uint,string> reportKeys=new Dictionary<uint,string>();
        private static readonly Comparison<IdentityRow> RowOrder=(a,b)=>string.CompareOrdinal(a.Key,b.Key);
        private List<Decision> AssociateReports(double now)
        {
            var rows=identityRows;rows.Clear();var coveredOwn=coveredOwnLaunches;coveredOwn.Clear();
            for(int i=0;i<Reports.Length;i++)
            {
                var report=Reports[i];if(!report.Active)continue;
                OwnLaunchRecord own=null;
                foreach(var local in OwnLaunches.Values)if(local.Terminal.IsValid&&local.Terminal.Equals(report.SourceTerminal)){own=local;coveredOwn.Add(local.Id);break;}
                var previous=Associations[i];
                if(!previous.HasTrack&&own!=null&&own.Track.IsValid)previous=new FriendlyMissileIdentityAssociation{TrackId=own.Track,LastAuthenticatedTime=own.Time,LastSupportedTime=own.Time};
                rows.Add(new IdentityRow{ReportIndex=i,Own=own,Evidence=report,Previous=previous,Key=ReportKey(report.SourceTerminal.Value.Packed)});
            }
            foreach(var own in OwnLaunches.Values)
            {
                if(coveredOwn.Contains(own.Id))continue;
                rows.Add(new IdentityRow{ReportIndex=-1,Own=own,Key=own.AssociationKey,
                    Evidence=new FriendlyMissileLinkReport{Active=true,Position=own.Position,Velocity=own.Velocity,StateTimestamp=own.Time,HasLaunchTimestamp=true,LaunchTimestamp=own.LaunchTime},
                    Previous=new FriendlyMissileIdentityAssociation{TrackId=own.Track,LastAuthenticatedTime=own.Time,LastSupportedTime=own.Time}});
            }
            rows.Sort(RowOrder);
            int n=rows.Count,m=missileTrackCacheCount;
            var costs=new double[n,m];var scores=new double[n,m];
            for(int r=0;r<n;r++)for(int c=0;c<m;c++)
            {
                costs[r,c]=OneToOneAssignment.Rejected;
                var row=rows[r];var report=row.Evidence;var track=missileTrackCache[c];
                if(friendlyMissileTrackMatched[c]||now<report.StateTimestamp||
                   (report.HasLaunchTimestamp&&track.FirstObservationTime>0&&track.FirstObservationTime+1e-4<report.LaunchTimestamp))continue;
                if(!MissileTrackFusion.TryScoreFriendlyDataLinkAssociation(in track,report.Position,report.Velocity,
                    report.StateTimestamp,now,FriendlyMissileReportRetentionSeconds,FriendlyMissileAssociationMaximumMeters,
                    FriendlyMissileAssociationVelocityGateMps,out double score))continue;
                scores[r,c]=score;costs[r,c]=score-(row.Previous.TrackId.Equals(track.TrackId)?100:0);
            }
            var assignment=OneToOneAssignment.Solve(costs,FriendlyMissileAssociationScoreSeparation,out _);
            var occupied=new bool[m];foreach(int index in assignment)if(index>=0)occupied[index]=true;
            var result=identityDecisions;result.Clear();
            for(int r=0;r<n;r++)
            {
                var row=rows[r];int index=row.ReportIndex,selected=assignment[r];var report=row.Evidence;var association=row.Previous;
                var decision=new Decision{ReportIndex=index,Own=row.Own,TrackIndex=selected,Score=double.PositiveInfinity,Resolution=FriendlyIdentityResolution.None};
                if(selected>=0)
                {
                    association.TrackId=missileTrackCache[selected].TrackId;association.LastAuthenticatedTime=association.LastSupportedTime=now;
                    association.LastAssociationScore=scores[r,selected];decision.Score=association.LastAssociationScore;decision.Resolution=FriendlyIdentityResolution.Authenticated;
                }
                else if(association.HasTrack)
                {
                    for(int c=0;c<m;c++)
                    {
                        var track=missileTrackCache[c];
                        if(occupied[c]||friendlyMissileTrackMatched[c]||!track.TrackId.Equals(association.TrackId))continue;
                        if(report.HasLaunchTimestamp&&track.FirstObservationTime>0&&track.FirstObservationTime+1e-4<report.LaunchTimestamp)break;
                        // Our own fire-control system knows this launch for its whole lifetime.
                        // Once a sensor track is authenticated, a telemetry dropout must not
                        // publish the same physical weapon again as an unknown threat.
                        bool persistentOwnBinding=row.Own!=null&&row.Own.Track.Equals(association.TrackId);
                        double support=math.max(association.LastSupportedTime,association.LastAuthenticatedTime);
                        if(!persistentOwnBinding&&now-support>FriendlyMissileIdentityCoastSeconds)break;
                        if(now-report.StateTimestamp>FriendlyMissileReportRetentionSeconds&&track.KinematicUpdateTime>0&&now-track.KinematicUpdateTime<=.25)association.LastSupportedTime=now;
                        decision.TrackIndex=c;decision.Score=association.LastAssociationScore;decision.Resolution=FriendlyIdentityResolution.Coasting;occupied[c]=true;break;
                    }
                    if(decision.TrackIndex<0)association.Clear();
                }
                if(index>=0)Associations[index]=association;
                else row.Own.Track=association.TrackId;
                result.Add(decision);
            }
            return result;
        }
        private string ReportKey(uint packed)
        {
            if(!reportKeys.TryGetValue(packed,out var key)){if(reportKeys.Count>=1024)reportKeys.Clear();key="R"+packed.ToString("D10");reportKeys[packed]=key;}
            return key;
        }
        private void ApplyLocalDecision(in Decision decision)
        {
            if(decision.TrackIndex<0)return;
            var own=decision.Own;var contact=missileTrackCache[decision.TrackIndex];
            contact.AssociatedSensorTrackId=contact.TrackId;contact.TrackId=own.ContactId;
            contact.OwnLaunchIdentity=own.Id;contact.AssignedTargetTrackId=own.AssignedTarget;
            contact.FirstObservationTime=own.LaunchTime;contact.Affiliation=TrackAffiliation.Friendly;contact.IdentityConfidence=1;
            contact.MissionRole=own.Role;contact.MissionRoleConfidence=1;contact.MissionRoleEvidence=MissileMissionRoleEvidence.OwnLaunch;
            contact.SourceMask|=MissileTrackSourceMask.OwnLaunch;
            if(contact.KinematicUpdateTime>own.Time){own.Position=contact.Position;own.Velocity=contact.Velocity;own.Time=contact.LastUpdateTime;}
            missileTrackCache[decision.TrackIndex]=contact;
        }
        public int ResolveIdentities(double now, double3 observerPosition, MissileTrackSnapshot[] contacts, int contactCount)
        {
            var replay=FrameEvaluated!=null?MissileContactReplayFrame.Capture(this,now,observerPosition,contacts,contactCount):null;
            missileTrackCache=contacts;missileTrackCacheCount=contactCount;
            // Sensor products contribute kinematics/classification, not identity
            // copied from an earlier published contact or another consumer.
            for(int i=0;i<contactCount;i++)
            {
                ref var observation=ref missileTrackCache[i];
                observation.OwnLaunchIdentity=Guid.Empty;observation.AssignedTargetTrackId=default;
                observation.ReportingTerminal=default;observation.AssociatedSensorTrackId=default;
                observation.SourceMask&=~MissileTrackSourceMask.OwnLaunch;
                observation.Affiliation=TrackAffiliation.Unknown;observation.IdentityConfidence=0;
            }
            Array.Clear(
                friendlyMissileTrackMatched,
                0,
                friendlyMissileTrackMatched.Length);
            for (var i = 0; i < missileTrackCacheCount; i++)
            {
                if (!retiredFriendlyMissileTracks.TryGetValue(
                        missileTrackCache[i].TrackId, out var retiredReport))
                {
                    continue;
                }

                // Preserve friendly IFF without offering a spent track to another
                // missile's association. HUD visibility is handled separately.
                friendlyMissileTrackMatched[i] = true;
                MissileTrackFusion.ApplyFriendlyDataLinkIdentity(
                    ref missileTrackCache[i], 0.0,
                    retiredReport.MissionRole, retiredReport.MissionRoleConfidence);
            }
            var batch=AssociateReports(now);
            foreach(var decision in batch)
            {
                if(decision.ReportIndex<0){ApplyLocalDecision(in decision);continue;}
                int reportIndex=decision.ReportIndex;
                var report=friendlyMissileReports[reportIndex];
                var resolution=decision.Resolution;
                int trackIndex=decision.TrackIndex;
                double score=decision.Score;
                var resolvedTrackId = trackIndex >= 0 &&
                                      trackIndex < missileTrackCacheCount
                    ? missileTrackCache[trackIndex].TrackId
                    : default;
                IdentityResolved?.Invoke(
                    report.SourceTerminal,
                    resolution,
                    friendlyMissileIdentityAssociations[reportIndex].TrackId,
                    resolvedTrackId,
                    score,
                    Math.Max(0.0, now - report.StateTimestamp));
                // Authenticated telemetry is an independently observable missile, not merely
                // a temporary colour tag on whichever radar plot happens to be closest.
                double age=now-report.StateTimestamp;
                bool matched=resolution!=FriendlyIdentityResolution.None&&resolution!=FriendlyIdentityResolution.Ambiguous&&trackIndex>=0;
                if(age<0 || (age>FriendlyMissileIdentityCoastSeconds && !matched))continue;
                int output=matched?trackIndex:missileTrackCacheCount;
                if(output>=missileTrackCache.Length)continue;
                var observed=matched?missileTrackCache[trackIndex]:default;
                var sensorTrack=matched?observed.TrackId:default;
                if(matched&&resolution==FriendlyIdentityResolution.Authenticated)TelemetryRefinement?.Invoke(sensorTrack,report.Position,report.Velocity,report.StateTimestamp);
                observed.TrackId=new TrackId(new Handle(2000+report.SourceTerminal.Value.Index,report.SourceTerminal.Value.Generation));
                observed.AssociatedSensorTrackId=sensorTrack;
                observed.ReportingTerminal=report.SourceTerminal;observed.KnownModel=report.KnownModel;
                // A sensor association cannot transfer another weapon's local launch
                // GUID into this terminal report (the HUD would suppress the ally).
                observed.OwnLaunchIdentity=Guid.Empty;
                observed.SourceMask&=~MissileTrackSourceMask.OwnLaunch;
                foreach(var own in ownLaunchRecords.Values)
                {
                    if(!own.Terminal.Equals(report.SourceTerminal))continue;
                    observed.OwnLaunchIdentity=own.Id;if(!own.KnownModel.IsEmpty)observed.KnownModel=own.KnownModel;
                    observed.FirstObservationTime=own.LaunchTime;
                    observed.AssignedTargetTrackId=own.AssignedTarget;
                    if(own.ContactId.IsValid)observed.TrackId=own.ContactId;
                    observed.SourceMask|=MissileTrackSourceMask.OwnLaunch;
                    if(report.StateTimestamp>=own.Time)
                    {own.Position=report.Position;own.Velocity=report.Velocity;own.Time=report.StateTimestamp;}
                    if(matched && resolution==FriendlyIdentityResolution.Authenticated)own.Track=sensorTrack;
                    break;
                }
                // Identity can coast longer than the report's useful kinematics.
                // Never replace a fresh radar estimate with seconds-old constant-velocity
                // telemetry just to keep its friendly identity alive.
                double reportUncertainty=3+300*age*age;
                bool useReportKinematics=!matched || (age<=FriendlyMissileReportRetentionSeconds &&
                    reportUncertainty<observed.PositionUncertaintyMeters);
                if(useReportKinematics)
                {
                    observed.Position=report.Position+report.Velocity*age;
                    observed.Velocity=report.Velocity;observed.VelocityUncertaintyMetersPerSecond=(float)(3+600*age);observed.RadialVelocityUncertaintyMetersPerSecond=observed.VelocityUncertaintyMetersPerSecond;observed.ObserverLineOfSight=math.normalizesafe((float3)(observed.Position-observerPosition),new float3(0,0,1));observed.Acceleration=double3.zero;observed.Jerk=double3.zero;
                    observed.PositionUncertaintyMeters=(float)(3+300*age*age);observed.CpaUncertaintyMeters=observed.PositionUncertaintyMeters;
                    observed.HasPositionCovariance=true;observed.PositionCovariance=double3x3.identity*(observed.PositionUncertaintyMeters*observed.PositionUncertaintyMeters/3.0);
                    observed.LastEvidenceTime=report.StateTimestamp;observed.KinematicUpdateTime=report.StateTimestamp;observed.LastUpdateTime=now;
                    observed.Quality=MissileTrackQuality.Warning;observed.Confirmed=true;observed.Confidence=1;
                }
                MissileTrackFusion.ApplyFriendlyDataLinkIdentity(ref observed,0,report.MissionRole,report.MissionRoleConfidence);
                missileTrackCache[output]=observed;friendlyMissileTrackMatched[output]=true;
                if(!matched)missileTrackCacheCount++;
            }
            AppendOwnContacts(now,observerPosition);
            if(replay!=null)
            {
                replay.expected=new MissileReplayTrack[missileTrackCacheCount];
                for(int i=0;i<missileTrackCacheCount;i++)replay.expected[i]=MissileReplayTrack.From(missileTrackCache[i]);
                replay.lossless=ReplayValueCodec.Encode(replay);
                FrameEvaluated?.Invoke(replay);
            }
            return missileTrackCacheCount;
        }

        private void AppendOwnContacts(double now,double3 observerPosition)
        {
            foreach(var own in OwnLaunches.Values)
            {
                bool found=false;
                for(int i=0;i<missileTrackCacheCount;i++)
                    if(missileTrackCache[i].OwnLaunchIdentity==own.Id)
                    {
                        if(own.ContactId.IsValid)
                        {
                            var contact=missileTrackCache[i];
                            if(!contact.AssociatedSensorTrackId.IsValid)contact.AssociatedSensorTrackId=contact.TrackId;
                            contact.TrackId=own.ContactId;missileTrackCache[i]=contact;
                        }
                        found=true;break;
                    }
                double age=now-own.Time;
                if(found||age<0||age>FriendlyMissileIdentityCoastSeconds||missileTrackCacheCount>=missileTrackCache.Length)continue;
                if(!own.ContactId.IsValid)own.ContactId=AllocateOwnIdentity();
                double sigma=3+300*age*age;
                var position=own.Position+own.Velocity*age;
                missileTrackCache[missileTrackCacheCount++]=new MissileTrackSnapshot
                {
                    KnownModel=own.KnownModel,TrackId=own.ContactId,ReportingTerminal=own.Terminal,OwnLaunchIdentity=own.Id,AssignedTargetTrackId=own.AssignedTarget,
                    Position=position,Velocity=own.Velocity,PositionUncertaintyMeters=(float)sigma,
                    HasPositionCovariance=true,PositionCovariance=double3x3.identity*(sigma*sigma/3),
                    VelocityUncertaintyMetersPerSecond=(float)(3+600*age),
                    ObserverLineOfSight=math.normalizesafe((float3)(position-observerPosition),new float3(0,0,1)),
                    Affiliation=TrackAffiliation.Friendly,IdentityConfidence=1,Confidence=1,
                    MissionRole=own.Role,MissionRoleConfidence=1,MissionRoleEvidence=MissileMissionRoleEvidence.OwnLaunch,
                    SourceMask=MissileTrackSourceMask.OwnLaunch,Quality=MissileTrackQuality.Warning,Confirmed=true,
                    FirstObservationTime=own.LaunchTime,LastEvidenceTime=own.Time,KinematicUpdateTime=own.Time,LastUpdateTime=now
                };
            }
        }
    }
}
