using System;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using Unity.Collections;
using Unity.Mathematics;

namespace ACunicombat.Tracking
{
    public enum RadarTrackDiagnosticKind : byte
    {
        InvalidMeasurement,
        AssociationAccepted,
        ManeuverAssociationAccepted,
        TrackAllocated,
        TrackAllocationFailed,
        LifecycleChanged,
        TrackReleased,
    }

    public struct RadarTrackDiagnostic
    {
        public RadarTrackDiagnosticKind Kind;
        public double Timestamp;
        public int MeasurementIndex;
        public SensorId SensorId;
        public TrackId TrackId;
        public TrackObjectClass ObjectClass;
        public TrackLifecycle PreviousLifecycle;
        public TrackLifecycle Lifecycle;
        public double3 MeasurementPosition;
        public double3 TrackPosition;
        public double NormalizedDistanceSquared;
        public double GateSquared;
        public int CandidateCount;
        public int StaleRejectedCount;
        public int ClassRejectedCount;
        public int GateRejectedCount;
        public double TrackAgeSeconds;
        public int CoastCount;
        public string Reason;
    }

    /// <summary>
    /// Deterministic nearest-neighbour tracker for the first playable radar slice. It associates
    /// anonymous measurements in Cartesian space and exposes only immutable TrackSnapshot values.
    /// </summary>
    public sealed class RadarTrackManager
    {
        public const int MaxTracks = 32;

        private const double AssociationGateSquared = 25.0;
        // The interceptor profile can exceed 500 m/s^2 and search dwells may
        // revisit it only every 0.2 s. The generic aircraft process tier is
        // too confident for that motion and repeatedly breaks association.
        private const double MissilePositionProcessNoise = 900.0;
        private const double MissileVelocityProcessNoise = 90_000.0;
        // 二级机动门限的加速度量级。340 覆盖 QB 的 321 m/s²（90 m/s / 0.28 s）。
        private const double VehicleManeuverAcceleration = 340.0;
        private const double MissileManeuverAcceleration = 600.0;
        private const double InitialVelocitySigmaMetersPerSecond = 300.0;
        private const double UnobservedMissAgeSeconds = 1.0;
        private const double MaximumAssociationAgeSeconds = 1.0;
        private const double DefaultMaximumTrackAgeSeconds = 3.0;
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
        private int activeCapacity = MaxTracks;
        private double maximumTrackAgeSeconds = DefaultMaximumTrackAgeSeconds;

        private struct AssociationSearchStats
        {
            public double BestNormalizedDistanceSquared;
            public double BestGateSquared;
            public int CandidateCount;
            public int StaleRejectedCount;
            public int ClassRejectedCount;
            public int GateRejectedCount;
        }

        public RadarTrackManager()
        {
            for (var i = 0; i < generations.Length; i++)
            {
                generations[i] = 1;
            }
        }

        public int Count => activeCount;
        public int Capacity => activeCapacity;
        public event Action<RadarTrackDiagnostic> Diagnostic;

        public void ConfigureLimits(int capacity, double trackMemorySeconds)
        {
            activeCapacity = math.clamp(capacity, 1, MaxTracks);
            maximumTrackAgeSeconds = math.max(0.25, trackMemorySeconds);
            for (var i = activeCapacity; i < slots.Length; i++)
            {
                if (slots[i].Active)
                {
                    ReleaseTrack(i);
                }
            }
        }

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
                    var missileTrack =
                        slot.ObjectClass == TrackObjectClass.Missile;
                    Cmkf.PredictCv(
                        ref slot.Filter,
                        dt,
                        missileTrack
                            ? MissilePositionProcessNoise
                            : 4.0,
                        missileTrack
                            ? MissileVelocityProcessNoise
                            : 16.0);
                }

