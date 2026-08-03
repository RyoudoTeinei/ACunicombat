using System;
using ACunicombat.FireControl;
using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;
using UnityEngine;
// 数据链契约（LinkMessage/LinkTerminalId）在 Contracts 程序集，无需额外 using。

namespace ACunicombat.Mechs
{
    public enum MissileFlightPhase : byte
    {
        LaunchDelay = 0,
        Boost = 1,
        MidcourseCoast = 2,
        SeekerAcquisition = 3,
        TerminalGuidance = 4,
        Ballistic = 5,
        ColdEjection = 6,
        AttitudeAlignment = 7,
    }

    public enum MissileEngagementState : byte
    {
        Active = 0,
        Spent = 1,
    }

    [DisallowMultipleComponent]
    public sealed class MechTestProjectile : MonoBehaviour
    {
        private static readonly System.Collections.Generic.List<MechTestProjectile> registry = new();
        public static System.Collections.Generic.IReadOnlyList<MechTestProjectile> Registry => registry;

        private const float GuidanceMemorySeconds = 4f;
        private const float InterceptorGuidanceMemorySeconds = 0.8f;
        private const float ReacquisitionGateMeters = 350f;
        private const float InterceptorReacquisitionGateMeters = 100f;
        private const float InterceptorAcquisitionGateMinimumMeters = 80f;
        private const float InterceptorAcquisitionGateMaximumMeters = 300f;
        private const float InterceptorVelocityAssociationGateMps = 300f;
        private const float InterceptorSeekerPositionBlend = 0.35f;
        private const float InterceptorSeekerVelocityBlend = 0.2f;
        private const float SeekerScanIntervalSeconds = 0.05f;
        private const float SeekerLockMemorySeconds = 0.35f;
        private const float InterceptorSeekerLockMemorySeconds = 0.65f;
        private const float InterceptorFuseEstimateMemorySeconds = 4f;
        private const float InterceptorFuseLookAheadSeconds = 0.06f;
        private const float InterceptorFuseUncertaintyAllowanceMeters = 8f;
        private const float DefaultMissileInterceptionHealth = 80f;
        // 与 MechTestWeaponSystem.ConfigureMissileHitbox 的 hitboxDiameterMeters = 0.5 对应。
        private const float MissileHitboxHalfWidthMeters = 0.25f;
        private const float AirToAirMidcourseLeadLimitSeconds = 15f;
        private const float AirToAirTerminalLeadLimitSeconds = 4f;
        private const float InterceptorMidcourseLeadLimitSeconds = 4f;
        private const float InterceptorTerminalLeadLimitSeconds = 1.5f;
        private const float EngagementAssessmentIntervalSeconds = 0.2f;
        private const float EngagementFailureConfirmationSeconds = 0.75f;
        private const float GuidanceRecoveryGraceSeconds = 2f;
        private const float MinimumEngagementAssessmentFlightSeconds = 1f;
        private const float MinimumOpeningSpeedMetersPerSecond = 5f;

        // 物理查询复用缓冲：SphereCastAll/OverlapSphere 每次分配数组，
        // 弹幕场景下是稳定的每帧 GC 压力源。主线程单发访问，静态复用安全。
        private static readonly RaycastHit[] SphereCastScratch = new RaycastHit[32];
        private static readonly Collider[] OverlapScratch = new Collider[64];
        private static readonly System.Collections.Generic.HashSet<CombatTestTarget> DamagedScratch = new();
        private static readonly System.Collections.Generic.HashSet<MechTestProjectile>
            DamagedProjectileScratch = new();

        private Transform owner;
        private MechRadarController radarController;
        private CombatTestTarget directGuidanceTarget;
        private MechTestProjectile directProjectileTarget;
        private bool interceptorMissile;
        private bool interceptorTargetCommitted;
        private Vector3 directTargetPreviousPosition;
        private Vector3 directTargetVelocity;
        private TrackId targetTrackId;
        private TestWeaponKind weaponKind;
        private Vector3 velocity;
        private float damage;
        private float collisionRadius;
        private float lifeRemaining;
        private float launchDelay;
        private float maximumTravelDistance;
        private float distanceTravelled;
        private MechEffectPrefabLibrary effectLibrary;
        private int impactFfxId;

        private TrackSnapshot cachedGuidanceTrack;
        private bool hasCachedGuidanceTrack;
        private float guidanceMemoryRemaining;
        private int guidanceChannelToken;
        private bool guidanceChannelReleased;

        private MissileFlightPhase missilePhase = MissileFlightPhase.LaunchDelay;
        private float missileFlightTime;
        private float dryMassKg;
        private float mainPropellantMassRemainingKg;
        private float mainEngineThrustNewtons;
        private float mainMassFlowKgPerSecond;
        private float rcsPropellantMassRemainingKg;
        private float rcsThrustNewtons;
        private float rcsMassFlowKgPerSecond;
        private float pitchYawInertiaKgM2;
        private float rcsMomentArmMeters;
        private float maximumAngularRateRadians;
        private float currentAngularRateRadians;
        private float seekerActivationDelay;
        private float seekerMaximumRange;
        private float seekerHalfAngleRadians;
        private float seekerGimbalRadians;
        private Vector3 rcsAccelerationWorld;
        private Vector3 rcsAttitudeTorqueLocal;
        private float rcsAttitudeThrottle;
        private float proximityFuseRadius;
        private float proximityFuseArmTime;
        private float proximityFuseSafeDistance;
        private float warheadLethalRadius;
        private float seekerScanRemaining;
        private float seekerLockMemoryRemaining;
        private bool seekerLocked;
        private Vector3 desiredAttitudeForward;
        private Vector3 seekerEstimatedPosition;
        private Vector3 seekerEstimatedVelocity;
        private Vector3 terminalFuseEstimatedPosition;
        private Vector3 terminalFuseEstimatedVelocity;
        private float terminalFuseEstimateMemoryRemaining;
        private float terminalFuseEstimateUncertaintyMeters;
        private Vector3 prelaunchInterceptPoint;
        private float prelaunchSteeringRemaining;
        private bool coldLaunchEnabled;
        private bool coldLaunchMotorIgnited;
        private Vector3 coldLaunchAxis;
        private Vector3 coldLaunchInheritedVelocity;
        private Vector3 coldLaunchPreviousPosition;
        private Vector3 coldLaunchCarrierPreviousPosition;
        private float coldLaunchElapsedSeconds;
        private float coldLaunchRelativeDistanceMeters;
        private float coldLaunchSafeSeparationSeconds;
        private float coldLaunchSafeSeparationMeters;
        private float coldLaunchIgnitionAlignmentRadians;
        private float coldLaunchAlignmentTimeoutRemaining;
        private double seekerLastMeasurementTime = -1.0;
        private ulong seekerDwellSequence;
        private MechRadarController.MissileSeekerHandle seekerHandle;
        private readonly Measurement[] seekerMeasurementScratch =
            new Measurement[8];
        // 物理数据链：中段修正只来自经链路投递的 GuidanceUpdate 报文。
        private LinkTerminalId linkTerminal;
        private ulong lastLinkSequence;
        private readonly LinkMessage[] linkInboxScratch = new LinkMessage[8];
        private bool irSourceRegistered;
        private bool irMotorStateRegistered;
        private const float MotorIrIntensityDb = 58f;
        private const float CoastIrIntensityDb = 28f;
        private ParticleSystem[] propulsionParticles = Array.Empty<ParticleSystem>();
        private TrailRenderer projectileTrail;
        private bool motorVisualActive;
        private MechMissileAudioController missileAudio;
        private ParticleSystem[] rcsParticles = Array.Empty<ParticleSystem>();
        private Vector3[] rcsExhaustDirectionsLocal = Array.Empty<Vector3>();
        private Vector3[] rcsTorqueAxesLocal = Array.Empty<Vector3>();
        private float[] rcsPulsePhase = Array.Empty<float>();
        private MechMissilePlume missilePlume;
        // 喷口位置由羽焰组件从实际弹体模型的包围盒量出（R-27 为 4 m、R-77 为 3.6 m，
        // 均以中心对齐原点）；这里的值只在模型缺失时兜底。
        private const float MissilePlumeFallbackNozzleOffset = 1.8f;
        private const float MissilePlumeScale = 1f;
        private static Material rcsParticleMaterial;
        // 真实 RCS 是开关式（bang-bang）阀门，不是连续可变油门；指令强度体现为
        // 脉冲占空比而不是喷流粗细。频率取略带互质关系的值，避免 8 个喷口整齐同步。
        private const float RcsPulseHertz = 14f;
        private const float RcsPulsePeakRate = 260f;
        private float interceptionHealth;
        // > 0 表示这是专用拦截弹种，带小半径近炸；0 表示普通动能弹。
        private float interceptorRoundLethalRadius;
        private bool interceptionResolved;
        private MissileEngagementState engagementState =
            MissileEngagementState.Active;
        private float engagementAssessmentRemaining;
        private float engagementFailureEvidenceSeconds;
        private float noGuidanceSeconds;

        public MissileFlightPhase MissilePhase => missilePhase;
        public MissileEngagementState EngagementState => engagementState;
        public bool IsEngagementActive =>
            IsMissile && engagementState == MissileEngagementState.Active;
        public bool SeekerLocked => seekerLocked;
        public float CurrentMassKg =>
            dryMassKg +
            mainPropellantMassRemainingKg +
            rcsPropellantMassRemainingKg;
        public float MainPropellantMassRemainingKg =>
            mainPropellantMassRemainingKg;
        public float RcsPropellantMassRemainingKg =>
            rcsPropellantMassRemainingKg;
        public float ManeuverDeltaVRemaining =>
            EstimateRcsDeltaVRemaining();
        public Vector3 Velocity => velocity;
        public bool IsMissile => weaponKind == TestWeaponKind.Missile;
        public bool IsInterceptableMissile =>
            IsMissile && !interceptionResolved && gameObject.activeInHierarchy;
        public bool HasActiveGuidance =>
            IsEngagementActive &&
            (directGuidanceTarget != null
                ? directGuidanceTarget.IsAlive
                : directProjectileTarget != null
                    ? directProjectileTarget.IsInterceptableMissile
                : seekerLocked ||
                  (hasCachedGuidanceTrack && guidanceMemoryRemaining > 0f));
        public TrackId GuidanceTrackId =>
            hasCachedGuidanceTrack && cachedGuidanceTrack.TrackId.IsValid
                ? cachedGuidanceTrack.TrackId
                : targetTrackId;

        public bool IsOwnedBy(Transform candidateOwner) =>
            candidateOwner != null && owner == candidateOwner;

        public bool TryPredictThreatTo(
            Transform defendedTarget,
            Vector3 defendedVelocity,
            float protectedRadius,
            float predictionHorizon,
            out float timeToClosestApproach,
            out float missDistance)
        {
            timeToClosestApproach = 0f;
            missDistance = float.PositiveInfinity;
            if (!IsInterceptableMissile ||
                !HasActiveGuidance ||
                defendedTarget == null)
            {
                return false;
            }

            var relativePosition =
                transform.position - defendedTarget.position;
            var relativeVelocity = velocity - defendedVelocity;
            var velocitySquared =
                relativeVelocity.sqrMagnitude;
            if (velocitySquared <= 0.01f)
            {
                return false;
            }

            var horizon = Mathf.Max(0.1f, predictionHorizon);
            var rawClosestTime =
                -Vector3.Dot(
                    relativePosition,
                    relativeVelocity) /
                velocitySquared;
            timeToClosestApproach =
                Mathf.Clamp(rawClosestTime, 0f, horizon);
            missDistance =
                (relativePosition +
                 relativeVelocity * timeToClosestApproach).magnitude;

            var explicitlyTargetsDefender =
                directGuidanceTarget != null &&
                (directGuidanceTarget.transform == defendedTarget ||
                 directGuidanceTarget.transform.IsChildOf(
                     defendedTarget) ||
                 defendedTarget.IsChildOf(
                     directGuidanceTarget.transform));
            if (explicitlyTargetsDefender)
            {
                if (rawClosestTime <= 0f)
                {
                    timeToClosestApproach = Mathf.Min(
                        horizon,
                        relativePosition.magnitude /
                        Mathf.Max(
                            relativeVelocity.magnitude,
                            1f));
                }
                missDistance = 0f;
                return true;
            }

            return rawClosestTime > 0f &&
                   rawClosestTime <= horizon &&
                   missDistance <= Mathf.Max(1f, protectedRadius);
        }

