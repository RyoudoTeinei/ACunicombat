using System;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.Kinematics;
using ACunicombat.Simulation.World;
using Unity.Collections;
using Unity.Mathematics;

namespace ACunicombat.Sensors
{
    /// <summary>
    /// Produces sensor measurements from world truth. Measurements never contain a truth entity id.
    /// The current implementation is deterministic and allocation-free after construction; its loops
    /// are intentionally kept on the main thread until the vertical slice has sufficient profiling data.
    /// </summary>
    public sealed class RadarSensorSystem : IDisposable
    {
        public const int DetectionRingCapacity = 4096;
        public const int CandidateCapacity = 2048;
        // This is a scene-level compute guard, not one aircraft's antenna-time
        // budget. Missile seekers are independent sensors and must not starve
        // the carrier's search radar merely because they share this simulation.
        // The design baseline (8 platforms x sensors plus seekers) already
        // exceeds the old value of 16.
        public const int DwellBudgetPerTick = 64;

        private readonly SimWorld world;
        private readonly SpatialHashGrid grid;
        private readonly DwellScheduler scheduler;
        private NativeList<Measurement> detectionRing;
        private NativeList<EmissionEvent> emissionRing;
        private NativeArray<Measurement> dwellScratch;
        private NativeList<Dwell> dueDwells;
        private NativeList<JamContribution> jamInbox;
        private NativeList<SyntheticDetection> syntheticInbox;
        private NativeList<int> candidateScratch;

        private readonly RadarSensorProfile[] sensorProfiles = new RadarSensorProfile[64];
        private readonly bool[] sensorRegistered = new bool[64];
        private readonly RadarTargetProfile[] targetProfiles = new RadarTargetProfile[SimWorld.MaxEntities];
        private readonly bool[] targetRegistered = new bool[SimWorld.MaxEntities];

        public uint SessionSeed;

        public RadarSensorSystem(SimWorld world, SpatialHashGrid grid, DwellScheduler scheduler)
        {
            this.world = world;
            this.grid = grid;
            this.scheduler = scheduler;
            detectionRing = new NativeList<Measurement>(DetectionRingCapacity, Allocator.Persistent);
            emissionRing = new NativeList<EmissionEvent>(1024, Allocator.Persistent);
            dwellScratch = new NativeArray<Measurement>(256, Allocator.Persistent);
            dueDwells = new NativeList<Dwell>(64, Allocator.Persistent);
            jamInbox = new NativeList<JamContribution>(256, Allocator.Persistent);
            syntheticInbox = new NativeList<SyntheticDetection>(256, Allocator.Persistent);
            candidateScratch = new NativeList<int>(CandidateCapacity, Allocator.Persistent);
        }

        public NativeList<JamContribution> JamInboxForEw => jamInbox;
        public NativeList<SyntheticDetection> SyntheticInboxForEw => syntheticInbox;
        public NativeList<Measurement> Detections => detectionRing;
        public NativeList<EmissionEvent> Emissions => emissionRing;

        public void RegisterSensor(in RadarSensorProfile profile)
        {
            var index = profile.SensorId.Value.Index;
            if (!profile.SensorId.IsValid || (uint)index >= sensorProfiles.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(profile), "SensorId is invalid or exceeds radar capacity.");
            }

            sensorProfiles[index] = profile;
            sensorRegistered[index] = true;
        }

        public void UnregisterSensor(SensorId sensorId)
        {
            var index = sensorId.Value.Index;
            if ((uint)index < sensorRegistered.Length &&
                sensorProfiles[index].SensorId.Equals(sensorId))
            {
                sensorRegistered[index] = false;
            }
        }

        public void RegisterTarget(in RadarTargetProfile profile)
        {
            var index = profile.EntityId.Value.Index;
            if (!profile.EntityId.IsValid || (uint)index >= targetProfiles.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(profile), "Target entity is invalid or exceeds radar capacity.");
            }