                if (simTime - slot.LastUpdateTime >= maximumTrackAgeSeconds)
                {
                    Emit(new RadarTrackDiagnostic
                    {
                        Kind = RadarTrackDiagnosticKind.TrackReleased,
                        Timestamp = simTime,
                        MeasurementIndex = -1,
                        TrackId = slot.Id,
                        ObjectClass = slot.ObjectClass,
                        PreviousLifecycle = slot.Lifecycle.State,
                        Lifecycle = TrackLifecycle.Dropped,
                        TrackPosition = slot.Filter.Position,
                        TrackAgeSeconds = simTime - slot.LastUpdateTime,
                        CoastCount = slot.Lifecycle.MissCount,
                        Reason = "maximum_track_age",
                    });
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
                    Emit(new RadarTrackDiagnostic
                    {
                        Kind = RadarTrackDiagnosticKind.InvalidMeasurement,
                        Timestamp = measurement.Timestamp,
                        MeasurementIndex = i,
                        SensorId = measurement.SensorId,
                        ObjectClass = measurement.ObjectClass,
                        Reason = "invalid_range",
                    });
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
                    measurement.Timestamp,
                    0.0,
                    out var regularStats);
                var maneuverAssociated = false;
                var maneuverStats = default(AssociationSearchStats);
                if (slotIndex < 0)
                {
                    // 二级"机动门限"：常规 5σ 门限是按等速模型的协方差算的，一次 QB
                    // （90 m/s / 0.28 s ≈ 321 m/s²）远超通用档过程噪声 qVelocity = 16
                    // （σ 仅 4 m/s per √s）所能解释的范围，量测会掉到门限外，于是同一个
                    // 物体被 AllocateTrack 新建成第二条航迹——老航迹带着机动前的速度
                    // coast 满 maximumTrackAgeSeconds，屏幕上就出现两个雷达标识，
                    // 而绑定在老 TrackId 上的 STT 因收不到更新而断开。
                    // 这里不放宽常规门限（那会牺牲平飞时的速度精度，也就抵消了
                    // 激光注入带来的横向速度改善），只在常规门限已经失败时，再问一次
                    // "有没有哪条航迹用一次合理的机动就能解释这个量测"。
                    var maneuverAcceleration =
                        ResolveManeuverAcceleration(measurement.ObjectClass);
                    slotIndex = FindAssociation(
                        cartesian,
                        in cartesianCovariance,
                        measurement.ObjectClass,
                        measurement.Timestamp,
                        maneuverAcceleration,
                        out maneuverStats);
                    maneuverAssociated = slotIndex >= 0;
                }

                var allocated = false;
                var failedStats = default(AssociationSearchStats);
                if (slotIndex < 0)
                {
                    failedStats = CombineAssociationStats(
                        in regularStats,
                        in maneuverStats);
                    slotIndex = AllocateTrack(
                        cartesian,
                        in cartesianCovariance,
                        in measurement,
                        measurement.Timestamp);
                    if (slotIndex < 0)
                    {
                        EmitAssociationDiagnostic(
                            RadarTrackDiagnosticKind.TrackAllocationFailed,
                            measurement.Timestamp,
                            i,
                            measurement.SensorId,
                            default,
                            measurement.ObjectClass,
                            cartesian,
                            in failedStats,
                            "track_capacity_exhausted");
                        continue;
                    }

                    allocated = true;
                }

