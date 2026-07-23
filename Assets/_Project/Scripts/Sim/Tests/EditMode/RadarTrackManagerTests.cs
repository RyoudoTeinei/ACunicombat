using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using ACunicombat.Tracking;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace ACunicombat.Tests.EditMode
{
    public class RadarTrackManagerTests
    {
        [Test]
        public void FirstMeasurement_CountsAsSingleTentativeHit()
        {
            var manager = new RadarTrackManager();
            using var measurements = new NativeList<Measurement>(4, Allocator.Temp);
            measurements.Add(MeasurementAt(0.04));

            manager.Process(measurements, double3.zero, 0.04);

            Assert.That(manager.TryGetBestTrack(out var track), Is.True);
            Assert.That(track.HitCount, Is.EqualTo(1));
            Assert.That(track.Lifecycle, Is.EqualTo(TrackLifecycle.Tentative));
        }

        [Test]
        public void ThreeAssociatedHits_ConfirmTrackWithoutTruthIdentity()
        {
            var manager = new RadarTrackManager();
            using var measurements = new NativeList<Measurement>(4, Allocator.Temp);

            for (var i = 1; i <= 3; i++)
            {
                var time = i * 0.04;
                measurements.Clear();
                measurements.Add(MeasurementAt(time));
                manager.Process(measurements, double3.zero, time);
            }

            Assert.That(manager.Count, Is.EqualTo(1));
            Assert.That(manager.TryGetBestTrack(out var track), Is.True);
            Assert.That(track.TrackId.IsValid, Is.True);
            Assert.That(track.HitCount, Is.EqualTo(3));
            Assert.That(track.Lifecycle, Is.EqualTo(TrackLifecycle.Confirmed));
            Assert.That(track.Position.z, Is.EqualTo(1_000.0).Within(2.0));
            Assert.That(track.SourceMask, Is.EqualTo(TrackSourceMask.Radar));
            Assert.That(track.Affiliation, Is.EqualTo(TrackAffiliation.Unknown));
            Assert.That(track.Classification, Is.Zero);
            Assert.That(track.IdentityConfidence, Is.Zero);

            var publicTracks = new TrackSnapshot[2];
            Assert.That(manager.CopyTracks(publicTracks), Is.EqualTo(1));
            Assert.That(publicTracks[0].TrackId, Is.EqualTo(track.TrackId));
            Assert.That(publicTracks[0].Affiliation, Is.EqualTo(TrackAffiliation.Unknown));
        }

        [Test]
        public void OverlappingDwellsInSameTick_RefineOneTrackAndCountOneHit()
        {
            var manager = new RadarTrackManager();
            using var measurements = new NativeList<Measurement>(4, Allocator.Temp);
            measurements.Add(MeasurementAt(0.04));
            var secondDwell = MeasurementAt(0.04);
            secondDwell.Range = 1_001f;
            measurements.Add(secondDwell);

            manager.Process(measurements, double3.zero, 0.04);

            Assert.That(manager.Count, Is.EqualTo(1));
            Assert.That(manager.TryGetBestTrack(out var track), Is.True);
            Assert.That(track.HitCount, Is.EqualTo(1));
            Assert.That(track.Lifecycle, Is.EqualTo(TrackLifecycle.Tentative));
            Assert.That(track.Position.z, Is.EqualTo(1_000.5).Within(2.0));
        }

        [Test]
        public void FusionTicksWithoutObservation_DoNotImmediatelyCoastConfirmedTrack()
        {
            var manager = new RadarTrackManager();
            using var measurements = new NativeList<Measurement>(4, Allocator.Temp);

            for (var i = 1; i <= 3; i++)
            {
                var time = i * 0.04;
                measurements.Clear();
                measurements.Add(MeasurementAt(time));
                manager.Process(measurements, double3.zero, time);
            }

            for (var i = 4; i <= 8; i++)
            {
                measurements.Clear();
                manager.Process(measurements, double3.zero, i * 0.04);
            }

            Assert.That(manager.Count, Is.EqualTo(1));
            Assert.That(manager.TryGetBestTrack(out var track), Is.True);
            Assert.That(track.Lifecycle, Is.EqualTo(TrackLifecycle.Confirmed));
            Assert.That(track.CoastCount, Is.Zero);
        }

        [Test]
        public void DwellPointingElsewhere_DoesNotCountAsTrackMiss()
        {
            var manager = new RadarTrackManager();
            using var measurements = new NativeList<Measurement>(4, Allocator.Temp);
            using var dwells = new NativeList<EmissionEvent>(4, Allocator.Temp);

            for (var i = 1; i <= 3; i++)
            {
                var time = i * 0.04;
                measurements.Clear();
                measurements.Add(MeasurementAt(time));
                manager.Process(measurements, double3.zero, time);
            }

            for (var i = 4; i <= 12; i++)
            {
                var time = i * 0.04;
                measurements.Clear();
                dwells.Clear();
                dwells.Add(RadarDwell(new float3(1f, 0f, 0f), 10f, time));
                manager.Process(measurements, dwells, double3.zero, double3.zero, time);
            }

            Assert.That(manager.TryGetBestTrack(out var track), Is.True);
            Assert.That(track.Lifecycle, Is.EqualTo(TrackLifecycle.Confirmed));
            Assert.That(track.CoastCount, Is.Zero);
        }

        [Test]
        public void CoveredTrackWithoutDetection_CountsOneMissPerFusionTick()
        {
            var manager = new RadarTrackManager();
            using var measurements = new NativeList<Measurement>(4, Allocator.Temp);
            using var dwells = new NativeList<EmissionEvent>(4, Allocator.Temp);

            for (var i = 1; i <= 3; i++)
            {
                var time = i * 0.04;
                measurements.Clear();
                measurements.Add(MeasurementAt(time));
                manager.Process(measurements, double3.zero, time);
            }

            for (var i = 4; i <= 6; i++)
            {
                var time = i * 0.04;
                measurements.Clear();
                dwells.Clear();
                dwells.Add(RadarDwell(new float3(0f, 0f, 1f), 10f, time));
                dwells.Add(RadarDwell(new float3(0f, 0f, 1f), 10f, time));
                manager.Process(measurements, dwells, double3.zero, double3.zero, time);
            }

            Assert.That(manager.TryGetBestTrack(out var track), Is.True);
            Assert.That(track.Lifecycle, Is.EqualTo(TrackLifecycle.Coasting));
            Assert.That(track.CoastCount, Is.EqualTo(3));
        }

        [Test]
        public void SingleTargetAtOneHundredMetersPerSecond_DoesNotSplitIntoCoastTrail()
        {
            var manager = new RadarTrackManager();
            using var measurements = new NativeList<Measurement>(4, Allocator.Temp);
            var initialPosition = new double3(0.0, 100.0, 5_000.0);
            var velocity = new double3(100.0, 0.0, 0.0);

            for (var tick = 1; tick <= 150; tick++)
            {
                var time = tick * 0.04;
                measurements.Clear();
                // A broad search radar need not revisit this target every fusion tick.
                if ((tick - 1) % 5 == 0)
                {
                    measurements.Add(MeasurementAtPosition(
                        time,
                        initialPosition + velocity * time,
                        velocity));
                }

                manager.Process(measurements, double3.zero, time);
            }

            Assert.That(manager.Count, Is.EqualTo(1));
            Assert.That(manager.TryGetBestTrack(out var track), Is.True);
            Assert.That(track.Lifecycle, Is.EqualTo(TrackLifecycle.Confirmed));
            Assert.That(track.Position.x, Is.EqualTo(600.0).Within(25.0));
            Assert.That(track.Position.y, Is.EqualTo(100.0).Within(5.0));
            Assert.That(track.Position.z, Is.EqualTo(5_000.0).Within(5.0));
            Assert.That(track.Velocity.x, Is.EqualTo(100.0).Within(15.0));
        }

        [Test]
        public void FastMovingOwnship_DopplerCompensationKeepsStationaryTargetOnOneTrack()
        {
            var manager = new RadarTrackManager();
            using var measurements = new NativeList<Measurement>(4, Allocator.Temp);
            var targetPosition = new double3(0.0, 100.0, 10_000.0);
            var targetVelocity = double3.zero;
            var sensorVelocity = new double3(0.0, 0.0, 500.0);

            for (var tick = 1; tick <= 100; tick++)
            {
                var time = tick * 0.04;
                var sensorPosition = sensorVelocity * time;
                measurements.Clear();
                measurements.Add(MeasurementFromMovingSensor(
                    time,
                    sensorPosition,
                    sensorVelocity,
                    targetPosition,
                    targetVelocity));

                manager.Process(
                    measurements,
                    sensorPosition,
                    sensorVelocity,
                    time);
            }

            Assert.That(manager.Count, Is.EqualTo(1));
            Assert.That(manager.TryGetBestTrack(out var track), Is.True);
            Assert.That(track.Lifecycle, Is.EqualTo(TrackLifecycle.Confirmed));
            Assert.That(track.Position.z, Is.EqualTo(targetPosition.z).Within(10.0));
            Assert.That(math.length(track.Velocity), Is.LessThan(20.0));
        }

        private static Measurement MeasurementAt(double timestamp)
        {
            return new Measurement
            {
                SensorId = new SensorId(new Handle(0, 1)),
                Timestamp = timestamp,
                Range = 1_000f,
                Direction = new float3(0f, 0f, 1f),
                RadialVelocity = 0f,
                LosRate = float3.zero,
                RangeVar = 4f,
                AngleVar = 1e-6f,
                VelocityVar = 1f,
                LosRateVar = 1e-6f,
                SnrDb = 20f,
                Flags = MeasurementFlags.None,
            };
        }

        private static Measurement MeasurementAtPosition(
            double timestamp,
            double3 position,
            double3 velocity)
        {
            var range = math.length(position);
            var direction = position / range;
            return new Measurement
            {
                SensorId = new SensorId(new Handle(0, 1)),
                Timestamp = timestamp,
                Range = (float)range,
                Direction = (float3)direction,
                RadialVelocity = (float)math.dot(velocity, direction),
                LosRate = float3.zero,
                RangeVar = 4f,
                AngleVar = 1e-6f,
                VelocityVar = 1f,
                LosRateVar = 1e-6f,
                SnrDb = 20f,
                Flags = MeasurementFlags.None,
            };
        }

        private static Measurement MeasurementFromMovingSensor(
            double timestamp,
            double3 sensorPosition,
            double3 sensorVelocity,
            double3 targetPosition,
            double3 targetVelocity)
        {
            var relativePosition = targetPosition - sensorPosition;
            var range = math.length(relativePosition);
            var direction = relativePosition / range;
            var relativeVelocity = targetVelocity - sensorVelocity;
            return new Measurement
            {
                SensorId = new SensorId(new Handle(0, 1)),
                Timestamp = timestamp,
                Range = (float)range,
                Direction = (float3)direction,
                RadialVelocity = (float)math.dot(relativeVelocity, direction),
                LosRate = float3.zero,
                RangeVar = 4f,
                AngleVar = 1e-6f,
                VelocityVar = 1f,
                LosRateVar = 1e-6f,
                SnrDb = 20f,
                Flags = MeasurementFlags.None,
            };
        }

        private static EmissionEvent RadarDwell(
            float3 direction,
            float beamWidthDegrees,
            double timestamp)
        {
            return new EmissionEvent
            {
                EmitterId = new EmitterId(new Handle(0, 1)),
                StartTime = timestamp,
                EndTime = timestamp + 0.01,
                Origin = double3.zero,
                Direction = direction,
                BeamWidth = math.radians(beamWidthDegrees),
                MaxRangeMeters = 100_000.0,
                EmissionType = EmissionType.RadarDwell,
            };
        }
    }
}
