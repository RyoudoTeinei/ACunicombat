using ACunicombat.FireControl;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using NUnit.Framework;
using Unity.Mathematics;

namespace ACunicombat.Tests.EditMode
{
    public class MissileIntentEstimatorTests
    {
        [Test]
        public void PersistentOwnshipGeometry_ResolvesIntentWithoutInferringRole()
        {
            var state = new MissileIntentEstimatorState();
            var tracks = new[]
            {
                HostileMissile(
                    4,
                    new double3(0.0, 0.0, 10_000.0),
                    new double3(0.0, 0.0, -1_000.0)),
                FriendlyMissile(
                    5,
                    new double3(5_000.0, 0.0, 7_000.0),
                    new double3(0.0, 0.0, 800.0)),
            };

            EvaluateSamples(state, tracks, 4);

            Assert.That(
                tracks[0].TargetIntent,
                Is.EqualTo(MissileTargetIntent.Ownship));
            Assert.That(tracks[0].IsLikelyTargetingOwnship, Is.True);
            Assert.That(
                tracks[0].MissionRole,
                Is.EqualTo(MissileMissionRole.Unknown));
            Assert.That(tracks[0].IsLikelyAirToAir, Is.False);
        }

        [Test]
        public void OwnshipIntent_DoesNotResolveBeforeEvidenceWindow()
        {
            var state = new MissileIntentEstimatorState();
            var tracks = new[]
            {
                HostileMissile(
                    4,
                    new double3(0.0, 0.0, 10_000.0),
                    new double3(0.0, 0.0, -1_000.0)),
                FriendlyMissile(
                    5,
                    new double3(5_000.0, 0.0, 7_000.0),
                    new double3(0.0, 0.0, 800.0)),
            };

            EvaluateSamples(state, tracks, 3);

            Assert.That(
                tracks[0].TargetIntent,
                Is.EqualTo(MissileTargetIntent.Unknown));
        }

        [Test]
        public void PersistentFriendlyMissileGeometry_ResolvesIntentWithoutInferringRole()
        {
            var state = new MissileIntentEstimatorState();
            var targetPosition = new double3(-2_000.0, 0.0, 6_000.0);
            var hostilePosition = new double3(5_000.0, 0.0, 9_000.0);
            var targetVelocity = new double3(0.0, 0.0, 600.0);
            var leadPoint = MissileEngagementMath.PredictLeadPoint(
                ToFloat3(hostilePosition),
                ToFloat3(targetPosition),
                ToFloat3(targetVelocity),
                1_200f,
                12f);
            var hostileVelocity = math.normalize(
                (double3)leadPoint - hostilePosition) * 1_200.0;
            var tracks = new[]
            {
                HostileMissile(4, hostilePosition, hostileVelocity),
                FriendlyMissile(
                    5,
                    targetPosition,
                    targetVelocity),
            };

            EvaluateSamples(state, tracks, 4);

            Assert.That(
                tracks[0].TargetIntent,
                Is.EqualTo(MissileTargetIntent.FriendlyMissile),
                $"confidence={tracks[0].TargetIntentConfidence:0.000}");
            Assert.That(tracks[0].IsLikelyInterceptor, Is.False);
            Assert.That(
                tracks[0].SuspectedTargetTrackId,
                Is.EqualTo(tracks[1].TrackId));
            Assert.That(
                tracks[0].MissionRole,
                Is.EqualTo(MissileMissionRole.Unknown));
        }

