using ACunicombat.FireControl;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using NUnit.Framework;
using Unity.Mathematics;

namespace ACunicombat.Tests.EditMode
{
    public class MissileTrackFusionTests
    {
        [Test]
        public void RadarOnlyTrack_IsIncomingButNotFireControlConfirmed()
        {
            var output = new MissileTrackSnapshot[2];
            var count = MissileTrackFusion.Fuse(
                MissileTrackFusionConfig.Default,
                new[] { IncomingRadarTrack() },
                1,
                new ThreatSnapshot[0],
                0,
                new ElectroOpticalObservation[0],
                0,
                double3.zero,
                double3.zero,
                10.0,
                output);

            Assert.That(count, Is.EqualTo(1));
            Assert.That(output[0].Incoming, Is.True);
            Assert.That(output[0].Confirmed, Is.False);
            Assert.That(
                output[0].SourceMask,
                Is.EqualTo(MissileTrackSourceMask.Radar));
        }

        [Test]
        public void IndependentBearingEvidence_ConfirmsRadarMissileTrack()
        {
            var output = new MissileTrackSnapshot[2];
            var threats = new[]
            {
                Threat(
                    EsmWaveformClass.MissileSeeker,
                    0.8f),
                Threat(
                    EsmWaveformClass.InfraredPlume,
                    0.9f),
            };
            var optical = new[]
            {
                new ElectroOpticalObservation
                {
                    SensorId =
                        new SensorId(new Handle(2, 1)),
                    Timestamp = 10.0,
                    Direction = math.normalize(
                        new float3(2f, 0f, 1_000f)),
                    AngleVar =
                        math.pow(math.radians(0.1f), 2f),
                    ObjectClass = TrackObjectClass.Missile,
                    ClassificationConfidence = 0.9f,
                },
            };

            var count = MissileTrackFusion.Fuse(
                MissileTrackFusionConfig.Default,
                new[] { IncomingRadarTrack() },
                1,
                threats,
                threats.Length,
                optical,
                optical.Length,
                double3.zero,
                double3.zero,
                10.0,
                output);

            Assert.That(count, Is.EqualTo(1));
            Assert.That(output[0].Confirmed, Is.True);
            Assert.That(
                output[0].SourceMask,
                Is.EqualTo(
                    MissileTrackSourceMask.Radar |
                    MissileTrackSourceMask.Rwr |
                    MissileTrackSourceMask.Maw |
                    MissileTrackSourceMask.ElectroOptical));
            Assert.That(output[0].Confidence, Is.GreaterThan(0.8f));
            Assert.That(
                output[0].PositionUncertaintyMeters,
                Is.GreaterThan(0f));
        }

        [Test]
        public void AssociatedLaserReturn_RefinesRangeAndUncertainty()
        {
            var track = IncomingRadarTrack();
            track.Position = new double3(0.0, 0.0, 1_080.0);
            var laser = new[]
            {
                new LaserRangeObservation
                {
                    SensorId =
                        new SensorId(new Handle(2, 1)),
                    Timestamp = 10.0,
                    RangeMeters = 1_000f,
                    RangeVar = 1f,
                    Direction = new float3(0f, 0f, 1f),
                    AngleVar =
                        math.pow(math.radians(0.02f), 2f),
                    ObjectClass = TrackObjectClass.Missile,
                    ClassificationConfidence = 0.9f,
                },
            };
            var output = new MissileTrackSnapshot[1];

            var count = MissileTrackFusion.Fuse(
                MissileTrackFusionConfig.Default,
                new[] { track },
                1,
                new ThreatSnapshot[0],
                0,
                new ElectroOpticalObservation[0],
                0,
                laser,
                laser.Length,
                double3.zero,
                double3.zero,
                10.0,
                output);

            Assert.That(count, Is.EqualTo(1));
            Assert.That(
                output[0].Position.z,
                Is.EqualTo(1_000.0).Within(0.01));
            Assert.That(
                output[0].SourceMask &
                MissileTrackSourceMask.LaserRangefinder,
                Is.Not.EqualTo(MissileTrackSourceMask.None));
            Assert.That(
                output[0].PositionUncertaintyMeters,
                Is.LessThan(2f));
        }