        private void OnEnable()
        {
            if (!registry.Contains(this))
            {
                registry.Add(this);
            }
        }

        private void OnDisable() => registry.Remove(this);

        public void Configure(
            Transform newOwner,
            MechRadarController newRadarController,
            TrackId newTargetTrackId,
            TestWeaponKind kind,
            Vector3 initialVelocity,
            float newDamage,
            float newCollisionRadius,
            float lifetimeSeconds,
            float delaySeconds = 0f,
            float accelerationMetersPerSecondSquared = 0f,
            float turnRateDegreesPerSecond = 0f,
            MechEffectPrefabLibrary newEffectLibrary = null,
            int newImpactFfxId = 0,
            float newMaximumTravelDistance = 0f,
            int newGuidanceChannelToken = 0,
            float newMotorBurnDuration = 2.1f,
            float newSeekerActivationDelay = 2.5f,
            float newSeekerMaximumRange = 8000f,
            float newSeekerHalfAngleDegrees = 35f,
            float newSeekerGimbalDegrees = 60f,
            float newMaximumLateralAcceleration = 75f,
            float newManeuverDeltaV = 180f,
            float newRcsLateralAcceleration = 0f,
            float newRcsDeltaV = 0f,
            float newProximityFuseRadius = 12f,
            float newProximityFuseArmTime = 0.75f,
            float newProximityFuseSafeDistance = 20f,
            float newWarheadLethalRadius = 18f,
            CombatTestTarget newDirectGuidanceTarget = null,
            bool newInterceptorMissile = false,
            MechTestProjectile newDirectProjectileTarget = null,
            MissileFlightProfile missileProfile = default,
            bool hasInitialGuidanceEstimate = false,
            Vector3 initialGuidancePosition = default,
            Vector3 initialGuidanceVelocity = default,
            float initialGuidanceUncertaintyMeters = 0f,
            Vector3 newPrelaunchInterceptPoint = default,
            bool newColdLaunch = false,
            Vector3 newColdLaunchInheritedVelocity = default,
            float newColdLaunchSafeSeparationSeconds = 0f,
            float newColdLaunchSafeSeparationMeters = 0f,
            float newColdLaunchIgnitionAlignmentDegrees = 8f,
            float newColdLaunchAlignmentTimeoutSeconds = 0.6f,
            bool newOwnshipMissileAudio = false)
        {
            owner = newOwner;
            radarController = newRadarController;
            directGuidanceTarget = newDirectGuidanceTarget;
            directProjectileTarget = newDirectProjectileTarget;
            interceptorMissile = newInterceptorMissile;
            interceptorTargetCommitted =
                newInterceptorMissile &&
                (newTargetTrackId.IsValid ||
                 newDirectProjectileTarget != null ||
                 hasInitialGuidanceEstimate);
            if (directGuidanceTarget != null)
            {
                directTargetPreviousPosition =
                    directGuidanceTarget.AimPoint.position;
            }
            else if (directProjectileTarget != null)
            {
                directTargetPreviousPosition =
                    directProjectileTarget.transform.position;
            }
            targetTrackId = newTargetTrackId;
            weaponKind = kind;
            engagementState = MissileEngagementState.Active;
            engagementAssessmentRemaining = 0f;
            engagementFailureEvidenceSeconds = 0f;
            noGuidanceSeconds = 0f;
            interceptionHealth = IsMissile
                ? DefaultMissileInterceptionHealth
                : 0f;
            interceptionResolved = false;
            velocity = initialVelocity;
            desiredAttitudeForward = transform.forward.sqrMagnitude > 0.001f
                ? transform.forward.normalized
                : initialVelocity.sqrMagnitude > 0.001f
                    ? initialVelocity.normalized
                    : Vector3.forward;
            if (hasInitialGuidanceEstimate)
            {
                terminalFuseEstimatedPosition =
                    initialGuidancePosition;
                terminalFuseEstimatedVelocity =
                    initialGuidanceVelocity;
                terminalFuseEstimateMemoryRemaining =
                    GetTerminalEstimateMemorySeconds();
                terminalFuseEstimateUncertaintyMeters =
                    Mathf.Max(
                        0f,
                        initialGuidanceUncertaintyMeters);
            }
            prelaunchInterceptPoint =
                newPrelaunchInterceptPoint;
            prelaunchSteeringRemaining =
                newInterceptorMissile &&
                (prelaunchInterceptPoint - transform.position)
                    .sqrMagnitude > 0.001f
                    ? 0.6f
                    : 0f;
            coldLaunchEnabled =
                weaponKind == TestWeaponKind.Missile &&
                newColdLaunch;
            coldLaunchMotorIgnited = !coldLaunchEnabled;
            coldLaunchAxis = transform.forward.sqrMagnitude > 0.001f
                ? transform.forward.normalized
                : initialVelocity.sqrMagnitude > 0.001f
                    ? initialVelocity.normalized
                    : Vector3.forward;
            coldLaunchInheritedVelocity =
                newColdLaunchInheritedVelocity;
            coldLaunchPreviousPosition = transform.position;
            coldLaunchCarrierPreviousPosition = owner != null
                ? owner.position
                : Vector3.zero;
            coldLaunchElapsedSeconds = 0f;
            coldLaunchRelativeDistanceMeters = 0f;
            coldLaunchSafeSeparationSeconds = Mathf.Max(
                0f,
                newColdLaunchSafeSeparationSeconds);
            coldLaunchSafeSeparationMeters = Mathf.Max(
                0f,
                newColdLaunchSafeSeparationMeters);
            coldLaunchIgnitionAlignmentRadians = Mathf.Clamp(
                    newColdLaunchIgnitionAlignmentDegrees,
                    0.5f,
                    45f) *
                Mathf.Deg2Rad;
            coldLaunchAlignmentTimeoutRemaining = Mathf.Max(
                0.05f,
                newColdLaunchAlignmentTimeoutSeconds);
            damage = newDamage;
            collisionRadius = Mathf.Max(0.01f, newCollisionRadius);
            lifeRemaining = Mathf.Max(0.1f, lifetimeSeconds);
            launchDelay = Mathf.Max(0f, delaySeconds);
            maximumTravelDistance = Mathf.Max(0f, newMaximumTravelDistance);
            distanceTravelled = 0f;
            effectLibrary = newEffectLibrary;
            impactFfxId = newImpactFfxId;
            guidanceChannelToken = newGuidanceChannelToken;
            guidanceChannelReleased = guidanceChannelToken <= 0;

            if (weaponKind == TestWeaponKind.Missile)
            {
                ConfigureMissileDynamics(
                    missileProfile,
                    accelerationMetersPerSecondSquared,
                    turnRateDegreesPerSecond,
                    newMotorBurnDuration,
                    newSeekerActivationDelay,
                    newSeekerMaximumRange,
                    newSeekerHalfAngleDegrees,
                    newSeekerGimbalDegrees,
                    newMaximumLateralAcceleration,
                    newManeuverDeltaV,
                    newRcsLateralAcceleration,
                    newRcsDeltaV,
                    newProximityFuseRadius,
                    newProximityFuseArmTime,
                    newProximityFuseSafeDistance,
                    newWarheadLethalRadius);
            }

            hasCachedGuidanceTrack =
                weaponKind == TestWeaponKind.Missile &&
                targetTrackId.IsValid &&
                radarController != null &&
                radarController.TryGetTrack(
                    targetTrackId,
                    out cachedGuidanceTrack);
            guidanceMemoryRemaining =
                hasCachedGuidanceTrack
                    ? GetGuidanceMemorySeconds()
                    : 0f;

            projectileTrail = GetComponent<TrailRenderer>();
            // 在羽焰与 RCS 构建之前采集：这两套由本组件显式开关，不应混进 propulsionParticles
            // 的批量控制里（RCS 的开关取决于姿态指令，而不是主机是否点火）。
            propulsionParticles = GetComponentsInChildren<ParticleSystem>(true);
            if (weaponKind == TestWeaponKind.Missile)
            {
                missilePlume = gameObject.AddComponent<MechMissilePlume>();
                missilePlume.Build(
                    MissilePlumeFallbackNozzleOffset,
                    MissilePlumeScale);
                CreateRcsThrusterVisuals();
            }
            if (weaponKind == TestWeaponKind.Missile &&
                radarController != null)
            {
                radarController.TryRegisterMissileSeeker(
                    transform.position,
                    velocity,
                    seekerMaximumRange,
                    seekerHalfAngleRadians *
                    2f * Mathf.Rad2Deg,
                    owner != null &&
                    ResolveCombatantRoot(owner) !=
                    ResolveCombatantRoot(
                        radarController.transform),
                    out seekerHandle);
                if (seekerHandle.IsValid)
                {
                    // 数据链终端 + 制导上行订阅：发射后的航迹更新只经物理链路到达。
                    if (radarController.TryRegisterMissileLinkTerminal(
                            in seekerHandle, out linkTerminal) &&
                        targetTrackId.IsValid)
                    {
                        radarController.BeginGuidanceUplink(
                            linkTerminal,
                            targetTrackId,
                            allowTrackReacquisition: !interceptorMissile);
                    }

                    // 红外点源（MAW 数据源）：发动机状态决定强度。
                    radarController.RegisterMissileIrSource(in seekerHandle, CoastIrIntensityDb);
                    irSourceRegistered = true;
                    irMotorStateRegistered = false;
                }
            }
            missilePhase = coldLaunchEnabled
                ? MissileFlightPhase.ColdEjection
                : launchDelay > 0f
                    ? MissileFlightPhase.LaunchDelay
                    : MissileFlightPhase.Boost;
            if (weaponKind == TestWeaponKind.Missile)
            {
                missileAudio =
                    GetComponent<MechMissileAudioController>();
                if (missileAudio == null)
                {
                    missileAudio =
                        gameObject.AddComponent<MechMissileAudioController>();
                }
                missileAudio.Configure(
                    coldLaunchEnabled,
                    newOwnshipMissileAudio);

                var motorStartsImmediately =
                    !coldLaunchEnabled &&
                    launchDelay <= 0f;
                motorVisualActive = !motorStartsImmediately;
                SetMotorVisual(motorStartsImmediately);
            }
        }

