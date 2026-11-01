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
        public void ConsecutiveBearings_PublishFilteredLosRateWithoutTruthVelocity()
        {
            using var world = new SimWorld();
            using var sensor = new ElectroOpticalSensorSystem(world) { SessionSeed = 11 };
            var receiverEntity = world.Register(State(double3.zero));
            var target = world.Register(State(new double3(0, 0, 1000)));
            sensor.RegisterTarget(new ElectroOpticalTargetProfile
            {
                EntityId = target, SignatureDb = 90, ReferenceDiameterMeters = .5f,
                ObjectClass = TrackObjectClass.Missile, ClassificationSignature = 1,
            });
            world.SwapSnapshots(0);
            var receiver = new ElectroOpticalReceiverProfile
            {
                SensorId = new SensorId(new Handle(2, 1)), OwnerEntityId = receiverEntity,
                Forward = new float3(0, 0, 1), FieldOfViewRadians = math.radians(60),
                MaximumRangeMeters = 5000, SensitivityFloorDb = -90,
                BearingSigmaRadians = 0, AngularResolutionFloorRadians = 0,
            };
            sensor.Tick(receiver, 0);
            Assert.That(sensor.Observations[0].HasLosRate, Is.False);

            Assert.That(world.TrySetState(target, State(new double3(10, 0, 1000))), Is.True);
            world.SwapSnapshots(.04);
            sensor.Tick(receiver, .04);
            var observation = sensor.Observations[0];
            Assert.That(observation.HasLosRate, Is.True);
            Assert.That(observation.LosRate.y, Is.GreaterThan(0));
            Assert.That(observation.LosRate.y, Is.LessThan(.25));
            Assert.That(observation.LosRateVar, Is.GreaterThan(0));

            sensor.Tick(receiver, 1.2);
            Assert.That(sensor.Observations[0].HasLosRate, Is.False);
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

        [Test]
        public void DynamicThermalSignatureUpdatesObservationAndStrongestTargetWinsCapacity()
        {
            using var world = new SimWorld();
            using var sensor = new ElectroOpticalSensorSystem(world) { SessionSeed = 7 };
            var receiverEntity = world.Register(State(double3.zero));
            var weak = world.Register(State(new double3(0, 0, 2000)));
            var strong = world.Register(State(new double3(100, 0, 2000)));
            sensor.RegisterTarget(new ElectroOpticalTargetProfile { EntityId = weak, SignatureDb = 80, ReferenceDiameterMeters = 10, ObjectClass = TrackObjectClass.AirVehicle, ClassificationSignature = .9f });
            sensor.RegisterTarget(new ElectroOpticalTargetProfile { EntityId = strong, SignatureDb = 90, ReferenceDiameterMeters = 10, ObjectClass = TrackObjectClass.AirVehicle, ClassificationSignature = .9f });
            world.SwapSnapshots(0);
            var receiver = new ElectroOpticalReceiverProfile
            {
                SensorId = new SensorId(new Handle(2, 1)), OwnerEntityId = receiverEntity,
                Forward = new float3(0, 0, 1), FieldOfViewRadians = math.radians(60),
                MaximumRangeMeters = 5000, SensitivityFloorDb = -90,
                BearingSigmaRadians = .001f, TrackCapacity = 1,
            };
            sensor.Tick(receiver, 1);
            Assert.AreEqual(1, sensor.Observations.Length);
            float before = sensor.Observations[0].SignalMarginDb;
            sensor.SetTargetSignature(weak, 120);
            sensor.Tick(receiver, 2);
            Assert.AreEqual(1, sensor.Observations.Length);
            Assert.Greater(sensor.Observations[0].SignalMarginDb, before);
        }

        [Test]
        public void ApparentDiameterAndResolutionAffectClassificationAndBearing()
        {
            using var world = new SimWorld();
            using var sensor = new ElectroOpticalSensorSystem(world) { SessionSeed = 9 };
            var receiverEntity = world.Register(State(double3.zero));
            var target = world.Register(State(new double3(0, 0, 2000)));
            sensor.RegisterTarget(new ElectroOpticalTargetProfile { EntityId = target, SignatureDb = 90, ReferenceDiameterMeters = .5f, ObjectClass = TrackObjectClass.AirVehicle, ClassificationSignature = 1 });
            world.SwapSnapshots(0);
            var receiver = new ElectroOpticalReceiverProfile
            {
                SensorId = new SensorId(new Handle(2, 1)), OwnerEntityId = receiverEntity,
                Forward = new float3(0, 0, 1), FieldOfViewRadians = math.radians(60),
                MaximumRangeMeters = 5000, SensitivityFloorDb = -90,
                BearingSigmaRadians = .0001f, AngularResolutionFloorRadians = .002f,
                ClassificationDiameterRadians = .01f, ClassificationScale = 1,
            };
            sensor.Tick(receiver, 1);
            float smallConfidence = sensor.Observations[0].ClassificationConfidence;
            float smallAngleVar = sensor.Observations[0].AngleVar;
            Assert.Less(smallConfidence, .1f);
            Assert.That(smallAngleVar, Is.GreaterThanOrEqualTo(.002f * .002f - 1e-9));
            sensor.RegisterTarget(new ElectroOpticalTargetProfile { EntityId = target, SignatureDb = 90, ReferenceDiameterMeters = 10, ObjectClass = TrackObjectClass.AirVehicle, ClassificationSignature = 1 });
            sensor.Tick(receiver, 2);
            Assert.Greater(sensor.Observations[0].ClassificationConfidence, smallConfidence);
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
