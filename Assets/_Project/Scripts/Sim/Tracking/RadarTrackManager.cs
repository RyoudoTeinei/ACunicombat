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
        TrackInitiationDeferred,
        TelemetryRefinementAccepted,
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
        // Track-initiation policy, evaluated against the receiver's reported angular
        // accuracy (not range or truth). Well-localized returns may initiate directly;
        // broad bearing detections need temporal confirmation. Equipment performance
        // determines AngleVar, so this is not a fixed detection-distance cutoff.
        public double SingleLookMissileBearingSigmaRadians { get; set; } = .005;
        private sealed class BirthCandidate {public FilterState State;public double LastTime;public int Hits;}
        private readonly BirthCandidate[] birthCandidates=new BirthCandidate[MaxTracks];
        private readonly BirthCandidate[] missileBirthCandidates=new BirthCandidate[MaxTracks];
        private bool ConfirmMissileBirth(in Measurement measurement,double3 observer,double3 observerVelocity,double3 position,in Matrix6 covariance)
        {
            int free=-1,best=-1;double bestCost=double.PositiveInfinity;FilterState bestState=default;
            for(int i=0;i<missileBirthCandidates.Length;i++)
            {
                var candidate=missileBirthCandidates[i];
                if(candidate==null||measurement.Timestamp-candidate.LastTime>MaximumAssociationAgeSeconds)
                {if(free<0)free=i;continue;}
                var predicted=candidate.State;
                Cmkf.PredictCv(ref predicted,math.max(0,measurement.Timestamp-predicted.Time),MissilePositionProcessNoise,MissileVelocityProcessNoise,true);
                Cmkf.BuildSphericalInnovation(predicted,observer,measurement.Range,measurement.Direction,measurement.RangeVar,measurement.AngleVar,out var z,out var r);
                if(!Cmkf.TryCalculatePositionInnovationNis(predicted,z,r,out double nis)||nis>=AssociationGateSquared)continue;
                double cost=Cmkf.PositionAssociationCost(predicted,r,nis);
                if(cost>=bestCost||!Cmkf.TryUpdateRadarMeasurement(ref predicted,observer,observerVelocity,
                    measurement.Range,measurement.Direction,measurement.RangeVar,measurement.AngleVar,measurement.RadialVelocity,measurement.VelocityVar))continue;
                best=i;bestCost=cost;bestState=predicted;
            }
            if(best>=0)
            {
                var previous=missileBirthCandidates[best];
                // Overlapping views of one echo are not independent confirmation.
                if(measurement.Timestamp<=previous.LastTime+1e-6)return false;
                previous.State=bestState;previous.LastTime=measurement.Timestamp;previous.Hits++;
                if(previous.Hits<2)return false;
                missileBirthCandidates[best]=null;return true;
            }
            if(free<0)return false;
            var state=new FilterState{Position=position,P=covariance,Time=measurement.Timestamp};
            var los=math.normalizesafe((double3)measurement.Direction,new double3(0,0,1));
            double radial=float.IsFinite(measurement.RadialVelocity)?measurement.RadialVelocity:0;
            state.Velocity=observerVelocity+los*radial;
            double transverse=InitialMissileTransverseSigmaMetersPerSecond*InitialMissileTransverseSigmaMetersPerSecond;
            for(int row=0;row<3;row++)for(int col=0;col<3;col++)
                state.P[row+3,col+3]=transverse*((row==col?1:0)-los[row]*los[col])+math.max(1,measurement.VelocityVar)*los[row]*los[col];
            missileBirthCandidates[free]=new BirthCandidate{State=state,LastTime=measurement.Timestamp,Hits=1};return false;
        }
        private bool ConfirmNearbyAircraftBirth(in Measurement measurement,double3 observer,double3 velocity,double3 position,in Matrix6 covariance)
        {
            int free=-1,best=-1;double score=25;
            for(int i=0;i<birthCandidates.Length;i++)
            {
                var candidate=birthCandidates[i];
                if(candidate==null||measurement.Timestamp-candidate.LastTime>1){if(free<0)free=i;continue;}
                var predicted=candidate.State;Cmkf.PredictCv(ref predicted,math.max(0,measurement.Timestamp-predicted.Time),100,400);
                Cmkf.BuildSphericalInnovation(predicted,observer,measurement.Range,measurement.Direction,measurement.RangeVar,measurement.AngleVar,out var z,out var r);
                if(Cmkf.TryCalculatePositionInnovationNis(predicted,z,r,out double nis)&&nis<score){score=nis;best=i;}
            }
            if(best<0)
            {
                if(free<0)return false;
                var state=new FilterState{Position=position,P=covariance,Time=measurement.Timestamp};
                var los=math.normalizesafe((double3)measurement.Direction,new double3(0,0,1));
                if(float.IsFinite(measurement.RadialVelocity))state.Velocity=velocity+los*measurement.RadialVelocity;
                for(int axis=3;axis<6;axis++)state.P[axis,axis]=InitialVelocitySigmaMetersPerSecond*InitialVelocitySigmaMetersPerSecond;
                birthCandidates[free]=new BirthCandidate{State=state,LastTime=measurement.Timestamp,Hits=1};return false;
            }
            var existing=birthCandidates[best];
            if(measurement.Timestamp>existing.LastTime+1e-6){existing.LastTime=measurement.Timestamp;existing.Hits++;}
            if(existing.Hits<3)return false;
            birthCandidates[best]=null;return true;
        }

        private readonly System.Collections.Generic.List<int> measurementOrder=new System.Collections.Generic.List<int>(256);
        private static bool MeasurementAfter(in Measurement a,in Measurement b)
        {
            if(a.Timestamp!=b.Timestamp)return a.Timestamp>b.Timestamp;
            double av=a.RangeVar+2.0*a.Range*a.Range*a.AngleVar;
            double bv=b.RangeVar+2.0*b.Range*b.Range*b.AngleVar;
            if(av!=bv)return av>bv;
            if(a.SensorId.Packed!=b.SensorId.Packed)return a.SensorId.Packed>b.SensorId.Packed;
            if(a.DwellSequence!=b.DwellSequence)return a.DwellSequence>b.DwellSequence;
            if(a.Range!=b.Range)return a.Range>b.Range;
            if(a.Direction.x!=b.Direction.x)return a.Direction.x>b.Direction.x;
            if(a.Direction.y!=b.Direction.y)return a.Direction.y>b.Direction.y;
            return a.Direction.z>b.Direction.z;
        }


        private const double AssociationGateSquared = 25.0;
        // Continuous manoeuvres need a velocity random-walk allowance, not just the rare QB gate.
        // 25 m/s per sqrt(second) covers sustained course changes between sparse revisits.
        private const double AircraftVelocityProcessNoise = 625.0;
        // The interceptor profile can exceed 500 m/s^2 and search dwells may
        // revisit it only every 0.2 s. The generic aircraft process tier is
        // too confident for that motion and repeatedly breaks association.
        private const double MissilePositionProcessNoise = 4.0;
        private const double MissileVelocityProcessNoise = 90_000.0;
        // 二级机动门限的加速度量级。340 覆盖 QB 的 321 m/s²（90 m/s / 0.28 s）。
        private const double VehicleManeuverAcceleration = 340.0;
        // Position measurements can continue throughout a boost while the CV velocity estimate
        // lags by almost the full delta-v. Preserve one QB burn of velocity uncertainty when the
        // normal gate fails instead of assuming all model error began in the latest 40 ms tick.
        private const double VehicleManeuverResponseSeconds = 0.28;
        private const double MissileManeuverAcceleration = 600.0;
        // Reachability bound, separate from filter tuning: includes interceptor axial and RCS acceleration.
        private const double MissileReachableAcceleration = 1500.0;
        // A precise missile return can sit just outside the normal 5-sigma gate while the
        // constant-velocity estimate catches up with a terminal correction. Preserve a short
        // response window instead of shrinking the maneuver allowance to the 40 ms fusion tick.
        private const double MissileManeuverResponseSeconds = 0.12;
        private const double InitialVelocitySigmaMetersPerSecond = 300.0;
        // An unresolved missile may already be travelling at 2–3 km/s sideways.
        // This is an uncertainty prior, not a speed limit; measured Doppler stays precise.
        private const double InitialMissileTransverseSigmaMetersPerSecond = 1500.0;
        private const double UnobservedMissAgeSeconds = 1.0;
        private const double MaximumAssociationAgeSeconds = 1.0;
        private const double DefaultMaximumTrackAgeSeconds = 3.0;
        private const double LaserRefinementMaximumAgeSeconds = 0.12;
        // Chi-square 99% quantile for a three-dimensional position innovation.
        private const double LaserRefinementNisGate = 11.345;

        private readonly System.Collections.Generic.HashSet<(uint sensor,ulong dwell,int slot)> dwellAssignments=new();
        private struct TrackSlot
        {
            public bool Active;
            public bool AssociationClosed;
            public TrackId Id;
            public FilterState Filter;
            public FilterState LastMeasuredFilter;
            public TrackLifecycleMachine Lifecycle;
            public double LastUpdateTime;
            public double FirstObservationTime;
            public double LastCloseProtectionTime;
            public int CoveredMisses;
            public float LastRadarSnrDb;
            public bool HasRadialHistory;
            public double RangeTime,MeasuredRange,MeasuredRangeVariance,MeasuredRangeRate,MeasuredRangeRateVariance;
            public double3 RangeObserverPosition,RangeObserverVelocity;
            public FilterState RangeFilter;
            public TrackSourceMask SourceMask;
            public TrackSourceEvidenceSet SourceEvidence;
            public float Confidence;
            public TrackObjectClass ObjectClass;
            public float ClassificationConfidence;
            public float InterferenceScore;
        }

        private readonly TrackSlot[] slots = new TrackSlot[MaxTracks];
        private readonly bool[] associated = new bool[MaxTracks];
        private readonly bool[] allocatedThisTick = new bool[MaxTracks];
        private readonly bool[] observed = new bool[MaxTracks];
        private readonly bool[] fullyObserved = new bool[MaxTracks];
        private readonly byte[] generations = new byte[MaxTracks];
        // Handle has an 8-bit generation. A wrapped ID must not alias a cached prior
        // missile during the retention window, even under extreme allocation pressure.
        private readonly double[,] generationRetiredUntil = new double[MaxTracks,256];
        private int activeCount;
        private int activeCapacity = MaxTracks;
        private double maximumTrackAgeSeconds = DefaultMaximumTrackAgeSeconds;

        private struct AssociationSearchStats
        {
            public double BestNormalizedDistanceSquared;
            public double BestGateSquared;
            public double BestAssociationCost;
            public int CandidateCount;
            public int StaleRejectedCount;
            public int ClassRejectedCount;
            public int GateRejectedCount;
        }

        internal readonly string ReplayIdentity=Guid.NewGuid().ToString("N");
        internal int ReplayDepth;

        public RadarTrackManager()
        {
            for (var i = 0; i < generations.Length; i++)
            {
                generations[i] = 1;
            }
        
            RadarReplayJournal.Emit(this,new RadarReplayOperation{kind="create"});
        }

        public int Count { get { int n=0;for(int i=0;i<activeCapacity;i++)if(slots[i].Active&&!slots[i].AssociationClosed)n++;return n; } }
        public int Capacity => activeCapacity;
        public event Action<RadarTrackDiagnostic> Diagnostic;

        public bool CloseMissileAssociation(TrackId id,double now,string reason="confirmed_missile_end_closes_association")
        {
            int index=id.Value.Index;
            if(!id.IsValid||(uint)index>=activeCapacity||!slots[index].Active||!slots[index].Id.Equals(id)||slots[index].ObjectClass!=TrackObjectClass.Missile||slots[index].AssociationClosed)return false;
            var slot=slots[index];var previous=slot.Lifecycle.State;slot.AssociationClosed=true;slot.Lifecycle.State=TrackLifecycle.Coasting;slots[index]=slot;
            Emit(new RadarTrackDiagnostic{Kind=RadarTrackDiagnosticKind.LifecycleChanged,Timestamp=now,TrackId=id,ObjectClass=slot.ObjectClass,PreviousLifecycle=previous,Lifecycle=slot.Lifecycle.State,TrackPosition=slot.Filter.Position,Reason=reason});
            RadarReplayJournal.Emit(this,new RadarReplayOperation{kind="close",track=id.Packed,time=now,reason=reason});return true;
        }

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
        
            RadarReplayJournal.Emit(this,new RadarReplayOperation{kind="limits",capacity=capacity,memory=trackMemorySeconds});
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
            var replay=RadarReplayJournal.Capture(measurements,observationDwells,hasObservationDwells,sensorPosition,sensorVelocity,simTime);
            ReplayDepth++;bool completed=false;
            try
            {
            var trackSections=ACunicombat.Simulation.Core.SimulationCostProbe.Sections.Begin(ACunicombat.Simulation.Core.SimulationCostProbe.SystemId.TrackPredict);
            for (var i = 0; i < slots.Length; i++)
            {
                associated[i] = false;
                allocatedThisTick[i] = false;
                observed[i] = false;
                fullyObserved[i] = false;
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
                            : AircraftVelocityProcessNoise,
                        integrateAccelerationNoise: missileTrack);
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

            trackSections.Next(ACunicombat.Simulation.Core.SimulationCostProbe.SystemId.TrackObservation);
            if (hasObservationDwells)
            {
                MarkObservationOpportunities(observationDwells);
            }

            // Process precise returns first within an epoch, so an overlapping sidelobe
            // cannot bootstrap an overconfident competing track before its mainlobe return.
            trackSections.Next(ACunicombat.Simulation.Core.SimulationCostProbe.SystemId.TrackBatchPlan);
            dwellAssignments.Clear();
            measurementOrder.Clear();
            for(int index=0;index<measurements.Length;index++)
            {
                int at=measurementOrder.Count;measurementOrder.Add(index);
                while(at>0 && MeasurementAfter(measurements[measurementOrder[at-1]],measurements[index]))
                {measurementOrder[at]=measurementOrder[at-1];at--;}
                measurementOrder[at]=index;
            }
            // Every dwell is assigned against the same predicted state. Commit in
            // GLOBAL precision order afterwards; grouping commits by dwell would let
            // an early dwell's sidelobe suppress a later dwell's precise mainlobe.
            batchPlans.Clear();
            foreach(int index in measurementOrder)PrepareResolvedBatch(measurements,index,sensorPosition,sensorVelocity);
            trackSections.Next(ACunicombat.Simulation.Core.SimulationCostProbe.SystemId.TrackAssociate);
            for (var order = 0; order < measurementOrder.Count; order++)
            {
                int i=measurementOrder[order];
                var measurement = measurements[i];
                if (!float.IsFinite(measurement.Range) || measurement.Range <= 0f ||
                    !math.all(math.isfinite(measurement.Direction)) || math.lengthsq(measurement.Direction)<1e-12f ||
                    !float.IsFinite(measurement.RangeVar)||measurement.RangeVar<0 || !float.IsFinite(measurement.AngleVar)||measurement.AngleVar<0)
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

                int slotIndex;bool maneuverAssociated;
                AssociationSearchStats regularStats,maneuverStats;
                bool batchPlanned=batchPlans.TryGetValue(i,out var plan);
                if(batchPlanned)
                {
                    if(plan.Ambiguous)
                    {
                        Emit(new RadarTrackDiagnostic{Kind=RadarTrackDiagnosticKind.TrackInitiationDeferred,Timestamp=measurement.Timestamp,
                            MeasurementIndex=i,SensorId=measurement.SensorId,ObjectClass=measurement.ObjectClass,Reason="ambiguous_resolved_batch"});
                        continue;
                    }
                    slotIndex=plan.Slot;maneuverAssociated=plan.Maneuver;
                    regularStats=plan.Stats;
                    maneuverStats=plan.Maneuver?plan.Stats:new AssociationSearchStats{BestNormalizedDistanceSquared=double.PositiveInfinity,BestGateSquared=AssociationGateSquared};
                    if(slotIndex<0)
                    {
                        // Independent dwells can observe a newly born track in this
                        // same epoch. Only births may be considered here: existing
                        // tracks must retain the immutable whole-batch assignment.
                        slotIndex=FindAssociation(in measurement,sensorPosition,sensorVelocity,cartesian,
                            in cartesianCovariance,measurement.ObjectClass,measurement.Timestamp,0,out var birthStats,
                            onlyNewlyAllocated:true);
                        if(slotIndex>=0)regularStats=birthStats;
                    }
                }
                else
                {
                slotIndex = FindAssociation(
                    in measurement, sensorPosition, sensorVelocity,
                    cartesian,
                    in cartesianCovariance,
                    measurement.ObjectClass,
                    measurement.Timestamp,
                    0.0,
                    out regularStats);
                maneuverAssociated = false;
                maneuverStats = default(AssociationSearchStats);
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
                        in measurement, sensorPosition, sensorVelocity,
                        cartesian,
                        in cartesianCovariance,
                        measurement.ObjectClass,
                        measurement.Timestamp,
                        maneuverAcceleration,
                        out maneuverStats);
                    maneuverAssociated = slotIndex >= 0;
                }

                }

                // After losing kinematic continuity, a precise local acquisition is a
                // new hypothesis unless an authenticated fresh terminal bridges the gap.
                // Sparse long-range radar reacquisition keeps its existing policy.
                if(slotIndex>=0 && slots[slotIndex].ObjectClass==TrackObjectClass.Missile &&
                    (measurement.Flags&MeasurementFlags.CloseProtectionReceiver)!=0 &&
                    measurement.Timestamp-slots[slotIndex].LastUpdateTime>MaximumAssociationAgeSeconds &&
                    !(slots[slotIndex].SourceEvidence.DataLink.Valid && measurement.Timestamp-slots[slotIndex].SourceEvidence.DataLink.Timestamp<=1))
                {
                    CloseMissileAssociation(slots[slotIndex].Id,measurement.Timestamp,"local_reacquisition_without_continuity");
                    slotIndex=-1;maneuverAssociated=false;
                }

                // A manoeuvre-only candidate must not steal a track from another
                // resolved return in this same dwell that already fits its normal gate.
                if(!batchPlanned && slotIndex>=0 && maneuverAssociated && PendingResolvedReturnHasRegularAssociation(
                    measurements,order+1,in measurement,slotIndex,sensorPosition,sensorVelocity))
                {slotIndex=-1;maneuverAssociated=false;}

                if(slotIndex>=0&&maneuverAssociated&&measurement.ObjectClass==TrackObjectClass.Missile&&measurement.Timestamp-slots[slotIndex].LastUpdateTime>1.0)
                {
                    // After a long gap a return outside the normal gate may be a NEW missile.
                    // Do not transplant its motion and inherited identity into the old hypothesis.
                    CloseMissileAssociation(slots[slotIndex].Id,measurement.Timestamp,"continuity_uncertain_after_coast");
                    slotIndex=-1;maneuverAssociated=false;
                }
                var allocated = false;
                var failedStats = default(AssociationSearchStats);
                if (slotIndex < 0)
                {
                    failedStats = CombineAssociationStats(
                        in regularStats,
                        in maneuverStats);
                    // Detection is not yet a stable public contact. Standard radar needs
                    // an independent revisit before assigning a missile track identity.
                    // The close-protection receiver measures full local motion and retains
                    // immediate acquisition; own launch/radio contacts are independent.
                    if(measurement.ObjectClass==TrackObjectClass.Missile&&
                        (measurement.Flags&MeasurementFlags.CloseProtectionReceiver)==0&&
                        measurement.AngleVar>SingleLookMissileBearingSigmaRadians*SingleLookMissileBearingSigmaRadians&&
                        !ConfirmMissileBirth(measurement,sensorPosition,sensorVelocity,cartesian,cartesianCovariance))
                    {
                        Emit(new RadarTrackDiagnostic{Kind=RadarTrackDiagnosticKind.TrackInitiationDeferred,Timestamp=measurement.Timestamp,
                            MeasurementIndex=i,SensorId=measurement.SensorId,ObjectClass=measurement.ObjectClass,MeasurementPosition=cartesian,
                            Reason="missile_requires_independent_revisit"});
                        continue;
                    }
                    // A marginal outlier beside an existing aircraft is a hypothesis, not
                    // an immediately publishable second aircraft. Missiles retain immediate acquisition.
                    double nearestNis=math.min(regularStats.BestNormalizedDistanceSquared,maneuverStats.BestNormalizedDistanceSquared);
                    if(measurement.ObjectClass==TrackObjectClass.AirVehicle&&nearestNis<100&&
                        !ConfirmNearbyAircraftBirth(measurement,sensorPosition,sensorVelocity,cartesian,cartesianCovariance))
                    {
                        Emit(new RadarTrackDiagnostic{Kind=RadarTrackDiagnosticKind.TrackInitiationDeferred,Timestamp=measurement.Timestamp,
                            MeasurementIndex=i,SensorId=measurement.SensorId,ObjectClass=measurement.ObjectClass,MeasurementPosition=cartesian,NormalizedDistanceSquared=nearestNis,Reason="nearby_aircraft_requires_independent_confirmation"});
                        continue;
                    }
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
                    allocatedThisTick[slotIndex] = true;
                }

                if(measurement.DwellSequence!=0)dwellAssignments.Add((measurement.SensorId.Packed,measurement.DwellSequence,slotIndex));
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
                // Returns are precision-sorted. Reusing a correlated, coarser view can move the mean
                // outside its retained covariance even when covariance shrinkage is prevented.
                if (!firstAssociationThisTick && measurement.Timestamp <= slot.LastUpdateTime + 1e-6) continue;
                if (maneuverAssociated)
                {
                    // 只关联上还不够：过度自信的速度协方差会让本次更新被强烈阻尼，
                    // 滤波器追不上新速度，下一拍照样掉出门限。必须同时把速度协方差
                    // 放开到这次机动可能造成的量级，滤波器才会真的"跳"到新速度上。
                    InflateForManeuver(
                        ref slot,
                        measurement.Timestamp,
                        ResolveManeuverAcceleration(measurement.ObjectClass),
                        ResolveManeuverResponseSeconds(measurement.ObjectClass));
                }
                // The independent observation updates both the mean and its covariance.
                if(!allocated && !Cmkf.TryUpdateRadarMeasurement(ref slot.Filter,sensorPosition,sensorVelocity,
                    measurement.Range,measurement.Direction,measurement.RangeVar,measurement.AngleVar,
                    measurement.RadialVelocity,measurement.VelocityVar))
                {
                    Emit(new RadarTrackDiagnostic{Kind=RadarTrackDiagnosticKind.InvalidMeasurement,Timestamp=measurement.Timestamp,
                        MeasurementIndex=i,SensorId=measurement.SensorId,Reason="joint_measurement_update_rejected"});
                    if(measurement.DwellSequence!=0)dwellAssignments.Remove((measurement.SensorId.Packed,measurement.DwellSequence,slotIndex));
                    continue;
                }
                if (float.IsFinite(measurement.RadialVelocity))
                {
                    if(allocated)
                    {
                        // The first Doppler return initializes velocity; it is not an outlier
                        // against an invented stationary prior. Preserve unresolved transverse motion.
                        var los=math.normalizesafe((double3)measurement.Direction,new double3(0,0,1));
                        double radial=measurement.RadialVelocity;
                        slot.Filter.Velocity=sensorVelocity+los*radial;
                        double initialSigma=measurement.ObjectClass==TrackObjectClass.Missile
                            ? InitialMissileTransverseSigmaMetersPerSecond : InitialVelocitySigmaMetersPerSecond;
                        double tangentVariance=initialSigma*initialSigma+radial*radial*math.max(0,measurement.AngleVar);
                        for(int row=0;row<3;row++)for(int col=0;col<3;col++)
                            slot.Filter.P[row+3,col+3]=tangentVariance*((row==col?1:0)-los[row]*los[col])+math.max(1e-3,measurement.VelocityVar)*los[row]*los[col];
                    }
                }

                // The close-protection receiver measures LOS rate as well as Doppler.
                // Ignoring it forces transverse velocity to converge from position differences,
                // which lags a guided missile turning after an accelerating observer.
                if ((measurement.Flags & MeasurementFlags.CloseProtectionReceiver) != 0 &&
                    math.all(math.isfinite(measurement.LosRate)) &&
                    math.isfinite(measurement.LosRateVar) && measurement.LosRateVar > 0 &&
                    math.isfinite(measurement.Range) && measurement.Range > 0)
                {
                    var los = math.normalizesafe(measurement.Direction, new float3(0,0,1));
                    var tangentA = math.normalizesafe(math.cross(los, math.abs(los.y)<.9f ? new float3(0,1,0) : new float3(1,0,0)));
                    var tangentB = math.cross(los,tangentA);
                    var transverse = sensorVelocity + (double3)math.cross(measurement.LosRate,los*measurement.Range);
                    float variance = math.max(1f,measurement.Range*measurement.Range*measurement.LosRateVar +
                        math.max(0,measurement.RangeVar)*math.lengthsq(measurement.LosRate));
                    Cmkf.UpdateRadialVelocity(ref slot.Filter,tangentA,(float)math.dot(transverse,(double3)tangentA),variance);
                    Cmkf.UpdateRadialVelocity(ref slot.Filter,tangentB,(float)math.dot(transverse,(double3)tangentB),variance);
                }


                slot.CoveredMisses=0;slot.LastRadarSnrDb=measurement.SnrDb;
                if(float.IsFinite(measurement.RadialVelocity) && measurement.VelocityVar>0)
                {
                    slot.HasRadialHistory=true;slot.RangeTime=measurement.Timestamp;
                    slot.MeasuredRange=measurement.Range;slot.MeasuredRangeVariance=measurement.RangeVar;
                    slot.MeasuredRangeRate=measurement.RadialVelocity;slot.MeasuredRangeRateVariance=measurement.VelocityVar;
                    slot.RangeObserverPosition=sensorPosition;slot.RangeObserverVelocity=sensorVelocity;
                    slot.RangeFilter=slot.Filter;
                }
                slot.LastMeasuredFilter = slot.Filter;
                slot.LastUpdateTime = measurement.Timestamp;
                slot.SourceMask |= TrackSourceMask.Radar;
                if((measurement.Flags&MeasurementFlags.CloseProtectionReceiver)!=0)
                {slot.SourceMask|=TrackSourceMask.CloseProtectionRadar;slot.LastCloseProtectionTime=measurement.Timestamp;}
                slot.InterferenceScore = (measurement.Flags & MeasurementFlags.Jammed) != 0 ? 1f : 0f;
                if (firstAssociationThisTick)
                {
                    slot.Lifecycle.OnHit();
                }

                slot.Confidence = ComputeConfidence(slot.Lifecycle.State, measurement.SnrDb, slot.InterferenceScore);
                slot.SourceEvidence.Record((measurement.Flags & MeasurementFlags.CloseProtectionReceiver)!=0 ? TrackSourceMask.CloseProtectionRadar : TrackSourceMask.Radar,slot.Confidence,measurement.Timestamp);
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

            trackSections.Next(ACunicombat.Simulation.Core.SimulationCostProbe.SystemId.TrackLifecycle);
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
                    // Absence during sparse search is not negative evidence. Only count
                    // executed dwells containing the whole 3-sigma position envelope.
                    if(fullyObserved[i] && slot.LastRadarSnrDb>=20)slot.CoveredMisses++;
                    if(slot.ObjectClass==TrackObjectClass.Missile && slot.CoveredMisses>=2 &&
                        trackAge>=UnobservedMissAgeSeconds && !slot.AssociationClosed)
                    {
                        slot.AssociationClosed=true;
                        Emit(new RadarTrackDiagnostic{Kind=RadarTrackDiagnosticKind.LifecycleChanged,Timestamp=simTime,
                            TrackId=slot.Id,ObjectClass=slot.ObjectClass,TrackPosition=slot.Filter.Position,
                            TrackAgeSeconds=trackAge,Reason="repeated_covered_misses_end_association"});
                    }
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
        
                trackSections.End();
                completed=true;
            }
            finally {ReplayDepth--;if(completed&&replay!=null)RadarReplayJournal.Emit(this,replay);}
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
                    if (!slots[trackIndex].Active || fullyObserved[trackIndex])
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

                    double alignment=math.clamp(math.dot(relative/range,beamDirection),-1,1);
                    observed[trackIndex] |= alignment>=halfBeamCos;
                    var filter=slots[trackIndex].Filter;
                    double envelope=3*math.sqrt(math.max(0,filter.P[0,0]+filter.P[1,1]+filter.P[2,2]));
                    bool insideRange=dwell.MaxRangeMeters<=0 || range+envelope<=dwell.MaxRangeMeters;
                    fullyObserved[trackIndex]=insideRange && envelope<range &&
                        math.acos(alignment)+math.asin(math.min(1,envelope/range))<=dwell.BeamWidth*.5;
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
                if (!slots[i].Active || slots[i].AssociationClosed || slots[i].Lifecycle.State == TrackLifecycle.Dropped)
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
                if (!slots[i].Active || slots[i].AssociationClosed || slots[i].Lifecycle.State == TrackLifecycle.Dropped)
                {
                    continue;
                }

                destination[count++] = BuildSnapshot(in slots[i]);
            }

            return count;
        }

        // Caller must authenticate and uniquely associate the independent terminal report.
        // Replayed reports cannot reduce covariance again, and telemetry never invents radar hits.
        public bool TryRefineMissileTelemetry(TrackId id,double3 position,double3 velocity,double reportTime,double now)
        {
            int index=id.Value.Index;double age=now-reportTime;
            if(!id.IsValid||(uint)index>=activeCapacity||age<0||age>2.5||!math.all(math.isfinite(position))||!math.all(math.isfinite(velocity)))return false;
            var slot=slots[index];
            if(!slot.Active||!slot.Id.Equals(id)||slot.AssociationClosed||slot.ObjectClass!=TrackObjectClass.Missile||
               (slot.SourceEvidence.DataLink.Valid&&slot.SourceEvidence.DataLink.Timestamp>=reportTime))return false;
            var candidate=slot.Filter;
            if(now<candidate.Time-1e-6)return false;
            // Refine the existing filter epoch; do not advance it past the next
            // completed-physics radar sample. Public snapshots predict to render time.
            age=candidate.Time-reportTime;
            if(age<0)return false;
            Matrix6 observation=default;double sigma=3+300*age*age;
            for(int axis=0;axis<3;axis++)observation[axis,axis]=sigma*sigma/3;
            if(!Cmkf.TryCalculatePositionInnovationNis(candidate,position+velocity*age,observation,out var nis)||nis>25)return false;
            if(!Cmkf.TryUpdatePosition(ref candidate,position+velocity*age,observation))return false;
            double velocitySigma=3+600*age;
            for(int axis=0;axis<3;axis++)
            {var direction=double3.zero;direction[axis]=1;Cmkf.UpdateRadialVelocity(ref candidate,(float3)direction,(float)velocity[axis],(float)(velocitySigma*velocitySigma/3));}
            if(!Cmkf.Healthy(candidate))return false;
            slot.Filter=candidate;slot.LastMeasuredFilter=candidate;
            slot.SourceMask|=TrackSourceMask.DataLink;slot.SourceEvidence.Record(TrackSourceMask.DataLink,1,reportTime);
            slots[index]=slot;
            Emit(new RadarTrackDiagnostic{Kind=RadarTrackDiagnosticKind.TelemetryRefinementAccepted,Timestamp=now,TrackId=id,ObjectClass=TrackObjectClass.Missile,TrackPosition=candidate.Position,TrackAgeSeconds=now-candidate.Time,NormalizedDistanceSquared=nis,MeasurementIndex=-1,Reason="authenticated_terminal_report"});
            RadarReplayJournal.Emit(this,new RadarReplayOperation{kind="telemetry",track=id.Packed,position=position,velocity=velocity,reportTime=reportTime,time=now});return true;
        }

        /// <summary>
        /// Refines an existing confirmed track with an anonymous monocular bearing.
        /// The prior range supplies geometry only; EO neither creates a track nor
        /// supplies an independent range measurement. Ambiguous candidates are rejected.
        /// </summary>
        public bool TryRefineConfirmedTrackWithBearing(
            in ElectroOpticalObservation observation,
            double3 sensorPosition,
            double simTime,
            out double normalizedAngle,
            double3 sensorVelocity = default)
        {
            normalizedAngle = double.PositiveInfinity;
            if (observation.Timestamp > simTime + 1e-6 || simTime - observation.Timestamp > 1 ||
                observation.ClassificationConfidence < .25f || observation.AngleVar <= 0 ||
                !math.all(math.isfinite(observation.Direction)) ||
                math.lengthsq(observation.Direction) < .5f) return false;
            int best = -1;
            double bestAngle = double.PositiveInfinity, secondAngle = double.PositiveInfinity;
            for (int i = 0; i < slots.Length; i++)
            {
                if (!slots[i].Active || slots[i].AssociationClosed ||
                    slots[i].Lifecycle.State != TrackLifecycle.Confirmed ||
                    slots[i].ObjectClass != observation.ObjectClass) continue;
                var predicted = slots[i].Filter;
                Cmkf.PredictCv(ref predicted, math.max(0, observation.Timestamp - predicted.Time),
                    observation.ObjectClass == TrackObjectClass.Missile ? MissilePositionProcessNoise : MissilePositionProcessNoise,
                    observation.ObjectClass == TrackObjectClass.Missile ? MissileVelocityProcessNoise : AircraftVelocityProcessNoise);
                var relative = predicted.Position - sensorPosition;
                double range = math.length(relative);
                if (range <= 1) continue;
                double angle = math.acos(math.clamp(math.dot(math.normalizesafe(relative),
                    math.normalizesafe(observation.Direction)), -1, 1));
                double gate = math.max(math.radians(.5), 4 * math.sqrt(observation.AngleVar));
                if (angle > gate) continue;
                if (angle < bestAngle)
                {
                    secondAngle = bestAngle;
                    bestAngle = angle;
                    best = i;
                }
                else if (angle < secondAngle) secondAngle = angle;
            }
            if (best < 0 || secondAngle - bestAngle < math.radians(.25)) return false;
            normalizedAngle = bestAngle;
            var slot = slots[best];
            var candidate = slot.Filter;
            Cmkf.PredictCv(ref candidate, math.max(0, observation.Timestamp - candidate.Time),
                observation.ObjectClass == TrackObjectClass.Missile ? MissilePositionProcessNoise : MissilePositionProcessNoise,
                observation.ObjectClass == TrackObjectClass.Missile ? MissileVelocityProcessNoise : AircraftVelocityProcessNoise);
            double priorRange = math.distance(candidate.Position, sensorPosition);
            if (!Cmkf.TryUpdateRadarPosition(ref candidate, sensorPosition, (float)priorRange,
                    observation.Direction, 0, observation.AngleVar)) return false;
            if (observation.HasLosRate && observation.LosRateVar > 0 &&
                math.all(math.isfinite(observation.LosRate)))
            {
                var los = math.normalizesafe(observation.Direction, new float3(0, 0, 1));
                var tangentA = math.normalizesafe(math.cross(los,
                    math.abs(los.y) < .9f ? new float3(0, 1, 0) : new float3(1, 0, 0)));
                var tangentB = math.cross(los, tangentA);
                var transverse = sensorVelocity +
                    (double3)math.cross(observation.LosRate, los * (float)priorRange);
                double rangeVariance = 0;
                for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                    rangeVariance += los[row] * candidate.P[row, col] * los[col];
                float variance = (float)math.max(.1,
                    priorRange * priorRange * observation.LosRateVar +
                    math.max(0, rangeVariance) * math.lengthsq(observation.LosRate));
                Cmkf.UpdateRadialVelocity(ref candidate, tangentA,
                    (float)math.dot(transverse, (double3)tangentA), variance);
                Cmkf.UpdateRadialVelocity(ref candidate, tangentB,
                    (float)math.dot(transverse, (double3)tangentB), variance);
            }
            slot.Filter = candidate;
            slot.LastMeasuredFilter = candidate;
            slot.LastUpdateTime = observation.Timestamp;
            slot.SourceMask |= TrackSourceMask.ElectroOptical;
            slot.SourceEvidence.Record(TrackSourceMask.ElectroOptical,
                observation.ClassificationConfidence, observation.Timestamp);
            slots[best] = slot;
            return true;
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
            if (slot.AssociationClosed || slot.Lifecycle.State != TrackLifecycle.Confirmed ||
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
            slot.LastMeasuredFilter = slot.Filter;
            slot.SourceMask |= TrackSourceMask.LaserRangefinder;
            slot.SourceEvidence.Record(TrackSourceMask.LaserRangefinder,math.saturate(1f-math.exp(-math.max(0,observation.SignalMarginDb)/10f)),observation.Timestamp);
            slots[index] = slot;
            RadarReplayJournal.Emit(this,new RadarReplayOperation{kind="laser",track=observation.CuedTrackId.Packed,laserSensor=observation.SensorId.Packed,laser=observation,observer=sensorPosition,time=simTime});return true;
        }

        /// <summary>
        /// <paramref name="maneuverAcceleration"/> 为 0 时是原来的常规 5σ 门限；大于 0 时额外
        /// 计入"一次该量级机动可以解释多少偏差"，用于常规门限失败后的二级挽救，避免同一个
        /// 目标在一次 QB 之后被拆成两条航迹。
        /// </summary>
        private bool PendingResolvedReturnHasRegularAssociation(NativeList<Measurement> measurements,
            int firstOrder,in Measurement current,int reservedSlot,double3 observer,double3 observerVelocity)
        {
            if(current.DwellSequence==0)return false;
            for(int order=firstOrder;order<measurementOrder.Count;order++)
            {
                var other=measurements[measurementOrder[order]];
                if(other.Timestamp!=current.Timestamp || other.DwellSequence!=current.DwellSequence ||
                    !other.SensorId.Equals(current.SensorId) || !float.IsFinite(other.Range) || other.Range<=0 ||
                    !math.all(math.isfinite(other.Direction)))continue;
                Cmkf.ConvertMeasurement(observer,other.Range,other.Direction,math.max(other.RangeVar,1e-3f),
                    math.max(other.AngleVar,1e-9f),out var position,out var covariance);
                if(FindAssociation(in other,observer,observerVelocity,position,in covariance,other.ObjectClass,
                    other.Timestamp,0,out _)==reservedSlot)return true;
            }
            return false;
        }

        private struct BatchPlan {public int Slot;public bool Maneuver,Ambiguous;public AssociationSearchStats Stats;}
        private readonly System.Collections.Generic.Dictionary<int,BatchPlan> batchPlans=new();
        private readonly System.Collections.Generic.List<int> batchRows=new System.Collections.Generic.List<int>();
        private double[,] batchCosts=new double[0,0];private AssociationSearchStats[,] batchStats=new AssociationSearchStats[0,0];private bool[,] batchManeuvers=new bool[0,0];
        private void PrepareResolvedBatch(NativeList<Measurement> measurements,int first,double3 observer,double3 observerVelocity)
        {
            var seed=measurements[first];if(seed.DwellSequence==0||batchPlans.ContainsKey(first))return;
            var rows=batchRows;rows.Clear();
            foreach(int index in measurementOrder)
            {var m=measurements[index];if(m.SensorId.Equals(seed.SensorId)&&m.DwellSequence==seed.DwellSequence&&float.IsFinite(m.Range)&&m.Range>0&&math.all(math.isfinite(m.Direction))&&math.lengthsq(m.Direction)>1e-12f&&float.IsFinite(m.RangeVar)&&m.RangeVar>=0&&float.IsFinite(m.AngleVar)&&m.AngleVar>=0)rows.Add(index);}
            if(rows.Count==0)return;
            // Reused per manager (one manager per observer, never shared across threads).
            if(batchCosts.GetLength(0)<rows.Count||batchCosts.GetLength(1)<activeCapacity)
            {int r=Math.Max(rows.Count,batchCosts.GetLength(0)),c=Math.Max(activeCapacity,batchCosts.GetLength(1));batchCosts=new double[r,c];batchStats=new AssociationSearchStats[r,c];batchManeuvers=new bool[r,c];}
            var costs=batchCosts;var stats=batchStats;var maneuvers=batchManeuvers;
            for(int row=0;row<rows.Count;row++)
            {
                var m=measurements[rows[row]];
                Cmkf.ConvertMeasurement(observer,m.Range,m.Direction,math.max(m.RangeVar,1e-3f),math.max(m.AngleVar,1e-9f),out var position,out var noise);
                for(int slot=0;slot<activeCapacity;slot++)
                {
                    costs[row,slot]=OneToOneAssignment.Rejected;maneuvers[row,slot]=false;stats[row,slot]=default;
                    if(OutsideAnyGate(slot,in m,observer))continue;
                    int found=FindAssociation(in m,observer,observerVelocity,position,in noise,m.ObjectClass,m.Timestamp,0,out var st,slot);
                    if(found<0)
                    {
                        found=FindAssociation(in m,observer,observerVelocity,position,in noise,m.ObjectClass,m.Timestamp,ResolveManeuverAcceleration(m.ObjectClass),out st,slot);
                        maneuvers[row,slot]=found>=0;
                    }
                    if(found<0)continue;
                    stats[row,slot]=st;costs[row,slot]=st.BestAssociationCost+(maneuvers[row,slot]?1000:0);
                }
            }
            var chosen=OneToOneAssignment.Solve(costs,rows.Count,activeCapacity,2*Math.Log(10),out _,out var ambiguous,out var margins);
            for(int row=0;row<rows.Count;row++)
            {
                int slot=chosen[row];var failed=new AssociationSearchStats{BestNormalizedDistanceSquared=double.PositiveInfinity,BestGateSquared=AssociationGateSquared};
                if(slot<0&&!ambiguous[row])
                {
                    var missing=measurements[rows[row]];
                    Cmkf.ConvertMeasurement(observer,missing.Range,missing.Direction,math.max(missing.RangeVar,1e-3f),math.max(missing.AngleVar,1e-9f),out var p,out var covariance);
                    FindAssociation(in missing,observer,observerVelocity,p,in covariance,missing.ObjectClass,missing.Timestamp,0,out var regular);
                    FindAssociation(in missing,observer,observerVelocity,p,in covariance,missing.ObjectClass,missing.Timestamp,ResolveManeuverAcceleration(missing.ObjectClass),out var maneuver);
                    failed=CombineAssociationStats(in regular,in maneuver);
                }
                batchPlans[rows[row]]=new BatchPlan{Slot=slot,Ambiguous=ambiguous[row],Maneuver=slot>=0&&maneuvers[row,slot],Stats=slot>=0?stats[row,slot]:failed};
                if(RadarReplayJournal.BatchEnabled)
                {
                    var ids=new System.Collections.Generic.List<uint>();var values=new System.Collections.Generic.List<double>();
                    for(int c=0;c<activeCapacity;c++)if(costs[row,c]<OneToOneAssignment.Rejected){ids.Add(slots[c].Id.Packed);values.Add(costs[row,c]);}
                    var m=measurements[rows[row]];
                    RadarReplayJournal.EmitBatch(new RadarBatchDecision{manager=ReplayIdentity,time=m.Timestamp,sensor=m.SensorId.Packed,dwell=m.DwellSequence,selected=slot>=0?slots[slot].Id.Packed:0,margin=margins[row],score=slot>=0?costs[row,slot]:double.PositiveInfinity,candidates=ids.ToArray(),costs=values.ToArray()});
                }
            }
        }

        // Conservative pre-gate for the batch plan: true only when the pair would fail the
        // position gate of both the regular and the manoeuvre search. NIS >= |v|^2/trace(S)
        // and |v|^2 >= dr^2 + r^2 (pi/4)^2 sin^2(bearing offset); half of that bound is used.
        private bool OutsideAnyGate(int slot,in Measurement m,double3 observer)
        {
            ref readonly var track=ref slots[slot];
            if(!track.Active)return true;
            ref readonly var filter=ref track.Filter;
            var relative=filter.Position-observer;double range=math.max(1,math.length(relative));
            var n=math.normalizesafe(relative,new double3(0,0,1));var d=math.normalizesafe((double3)m.Direction,n);
            double forward=math.dot(d,n),offset=math.max(0,math.dot(d,d)-forward*forward),dr=m.Range-range;
            double lower=dr*dr+.6168*range*range*offset;
            double positionTrace=math.max(0,filter.P[0,0]+filter.P[1,1]+filter.P[2,2]);
            double angular=math.max(0,(double)m.AngleVar)+positionTrace/(2*range*range);
            double radial=math.max(1e-9,(double)m.RangeVar)+range*range*angular*angular;
            double window=math.max(math.max(0,m.Timestamp-track.LastUpdateTime),ResolveManeuverResponseSeconds(m.ObjectClass));
            double manoeuvre=ResolveManeuverAcceleration(m.ObjectClass)*window*window;
            double trace=positionTrace+radial+2*range*range*math.max(1e-12,(double)m.AngleVar)+3e-9+3*manoeuvre*manoeuvre;
            return .5*lower>AssociationGateSquared*trace;
        }

        private int FindAssociation(
            in Measurement measurement, double3 observerPosition, double3 observerVelocity,
            double3 position,
            in Matrix6 measurementCovariance,
            TrackObjectClass measuredObjectClass,
            double measurementTime,
            double maneuverAcceleration,
            out AssociationSearchStats stats, int requiredSlot = -1, bool onlyNewlyAllocated = false)
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
                if ((requiredSlot>=0&&i!=requiredSlot)||(onlyNewlyAllocated&&!allocatedThisTick[i])||!slots[i].Active)
                {
                    continue;
                }

                if(slots[i].AssociationClosed){stats.StaleRejectedCount++;continue;}
                // Classified tracks may reacquire throughout their retention interval, subject
                // to position and Doppler gates. A separate one-second cutoff created duplicate
                // aircraft and missiles after sparse long-range revisits. Unknowns stay conservative.
                bool classifiedReacquisition=measuredObjectClass!=TrackObjectClass.Unknown&&measuredObjectClass==slots[i].ObjectClass;
                if (measurementTime - slots[i].LastUpdateTime >
                        (classifiedReacquisition?maximumTrackAgeSeconds:MaximumAssociationAgeSeconds) ||
                    (!classifiedReacquisition&&slots[i].Lifecycle.State == TrackLifecycle.Degraded))
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

                // Coasting covariance is not unlimited manoeuvring authority. Keep association
                // inside the reachable envelope from the last measured state, with measurement noise.
                if(slots[i].ObjectClass==TrackObjectClass.Missile && slots[i].HasRadialHistory)
                {
                    var history=slots[i];double elapsed=math.max(0,measurementTime-history.RangeTime);
                    var observerTravel=observerPosition-history.RangeObserverPosition;
                    double accelerationDisplacement=.5*MissileReachableAcceleration*elapsed*elapsed;
                    double variance=math.max(0,history.MeasuredRangeVariance)+math.max(0,measurement.RangeVar);
                    // Convexity of distance gives a lower support bound independently of
                    // unresolved transverse velocity. The reversed path gives an upper bound.
                    double lower=history.MeasuredRange+history.MeasuredRangeRate*elapsed-accelerationDisplacement-
                        math.length(observerTravel-history.RangeObserverVelocity*elapsed);
                    double lowerSigma=math.sqrt(math.max(1e-6,variance+elapsed*elapsed*history.MeasuredRangeRateVariance));
                    if(measurement.Range<lower-5*lowerSigma){stats.GateRejectedCount++;continue;}
                    if(float.IsFinite(measurement.RadialVelocity) && measurement.VelocityVar>0)
                    {
                        // A diffuse bearing is not permission to reverse a precisely measured
                        // Doppler history. Compare the two scalar range rates using the prior
                        // trajectory's LOS rotation; the new noisy bearing must not dilute it.
                        var anchor=history.RangeFilter;
                        var oldRelative=anchor.Position-history.RangeObserverPosition;
                        var newRelative=anchor.Position+anchor.Velocity*elapsed-observerPosition;
                        double oldRange=math.max(1,math.length(oldRelative)),newRange=math.max(1,math.length(newRelative));
                        var oldLos=oldRelative/oldRange;var newLos=newRelative/newRange;
                        var oldVelocity=anchor.Velocity-history.RangeObserverVelocity;
                        var newVelocity=anchor.Velocity-observerVelocity;
                        double oldRate=math.dot(oldVelocity,oldLos),newRate=math.dot(newVelocity,newLos);
                        var oldGradient=(oldVelocity-oldLos*oldRate)/oldRange;
                        var newGradient=(newVelocity-newLos*newRate)/newRange;
                        var pGradient=newGradient-oldGradient;
                        var vGradient=newLos-oldLos+elapsed*newGradient;
                        double rateVariance=history.MeasuredRangeRateVariance+measurement.VelocityVar;
                        for(int row=0;row<3;row++)for(int col=0;col<3;col++)
                            rateVariance+=pGradient[row]*anchor.P[row,col]*pGradient[col]
                                +vGradient[row]*anchor.P[row+3,col+3]*vGradient[col]
                                +2*pGradient[row]*anchor.P[row,col+3]*vGradient[col];
                        // Unobserved acceleration can also rotate the LOS, especially on a
                        // genuine fly-by. Include that geometric support instead of clamping
                        // incoming/outgoing signs or banning sparse revisits.
                        double directionAllowance=accelerationDisplacement>=newRange?2:
                            2*math.sin(.5*math.asin(accelerationDisplacement/newRange));
                        double rateAllowance=MissileReachableAcceleration*elapsed+
                            directionAllowance*math.length(newVelocity)+5*math.sqrt(math.max(1e-6,rateVariance));
                        if(math.abs(measurement.RadialVelocity-(history.MeasuredRangeRate+newRate-oldRate))>rateAllowance)
                        {stats.GateRejectedCount++;continue;}
                        double upper=history.MeasuredRange+measurement.RadialVelocity*elapsed+accelerationDisplacement+
                            math.length(observerTravel-observerVelocity*elapsed);
                        double upperSigma=math.sqrt(math.max(1e-6,variance+elapsed*elapsed*measurement.VelocityVar));
                        if(measurement.Range>upper+5*upperSigma){stats.GateRejectedCount++;continue;}
                    }
                }
                if(slots[i].ObjectClass==TrackObjectClass.Missile && slots[i].Lifecycle.HitCount>=3)
                {
                    var anchor=slots[i].LastMeasuredFilter;
                    double dt=math.max(0,measurementTime-anchor.Time);
                    var expected=anchor.Position+anchor.Velocity*dt;
                    double variance=0;
                    for(int axis=0;axis<3;axis++)variance+=anchor.P[axis,axis]+dt*dt*anchor.P[axis+3,axis+3]+measurementCovariance[axis,axis];
                    double reach=5*math.sqrt(math.max(1,variance))+.5*MissileReachableAcceleration*dt*dt;
                    if(math.distance(position,expected)>reach){stats.GateRejectedCount++;continue;}
                    if(float.IsFinite(measurement.RadialVelocity)&&measurement.VelocityVar>0)
                    {
                        var los=math.normalizesafe((double3)measurement.Direction,new double3(0,0,1));
                        var relative=anchor.Velocity-observerVelocity;double doppler=math.dot(relative,los),v=measurement.VelocityVar;
                        for(int row=0;row<3;row++)for(int col=0;col<3;col++)v+=los[row]*anchor.P[row+3,col+3]*los[col];
                        v+=measurement.AngleVar*math.max(0,math.lengthsq(relative)-doppler*doppler)
                            +doppler*doppler*measurement.AngleVar*measurement.AngleVar;
                        if(math.abs(measurement.RadialVelocity-doppler)>5*math.sqrt(math.max(1,v))+MissileReachableAcceleration*dt){stats.GateRejectedCount++;continue;}
                    }
                    if((measurement.Flags&MeasurementFlags.CloseProtectionReceiver)!=0 &&
                        float.IsFinite(measurement.RadialVelocity) && measurement.LosRateVar>0 &&
                        math.all(math.isfinite(measurement.LosRate)))
                    {
                        // Near a side-on crossing, radial speed alone can hide a complete
                        // reversal. Apply the same existing reachability bound to the
                        // full velocity actually measured by the local LOS-rate receiver.
                        var los=math.normalizesafe((double3)measurement.Direction,new double3(0,0,1));
                        var measuredVelocity=observerVelocity+los*measurement.RadialVelocity+
                            math.cross((double3)measurement.LosRate,los)*measurement.Range;
                        double velocityVariance=anchor.P[3,3]+anchor.P[4,4]+anchor.P[5,5]+
                            math.max(0,measurement.VelocityVar)+2*measurement.Range*measurement.Range*measurement.LosRateVar+
                            math.max(0,measurement.RangeVar)*math.lengthsq(measurement.LosRate)+
                            2*measurement.AngleVar*math.lengthsq(measuredVelocity-observerVelocity);
                        double reachableSpeedChange=5*math.sqrt(math.max(1,velocityVariance))+MissileReachableAcceleration*dt;
                        if(math.distance(measuredVelocity,anchor.Velocity)>reachableSpeedChange)
                        {stats.GateRejectedCount++;continue;}
                    }
                }
                if(measurement.DwellSequence!=0&&dwellAssignments.Contains((measurement.SensorId.Packed,measurement.DwellSequence,i)))continue;
                stats.CandidateCount++;

                // 机动门限项：机体的 CV 速度估计可能在整个 QB 窗口内持续落后。
                // 因此位置余量必须覆盖完整响应窗口，而不能随着最新一次量测间隔缩回
                // 40 ms；导弹也保留一个更短的末段修正响应窗口。
                var maneuverVariance = 0.0;
                if (maneuverAcceleration > 0.0)
                {
                    var unobservedSeconds = math.max(
                        0.0,
                        measurementTime - slots[i].LastUpdateTime);
                    var responseSeconds = ResolveManeuverResponseSeconds(
                        measuredObjectClass);
                    var maneuverWindow = math.max(
                        unobservedSeconds,
                        responseSeconds);
                    var maneuverOffset =
                        maneuverAcceleration * maneuverWindow * maneuverWindow;
                    maneuverVariance = maneuverOffset * maneuverOffset;
                }

                Cmkf.BuildSphericalInnovation(slots[i].Filter,observerPosition,measurement.Range,measurement.Direction,
                    measurement.RangeVar,measurement.AngleVar,out var associationPosition,out var associationCovariance);
                for(int axis=0;axis<3;axis++)associationCovariance[axis,axis]+=maneuverVariance;
                if(!Cmkf.TryCalculatePositionInnovationNis(slots[i].Filter,associationPosition,associationCovariance,out var normalizedDistance))continue;

                if(float.IsFinite(measurement.RadialVelocity)&&measurement.VelocityVar>0)
                {
                    // Doppler is measured along the actual LOS, independently of bearing noise.
                    // Projecting precise velocity onto a noisy sidelobe bearing invents a
                    // radial residual and splits an already precise track into a ghost.
                    var predictedRelative=slots[i].Filter.Position-observerPosition;
                    var los=math.normalizesafe(predictedRelative,new double3(0,0,1));
                    var relativeVelocity=slots[i].Filter.Velocity-observerVelocity;
                    double predicted=math.dot(relativeVelocity,los),variance=measurement.VelocityVar;
                    for(int row=0;row<3;row++)for(int col=0;col<3;col++)variance+=los[row]*slots[i].Filter.P[row+3,col+3]*los[col];
                    var positionJacobian=(relativeVelocity-los*predicted)/math.max(1,math.length(predictedRelative));
                    for(int row=0;row<3;row++)for(int col=0;col<3;col++)
                        variance+=positionJacobian[row]*slots[i].Filter.P[row,col]*positionJacobian[col]
                            +2*positionJacobian[row]*slots[i].Filter.P[row,col+3]*los[col];
                    variance+=measurement.AngleVar*math.max(0,math.lengthsq(relativeVelocity)-predicted*predicted);
                    double velocityAllowance=maneuverAcceleration*math.max(ResolveManeuverResponseSeconds(measuredObjectClass),measurementTime-slots[i].LastUpdateTime);
                    variance+=velocityAllowance*velocityAllowance;
                    double residual=measurement.RadialVelocity-predicted;
                    if(residual*residual/math.max(variance,1e-6)>AssociationGateSquared)continue;
                }

                double velocityNormalization=0;
                // A close-protection observation also measures transverse motion. Doppler
                // alone cannot separate crossing salvo rounds with similar radial speed.
                if((measurement.Flags&MeasurementFlags.CloseProtectionReceiver)!=0 && measurement.LosRateVar>0 &&
                    math.all(math.isfinite(measurement.LosRate)) && float.IsFinite(measurement.RadialVelocity))
                {
                    var los=math.normalizesafe((double3)measurement.Direction,new double3(0,0,1));
                    var observedVelocity=observerVelocity+los*measurement.RadialVelocity+math.cross((double3)measurement.LosRate,los)*measurement.Range;
                    var velocityPrior=new FilterState{Position=slots[i].Filter.Velocity};Matrix6 velocityNoise=default;
                    double transverse=math.max(1,measurement.Range*measurement.Range*measurement.LosRateVar+measurement.RangeVar*math.lengthsq(measurement.LosRate));
                    double angularError=measurement.AngleVar*math.lengthsq(observedVelocity-observerVelocity);
                    double response=maneuverAcceleration*math.max(ResolveManeuverResponseSeconds(measuredObjectClass),measurementTime-slots[i].LastUpdateTime);
                    for(int row=0;row<3;row++)for(int col=0;col<3;col++)
                    {
                        velocityPrior.P[row,col]=slots[i].Filter.P[row+3,col+3];
                        velocityNoise[row,col]=measurement.VelocityVar*los[row]*los[col]+transverse*((row==col?1:0)-los[row]*los[col]);
                        if(row==col)velocityNoise[row,col]+=angularError+response*response;
                    }
                    if(!Cmkf.TryCalculatePositionInnovationNis(velocityPrior,observedVelocity,velocityNoise,out var velocityNis)||velocityNis>AssociationGateSquared){stats.GateRejectedCount++;continue;}
                    normalizedDistance+=velocityNis;velocityNormalization=Cmkf.PositionAssociationCost(velocityPrior,velocityNoise,0);
                }

                // A second return in the same tick must use the same statistical gate as the
                // first one. Tightening it to 2.5 sigma split overlapping dwells into new tracks:
                // the rejected return was still inside the normal 5-sigma uncertainty volume.
                // Targets that cannot be resolved inside that volume should remain one track
                // until their measurements separate; inventing a second track is false precision.
                var gate = AssociationGateSquared;
                if(normalizedDistance<gate && (!associated[i] || measurement.Timestamp>slots[i].LastUpdateTime+1e-6))
                {
                    // Association must survive the actual joint update. Previously a
                    // position hit could reject Doppler silently yet refresh identity
                    // and lifetime, allowing an incoming track to follow an outgoing round.
                    var preview=slots[i];
                    if(maneuverAcceleration>0)InflateForManeuver(ref preview,measurementTime,maneuverAcceleration,
                        ResolveManeuverResponseSeconds(measuredObjectClass));
                    if(!Cmkf.TryUpdateRadarMeasurement(ref preview.Filter,observerPosition,observerVelocity,
                        measurement.Range,measurement.Direction,measurement.RangeVar,measurement.AngleVar,
                        measurement.RadialVelocity,measurement.VelocityVar))
                    {stats.GateRejectedCount++;continue;}
                }
                if (bestIndex < 0 && normalizedDistance < stats.BestNormalizedDistanceSquared)
                {
                    stats.BestNormalizedDistanceSquared = normalizedDistance;
                    stats.BestGateSquared = gate;
                }
                var associationCost=Cmkf.PositionAssociationCost(slots[i].Filter,associationCovariance,normalizedDistance)+velocityNormalization;
                if (normalizedDistance >= gate || associationCost >= bestDistance)
                {
                    if (normalizedDistance >= gate)
                    {
                        stats.GateRejectedCount++;
                    }
                    continue;
                }

                bestDistance = associationCost;
                stats.BestAssociationCost=associationCost;
                bestIndex = i;
                stats.BestNormalizedDistanceSquared = normalizedDistance;
                stats.BestGateSquared = gate;
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
        /// 导弹档的过程噪声本身已经很宽，这里给更高量级覆盖末段修正。
        /// </summary>
        private static double ResolveManeuverAcceleration(
            TrackObjectClass measuredObjectClass)
        {
            return measuredObjectClass == TrackObjectClass.Missile
                ? MissileManeuverAcceleration
                : VehicleManeuverAcceleration;
        }

        private static double ResolveManeuverResponseSeconds(
            TrackObjectClass measuredObjectClass)
        {
            return measuredObjectClass switch
            {
                TrackObjectClass.AirVehicle =>
                    VehicleManeuverResponseSeconds,
                TrackObjectClass.Missile =>
                    MissileManeuverResponseSeconds,
                _ => 0.0,
            };
        }

        /// <summary>
        /// 经二级门限关联后放开速度协方差。机体至少保留一个 QB 响应窗口的
        /// a·Δt 速度不确定度，滤波器才能在这一拍跟上模型滞后，而不是下一拍
        /// 再次掉出门限；导弹使用较短的末段修正响应窗口。
        /// </summary>
        private static void InflateForManeuver(
            ref TrackSlot slot,
            double measurementTime,
            double maneuverAcceleration,
            double responseSeconds)
        {
            var unobservedSeconds = math.max(
                0.0,
                measurementTime - slot.LastUpdateTime);
            var velocityUncertainty = maneuverAcceleration *
                                      math.max(unobservedSeconds, responseSeconds);
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
            var slotIndex = FindFreeSlot(timestamp);
            if (slotIndex < 0)
            {
                slotIndex = FindCapacityReplacement(
                    measurement.ObjectClass,
                    timestamp);
                if (slotIndex < 0)
                {
                    return -1;
                }

                var replaced = slots[slotIndex];
                Emit(new RadarTrackDiagnostic
                {
                    Kind = RadarTrackDiagnosticKind.TrackReleased,
                    Timestamp = timestamp,
                    MeasurementIndex = -1,
                    TrackId = replaced.Id,
                    ObjectClass = replaced.ObjectClass,
                    PreviousLifecycle = replaced.Lifecycle.State,
                    Lifecycle = TrackLifecycle.Dropped,
                    TrackPosition = replaced.Filter.Position,
                    TrackAgeSeconds = math.max(
                        0.0,
                        timestamp - replaced.LastUpdateTime),
                    CoastCount = replaced.Lifecycle.MissCount,
                    Reason = "capacity_replacement",
                });
                ReleaseTrack(slotIndex);
            }

            Matrix6 covariance = default;
            for(int row=0;row<3;row++)for(int column=0;column<3;column++)covariance[row,column]=measurementCovariance[row,column];
            for (var axis = 0; axis < 3; axis++)
            {
                covariance[axis, axis] = math.max(
                    measurementCovariance[axis, axis],
                    1.0);
                covariance[axis + 3, axis + 3] =
                    math.pow(measurement.ObjectClass==TrackObjectClass.Missile
                        ? InitialMissileTransverseSigmaMetersPerSecond : InitialVelocitySigmaMetersPerSecond,2);
            }

            var lifecycle = TrackLifecycleMachine.Create();
            slots[slotIndex] = new TrackSlot
            {
                Active = true,
                Id = new TrackId(
                    new Handle(slotIndex, generations[slotIndex])),
                Filter = new FilterState
                {
                    Position = position,
                    Velocity = double3.zero,
                    P = covariance,
                    Time = timestamp,
                },
                Lifecycle = lifecycle,
                LastUpdateTime = timestamp,
                FirstObservationTime = timestamp,
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
            associated[slotIndex] = false;
            activeCount++;
            return slotIndex;
        }

        private int FindFreeSlot(double timestamp)
        {
            for (var i = 0; i < activeCapacity; i++)
            {
                if (!slots[i].Active && generationRetiredUntil[i,generations[i]] <= timestamp)
                {
                    return i;
                }
            }

            return -1;
        }

        private int FindCapacityReplacement(
            TrackObjectClass incomingClass,
            double timestamp)
        {
            var bestIndex = -1;
            var bestRank = int.MaxValue;
            var bestAge = double.NegativeInfinity;
            for (var i = 0; i < activeCapacity; i++)
            {
                var slot = slots[i];
                if (!slot.Active)
                {
                    continue;
                }

                // A new plot must get a chance to collect its confirmation hits. Otherwise
                // a dense volley evicts/reallocates the same tentative slot repeatedly in
                // ONE sensor tick, making both track identity and screen position thrash.
                var age = math.max(0.0, timestamp - slot.LastUpdateTime);
                bool higherPriorityPlatform=incomingClass==TrackObjectClass.AirVehicle && slot.ObjectClass!=TrackObjectClass.AirVehicle;
                if (!slot.AssociationClosed && !higherPriorityPlatform && (associated[i] || age < 0.5)) continue;
                byte nextGeneration=(byte)(generations[i]+1);
                if(nextGeneration==0)nextGeneration=1;
                if(generationRetiredUntil[i,nextGeneration]>timestamp)continue;

                // Non-platform returns must never evict the platform track that fire control
                // and STT depend on. A fresh platform return may, however, reclaim one of
                // their slots when the processor is completely saturated.
                if (incomingClass != TrackObjectClass.AirVehicle &&
                    slot.ObjectClass == TrackObjectClass.AirVehicle)
                {
                    continue;
                }

                var rank = slot.AssociationClosed ? -1 : slot.Lifecycle.State switch
                {
                    TrackLifecycle.Degraded => 0,
                    TrackLifecycle.Coasting => 1,
                    TrackLifecycle.Tentative => 2,
                    TrackLifecycle.Confirmed
                        when incomingClass == TrackObjectClass.AirVehicle &&
                             slot.ObjectClass != TrackObjectClass.AirVehicle => 3,
                    _ => int.MaxValue,
                };
                if (rank == int.MaxValue)
                {
                    continue;
                }

                if (rank < bestRank ||
                    (rank == bestRank && age > bestAge))
                {
                    bestRank = rank;
                    bestAge = age;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        private void ReleaseTrack(int index)
        {
            generationRetiredUntil[index,generations[index]]=slots[index].Filter.Time + math.max(60,maximumTrackAgeSeconds+5);
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
                AssociationClosed = slot.AssociationClosed,
                Position = slot.Filter.Position,
                Velocity = slot.Filter.Velocity,
                Confidence = slot.Confidence,
                ObjectClass = slot.ObjectClass,
                Classification = slot.ClassificationConfidence,
                IdentityConfidence = 0f,
                Affiliation = TrackAffiliation.Unknown,
                LastUpdateTime = slot.LastUpdateTime,
                FirstObservationTime = slot.FirstObservationTime,
                LastCloseProtectionTime = slot.LastCloseProtectionTime,
                LastPredictTime = slot.Filter.Time,
                CoastCount = slot.Lifecycle.MissCount,
                HitCount = slot.Lifecycle.HitCount,
                SourceMask = slot.SourceMask,
                SourceEvidence = slot.SourceEvidence,
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