        [Test]
        public void AuthenticatedDataLinkCandidate_ResolvesInterceptorWithoutFriendlyRadarTrack()
        {
            var state = new MissileIntentEstimatorState();
            var targetPosition = new double3(-2_000.0, 0.0, 6_000.0);
            var hostilePosition = new double3(5_000.0, 0.0, 9_000.0);
            var targetVelocity = new double3(0.0, 0.0, 600.0);
            var leadPoint = MissileEngagementMath.PredictLeadPoint(
                ToFloat3(hostilePosition),
                ToFloat3(targetPosition),
                ToFloat3(targetVelocity),
                1_200f,
                12f);
            var tracks = new[]
            {
                HostileMissile(
                    4,
                    hostilePosition,
                    math.normalize((double3)leadPoint - hostilePosition) *
                    1_200.0),
            };
            var candidates = new FriendlyMissileIntentCandidate[1];

            for (var i = 0; i < 4; i++)
            {
                var now = 10.0 + i * 0.24;
                tracks[0].KinematicUpdateTime = now;
                tracks[0].LastEvidenceTime = now;
                tracks[0].LastUpdateTime = now;
                candidates[0] = new FriendlyMissileIntentCandidate
                {
                    TrackId = new TrackId(new Handle(5, 1)),
                    Position = targetPosition,
                    Velocity = targetVelocity,
                    LastEvidenceTime = now,
                };

                MissileIntentEstimator.Evaluate(
                    MissileIntentEstimatorConfig.Default,
                    state,
                    tracks,
                    tracks.Length,
                    candidates,
                    candidates.Length,
                    double3.zero,
                    double3.zero,
                    now);
            }

            Assert.That(
                tracks[0].TargetIntent,
                Is.EqualTo(MissileTargetIntent.FriendlyMissile),
                $"confidence={tracks[0].TargetIntentConfidence:0.000}");
            Assert.That(tracks[0].IsLikelyInterceptor, Is.False);
            Assert.That(
                tracks[0].SuspectedTargetTrackId,
                Is.EqualTo(candidates[0].TrackId));
        }

        [Test]
        public void ReciprocalMissileCrossing_DoesNotResolveInterceptorFromCpaAlone()
        {
            var state = new MissileIntentEstimatorState();
            var tracks = new[]
            {
                HostileMissile(
                    4,
                    new double3(0.0, 0.0, 1_000.0),
                    new double3(0.0, 0.0, -1_400.0)),
            };
            var candidates = new[]
            {
                new FriendlyMissileIntentCandidate
                {
                    TrackId = new TrackId(new Handle(5, 1)),
                    Position = new double3(40.0, 0.0, 750.0),
                    Velocity = new double3(0.0, 0.0, 1_400.0),
                    LastEvidenceTime = 10.0,
                },
            };

            for (var i = 0; i < 4; i++)
            {
                var now = 10.0 + i * 0.24;
                tracks[0].KinematicUpdateTime = now;
                tracks[0].LastEvidenceTime = now;
                tracks[0].LastUpdateTime = now;
                candidates[0].LastEvidenceTime = now;
                MissileIntentEstimator.Evaluate(
                    MissileIntentEstimatorConfig.Default,
                    state,
                    tracks,
                    tracks.Length,
                    candidates,
                    candidates.Length,
                    new double3(0.0, 0.0, -4_000.0),
                    double3.zero,
                    now);
            }

            Assert.That(
                tracks[0].TargetIntent,
                Is.Not.EqualTo(MissileTargetIntent.FriendlyMissile));
            Assert.That(tracks[0].IsLikelyInterceptor, Is.False);
        }

        [Test]
        public void DataLinkCandidateForSameTrack_DoesNotRelabelFriendlyIdentityDropout()
        {
            var state = new MissileIntentEstimatorState();
            var track = HostileMissile(
                4,
                new double3(5_000.0, 0.0, 9_000.0),
                new double3(-900.0, 0.0, -300.0));
            var tracks = new[] { track };
            var candidates = new[]
            {
                new FriendlyMissileIntentCandidate
                {
                    TrackId = track.TrackId,
                    Position = track.Position,
                    Velocity = track.Velocity,
                    LastEvidenceTime = 10.0,
                },
            };

            for (var i = 0; i < 8; i++)
            {
                var now = 10.0 + i * 0.24;
                tracks[0].KinematicUpdateTime = now;
                tracks[0].LastEvidenceTime = now;
                tracks[0].LastUpdateTime = now;
                candidates[0].LastEvidenceTime = now;
                MissileIntentEstimator.Evaluate(
                    MissileIntentEstimatorConfig.Default,
                    state,
                    tracks,
                    tracks.Length,
                    candidates,
                    candidates.Length,
                    new double3(-2_000.0, 0.0, 6_000.0),
                    new double3(0.0, 0.0, 600.0),
                    now);
            }

            Assert.That(
                tracks[0].TargetIntent,
                Is.Not.EqualTo(MissileTargetIntent.FriendlyMissile));
        }