        [Test]
        public void ActiveSeekerClosingAtLongRange_IsMarkedIncomingBeforeCpaWindow()
        {
            var track = IncomingRadarTrack();
            track.Position = new double3(0.0, 0.0, 12_000.0);
            track.Velocity = new double3(80.0, 0.0, -200.0);
            var output = new MissileTrackSnapshot[1];

            var count = MissileTrackFusion.Fuse(
                MissileTrackFusionConfig.Default,
                new[] { track },
                1,
                new[]
                {
                    Threat(
                        EsmWaveformClass.MissileSeeker,
                        0.9f),
                },
                1,
                new ElectroOpticalObservation[0],
                0,
                double3.zero,
                double3.zero,
                10.0,
                output);

            Assert.That(count, Is.EqualTo(1));
            Assert.That(output[0].Incoming, Is.True);
            Assert.That(output[0].Confirmed, Is.True);
            Assert.That(
                output[0].TimeToClosestApproachSeconds,
                Is.GreaterThan(30f));
        }

        [Test]
        public void ActiveSeekerMovingAway_IsConfirmedButNotIncoming()
        {
            var track = IncomingRadarTrack();
            track.Position = new double3(0.0, 0.0, 5_000.0);
            track.Velocity = new double3(0.0, 0.0, 200.0);
            var output = new MissileTrackSnapshot[1];

            MissileTrackFusion.Fuse(
                MissileTrackFusionConfig.Default,
                new[] { track },
                1,
                new[]
                {
                    Threat(
                        EsmWaveformClass.MissileSeeker,
                        0.9f),
                },
                1,
                new ElectroOpticalObservation[0],
                0,
                double3.zero,
                double3.zero,
                10.0,
                output);

            Assert.That(output[0].Confirmed, Is.True);
            Assert.That(output[0].Incoming, Is.False);
        }

        [Test]
        public void MawCorrelatedUncertainCpa_IsMarkedIncomingAtLongRange()
        {
            var track = IncomingRadarTrack();
            track.Position = new double3(1_200.0, 0.0, 10_000.0);
            track.Velocity = new double3(0.0, 0.0, -250.0);
            track.SetCovariance(3, 3, 400.0);
            var output = new MissileTrackSnapshot[1];

            MissileTrackFusion.Fuse(
                MissileTrackFusionConfig.Default,
                new[] { track },
                1,
                new[]
                {
                    Threat(
                        EsmWaveformClass.InfraredPlume,
                        0.9f),
                },
                1,
                new ElectroOpticalObservation[0],
                0,
                double3.zero,
                double3.zero,
                10.0,
                output);

            Assert.That(output[0].Confirmed, Is.True);
            Assert.That(output[0].Incoming, Is.True);
            Assert.That(
                output[0].ClosestApproachDistanceMeters,
                Is.GreaterThan(
                    MissileTrackFusionConfig.Default
                        .ProtectedRadiusMeters));
        }

        [Test]
        public void FusedPositionEpoch_DoesNotApplyRadarPredictionTwice()
        {
            var track = IncomingRadarTrack();
            track.Position = new double3(0.0, 0.0, 2_000.0);
            track.Velocity = new double3(0.0, 0.0, -200.0);
            track.LastPredictTime = 9.0;
            track.LastUpdateTime = 9.0;
            var output = new MissileTrackSnapshot[1];

            MissileTrackFusion.Fuse(
                MissileTrackFusionConfig.Default,
                new[] { track },
                1,
                new ThreatSnapshot[0],
                0,
                new ElectroOpticalObservation[0],
                0,
                double3.zero,
                double3.zero,
                10.0,
                output);

            Assert.That(output[0].Position.z, Is.EqualTo(1_800.0));
            Assert.That(output[0].LastUpdateTime, Is.EqualTo(10.0));
            Assert.That(
                output[0].PredictPosition(10.0).z,
                Is.EqualTo(1_800.0));
        }