                var firstAssociationThisTick = !associated[slotIndex];
                var slot = slots[slotIndex];
                var previousLifecycle = slot.Lifecycle.State;
                if (allocated)
                {
                    EmitAssociationDiagnostic(
                        RadarTrackDiagnosticKind.TrackAllocated,
                        measurement.Timestamp,
                        i,
                        measurement.SensorId,
                        slot.Id,
                        measurement.ObjectClass,
                        cartesian,
                        in failedStats,
                        "no_association_inside_gate");
                }
                else
                {
                    var stats = maneuverAssociated ? maneuverStats : regularStats;
                    EmitAssociationDiagnostic(
                        maneuverAssociated
                            ? RadarTrackDiagnosticKind.ManeuverAssociationAccepted
                            : RadarTrackDiagnosticKind.AssociationAccepted,
                        measurement.Timestamp,
                        i,
                        measurement.SensorId,
                        slot.Id,
                        measurement.ObjectClass,
                        cartesian,
                        in stats,
                        firstAssociationThisTick
                            ? "first_measurement_this_tick"
                            : "overlapping_measurement");
                }
                if (maneuverAssociated)
                {
                    // 只关联上还不够：过度自信的速度协方差会让本次更新被强烈阻尼，
                    // 滤波器追不上新速度，下一拍照样掉出门限。必须同时把速度协方差
                    // 放开到这次机动可能造成的量级，滤波器才会真的"跳"到新速度上。
                    InflateForManeuver(
                        ref slot,
                        measurement.Timestamp,
                        ResolveManeuverAcceleration(measurement.ObjectClass));
                }
                // Multiple overlapping dwells in one sensor tick are correlated views of the
                // same state. Let them refine the estimate, but do not count them as independent
                // evidence by shrinking the covariance again. Repeated covariance collapse made
                // the next legitimate return miss even a 5-sigma association gate.
                var covarianceBeforeOverlap = slot.Filter.P;
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

                if (!firstAssociationThisTick)
                {
                    slot.Filter.P = covarianceBeforeOverlap;
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
                if (slot.Lifecycle.State != previousLifecycle)
                {
                    EmitLifecycleChanged(
                        measurement.Timestamp,
                        slot,
                        previousLifecycle,
                        "measurement_hit");
                }
            }

