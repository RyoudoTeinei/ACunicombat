using ACunicombat.Sensing.Contracts;
using ACunicombat.Sensors;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.Kinematics;
using ACunicombat.Simulation.World;
using NUnit.Framework;
using Unity.Mathematics;

namespace ACunicombat.Tests.EditMode
{
    public class ElectroOpticalSensorSystemTests
    {
        [Test]
        public void MissileInsideFieldOfView_ProducesAnonymousBearing()
        {
            using var world = new SimWorld();
            using var sensor =
                new ElectroOpticalSensorSystem(world)
                {
                    SessionSeed = 42u,
                };

            var receiverEntity = world.Register(
                State(double3.zero));
            var missileEntity = world.Register(
                State(new double3(50.0, 20.0, 2_000.0)));
            sensor.RegisterTarget(
                new ElectroOpticalTargetProfile
                {
                    EntityId = missileEntity,
                    SignatureDb = 90f,
                    ReferenceDiameterMeters = 0.5f,
                    ObjectClass = TrackObjectClass.Missile,
                    ClassificationSignature = 0.9f,
                });
            world.SwapSnapshots(0.0);

            sensor.Tick(
                new ElectroOpticalReceiverProfile
                {
                    SensorId =
                        new SensorId(new Handle(2, 1)),
                    OwnerEntityId = receiverEntity,
                    Forward = new float3(0f, 0f, 1f),
                    FieldOfViewRadians = math.radians(60f),
                    MaximumRangeMeters = 5_000.0,
                    SensitivityFloorDb = -90f,
                    BearingSigmaRadians = math.radians(0.2f),
                },
                1.0);

            Assert.That(sensor.Observations.Length, Is.EqualTo(1));
            var observation = sensor.Observations[0];
            Assert.That(
                observation.ObjectClass,
                Is.EqualTo(TrackObjectClass.Missile));
            Assert.That(
                observation.ClassificationConfidence,
                Is.GreaterThan(0.5f));
            Assert.That(
                math.length(observation.Direction),
                Is.EqualTo(1f).Within(1e-4f));
            Assert.That(observation.AngleVar, Is.GreaterThan(0f));
            Assert.That(
                math.dot(
                    observation.Direction,
                    math.normalize(new float3(50f, 20f, 2_000f))),
                Is.GreaterThan(0.999f));
        }

        [Test]
        public void MissileOutsideFieldOfView_IsNotPublished()
        {
            using var world = new SimWorld();
            using var sensor =
                new ElectroOpticalSensorSystem(world);

            var receiverEntity = world.Register(
                State(double3.zero));
            var missileEntity = world.Register(
                State(new double3(2_000.0, 0.0, 0.0)));
            sensor.RegisterTarget(
                new ElectroOpticalTargetProfile
                {
                    EntityId = missileEntity,
                    SignatureDb = 90f,
                    ReferenceDiameterMeters = 0.5f,
                    ObjectClass = TrackObjectClass.Missile,
                    ClassificationSignature = 1f,
                });
            world.SwapSnapshots(0.0);

            sensor.Tick(
                new ElectroOpticalReceiverProfile
                {
                    SensorId =
                        new SensorId(new Handle(2, 1)),
                    OwnerEntityId = receiverEntity,
                    Forward = new float3(0f, 0f, 1f),
                    FieldOfViewRadians = math.radians(30f),
                    MaximumRangeMeters = 5_000.0,
                    SensitivityFloorDb = -90f,
                    BearingSigmaRadians = math.radians(0.2f),
                },
                1.0);

            Assert.That(sensor.Observations.Length, Is.Zero);
        }

        private static KinematicState State(double3 position)
        {
            return new KinematicState
            {
                PositionWorld = position,
                VelocityWorld = double3.zero,
                Rotation = quaternion.identity,
                AngularVelocity = float3.zero,
                Mass = 1.0,
            };
        }
    }
}