        [Test]
        public void StatefulFusion_HoldsWeaponsQualityAcrossBriefEvidenceLoss()
        {
            var state = new MissileTrackFusionState();
            var output = new MissileTrackSnapshot[1];
            var track = IncomingRadarTrack();

            MissileTrackFusion.Fuse(
                MissileTrackFusionConfig.Default,
                state,
                new[] { track },
                1,
                new[]
                {
                    Threat(
                        EsmWaveformClass.MissileSeeker,
                        0.9f),
                },
                1,
                new ElectroOpticalObservation[0],
                0,
                double3.zero,
                double3.zero,
                10.0,
                output);
            Assert.That(output[0].IsWeaponsQuality, Is.True);

            MissileTrackFusion.Fuse(
                MissileTrackFusionConfig.Default,
                state,
                new[] { track },
                1,
                new ThreatSnapshot[0],
                0,
                new ElectroOpticalObservation[0],
                0,
                double3.zero,
                double3.zero,
                10.8,
                output);
            Assert.That(output[0].IsWeaponsQuality, Is.True);

            MissileTrackFusion.Fuse(
                MissileTrackFusionConfig.Default,
                state,
                new[] { track },
                1,
                new ThreatSnapshot[0],
                0,
                new ElectroOpticalObservation[0],
                0,
                double3.zero,
                double3.zero,
                14.2,
                output);
            Assert.That(output[0].IsWarning, Is.True);
            Assert.That(output[0].IsWeaponsQuality, Is.False);
        }

        [Test]
        public void StatefulFusion_EstimatesAccelerationFromNewVelocityEvidence()
        {
            var config = MissileTrackFusionConfig.Default;
            config.AccelerationFilterTimeSeconds = 0f;
            config.JerkFilterTimeSeconds = 0f;
            var state = new MissileTrackFusionState();
            var output = new MissileTrackSnapshot[1];
            var track = IncomingRadarTrack();

            MissileTrackFusion.Fuse(
                config,
                state,
                new[] { track },
                1,
                new ThreatSnapshot[0],
                0,
                new ElectroOpticalObservation[0],
                0,
                double3.zero,
                double3.zero,
                10.0,
                output);

            track.Velocity =
                new double3(0.0, 0.0, -125.0);
            track.LastUpdateTime = 10.1;
            track.LastPredictTime = 10.1;
            MissileTrackFusion.Fuse(
                config,
                state,
                new[] { track },
                1,
                new ThreatSnapshot[0],
                0,
                new ElectroOpticalObservation[0],
                0,
                double3.zero,
                double3.zero,
                10.1,
                output);

            Assert.That(
                output[0].Acceleration.z,
                Is.EqualTo(-250.0).Within(0.01));
            Assert.That(
                output[0].PredictPosition(10.2).z,
                Is.EqualTo(
                    output[0].Position.z -
                    12.5 -
                    1.25).Within(0.01));

            track.Velocity =
                new double3(0.0, 0.0, -155.0);
            track.LastUpdateTime = 10.2;
            track.LastPredictTime = 10.2;
            MissileTrackFusion.Fuse(
                config,
                state,
                new[] { track },
                1,
                new ThreatSnapshot[0],
                0,
                new ElectroOpticalObservation[0],
                0,
                double3.zero,
                double3.zero,
                10.2,
                output);

            Assert.That(
                output[0].Acceleration.z,
                Is.EqualTo(-300.0).Within(0.01));
            Assert.That(
                output[0].Jerk.z,
                Is.EqualTo(-500.0).Within(0.01));
            Assert.That(
                output[0].PredictVelocity(10.3).z,
                Is.EqualTo(-187.5).Within(0.01));
            Assert.That(
                output[0].PredictAcceleration(10.3).z,
                Is.EqualTo(-350.0).Within(0.01));
        }

        [Test]
        public void CorroboratedUnknownRadarTrack_IsWarningOnly()
        {
            var track = IncomingRadarTrack();
            track.ObjectClass = TrackObjectClass.Unknown;
            track.Classification = 0.1f;
            var output = new MissileTrackSnapshot[1];

            var count = MissileTrackFusion.Fuse(
                MissileTrackFusionConfig.Default,
                new[] { track },
                1,
                new[]
                {
                    Threat(
                        EsmWaveformClass.MissileSeeker,
                        0.9f),
                },
                1,
                new ElectroOpticalObservation[0],
                0,
                double3.zero,
                double3.zero,
                10.0,
                output);

            Assert.That(count, Is.EqualTo(1));
            Assert.That(output[0].IsWarning, Is.True);
            Assert.That(output[0].IsWeaponsQuality, Is.False);
            Assert.That(output[0].Incoming, Is.True);
        }

