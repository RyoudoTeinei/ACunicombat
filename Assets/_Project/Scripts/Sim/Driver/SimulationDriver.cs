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
        private ObserverEntityBindings sceneEntities;
        private SpatialHashGrid grid;
        private DwellScheduler dwellScheduler;
        private RadarSensorSystem radar;
        private readonly RadarSensorAllocator radarIdentities = new RadarSensorAllocator();
        public SensorId PrimaryRadarIdentity=>radarIdentities.PrimaryRadar;
        public SensorId WarningReceiverIdentity=>radarIdentities.WarningReceiver;
        public SensorId OpticalReceiverIdentity=>radarIdentities.OpticalReceiver;
        public SensorId LaserRangefinderIdentity=>radarIdentities.LaserRangefinder;
        public LinkTerminalId PrimaryLinkIdentity=>new LinkTerminalId(new Handle(1,1));
        private readonly DeviceHandleAllocator emitterIdentities=new DeviceHandleAllocator(256);
        private readonly System.Collections.Generic.Dictionary<SensorId,EmitterId> sensorEmitters=new System.Collections.Generic.Dictionary<SensorId,EmitterId>();
        private readonly System.Collections.Generic.Dictionary<LinkTerminalId,EmitterId> linkEmitters=new System.Collections.Generic.Dictionary<LinkTerminalId,EmitterId>();
        public EmitterId GetSensorEmitterId(SensorId sensor)
        {
            if(!radarIdentities.Owns(sensor))throw new System.InvalidOperationException("Unallocated sensor cannot own an emitter.");
            if(sensorEmitters.TryGetValue(sensor,out var emitter))return emitter;
            if(!emitterIdentities.TryAllocate(out var handle))throw new System.InvalidOperationException("Emitter capacity exhausted.");
            emitter=new EmitterId(handle);sensorEmitters.Add(sensor,emitter);return emitter;
        }
        public EmitterId GetLinkEmitterId(LinkTerminalId terminal)
        {
            if(!linkIdentities.Owns(terminal.Value))throw new System.InvalidOperationException("Unallocated terminal cannot own an emitter.");
            if(linkEmitters.TryGetValue(terminal,out var emitter))return emitter;
            if(!emitterIdentities.TryAllocate(out var handle))throw new System.InvalidOperationException("Emitter capacity exhausted.");
            emitter=new EmitterId(handle);linkEmitters.Add(terminal,emitter);return emitter;
        }
        public bool TryAllocateRadarSensor(out SensorId sensor) => radarIdentities.TryAllocate(out sensor);
        private PlatformTrackContext trackContext;
        private EsmReceiverSystem esmReceiver;
        private PlatformCombatContext combatContext;
        private EsmThreatCorrelator threatCorrelator => combatContext?.Threats;
        private MawSensorSystem mawSensor;
        private ElectroOpticalSensorSystem electroOpticalSensor;
        private LaserRangefinderSystem laserRangefinder;
        private DataLinkSystem dataLink;
        private readonly DeviceHandleAllocator linkIdentities=new DeviceHandleAllocator(DataLinkSystem.MaxTerminals,1);
        public bool TryAllocateLinkTerminal(out LinkTerminalId terminal)
        {var result=linkIdentities.TryAllocate(out var handle);terminal=new LinkTerminalId(handle);return result;}
        private readonly TrackSnapshot[] telemetryTrackScratch =
            new TrackSnapshot[RadarTrackManager.MaxTracks];
        private readonly ThreatSnapshot[] telemetryThreatScratch =
            new ThreatSnapshot[EsmThreatCorrelator.MaxThreats];
        private CombatTelemetrySession telemetry;
        private string telemetrySource;
        private double nextTelemetryTrackSnapshotTime;
        private double nextTelemetrySensorSummaryTime;

        private bool DetailedTelemetry =>
            telemetry != null && telemetry.IsRecording && telemetryDetail == CombatTelemetryDetail.Detailed;

        public System.Guid ObserverScope => trackContext?.ObserverScope ?? System.Guid.Empty;
        private System.Func<double> simulationTimeSource;
        public ulong ObservationRevision { get; private set; }
        // Cadence clocks stay local; timestamps share battlefield physics time even for late joiners.
        // Fixtures that deliberately bypass Awake may inject their existing manual motion clock.
        public double SimTimeSeconds => simulationTimeSource!=null ? simulationTimeSource() : motionClock.SimTimeSeconds;
        public long MotionOverrunTicks => motionClock.OverrunTicks;
        public int DetectionCount => radar?.Detections.Length ?? 0;
        public int TrackCount => trackContext?.Count ?? 0;
        public int TrackCapacity => trackContext?.Capacity ?? RadarTrackManager.MaxTracks;
        public int ScheduledDwellCount => dwellScheduler?.Count ?? 0;
        public long DeferredDwellCount => dwellScheduler?.DeferredCount ?? 0;

        private void Awake()
        {
            simulationTimeSource = () => Time.fixedTimeAsDouble;
            motionClock = new FixedTickClock(MotionStepSeconds, maxMotionCatchUpTicks);
            sensorClock = new FixedTickClock(SensorStepSeconds, maxSensorCatchUpTicks);
            fusionClock = new FixedTickClock(FusionStepSeconds, maxSensorCatchUpTicks);
            world = new SimWorld();
            grid = new SpatialHashGrid(SimWorld.MaxEntities * 2);
            dwellScheduler = new DwellScheduler(512);
            radar = new RadarSensorSystem(world, grid, dwellScheduler) { SessionSeed = sessionSeed };
            trackContext = new PlatformTrackContext();
            esmReceiver = new EsmReceiverSystem(world) { SessionSeed = sessionSeed };
            combatContext = new PlatformCombatContext(trackContext);
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
            if (enableCombatTelemetry)
            {
                try
                {
                    telemetry = CombatTelemetrySession.Acquire(
                        telemetryMaximumFileSizeMegabytes);
                    telemetrySource = $"{name}#{GetEntityId()}";
                    trackContext.Diagnostic += OnRadarTrackDiagnostic;
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

        public void UnregisterEntity(SimEntityId entityId)
        {
            if (sceneEntities != null && sceneEntities.OwnsLocalEntity(entityId))
                throw new System.InvalidOperationException("Scene entity registrations must release their lease.");
            world.Unregister(entityId);
        }

        public SceneEntityLease RegisterSceneEntity(BattlefieldEntityIdentity source, in KinematicState initial, SceneEntityProjection projection = SceneEntityProjection.Body)
        {
            if (source == null) throw new System.ArgumentNullException(nameof(source));
            if (sceneEntities == null)
            {
                sceneEntities = new ObserverEntityBindings(world, BattlefieldEntityIdentity.Registry);
                sceneEntities.LocalEntityReleased += UnregisterSceneTargetProfiles;
            }
            return sceneEntities.Acquire(source.Identity, in initial, projection);
        }
        public bool IsSceneEntityCurrent(in SceneEntityLease lease) => sceneEntities != null && sceneEntities.IsCurrent(in lease);
        public bool ReleaseSceneEntity(in SceneEntityLease lease) => sceneEntities != null && sceneEntities.Release(in lease);
        private void UnregisterSceneTargetProfiles(SimEntityId entity)
        {
            radar?.UnregisterTarget(entity);
            electroOpticalSensor?.UnregisterTarget(entity);
            laserRangefinder?.UnregisterTarget(entity);
            mawSensor?.UnregisterSource(entity);
        }

        public bool SetEntityState(SimEntityId entityId, in KinematicState state) =>
            world.TrySetState(entityId, state);

        public bool SetTimedEntityState(SimEntityId entityId,in KinematicState state,double sampledTime)=>world.TrySetTimedState(entityId,in state,sampledTime);

        public bool SetRadarSensorEfficiency(SensorId sensor,float efficiency) => radar != null && radar.SetSensorEfficiency(sensor,efficiency);

        public void RegisterRadarSensor(in RadarSensorProfile profile)
        {
            if(!radarIdentities.TryReserve(profile.SensorId))throw new System.InvalidOperationException("Radar identity is stale.");
            var registered=profile;registered.EmitterId=GetSensorEmitterId(profile.SensorId);
            trackContext.BindPrimaryRadar(in registered);
            radar.RegisterSensor(registered);
            telemetry?.RecordRadarProfile(SimTimeSeconds,telemetrySource,in registered,false);
        }
        public void RegisterAuxiliaryRadarSensor(in RadarSensorProfile profile, bool closeProtection = false)
        {
            if(!radarIdentities.TryReserve(profile.SensorId))throw new System.InvalidOperationException("Radar identity is stale.");
            var registered=profile;registered.EmitterId=GetSensorEmitterId(profile.SensorId);
            radar.RegisterSensor(registered);
            if(registered.OwnerEntityId.Equals(trackContext.OriginEntity))trackContext.RegisterAuxiliary(in registered,closeProtection);
            telemetry?.RecordRadarProfile(SimTimeSeconds,telemetrySource,in registered,true);
        }
        public void UnregisterRadarSensor(SensorId sensorId)
        {
            trackContext.RemoveAuxiliary(sensorId);
            radar.UnregisterSensor(sensorId);
            radarIdentities.Release(sensorId);
            if(sensorEmitters.TryGetValue(sensorId,out var emitter)){emitterIdentities.Release(emitter.Value);sensorEmitters.Remove(sensorId);}
        }

        /// <summary>登记本机被动告警接收机；相关器只消费该接收机的接触。
        /// MAW 红外通道复用同一接收机身份（同一相关器、同一 RWR 环）。</summary>
        public void RegisterEsmReceiver(in EsmReceiverProfile profile)
        {
            esmReceiver.RegisterReceiver(profile);
            combatContext.WarningReceiver = profile.ReceiverId;
            combatContext.WarningOwner = profile.OwnerEntityId;
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

        private float opticalEfficiency=1f;
        public void SetOpticalEfficiency(float value)=>opticalEfficiency=float.IsFinite(value)?math.clamp(value,0,1):0;
        public bool SetLaserEfficiency(SensorId id,float value)=>laserRangefinder!=null&&laserRangefinder.SetSensorEfficiency(id,value);
        public void RegisterElectroOpticalReceiver(
            in ElectroOpticalReceiverProfile profile)
        {
            combatContext.OpticalReceiver = profile;
            combatContext.HasOpticalReceiver = true;
            telemetry?.RecordElectroOpticalProfile(
                SimTimeSeconds,
                telemetrySource,
                in profile);
        }

        public void UpdateElectroOpticalForward(float3 forward)
        {
            combatContext.OpticalReceiver.Forward = forward;
        }

        public void RegisterElectroOpticalTarget(
            in ElectroOpticalTargetProfile profile) =>
            electroOpticalSensor.RegisterTarget(profile);

        public void UnregisterElectroOpticalTarget(
            SimEntityId entityId) =>
            electroOpticalSensor.UnregisterTarget(entityId);

        public void SetElectroOpticalTargetSignature(SimEntityId entityId, float signatureDb) =>
            electroOpticalSensor.SetTargetSignature(entityId, signatureDb);

        public int CopyElectroOpticalObservations(
            ElectroOpticalObservation[] destination)
        {
            if (destination == null || opticalEfficiency<=0 ||
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
            if(!linkIdentities.TryReserve(profile.TerminalId.Value))throw new System.InvalidOperationException("Data-link identity is stale.");
            var registered=profile;registered.EmitterId=GetLinkEmitterId(profile.TerminalId);
            dataLink.RegisterTerminal(registered);
            telemetry?.RecordLinkProfile(SimTimeSeconds,telemetrySource,in registered);
        }
        public void UnregisterLinkTerminal(LinkTerminalId terminalId)
        {dataLink.UnregisterTerminal(terminalId);linkIdentities.Release(terminalId.Value);
            if(linkEmitters.TryGetValue(terminalId,out var emitter)){emitterIdentities.Release(emitter.Value);linkEmitters.Remove(terminalId);}
        }

        public long AcceptedLinkMessageCount => dataLink?.AcceptedMessageCount ?? 0;
        public long RejectedLinkMessageCount => dataLink?.RejectedMessageCount ?? 0;
        public long DeliveredLinkMessageCount => dataLink?.DeliveredMessageCount ?? 0;
        public long LinkDeliveryAttemptsFor(LinkTerminalId terminal) => dataLink?.DeliveryAttemptsFor(terminal) ?? 0;
        public long LinkDeliveriesFor(LinkTerminalId terminal) => dataLink?.DeliveriesFor(terminal) ?? 0;

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

        public void QueueJamContribution(in JamContribution contribution)
        {
            if (contribution.VictimSensorId.IsValid) radar.JamInboxForEw.Add(contribution);
        }

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

        public bool TryRefineMissileTelemetry(TrackId id,double3 position,double3 velocity,double reportTime)
        {
            if(trackContext==null||!trackContext.TryRefineMissileTelemetry(id,position,velocity,reportTime,SimTimeSeconds))return false;
            ObservationRevision++;return true;
        }
        public bool CloseMissileAssociation(TrackId id)
        {
            if(trackContext==null||!trackContext.CloseMissileAssociation(id,SimTimeSeconds))return false;
            ObservationRevision++;return true;
        }
        public bool TryGetTrack(TrackId trackId, out TrackSnapshot snapshot) =>
            trackContext.TryGetTrack(trackId, out snapshot);

        public bool TryGetBestTrack(out TrackSnapshot snapshot) =>
            trackContext.TryGetBestTrack(out snapshot);

        public int CopyTracks(TrackSnapshot[] destination) =>
            trackContext?.CopyTracks(destination) ?? 0;

        public void ConfigureRadarTracking(int capacity, float trackMemorySeconds)
        {
            trackContext?.ConfigureLimits(capacity, trackMemorySeconds);
            telemetry?.RecordTrackingConfiguration(
                SimTimeSeconds,
                telemetrySource,
                capacity,
                trackMemorySeconds,
                telemetryTrackSnapshotIntervalSeconds);
        }

        /// <summary>Executed emissions for the requested local sensor; scheduled-but-deferred dwells are excluded.</summary>
        private readonly System.Collections.Generic.List<EmissionEvent> externalIff=new System.Collections.Generic.List<EmissionEvent>();
        private readonly System.Collections.Generic.Dictionary<System.Guid,EmitterId> iffEmitterIds=new System.Collections.Generic.Dictionary<System.Guid,EmitterId>();
        public void ReleaseExternalIffEmitter(System.Guid source)
        {if(iffEmitterIds.TryGetValue(source,out var emitter)){emitterIdentities.Release(emitter.Value);iffEmitterIds.Remove(source);}}
        public void QueueExternalEmission(System.Guid source,EmissionEvent emission)
        {
            if(externalIff.Count>=1024)return;
            if(!iffEmitterIds.TryGetValue(source,out var emitter))
            {if(!emitterIdentities.TryAllocate(out var handle))return;emitter=new EmitterId(handle);iffEmitterIds.Add(source,emitter);}
            emission.EmitterId=emitter;externalIff.Add(emission);
        }
        public void QueueExternalIffEmission(System.Guid source,double3 origin,float3 direction,float beamWidth,float powerDb)
        {
            QueueExternalEmission(source,new EmissionEvent{Origin=origin,Direction=direction,BeamWidth=beamWidth,
                StartTime=SimTimeSeconds,EndTime=SimTimeSeconds+.1,MaxRangeMeters=1e9,FrequencyBand=1,WaveformId=0,
                EmissionType=EmissionType.Iff,MainlobePowerDb=powerDb,SidelobeLevelDb=-30});
        }

        public int CopyWarningObservations(SensorId receiver,WarningObservation[] destination)
        {if(esmReceiver==null||destination==null)return 0;int count=0;for(int i=0;i<esmReceiver.Contacts.Length&&count<destination.Length;i++)if(esmReceiver.Contacts[i].ReceiverId.Equals(receiver))destination[count++]=esmReceiver.Contacts[i];return count;}

        public int CopyRadarEmissions(SensorId sensor, EmissionEvent[] destination)
        {
            if(radar==null||destination==null||!sensor.IsValid)return 0;
            var emitter=GetSensorEmitterId(sensor);int count=0;
            for(int i=0;i<radar.Emissions.Length&&count<destination.Length;i++)
            {
                var emission=radar.Emissions[i];
                if(emission.EmitterId.Equals(emitter))destination[count++]=emission;
            }
            return count;
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

        // ---- Fixed step: every observer driver is advanced together so the tracker phase
        // (B) of all observers can run in parallel. Per observer the order is unchanged.
        private static readonly System.Collections.Generic.List<SimulationDriver> activeDrivers = new();
        private static double scheduledStep = double.NaN;
        /// <summary>Run observer trackers on worker threads (identical results; see crowd benchmark).</summary>
        public static bool ParallelTracking { get; set; } = true;
        /// <summary>Raised on the main thread after a parallel fixed step advanced every driver.</summary>
        public static event System.Action AfterScheduledStep;
        private void OnEnable() { if (!activeDrivers.Contains(this)) activeDrivers.Add(this); }
        private void OnDisable() { activeDrivers.Remove(this); }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetScheduler() { activeDrivers.Clear(); scheduledStep = double.NaN; }

        private int stepSensorTicks, stepMotionTicks;
        private double roundTickTime;
        private double stepDt;

        private void FixedUpdate()
        {
            if (!ParallelTracking || activeDrivers.Count < 2 || RadarReplayJournal.Enabled || RadarReplayJournal.BatchEnabled)
            {
                using var costScope = ACunicombat.Simulation.Core.SimulationCostProbe.Measure(ACunicombat.Simulation.Core.SimulationCostProbe.SystemId.Driver);
                BeginFixedStep();
                for (var i = 0; i < stepSensorTicks; i++)
                {
                    var tickTime = StepTickTime(i);
                    SensorTickA(tickTime);
                    tickSections.Next(ACunicombat.Simulation.Core.SimulationCostProbe.SystemId.SensorTracking);
                    SensorTickB(tickTime);
                    SensorTickC(tickTime);
                }
                EndFixedStep();
                return;
            }
            // The first driver to reach this physics step advances all of them.
            var step = Time.fixedTimeAsDouble;
            if (step == scheduledStep) return;
            scheduledStep = step;
            RunScheduledStep();
        }

        private static readonly System.Collections.Generic.List<SimulationDriver> stepDrivers = new();
        private static void RunScheduledStep()
        {
            stepDrivers.Clear();
            foreach (var driver in activeDrivers) if (driver != null && driver.isActiveAndEnabled) stepDrivers.Add(driver);
            int rounds = 0;
            foreach (var driver in stepDrivers)
            {
                using var costScope = ACunicombat.Simulation.Core.SimulationCostProbe.Measure(ACunicombat.Simulation.Core.SimulationCostProbe.SystemId.Driver);
                driver.BeginFixedStep();
                rounds = System.Math.Max(rounds, driver.stepSensorTicks);
            }
            for (var round = 0; round < rounds; round++)
            {
                foreach (var driver in stepDrivers)
                {
                    if (round >= driver.stepSensorTicks) continue;
                    // Unity time is main-thread only: resolve the tick time here for phase B.
                    driver.roundTickTime = driver.StepTickTime(round);
                    using var costScope = ACunicombat.Simulation.Core.SimulationCostProbe.Measure(ACunicombat.Simulation.Core.SimulationCostProbe.SystemId.Driver);
                    driver.SensorTickA(driver.roundTickTime);
                }
                using (var trackScope = ACunicombat.Simulation.Core.SimulationCostProbe.Measure(ACunicombat.Simulation.Core.SimulationCostProbe.SystemId.SensorTracking))
                {
                    var currentRound = round;
                    System.Threading.Tasks.Parallel.For(0, stepDrivers.Count, index =>
                    {
                        var driver = stepDrivers[index];
                        if (currentRound >= driver.stepSensorTicks) return;
                        ACunicombat.Simulation.Core.SimulationCostProbe.MarkWorkerThread(true);
                        try { driver.SensorTickB(driver.roundTickTime); }
                        finally { ACunicombat.Simulation.Core.SimulationCostProbe.MarkWorkerThread(false); }
                    });
                }
                foreach (var driver in stepDrivers)
                {
                    if (round >= driver.stepSensorTicks) continue;
                    using var costScope = ACunicombat.Simulation.Core.SimulationCostProbe.Measure(ACunicombat.Simulation.Core.SimulationCostProbe.SystemId.Driver);
                    driver.SensorTickC(driver.roundTickTime);
                }
            }
            foreach (var driver in stepDrivers) driver.EndFixedStep();
            // Every observer has ticked: dependants (e.g. cooperative fusion) may now advance
            // their own per-observer work in parallel before other scripts read it.
            AfterScheduledStep?.Invoke();
        }

        private void BeginFixedStep()
        {
            if(telemetry!=null&&telemetry.TryTakeFailure(out var recordingFailure))Debug.LogWarning("[CombatTelemetry] Recording stopped: "+recordingFailure);
            sceneEntities?.PruneRetired();
            stepDt = Time.fixedDeltaTime;
            stepMotionTicks = motionClock.Advance(stepDt);
            stepSensorTicks = sensorClock.Advance(stepDt);
        }

        // Advance 已把时钟推到最终时刻；追帧时回推出每个 tick 的实际时间。
        // 两个追赶 tick 共用最终时间戳会让第二个消费不到任何到期驻留，
        // 还清空上一 tick 的探测环——等于白丢一帧探测。
        // This driver runs before the scene's physics movers. Their published
        // poses and velocities belong to the last completed physics step.
        // Labelling them 'now' made velocity one step stale during acceleration.
        private double StepTickTime(int i) => SimTimeSeconds - stepDt - SensorStepSeconds * (stepSensorTicks - 1 - i);

        private void EndFixedStep()
        {
            fusionClock.Advance(stepDt);
            ObservationRevision++;
            if (motionClock.OverrunTicks > 0 && stepMotionTicks == maxMotionCatchUpTicks)
            {
                Debug.LogWarning($"[SimDriver] motion tick overrun total={motionClock.OverrunTicks}");
            }
        }

        // Sensor tick split for parallel tracking: A (sensors) and C (links, warning,
        // telemetry) run on the main thread; B touches only this observer's tracker and may
        // run on a worker thread. Order within one observer is unchanged: A, B, C.
        private ACunicombat.Simulation.Core.SimulationCostProbe.Sections tickSections;
        private readonly System.Collections.Generic.List<(LaserRangeObservation observation, bool accepted, double nis)> pendingLaserTelemetry = new();
        internal void SensorTickA(double tickTime)
        {
            var sections=ACunicombat.Simulation.Core.SimulationCostProbe.Sections.Begin(ACunicombat.Simulation.Core.SimulationCostProbe.SystemId.SensorRadarTick);
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
            sections.Next(ACunicombat.Simulation.Core.SimulationCostProbe.SystemId.SensorOptics);
            if (combatContext.HasOpticalReceiver)
            {
                var effectiveOptical=combatContext.OpticalReceiver;
                effectiveOptical.MaximumRangeMeters*=math.sqrt(opticalEfficiency);
                effectiveOptical.BearingSigmaRadians/=math.max(.05f,opticalEfficiency);
                effectiveOptical.ClassificationScale*=opticalEfficiency;
                if(opticalEfficiency>0)electroOpticalSensor.Tick(in effectiveOptical,tickTime);
                else electroOpticalSensor.Observations.Clear();
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
            tickSections=sections;
        }

        /// <summary>Tracker update only. Thread-safe with respect to every other observer.</summary>
        internal void SensorTickB(double tickTime)
        {
            var sensorPosition = double3.zero;
            var sensorVelocity = double3.zero;
            if (trackContext.OriginEntity.IsValid
                && world.TryGetReadState(trackContext.OriginEntity, out var sensorState))
            {
                sensorPosition = sensorState.PositionWorld;
                sensorVelocity = sensorState.VelocityWorld;
            }

            trackContext.Process(radar.Detections, radar.Emissions,
                sensorPosition, sensorVelocity, tickTime);

            if (combatContext.HasOpticalReceiver)
            {
                for (int opticalIndex = 0; opticalIndex < electroOpticalSensor.Observations.Length; opticalIndex++)
                {
                    trackContext.TryRefineConfirmedTrackWithBearing(
                        electroOpticalSensor.Observations[opticalIndex], sensorPosition, tickTime,
                        out _, sensorVelocity);
                }
            }

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
                        trackContext.TryRefineConfirmedTrackWithLaser(
                        in laserObservation,
                        sensorPosition,
                        tickTime,
                        out var normalizedInnovationSquared);
                    if (telemetry != null) pendingLaserTelemetry.Add((laserObservation, refinementAccepted, normalizedInnovationSquared));
                }
            }

        }

        internal void SensorTickC(double tickTime)
        {
            var sections=tickSections;
            sections.Next(ACunicombat.Simulation.Core.SimulationCostProbe.SystemId.SensorDataLink);
            foreach (var laser in pendingLaserTelemetry)
                telemetry?.RecordLaserObservation(tickTime, telemetrySource, in laser.observation, laser.accepted, laser.nis);
            pendingLaserTelemetry.Clear();
            FlushTrackDiagnostics();
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

            sections.Next(ACunicombat.Simulation.Core.SimulationCostProbe.SystemId.SensorEsm);
            // 被动告警：同 Tick 消费全部辐射环（每环每 Tick 清空，接收必须
            // 与辐射同步）——雷达环 + 数据链环 + MAW 红外通道，汇入同一
            // 接触环后由相关器按本机接收机 Id 过滤处理。
            esmReceiver.BeginTick();
            if(externalIff.Count>0)
            {
                using(var iffEvents=new NativeList<EmissionEvent>(externalIff.Count,Allocator.Temp))
                {for(int pendingExternalIndex=externalIff.Count-1;pendingExternalIndex>=0;pendingExternalIndex--)if(externalIff[pendingExternalIndex].StartTime<=tickTime){iffEvents.Add(externalIff[pendingExternalIndex]);externalIff.RemoveAt(pendingExternalIndex);}esmReceiver.Consume(iffEvents,tickTime);}
            }
            esmReceiver.Consume(radar.Emissions, tickTime);
            esmReceiver.Consume(dataLink.Emissions, tickTime);
            esmReceiver.Consume(
                laserRangefinder.Emissions,
                tickTime);
            if (combatContext.WarningReceiver.IsValid)
            {
                mawSensor.AppendContacts(
                    combatContext.WarningOwner,
                    combatContext.WarningReceiver,
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
                combatContext.WarningReceiver,
                tickTime);

            sections.Next(ACunicombat.Simulation.Core.SimulationCostProbe.SystemId.SensorTelemetry);
            var telemetryThreatCount = 0;
            if (telemetry != null && telemetry.IsRecording)
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
                    var telemetryTrackCount = trackContext.CopyTracks(
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
                        combatContext.HasOpticalReceiver
                            ? electroOpticalSensor.Observations.Length
                            : 0,
                        laserRangefinder.Observations.Length,
                        esmReceiver.Contacts.Length,
                        trackContext.Count,
                        telemetryThreatCount,
                        dwellScheduler.Count,
                        dwellScheduler.DeferredCount);
                    nextTelemetrySensorSummaryTime = tickTime + 1.0;
                }
            }
            sections.End();
        }

        private void OnDestroy()
        {
            if (trackContext != null)
            {
                trackContext.Diagnostic -= OnRadarTrackDiagnostic;
            }
            CombatTelemetrySession.Release(telemetry);
            telemetry = null;
            sceneEntities?.Dispose();
            dataLink?.Dispose();
            laserRangefinder?.Dispose();
            electroOpticalSensor?.Dispose();
            esmReceiver?.Dispose();
            radar?.Dispose();
            trackContext?.Dispose();
            dwellScheduler?.Dispose();
            grid?.Dispose();
            world?.Dispose();
        }

        // Tracker diagnostics may be raised on a worker thread during tick phase B; they are
        // queued here and written on the main thread in phase C of the same tick.
        private readonly System.Collections.Generic.List<RadarTrackDiagnostic> pendingTrackDiagnostics = new();
        private void OnRadarTrackDiagnostic(RadarTrackDiagnostic diagnostic)
        {
            if (!DetailedTelemetry &&
                (diagnostic.Kind == RadarTrackDiagnosticKind.AssociationAccepted ||
                 diagnostic.Kind == RadarTrackDiagnosticKind.ManeuverAssociationAccepted))
            {
                return;
            }

            if (telemetry != null) pendingTrackDiagnostics.Add(diagnostic);
        }

        private void FlushTrackDiagnostics()
        {
            for (var i = 0; i < pendingTrackDiagnostics.Count; i++)
            {
                var diagnostic = pendingTrackDiagnostics[i];
                telemetry?.RecordRadarTrackDiagnostic(telemetrySource, in diagnostic);
            }
            pendingTrackDiagnostics.Clear();
        }
    }
}