        private void ConfigureMissileDynamics(
            MissileFlightProfile profile,
            float legacyAcceleration,
            float legacyTurnRateDegrees,
            float legacyMotorBurnDuration,
            float legacySeekerActivationDelay,
            float legacySeekerMaximumRange,
            float legacySeekerHalfAngleDegrees,
            float legacySeekerGimbalDegrees,
            float legacyMaximumLateralAcceleration,
            float legacyManeuverDeltaV,
            float legacyRcsLateralAcceleration,
            float legacyRcsDeltaV,
            float legacyProximityFuseRadius,
            float legacyProximityFuseArmTime,
            float legacyProximityFuseSafeDistance,
            float legacyWarheadLethalRadius)
        {
            if (!profile.IsConfigured)
            {
                profile = MissileFlightProfile.AirToAirDefault;
                var initialMass = profile.InitialMassKg;
                if (legacyAcceleration > 0f)
                {
                    profile.MainEngineThrustNewtons =
                        legacyAcceleration * initialMass;
                }

                if (legacyMotorBurnDuration > 0f)
                {
                    profile.MainPropellantMassKg =
                        profile.MainMassFlowKgPerSecond *
                        legacyMotorBurnDuration;
                }

                if (legacyTurnRateDegrees > 0f)
                {
                    profile.MaximumAngularRateDegreesPerSecond =
                        legacyTurnRateDegrees;
                }

                var legacyRcsAcceleration = Mathf.Max(
                    legacyMaximumLateralAcceleration,
                    legacyRcsLateralAcceleration);
                if (legacyRcsAcceleration > 0f)
                {
                    profile.RcsThrustNewtons =
                        legacyRcsAcceleration * initialMass;
                }

                var legacyRcsDeltaVTotal = Mathf.Max(
                    0f,
                    legacyManeuverDeltaV) +
                    Mathf.Max(0f, legacyRcsDeltaV);
                if (legacyRcsDeltaVTotal > 0f &&
                    legacyRcsAcceleration > 0f)
                {
                    profile.RcsPropellantMassKg =
                        profile.RcsMassFlowKgPerSecond *
                        legacyRcsDeltaVTotal /
                        legacyRcsAcceleration;
                }

                profile.SeekerActivationDelaySeconds =
                    legacySeekerActivationDelay;
                profile.SeekerRangeMeters =
                    legacySeekerMaximumRange;
                profile.SeekerSearchHalfAngleDegrees =
                    legacySeekerHalfAngleDegrees;
                profile.SeekerGimbalDegrees =
                    legacySeekerGimbalDegrees;
                profile.ProximityFuseRadiusMeters =
                    legacyProximityFuseRadius;
                profile.ProximityFuseArmSeconds =
                    legacyProximityFuseArmTime;
                profile.ProximityFuseSafeDistanceMeters =
                    legacyProximityFuseSafeDistance;
                profile.WarheadLethalRadiusMeters =
                    legacyWarheadLethalRadius;
            }

            profile = profile.Sanitized();
            dryMassKg = profile.DryMassKg;
            mainPropellantMassRemainingKg =
                profile.MainPropellantMassKg;
            mainEngineThrustNewtons =
                profile.MainEngineThrustNewtons;
            mainMassFlowKgPerSecond =
                profile.MainMassFlowKgPerSecond;
            rcsPropellantMassRemainingKg =
                profile.RcsPropellantMassKg;
            rcsThrustNewtons = profile.RcsThrustNewtons;
            rcsMassFlowKgPerSecond =
                profile.RcsMassFlowKgPerSecond;
            pitchYawInertiaKgM2 =
                profile.PitchYawInertiaKgM2;
            rcsMomentArmMeters =
                profile.RcsMomentArmMeters;
            maximumAngularRateRadians =
                profile.MaximumAngularRateDegreesPerSecond *
                Mathf.Deg2Rad;
            currentAngularRateRadians = 0f;
            seekerActivationDelay =
                profile.SeekerActivationDelaySeconds;
            seekerMaximumRange =
                profile.SeekerRangeMeters;
            seekerHalfAngleRadians =
                profile.SeekerSearchHalfAngleDegrees *
                Mathf.Deg2Rad;
            seekerGimbalRadians =
                profile.SeekerGimbalDegrees *
                Mathf.Deg2Rad;
            proximityFuseRadius =
                profile.ProximityFuseRadiusMeters;
            proximityFuseArmTime =
                profile.ProximityFuseArmSeconds;
            proximityFuseSafeDistance =
                profile.ProximityFuseSafeDistanceMeters;
            warheadLethalRadius =
                profile.WarheadLethalRadiusMeters;
            lifeRemaining = profile.MaximumFlightTimeSeconds;
            maximumTravelDistance = profile.MaximumRangeMeters;
        }

        private static Transform ResolveCombatantRoot(
            Transform source)
        {
            if (source == null)
            {
                return null;
            }

            var rig =
                source.GetComponentInParent<MechPlayerRig>();
            if (rig != null)
            {
                return rig.transform;
            }

            var target =
                source.GetComponentInParent<CombatTestTarget>();
            return target != null ? target.transform : source;
        }

        private void FixedUpdate()
        {
            var dt = Time.fixedDeltaTime;
            lifeRemaining -= dt;
            if (lifeRemaining <= 0f)
            {
                Destroy(gameObject);
                return;
            }

            if (weaponKind == TestWeaponKind.Missile)
            {
                rcsAccelerationWorld = Vector3.zero;
                rcsAttitudeTorqueLocal = Vector3.zero;
                rcsAttitudeThrottle = 0f;
            }

            var coldLaunchAttitudeUpdated = false;
            var motorIgnited = coldLaunchEnabled
                ? coldLaunchMotorIgnited
                : launchDelay <= 0f;
            if (weaponKind == TestWeaponKind.Missile &&
                coldLaunchEnabled &&
                !coldLaunchMotorIgnited)
            {
                coldLaunchAttitudeUpdated =
                    UpdateColdLaunchSequence(dt);
                motorIgnited = coldLaunchMotorIgnited;
            }
            else if (weaponKind == TestWeaponKind.Missile &&
                     launchDelay > 0f)
            {
                launchDelay = Mathf.Max(0f, launchDelay - dt);
                motorIgnited = launchDelay <= 0f;
                if (motorIgnited)
                {
                    missilePhase = MissileFlightPhase.Boost;
                    SetMotorVisual(true);
                }
            }

            var proximityDetonated = false;
            if (weaponKind == TestWeaponKind.Missile)
            {
                missileFlightTime += dt;
                if (motorIgnited)
                {
                    UpdateMissile(dt);
                    if (!coldLaunchAttitudeUpdated)
                    {
                        UpdateMissileAttitude(dt);
                    }
                }
                UpdateRcsThrusterVisuals(dt);
                proximityDetonated = TryDetonateProximity(dt);
            }

            if (proximityDetonated)
            {
                return;
            }

            var travel = velocity * dt;
            if (maximumTravelDistance > 0f &&
                distanceTravelled + travel.magnitude >= maximumTravelDistance)
            {
                Destroy(gameObject);
                return;
            }

            var hasSweepHit = TryFindHit(travel, out var hit);
            var speed = velocity.magnitude;
            var sweepTime = hasSweepHit && speed > 0.001f
                ? hit.distance / speed
                : float.PositiveInfinity;

            // 弹丸对来袭导弹改用双体 CPA 判定，并与扫掠结果比较发生时刻取更早者。
            // 只用 SphereCast 是不够的：它扫掠自己这一 tick 的位移，却把导弹当成 tick 开始时
            // 的静止点。步枪弹 1500 m/s 在 0.02 s 步长下每 tick 走 30 m，而有效命中半径只有
            // 0.33 m（弹丸 0.08 + 导弹半宽 0.25）；导弹自身每 tick 还要走十几米。
            // 后果是几何相关的漏判：迎头交会时导弹位移几乎沿弹道方向、垂直分量小，仍能命中；
            // 侧向穿越时位移全是垂直分量，必然判不到。这与 2026-07-29 修复导弹战斗部
            // "近炸触发却未命中物理导弹" 是同一类问题，那次的解法同样是算双方真实轨迹的最近接近。
            if (weaponKind != TestWeaponKind.Missile &&
                TryFindMissileClosestApproach(
                    dt,
                    out var interceptTarget,
                    out var interceptTime,
                    out var interceptPoint) &&
                interceptTime <= sweepTime &&
                TryApplyGunInterception(interceptTarget, interceptPoint))
            {
                Destroy(gameObject);
                return;
            }

            if (hasSweepHit)
            {
                HandleDirectImpact(hit);
                Destroy(gameObject);
                return;
            }

            transform.position += travel;
            if (weaponKind == TestWeaponKind.Missile)
            {
                // The radar driver runs before projectiles on the next fixed
                // tick, so publish the fully integrated state here.
                radarController?.UpdateMissileSeekerState(
                    seekerHandle,
                    transform.position,
                    velocity);
            }
            distanceTravelled += travel.magnitude;
            if (weaponKind != TestWeaponKind.Missile &&
                velocity.sqrMagnitude > 0.001f)
            {
                transform.rotation = Quaternion.LookRotation(
                    velocity.normalized,
                    transform.up);
            }
        }

        private bool UpdateColdLaunchSequence(float deltaTime)
        {
            coldLaunchElapsedSeconds += deltaTime;
            var projectileDisplacement =
                transform.position - coldLaunchPreviousPosition;
            var carrierDisplacement = owner != null
                ? owner.position - coldLaunchCarrierPreviousPosition
                : coldLaunchInheritedVelocity * deltaTime;
            var relativeDisplacement =
                projectileDisplacement - carrierDisplacement;
            coldLaunchRelativeDistanceMeters += Mathf.Max(
                    0f,
                    Vector3.Dot(
                        relativeDisplacement,
                        coldLaunchAxis));
            coldLaunchPreviousPosition = transform.position;
            if (owner != null)
            {
                coldLaunchCarrierPreviousPosition = owner.position;
            }

            if (missilePhase == MissileFlightPhase.ColdEjection)
            {
                desiredAttitudeForward = coldLaunchAxis;
                if (coldLaunchElapsedSeconds <
                        coldLaunchSafeSeparationSeconds ||
                    coldLaunchRelativeDistanceMeters <
                        coldLaunchSafeSeparationMeters)
                {
                    return false;
                }

                missilePhase =
                    MissileFlightPhase.AttitudeAlignment;
                currentAngularRateRadians = 0f;
            }

            var toIntercept =
                prelaunchInterceptPoint - transform.position;
            if (toIntercept.sqrMagnitude > 0.001f)
            {
                desiredAttitudeForward =
                    toIntercept.normalized;
            }

            UpdateMissileAttitude(deltaTime);
            coldLaunchAlignmentTimeoutRemaining = Mathf.Max(
                0f,
                coldLaunchAlignmentTimeoutRemaining - deltaTime);
            var alignmentRadians = Vector3.Angle(
                    transform.forward,
                    desiredAttitudeForward) *
                Mathf.Deg2Rad;
            if (alignmentRadians <=
                    coldLaunchIgnitionAlignmentRadians ||
                coldLaunchAlignmentTimeoutRemaining <= 0f)
            {
                coldLaunchMotorIgnited = true;
                missilePhase = MissileFlightPhase.Boost;
                SetMotorVisual(true);
            }

            return true;
        }

