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
        public float AngularResolutionFloorRadians;
        public float ClassificationDiameterRadians;
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
        private struct Candidate { public int Index; public float MarginDb; }
        private readonly Candidate[] candidates = new Candidate[ObservationCapacity];
        private int candidateCount;

        private readonly SimWorld world;
        private readonly ElectroOpticalTargetProfile[] targets =
            new ElectroOpticalTargetProfile[SimWorld.MaxEntities];
        private readonly bool[] registered =
            new bool[SimWorld.MaxEntities];
        private readonly float3[] previousDirections =
            new float3[SimWorld.MaxEntities];
        private readonly float3[] filteredLosRates =
            new float3[SimWorld.MaxEntities];
        private readonly double[] previousObservationTimes =
            new double[SimWorld.MaxEntities];
        private readonly float[] filteredLosRateVars =
            new float[SimWorld.MaxEntities];
        private readonly float[] previousAngleVars =
            new float[SimWorld.MaxEntities];
        private readonly bool[] hasPreviousObservation =
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

        public void SetTargetSignature(SimEntityId entityId, float signatureDb)
        {
            var index = entityId.Value.Index;
            if ((uint)index < registered.Length && registered[index] &&
                targets[index].EntityId.Equals(entityId) && float.IsFinite(signatureDb))
            {
                var profile = targets[index];
                profile.SignatureDb = signatureDb;
                targets[index] = profile;
            }
        }

        public void UnregisterTarget(SimEntityId entityId)
        {
            var index = entityId.Value.Index;
            if ((uint)index < registered.Length && targets[index].EntityId.Equals(entityId))
            {
                registered[index] = false;
                hasPreviousObservation[index] = false;
                previousDirections[index] = float3.zero;
                filteredLosRates[index] = float3.zero;
                filteredLosRateVars[index] = 0f;
                previousAngleVars[index] = 0f;
                previousObservationTimes[index] = 0.0;
            }
        }

        private void EstimateLosRate(
            int index,
            float3 direction,
            float angleVar,
            double timestamp,
            out bool hasRate,
            out float3 rate,
            out float rateVar)
        {
            hasRate = false;
            rate = float3.zero;
            rateVar = 0f;
            if (hasPreviousObservation[index])
            {
                double elapsed = timestamp - previousObservationTimes[index];
                if (elapsed >= 0.02 && elapsed <= 0.5)
                {
                    var rawRate = math.cross(previousDirections[index], direction) /
                                  (float)elapsed;
                    var rawVariance =
                        (angleVar + previousAngleVars[index]) /
                        (float)(elapsed * elapsed) + 1e-8f;
                    var blend = (float)(elapsed / (elapsed + 0.12));
                    rate = math.lerp(filteredLosRates[index], rawRate, blend);
                    rateVar = math.lerp(filteredLosRateVars[index], rawVariance, blend) + 1e-8f;
                    hasRate = math.all(math.isfinite(rate)) && float.IsFinite(rateVar);
                }
                else
                {
                    filteredLosRates[index] = float3.zero;
                    filteredLosRateVars[index] = 0f;
                }
            }
            previousDirections[index] = direction;
            previousAngleVars[index] = angleVar;
            previousObservationTimes[index] = timestamp;
            hasPreviousObservation[index] = true;
            if (hasRate)
            {
                filteredLosRates[index] = rate;
                filteredLosRateVars[index] = rateVar;
            }
        }

        public void Tick(
            in ElectroOpticalReceiverProfile receiver,
            double simTime)
        {
            observations.Clear();
            candidateCount = 0;
            if (!receiver.SensorId.IsValid ||
                !world.IsAlive(receiver.OwnerEntityId)) return;
            var receiverState = world.ReadSnapshot[receiver.OwnerEntityId.Value.Index];
            var forward = math.normalizesafe(receiver.Forward, new float3(0f, 0f, 1f));
            var minimumDot = math.cos(receiver.FieldOfViewRadians * 0.5f);
            int observationLimit = math.clamp(
                receiver.TrackCapacity > 0 ? receiver.TrackCapacity : ObservationCapacity,
                1, ObservationCapacity);
            for (var i = 0; i < registered.Length; i++)
            {
                if (!registered[i] || !world.IsAlive(targets[i].EntityId) ||
                    !world.GetEntityIdAt(i).Equals(targets[i].EntityId)) continue;
                var relative = world.ReadSnapshot[i].PositionWorld - receiverState.PositionWorld;
                double range = math.length(relative);
                if (range < 1.0 || range > receiver.MaximumRangeMeters) continue;
                var trueDirection = (float3)(relative / range);
                if (math.dot(forward, trueDirection) < minimumDot) continue;
                float marginDb = targets[i].SignatureDb - 20f * (float)math.log10(range) -
                                 receiver.SensitivityFloorDb;
                if (marginDb > 0) QueueCandidate(i, marginDb, observationLimit);
            }

            for (var c = 0; c < candidateCount; c++)
            {
                int i = candidates[c].Index;
                float marginDb = candidates[c].MarginDb;
                var targetState = world.ReadSnapshot[i];
                var relative = targetState.PositionWorld - receiverState.PositionWorld;
                double range = math.length(relative);
                var trueDirection = (float3)(relative / range);
                var target = targets[i];
                float apparentDiameter = target.ReferenceDiameterMeters / (float)range;
                float probability = math.saturate(marginDb / 8f);
                var seed = SimHash.Chain(SessionSeed, receiver.SensorId.Packed, (uint)i,
                    (uint)math.round(simTime * 25.0));
                if (probability < 1f && !RadarMath.RollDetection(probability, seed)) continue;
                var sigma = math.max(receiver.AngularResolutionFloorRadians,
                    receiver.BearingSigmaRadians / math.sqrt(math.max(RadarMath.LinearFromDb(marginDb), 1f)));
                var rng = new SimRng(SimHash.Combine(seed, 0xE071CA1u));
                BuildPerpendicularBasis(trueDirection, out var tangentA, out var tangentB);
                var noisyDirection = math.normalizesafe(
                    trueDirection + tangentA * (rng.NextGaussian() * sigma) +
                    tangentB * (rng.NextGaussian() * sigma), trueDirection);
                float diameterQuality = receiver.ClassificationDiameterRadians > 0
                    ? math.saturate(apparentDiameter / receiver.ClassificationDiameterRadians)
                    : 1f;
                EstimateLosRate(i, noisyDirection, sigma * sigma, simTime,
                    out var hasLosRate, out var losRate, out var losRateVar);
                observations.Add(new ElectroOpticalObservation
                {
                    SensorId = receiver.SensorId,
                    Timestamp = simTime,
                    Direction = noisyDirection,
                    AngleVar = sigma * sigma,
                    HasLosRate = hasLosRate,
                    LosRate = losRate,
                    LosRateVar = losRateVar,
                    ApparentDiameterRadians = apparentDiameter,
                    SignalMarginDb = marginDb,
                    ObjectClass = target.ObjectClass,
                    ClassificationConfidence = target.ClassificationSignature *
                        math.saturate(receiver.ClassificationScale > 0f ? receiver.ClassificationScale : 1f) *
                        math.saturate(marginDb / 12f) * diameterQuality,
                });
            }
        }

        private void QueueCandidate(int index, float marginDb, int limit)
        {
            int insert = candidateCount;
            if (insert >= limit)
            {
                if (marginDb <= candidates[limit - 1].MarginDb) return;
                insert = limit - 1;
            }
            else candidateCount++;
            while (insert > 0 && candidates[insert - 1].MarginDb < marginDb)
            {
                if (insert < limit) candidates[insert] = candidates[insert - 1];
                insert--;
            }
            candidates[insert] = new Candidate { Index = index, MarginDb = marginDb };
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
