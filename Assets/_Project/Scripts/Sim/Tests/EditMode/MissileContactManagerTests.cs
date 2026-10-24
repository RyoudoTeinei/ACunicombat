using ACunicombat.FireControl;
using ACunicombat.Sensing.Contracts;
using NUnit.Framework;
using Unity.Mathematics;
using ACunicombat.Simulation.Core;
using UnityEngine;
using ACunicombat.Tracking;
using Unity.Collections;
using System.Collections.Generic;

namespace ACunicombat.Tests.EditMode
{
    public class MissileContactManagerTests
    {
        [Test]
        public void RadarOffDoesNotExpireContinuouslyRefinedTelemetryKinematics()
        {
            var radar=new RadarTrackManager();var contacts=new MissileContactManager();
            var terminal=new LinkTerminalId(new Handle(0,1));
            using var measurements=new NativeList<Measurement>(1,Allocator.Temp);
            for(int i=1;i<=8;i++)
            {
                double t=i*.04;measurements.Clear();measurements.Add(new Measurement
                {SensorId=new SensorId(new Handle(0,1)),Timestamp=t,Range=(float)(5000+500*t+100*t*t),Direction=new float3(0,0,1),
                 RadialVelocity=(float)(500+200*t),RangeVar=1,AngleVar=1e-8f,VelocityVar=1,SnrDb=40,
                 ObjectClass=TrackObjectClass.Missile,ClassificationConfidence=1});
                radar.Process(measurements,double3.zero,t);
            }
            Assert.IsTrue(radar.TryGetBestTrack(out var original));int refinements=0;
            double now=.32;
            contacts.TelemetryRefinement=(id,p,v,time)=>{if(radar.TryRefineMissileTelemetry(id,p,v,time,now))refinements++;};
            var raw=new TrackSnapshot[64];var output=new MissileTrackSnapshot[128];var config=MissileTrackFusionConfig.Default;
            measurements.Clear();
            for(int i=9;i<=80;i++)
            {
                now=i*.04;radar.Process(measurements,double3.zero,now);
                double sent=now-.04;
                contacts.Reports[0]=new FriendlyMissileLinkReport{Active=true,SourceTerminal=terminal,StateTimestamp=sent,
                    Position=new double3(0,0,5000+500*sent+100*sent*sent),Velocity=new double3(0,0,500+200*sent)};
                int count=MissileTrackFusion.Fuse(config,raw,radar.CopyTracks(raw),null,0,null,0,double3.zero,double3.zero,now,output);
                count=contacts.ResolveIdentities(now,double3.zero,output,count);
                Assert.AreEqual(1,count,"A live telemetry stream and its coasting radar history are one contact.");
            }
            Assert.Greater(refinements,65,"Telemetry must continue refining after the last radar hit becomes older than one second.");
            Assert.IsTrue(radar.TryGetTrack(original.TrackId,out var current));
            Assert.AreEqual(original.LastUpdateTime,current.LastUpdateTime,"Radio reports must not fabricate radar hits.");
            Assert.Greater(current.SourceEvidence.DataLink.Timestamp,3);
            Assert.Greater(current.Velocity.z,1050);
        }

        [Test]
        public void UnityFixedStepRoundingCannotStretchFortyMillisecondScanning()
        {
            double dt=2822399.0/141120000.0,now=0,next=0;int last=-1,count=0;
            var clock=new FixedTickClock(.04,10);
            for(int tick=1;tick<=100;tick++)
            {
                now+=dt;
                if(!SimulationTime.IsDue(now,next))continue;
                if(last>=0)Assert.That(tick-last,Is.EqualTo(2));
                last=tick;count++;next=now+(double).04f;
            }
            Assert.That(count,Is.EqualTo(50));
            int sensorTicks=0;for(int i=0;i<100;i++)sensorTicks+=clock.Advance(dt);
            Assert.That(sensorTicks,Is.EqualTo(50));
            Assert.That(SimulationTime.IsDue(1,1.0001),Is.False,"A real future deadline must not run early.");
        }

