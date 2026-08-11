using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;

namespace ACunicombat.FireControl
{
    /// <summary>
    /// 真比例导引（设计文档 17.2）：直接消费导引头 LOS 率测量（Measurement.LosRate），
    /// 不经过位置滤波器——链路最短且拟真。a_cmd = N·V_c·(Ω × R̂)，恒垂直于视线。
    /// </summary>
    public static class PnGuidance
    {
        /// <param name="losRate">Ω：导引头测得的视线角速率向量（世界系，rad/s）。</param>
        /// <param name="losDirection">R̂：视线单位向量（导弹→目标）。</param>
        /// <param name="closingVelocity">V_c = −(R·V)/|R|：雷达导引头多普勒直测，红外由 Ω 与角尺寸变化率估计。</param>
        /// <param name="navigationConstant">N ≈ 3~4。</param>
        /// <param name="maxAcceleration">结构过载上限（m/s²），超出截断——燃料/过载不足 = 自然脱靶。</param>
        public static float3 Command(
            float3 losRate, float3 losDirection, float closingVelocity,
            float navigationConstant, float maxAcceleration)
        {
            var aCmd = navigationConstant * closingVelocity * math.cross(losRate, losDirection);
            var magnitude = math.length(aCmd);
            return magnitude > maxAcceleration ? aCmd * (maxAcceleration / magnitude) : aCmd;
        }

        /// <summary>框架角检查（17.2）：视线与导引头轴夹角超上限 → 脱锁。</summary>
        public static bool WithinGimbalLimit(float3 losDirection, float3 seekerAxis, float maxGimbalRadians) =>
            math.dot(losDirection, seekerAxis) >= math.cos(maxGimbalRadians);
    }

    public static class MissileEngagementMath
    {
        public static bool IsCompatibleSeekerTarget(
            bool interceptorMissile,
            TrackObjectClass objectClass,
            float classificationConfidence,
            float minimumClassificationConfidence = 0.25f)
        {
            if (!math.isfinite(classificationConfidence) ||
                classificationConfidence <
                    math.max(0f, minimumClassificationConfidence))
            {
                return false;
            }

            var expectedClass = interceptorMissile
                ? TrackObjectClass.Missile
                : TrackObjectClass.AirVehicle;
            return objectClass == expectedClass;
        }

        public static bool IntersectsProtectedCpaWindow(
            float timeToClosestApproachSeconds,
            float closestApproachDistanceMeters,
            float cpaUncertaintyMeters,
            float predictionHorizonSeconds,
            float protectedRadiusMeters,
            float uncertaintySigmaMultiplier = 2f,
            float maximumUncertaintyAllowanceMeters = 2500f)
        {
            if (timeToClosestApproachSeconds < 0f ||
                timeToClosestApproachSeconds >
                    predictionHorizonSeconds)
            {
                return false;
            }

            var uncertaintyAllowance = math.min(
                math.max(0f, cpaUncertaintyMeters) *
                    math.max(0f, uncertaintySigmaMultiplier),
                math.max(0f, maximumUncertaintyAllowanceMeters));
            return closestApproachDistanceMeters <=
                   protectedRadiusMeters + uncertaintyAllowance;
        }

        public static bool IsInsideSeekerVolume(
            float3 missilePosition,
            float3 seekerForward,
            float3 targetPosition,
            float maximumRange,
            float halfAngleRadians)
        {
            var offset = targetPosition - missilePosition;
            var rangeSquared = math.lengthsq(offset);
            if (rangeSquared <= 1e-6f ||
                rangeSquared > maximumRange * maximumRange)
            {
                return false;
            }

            var forward = math.normalizesafe(
                seekerForward,
                new float3(0f, 0f, 1f));
            var direction = offset * math.rsqrt(rangeSquared);
            return math.dot(forward, direction) >=
                   math.cos(halfAngleRadians);
        }

        public static float3 LineOfSightRate(
            float3 relativePosition,
            float3 relativeVelocity)
        {
            var rangeSquared = math.lengthsq(relativePosition);
            return rangeSquared > 1e-6f
                ? math.cross(relativePosition, relativeVelocity) /
                  rangeSquared
                : float3.zero;
        }

