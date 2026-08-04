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

        private const double AssociationGateSquared = 25.0;
        private const double InitialVelocitySigmaMetersPerSecond = 300.0;
        private const double UnobservedMissAgeSeconds = 1.0;
        private const double MaximumAssociationAgeSeconds = 1.0;
        private const double MaximumTrackAgeSeconds = 3.0;
        private const double LaserRefinementMaximumAgeSeconds = 0.12;
        // Chi-square 99% quantile for a three-dimensional position innovation.
        private const double LaserRefinementNisGate = 11.345;

        private struct TrackSlot
        {
            public bool Active;
            public TrackId Id;
            public FilterState Filter;
            public TrackLifecycleMachine Lifecycle;
            public double LastUpdateTime;
            public TrackSourceMask SourceMask;
            public float Confidence;
            public TrackObjectClass ObjectClass;
            public float ClassificationConfidence;
            public float InterferenceScore;
        }

        private readonly TrackSlot[] slots = new TrackSlot[MaxTracks];
        private readonly bool[] associated = new bool[MaxTracks];
        private readonly bool[] observed = new bool[MaxTracks];
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
            ProcessInternal(
                measurements,
                default,
                false,
                sensorPosition,
                sensorVelocity,
                simTime);
        }

        public void Process(
            NativeList<Measurement> measurements,
            NativeList<EmissionEvent> observationDwells,
            double3 sensorPosition,
            double3 sensorVelocity,
            double simTime)
        {
            ProcessInternal(
                measurements,
                observationDwells,
                true,
                sensorPosition,
                sensorVelocity,
                simTime);
        }

        private void ProcessInternal(
            NativeList<Measurement> measurements,
            NativeList<EmissionEvent> observationDwells,
            bool hasObservationDwells,
            double3 sensorPosition,
            double3 sensorVelocity,
            double simTime)
        {
            for (var i = 0; i < slots.Length; i++)
            {
                associated[i] = false;
                observed[i] = false;
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

                if (simTime - slot.LastUpdateTime >= MaximumTrackAgeSeconds)
                {
                    ReleaseTrack(i);
                    continue;
                }

                slots[i] = slot;
            }

            if (hasObservationDwells)
            {
                MarkObservationOpportunities(observationDwells);
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

                var slotIndex = FindAssociation(
                    cartesian,
                    in cartesianCovariance,
                    measurement.ObjectClass,
                    measurement.Timestamp);
                if (slotIndex < 0)
                {
                    slotIndex = AllocateTrack(
                        cartesian,
                        in cartesianCovariance,
                        in measurement,
                        measurement.Timestamp);
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
                slot.SourceMask |= TrackSourceMask.Radar;
                slot.InterferenceScore = (measurement.Flags & MeasurementFlags.Jammed) != 0 ? 1f : 0f;
                if (firstAssociationThisTick)
                {
                    slot.Lifecycle.OnHit();
                }

                slot.Confidence = ComputeConfidence(slot.Lifecycle.State, measurement.SnrDb, slot.InterferenceScore);
                UpdateClassification(ref slot, in measurement);
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
                    var trackAge = math.max(0.0, simTime - slot.LastUpdateTime);
                    if (observed[i] || trackAge >= UnobservedMissAgeSeconds)
                    {
                        slot.Lifecycle.OnMiss();
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

        private void MarkObservationOpportunities(NativeList<EmissionEvent> observationDwells)
        {
            if (!observationDwells.IsCreated)
            {
                return;
            }

            for (var dwellIndex = 0; dwellIndex < observationDwells.Length; dwellIndex++)
            {
                var dwell = observationDwells[dwellIndex];
                if (dwell.EmissionType != EmissionType.RadarDwell ||
                    dwell.BeamWidth <= 0f)
                {
                    continue;
                }

                var beamDirection = math.normalizesafe(
                    (double3)dwell.Direction,
                    new double3(0.0, 0.0, 1.0));
                var halfBeamCos = math.cos(dwell.BeamWidth * 0.5);
                for (var trackIndex = 0; trackIndex < slots.Length; trackIndex++)
                {
                    if (!slots[trackIndex].Active || observed[trackIndex])
                    {
                        continue;
                    }

                    var relative = slots[trackIndex].Filter.Position - dwell.Origin;
                    var range = math.length(relative);
                    if (range < 1.0 ||
                        (dwell.MaxRangeMeters > 0.0 && range > dwell.MaxRangeMeters))
                    {
                        continue;
                    }

                    observed[trackIndex] =
                        math.dot(relative / range, beamDirection) >= halfBeamCos;
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

        /// <summary>
        /// Applies a cued laser return to an already confirmed missile track.
        /// The update cannot create, confirm, revive, or keep a radar track
        /// alive. A strict NIS gate protects the high-gain position update from
        /// pulling the filter onto another object inside the laser beam.
        /// </summary>
        public bool TryRefineConfirmedTrackWithLaser(
            in LaserRangeObservation observation,
            double3 sensorPosition,
            double simTime,
            out double normalizedInnovationSquared)
        {
            normalizedInnovationSquared = double.PositiveInfinity;
            if (!observation.CuedTrackId.IsValid ||
                observation.ObjectClass != TrackObjectClass.Missile ||
                float.IsNaN(observation.RangeMeters) ||
                observation.RangeMeters <= 0f ||
                math.lengthsq(observation.Direction) <= 1e-8f ||
                observation.Timestamp > simTime + 1e-6 ||
                simTime - observation.Timestamp >
                    LaserRefinementMaximumAgeSeconds)
            {
                return false;
            }

            var index = observation.CuedTrackId.Value.Index;
            if ((uint)index >= slots.Length ||
                !slots[index].Active ||
                !slots[index].Id.Equals(observation.CuedTrackId))
            {
                return false;
            }

            var slot = slots[index];
            if (slot.Lifecycle.State != TrackLifecycle.Confirmed ||
                slot.ObjectClass != TrackObjectClass.Missile)
            {
                return false;
            }

            var propagationSeconds =
                observation.Timestamp - slot.Filter.Time;
            if (propagationSeconds < -1e-6)
            {
                return false;
            }
            if (propagationSeconds > 0.0)
            {
                Cmkf.PredictCv(
                    ref slot.Filter,
                    propagationSeconds,
                    4.0,
                    16.0);
            }

            Cmkf.ConvertMeasurement(
                sensorPosition,
                observation.RangeMeters,
                math.normalizesafe(
                    observation.Direction,
                    new float3(0f, 0f, 1f)),
                math.max(observation.RangeVar, 1e-3f),
                math.max(observation.AngleVar, 1e-12f),
                out var cartesian,
                out var measurementCovariance);
            if (!Cmkf.TryCalculatePositionInnovationNis(
                    in slot.Filter,
                    cartesian,
                    in measurementCovariance,
                    out normalizedInnovationSquared) ||
                normalizedInnovationSquared > LaserRefinementNisGate)
            {
                return false;
            }

            Cmkf.UpdatePosition(
                ref slot.Filter,
                cartesian,
                in measurementCovariance);
            slot.SourceMask |= TrackSourceMask.LaserRangefinder;
            slots[index] = slot;
            return true;
        }

        private int FindAssociation(
            double3 position,
            in Matrix6 measurementCovariance,
            TrackObjectClass measuredObjectClass,
            double measurementTime)
        {
            var bestIndex = -1;
            var bestDistance = double.MaxValue;
            for (var i = 0; i < slots.Length; i++)
            {
                if (!slots[i].Active)
                {
                    continue;
                }

                // A stale covariance ellipsoid can grow until it spans kilometres and then
                // absorb a newly launched target of the same class. Keep the old track for
                // display/coasting, but require fresh evidence before it can claim a return.
                if (measurementTime - slots[i].LastUpdateTime >
                        MaximumAssociationAgeSeconds ||
                    slots[i].Lifecycle.State == TrackLifecycle.Degraded)
                {
                    continue;
                }

                // A missile starts almost exactly on top of its launch aircraft. Position-only
                // association would otherwise absorb the first missile returns into the aircraft
                // track, preventing the defensive fusion layer from ever seeing a missile track.
                // Unknown classifications remain eligible so weak/ambiguous returns can still
                // mature normally.
                if (measuredObjectClass != TrackObjectClass.Unknown &&
                    slots[i].ObjectClass != TrackObjectClass.Unknown &&
                    measuredObjectClass != slots[i].ObjectClass)
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

                // 本 tick 已吃到量测的槽门限收紧 4×：同目标重叠驻留的重复量测
                // 归一化距离 ≪ 门限仍可合并，但近距编队的相邻目标不再被
                // 已关联的航迹抢走（最近邻无排他性的最小缓解）。
                // Overlapping dwells may report the same small, fast target
                // several times in one fusion tick. Lifecycle hits are
                // already de-duplicated by associated[], so retaining the
                // normal covariance gate prevents noisy missile returns from
                // allocating a trail of duplicate tracks.
                var gate = AssociationGateSquared;
                if (normalizedDistance >= gate || normalizedDistance >= bestDistance)
                {
                    continue;
                }

                bestDistance = normalizedDistance;
                bestIndex = i;
            }

            return bestIndex;
        }

        private int AllocateTrack(
            double3 position,
            in Matrix6 measurementCovariance,
            in Measurement measurement,
            double timestamp)
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
                    SourceMask = TrackSourceMask.Radar,
                    Confidence = 0.2f,
                    ObjectClass = measurement.ObjectClass,
                    ClassificationConfidence =
                        math.saturate(
                            measurement.ClassificationConfidence * 0.5f),
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

        private static void UpdateClassification(
            ref TrackSlot slot,
            in Measurement measurement)
        {
            var evidence =
                math.saturate(measurement.ClassificationConfidence);
            if (measurement.ObjectClass == TrackObjectClass.Unknown ||
                evidence <= 0f)
            {
                return;
            }

            if (slot.ObjectClass == TrackObjectClass.Unknown ||
                slot.ObjectClass == measurement.ObjectClass)
            {
                slot.ObjectClass = measurement.ObjectClass;
                slot.ClassificationConfidence = math.saturate(
                    slot.ClassificationConfidence +
                    (1f - slot.ClassificationConfidence) *
                    evidence * 0.35f);
                return;
            }

            slot.ClassificationConfidence =
                math.max(0f, slot.ClassificationConfidence - evidence * 0.4f);
            if (slot.ClassificationConfidence <= 0.2f)
            {
                slot.ObjectClass = measurement.ObjectClass;
                slot.ClassificationConfidence = evidence * 0.35f;
            }
        }

        private static TrackSnapshot BuildSnapshot(in TrackSlot slot)
        {
            var snapshot = new TrackSnapshot
            {
                TrackId = slot.Id,
                Position = slot.Filter.Position,
                Velocity = slot.Filter.Velocity,
                Confidence = slot.Confidence,
                ObjectClass = slot.ObjectClass,
                Classification = slot.ClassificationConfidence,
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
