using System;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.World;
using Unity.Collections;
using Unity.Mathematics;

namespace ACunicombat.Sensors
{
    public struct LaserRangefinderProfile
    {
        public SensorId SensorId;
        public EmitterId EmitterId;
        public SimEntityId OwnerEntityId;
        public double MaximumRangeMeters;
        public float BeamWidthRadians;
        public float PulseDurationSeconds;
        public float MainlobePowerDb;
        public float SidelobeLevelDb;
        public float ReceiveSensitivityDb;
        public float BaseRangeSigmaMeters;
        public float RangeErrorPartsPerMillion;
        public float BearingSigmaRadians;
        public ushort FrequencyBand;
        public ushort WaveformId;
    }

    public struct LaserRangeTargetProfile
    {
        public SimEntityId EntityId;
        public float ReflectivityDb;
        public TrackObjectClass ObjectClass;
        public float ClassificationSignature;
    }

    public struct LaserPulse
    {
        public SensorId SensorId;
        public TrackId CuedTrackId;
        public double DueTime;
        public float3 Direction;
        public ulong Sequence;
    }

    /// <summary>
    /// Active narrow-beam laser rangefinder. Pulses create observable emission
    /// events and anonymous noisy range returns; no entity identity leaves this
    /// sensor boundary.
    /// </summary>
    public sealed class LaserRangefinderSystem : IDisposable
    {
        public const int MaximumSensors = 8;
        public const int ObservationCapacity = 64;
        public const int PulseCapacity = 64;

        private readonly SimWorld world;
        private readonly LaserRangefinderProfile[] sensors =
            new LaserRangefinderProfile[MaximumSensors];
        private readonly float[] efficiencies = new float[MaximumSensors];
        private readonly bool[] sensorRegistered =
            new bool[MaximumSensors];
        private readonly LaserRangeTargetProfile[] targets =
            new LaserRangeTargetProfile[SimWorld.MaxEntities];
        private readonly bool[] targetRegistered =
            new bool[SimWorld.MaxEntities];
        private NativeList<LaserPulse> pendingPulses;
        private NativeList<LaserRangeObservation> observations;
        private NativeList<EmissionEvent> emissions;

        public uint SessionSeed;

        public LaserRangefinderSystem(SimWorld world)
        {
            this.world = world;
            pendingPulses = new NativeList<LaserPulse>(
                PulseCapacity,
                Allocator.Persistent);
            observations = new NativeList<LaserRangeObservation>(
                ObservationCapacity,
                Allocator.Persistent);
            emissions = new NativeList<EmissionEvent>(
                PulseCapacity,
                Allocator.Persistent);
        }

        public NativeList<LaserRangeObservation> Observations =>
            observations;

        public NativeList<EmissionEvent> Emissions => emissions;

        public void RegisterSensor(in LaserRangefinderProfile profile)
        {
            var index = profile.SensorId.Value.Index;
            if (!profile.SensorId.IsValid ||
                (uint)index >= sensorRegistered.Length ||
                !profile.OwnerEntityId.IsValid)
            {
                throw new ArgumentOutOfRangeException(nameof(profile));
            }

            efficiencies[index]=1f;
            sensors[index] = profile;
            sensorRegistered[index] = true;
        }

        public bool SetSensorEfficiency(SensorId id,float efficiency)
        {
            int index=id.Value.Index;if((uint)index>=sensors.Length||!sensorRegistered[index]||!sensors[index].SensorId.Equals(id))return false;
            efficiencies[index]=float.IsFinite(efficiency)?math.clamp(efficiency,0,1):0;return true;
        }
        public void UnregisterSensor(SensorId sensorId)
        {
            var index = sensorId.Value.Index;
            if ((uint)index < sensorRegistered.Length &&
                sensors[index].SensorId.Equals(sensorId))
            {
                sensorRegistered[index] = false;
            }
        }

        public void RegisterTarget(in LaserRangeTargetProfile profile)
        {
            var index = profile.EntityId.Value.Index;
            if (!profile.EntityId.IsValid ||
                (uint)index >= targetRegistered.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(profile));
            }

            targets[index] = profile;
            targetRegistered[index] = true;
        }

        public void UnregisterTarget(SimEntityId entityId)
        {
            var index = entityId.Value.Index;
            if ((uint)index < targetRegistered.Length &&
                targets[index].EntityId.Equals(entityId))
            {
                targetRegistered[index] = false;
            }
        }

        public bool SchedulePulse(in LaserPulse pulse)
        {
            if (!pulse.SensorId.IsValid ||
                math.lengthsq(pulse.Direction) <= 1e-8f ||
                pendingPulses.Length >= PulseCapacity)
            {
                return false;
            }

            pendingPulses.Add(pulse);
            return true;
        }

        public void Tick(double simTime)
        {
            observations.Clear();
            emissions.Clear();
            var retained = 0;
            for (var i = 0; i < pendingPulses.Length; i++)
            {
                var pulse = pendingPulses[i];
                if (pulse.DueTime > simTime + 1e-6)
                {
                    pendingPulses[retained++] = pulse;
                    continue;
                }

                ProcessPulse(in pulse, simTime);
            }

            if (retained < pendingPulses.Length)
            {
                pendingPulses.ResizeUninitialized(retained);
            }
        }