        public static float SolveLeadTime(
            float3 relativePosition,
            float3 targetVelocity,
            float missileSpeed,
            float maximumLeadTimeSeconds)
        {
            var rangeSquared = math.lengthsq(relativePosition);
            var maximumLead = math.max(0f, maximumLeadTimeSeconds);
            var speed = math.max(0f, missileSpeed);
            if (rangeSquared <= 1e-6f ||
                speed <= 1e-3f ||
                maximumLead <= 0f)
            {
                return 0f;
            }

            var a = math.lengthsq(targetVelocity) - speed * speed;
            var b = 2f * math.dot(relativePosition, targetVelocity);
            var c = rangeSquared;
            const float epsilon = 1e-5f;
            var solution = float.PositiveInfinity;

            if (math.abs(a) <= epsilon)
            {
                if (math.abs(b) > epsilon)
                {
                    var linearTime = -c / b;
                    if (linearTime > 0f)
                    {
                        solution = linearTime;
                    }
                }
            }
            else
            {
                var discriminant = b * b - 4f * a * c;
                if (discriminant >= 0f)
                {
                    var root = math.sqrt(discriminant);
                    var first = (-b - root) / (2f * a);
                    var second = (-b + root) / (2f * a);
                    if (first > 0f)
                    {
                        solution = first;
                    }
                    if (second > 0f)
                    {
                        solution = math.min(solution, second);
                    }
                }
            }

            if (!math.isfinite(solution))
            {
                // Current speed can be below target speed during cold launch,
                // while the missile still has substantial boost remaining.
                // Keep aiming at the bounded future basket until a positive
                // constant-speed solution becomes available.
                solution = maximumLead;
            }

            return math.clamp(solution, 0f, maximumLead);
        }

        public static float3 PredictLeadPoint(
            float3 missilePosition,
            float3 targetPosition,
            float3 targetVelocity,
            float missileSpeed,
            float maximumLeadTimeSeconds)
        {
            var leadTime = SolveLeadTime(
                targetPosition - missilePosition,
                targetVelocity,
                missileSpeed,
                maximumLeadTimeSeconds);
            return targetPosition + targetVelocity * leadTime;
        }

        public static bool HasReachableIntercept(
            float3 relativeTargetPosition,
            float3 targetVelocity,
            float3 missileVelocity,
            float remainingFlightTimeSeconds,
            float remainingTravelDistanceMeters,
            float availableDeltaVMetersPerSecond,
            int sampleCount = 32)
        {
            var horizon = math.max(0f, remainingFlightTimeSeconds);
            var travelBudget = math.max(
                0f,
                remainingTravelDistanceMeters);
            var deltaVBudget = math.max(
                0f,
                availableDeltaVMetersPerSecond);
            if (horizon <= 0.05f ||
                travelBudget <= 0.1f ||
                !math.all(math.isfinite(relativeTargetPosition)) ||
                !math.all(math.isfinite(targetVelocity)) ||
                !math.all(math.isfinite(missileVelocity)))
            {
                return false;
            }

            var samples = math.clamp(sampleCount, 4, 128);
            for (var i = 1; i <= samples; i++)
            {
                var normalizedTime = i / (float)samples;
                var time = math.max(
                    0.05f,
                    horizon * normalizedTime * normalizedTime);
                var interceptOffset =
                    relativeTargetPosition + targetVelocity * time;
                if (math.length(interceptOffset) > travelBudget)
                {
                    continue;
                }

                var requiredVelocity = interceptOffset / time;
                var requiredDeltaV = math.length(
                    requiredVelocity - missileVelocity);
                if (requiredDeltaV <= deltaVBudget)
                {
                    return true;
                }
            }

            return false;
        }

        public static bool TryGetProximityPass(
            float3 relativePosition,
            float3 relativeVelocity,
            float stepSeconds,
            float fuseRadius,
            out float timeToClosestApproach,
            out float closestDistance)
        {
            var result = CpaSolver.Solve(
                relativePosition,
                relativeVelocity,
                double3.zero,
                math.max(0f, stepSeconds));
            timeToClosestApproach = (float)result.Time;
            closestDistance = (float)result.Distance;
            return closestDistance <= fuseRadius;
        }

        public static bool ProximityOccursBeforeImpact(
            float proximityTimeSeconds,
            float impactTimeSeconds,
            float coincidenceToleranceSeconds = 0.0001f)
        {
            if (!math.isfinite(proximityTimeSeconds))
            {
                return false;
            }

            return proximityTimeSeconds +
                   math.max(0f, coincidenceToleranceSeconds) <
                   impactTimeSeconds;
        }
    }
}
