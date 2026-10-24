using System;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.Driver;
using ACunicombat.Simulation.World;
using ACunicombat.Tracking;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace ACunicombat.SensorBench
{
    public sealed class PlatformTrackContextTests
    {
        [Test] public void TwoObserversDoNotShareDetectionsOrTracks()
        {
            using var a = new PlatformTrackContext();
            using var b = new PlatformTrackContext();
            var pa = SensorBenchTests.Profile(0, new SimEntityId(new Handle(0, 1)));
            var pb = SensorBenchTests.Profile(1, new SimEntityId(new Handle(1, 1)));
            a.BindPrimaryRadar(in pa); b.BindPrimaryRadar(in pb);
            using var measurements = new NativeList<Measurement>(1, Allocator.Temp);
            using var emissions = new NativeList<EmissionEvent>(1, Allocator.Temp);
            for (var step = 1; step <= 8; step++)
            {
                measurements.Clear(); measurements.Add(Detection(pa.SensorId, step * .04));
                a.Process(measurements, emissions, double3.zero, double3.zero, step * .04);
                b.Process(measurements, emissions, double3.zero, double3.zero, step * .04);
            }
            Assert.That(a.Count, Is.EqualTo(1));
            Assert.That(b.Count, Is.Zero);
        }
        [Test] public void OldSensorGenerationCannotCreateNewObserverTrack()
        {
            using var context = new PlatformTrackContext();
            var profile = SensorBenchTests.Profile(0, new SimEntityId(new Handle(0, 2)));
            profile.SensorId = new SensorId(new Handle(0, 2)); context.BindPrimaryRadar(in profile);
            using var measurements = new NativeList<Measurement>(1, Allocator.Temp);
            using var emissions = new NativeList<EmissionEvent>(1, Allocator.Temp);
            measurements.Add(Detection(new SensorId(new Handle(0, 1)), .04));
            context.Process(measurements, emissions, double3.zero, double3.zero, .04);
            Assert.That(context.Count, Is.Zero);
        }
        [Test] public void DifferentOwnerCannotInheritOldContext()
        {
            using var context = new PlatformTrackContext();
            var profile = SensorBenchTests.Profile(0, new SimEntityId(new Handle(0, 1)));
            context.BindPrimaryRadar(in profile);
            context.BindPrimaryRadar(in profile); // Same observer may update radar parameters.
            profile.OwnerEntityId = new SimEntityId(new Handle(0, 2));
            Assert.Throws<InvalidOperationException>(() => context.BindPrimaryRadar(in profile));
        }
        [Test] public void ExtractedContextPreservesLegacyTrackResults()
        {
            using var context = new PlatformTrackContext();
            var legacy = new RadarTrackManager();
            var profile = SensorBenchTests.Profile(0, new SimEntityId(new Handle(0, 1)));
            context.BindPrimaryRadar(in profile);
            using var measurements = new NativeList<Measurement>(1, Allocator.Temp);
            using var emissions = new NativeList<EmissionEvent>(1, Allocator.Temp);
            for (var step = 1; step <= 30; step++)
            {
                var time = step * .04;
                measurements.Clear();
                if (step < 16 || step > 23) measurements.Add(Detection(profile.SensorId, time));
                emissions.Clear(); emissions.Add(new EmissionEvent { EmitterId = profile.EmitterId,
                    StartTime = time, EndTime = time + .008, Direction = new float3(0,0,1),
                    BeamWidth = .2f, MaxRangeMeters = 10000, EmissionType = EmissionType.RadarDwell });
                context.Process(measurements, emissions, double3.zero, double3.zero, time);
                legacy.Process(measurements, emissions, double3.zero, double3.zero, time);
                Assert.That(context.Count, Is.EqualTo(legacy.Count));
                var found = legacy.TryGetBestTrack(out var before);
                Assert.That(context.TryGetBestTrack(out var after), Is.EqualTo(found));
                if (!found) continue;
                Assert.That(after.Position, Is.EqualTo(before.Position));
                Assert.That(after.Velocity, Is.EqualTo(before.Velocity));
                Assert.That(after.Lifecycle, Is.EqualTo(before.Lifecycle));
                Assert.That(after.Confidence, Is.EqualTo(before.Confidence));
                Assert.That(after.LastUpdateTime, Is.EqualTo(before.LastUpdateTime));
                for (var i=0;i<6;i++) for(var j=i;j<6;j++)
                    Assert.That(after.GetCovariance(i,j), Is.EqualTo(before.GetCovariance(i,j)));
            }
        }
    
        [Test] public void SameLocalTrackNumberCannotCrossObserverBoundary()
        {
            using var a=new PlatformTrackContext();using var b=new PlatformTrackContext();
            var pa=SensorBenchTests.Profile(0,new SimEntityId(new Handle(0,1)));
            var pb=SensorBenchTests.Profile(1,new SimEntityId(new Handle(1,1)));
            a.BindPrimaryRadar(in pa);b.BindPrimaryRadar(in pb);
            using var detections=new NativeList<Measurement>(2,Allocator.Temp);
            using var emissions=new NativeList<EmissionEvent>(1,Allocator.Temp);
            detections.Add(Detection(pa.SensorId,.04));detections.Add(Detection(pb.SensorId,.04));
            a.Process(detections,emissions,double3.zero,double3.zero,.04);
            b.Process(detections,emissions,double3.zero,double3.zero,.04);
            Assert.That(a.TryGetBestTrack(out var ta)&&b.TryGetBestTrack(out var tb),Is.True);
            b.TryGetBestTrack(out var other);
            Assert.That(other.TrackId,Is.EqualTo(ta.TrackId));
            var reference=a.Reference(ta.TrackId);
            Assert.That(a.TryGetTrack(in reference,out _),Is.True);
            Assert.That(b.TryGetTrack(in reference,out _),Is.False);
        }
        private static Measurement Detection(SensorId id, double time) => new Measurement {
            SensorId=id, Timestamp=time, Range=1000, Direction=new float3(0,0,1),
            RangeVar=4, AngleVar=1e-6f, VelocityVar=1, LosRateVar=1e-6f,
            SnrDb=30, ObjectClass=TrackObjectClass.AirVehicle, ClassificationConfidence=1
        };
    }
}