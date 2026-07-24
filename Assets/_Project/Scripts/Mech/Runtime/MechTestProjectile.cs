using System;
using ACunicombat.FireControl;
using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;
using UnityEngine;

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
        private const float GuidanceMemorySeconds = 4f;
        private const float ReacquisitionGateMeters = 350f;
        private const float SeekerScanIntervalSeconds = 0.05f;
        private const float SeekerLockMemorySeconds = 0.35f;

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
        private Vector3 seekerEstimatedPosition;
        private Vector3 seekerEstimatedVelocity;
        private double seekerLastMeasurementTime = -1.0;
        private ulong seekerDwellSequence;
        private MechRadarController.MissileSeekerHandle seekerHandle;
        private readonly Measurement[] seekerMeasurementScratch =
            new Measurement[8];
        private ParticleSystem[] propulsionParticles = Array.Empty<ParticleSystem>();
        private TrailRenderer projectileTrail;
        private bool motorVisualActive;

        public MissileFlightPhase MissilePhase => missilePhase;
        public bool SeekerLocked => seekerLocked;
        public float ManeuverDeltaVRemaining => maneuverDeltaVRemaining;
        public Vector3 Velocity => velocity;

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

            if (launchDelay > 0f)
            {
                launchDelay -= dt;
                if (launchDelay > 0f)
                {
                    return;
                }

                missilePhase = MissileFlightPhase.Boost;
                SetMotorVisual(weaponKind == TestWeaponKind.Missile);
            }

            var proximityDetonated = false;
            if (weaponKind == TestWeaponKind.Missile)
            {
                missileFlightTime += dt;
                UpdateMissile(dt);
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
            if (velocity.sqrMagnitude > 0.001f)
            {
                transform.rotation = Quaternion.LookRotation(
                    velocity.normalized,
                    transform.up);
            }
        }

        private void UpdateMissile(float deltaTime)
        {
            var motorActive = missileFlightTime < motorBurnDuration;
            SetMotorVisual(motorActive);
            radarController?.UpdateMissileSeekerState(
                seekerHandle,
                transform.position,
                velocity);

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
                ApplyGuidance(
                    targetPosition,
                    targetVelocity,
                    seekerLocked,
                    deltaTime);
            }
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

            if (seekerLocked)
            {
                seekerEstimatedPosition +=
                    seekerEstimatedVelocity * deltaTime;
                var toEstimate =
                    seekerEstimatedPosition - transform.position;
                var seekerForward = velocity.sqrMagnitude > 0.001f
                    ? velocity.normalized
                    : transform.forward;
                if (toEstimate.sqrMagnitude <= 0.001f ||
                    !PnGuidance.WithinGimbalLimit(
                        ToFloat3(toEstimate.normalized),
                        ToFloat3(seekerForward),
                        seekerGimbalRadians))
                {
                    seekerLockMemoryRemaining -= deltaTime;
                    if (seekerLockMemoryRemaining <= 0f)
                    {
                        seekerLocked = false;
                    }
                }
            }

            seekerScanRemaining -= deltaTime;
            if (seekerScanRemaining > 0f)
            {
                return;
            }
            seekerScanRemaining = SeekerScanIntervalSeconds;

            var searchDirection = seekerLocked
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
                seekerLockMemoryRemaining -= SeekerScanIntervalSeconds;
                if (seekerLockMemoryRemaining <= 0f)
                {
                    seekerLocked = false;
                }
                return;
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

        private bool TryUpdateDatalinkTrack(
            float deltaTime,
            out TrackSnapshot track)
        {
            track = default;
            if (guidanceChannelReleased ||
                !targetTrackId.IsValid ||
                radarController == null)
            {
                return false;
            }

            if (radarController.TryGetTrack(targetTrackId, out track))
            {
                cachedGuidanceTrack = track;
                hasCachedGuidanceTrack = true;
                guidanceMemoryRemaining = GuidanceMemorySeconds;
                return true;
            }

            if (hasCachedGuidanceTrack &&
                radarController.TryReacquireTrack(
                    cachedGuidanceTrack,
                    ReacquisitionGateMeters,
                    out track))
            {
                targetTrackId = track.TrackId;
                cachedGuidanceTrack = track;
                guidanceMemoryRemaining = GuidanceMemorySeconds;
                return true;
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
            var colliders = Physics.OverlapSphere(
                detonationPoint,
                warheadLethalRadius,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Collide);
            var damagedAnyTarget = false;
            var damagedTargets = new System.Collections.Generic.HashSet<
                CombatTestTarget>();
            for (var i = 0; i < colliders.Length; i++)
            {
                var candidate =
                    colliders[i].GetComponentInParent<CombatTestTarget>();
                if (candidate == null ||
                    !candidate.IsAlive ||
                    !damagedTargets.Add(candidate))
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

            var hits = Physics.SphereCastAll(
                transform.position,
                collisionRadius,
                travel / distance,
                distance,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Collide);
            Array.Sort(
                hits,
                (left, right) => left.distance.CompareTo(right.distance));
            for (var i = 0; i < hits.Length; i++)
            {
                var colliderTransform = hits[i].collider.transform;
                if (owner != null &&
                    (colliderTransform == owner ||
                     colliderTransform.IsChildOf(owner)))
                {
                    continue;
                }

                selectedHit = hits[i];
                return true;
            }

            return false;
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
            radarController?.UnregisterMissileSeeker(seekerHandle);
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
