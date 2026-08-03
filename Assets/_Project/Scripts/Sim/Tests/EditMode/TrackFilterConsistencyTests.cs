using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using ACunicombat.Tracking;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace ACunicombat.Tests.EditMode
{
    /// <summary>
    /// 跟踪滤波器一致性验收（对应 <c>Cmkf.cs</c> 里"必须以 NEES/NIS 作为验收，不得用目测
    /// 代替"的要求）。这组测试是把激光测距接入跟踪器之前必须先立起来的基线。
    ///
    /// <b>NEES</b>（归一化估计误差平方）= eᵀP⁻¹e，e 为 6 维状态误差（位置+速度）。
    /// 滤波器一致时期望值等于状态维数 6。<b>偏大</b>说明协方差被低估（过度自信）——这是
    /// 危险方向，会让下游的关联门限、去重门限和火控交战门限全部失效；<b>偏小</b>说明过度
    /// 保守，代价是精度浪费但不会造成错误判决。
    ///
    /// <b>后验残差检验</b>取代严格 NIS：真正的 NIS 需要滤波器内部的新息与 S = HP⁻Hᵀ+R，
    /// 而 <c>RadarTrackManager</c> 不暴露它们；在测试里自行重建预测协方差等于把滤波器实现
    /// 抄一遍，那样的断言是同义反复。因此这里改为检验后验残差幅度必须小于量测噪声——
    /// 这是滤波有效性的必要条件，且不依赖内部实现。
    ///
    /// <b>横向速度 RMSE</b> 是本轮改动真正要优化的量：提前量误差 = 速度误差 × 飞行时间，
    /// 而横向速度在雷达链路里没有任何直接量测（多普勒只给径向），只能由位置序列的斜率推出。
    /// </summary>
    public class TrackFilterConsistencyTests
    {
        private const int StateDimension = 6;
        private const double StepSeconds = 0.04;
        private const int StepsPerTrial = 80;
        // 前若干步滤波器仍在收敛（初始速度 sigma 为 300 m/s），不计入统计。
        private const int WarmupSteps = 25;
        private const int Trials = 24;

        // 点防御波束 4°、SNR 20 dB 时 SigmaAngle = BeamWidth/(1.6·√(2·SNR)) ≈ 3.1 mrad。
        private const float RadarAngleSigmaRadians = 3.1e-3f;
        private const float RadarRangeSigmaMeters = 6f;
        private const float RadarRadialVelocitySigma = 2f;
        // 激光：BearingSigmaRadians = 0.03°、BaseRangeSigmaMeters = 0.75（见
        // MechRadarController 的 LaserRangefinderProfile 注册）。
        private const float LaserAngleSigmaRadians = 0.03f * math.TORADIANS;
        private const float LaserRangeSigmaMeters = 0.75f;

        private static readonly double3 TrueInitialPosition =
            new double3(0.0, 0.0, 3_000.0);
        // 横向为主的交会：横向速度分量正是雷达最难估的方向，也是提前量误差的来源。
        private static readonly double3 TrueVelocity =
            new double3(220.0, 0.0, -900.0);

        [Test]
        public void RadarOnly_FilterCovarianceIsNotOverconfident()
        {
            var result = RunScenario(useLaser: false, seed: 0x51ED2411u);

            Report(result.Describe("radar-only"));

            // 期望 6。上界是硬性验收：超过 2 倍意味着协方差被低估到会让下游门限失效。
            Assert.That(
                result.AverageNees,
                Is.LessThan(2.0 * StateDimension),
                "NEES 远大于状态维数说明协方差被低估（过度自信）。");
            // 下界很宽松：过度保守只损失精度，不产生错误判决。
            Assert.That(
                result.AverageNees,
                Is.GreaterThan(0.2 * StateDimension),
                "NEES 远小于状态维数说明协方差被高估到失去意义。");
        }

        [Test]
        public void RadarOnly_PosteriorResidualIsSmallerThanMeasurementNoise()
        {
            var result = RunScenario(useLaser: false, seed: 0x51ED2411u);

            Report(result.Describe("radar-only"));

            // 后验位置残差必须小于量测噪声，否则滤波没有起到任何平滑作用。
            var measurementCrossRangeSigma =
                RadarAngleSigmaRadians * math.length(TrueInitialPosition);
            Assert.That(
                result.PositionRmse,
                Is.LessThan(measurementCrossRangeSigma),
                "后验位置误差不应大于单次量测的横向噪声。");
        }

        [Test]
        public void LaserRefinement_ImprovesCrossRangeVelocityWithoutBreakingConsistency()
        {
            var radarOnly = RunScenario(useLaser: false, seed: 0x51ED2411u);
            var withLaser = RunScenario(useLaser: true, seed: 0x51ED2411u);

            Report(radarOnly.Describe("radar-only"));
            Report(withLaser.Describe("radar+laser"));
            Report(
                $"cross-range velocity RMSE: " +
                $"{radarOnly.CrossRangeVelocityRmse:0.00} -> " +
                $"{withLaser.CrossRangeVelocityRmse:0.00} m/s");

            // 这是整轮改动的验收标准：接入激光必须显著改善横向速度精度。
            // 激光横向 sigma 比雷达小约 5.9 倍（0.524 mrad vs 3.1 mrad），保守要求 2 倍。
            Assert.That(
                withLaser.CrossRangeVelocityRmse,
                Is.LessThan(radarOnly.CrossRangeVelocityRmse / 2.0),
                "接入激光后横向速度误差应至少改善一半，否则量测注入没有真正生效。");

            // 精度提升不得以协方差失真为代价——激光横向方差比雷达小约 35 倍，
            // 卡尔曼增益接近 1，这正是最容易把协方差压到过度自信的场景。
            Assert.That(
                withLaser.AverageNees,
                Is.LessThan(2.0 * StateDimension),
                "接入激光后协方差被低估，下游门限会失效。");
        }

        /// <summary>
        /// 双通道输出实测值。`TestContext.WriteLine` 在 batchmode 下既不进 Editor 日志、
        /// 结果 XML 又未必落地，而这些数值是标定 NEES 阈值的唯一依据，所以同时走
        /// `Debug.Log`（一定进 -logFile）和一个 Temp 下的文本文件（便于直接读取）。
        /// </summary>
        private static void Report(string line)
        {
            TestContext.WriteLine(line);
            UnityEngine.Debug.Log($"[TrackFilterConsistency] {line}");
            try
            {
                var path = System.IO.Path.Combine(
                    UnityEngine.Application.dataPath,
                    "..",
                    "Temp",
                    "track-filter-consistency.txt");
                System.IO.File.AppendAllText(path, line + System.Environment.NewLine);
            }
            catch (System.IO.IOException)
            {
                // 落盘只是便利手段，失败不应影响验收结论。
            }
        }

        private static ScenarioResult RunScenario(bool useLaser, uint seed)
        {
            var neesSum = 0.0;
            var neesCount = 0;
            var positionSquaredErrorSum = 0.0;
            var crossRangeVelocitySquaredErrorSum = 0.0;
            var sampleCount = 0;
            var rng = new DeterministicGaussian(seed);

            // 横向单位向量：真速度中垂直于初始视线的分量方向，即"提前量误差"的方向。
            var lineOfSight = math.normalize(TrueInitialPosition);
            var crossRangeAxis = math.normalize(
                TrueVelocity - lineOfSight * math.dot(TrueVelocity, lineOfSight));

            for (var trial = 0; trial < Trials; trial++)
            {
                var manager = new RadarTrackManager();
                using var measurements =
                    new NativeList<Measurement>(4, Allocator.Temp);

                for (var step = 1; step <= StepsPerTrial; step++)
                {
                    var time = step * StepSeconds;
                    var truePosition = TrueInitialPosition + TrueVelocity * time;

                    measurements.Clear();
                    measurements.Add(BuildRadarMeasurement(
                        ref rng,
                        truePosition,
                        TrueVelocity,
                        time));
                    if (useLaser)
                    {
                        measurements.Add(BuildLaserMeasurement(
                            ref rng,
                            truePosition,
                            time));
                    }

                    manager.Process(measurements, double3.zero, time);

                    if (step <= WarmupSteps ||
                        !manager.TryGetBestTrack(out var track))
                    {
                        continue;
                    }

                    var positionError = truePosition - track.Position;
                    var velocityError = TrueVelocity - track.Velocity;
                    if (TryComputeNees(
                            in track,
                            positionError,
                            velocityError,
                            out var nees))
                    {
                        neesSum += nees;
                        neesCount++;
                    }

                    positionSquaredErrorSum += math.lengthsq(positionError);
                    var crossRangeVelocityError =
                        math.dot(velocityError, crossRangeAxis);
                    crossRangeVelocitySquaredErrorSum +=
                        crossRangeVelocityError * crossRangeVelocityError;
                    sampleCount++;
                }
            }

            Assert.That(
                sampleCount,
                Is.GreaterThan(0),
                "场景没有产生任何可评估样本，航迹可能从未确认。");
            return new ScenarioResult
            {
                AverageNees = neesCount > 0 ? neesSum / neesCount : double.NaN,
                NeesSamples = neesCount,
                PositionRmse = math.sqrt(positionSquaredErrorSum / sampleCount),
                CrossRangeVelocityRmse =
                    math.sqrt(crossRangeVelocitySquaredErrorSum / sampleCount),
                Samples = sampleCount,
            };
        }

        private static Measurement BuildRadarMeasurement(
            ref DeterministicGaussian rng,
            double3 truePosition,
            double3 trueVelocity,
            double time)
        {
            var range = math.length(truePosition);
            var trueDirection = truePosition / range;
            var noisyDirection = ApplyAngleNoise(
                ref rng,
                trueDirection,
                RadarAngleSigmaRadians);
            return new Measurement
            {
                SensorId = new SensorId(new Handle(0, 1)),
                Timestamp = time,
                Range = (float)range + (float)rng.Next() * RadarRangeSigmaMeters,
                Direction = (float3)noisyDirection,
                RadialVelocity =
                    (float)math.dot(trueVelocity, trueDirection) +
                    (float)rng.Next() * RadarRadialVelocitySigma,
                LosRate = new float3(float.NaN),
                RangeVar = RadarRangeSigmaMeters * RadarRangeSigmaMeters,
                AngleVar = RadarAngleSigmaRadians * RadarAngleSigmaRadians,
                VelocityVar =
                    RadarRadialVelocitySigma * RadarRadialVelocitySigma,
                LosRateVar = float.NaN,
                SnrDb = 20f,
                ObjectClass = TrackObjectClass.Missile,
                ClassificationConfidence = 0.9f,
                Flags = MeasurementFlags.None,
            };
        }

        /// <summary>
        /// 激光测距返回：高精度距离 + 高精度角度，无多普勒。标记为 RefinementOnly，
        /// 因此只能精化已有航迹、不能凭它自己新建航迹。
        /// </summary>
        private static Measurement BuildLaserMeasurement(
            ref DeterministicGaussian rng,
            double3 truePosition,
            double time)
        {
            var range = math.length(truePosition);
            var trueDirection = truePosition / range;
            var noisyDirection = ApplyAngleNoise(
                ref rng,
                trueDirection,
                LaserAngleSigmaRadians);
            return new Measurement
            {
                SensorId = new SensorId(new Handle(1, 1)),
                Timestamp = time,
                Range = (float)range + (float)rng.Next() * LaserRangeSigmaMeters,
                Direction = (float3)noisyDirection,
                RadialVelocity = float.NaN,
                LosRate = new float3(float.NaN),
                RangeVar = LaserRangeSigmaMeters * LaserRangeSigmaMeters,
                AngleVar = LaserAngleSigmaRadians * LaserAngleSigmaRadians,
                VelocityVar = float.NaN,
                LosRateVar = float.NaN,
                SnrDb = 40f,
                ObjectClass = TrackObjectClass.Missile,
                ClassificationConfidence = 0.9f,
                Flags = MeasurementFlags.RefinementOnly,
            };
        }

        private static double3 ApplyAngleNoise(
            ref DeterministicGaussian rng,
            double3 trueDirection,
            float angleSigmaRadians)
        {
            var reference = math.abs(trueDirection.y) > 0.9
                ? new double3(1.0, 0.0, 0.0)
                : new double3(0.0, 1.0, 0.0);
            var tangentA = math.normalize(math.cross(reference, trueDirection));
            var tangentB = math.cross(trueDirection, tangentA);
            return math.normalize(
                trueDirection +
                tangentA * (rng.Next() * angleSigmaRadians) +
                tangentB * (rng.Next() * angleSigmaRadians));
        }

        private static bool TryComputeNees(
            in TrackSnapshot track,
            double3 positionError,
            double3 velocityError,
            out double nees)
        {
            nees = 0.0;
            var covariance = new double[StateDimension * StateDimension];
            for (var row = 0; row < StateDimension; row++)
            {
                for (var column = 0; column < StateDimension; column++)
                {
                    covariance[row * StateDimension + column] =
                        track.GetCovariance(row, column);
                }
            }

            if (!TryInvertSymmetric(covariance, out var inverse))
            {
                return false;
            }

            var error = new[]
            {
                positionError.x,
                positionError.y,
                positionError.z,
                velocityError.x,
                velocityError.y,
                velocityError.z,
            };
            for (var row = 0; row < StateDimension; row++)
            {
                var accumulated = 0.0;
                for (var column = 0; column < StateDimension; column++)
                {
                    accumulated +=
                        inverse[row * StateDimension + column] * error[column];
                }

                nees += error[row] * accumulated;
            }

            return nees >= 0.0 && !double.IsNaN(nees);
        }

        /// <summary>带部分选主元的高斯-约当求逆；只在测试里使用，不追求性能。</summary>
        private static bool TryInvertSymmetric(
            double[] matrix,
            out double[] inverse)
        {
            const int n = StateDimension;
            var work = (double[])matrix.Clone();
            inverse = new double[n * n];
            for (var i = 0; i < n; i++)
            {
                inverse[i * n + i] = 1.0;
            }

            for (var column = 0; column < n; column++)
            {
                var pivotRow = column;
                var pivotMagnitude = math.abs(work[column * n + column]);
                for (var row = column + 1; row < n; row++)
                {
                    var magnitude = math.abs(work[row * n + column]);
                    if (magnitude > pivotMagnitude)
                    {
                        pivotMagnitude = magnitude;
                        pivotRow = row;
                    }
                }

                if (pivotMagnitude < 1e-18)
                {
                    return false;
                }

                if (pivotRow != column)
                {
                    SwapRows(work, pivotRow, column, n);
                    SwapRows(inverse, pivotRow, column, n);
                }

                var pivot = work[column * n + column];
                for (var k = 0; k < n; k++)
                {
                    work[column * n + k] /= pivot;
                    inverse[column * n + k] /= pivot;
                }

                for (var row = 0; row < n; row++)
                {
                    if (row == column)
                    {
                        continue;
                    }

                    var factor = work[row * n + column];
                    if (factor == 0.0)
                    {
                        continue;
                    }

                    for (var k = 0; k < n; k++)
                    {
                        work[row * n + k] -= factor * work[column * n + k];
                        inverse[row * n + k] -= factor * inverse[column * n + k];
                    }
                }
            }

            return true;
        }

        private static void SwapRows(double[] matrix, int first, int second, int n)
        {
            for (var k = 0; k < n; k++)
            {
                (matrix[first * n + k], matrix[second * n + k]) =
                    (matrix[second * n + k], matrix[first * n + k]);
            }
        }

        private struct ScenarioResult
        {
            public double AverageNees;
            public int NeesSamples;
            public double PositionRmse;
            public double CrossRangeVelocityRmse;
            public int Samples;

            public string Describe(string label) =>
                $"[{label}] NEES={AverageNees:0.000} (期望 {StateDimension}, " +
                $"n={NeesSamples}), 位置 RMSE={PositionRmse:0.00} m, " +
                $"横向速度 RMSE={CrossRangeVelocityRmse:0.00} m/s, " +
                $"样本={Samples}";
        }

        /// <summary>
        /// 确定性高斯抽样（Box-Muller + 固定 LCG）。测试必须可复现，不能用 UnityEngine.Random。
        /// </summary>
        private struct DeterministicGaussian
        {
            private uint state;
            private double spare;
            private bool hasSpare;

            public DeterministicGaussian(uint seed)
            {
                state = seed != 0u ? seed : 1u;
                spare = 0.0;
                hasSpare = false;
            }

            public double Next()
            {
                if (hasSpare)
                {
                    hasSpare = false;
                    return spare;
                }

                var first = math.max(NextUniform(), 1e-12);
                var second = NextUniform();
                var radius = math.sqrt(-2.0 * math.log(first));
                var angle = 2.0 * math.PI_DBL * second;
                spare = radius * math.sin(angle);
                hasSpare = true;
                return radius * math.cos(angle);
            }

            private double NextUniform()
            {
                state = unchecked(state * 1664525u + 1013904223u);
                return (state >> 8) / (double)(1 << 24);
            }
        }
    }
}