            for (var i = 0; i < slots.Length; i++)
            {
                if (!slots[i].Active)
                {
                    continue;
                }

                var slot = slots[i];
                var previousLifecycle = slot.Lifecycle.State;
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
                    EmitLifecycleChanged(
                        simTime,
                        slot,
                        previousLifecycle,
                        "miss_or_covariance_limit");
                    Emit(new RadarTrackDiagnostic
                    {
                        Kind = RadarTrackDiagnosticKind.TrackReleased,
                        Timestamp = simTime,
                        MeasurementIndex = -1,
                        TrackId = slot.Id,
                        ObjectClass = slot.ObjectClass,
                        PreviousLifecycle = previousLifecycle,
                        Lifecycle = TrackLifecycle.Dropped,
                        TrackPosition = slot.Filter.Position,
                        TrackAgeSeconds = simTime - slot.LastUpdateTime,
                        CoastCount = slot.Lifecycle.MissCount,
                        Reason = "lifecycle_dropped",
                    });
                    ReleaseTrack(i);
                }
                else
                {
                    slots[i] = slot;
                    if (slot.Lifecycle.State != previousLifecycle)
                    {
                        EmitLifecycleChanged(
                            simTime,
                            slot,
                            previousLifecycle,
                            "miss_or_covariance_limit");
                    }
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
                for (var trackIndex = 0; trackIndex < activeCapacity; trackIndex++)
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
            if ((uint)index >= activeCapacity || !slots[index].Active || !slots[index].Id.Equals(id))
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
            for (var i = 0; i < activeCapacity; i++)
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
            for (var i = 0; i < activeCapacity && count < destination.Length; i++)
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
            if ((uint)index >= activeCapacity ||
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

        /// <summary>
        /// <paramref name="maneuverAcceleration"/> 为 0 时是原来的常规 5σ 门限；大于 0 时额外
        /// 计入"一次该量级机动可以解释多少偏差"，用于常规门限失败后的二级挽救，避免同一个
        /// 目标在一次 QB 之后被拆成两条航迹。
        /// </summary>
        private int FindAssociation(
            double3 position,
            in Matrix6 measurementCovariance,
            TrackObjectClass measuredObjectClass,
            double measurementTime,
            double maneuverAcceleration,
            out AssociationSearchStats stats)
        {
            stats = new AssociationSearchStats
            {
                BestNormalizedDistanceSquared = double.PositiveInfinity,
                BestGateSquared = AssociationGateSquared,
            };
            var bestIndex = -1;
            var bestDistance = double.MaxValue;
            for (var i = 0; i < activeCapacity; i++)
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
                    stats.StaleRejectedCount++;
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
                    stats.ClassRejectedCount++;
                    continue;
                }

                stats.CandidateCount++;

                // 机动门限项：距上次量测 Δt 之后，一次 maneuverAcceleration 量级的机动
                // 最多把目标带偏约 a·Δt²（取整而非 ½ 以留余量），按 1σ 计入位置方差。
                // maneuverAcceleration = 0 时该项为零，退化为原来的常规门限。
                var maneuverVariance = 0.0;
                if (maneuverAcceleration > 0.0)
                {
                    var unobservedSeconds = math.max(
                        0.0,
                        measurementTime - slots[i].LastUpdateTime);
                    var maneuverOffset =
                        maneuverAcceleration * unobservedSeconds * unobservedSeconds;
                    maneuverVariance = maneuverOffset * maneuverOffset;
                }

                var delta = position - slots[i].Filter.Position;
                var normalizedDistance = 0.0;
                for (var axis = 0; axis < 3; axis++)
                {
                    var variance = math.max(
                        slots[i].Filter.P[axis, axis] +
                        measurementCovariance[axis, axis] +
                        maneuverVariance,
                        1e-6);
                    normalizedDistance += delta[axis] * delta[axis] / variance;
                }

                // A second return in the same tick must use the same statistical gate as the
                // first one. Tightening it to 2.5 sigma split overlapping dwells into new tracks:
                // the rejected return was still inside the normal 5-sigma uncertainty volume.
                // Targets that cannot be resolved inside that volume should remain one track
                // until their measurements separate; inventing a second track is false precision.
                var gate = AssociationGateSquared;
                if (normalizedDistance < stats.BestNormalizedDistanceSquared)
                {
                    stats.BestNormalizedDistanceSquared = normalizedDistance;
                    stats.BestGateSquared = gate;
                }
                if (normalizedDistance >= gate || normalizedDistance >= bestDistance)
                {
                    if (normalizedDistance >= gate)
                    {
                        stats.GateRejectedCount++;
                    }
                    continue;
                }

                bestDistance = normalizedDistance;
                bestIndex = i;
            }

            return bestIndex;
        }

        private void EmitAssociationDiagnostic(
            RadarTrackDiagnosticKind kind,
            double timestamp,
            int measurementIndex,
            SensorId sensorId,
            TrackId trackId,
            TrackObjectClass objectClass,
            double3 measurementPosition,
            in AssociationSearchStats stats,
            string reason)
        {
            var trackPosition = double3.zero;
            var trackAge = 0.0;
            if (trackId.IsValid)
            {
                var index = trackId.Value.Index;
                if ((uint)index < slots.Length && slots[index].Active)
                {
                    trackPosition = slots[index].Filter.Position;
                    trackAge = math.max(
                        0.0,
                        timestamp - slots[index].LastUpdateTime);
                }
            }

            Emit(new RadarTrackDiagnostic
            {
                Kind = kind,
                Timestamp = timestamp,
                MeasurementIndex = measurementIndex,
                SensorId = sensorId,
                TrackId = trackId,
                ObjectClass = objectClass,
                MeasurementPosition = measurementPosition,
                TrackPosition = trackPosition,
                NormalizedDistanceSquared = stats.BestNormalizedDistanceSquared,
                GateSquared = stats.BestGateSquared,
                CandidateCount = stats.CandidateCount,
                StaleRejectedCount = stats.StaleRejectedCount,
                ClassRejectedCount = stats.ClassRejectedCount,
                GateRejectedCount = stats.GateRejectedCount,
                TrackAgeSeconds = trackAge,
                Reason = reason,
            });
        }

