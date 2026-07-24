using ACunicombat.Sensing.Contracts;
using ACunicombat.Sensors;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.Kinematics;
using ACunicombat.Simulation.World;
using ACunicombat.Tracking;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace ACunicombat.Simulation.Driver
{
    /// <summary>
    /// Scene-level owner of simulation clocks, world snapshots, radar detections and local tracks.
    /// Runtime consumers register state here but can only read fire-control-safe TrackSnapshot values.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class SimulationDriver : MonoBehaviour
    {
        public const double MotionStepSeconds = 0.01;
        public const double SensorStepSeconds = 0.04;
        public const double FusionStepSeconds = 0.04;

        [SerializeField] private int maxMotionCatchUpTicks = 8;
        [SerializeField] private int maxSensorCatchUpTicks = 2;
        [SerializeField] private uint sessionSeed = 1;

        private FixedTickClock motionClock;
        private FixedTickClock sensorClock;
        private FixedTickClock fusionClock;
        private SimWorld world;
        private SpatialHashGrid grid;
        private DwellScheduler dwellScheduler;
        private RadarSensorSystem radar;
        private RadarTrackManager trackManager;
        private SimEntityId trackOriginEntity;
        private SensorId trackSensorId;
        private EmitterId trackEmitterId;
        private NativeList<Measurement> trackDetectionScratch;
        private NativeList<EmissionEvent> trackEmissionScratch;

        public double SimTimeSeconds => motionClock.SimTimeSeconds;
        public long MotionOverrunTicks => motionClock.OverrunTicks;
        public int DetectionCount => radar?.Detections.Length ?? 0;
        public int TrackCount => trackManager?.Count ?? 0;
        public int TrackCapacity => RadarTrackManager.MaxTracks;
        public int ScheduledDwellCount => dwellScheduler?.Count ?? 0;
        public long DeferredDwellCount => dwellScheduler?.DeferredCount ?? 0;

        private void Awake()
        {
            motionClock = new FixedTickClock(MotionStepSeconds, maxMotionCatchUpTicks);
            sensorClock = new FixedTickClock(SensorStepSeconds, maxSensorCatchUpTicks);
            fusionClock = new FixedTickClock(FusionStepSeconds, maxSensorCatchUpTicks);
            world = new SimWorld();
            grid = new SpatialHashGrid(SimWorld.MaxEntities * 2);
            dwellScheduler = new DwellScheduler(512);
            radar = new RadarSensorSystem(world, grid, dwellScheduler) { SessionSeed = sessionSeed };
            trackManager = new RadarTrackManager();
            trackDetectionScratch =
                new NativeList<Measurement>(
                    RadarSensorSystem.DetectionRingCapacity,
                    Allocator.Persistent);
            trackEmissionScratch =
                new NativeList<EmissionEvent>(
                    1024,
                    Allocator.Persistent);
        }

        public SimEntityId RegisterEntity(in KinematicState initial) => world.Register(initial);

        public void UnregisterEntity(SimEntityId entityId) => world.Unregister(entityId);

        public bool SetEntityState(SimEntityId entityId, in KinematicState state) =>
            world.TrySetState(entityId, state);

        public void RegisterRadarSensor(in RadarSensorProfile profile)
        {
            radar.RegisterSensor(profile);
            trackOriginEntity = profile.OwnerEntityId;
            trackSensorId = profile.SensorId;
            trackEmitterId = profile.EmitterId;
        }

        public void RegisterAuxiliaryRadarSensor(
            in RadarSensorProfile profile) =>
            radar.RegisterSensor(profile);

        public void UnregisterRadarSensor(SensorId sensorId) =>
            radar.UnregisterSensor(sensorId);

        public void RegisterRadarTarget(in RadarTargetProfile profile) => radar.RegisterTarget(profile);

        public void UnregisterRadarTarget(SimEntityId entityId) => radar.UnregisterTarget(entityId);

        public void ScheduleRadarDwell(in Dwell dwell) => radar.ScheduleDwell(dwell);

        public bool TryGetTrack(TrackId trackId, out TrackSnapshot snapshot) =>
            trackManager.TryGetTrack(trackId, out snapshot);

        public bool TryGetBestTrack(out TrackSnapshot snapshot) =>
            trackManager.TryGetBestTrack(out snapshot);

        public int CopyTracks(TrackSnapshot[] destination) =>
            trackManager?.CopyTracks(destination) ?? 0;

        public int CopyDetections(Measurement[] destination)
        {
            if (destination == null || radar == null)
            {
                return 0;
            }

            var count = math.min(destination.Length, radar.Detections.Length);
            for (var i = 0; i < count; i++)
            {
                destination[i] = radar.Detections[i];
            }

            return count;
        }

        public int CopyDetections(
            SensorId sensorId,
            Measurement[] destination)
        {
            if (destination == null || radar == null)
            {
                return 0;
            }

            var count = 0;
            for (var i = 0;
                 i < radar.Detections.Length && count < destination.Length;
                 i++)
            {
                var measurement = radar.Detections[i];
                if (measurement.SensorId.Equals(sensorId))
                {
                    destination[count++] = measurement;
                }
            }

            return count;
        }

        private void FixedUpdate()
        {
            var dt = Time.fixedDeltaTime;
            var motionTicks = motionClock.Advance(dt);

            var sensorTicks = sensorClock.Advance(dt);
            for (var i = 0; i < sensorTicks; i++)
            {
                radar.Tick(sensorClock.SimTimeSeconds);
                var sensorPosition = double3.zero;
                var sensorVelocity = double3.zero;
                if (trackOriginEntity.IsValid
                    && world.TryGetReadState(trackOriginEntity, out var sensorState))
                {
                    sensorPosition = sensorState.PositionWorld;
                    sensorVelocity = sensorState.VelocityWorld;
                }

                trackDetectionScratch.Clear();
                for (var detectionIndex = 0;
                     detectionIndex < radar.Detections.Length;
                     detectionIndex++)
                {
                    var measurement =
                        radar.Detections[detectionIndex];
                    if (measurement.SensorId.Equals(trackSensorId))
                    {
                        trackDetectionScratch.Add(measurement);
                    }
                }

                trackEmissionScratch.Clear();
                for (var emissionIndex = 0;
                     emissionIndex < radar.Emissions.Length;
                     emissionIndex++)
                {
                    var emission = radar.Emissions[emissionIndex];
                    if (emission.EmitterId.Equals(trackEmitterId))
                    {
                        trackEmissionScratch.Add(emission);
                    }
                }

                trackManager.Process(
                    trackDetectionScratch,
                    trackEmissionScratch,
                    sensorPosition,
                    sensorVelocity,
                    sensorClock.SimTimeSeconds);
            }

            fusionClock.Advance(dt);
            if (motionClock.OverrunTicks > 0 && motionTicks == maxMotionCatchUpTicks)
            {
                Debug.LogWarning($"[SimDriver] motion tick overrun total={motionClock.OverrunTicks}");
            }
        }

        private void OnDestroy()
        {
            radar?.Dispose();
            if (trackDetectionScratch.IsCreated)
            {
                trackDetectionScratch.Dispose();
            }
            if (trackEmissionScratch.IsCreated)
            {
                trackEmissionScratch.Dispose();
            }
            dwellScheduler?.Dispose();
            grid?.Dispose();
            world?.Dispose();
        }
    }
}