        private void UpdateMissile(float deltaTime)
        {
            if (terminalFuseEstimateMemoryRemaining > 0f)
            {
                terminalFuseEstimatedPosition +=
                    terminalFuseEstimatedVelocity * deltaTime;
            }
            terminalFuseEstimateMemoryRemaining = Mathf.Max(
                0f,
                terminalFuseEstimateMemoryRemaining - deltaTime);
            prelaunchSteeringRemaining = Mathf.Max(
                0f,
                prelaunchSteeringRemaining - deltaTime);
            var motorActive =
                mainPropellantMassRemainingKg > 0.0001f &&
                mainEngineThrustNewtons > 0f &&
                mainMassFlowKgPerSecond > 0f;
            SetMotorVisual(motorActive);
            if (irSourceRegistered && motorActive != irMotorStateRegistered)
            {
                // 燃烧段羽焰强度高一个量级；燃尽滑翔段 MAW 告警可能中断（真实行为）。
                radarController.SetMissileIrIntensity(
                    in seekerHandle,
                    motorActive ? MotorIrIntensityDb : CoastIrIntensityDb);
                irMotorStateRegistered = motorActive;
            }

            var currentSpeed = velocity.magnitude;
            if (motorActive)
            {
                var consumedPropellant = Mathf.Min(
                    mainPropellantMassRemainingKg,
                    mainMassFlowKgPerSecond * deltaTime);
                var effectiveMass = Mathf.Max(
                    dryMassKg,
                    CurrentMassKg - consumedPropellant * 0.5f);
                var thrustDirection =
                    transform.forward.sqrMagnitude > 0.001f
                        ? transform.forward.normalized
                        : currentSpeed > 0.01f
                            ? velocity / currentSpeed
                            : Vector3.forward;
                velocity +=
                    thrustDirection *
                    (mainEngineThrustNewtons / effectiveMass) *
                    deltaTime;
                mainPropellantMassRemainingKg -=
                    consumedPropellant;
            }

            if (engagementState == MissileEngagementState.Spent)
            {
                desiredAttitudeForward = transform.forward;
                currentAngularRateRadians = 0f;
                return;
            }

            Vector3 targetPosition;
            Vector3 targetVelocity;
            var hasGuidance = false;
            var refreshesTerminalEstimate = false;
            var seekerActive = missileFlightTime >= seekerActivationDelay;
            var directVehicleTargetAlive =
                directGuidanceTarget != null &&
                directGuidanceTarget.IsAlive;
            var directMissileTargetAlive =
                directProjectileTarget != null &&
                directProjectileTarget.IsInterceptableMissile;
            var committedDirectMissileTargetLost =
                interceptorTargetCommitted &&
                directProjectileTarget != null &&
                !directMissileTargetAlive;
            var hasDirectTarget =
                directVehicleTargetAlive ||
                directMissileTargetAlive;
            var directTargetPosition =
                directVehicleTargetAlive
                    ? directGuidanceTarget.AimPoint.position
                    : directMissileTargetAlive
                        ? directProjectileTarget.transform.position
                        : default;

            var directSeekerHasTrack = false;
            if (hasDirectTarget && seekerActive)
            {
                directSeekerHasTrack =
                    UpdateDirectGuidanceSeekerEmission(
                        deltaTime,
                        directTargetPosition);
            }
            else if (seekerActive && !committedDirectMissileTargetLost)
            {
                UpdateSeeker(deltaTime);
            }
            else if (committedDirectMissileTargetLost)
            {
                // An interceptor is a one-target weapon. Once its explicitly
                // assigned threat is gone, retain the last inertial estimate
                // instead of allowing the seeker to capture another missile.
                seekerLocked = false;
            }

            if (hasDirectTarget)
            {
                var currentTargetPosition = directTargetPosition;
                directTargetVelocity = directMissileTargetAlive
                    ? directProjectileTarget.Velocity
                    : (currentTargetPosition -
                       directTargetPreviousPosition) /
                      Mathf.Max(deltaTime, 0.001f);
                directTargetPreviousPosition = currentTargetPosition;
                missilePhase = seekerActive
                    ? MissileFlightPhase.TerminalGuidance
                    : motorActive
                        ? MissileFlightPhase.Boost
                        : MissileFlightPhase.MidcourseCoast;
                targetPosition = currentTargetPosition;
                targetVelocity = directTargetVelocity;
                seekerEstimatedPosition = targetPosition;
                seekerEstimatedVelocity = targetVelocity;
                seekerLocked =
                    seekerActive && directSeekerHasTrack;
                hasGuidance = true;
                refreshesTerminalEstimate = true;
            }
            else if (seekerLocked)
            {
                missilePhase = MissileFlightPhase.TerminalGuidance;
                targetPosition = seekerEstimatedPosition;
                targetVelocity = seekerEstimatedVelocity;
                hasGuidance = true;
                refreshesTerminalEstimate = true;
                ReleaseGuidanceChannel();
            }
            else if (TryUpdateDatalinkTrack(deltaTime, out var track))
            {
                missilePhase = motorActive
                    ? MissileFlightPhase.Boost
                    : seekerActive
                        ? MissileFlightPhase.SeekerAcquisition
                        : MissileFlightPhase.MidcourseCoast;
                targetPosition = radarController.PredictTrackPosition(track);
                targetVelocity = new Vector3(
                    (float)track.Velocity.x,
                    (float)track.Velocity.y,
                    (float)track.Velocity.z);
                hasGuidance = true;
                refreshesTerminalEstimate = true;
            }
            else if (interceptorMissile &&
                     terminalFuseEstimateMemoryRemaining > 0f)
            {
                missilePhase = motorActive
                    ? MissileFlightPhase.Boost
                    : MissileFlightPhase.MidcourseCoast;
                targetPosition = terminalFuseEstimatedPosition;
                targetVelocity = terminalFuseEstimatedVelocity;
                hasGuidance = true;
            }
            else
            {
                missilePhase = motorActive
                    ? MissileFlightPhase.Boost
                    : seekerActive
                        ? MissileFlightPhase.SeekerAcquisition
                        : MissileFlightPhase.Ballistic;
                targetPosition = default;
                targetVelocity = default;
            }

            UpdateEngagementViability(
                hasGuidance,
                targetPosition,
                targetVelocity,
                deltaTime);
            if (engagementState == MissileEngagementState.Spent)
            {
                desiredAttitudeForward = transform.forward;
                currentAngularRateRadians = 0f;
                return;
            }

            if (hasGuidance)
            {
                if (refreshesTerminalEstimate)
                {
                    terminalFuseEstimatedPosition = targetPosition;
                    terminalFuseEstimatedVelocity = targetVelocity;
                    terminalFuseEstimateMemoryRemaining =
                        GetTerminalEstimateMemorySeconds();
                }
                var toTarget = targetPosition - transform.position;
                var terminalGuidance =
                    seekerLocked || directSeekerHasTrack;
                var guidanceSpeed = EstimateGuidanceSpeed(
                    toTarget.magnitude,
                    terminalGuidance);
                var leadTimeLimit = interceptorMissile
                    ? terminalGuidance
                        ? InterceptorTerminalLeadLimitSeconds
                        : InterceptorMidcourseLeadLimitSeconds
                    : terminalGuidance
                        ? AirToAirTerminalLeadLimitSeconds
                        : AirToAirMidcourseLeadLimitSeconds;
                var leadPoint = ToVector3(
                    MissileEngagementMath.PredictLeadPoint(
                        ToFloat3(transform.position),
                        ToFloat3(targetPosition),
                        ToFloat3(targetVelocity),
                        guidanceSpeed,
                        leadTimeLimit));
                var attitudeTarget =
                    interceptorMissile &&
                    prelaunchSteeringRemaining > 0f
                        ? prelaunchInterceptPoint
                        : leadPoint;
                var toAttitudeTarget =
                    attitudeTarget - transform.position;
                if (toAttitudeTarget.sqrMagnitude > 0.001f)
                {
                    desiredAttitudeForward =
                        toAttitudeTarget.normalized;
                }
                else if (toTarget.sqrMagnitude > 0.001f)
                {
                    desiredAttitudeForward = toTarget.normalized;
                }
                ApplyGuidance(
                    targetPosition,
                    targetVelocity,
                    desiredAttitudeForward,
                    terminalGuidance,
                    deltaTime);
            }
            else if (velocity.sqrMagnitude > 0.001f)
            {
                desiredAttitudeForward = velocity.normalized;
            }
        }

        private void UpdateEngagementViability(
            bool hasGuidance,
            Vector3 targetPosition,
            Vector3 targetVelocity,
            float deltaTime)
        {
            if (engagementState != MissileEngagementState.Active ||
                missileFlightTime <
                    MinimumEngagementAssessmentFlightSeconds)
            {
                return;
            }

            if (!hasGuidance)
            {
                engagementFailureEvidenceSeconds = 0f;
                noGuidanceSeconds += deltaTime;
                if (noGuidanceSeconds >= GuidanceRecoveryGraceSeconds)
                {
                    MarkEngagementSpent();
                }
                return;
            }

            noGuidanceSeconds = 0f;
            engagementAssessmentRemaining -= deltaTime;
            if (engagementAssessmentRemaining > 0f)
            {
                return;
            }
            engagementAssessmentRemaining =
                EngagementAssessmentIntervalSeconds;

            var relativePosition =
                targetPosition - transform.position;
            var range = relativePosition.magnitude;
            if (range <= Mathf.Max(
                    warheadLethalRadius,
                    proximityFuseRadius))
            {
                engagementFailureEvidenceSeconds = 0f;
                return;
            }

            var relativeVelocity = targetVelocity - velocity;
            var closingSpeed = range > 0.01f
                ? -Vector3.Dot(relativePosition, relativeVelocity) / range
                : 0f;
            if (closingSpeed >= -MinimumOpeningSpeedMetersPerSecond)
            {
                engagementFailureEvidenceSeconds = 0f;
                return;
            }

            var remainingTravelDistance = maximumTravelDistance > 0f
                ? Mathf.Max(
                    0f,
                    maximumTravelDistance - distanceTravelled)
                : Mathf.Max(1000f, velocity.magnitude * lifeRemaining);
            var availableDeltaV =
                EstimateMainEngineDeltaVRemaining() +
                EstimateRcsDeltaVRemaining() +
                Mathf.Max(75f, velocity.magnitude * 0.08f);
            var reachable = MissileEngagementMath.HasReachableIntercept(
                ToFloat3(relativePosition),
                ToFloat3(targetVelocity),
                ToFloat3(velocity),
                lifeRemaining,
                remainingTravelDistance,
                availableDeltaV);
            if (reachable)
            {
                engagementFailureEvidenceSeconds = 0f;
                return;
            }

            engagementFailureEvidenceSeconds +=
                EngagementAssessmentIntervalSeconds;
            if (engagementFailureEvidenceSeconds >=
                EngagementFailureConfirmationSeconds)
            {
                MarkEngagementSpent();
            }
        }

        private void MarkEngagementSpent()
        {
            if (engagementState != MissileEngagementState.Active)
            {
                return;
            }

            engagementState = MissileEngagementState.Spent;
            missilePhase = MissileFlightPhase.Ballistic;
            seekerLocked = false;
            seekerLockMemoryRemaining = 0f;
            hasCachedGuidanceTrack = false;
            guidanceMemoryRemaining = 0f;
            targetTrackId = default;
            directGuidanceTarget = null;
            directProjectileTarget = null;
            terminalFuseEstimateMemoryRemaining = 0f;
            rcsAccelerationWorld = Vector3.zero;
            rcsAttitudeTorqueLocal = Vector3.zero;
            rcsAttitudeThrottle = 0f;
            ReleaseGuidanceChannel();

            if (radarController != null && linkTerminal.IsValid)
            {
                radarController.UnregisterMissileLinkTerminal(
                    linkTerminal);
                linkTerminal = default;
            }
        }

