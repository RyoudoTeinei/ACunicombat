using ACunicombat.Sensing.Contracts;
using ACunicombat.Sensors;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.Kinematics;
using ACunicombat.Simulation.World;
using ACunicombat.Tracking;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace ACunicombat.Tests.EditMode
{
    public class RadarRangeReliabilityTests
    {
        private static readonly double[] DesignedRangesMeters =
        {
            5_000.0,
            20_000.0,
            50_000.0,
            80_000.0,
            100_000.0,
        };

        [TestCaseSource(nameof(DesignedRangesMeters))]
        public void ProductionMainRadar_CenteredDwellDetectsTargetAcrossEnvelope(
            double rangeMeters)
        {
            using var world = new SimWorld();
            using var grid = new SpatialHashGrid(SimWorld.MaxEntities * 2);
            using var scheduler = new DwellScheduler(32);
            using var radar = new RadarSensorSystem(world, grid, scheduler)
            {
                SessionSeed = 1234u,
            };

            var sensorEntity =
                world.Register(State(double3.zero, double3.zero));
            var targetEntity = world.Register(State(
                new double3(0.0, 0.0, rangeMeters),
                new double3(150.0, 0.0, 0.0)));
            var sensorId = new SensorId(new Handle(0, 1));
            radar.RegisterSensor(MainRadarProfile(sensorId, sensorEntity));
            radar.RegisterTarget(new RadarTargetProfile
            {
                EntityId = targetEntity,
                RadarCrossSectionSquareMeters = 18f,
            });
            radar.ScheduleDwell(new Dwell
            {
                SensorId = sensorId,
                DueTime = 0.04,
                DwellTimeSeconds = 0.008f,
                BeamDirection = new float3(0f, 0f, 1f),
                BeamWidthRadians = math.radians(12f),
                Priority = DwellPriority.WideSearch,
                Sequence = 1,
            });

            radar.Tick(0.04);

            Assert.That(radar.Detections.Length, Is.EqualTo(1));
            Assert.That(
                radar.Detections[0].Range,
                Is.EqualTo((float)rangeMeters).Within(
                    math.max(5f, (float)rangeMeters * 0.01f)));
            Assert.That(radar.Detections[0].SnrDb, Is.GreaterThan(30f));
        }

        [Test]
        public void ProductionMainRadar_DetectionProbabilityIsNotTheFiveKilometerLimit()
        {
            var profile = MainRadarProfile(
                new SensorId(new Handle(0, 1)),
                new SimEntityId(new Handle(0, 1)));
            var rcsDbsm = 10f * math.log10(18f);

            foreach (var rangeMeters in DesignedRangesMeters)
            {
                var received = RadarMath.ReceivedPowerDb(
                    profile.DwellConfig,
                    rcsDbsm,
                    rangeMeters);
                var snr = RadarMath.SnrDb(
                    received,
                    profile.DwellConfig.NoisePowerDb,
                    profile.ClutterPowerDb,
                    -300f);
                var probability = RadarMath.DetectionProbability(
                    profile.DwellConfig,
                    snr);

                Assert.That(
                    probability,
                    Is.EqualTo(1f).Within(1e-6f),
                    $"Unexpected RF-limited detection at {rangeMeters / 1000.0:0} km.");
            }
        }

        [Test]
        public void HighSnrDopplerPrecision_DoesNotBecomeEffectivelyInfinite()
        {
            var profile = MainRadarProfile(
                new SensorId(new Handle(0, 1)),
                new SimEntityId(new Handle(0, 1)));

            var sigma = RadarMath.SigmaVelocity(
                profile.DwellConfig,
                RadarMath.LinearFromDb(90f));

            Assert.That(sigma, Is.EqualTo(0.2f).Within(1e-6f));
        }

        [TestCase(5_000.0)]
        [TestCase(50_000.0)]
        public void MovingPlatforms_CenteredRevisitsMaintainOneConfirmedTrack(
            double initialRangeMeters)
        {
            using var world = new SimWorld();
            using var grid = new SpatialHashGrid(SimWorld.MaxEntities * 2);
            using var scheduler = new DwellScheduler(512);
            using var radar = new RadarSensorSystem(world, grid, scheduler)
            {
                SessionSeed = 4321u,
            };
            var tracker = new RadarTrackManager();

            var sensorVelocity = new double3(0.0, 0.0, 250.0);
            var targetVelocity = new double3(180.0, 0.0, 0.0);
            var sensorEntity =
                world.Register(State(double3.zero, sensorVelocity));
            var targetEntity = world.Register(State(
                new double3(0.0, 100.0, initialRangeMeters),
                targetVelocity));
            var sensorId = new SensorId(new Handle(0, 1));
            radar.RegisterSensor(MainRadarProfile(sensorId, sensorEntity));
            radar.RegisterTarget(new RadarTargetProfile
            {
                EntityId = targetEntity,
                RadarCrossSectionSquareMeters = 18f,
            });

            for (var tick = 1; tick <= 250; tick++)
            {
                var time = tick * 0.04;
                var sensorPosition = sensorVelocity * time;
                var targetPosition =
                    new double3(0.0, 100.0, initialRangeMeters) +
                    targetVelocity * time;
                Assert.That(
                    world.TrySetState(
                        sensorEntity,
                        State(sensorPosition, sensorVelocity)),
                    Is.True);
                Assert.That(
                    world.TrySetState(
                        targetEntity,
                        State(targetPosition, targetVelocity)),
                    Is.True);

                var relative = targetPosition - sensorPosition;
                radar.ScheduleDwell(new Dwell
                {
                    SensorId = sensorId,
                    DueTime = time,
                    DwellTimeSeconds = 0.014f,
                    BeamDirection = (float3)math.normalize(relative),
                    BeamWidthRadians = math.radians(3f),
                    Priority = DwellPriority.TrackWhileScan,
                    Sequence = (ulong)tick,
                });
                radar.Tick(time);
                tracker.Process(
                    radar.Detections,
                    sensorPosition,
                    sensorVelocity,
                    time);
            }

            Assert.That(tracker.Count, Is.EqualTo(1));
            Assert.That(tracker.TryGetBestTrack(out var track), Is.True);
            Assert.That(track.Lifecycle, Is.EqualTo(TrackLifecycle.Confirmed));
            Assert.That(
                math.distance(
                    track.Position,
                    new double3(1_800.0, 100.0, initialRangeMeters)),
                Is.LessThan(80.0));
            Assert.That(
                math.distance(track.Velocity, targetVelocity),
                Is.LessThan(30.0));
        }

        private static RadarSensorProfile MainRadarProfile(
            SensorId sensorId,
            SimEntityId owner)
        {
            return new RadarSensorProfile
            {
                SensorId = sensorId,
                EmitterId = new EmitterId(new Handle(0, 1)),
                PlatformId = new PlatformId(new Handle(0, 1)),
                OwnerEntityId = owner,
                DwellConfig = new RadarDwellConfig
                {
                    ConstantsDb = 215f,
                    IntegrationGainDb = 6f,
                    NoisePowerDb = 0f,
                    DetectThresholdLowDb = 3f,
                    DetectThresholdHighDb = 10f,
                    RangeResolutionMeters = 12f,
                    BeamWidthRadians = math.radians(12f),
                    VelocityResolutionMps = 2f,
                },
                Capability =
                    SensorCapability.Range |
                    SensorCapability.Doppler |
                    SensorCapability.LosRate,
                MaxRangeMeters = 100_000.0,
                FrequencyBand = 1,
                WaveformId = 1,
                MainlobePowerDb = 70f,
                SidelobeLevelDb = -25f,
                ClutterPowerDb = -30f,
                Resolutions = new ResolutionMerge.Resolutions
                {
                    RangeMeters = 12f,
                    AngleRadians = math.radians(1.5f),
                    VelocityMps = 2f,
                },
            };
        }

        private static KinematicState State(
            double3 position,
            double3 velocity)
        {
            return new KinematicState
            {
                PositionWorld = position,
                VelocityWorld = velocity,
                Rotation = quaternion.identity,
                AngularVelocity = float3.zero,
                Mass = 1.0,
            };
        }
    }
}
