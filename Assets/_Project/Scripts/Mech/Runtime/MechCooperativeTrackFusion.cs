using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Tracking;
using Unity.Mathematics;
namespace ACunicombat.Mechs
{
    /// <summary>Observer-owned fusion of received friendly navigation reports and local tracks.
    /// Inputs contain only received observations; no entity registry or other actors' transforms.</summary>
    public sealed class MechCooperativeTrackFusion
    {
        private sealed class History { public TrackId Radar;public double RadarTime=-1,ReportTime=-1;public int Confirmations; }
        private readonly Dictionary<TrackId,History> history=new Dictionary<TrackId,History>();
        private readonly List<TrackSnapshot> contacts=new List<TrackSnapshot>(96);
        private readonly List<TrackId> obsolete=new List<TrackId>();
        private readonly List<TrackSnapshot> reports=new List<TrackSnapshot>(64);
        // Association diagnostics are kept as values and formatted only when read.
        private enum DecisionKind:byte{OutsideGate,Ambiguous,Awaiting,Matched}
        private struct Decision{public TrackId Report,Local;public DecisionKind Kind;public double Score;}
        private readonly List<Decision> decisions=new List<Decision>();
        private readonly List<string> decisionText=new List<string>();
        private static readonly TrackSourceMask[] Sources={TrackSourceMask.Radar,TrackSourceMask.ElectroOptical,TrackSourceMask.Esm,TrackSourceMask.DataLink,TrackSourceMask.LaserRangefinder,TrackSourceMask.CloseProtectionRadar};
        public IReadOnlyList<TrackSnapshot> Contacts=>contacts;
        public IReadOnlyList<string> Decisions
        {
            get
            {
                decisionText.Clear();
                foreach(var d in decisions)decisionText.Add(d.Kind switch
                {
                    DecisionKind.OutsideGate=>$"{d.Report}: outside_gate",
                    DecisionKind.Ambiguous=>$"{d.Report}: ambiguous",
                    DecisionKind.Awaiting=>$"{d.Report}: awaiting_independent_updates",
                    _=>$"{d.Report}: matched {d.Local}, NIS={d.Score:F3}",
                });
                return decisionText;
            }
        }
        private const double Gate=36,MaxAge=3,AmbiguityMargin=4;
        private static bool Fresh(in TrackSnapshot t,double now)=>t.TrackId.IsValid && t.Lifecycle!=TrackLifecycle.Dropped &&
            double.IsFinite(t.LastUpdateTime)&&double.IsFinite(t.LastPredictTime)&&t.LastPredictTime<=now+1e-6&&now-t.LastUpdateTime>=-1e-6&&now-t.LastUpdateTime<=MaxAge&&
            math.all(math.isfinite(t.Position))&&math.all(math.isfinite(t.Velocity));
        private static FilterState Predict(in TrackSnapshot t,double now)
        {
            var state=new FilterState{Position=t.Position,Velocity=t.Velocity,Time=t.LastPredictTime};
            for(int i=0;i<6;i++)for(int j=0;j<6;j++)state.P[i,j]=t.GetCovariance(i,j);
            // Navigation/registration uncertainty floors, not distance or velocity difference gates.
            for(int i=0;i<3;i++){state.P[i,i]+=4;state.P[i+3,i+3]+=25;}
            Cmkf.PredictCv(ref state,math.max(0,now-t.LastPredictTime),4,16);return state;
        }
        // A radar track eligible to be authenticated by a report, with its prediction to 'now'.
        private static bool Eligible(in TrackSnapshot radar,double now,out FilterState a)
        {
            a=default;
            if(!Fresh(radar,now)||radar.Lifecycle==TrackLifecycle.Tentative||radar.ObjectClass==TrackObjectClass.Missile||radar.ObjectClass==TrackObjectClass.Decoy)return false;
            if(radar.Affiliation==TrackAffiliation.Hostile&&radar.IdentityConfidence>=.9f)return false;
            a=Predict(radar,now);
            if(!Cmkf.Healthy(a))return false;
            // An almost unconstrained radar return cannot authenticate an arbitrary nearby actor.
            return !(a.P[0,0]+a.P[1,1]+a.P[2,2]>3*5000.0*5000.0);
        }
        private static double Score(in TrackSnapshot radar,in TrackSnapshot report,double now)
        {
            if(!Eligible(radar,now,out var a))return double.PositiveInfinity;
            var b=Predict(report,now);
            return Cmkf.Healthy(b)?Score(in a,in b):double.PositiveInfinity;
        }
        /// <summary>Gated position+velocity NIS between two predicted states.</summary>
        private static double Score(in FilterState a,in FilterState b)
        {
            if(!Cmkf.TryCalculatePositionInnovationNis(a,b.Position,b.P,out double positionNis))return double.PositiveInfinity;
            var av=new FilterState{Position=a.Velocity};var bv=new Matrix6();
            for(int i=0;i<3;i++)for(int j=0;j<3;j++){av.P[i,j]=a.P[i+3,j+3];bv[i,j]=b.P[i+3,j+3];}
            if(!Cmkf.TryCalculatePositionInnovationNis(av,b.Velocity,bv,out double velocityNis))return double.PositiveInfinity;
            double sum=positionNis+velocityNis;return sum<=Gate?sum:double.PositiveInfinity;
        }
        private double[] scores=new double[0];
        private FilterState[] radarPredicted=new FilterState[0];
        private bool[] radarEligible=new bool[0];
        private bool[] matchedScratch=new bool[0];
        private FilterState[] reportPredicted=new FilterState[0];private bool[] reportHealthy=new bool[0];
        private void EnsureScratch(int reportCount,int radarCount)
        {
            if(scores.Length<reportCount*radarCount)scores=new double[System.Math.Max(64,reportCount*radarCount*2)];
            if(radarPredicted.Length<radarCount){radarPredicted=new FilterState[radarCount*2];radarEligible=new bool[radarCount*2];}
            if(reportPredicted.Length<reportCount){reportPredicted=new FilterState[Math.Max(8,reportCount*2)];reportHealthy=new bool[reportPredicted.Length];}
        }
        private int IndexOfReport(TrackId id){for(int i=0;i<reports.Count;i++)if(reports[i].TrackId.Equals(id))return i;return -1;}
        public void Update(TrackSnapshot[] local,int count,IReadOnlyList<TrackSnapshot> received,double now)
        {
            contacts.Clear();reports.Clear();decisions.Clear();
            count=Math.Min(count,local.Length);for(int i=0;i<count;i++)contacts.Add(local[i]);
            foreach(var report in received)
            {
                if(!Fresh(report,now)||report.ObjectClass!=TrackObjectClass.AirVehicle||report.Affiliation!=TrackAffiliation.Friendly||
                    report.IdentityConfidence<.9f||(report.SourceMask&TrackSourceMask.DataLink)==0)continue;
                int existing=IndexOfReport(report.TrackId);
                if(existing<0)reports.Add(report);else if(report.LastUpdateTime>reports[existing].LastUpdateTime)reports[existing]=report;
            }
            obsolete.Clear();foreach(var key in history.Keys)if(IndexOfReport(key)<0)obsolete.Add(key);
            foreach(var key in obsolete)history.Remove(key);
            // Every (report, radar) score is needed for best/second and again for the
            // cross-report uniqueness test: predict each state once and score each pair once.
            int reportCount=reports.Count;
            EnsureScratch(reportCount,count);
            for(int i=0;i<count;i++)radarEligible[i]=Eligible(local[i],now,out radarPredicted[i]);
            for(int j=0;j<reportCount;j++){reportPredicted[j]=Predict(reports[j],now);reportHealthy[j]=Cmkf.Healthy(reportPredicted[j]);}
            // Burst batch with the same per-pair arithmetic as Score(in FilterState,in FilterState).
            CmkfBurst.PairNisScores(radarPredicted,radarEligible,count,reportPredicted,reportHealthy,reportCount,Gate,scores);
            if(matchedScratch.Length<reportCount)matchedScratch=new bool[Math.Max(8,reportCount*2)];
            var matched=matchedScratch;Array.Clear(matched,0,reportCount);
            for(int j=0;j<reports.Count;j++)
            {
                int best=-1;double score=double.PositiveInfinity,second=double.PositiveInfinity;
                for(int i=0;i<count;i++){double candidate=scores[j*count+i];if(candidate<score){second=score;score=candidate;best=i;}else if(candidate<second)second=candidate;}
                bool unique=best>=0 && second-score>=AmbiguityMargin;
                if(unique)for(int other=0;other<reports.Count;other++)if(other!=j && scores[other*count+best]<score+AmbiguityMargin){unique=false;break;}
                if(!history.TryGetValue(reports[j].TrackId,out var state)){state=new History();history.Add(reports[j].TrackId,state);}
                if(!unique){state.Radar=default;state.Confirmations=0;decisions.Add(new Decision{Report=reports[j].TrackId,Kind=best<0?DecisionKind.OutsideGate:DecisionKind.Ambiguous});continue;}
                if(!state.Radar.Equals(local[best].TrackId)){state.Radar=local[best].TrackId;state.Confirmations=0;state.ReportTime=-1;state.RadarTime=-1;}
                if(reports[j].LastUpdateTime>state.ReportTime && local[best].LastUpdateTime>state.RadarTime)
                {state.Confirmations++;state.ReportTime=reports[j].LastUpdateTime;state.RadarTime=local[best].LastUpdateTime;}
                if(state.Confirmations<2){decisions.Add(new Decision{Report=reports[j].TrackId,Kind=DecisionKind.Awaiting});continue;}
                contacts[best]=Fuse(local[best],reports[j],now);matched[j]=true;
                decisions.Add(new Decision{Report=reports[j].TrackId,Local=local[best].TrackId,Kind=DecisionKind.Matched,Score=score});
            }
            for(int j=0;j<reports.Count;j++)if(!matched[j])contacts.Add(reports[j]);
        }
        private static TrackSnapshot Fuse(TrackSnapshot radar,TrackSnapshot report,double now)
        {
            var state=Predict(radar,now);var nav=Predict(report,now);
            Cmkf.TryUpdatePosition(ref state,nav.Position,nav.P);
            for(int axis=0;axis<3;axis++)
            {
                float3 direction=axis==0?new float3(1,0,0):axis==1?new float3(0,1,0):new float3(0,0,1);
                Cmkf.UpdateRadialVelocity(ref state,direction,(float)nav.Velocity[axis],(float)nav.P[axis+3,axis+3]);
            }
            radar.Position=state.Position;radar.Velocity=state.Velocity;radar.LastPredictTime=now;
            radar.LastUpdateTime=math.max(radar.LastUpdateTime,report.LastUpdateTime);
            for(int i=0;i<6;i++)for(int j=i;j<6;j++)radar.SetCovariance(i,j,state.P[i,j]);
            radar.Affiliation=TrackAffiliation.Friendly;radar.IdentityConfidence=report.IdentityConfidence;
            radar.IsIdentifiedMech=report.IsIdentifiedMech;radar.IdentifiedWeight=report.IdentifiedWeight;radar.IdentifiedRole=report.IdentifiedRole;
            radar.SourceMask|=TrackSourceMask.DataLink;
            foreach(var source in Sources){var evidence=report.SourceEvidence.Get(source);if(evidence.Valid)radar.SourceEvidence.Record(source,evidence.Confidence,evidence.Timestamp);}
            return radar;
        }
        public bool TryGetReportId(TrackId local,out TrackId report)
        {
            report=default;if(!TryGet(local,out _))return false;
            foreach(var r in reports)if(r.TrackId.Equals(local)){report=r.TrackId;return true;}
            foreach(var entry in history)if(entry.Value.Confirmations>=2&&entry.Value.Radar.Equals(local)){if(report.IsValid)return false;report=entry.Key;}
            return report.IsValid;
        }
        public int CopyTo(TrackSnapshot[] destination)
        {int count=Math.Min(destination.Length,contacts.Count);for(int i=0;i<count;i++)destination[i]=contacts[i];return count;}
        public bool TryGet(TrackId id,out TrackSnapshot result)
        {foreach(var t in contacts)if(t.TrackId.Equals(id)){result=t;return true;}result=default;return false;}
    }
}
