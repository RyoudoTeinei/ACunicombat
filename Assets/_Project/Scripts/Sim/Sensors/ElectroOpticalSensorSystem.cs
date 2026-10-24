using System;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.World;
using Unity.Collections;
using Unity.Mathematics;

namespace ACunicombat.Sensors
{
    public struct ElectroOpticalReceiverProfile
    {
        public SensorId SensorId;
        public SimEntityId OwnerEntityId;
        public float3 Forward;
        public float FieldOfViewRadians;
        public double MaximumRangeMeters;
        public float SensitivityFloorDb;
        public float BearingSigmaRadians;
        public int TrackCapacity;
        public float ClassificationScale;
    }

    public struct ElectroOpticalTargetProfile
    {
        public SimEntityId EntityId;
        public float SignatureDb;
        public float ReferenceDiameterMeters;
        public TrackObjectClass ObjectClass;
        public float ClassificationSignature;
    }

    /// <summary>
    /// Monocular EO channel. Scene truth is confined to the sensor boundary;
    /// consumers receive anonymous, noisy bearing-only observations.
    /// </summary>
    public sealed class ElectroOpticalSensorSystem : IDisposable
    {
        public const int ObservationCapacity = 128;

        private readonly SimWorld world;
        private readonly ElectroOpticalTargetProfile[] targets =
            new ElectroOpticalTargetProfile[SimWorld.MaxEntities];
        private readonly bool[] registered =
            new bool[SimWorld.MaxEntities];
        private NativeList<ElectroOpticalObservation> observations;

        public uint SessionSeed;

        public ElectroOpticalSensorSystem(SimWorld world)
        {
            this.world = world;
            observations =
                new NativeList<ElectroOpticalObservation>(
                    ObservationCapacity,
                    Allocator.Persistent);
        }

        public NativeList<ElectroOpticalObservation> Observations =>
            observations;

        public void RegisterTarget(
            in ElectroOpticalTargetProfile profile)
        {
            var index = profile.EntityId.Value.Index;
            if (!profile.EntityId.IsValid ||
                (uint)index >= registered.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(profile));
            }

            targets[index] = profile;
            registered[index] = true;
        }

        public void UnregisterTarget(SimEntityId entityId)
        {
            var index = entityId.Value.Index;
            if ((uint)index < registered.Length && targets[index].EntityId.Equals(entityId))
            {
                registered[index] = false;
            }
        }

        public void Tick(
            in ElectroOpticalReceiverProfile receiver,
            double simTime)
        {
            observations.Clear();
            if (!receiver.SensorId.IsValid ||
                !world.IsAlive(receiver.OwnerEntityId))
            {
                return;
            }

            var receiverState =
                world.ReadSnapshot[
                    receiver.OwnerEntityId.Value.Index];
            var forward = math.normalizesafe(
                receiver.Forward,
                new float3(0f, 0f, 1f));
            var minimumDot =
                math.cos(receiver.FieldOfViewRadians * 0.5f);
            var observationLimit = math.clamp(
                receiver.TrackCapacity > 0 ? receiver.TrackCapacity : ObservationCapacity,
                1,
                ObservationCapacity);
            for (var i = 0;
                 i < registered.Length &&
                 observations.Length < observationLimit;
                 i++)
            {
                if (!registered[i] ||
                    !world.IsAlive(targets[i].EntityId) ||
                    !world.GetEntityIdAt(i).Equals(
                        targets[i].EntityId))
                {
                    continue;
                }

                var targetState = world.ReadSnapshot[i];
                var relative =
                    targetState.PositionWorld -
                    receiverState.PositionWorld;
                var range = math.length(relative);
                if (range < 1.0 ||
                    range > receiver.MaximumRangeMeters)
                {
                    continue;
                }

                var trueDirection =
                    (float3)(relative / range);
                if (math.dot(forward, trueDirection) <
                    minimumDot)
                {
                    continue;
                }

                var target = targets[i];
                var apparentDiameter =
                    target.ReferenceDiameterMeters /
                    (float)range;
                var signalDb =
                    target.SignatureDb -
                    20f * (float)math.log10(range);
                var marginDb =
                    signalDb - receiver.SensitivityFloorDb;
                if (marginDb <= 0f)
                {
                    continue;
                }

                var probability = math.saturate(
                    marginDb / 8f);
                var seed = SimHash.Chain(
                    SessionSeed,
                    receiver.SensorId.Packed,
                    (uint)i,
                    (uint)math.round(simTime * 25.0));
                if (probability < 1f &&
                    !RadarMath.RollDetection(
                        probability,
                        seed))
                {
                    continue;
                }

                var sigma =
                    receiver.BearingSigmaRadians /
                    math.sqrt(
                        math.max(
                            RadarMath.LinearFromDb(marginDb),
                            1f));
                var rng = new SimRng(
                    SimHash.Combine(seed, 0xE071CA1u));
                BuildPerpendicularBasis(
                    trueDirection,
                    out var tangentA,
                    out var tangentB);
                var noisyDirection = math.normalizesafe(
                    trueDirection +
                    tangentA * (rng.NextGaussian() * sigma) +
                    tangentB * (rng.NextGaussian() * sigma),
                    trueDirection);

                observations.Add(
                    new ElectroOpticalObservation
                    {
                        SensorId = receiver.SensorId,
                        Timestamp = simTime,
                        Direction = noisyDirection,
                        AngleVar = sigma * sigma,
                        ApparentDiameterRadians =
                            apparentDiameter,
                        SignalMarginDb = marginDb,
                        ObjectClass = target.ObjectClass,
                        ClassificationConfidence =
                            target.ClassificationSignature *
                            math.saturate(
                                receiver.ClassificationScale > 0f
                                    ? receiver.ClassificationScale
                                    : 1f) *
                            math.saturate(marginDb / 12f),
                    });
            }
        }

        private static void BuildPerpendicularBasis(
            float3 direction,
            out float3 tangentA,
            out float3 tangentB)
        {
            var reference = math.abs(direction.y) < 0.9f
                ? new float3(0f, 1f, 0f)
                : new float3(1f, 0f, 0f);
            tangentA = math.normalizesafe(
                math.cross(direction, reference),
                new float3(1f, 0f, 0f));
            tangentB = math.normalizesafe(
                math.cross(direction, tangentA),
                new float3(0f, 1f, 0f));
        }

        public void Dispose()
        {
            if (observations.IsCreated)
            {
                observations.Dispose();
            }
        }
    }
}
