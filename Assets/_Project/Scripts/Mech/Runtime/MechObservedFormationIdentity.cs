using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;
namespace ACunicombat.Mechs
{
    // Observer-local formation evidence only. Never reads scene teams, fleets or transforms.
    public sealed class MechObservedFormationIdentity
    {
        public struct Inference { public TrackId Member, HostileAnchor; }
        private struct Pair { public double First, Last; public double3 Offset; }
        private readonly Dictionary<(TrackId,TrackId),Pair> pairs=new Dictionary<(TrackId,TrackId),Pair>();
        private readonly List<(TrackId,TrackId)> retired=new List<(TrackId,TrackId)>();
        private double previous=double.NegativeInfinity;
        public const double HoldSeconds=3, MaximumSeparationMetres=5000, MaximumRelativeSpeed=100, MaximumOffsetDriftMetres=200;
        public void Update(TrackSnapshot[] tracks,int count,Dictionary<TrackId,ContactIdentitySnapshot> identities,double now,List<Inference> output)
        {
            output.Clear();if(!double.IsFinite(now))return;if(now<previous)pairs.Clear();previous=now;
            for(int i=0;i<count;i++)
            {
                var member=tracks[i];if(!Usable(member,now)||!identities.TryGetValue(member.TrackId,out var identity)||identity.Relation!=IdentityRelation.Unknown&&identity.Relation!=IdentityRelation.SuspectedHostile)continue;
                for(int j=0;j<count;j++)
                {
                    var anchor=tracks[j];if(i==j||!Usable(anchor,now)||!identities.TryGetValue(anchor.TrackId,out var source)||source.Relation!=IdentityRelation.Hostile||source.Reason==IdentityReason.FormationAssociation||source.Conflict)continue;
                    var key=(member.TrackId,anchor.TrackId);var offset=member.PredictPosition(now)-anchor.PredictPosition(now);
                    if(math.lengthsq(offset)>MaximumSeparationMetres*MaximumSeparationMetres||math.lengthsq(member.Velocity-anchor.Velocity)>MaximumRelativeSpeed*MaximumRelativeSpeed||math.dot(math.normalize(member.Velocity),math.normalize(anchor.Velocity))<.9659258){pairs.Remove(key);continue;}
                    if(!pairs.TryGetValue(key,out var pair)||now-pair.Last>1||math.distance(offset,pair.Offset)>MaximumOffsetDriftMetres)pair=new Pair{First=now,Offset=offset};
                    pair.Last=now;if(pairs.Count<1024||pairs.ContainsKey(key))pairs[key]=pair;
                    if(now-pair.First>=HoldSeconds){output.Add(new Inference{Member=member.TrackId,HostileAnchor=anchor.TrackId});break;}
                }
            }
            retired.Clear();foreach(var p in pairs)if(now-p.Value.Last>1)retired.Add(p.Key);foreach(var k in retired)pairs.Remove(k);
        }
        private static bool Usable(in TrackSnapshot t,double now)
        {
            if(!t.TrackId.IsValid||t.ObjectClass!=TrackObjectClass.AirVehicle||t.Lifecycle!=TrackLifecycle.Confirmed||t.Confidence<.8f||now<t.LastUpdateTime||now-t.LastUpdateTime>1||!math.all(math.isfinite(t.Position))||!math.all(math.isfinite(t.Velocity))||math.lengthsq(t.Velocity)<900)return false;
            double variance=0;for(int i=0;i<3;i++){double v=t.GetCovariance(i,i);if(!double.IsFinite(v)||v<0)return false;variance+=v;}return variance<=250*250;
        }
    }
}
