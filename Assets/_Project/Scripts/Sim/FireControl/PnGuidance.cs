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
    }
}
