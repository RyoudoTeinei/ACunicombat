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
        private sealed class DiagnosticBuffer<T> : System.Collections.Generic.IReadOnlyList<T> where T:struct
        {
            private readonly T[] items=new T[512];private int first;
            public int Count {get;private set;}
            public T this[int index] {get {if((uint)index>=Count)throw new ArgumentOutOfRangeException(nameof(index));return items[(first+index)%items.Length];}}
            public void Add(T value)
            {
                if(Count<items.Length){items[(first+Count)%items.Length]=value;Count++;}
                else {items[first]=value;first=(first+1)%items.Length;}
            }
            public System.Collections.Generic.IEnumerator<T> GetEnumerator(){for(int i=0;i<Count;i++)yield return this[i];}
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
        }

        public enum DwellResult { Executed, AntennaBudget, Expired, InvalidRequest, QueueFull, Displaced, Unavailable }
        public struct DwellDiagnostic
        {
            public SensorId Sensor; public ulong Sequence; public DwellPriority Priority;
            public DwellResult Result; public double Time, RequestedTime; public float Duration, CreditSeconds;
        }
        private readonly DiagnosticBuffer<DwellDiagnostic> diagnostics=new DiagnosticBuffer<DwellDiagnostic>();
        public System.Collections.Generic.IReadOnlyList<DwellDiagnostic> Diagnostics => diagnostics;
        public struct SignalDiagnostic
        {
            public SensorId Sensor;public ulong Sequence;
            public float ReceiverInputPowerDbw,NoisePowerDbw,InterferencePowerDbw,IntegrationGainDb,SnrDb,DetectionProbability;
            public bool Detected;
        }
        private readonly DiagnosticBuffer<SignalDiagnostic> signalDiagnostics=new DiagnosticBuffer<SignalDiagnostic>();
        public System.Collections.Generic.IReadOnlyList<SignalDiagnostic> SignalDiagnostics => signalDiagnostics;
        public long ExecutedPhysicalDwells { get; private set; }
        public double TotalPhysicalDwellSeconds { get; private set; }
        public long BudgetDeferredCount { get; private set; }
        public long ExpiredDwellCount { get; private set; }
        public long RejectedDwellCount { get; private set; }
        private readonly double[] budgetTimes=new double[64];
        private readonly float[] budgetCredits=new float[64];
        private readonly bool[] budgetBlocked=new bool[64];
        private const int PendingPerPhysicalSensor=16;
        private const float MaximumPhysicalDwellSeconds=.1f;
        private const double MaximumQueueAgeSeconds=.5;
        private void Diagnose(in Dwell dwell,DwellResult result)
        {
            int index=dwell.SensorId.Value.Index;
            diagnostics.Add(new DwellDiagnostic{Sensor=dwell.SensorId,Sequence=dwell.Sequence,Priority=dwell.Priority,
                Result=result,Time=world.SnapshotTime,RequestedTime=dwell.DueTime,Duration=dwell.DwellTimeSeconds,
                CreditSeconds=(uint)index<budgetCredits.Length?budgetCredits[index]:0});
        }
        private readonly bool[] sensorRegistered = new bool[64];
        private readonly float[] sensorEfficiency = new float[64];
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

            sensorEfficiency[index] = 1f;
            budgetCredits[index]=0;budgetTimes[index]=world.SnapshotTime;
            sensorProfiles[index] = profile;
            sensorRegistered[index] = true;
        }

        public bool SetSensorEfficiency(SensorId sensorId, float efficiency)
        {
            int index=sensorId.Value.Index;
            if((uint)index>=sensorProfiles.Length||!sensorRegistered[index]||!sensorProfiles[index].SensorId.Equals(sensorId))return false;
            sensorEfficiency[index]=float.IsFinite(efficiency)?math.clamp(efficiency,0f,1f):0;
            return true;
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
            if ((uint)index < targetRegistered.Length && targetProfiles[index].EntityId.Equals(entityId))
            {
                targetRegistered[index] = false;
            }
        }

        public void ScheduleDwell(in Dwell dwell)
        {
            int index=dwell.SensorId.Value.Index;
            if((uint)index>=sensorProfiles.Length||!sensorRegistered[index]||!sensorProfiles[index].SensorId.Equals(dwell.SensorId))
            {RejectedDwellCount++;Diagnose(dwell,DwellResult.Unavailable);return;}
            if(!sensorProfiles[index].Physical.IsValid){scheduler.Schedule(dwell);return;}
            if(!float.IsFinite(dwell.DwellTimeSeconds)||dwell.DwellTimeSeconds<=0||dwell.DwellTimeSeconds>MaximumPhysicalDwellSeconds||
                !double.IsFinite(dwell.DueTime)||!math.all(math.isfinite(dwell.BeamDirection))||math.lengthsq(dwell.BeamDirection)<1e-8f)
            {RejectedDwellCount++;Diagnose(dwell,DwellResult.InvalidRequest);return;}
            if(!scheduler.TryScheduleBounded(dwell,PendingPerPhysicalSensor,out bool displaced))
            {RejectedDwellCount++;Diagnose(dwell,DwellResult.QueueFull);}
            else if(displaced){RejectedDwellCount++;Diagnose(dwell,DwellResult.Displaced);}
        }

        public void Tick(double simTime)
        {
            detectionRing.Clear();
            emissionRing.Clear();
            world.SwapSnapshots(simTime);
            grid.Rebuild(world.ReadSnapshot, world.AliveMask, world.EntityCapacityInUse);

            for(int index=0;index<sensorProfiles.Length;index++)
            {
                budgetBlocked[index]=false;
                if(!sensorRegistered[index]||!sensorProfiles[index].Physical.IsValid)continue;
                float duty=sensorProfiles[index].Physical.AntennaDutyCycle;
                if(duty<=0)duty=1f;
                budgetCredits[index]=math.min(MaximumPhysicalDwellSeconds,budgetCredits[index]+(float)math.max(0,simTime-budgetTimes[index])*math.min(1f,duty));
                budgetTimes[index]=simTime;
            }
            dueDwells.Clear();
            scheduler.ConsumeDue(simTime, DwellBudgetPerTick, dueDwells);
            for (var i = 0; i < dueDwells.Length; i++)
            {
                var dwell=dueDwells[i];int index=dwell.SensorId.Value.Index;
                if((uint)index>=sensorProfiles.Length||!sensorRegistered[index]||!sensorProfiles[index].SensorId.Equals(dwell.SensorId))
                {Diagnose(dwell,DwellResult.Unavailable);continue;}
                if(sensorProfiles[index].Physical.IsValid)
                {
                    if(simTime-dwell.DueTime>MaximumQueueAgeSeconds){ExpiredDwellCount++;Diagnose(dwell,DwellResult.Expired);continue;}
                    if(sensorEfficiency[index]<=0){Diagnose(dwell,DwellResult.Unavailable);continue;}
                    if(budgetBlocked[index]||budgetCredits[index]+1e-7f<dwell.DwellTimeSeconds)
                    {
                        budgetBlocked[index]=true;BudgetDeferredCount++;Diagnose(dwell,DwellResult.AntennaBudget);
                        // Requests removed from the scheduler above leave room for their own deferral.
                        scheduler.Schedule(dwell);continue;
                    }
                    budgetCredits[index]=math.max(0,budgetCredits[index]-dwell.DwellTimeSeconds);
                    ExecutedPhysicalDwells++;TotalPhysicalDwellSeconds+=dwell.DwellTimeSeconds;
                    Diagnose(dwell,DwellResult.Executed);
                }
                ProcessDwell(dwell);
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

            float efficiency=sensorEfficiency[sensorIndex];if(efficiency<=0)return;
            var profile = sensorProfiles[sensorIndex];
            profile.MaxRangeMeters *= profile.Physical.IsValid ? math.pow(efficiency,.25f) : math.sqrt(efficiency);
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
            if(profile.Physical.IsValid)
            {
                measurementConfig=profile.Physical.BuildDwell(dwell.DwellTimeSeconds,beamWidth,efficiency);
                profile.DwellConfig=measurementConfig;
                beamWidth=measurementConfig.BeamWidthRadians;
            }
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
                MainlobePowerDb = profile.Physical.IsValid
                    ? RadarMath.DbFromLinear(profile.Physical.AverageTransmitPowerWatts * efficiency * profile.Physical.GainLinear *
                        math.pow(profile.Physical.BeamWidthRadians / measurementConfig.BeamWidthRadians,2f))
                    : profile.MainlobePowerDb,
                SidelobeLevelDb = profile.SidelobeLevelDb,
            });

            var jamPowerDb = AggregateJamPower(profile,beamDirection,measurementConfig);
            float clutterDb=profile.ClutterPowerDb;
            if(profile.Physical.IsValid&&!profile.ClutterPowerIsDbw)clutterDb+=measurementConfig.NoisePowerDb;
            var halfBeamCos = math.cos(beamWidth * 0.5f);
            var count = 0;
            candidateScratch.Clear();
            if(profile.Physical.IsValid)
            {
                // Bounded by registered scene entities, not empty space or an arbitrary radar range.
                for(int index=0;index<world.EntityCapacityInUse;index++)
                    if(targetRegistered[index] && world.AliveMask[index]) candidateScratch.Add(index);
            }
            else grid.QueryBeamBroadphase(
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
                if (range < 1.0 || (!profile.Physical.IsValid && range > profile.MaxRangeMeters))
                {
                    continue;
                }

                var trueDirection = (float3)(relativePosition / range);
                if (!profile.Physical.IsValid && math.dot(beamDirection, trueDirection) < halfBeamCos)
                {
                    continue;
                }

                var rcsDbsm = 10f * math.log10(math.max(targetProfile.RadarCrossSectionSquareMeters, 1e-6f));
                var receivedPowerDb = RadarMath.ReceivedPowerDb(profile.DwellConfig, rcsDbsm, range);
                if(profile.Physical.IsValid)
                    receivedPowerDb+=2f*RadarMath.AntennaResponseDb(math.dot(beamDirection,trueDirection),measurementConfig.BeamWidthRadians,
                        profile.SidelobeLevelDb<0?profile.SidelobeLevelDb:-25f);
                var snrDb = RadarMath.SnrDb(
                    receivedPowerDb,
                    profile.DwellConfig.NoisePowerDb,
                    clutterDb,
                    jamPowerDb);
                var probability = RadarMath.DetectionProbability(profile.DwellConfig, snrDb);
                var seed = SimHash.Chain(
                    SessionSeed,
                    profile.SensorId.Packed,
                    (uint)dwell.Sequence,
                    currentEntityId.Packed);
                bool detected=RadarMath.RollDetection(probability,seed);
                if(profile.Physical.IsValid)
                {
                    signalDiagnostics.Add(new SignalDiagnostic{Sensor=profile.SensorId,Sequence=dwell.Sequence,
                        ReceiverInputPowerDbw=receivedPowerDb-measurementConfig.IntegrationGainDb,NoisePowerDbw=measurementConfig.NoisePowerDb,
                        InterferencePowerDbw=jamPowerDb,IntegrationGainDb=measurementConfig.IntegrationGainDb,SnrDb=snrDb,
                        DetectionProbability=probability,Detected=detected});
                }
                if (!detected)
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
                // A sidelobe return does not provide mainlobe-quality angular localization.
                if(profile.Physical.IsValid && math.dot(beamDirection,trueDirection)<halfBeamCos)
                    sigmaAngle=math.max(sigmaAngle,measurementConfig.BeamWidthRadians);
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
                var resolved=dwellScratch[i];resolved.DwellSequence=dwell.Sequence;detectionRing.Add(resolved);
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

        private float AggregateJamPower(in RadarSensorProfile profile, float3 beamDirection, in RadarDwellConfig config)
        {
            var linear = 0f;
            for (var i = 0; i < jamInbox.Length; i++)
            {
                var jam = jamInbox[i];
                if (!jam.VictimSensorId.Equals(profile.SensorId) || !float.IsFinite(jam.PowerDb))continue;
                float contribution=jam.PowerDb;
                if(profile.Physical.IsValid)
                {
                    float overlap;
                    if(jam.CenterFrequencyHz>0 && jam.BandwidthHz>0)
                        overlap=RadarMath.SpectralOverlap(profile.Physical.FrequencyHz,profile.Physical.SignalBandwidthHz,jam.CenterFrequencyHz,jam.BandwidthHz);
                    else overlap=jam.FrequencyBand==profile.FrequencyBand?1f:0f;
                    if(overlap<=0)continue;
                    if(!jam.PowerIsDbw)contribution+=config.NoisePowerDb;
                    contribution+=10f*math.log10(overlap);
                    if(math.lengthsq(jam.ArrivalDirection)>1e-8f)
                        contribution+=RadarMath.AntennaResponseDb(math.dot(beamDirection,math.normalize(jam.ArrivalDirection)),config.BeamWidthRadians,
                            profile.SidelobeLevelDb<0?profile.SidelobeLevelDb:-25f);
                }
                else if(jam.FrequencyBand!=profile.FrequencyBand || jam.PowerIsDbw)continue;
                linear += RadarMath.LinearFromDb(contribution);
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
