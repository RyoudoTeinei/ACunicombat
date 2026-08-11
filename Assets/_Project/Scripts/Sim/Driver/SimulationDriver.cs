using ACunicombat.DataLink;
using ACunicombat.FireControl;
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
    public enum CombatTelemetryDetail : byte
    {
        Events = 0,
        Detailed = 1,
    }

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
        [Header("Combat telemetry")]
        [SerializeField] private bool enableCombatTelemetry = true;
        [SerializeField] private CombatTelemetryDetail telemetryDetail =
            CombatTelemetryDetail.Events;
        [SerializeField, Min(0.04f)] private float telemetryTrackSnapshotIntervalSeconds = 0.2f;
        [SerializeField, Min(1)] private int telemetryMaximumFileSizeMegabytes = 256;

        private FixedTickClock motionClock;
        private FixedTickClock sensorClock;
        private FixedTickClock fusionClock;
        private SimWorld world;
        private SpatialHashGrid grid;
        private DwellScheduler dwellScheduler;
        private RadarSensorSystem radar;
        private RadarTrackManager trackManager;
        private EsmReceiverSystem esmReceiver;
        private EsmThreatCorrelator threatCorrelator;
        private SensorId ownshipEsmReceiverId;
        private MawSensorSystem mawSensor;
        private SimEntityId ownshipMawEntity;
        private ElectroOpticalSensorSystem electroOpticalSensor;
        private ElectroOpticalReceiverProfile electroOpticalReceiver;
        private bool electroOpticalReceiverRegistered;
        private LaserRangefinderSystem laserRangefinder;
        private DataLinkSystem dataLink;
        private SimEntityId trackOriginEntity;
        private SensorId trackSensorId;
        private EmitterId trackEmitterId;
        private NativeList<Measurement> trackDetectionScratch;
        private NativeList<EmissionEvent> trackEmissionScratch;
        private readonly TrackSnapshot[] telemetryTrackScratch =
            new TrackSnapshot[RadarTrackManager.MaxTracks];
        private readonly ThreatSnapshot[] telemetryThreatScratch =
            new ThreatSnapshot[EsmThreatCorrelator.MaxThreats];
        private CombatTelemetrySession telemetry;
        private string telemetrySource;
        private double nextTelemetryTrackSnapshotTime;
        private double nextTelemetrySensorSummaryTime;

        private bool DetailedTelemetry =>
            telemetry != null && telemetryDetail == CombatTelemetryDetail.Detailed;

        public double SimTimeSeconds => motionClock.SimTimeSeconds;
        public long MotionOverrunTicks => motionClock.OverrunTicks;
        public int DetectionCount => radar?.Detections.Length ?? 0;
        public int TrackCount => trackManager?.Count ?? 0;
        public int TrackCapacity => trackManager?.Capacity ?? RadarTrackManager.MaxTracks;
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
            esmReceiver = new EsmReceiverSystem(world) { SessionSeed = sessionSeed };
            threatCorrelator = new EsmThreatCorrelator();
            mawSensor = new MawSensorSystem(world) { SessionSeed = sessionSeed };
            electroOpticalSensor =
                new ElectroOpticalSensorSystem(world)
                {
                    SessionSeed = sessionSeed,
                };
            laserRangefinder =
                new LaserRangefinderSystem(world)
                {
                    SessionSeed = sessionSeed,
                };
            dataLink = new DataLinkSystem(world) { SessionSeed = sessionSeed };
            trackDetectionScratch =
                new NativeList<Measurement>(
                    RadarSensorSystem.DetectionRingCapacity,
                    Allocator.Persistent);
            trackEmissionScratch =
                new NativeList<EmissionEvent>(
                    1024,
                    Allocator.Persistent);
            if (enableCombatTelemetry)
            {
                try
                {
                    telemetry = CombatTelemetrySession.Acquire(
                        telemetryMaximumFileSizeMegabytes);
                    telemetrySource = $"{name}#{GetEntityId()}";
                    trackManager.Diagnostic += OnRadarTrackDiagnostic;
                    telemetry.RecordDriverStart(
                        SimTimeSeconds,
                        telemetrySource,
                        sessionSeed,
                        MotionStepSeconds,
                        SensorStepSeconds,
                        FusionStepSeconds);
                }
                catch (System.Exception exception)
                {
                    telemetry = null;
                    Debug.LogWarning(
                        $"[CombatTelemetry] Unable to start recording: {exception.Message}",
                        this);
                }
            }
        }

        public string CombatTelemetryFilePath => telemetry?.CurrentFilePath ?? string.Empty;

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
            telemetry?.RecordRadarProfile(
                SimTimeSeconds,
                telemetrySource,
                in profile,
                false);
        }

        public void RegisterAuxiliaryRadarSensor(
            in RadarSensorProfile profile)
        {
            radar.RegisterSensor(profile);
            telemetry?.RecordRadarProfile(
                SimTimeSeconds,
                telemetrySource,
                in profile,
                true);
        }

        public void UnregisterRadarSensor(SensorId sensorId) =>
            radar.UnregisterSensor(sensorId);

        /// <summary>登记本机被动告警接收机；相关器只消费该接收机的接触。
        /// MAW 红外通道复用同一接收机身份（同一相关器、同一 RWR 环）。</summary>
        public void RegisterEsmReceiver(in EsmReceiverProfile profile)
        {
            esmReceiver.RegisterReceiver(profile);
            ownshipEsmReceiverId = profile.ReceiverId;
            ownshipMawEntity = profile.OwnerEntityId;
            telemetry?.RecordEsmProfile(
                SimTimeSeconds,
                telemetrySource,
                in profile);
        }

        // ---- 红外点源（MAW 数据源，场景桥按热状态喂入） ----

        public void RegisterIrSource(SimEntityId entityId, float intensityDb) =>
            mawSensor.RegisterSource(entityId, intensityDb);

        public void SetIrSourceIntensity(SimEntityId entityId, float intensityDb) =>
            mawSensor.SetSourceIntensity(entityId, intensityDb);

        public void UnregisterIrSource(SimEntityId entityId) =>
            mawSensor.UnregisterSource(entityId);

        public void RegisterElectroOpticalReceiver(
            in ElectroOpticalReceiverProfile profile)
        {
            electroOpticalReceiver = profile;
            electroOpticalReceiverRegistered = true;
            telemetry?.RecordElectroOpticalProfile(
                SimTimeSeconds,
                telemetrySource,
                in profile);
        }

        public void UpdateElectroOpticalForward(float3 forward)
        {
            electroOpticalReceiver.Forward = forward;
        }

        public void RegisterElectroOpticalTarget(
            in ElectroOpticalTargetProfile profile) =>
            electroOpticalSensor.RegisterTarget(profile);

        public void UnregisterElectroOpticalTarget(
            SimEntityId entityId) =>
            electroOpticalSensor.UnregisterTarget(entityId);

        public int CopyElectroOpticalObservations(
            ElectroOpticalObservation[] destination)
        {
            if (destination == null ||
                electroOpticalSensor == null)
            {
                return 0;
            }

            var source = electroOpticalSensor.Observations;
            var count = math.min(
                destination.Length,
                source.Length);
            for (var i = 0; i < count; i++)
            {
                destination[i] = source[i];
            }

            return count;
        }

        public void RegisterLaserRangefinder(
            in LaserRangefinderProfile profile)
        {
            laserRangefinder.RegisterSensor(profile);
            telemetry?.RecordLaserProfile(
                SimTimeSeconds,
                telemetrySource,
                in profile);
        }

        public void UnregisterLaserRangefinder(SensorId sensorId) =>
            laserRangefinder.UnregisterSensor(sensorId);

        public void RegisterLaserRangeTarget(
            in LaserRangeTargetProfile profile) =>
            laserRangefinder.RegisterTarget(profile);

        public void UnregisterLaserRangeTarget(SimEntityId entityId) =>
            laserRangefinder.UnregisterTarget(entityId);

        public bool ScheduleLaserPulse(in LaserPulse pulse)
        {
            var accepted = laserRangefinder != null &&
                           laserRangefinder.SchedulePulse(in pulse);
            telemetry?.RecordLaserPulse(
                SimTimeSeconds,
                telemetrySource,
                in pulse,
                accepted);
            return accepted;
        }

        public int CopyLaserRangeObservations(
            LaserRangeObservation[] destination)
        {
            if (destination == null || laserRangefinder == null)
            {
                return 0;
            }

            var source = laserRangefinder.Observations;
            var count = math.min(destination.Length, source.Length);
            for (var i = 0; i < count; i++)
            {
                destination[i] = source[i];
            }

            return count;
        }

        // ---- 数据链（终端注册 / 收发，框架级：机体/无人机/僚机/导弹/拦截弹共用） ----

        public void RegisterLinkTerminal(in LinkTerminalProfile profile)
        {
            dataLink.RegisterTerminal(profile);
            telemetry?.RecordLinkProfile(
                SimTimeSeconds,
                telemetrySource,
                in profile);
        }

        public void UnregisterLinkTerminal(LinkTerminalId terminalId) =>
            dataLink.UnregisterTerminal(terminalId);

        /// <summary>入队发送；下一个传感器 Tick 投递（一 Tick 固有时延）。</summary>
        public bool SendLinkMessage(in LinkMessage message)
        {
            var accepted = dataLink != null && dataLink.Send(message);
            if (DetailedTelemetry)
            {
                telemetry.RecordDataLink(
                    SimTimeSeconds,
                    telemetrySource,
                    "message_queued",
                    in message,
                    accepted);
            }
            return accepted;
        }

        public int CopyLinkInbox(LinkTerminalId terminalId, LinkMessage[] destination)
        {
            var count = dataLink?.CopyInbox(terminalId, destination) ?? 0;
            if (DetailedTelemetry)
            {
                for (var i = 0; i < count; i++)
                {
                    telemetry.RecordDataLink(
                        SimTimeSeconds,
                        telemetrySource,
                        "message_received",
                        in destination[i],
                        true);
                }
            }
            return count;
        }

        public void UnregisterEsmReceiver(SensorId receiverId) =>
            esmReceiver.UnregisterReceiver(receiverId);

        public void RegisterEsmWaveformClass(ushort band, ushort waveformId, EsmWaveformClass waveformClass) =>
            threatCorrelator.RegisterWaveformClass(band, waveformId, waveformClass);

        public int ThreatCount => threatCorrelator?.Count ?? 0;

        public int CopyThreats(ThreatSnapshot[] destination) =>
            threatCorrelator?.CopyThreats(destination) ?? 0;

        public bool TryGetHighestThreat(out ThreatSnapshot snapshot)
        {
            snapshot = default;
            return threatCorrelator != null && threatCorrelator.TryGetHighestThreat(out snapshot);
        }

        public void RegisterRadarTarget(in RadarTargetProfile profile) => radar.RegisterTarget(profile);

        public void UnregisterRadarTarget(SimEntityId entityId) => radar.UnregisterTarget(entityId);

        public void ScheduleRadarDwell(in Dwell dwell)
        {
            radar.ScheduleDwell(dwell);
            if (DetailedTelemetry)
            {
                telemetry.RecordDwell(SimTimeSeconds, telemetrySource, in dwell);
            }
        }

        public void RecordFriendlyIdentityResolution(
            LinkTerminalId sourceTerminal,
            FriendlyIdentityResolution resolution,
            TrackId associatedTrackId,
            TrackId resolvedTrackId,
            double score,
            double reportAgeSeconds)
        {
            telemetry?.RecordFriendlyIdentityResolution(
                SimTimeSeconds,
                telemetrySource,
                sourceTerminal,
                resolution,
                associatedTrackId,
                resolvedTrackId,
                score,
                reportAgeSeconds);
        }

        public void RecordMissileTracks(
            MissileTrackSnapshot[] tracks,
            int count)
        {
            if (telemetry == null || tracks == null)
            {
                return;
            }

            var limit = math.min(count, tracks.Length);
            for (var i = 0; i < limit; i++)
            {
                telemetry.RecordMissileTrack(
                    SimTimeSeconds,
                    telemetrySource,
                    in tracks[i]);
            }
        }

        public bool TryGetTrack(TrackId trackId, out TrackSnapshot snapshot) =>
            trackManager.TryGetTrack(trackId, out snapshot);

        public bool TryGetBestTrack(out TrackSnapshot snapshot) =>
            trackManager.TryGetBestTrack(out snapshot);

        public int CopyTracks(TrackSnapshot[] destination) =>
            trackManager?.CopyTracks(destination) ?? 0;

        public void ConfigureRadarTracking(int capacity, float trackMemorySeconds)
        {
            trackManager?.ConfigureLimits(capacity, trackMemorySeconds);
            telemetry?.RecordTrackingConfiguration(
                SimTimeSeconds,
                telemetrySource,
                capacity,
                trackMemorySeconds,
                telemetryTrackSnapshotIntervalSeconds);
        }

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
                // Advance 已把时钟推到最终时刻；追帧时回推出每个 tick 的实际时间。
                // 两个追赶 tick 共用最终时间戳会让第二个消费不到任何到期驻留，
                // 还清空上一 tick 的探测环——等于白丢一帧探测。
                var tickTime = sensorClock.SimTimeSeconds
                               - SensorStepSeconds * (sensorTicks - 1 - i);
                radar.Tick(tickTime);
                if (DetailedTelemetry)
                {
                    for (var emissionIndex = 0;
                         emissionIndex < radar.Emissions.Length;
                         emissionIndex++)
                    {
                        telemetry.RecordEmission(
                            tickTime,
                            telemetrySource,
                            "radar",
                            in radar.Emissions.ElementAt(emissionIndex));
                    }
                    for (var detectionIndex = 0;
                         detectionIndex < radar.Detections.Length;
                         detectionIndex++)
                    {
                        telemetry.RecordRadarMeasurement(
                            tickTime,
                            telemetrySource,
                            detectionIndex,
                            in radar.Detections.ElementAt(detectionIndex));
                    }
                }
                if (electroOpticalReceiverRegistered)
                {
                    electroOpticalSensor.Tick(
                        in electroOpticalReceiver,
                        tickTime);
                    if (DetailedTelemetry)
                    {
                        for (var observationIndex = 0;
                             observationIndex < electroOpticalSensor.Observations.Length;
                             observationIndex++)
                        {
                            telemetry.RecordElectroOptical(
                                tickTime,
                                telemetrySource,
                                in electroOpticalSensor.Observations.ElementAt(observationIndex));
                        }
                    }
                }
                laserRangefinder.Tick(tickTime);
                if (DetailedTelemetry)
                {
                    for (var emissionIndex = 0;
                         emissionIndex < laserRangefinder.Emissions.Length;
                         emissionIndex++)
                    {
                        telemetry.RecordEmission(
                            tickTime,
                            telemetrySource,
                            "laser_rangefinder",
                            in laserRangefinder.Emissions.ElementAt(emissionIndex));
                    }
                }
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
                    tickTime);

                // Laser returns refine only the confirmed missile track that
                // cued the pulse. They do not create tracks, count as radar
                // hits, or keep a coasting radar track alive.
                if (laserRangefinder != null)
                {
                    var laserObservations = laserRangefinder.Observations;
                    for (var laserIndex = 0;
                         laserIndex < laserObservations.Length;
                         laserIndex++)
                    {
                        var laserObservation =
                            laserObservations[laserIndex];
                        var refinementAccepted =
                            trackManager.TryRefineConfirmedTrackWithLaser(
                            in laserObservation,
                            sensorPosition,
                            tickTime,
                            out var normalizedInnovationSquared);
                        telemetry?.RecordLaserObservation(
                            tickTime,
                            telemetrySource,
                            in laserObservation,
                            refinementAccepted,
                            normalizedInnovationSquared);
                    }
                }

                // 数据链：投递队列中的报文（写链路辐射 + 按物理条件投递）。
                dataLink.Tick(tickTime);
                if (DetailedTelemetry)
                {
                    for (var emissionIndex = 0;
                         emissionIndex < dataLink.Emissions.Length;
                         emissionIndex++)
                    {
                        telemetry.RecordEmission(
                            tickTime,
                            telemetrySource,
                            "data_link",
                            in dataLink.Emissions.ElementAt(emissionIndex));
                    }
                }

                // 被动告警：同 Tick 消费全部辐射环（每环每 Tick 清空，接收必须
                // 与辐射同步）——雷达环 + 数据链环 + MAW 红外通道，汇入同一
                // 接触环后由相关器按本机接收机 Id 过滤处理。
                esmReceiver.BeginTick();
                esmReceiver.Consume(radar.Emissions, tickTime);
                esmReceiver.Consume(dataLink.Emissions, tickTime);
                esmReceiver.Consume(
                    laserRangefinder.Emissions,
                    tickTime);
                if (ownshipEsmReceiverId.IsValid)
                {
                    mawSensor.AppendContacts(
                        ownshipMawEntity,
                        ownshipEsmReceiverId,
                        esmReceiver.Contacts,
                        tickTime);
                }

                if (DetailedTelemetry)
                {
                    for (var contactIndex = 0;
                         contactIndex < esmReceiver.Contacts.Length;
                         contactIndex++)
                    {
                        telemetry.RecordWarningContact(
                            tickTime,
                            telemetrySource,
                            in esmReceiver.Contacts.ElementAt(contactIndex));
                    }
                }

                threatCorrelator.Process(
                    esmReceiver.Contacts,
                    ownshipEsmReceiverId,
                    tickTime);

                var telemetryThreatCount = 0;
                if (telemetry != null)
                {
                    telemetryThreatCount = threatCorrelator.CopyThreats(
                        telemetryThreatScratch);
                    for (var threatIndex = 0;
                         threatIndex < telemetryThreatCount;
                         threatIndex++)
                    {
                        telemetry.RecordThreat(
                            tickTime,
                            telemetrySource,
                            in telemetryThreatScratch[threatIndex],
                            DetailedTelemetry);
                    }

                    if (tickTime + 1e-9 >= nextTelemetryTrackSnapshotTime)
                    {
                        var telemetryTrackCount = trackManager.CopyTracks(
                            telemetryTrackScratch);
                        for (var trackIndex = 0;
                             trackIndex < telemetryTrackCount;
                             trackIndex++)
                        {
                            telemetry.RecordTrackSnapshot(
                                tickTime,
                                telemetrySource,
                                in telemetryTrackScratch[trackIndex]);
                        }
                        nextTelemetryTrackSnapshotTime = tickTime +
                            math.max(
                                telemetryDetail == CombatTelemetryDetail.Detailed
                                    ? SensorStepSeconds
                                    : 0.5,
                                telemetryTrackSnapshotIntervalSeconds);
                    }

                    if (DetailedTelemetry ||
                        tickTime + 1e-9 >= nextTelemetrySensorSummaryTime)
                    {
                        telemetry.RecordSensorTick(
                            tickTime,
                            telemetrySource,
                            radar.Detections.Length,
                            radar.Emissions.Length,
                            electroOpticalReceiverRegistered
                                ? electroOpticalSensor.Observations.Length
                                : 0,
                            laserRangefinder.Observations.Length,
                            esmReceiver.Contacts.Length,
                            trackManager.Count,
                            telemetryThreatCount,
                            dwellScheduler.Count,
                            dwellScheduler.DeferredCount);
                        nextTelemetrySensorSummaryTime = tickTime + 1.0;
                    }
                }
            }

            fusionClock.Advance(dt);
            if (motionClock.OverrunTicks > 0 && motionTicks == maxMotionCatchUpTicks)
            {
                Debug.LogWarning($"[SimDriver] motion tick overrun total={motionClock.OverrunTicks}");
            }
        }

        private void OnDestroy()
        {
            if (trackManager != null)
            {
                trackManager.Diagnostic -= OnRadarTrackDiagnostic;
            }
            CombatTelemetrySession.Release(telemetry);
            telemetry = null;
            dataLink?.Dispose();
            laserRangefinder?.Dispose();
            electroOpticalSensor?.Dispose();
            esmReceiver?.Dispose();
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

        private void OnRadarTrackDiagnostic(RadarTrackDiagnostic diagnostic)
        {
            if (!DetailedTelemetry &&
                (diagnostic.Kind == RadarTrackDiagnosticKind.AssociationAccepted ||
                 diagnostic.Kind == RadarTrackDiagnosticKind.ManeuverAssociationAccepted))
            {
                return;
            }

            telemetry?.RecordRadarTrackDiagnostic(
                telemetrySource,
                in diagnostic);
        }
    }
}
