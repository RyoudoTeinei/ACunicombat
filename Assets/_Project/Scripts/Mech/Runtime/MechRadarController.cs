using System;
using System.Collections.Generic;
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
    public enum MechTargetDamageCondition : byte
    {
        Unknown = 0,
        Nominal = 1,
        LightDamage = 2,
        ModerateDamage = 3,
        HeavyDamage = 4,
        CriticalDamage = 5,
    }

    public readonly struct MechTargetDamageAssessment
    {
        public MechTargetDamageAssessment(
            TrackId trackId,
            MechTargetDamageCondition condition,
            float confidence,
            double observationTime)
        {
            TrackId = trackId;
            Condition = condition;
            Confidence = Mathf.Clamp01(confidence);
            ObservationTime = observationTime;
        }

        public TrackId TrackId { get; }
        public MechTargetDamageCondition Condition { get; }
        public float Confidence { get; }
        public double ObservationTime { get; }
    }

    public static class MechTargetDamageAssessmentMath
    {
        public const float MinimumUsableSensorQuality = 0.2f;

        public static float CalculateObservedHealthFraction(
            float actualHealthFraction,
            float sensorQuality,
            float healthVariation)
        {
            var noisyHealth = Mathf.Clamp01(
                Mathf.Clamp01(actualHealthFraction) *
                Mathf.Clamp(healthVariation, 0.9f, 1.1f));
            var diagnosticGain = Mathf.Lerp(
                0.65f,
                1f,
                Mathf.Clamp01(sensorQuality));
            return Mathf.Clamp01(
                1f - (1f - noisyHealth) * diagnosticGain);
        }

        public static MechTargetDamageCondition Classify(
            float observedHealthFraction,
            float sensorQuality)
        {
            if (sensorQuality < MinimumUsableSensorQuality)
            {
                return MechTargetDamageCondition.Unknown;
            }

            var health = Mathf.Clamp01(observedHealthFraction);
            if (health >= 0.88f) return MechTargetDamageCondition.Nominal;
            if (health >= 0.68f) return MechTargetDamageCondition.LightDamage;
            if (health >= 0.43f) return MechTargetDamageCondition.ModerateDamage;
            if (health >= 0.18f) return MechTargetDamageCondition.HeavyDamage;
            return MechTargetDamageCondition.CriticalDamage;
        }

        public static float DeterministicHealthVariation(uint trackKey, uint sequence)
        {
            var value = trackKey ^ (sequence * 0x9E3779B9u) ^ 0xA511E9B3u;
            value ^= value >> 16;
            value *= 0x7FEB352Du;
            value ^= value >> 15;
            value *= 0x846CA68Bu;
            value ^= value >> 16;
            return Mathf.Lerp(
                0.9f,
                1.1f,
                (value & 0x00FFFFFFu) / 16777215f);
        }
    }

    public enum MechDataLinkPolicy : byte
    {
        Automatic = 0,
        SituationalAwareness = 10,
        AirDefense = 20,
        Silent = 30
    }

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

        private static readonly List<MechRadarController> activeControllers =
            new List<MechRadarController>();

        private const int MaximumSceneTargets = 16;
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
        private const float LaserRangeBeamWidthDegrees = 0.4f;
        private const float RadarRangeMeters = 100_000f;
        private const float DesignationBaseSeconds = 0.3f;
        private const float DesignationCaptureBoxPixels = 54f;
        private const float SttAzimuthLimitDegrees = SearchAzimuthDegrees * 0.5f;
        private const float SttElevationLimitDegrees = SearchElevationDegrees * 0.5f;
        private enum SttBreakReason : byte
        {
            Manual,
            OperationModeDisabled,
            RadarPowerDisabled,
            TrackMissing,
            TrackDropped,
            TrackStale,
            OutsideRadarFrame,
            TrackingRateExceeded,
        }

        private struct GuidanceChannel
        {
            public TrackId TrackId;
            public int ReferenceCount;
        }

        private struct DamageAssessmentState
        {
            public float SmoothedHealthFraction;
            public float Confidence;
            public double LastObservationTime;
            public double NextObservationTime;
            public uint ObservationSequence;
            public bool HasHealthEstimate;
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
        private readonly SensorId laserRangefinderId =
            new SensorId(new Handle(2, 1));
        private readonly EmitterId laserRangefinderEmitterId =
            new EmitterId(new Handle(96, 1));
        [Tooltip("给场景目标自动补挂测试辐射源（RWR 没有会辐射的对手就听不到任何东西）。")]
        [SerializeField] private bool autoEquipTestEmitters = true;
        [SerializeField] private bool radarPowerEnabled = true;
        [SerializeField] private bool retainSttWhenOperationModeDisabled;
        [SerializeField] private MechDataLinkPolicy dataLinkPolicy =
            MechDataLinkPolicy.Automatic;

        // ---- 数据链：本机终端 index 1，导弹终端 8..63；EmitterId 用 32+index
        // 区段与雷达发射机（0 区段）分开，ESM 消隐按 EmitterId 精确匹配。----
        private const int MissileLinkIndexStart = 8;
        private const int LinkEmitterIndexBase = 32;
        private const byte GuidanceNetworkId = 0;
        private const float GuidanceReacquisitionGateMeters = 350f;
        // STT 重获门限比制导链路窄：玩家锁定错目标的代价比导弹丢中段修正更高。
        // 实测被拆分的虚影与真目标相距约 50 m（截图 T03/T05：5.54 vs 5.49 km），
        // 150 m 有余量而不至于跨到编队里的另一架。
        private const float SttReacquisitionGateMeters = 150f;
        private const float FriendlyMissileReportRetentionSeconds = 1.0f;
        private const float FriendlyMissileAssociationMaximumMeters = 120f;
        private const float FriendlyMissileAssociationVelocityGateMps = 180f;
        private const double FriendlyMissileAssociationScoreSeparation = 0.35;
        private readonly LinkTerminalId ownLinkTerminalId = new LinkTerminalId(new Handle(1, 1));
        private readonly bool[] missileLinkAllocated = new bool[64];
        private readonly byte[] missileLinkGenerations = new byte[64];
        private readonly SimEntityId[] missileLinkEntities = new SimEntityId[64];

        private struct FriendlyMissileLinkReport
        {
            public bool Active;
            public LinkTerminalId SourceTerminal;
            public double3 Position;
            public double3 Velocity;
            public MissileMissionRole MissionRole;
            public float MissionRoleConfidence;
            public double Timestamp;
            public ulong Sequence;
        }

        private readonly FriendlyMissileLinkReport[] friendlyMissileReports =
            new FriendlyMissileLinkReport[64];
        private readonly bool[] friendlyMissileTrackMatched =
            new bool[MaximumPublicTracks];
        private readonly LinkMessage[] ownLinkInboxScratch = new LinkMessage[64];

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
        private readonly LaserRangeObservation[] laserRangeScratch =
            new LaserRangeObservation[
                LaserRangefinderSystem.ObservationCapacity];
        private readonly MissileTrackFusionConfig missileTrackFusionConfig =
            MissileTrackFusionConfig.Default;
        private int missileTrackCacheCount;
        private double missileTrackCacheTime = double.NaN;
        private readonly Measurement[] measurementScratch = new Measurement[MaximumPublicEchoes];
        private GuidanceChannel[] guidanceChannels = Array.Empty<GuidanceChannel>();
        private readonly bool[] auxiliarySensorAllocated = new bool[64];
        private readonly byte[] auxiliarySensorGenerations = new byte[64];
        private readonly double[] nextTwsRevisitTimes =
            new double[MaximumPublicTracks];
        private readonly uint[] twsRevisitTrackKeys =
            new uint[MaximumPublicTracks];
        private RasterScanPattern searchPattern;

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
        // 最后一次健康的 STT 航迹快照，用于目标机动导致航迹被拆分后转移锁定。
        private TrackSnapshot lastSttTrack;
        private bool hasLastSttTrack;
        private TrackId pointDefenseCueTrackId;
        private float designationProgress;
        private bool trackOperationMode;
        private float beamAzimuthDegrees;
        private float beamElevationDegrees;
        private Vector3 searchFrameForward;
        private Vector3 searchFrameRight;
        private Vector3 searchFrameUp;
        private MechRadarPerformanceProfile radarPerformance;
        private MechFireControlComputerProfile fireControlPerformance;
        private MechHeadSensorPerformance headPerformance =
            new MechHeadSensorPerformance();
        private MechDataLinkPerformance dataLinkPerformance =
            MechDataLinkPerformance.CreateStandardFallback();
        private double sttLastMaintainableTime = double.NegativeInfinity;
        private int fireControlReservationFrame = -1;
        private int fireControlSolutionReservations;
        private int weaponControlReservations;
        private readonly HashSet<TrackId> fireControlTargetReservations = new HashSet<TrackId>();
        private readonly Dictionary<TrackId, DamageAssessmentState> damageAssessments =
            new Dictionary<TrackId, DamageAssessmentState>();
        private TrackId damageAssessmentTrackId;

        private float RuntimeRadarRangeMeters =>
            radarPerformance.MaximumRangeMeters > 0f
                ? radarPerformance.MaximumRangeMeters
                : RadarRangeMeters;
        private float RuntimeSearchIntervalSeconds =>
            radarPerformance.SearchIntervalSeconds > 0f
                ? radarPerformance.SearchIntervalSeconds
                : SearchIntervalSeconds;
        private float RuntimeTwsRevisitIntervalSeconds =>
            radarPerformance.TwsRevisitIntervalSeconds > 0f
                ? radarPerformance.TwsRevisitIntervalSeconds
                : TwsRevisitIntervalSeconds;
        private float RuntimeSttIntervalSeconds =>
            radarPerformance.SttRevisitIntervalSeconds > 0f
                ? radarPerformance.SttRevisitIntervalSeconds
                : SttIntervalSeconds;
        private float RuntimeSearchAzimuthDegrees =>
            radarPerformance.SearchAzimuthDegrees > 0f
                ? radarPerformance.SearchAzimuthDegrees
                : SearchAzimuthDegrees;
        private float RuntimeSearchElevationDegrees =>
            radarPerformance.SearchElevationDegrees > 0f
                ? radarPerformance.SearchElevationDegrees
                : SearchElevationDegrees;

        public double SimTimeSeconds => driver != null ? driver.SimTimeSeconds : Time.timeAsDouble;
        public int DetectionCount => driver != null ? driver.DetectionCount : 0;
        public int TrackCount => driver != null ? driver.TrackCount : 0;
        public bool TrackOperationMode => trackOperationMode;
        public bool RadarPowerEnabled => radarPowerEnabled;
        public bool RetainSttWhenOperationModeDisabled =>
            retainSttWhenOperationModeDisabled;
        public MechDataLinkPolicy DataLinkPolicy => dataLinkPolicy;
        public bool DataLinkTransmitEnabled =>
            dataLinkPolicy != MechDataLinkPolicy.Silent;
        public string DataLinkPolicyDisplayName => dataLinkPolicy switch
        {
            MechDataLinkPolicy.SituationalAwareness => "态势优先",
            MechDataLinkPolicy.AirDefense => "防空优先",
            MechDataLinkPolicy.Silent => "静默",
            _ => "自动"
        };
        public TrackId CandidateTrackId => candidateTrackId;
        public TrackId SttTrackId => sttTrackId;
        public bool HasStt => sttTrackId.IsValid && TryGetTrack(sttTrackId, out _);
        public float DesignationProgress => designationProgress;
        public float PointDefenseRating => fireControlPerformance.PointDefenseRating;
        public static IReadOnlyList<MechRadarController> ActiveControllers =>
            activeControllers;

        /// <summary>
        /// STT 航迹因目标机动被跟踪层拆成新航迹时，把锁定转移到新航迹而不是直接断锁。
        ///
        /// 之所以需要它：STT 绑定的是 <c>TrackId</c>，而一次 QB 可能让量测掉出关联门限、
        /// 被 <c>AllocateTrack</c> 判成新目标；此时老 TrackId 再也收不到更新，STT 走
        /// TrackStale/TrackMissing 断开。导弹制导上行链路早就有这层重获
        /// （见 GuidanceReacquisitionGateMeters 那段），但玩家的 STT 一直没有。
        ///
        /// 这是第二层保险——第一层是 <c>RadarTrackManager</c> 的二级机动门限，本来就应该
        /// 避免拆分发生；两者都在，才不会因为某次机动幅度超出预期而丢锁。
        /// </summary>
        private bool TryReacquireSttTrack(out TrackSnapshot track)
        {
            track = default;
            if (!hasLastSttTrack)
            {
                return false;
            }

            if (!TryReacquireTrack(
                    lastSttTrack,
                    SttReacquisitionGateMeters,
                    out var candidate))
            {
                return false;
            }

            if (candidate.TrackId.Equals(sttTrackId))
            {
                // 同一条航迹只是暂时没更新，交回原有的陈旧判定处理。
                return false;
            }

            sttTrackId = candidate.TrackId;
            candidateTrackId = candidate.TrackId;
            lastSttTrack = candidate;
            track = candidate;
            return true;
        }

        public void CuePointDefenseTrack(TrackId trackId)
        {
            if (!radarPowerEnabled)
            {
                return;
            }

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
            if (enabled && !radarPowerEnabled)
            {
                return;
            }

            trackOperationMode = enabled;
            if (!enabled && !retainSttWhenOperationModeDisabled)
            {
                BreakSingleTargetTrack(
                    SttBreakReason.OperationModeDisabled);
            }
        }

        public void SetRadarPowerEnabled(bool enabled)
        {
            if (radarPowerEnabled == enabled)
            {
                return;
            }

            radarPowerEnabled = enabled;
            if (!enabled)
            {
                trackOperationMode = false;
                pointDefenseCueTrackId = default;
                pointDefenseCueUntilTime = double.NegativeInfinity;
                BreakSingleTargetTrack(SttBreakReason.RadarPowerDisabled);
                return;
            }

            var now = SimTimeSeconds;
            nextSearchDwellTime = now;
            nextSttDwellTime = now;
            nextMissileCueDwellTime = now;
            nextPointDefenseDwellTime = now;
        }

        public void SetRetainSttWhenOperationModeDisabled(bool enabled)
        {
            retainSttWhenOperationModeDisabled = enabled;
        }

        public void CycleDataLinkPolicy()
        {
            dataLinkPolicy = dataLinkPolicy switch
            {
                MechDataLinkPolicy.Automatic => MechDataLinkPolicy.SituationalAwareness,
                MechDataLinkPolicy.SituationalAwareness => MechDataLinkPolicy.AirDefense,
                MechDataLinkPolicy.AirDefense => MechDataLinkPolicy.Silent,
                _ => MechDataLinkPolicy.Automatic
            };
        }

        public float GetFriendlyMissileReportIntervalSeconds(
            MissileMissionRole missionRole)
        {
            if (!DataLinkTransmitEnabled)
            {
                return 0.5f;
            }

            var baseRate = Mathf.Max(0.25f, dataLinkPerformance.trackShareUpdateRateHz);
            var multiplier = dataLinkPolicy switch
            {
                MechDataLinkPolicy.SituationalAwareness => 1.5f,
                MechDataLinkPolicy.AirDefense when
                    missionRole == MissileMissionRole.Interceptor => 2f,
                MechDataLinkPolicy.AirDefense => 0.5f,
                _ => 1f
            };
            return 1f / Mathf.Max(0.25f, baseRate * multiplier);
        }

        private float RuntimeGuidanceUplinkIntervalSeconds
        {
            get
            {
                var rate = Mathf.Max(0.25f, dataLinkPerformance.guidanceUpdateRateHz);
                if (dataLinkPolicy == MechDataLinkPolicy.AirDefense)
                {
                    rate *= 1.25f;
                }
                return 1f / rate;
            }
        }

        public void BreakSingleTargetTrack()
        {
            BreakSingleTargetTrack(SttBreakReason.Manual);
        }

        private void BreakSingleTargetTrack(SttBreakReason reason)
        {
            var previousTrackId = sttTrackId;
            if (previousTrackId.IsValid)
            {
                var details = TryGetTrack(previousTrackId, out var track)
                    ? $" class={track.ObjectClass}" +
                      $" lifecycle={track.Lifecycle}" +
                      $" confidence={track.Confidence:0.00}" +
                      $" age={Math.Max(0.0, SimTimeSeconds - track.LastUpdateTime):0.000}s"
                    : " track=unavailable";
                Debug.Log(
                    $"[MechRadar][STT] break track={previousTrackId.Packed}" +
                    $" reason={reason}{details}",
                    this);
            }

            sttTrackId = default;
            candidateTrackId = default;
            designationProgress = 0f;
            sttLastMaintainableTime = double.NegativeInfinity;
        }

        public bool TryRegisterMissileSeeker(
            Vector3 position,
            Vector3 velocity,
            float maximumRangeMeters,
            float beamWidthDegrees,
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

                // Detectability is a physical property. Affiliation is assigned later
                // by authenticated data-link association, never by target registration.
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
                driver.RegisterLaserRangeTarget(
                    new LaserRangeTargetProfile
                    {
                        EntityId = entity,
                        ReflectivityDb = 0f,
                        ObjectClass =
                            TrackObjectClass.Missile,
                        ClassificationSignature = 0.9f,
                    });

                auxiliarySensorAllocated[index] = true;
                handle = new MissileSeekerHandle(
                    entity,
                    sensor,
                    radarTargetRegistered: true);
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
                driver.UnregisterLaserRangeTarget(
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
                var laserRangeCount =
                    driver.CopyLaserRangeObservations(
                        laserRangeScratch);
                missileTrackCacheCount = MissileTrackFusion.Fuse(
                    in missileTrackFusionConfig,
                    missileTrackFusionState,
                    trackScratch,
                    trackCount,
                    threatScratch,
                    threatCount,
                    electroOpticalScratch,
                    electroOpticalCount,
                    laserRangeScratch,
                    laserRangeCount,
                    ToDouble3(transform.position),
                    ToDouble3(playerVelocity),
                    now,
                    missileTrackCache);
                ApplyFriendlyMissileIdentification(now);
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
            if (!radarPowerEnabled || destination == null || driver == null)
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
            EnsureInitialized();
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
                RangeMeters = RuntimeRadarRangeMeters,
                ScanAzimuthDegrees = RuntimeSearchAzimuthDegrees,
                ScanElevationDegrees = RuntimeSearchElevationDegrees,
                BeamAzimuthDegrees = beamAzimuthDegrees,
                BeamElevationDegrees = beamElevationDegrees,
                BeamWidthDegrees = hasStt
                    ? SttBeamWidthDegrees
                    : SearchBeamWidthDegrees,
                SearchFrameProgress = searchPattern.FrameProgress,
                SearchColumnCount = searchPattern.ColumnCount,
                SearchRowCount = searchPattern.RowCount,
                TrackCount = count,
                TrackCapacity = driver != null ? driver.TrackCapacity : MaximumPublicTracks,
                RefreshRateHz = GetSearchFrameRate(hasStt),
                TargetRefreshRateHz = hasStt
                    ? 1f / Mathf.Max(RuntimeSttIntervalSeconds, (float)SimulationDriver.SensorStepSeconds)
                    : hasTwsTrack
                        ? 1f / RuntimeTwsRevisitIntervalSeconds
                        : GetSearchFrameRate(false),
                // 显示用近似：排队中的驻留 + 本 tick 常态负载相对全局仿真预算的占比。
                DwellLoad = Mathf.Clamp01(
                    (scheduledDwells + (hasStt ? 5f : 7f)) /
                    RadarSensorSystem.DwellBudgetPerTick),
                DeferredDwellCount = driver != null ? driver.DeferredDwellCount : 0,
                GuidanceChannelsUsed = usedChannels,
                GuidanceChannelCapacity = guidanceChannels.Length,
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

        public bool TryGetTargetDamageAssessment(
            TrackId trackId,
            out MechTargetDamageAssessment assessment)
        {
            assessment = default;
            if (!trackId.IsValid ||
                !trackId.Equals(sttTrackId) ||
                !damageAssessments.TryGetValue(trackId, out var state))
            {
                return false;
            }

            var now = SimTimeSeconds;
            if (now - state.LastObservationTime > 2.5)
            {
                return false;
            }

            var condition = state.HasHealthEstimate
                ? MechTargetDamageAssessmentMath.Classify(
                    state.SmoothedHealthFraction,
                    state.Confidence)
                : MechTargetDamageCondition.Unknown;
            assessment = new MechTargetDamageAssessment(
                trackId,
                condition,
                state.Confidence,
                state.LastObservationTime);
            return true;
        }

        public bool TryAssignFireControlTrack(
            TrackId trackId,
            bool requestSingleTargetTrack = true)
        {
            if (!radarPowerEnabled ||
                !trackOperationMode ||
                !trackId.IsValid ||
                !TryGetTrack(trackId, out var track) ||
                !IsFireControlUsable(track.Lifecycle))
            {
                return false;
            }

            candidateTrackId = trackId;
            designationProgress = 1f;
            if (!requestSingleTargetTrack)
            {
                return true;
            }

            if (track.Lifecycle != TrackLifecycle.Confirmed ||
                !IsInsideStableTrackRange(track))
            {
                return false;
            }

            if (!sttTrackId.Equals(trackId))
            {
                sttTrackId = trackId;
                sttLastMaintainableTime = SimTimeSeconds;
            }

            lastSttTrack = track;
            hasLastSttTrack = true;
            return true;
        }

        public bool TryReacquireTrack(
            in TrackSnapshot previousTrack,
            float basePositionGateMeters,
            out TrackSnapshot snapshot)
        {
            var count = CopyTracks(trackScratch);
            return TrackReacquisition.TrySelectCandidate(
                in previousTrack,
                trackScratch,
                count,
                SimTimeSeconds,
                basePositionGateMeters,
                out snapshot);
        }

        /// <summary>
        /// Updates the player designation cursor. Only confirmed public tracks participate.
        /// </summary>
        public void UpdateDesignation(Camera viewCamera, Vector2 reticleViewport, float deltaTime)
        {
            if (!radarPowerEnabled || !trackOperationMode || viewCamera == null)
            {
                return;
            }

            // Once STT is established, target changes must be explicit. Continuing
            // reticle selection here allowed a newly launched missile on the same
            // line of sight to overwrite the aircraft track silently.
            if (HasStt)
            {
                candidateTrackId = sttTrackId;
                designationProgress = 1f;
                return;
            }

            var count = CopyTracks(trackScratch);
            var bestId = default(TrackId);
            var bestScore = float.MaxValue;
            var bestClassPriority = int.MaxValue;
            var retainCurrentCandidate = false;
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
                if (track.TrackId.Equals(candidateTrackId))
                {
                    retainCurrentCandidate = true;
                }

                var classPriority = DesignationClassPriority(
                    track.ObjectClass);
                if (classPriority < bestClassPriority ||
                    (classPriority == bestClassPriority &&
                     score < bestScore))
                {
                    bestClassPriority = classPriority;
                    bestScore = score;
                    bestId = track.TrackId;
                }
            }

            // Measurement noise must not restart designation while the selected
            // track remains inside the capture box. The player can still move the
            // reticle off it or use the explicit cycle/break controls.
            if (retainCurrentCandidate)
            {
                bestId = candidateTrackId;
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
                candidate.Lifecycle == TrackLifecycle.Confirmed &&
                IsInsideStableTrackRange(candidate))
            {
                sttTrackId = candidate.TrackId;
                candidateTrackId = sttTrackId;
                sttLastMaintainableTime = SimTimeSeconds;
                Debug.Log(
                    $"[MechRadar][STT] acquired track={sttTrackId.Packed}" +
                    $" class={candidate.ObjectClass}" +
                    $" confidence={candidate.Confidence:0.00}" +
                    $" interference={candidate.InterferenceScore:0.00}",
                    this);
            }
        }

        private static int DesignationClassPriority(
            TrackObjectClass objectClass) => objectClass switch
        {
            TrackObjectClass.AirVehicle => 0,
            TrackObjectClass.Unknown => 1,
            TrackObjectClass.Missile => 2,
            _ => 3,
        };

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
            double maximumFlightTimeSeconds,
            out TrackSnapshot track,
            out FireSolution solution)
        {
            solution = default;
            if (!TryGetFireControlTrack(out track))
            {
                return false;
            }

            if (!TryReserveFireControlSolution(track.TrackId))
            {
                return false;
            }

            var fireControlTrack = MechFireControlPerformanceModel.ScaleTrackForFireSolution(
                track,
                fireControlPerformance);
            solution = FireSolutionSolver.Solve(
                fireControlTrack,
                ToDouble3(transform.position),
                ToDouble3(playerVelocity),
                projectileSpeed,
                SimTimeSeconds);
            if (!solution.WeaponReleaseAuthorized)
            {
                return false;
            }

			var flightTimeSeconds = solution.InterceptTime - SimTimeSeconds;
			if (flightTimeSeconds < 0.0 ||
				(fireControlPerformance.MaximumPredictionTimeSeconds > 0f &&
				 flightTimeSeconds > fireControlPerformance.MaximumPredictionTimeSeconds) ||
				(maximumFlightTimeSeconds > 0.0 && flightTimeSeconds > maximumFlightTimeSeconds))
			{
				return false;
			}

            var ray = GetAimRay();
			var currentTrackPosition = ToVector3(track.PredictPosition(SimTimeSeconds));
			var toCurrentTrack = currentTrackPosition - ray.origin;
			return toCurrentTrack.sqrMagnitude > 0.001f
			       && Vector3.Angle(ray.direction, toCurrentTrack) <= coneDegrees;
        }

        public bool TryAuthorizeMissileLaunch(
            MissileGuidanceMode guidanceMode,
            in MissileKinematicProfile kinematics,
            TrackObjectClass requiredTargetClass,
            out TrackSnapshot track,
            out FireSolution solution,
            out int guidanceChannelToken)
        {
            guidanceChannelToken = 0;
            if (!TryGetMissileFireSolution(
                    kinematics,
                    out track,
                    out solution))
            {
                return false;
            }

            if (track.ObjectClass != requiredTargetClass ||
                track.Classification < 0.25f)
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
            in MissileKinematicProfile kinematics,
            out TrackSnapshot track,
            out FireSolution solution)
        {
            solution = default;
            if (!TryGetFireControlTrack(out track))
            {
                return false;
            }

            if (!TryReserveFireControlSolution(track.TrackId))
            {
                return false;
            }

            var fireControlTrack = MechFireControlPerformanceModel.ScaleTrackForFireSolution(
                track,
                fireControlPerformance);
            solution = FireSolutionSolver.SolveMissile(
                fireControlTrack,
                ToDouble3(transform.position),
                ToDouble3(playerVelocity),
                kinematics,
                SimTimeSeconds);
            if (!solution.WeaponReleaseAuthorized)
            {
                return false;
            }

            return true;
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

        private bool TryReserveFireControlSolution(TrackId trackId)
        {
            if (fireControlReservationFrame != Time.frameCount)
            {
                fireControlReservationFrame = Time.frameCount;
                fireControlSolutionReservations = 0;
                weaponControlReservations = 0;
                fireControlTargetReservations.Clear();
            }

            var isNewTarget = !fireControlTargetReservations.Contains(trackId);
            if (isNewTarget &&
                fireControlTargetReservations.Count >=
                Mathf.Max(1, fireControlPerformance.MultiTargetEngagementCapacity))
            {
                return false;
            }
            if (fireControlSolutionReservations >=
                    Mathf.Max(1, fireControlPerformance.SimultaneousSolutionCapacity) ||
                weaponControlReservations >=
                    Mathf.Max(1, fireControlPerformance.WeaponControlChannels))
            {
                return false;
            }

            fireControlTargetReservations.Add(trackId);
            fireControlSolutionReservations++;
            weaponControlReservations++;
            return true;
        }

        private bool TryAcquireGuidanceChannel(TrackId trackId, out int token)
        {
            var distinctTargetCount = 0;
            for (var i = 0; i < guidanceChannels.Length; i++)
            {
                if (guidanceChannels[i].ReferenceCount > 0 &&
                    guidanceChannels[i].TrackId.Equals(trackId))
                {
                    guidanceChannels[i].ReferenceCount++;
                    token = i + 1;
                    return true;
                }

                if (guidanceChannels[i].ReferenceCount > 0)
                {
                    distinctTargetCount++;
                }
            }

            if (distinctTargetCount >=
                Mathf.Max(1, fireControlPerformance.MultiTargetEngagementCapacity))
            {
                token = 0;
                return false;
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

        private void OnEnable()
        {
            if (!activeControllers.Contains(this))
            {
                activeControllers.Add(this);
            }
        }

        private void OnDisable()
        {
            activeControllers.Remove(this);
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
            if (radarPowerEnabled)
            {
                ScheduleDwells();
            }
            PumpGuidanceUplinks(driver.SimTimeSeconds);
            ConsumeFriendlyMissileReports();
        }

        private void LateUpdate()
        {
            if (!initialized || !HasStt ||
                !TryGetTrack(sttTrackId, out var track) ||
                track.ObjectClass != TrackObjectClass.AirVehicle)
            {
                damageAssessmentTrackId = default;
                damageAssessments.Clear();
                return;
            }

            if (!damageAssessmentTrackId.Equals(track.TrackId))
            {
                damageAssessmentTrackId = track.TrackId;
                damageAssessments.Clear();
            }

            UpdateTargetDamageAssessment(in track);
        }

        private void UpdateTargetDamageAssessment(in TrackSnapshot track)
        {
            var now = SimTimeSeconds;
            damageAssessments.TryGetValue(track.TrackId, out var state);
            if (now + 1e-6 < state.NextObservationTime)
            {
                return;
            }

            if (!TryResolveSceneTarget(in track, out var target))
            {
                state.HasHealthEstimate = false;
                state.Confidence = 0f;
                state.LastObservationTime = now;
                state.NextObservationTime = now + 0.75;
                damageAssessments[track.TrackId] = state;
                return;
            }

            var targetPosition = target.AimPoint != null
                ? target.AimPoint.position
                : target.transform.position;
            var rangeMeters = Vector3.Distance(transform.position, targetPosition);
            var sensorQuality = CalculateDamageAssessmentSensorQuality(
                in track,
                target,
                targetPosition,
                rangeMeters);
            state.ObservationSequence++;
            var variation = MechTargetDamageAssessmentMath.DeterministicHealthVariation(
                track.TrackId.Packed,
                state.ObservationSequence);
            var observedHealth = MechTargetDamageAssessmentMath.CalculateObservedHealthFraction(
                target.HealthFraction,
                sensorQuality,
                variation);

            if (sensorQuality >= MechTargetDamageAssessmentMath.MinimumUsableSensorQuality)
            {
                var smoothing = Mathf.Lerp(0.28f, 0.68f, sensorQuality);
                state.SmoothedHealthFraction = state.HasHealthEstimate
                    ? Mathf.Lerp(state.SmoothedHealthFraction, observedHealth, smoothing)
                    : observedHealth;
                state.HasHealthEstimate = true;
            }
            else
            {
                state.HasHealthEstimate = false;
            }

            state.Confidence = sensorQuality;
            state.LastObservationTime = now;
            state.NextObservationTime = now + Mathf.Lerp(1.4f, 0.35f, sensorQuality);
            damageAssessments[track.TrackId] = state;
        }

        private bool TryResolveSceneTarget(
            in TrackSnapshot track,
            out CombatTestTarget target)
        {
            target = null;
            var predictedPosition = PredictTrackPosition(track);
            var covarianceSum =
                track.GetCovariance(0, 0) +
                track.GetCovariance(1, 1) +
                track.GetCovariance(2, 2);
            var positionVariance = double.IsFinite(covarianceSum)
                ? Mathf.Max(0f, (float)covarianceSum)
                : 0f;
            var associationGate = Mathf.Clamp(
                Mathf.Sqrt(positionVariance) * 4f + 60f,
                120f,
                1500f);
            var bestDistance = associationGate;
            var sensorPlatform = GetComponentInParent<CombatTestTarget>();

            for (var i = 0; i < sceneTargetCount; i++)
            {
                var candidate = sceneTargets[i];
                if (candidate == null || !candidate.IsAlive ||
                    !IsAffiliationCompatible(track.Affiliation, sensorPlatform, candidate))
                {
                    continue;
                }

                var candidatePosition = candidate.AimPoint != null
                    ? candidate.AimPoint.position
                    : candidate.transform.position;
                var distance = Vector3.Distance(predictedPosition, candidatePosition);
                if (distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                target = candidate;
            }

            return target != null;
        }

        private static bool IsAffiliationCompatible(
            TrackAffiliation affiliation,
            CombatTestTarget sensorPlatform,
            CombatTestTarget candidate)
        {
            if (sensorPlatform == null || affiliation == TrackAffiliation.Unknown)
            {
                return true;
            }

            var sameTeam = sensorPlatform.TeamId == candidate.TeamId;
            return affiliation == TrackAffiliation.Friendly ? sameTeam : !sameTeam;
        }

        private float CalculateDamageAssessmentSensorQuality(
            in TrackSnapshot track,
            CombatTestTarget target,
            Vector3 targetPosition,
            float rangeMeters)
        {
            var opticalQuality = 0f;
            if ((track.SourceMask & TrackSourceMask.ElectroOptical) != 0 &&
                headPerformance != null &&
                headPerformance.HasData)
            {
                var thermal01 = Mathf.Clamp01(target.ThermalSignature / 8f);
                var referenceRangeMeters = Mathf.Lerp(
                    Mathf.Max(0.1f, headPerformance.referenceColdRangeKm),
                    Mathf.Max(0.1f, headPerformance.referenceHotRangeKm),
                    thermal01) * 1000f;
                var rangeQuality = SmoothFalloff(
                    rangeMeters / Mathf.Max(100f, referenceRangeMeters),
                    0.45f,
                    1.05f);
                var sensorForward = aimingCamera != null
                    ? aimingCamera.transform.forward
                    : transform.forward;
                var lineOfSight = targetPosition - transform.position;
                var offBoresight = lineOfSight.sqrMagnitude > 0.01f
                    ? Vector3.Angle(sensorForward, lineOfSight)
                    : 0f;
                var pointingLimit = Mathf.Lerp(
                    18f,
                    95f,
                    Mathf.Clamp01(headPerformance.pointingCapability / 100f));
                var pointingQuality = SmoothFalloff(
                    offBoresight,
                    pointingLimit * 0.65f,
                    pointingLimit);
                var capability =
                    headPerformance.identificationCapability * 0.5f +
                    headPerformance.trackingCapability * 0.3f +
                    headPerformance.pointingCapability * 0.2f;
                opticalQuality = Mathf.Clamp01(capability / 100f) *
                                 rangeQuality *
                                 pointingQuality;
            }

            var electromagneticQuality = 0f;
            if ((track.SourceMask & TrackSourceMask.Radar) != 0)
            {
                var stableRange = Mathf.Max(100f, radarPerformance.StableTrackRangeMeters);
                var rangeQuality = SmoothFalloff(
                    rangeMeters / stableRange,
                    0.55f,
                    1.15f);
                electromagneticQuality = Mathf.Clamp01(
                    radarPerformance.LockMaintenanceRating / 100f) *
                    rangeQuality;
            }

            if ((track.SourceMask & TrackSourceMask.Esm) != 0)
            {
                var emissionQuality = Mathf.Clamp01(
                    target.ElectromagneticSignature / 10f) *
                    Mathf.Clamp01(track.Classification);
                electromagneticQuality = 1f -
                    (1f - electromagneticQuality) *
                    (1f - emissionQuality);
            }

            var fusedQuality = 1f -
                (1f - opticalQuality) *
                (1f - electromagneticQuality);
            return Mathf.Clamp01(fusedQuality * Mathf.Lerp(
                0.55f,
                1f,
                Mathf.Clamp01(track.Confidence)));
        }

        private static float SmoothFalloff(float value, float fullAt, float zeroAt)
        {
            var t = Mathf.InverseLerp(fullAt, zeroAt, value);
            var smooth = t * t * (3f - 2f * t);
            return 1f - smooth;
        }

        private void ScheduleDwells()
        {
            var now = driver.SimTimeSeconds;
            var hasStt = HasStt;
            if (now + 1e-6 >= nextSearchDwellTime)
            {
                ScheduleSearchBatch(
                    now,
                    hasStt ? SttSearchDwellsPerTick : SearchDwellsPerTick);
                nextSearchDwellTime = now + RuntimeSearchIntervalSeconds;
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
                BreakSingleTargetTrack(
                    SttBreakReason.OutsideRadarFrame);
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
            nextSttDwellTime = now + RuntimeSttIntervalSeconds;
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
                TryGetMissileTrack(
                    pointDefenseCueTrackId,
                    out var fusedPointDefenseTrack)
                    ? ToVector3(
                        fusedPointDefenseTrack.PredictPosition(now))
                    : PredictTrackPosition(pointDefenseTrack);
            var direction =
                (trackedPosition - transform.position).normalized;
            if (direction.sqrMagnitude < 0.5f ||
                !IsWithinBodyRadarFrame(direction))
            {
                return;
            }

            // The primary STT and a point-defense precision revisit each consume one
            // narrow-track channel. A one-channel radar preserves the aircraft STT and
            // lets point defense fall back to TWS plus laser ranging.
            if (!HasStt || radarPerformance.SttChannelCapacity >= 2)
            {
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
            }
            driver.ScheduleLaserPulse(new LaserPulse
            {
                SensorId = laserRangefinderId,
                CuedTrackId = pointDefenseCueTrackId,
                DueTime = now,
                Direction = ToFloat3(direction),
                Sequence = ++dwellSequence,
            });
            nextPointDefenseDwellTime =
                now + PointDefenseRevisitIntervalSeconds;
        }

        private void ScheduleSearchBatch(double now, int dwellCount)
        {
            CaptureSearchFrame();
            for (var i = 0; i < dwellCount; i++)
            {
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
            var scheduled = 0;
            for (var attempts = 0;
                 attempts < count && scheduled < TwsRevisitsPerTick;
                 attempts++)
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
                if (direction.sqrMagnitude < 0.5f ||
                    !IsWithinBodyRadarFrame(direction))
                {
                    nextTwsRevisitTimes[selectedSlot] = now +
                        RuntimeTwsRevisitIntervalSeconds;
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
                scheduled++;
                nextTwsRevisitTimes[selectedSlot] = now +
                    (hasStt
                        ? RuntimeTwsRevisitIntervalSeconds * 1.5f
                        : RuntimeTwsRevisitIntervalSeconds);
            }
        }

        private void CaptureSearchFrame()
        {
            var frameRotation = GetBodyRadarFrameRotation();
            searchFrameForward = frameRotation * Vector3.forward;
            searchFrameRight = frameRotation * Vector3.right;
            searchFrameUp = frameRotation * Vector3.up;
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
                searchPattern.CellCount * RuntimeSearchIntervalSeconds / dwellsPerTick;
            return frameSeconds > 0f ? 1f / frameSeconds : 0f;
        }

        private void ValidateSttTrack()
        {
            if (!sttTrackId.IsValid)
            {
                return;
            }

            if (!TryGetTrack(sttTrackId, out var track))
            {
                if (!TryReacquireSttTrack(out track))
                {
                    BreakSingleTargetTrack(SttBreakReason.TrackMissing);
                    return;
                }
            }

            if (track.Lifecycle == TrackLifecycle.Dropped)
            {
                if (!TryReacquireSttTrack(out track))
                {
                    BreakSingleTargetTrack(SttBreakReason.TrackDropped);
                    return;
                }
            }

            var lockRating01 = Mathf.Clamp01(radarPerformance.LockMaintenanceRating / 100f);
            var trackMemorySeconds = radarPerformance.TrackMemorySeconds > 0f
                ? radarPerformance.TrackMemorySeconds
                : 3f;
            trackMemorySeconds *= Mathf.Lerp(0.65f, 1.35f, lockRating01);
            if (SimTimeSeconds - track.LastUpdateTime > trackMemorySeconds)
            {
                if (!TryReacquireSttTrack(out track))
                {
                    BreakSingleTargetTrack(SttBreakReason.TrackStale);
                    return;
                }
            }

            // 记住最后一次健康的 STT 航迹，供上面三处重获使用。
            lastSttTrack = track;
            hasLastSttTrack = true;

            var relativePosition = PredictTrackPosition(track) - transform.position;
            var maintainable = IsWithinBodyRadarFrame(relativePosition) &&
                               relativePosition.magnitude <= RuntimeRadarRangeMeters;

            var maximumTrackingRate =
                radarPerformance.MaximumTrackingRateDegreesPerSecond;
            if (maximumTrackingRate > 0f && relativePosition.sqrMagnitude > 0.0001f)
            {
                var relativeVelocity = ToVector3(track.Velocity) - playerVelocity;
                var angularRateRadiansPerSecond =
                    Vector3.Cross(relativePosition, relativeVelocity).magnitude /
                    relativePosition.sqrMagnitude;
                var angularRateDegreesPerSecond =
                    angularRateRadiansPerSecond * Mathf.Rad2Deg;
                maintainable &= angularRateDegreesPerSecond <= maximumTrackingRate;
            }

            maintainable &= relativePosition.magnitude <=
                            Mathf.Max(1f, radarPerformance.StableTrackRangeMeters);
            if (maintainable)
            {
                sttLastMaintainableTime = SimTimeSeconds;
                return;
            }

            var maintenanceGraceSeconds = Mathf.Lerp(0.12f, 2.5f, lockRating01);
            if (SimTimeSeconds - sttLastMaintainableTime > maintenanceGraceSeconds)
            {
                BreakSingleTargetTrack(SttBreakReason.TrackingRateExceeded);
            }
        }

        private bool IsInsideStableTrackRange(in TrackSnapshot track)
        {
            var range = (PredictTrackPosition(track) - transform.position).magnitude;
            return range <= Mathf.Max(1f, radarPerformance.StableTrackRangeMeters);
        }

        private bool IsWithinBodyRadarFrame(Vector3 worldDirection)
        {
            if (worldDirection.sqrMagnitude < 0.0001f)
            {
                return false;
            }

            var frameRotation = GetBodyRadarFrameRotation();
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
            return Mathf.Abs(azimuth) <= RuntimeSearchAzimuthDegrees * 0.5f &&
                   Mathf.Abs(elevation) <= RuntimeSearchElevationDegrees * 0.5f;
        }

        private Quaternion GetBodyRadarFrameRotation()
        {
            if (flightMotion == null)
            {
                flightMotion = GetComponent<MechFlightMotionController>();
            }

            return flightMotion != null
                ? flightMotion.LogicalRotation
                : transform.rotation;
        }

        private void EnsureInitialized()
        {
            if (initialized)
            {
                return;
            }

            if (aimingCamera == null) aimingCamera = Camera.main;
            var runtimeState = GetComponent<MechRuntimeState>();
            var installedStats = runtimeState != null ? runtimeState.AggregatedStats : null;
            headPerformance = installedStats?.headSensorPerformance?.Clone() ??
                new MechHeadSensorPerformance();
            radarPerformance = MechFireControlPerformanceModel.BuildRadarProfile(installedStats);
            fireControlPerformance = MechFireControlPerformanceModel.BuildFireControlProfile(
                installedStats,
                radarPerformance);
            dataLinkPerformance = installedStats?.dataLinkPerformance?.Clone() ??
                MechDataLinkPerformance.CreateStandardFallback();
            if (!dataLinkPerformance.HasData)
            {
                dataLinkPerformance = MechDataLinkPerformance.CreateStandardFallback();
            }
            searchPattern = new RasterScanPattern(
                RuntimeSearchAzimuthDegrees,
                RuntimeSearchElevationDegrees,
                SearchBeamSpacingDegrees);
            guidanceChannels = new GuidanceChannel[
                Mathf.Max(0, fireControlPerformance.GuidanceChannelCapacity)];
            driver = GetComponent<SimulationDriver>();
            if (driver == null)
            {
                driver = gameObject.AddComponent<SimulationDriver>();
            }
            driver.ConfigureRadarTracking(
                radarPerformance.TrackCapacity,
                radarPerformance.TrackMemorySeconds);

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
                    RangeResolutionMeters = radarPerformance.RangeAccuracyMeters,
                    BeamWidthRadians = SearchBeamWidthDegrees * Mathf.Deg2Rad,
                    VelocityResolutionMps = radarPerformance.RadialVelocityAccuracyMetersPerSecond,
                },
                Capability = SensorCapability.Range | SensorCapability.Doppler | SensorCapability.LosRate,
                MaxRangeMeters = RuntimeRadarRangeMeters,
                FrequencyBand = 1,
                WaveformId = 1,
                MainlobePowerDb = 70f,
                SidelobeLevelDb = -25f,
                ClutterPowerDb = -30f,
                MeasurementPrecisionScale = radarPerformance.MeasurementPrecisionScale,
                Resolutions = new ResolutionMerge.Resolutions
                {
                    RangeMeters = radarPerformance.RangeAccuracyMeters,
                    AngleRadians = radarPerformance.AngularAccuracyMilliradians * 0.001f,
                    VelocityMps = radarPerformance.RadialVelocityAccuracyMetersPerSecond,
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
                TransmitPowerDb = dataLinkPerformance.transmitPowerDb,
                SidelobeLevelDb = dataLinkPerformance.sidelobeLevelDb,
                BeamWidthRadians = 20f * Mathf.Deg2Rad,
                ReceiveSensitivityDb = dataLinkPerformance.receiveSensitivityDb,
                MaxRangeMeters = dataLinkPerformance.referenceCommunicationRangeKm * 1000.0,
                BandwidthKilobitsPerSecond = dataLinkPerformance.bandwidthKbps,
                MaxNetworkNodes = dataLinkPerformance.maximumNetworkNodes,
                ProcessingLatencySeconds = dataLinkPerformance.endToEndLatencyMs * 0.0005f,
                JamResistanceDb = dataLinkPerformance.jamResistanceRating * 0.12f,
                FrequencyBand = (ushort)Mathf.Clamp(dataLinkPerformance.frequencyBand, 1, ushort.MaxValue),
                WaveformId = (ushort)Mathf.Clamp(dataLinkPerformance.waveformId, 1, ushort.MaxValue),
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
                    FieldOfViewRadians = Mathf.Lerp(
                        25f,
                        120f,
                        Mathf.Clamp01(headPerformance.searchCapability / 100f)) *
                        Mathf.Deg2Rad,
                    MaximumRangeMeters = Mathf.Max(
                        100f,
                        headPerformance.referenceHotRangeKm * 1000f),
                    SensitivityFloorDb = -60f,
                    BearingSigmaRadians = Mathf.Lerp(
                        0.45f,
                        0.04f,
                        Mathf.Clamp01(headPerformance.trackingCapability / 100f)) *
                        Mathf.Deg2Rad,
                    TrackCapacity = Mathf.Max(1, headPerformance.trackCapacity),
                    ClassificationScale = Mathf.Clamp01(
                        headPerformance.identificationCapability / 100f),
                });
            driver.RegisterLaserRangefinder(
                new LaserRangefinderProfile
                {
                    SensorId = laserRangefinderId,
                    EmitterId = laserRangefinderEmitterId,
                    OwnerEntityId = playerEntity,
                    MaximumRangeMeters = Mathf.Max(
                        100f,
                        headPerformance.laserRangefinderRangeKm * 1000f),
                    BeamWidthRadians =
                        LaserRangeBeamWidthDegrees *
                        Mathf.Deg2Rad,
                    PulseDurationSeconds = 0.00001f,
                    MainlobePowerDb = 120f,
                    SidelobeLevelDb = -160f,
                    ReceiveSensitivityDb = -50f,
                    BaseRangeSigmaMeters = Mathf.Max(
                        0.05f,
                        headPerformance.laserRangefinderErrorM),
                    RangeErrorPartsPerMillion = 20f,
                    BearingSigmaRadians =
                        0.03f * Mathf.Deg2Rad,
                    FrequencyBand = 3,
                    WaveformId = 20,
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
            driver.RegisterEsmWaveformClass(
                3,
                20,
                EsmWaveformClass.LaserRangefinder);

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
            Vector3 position,
            Vector3 velocity,
            out LinkTerminalId terminalId)
        {
            EnsureInitialized();
            terminalId = default;
            if (driver == null)
            {
                return false;
            }

            var allocatedNodeCount = 1;
            for (var i = MissileLinkIndexStart; i < missileLinkAllocated.Length; i++)
            {
                if (missileLinkAllocated[i]) allocatedNodeCount++;
            }
            if (allocatedNodeCount >= Mathf.Max(1, dataLinkPerformance.maximumNetworkNodes))
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
                var linkEntity = driver.RegisterEntity(BuildState(position, velocity));
                driver.RegisterLinkTerminal(new LinkTerminalProfile
                {
                    TerminalId = terminalId,
                    OwnerEntityId = linkEntity,
                    EmitterId = new EmitterId(new Handle(LinkEmitterIndexBase + index, generation)),
                    NetworkId = GuidanceNetworkId,
                    // 导弹接收制导上行，并以同一认证终端回传自身航迹供友军识别。
                    TransmitPowerDb = 40f,
                    SidelobeLevelDb = -30f,
                    BeamWidthRadians = 2f * Mathf.PI,
                    ReceiveSensitivityDb = -70f,
                    MaxRangeMeters = dataLinkPerformance.referenceCommunicationRangeKm * 1000.0,
                    BandwidthKilobitsPerSecond = dataLinkPerformance.bandwidthKbps,
                    MaxNetworkNodes = dataLinkPerformance.maximumNetworkNodes,
                    ProcessingLatencySeconds = dataLinkPerformance.endToEndLatencyMs * 0.0005f,
                    JamResistanceDb = dataLinkPerformance.jamResistanceRating * 0.12f,
                    FrequencyBand = (ushort)Mathf.Clamp(dataLinkPerformance.frequencyBand, 1, ushort.MaxValue),
                    WaveformId = (ushort)Mathf.Clamp(dataLinkPerformance.waveformId, 1, ushort.MaxValue),
                });
                missileLinkAllocated[index] = true;
                missileLinkEntities[index] = linkEntity;
                friendlyMissileReports[index] = default;
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
                if (missileLinkEntities[index].IsValid)
                {
                    driver.UnregisterEntity(missileLinkEntities[index]);
                }
                missileLinkAllocated[index] = false;
                missileLinkEntities[index] = default;
                friendlyMissileReports[index] = default;
                missileLinkGenerations[index]++;
                if (missileLinkGenerations[index] == 0)
                {
                    missileLinkGenerations[index] = 1;
                }
            }
        }

        public void UpdateMissileLinkTerminalState(
            LinkTerminalId terminalId,
            Vector3 position,
            Vector3 velocity)
        {
            if (!TryGetOwnedMissileLinkEntity(terminalId, out var entityId))
            {
                return;
            }

            driver.SetEntityState(entityId, BuildState(position, velocity));
        }

        public bool SendFriendlyMissileTrackReport(
            LinkTerminalId terminalId,
            Vector3 position,
            Vector3 velocity,
            MissileMissionRole missionRole)
        {
            if (!DataLinkTransmitEnabled ||
                !TryGetOwnedMissileLinkEntity(terminalId, out _))
            {
                return false;
            }

            var now = SimTimeSeconds;
            return driver.SendLinkMessage(new LinkMessage
            {
                Kind = LinkMessageKind.TrackShare,
                NetworkId = GuidanceNetworkId,
                SourceTerminal = terminalId,
                TargetTerminal = ownLinkTerminalId,
                SendTime = now,
                TrackPayload = new TrackSnapshot
                {
                    OwnerPlatformId = new PlatformId(terminalId.Value),
                    Position = ToDouble3(position),
                    Velocity = ToDouble3(velocity),
                    Confidence = 1f,
                    ObjectClass = TrackObjectClass.Missile,
                    MissileRole = missionRole,
                    MissileRoleConfidence =
                        missionRole == MissileMissionRole.Unknown ? 0f : 1f,
                    Classification = 1f,
                    IdentityConfidence = 1f,
                    Affiliation = TrackAffiliation.Friendly,
                    LastUpdateTime = now,
                    LastPredictTime = now,
                    SourceMask = TrackSourceMask.DataLink,
                    Lifecycle = TrackLifecycle.Confirmed,
                },
            });
        }

        private bool TryGetOwnedMissileLinkEntity(
            LinkTerminalId terminalId,
            out SimEntityId entityId)
        {
            entityId = default;
            var index = terminalId.Value.Index;
            if (driver == null ||
                !terminalId.IsValid ||
                index < MissileLinkIndexStart ||
                index >= missileLinkAllocated.Length ||
                !missileLinkAllocated[index] ||
                missileLinkGenerations[index] != terminalId.Value.Generation)
            {
                return false;
            }

            entityId = missileLinkEntities[index];
            return entityId.IsValid;
        }

        private void ConsumeFriendlyMissileReports()
        {
            if (driver == null)
            {
                return;
            }

            var count = driver.CopyLinkInbox(
                ownLinkTerminalId,
                ownLinkInboxScratch);
            for (var i = 0; i < count; i++)
            {
                var message = ownLinkInboxScratch[i];
                var terminalIndex = message.SourceTerminal.Value.Index;
                if (message.Kind != LinkMessageKind.TrackShare ||
                    message.NetworkId != GuidanceNetworkId ||
                    terminalIndex < MissileLinkIndexStart ||
                    terminalIndex >= missileLinkAllocated.Length ||
                    !missileLinkAllocated[terminalIndex] ||
                    missileLinkGenerations[terminalIndex] !=
                        message.SourceTerminal.Value.Generation ||
                    message.TrackPayload.Affiliation != TrackAffiliation.Friendly ||
                    message.TrackPayload.ObjectClass != TrackObjectClass.Missile ||
                    message.TrackPayload.IdentityConfidence < 0.99f)
                {
                    continue;
                }

                ref var report = ref friendlyMissileReports[terminalIndex];
                if (message.Sequence <= report.Sequence)
                {
                    continue;
                }

                report = new FriendlyMissileLinkReport
                {
                    Active = true,
                    SourceTerminal = message.SourceTerminal,
                    Position = message.TrackPayload.Position,
                    Velocity = message.TrackPayload.Velocity,
                    MissionRole = message.TrackPayload.MissileRole,
                    MissionRoleConfidence =
                        message.TrackPayload.MissileRoleConfidence,
                    Timestamp = message.DeliveryTime,
                    Sequence = message.Sequence,
                };
                missileTrackCacheTime = double.NaN;
            }
        }

        private void ApplyFriendlyMissileIdentification(double now)
        {
            Array.Clear(
                friendlyMissileTrackMatched,
                0,
                friendlyMissileTrackMatched.Length);
            for (var reportIndex = MissileLinkIndexStart;
                 reportIndex < friendlyMissileReports.Length;
                 reportIndex++)
            {
                var report = friendlyMissileReports[reportIndex];
                var age = math.max(0.0, now - report.Timestamp);
                if (!report.Active ||
                    age > FriendlyMissileReportRetentionSeconds)
                {
                    continue;
                }

                if (!MissileTrackFusion.TrySelectFriendlyDataLinkAssociation(
                        missileTrackCache,
                        missileTrackCacheCount,
                        friendlyMissileTrackMatched,
                        report.Position,
                        report.Velocity,
                        report.Timestamp,
                        now,
                        FriendlyMissileReportRetentionSeconds,
                        FriendlyMissileAssociationMaximumMeters,
                        FriendlyMissileAssociationVelocityGateMps,
                        FriendlyMissileAssociationScoreSeparation,
                        out var trackIndex,
                        out var score))
                {
                    continue;
                }

                friendlyMissileTrackMatched[trackIndex] = true;
                MissileTrackFusion.ApplyFriendlyDataLinkIdentity(
                    ref missileTrackCache[trackIndex],
                    score,
                    report.MissionRole,
                    report.MissionRoleConfidence);
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
            if (!DataLinkTransmitEnabled)
            {
                return;
            }

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
                    var reacquisitionWindow =
                        Mathf.Max(0f, fireControlPerformance.TrackReacquisitionTimeSeconds);
                    var lastTrackAge = subscription.LastSent.TrackId.IsValid
                        ? now - subscription.LastSent.LastUpdateTime
                        : double.PositiveInfinity;
                    if (subscription.AllowTrackReacquisition &&
                        lastTrackAge <= reacquisitionWindow &&
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
                        subscription.NextSendTime = now + RuntimeGuidanceUplinkIntervalSeconds;
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
                subscription.NextSendTime = now + RuntimeGuidanceUplinkIntervalSeconds;
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
            var targetIndex = sceneTargetCount++;

            if (autoEquipTestEmitters)
            {
                var emitter =
                    sceneTarget.GetComponent<MechTestRadarEmitter>() ??
                    sceneTarget.gameObject.AddComponent<MechTestRadarEmitter>();
                emitter.ConfigureEmissionSignature(
                    sceneTarget.ElectromagneticSignature);
                emitter.RegisterObserverDriver(
                    driver,
                    transform,
                    63 - targetIndex);
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
                driver.RegisterElectroOpticalTarget(
                    new ElectroOpticalTargetProfile
                    {
                        EntityId = targetEntities[index],
                        SignatureDb = 30f + 20f * Mathf.Log10(
                            Mathf.Max(0.01f, sceneTarget.ThermalSignature)),
                        ReferenceDiameterMeters = 12f,
                        ObjectClass = TrackObjectClass.AirVehicle,
                        ClassificationSignature = 0.9f,
                    });
                driver.RegisterLaserRangeTarget(
                    new LaserRangeTargetProfile
                    {
                        EntityId = targetEntities[index],
                        ReflectivityDb = 10f * Mathf.Log10(
                            Mathf.Max(0.01f, sceneTarget.RadarCrossSectionSquareMeters)),
                        ObjectClass = TrackObjectClass.AirVehicle,
                        ClassificationSignature = 0.9f,
                    });
            }
            else
            {
                driver.UnregisterRadarTarget(targetEntities[index]);
                driver.UnregisterElectroOpticalTarget(targetEntities[index]);
                driver.UnregisterLaserRangeTarget(targetEntities[index]);
            }

            targetRadarRegistered[index] = shouldRegister;
        }

        private void UpdateBeamAngles(Vector3 worldDirection)
        {
            ResolveBodyRadarAngles(
                worldDirection,
                out beamAzimuthDegrees,
                out beamElevationDegrees);
        }

        private void ResolveBodyRadarAngles(
            Vector3 worldDirection,
            out float azimuthDegrees,
            out float elevationDegrees)
        {
            var direction = worldDirection.normalized;
            var local = Quaternion.Inverse(GetBodyRadarFrameRotation()) * direction;
            azimuthDegrees = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            elevationDegrees = Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg;
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
            activeControllers.Remove(this);
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
