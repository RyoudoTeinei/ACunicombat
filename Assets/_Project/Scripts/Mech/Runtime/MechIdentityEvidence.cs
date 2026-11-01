using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
namespace ACunicombat.Mechs
{
    public enum IdentityEvidenceKind { FriendlyAuthentication, NeutralAuthentication, ThirdPartyAuthentication, CooperativeFriendly, MissionHostile, ObservedAttack, FireControlIllumination, AuthenticationConflict, IncomingFireBearing, OpticalIdentity, WaveformIdentity, WeaponLaunch, LaserIllumination, DirectedJamming, FormationHostile }
    public struct IdentityEvidence
    {
        public string OriginalId, Source;
        public IdentityEvidenceKind Kind;
        public float Quality, Association;
        public double ObservedAt, ExpiresAt;
    }
    // Pure observer-owned evidence reducer. No world objects, team IDs, or enemy internal state.
    public sealed class MechIdentityEvidence
    {
        private sealed class Record
        {
            public readonly Dictionary<string,IdentityEvidence> Evidence=new Dictionary<string,IdentityEvidence>();
            public bool HostileMemory,ProtectedIdentityMemory,FriendlyIdentityMemory;public IdentityReason HostileReason;
            public IdentityRelation? DecidedRelation;
            public double SuspicionSince=double.NaN, SuspicionLost=double.NaN, LastSeen;
            public double LockSince=double.NaN, LastLockObservation=double.NegativeInfinity;
            public double HostilityClearedAt=double.NegativeInfinity;
            public IdentityRelation LastRelation;public ContactIdentitySnapshot LastSnapshot;
            public IffOutcome Iff;public IdentityRelation? ManualRelation;public double ManualTime;
        }
        private readonly Dictionary<TrackId,Record> records=new Dictionary<TrackId,Record>();
        private readonly List<string> expired=new List<string>();
        private readonly List<TrackId> obsolete=new List<TrackId>();
        public bool AllowUnknownPlatformAttack {get;set;} // Explicit training ROE; tight is the engine default.
        public event Action<TrackId,ContactIdentitySnapshot> Changed;
        private double previousTime=double.NegativeInfinity;
        private Record Get(TrackId id){if(!records.TryGetValue(id,out var r)){r=new Record();records.Add(id,r);}return r;}
        public bool Submit(TrackId id,IdentityEvidence e,double now)
        {
            if(!id.IsValid||string.IsNullOrEmpty(e.OriginalId)||!double.IsFinite(now)||!double.IsFinite(e.ObservedAt)||!double.IsFinite(e.ExpiresAt)||!float.IsFinite(e.Quality)||!float.IsFinite(e.Association)||e.ObservedAt>now+.001||e.ExpiresAt<=now||e.Quality<0||e.Quality>1||e.Association<0||e.Association>1)return false;
            // Early-development placeholders must never fabricate recognition results.
            if(e.Kind==IdentityEvidenceKind.OpticalIdentity||e.Kind==IdentityEvidenceKind.WaveformIdentity)return false;
            var r=Get(id);
            if((e.Kind==IdentityEvidenceKind.ObservedAttack||e.Kind==IdentityEvidenceKind.MissionHostile)&&e.ObservedAt<=r.HostilityClearedAt)return false;
            if(r.Evidence.TryGetValue(e.OriginalId,out var old)&&e.ObservedAt<=old.ObservedAt)return false;
            if(r.Evidence.Count>=128&&!r.Evidence.ContainsKey(e.OriginalId))return false;
            r.Evidence[e.OriginalId]=e;r.LastSeen=now;return true;
        }
        public bool SetManualRelation(TrackId id,IdentityRelation relation,double now)
        {
            if(!id.IsValid||!double.IsFinite(now)||(relation!=IdentityRelation.Friendly&&relation!=IdentityRelation.Neutral&&relation!=IdentityRelation.Hostile))return false;
            var r=Get(id);r.ManualRelation=relation;r.ManualTime=now;r.LastSeen=now;return true;
        }
        public void SetIff(TrackId id,IffOutcome result){if(id.IsValid)Get(id).Iff=result;}
        public bool HasDecidedIdentity(TrackId id)=>records.TryGetValue(id,out var r)&&(r.DecidedRelation.HasValue||r.ManualRelation==IdentityRelation.Friendly||r.ManualRelation==IdentityRelation.Hostile);
        public void CorrectHostility(TrackId id)=>CorrectHostility(id,previousTime);
        public void CorrectHostility(TrackId id,double now)
        {if(!id.IsValid)return;var r=Get(id);if(r.DecidedRelation==IdentityRelation.Hostile)r.DecidedRelation=null;r.HostileMemory=false;r.LockSince=double.NaN;r.LastLockObservation=double.NegativeInfinity;r.HostilityClearedAt=now;expired.Clear();foreach(var pair in r.Evidence)if(pair.Value.Kind==IdentityEvidenceKind.MissionHostile||pair.Value.Kind==IdentityEvidenceKind.ObservedAttack)expired.Add(pair.Key);foreach(var k in expired)r.Evidence.Remove(k);}
        public void RevokeFriendlyIdentity(TrackId id)
        {if(!id.IsValid||!records.TryGetValue(id,out var r))return;if(r.DecidedRelation==IdentityRelation.Friendly)r.DecidedRelation=null;r.FriendlyIdentityMemory=false;}
        public void RemoveEvidence(TrackId id,string originalId){if(records.TryGetValue(id,out var r))r.Evidence.Remove(originalId);}
        public void Forget(TrackId id)=>records.Remove(id);
        public ContactIdentitySnapshot Evaluate(TrackId id,double now)
        {
            if(now<previousTime)records.Clear();previousTime=now;
            var r=Get(id);r.LastSeen=now;expired.Clear();
            float friendly=0,neutral=0,third=0,attack=0,hostile=0,suspicion=0,launchSuspicion=0,conflict=0;double latest=0,until=0;
            float formationHostile=0; bool clearAttack=false; double lockObservation=double.NegativeInfinity; bool recentLaunch=false;
            foreach(var pair in r.Evidence)
            {
                var e=pair.Value;if(e.ExpiresAt<=now){expired.Add(pair.Key);continue;}
                float score=e.Quality*e.Association;latest=Math.Max(latest,e.ObservedAt);until=Math.Max(until,e.ExpiresAt);
                // Fresh observations only: retained packets cannot manufacture sustained locking.
                if(e.ObservedAt>r.HostilityClearedAt)
                {
                    if(e.Kind==IdentityEvidenceKind.ObservedAttack&&e.Quality>=.95f&&e.Association>=.8f)clearAttack=true;
                    if(e.Kind==IdentityEvidenceKind.FireControlIllumination&&score>=.65f&&e.Association>=.8f&&now-e.ObservedAt<=.5)
                        lockObservation=Math.Max(lockObservation,e.ObservedAt);
                    if(e.Kind==IdentityEvidenceKind.WeaponLaunch&&score>=.65f&&e.Association>=.8f&&now-e.ObservedAt<=5)recentLaunch=true;
                }
                switch(e.Kind){case IdentityEvidenceKind.FriendlyAuthentication:case IdentityEvidenceKind.CooperativeFriendly:friendly=Math.Max(friendly,score);break;
                case IdentityEvidenceKind.NeutralAuthentication:neutral=Math.Max(neutral,score);third=Math.Max(third,score);break;
                case IdentityEvidenceKind.ThirdPartyAuthentication:third=Math.Max(third,score);break;
                case IdentityEvidenceKind.ObservedAttack:attack=Math.Max(attack,score);break;
                case IdentityEvidenceKind.FormationHostile:if(e.ObservedAt>r.HostilityClearedAt)formationHostile=Math.Max(formationHostile,score);break;
                case IdentityEvidenceKind.MissionHostile:hostile=Math.Max(hostile,score);break;
                case IdentityEvidenceKind.IncomingFireBearing:suspicion=Math.Max(suspicion,score);break;
                case IdentityEvidenceKind.FireControlIllumination:suspicion=Math.Max(suspicion,Math.Min(.7f,score));break;
                case IdentityEvidenceKind.WeaponLaunch:launchSuspicion=Math.Max(launchSuspicion,score);break;
                case IdentityEvidenceKind.LaserIllumination:case IdentityEvidenceKind.DirectedJamming:suspicion=Math.Max(suspicion,score);break;
                case IdentityEvidenceKind.AuthenticationConflict:conflict=Math.Max(conflict,score);suspicion=Math.Max(suspicion,score);break;}
            }
            foreach(var k in expired)r.Evidence.Remove(k);
            bool combined=false;
            if(!r.DecidedRelation.HasValue)
            {
                if(double.IsNegativeInfinity(lockObservation)){r.LockSince=double.NaN;r.LastLockObservation=double.NegativeInfinity;}
                else if(lockObservation>r.LastLockObservation)
                {
                    if(double.IsNaN(r.LockSince)||lockObservation-r.LastLockObservation>.75)r.LockSince=lockObservation;
                    r.LastLockObservation=lockObservation;
                }
                combined=recentLaunch&&!double.IsNaN(r.LockSince)&&r.LastLockObservation-r.LockSince>=2;
            }
            bool illuminated=!double.IsNegativeInfinity(lockObservation);
            double lockDuration=illuminated&&!double.IsNaN(r.LockSince)?r.LastLockObservation-r.LockSince:0;
            bool sustainedHostile=illuminated&&lockDuration>=5;
            if(illuminated&&lockDuration>=2)suspicion=Math.Max(suspicion,.85f);
            bool confirmedAttack=attack>=.8f||clearAttack;
            // The first decisive identification is stable. Subsequent evidence remains
            // available as conflict diagnostics but cannot flip an ally or an enemy.
            if(!r.DecidedRelation.HasValue)
            {
                if(friendly>=.8f){r.DecidedRelation=IdentityRelation.Friendly;r.FriendlyIdentityMemory=true;r.ProtectedIdentityMemory=true;}
                else if(confirmedAttack||hostile>=.9f||combined||sustainedHostile||formationHostile>=.9f){r.DecidedRelation=IdentityRelation.Hostile;r.HostileMemory=true;r.HostileReason=confirmedAttack?IdentityReason.ObservedAttack:combined?IdentityReason.CombinedBehavior:sustainedHostile?IdentityReason.SustainedIllumination:hostile>=.9f?IdentityReason.MissionIntelligence:IdentityReason.FormationAssociation;}
            }
            if(neutral>=.8f||third>=.8f)r.ProtectedIdentityMemory=true;
            // Authenticated allies firing weapons is normal; a launch alone is not evidence against their allegiance.
            if(friendly<.8f)suspicion=Math.Max(suspicion,launchSuspicion);
            bool suspect=suspicion>=.65f;
            if(suspect){if(double.IsNaN(r.SuspicionSince))r.SuspicionSince=now;r.SuspicionLost=double.NaN;}
            else{r.SuspicionSince=double.NaN;if(double.IsNaN(r.SuspicionLost))r.SuspicionLost=now;}
            bool hold=r.LastRelation==IdentityRelation.SuspectedHostile&&now-r.SuspicionLost<5;
            bool established=illuminated||(suspect&&now-r.SuspicionSince>=2);
            var relation=r.DecidedRelation??((established||hold)?IdentityRelation.SuspectedHostile:neutral>=.8f?IdentityRelation.Neutral:IdentityRelation.Unknown);
            bool disputed=((friendly>=.8f||r.FriendlyIdentityMemory)&&(r.HostileMemory||confirmedAttack||hostile>=.9f||conflict>=.7f||neutral>=.8f||established||hold));
            var snapshot=new ContactIdentitySnapshot{Available=true,Relation=relation,Party=friendly>=.8f||r.FriendlyIdentityMemory?IdentityParty.OwnAlliance:third>=.8f?IdentityParty.ThirdParty:IdentityParty.Unknown,
                Conflict=disputed,UnderAttack=confirmedAttack,CanInterceptThreat=confirmedAttack,
                CanAttackPlatform=relation==IdentityRelation.Hostile||(!disputed&&AllowUnknownPlatformAttack&&(relation==IdentityRelation.Unknown||relation==IdentityRelation.SuspectedHostile)&&third<.8f&&!r.ProtectedIdentityMemory),
                Confidence=r.DecidedRelation.HasValue?1:Math.Max(r.HostileMemory?.8f:0,Math.Max(Math.Max(friendly,neutral),Math.Max(attack,Math.Max(hostile,suspicion)))),Suspicion=r.HostileMemory?1:suspicion,EvidenceTime=latest,ValidUntil=r.DecidedRelation.HasValue?double.MaxValue:until,Iff=r.Iff,
                Reason=disputed?IdentityReason.EvidenceConflict:confirmedAttack?IdentityReason.ObservedAttack:relation==IdentityRelation.Hostile?r.HostileReason:relation==IdentityRelation.SuspectedHostile?IdentityReason.SustainedIllumination:relation==IdentityRelation.Friendly||friendly>=.8f||neutral>=.8f||third>=.8f?IdentityReason.Authenticated:r.ProtectedIdentityMemory||r.LastRelation!=IdentityRelation.Unknown?IdentityReason.EvidenceExpired:IdentityReason.None};
            if(r.ManualRelation.HasValue)
            {
                relation=r.ManualRelation.Value;snapshot.Conflict|=snapshot.Relation!=IdentityRelation.Unknown&&snapshot.Relation!=relation;
                snapshot.Relation=relation;snapshot.Reason=IdentityReason.MissionIntelligence;
                snapshot.CanAttackPlatform=relation==IdentityRelation.Hostile;
                snapshot.Party=relation==IdentityRelation.Friendly?IdentityParty.OwnAlliance:relation==IdentityRelation.Neutral?IdentityParty.ThirdParty:IdentityParty.Unknown;
                snapshot.Confidence=1;snapshot.Suspicion=relation==IdentityRelation.Hostile?1:0;snapshot.EvidenceTime=r.ManualTime;snapshot.ValidUntil=double.MaxValue;
            }
            if(relation!=r.LastRelation||snapshot.Conflict!=r.LastSnapshot.Conflict||snapshot.CanAttackPlatform!=r.LastSnapshot.CanAttackPlatform||snapshot.UnderAttack!=r.LastSnapshot.UnderAttack||snapshot.Party!=r.LastSnapshot.Party){Changed?.Invoke(id,snapshot);}
            r.LastRelation=relation;r.LastSnapshot=snapshot;return snapshot;
        }
        public void Prune(double now){obsolete.Clear();foreach(var pair in records)if(now-pair.Value.LastSeen>120)obsolete.Add(pair.Key);foreach(var id in obsolete)records.Remove(id);}
    }
}
