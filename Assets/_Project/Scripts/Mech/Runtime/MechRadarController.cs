using System;
using System.IO;
using ACunicombat.DataLink;
using ACunicombat.FireControl;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Sensors;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.Driver;
using ACunicombat.Simulation.Kinematics;
using ACunicombat.Simulation.World;
using Unity.Mathematics;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Bridges scene truth into the deterministic sensor simulation and exposes only measurements,
    /// tracks, radar status and fire-control products to gameplay/UI consumers.
    /// </summary>
    [DefaultExecutionOrder(-1100)]
    [DisallowMultipleComponent]
    public sealed class MechRadarController : MonoBehaviour
    {
        public const int MaximumPublicTracks = 32;
        public const int MaximumPublicEchoes = 64;

        private const int MaximumSceneTargets = 16;
        private const int GuidanceChannelCapacity = 2;
        private const float SearchIntervalSeconds = 0.04f;
        private const float SttIntervalSeconds = 0.035f;
        private const float SearchAzimuthDegrees = 100f;
        private const float SearchElevationDegrees = 60f;
        private const float SearchBeamWidthDegrees = 12f;
        // Keep meaningful overlap between adjacent raster dwells. At 9 degrees
        // the 12-degree beam left only ~0.025 degrees of worst-case diagonal
        // coverage margin, so small frame motion could thread between dwells.
        private const float SearchBeamSpacingDegrees = 8f;
        private const int SearchDwellsPerTick = 5;
        private const int SttSearchDwellsPerTick = 2;
        private const int TwsRevisitsPerTick = 2;
        private const float TwsRevisitIntervalSeconds = 0.24f;
        private const float TwsBeamWidthDegrees = 8f;
        private const float SttBeamWidthDegrees = 6f;
        private const float MissileCueIntervalSeconds = 0.02f;
        private const float MissileCueMaximumAgeSeconds = 0.45f;
        private const float MissileCueBeamWidthDegrees = 20f;
        private const int MissileCueDwellsPerTick = 4;
        private const float PointDefenseRevisitIntervalSeconds = 0.04f;
        private const float PointDefenseBeamWidthDegrees = 4f;
        private const float PointDefenseCueHoldSeconds = 0.3f;
        private const float RadarRangeMeters = 100_000f;
        private const float DesignationBaseSeconds = 0.3f;
        private const float DesignationCaptureBoxPixels = 54f;
        private const float SttAzimuthLimitDegrees = SearchAzimuthDegrees * 0.5f;
        private const float SttElevationLimitDegrees = SearchElevationDegrees * 0.5f;
        private struct GuidanceChannel
        {
            public TrackId TrackId;
            public int ReferenceCount;
        }

        private SimulationDriver driver;
        private Camera aimingCamera;
        private MechFlightMotionController flightMotion;
        private CombatTestTarget configuredTarget;
        private SimEntityId playerEntity;
        private readonly CombatTestTarget[] sceneTargets = new CombatTestTarget[MaximumSceneTargets];
        private readonly MechFlightMotionController[] sceneTargetMotions =
            new MechFlightMotionController[MaximumSceneTargets];
        private readonly SimEntityId[] targetEntities = new SimEntityId[MaximumSceneTargets];
        private readonly Vector3[] previousTargetPositions = new Vector3[MaximumSceneTargets];
        private readonly bool[] targetRadarRegistered = new bool[MaximumSceneTargets];
        private int sceneTargetCount;
        private readonly SensorId sensorId = new SensorId(new Handle(0, 1));
        // ESM 接收机与雷达传感器分属两套系统，索引空间独立。
        private readonly SensorId esmReceiverId = new SensorId(new Handle(0, 1));
        private readonly SensorId electroOpticalReceiverId =
            new SensorId(new Handle(0, 1));
        [Tooltip("给场景目标自动补挂测试辐射源（RWR 没有会辐射的对手就听不到任何东西）。")]
        [SerializeField] private bool autoEquipTestEmitters = true;

        // ---- 数据链：本机终端 index 1，导弹终端 8..63；EmitterId 用 32+index
        // 区段与雷达发射机（0 区段）分开，ESM 消隐按 EmitterId 精确匹配。----
        private const int MissileLinkIndexStart = 8;
        private const int LinkEmitterIndexBase = 32;
        private const byte GuidanceNetworkId = 0;
        private const float GuidanceUplinkIntervalSeconds = 0.25f;
        private const float GuidanceReacquisitionGateMeters = 350f;
        private readonly LinkTerminalId ownLinkTerminalId = new LinkTerminalId(new Handle(1, 1));
        private readonly bool[] missileLinkAllocated = new bool[64];
        private readonly byte[] missileLinkGenerations = new byte[64];

        private struct UplinkSubscription
        {
            public bool Active;
            public bool AllowTrackReacquisition;
            public LinkTerminalId MissileTerminal;
            public TrackId TrackId;
            public TrackSnapshot LastSent;
            public double NextSendTime;
        }

        private readonly UplinkSubscription[] uplinkSubscriptions = new UplinkSubscription[8];
        private readonly TrackSnapshot[] trackScratch = new TrackSnapshot[MaximumPublicTracks];
        private readonly MissileTrackSnapshot[] missileTrackScratch =
            new MissileTrackSnapshot[MaximumPublicTracks];
        private readonly MissileTrackSnapshot[] missileTrackCache =
            new MissileTrackSnapshot[MaximumPublicTracks];
        private readonly MissileTrackFusionState missileTrackFusionState =
            new MissileTrackFusionState();
        private readonly ThreatSnapshot[] threatScratch = new ThreatSnapshot[16];
        private readonly ElectroOpticalObservation[] electroOpticalScratch =
            new ElectroOpticalObservation[
                ElectroOpticalSensorSystem.ObservationCapacity];
        private readonly MissileTrackFusionConfig missileTrackFusionConfig =
            MissileTrackFusionConfig.Default;
        private int missileTrackCacheCount;
        private double missileTrackCacheTime = double.NaN;
        private readonly Measurement[] measurementScratch = new Measurement[MaximumPublicEchoes];
        private readonly GuidanceChannel[] guidanceChannels =
            new GuidanceChannel[GuidanceChannelCapacity];
        private readonly bool[] auxiliarySensorAllocated = new bool[64];
        private readonly byte[] auxiliarySensorGenerations = new byte[64];
        private readonly double[] nextTwsRevisitTimes =
            new double[MaximumPublicTracks];
        private readonly uint[] twsRevisitTrackKeys =
            new uint[MaximumPublicTracks];
        private readonly RasterScanPattern searchPattern = new RasterScanPattern(
            SearchAzimuthDegrees,
            SearchElevationDegrees,
            SearchBeamSpacingDegrees);

        private Vector3 previousPlayerPosition;
        private Vector3 playerVelocity;
        private bool initialized;
        private double nextSearchDwellTime;
        private double nextSttDwellTime;
        private double nextMissileCueDwellTime;
        private double nextPointDefenseDwellTime;
        private double pointDefenseCueUntilTime =
            double.NegativeInfinity;
        private ulong dwellSequence;
        private TrackId candidateTrackId;
        private TrackId sttTrackId;
        private TrackId pointDefenseCueTrackId;
        private float designationProgress;
        private bool trackOperationMode;
        private float beamAzimuthDegrees;
        private float beamElevationDegrees;
        private Vector3 searchFrameForward;
        private Vector3 searchFrameRight;
        private Vector3 searchFrameUp;

        public double SimTimeSeconds => driver != null ? driver.SimTimeSeconds : Time.timeAsDouble;
        public int DetectionCount => driver != null ? driver.DetectionCount : 0;
        public int TrackCount => driver != null ? driver.TrackCount : 0;
        public bool TrackOperationMode => trackOperationMode;
        public TrackId CandidateTrackId => candidateTrackId;
        public TrackId SttTrackId => sttTrackId;
        public bool HasStt => sttTrackId.IsValid && TryGetTrack(sttTrackId, out _);
        public float DesignationProgress => designationProgress;

        public void CuePointDefenseTrack(TrackId trackId)
        {
            pointDefenseCueTrackId = trackId;
            pointDefenseCueUntilTime = trackId.IsValid
                ? SimTimeSeconds + PointDefenseCueHoldSeconds
                : double.NegativeInfinity;
        }

        public readonly struct MissileSeekerHandle
        {
            internal MissileSeekerHandle(
                SimEntityId entityId,
                SensorId sensorId,
                bool radarTargetRegistered)
            {
                EntityId = entityId;
                SensorId = sensorId;
                RadarTargetRegistered = radarTargetRegistered;
            }

            internal SimEntityId EntityId { get; }
            internal bool RadarTargetRegistered { get; }
            public SensorId SensorId { get; }
            public bool IsValid =>
                EntityId.IsValid && SensorId.IsValid;
        }

        public void Configure(Camera newAimingCamera, CombatTestTarget newTarget)
        {
            aimingCamera = newAimingCamera;
            configuredTarget = newTarget;
            if (Application.isPlaying && isActiveAndEnabled)
            {
                EnsureInitialized();
            }
        }

        public void SetTrackOperationMode(bool enabled)
        {
            trackOperationMode = enabled;
            if (!enabled)
            {
                BreakSingleTargetTrack();
            }
        }

        public void BreakSingleTargetTrack()
        {
            sttTrackId = default;
            candidateTrackId = default;
            designationProgress = 0f;
        }

        public bool TryRegisterMissileSeeker(
            Vector3 position,
            Vector3 velocity,
            float maximumRangeMeters,
            float beamWidthDegrees,
            bool registerAsHostileRadarTarget,
            out MissileSeekerHandle handle)
        {
            EnsureInitialized();
            handle = default;
            if (driver == null)
            {
                return false;
            }

            for (var index = 1;
                 index < auxiliarySensorAllocated.Length;
                 index++)
            {
                if (auxiliarySensorAllocated[index])
                {
                    continue;
                }

                var generation = auxiliarySensorGenerations[index];
                if (generation == 0)
                {
                    generation = 1;
                    auxiliarySensorGenerations[index] = generation;
                }

                var sensor = new SensorId(
                    new Handle(index, generation));
                var entity = driver.RegisterEntity(
                    BuildState(position, velocity));
                driver.RegisterAuxiliaryRadarSensor(
                    new RadarSensorProfile
                    {
                        SensorId = sensor,
                        EmitterId = new EmitterId(
                            new Handle(index, generation)),
                        PlatformId = new PlatformId(
                            new Handle(index, generation)),
                        OwnerEntityId = entity,
                        DwellConfig = new RadarDwellConfig
                        {
                            ConstantsDb = 215f,
                            IntegrationGainDb = 8f,
                            NoisePowerDb = 0f,
                            DetectThresholdLowDb = 3f,
                            DetectThresholdHighDb = 10f,
                            RangeResolutionMeters = 3f,
                            BeamWidthRadians =
                                beamWidthDegrees * Mathf.Deg2Rad,
                            VelocityResolutionMps = 0.75f,
                        },
                        Capability =
                            SensorCapability.Range |
                            SensorCapability.Doppler |
                            SensorCapability.LosRate,
                        MaxRangeMeters =
                            Mathf.Max(100f, maximumRangeMeters),
                        FrequencyBand = 1,
                        WaveformId = 2,
                        MainlobePowerDb = 56f,
                        SidelobeLevelDb = -32f,
                        ClutterPowerDb = -45f,
                        Resolutions =
                            new ResolutionMerge.Resolutions
                            {
                                RangeMeters = 3f,
                                AngleRadians = 0.5f * Mathf.Deg2Rad,
                                VelocityMps = 0.75f,
                            },
                    });

                if (registerAsHostileRadarTarget)
                {
                    driver.RegisterRadarTarget(new RadarTargetProfile
                    {
                        EntityId = entity,
                        RadarCrossSectionSquareMeters = 0.04f,
                        ObjectClass = TrackObjectClass.Missile,
                        ClassificationSignature = 0.9f,
                    });
                    driver.RegisterElectroOpticalTarget(
                        new ElectroOpticalTargetProfile
                        {
                            EntityId = entity,
                            SignatureDb = 30f,
                            ReferenceDiameterMeters = 0.5f,
                            ObjectClass =
                                TrackObjectClass.Missile,
                            ClassificationSignature = 0.9f,
                        });
                }

                auxiliarySensorAllocated[index] = true;
                handle = new MissileSeekerHandle(
                    entity,
                    sensor,
                    registerAsHostileRadarTarget);
                return true;
            }

            return false;
        }

        public void UpdateMissileSeekerState(
            in MissileSeekerHandle handle,
            Vector3 position,
            Vector3 velocity)
        {
            if (driver == null || !handle.IsValid)
            {
                return;
            }

            driver.SetEntityState(
                handle.EntityId,
                BuildState(position, velocity));
        }

        public void ScheduleMissileSeekerDwell(
            in MissileSeekerHandle handle,
            Vector3 direction,
            float beamWidthDegrees,
            ulong sequence)
        {
            if (driver == null ||
                !handle.IsValid ||
                direction.sqrMagnitude <= 0.001f)
            {
                return;
            }

            driver.ScheduleRadarDwell(new Dwell
            {
                SensorId = handle.SensorId,
                DueTime = SimTimeSeconds,
                DwellTimeSeconds = 0.004f,
                BeamDirection = ToFloat3(direction.normalized),
                BeamWidthRadians =
                    Mathf.Max(0.1f, beamWidthDegrees) * Mathf.Deg2Rad,
                Priority = DwellPriority.MissileTerminal,
                Sequence = sequence,
            });
        }

        public int CopyMissileSeekerMeasurements(
            in MissileSeekerHandle handle,
            Measurement[] destination) =>
            driver != null && handle.IsValid
                ? driver.CopyDetections(handle.SensorId, destination)
                : 0;

        public void UnregisterMissileSeeker(
            in MissileSeekerHandle handle)
        {
            if (driver == null || !handle.IsValid)
            {
                return;
            }

            driver.UnregisterRadarSensor(handle.SensorId);
            if (handle.RadarTargetRegistered)
            {
                driver.UnregisterRadarTarget(handle.EntityId);
                driver.UnregisterElectroOpticalTarget(
                    handle.EntityId);
            }
            driver.UnregisterEntity(handle.EntityId);
            var index = handle.SensorId.Value.Index;
            if ((uint)index < auxiliarySensorAllocated.Length)
            {
                auxiliarySensorAllocated[index] = false;
                auxiliarySensorGenerations[index]++;
                if (auxiliarySensorGenerations[index] == 0)
                {
                    auxiliarySensorGenerations[index] = 1;
                }
            }
        }

        public int CopyTracks(TrackSnapshot[] destination) =>
            driver != null ? driver.CopyTracks(destination) : 0;

        public int CopyMissileTracks(MissileTrackSnapshot[] destination)
        {
            if (destination == null || destination.Length == 0 ||
                driver == null)
            {
                return 0;
            }

            var now = SimTimeSeconds;
            if (double.IsNaN(missileTrackCacheTime) ||
                Math.Abs(now - missileTrackCacheTime) > 1e-9)
            {
                var trackCount = CopyTracks(trackScratch);
                var threatCount = CopyThreats(threatScratch);
                var electroOpticalCount =
                    driver.CopyElectroOpticalObservations(
                        electroOpticalScratch);
                missileTrackCacheCount = MissileTrackFusion.Fuse(
                    in missileTrackFusionConfig,
                    missileTrackFusionState,
                    trackScratch,
                    trackCount,
                    threatScratch,
                    threatCount,
                    electroOpticalScratch,
                    electroOpticalCount,
                    ToDouble3(transform.position),
                    ToDouble3(playerVelocity),
                    now,
                    missileTrackCache);
                missileTrackCacheTime = now;
            }

            var copyCount = Math.Min(
                missileTrackCacheCount,
                destination.Length);
            Array.Copy(
                missileTrackCache,
                destination,
                copyCount);
            return copyCount;
        }

        public bool TryGetMissileTrack(
            TrackId trackId,
            out MissileTrackSnapshot snapshot)
        {
            snapshot = default;
            if (!trackId.IsValid)
            {
                return false;
            }

            var count = CopyMissileTracks(missileTrackScratch);
            for (var i = 0; i < count; i++)
            {
                if (missileTrackScratch[i].TrackId.Equals(trackId))
                {
                    snapshot = missileTrackScratch[i];
                    return true;
                }
            }

            return false;
        }

        public int ThreatCount => driver != null ? driver.ThreatCount : 0;

        public int CopyThreats(ThreatSnapshot[] destination) =>
            driver != null ? driver.CopyThreats(destination) : 0;

        public bool TryGetHighestThreat(out ThreatSnapshot snapshot)
        {
            snapshot = default;
            return driver != null && driver.TryGetHighestThreat(out snapshot);
        }

        public bool TryGetTrackAtViewport(
            Camera viewCamera,
            Vector2 viewportPoint,
            out TrackSnapshot selectedTrack)
        {
            selectedTrack = default;
            if (viewCamera == null)
            {
                return false;
            }

            var count = CopyTracks(trackScratch);
            var captureHalfSize = DesignationCaptureBoxPixels * 0.5f;
            var pixelWidth = Mathf.Max(1f, viewCamera.pixelWidth);
            var pixelHeight = Mathf.Max(1f, viewCamera.pixelHeight);
            var bestScore = float.MaxValue;
            var found = false;
            for (var i = 0; i < count; i++)
            {
                var track = trackScratch[i];
                var selectedByFireControl =
                    track.TrackId.Equals(sttTrackId) ||
                    track.TrackId.Equals(candidateTrackId);
                var visibleNormalTrack =
                    track.Confidence >= 0.35f &&
                    (track.Lifecycle == TrackLifecycle.Confirmed ||
                     (track.Lifecycle == TrackLifecycle.Coasting &&
                      track.CoastCount <= 4));
                if ((!selectedByFireControl && !visibleNormalTrack) ||
                    !IsFireControlUsable(track.Lifecycle))
                {
                    continue;
                }

                var viewport = viewCamera.WorldToViewportPoint(
                    PredictTrackPosition(track));
                if (viewport.z <= 0f)
                {
                    continue;
                }

                var dxPixels = (viewport.x - viewportPoint.x) * pixelWidth;
                var dyPixels = (viewport.y - viewportPoint.y) * pixelHeight;
                if (Mathf.Abs(dxPixels) > captureHalfSize ||
                    Mathf.Abs(dyPixels) > captureHalfSize)
                {
                    continue;
                }

                var score = dxPixels * dxPixels + dyPixels * dyPixels;
                if (score >= bestScore)
                {
                    continue;
                }

                bestScore = score;
                selectedTrack = track;
                found = true;
            }

            return found;
        }

        public int CopyEchoes(RadarEchoSnapshot[] destination)
        {
            if (destination == null || driver == null)
            {
                return 0;
            }

            // 只取主雷达自己的量测：探测环里混着导弹导引头的回波，它们以导弹为
            // 原点，用玩家位置重建会在 HUD 上画出幽灵回波。
            var count = driver.CopyDetections(sensorId, measurementScratch);
            count = Mathf.Min(count, destination.Length);
            for (var i = 0; i < count; i++)
            {
                var measurement = measurementScratch[i];
                destination[i] = new RadarEchoSnapshot
                {
                    EstimatedPosition = ToDouble3(transform.position)
                                        + new double3(
                                            measurement.Direction.x,
                                            measurement.Direction.y,
                                            measurement.Direction.z) * measurement.Range,
                    Timestamp = measurement.Timestamp,
                    SnrDb = measurement.SnrDb,
                    Flags = measurement.Flags,
                    Source = (measurement.Flags & MeasurementFlags.Passive) != 0
                        ? TrackSourceMask.Esm
                        : TrackSourceMask.Radar,
                };
            }

            return count;
        }

        public RadarStatusSnapshot GetStatusSnapshot()
        {
            var flags = RadarStatusFlags.None;
            var count = CopyTracks(trackScratch);
            var hasTwsTrack = false;
            for (var i = 0; i < count; i++)
            {
                if (trackScratch[i].Lifecycle == TrackLifecycle.Confirmed ||
                    trackScratch[i].Lifecycle == TrackLifecycle.Coasting ||
                    trackScratch[i].Lifecycle == TrackLifecycle.Degraded)
                {
                    hasTwsTrack = true;
                }

                if (trackScratch[i].InterferenceScore > 0.25f)
                {
                    flags |= RadarStatusFlags.Jammed;
                }

                if (trackScratch[i].Lifecycle == TrackLifecycle.Degraded)
                {
                    flags |= RadarStatusFlags.TrackDegraded;
                }
            }

            var scheduledDwells = driver != null ? driver.ScheduledDwellCount : 0;
            if (scheduledDwells > RadarSensorSystem.DwellBudgetPerTick)
            {
                flags |= RadarStatusFlags.Overloaded;
            }

            var usedChannels = 0;
            for (var i = 0; i < guidanceChannels.Length; i++)
            {
                if (guidanceChannels[i].ReferenceCount > 0)
                {
                    usedChannels++;
                }
            }

            var hasStt = HasStt;
            return new RadarStatusSnapshot
            {
                Mode = hasStt
                    ? RadarOperatingMode.SingleTargetTrack
                    : hasTwsTrack
                        ? RadarOperatingMode.TrackWhileScan
                        : RadarOperatingMode.Search,
                RangeMeters = RadarRangeMeters,
                ScanAzimuthDegrees = SearchAzimuthDegrees,
                ScanElevationDegrees = SearchElevationDegrees,
                BeamAzimuthDegrees = beamAzimuthDegrees,
                BeamElevationDegrees = beamElevationDegrees,
                BeamWidthDegrees = SearchBeamWidthDegrees,
                SearchFrameProgress = searchPattern.FrameProgress,
                SearchColumnCount = searchPattern.ColumnCount,
                SearchRowCount = searchPattern.RowCount,
                TrackCount = count,
                TrackCapacity = driver != null ? driver.TrackCapacity : MaximumPublicTracks,
                RefreshRateHz = GetSearchFrameRate(hasStt),
                TargetRefreshRateHz = hasStt
                    ? 1f / Mathf.Max(SttIntervalSeconds, (float)SimulationDriver.SensorStepSeconds)
                    : hasTwsTrack
                        ? 1f / TwsRevisitIntervalSeconds
                        : GetSearchFrameRate(false),
                // 显示用近似：排队中的驻留 + 本 tick 常态负载相对全局仿真预算的占比。
                DwellLoad = Mathf.Clamp01(
                    (scheduledDwells + (hasStt ? 5f : 7f)) /
                    RadarSensorSystem.DwellBudgetPerTick),
                DeferredDwellCount = driver != null ? driver.DeferredDwellCount : 0,
                GuidanceChannelsUsed = usedChannels,
                GuidanceChannelCapacity = GuidanceChannelCapacity,
                OwnshipVelocity = ToDouble3(playerVelocity),
                SttTrackId = hasStt ? sttTrackId : default,
                SttProgress = designationProgress,
                Flags = flags,
            };
        }

        public bool TryGetBestTrack(out TrackSnapshot snapshot)
        {
            snapshot = default;
            return driver != null && driver.TryGetBestTrack(out snapshot);
        }

        public bool TryGetTrack(TrackId trackId, out TrackSnapshot snapshot)
        {
            snapshot = default;
            return driver != null && driver.TryGetTrack(trackId, out snapshot);
        }

        public bool TryReacquireTrack(
            in TrackSnapshot previousTrack,
            float basePositionGateMeters,
            out TrackSnapshot snapshot)
        {
            snapshot = default;
            if (!TryGetBestTrack(out var candidate))
            {
                return false;
            }

            var previousPosition = PredictTrackPosition(previousTrack);
            var candidatePosition = PredictTrackPosition(candidate);
            var previousAge = math.max(0.0, SimTimeSeconds - previousTrack.LastUpdateTime);
            var positionGate = math.max(
                basePositionGateMeters,
                basePositionGateMeters + math.length(previousTrack.Velocity) * previousAge * 0.5);
            if (math.distancesq(ToDouble3(previousPosition), ToDouble3(candidatePosition)) >
                positionGate * positionGate)
            {
                return false;
            }

            snapshot = candidate;
            return true;
        }

        /// <summary>
        /// Updates the player designation cursor. Only confirmed public tracks participate.
        /// </summary>
        public void UpdateDesignation(Camera viewCamera, Vector2 reticleViewport, float deltaTime)
        {
            if (!trackOperationMode || viewCamera == null)
            {
                return;
            }

            var count = CopyTracks(trackScratch);
            var bestId = default(TrackId);
            var bestScore = float.MaxValue;
            var captureHalfSize = DesignationCaptureBoxPixels * 0.5f;
            var pixelWidth = Mathf.Max(1f, viewCamera.pixelWidth);
            var pixelHeight = Mathf.Max(1f, viewCamera.pixelHeight);
            for (var i = 0; i < count; i++)
            {
                var track = trackScratch[i];
                if (track.Lifecycle != TrackLifecycle.Confirmed)
                {
                    continue;
                }

                var position = PredictTrackPosition(track);
                var viewport = viewCamera.WorldToViewportPoint(position);
                if (viewport.z <= 0f)
                {
                    continue;
                }

                if (!IsWithinBodyRadarFrame(position - transform.position))
                {
                    continue;
                }

                var dxPixels = (viewport.x - reticleViewport.x) * pixelWidth;
                var dyPixels = (viewport.y - reticleViewport.y) * pixelHeight;
                if (Mathf.Abs(dxPixels) > captureHalfSize ||
                    Mathf.Abs(dyPixels) > captureHalfSize)
                {
                    continue;
                }

                var score = dxPixels * dxPixels + dyPixels * dyPixels;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestId = track.TrackId;
                }
            }

            // The reticle center must remain inside the same 54 px box drawn around a normal track.
            if (!bestId.IsValid)
            {
                candidateTrackId = default;
                designationProgress = 0f;
                return;
            }

            if (!candidateTrackId.Equals(bestId))
            {
                candidateTrackId = bestId;
                designationProgress = 0f;
            }

            if (!TryGetTrack(candidateTrackId, out var candidate))
            {
                designationProgress = 0f;
                return;
            }

            var requiredSeconds = Mathf.Lerp(
                0.45f,
                0.22f,
                Mathf.Clamp01(candidate.Confidence * (1f - candidate.InterferenceScore)));
            designationProgress = Mathf.Clamp01(
                designationProgress + deltaTime / Mathf.Max(requiredSeconds, DesignationBaseSeconds * 0.5f));
            if (designationProgress < 1f)
            {
                return;
            }

            // A failed designation remains a TWS candidate. It is never deleted by the UI.
            if (candidate.Confidence >= 0.25f &&
                candidate.Lifecycle == TrackLifecycle.Confirmed)
            {
                sttTrackId = candidate.TrackId;
            }
        }

        public void CycleCandidate(int direction)
        {
            var count = CopyTracks(trackScratch);
            if (count == 0)
            {
                return;
            }

            var current = -1;
            for (var i = 0; i < count; i++)
            {
                if (trackScratch[i].TrackId.Equals(candidateTrackId))
                {
                    current = i;
                    break;
                }
            }

            for (var step = 1; step <= count; step++)
            {
                var index = (current + direction * step) % count;
                if (index < 0) index += count;
                if (IsFireControlUsable(trackScratch[index].Lifecycle))
                {
                    candidateTrackId = trackScratch[index].TrackId;
                    designationProgress = 0f;
                    return;
                }
            }
        }

        public bool TryGetFireSolution(
            float coneDegrees,
            double projectileSpeed,
            out TrackSnapshot track,
            out FireSolution solution)
        {
            solution = default;
            if (!TryGetFireControlTrack(out track))
            {
                return false;
            }

            solution = FireSolutionSolver.Solve(
                track,
                ToDouble3(transform.position),
                ToDouble3(playerVelocity),
                projectileSpeed,
                SimTimeSeconds);
            if (!solution.WeaponReleaseAuthorized)
            {
                return false;
            }

            var ray = GetAimRay();
            var toIntercept = ToVector3(solution.InterceptPoint) - ray.origin;
            return toIntercept.sqrMagnitude > 0.001f
                   && Vector3.Angle(ray.direction, toIntercept) <= coneDegrees;
        }

        public bool TryAuthorizeMissileLaunch(
            MissileGuidanceMode guidanceMode,
            float coneDegrees,
            in MissileKinematicProfile kinematics,
            out TrackSnapshot track,
            out FireSolution solution,
            out int guidanceChannelToken)
        {
            guidanceChannelToken = 0;
            if (!TryGetMissileFireSolution(
                    coneDegrees,
                    kinematics,
                    out track,
                    out solution))
            {
                return false;
            }

            if (guidanceMode == MissileGuidanceMode.SemiActiveRadar && !track.TrackId.Equals(sttTrackId))
            {
                return false;
            }

            if (guidanceMode == MissileGuidanceMode.Infrared)
            {
                return solution.Quality >= 0.2f;
            }

            if (guidanceMode == MissileGuidanceMode.ActiveRadar ||
                guidanceMode == MissileGuidanceMode.Command)
            {
                return TryAcquireGuidanceChannel(track.TrackId, out guidanceChannelToken);
            }

            return true;
        }

        public bool TryGetMissileFireSolution(
            float coneDegrees,
            in MissileKinematicProfile kinematics,
            out TrackSnapshot track,
            out FireSolution solution)
        {
            solution = default;
            if (!TryGetFireControlTrack(out track))
            {
                return false;
            }

            solution = FireSolutionSolver.SolveMissile(
                track,
                ToDouble3(transform.position),
                ToDouble3(playerVelocity),
                kinematics,
                SimTimeSeconds);
            if (!solution.WeaponReleaseAuthorized)
            {
                return false;
            }

            var ray = GetAimRay();
            var toIntercept =
                ToVector3(solution.InterceptPoint) - ray.origin;
            return toIntercept.sqrMagnitude > 0.001f &&
                   Vector3.Angle(ray.direction, toIntercept) <=
                   coneDegrees;
        }

        public void ReleaseGuidanceChannel(int token)
        {
            var index = token - 1;
            if ((uint)index >= guidanceChannels.Length ||
                guidanceChannels[index].ReferenceCount <= 0)
            {
                return;
            }

            guidanceChannels[index].ReferenceCount--;
            if (guidanceChannels[index].ReferenceCount == 0)
            {
                guidanceChannels[index] = default;
            }
        }

        public bool RetainGuidanceChannel(int token)
        {
            var index = token - 1;
            if ((uint)index >= guidanceChannels.Length ||
                guidanceChannels[index].ReferenceCount <= 0)
            {
                return false;
            }

            guidanceChannels[index].ReferenceCount++;
            return true;
        }

        public Vector3 PredictTrackPosition(in TrackSnapshot track)
        {
            return ToVector3(track.PredictPosition(SimTimeSeconds));
        }

        private bool TryGetFireControlTrack(out TrackSnapshot track)
        {
            if (sttTrackId.IsValid && TryGetTrack(sttTrackId, out track) &&
                IsFireControlUsable(track.Lifecycle))
            {
                return true;
            }

            if (candidateTrackId.IsValid && TryGetTrack(candidateTrackId, out track) &&
                IsFireControlUsable(track.Lifecycle))
            {
                return true;
            }

            if (!TryGetBestTrack(out track))
            {
                return false;
            }

            return IsFireControlUsable(track.Lifecycle);
        }

        private static bool IsFireControlUsable(TrackLifecycle lifecycle) =>
            lifecycle == TrackLifecycle.Confirmed ||
            lifecycle == TrackLifecycle.Coasting ||
            lifecycle == TrackLifecycle.Degraded;

        private bool TryAcquireGuidanceChannel(TrackId trackId, out int token)
        {
            for (var i = 0; i < guidanceChannels.Length; i++)
            {
                if (guidanceChannels[i].ReferenceCount > 0 &&
                    guidanceChannels[i].TrackId.Equals(trackId))
                {
                    guidanceChannels[i].ReferenceCount++;
                    token = i + 1;
                    return true;
                }
            }

            for (var i = 0; i < guidanceChannels.Length; i++)
            {
                if (guidanceChannels[i].ReferenceCount != 0)
                {
                    continue;
                }

                guidanceChannels[i] = new GuidanceChannel
                {
                    TrackId = trackId,
                    ReferenceCount = 1,
                };
                token = i + 1;
                return true;
            }

            token = 0;
            return false;
        }

        private void Awake()
        {
            if (aimingCamera == null) aimingCamera = Camera.main;
            flightMotion = GetComponent<MechFlightMotionController>();
        }

        private void FixedUpdate()
        {
            EnsureInitialized();
            if (!initialized)
            {
                return;
            }

            var dt = Mathf.Max(Time.fixedDeltaTime, 1e-4f);
            var playerPosition = transform.position;
            playerVelocity = flightMotion != null
                ? flightMotion.Velocity
                : (playerPosition - previousPlayerPosition) / dt;
            driver.SetEntityState(playerEntity, BuildState(transform, playerVelocity));
            var electroOpticalForward =
                aimingCamera != null
                    ? aimingCamera.transform.forward
                    : transform.forward;
            driver.UpdateElectroOpticalForward(
                ToFloat3(electroOpticalForward));
            previousPlayerPosition = playerPosition;

            for (var i = 0; i < sceneTargetCount; i++)
            {
                var sceneTarget = sceneTargets[i];
                var source = sceneTarget != null ? sceneTarget.AimPoint : null;
                var position = source != null ? source.position : previousTargetPositions[i];
                var targetMotion = sceneTargetMotions[i];
                var velocity = targetMotion != null
                    ? targetMotion.Velocity
                    : (position - previousTargetPositions[i]) / dt;
                driver.SetEntityState(targetEntities[i], BuildState(source, velocity));
                previousTargetPositions[i] = position;
                UpdateTargetRegistration(i);
            }

            ValidateSttTrack();
            ScheduleDwells();
            PumpGuidanceUplinks(driver.SimTimeSeconds);
        }

        private void ScheduleDwells()
        {
            var now = driver.SimTimeSeconds;
            var aimRay = GetAimRay();
            var hasStt = HasStt;
            if (now + 1e-6 >= nextSearchDwellTime)
            {
                ScheduleSearchBatch(
                    now,
                    aimRay,
                    hasStt ? SttSearchDwellsPerTick : SearchDwellsPerTick);
                nextSearchDwellTime = now + SearchIntervalSeconds;
            }

            ScheduleMissileWarningCues(now);
            SchedulePointDefenseRevisit(now);
            ScheduleTwsRevisits(now, hasStt);

            if (!hasStt || now + 1e-6 < nextSttDwellTime ||
                !TryGetTrack(sttTrackId, out var sttTrack))
            {
                return;
            }

            var sttPosition = PredictTrackPosition(sttTrack);
            var direction = (sttPosition - transform.position).normalized;
            if (direction.sqrMagnitude < 0.5f ||
                !IsWithinBodyRadarFrame(direction))
            {
                BreakSingleTargetTrack();
                return;
            }

            driver.ScheduleRadarDwell(new Dwell
            {
                SensorId = sensorId,
                DueTime = now,
                DwellTimeSeconds = 0.028f,
                BeamDirection = direction,
                BeamWidthRadians = SttBeamWidthDegrees * Mathf.Deg2Rad,
                Priority = DwellPriority.SingleTargetTrack,
                Sequence = ++dwellSequence,
            });
            nextSttDwellTime = now + SttIntervalSeconds;
        }

        private void ScheduleMissileWarningCues(double now)
        {
            if (now + 1e-6 < nextMissileCueDwellTime)
            {
                return;
            }

            nextMissileCueDwellTime =
                now + MissileCueIntervalSeconds;
            var threatCount = CopyThreats(threatScratch);
            var scheduled = 0;
            for (var i = 0;
                 i < threatCount &&
                 scheduled < MissileCueDwellsPerTick;
                 i++)
            {
                var threat = threatScratch[i];
                if ((threat.WaveformClass !=
                         EsmWaveformClass.MissileSeeker &&
                     threat.WaveformClass !=
                         EsmWaveformClass.InfraredPlume) ||
                    now - threat.LastSeenTime >
                        MissileCueMaximumAgeSeconds)
                {
                    continue;
                }

                var direction =
                    ToVector3(threat.DirectionWorld);
                if (!IsWithinBodyRadarFrame(direction))
                {
                    continue;
                }

                driver.ScheduleRadarDwell(new Dwell
                {
                    SensorId = sensorId,
                    DueTime = now,
                    DwellTimeSeconds = 0.012f,
                    BeamDirection =
                        ToFloat3(direction.normalized),
                    BeamWidthRadians =
                        MissileCueBeamWidthDegrees *
                        Mathf.Deg2Rad,
                    Priority = DwellPriority.AlertConfirm,
                    Sequence = ++dwellSequence,
                });
                scheduled++;
            }
        }

        private void SchedulePointDefenseRevisit(double now)
        {
            if (!pointDefenseCueTrackId.IsValid ||
                now > pointDefenseCueUntilTime ||
                now + 1e-6 < nextPointDefenseDwellTime)
            {
                return;
            }

            if (!TryGetTrack(
                    pointDefenseCueTrackId,
                    out var pointDefenseTrack))
            {
                pointDefenseCueTrackId = default;
                pointDefenseCueUntilTime = double.NegativeInfinity;
                return;
            }

            var trackedPosition =
                PredictTrackPosition(pointDefenseTrack);
            var direction =
                (trackedPosition - transform.position).normalized;
            if (direction.sqrMagnitude < 0.5f ||
                !IsWithinBodyRadarFrame(direction))
            {
                return;
            }

            driver.ScheduleRadarDwell(new Dwell
            {
                SensorId = sensorId,
                DueTime = now,
                DwellTimeSeconds = 0.024f,
                BeamDirection = direction,
                BeamWidthRadians =
                    PointDefenseBeamWidthDegrees * Mathf.Deg2Rad,
                Priority = DwellPriority.SingleTargetTrack,
                Sequence = ++dwellSequence,
            });
            nextPointDefenseDwellTime =
                now + PointDefenseRevisitIntervalSeconds;
        }

        private void ScheduleSearchBatch(double now, Ray aimRay, int dwellCount)
        {
            for (var i = 0; i < dwellCount; i++)
            {
                if (searchPattern.IsFrameStart)
                {
                    CaptureSearchFrame(aimRay);
                }

                var cell = searchPattern.Next();
                var direction = SearchDirection(cell.x, cell.y);
                driver.ScheduleRadarDwell(new Dwell
                {
                    SensorId = sensorId,
                    DueTime = now,
                    DwellTimeSeconds = 0.008f,
                    BeamDirection = direction,
                    BeamWidthRadians = SearchBeamWidthDegrees * Mathf.Deg2Rad,
                    Priority = DwellPriority.WideSearch,
                    Sequence = ++dwellSequence,
                });
                UpdateBeamAngles(direction);
            }
        }

        private void ScheduleTwsRevisits(double now, bool hasStt)
        {
            var count = CopyTracks(trackScratch);
            Span<bool> pickedThisCall = stackalloc bool[MaximumPublicTracks];
            for (var scheduled = 0; scheduled < TwsRevisitsPerTick; scheduled++)
            {
                // 每轮挑"最久未访"的到期航迹，而不是按槽位序取前 N 个——
                // 槽位序在满载时会让高索引航迹饥饿。
                var bestIndex = -1;
                var bestDueTime = double.MaxValue;
                for (var i = 0; i < count; i++)
                {
                    if (pickedThisCall[i])
                    {
                        continue;
                    }

                    var track = trackScratch[i];
                    if (track.Lifecycle == TrackLifecycle.Dropped ||
                        track.TrackId.Equals(sttTrackId))
                    {
                        continue;
                    }

                    var slot = track.TrackId.Value.Index;
                    if ((uint)slot >= nextTwsRevisitTimes.Length)
                    {
                        continue;
                    }

                    if (twsRevisitTrackKeys[slot] != track.TrackId.Packed)
                    {
                        twsRevisitTrackKeys[slot] = track.TrackId.Packed;
                        nextTwsRevisitTimes[slot] = now;
                    }

                    if (now + 1e-6 < nextTwsRevisitTimes[slot] ||
                        nextTwsRevisitTimes[slot] >= bestDueTime)
                    {
                        continue;
                    }

                    bestDueTime = nextTwsRevisitTimes[slot];
                    bestIndex = i;
                }

                if (bestIndex < 0)
                {
                    return;
                }

                pickedThisCall[bestIndex] = true;
                var selected = trackScratch[bestIndex];
                var selectedSlot = selected.TrackId.Value.Index;
                var predictedPosition = PredictTrackPosition(selected);
                var direction = (predictedPosition - transform.position).normalized;
                if (direction.sqrMagnitude < 0.5f)
                {
                    continue;
                }

                driver.ScheduleRadarDwell(new Dwell
                {
                    SensorId = sensorId,
                    DueTime = now,
                    DwellTimeSeconds = selected.Lifecycle == TrackLifecycle.Tentative
                        ? 0.012f
                        : 0.016f,
                    BeamDirection = direction,
                    BeamWidthRadians = TwsBeamWidthDegrees * Mathf.Deg2Rad,
                    Priority = selected.Lifecycle == TrackLifecycle.Tentative
                        ? DwellPriority.AlertConfirm
                        : DwellPriority.TrackWhileScan,
                    Sequence = ++dwellSequence,
                });
                nextTwsRevisitTimes[selectedSlot] = now +
                    (hasStt ? TwsRevisitIntervalSeconds * 1.5f : TwsRevisitIntervalSeconds);
            }
        }

        private void CaptureSearchFrame(Ray aimRay)
        {
            searchFrameForward = aimRay.direction.normalized;
            var referenceUp = aimingCamera != null
                ? aimingCamera.transform.up
                : transform.up;
            searchFrameRight = Vector3.Cross(referenceUp, searchFrameForward).normalized;
            if (searchFrameRight.sqrMagnitude < 0.5f)
            {
                searchFrameRight = transform.right;
            }

            searchFrameUp = Vector3.Cross(searchFrameForward, searchFrameRight).normalized;
        }

        private Vector3 SearchDirection(float azimuthDegrees, float elevationDegrees)
        {
            // 球面组合而非 tan 叠加：tan 叠加在大方位角处把实际仰角压缩 1/cos(az)
            // （az=50°、el=30° 的格子实际仰角只有 ~20°），而 IsWithinBodyRadarFrame
            // 按真实球面角放行 ±50°/±30° 全域——四角会成为永远扫不到的盲区。
            // 此写法保证 atan2(x,z)=az、asin(y)=el 与判定/显示换算严格一致。
            var azimuth = azimuthDegrees * Mathf.Deg2Rad;
            var elevation = elevationDegrees * Mathf.Deg2Rad;
            var cosElevation = Mathf.Cos(elevation);
            return (
                searchFrameRight * (Mathf.Sin(azimuth) * cosElevation) +
                searchFrameUp * Mathf.Sin(elevation) +
                searchFrameForward * (Mathf.Cos(azimuth) * cosElevation)).normalized;
        }

        private float GetSearchFrameRate(bool hasStt)
        {
            var dwellsPerTick = hasStt
                ? SttSearchDwellsPerTick
                : SearchDwellsPerTick;
            var frameSeconds =
                searchPattern.CellCount * SearchIntervalSeconds / dwellsPerTick;
            return frameSeconds > 0f ? 1f / frameSeconds : 0f;
        }

        private void ValidateSttTrack()
        {
            if (!sttTrackId.IsValid)
            {
                return;
            }

            if (!TryGetTrack(sttTrackId, out var track) ||
                track.Lifecycle == TrackLifecycle.Dropped ||
                SimTimeSeconds - track.LastUpdateTime > 3.0 ||
                !IsWithinBodyRadarFrame(
                    PredictTrackPosition(track) - transform.position))
            {
                BreakSingleTargetTrack();
            }
        }

        private bool IsWithinBodyRadarFrame(Vector3 worldDirection)
        {
            if (worldDirection.sqrMagnitude < 0.0001f)
            {
                return false;
            }

            if (flightMotion == null)
            {
                flightMotion = GetComponent<MechFlightMotionController>();
            }

            var frameRotation = flightMotion != null
                ? flightMotion.LogicalRotation
                : transform.rotation;
            var localDirection =
                Quaternion.Inverse(frameRotation) * worldDirection.normalized;
            if (localDirection.z <= 0f)
            {
                return false;
            }

            var azimuth = Mathf.Atan2(
                localDirection.x,
                localDirection.z) * Mathf.Rad2Deg;
            var horizontalLength = Mathf.Sqrt(
                localDirection.x * localDirection.x +
                localDirection.z * localDirection.z);
            var elevation = Mathf.Atan2(
                localDirection.y,
                horizontalLength) * Mathf.Rad2Deg;
            return Mathf.Abs(azimuth) <= SttAzimuthLimitDegrees &&
                   Mathf.Abs(elevation) <= SttElevationLimitDegrees;
        }

        private void EnsureInitialized()
        {
            if (initialized)
            {
                return;
            }

            if (aimingCamera == null) aimingCamera = Camera.main;
            driver = FindAnyObjectByType<SimulationDriver>();
            if (driver == null)
            {
                driver = new GameObject("AC6_Radar_Simulation").AddComponent<SimulationDriver>();
            }

            previousPlayerPosition = transform.position;
            playerEntity = driver.RegisterEntity(BuildState(transform, Vector3.zero));
            DiscoverTargets();
            driver.RegisterRadarSensor(new RadarSensorProfile
            {
                SensorId = sensorId,
                EmitterId = new EmitterId(new Handle(0, 1)),
                PlatformId = new PlatformId(new Handle(0, 1)),
                OwnerEntityId = playerEntity,
                DwellConfig = new RadarDwellConfig
                {
                    ConstantsDb = 215f,
                    IntegrationGainDb = 6f,
                    NoisePowerDb = 0f,
                    DetectThresholdLowDb = 3f,
                    DetectThresholdHighDb = 10f,
                    RangeResolutionMeters = 12f,
                    BeamWidthRadians = SearchBeamWidthDegrees * Mathf.Deg2Rad,
                    VelocityResolutionMps = 2f,
                },
                Capability = SensorCapability.Range | SensorCapability.Doppler | SensorCapability.LosRate,
                MaxRangeMeters = RadarRangeMeters,
                FrequencyBand = 1,
                WaveformId = 1,
                MainlobePowerDb = 70f,
                SidelobeLevelDb = -25f,
                ClutterPowerDb = -30f,
                Resolutions = new ResolutionMerge.Resolutions
                {
                    RangeMeters = 12f,
                    AngleRadians = 1.5f * Mathf.Deg2Rad,
                    VelocityMps = 2f,
                },
            });
            // 本机数据链终端（制导网 0）：定向上行 20° 波束指向导弹，功率低于雷达
            // 主瓣——敌方 ESM 能在多远听到上行由这两个数值决定。
            driver.RegisterLinkTerminal(new LinkTerminalProfile
            {
                TerminalId = ownLinkTerminalId,
                OwnerEntityId = playerEntity,
                EmitterId = new EmitterId(new Handle(LinkEmitterIndexBase + 1, 1)),
                NetworkId = GuidanceNetworkId,
                TransmitPowerDb = 52f,
                SidelobeLevelDb = -30f,
                BeamWidthRadians = 20f * Mathf.Deg2Rad,
                ReceiveSensitivityDb = -60f,
                MaxRangeMeters = 150_000.0,
                FrequencyBand = 2,
                WaveformId = 10,
            });

            // 被动告警接收机：消隐本机雷达与本机链路终端；本机导弹导引头的辐射
            // 照样能听到（真实 RWR 分不清敌我辐射源，主瓣朝外时多数被阈值滤掉）。
            driver.RegisterEsmReceiver(new EsmReceiverProfile
            {
                ReceiverId = esmReceiverId,
                OwnerEntityId = playerEntity,
                BlankedEmitterId = new EmitterId(new Handle(0, 1)),
                BlankedEmitterId2 = new EmitterId(new Handle(LinkEmitterIndexBase + 1, 1)),
                SensitivityFloorDb = -60f,
                BearingSigmaRadians = 8f * Mathf.Deg2Rad,
            });
            driver.RegisterElectroOpticalReceiver(
                new ElectroOpticalReceiverProfile
                {
                    SensorId = electroOpticalReceiverId,
                    OwnerEntityId = playerEntity,
                    Forward = ToFloat3(
                        aimingCamera != null
                            ? aimingCamera.transform.forward
                            : transform.forward),
                    FieldOfViewRadians =
                        (aimingCamera != null
                            ? aimingCamera.fieldOfView
                            : 60f) *
                        Mathf.Deg2Rad,
                    MaximumRangeMeters = 30_000.0,
                    SensitivityFloorDb = -60f,
                    BearingSigmaRadians =
                        0.15f * Mathf.Deg2Rad,
                });
            RegisterWaveformLibrary();
            initialized = true;

            for (var i = 0; i < sceneTargetCount; i++)
            {
                UpdateTargetRegistration(i);
            }
        }

        [Serializable]
        private struct WaveformLibraryEntry
        {
            public int band;
            public int waveformId;
            public string className;
            public string name;
        }

        [Serializable]
        private struct WaveformLibraryFile
        {
            public WaveformLibraryEntry[] entries;
        }

        /// <summary>
        /// 波形库：代码内默认条目保底，StreamingAssets/EsmWaveformLibrary.json
        /// 存在时逐条覆盖/追加（P2 数据文件化——威胁类型扩展不再改代码）。
        /// </summary>
        private void RegisterWaveformLibrary()
        {
            driver.RegisterEsmWaveformClass(1, 1, EsmWaveformClass.FireControlRadar);
            driver.RegisterEsmWaveformClass(1, 2, EsmWaveformClass.MissileSeeker);
            driver.RegisterEsmWaveformClass(1, 3, EsmWaveformClass.SearchRadar);
            driver.RegisterEsmWaveformClass(2, 10, EsmWaveformClass.DataLinkUplink);
            driver.RegisterEsmWaveformClass(
                MawSensorSystem.InfraredBand,
                MawSensorSystem.PlumeWaveformId,
                EsmWaveformClass.InfraredPlume);

            var path = Path.Combine(Application.streamingAssetsPath, "EsmWaveformLibrary.json");
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                var file = JsonUtility.FromJson<WaveformLibraryFile>(File.ReadAllText(path));
                if (file.entries == null)
                {
                    return;
                }

                foreach (var entry in file.entries)
                {
                    if (Enum.TryParse<EsmWaveformClass>(entry.className, out var waveformClass))
                    {
                        driver.RegisterEsmWaveformClass(
                            (ushort)entry.band, (ushort)entry.waveformId, waveformClass);
                    }
                    else
                    {
                        Debug.LogWarning(
                            $"[MechRadar] 波形库条目类名无法解析: {entry.className} ({entry.name})", this);
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[MechRadar] 波形库加载失败，使用内置默认: {exception.Message}", this);
            }
        }

        // ---- 数据链：导弹终端分配 / 制导上行泵 / 红外源桥 ----

        public bool TryRegisterMissileLinkTerminal(
            in MissileSeekerHandle seekerHandle,
            out LinkTerminalId terminalId)
        {
            terminalId = default;
            if (driver == null || !seekerHandle.IsValid)
            {
                return false;
            }

            for (var index = MissileLinkIndexStart; index < missileLinkAllocated.Length; index++)
            {
                if (missileLinkAllocated[index])
                {
                    continue;
                }

                var generation = missileLinkGenerations[index];
                if (generation == 0)
                {
                    generation = 1;
                    missileLinkGenerations[index] = generation;
                }

                terminalId = new LinkTerminalId(new Handle(index, generation));
                driver.RegisterLinkTerminal(new LinkTerminalProfile
                {
                    TerminalId = terminalId,
                    OwnerEntityId = seekerHandle.EntityId,
                    EmitterId = new EmitterId(new Handle(LinkEmitterIndexBase + index, generation)),
                    NetworkId = GuidanceNetworkId,
                    // 导弹端暂只收不发；发射参数为将来回传/应答预留。
                    TransmitPowerDb = 40f,
                    SidelobeLevelDb = -30f,
                    BeamWidthRadians = 2f * Mathf.PI,
                    ReceiveSensitivityDb = -70f,
                    MaxRangeMeters = 150_000.0,
                    FrequencyBand = 2,
                    WaveformId = 10,
                });
                missileLinkAllocated[index] = true;
                return true;
            }

            return false;
        }

        public void UnregisterMissileLinkTerminal(LinkTerminalId terminalId)
        {
            if (driver == null || !terminalId.IsValid)
            {
                return;
            }

            EndGuidanceUplink(terminalId);
            driver.UnregisterLinkTerminal(terminalId);
            var index = terminalId.Value.Index;
            if (index >= MissileLinkIndexStart && index < missileLinkAllocated.Length)
            {
                missileLinkAllocated[index] = false;
                missileLinkGenerations[index]++;
                if (missileLinkGenerations[index] == 0)
                {
                    missileLinkGenerations[index] = 1;
                }
            }
        }

        public bool BeginGuidanceUplink(
            LinkTerminalId missileTerminal,
            TrackId trackId,
            bool allowTrackReacquisition = true)
        {
            if (!missileTerminal.IsValid || !trackId.IsValid)
            {
                return false;
            }

            for (var i = 0; i < uplinkSubscriptions.Length; i++)
            {
                if (uplinkSubscriptions[i].Active)
                {
                    continue;
                }

                var subscription = new UplinkSubscription
                {
                    Active = true,
                    AllowTrackReacquisition = allowTrackReacquisition,
                    MissileTerminal = missileTerminal,
                    TrackId = trackId,
                    NextSendTime = 0.0,
                };
                if (TryGetTrack(trackId, out var track))
                {
                    subscription.LastSent = track;
                }

                uplinkSubscriptions[i] = subscription;
                return true;
            }

            return false;
        }

        public void EndGuidanceUplink(LinkTerminalId missileTerminal)
        {
            for (var i = 0; i < uplinkSubscriptions.Length; i++)
            {
                if (uplinkSubscriptions[i].Active &&
                    uplinkSubscriptions[i].MissileTerminal.Equals(missileTerminal))
                {
                    uplinkSubscriptions[i] = default;
                }
            }
        }

        public int CopyLinkMessages(LinkTerminalId terminalId, LinkMessage[] destination) =>
            driver != null ? driver.CopyLinkInbox(terminalId, destination) : 0;

        public void RegisterMissileIrSource(in MissileSeekerHandle seekerHandle, float intensityDb)
        {
            if (driver != null && seekerHandle.IsValid)
            {
                driver.RegisterIrSource(seekerHandle.EntityId, intensityDb);
            }
        }

        public void SetMissileIrIntensity(in MissileSeekerHandle seekerHandle, float intensityDb)
        {
            if (driver != null && seekerHandle.IsValid)
            {
                driver.SetIrSourceIntensity(seekerHandle.EntityId, intensityDb);
            }
        }

        public void UnregisterMissileIrSource(in MissileSeekerHandle seekerHandle)
        {
            if (driver != null && seekerHandle.IsValid)
            {
                driver.UnregisterIrSource(seekerHandle.EntityId);
            }
        }

        /// <summary>
        /// 制导上行泵：按周期把航迹快照经物理链路发往在飞导弹。允许换目标的
        /// 攻击弹在原航迹丢失时可由发射端再捕获；单目标拦截弹保持原航迹承诺。
        /// </summary>
        private void PumpGuidanceUplinks(double now)
        {
            for (var i = 0; i < uplinkSubscriptions.Length; i++)
            {
                var subscription = uplinkSubscriptions[i];
                if (!subscription.Active || now + 1e-6 < subscription.NextSendTime)
                {
                    continue;
                }

                TrackSnapshot track = default;
                var haveTrack =
                    subscription.TrackId.IsValid &&
                    TryGetTrack(subscription.TrackId, out track) &&
                    IsFireControlUsable(track.Lifecycle) &&
                    (subscription.AllowTrackReacquisition ||
                     track.Lifecycle == TrackLifecycle.Confirmed);
                if (!haveTrack)
                {
                    if (subscription.AllowTrackReacquisition &&
                        subscription.LastSent.TrackId.IsValid &&
                        TryReacquireTrack(
                            subscription.LastSent,
                            GuidanceReacquisitionGateMeters,
                            out track))
                    {
                        subscription.TrackId = track.TrackId;
                    }
                    else
                    {
                        // 无可发的航迹：跳过本周期，链路静默（导弹端靠记忆外推）。
                        subscription.NextSendTime = now + GuidanceUplinkIntervalSeconds;
                        uplinkSubscriptions[i] = subscription;
                        continue;
                    }
                }

                subscription.LastSent = track;
                driver.SendLinkMessage(new LinkMessage
                {
                    Kind = LinkMessageKind.GuidanceUpdate,
                    NetworkId = GuidanceNetworkId,
                    SourceTerminal = ownLinkTerminalId,
                    TargetTerminal = subscription.MissileTerminal,
                    SendTime = now,
                    TrackPayload = track,
                });
                subscription.NextSendTime = now + GuidanceUplinkIntervalSeconds;
                uplinkSubscriptions[i] = subscription;
            }
        }

        private void DiscoverTargets()
        {
            var discovered = FindObjectsByType<CombatTestTarget>();
            if (configuredTarget != null)
            {
                AddSceneTarget(configuredTarget);
            }

            for (var i = 0; i < discovered.Length && sceneTargetCount < MaximumSceneTargets; i++)
            {
                AddSceneTarget(discovered[i]);
            }

            Debug.Log(
                $"[MechRadar] {name} registered {sceneTargetCount} scene targets.",
                this);
        }

        private void AddSceneTarget(CombatTestTarget sceneTarget)
        {
            if (sceneTarget == null || sceneTargetCount >= MaximumSceneTargets)
            {
                return;
            }
            if (sceneTarget.transform == transform ||
                sceneTarget.transform.IsChildOf(transform) ||
                transform.IsChildOf(sceneTarget.transform))
            {
                return;
            }

            for (var i = 0; i < sceneTargetCount; i++)
            {
                if (sceneTargets[i] == sceneTarget)
                {
                    return;
                }
            }

            var source = sceneTarget.AimPoint;
            sceneTargets[sceneTargetCount] = sceneTarget;
            sceneTargetMotions[sceneTargetCount] =
                sceneTarget.GetComponent<MechFlightMotionController>();
            previousTargetPositions[sceneTargetCount] =
                source != null ? source.position : sceneTarget.transform.position;
            targetEntities[sceneTargetCount] =
                driver.RegisterEntity(BuildState(source, Vector3.zero));
            Debug.Log(
                $"[MechRadar] target[{sceneTargetCount}] " +
                $"id={sceneTarget.TargetId} team={sceneTarget.TeamId} " +
                $"position={(source != null ? source.position : sceneTarget.transform.position)} " +
                $"rcs={sceneTarget.RadarCrossSectionSquareMeters:0.##}",
                this);
            sceneTargetCount++;

            if (autoEquipTestEmitters &&
                sceneTarget.GetComponent<MechTestRadarEmitter>() == null)
            {
                sceneTarget.gameObject.AddComponent<MechTestRadarEmitter>();
            }
        }

        private void UpdateTargetRegistration(int index)
        {
            var sceneTarget = sceneTargets[index];
            var shouldRegister = sceneTarget != null && sceneTarget.IsAlive;
            if (shouldRegister == targetRadarRegistered[index])
            {
                return;
            }

            if (shouldRegister)
            {
                driver.RegisterRadarTarget(new RadarTargetProfile
                {
                    EntityId = targetEntities[index],
                    RadarCrossSectionSquareMeters = sceneTarget.RadarCrossSectionSquareMeters,
                    ObjectClass = TrackObjectClass.AirVehicle,
                    ClassificationSignature = 0.85f,
                });
            }
            else
            {
                driver.UnregisterRadarTarget(targetEntities[index]);
            }

            targetRadarRegistered[index] = shouldRegister;
        }

        private void UpdateBeamAngles(Vector3 worldDirection)
        {
            var direction = worldDirection.normalized;
            var local = searchFrameForward.sqrMagnitude > 0.5f
                ? new Vector3(
                    Vector3.Dot(direction, searchFrameRight),
                    Vector3.Dot(direction, searchFrameUp),
                    Vector3.Dot(direction, searchFrameForward))
                : (aimingCamera != null
                    ? aimingCamera.transform.InverseTransformDirection(direction)
                    : transform.InverseTransformDirection(direction));
            beamAzimuthDegrees = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            beamElevationDegrees = Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg;
        }

        private Ray GetAimRay()
        {
            if (aimingCamera == null) aimingCamera = Camera.main;
            return aimingCamera != null
                ? aimingCamera.ViewportPointToRay(new Vector3(0.5f, 0.56f, 0f))
                : new Ray(transform.position, transform.forward);
        }

        private static KinematicState BuildState(Transform source, Vector3 velocity)
        {
            return new KinematicState
            {
                PositionWorld = source != null ? ToDouble3(source.position) : double3.zero,
                VelocityWorld = ToDouble3(velocity),
                Rotation = source != null
                    ? (quaternion)source.rotation
                    : quaternion.identity,
                AngularVelocity = float3.zero,
                Mass = 1.0,
            };
        }

        private static KinematicState BuildState(
            Vector3 position,
            Vector3 velocity)
        {
            return new KinematicState
            {
                PositionWorld = ToDouble3(position),
                VelocityWorld = ToDouble3(velocity),
            };
        }

        private static double3 ToDouble3(Vector3 value) =>
            new double3(value.x, value.y, value.z);

        private static float3 ToFloat3(Vector3 value) =>
            new float3(value.x, value.y, value.z);

        private static Vector3 ToVector3(double3 value) =>
            new Vector3((float)value.x, (float)value.y, (float)value.z);

        private static Vector3 ToVector3(float3 value) =>
            new Vector3(value.x, value.y, value.z);

        private void OnDestroy()
        {
            if (!initialized || driver == null)
            {
                return;
            }

            for (var i = 0; i < sceneTargetCount; i++)
            {
                driver.UnregisterRadarTarget(targetEntities[i]);
                driver.UnregisterEntity(targetEntities[i]);
            }

            driver.UnregisterEntity(playerEntity);
        }
    }
}
