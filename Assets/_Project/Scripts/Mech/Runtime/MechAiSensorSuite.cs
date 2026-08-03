using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Sensor simulation owned by an AI combatant. Truth is used only to
    /// generate noisy observations; consumers receive the filtered track.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechAiSensorSuite : MonoBehaviour
    {
        public readonly struct SensorTrack
        {
            public SensorTrack(
                Vector3 position,
                Vector3 velocity,
                Vector3 acceleration,
                float confidence,
                float uncertaintyMeters,
                float ageSeconds,
                bool radar,
                bool optical,
                bool laser)
            {
                Position = position;
                Velocity = velocity;
                Acceleration = acceleration;
                Confidence = confidence;
                UncertaintyMeters = uncertaintyMeters;
                AgeSeconds = ageSeconds;
                HasRadar = radar;
                HasOptical = optical;
                HasLaserRange = laser;
            }

            public Vector3 Position { get; }
            public Vector3 Velocity { get; }
            public Vector3 Acceleration { get; }
            public float Confidence { get; }
            public float UncertaintyMeters { get; }
            public float AgeSeconds { get; }
            public bool HasRadar { get; }
            public bool HasOptical { get; }
            public bool HasLaserRange { get; }

            public Vector3 Predict(float seconds)
            {
                seconds = Mathf.Max(0f, seconds);
                return Position + Velocity * seconds +
                       Acceleration * (0.5f * seconds * seconds);
            }
        }

        [Header("Radar")]
        [SerializeField, Min(1000f)] private float radarMaximumRangeMeters = 50000f;
        [SerializeField, Range(5f, 90f)] private float radarHalfAzimuthDegrees = 50f;
        [SerializeField, Range(5f, 60f)] private float radarHalfElevationDegrees = 30f;
        [SerializeField, Min(0.02f)] private float searchRevisitSeconds = 0.12f;
        [SerializeField, Min(0.01f)] private float trackRevisitSeconds = 0.04f;
        [SerializeField, Min(0f)] private float searchBearingSigmaDegrees = 1.5f;
        [SerializeField, Min(0f)] private float trackBearingSigmaDegrees = 0.22f;
        [SerializeField, Min(0f)] private float radarRangeSigmaMeters = 12f;
        [SerializeField, Min(0f)] private float radarVelocitySigmaMetersPerSecond = 2f;

        [Header("Electro-optical and laser")]
        [SerializeField, Min(100f)] private float opticalMaximumRangeMeters = 30000f;
        [SerializeField, Range(1f, 60f)] private float opticalHalfAngleDegrees = 18f;
        [SerializeField, Min(0f)] private float opticalBearingSigmaDegrees = 0.15f;
        [SerializeField, Min(100f)] private float laserMaximumRangeMeters = 20000f;
        [SerializeField, Range(0.05f, 5f)] private float laserHalfAngleDegrees = 0.8f;
        [SerializeField, Min(0f)] private float laserRangeSigmaMeters = 0.75f;

        [Header("Track filter")]
        [SerializeField, Range(0.05f, 1f)] private float positionGain = 0.58f;
        [SerializeField, Range(0.01f, 1f)] private float velocityGain = 0.34f;
        [SerializeField, Range(0.01f, 1f)] private float accelerationGain = 0.2f;
        [SerializeField, Min(0.1f)] private float coastMemorySeconds = 2.2f;
        [SerializeField, Min(1)] private int confirmationHits = 3;

        private static readonly RaycastHit[] OcclusionHits = new RaycastHit[32];
        private static uint nextNoiseSeed = 0x85ebca6bu;

        private CombatTestTarget target;
        private Camera sensorCamera;
        private MechFlightMotionController targetMotion;
        private float nextObservationTime;
        private float lastFilterTime;
        private float lastObservationTime = float.NegativeInfinity;
        private Vector3 estimatedPosition;
        private Vector3 estimatedVelocity;
        private Vector3 estimatedAcceleration;
        private Vector3 previousMeasuredVelocity;
        private float uncertaintyMeters = 1000f;
        private float confidence;
        private int hitCount;
        private bool initialized;
        private bool lastRadar;
        private bool lastOptical;
        private bool lastLaser;
        private uint noiseState;

        public bool HasTrack =>
            initialized &&
            Time.time - lastObservationTime <= coastMemorySeconds;

        public bool HasFireControlTrack =>
            HasTrack &&
            hitCount >= confirmationHits &&
            confidence >= 0.55f &&
            Time.time - lastObservationTime <= 0.65f;

        public void Configure(CombatTestTarget newTarget, Camera newSensorCamera)
        {
            target = newTarget;
            sensorCamera = newSensorCamera;
            targetMotion = target != null
                ? target.GetComponent<MechFlightMotionController>()
                : null;
        }

        private void Awake()
        {
            noiseState = nextNoiseSeed;
            nextNoiseSeed = unchecked(nextNoiseSeed + 0x6d2b79f5u);
            if (noiseState == 0u)
            {
                noiseState = 1u;
            }
        }

        public void Tick(float now)
        {
            CoastTrack(now);
            if (target == null || !target.IsAlive ||
                sensorCamera == null || now < nextObservationTime)
            {
                return;
            }

            var origin = sensorCamera.transform.position;
            var truePosition = target.AimPoint.position;
            var toTarget = truePosition - origin;
            var range = toTarget.magnitude;
            if (range <= 0.01f)
            {
                return;
            }

            var direction = toTarget / range;
            var local = sensorCamera.transform.InverseTransformDirection(direction);
            var azimuth = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            var elevation = Mathf.Atan2(
                local.y,
                Mathf.Sqrt(local.x * local.x + local.z * local.z)) *
                Mathf.Rad2Deg;
            var clearLineOfSight = HasLineOfSight(origin, direction, range);
            var rcsScale = Mathf.Pow(
                Mathf.Max(0.01f, target.RadarCrossSectionSquareMeters / 18f),
                0.25f);
            var effectiveRadarRange = radarMaximumRangeMeters * rcsScale;
            var radarInFrame = local.z > 0f &&
                               Mathf.Abs(azimuth) <= radarHalfAzimuthDegrees &&
                               Mathf.Abs(elevation) <= radarHalfElevationDegrees;
            var radarDetection = false;
            if (radarInFrame && clearLineOfSight && range <= effectiveRadarRange)
            {
                var normalizedRange = range / Mathf.Max(1f, effectiveRadarRange);
                var probability = Mathf.Clamp01(
                    1.08f - normalizedRange * normalizedRange *
                    normalizedRange * normalizedRange);
                radarDetection = NextUniform() <= probability;
            }

            var angularOffset = Vector3.Angle(
                sensorCamera.transform.forward,
                direction);
            var opticalDetection = clearLineOfSight &&
                                   range <= opticalMaximumRangeMeters &&
                                   angularOffset <= opticalHalfAngleDegrees;
            if (!radarDetection &&
                !(opticalDetection && HasTrack))
            {
                nextObservationTime = now + searchRevisitSeconds;
                return;
            }

            var laserDetection = HasTrack && clearLineOfSight &&
                                 range <= laserMaximumRangeMeters &&
                                 angularOffset <= laserHalfAngleDegrees;
            var bearingSigma = opticalDetection
                ? opticalBearingSigmaDegrees
                : HasFireControlTrack
                    ? trackBearingSigmaDegrees
                    : searchBearingSigmaDegrees;
            var rangeSigma = laserDetection
                ? laserRangeSigmaMeters
                : radarDetection
                    ? radarRangeSigmaMeters
                    : Mathf.Max(uncertaintyMeters, range * 0.015f);
            var measuredRange = radarDetection || laserDetection
                ? Mathf.Max(1f, range + NextGaussian() * rangeSigma)
                : Mathf.Max(1f, Vector3.Distance(origin, estimatedPosition));
            var measuredDirection = ApplyBearingNoise(direction, bearingSigma);
            var measuredPosition = origin + measuredDirection * measuredRange;
            var trueVelocity = targetMotion != null
                ? targetMotion.Velocity
                : Vector3.zero;
            var measuredVelocity = trueVelocity + new Vector3(
                NextGaussian(),
                NextGaussian(),
                NextGaussian()) * radarVelocitySigmaMetersPerSecond;

            UpdateTrack(
                now,
                measuredPosition,
                measuredVelocity,
                rangeSigma,
                bearingSigma,
                range,
                radarDetection,
                opticalDetection,
                laserDetection);
            nextObservationTime = now +
                                  (HasFireControlTrack
                                      ? trackRevisitSeconds
                                      : searchRevisitSeconds);
        }

        public bool TryGetTrack(out SensorTrack track)
        {
            if (!HasTrack)
            {
                track = default;
                return false;
            }

            var age = Mathf.Max(0f, Time.time - lastFilterTime);
            track = new SensorTrack(
                estimatedPosition + estimatedVelocity * age +
                estimatedAcceleration * (0.5f * age * age),
                estimatedVelocity + estimatedAcceleration * age,
                estimatedAcceleration,
                confidence,
                uncertaintyMeters + age * Mathf.Max(2f, estimatedVelocity.magnitude * 0.01f),
                Mathf.Max(0f, Time.time - lastObservationTime),
                lastRadar,
                lastOptical,
                lastLaser);
            return true;
        }

        private void CoastTrack(float now)
        {
            if (!initialized)
            {
                return;
            }

            var dt = Mathf.Max(0f, now - lastFilterTime);
            if (dt > 0f)
            {
                estimatedPosition += estimatedVelocity * dt +
                                     estimatedAcceleration * (0.5f * dt * dt);
                estimatedVelocity += estimatedAcceleration * dt;
                uncertaintyMeters += dt * 8f;
                lastFilterTime = now;
            }

            var age = now - lastObservationTime;
            if (age > 0.25f)
            {
                confidence = Mathf.MoveTowards(
                    confidence,
                    0f,
                    dt / Mathf.Max(0.1f, coastMemorySeconds));
            }
            if (age > coastMemorySeconds)
            {
                initialized = false;
                hitCount = 0;
                confidence = 0f;
            }
        }

        private void UpdateTrack(
            float now,
            Vector3 measuredPosition,
            Vector3 measuredVelocity,
            float rangeSigma,
            float bearingSigmaDegrees,
            float range,
            bool radar,
            bool optical,
            bool laser)
        {
            if (!initialized)
            {
                estimatedPosition = measuredPosition;
                estimatedVelocity = measuredVelocity;
                estimatedAcceleration = Vector3.zero;
                previousMeasuredVelocity = measuredVelocity;
                initialized = true;
                hitCount = 0;
            }
            else
            {
                var dt = Mathf.Max(0.02f, now - lastObservationTime);
                estimatedPosition = Vector3.Lerp(
                    estimatedPosition,
                    measuredPosition,
                    positionGain);
                estimatedVelocity = Vector3.Lerp(
                    estimatedVelocity,
                    measuredVelocity,
                    velocityGain);
                var measuredAcceleration =
                    (measuredVelocity - previousMeasuredVelocity) / dt;
                estimatedAcceleration = Vector3.Lerp(
                    estimatedAcceleration,
                    measuredAcceleration,
                    accelerationGain);
                previousMeasuredVelocity = measuredVelocity;
            }

            hitCount++;
            confidence = Mathf.Clamp01(
                confidence + (laser ? 0.24f : optical && radar ? 0.18f : 0.12f));
            uncertaintyMeters = Mathf.Max(
                0.5f,
                rangeSigma + Mathf.Tan(bearingSigmaDegrees * Mathf.Deg2Rad) * range);
            lastObservationTime = now;
            lastFilterTime = now;
            lastRadar = radar;
            lastOptical = optical;
            lastLaser = laser;
        }

        private bool HasLineOfSight(Vector3 origin, Vector3 direction, float range)
        {
            var hitCount = Physics.RaycastNonAlloc(
                origin,
                direction,
                OcclusionHits,
                range + 2f,
                ~0,
                QueryTriggerInteraction.Collide);
            var nearestDistance = float.PositiveInfinity;
            Transform nearest = null;
            for (var i = 0; i < hitCount; i++)
            {
                var hitTransform = OcclusionHits[i].collider != null
                    ? OcclusionHits[i].collider.transform
                    : null;
                if (hitTransform == null ||
                    hitTransform == transform ||
                    hitTransform.IsChildOf(transform))
                {
                    continue;
                }
                if (OcclusionHits[i].distance < nearestDistance)
                {
                    nearestDistance = OcclusionHits[i].distance;
                    nearest = hitTransform;
                }
            }

            return nearest == null ||
                   nearest == target.transform ||
                   nearest.IsChildOf(target.transform);
        }

        private Vector3 ApplyBearingNoise(Vector3 direction, float sigmaDegrees)
        {
            if (sigmaDegrees <= 0f)
            {
                return direction;
            }

            var referenceUp = Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > 0.95f
                ? Vector3.right
                : Vector3.up;
            var right = Vector3.Cross(referenceUp, direction).normalized;
            var up = Vector3.Cross(direction, right).normalized;
            var scale = sigmaDegrees * Mathf.Deg2Rad;
            return (direction +
                    right * (Mathf.Clamp(NextGaussian(), -2.5f, 2.5f) * scale) +
                    up * (Mathf.Clamp(NextGaussian(), -2.5f, 2.5f) * scale))
                .normalized;
        }

        private float NextGaussian()
        {
            var first = Mathf.Max(0.000001f, NextUniform());
            var second = NextUniform();
            return Mathf.Sqrt(-2f * Mathf.Log(first)) *
                   Mathf.Cos(2f * Mathf.PI * second);
        }

        private float NextUniform()
        {
            var state = noiseState;
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            noiseState = state != 0u ? state : 1u;
            return (noiseState & 0x00ffffffu) / 16777216f;
        }
    }
}
