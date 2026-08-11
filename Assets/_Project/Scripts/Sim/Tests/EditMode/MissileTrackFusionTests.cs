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
        public void AuthenticatedFriendlyReport_MarksMatchingMissileTrackFriendly()
        {
            var track = new MissileTrackSnapshot
            {
                TrackId = new TrackId(new Handle(7, 1)),
                Position = new double3(100.0, 0.0, 1_000.0),
                Velocity = new double3(10.0, 0.0, -200.0),
                PositionUncertaintyMeters = 8f,
                SourceMask = MissileTrackSourceMask.Radar,
                LastUpdateTime = 20.0,
            };

            var matched = MissileTrackFusion.TryScoreFriendlyDataLinkAssociation(
                in track,
                new double3(99.0, 0.0, 1_020.0),
                new double3(10.0, 0.0, -200.0),
                19.9,
                20.0,
                1.0,
                120f,
                180f,
                out var score);

            Assert.That(matched, Is.True);
            MissileTrackFusion.ApplyFriendlyDataLinkIdentity(
                ref track,
                score,
                MissileMissionRole.Interceptor,
                1f);
            Assert.That(track.IsFriendly, Is.True);
            Assert.That(
                track.MissionRole,
                Is.EqualTo(MissileMissionRole.Interceptor));
            Assert.That(
                track.MissionRoleConfidence,
                Is.EqualTo(track.IdentityConfidence).Within(0.0001f));
            Assert.That(track.HasAuthenticatedMissionRole, Is.True);
            Assert.That(
                track.SourceMask & MissileTrackSourceMask.DataLink,
                Is.EqualTo(MissileTrackSourceMask.DataLink));
        }

        [Test]
        public void AuthenticatedFriendlyReport_ClearsStaleRoleWhenReportRoleIsUnknown()
        {
            var track = new MissileTrackSnapshot
            {
                MissionRole = MissileMissionRole.Interceptor,
                MissionRoleConfidence = 1f,
            };

            MissileTrackFusion.ApplyFriendlyDataLinkIdentity(
                ref track,
                0.0,
                MissileMissionRole.Unknown,
                1f);

            Assert.That(
                track.MissionRole,
                Is.EqualTo(MissileMissionRole.Unknown));
            Assert.That(track.MissionRoleConfidence, Is.Zero);
            Assert.That(track.HasAuthenticatedMissionRole, Is.False);
        }

        [Test]
        public void FriendlyReport_DoesNotAssociateAcrossPositionOrTimeGate()
        {
            var track = new MissileTrackSnapshot
            {
                Position = new double3(0.0, 0.0, 1_000.0),
                Velocity = new double3(0.0, 0.0, -100.0),
                PositionUncertaintyMeters = 5f,
                LastUpdateTime = 10.0,
            };

            Assert.That(
                MissileTrackFusion.TryScoreFriendlyDataLinkAssociation(
                    in track,
                    new double3(500.0, 0.0, 1_000.0),
                    track.Velocity,
                    10.0,
                    10.0,
                    1.0,
                    120f,
                    180f,
                    out _),
                Is.False);
            Assert.That(
                MissileTrackFusion.TryScoreFriendlyDataLinkAssociation(
                    in track,
                    track.Position,
                    track.Velocity,
                    8.0,
                    10.0,
                    1.0,
                    120f,
                    180f,
                    out _),
                Is.False);
        }

        [Test]
        public void FriendlyReport_SelectsBestTrackRegardlessOfTrackOrder()
        {
            var tracks = new[]
            {
                new MissileTrackSnapshot
                {
                    Position = new double3(35.0, 0.0, 1_000.0),
                    Velocity = new double3(20.0, 0.0, -180.0),
                    PositionUncertaintyMeters = 20f,
                    LastUpdateTime = 10.0,
                },
                new MissileTrackSnapshot
                {
                    Position = new double3(0.5, 0.0, 1_000.0),
                    Velocity = new double3(0.0, 0.0, -200.0),
                    PositionUncertaintyMeters = 20f,
                    LastUpdateTime = 10.0,
                },
            };

            Assert.That(
                MissileTrackFusion.TrySelectFriendlyDataLinkAssociation(
                    tracks,
                    tracks.Length,
                    new bool[tracks.Length],
                    new double3(0.0, 0.0, 1_000.0),
                    new double3(0.0, 0.0, -200.0),
                    10.0,
                    10.0,
                    1.0,
                    120f,
                    180f,
                    0.35,
                    out var trackIndex,
                    out _),
                Is.True);
            Assert.That(trackIndex, Is.EqualTo(1));
        }

        [Test]
        public void FriendlyReport_DoesNotAssignIdentityWhenTwoMissilesAreAmbiguous()
        {
            var tracks = new[]
            {
                new MissileTrackSnapshot
                {
                    Position = new double3(1.0, 0.0, 1_000.0),
                    Velocity = new double3(0.0, 0.0, -200.0),
                    PositionUncertaintyMeters = 20f,
                    LastUpdateTime = 10.0,
                },
                new MissileTrackSnapshot
                {
                    Position = new double3(3.0, 0.0, 1_000.0),
                    Velocity = new double3(5.0, 0.0, -200.0),
                    PositionUncertaintyMeters = 20f,
                    LastUpdateTime = 10.0,
                },
            };

            Assert.That(
                MissileTrackFusion.TrySelectFriendlyDataLinkAssociation(
                    tracks,
                    tracks.Length,
                    new bool[tracks.Length],
                    new double3(0.0, 0.0, 1_000.0),
                    new double3(0.0, 0.0, -200.0),
                    10.0,
                    10.0,
                    1.0,
                    120f,
                    180f,
                    0.35,
                    out _,
                    out _),
                Is.False);
        }

        [Test]
        public void TrackReacquisition_DoesNotReplaceAircraftWithNearbyMissile()
        {
            var previous = ReacquisitionTrack(
                3,
                TrackObjectClass.AirVehicle,
                new double3(0.0, 0.0, 1_000.0),
                0.8f);
            var candidates = new[]
            {
                ReacquisitionTrack(
                    4,
                    TrackObjectClass.Missile,
                    new double3(2.0, 0.0, 1_000.0),
                    1f),
                ReacquisitionTrack(
                    5,
                    TrackObjectClass.AirVehicle,
                    new double3(45.0, 0.0, 1_000.0),
                    0.6f),
            };

            Assert.That(
                TrackReacquisition.TrySelectCandidate(
                    in previous,
                    candidates,
                    candidates.Length,
                    10.0,
                    150f,
                    out var selected),
                Is.True);
            Assert.That(
                selected.TrackId,
                Is.EqualTo(candidates[1].TrackId));
        }

        [Test]
        public void TrackReacquisition_RejectsDifferentClassWhenNoMatchExists()
        {
            var previous = ReacquisitionTrack(
                3,
                TrackObjectClass.AirVehicle,
                new double3(0.0, 0.0, 1_000.0),
                0.8f);
            var missile = ReacquisitionTrack(
                4,
                TrackObjectClass.Missile,
                new double3(2.0, 0.0, 1_000.0),
                1f);

            Assert.That(
                TrackReacquisition.TrySelectCandidate(
                    in previous,
                    new[] { missile },
                    1,
                    10.0,
                    150f,
                    out _),
                Is.False);
        }

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
            track.Velocity = new double3(8.0, 0.0, -200.0);
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
        public void ActiveSeekerClosingTowardAnotherTarget_IsNotMarkedIncoming()
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
            Assert.That(output[0].Confirmed, Is.True);
            Assert.That(output[0].Incoming, Is.False);
            Assert.That(
                output[0].ClosestApproachDistanceMeters,
                Is.GreaterThan(
                    MissileTrackFusionConfig.Default
                        .GuidedThreatMaximumCpaMeters));
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

        private static TrackSnapshot ReacquisitionTrack(
            int index,
            TrackObjectClass objectClass,
            double3 position,
            float confidence)
        {
            return new TrackSnapshot
            {
                TrackId = new TrackId(new Handle(index, 1)),
                Position = position,
                Velocity = double3.zero,
                Confidence = confidence,
                ObjectClass = objectClass,
                Classification = 0.9f,
                LastUpdateTime = 10.0,
                LastPredictTime = 10.0,
                HitCount = 3,
                Lifecycle = TrackLifecycle.Confirmed,
                SourceMask = TrackSourceMask.Radar,
            };
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