        private void UpdateMissileAttitude(float deltaTime)
        {
            if (desiredAttitudeForward.sqrMagnitude <= 0.001f)
            {
                currentAngularRateRadians = 0f;
                return;
            }

            var forward = desiredAttitudeForward.normalized;
            var up = Vector3.ProjectOnPlane(transform.up, forward);
            if (up.sqrMagnitude <= 0.001f)
            {
                up = Vector3.ProjectOnPlane(Vector3.up, forward);
            }
            if (up.sqrMagnitude <= 0.001f)
            {
                up = Vector3.ProjectOnPlane(Vector3.right, forward);
            }

            var targetRotation = Quaternion.LookRotation(forward, up.normalized);
            var angleRadians =
                Quaternion.Angle(transform.rotation, targetRotation) *
                Mathf.Deg2Rad;
            if (angleRadians <= 0.0001f)
            {
                currentAngularRateRadians = 0f;
                transform.rotation = targetRotation;
                return;
            }

            var angularAcceleration =
                AvailableRcsAngularAcceleration();
            if (angularAcceleration <= 0f)
            {
                currentAngularRateRadians = 0f;
                return;
            }

            var stoppingLimitedRate = Mathf.Sqrt(
                2f * angularAcceleration * angleRadians);
            var targetAngularRate = Mathf.Min(
                maximumAngularRateRadians,
                stoppingLimitedRate);
            var previousAngularRate = currentAngularRateRadians;
            currentAngularRateRadians = Mathf.MoveTowards(
                currentAngularRateRadians,
                targetAngularRate,
                angularAcceleration * deltaTime);
            var turnRadians = Mathf.Min(
                angleRadians,
                currentAngularRateRadians * deltaTime);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation,
                targetRotation,
                turnRadians * Mathf.Rad2Deg);

            var rcsThrottle = Mathf.Clamp01(
                Mathf.Abs(
                    currentAngularRateRadians -
                    previousAngularRate) /
                Mathf.Max(
                    angularAcceleration * deltaTime,
                    0.001f));
            var rotationAxisWorld =
                Vector3.Cross(transform.forward, forward);
            var angularRateDelta =
                currentAngularRateRadians - previousAngularRate;
            if (rcsThrottle > 0.001f &&
                rotationAxisWorld.sqrMagnitude > 0.000001f &&
                Mathf.Abs(angularRateDelta) > 0.000001f)
            {
                var torqueDirectionWorld =
                    rotationAxisWorld.normalized *
                    Mathf.Sign(angularRateDelta);
                rcsAttitudeTorqueLocal =
                    transform.InverseTransformDirection(
                        torqueDirectionWorld);
                rcsAttitudeThrottle = Mathf.Max(
                    rcsAttitudeThrottle,
                    rcsThrottle);
            }
            ConsumeRcsPropellant(rcsThrottle, deltaTime);
        }

        private void ApplyGuidance(
            Vector3 targetPosition,
            Vector3 targetVelocity,
            Vector3 guidanceDirection,
            bool terminal,
            float deltaTime)
        {
            if (velocity.sqrMagnitude <= 0.001f ||
                rcsPropellantMassRemainingKg <= 0f ||
                rcsThrustNewtons <= 0f)
            {
                return;
            }

            var relativePosition = targetPosition - transform.position;
            if (relativePosition.sqrMagnitude <= 0.001f)
            {
                return;
            }

            var relativeVelocity = targetVelocity - velocity;
            var losDirection = relativePosition.normalized;
            var closingVelocity = Mathf.Max(
                0f,
                -Vector3.Dot(relativeVelocity, losDirection));
            var losRate = MissileEngagementMath.LineOfSightRate(
                ToFloat3(relativePosition),
                ToFloat3(relativeVelocity));
            var speed = velocity.magnitude;
            var phaseAuthority = terminal ? 1f : 0.72f;
            var rcsAccelerationLimit =
                rcsThrustNewtons /
                Mathf.Max(CurrentMassKg, 0.001f);
            var accelerationLimit =
                rcsAccelerationLimit * phaseAuthority;
            if (accelerationLimit <= 0f)
            {
                return;
            }

            var command = PnGuidance.Command(
                losRate,
                ToFloat3(losDirection),
                closingVelocity,
                interceptorMissile
                    ? terminal ? 5.5f : 4.5f
                    : terminal ? 4.5f : 3.5f,
                accelerationLimit);
            var lateralAcceleration = Vector3.ProjectOnPlane(
                ToVector3(command),
                velocity.normalized);

            // PN is almost zero at launch on a constant bearing. The bounded
            // term establishes the predicted intercept geometry, rather than
            // pulling the missile back into pure pursuit of current position.
            var steeringDirection =
                guidanceDirection.sqrMagnitude > 0.001f
                    ? guidanceDirection.normalized
                    : losDirection;
            var desiredLateralVelocity = Vector3.ProjectOnPlane(
                steeringDirection * speed,
                velocity.normalized);
            var pursuitAcceleration = Vector3.ClampMagnitude(
                desiredLateralVelocity / (terminal ? 0.65f : 1.5f),
                accelerationLimit * 0.45f);
            lateralAcceleration = Vector3.ClampMagnitude(
                lateralAcceleration + pursuitAcceleration,
                accelerationLimit);

            var usedDeltaV = lateralAcceleration.magnitude * deltaTime;
            if (usedDeltaV <= 0f)
            {
                return;
            }

            velocity += lateralAcceleration * deltaTime;

            rcsAccelerationWorld = lateralAcceleration;
            var rcsThrottle = Mathf.Clamp01(
                lateralAcceleration.magnitude /
                Mathf.Max(
                    rcsAccelerationLimit * phaseAuthority,
                    0.001f));
            ConsumeRcsPropellant(rcsThrottle, deltaTime);
        }

        private float EstimateGuidanceSpeed(
            float targetRange,
            bool terminal)
        {
            var speed = Mathf.Max(velocity.magnitude, 1f);
            if (mainPropellantMassRemainingKg <= 0.0001f ||
                mainEngineThrustNewtons <= 0f ||
                mainMassFlowKgPerSecond <= 0f)
            {
                return speed;
            }

            var burnRemaining =
                mainPropellantMassRemainingKg /
                mainMassFlowKgPerSecond;
            var projectionLimit = interceptorMissile
                ? terminal ? 0.35f : 0.8f
                : terminal ? 0.75f : 2.5f;
            var rangeTime = targetRange /
                            Mathf.Max(speed, 100f);
            var projectionTime = Mathf.Min(
                burnRemaining,
                projectionLimit,
                rangeTime);
            var axialAcceleration =
                mainEngineThrustNewtons /
                Mathf.Max(CurrentMassKg, 0.001f);
            return speed +
                   axialAcceleration *
                   Mathf.Max(0f, projectionTime) *
                   0.7f;
        }

        private float AvailableRcsAngularAcceleration()
        {
            if (rcsPropellantMassRemainingKg <= 0f ||
                rcsThrustNewtons <= 0f ||
                rcsMomentArmMeters <= 0f ||
                pitchYawInertiaKgM2 <= 0f)
            {
                return 0f;
            }

            return rcsThrustNewtons *
                   rcsMomentArmMeters /
                   pitchYawInertiaKgM2;
        }

        private float EstimateRcsDeltaVRemaining()
        {
            if (rcsPropellantMassRemainingKg <= 0f ||
                rcsThrustNewtons <= 0f ||
                rcsMassFlowKgPerSecond <= 0f)
            {
                return 0f;
            }

            var burnoutMass = Mathf.Max(
                dryMassKg + mainPropellantMassRemainingKg,
                0.001f);
            var currentMass = burnoutMass +
                              rcsPropellantMassRemainingKg;
            var effectiveExhaustVelocity =
                rcsThrustNewtons / rcsMassFlowKgPerSecond;
            return effectiveExhaustVelocity *
                   Mathf.Log(currentMass / burnoutMass);
        }

        private float EstimateMainEngineDeltaVRemaining()
        {
            if (mainPropellantMassRemainingKg <= 0f ||
                mainEngineThrustNewtons <= 0f ||
                mainMassFlowKgPerSecond <= 0f)
            {
                return 0f;
            }

            var burnoutMass = Mathf.Max(
                dryMassKg + rcsPropellantMassRemainingKg,
                0.001f);
            var currentMass = burnoutMass +
                              mainPropellantMassRemainingKg;
            var effectiveExhaustVelocity =
                mainEngineThrustNewtons / mainMassFlowKgPerSecond;
            return effectiveExhaustVelocity *
                   Mathf.Log(currentMass / burnoutMass);
        }

        private void ConsumeRcsPropellant(
            float throttle,
            float deltaTime)
        {
            if (throttle <= 0f ||
                deltaTime <= 0f ||
                rcsPropellantMassRemainingKg <= 0f)
            {
                return;
            }

            rcsPropellantMassRemainingKg = Mathf.Max(
                0f,
                rcsPropellantMassRemainingKg -
                rcsMassFlowKgPerSecond *
                Mathf.Clamp01(throttle) *
                deltaTime);
        }

