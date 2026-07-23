using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using Unity.Collections;
using Unity.Mathematics;

namespace ACunicombat.Tracking
{
    /// <summary>
    /// Deterministic nearest-neighbour tracker for the first playable radar slice. It associates
    /// anonymous measurements in Cartesian space and exposes only immutable TrackSnapshot values.
    /// </summary>
    public sealed class RadarTrackManager
    {
        public const int MaxTracks = 32;

        // Process runs at the fusion tick rate, not at a guaranteed observation opportunity
        // for every target. Count at most one miss per observation window so normal radar
        // revisit gaps do not turn one moving target into a trail of coasting tracks.
        private const double MissObservationIntervalSeconds = 0.25;
        private const double AssociationGateSquared = 25.0;
        private const double InitialVelocitySigmaMetersPerSecond = 300.0;

        private struct TrackSlot
        {
            public bool Active;
            public TrackId Id;
            public FilterState Filter;
            public TrackLifecycleMachine Lifecycle;
            public double LastUpdateTime;
            public double LastMissEvaluationTime;
            public TrackSourceMask SourceMask;
            public float Confidence;
            public float InterferenceScore;
        }

        private readonly TrackSlot[] slots = new TrackSlot[MaxTracks];
        private readonly bool[] associated = new bool[MaxTracks];
        private readonly byte[] generations = new byte[MaxTracks];
        private int activeCount;

        public RadarTrackManager()
        {
            for (var i = 0; i < generations.Length; i++)
            {
                generations[i] = 1;
            }
        }

        public int Count => activeCount;

        public void Process(
            NativeList<Measurement> measurements,
            double3 sensorPosition,
            double simTime)
        {
            Process(measurements, sensorPosition, double3.zero, simTime);
        }

        public void Process(
            NativeList<Measurement> measurements,
            double3 sensorPosition,
            double3 sensorVelocity,
            double simTime)
        {
            for (var i = 0; i < slots.Length; i++)
            {
                associated[i] = false;
                if (!slots[i].Active)
                {
                    continue;
                }

                var slot = slots[i];
                var dt = math.max(0.0, simTime - slot.Filter.Time);
                if (dt > 0.0)
                {
                    Cmkf.PredictCv(ref slot.Filter, dt, 4.0, 16.0);
                }

                slots[i] = slot;
            }

            for (var i = 0; i < measurements.Length; i++)
            {
                var measurement = measurements[i];
                if (float.IsNaN(measurement.Range) || measurement.Range <= 0f)
                {
                    continue;
                }

                Cmkf.ConvertMeasurement(
                    sensorPosition,
                    measurement.Range,
                    measurement.Direction,
                    math.max(measurement.RangeVar, 1e-3f),
                    math.max(measurement.AngleVar, 1e-9f),
                    out var cartesian,
                    out var cartesianCovariance);

                var slotIndex = FindAssociation(cartesian, in cartesianCovariance);
                if (slotIndex < 0)
                {
                    slotIndex = AllocateTrack(cartesian, in cartesianCovariance, measurement.Timestamp);
                    if (slotIndex < 0)
                    {
                        continue;
                    }
                }

                var firstAssociationThisTick = !associated[slotIndex];
                var slot = slots[slotIndex];
                Cmkf.UpdatePosition(ref slot.Filter, cartesian, cartesianCovariance);
                if (!float.IsNaN(measurement.RadialVelocity))
                {
                    // Radar Doppler is target velocity relative to the sensor. The filter state is
                    // world-space, so restore the sensor's radial velocity before updating it.
                    var worldRadialVelocity =
                        measurement.RadialVelocity
                        + math.dot(sensorVelocity, (double3)measurement.Direction);
                    Cmkf.UpdateRadialVelocity(
                        ref slot.Filter,
                        measurement.Direction,
                        (float)worldRadialVelocity,
                        math.max(measurement.VelocityVar, 1e-3f));
                }

                slot.LastUpdateTime = measurement.Timestamp;
                slot.LastMissEvaluationTime = measurement.Timestamp;
                slot.SourceMask |= TrackSourceMask.Radar;
                slot.InterferenceScore = (measurement.Flags & MeasurementFlags.Jammed) != 0 ? 1f : 0f;
                if (firstAssociationThisTick)
                {
                    slot.Lifecycle.OnHit();
                }

                slot.Confidence = ComputeConfidence(slot.Lifecycle.State, measurement.SnrDb, slot.InterferenceScore);
                slots[slotIndex] = slot;
                associated[slotIndex] = true;
            }

            for (var i = 0; i < slots.Length; i++)
            {
                if (!slots[i].Active)
                {
                    continue;
                }

                var slot = slots[i];
                if (!associated[i])
                {
                    var missReferenceTime = math.max(
                        slot.LastUpdateTime,
                        slot.LastMissEvaluationTime);
                    if (simTime - missReferenceTime >= MissObservationIntervalSeconds)
                    {
                        slot.Lifecycle.OnMiss();
                        slot.LastMissEvaluationTime = simTime;
                        slot.Confidence *= 0.85f;
                    }

                    if (slot.Lifecycle.State == TrackLifecycle.Coasting ||
                        slot.Lifecycle.State == TrackLifecycle.Degraded)
                    {
                        var positionTrace =
                            slot.Filter.P[0, 0] + slot.Filter.P[1, 1] + slot.Filter.P[2, 2];
                        slot.Lifecycle.OnCoastCovariance(
                            positionTrace,
                            250_000.0,
                            4_000_000.0);
                    }
                }

                if (slot.Lifecycle.State == TrackLifecycle.Dropped)
                {
                    ReleaseTrack(i);
                }
                else
                {
                    slots[i] = slot;
                }
            }
        }