        [Test]
        public void CollinearOwnshipAndFriendlyMissile_RemainsUnknown()
        {
            var state = new MissileIntentEstimatorState();
            var tracks = new[]
            {
                HostileMissile(
                    4,
                    new double3(0.0, 0.0, 10_000.0),
                    new double3(0.0, 0.0, -1_000.0)),
                FriendlyMissile(
                    5,
                    new double3(0.0, 0.0, 5_000.0),
                    new double3(0.0, 0.0, 800.0)),
            };

            EvaluateSamples(state, tracks, 8);

            Assert.That(
                tracks[0].TargetIntent,
                Is.EqualTo(MissileTargetIntent.Unknown));
            Assert.That(tracks[0].MissionRoleConfidence, Is.Zero);
        }

        [Test]
        public void RepeatedFrameWithoutNewKinematics_DoesNotMatureIntent()
        {
            var state = new MissileIntentEstimatorState();
            var tracks = new[]
            {
                HostileMissile(
                    4,
                    new double3(0.0, 0.0, 10_000.0),
                    new double3(0.0, 0.0, -1_000.0)),
            };
            tracks[0].KinematicUpdateTime = 10.0;

            for (var i = 0; i < 20; i++)
            {
                var now = 10.0 + i * 0.05;
                tracks[0].LastEvidenceTime = now;
                MissileIntentEstimator.Evaluate(
                    MissileIntentEstimatorConfig.Default,
                    state,
                    tracks,
                    tracks.Length,
                    double3.zero,
                    double3.zero,
                    now);
            }

            Assert.That(
                tracks[0].TargetIntent,
                Is.EqualTo(MissileTargetIntent.Unknown));
        }

        [Test]
        public void SingleContradictorySample_DoesNotSwitchResolvedIntent()
        {
            var state = new MissileIntentEstimatorState();
            var tracks = new[]
            {
                HostileMissile(
                    4,
                    new double3(0.0, 0.0, 10_000.0),
                    new double3(0.0, 0.0, -1_000.0)),
                FriendlyMissile(
                    5,
                    new double3(5_000.0, 0.0, 7_000.0),
                    new double3(0.0, 0.0, 800.0)),
            };
            EvaluateSamples(state, tracks, 4);
            Assert.That(
                tracks[0].TargetIntent,
                Is.EqualTo(MissileTargetIntent.Ownship),
                "ownship intent precondition");

            var now = 11.0;
            var offset = tracks[1].Position - tracks[0].Position;
            tracks[0].Velocity = math.normalize(offset) * 1_200.0;
            tracks[0].KinematicUpdateTime = now;
            tracks[0].LastEvidenceTime = now;
            tracks[1].KinematicUpdateTime = now;
            tracks[1].LastEvidenceTime = now;
            tracks[1].LastUpdateTime = now;
            MissileIntentEstimator.Evaluate(
                MissileIntentEstimatorConfig.Default,
                state,
                tracks,
                tracks.Length,
                double3.zero,
                double3.zero,
                now);

            Assert.That(
                tracks[0].TargetIntent,
                Is.EqualTo(MissileTargetIntent.Ownship),
                $"single contrary sample confidence={tracks[0].TargetIntentConfidence:0.000}");
        }

