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
    }

    [DisallowMultipleComponent]
    public sealed class MechTestProjectile : MonoBehaviour
    {
        private static readonly System.Collections.Generic.List<MechTestProjectile> registry = new();
        public static System.Collections.Generic.IReadOnlyList<MechTestProjectile> Registry => registry;

        private const float GuidanceMemorySeconds = 4f;
        private const float ReacquisitionGateMeters = 350f;
        private const float SeekerScanIntervalSeconds = 0.05f;
        private const float SeekerLockMemorySeconds = 0.35f;

        // 物理查询复用缓冲：SphereCastAll/OverlapSphere 每次分配数组，
        // 弹幕场景下是稳定的每帧 GC 压力源。主线程单发访问，静态复用安全。
        private static readonly RaycastHit[] SphereCastScratch = new RaycastHit[32];
        private static readonly Collider[] OverlapScratch = new Collider[64];
        private static readonly System.Collections.Generic.HashSet<CombatTestTarget> DamagedScratch = new();

        private Transform owner;
        private MechRadarController radarController;
        private TrackId targetTrackId;
        private TestWeaponKind weaponKind;
        private Vector3 velocity;
        private float damage;
        private float collisionRadius;
        private float lifeRemaining;
        private float launchDelay;
        private float acceleration;
        private float turnRateRadians;
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
        private float motorBurnDuration;
        private float motorBurnElapsed;
        private float seekerActivationDelay;
        private float seekerMaximumRange;
        private float seekerHalfAngleRadians;
        private float seekerGimbalRadians;
        private float maximumLateralAcceleration;
        private float maneuverDeltaVRemaining;
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

        public MissileFlightPhase MissilePhase => missilePhase;
        public bool SeekerLocked => seekerLocked;
        public float ManeuverDeltaVRemaining => maneuverDeltaVRemaining;
        public Vector3 Velocity => velocity;
        public bool IsMissile => weaponKind == TestWeaponKind.Missile;
        public TrackId GuidanceTrackId =>
            hasCachedGuidanceTrack && cachedGuidanceTrack.TrackId.IsValid
                ? cachedGuidanceTrack.TrackId
                : targetTrackId;

        public bool IsOwnedBy(Transform candidateOwner) =>
            candidateOwner != null && owner == candidateOwner;

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
            float newProximityFuseRadius = 12f,
            float newProximityFuseArmTime = 0.75f,
            float newProximityFuseSafeDistance = 20f,
            float newWarheadLethalRadius = 18f)
        {
            owner = newOwner;
            radarController = newRadarController;
            targetTrackId = newTargetTrackId;
            weaponKind = kind;
            velocity = initialVelocity;
            desiredAttitudeForward = transform.forward.sqrMagnitude > 0.001f
                ? transform.forward.normalized
                : initialVelocity.sqrMagnitude > 0.001f
                    ? initialVelocity.normalized
                    : Vector3.forward;
            damage = newDamage;
            collisionRadius = Mathf.Max(0.01f, newCollisionRadius);
            lifeRemaining = Mathf.Max(0.1f, lifetimeSeconds);
            launchDelay = Mathf.Max(0f, delaySeconds);
            acceleration = Mathf.Max(0f, accelerationMetersPerSecondSquared);
            turnRateRadians = Mathf.Max(0f, turnRateDegreesPerSecond) * Mathf.Deg2Rad;
            maximumTravelDistance = Mathf.Max(0f, newMaximumTravelDistance);
            distanceTravelled = 0f;
            effectLibrary = newEffectLibrary;
            impactFfxId = newImpactFfxId;
            guidanceChannelToken = newGuidanceChannelToken;
            guidanceChannelReleased = guidanceChannelToken <= 0;

            motorBurnDuration = Mathf.Max(0.05f, newMotorBurnDuration);
            seekerActivationDelay = Mathf.Max(0.05f, newSeekerActivationDelay);
            seekerMaximumRange = Mathf.Max(100f, newSeekerMaximumRange);
            seekerHalfAngleRadians =
                Mathf.Clamp(newSeekerHalfAngleDegrees, 1f, 89f) * Mathf.Deg2Rad;
            seekerGimbalRadians =
                Mathf.Clamp(newSeekerGimbalDegrees, 1f, 89f) * Mathf.Deg2Rad;
            maximumLateralAcceleration =
                Mathf.Max(0f, newMaximumLateralAcceleration);
            maneuverDeltaVRemaining = Mathf.Max(0f, newManeuverDeltaV);
            proximityFuseRadius = Mathf.Max(collisionRadius, newProximityFuseRadius);
            proximityFuseArmTime = Mathf.Max(0f, newProximityFuseArmTime);
            proximityFuseSafeDistance =
                Mathf.Max(collisionRadius, newProximityFuseSafeDistance);
            warheadLethalRadius =
                Mathf.Max(proximityFuseRadius, newWarheadLethalRadius);

            hasCachedGuidanceTrack =
                weaponKind == TestWeaponKind.Missile &&
                targetTrackId.IsValid &&
                radarController != null &&
                radarController.TryGetTrack(
                    targetTrackId,
                    out cachedGuidanceTrack);
            guidanceMemoryRemaining =
                hasCachedGuidanceTrack ? GuidanceMemorySeconds : 0f;

            projectileTrail = GetComponent<TrailRenderer>();
            propulsionParticles = GetComponentsInChildren<ParticleSystem>(true);
            if (weaponKind == TestWeaponKind.Missile &&
                radarController != null)
            {
                radarController.TryRegisterMissileSeeker(
                    transform.position,
                    velocity,
                    seekerMaximumRange,
                    newSeekerHalfAngleDegrees * 2f,
                    out seekerHandle);
                if (seekerHandle.IsValid)
                {
                    // 数据链终端 + 制导上行订阅：发射后的航迹更新只经物理链路到达。
                    if (radarController.TryRegisterMissileLinkTerminal(
                            in seekerHandle, out linkTerminal) &&
                        targetTrackId.IsValid)
                    {
                        radarController.BeginGuidanceUplink(linkTerminal, targetTrackId);
                    }

                    // 红外点源（MAW 数据源）：发动机状态决定强度。
                    radarController.RegisterMissileIrSource(in seekerHandle, CoastIrIntensityDb);
                    irSourceRegistered = true;
                    irMotorStateRegistered = false;
                }
            }
            missilePhase = launchDelay > 0f
                ? MissileFlightPhase.LaunchDelay
                : MissileFlightPhase.Boost;
            if (weaponKind == TestWeaponKind.Missile)
            {
                var motorStartsImmediately = launchDelay <= 0f;
                motorVisualActive = !motorStartsImmediately;
                SetMotorVisual(motorStartsImmediately);
            }
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

            var motorIgnited = launchDelay <= 0f;
            if (weaponKind == TestWeaponKind.Missile && launchDelay > 0f)
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
                    motorBurnElapsed += dt;
                    UpdateMissile(dt);
                    UpdateMissileAttitude(dt);
                }
                else
                {
                    // Cold ejection still moves ballistically; only motor,
                    // seeker and guidance wait for safe separation.
                    radarController?.UpdateMissileSeekerState(
                        seekerHandle,
                        transform.position,
                        velocity);
                }
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

            if (TryFindHit(travel, out var hit))
            {
                HandleDirectImpact(hit);
                Destroy(gameObject);
                return;
            }

            transform.position += travel;
            distanceTravelled += travel.magnitude;
            if (weaponKind != TestWeaponKind.Missile &&
                velocity.sqrMagnitude > 0.001f)
            {
                transform.rotation = Quaternion.LookRotation(
                    velocity.normalized,
                    transform.up);
            }
        }

        private void UpdateMissile(float deltaTime)
        {
            var motorActive = motorBurnElapsed < motorBurnDuration;
            SetMotorVisual(motorActive);
            radarController?.UpdateMissileSeekerState(
                seekerHandle,
                transform.position,
                velocity);
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
                var thrustDirection =
                    transform.forward.sqrMagnitude > 0.001f
                        ? transform.forward.normalized
                        : currentSpeed > 0.01f
                            ? velocity / currentSpeed
                            : Vector3.forward;
                velocity +=
                    thrustDirection * acceleration * deltaTime;
            }

            Vector3 targetPosition;
            Vector3 targetVelocity;
            var hasGuidance = false;
            var seekerActive = missileFlightTime >= seekerActivationDelay;

            if (seekerActive)
            {
                UpdateSeeker(deltaTime);
            }

            if (seekerLocked)
            {
                missilePhase = MissileFlightPhase.TerminalGuidance;
                targetPosition = seekerEstimatedPosition;
                targetVelocity = seekerEstimatedVelocity;
                hasGuidance = true;
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

            if (hasGuidance)
            {
                var toTarget = targetPosition - transform.position;
                if (toTarget.sqrMagnitude > 0.001f)
                {
                    desiredAttitudeForward = toTarget.normalized;
                }
                ApplyGuidance(
                    targetPosition,
                    targetVelocity,
                    seekerLocked,
                    deltaTime);
            }
            else if (velocity.sqrMagnitude > 0.001f)
            {
                desiredAttitudeForward = velocity.normalized;
            }
        }

        private void UpdateMissileAttitude(float deltaTime)
        {
            if (desiredAttitudeForward.sqrMagnitude <= 0.001f)
            {
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
            var turnDegreesPerSecond = turnRateRadians > 0f
                ? turnRateRadians * Mathf.Rad2Deg
                : 90f;
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation,
                targetRotation,
                turnDegreesPerSecond * deltaTime);
        }

        private void ApplyGuidance(
            Vector3 targetPosition,
            Vector3 targetVelocity,
            bool terminal,
            float deltaTime)
        {
            if (velocity.sqrMagnitude <= 0.001f ||
                maneuverDeltaVRemaining <= 0f)
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
            var turnLimitedAcceleration = turnRateRadians > 0f
                ? turnRateRadians * speed
                : maximumLateralAcceleration;
            var phaseAcceleration = terminal
                ? maximumLateralAcceleration
                : maximumLateralAcceleration * 0.38f;
            var accelerationLimit = Mathf.Min(
                phaseAcceleration,
                turnLimitedAcceleration,
                maneuverDeltaVRemaining / Mathf.Max(deltaTime, 0.001f));
            if (accelerationLimit <= 0f)
            {
                return;
            }

            var command = PnGuidance.Command(
                losRate,
                ToFloat3(losDirection),
                closingVelocity,
                terminal ? 4f : 3f,
                accelerationLimit);
            var lateralAcceleration = Vector3.ProjectOnPlane(
                ToVector3(command),
                velocity.normalized);

            // PN can be nearly zero immediately after launch even when the
            // handoff basket is off-axis. A bounded pursuit term establishes
            // the intercept geometry without directly rotating velocity.
            var desiredLateralVelocity = Vector3.ProjectOnPlane(
                losDirection * speed,
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

            var preservedSpeed = velocity.magnitude;
            velocity += lateralAcceleration * deltaTime;
            if (velocity.sqrMagnitude > 0.001f)
            {
                velocity = velocity.normalized * preservedSpeed;
            }
            maneuverDeltaVRemaining =
                Mathf.Max(0f, maneuverDeltaVRemaining - usedDeltaV);
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
            var referencePosition = seekerLocked
                ? seekerEstimatedPosition
                : hasHandoff && radarController != null
                ? radarController.PredictTrackPosition(cachedGuidanceTrack)
                : transform.position + transform.forward * seekerMaximumRange;

            for (var i = 0; i < measurementCount; i++)
            {
                var measurement = seekerMeasurementScratch[i];
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
                var gate = seekerLocked
                    ? ReacquisitionGateMeters
                    : ReacquisitionGateMeters * 4f;
                if ((hasHandoff || seekerLocked) &&
                    associationError > gate)
                {
                    continue;
                }

                var score =
                    associationError * 2f -
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
            var radialVelocity =
                float.IsFinite(bestMeasurement.RadialVelocity)
                    ? bestMeasurement.RadialVelocity
                    : 0f;
            var losRate = math.all(
                math.isfinite(bestMeasurement.LosRate))
                ? ToVector3(bestMeasurement.LosRate)
                : Vector3.zero;
            var relativeVelocity =
                direction * radialVelocity +
                Vector3.Cross(losRate, relativePosition);
            seekerEstimatedVelocity =
                velocity + relativeVelocity;
            var selectedMeasurementAge = Mathf.Max(
                0f,
                (float)(radarController.SimTimeSeconds -
                        bestMeasurement.Timestamp));
            var selectedSensorPosition =
                transform.position -
                velocity * selectedMeasurementAge;
            seekerEstimatedPosition =
                selectedSensorPosition +
                relativePosition +
                seekerEstimatedVelocity *
                selectedMeasurementAge;
            seekerLastMeasurementTime = bestMeasurement.Timestamp;
            seekerLocked = true;
            seekerLockMemoryRemaining = SeekerLockMemorySeconds;
        }

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

                    lastLinkSequence = message.Sequence;
                    cachedGuidanceTrack = message.TrackPayload;
                    hasCachedGuidanceTrack = true;
                    guidanceMemoryRemaining = GuidanceMemorySeconds;
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

        private bool TryDetonateProximity(float deltaTime)
        {
            if (!seekerLocked ||
                missileFlightTime < proximityFuseArmTime ||
                distanceTravelled < proximityFuseSafeDistance)
            {
                return false;
            }

            var relativePosition =
                seekerEstimatedPosition - transform.position;
            var relativeVelocity =
                seekerEstimatedVelocity - velocity;
            if (!MissileEngagementMath.TryGetProximityPass(
                    ToFloat3(relativePosition),
                    ToFloat3(relativeVelocity),
                    deltaTime,
                    proximityFuseRadius,
                    out var timeToClosestApproach,
                    out _))
            {
                return false;
            }

            var detonationPoint =
                transform.position + velocity * timeToClosestApproach;
            DetonateWarhead(detonationPoint);
            Destroy(gameObject);
            return true;
        }

        private void DetonateWarhead(Vector3 detonationPoint)
        {
            var colliderCount = Physics.OverlapSphereNonAlloc(
                detonationPoint,
                warheadLethalRadius,
                OverlapScratch,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Collide);
            var damagedAnyTarget = false;
            DamagedScratch.Clear();
            for (var i = 0; i < colliderCount; i++)
            {
                var candidate =
                    OverlapScratch[i].GetComponentInParent<CombatTestTarget>();
                if (candidate == null ||
                    !candidate.IsAlive ||
                    !DamagedScratch.Add(candidate))
                {
                    continue;
                }

                var distance = Vector3.Distance(
                    detonationPoint,
                    candidate.AimPoint.position);
                if (distance > warheadLethalRadius)
                {
                    continue;
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
                damagedAnyTarget = true;
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

                bestDistance = hit.distance;
                selectedHit = hit;
                found = true;
            }

            return found;
        }

        private void HandleDirectImpact(RaycastHit hit)
        {
            var hitTarget =
                hit.collider.GetComponentInParent<CombatTestTarget>();
            if (hitTarget != null)
            {
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
