using ACunicombat.Sensing.Contracts;
using ACunicombat.Sensors;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.Kinematics;
using ACunicombat.Simulation.World;
using NUnit.Framework;
using Unity.Mathematics;

namespace ACunicombat.Tests.EditMode
{
    public class LaserRangefinderSystemTests
    {
        [Test]
        public void TargetInsideBeam_ProducesRangeAndLaserEmission()
        {
            using var world = new SimWorld();
            using var sensor = new LaserRangefinderSystem(world)
            {
                SessionSeed = 42u,
            };
            var owner = world.Register(State(double3.zero));
            var target = world.Register(
                State(new double3(4.0, 0.0, 2_000.0)));
            var sensorId = new SensorId(new Handle(2, 1));

            sensor.RegisterSensor(Profile(sensorId, owner));
            sensor.RegisterTarget(new LaserRangeTargetProfile
            {
                EntityId = target,
                ReflectivityDb = 0f,
                ObjectClass = TrackObjectClass.Missile,
                ClassificationSignature = 0.9f,
            });
            world.SwapSnapshots(0.0);
            Assert.That(sensor.SchedulePulse(new LaserPulse
            {
                SensorId = sensorId,
                DueTime = 1.0,
                Direction = math.normalize(new float3(4f, 0f, 2_000f)),
                Sequence = 7,
            }), Is.True);

            sensor.Tick(1.0);

            Assert.That(sensor.Emissions.Length, Is.EqualTo(1));
            Assert.That(
                sensor.Emissions[0].EmissionType,
                Is.EqualTo(EmissionType.Laser));
            Assert.That(sensor.Observations.Length, Is.EqualTo(1));
            var observation = sensor.Observations[0];
            Assert.That(observation.SensorId, Is.EqualTo(sensorId));
            Assert.That(observation.Timestamp, Is.EqualTo(1.0));
            Assert.That(
                observation.RangeMeters,
                Is.EqualTo(2_000f).Within(5f));
            Assert.That(observation.RangeVar, Is.GreaterThan(0f));
            Assert.That(
                observation.ObjectClass,
                Is.EqualTo(TrackObjectClass.Missile));
        }

        [Test]
        public void TargetOutsideBeam_EmitsPulseWithoutPublishingReturn()
        {
            using var world = new SimWorld();
            using var sensor = new LaserRangefinderSystem(world);
            var owner = world.Register(State(double3.zero));
            var target = world.Register(
                State(new double3(200.0, 0.0, 2_000.0)));
            var sensorId = new SensorId(new Handle(2, 1));

            sensor.RegisterSensor(Profile(sensorId, owner));
            sensor.RegisterTarget(new LaserRangeTargetProfile
            {
                EntityId = target,
                ReflectivityDb = 0f,
                ObjectClass = TrackObjectClass.Missile,
                ClassificationSignature = 1f,
            });
            world.SwapSnapshots(0.0);
            sensor.SchedulePulse(new LaserPulse
            {
                SensorId = sensorId,
                DueTime = 1.0,
                Direction = new float3(0f, 0f, 1f),
                Sequence = 8,
            });

            sensor.Tick(1.0);

            Assert.That(sensor.Emissions.Length, Is.EqualTo(1));
            Assert.That(sensor.Observations.Length, Is.Zero);
        }

        private static LaserRangefinderProfile Profile(
            SensorId sensorId,
            SimEntityId owner) =>
            new LaserRangefinderProfile
            {
                SensorId = sensorId,
                EmitterId = new EmitterId(new Handle(96, 1)),
                OwnerEntityId = owner,
                MaximumRangeMeters = 12_000.0,
                BeamWidthRadians = math.radians(0.4f),
                PulseDurationSeconds = 0.00001f,
                MainlobePowerDb = 120f,
                SidelobeLevelDb = -160f,
                ReceiveSensitivityDb = -50f,
                BaseRangeSigmaMeters = 0.75f,
                RangeErrorPartsPerMillion = 20f,
                BearingSigmaRadians = math.radians(0.03f),
                FrequencyBand = 3,
                WaveformId = 20,
            };

        private static KinematicState State(double3 position) =>
            new KinematicState
            {
                PositionWorld = position,
                VelocityWorld = double3.zero,
                Rotation = quaternion.identity,
                AngularVelocity = float3.zero,
                Mass = 1.0,
            };
    }
}
