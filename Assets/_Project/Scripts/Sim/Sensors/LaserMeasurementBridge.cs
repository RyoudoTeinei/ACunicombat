using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;

namespace ACunicombat.Sensors
{
    /// <summary>
    /// 把激光测距回波转成跟踪器可消费的 <see cref="Measurement"/>。
    ///
    /// 为什么需要它：在此之前激光只在 <c>MissileTrackFusion.AssociateLaserRangeObservation</c>
    /// 里被消费，那是<b>跟踪器的下游</b>，做的是单帧事后覆写 <c>estimatedPosition</c>。
    /// 卡尔曼滤波器的速度来自量测<b>序列</b>按协方差加权的斜率，事后替换一个输出位置提供的
    /// 速度信息严格为零——因此 <c>MissileTrackSnapshot.Velocity</c> 一直是纯雷达质量。
    ///
    /// 而横向速度恰恰没有任何直接量测（多普勒只给径向），只能由位置序列推出，其精度直接
    /// 由位置横向噪声决定：雷达在 3 km 处约 9.3 m（4° 波束、SNR 20 dB），激光约 1.57 m
    /// （0.03° 测角）。把激光作为位置量测送进滤波器，横向速度误差随之改善，提前量误差
    /// （= 速度误差 × 飞行时间）才会落到近炸半径以内。
    ///
    /// <see cref="Measurement"/> 需要的字段与 <see cref="LaserRangeObservation"/> 完全对应，
    /// 所以走跟踪器现有的 <c>ConvertMeasurement</c> → <c>UpdatePosition</c> 路径即可；
    /// 无多普勒的维度按契约填 NaN。标记 <see cref="MeasurementFlags.RefinementOnly"/>
    /// 使其只能精化已有航迹。
    /// </summary>
    public static class LaserMeasurementBridge
    {
        /// <summary>激光回波若与任何航迹都关联不上，跟踪器会直接丢弃而不是新建航迹。</summary>
        public static bool TryConvert(
            in LaserRangeObservation observation,
            out Measurement measurement)
        {
            measurement = default;
            if (float.IsNaN(observation.RangeMeters) ||
                observation.RangeMeters <= 0f ||
                math.lengthsq(observation.Direction) <= 1e-8f)
            {
                return false;
            }

            measurement = new Measurement
            {
                SensorId = observation.SensorId,
                Timestamp = observation.Timestamp,
                Range = observation.RangeMeters,
                Direction = math.normalize(observation.Direction),
                // 激光测距没有多普勒，也不输出视线角速率：按契约填 NaN，跟踪器会跳过对应更新。
                RadialVelocity = float.NaN,
                LosRate = new float3(float.NaN),
                RangeVar = math.max(observation.RangeVar, 1e-3f),
                AngleVar = math.max(observation.AngleVar, 1e-12f),
                VelocityVar = float.NaN,
                LosRateVar = float.NaN,
                SnrDb = observation.SignalMarginDb,
                ObjectClass = observation.ObjectClass,
                ClassificationConfidence = observation.ClassificationConfidence,
                Flags = MeasurementFlags.RefinementOnly,
            };
            return true;
        }
    }
}