        [Test]
        public void LocalLaunchAndRadioAreOneEvidenceRowAndOnePublicIdentity()
        {
            var manager=new MissileContactManager();var id=System.Guid.NewGuid();
            var terminal=new LinkTerminalId(new Handle(0,1));
            manager.RegisterOwnLaunch(id,new double3(0,0,1000),double3.zero,MissileMissionRole.AirToAir,1,model:new FixedString128Bytes("SIM-29"));
            manager.BindOwnTerminal(id,terminal);manager.OwnLaunches[id].Track=new TrackId(new Handle(0,1));
            manager.Reports[0]=new FriendlyMissileLinkReport{Active=true,SourceTerminal=terminal,Position=new double3(0,0,1200),StateTimestamp=1.2,HasLaunchTimestamp=true,LaunchTimestamp=1};
            var output=new MissileTrackSnapshot[128];
            for(int i=0;i<2;i++)output[i]=new MissileTrackSnapshot{TrackId=new TrackId(new Handle(i,1)),Position=new double3(0,0,1000+200*i),FirstObservationTime=1.04,LastUpdateTime=1.2,LastEvidenceTime=1.2,KinematicUpdateTime=1.2,PositionUncertaintyMeters=2,Quality=MissileTrackQuality.Warning};
            int count=manager.ResolveIdentities(1.2,double3.zero,output,2),owned=0;
            for(int i=0;i<count;i++)if(output[i].OwnLaunchIdentity==id){Assert.That(output[i].KnownModel.ToString(),Is.EqualTo("SIM-29"));owned++;Assert.That(output[i].AssociatedSensorTrackId,Is.EqualTo(new TrackId(new Handle(1,1))));}
            Assert.That(owned,Is.EqualTo(1));Assert.That(output[0].IsFriendly,Is.False);
        }

        [Test]
        public void IntentProjectionDoesNotTreatEvidenceAgeAsStateAge()
        {
            var candidate=new FriendlyMissileIntentCandidate{Position=new double3(0,0,100),Velocity=new double3(0,0,100),LastEvidenceTime=9,StateTime=10};
            Assert.That(candidate.PredictPosition(10).z,Is.EqualTo(100));
            Assert.That(candidate.PredictPosition(10.2).z,Is.EqualTo(120).Within(1e-8));
        }

        [Test]
        public void RadarInputsReplayFromCreationIncludingTelemetryAndEndOfLife()
        {
            var operations=new List<RadarReplayOperation>();
            System.Action<RadarReplayOperation> capture=op=>operations.Add(JsonUtility.FromJson<RadarReplayOperation>(JsonUtility.ToJson(op)));
            RadarReplayJournal.Produced+=capture;
            try
            {
                var manager=new RadarTrackManager();manager.ConfigureLimits(8,3);
                using(var measurements=new NativeList<Measurement>(2,Allocator.Temp))
                {
                    for(int i=1;i<=4;i++)
                    {
                        measurements.Clear();measurements.Add(new Measurement{SensorId=new SensorId(new Handle(1,1)),DwellSequence=(ulong)i,Timestamp=.04*i,
                            Range=1000,Direction=new float3(0,0,1),RangeVar=1,AngleVar=.00001f,RadialVelocity=0,VelocityVar=1,
                            ObjectClass=TrackObjectClass.Missile,ClassificationConfidence=1,SnrDb=30});
                        manager.Process(measurements,double3.zero,.04*i);
                    }
                    Assert.That(manager.TryGetBestTrack(out var track),Is.True);
                    Assert.That(manager.TryRefineMissileTelemetry(track.TrackId,new double3(0,0,1000),double3.zero,.16,.16),Is.True);
                    Assert.That(manager.CloseMissileAssociation(track.TrackId,.17),Is.True);
                }
            }
            finally{RadarReplayJournal.Produced-=capture;}
            var replay=new RadarTrackManager();
            foreach(var op in operations)Assert.That(op.Apply(replay,out string detail),Is.True,detail);
            Assert.That(operations.Count,Is.GreaterThanOrEqualTo(8));
        }