        private void ProcessPulse(in LaserPulse pulse, double simTime)
        {
            var sensorIndex = pulse.SensorId.Value.Index;
            if ((uint)sensorIndex >= sensorRegistered.Length ||
                !sensorRegistered[sensorIndex])
            {
                return;
            }

            float efficiency=efficiencies[sensorIndex];if(efficiency<=0)return;
            var sensor = sensors[sensorIndex];
            sensor.MaximumRangeMeters*=math.sqrt(efficiency);sensor.BaseRangeSigmaMeters/=math.max(.05f,efficiency);
            if (!sensor.SensorId.Equals(pulse.SensorId) ||
                !world.IsAlive(sensor.OwnerEntityId))
            {
                return;
            }

            var ownerState =
                world.ReadSnapshot[sensor.OwnerEntityId.Value.Index];
            var direction = math.normalizesafe(
                pulse.Direction,
                new float3(0f, 0f, 1f));
            // Returns are generated from the current world snapshot, so they
            // must carry the sensor tick epoch even if a queued pulse was late.
            var timestamp = simTime;
            emissions.Add(new EmissionEvent
            {
                EmitterId = sensor.EmitterId,
                StartTime = timestamp,
                EndTime = timestamp + math.max(
                    1e-6f,
                    sensor.PulseDurationSeconds),
                Origin = ownerState.PositionWorld,
                Direction = direction,
                BeamWidth = math.max(
                    math.radians(0.01f),
                    sensor.BeamWidthRadians),
                MaxRangeMeters = sensor.MaximumRangeMeters,
                FrequencyBand = sensor.FrequencyBand,
                WaveformId = sensor.WaveformId,
                EmissionType = EmissionType.Laser,
                MainlobePowerDb = sensor.MainlobePowerDb,
                SidelobeLevelDb = sensor.SidelobeLevelDb,
            });

            var minimumDot = math.cos(
                math.max(
                    math.radians(0.01f),
                    sensor.BeamWidthRadians) * 0.5f);
            for (var i = 0;
                 i < targetRegistered.Length &&
                 observations.Length < ObservationCapacity;
                 i++)
            {
                if (!targetRegistered[i] ||
                    !world.IsAlive(targets[i].EntityId) ||
                    !world.GetEntityIdAt(i).Equals(targets[i].EntityId) ||
                    targets[i].EntityId.Equals(sensor.OwnerEntityId))
                {
                    continue;
                }

                var relative =
                    world.ReadSnapshot[i].PositionWorld -
                    ownerState.PositionWorld;
                var range = math.length(relative);
                if (range < 1.0 || range > sensor.MaximumRangeMeters)
                {
                    continue;
                }

                var trueDirection = (float3)(relative / range);
                if (math.dot(direction, trueDirection) < minimumDot)
                {
                    continue;
                }

                var target = targets[i];
                var receivedDb =
                    sensor.MainlobePowerDb +
                    target.ReflectivityDb -
                    40f * (float)math.log10(range);
                var marginDb =
                    receivedDb - sensor.ReceiveSensitivityDb;
                if (marginDb <= 0f)
                {
                    continue;
                }

                var probability = math.saturate(marginDb / 8f);
                var seed = SimHash.Chain(
                    SessionSeed,
                    sensor.SensorId.Packed,
                    (uint)i,
                    (uint)pulse.Sequence);
                if (probability < 1f &&
                    !RadarMath.RollDetection(probability, seed))
                {
                    continue;
                }

                var rng = new SimRng(
                    SimHash.Combine(seed, 0x1A53E2D9u));
                var rangeSigma = math.max(
                    0.05f,
                    sensor.BaseRangeSigmaMeters +
                    (float)range *
                    sensor.RangeErrorPartsPerMillion * 1e-6f);
                var angleSigma = math.max(
                    math.radians(0.001f),
                    sensor.BearingSigmaRadians /
                    math.sqrt(math.max(
                        RadarMath.LinearFromDb(marginDb),
                        1f)));
                BuildPerpendicularBasis(
                    trueDirection,
                    out var tangentA,
                    out var tangentB);
                var measuredDirection = math.normalizesafe(
                    trueDirection +
                    tangentA * (rng.NextGaussian() * angleSigma) +
                    tangentB * (rng.NextGaussian() * angleSigma),
                    trueDirection);
                var measuredRange = math.max(
                    0.1f,
                    (float)range + rng.NextGaussian() * rangeSigma);
                observations.Add(new LaserRangeObservation
                {
                    SensorId = sensor.SensorId,
                    CuedTrackId = pulse.CuedTrackId,
                    Timestamp = timestamp,
                    RangeMeters = measuredRange,
                    RangeVar = rangeSigma * rangeSigma,
                    Direction = measuredDirection,
                    AngleVar = angleSigma * angleSigma,
                    SignalMarginDb = marginDb,
                    ObjectClass = target.ObjectClass,
                    ClassificationConfidence =
                        target.ClassificationSignature *
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
            if (pendingPulses.IsCreated)
            {
                pendingPulses.Dispose();
            }
            if (observations.IsCreated)
            {
                observations.Dispose();
            }
            if (emissions.IsCreated)
            {
                emissions.Dispose();
            }
        }
    }
}