        private static AssociationSearchStats CombineAssociationStats(
            in AssociationSearchStats regular,
            in AssociationSearchStats maneuver)
        {
            var regularBest = regular.BestNormalizedDistanceSquared;
            var maneuverBest = maneuver.BestNormalizedDistanceSquared;
            var useRegular = regularBest <= maneuverBest;
            return new AssociationSearchStats
            {
                BestNormalizedDistanceSquared = useRegular
                    ? regularBest
                    : maneuverBest,
                BestGateSquared = useRegular
                    ? regular.BestGateSquared
                    : maneuver.BestGateSquared,
                CandidateCount = regular.CandidateCount + maneuver.CandidateCount,
                StaleRejectedCount = regular.StaleRejectedCount + maneuver.StaleRejectedCount,
                ClassRejectedCount = regular.ClassRejectedCount + maneuver.ClassRejectedCount,
                GateRejectedCount = regular.GateRejectedCount + maneuver.GateRejectedCount,
            };
        }

        private void EmitLifecycleChanged(
            double timestamp,
            in TrackSlot slot,
            TrackLifecycle previousLifecycle,
            string reason)
        {
            if (slot.Lifecycle.State == previousLifecycle)
            {
                return;
            }

            Emit(new RadarTrackDiagnostic
            {
                Kind = RadarTrackDiagnosticKind.LifecycleChanged,
                Timestamp = timestamp,
                MeasurementIndex = -1,
                TrackId = slot.Id,
                ObjectClass = slot.ObjectClass,
                PreviousLifecycle = previousLifecycle,
                Lifecycle = slot.Lifecycle.State,
                TrackPosition = slot.Filter.Position,
                TrackAgeSeconds = math.max(0.0, timestamp - slot.LastUpdateTime),
                CoastCount = slot.Lifecycle.MissCount,
                Reason = reason,
            });
        }

        private void Emit(in RadarTrackDiagnostic diagnostic)
        {
            try
            {
                Diagnostic?.Invoke(diagnostic);
            }
            catch (Exception)
            {
                // Diagnostics must never change tracker behavior.
            }
        }

        /// <summary>
        /// 二级门限使用的机动加速度量级（m/s²）。机体档取 340，覆盖 QB 的
        /// 90 m/s / 0.28 s ≈ 321 m/s²（见 MechFlightMotionController.ConfigureQuickBoost）。
        /// 导弹档的过程噪声本身已经很宽，这里给同一量级只是兜底。
        /// </summary>
        private static double ResolveManeuverAcceleration(
            TrackObjectClass measuredObjectClass)
        {
            return measuredObjectClass == TrackObjectClass.Missile
                ? MissileManeuverAcceleration
                : VehicleManeuverAcceleration;
        }

        /// <summary>
        /// 经二级门限关联后放开速度协方差：一次未被观测到的机动最多改变速度
        /// a·Δt，把它作为 1σ 加进速度对角线，滤波器才能在这一拍就跟上新速度，
        /// 而不是被旧的小协方差压住、下一拍继续掉出门限。
        /// </summary>
        private static void InflateForManeuver(
            ref TrackSlot slot,
            double measurementTime,
            double maneuverAcceleration)
        {
            var unobservedSeconds = math.max(
                0.0,
                measurementTime - slot.LastUpdateTime);
            var velocityUncertainty = maneuverAcceleration * unobservedSeconds;
            if (velocityUncertainty <= 0.0)
            {
                return;
            }

            var additionalVariance = velocityUncertainty * velocityUncertainty;
            for (var axis = 0; axis < 3; axis++)
            {
                slot.Filter.P[axis + 3, axis + 3] += additionalVariance;
            }
        }

        private int AllocateTrack(
            double3 position,
            in Matrix6 measurementCovariance,
            in Measurement measurement,
            double timestamp)
        {
            for (var i = 0; i < activeCapacity; i++)
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