        [Test]
        public void RecordedEvidenceReplaysExactlyAfterSerializationAndPermutation()
        {
            var manager=new MissileContactManager();
            var observations=new MissileTrackSnapshot[128];
            for(int i=0;i<2;i++)
            {
                observations[i]=new MissileTrackSnapshot{TrackId=new TrackId(new Handle(i,1)),Position=new double3(0,0,1000+500*i),PositionUncertaintyMeters=3,VelocityUncertaintyMetersPerSecond=10,LastUpdateTime=10,KinematicUpdateTime=10,LastEvidenceTime=10,Quality=MissileTrackQuality.Warning};
                manager.Reports[i]=new FriendlyMissileLinkReport{Active=true,SourceTerminal=new LinkTerminalId(new Handle(i,1)),Position=observations[i].Position,StateTimestamp=10};
            }
            MissileContactReplayFrame captured=null;manager.FrameEvaluated+=f=>captured=f;
            Assert.That(manager.ResolveIdentities(10,double3.zero,observations,2),Is.EqualTo(2));
            var restored=JsonUtility.FromJson<MissileContactReplayFrame>(JsonUtility.ToJson(captured)).RestoreExact();
            Assert.That(restored.Matches(out string detail),Is.True,detail);
            System.Array.Reverse(restored.observations);
            Assert.That(restored.Matches(out detail),Is.True,detail);
        }

        [Test]
        public void LocalLaunchIsPublishedWithoutRadarAndEndsWithoutHudSpecialCases()
        {
            var manager=new MissileContactManager();var id=System.Guid.NewGuid();
            manager.OwnLaunches[id]=new OwnLaunchRecord{Id=id,ContactId=manager.AllocateOwnIdentity(),Position=new double3(0,0,100),Velocity=new double3(0,0,1000),Time=10,LaunchTime=10};
            var output=new MissileTrackSnapshot[128];
            Assert.That(manager.ResolveIdentities(10.1,double3.zero,output,0),Is.EqualTo(1));
            Assert.That(output[0].OwnLaunchIdentity,Is.EqualTo(id));Assert.That(output[0].IsFriendly,Is.True);
            Assert.That(output[0].Position.z,Is.EqualTo(200).Within(1e-8));
            manager.OwnLaunches.Remove(id);
            Assert.That(manager.ResolveIdentities(10.2,double3.zero,output,0),Is.Zero);
        }

        [Test]
        public void FutureReportCannotCreateOrAuthenticateAContact()
        {
            var manager=new MissileContactManager();manager.Reports[0]=new FriendlyMissileLinkReport{Active=true,SourceTerminal=new LinkTerminalId(new Handle(0,1)),StateTimestamp=11};
            Assert.That(manager.ResolveIdentities(10,double3.zero,new MissileTrackSnapshot[128],0),Is.Zero);
            Assert.That(manager.Associations[0].HasTrack,Is.False);
        }

        [Test]
        public void BatchAssignmentDoesNotLoseConstrainedRowToGreedyChoice()
        {
            var result=OneToOneAssignment.Solve(new double[,]{{.1,.2},{.3,OneToOneAssignment.Rejected}},.35,out _);
            Assert.That(result,Is.EqualTo(new[]{1,0}));
            var reversed=OneToOneAssignment.Solve(new double[,]{{.3,OneToOneAssignment.Rejected},{.1,.2}},.35,out _);
            Assert.That(reversed,Is.EqualTo(new[]{0,1}));
        }

        [Test]
        public void EquallyPlausibleWholeBatchAssignmentsRemainAmbiguous()
        {
            Assert.That(OneToOneAssignment.Solve(new double[,]{{1,1},{1,1}},.35,out _),Is.EqualTo(new[]{-1,-1}));
            Assert.That(OneToOneAssignment.Solve(new double[,]{{OneToOneAssignment.Rejected}},.35,out _),Is.EqualTo(new[]{-1}));
        }

        [Test]
        public void PositionAndCovarianceProjectionShareTheSameEpoch()
        {
            var track=new TrackSnapshot{LastPredictTime=10,Position=new double3(0,0,100),Velocity=new double3(10,0,0)};
            var pp=new[]{4d,9d,16d};var pv=new[]{2d,-3d,4d};var vv=new[]{100d,400d,900d};
            for(int i=0;i<3;i++){track.SetCovariance(i,i,pp[i]);track.SetCovariance(i,i+3,pv[i]);track.SetCovariance(i+3,i+3,vv[i]);}
            Assert.That(track.PredictPosition(10.2).x,Is.EqualTo(2).Within(1e-10));
            Assert.That(track.PredictPositionUncertainty(10.2),Is.EqualTo(System.Math.Sqrt(86.2)).Within(1e-10));
            Assert.That(track.PredictPositionUncertainty(9),Is.EqualTo(System.Math.Sqrt(29)).Within(1e-10));
            Assert.That(track.GetCovariance(0,0),Is.EqualTo(4),"Projection must not mutate the observation.");
        }
    }
}