        [Test]
        public void CorroboratedAirVehicleTrack_IsNotRelabeledAsMissile()
        {
            var track = IncomingRadarTrack();
            track.ObjectClass = TrackObjectClass.AirVehicle;
            track.Classification = 0.9f;
            var output = new MissileTrackSnapshot[1];

            var count = MissileTrackFusion.Fuse(
                MissileTrackFusionConfig.Default,
                new[] { track },
                1,
                new[]
                {
                    Threat(
                        EsmWaveformClass.MissileSeeker,
                        0.9f),
                },
                1,
                new ElectroOpticalObservation[0],
                0,
                double3.zero,
                double3.zero,
                10.0,
                output);

            Assert.That(count, Is.Zero);
        }

        [Test]
        public void CorrelatedTentativeTrack_IsShownBeforeVelocityConverges()
        {
            var track = IncomingRadarTrack();
            track.Lifecycle = TrackLifecycle.Tentative;
            track.HitCount = 1;
            track.Velocity = double3.zero;
            var output = new MissileTrackSnapshot[1];

            var count = MissileTrackFusion.Fuse(
                MissileTrackFusionConfig.Default,
                new[] { track },
                1,
                new[]
                {
                    Threat(
                        EsmWaveformClass.MissileSeeker,
                        0.9f),
                },
                1,
                new ElectroOpticalObservation[0],
                0,
                double3.zero,
                double3.zero,
                10.0,
                output);

            Assert.That(count, Is.EqualTo(1));
            Assert.That(output[0].IsWarning, Is.True);
            Assert.That(output[0].IsWeaponsQuality, Is.False);
            Assert.That(output[0].Incoming, Is.True);
        }

        [Test]
        public void DuplicateRadarTracksForOneMissile_AreConsolidated()
        {
            var first = IncomingRadarTrack();
            var duplicate = IncomingRadarTrack();
            duplicate.TrackId =
                new TrackId(new Handle(5, 1));
            duplicate.Position =
                new double3(140.0, 0.0, 1_020.0);
            duplicate.Velocity =
                new double3(8.0, 0.0, -92.0);
            duplicate.Confidence = 0.72f;
            var output = new MissileTrackSnapshot[4];

            var count = MissileTrackFusion.Fuse(
                MissileTrackFusionConfig.Default,
                new[] { first, duplicate },
                2,
                new[]
                {
                    Threat(
                        EsmWaveformClass.MissileSeeker,
                        0.9f),
                },
                1,
                new ElectroOpticalObservation[0],
                0,
                double3.zero,
                double3.zero,
                10.0,
                output);

            Assert.That(count, Is.EqualTo(1));
            Assert.That(output[0].IsWeaponsQuality, Is.True);
            Assert.That(output[0].Incoming, Is.True);
        }

        private static TrackSnapshot IncomingRadarTrack()
        {
            var track = new TrackSnapshot
            {
                TrackId =
                    new TrackId(new Handle(4, 1)),
                Position = new double3(0.0, 0.0, 1_000.0),
                Velocity = new double3(0.0, 0.0, -100.0),
                Confidence = 0.85f,
                ObjectClass = TrackObjectClass.Missile,
                Classification = 0.9f,
                LastUpdateTime = 10.0,
                LastPredictTime = 10.0,
                HitCount = 4,
                Lifecycle = TrackLifecycle.Confirmed,
                SourceMask = TrackSourceMask.Radar,
            };
            track.SetCovariance(0, 0, 100.0);
            track.SetCovariance(1, 1, 100.0);
            track.SetCovariance(2, 2, 400.0);
            return track;
        }

        private static ThreatSnapshot Threat(
            EsmWaveformClass waveformClass,
            float identificationConfidence)
        {
            return new ThreatSnapshot
            {
                ThreatId =
                    new EsmThreatId(new Handle(3, 1)),
                DirectionWorld = new float3(0f, 0f, 1f),
                WaveformClass = waveformClass,
                IdentificationConfidence =
                    identificationConfidence,
                LastSeenTime = 10.0,
            };
        }
    }
}