        private void UpdateSeeker(float deltaTime)
        {
            if (!seekerHandle.IsValid || radarController == null)
            {
                seekerLocked = false;
                return;
            }

            var seekerForward = velocity.sqrMagnitude > 0.001f
                ? velocity.normalized
                : transform.forward;
            var estimateWithinGimbal = false;
            if (seekerLocked)
            {
                seekerEstimatedPosition +=
                    seekerEstimatedVelocity * deltaTime;
                // 锁定记忆唯一衰减路径：每帧扣 deltaTime，仅被接受的新量测重置满。
                // 原实现"框架角外按帧扣 + 无量测按扫描周期再扣"会双重扣减，
                // 实际记忆时长只有名义值的一半左右。
                seekerLockMemoryRemaining -= deltaTime;
                if (seekerLockMemoryRemaining <= 0f)
                {
                    seekerLocked = false;
                }
                else
                {
                    var toEstimate =
                        seekerEstimatedPosition - transform.position;
                    estimateWithinGimbal =
                        toEstimate.sqrMagnitude > 0.001f &&
                        PnGuidance.WithinGimbalLimit(
                            ToFloat3(toEstimate.normalized),
                            ToFloat3(seekerForward),
                            seekerGimbalRadians);
                }
            }

            seekerScanRemaining -= deltaTime;
            if (seekerScanRemaining > 0f)
            {
                return;
            }
            seekerScanRemaining = SeekerScanIntervalSeconds;

            // 估计位置出框架角时天线摆不过去，退回数据链/惯性方向搜索。
            var searchDirection = seekerLocked && estimateWithinGimbal
                ? seekerEstimatedPosition - transform.position
                : hasCachedGuidanceTrack && radarController != null
                    ? radarController.PredictTrackPosition(
                          cachedGuidanceTrack) -
                      transform.position
                    : velocity;
            if (searchDirection.sqrMagnitude <= 0.001f)
            {
                searchDirection = transform.forward;
            }
            radarController.ScheduleMissileSeekerDwell(
                seekerHandle,
                searchDirection,
                seekerLocked
                    ? 6f
                    : seekerHalfAngleRadians * Mathf.Rad2Deg * 2f,
                ++seekerDwellSequence);

            var measurementCount =
                radarController.CopyMissileSeekerMeasurements(
                    seekerHandle,
                    seekerMeasurementScratch);
            if (measurementCount <= 0)
            {
                return; // 记忆衰减已由上方每帧路径统一处理。
            }

            var bestScore = float.PositiveInfinity;
            var bestMeasurement = default(Measurement);
            var hasMeasurement = false;
            var hasHandoff = hasCachedGuidanceTrack;
            var hasInterceptorEstimate =
                interceptorTargetCommitted &&
                terminalFuseEstimateMemoryRemaining > 0f;
            var referencePosition = seekerLocked
                ? seekerEstimatedPosition
                : hasHandoff && radarController != null
                ? radarController.PredictTrackPosition(cachedGuidanceTrack)
                : hasInterceptorEstimate
                    ? terminalFuseEstimatedPosition
                : transform.position + transform.forward * seekerMaximumRange;
            var referenceVelocity = seekerLocked
                ? seekerEstimatedVelocity
                : hasHandoff
                    ? new Vector3(
                        (float)cachedGuidanceTrack.Velocity.x,
                        (float)cachedGuidanceTrack.Velocity.y,
                        (float)cachedGuidanceTrack.Velocity.z)
                    : hasInterceptorEstimate
                        ? terminalFuseEstimatedVelocity
                        : velocity;

            for (var i = 0; i < measurementCount; i++)
            {
                var measurement = seekerMeasurementScratch[i];
                if (interceptorMissile &&
                    (measurement.ObjectClass !=
                         TrackObjectClass.Missile ||
                     measurement.ClassificationConfidence < 0.25f))
                {
                    continue;
                }

                if (!float.IsFinite(measurement.Range) ||
                    measurement.Range <= 0f ||
                    measurement.Timestamp <=
                    seekerLastMeasurementTime)
                {
                    continue;
                }

                // 框架角物理限制：传感器仿真不建模天线罩，视线在框架角外的
                // 回波不可能被本导引头接收，在消费端过滤。
                if (!PnGuidance.WithinGimbalLimit(
                        measurement.Direction,
                        ToFloat3(seekerForward),
                        seekerGimbalRadians))
                {
                    continue;
                }

                var measurementAge = Mathf.Max(
                    0f,
                    (float)(radarController.SimTimeSeconds -
                            measurement.Timestamp));
                var sensorPositionAtMeasurement =
                    transform.position - velocity * measurementAge;
                var measuredPosition =
                    sensorPositionAtMeasurement +
                    ToVector3(measurement.Direction) *
                    measurement.Range;
                var associationError = Vector3.Distance(
                    measuredPosition,
                    referencePosition);
                var gate = interceptorTargetCommitted
                    ? seekerLocked
                        ? InterceptorReacquisitionGateMeters
                        : Mathf.Clamp(
                            terminalFuseEstimateUncertaintyMeters * 2f +
                            InterceptorAcquisitionGateMinimumMeters,
                            InterceptorAcquisitionGateMinimumMeters,
                            InterceptorAcquisitionGateMaximumMeters)
                    : seekerLocked
                        ? ReacquisitionGateMeters
                        : ReacquisitionGateMeters * 4f;
                if ((hasHandoff || seekerLocked ||
                     hasInterceptorEstimate) &&
                    associationError > gate)
                {
                    continue;
                }

                var candidateTargetVelocity =
                    EstimateSeekerTargetVelocity(
                        in measurement,
                        velocity);
                var velocityAssociationError = Vector3.Distance(
                    candidateTargetVelocity,
                    referenceVelocity);
                if (interceptorTargetCommitted &&
                    (!IsFinite(candidateTargetVelocity) ||
                     velocityAssociationError >
                         InterceptorVelocityAssociationGateMps))
                {
                    continue;
                }

                var score =
                    associationError * 2f +
                    velocityAssociationError * 0.35f -
                    measurement.SnrDb * 4f;
                if (score >= bestScore)
                {
                    continue;
                }

                bestScore = score;
                bestMeasurement = measurement;
                hasMeasurement = true;
            }

            if (!hasMeasurement)
            {
                return;
            }

            var direction =
                ToVector3(bestMeasurement.Direction).normalized;
            var relativePosition =
                direction * bestMeasurement.Range;
            var measuredTargetVelocity =
                EstimateSeekerTargetVelocity(
                    in bestMeasurement,
                    velocity);
            var selectedMeasurementAge = Mathf.Max(
                0f,
                (float)(radarController.SimTimeSeconds -
                        bestMeasurement.Timestamp));
            var selectedSensorPosition =
                transform.position -
                velocity * selectedMeasurementAge;
            var measuredPositionNow =
                selectedSensorPosition +
                relativePosition +
                measuredTargetVelocity *
                selectedMeasurementAge;
            if (interceptorTargetCommitted)
            {
                seekerEstimatedPosition = Vector3.Lerp(
                    referencePosition,
                    measuredPositionNow,
                    InterceptorSeekerPositionBlend);
                seekerEstimatedVelocity = Vector3.Lerp(
                    referenceVelocity,
                    measuredTargetVelocity,
                    InterceptorSeekerVelocityBlend);
            }
            else
            {
                seekerEstimatedPosition = measuredPositionNow;
                seekerEstimatedVelocity = measuredTargetVelocity;
            }
            seekerLastMeasurementTime = bestMeasurement.Timestamp;
            seekerLocked = true;
            seekerLockMemoryRemaining = interceptorMissile
                ? InterceptorSeekerLockMemorySeconds
                : SeekerLockMemorySeconds;
        }

        private static Vector3 EstimateSeekerTargetVelocity(
            in Measurement measurement,
            Vector3 sensorVelocity)
        {
            var direction =
                ToVector3(measurement.Direction).normalized;
            var relativePosition =
                direction * measurement.Range;
            var radialVelocity =
                float.IsFinite(measurement.RadialVelocity)
                    ? measurement.RadialVelocity
                    : 0f;
            var losRate = math.all(
                math.isfinite(measurement.LosRate))
                    ? ToVector3(measurement.LosRate)
                    : Vector3.zero;
            return sensorVelocity +
                   direction * radialVelocity +
                   Vector3.Cross(losRate, relativePosition);
        }