        [Test]
        public void SustainedContradictoryEvidence_SwitchesResolvedIntent()
        {
            var state = new MissileIntentEstimatorState();
            var tracks = new[]
            {
                HostileMissile(
                    4,
                    new double3(0.0, 0.0, 10_000.0),
                    new double3(0.0, 0.0, -1_000.0)),
                FriendlyMissile(
                    5,
                    new double3(5_000.0, 0.0, 7_000.0),
                    new double3(0.0, 0.0, 800.0)),
            };
            EvaluateSamples(state, tracks, 4);

            var targetVelocity = new double3(0.0, 0.0, 600.0);
            tracks[1].Position = new double3(-2_000.0, 0.0, 6_000.0);
            tracks[1].Velocity = targetVelocity;
            tracks[0].Position = new double3(5_000.0, 0.0, 9_000.0);
            var leadPoint = MissileEngagementMath.PredictLeadPoint(
                ToFloat3(tracks[0].Position),
                ToFloat3(tracks[1].Position),
                ToFloat3(targetVelocity),
                1_200f,
                12f);
            tracks[0].Velocity = math.normalize(
                (double3)leadPoint - tracks[0].Position) * 1_200.0;

            for (var i = 0; i < 8; i++)
            {
                var now = 11.0 + i * 0.24;
                for (var trackIndex = 0;
                     trackIndex < tracks.Length;
                     trackIndex++)
                {
                    tracks[trackIndex].KinematicUpdateTime = now;
                    tracks[trackIndex].LastEvidenceTime = now;
                    tracks[trackIndex].LastUpdateTime = now;
                }
                MissileIntentEstimator.Evaluate(
                    MissileIntentEstimatorConfig.Default,
                    state,
                    tracks,
                    tracks.Length,
                    double3.zero,
                    double3.zero,
                    now);
            }

            Assert.That(
                tracks[0].TargetIntent,
                Is.EqualTo(MissileTargetIntent.FriendlyMissile));
            Assert.That(tracks[0].IsLikelyInterceptor, Is.False);
            Assert.That(
                tracks[0].MissionRole,
                Is.EqualTo(MissileMissionRole.Unknown));
        }

        [Test]
        public void StrongFreshMainlobeEvidence_AcceleratesOwnshipIntent()
        {
            var state = new MissileIntentEstimatorState();
            var tracks = new[]
            {
                HostileMissile(
                    4,
                    new double3(0.0, 0.0, 10_000.0),
                    new double3(0.0, 0.0, -1_000.0)),
            };

            for (var i = 0; i < 3; i++)
            {
                var now = 10.0 + i * 0.08;
                tracks[0].KinematicUpdateTime = now;
                tracks[0].LastEvidenceTime = now;
                tracks[0].LastUpdateTime = now;
                tracks[0].SeekerOwnshipEvidence = 0.85f;
                tracks[0].LastSeekerMainlobeTime = now;
                MissileIntentEstimator.Evaluate(
                    MissileIntentEstimatorConfig.Default,
                    state,
                    tracks,
                    tracks.Length,
                    double3.zero,
                    double3.zero,
                    now);
            }

            Assert.That(
                tracks[0].TargetIntent,
                Is.EqualTo(MissileTargetIntent.Ownship));
            Assert.That(tracks[0].IsLikelyTargetingOwnship, Is.True);
        }

        [Test]
        public void SidelobeDiscovery_DoesNotAccelerateOwnshipIntent()
        {
            var state = new MissileIntentEstimatorState();
            var tracks = new[]
            {
                HostileMissile(
                    4,
                    new double3(0.0, 0.0, 10_000.0),
                    new double3(0.0, 0.0, -1_000.0)),
            };
            tracks[0].SourceMask |= MissileTrackSourceMask.Rwr;

            for (var i = 0; i < 3; i++)
            {
                var now = 10.0 + i * 0.08;
                tracks[0].KinematicUpdateTime = now;
                tracks[0].LastEvidenceTime = now;
                tracks[0].LastUpdateTime = now;
                MissileIntentEstimator.Evaluate(
                    MissileIntentEstimatorConfig.Default,
                    state,
                    tracks,
                    tracks.Length,
                    double3.zero,
                    double3.zero,
                    now);
            }

            Assert.That(
                tracks[0].TargetIntent,
                Is.EqualTo(MissileTargetIntent.Unknown));
        }

        [Test]
        public void EsmAirToAirRole_DoesNotImplyOwnshipIntent()
        {
            var track = HostileMissile(
                5,
                new double3(0.0, 0.0, 5_000.0),
                new double3(0.0, 0.0, 800.0));
            track.MissionRole = MissileMissionRole.AirToAir;
            track.MissionRoleConfidence = 0.9f;
            track.MissionRoleEvidence =
                MissileMissionRoleEvidence.EsmWaveform;
            var tracks = new[] { track };

            EvaluateSamples(
                new MissileIntentEstimatorState(),
                tracks,
                8);

            Assert.That(tracks[0].IsLikelyAirToAir, Is.True);
            Assert.That(tracks[0].IsLikelyTargetingOwnship, Is.False);
            Assert.That(
                tracks[0].TargetIntent,
                Is.EqualTo(MissileTargetIntent.Unknown));
        }