            targetProfiles[index] = profile;
            targetRegistered[index] = true;
        }

        public void UnregisterTarget(SimEntityId entityId)
        {
            var index = entityId.Value.Index;
            if ((uint)index < targetRegistered.Length)
            {
                targetRegistered[index] = false;
            }
        }

        public void ScheduleDwell(in Dwell dwell) => scheduler.Schedule(dwell);

        public void Tick(double simTime)
        {
            detectionRing.Clear();
            emissionRing.Clear();
            world.SwapSnapshots(simTime);
            grid.Rebuild(world.ReadSnapshot, world.AliveMask, world.EntityCapacityInUse);

            dueDwells.Clear();
            scheduler.ConsumeDue(simTime, DwellBudgetPerTick, dueDwells);
            for (var i = 0; i < dueDwells.Length; i++)
            {
                ProcessDwell(dueDwells[i]);
            }

            jamInbox.Clear();
            syntheticInbox.Clear();
        }

        private void ProcessDwell(in Dwell dwell)
        {
            var sensorIndex = dwell.SensorId.Value.Index;
            if ((uint)sensorIndex >= sensorProfiles.Length || !sensorRegistered[sensorIndex])
            {
                return;
            }

            var profile = sensorProfiles[sensorIndex];
            if (!profile.SensorId.Equals(dwell.SensorId)
                || !TryGetSnapshot(profile.OwnerEntityId, out var sensorState))
            {
                return;
            }

            var beamDirection = math.normalizesafe(dwell.BeamDirection, new float3(0f, 0f, 1f));
            var beamWidth = dwell.BeamWidthRadians > 0f
                ? dwell.BeamWidthRadians
                : profile.DwellConfig.BeamWidthRadians;
            var measurementConfig = profile.DwellConfig;
            measurementConfig.BeamWidthRadians = beamWidth;
            emissionRing.Add(new EmissionEvent
            {
                EmitterId = profile.EmitterId,
                StartTime = world.SnapshotTime,
                EndTime = world.SnapshotTime + Math.Max(0.0, dwell.DwellTimeSeconds),
                Origin = sensorState.PositionWorld,
                Direction = beamDirection,
                BeamWidth = beamWidth,
                MaxRangeMeters = profile.MaxRangeMeters,
                FrequencyBand = profile.FrequencyBand,
                WaveformId = profile.WaveformId,
                EmissionType = profile.EmissionType,
                MainlobePowerDb = profile.MainlobePowerDb,
                SidelobeLevelDb = profile.SidelobeLevelDb,
            });

            var jamPowerDb = AggregateJamPower(profile.SensorId, profile.FrequencyBand);
            var halfBeamCos = math.cos(beamWidth * 0.5f);
            var count = 0;
            candidateScratch.Clear();
            grid.QueryBeamBroadphase(
                sensorState.PositionWorld, beamDirection, profile.MaxRangeMeters,
                beamWidth * 0.5f, candidateScratch);
            for (var i = 0; i < candidateScratch.Length && count < dwellScratch.Length; i++)
            {
                var targetIndex = candidateScratch[i];
                if ((uint)targetIndex >= targetProfiles.Length || !targetRegistered[targetIndex])
                {
                    continue;
                }

                var targetProfile = targetProfiles[targetIndex];
                var currentEntityId = world.GetEntityIdAt(targetIndex);
                if (!currentEntityId.Equals(targetProfile.EntityId)
                    || currentEntityId.Equals(profile.OwnerEntityId)
                    || !TryGetSnapshot(currentEntityId, out var targetState))
                {
                    continue;
                }

                var relativePosition = targetState.PositionWorld - sensorState.PositionWorld;
                var range = math.length(relativePosition);
                if (range < 1.0 || range > profile.MaxRangeMeters)
                {
                    continue;
                }

                var trueDirection = (float3)(relativePosition / range);
                if (math.dot(beamDirection, trueDirection) < halfBeamCos)
                {
                    continue;
                }

                var rcsDbsm = 10f * math.log10(math.max(targetProfile.RadarCrossSectionSquareMeters, 1e-6f));
                var receivedPowerDb = RadarMath.ReceivedPowerDb(profile.DwellConfig, rcsDbsm, range);
                var snrDb = RadarMath.SnrDb(
                    receivedPowerDb,
                    profile.DwellConfig.NoisePowerDb,
                    profile.ClutterPowerDb,
                    jamPowerDb);
                var probability = RadarMath.DetectionProbability(profile.DwellConfig, snrDb);
                var seed = SimHash.Chain(
                    SessionSeed,
                    profile.SensorId.Packed,
                    (uint)dwell.Sequence,
                    currentEntityId.Packed);
                if (!RadarMath.RollDetection(probability, seed))
                {
                    continue;
                }

                var rng = new SimRng(SimHash.Combine(seed, 0xA17E39B1u));
                var snrLinear = RadarMath.LinearFromDb(snrDb);
                var sigmaRange = RadarMath.SigmaRange(
                    measurementConfig,
                    snrLinear);
                var sigmaAngle = RadarMath.SigmaAngle(
                    measurementConfig,
                    snrLinear);
                var sigmaVelocity = RadarMath.SigmaVelocity(
                    measurementConfig,
                    snrLinear);
                var measurementPrecisionScale = profile.MeasurementPrecisionScale > 0f
                    ? profile.MeasurementPrecisionScale
                    : 1f;
                sigmaRange *= measurementPrecisionScale;
                sigmaAngle *= measurementPrecisionScale;
                sigmaVelocity *= measurementPrecisionScale;
                BuildPerpendicularBasis(trueDirection, out var tangentA, out var tangentB);
                var noisyDirection = math.normalizesafe(
                    trueDirection
                    + tangentA * (rng.NextGaussian() * sigmaAngle)
                    + tangentB * (rng.NextGaussian() * sigmaAngle),
                    trueDirection);
                var relativeVelocity = targetState.VelocityWorld - sensorState.VelocityWorld;
                var radialVelocity = (float)math.dot(relativeVelocity, (double3)trueDirection);
                var losRate = (float3)(math.cross(relativePosition, relativeVelocity) / (range * range));
                // 抽样序固定为 距离→径向速度→LOS率×2：新增抽样只能追加在末尾，
                // 否则同一 seed 下先前字段的噪声值全部改变（确定性回放约束）。
                var rangeNoise = rng.NextGaussian() * sigmaRange;
                var velocityNoise = rng.NextGaussian() * sigmaVelocity;
                // LOS 率噪声与 LosRateVar 同源：σ = σθ/驻留时间。不加噪的真值 LOS 率
                // 会让 PN 制导拿到完美视线率，终端精度远超噪声模型应有水平。
                var losRateSigma = sigmaAngle / math.max(dwell.DwellTimeSeconds, 1e-2f);
                var noisyLosRate = losRate
                    + tangentA * (rng.NextGaussian() * losRateSigma)
                    + tangentB * (rng.NextGaussian() * losRateSigma);

                dwellScratch[count++] = new Measurement
                {
                    SensorId = profile.SensorId,
                    Timestamp = world.SnapshotTime,
                    Range = (profile.Capability & SensorCapability.Range) != 0
                        ? (float)range + rangeNoise
                        : float.NaN,
                    Direction = noisyDirection,
                    RadialVelocity = (profile.Capability & SensorCapability.Doppler) != 0
                        ? radialVelocity + velocityNoise
                        : float.NaN,
                    LosRate = (profile.Capability & SensorCapability.LosRate) != 0
                        ? noisyLosRate
                        : new float3(float.NaN),
                    RangeVar = sigmaRange * sigmaRange,
                    AngleVar = sigmaAngle * sigmaAngle,
                    VelocityVar = sigmaVelocity * sigmaVelocity,
                     LosRateVar = sigmaAngle * sigmaAngle
                         / math.max(dwell.DwellTimeSeconds * dwell.DwellTimeSeconds, 1e-4f),
                     SnrDb = snrDb,
                     ObjectClass = targetProfile.ObjectClass,
                     ClassificationConfidence =
                         targetProfile.ClassificationSignature *
                         math.saturate((snrDb - 2f) / 18f),
                     Flags = jamPowerDb > profile.DwellConfig.NoisePowerDb
                         ? MeasurementFlags.Jammed
                         : MeasurementFlags.None,
                };
            }

            for (var i = 0; i < syntheticInbox.Length && count < dwellScratch.Length; i++)
            {
                var synthetic = syntheticInbox[i];
                if (synthetic.VictimSensorId.Equals(profile.SensorId) && synthetic.DwellSequence == dwell.Sequence)
                {
                    dwellScratch[count++] = synthetic.Measurement;
                }
            }

            var mergedCount = ResolutionMerge.MergeInPlace(
                dwellScratch,
                count,
                profile.Capability,
                profile.Resolutions);
            var writable = math.min(mergedCount, DetectionRingCapacity - detectionRing.Length);
            for (var i = 0; i < writable; i++)
            {
                detectionRing.Add(dwellScratch[i]);
            }
        }

        private bool TryGetSnapshot(SimEntityId entityId, out KinematicState state)
        {
            if (!world.IsAlive(entityId))
            {
                state = default;
                return false;
            }

            state = world.ReadSnapshot[entityId.Value.Index];
            return true;
        }

        private float AggregateJamPower(SensorId sensorId, ushort frequencyBand)
        {
            var linear = 0f;
            for (var i = 0; i < jamInbox.Length; i++)
            {
                var jam = jamInbox[i];
                if (jam.VictimSensorId.Equals(sensorId) && jam.FrequencyBand == frequencyBand)
                {
                    linear += RadarMath.LinearFromDb(jam.PowerDb);
                }
            }

            return linear > 0f ? RadarMath.DbFromLinear(linear) : -300f;
        }

        private static void BuildPerpendicularBasis(float3 direction, out float3 tangentA, out float3 tangentB)
        {
            var reference = math.abs(direction.y) < 0.9f
                ? new float3(0f, 1f, 0f)
                : new float3(1f, 0f, 0f);
            tangentA = math.normalizesafe(math.cross(direction, reference), new float3(1f, 0f, 0f));
            tangentB = math.normalizesafe(math.cross(direction, tangentA), new float3(0f, 1f, 0f));
        }

        public void Dispose()
        {
            detectionRing.Dispose();
            emissionRing.Dispose();
            dwellScratch.Dispose();
            dueDwells.Dispose();
            jamInbox.Dispose();
            syntheticInbox.Dispose();
            candidateScratch.Dispose();
        }
    }
}
