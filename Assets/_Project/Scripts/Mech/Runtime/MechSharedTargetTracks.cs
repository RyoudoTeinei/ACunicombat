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
        public int Merge(TrackSnapshot[] output,int count,double now)
        {
            foreach(var key in reports.Where(p=>now-p.Value.Track.LastUpdateTime>2).Select(p=>p.Key).ToArray())reports.Remove(key);
            // A sender has already resolved its distinct tracks. They must not be
            // collapsed into each other while processing that sender's report batch.
            // Different observers may corroborate the same public contact.
            foreach(var group in reports.OrderBy(p=>p.Key.Item1).ThenBy(p=>p.Key.Item2).GroupBy(p=>p.Key.Item1))
            {
                var entries=group.Select(p=>p.Value).ToArray();int priorCount=count;
                var costs=new double[entries.Length,priorCount];
                for(int row=0;row<entries.Length;row++)for(int i=0;i<priorCount;i++)
                {
                    costs[row,i]=OneToOneAssignment.Rejected;var remote=entries[row].Track;
                    var local=output[i];if(local.ObjectClass!=TrackObjectClass.AirVehicle)continue;
                    var delta=remote.PredictPosition(now)-local.PredictPosition(now);
                    var covariance=remote.PredictPositionCovariance(now)+local.PredictPositionCovariance(now)+double3x3.identity*4;
                    double det=math.determinant(covariance);if(!double.IsFinite(det)||det<=1e-9)continue;
                    double score=math.dot(delta,math.mul(math.inverse(covariance),delta));
                    double velocityVariance=25;for(int axis=0;axis<3;axis++)velocityVariance+=remote.GetCovariance(axis+3,axis+3)+local.GetCovariance(axis+3,axis+3);
                    score+=math.lengthsq(remote.Velocity-local.Velocity)/math.max(25,velocityVariance);
                    if(double.IsFinite(score)&&score<=11.345)costs[row,i]=score;
                }
                var assignment=OneToOneAssignment.Solve(costs,4,out _);
                for(int row=0;row<entries.Length;row++)
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
                output[match>=0?match:count++]=result;
                }
            }
            return count;
        }
    }
}