        private static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.x) &&
            float.IsFinite(value.y) &&
            float.IsFinite(value.z);

        /// <summary>
        /// 中段修正 = 消费经物理链路投递的 GuidanceUpdate 报文（发射端泵按周期
        /// 上行；再捕获也在发射端做）。载机被击落/出链路距离/被干扰 → 报文停，
        /// 导弹只剩记忆外推——不再有任何直读航迹库的后门。
        /// 制导通道（资源）与链路接收独立：pitbull 释放通道后上行仍在，
        /// 丢锁可靠链路重新引导。
        /// </summary>
        private bool TryUpdateDatalinkTrack(
            float deltaTime,
            out TrackSnapshot track)
        {
            track = default;
            if (radarController == null)
            {
                return false;
            }

            if (linkTerminal.IsValid)
            {
                var messageCount = radarController.CopyLinkMessages(
                    linkTerminal, linkInboxScratch);
                for (var i = 0; i < messageCount; i++)
                {
                    var message = linkInboxScratch[i];
                    if (message.Kind != LinkMessageKind.GuidanceUpdate ||
                        message.Sequence <= lastLinkSequence)
                    {
                        continue; // 投递箱按 Tick 清空但轮询更频繁：Sequence 去重。
                    }

                    if (interceptorTargetCommitted &&
                        targetTrackId.IsValid &&
                        !message.TrackPayload.TrackId.Equals(targetTrackId))
                    {
                        // Defensive interceptors must not accept a newly
                        // associated threat from the carrier uplink.
                        continue;
                    }

                    lastLinkSequence = message.Sequence;
                    cachedGuidanceTrack = message.TrackPayload;
                    hasCachedGuidanceTrack = true;
                    guidanceMemoryRemaining =
                        GetGuidanceMemorySeconds();
                }
            }

            guidanceMemoryRemaining =
                Mathf.Max(0f, guidanceMemoryRemaining - deltaTime);
            if (!hasCachedGuidanceTrack || guidanceMemoryRemaining <= 0f)
            {
                return false;
            }

            track = cachedGuidanceTrack;
            return true;
        }

        private float GetGuidanceMemorySeconds() =>
            interceptorTargetCommitted
                ? InterceptorGuidanceMemorySeconds
                : GuidanceMemorySeconds;

        private float GetTerminalEstimateMemorySeconds() =>
            interceptorTargetCommitted
                ? InterceptorGuidanceMemorySeconds
                : InterceptorFuseEstimateMemorySeconds;

        private bool TryDetonateProximity(float deltaTime)
        {
            if (missileFlightTime < proximityFuseArmTime ||
                distanceTravelled < proximityFuseSafeDistance)
            {
                return false;
            }

            var hasTerminalEstimate =
                seekerLocked ||
                (interceptorMissile &&
                 terminalFuseEstimateMemoryRemaining > 0f);
            if (!hasTerminalEstimate)
            {
                return false;
            }

            var estimatedPosition = seekerLocked
                ? seekerEstimatedPosition
                : terminalFuseEstimatedPosition;
            var estimatedVelocity = seekerLocked
                ? seekerEstimatedVelocity
                : terminalFuseEstimatedVelocity;
            var relativePosition =
                estimatedPosition - transform.position;
            var relativeVelocity =
                estimatedVelocity - velocity;
            var fuseLookAhead = interceptorMissile
                ? Mathf.Max(
                    deltaTime,
                    InterceptorFuseLookAheadSeconds)
                : deltaTime;
            var effectiveFuseRadius = proximityFuseRadius;
            if (interceptorMissile)
            {
                effectiveFuseRadius += Mathf.Min(
                    InterceptorFuseUncertaintyAllowanceMeters,
                    terminalFuseEstimateUncertaintyMeters * 0.05f);
            }
            if (!MissileEngagementMath.TryGetProximityPass(
                    ToFloat3(relativePosition),
                    ToFloat3(relativeVelocity),
                    fuseLookAhead,
                    effectiveFuseRadius,
                    out var timeToClosestApproach,
                    out var predictedMissDistance))
            {
                return false;
            }

            var detonationPoint =
                transform.position + velocity * timeToClosestApproach;
            if (interceptorMissile)
            {
                Debug.Log(
                    $"[Interceptor] Proximity fuze triggered: " +
                    $"CPA={timeToClosestApproach:0.000}s, " +
                    $"miss={predictedMissDistance:0.0}m, " +
                    $"radius={effectiveFuseRadius:0.0}m, " +
                    $"seeker={(seekerLocked ? "locked" : "inertial")}.");
            }
            DetonateWarhead(
                detonationPoint,
                interceptorMissile
                    ? timeToClosestApproach
                    : 0f);
            Destroy(gameObject);
            return true;
        }

        private void DetonateWarhead(
            Vector3 detonationPoint,
            float predictionSeconds = 0f)
        {
            if (interceptorMissile)
            {
                DetonateInterceptorWarhead(
                    detonationPoint,
                    predictionSeconds);
                return;
            }

            var colliderCount = Physics.OverlapSphereNonAlloc(
                detonationPoint,
                warheadLethalRadius,
                OverlapScratch,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Collide);
            var damagedAnyTarget = false;
            DamagedScratch.Clear();
            if (directGuidanceTarget != null)
            {
                damagedAnyTarget = TryDamageCombatTarget(
                    directGuidanceTarget,
                    detonationPoint);
            }
            for (var i = 0; i < colliderCount; i++)
            {
                var candidate =
                    OverlapScratch[i].GetComponentInParent<CombatTestTarget>();
                damagedAnyTarget |= TryDamageCombatTarget(
                    candidate,
                    detonationPoint);
            }

            if (!damagedAnyTarget)
            {
                MechTestWeaponSystem.SpawnImpact(
                    detonationPoint,
                    velocity.sqrMagnitude > 0.001f
                        ? -velocity.normalized
                        : -transform.forward,
                    weaponKind,
                    effectLibrary,
                    impactFfxId);
            }
        }

        private bool TryDamageCombatTarget(
            CombatTestTarget candidate,
            Vector3 detonationPoint)
        {
            if (candidate == null ||
                !candidate.IsAlive ||
                !DamagedScratch.Add(candidate))
            {
                return false;
            }

            var distance = Vector3.Distance(
                detonationPoint,
                candidate.AimPoint.position);
            if (distance > warheadLethalRadius)
            {
                return false;
            }

            var falloff = Mathf.Lerp(
                0.25f,
                1f,
                1f - distance / warheadLethalRadius);
            candidate.ApplyDamage(
                damage * falloff,
                detonationPoint,
                velocity.sqrMagnitude > 0.001f
                    ? velocity.normalized
                    : transform.forward,
                weaponKind,
                effectLibrary,
                impactFfxId);
            return true;
        }

        private bool UpdateDirectGuidanceSeekerEmission(
            float deltaTime,
            Vector3 targetPosition)
        {
            if (!seekerHandle.IsValid || radarController == null)
            {
                return false;
            }

            var direction = targetPosition - transform.position;
            if (direction.sqrMagnitude <= 0.001f)
            {
                return false;
            }

            direction.Normalize();
            var seekerForward = velocity.sqrMagnitude > 0.001f
                ? velocity.normalized
                : transform.forward;
            if (!PnGuidance.WithinGimbalLimit(
                    ToFloat3(direction),
                    ToFloat3(seekerForward),
                    seekerGimbalRadians))
            {
                return false;
            }

            seekerScanRemaining -= deltaTime;
            if (seekerScanRemaining > 0f)
            {
                return true;
            }

            seekerScanRemaining = SeekerScanIntervalSeconds;
            radarController.ScheduleMissileSeekerDwell(
                seekerHandle,
                direction,
                6f,
                ++seekerDwellSequence);
            return true;
        }

        /// <summary>
        /// 弹丸对来袭导弹的双体最近接近判定。与 <c>TryFindHit</c> 的 SphereCast 不同，这里同时
        /// 考虑双方在本 tick 内的真实位移，因此不受"侧向穿越必漏判"的影响。
        /// </summary>
        private bool TryFindMissileClosestApproach(
            float deltaTime,
            out MechTestProjectile target,
            out float interceptTime,
            out Vector3 interceptPoint)
        {
            target = null;
            interceptTime = float.PositiveInfinity;
            interceptPoint = default;
            if (registry.Count == 0)
            {
                return false;
            }

            // 动能弹只有物理接触半径；专用拦截弹种额外带一个小半径近炸（见
            // ConfigureInterceptorRound）。0.33 m 的接触半径在数公里外几乎不可能命中，
            // 近炸半径才是把命中窗口放大到有意义尺度的手段。
            var lethalRadius = Mathf.Max(
                collisionRadius + MissileHitboxHalfWidthMeters,
                interceptorRoundLethalRadius);
            var window = Mathf.Max(deltaTime, 0.0001f);
            var bestMiss = float.PositiveInfinity;

            for (var i = 0; i < registry.Count; i++)
            {
                var candidate = registry[i];
                if (candidate == null ||
                    candidate == this ||
                    candidate.IsOwnedBy(owner) ||
                    !candidate.IsInterceptableMissile)
                {
                    continue;
                }

                var relativePosition =
                    candidate.transform.position - transform.position;
                var relativeVelocity = candidate.velocity - velocity;
                var relativeSpeedSquared = relativeVelocity.sqrMagnitude;
                var cpaTime = relativeSpeedSquared > 0.001f
                    ? Mathf.Clamp(
                        -Vector3.Dot(relativePosition, relativeVelocity) /
                        relativeSpeedSquared,
                        0f,
                        window)
                    : 0f;
                var miss =
                    (relativePosition + relativeVelocity * cpaTime).magnitude;
                if (miss > lethalRadius)
                {
                    continue;
                }

                // 同 tick 内多个候选取最先接近的；时刻相同则取脱靶量更小的。
                if (cpaTime < interceptTime ||
                    (Mathf.Approximately(cpaTime, interceptTime) && miss < bestMiss))
                {
                    target = candidate;
                    interceptTime = cpaTime;
                    bestMiss = miss;
                    interceptPoint = transform.position + velocity * cpaTime;
                }
            }

            return target != null;
        }

        private bool TryApplyGunInterception(
            MechTestProjectile target,
            Vector3 interceptPoint)
        {
            if (target == null)
            {
                return false;
            }

            var hitDirection = velocity.sqrMagnitude > 0.001f
                ? velocity.normalized
                : transform.forward;
            // ApplyPointDefenseDamage 自己会生成命中特效，这里不要重复生成。
            // 返回 false 说明目标已经被别的弹丸解决掉了，本弹应当继续飞而不是凭空消失。
            return target.ApplyPointDefenseDamage(
                damage,
                interceptPoint,
                hitDirection,
                weaponKind,
                effectLibrary,
                impactFfxId);
        }

        /// <summary>
        /// 把本弹丸标记为专用拦截弹种：给一个小半径近炸，用于对抗来袭导弹。
        /// 普通弹丸不调用本方法，保持纯动能直击。
        /// </summary>
        public void ConfigureInterceptorRound(float lethalRadiusMeters)
        {
            interceptorRoundLethalRadius = Mathf.Max(0f, lethalRadiusMeters);
        }

        private void DetonateInterceptorWarhead(
            Vector3 detonationPoint,
            float predictionSeconds)
        {
            var damagedAnyMissile = false;
            DamagedProjectileScratch.Clear();
            var predictionWindow = Mathf.Max(
                Time.fixedDeltaTime,
                predictionSeconds);
            var eligibleMissileCount = 0;
            var nearestPhysicalMissDistance = float.PositiveInfinity;
            for (var i = 0; i < registry.Count; i++)
            {
                var candidate = registry[i];
                if (candidate == null ||
                    candidate == this ||
                    candidate.IsOwnedBy(owner) ||
                    !candidate.IsInterceptableMissile ||
                    !DamagedProjectileScratch.Add(candidate))
                {
                    continue;
                }

                eligibleMissileCount++;
                var relativePosition =
                    candidate.transform.position - transform.position;
                var relativeVelocity =
                    candidate.velocity - velocity;
                var relativeSpeedSquared =
                    relativeVelocity.sqrMagnitude;
                var physicalCpaTime = relativeSpeedSquared > 0.001f
                    ? Mathf.Clamp(
                        -Vector3.Dot(
                            relativePosition,
                            relativeVelocity) /
                        relativeSpeedSquared,
                        0f,
                        predictionWindow)
                    : 0f;
                var separationAtCpa =
                    relativePosition +
                    relativeVelocity * physicalCpaTime;
                var distance = separationAtCpa.magnitude;
                nearestPhysicalMissDistance = Mathf.Min(
                    nearestPhysicalMissDistance,
                    distance);
                if (distance > warheadLethalRadius)
                {
                    continue;
                }

                var physicalDetonationPoint =
                    transform.position +
                    velocity * physicalCpaTime;
                var falloff = Mathf.Lerp(
                    0.45f,
                    1f,
                    1f - distance / warheadLethalRadius);
                var healthBefore = candidate.interceptionHealth;
                var damageApplied =
                    candidate.ApplyPointDefenseDamage(
                    damage * falloff,
                    physicalDetonationPoint,
                    velocity.sqrMagnitude > 0.001f
                        ? velocity.normalized
                        : transform.forward,
                    TestWeaponKind.Missile,
                    effectLibrary,
                    impactFfxId);
                if (!damageApplied)
                {
                    continue;
                }

                damagedAnyMissile = true;
                Debug.Log(
                    $"[Interceptor] Warhead damaged missile: " +
                    $"CPA={physicalCpaTime:0.000}s, " +
                    $"miss={distance:0.0}m, " +
                    $"damage={damage * falloff:0.0}, " +
                    $"health={healthBefore:0.0}->" +
                    $"{candidate.interceptionHealth:0.0}, " +
                    $"destroyed={!candidate.IsInterceptableMissile}.");
            }

            if (!damagedAnyMissile)
            {
                Debug.LogWarning(
                    $"[Interceptor] Warhead detonated without a missile " +
                    $"inside lethal radius {warheadLethalRadius:0.0}m; " +
                    $"eligible={eligibleMissileCount}, nearest=" +
                    $"{nearestPhysicalMissDistance:0.0}m.");
                MechTestWeaponSystem.SpawnImpact(
                    detonationPoint,
                    velocity.sqrMagnitude > 0.001f
                        ? -velocity.normalized
                        : -transform.forward,
                    TestWeaponKind.Missile,
                    effectLibrary,
                    impactFfxId);
            }
        }

        public bool ApplyPointDefenseDamage(
            float incomingDamage,
            Vector3 hitPoint,
            Vector3 hitDirection,
            TestWeaponKind incomingWeaponKind,
            MechEffectPrefabLibrary incomingEffectLibrary = null,
            int incomingImpactFfxId = 0)
        {
            if (!IsInterceptableMissile || incomingDamage <= 0f)
            {
                return false;
            }

            interceptionHealth -= incomingDamage;
            if (interceptionHealth > 0f)
            {
                MechTestWeaponSystem.SpawnImpact(
                    hitPoint,
                    hitDirection.sqrMagnitude > 0.001f
                        ? -hitDirection.normalized
                        : -transform.forward,
                    incomingWeaponKind,
                    incomingEffectLibrary,
                    incomingImpactFfxId);
                return true;
            }

            interceptionResolved = true;
            var hitbox = GetComponent<Collider>();
            if (hitbox != null)
            {
                hitbox.enabled = false;
            }

            var warheadArmed =
                missileFlightTime >= proximityFuseArmTime &&
                distanceTravelled >= proximityFuseSafeDistance;
            if (warheadArmed)
            {
                DetonateWarhead(transform.position);
            }
            else
            {
                MechTestWeaponSystem.SpawnImpact(
                    hitPoint,
                    hitDirection.sqrMagnitude > 0.001f
                        ? -hitDirection.normalized
                        : -transform.forward,
                    incomingWeaponKind,
                    incomingEffectLibrary,
                    incomingImpactFfxId);
            }

            Destroy(gameObject);
            return true;
        }

        private bool TryFindHit(Vector3 travel, out RaycastHit selectedHit)
        {
            selectedHit = default;
            var distance = travel.magnitude;
            if (distance <= Mathf.Epsilon)
            {
                return false;
            }

            var hitCount = Physics.SphereCastNonAlloc(
                transform.position,
                collisionRadius,
                travel / distance,
                SphereCastScratch,
                distance,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Collide);
            var bestDistance = float.MaxValue;
            var found = false;
            for (var i = 0; i < hitCount; i++)
            {
                var hit = SphereCastScratch[i];
                if (hit.distance >= bestDistance)
                {
                    continue;
                }

                var colliderTransform = hit.collider.transform;
                if (owner != null &&
                    (colliderTransform == owner ||
                     colliderTransform.IsChildOf(owner)))
                {
                    continue;
                }

                var hitProjectile =
                    hit.collider.GetComponentInParent<MechTestProjectile>();
                if (hitProjectile != null)
                {
                    if (hitProjectile == this ||
                        hitProjectile.IsOwnedBy(owner))
                    {
                        continue;
                    }

                    if (weaponKind == TestWeaponKind.Missile &&
                        !interceptorMissile)
                    {
                        continue;
                    }

                    if (!hitProjectile.IsInterceptableMissile)
                    {
                        continue;
                    }
                }
                else if (interceptorMissile &&
                         hit.collider.GetComponentInParent<
                             CombatTestTarget>() != null)
                {
                    continue;
                }

                bestDistance = hit.distance;
                selectedHit = hit;
                found = true;
            }

            return found;
        }

        private void HandleDirectImpact(RaycastHit hit)
        {
            var hitProjectile =
                hit.collider.GetComponentInParent<MechTestProjectile>();
            if (hitProjectile != null &&
                hitProjectile.ApplyPointDefenseDamage(
                    damage,
                    hit.point,
                    velocity.sqrMagnitude > 0.001f
                        ? velocity.normalized
                        : transform.forward,
                    weaponKind,
                    effectLibrary,
                    impactFfxId))
            {
                return;
            }

            var hitTarget =
                hit.collider.GetComponentInParent<CombatTestTarget>();
            if (hitTarget != null)
            {
                if (interceptorMissile)
                {
                    return;
                }

                hitTarget.ApplyDamage(
                    damage,
                    hit.point,
                    velocity.sqrMagnitude > 0.001f
                        ? velocity.normalized
                        : transform.forward,
                    weaponKind,
                    effectLibrary,
                    impactFfxId);
                return;
            }

            MechTestWeaponSystem.SpawnImpact(
                hit.point,
                hit.normal,
                weaponKind,
                effectLibrary,
                impactFfxId);
        }

        private void SetMotorVisual(bool active)
        {
            if (motorVisualActive == active)
            {
                return;
            }
            motorVisualActive = active;
            missileAudio?.SetMotorActive(active);
            missilePlume?.SetBurning(active);

            if (projectileTrail != null)
            {
                projectileTrail.emitting = active;
            }

            for (var i = 0; i < propulsionParticles.Length; i++)
            {
                var particles = propulsionParticles[i];
                if (particles == null)
                {
                    continue;
                }

                if (active)
                {
                    particles.Play(true);
                }
                else
                {
                    particles.Stop(
                        true,
                        ParticleSystemStopBehavior.StopEmitting);
                }
            }
        }

        private void CreateRcsThrusterVisuals()
        {
            const int ringCount = 2;
            const int nozzlesPerRing = 4;
            const float nozzleRadius = 0.14f;
            var ringPositions = new[] { 0.78f, 0.08f };
            var exhaustDirections = new[]
            {
                Vector3.right,
                Vector3.up,
                Vector3.left,
                Vector3.down,
            };

            var root = new GameObject("Missile_RCS_Thrusters");
            root.transform.SetParent(transform, false);
            root.transform.localScale = new Vector3(
                SafeReciprocal(transform.localScale.x),
                SafeReciprocal(transform.localScale.y),
                SafeReciprocal(transform.localScale.z));

            rcsParticles =
                new ParticleSystem[ringCount * nozzlesPerRing];
            rcsExhaustDirectionsLocal =
                new Vector3[rcsParticles.Length];
            rcsTorqueAxesLocal =
                new Vector3[rcsParticles.Length];
            rcsPulsePhase = new float[rcsParticles.Length];
            var particleMaterial = GetRcsParticleMaterial();
            var thermal = root.AddComponent<MechEffectThermalAppearance>();
            thermal.Configure(MechEffectThermalAppearance.Signature.HotSaturating);
            var nozzleIndex = 0;
            for (var ring = 0; ring < ringCount; ring++)
            {
                for (var directionIndex = 0;
                     directionIndex < nozzlesPerRing;
                     directionIndex++)
                {
                    var exhaustDirection =
                        exhaustDirections[directionIndex];
                    var nozzle = new GameObject(
                        $"RCS_{ring}_{directionIndex}");
                    nozzle.transform.SetParent(root.transform, false);
                    nozzle.transform.localPosition =
                        exhaustDirection * nozzleRadius +
                        Vector3.forward * ringPositions[ring];
                    var upReference =
                        Mathf.Abs(Vector3.Dot(
                            exhaustDirection,
                            Vector3.up)) > 0.9f
                            ? Vector3.forward
                            : Vector3.up;
                    nozzle.transform.localRotation =
                        Quaternion.LookRotation(
                            exhaustDirection,
                            upReference);

                    var particles =
                        nozzle.AddComponent<ParticleSystem>();
                    ConfigureRcsParticleSystem(
                        particles,
                        particleMaterial);
                    thermal.Track(
                        particles.GetComponent<ParticleSystemRenderer>());
                    // 各喷口相位错开，避免整机同步闪烁成"一拍一拍"的假象。
                    rcsPulsePhase[nozzleIndex] =
                        (nozzleIndex * 0.37f) % 1f;
                    rcsParticles[nozzleIndex] = particles;
                    rcsExhaustDirectionsLocal[nozzleIndex] =
                        exhaustDirection;
                    var momentArmDirection = ring == 0
                        ? Vector3.forward
                        : Vector3.back;
                    rcsTorqueAxesLocal[nozzleIndex] =
                        Vector3.Cross(
                                momentArmDirection,
                                -exhaustDirection)
                            .normalized;
                    nozzleIndex++;
                }
            }
        }

        private static void ConfigureRcsParticleSystem(
            ParticleSystem particles,
            Material particleMaterial)
        {
            var main = particles.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace =
                ParticleSystemSimulationSpace.World;
            main.maxParticles = 64;
            main.startLifetime =
                new ParticleSystem.MinMaxCurve(0.05f, 0.1f);
            main.startSpeed =
                new ParticleSystem.MinMaxCurve(9f, 15f);
            main.startSize =
                new ParticleSystem.MinMaxCurve(0.03f, 0.055f);
            main.startColor = Color.white;

            var emission = particles.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;

            var shape = particles.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            // 单组元推进剂在真空里同样是欠膨胀自由膨胀，出口压远高于环境压（真空为 0），
            // 扩张角很大。原来的 5° 是大气/科幻片里的细直喷流读法，真空里不成立。
            shape.angle = 26f;
            shape.radius = 0.01f;
            shape.radiusThickness = 1f;

            // 欠膨胀羽流离开喷口后持续张开，用尺寸曲线表达，而不是靠拉伸渲染伪造。
            var sizeOverLifetime = particles.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(
                1f,
                new AnimationCurve(
                    new Keyframe(0f, 0.45f),
                    new Keyframe(0.35f, 1f),
                    new Keyframe(1f, 1.65f)));

            var colorOverLifetime = particles.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(
                new Gradient
                {
                    colorKeys = new[]
                    {
                        new GradientColorKey(
                            new Color(0.9f, 0.96f, 1f),
                            0f),
                        new GradientColorKey(Color.white, 1f),
                    },
                    alphaKeys = new[]
                    {
                        new GradientAlphaKey(0.95f, 0f),
                        new GradientAlphaKey(0.55f, 0.35f),
                        new GradientAlphaKey(0f, 1f),
                    },
                });

            var renderer =
                particles.GetComponent<ParticleSystemRenderer>();
            // 短促宽扇形优先于细长拉丝：Stretch 只保留很轻的一点速度方向感。
            renderer.renderMode =
                ParticleSystemRenderMode.Stretch;
            renderer.lengthScale = 0.7f;
            renderer.velocityScale = 0.02f;
            MechEffectResources.ConfigureUnlitRenderer(renderer, particleMaterial);

            particles.Play(false);
        }

        private void UpdateRcsThrusterVisuals(float deltaTime)
        {
            if (rcsParticles.Length == 0)
            {
                return;
            }

            var hasTranslationCommand =
                rcsAccelerationWorld.sqrMagnitude > 0.01f &&
                rcsPropellantMassRemainingKg > 0f;
            var hasAttitudeCommand =
                rcsAttitudeTorqueLocal.sqrMagnitude > 0.01f &&
                rcsAttitudeThrottle > 0.001f &&
                rcsPropellantMassRemainingKg > 0f;
            var exhaustDirectionWorld = hasTranslationCommand
                ? -rcsAccelerationWorld.normalized
                : Vector3.zero;
            var accelerationFraction = hasTranslationCommand
                ? Mathf.Clamp01(
                    rcsAccelerationWorld.magnitude /
                    Mathf.Max(
                        rcsThrustNewtons /
                        Mathf.Max(CurrentMassKg, 0.001f),
                        0.001f))
                : 0f;

            for (var i = 0; i < rcsParticles.Length; i++)
            {
                var particles = rcsParticles[i];
                if (particles == null)
                {
                    continue;
                }

                var nozzleDirectionWorld =
                    transform.TransformDirection(
                        rcsExhaustDirectionsLocal[i]);
                var translationAlignment = hasTranslationCommand
                    ? Mathf.Max(
                        0f,
                        Vector3.Dot(
                            nozzleDirectionWorld,
                            exhaustDirectionWorld))
                    : 0f;
                var translationOutput =
                    translationAlignment > 0.12f
                    ? Mathf.InverseLerp(
                          0.12f,
                          1f,
                          translationAlignment) *
                      Mathf.Lerp(0.55f, 1f, accelerationFraction)
                    : 0f;
                var attitudeAlignment = hasAttitudeCommand &&
                                        i < rcsTorqueAxesLocal.Length
                    ? Mathf.Max(
                        0f,
                        Vector3.Dot(
                            rcsTorqueAxesLocal[i],
                            rcsAttitudeTorqueLocal.normalized))
                    : 0f;
                var attitudeOutput = attitudeAlignment > 0.12f
                    ? Mathf.InverseLerp(
                          0.12f,
                          1f,
                          attitudeAlignment) *
                      Mathf.Lerp(
                          0.55f,
                          1f,
                          rcsAttitudeThrottle)
                    : 0f;
                var output = Mathf.Max(
                    translationOutput,
                    attitudeOutput);

                // bang-bang：阀门只有全开与全关两态，指令强度决定占空比。
                // 连续按比例缩放喷流量是电动比例阀的行为，冷气/单组元 RCS 不是这样工作的，
                // 脉冲串同时也符合"姿态控制在持续抖动纠偏"的观感。
                var emission = particles.emission;
                if (output <= 0.001f)
                {
                    rcsPulsePhase[i] = 0f;
                    emission.rateOverTime = 0f;
                    continue;
                }

                rcsPulsePhase[i] = Mathf.Repeat(
                    rcsPulsePhase[i] + deltaTime * RcsPulseHertz,
                    1f);
                // 最低占空比保证微弱指令也能看到独立的短脉冲，而不是稀疏到看不出喷。
                var dutyCycle = Mathf.Lerp(0.18f, 1f, output);
                emission.rateOverTime = rcsPulsePhase[i] < dutyCycle
                    ? RcsPulsePeakRate
                    : 0f;
            }
        }

        private static Material GetRcsParticleMaterial()
        {
            if (rcsParticleMaterial != null)
            {
                return rcsParticleMaterial;
            }

            // 修复前这里只设 _BaseColor/_Color，从未配置 URP Particles/Unlit 的混合状态。
            // 该 shader 默认 _Surface=0(Opaque)、_SrcBlend=One、_DstBlend=Zero、_ZWrite=1，
            // 又没有贴图，所以 8 个喷口渲染成的是不透明、写深度的白色拉伸方片。
            // 颜色值推过 Bloom 阈值（战斗 Volume 为 1.1）后喷流才会真正发光。
            rcsParticleMaterial = MechEffectResources.CreateAdditiveParticleMaterial(
                "Runtime Missile RCS Plume",
                new Color(2.1f, 2.35f, 2.6f, 1f));
            return rcsParticleMaterial;
        }

        private static float SafeReciprocal(float value) =>
            Mathf.Abs(value) > 0.0001f ? 1f / value : 1f;

        private void OnDestroy()
        {
            ReleaseGuidanceChannel();
            if (radarController != null)
            {
                if (irSourceRegistered)
                {
                    radarController.UnregisterMissileIrSource(in seekerHandle);
                }

                if (linkTerminal.IsValid)
                {
                    radarController.UnregisterMissileLinkTerminal(linkTerminal);
                }

                radarController.UnregisterMissileSeeker(seekerHandle);
            }

            linkTerminal = default;
            seekerHandle = default;
        }

        private void ReleaseGuidanceChannel()
        {
            if (guidanceChannelReleased)
            {
                return;
            }

            radarController?.ReleaseGuidanceChannel(guidanceChannelToken);
            guidanceChannelReleased = true;
            guidanceChannelToken = 0;
        }

        private static float3 ToFloat3(Vector3 value) =>
            new float3(value.x, value.y, value.z);

        private static Vector3 ToVector3(float3 value) =>
            new Vector3(value.x, value.y, value.z);
    }
}