        public bool TryGetTrack(TrackId id, out TrackSnapshot snapshot)
        {
            var index = id.Value.Index;
            if ((uint)index >= slots.Length || !slots[index].Active || !slots[index].Id.Equals(id))
            {
                snapshot = default;
                return false;
            }

            snapshot = BuildSnapshot(in slots[index]);
            return true;
        }

        public bool TryGetBestTrack(out TrackSnapshot snapshot)
        {
            var bestIndex = -1;
            var bestScore = float.MinValue;
            for (var i = 0; i < slots.Length; i++)
            {
                if (!slots[i].Active || slots[i].Lifecycle.State == TrackLifecycle.Dropped)
                {
                    continue;
                }

                var score = slots[i].Confidence
                    + (slots[i].Lifecycle.State == TrackLifecycle.Confirmed ? 1f : 0f);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
            {
                snapshot = default;
                return false;
            }

            snapshot = BuildSnapshot(in slots[bestIndex]);
            return true;
        }

        /// <summary>
        /// Copies active public snapshots into caller-owned storage. No simulation entity or
        /// measurement association identity crosses this boundary.
        /// </summary>
        public int CopyTracks(TrackSnapshot[] destination)
        {
            if (destination == null || destination.Length == 0)
            {
                return 0;
            }

            var count = 0;
            for (var i = 0; i < slots.Length && count < destination.Length; i++)
            {
                if (!slots[i].Active || slots[i].Lifecycle.State == TrackLifecycle.Dropped)
                {
                    continue;
                }

                destination[count++] = BuildSnapshot(in slots[i]);
            }

            return count;
        }

        private int FindAssociation(double3 position, in Matrix6 measurementCovariance)
        {
            var bestIndex = -1;
            var bestDistance = AssociationGateSquared;
            for (var i = 0; i < slots.Length; i++)
            {
                if (!slots[i].Active)
                {
                    continue;
                }

                var delta = position - slots[i].Filter.Position;
                var normalizedDistance = 0.0;
                for (var axis = 0; axis < 3; axis++)
                {
                    var variance = math.max(
                        slots[i].Filter.P[axis, axis] + measurementCovariance[axis, axis],
                        1e-6);
                    normalizedDistance += delta[axis] * delta[axis] / variance;
                }

                if (normalizedDistance < bestDistance)
                {
                    bestDistance = normalizedDistance;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        private int AllocateTrack(double3 position, in Matrix6 measurementCovariance, double timestamp)
        {
            for (var i = 0; i < slots.Length; i++)
            {
                if (slots[i].Active)
                {
                    continue;
                }

                Matrix6 covariance = default;
                for (var axis = 0; axis < 3; axis++)
                {
                    covariance[axis, axis] = math.max(measurementCovariance[axis, axis], 1.0);
                    covariance[axis + 3, axis + 3] =
                        InitialVelocitySigmaMetersPerSecond *
                        InitialVelocitySigmaMetersPerSecond;
                }

                var lifecycle = TrackLifecycleMachine.Create();
                slots[i] = new TrackSlot
                {
                    Active = true,
                    Id = new TrackId(new Handle(i, generations[i])),
                    Filter = new FilterState
                    {
                        Position = position,
                        Velocity = double3.zero,
                        P = covariance,
                        Time = timestamp,
                    },
                    Lifecycle = lifecycle,
                    LastUpdateTime = timestamp,
                    LastMissEvaluationTime = timestamp,
                    SourceMask = TrackSourceMask.Radar,
                    Confidence = 0.2f,
                };
                // The caller performs the first update and records exactly one lifecycle hit.
                // Additional overlapping dwells in the same fusion tick may still refine this
                // track, but they must not create duplicate tracks or inflate its hit count.
                associated[i] = false;
                activeCount++;
                return i;
            }

            return -1;
        }

        private void ReleaseTrack(int index)
        {
            slots[index] = default;
            var next = (byte)(generations[index] + 1);
            generations[index] = next == 0 ? (byte)1 : next;
            activeCount--;
        }

        private static float ComputeConfidence(TrackLifecycle lifecycle, float snrDb, float interference)
        {
            var lifecycleWeight = lifecycle == TrackLifecycle.Confirmed ? 0.65f : 0.3f;
            var snrWeight = math.saturate((snrDb + 5f) / 30f) * 0.35f;
            return math.saturate(lifecycleWeight + snrWeight - interference * 0.2f);
        }

        private static TrackSnapshot BuildSnapshot(in TrackSlot slot)
        {
            var snapshot = new TrackSnapshot
            {
                TrackId = slot.Id,
                Position = slot.Filter.Position,
                Velocity = slot.Filter.Velocity,
                Confidence = slot.Confidence,
                Classification = 0f,
                IdentityConfidence = 0f,
                Affiliation = TrackAffiliation.Unknown,
                LastUpdateTime = slot.LastUpdateTime,
                LastPredictTime = slot.Filter.Time,
                CoastCount = slot.Lifecycle.MissCount,
                HitCount = slot.Lifecycle.HitCount,
                SourceMask = slot.SourceMask,
                ConsistencyScore = 1f,
                InterferenceScore = slot.InterferenceScore,
                Lifecycle = slot.Lifecycle.State,
            };

            for (var row = 0; row < 6; row++)
            {
                for (var column = row; column < 6; column++)
                {
                    snapshot.SetCovariance(row, column, slot.Filter.P[row, column]);
                }
            }

            return snapshot;
        }
    }
}
