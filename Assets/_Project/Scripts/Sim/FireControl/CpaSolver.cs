using System;
using Unity.Mathematics;

namespace ACunicombat.FireControl
{
    /// <summary>
    /// 解析 CPA（设计文档 17.3，v1.2 定案算法）：tick 内匀加速段的最近接近点。
    /// d|Δp|²/dt 展开为三次多项式，用"导数二次方程极值点隔离单调区间 + 固定 32 次二分"
    /// 求全部区间实根——确定性、无超越函数、同输入同步数同结果。
    /// 不采用 Cardano（libm 依赖）与单起点 Newton（无法覆盖全部实根）。
    /// 双精度计算，与浮动原点无关。
    /// </summary>
    public static class CpaSolver
    {
        public struct Result
        {
            /// <summary>[0, dt] 内的最近接近时刻。</summary>
            public double Time;
            public double Distance;
        }

        private const double Epsilon = 1e-12;
        private const int BisectionSteps = 32;

        /// <param name="dp0">相对位置 Δp₀（目标 − 导弹）。</param>
        /// <param name="dv">相对速度 Δv。</param>
        /// <param name="da">相对加速度 Δa。</param>
        /// <param name="dt">tick 长度（秒）。</param>
        public static Result Solve(double3 dp0, double3 dv, double3 da, double dt)
        {
            var a = 0.5 * da; // Δp(t) = Δp₀ + Δv·t + A·t²，A = ½Δa
            var c3 = 2.0 * math.dot(a, a);
            var c2 = 3.0 * math.dot(dv, a);
            var c1 = math.dot(dv, dv) + 2.0 * math.dot(dp0, a);
            var c0 = math.dot(dp0, dv);

            // 候选集 = 区间内全部实根 ∪ {0, dt}（17.3 第 4 步）。
            Span<double> candidates = stackalloc double[5];
            var candidateCount = 0;
            candidates[candidateCount++] = 0.0;
            candidates[candidateCount++] = dt;

            if (math.lengthsq(da) < Epsilon)
            {
                // 步骤 1：|Δa|² < ε → 线性交会 t* = −c₀/c₁（c₁ < ε 时取端点）。
                if (math.abs(c1) > Epsilon)
                {
                    var t = -c0 / c1;
                    if (t > 0.0 && t < dt)
                    {
                        candidates[candidateCount++] = t;
                    }
                }
            }
            else
            {
                // 步骤 2：导数二次方程 3c₃t² + 2c₂t + c₁ = 0 的极值点切分单调子区间。
                Span<double> breakpoints = stackalloc double[4];
                var breakCount = 0;
                breakpoints[breakCount++] = 0.0;

                var qa = 3.0 * c3;
                var qb = 2.0 * c2;
                var disc = qb * qb - 4.0 * qa * c1;
                if (disc > 0.0 && qa > Epsilon)
                {
                    var sq = math.sqrt(disc);
                    var e1 = (-qb - sq) / (2.0 * qa);
                    var e2 = (-qb + sq) / (2.0 * qa);
                    if (e1 > 0.0 && e1 < dt)
                    {
                        breakpoints[breakCount++] = e1;
                    }

                    if (e2 > 0.0 && e2 < dt && e2 > e1)
                    {
                        breakpoints[breakCount++] = e2;
                    }
                }

                breakpoints[breakCount++] = dt;

                // 步骤 3：每个端点异号的单调子区间固定 32 次二分；同号无根。
                for (var i = 0; i < breakCount - 1; i++)
                {
                    var lo = breakpoints[i];
                    var hi = breakpoints[i + 1];
                    var flo = Cubic(c3, c2, c1, c0, lo);
                    var fhi = Cubic(c3, c2, c1, c0, hi);
                    if (flo == 0.0)
                    {
                        candidates[candidateCount++] = lo;
                        continue;
                    }

                    if (flo * fhi > 0.0)
                    {
                        continue;
                    }

                    for (var step = 0; step < BisectionSteps; step++)
                    {
                        var mid = 0.5 * (lo + hi);
                        var fmid = Cubic(c3, c2, c1, c0, mid);
                        if (flo * fmid <= 0.0)
                        {
                            hi = mid;
                        }
                        else
                        {
                            lo = mid;
                            flo = fmid;
                        }
                    }

                    candidates[candidateCount++] = 0.5 * (lo + hi);
                }
            }

            // 步骤 4：候选集逐一求距离取最小。
            var best = new Result { Time = 0.0, Distance = double.MaxValue };
            for (var i = 0; i < candidateCount; i++)
            {
                var t = candidates[i];
                var p = dp0 + dv * t + a * (t * t);
                var dist = math.length(p);
                if (dist < best.Distance)
                {
                    best.Time = t;
                    best.Distance = dist;
                }
            }

            return best;
        }

        private static double Cubic(double c3, double c2, double c1, double c0, double t) =>
            ((c3 * t + c2) * t + c1) * t + c0;
    }
}
