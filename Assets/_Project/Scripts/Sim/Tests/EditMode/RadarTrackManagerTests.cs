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
        public void MissileSignatureEvidence_PropagatesWithoutEntityIdentity()
        {
            var manager = new RadarTrackManager();
            using var measurements =
                new NativeList<Measurement>(4, Allocator.Temp);

            for (var i = 1; i <= 3; i++)
            {
                var measurement = MeasurementAt(i * 0.04);
                measurement.ObjectClass =
                    TrackObjectClass.Missile;
                measurement.ClassificationConfidence = 0.8f;
                measurements.Clear();
                measurements.Add(measurement);
                manager.Process(
                    measurements,
                    double3.zero,
                    i * 0.04);
            }

            Assert.That(
                manager.TryGetBestTrack(out var track),
                Is.True);
            Assert.That(
                track.ObjectClass,
                Is.EqualTo(TrackObjectClass.Missile));
            Assert.That(
                track.Classification,
                Is.GreaterThan(0.5f));
            Assert.That(
                track.IdentityConfidence,
                Is.Zero);
            Assert.That(
                track.Affiliation,
                Is.EqualTo(TrackAffiliation.Unknown));
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
        public void NoisyOverlappingMissileDwells_DoNotAllocateDuplicateTracks()
        {
            var manager = new RadarTrackManager();
            using var measurements =
                new NativeList<Measurement>(4, Allocator.Temp);
            var velocity = new double3(0.0, 0.0, -350.0);
            var first = MeasurementAtPosition(
                0.04,
                new double3(0.0, 0.0, 7_400.0),
                velocity);
            first.ObjectClass = TrackObjectClass.Missile;
            first.ClassificationConfidence = 0.9f;
            var overlappingDwell = MeasurementAtPosition(
                0.04,
                new double3(22.0, 0.0, 7_398.0),
                velocity);
            overlappingDwell.ObjectClass =
                TrackObjectClass.Missile;
            overlappingDwell.ClassificationConfidence = 0.9f;
            measurements.Add(first);
            measurements.Add(overlappingDwell);

            manager.Process(
                measurements,
                double3.zero,
                double3.zero,
                0.04);

            Assert.That(manager.Count, Is.EqualTo(1));
            Assert.That(
                manager.TryGetBestTrack(out var track),
                Is.True);
            Assert.That(track.HitCount, Is.EqualTo(1));
            Assert.That(
                track.ObjectClass,
                Is.EqualTo(TrackObjectClass.Missile));
        }

        [Test]
        public void OverlappingReturnInsideFiveSigma_DoesNotUseTighterSecondHitGate()
        {
            var manager = new RadarTrackManager();
            using var measurements =
                new NativeList<Measurement>(4, Allocator.Temp);
            var velocity = new double3(0.0, 0.0, -350.0);
            var first = MeasurementAtPosition(
                0.04,
                new double3(0.0, 0.0, 7_400.0),
                velocity);
            first.ObjectClass = TrackObjectClass.Missile;
            first.ClassificationConfidence = 0.9f;
            var overlappingDwell = MeasurementAtPosition(
                0.04,
                new double3(25.0, 0.0, 7_398.0),
                velocity);
            overlappingDwell.ObjectClass = TrackObjectClass.Missile;
            overlappingDwell.ClassificationConfidence = 0.9f;
            measurements.Add(first);
            measurements.Add(overlappingDwell);

            manager.Process(
                measurements,
                double3.zero,
                double3.zero,
                0.04);

            Assert.That(manager.Count, Is.EqualTo(1));
            Assert.That(manager.TryGetBestTrack(out var track), Is.True);
            Assert.That(track.HitCount, Is.EqualTo(1));
        }

        [Test]
        public void ManyOverlappingReturns_DoNotCollapseCovarianceOrSplitNextTick()
        {
            var manager = new RadarTrackManager();
            using var measurements =
                new NativeList<Measurement>(20, Allocator.Temp);
            var velocity = new double3(0.0, 0.0, -350.0);
            for (var i = 0; i < 12; i++)
            {
                var measurement = MeasurementAtPosition(
                    0.04,
                    new double3(i % 2 == 0 ? 3.0 : -3.0, 0.0, 7_400.0),
                    velocity);
                measurement.ObjectClass = TrackObjectClass.Missile;
                measurement.ClassificationConfidence = 0.9f;
                measurements.Add(measurement);
            }

            manager.Process(
                measurements,
                double3.zero,
                double3.zero,
                0.04);
            measurements.Clear();
            var next = MeasurementAtPosition(
                0.08,
                new double3(20.0, 0.0, 7_386.0),
                velocity);
            next.ObjectClass = TrackObjectClass.Missile;
            next.ClassificationConfidence = 0.9f;
            measurements.Add(next);

            manager.Process(
                measurements,
                double3.zero,
                double3.zero,
                0.08);

            Assert.That(manager.Count, Is.EqualTo(1));
            Assert.That(manager.TryGetBestTrack(out var track), Is.True);
            Assert.That(track.HitCount, Is.EqualTo(2));
        }

        [Test]
        public void HighAccelerationMissileAtSearchCadence_RemainsSingleTrack()
        {
            var manager = new RadarTrackManager();
            using var measurements =
                new NativeList<Measurement>(4, Allocator.Temp);
            var initialPosition = new double3(0.0, 0.0, 2_000.0);
            var initialVelocity = new double3(0.0, 0.0, -100.0);
            var acceleration = new double3(800.0, 0.0, 0.0);

            for (var sample = 1; sample <= 20; sample++)
            {
                var time = sample * 0.2;
                var position = initialPosition +
                               initialVelocity * time +
                               acceleration * (0.5 * time * time);
                var velocity = initialVelocity + acceleration * time;
                var measurement = MeasurementAtPosition(
                    time,
                    position,
                    velocity);
                measurement.ObjectClass = TrackObjectClass.Missile;
                measurement.ClassificationConfidence = 0.9f;
                measurements.Clear();
                measurements.Add(measurement);

                manager.Process(
                    measurements,
                    double3.zero,
                    double3.zero,
                    time);
            }

            Assert.That(manager.Count, Is.EqualTo(1));
            Assert.That(
                manager.TryGetBestTrack(out var track),
                Is.True);
            Assert.That(
                track.Lifecycle,
                Is.EqualTo(TrackLifecycle.Confirmed));
        }

        [Test]
        public void SustainedQuickBoostAtFusionCadence_RemainsSingleAirVehicleTrack()
        {
            var manager = new RadarTrackManager();
            using var measurements =
                new NativeList<Measurement>(4, Allocator.Temp);
            var position = new double3(0.0, 100.0, 5_000.0);
            var velocity = new double3(150.0, 0.0, 0.0);
            var acceleration = new double3(-340.0, 0.0, 0.0);
            const double step = 0.04;

            for (var tick = 1; tick <= 80; tick++)
            {
                if (tick > 50 && tick <= 57)
                {
                    position += velocity * step +
                                acceleration * (0.5 * step * step);
                    velocity += acceleration * step;
                }
                else
                {
                    position += velocity * step;
                }

                var time = tick * step;
                var measurement = MeasurementAtPosition(
                    time,
                    position,
                    velocity);
                measurement.ObjectClass = TrackObjectClass.AirVehicle;
                measurement.ClassificationConfidence = 0.9f;
                measurement.RangeVar = 1.44f;
                measurement.AngleVar = 1e-8f;
                measurements.Clear();
                measurements.Add(measurement);

                manager.Process(
                    measurements,
                    double3.zero,
                    double3.zero,
                    time);
            }

            Assert.That(manager.Count, Is.EqualTo(1));
            Assert.That(manager.TryGetBestTrack(out var track), Is.True);
            Assert.That(track.Lifecycle, Is.EqualTo(TrackLifecycle.Confirmed));
            Assert.That(track.Position.x, Is.EqualTo(position.x).Within(5.0));
        }

        [Test]
        public void QuickBoostVelocityModelLag_DoesNotAllocateAirVehicleGhost()
        {
            var manager = new RadarTrackManager();
            using var measurements =
                new NativeList<Measurement>(4, Allocator.Temp);
            var position = new double3(0.0, 100.0, 5_000.0);
            var velocity = new double3(150.0, 0.0, 0.0);
            const double step = 0.04;

            for (var tick = 1; tick <= 80; tick++)
            {
                // The tracker can leave a boost with almost one complete QB delta-v of lag even
                // though position returns continued throughout the burn. Association must cover
                // that velocity-model error for at least the next sensor interval.
                if (tick == 51)
                {
                    velocity += new double3(-90.0, 0.0, 0.0);
                }
                position += velocity * step;

                var time = tick * step;
                var measurement = MeasurementAtPosition(
                    time,
                    position,
                    velocity);
                measurement.ObjectClass = TrackObjectClass.AirVehicle;
                measurement.ClassificationConfidence = 0.9f;
                measurement.RangeVar = 1.44f;
                measurement.AngleVar = 1e-8f;
                measurements.Clear();
                measurements.Add(measurement);

                manager.Process(
                    measurements,
                    double3.zero,
                    double3.zero,
                    time);
            }

            Assert.That(manager.Count, Is.EqualTo(1));
            Assert.That(manager.TryGetBestTrack(out var track), Is.True);
            Assert.That(track.Lifecycle, Is.EqualTo(TrackLifecycle.Confirmed));
            Assert.That(track.Position.x, Is.EqualTo(position.x).Within(5.0));
        }

        [Test]
        public void CoLocatedAircraftAndMissile_ReturnsRemainSeparateTracks()
        {
            var manager = new RadarTrackManager();
            using var measurements =
                new NativeList<Measurement>(4, Allocator.Temp);
            var aircraft = MeasurementAt(0.04);
            aircraft.ObjectClass = TrackObjectClass.AirVehicle;
            aircraft.ClassificationConfidence = 0.85f;
            var missile = MeasurementAt(0.04);
            missile.ObjectClass = TrackObjectClass.Missile;
            missile.ClassificationConfidence = 0.9f;
            measurements.Add(aircraft);
            measurements.Add(missile);

            manager.Process(measurements, double3.zero, 0.04);

            var tracks = new TrackSnapshot[4];
            Assert.That(manager.CopyTracks(tracks), Is.EqualTo(2));
            Assert.That(
                new[] { tracks[0].ObjectClass, tracks[1].ObjectClass },
                Does.Contain(TrackObjectClass.AirVehicle));
            Assert.That(
                new[] { tracks[0].ObjectClass, tracks[1].ObjectClass },
                Does.Contain(TrackObjectClass.Missile));
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
        public void ConfirmedTrackWithoutObservation_AgesOutAndReleasesItsSlot()
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

            measurements.Clear();
            for (var i = 0; i < 3; i++)
            {
                var time = 1.12 + i * 0.04;
                manager.Process(measurements, double3.zero, time);
            }

            Assert.That(manager.TryGetBestTrack(out var coastingTrack), Is.True);
            Assert.That(
                coastingTrack.Lifecycle,
                Is.EqualTo(TrackLifecycle.Coasting).Or.EqualTo(TrackLifecycle.Degraded));

            manager.Process(measurements, double3.zero, 3.20);

            Assert.That(manager.Count, Is.Zero);
            Assert.That(manager.TryGetBestTrack(out _), Is.False);
        }

        [Test]
        public void StaleTrack_DoesNotAbsorbNewSameClassReturn()
        {
            var manager = new RadarTrackManager();
            using var measurements = new NativeList<Measurement>(4, Allocator.Temp);

            for (var i = 1; i <= 3; i++)
            {
                var measurement = MeasurementAt(i * 0.04);
                measurement.ObjectClass = TrackObjectClass.Missile;
                measurement.ClassificationConfidence = 0.9f;
                measurements.Clear();
                measurements.Add(measurement);
                manager.Process(measurements, double3.zero, i * 0.04);
            }

            measurements.Clear();
            manager.Process(measurements, double3.zero, 1.20);

            var newMissile = MeasurementAtPosition(
                1.24,
                new double3(0.0, 0.0, 1_050.0),
                double3.zero);
            newMissile.ObjectClass = TrackObjectClass.Missile;
            newMissile.ClassificationConfidence = 0.9f;
            measurements.Add(newMissile);
            manager.Process(measurements, double3.zero, 1.24);

            Assert.That(manager.Count, Is.EqualTo(2));
        }

        [Test]
        public void LongSessionTimestamp_StillConfirmsAndMaintainsOneTrack()
        {
            var manager = new RadarTrackManager();
            using var measurements = new NativeList<Measurement>(4, Allocator.Temp);
            const double sessionTime = 6.0 * 60.0 * 60.0;

            for (var i = 1; i <= 3; i++)
            {
                var time = sessionTime + i * 0.04;
                measurements.Clear();
                measurements.Add(MeasurementAt(time));
                manager.Process(measurements, double3.zero, time);
            }

            Assert.That(manager.Count, Is.EqualTo(1));
            Assert.That(manager.TryGetBestTrack(out var track), Is.True);
            Assert.That(track.Lifecycle, Is.EqualTo(TrackLifecycle.Confirmed));
            Assert.That(track.HitCount, Is.EqualTo(3));
        }

        [Test]
        public void RepeatedTransientTracks_DoNotExhaustTrackCapacity()
        {
            var manager = new RadarTrackManager();
            using var measurements = new NativeList<Measurement>(4, Allocator.Temp);

            for (var cycle = 0; cycle < RadarTrackManager.MaxTracks + 8; cycle++)
            {
                var startTime = cycle * 3.2;
                for (var hit = 1; hit <= 3; hit++)
                {
                    var time = startTime + hit * 0.04;
                    var measurement = MeasurementAt(time);
                    measurement.ObjectClass = TrackObjectClass.Missile;
                    measurement.ClassificationConfidence = 0.9f;
                    measurements.Clear();
                    measurements.Add(measurement);
                    manager.Process(measurements, double3.zero, time);
                }

                measurements.Clear();
                manager.Process(
                    measurements,
                    double3.zero,
                    startTime + 3.16);
                Assert.That(manager.Count, Is.Zero);
            }
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
