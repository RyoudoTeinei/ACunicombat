using System;
using System.Collections.Generic;
using System.Linq;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using Unity.Mathematics;
namespace ACunicombat.Mechs
{
    // Received observations only. No remote entity/transform or world identity crosses here.
    public sealed class MechSharedTargetTracks
    {
        sealed class Entry {public TrackSnapshot Track;public TrackId Local,Public;}
        readonly Dictionary<(uint,uint),Entry> reports=new();
        int next=30000;
        public static bool MatchesOwnNavigation(in TrackSnapshot report,double3 ownPosition,double3 ownVelocity,double now,bool authenticatedSelf=false)
        {
            if(!authenticatedSelf||report.ObjectClass!=TrackObjectClass.AirVehicle)return false;
            double sigma=report.PredictPositionUncertainty(now),velocityVariance=25;
            for(int i=0;i<3;i++)velocityVariance+=math.max(0,report.GetCovariance(i+3,i+3));
            // Local navigation is known to this receiver. A poorly localized distant
            // observation is not a self report merely because its ellipse is huge.
            return math.distance(report.PredictPosition(now),ownPosition)<=math.clamp(3*sigma,5,40)&&
                math.lengthsq(report.Velocity-ownVelocity)<=9*velocityVariance;
        }
        public void Remove(LinkTerminalId sender,TrackId track)=>reports.Remove((sender.Packed,track.Packed));
        public bool Receive(LinkTerminalId sender,in TrackSnapshot track,double now)
        {
            if(!sender.IsValid||!track.TrackId.IsValid||track.ObjectClass!=TrackObjectClass.AirVehicle||
                !math.all(math.isfinite(track.Position))||!math.all(math.isfinite(track.Velocity))||
                !double.IsFinite(track.LastUpdateTime)||track.LastUpdateTime>now||now-track.LastUpdateTime>2||
                track.Lifecycle==TrackLifecycle.Dropped||track.Lifecycle==TrackLifecycle.Tentative)return false;
            for(int row=0;row<6;row++)for(int column=0;column<6;column++)
                if(!double.IsFinite(track.GetCovariance(row,column))||row==column&&track.GetCovariance(row,column)<0)return false;
            var key=(sender.Packed,track.TrackId.Packed);
            if(reports.TryGetValue(key,out var old)&&old.Track.LastUpdateTime>=track.LastUpdateTime)return false;
            if(old==null){if(reports.Count>=128)return false;old=new Entry{Local=new TrackId(new Handle(next++,1))};reports[key]=old;}
            old.Track=track;return true;
        }
        readonly List<(uint,uint)> stale=new(),orderedKeys=new();
        readonly List<Entry> groupEntries=new();
        double3[] localPosition=new double3[0];double3x3[] localCovariance=new double3x3[0];
        public int Merge(TrackSnapshot[] output,int count,double now)
        {
            stale.Clear();foreach(var p in reports)if(now-p.Value.Track.LastUpdateTime>2)stale.Add(p.Key);
            foreach(var key in stale)reports.Remove(key);
            // Sender-ascending, then track-ascending: the same order as the former OrderBy/GroupBy.
            orderedKeys.Clear();foreach(var key in reports.Keys)orderedKeys.Add(key);orderedKeys.Sort();
            // Predictions of the published contacts are reused for every report row; those
            // contacts only change after their own group has been assigned (see below).
            if(localPosition.Length<output.Length){localPosition=new double3[output.Length];localCovariance=new double3x3[output.Length];}
            int predicted=0;
            // A sender has already resolved its distinct tracks. They must not be
            // collapsed into each other while processing that sender's report batch.
            // Different observers may corroborate the same public contact.
            for(int start=0;start<orderedKeys.Count;)
            {
                uint sender=orderedKeys[start].Item1;int end=start;groupEntries.Clear();
                while(end<orderedKeys.Count&&orderedKeys[end].Item1==sender){groupEntries.Add(reports[orderedKeys[end]]);end++;}
                start=end;
                var entries=groupEntries;int priorCount=count;
                for(;predicted<priorCount;predicted++){localPosition[predicted]=output[predicted].PredictPosition(now);localCovariance[predicted]=output[predicted].PredictPositionCovariance(now);}
                var costs=new double[entries.Count,priorCount];
                for(int row=0;row<entries.Count;row++)
                {
                var remote=entries[row].Track;var remotePosition=remote.PredictPosition(now);var remoteCovariance=remote.PredictPositionCovariance(now);
                for(int i=0;i<priorCount;i++)
                {
                    costs[row,i]=OneToOneAssignment.Rejected;
                    var local=output[i];if(local.ObjectClass!=TrackObjectClass.AirVehicle)continue;
                    var delta=remotePosition-localPosition[i];
                    var covariance=remoteCovariance+localCovariance[i]+double3x3.identity*4;
                    double det=math.determinant(covariance);if(!double.IsFinite(det)||det<=1e-9)continue;
                    double score=math.dot(delta,math.mul(math.inverse(covariance),delta));
                    double velocityVariance=25;for(int axis=0;axis<3;axis++)velocityVariance+=remote.GetCovariance(axis+3,axis+3)+local.GetCovariance(axis+3,axis+3);
                    score+=math.lengthsq(remote.Velocity-local.Velocity)/math.max(25,velocityVariance);
                    if(double.IsFinite(score)&&score<=11.345)costs[row,i]=score;
                }
                }
                var assignment=OneToOneAssignment.Solve(costs,4,out _);
                for(int row=0;row<entries.Count;row++)
                {
                var e=entries[row];var remote=e.Track;int match=assignment[row];
                if(match<0&&count>=output.Length)continue;
                var result=remote;
                result.TrackId=match>=0?output[match].TrackId:(e.Public.IsValid?e.Public:e.Local);
                if(match>=0)
                {
                    var local=output[match];
                    if(local.PredictPositionUncertainty(now)<=remote.PredictPositionUncertainty(now))result=local;
                    else {result.TrackId=local.TrackId;result.Identity=local.Identity;result.Affiliation=local.Affiliation;result.SourceEvidence=local.SourceEvidence;}
                    result.SourceMask|=local.SourceMask;
                    e.Public=result.TrackId;
                }
                result.SourceMask|=TrackSourceMask.DataLink;
                result.SourceEvidence.Record(TrackSourceMask.DataLink,remote.Confidence,remote.LastUpdateTime);
                // A received sender's radar evidence is not a local radar observation.
                if(match<0)
                {
                    result.SourceMask=TrackSourceMask.DataLink;result.SourceEvidence=default;result.SourceEvidence.Record(TrackSourceMask.DataLink,remote.Confidence,remote.LastUpdateTime);
                    bool duplicate=false;for(int i=0;i<count;i++)if(output[i].TrackId.Packed==result.TrackId.Packed){duplicate=true;break;}
                    if(duplicate)continue;
                }
                if(match>=0){output[match]=result;localPosition[match]=result.PredictPosition(now);localCovariance[match]=result.PredictPositionCovariance(now);}
                else output[count++]=result;
                }
            }
            return count;
        }
    }
}
