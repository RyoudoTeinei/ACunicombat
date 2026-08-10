using ACunicombat.Sensing.Contracts;
using Unity.Collections;
using Unity.Mathematics;

namespace ACunicombat.Sensors
{
    /// <summary>
    /// 分辨率合并（设计文档 10.6，v1.2 定案）：同驻留内检测点在测量空间的
    /// SNR 降序贪心聚类。玩法层面的工程近似，不等价于 CFAR（声明边界见文档）。
    /// 字段有效性规则：判据与加权只在传感器能力掩码声明的维度上进行——
    /// NaN 比较恒为 false，不处理会使无多普勒雷达永远无法合并。
    /// 方差按含簇内均值离差的混合公式合成，禁止简单加权平均（NEES 验收依赖）。
    /// 纯静态无分配，可包进 Burst Job（管线第 8 步）。
    /// </summary>
    public static class ResolutionMerge
    {
        public struct Resolutions
        {
            public float RangeMeters;
            public float AngleRadians;
            public float VelocityMps;
        }

        /// <summary>
        /// 就地合并 detections[0..count)，返回合并后的数量（结果压缩在数组前段）。
        /// 实现顺序：先把数组本体按 SNR 降序原地排序，再单趟贪心聚类——
        /// 簇核写入位置 k 时恒有 k ≤ 当前读取位置 s，且 (k, s) 之间只剩已消费元素，
        /// 不存在"结果覆盖未处理检测点"的别名问题。
        /// </summary>
        public static int MergeInPlace(
            NativeArray<Measurement> detections, int count,
            SensorCapability capability, in Resolutions res)
        {
            if (count <= 1)
            {
                return count;
            }

            // SNR 降序插入排序（驻留内 n 很小，稳定且确定）。
            for (var i = 1; i < count; i++)
            {
                var key = detections[i];
                var j = i - 1;
                while (j >= 0 && detections[j].SnrDb < key.SnrDb)
                {
                    detections[j + 1] = detections[j];
                    j--;
                }

                detections[j + 1] = key;
            }

            // 贪心聚类：按 SNR 序取点，能并入已有簇核则并入，否则自成簇核。
            // 判据只对簇核比较（消除两两合并的次序依赖，结果确定）。
            var mergedCount = 0;
            for (var s = 0; s < count; s++)
            {
                var candidate = detections[s];
                var assigned = false;
                for (var c = 0; c < mergedCount; c++)
                {
                    if (WithinResolution(detections[c], candidate, capability, res))
                    {
                        detections[c] = MergePair(detections[c], candidate, capability);
                        assigned = true;
                        break;
                    }
                }

                if (!assigned)
                {
                    detections[mergedCount++] = candidate;
                }
            }

            return mergedCount;
        }

        /// <summary>判据仅在能力掩码维度上评估；角度维度始终参与。</summary>
        private static bool WithinResolution(
            in Measurement core, in Measurement other, SensorCapability cap, in Resolutions res)
        {
            // The simulation target registry supplies the object class independently
            // from the noisy confidence estimate. Never collapse two known, different
            // classes into one plot: doing so lets a much stronger aircraft return erase
            // a missile return while both occupy the same range/angle/Doppler cell.
            if (core.ObjectClass != TrackObjectClass.Unknown &&
                other.ObjectClass != TrackObjectClass.Unknown &&
                core.ObjectClass != other.ObjectClass)
            {
                return false;
            }

            var cosAngle = math.dot(core.Direction, other.Direction);
            if (cosAngle < math.cos(res.AngleRadians))
            {
                return false;
            }

            if ((cap & SensorCapability.Range) != 0 && math.abs(core.Range - other.Range) >= res.RangeMeters)
            {
                return false;
            }

            if ((cap & SensorCapability.Doppler) != 0
                && math.abs(core.RadialVelocity - other.RadialVelocity) >= res.VelocityMps)
            {
                return false;
            }

            return true;
        }

        private static Measurement MergePair(in Measurement a, in Measurement b, SensorCapability cap)
        {
            var snrA = RadarMath.LinearFromDb(a.SnrDb);
            var snrB = RadarMath.LinearFromDb(b.SnrDb);
            var wA = snrA / (snrA + snrB);
            var wB = 1f - wA;

            var merged = a;
            merged.Direction = math.normalize(a.Direction * wA + b.Direction * wB);
            merged.SnrDb = RadarMath.DbFromLinear(snrA + snrB);
            merged.Flags = a.Flags | b.Flags;
            merged.Timestamp = math.min(a.Timestamp, b.Timestamp);
            if (a.ObjectClass == b.ObjectClass)
            {
                merged.ClassificationConfidence =
                    math.saturate(
                        a.ClassificationConfidence * wA +
                        b.ClassificationConfidence * wB);
            }
            else if (b.ClassificationConfidence >
                     a.ClassificationConfidence)
            {
                merged.ObjectClass = b.ObjectClass;
                merged.ClassificationConfidence =
                    b.ClassificationConfidence;
            }

            if ((cap & SensorCapability.Range) != 0)
            {
                merged.Range = wA * a.Range + wB * b.Range;
                merged.RangeVar = MixtureVariance(wA, a.Range, a.RangeVar, wB, b.Range, b.RangeVar);
            }

            if ((cap & SensorCapability.Doppler) != 0)
            {
                merged.RadialVelocity = wA * a.RadialVelocity + wB * b.RadialVelocity;
                merged.VelocityVar = MixtureVariance(
                    wA, a.RadialVelocity, a.VelocityVar, wB, b.RadialVelocity, b.VelocityVar);
            }

            if ((cap & SensorCapability.LosRate) != 0)
            {
                merged.LosRate = wA * a.LosRate + wB * b.LosRate;
                // 矢量维度的均值离差项取各分量离差的平方和（保守上界）。
                var dA = math.lengthsq(a.LosRate - merged.LosRate);
                var dB = math.lengthsq(b.LosRate - merged.LosRate);
                merged.LosRateVar = wA * (a.LosRateVar + dA) + wB * (b.LosRateVar + dB);
            }

            // 角度维度无标量均值：离差项 = 各方向与合并方向的夹角平方（小角近似）。
            var devA = AngleBetween(a.Direction, merged.Direction);
            var devB = AngleBetween(b.Direction, merged.Direction);
            merged.AngleVar = wA * (a.AngleVar + devA * devA) + wB * (b.AngleVar + devB * devB);
            return merged;
        }

        /// <summary>
        /// 标量混合方差，中心化形式：σ² = Σwᵢ(σᵢ² + (μᵢ − μ̄)²)。
        /// 与设计文档 10.6 的 Σwᵢ(σᵢ²+μᵢ²)−μ̄² 数学等价，但避免大数平方相减的
        /// float 灾难性抵消（μ~10⁴ 时非中心化形式误差可达个位数）。
        /// </summary>
        public static float MixtureVariance(float wA, float muA, float varA, float wB, float muB, float varB)
        {
            var mean = wA * muA + wB * muB;
            var dA = muA - mean;
            var dB = muB - mean;
            return wA * (varA + dA * dA) + wB * (varB + dB * dB);
        }

        private static float AngleBetween(float3 u, float3 v) =>
            math.acos(math.clamp(math.dot(u, v), -1f, 1f));
    }
}
