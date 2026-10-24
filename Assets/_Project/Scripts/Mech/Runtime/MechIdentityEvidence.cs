using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
namespace ACunicombat.Mechs
{
    public enum IdentityEvidenceKind { FriendlyAuthentication, NeutralAuthentication, ThirdPartyAuthentication, CooperativeFriendly, MissionHostile, ObservedAttack, FireControlIllumination, AuthenticationConflict, IncomingFireBearing, OpticalIdentity, WaveformIdentity, WeaponLaunch, LaserIllumination, DirectedJamming }
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
            public double SuspicionSince=double.NaN, SuspicionLost=double.NaN, LastSeen;
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
        public void CorrectHostility(TrackId id)=>CorrectHostility(id,previousTime);
        public void CorrectHostility(TrackId id,double now)
        {if(!id.IsValid)return;var r=Get(id);r.HostileMemory=false;r.HostilityClearedAt=now;expired.Clear();foreach(var pair in r.Evidence)if(pair.Value.Kind==IdentityEvidenceKind.MissionHostile||pair.Value.Kind==IdentityEvidenceKind.ObservedAttack)expired.Add(pair.Key);foreach(var k in expired)r.Evidence.Remove(k);}
        public void RemoveEvidence(TrackId id,string originalId){if(records.TryGetValue(id,out var r))r.Evidence.Remove(originalId);}
        public void Forget(TrackId id)=>records.Remove(id);
        public ContactIdentitySnapshot Evaluate(TrackId id,double now)
        {
            if(now<previousTime)records.Clear();previousTime=now;
            var r=Get(id);r.LastSeen=now;expired.Clear();
            float friendly=0,neutral=0,third=0,attack=0,hostile=0,suspicion=0,launchSuspicion=0,conflict=0;double latest=0,until=0;
            foreach(var pair in r.Evidence)
            {
                var e=pair.Value;if(e.ExpiresAt<=now){expired.Add(pair.Key);continue;}
                float score=e.Quality*e.Association;latest=Math.Max(latest,e.ObservedAt);until=Math.Max(until,e.ExpiresAt);
                switch(e.Kind){case IdentityEvidenceKind.FriendlyAuthentication:case IdentityEvidenceKind.CooperativeFriendly:friendly=Math.Max(friendly,score);break;
                case IdentityEvidenceKind.NeutralAuthentication:neutral=Math.Max(neutral,score);third=Math.Max(third,score);break;
                case IdentityEvidenceKind.ThirdPartyAuthentication:third=Math.Max(third,score);break;
                case IdentityEvidenceKind.ObservedAttack:attack=Math.Max(attack,score);break;
                case IdentityEvidenceKind.MissionHostile:hostile=Math.Max(hostile,score);break;
                case IdentityEvidenceKind.IncomingFireBearing:suspicion=Math.Max(suspicion,score);break;
                case IdentityEvidenceKind.FireControlIllumination:suspicion=Math.Max(suspicion,score);break;
                case IdentityEvidenceKind.WeaponLaunch:launchSuspicion=Math.Max(launchSuspicion,score);break;
                case IdentityEvidenceKind.LaserIllumination:case IdentityEvidenceKind.DirectedJamming:suspicion=Math.Max(suspicion,score);break;
                case IdentityEvidenceKind.AuthenticationConflict:conflict=Math.Max(conflict,score);suspicion=Math.Max(suspicion,score);break;}
            }
            foreach(var k in expired)r.Evidence.Remove(k);
            // Bearing-only impacts remain suspicion; confirmed attack needs a causal observed weapon history.
            if(attack>=.8f||hostile>=.9f){r.HostileMemory=true;r.HostileReason=attack>=.8f?IdentityReason.ObservedAttack:IdentityReason.MissionIntelligence;}
            if(friendly>=.8f){r.FriendlyIdentityMemory=true;r.ProtectedIdentityMemory=true;}
            if(neutral>=.8f||third>=.8f)r.ProtectedIdentityMemory=true;
            // Authenticated allies firing weapons is normal; a launch alone is not evidence against their allegiance.
            if(friendly<.8f)suspicion=Math.Max(suspicion,launchSuspicion);
            bool suspect=suspicion>=.65f;
            if(suspect){if(double.IsNaN(r.SuspicionSince))r.SuspicionSince=now;r.SuspicionLost=double.NaN;}
            else{r.SuspicionSince=double.NaN;if(double.IsNaN(r.SuspicionLost))r.SuspicionLost=now;}
            bool hold=r.LastRelation==IdentityRelation.SuspectedHostile&&now-r.SuspicionLost<5;
            bool established=suspect&&now-r.SuspicionSince>=2;
            var relation=r.HostileMemory?IdentityRelation.Hostile:(established||hold)?IdentityRelation.SuspectedHostile:friendly>=.8f?IdentityRelation.Friendly:neutral>=.8f?IdentityRelation.Neutral:IdentityRelation.Unknown;
            bool disputed=((friendly>=.8f||r.FriendlyIdentityMemory)&&(r.HostileMemory||conflict>=.7f||neutral>=.8f||established||hold));
            var snapshot=new ContactIdentitySnapshot{Available=true,Relation=relation,Party=friendly>=.8f?IdentityParty.OwnAlliance:third>=.8f?IdentityParty.ThirdParty:IdentityParty.Unknown,
                Conflict=disputed,UnderAttack=attack>=.8f,CanInterceptThreat=attack>=.8f,
                CanAttackPlatform=relation==IdentityRelation.Hostile||(!disputed&&AllowUnknownPlatformAttack&&(relation==IdentityRelation.Unknown||relation==IdentityRelation.SuspectedHostile)&&third<.8f&&!r.ProtectedIdentityMemory),
                Confidence=Math.Max(r.HostileMemory?.8f:0,Math.Max(Math.Max(friendly,neutral),Math.Max(attack,Math.Max(hostile,suspicion)))),EvidenceTime=latest,ValidUntil=until,Iff=r.Iff,
                Reason=disputed?IdentityReason.EvidenceConflict:attack>=.8f?IdentityReason.ObservedAttack:relation==IdentityRelation.Hostile?r.HostileReason:relation==IdentityRelation.SuspectedHostile?IdentityReason.SustainedIllumination:friendly>=.8f||neutral>=.8f||third>=.8f?IdentityReason.Authenticated:r.ProtectedIdentityMemory||r.LastRelation!=IdentityRelation.Unknown?IdentityReason.EvidenceExpired:IdentityReason.None};
            if(r.ManualRelation.HasValue)
            {
                relation=r.ManualRelation.Value;snapshot.Conflict|=snapshot.Relation!=IdentityRelation.Unknown&&snapshot.Relation!=relation;
                snapshot.Relation=relation;snapshot.Reason=IdentityReason.MissionIntelligence;
                snapshot.CanAttackPlatform=relation==IdentityRelation.Hostile;
                snapshot.Party=relation==IdentityRelation.Friendly?IdentityParty.OwnAlliance:relation==IdentityRelation.Neutral?IdentityParty.ThirdParty:IdentityParty.Unknown;
                snapshot.Confidence=1;snapshot.EvidenceTime=r.ManualTime;snapshot.ValidUntil=double.MaxValue;
            }
            if(relation!=r.LastRelation||snapshot.Conflict!=r.LastSnapshot.Conflict||snapshot.CanAttackPlatform!=r.LastSnapshot.CanAttackPlatform||snapshot.UnderAttack!=r.LastSnapshot.UnderAttack||snapshot.Party!=r.LastSnapshot.Party){Changed?.Invoke(id,snapshot);}
            r.LastRelation=relation;r.LastSnapshot=snapshot;return snapshot;
        }
        public void Prune(double now){obsolete.Clear();foreach(var pair in records)if(now-pair.Value.LastSeen>120)obsolete.Add(pair.Key);foreach(var id in obsolete)records.Remove(id);}
    }
}
