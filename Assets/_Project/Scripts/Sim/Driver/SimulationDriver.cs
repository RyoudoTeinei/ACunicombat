using ACunicombat.DataLink;
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

        /// <summary>登记本机被动告警接收机；相关器只消费该接收机的接触。
        /// MAW 红外通道复用同一接收机身份（同一相关器、同一 RWR 环）。</summary>
        public void RegisterEsmReceiver(in EsmReceiverProfile profile)
        {
            esmReceiver.RegisterReceiver(profile);
            ownshipEsmReceiverId = profile.ReceiverId;
            ownshipMawEntity = profile.OwnerEntityId;
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
            in LaserRangefinderProfile profile) =>
            laserRangefinder.RegisterSensor(profile);

        public void UnregisterLaserRangefinder(SensorId sensorId) =>
            laserRangefinder.UnregisterSensor(sensorId);

        public void RegisterLaserRangeTarget(
            in LaserRangeTargetProfile profile) =>
            laserRangefinder.RegisterTarget(profile);

        public void UnregisterLaserRangeTarget(SimEntityId entityId) =>
            laserRangefinder.UnregisterTarget(entityId);

        public bool ScheduleLaserPulse(in LaserPulse pulse) =>
            laserRangefinder != null &&
            laserRangefinder.SchedulePulse(in pulse);

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

        public void RegisterLinkTerminal(in LinkTerminalProfile profile) =>
            dataLink.RegisterTerminal(profile);

        public void UnregisterLinkTerminal(LinkTerminalId terminalId) =>
            dataLink.UnregisterTerminal(terminalId);

        /// <summary>入队发送；下一个传感器 Tick 投递（一 Tick 固有时延）。</summary>
        public bool SendLinkMessage(in LinkMessage message) =>
            dataLink != null && dataLink.Send(message);

        public int CopyLinkInbox(LinkTerminalId terminalId, LinkMessage[] destination) =>
            dataLink?.CopyInbox(terminalId, destination) ?? 0;

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
                // Advance 已把时钟推到最终时刻；追帧时回推出每个 tick 的实际时间。
                // 两个追赶 tick 共用最终时间戳会让第二个消费不到任何到期驻留，
                // 还清空上一 tick 的探测环——等于白丢一帧探测。
                var tickTime = sensorClock.SimTimeSeconds
                               - SensorStepSeconds * (sensorTicks - 1 - i);
                radar.Tick(tickTime);
                if (electroOpticalReceiverRegistered)
                {
                    electroOpticalSensor.Tick(
                        in electroOpticalReceiver,
                        tickTime);
                }
                laserRangefinder.Tick(tickTime);
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

                // 激光测距回波与雷达检测一起送进跟踪器，让滤波器的速度估计也受益于激光的
                // 高测角精度——横向速度没有任何直接量测，只能由位置序列的斜率推出，因此位置
                // 横向噪声（雷达约 9.3 m / 激光约 1.57 m @3 km）直接决定提前量误差。
                // 标记为 RefinementOnly，孤立回波只会被丢弃而不会新建航迹。
                if (laserRangefinder != null)
                {
                    var laserObservations = laserRangefinder.Observations;
                    for (var laserIndex = 0;
                         laserIndex < laserObservations.Length;
                         laserIndex++)
                    {
                        if (LaserMeasurementBridge.TryConvert(
                                laserObservations[laserIndex],
                                out var laserMeasurement))
                        {
                            trackDetectionScratch.Add(laserMeasurement);
                        }
                    }
                }

                trackManager.Process(
                    trackDetectionScratch,
                    trackEmissionScratch,
                    sensorPosition,
                    sensorVelocity,
                    tickTime);

                // 数据链：投递队列中的报文（写链路辐射 + 按物理条件投递）。
                dataLink.Tick(tickTime);

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

                threatCorrelator.Process(
                    esmReceiver.Contacts,
                    ownshipEsmReceiverId,
                    tickTime);
            }

            fusionClock.Advance(dt);
            if (motionClock.OverrunTicks > 0 && motionTicks == maxMotionCatchUpTicks)
            {
                Debug.LogWarning($"[SimDriver] motion tick overrun total={motionClock.OverrunTicks}");
            }
        }

        private void OnDestroy()
        {
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
    }
}
