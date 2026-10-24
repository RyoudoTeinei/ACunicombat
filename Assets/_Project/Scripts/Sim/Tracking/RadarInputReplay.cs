using System;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace ACunicombat.Tracking
{
    [Serializable] public sealed class RadarBatchDecision
    {
        public string manager;public double time,margin,score;public uint sensor,selected;public ulong dwell;
        public uint[] candidates;public double[] costs;
    }
    [Serializable] public sealed class RadarReplayMeasurement {public uint sensor;public Measurement state;}
    [Serializable] public sealed class RadarReplayDwell {public uint emitter;public EmissionEvent state;}
    [Serializable] public sealed class RadarReplayTrack
    {
        public uint id;public double3 position,velocity;public double[] covariance;
        public double observed,predicted;public int hits,coasts;public TrackLifecycle lifecycle;
        public TrackObjectClass objectClass;public TrackSourceMask sources;public bool closed;
        public static RadarReplayTrack From(TrackSnapshot s)
        {
            var p=new double[21];int at=0;
            for(int row=0;row<6;row++)for(int col=row;col<6;col++)p[at++]=s.GetCovariance(row,col);
            return new RadarReplayTrack{id=s.TrackId.Packed,position=s.Position,velocity=s.Velocity,covariance=p,
                observed=s.LastUpdateTime,predicted=s.LastPredictTime,hits=s.HitCount,coasts=s.CoastCount,
                lifecycle=s.Lifecycle,objectClass=s.ObjectClass,sources=s.SourceMask,closed=s.AssociationClosed};
        }
    }
    [Serializable] public sealed class RadarReplayOperation
    {
        public int version=1;public string manager,kind,reason,lossless;
        public double time,reportTime,memory;public double3 observer,observerVelocity,position,velocity;
        public int capacity;public uint track,laserSensor;
        public bool hasDwells;public LaserRangeObservation laser;
        public RadarReplayMeasurement[] measurements;public RadarReplayDwell[] dwells;
        public RadarReplayTrack[] expected;
        private static Handle Handle(uint p)=>new Handle((int)(p&0xffffff),(byte)(p>>24));
        public bool Apply(RadarTrackManager target,out string detail)
        {
            if(!string.IsNullOrEmpty(lossless))return ReplayValueCodec.Decode<RadarReplayOperation>(lossless).Apply(target,out detail);
            bool accepted=true;
            switch(kind)
            {
                case "create":break;
                case "limits":target.ConfigureLimits(capacity,memory);break;
                case "close":accepted=target.CloseMissileAssociation(new TrackId(Handle(track)),time,reason);break;
                case "telemetry":accepted=target.TryRefineMissileTelemetry(new TrackId(Handle(track)),position,velocity,reportTime,time);break;
                case "laser":
                    var l=laser;l.CuedTrackId=new TrackId(Handle(track));l.SensorId=new SensorId(Handle(laserSensor));
                    accepted=target.TryRefineConfirmedTrackWithLaser(l,observer,time,out _);break;
                case "process":
                    using(var ms=new NativeList<Measurement>(Math.Max(1,measurements.Length),Allocator.Temp))
                    using(var ds=new NativeList<EmissionEvent>(Math.Max(1,dwells.Length),Allocator.Temp))
                    {
                        foreach(var x in measurements){var m=x.state;m.SensorId=new SensorId(Handle(x.sensor));ms.Add(m);}
                        foreach(var x in dwells){var d=x.state;d.EmitterId=new EmitterId(Handle(x.emitter));ds.Add(d);}
                        if(hasDwells)target.Process(ms,ds,observer,observerVelocity,time);else target.Process(ms,observer,observerVelocity,time);
                    }
                    break;
                default:throw new InvalidOperationException("Unknown radar replay operation: "+kind);
            }
            if(!accepted){detail=kind+" rejected on replay";return false;}
            var actual=RadarReplayJournal.Snapshot(target);
            if(actual.Length!=expected.Length){detail="radar track count differs";return false;}
            for(int i=0;i<actual.Length;i++)if(JsonUtility.ToJson(actual[i])!=JsonUtility.ToJson(expected[i])){detail=$"radar track {expected[i].id} differs after {kind}\nactual: {JsonUtility.ToJson(actual[i])}\nexpected: {JsonUtility.ToJson(expected[i])}";return false;}
            detail="Exact output match";return true;
        }
    }
    public static class RadarReplayJournal
    {
        public static event Action<RadarBatchDecision> BatchProduced;
        public static bool BatchEnabled=>BatchProduced!=null;
        internal static void EmitBatch(RadarBatchDecision decision)=>BatchProduced?.Invoke(decision);
        public static event Action<RadarReplayOperation> Produced;
        public static bool Enabled=>Produced!=null;
        public static RadarReplayTrack[] Snapshot(RadarTrackManager manager)
        {
            var tracks=new TrackSnapshot[RadarTrackManager.MaxTracks];int count=manager.CopyTracks(tracks);
            var result=new RadarReplayTrack[count];for(int i=0;i<count;i++)result[i]=RadarReplayTrack.From(tracks[i]);return result;
        }
        internal static void Emit(RadarTrackManager manager,RadarReplayOperation operation)
        {
            if(!Enabled||manager.ReplayDepth>0)return;
            operation.manager=manager.ReplayIdentity;operation.expected=Snapshot(manager);
            operation.lossless=ReplayValueCodec.Encode(operation);Produced?.Invoke(operation);
        }
        internal static RadarReplayOperation Capture(NativeList<Measurement> ms,NativeList<EmissionEvent> ds,bool hasDwells,double3 observer,double3 velocity,double time)
        {
            if(!Enabled)return null;
            var op=new RadarReplayOperation{kind="process",time=time,observer=observer,observerVelocity=velocity,hasDwells=hasDwells,
                measurements=new RadarReplayMeasurement[ms.Length],dwells=new RadarReplayDwell[hasDwells&&ds.IsCreated?ds.Length:0]};
            for(int i=0;i<op.measurements.Length;i++)op.measurements[i]=new RadarReplayMeasurement{sensor=ms[i].SensorId.Packed,state=ms[i]};
            for(int i=0;i<op.dwells.Length;i++)op.dwells[i]=new RadarReplayDwell{emitter=ds[i].EmitterId.Value.Packed,state=ds[i]};
            return op;
        }
    }
}
