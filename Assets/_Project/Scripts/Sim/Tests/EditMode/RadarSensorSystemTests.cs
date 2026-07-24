using ACunicombat.Sensing.Contracts;
using ACunicombat.Sensors;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.Kinematics;
using ACunicombat.Simulation.World;
using NUnit.Framework;
using Unity.Mathematics;

namespace ACunicombat.Tests.EditMode
{
    public class RadarSensorSystemTests
    {
        [Test]
        public void RegisteredTargetInsideBeam_ProducesAnonymousMeasurement()
        {
            using var world = new SimWorld();
            using var grid = new SpatialHashGrid(SimWorld.MaxEntities * 2);
            using var scheduler = new DwellScheduler(32);
            using var radar = new RadarSensorSystem(world, grid, scheduler) { SessionSeed = 1234u };

            var sensorEntity = world.Register(State(double3.zero, double3.zero));
            var targetEntity = world.Register(State(
                new double3(0.0, 0.0, 1_000.0),
                new double3(0.0, 0.0, -10.0)));
            var sensorId = new SensorId(new Handle(0, 1));
            radar.RegisterSensor(TestSensor(sensorId, sensorEntity));
            radar.RegisterTarget(new RadarTargetProfile
            {
                EntityId = targetEntity,
                RadarCrossSectionSquareMeters = 1f,
            });
            radar.ScheduleDwell(new Dwell
            {
                SensorId = sensorId,
                DueTime = 0.04,
                DwellTimeSeconds = 0.02f,
                BeamDirection = new float3(0f, 0f, 1f),
                BeamWidthRadians = math.radians(20f),
                Priority = DwellPriority.TrackWhileScan,
                Sequence = 1,
            });

            radar.Tick(0.04);

            Assert.That(radar.Emissions.Length, Is.EqualTo(1));
            Assert.That(radar.Emissions[0].MaxRangeMeters, Is.EqualTo(5_000.0));
            Assert.That(radar.Detections.Length, Is.EqualTo(1));
            var measurement = radar.Detections[0];
            Assert.That(measurement.SensorId, Is.EqualTo(sensorId));
            Assert.That(measurement.Timestamp, Is.EqualTo(0.04).Within(1e-9));
            Assert.That(measurement.Range, Is.EqualTo(1_000f).Within(5f));
            Assert.That(measurement.Direction.z, Is.GreaterThan(0.999f));
            Assert.That(measurement.RadialVelocity, Is.EqualTo(-10f).Within(1f));
        }

        [Test]
        public void UnregisteredAuxiliarySensor_DoesNotProcessPendingDwell()
        {
            using var world = new SimWorld();
            using var grid =
                new SpatialHashGrid(SimWorld.MaxEntities * 2);
            using var scheduler = new DwellScheduler(32);
            using var radar =
                new RadarSensorSystem(world, grid, scheduler)
                {
                    SessionSeed = 1234u,
                };

            var sensorEntity =
                world.Register(State(double3.zero, double3.zero));
            var targetEntity = world.Register(State(
                new double3(0.0, 0.0, 1_000.0),
                double3.zero));
            var sensorId = new SensorId(new Handle(3, 1));
            radar.RegisterSensor(TestSensor(sensorId, sensorEntity));
            radar.RegisterTarget(new RadarTargetProfile
            {
                EntityId = targetEntity,
                RadarCrossSectionSquareMeters = 1f,
            });
            radar.ScheduleDwell(new Dwell
            {
                SensorId = sensorId,
                DueTime = 0.04,
                DwellTimeSeconds = 0.02f,
                BeamDirection = new float3(0f, 0f, 1f),
                BeamWidthRadians = math.radians(20f),
                Priority = DwellPriority.MissileTerminal,
                Sequence = 1,
            });

            radar.UnregisterSensor(sensorId);
            radar.Tick(0.04);

            Assert.That(radar.Emissions.Length, Is.Zero);
            Assert.That(radar.Detections.Length, Is.Zero);
        }

        private static RadarSensorProfile TestSensor(SensorId sensorId, SimEntityId owner)
        {
            return new RadarSensorProfile
            {
                SensorId = sensorId,
                EmitterId = new EmitterId(new Handle(0, 1)),
                PlatformId = new PlatformId(new Handle(0, 1)),
                OwnerEntityId = owner,
                DwellConfig = new RadarDwellConfig
                {
                    ConstantsDb = 220f,
                    IntegrationGainDb = 0f,
                    NoisePowerDb = 0f,
                    DetectThresholdLowDb = -10f,
                    DetectThresholdHighDb = -5f,
                    RangeResolutionMeters = 10f,
                    BeamWidthRadians = math.radians(20f),
                    VelocityResolutionMps = 1f,
                },
                Capability = SensorCapability.Range | SensorCapability.Doppler | SensorCapability.LosRate,
                MaxRangeMeters = 5_000.0,
                FrequencyBand = 1,
                WaveformId = 1,
                MainlobePowerDb = 70f,
                SidelobeLevelDb = -25f,
                ClutterPowerDb = -300f,
                Resolutions = new ResolutionMerge.Resolutions
                {
                    RangeMeters = 10f,
                    AngleRadians = math.radians(1f),
                    VelocityMps = 1f,
                },
            };
        }

        private static KinematicState State(double3 position, double3 velocity)
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
