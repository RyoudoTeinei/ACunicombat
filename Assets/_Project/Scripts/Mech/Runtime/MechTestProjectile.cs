using System;
using ACunicombat.Sensing.Contracts;
using UnityEngine;

namespace ACunicombat.Mechs
{
    [DisallowMultipleComponent]
    public sealed class MechTestProjectile : MonoBehaviour
    {
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
        private float maximumSpeed;
        private float turnRateRadians;
        private float maximumTravelDistance;
        private float distanceTravelled;
        private MechEffectPrefabLibrary effectLibrary;
        private int impactFfxId;
        private TrackSnapshot cachedGuidanceTrack;
        private bool hasCachedGuidanceTrack;
        private float guidanceCoastRemaining;
        private int guidanceChannelToken;
        private float guidanceChannelReleaseRemaining;
        private bool guidanceChannelReleased;

        private const float GuidanceCoastSeconds = 4f;
        private const float ActiveRadarMidcourseSeconds = 4f;
        private const float ReacquisitionGateMeters = 350f;

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
            float maximumSpeedMetersPerSecond = 0f,
            float turnRateDegreesPerSecond = 0f,
            MechEffectPrefabLibrary newEffectLibrary = null,
            int newImpactFfxId = 0,
            float newMaximumTravelDistance = 0f,
            int newGuidanceChannelToken = 0)
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
            maximumSpeed = Mathf.Max(initialVelocity.magnitude, maximumSpeedMetersPerSecond);
            turnRateRadians = Mathf.Max(0f, turnRateDegreesPerSecond) * Mathf.Deg2Rad;
            maximumTravelDistance = Mathf.Max(0f, newMaximumTravelDistance);
            distanceTravelled = 0f;
            effectLibrary = newEffectLibrary;
            impactFfxId = newImpactFfxId;
            guidanceChannelToken = newGuidanceChannelToken;
            guidanceChannelReleaseRemaining = guidanceChannelToken > 0
                ? ActiveRadarMidcourseSeconds
                : 0f;
            guidanceChannelReleased = guidanceChannelToken <= 0;

            hasCachedGuidanceTrack = weaponKind == TestWeaponKind.Missile &&
                                     targetTrackId.IsValid &&
                                     radarController != null &&
                                     radarController.TryGetTrack(targetTrackId, out cachedGuidanceTrack);
            guidanceCoastRemaining = hasCachedGuidanceTrack ? GuidanceCoastSeconds : 0f;
        }

        private void Update()
        {
            var dt = Time.deltaTime;
            lifeRemaining -= dt;
            if (lifeRemaining <= 0f)
            {
                Destroy(gameObject);
                return;
            }

            if (launchDelay > 0f)
            {
                launchDelay -= dt;
                return;
            }

            if (weaponKind == TestWeaponKind.Missile)
            {
                if (!guidanceChannelReleased)
                {
                    guidanceChannelReleaseRemaining -= dt;
                    if (guidanceChannelReleaseRemaining <= 0f)
                    {
                        ReleaseGuidanceChannel();
                    }
                }

                var speed = Mathf.Min(maximumSpeed, velocity.magnitude + acceleration * dt);
                if (TryUpdateGuidanceTrack(dt, out var track))
                {
                    var targetPosition = radarController.PredictTrackPosition(track);
                    var toTarget = targetPosition - transform.position;
                    if (toTarget.sqrMagnitude > 0.001f)
                    {
                        var timeToGo = Mathf.Clamp(
                            toTarget.magnitude / Mathf.Max(speed, 1f),
                            0f,
                            3f);
                        var targetVelocity = new Vector3(
                            (float)track.Velocity.x,
                            (float)track.Velocity.y,
                            (float)track.Velocity.z);
                        var desired = targetPosition + targetVelocity * timeToGo * 0.65f -
                                      transform.position;
                        var currentDirection = velocity.sqrMagnitude > 0.001f
                            ? velocity.normalized
                            : transform.forward;
                        var direction = Vector3.RotateTowards(
                            currentDirection,
                            desired.normalized,
                            turnRateRadians * dt,
                            0f);
                        velocity = direction * speed;
                    }
                    else
                    {
                        velocity = velocity.normalized * speed;
                    }
                }
                else if (velocity.sqrMagnitude > 0.001f)
                {
                    // Motor thrust continues after seeker/track loss; only steering is lost.
                    velocity = velocity.normalized * speed;
                }
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
                var hitTarget = hit.collider.GetComponentInParent<CombatTestTarget>();
                if (hitTarget != null)
                {
                    hitTarget.ApplyDamage(
                        damage, hit.point, velocity.normalized, weaponKind, effectLibrary, impactFfxId);
                }
                else
                {
                    MechTestWeaponSystem.SpawnImpact(
                        hit.point, hit.normal, weaponKind, effectLibrary, impactFfxId);
                }

                Destroy(gameObject);
                return;
            }

            transform.position += travel;
            distanceTravelled += travel.magnitude;
            if (velocity.sqrMagnitude > 0.001f)
            {
                transform.rotation = Quaternion.LookRotation(velocity.normalized, Vector3.up);
            }
        }

        private bool TryUpdateGuidanceTrack(float deltaTime, out TrackSnapshot track)
        {
            track = default;
            if (!targetTrackId.IsValid || radarController == null)
            {
                return false;
            }

            if (radarController.TryGetTrack(targetTrackId, out track))
            {
                cachedGuidanceTrack = track;
                hasCachedGuidanceTrack = true;
                guidanceCoastRemaining = GuidanceCoastSeconds;
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
                guidanceCoastRemaining = GuidanceCoastSeconds;
                return true;
            }

            guidanceCoastRemaining = Mathf.Max(0f, guidanceCoastRemaining - deltaTime);
            if (!hasCachedGuidanceTrack || guidanceCoastRemaining <= 0f)
            {
                return false;
            }

            track = cachedGuidanceTrack;
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

            var hits = Physics.SphereCastAll(
                transform.position,
                collisionRadius,
                travel / distance,
                distance,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Collide);
            Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
            for (var i = 0; i < hits.Length; i++)
            {
                var colliderTransform = hits[i].collider.transform;
                if (owner != null && (colliderTransform == owner || colliderTransform.IsChildOf(owner)))
                {
                    continue;
                }

                selectedHit = hits[i];
                return true;
            }

            return false;
        }

        private void OnDestroy()
        {
            ReleaseGuidanceChannel();
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
    }
}
