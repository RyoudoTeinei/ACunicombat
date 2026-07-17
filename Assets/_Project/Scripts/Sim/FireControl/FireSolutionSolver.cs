using System;
using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;

namespace ACunicombat.FireControl
{
    public enum FireSolutionFailure : byte
    {
        None = 0,
        TrackTooOld = 1,
        CovarianceTooLarge = 2,
        OutOfWeaponEnvelope = 3,
        LifecycleBelowConfirmed = 4,
    }

    /// <summary>火控输出契约（设计文档 16）。</summary>
    public struct FireSolution
    {
        public double3 InterceptPoint;
        public double InterceptTime;
        /// <summary>由 P 在拦截几何上的投影推得的预测命中概率。</summary>
        public float Quality;
        public bool WeaponReleaseAuthorized;
        public double3 MidcourseAimPoint;
        public double SeekerActivationTime;
        /// <summary>末段搜索篮：协方差投影椭球半轴（米）。</summary>
        public float3 SearchBasketSemiAxes;
        public FireSolutionFailure Failure;
    }

    /// <summary>
    /// 火控解算（设计文档 16）：只读 TrackSnapshot，asmdef 上无法触及 World。
    /// 机炮散布属于武器系统，火控误差属于航迹协方差，两者不混。
    /// 允许低质量发射：解算照给，搜索篮巨大，导弹末段找不到目标是自然结果。
    /// </summary>
    public static class FireSolutionSolver
    {
        /// <summary>
        /// 拦截点迭代（16）：t* 使 |目标预测(t*) − 弹道(t*)| 最小，2~3 次收敛。
        /// 首版常速度目标 + 常速度弹丸：t ← |目标(t) − 射手| / 弹速，迭代 3 次。
        /// </summary>
        public static FireSolution Solve(
            in TrackSnapshot track, double3 shooterPosition, double projectileSpeed, double requestTime)
        {
            var solution = default(FireSolution);
            if (track.Lifecycle < TrackLifecycle.Confirmed)
            {
                solution.Failure = FireSolutionFailure.LifecycleBelowConfirmed;
                return solution;
            }

            var timeOfFlight = 0.0;
            for (var i = 0; i < 3; i++)
            {
                var predicted = track.Position + track.Velocity * (requestTime - track.LastUpdateTime + timeOfFlight);
                timeOfFlight = math.length(predicted - shooterPosition) / math.max(projectileSpeed, 1.0);
            }

            solution.InterceptTime = requestTime + timeOfFlight;
            solution.InterceptPoint = track.Position
                + track.Velocity * (solution.InterceptTime - track.LastUpdateTime);
            solution.MidcourseAimPoint = solution.InterceptPoint;

            // TODO(阶段 1)：解算质量 = P 前推到拦截时刻后在拦截几何上的投影 → 命中概率；
            // 质量门限 + lifecycle 共同给出 WeaponReleaseAuthorized；
            // 搜索篮 = 前推协方差的位置块特征值开方（椭球半轴）；
            // 超出武器包线判定（射程/离轴角）。
            throw new NotImplementedException("阶段 1 实现点：解算质量/开火许可/搜索篮（设计文档 16）。");
        }
    }
}
