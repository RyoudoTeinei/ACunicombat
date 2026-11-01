using System;
using System.IO;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using UnityEngine;
using Unity.Mathematics;
namespace ACunicombat.Mechs
{
    [DisallowMultipleComponent]
    public sealed partial class MechIdentityController : MonoBehaviour, ICombatObservationReceiver
    {
        [Header("Independent IFF equipment")]
        public bool InterrogatorEnabled=true,ResponderEnabled=true;
        public IffReplyPolicy ReplyPolicy=IffReplyPolicy.TrustedOnly;
        public string AuthenticationDomain="";
        public string[] AuthorizedDomains=Array.Empty<string>(),NeutralDomains=Array.Empty<string>(),AlliedDomains=Array.Empty<string>(),ThirdPartyDomains=Array.Empty<string>();
        public float RadioRangeMetres=1000000,TransmitPowerDbm=40,AntennaGainDb=16,ReceiveSensitivityDbm=-115,InterferenceDb;
        public double FrequencyHz=1030000000,ProcessingSeconds=.05,PacketSeconds=.02;
        public float BeamWidthDegrees=12;
        [Header("Mission rules (training permits unknown targets)")]
        public bool AllowUnknownPlatformAttack=true;
        public bool LogDecisions=true;
        public MechRadarController Radar {get;private set;}
        public IffCredential Credential {get;private set;}
        public bool ReceiverAvailable=>isActiveAndEnabled&&(InterrogatorEnabled||(ResponderEnabled&&ReplyPolicy!=IffReplyPolicy.Off))&&(body==null||body.IsAlive);
        public bool TransmitterAvailable=>isActiveAndEnabled&&(InterrogatorEnabled||(ResponderEnabled&&ReplyPolicy!=IffReplyPolicy.Off))&&(body==null||body.IsAlive);
        private CombatTestTarget body;
        private MechIffProtocol protocol;
        private readonly MechObservedFormationIdentity formationIdentity=new MechObservedFormationIdentity();
        private readonly List<MechObservedFormationIdentity.Inference> formationInferences=new List<MechObservedFormationIdentity.Inference>();
        private double nextFormationPoll;
        private readonly MechIdentityEvidence identities=new MechIdentityEvidence();
        private readonly TrackSnapshot[] tracks=new TrackSnapshot[96];
        private readonly HashSet<TrackId> active=new HashSet<TrackId>();
        private readonly Dictionary<Guid,double> receivedQueries=new Dictionary<Guid,double>();
        private readonly Dictionary<TrackId,ContactIdentitySnapshot> snapshots=new Dictionary<TrackId,ContactIdentitySnapshot>();
        private readonly HashSet<Guid> revokedCredentials=new HashSet<Guid>();
        private readonly Dictionary<Guid,TrackId> certifiedBindings=new Dictionary<Guid,TrackId>();
        private readonly Dictionary<TrackId,(double at,double until)> iffEvidence=new Dictionary<TrackId,(double,double)>();
        public int AuthenticationCount {get;private set;}
        private int count,queryCursor;private double nextPoll;private Vector3 pendingBearing;private StreamWriter log;
        private readonly ThreatSnapshot[] threats=new ThreatSnapshot[32];
        public string LogPath {get;private set;}
        public static string LogDirectoryOverride;
        private void Start()
        {
            Radar=GetComponent<MechRadarController>();body=GetComponent<CombatTestTarget>();
            // Early scenario provisioning ONLY. Runtime authentication and fusion never compare team IDs.
            string domain=string.IsNullOrWhiteSpace(AuthenticationDomain)?"mission-domain-"+(body!=null?body.TeamId:0):AuthenticationDomain;
            Credential=new IffCredential(Guid.NewGuid(),domain,Time.timeAsDouble+86400, IffAuthority.NewSecret());MechIffRadio.Authority.Register(Credential);
            protocol=new MechIffProtocol(MechIffRadio.Authority,Credential);protocol.Result+=OnIffResult;
            identities.Changed+=(id,state)=>Log("identity_transition",id,state.Iff,state.Relation+":"+state.Reason);
            MechIffRadio.Register(this);
            MechCombatObservationSensor.Register(this);combat.Report+=AcceptLocalBehavior;combat.Diagnostic+=(id,reason)=>Log(reason.StartsWith("confirmed_attack:",StringComparison.Ordinal)?"attack_confirmation":"attack_trajectory_pending",default,IffOutcome.None,reason,id.ToString("N"));
        }
        private void LateUpdate()
        {
            using var costScope = ACunicombat.Simulation.Core.SimulationCostProbe.Measure(ACunicombat.Simulation.Core.SimulationCostProbe.SystemId.Identity);
            if(protocol==null)return;
            double now=Time.timeAsDouble;MechIffRadio.Tick(now);protocol.Tick(now);
            if(Radar==null||Radar.TelemetryDriver==null)return;
            if(now<nextPoll)return;nextPoll=now+.2;
            count=Radar.CopyIdentityInputTracks(tracks);active.Clear();identities.AllowUnknownPlatformAttack=AllowUnknownPlatformAttack;
            for(int i=0;i<count;i++)
            {
                var t=tracks[i];if(t.ObjectClass==TrackObjectClass.Missile||t.ObjectClass==TrackObjectClass.Decoy)continue;active.Add(t.TrackId);
                var link=t.SourceEvidence.DataLink;
                if(!identities.HasDecidedIdentity(t.TrackId)&&t.Affiliation==TrackAffiliation.Friendly&&t.IdentityConfidence>=.8f&&(t.SourceMask&TrackSourceMask.DataLink)!=0&&link.Valid&&Radar.SimTimeSeconds-link.Timestamp<=3)
                    identities.Submit(t.TrackId,new IdentityEvidence{OriginalId="cooperative-self-report",Source="authenticated-link",Kind=IdentityEvidenceKind.CooperativeFriendly,Quality=t.IdentityConfidence,Association=1,ObservedAt=now,ExpiresAt=now+3},now);
            }
            protocol.CancelMissing(active,now);
            var stale=new List<TrackId>();foreach(var id in snapshots.Keys)if(!active.Contains(id))stale.Add(id);foreach(var id in stale)snapshots.Remove(id);
            stale.Clear();foreach(var pair in iffEvidence)if(pair.Value.until<now)stale.Add(pair.Key);foreach(var id in stale)iffEvidence.Remove(id);
            var retiredSubjects=new List<Guid>();foreach(var pair in certifiedBindings)if(!active.Contains(pair.Value)&&!iffEvidence.ContainsKey(pair.Value))retiredSubjects.Add(pair.Key);foreach(var subject in retiredSubjects)certifiedBindings.Remove(subject);
            PollCombatObservations(now);
            ObserveIllumination(now);
            foreach(var id in active)snapshots[id]=identities.Evaluate(id,now);identities.Prune(now);
            if(now>=nextFormationPoll)
            {
                nextFormationPoll=now+.5;
                formationIdentity.Update(tracks,count,snapshots,Radar.SimTimeSeconds,formationInferences);
                foreach(var inferred in formationInferences)
                {
                    if(identities.HasDecidedIdentity(inferred.Member))continue;
                    identities.Submit(inferred.Member,new IdentityEvidence{OriginalId="formation/"+inferred.HostileAnchor,Source="observed-formation",Kind=IdentityEvidenceKind.FormationHostile,Quality=.95f,Association=1,ObservedAt=now,ExpiresAt=now+3},now);
                    snapshots[inferred.Member]=identities.Evaluate(inferred.Member,now);
                    Log("formation_hostile",inferred.Member,IffOutcome.None,"observed_anchor="+inferred.HostileAnchor);
                }
            }
            if(InterrogatorEnabled&&TransmitterAvailable&&count>0)
            {
                for(int n=0;n<count;n++)
                {
                    var t=tracks[(queryCursor+n)%count];if(!active.Contains(t.TrackId)||identities.HasDecidedIdentity(t.TrackId)||t.Lifecycle==TrackLifecycle.Tentative)continue;
                    var position=(Vector3)(float3)t.PredictPosition(Radar.SimTimeSeconds);var delta=position-transform.position;
                    var query=protocol.Begin(t.TrackId,delta.magnitude,now);if(query==null)continue;
                    queryCursor=(queryCursor+n+1)%count;Log("query_sent",t.TrackId,IffOutcome.Waiting,$"deadline={query.Deadline:F4}; estimatedRange={delta.magnitude:F1}",query.Transaction.ToString("N"));MechIffRadio.Interrogate(this,query,delta.normalized,now);break;
                }
            }
        }
        public bool RequestInterrogation(ObserverTrackRef reference,out string reason)
        {
            reason="IFF 未就绪";
            if(protocol==null||Radar==null||!InterrogatorEnabled||!TransmitterAvailable)return false;
            if(!Radar.TryGetTrack(reference,out var track)||track.ObjectClass==TrackObjectClass.Missile||track.ObjectClass==TrackObjectClass.Decoy||track.Lifecycle==TrackLifecycle.Dropped){reason="目标观测无效";return false;}
            var delta=(Vector3)(float3)track.PredictPosition(Radar.SimTimeSeconds)-transform.position;
            double now=Time.timeAsDouble;var query=protocol.Begin(track.TrackId,delta.magnitude,now);
            if(query==null){reason="正在问询、冷却中或通道已满";return false;}
            identities.SetIff(track.TrackId,IffOutcome.Waiting);snapshots.Remove(track.TrackId);
            MechIffRadio.Interrogate(this,query,delta.normalized,now);reason="已发出问询，等待应答";
            Log("manual_query_sent",track.TrackId,IffOutcome.Waiting,reason,query.Transaction.ToString("N"));return true;
        }
        public void ReceiveQuery(MechIdentityController requester,IffChallenge query,double now)
        {
            if(!ReceiverAvailable||!ResponderEnabled||ReplyPolicy==IffReplyPolicy.Off)return;
            if(!MechIffRadio.Authority.VerifyChallenge(query,now)){Log("query_rejected",default,IffOutcome.InvalidProof,"requester_authentication");return;}
            var old=new List<Guid>();foreach(var e in receivedQueries)if(e.Value<now)old.Add(e.Key);foreach(var key in old)receivedQueries.Remove(key);
            if(receivedQueries.ContainsKey(query.Transaction)||receivedQueries.Count>=256){Log("query_rejected",default,IffOutcome.Replay,"duplicate_or_capacity");return;}
            receivedQueries[query.Transaction]=query.Deadline;
            if(ReplyPolicy==IffReplyPolicy.Silent){Log("query_silent",default,IffOutcome.None,"emission_control");return;}
            bool accepted=query.Domain==Credential.Domain||Array.IndexOf(AlliedDomains,query.Domain)>=0||(ReplyPolicy==IffReplyPolicy.AuthorizedDomains&&Array.IndexOf(AuthorizedDomains,query.Domain)>=0);
            if(!accepted){Log("query_rejected",default,IffOutcome.Untrusted,"unauthorized_domain");return;}
            MechIffRadio.Respond(this,requester,query,now);Log("reply_scheduled",default,IffOutcome.None,query.Transaction.ToString("N"));
        }
        public void ReceiveReply(IffReply reply,Vector3 bearing,double now){if(protocol==null)return;pendingBearing=bearing;protocol.Receive(reply,now);}
        private void OnIffResult(TrackId asked,IffOutcome outcome,IffReply reply)
        {
            double now=Time.timeAsDouble;identities.SetIff(asked,outcome==IffOutcome.Authenticated?IffOutcome.AuthenticatedUnassociated:outcome);Log("iff_decision",asked,outcome,reply!=null?reply.Subject.ToString("N"):"",protocol?.LastTransaction.ToString("N"));
            if(outcome==IffOutcome.InvalidProof&&reply!=null)ObserveInvalidAuthentication(reply,now);
            if(outcome!=IffOutcome.Authenticated||reply==null)return;
            if(revokedCredentials.Contains(reply.Subject)){identities.SetIff(asked,IffOutcome.Untrusted);Log("revoked_response",asked,IffOutcome.Untrusted,reply.Subject.ToString("N"),reply.Transaction.ToString("N"));return;}
            int match=AssociateReply(tracks,count,reply,transform.position,pendingBearing,now,Radar.SimTimeSeconds,out float association);
            if(match<0){identities.SetIff(asked,IffOutcome.AuthenticatedUnassociated);Log("iff_association",asked,IffOutcome.AuthenticatedUnassociated,"ambiguous_or_outside_gate");return;}
            var id=tracks[match].TrackId;
            if(certifiedBindings.TryGetValue(reply.Subject,out var previous)&&!previous.Equals(id)&&active.Contains(previous))
            {identities.SetIff(asked,IffOutcome.AuthenticatedUnassociated);Log("iff_association",asked,IffOutcome.AuthenticatedUnassociated,"identity_already_bound_to_live_track");return;}
            bool own=reply.Domain==Credential.Domain||Array.IndexOf(AlliedDomains,reply.Domain)>=0;
            bool neutral=Array.IndexOf(NeutralDomains,reply.Domain)>=0;
            bool third=Array.IndexOf(ThirdPartyDomains,reply.Domain)>=0;
            if(!own&&!neutral&&!third){identities.SetIff(asked,IffOutcome.Untrusted);return;}
            certifiedBindings[reply.Subject]=id;AuthenticationCount++;iffEvidence[id]=(now,Math.Min(reply.Expires,now+20));
            identities.Submit(id,new IdentityEvidence{OriginalId="iff/"+reply.Subject.ToString("N"),Source="iff",Kind=own?IdentityEvidenceKind.FriendlyAuthentication:neutral?IdentityEvidenceKind.NeutralAuthentication:IdentityEvidenceKind.ThirdPartyAuthentication,Quality=1,Association=association,ObservedAt=now,ExpiresAt=Math.Min(reply.Expires,now+20)},now);
            identities.SetIff(id,IffOutcome.Authenticated);Log("iff_associated",id,IffOutcome.Authenticated,$"{reply.Domain}; association={association:F3}; expires={Math.Min(reply.Expires,now+20):F3}");
        }
        public static int AssociateReply(TrackSnapshot[] input,int length,IffReply reply,Vector3 ownPosition,Vector3 bearing,double now,double trackTime)=>AssociateReply(input,length,reply,ownPosition,bearing,now,trackTime,out _);
        public static int AssociateReply(TrackSnapshot[] input,int length,IffReply reply,Vector3 ownPosition,Vector3 bearing,double now,double trackTime,out float association)
        {
            association=0;
            int best=-1;double first=double.PositiveInfinity,second=double.PositiveInfinity;
            var reported=reply.Position+reply.Velocity*Math.Max(0,now-reply.Sent);
            for(int i=0;i<length;i++)
            {
                var t=input[i];if(!t.TrackId.IsValid||t.Lifecycle==TrackLifecycle.Dropped||trackTime-t.LastUpdateTime>3||t.ObjectClass==TrackObjectClass.Missile)continue;
                var p=t.PredictPosition(trackTime);var direction=(Vector3)(float3)(p-(double3)(float3)ownPosition);
                if(bearing.sqrMagnitude<.5f||Vector3.Angle(direction,bearing)>3)continue;
                double score=0;for(int axis=0;axis<3;axis++){double v=Math.Max(25,t.GetCovariance(axis,axis));if(v>25000000){score=double.PositiveInfinity;break;}double d=p[axis]-reported[axis];score+=d*d/v;double dv=t.Velocity[axis]-reply.Velocity[axis];score+=dv*dv/Math.Max(25,t.GetCovariance(axis+3,axis+3));}
                if(score<first){second=first;first=score;best=i;}else if(score<second)second=score;
            }
            if(first>36||second-first<4)return -1;association=(float)(1-.2*first/36);return best;
        }
        private int UniqueBearing(Vector3 bearing,float degrees)
        {
            int found=-1;for(int i=0;i<count;i++){var t=tracks[i];if(!active.Contains(t.TrackId)||t.Lifecycle==TrackLifecycle.Tentative||t.Lifecycle==TrackLifecycle.Dropped||t.Confidence<.5f||Radar.SimTimeSeconds-t.LastUpdateTime>3)continue;var direction=(Vector3)(float3)t.PredictPosition(Radar.SimTimeSeconds)-transform.position;
                if(Vector3.Angle(direction,bearing)>degrees)continue;if(found>=0)return -1;found=i;}return found;
        }
        public const float IlluminationAssociationHalfAngleDegrees=12;
        public static bool IsRadarLockEvidence(ThreatSnapshot threat,double now)=>
            (threat.WaveformClass==EsmWaveformClass.FireControlRadar||threat.WaveformClass==EsmWaveformClass.SearchRadar)&&(threat.Level==EsmThreatLevel.Locked||threat.Level==EsmThreatLevel.MissileActive)&&threat.MainlobeContactCount>0&&now>=threat.LastMainlobeSeenTime&&now-threat.LastMainlobeSeenTime<=.5&&now>=threat.LastSeenTime&&now-threat.LastSeenTime<=.5;
        private double nextIlluminationDiagnostic;
        private void ObserveIllumination(double now)
        {
            int n=Radar.CopyThreats(threats);
            for(int i=0;i<n;i++){var threat=threats[i];if(!IsRadarLockEvidence(threat,Radar.SimTimeSeconds))continue;
                int match=UniqueBearing((Vector3)threat.DirectionWorld,IlluminationAssociationHalfAngleDegrees);if(match<0){if(now>=nextIlluminationDiagnostic){nextIlluminationDiagnostic=now+2;Log("illumination_unassociated",default,IffOutcome.None,"fresh_radar_lock_without_unique_platform; threat="+threat.ThreatId);}continue;}
                PublishContinuousBehavior(tracks[match],CombatBehaviorKind.FireControlLock,"lock/"+threat.ThreatId,now,.75f,.9f);}
        }
        // Call with locally measured incident direction only; never with an attacker scene identity.
        public void ObserveImpact(Vector3 projectileTravelDirection,Guid eventId=default)
        {
            if(Radar==null||projectileTravelDirection.sqrMagnitude<.5f)return;
            int match=UniqueBearing(-projectileTravelDirection.normalized,5);double now=Time.timeAsDouble;
            if(match<0){Log("impact_unattributed",default,IffOutcome.None,"no_unique_observed_source");return;}
            identities.Submit(tracks[match].TrackId,new IdentityEvidence{OriginalId="impact/"+(eventId==Guid.Empty?Guid.NewGuid():eventId).ToString("N"),Source="local-impact",Kind=IdentityEvidenceKind.IncomingFireBearing,Quality=1,Association=.85f,ObservedAt=now,ExpiresAt=now+15},now);
        }
        public bool SubmitMissionEvidence(ObserverTrackRef track,IdentityEvidence evidence)
        {
            if(Radar==null||track.ObserverScope!=Radar.ObserverScope||!track.IsValid)return false;
            bool accepted=identities.Submit(track.TrackId,evidence,Time.timeAsDouble);
            Log("mission_evidence",track.TrackId,IffOutcome.None,$"accepted={accepted}; kind={evidence.Kind}; original={evidence.OriginalId}");return accepted;
        }
        public bool MarkContact(ObserverTrackRef track,IdentityRelation relation)
        {
            if(Radar==null||identities==null||track.ObserverScope!=Radar.ObserverScope||!track.IsValid)return false;
            if(!Radar.TryGetTrack(track,out var observation)||observation.Lifecycle==TrackLifecycle.Dropped||observation.ObjectClass==TrackObjectClass.Missile||observation.ObjectClass==TrackObjectClass.Decoy)return false;
            bool accepted=identities.SetManualRelation(track.TrackId,relation,Time.timeAsDouble);
            if(accepted){snapshots[track.TrackId]=identities.Evaluate(track.TrackId,Time.timeAsDouble);Log("manual_identification",track.TrackId,IffOutcome.None,relation.ToString());}
            return accepted;
        }
        public bool TryGetCertifiedSubject(TrackId track,out Guid subject)
        {
            subject=Guid.Empty;if(!iffEvidence.TryGetValue(track,out var evidence)||Time.timeAsDouble>evidence.until)return false;
            foreach(var pair in certifiedBindings)if(pair.Value.Equals(track)&&!revokedCredentials.Contains(pair.Key)){if(subject!=Guid.Empty){subject=Guid.Empty;return false;}subject=pair.Key;}
            return subject!=Guid.Empty;
        }
        public bool CorrectHostility(ObserverTrackRef track)
        {if(Radar==null||track.ObserverScope!=Radar.ObserverScope||!track.IsValid)return false;identities.CorrectHostility(track.TrackId,Time.timeAsDouble);snapshots.Remove(track.TrackId);Log("mission_correction",track.TrackId,IffOutcome.None,"clear_hostile_evidence");return true;}
        // Explicit trusted mission revocation; not a claim inferred from a missing reply.
        public void RevokeCertifiedIdentity(Guid subject)
        {
            revokedCredentials.Add(subject);
            if(!certifiedBindings.TryGetValue(subject,out var track))return;
            identities.RemoveEvidence(track,"iff/"+subject.ToString("N"));identities.RevokeFriendlyIdentity(track);identities.SetIff(track,IffOutcome.InvalidProof);
            iffEvidence.Remove(track);certifiedBindings.Remove(subject);snapshots.Remove(track);Log("credential_revocation",track,IffOutcome.InvalidProof,subject.ToString("N"));
        }
        public bool TryGetIdentity(TrackId id,out ContactIdentitySnapshot state)=>snapshots.TryGetValue(id,out state);
        public bool CanAttackPlatform(TrackSnapshot t)
        {if(t.ObjectClass==TrackObjectClass.Missile)return t.Affiliation!=TrackAffiliation.Friendly;Apply(ref t);return t.Identity.Available?t.Identity.CanAttackPlatform:AllowUnknownPlatformAttack&&t.Affiliation!=TrackAffiliation.Friendly;}
        public void Apply(ref TrackSnapshot track)
        {
            if(track.ObjectClass==TrackObjectClass.Missile)return;
            identities.AllowUnknownPlatformAttack=AllowUnknownPlatformAttack;
            if(!TryGetIdentity(track.TrackId,out var state))
            {
                if(!identities.HasDecidedIdentity(track.TrackId)&&track.Affiliation==TrackAffiliation.Friendly&&track.IdentityConfidence>=.8f&&(track.SourceMask&TrackSourceMask.DataLink)!=0)
                    identities.Submit(track.TrackId,new IdentityEvidence{OriginalId="cooperative-self-report",Source="authenticated-link",Kind=IdentityEvidenceKind.CooperativeFriendly,Quality=track.IdentityConfidence,Association=1,ObservedAt=Time.timeAsDouble,ExpiresAt=Time.timeAsDouble+3},Time.timeAsDouble);
                state=identities.Evaluate(track.TrackId,Time.timeAsDouble);snapshots[track.TrackId]=state;
            }
            ApplyCombatSources(ref track);
            track.Identity=state;track.Affiliation=state.Relation==IdentityRelation.Friendly?TrackAffiliation.Friendly:state.Relation==IdentityRelation.Hostile?TrackAffiliation.Hostile:TrackAffiliation.Unknown;
            track.IdentityConfidence=state.Confidence;
            if(iffEvidence.TryGetValue(track.TrackId,out var evidence)&&Time.timeAsDouble<=evidence.until)
            {
                track.SourceMask|=TrackSourceMask.Iff;
                // The identity remains valid for its cached lifetime; source score still exposes age.
                double stamp=(Radar!=null?Radar.SimTimeSeconds:Time.timeAsDouble)-(Time.timeAsDouble-evidence.at);
                track.SourceEvidence.Record(TrackSourceMask.Iff,1,stamp);
            }
        }
        [Serializable] private struct LogEntry {public double time;public string observer,kind,track,result,reason,transaction;public int pending;}
        public void Log(string kind,TrackId track,IffOutcome outcome,string reason,string transaction=null)
        {
            if(!LogDecisions)return;
            try{if(log==null){string folder=LogDirectoryOverride??Path.Combine(Application.persistentDataPath,"IdentityLogs");Directory.CreateDirectory(folder);LogPath=Path.Combine(folder,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".jsonl");log=new StreamWriter(LogPath){AutoFlush=true};}
                log.WriteLine(JsonUtility.ToJson(new LogEntry{time=Time.timeAsDouble,observer=Credential?.Subject.ToString("N")??name,kind=kind,track=track.ToString(),result=outcome.ToString(),reason=reason,transaction=transaction??"",pending=protocol?.PendingCount??0}));}
            catch(IOException){LogDecisions=false;}
        }
        private void OnEnable(){if(Credential!=null){MechIffRadio.Authority.Register(Credential);MechIffRadio.Register(this);MechCombatObservationSensor.Register(this);}}
        private void OnDisable(){MechCombatObservationSensor.Unregister(this);MechIffRadio.Unregister(this);log?.Dispose();log=null;}
        private void OnDestroy(){MechCombatObservationSensor.Unregister(this);MechIffRadio.Unregister(this);log?.Dispose();}
    }
}