        [Test]
        public void FriendlyAuthenticatedRole_IsNotOverwrittenByIntentEstimator()
        {
            var track = FriendlyMissile(
                5,
                new double3(0.0, 0.0, 5_000.0),
                new double3(0.0, 0.0, 800.0));
            track.MissionRole = MissileMissionRole.Interceptor;
            track.MissionRoleConfidence = 1f;
            var tracks = new[] { track };

            MissileIntentEstimator.Evaluate(
                MissileIntentEstimatorConfig.Default,
                new MissileIntentEstimatorState(),
                tracks,
                tracks.Length,
                double3.zero,
                double3.zero,
                10.0);

            Assert.That(
                tracks[0].MissionRole,
                Is.EqualTo(MissileMissionRole.Interceptor));
            Assert.That(tracks[0].MissionRoleConfidence, Is.EqualTo(1f));
        }

        [Test]
        public void EsmIdentifiedRole_IsNotOverwrittenByWeakIntent()
        {
            var track = HostileMissile(
                5,
                new double3(0.0, 0.0, 5_000.0),
                new double3(0.0, 0.0, -800.0));
            track.MissionRole = MissileMissionRole.Interceptor;
            track.MissionRoleConfidence = 0.8f;
            track.MissionRoleEvidence =
                MissileMissionRoleEvidence.EsmWaveform;
            var tracks = new[] { track };

            MissileIntentEstimator.Evaluate(
                MissileIntentEstimatorConfig.Default,
                new MissileIntentEstimatorState(),
                tracks,
                tracks.Length,
                double3.zero,
                double3.zero,
                10.0);

            Assert.That(
                tracks[0].MissionRole,
                Is.EqualTo(MissileMissionRole.Interceptor));
            Assert.That(
                tracks[0].MissionRoleConfidence,
                Is.EqualTo(0.8f).Within(0.0001f));
        }

        private static void EvaluateSamples(
            MissileIntentEstimatorState state,
            MissileTrackSnapshot[] tracks,
            int sampleCount)
        {
            for (var i = 0; i < sampleCount; i++)
            {
                var now = 10.0 + i * 0.24;
                for (var trackIndex = 0;
                     trackIndex < tracks.Length;
                     trackIndex++)
                {
                    tracks[trackIndex].KinematicUpdateTime = now;
                    tracks[trackIndex].LastEvidenceTime = now;
                    tracks[trackIndex].LastUpdateTime = now;
                }

                MissileIntentEstimator.Evaluate(
                    MissileIntentEstimatorConfig.Default,
                    state,
                    tracks,
                    tracks.Length,
                    double3.zero,
                    double3.zero,
                    now);
            }
        }

        private static MissileTrackSnapshot HostileMissile(
            int index,
            double3 position,
            double3 velocity)
        {
            return new MissileTrackSnapshot
            {
                TrackId = new TrackId(new Handle(index, 1)),
                Position = position,
                Velocity = velocity,
                PositionUncertaintyMeters = 20f,
                Confidence = 0.9f,
                SourceMask = MissileTrackSourceMask.Radar,
                Quality = MissileTrackQuality.WeaponsQuality,
                Confirmed = true,
                LastEvidenceTime = 10.0,
                KinematicUpdateTime = 10.0,
                LastUpdateTime = 10.0,
            };
        }

        private static MissileTrackSnapshot FriendlyMissile(
            int index,
            double3 position,
            double3 velocity)
        {
            var track = HostileMissile(index, position, velocity);
            track.Affiliation = TrackAffiliation.Friendly;
            track.IdentityConfidence = 1f;
            track.SourceMask |= MissileTrackSourceMask.DataLink;
            return track;
        }

        private static float3 ToFloat3(double3 value) =>
            new float3(
                (float)value.x,
                (float)value.y,
                (float)value.z);
    }
}
